//! The scene as the world an NPC's tree acts in (`eve_behaviors::env::World`): what a task asks of the server's ballpark, dogma engine, inventory and
//! the rest is answered from the scene, and what it orders is done to the scene's ships. Each trait is answered as far as the scene has the thing
//! (`ballpark.rs`); what it has not is left at the trait's default, the answer of a world with nothing of the kind.

use std::collections::BTreeMap;

use eve_behaviors::env::{Entities, Inventory, InventoryItem, Logging, Random, Spawning, Universe};
use eve_behaviors::events::{Subject, message};
use eve_behaviors::tree::Shared;
use eve_behaviors::value::Value;

use super::ballpark::bubble_of;
use super::{NotifyTarget, RangeNotification, WorldState};
use crate::npc::is_npc_session;
use crate::scene::{Command, Out, Scene, SessionId};

/// A scene, the moment it is at, and what the world has to tell the host.
pub struct SceneWorld<'a> {
    pub(crate) scene: &'a mut Scene,
    pub(crate) now: i64,
    pub(crate) out: &'a mut Vec<Out>,
    pub(crate) state: &'a mut WorldState,
}

impl<'a> SceneWorld<'a> {
    /// The scene's ships are brought to `now`, so what the tasks read of them is current.
    pub(crate) fn new(
        scene: &'a mut Scene,
        now: i64,
        out: &'a mut Vec<Out>,
        state: &'a mut WorldState,
    ) -> Self {
        scene.settle_all(now);
        Self { scene, now, out, state }
    }

    /// The session of the ship with this item id.
    pub(crate) fn session_of(&self, ship_id: i64) -> Option<SessionId> {
        self.scene.session_of(ship_id)
    }

    /// What a ship of the scene is ordered to do: the players who see it are told and what the host must hear is kept; what is for an NPC is not.
    pub(crate) fn command(&mut self, ball_id: i64, command: Command) {
        let Some(session) = self.session_of(ball_id) else { return };
        let told = self.scene.command(session, command, self.now);
        self.out.extend(told.into_iter().filter(|out| !is_npc_session(out.session())));
    }

    /// What happened in the scene since the trees were last told, told to whoever asked to hear of it: locks that were had or lost, ships that came
    /// within a sensor's range or left it, and offensive acts against the NPCs.
    pub(crate) fn tell_what_happened(&mut self, shared: &mut Shared) {
        self.tell_locks(shared);
        self.tell_proximity(shared);
        self.tell_aggressions(shared);
        self.tell_protection(shared);
    }

    /// `INVULNERABILITY_CANCELED(item, isCompleted, reason)` to the bubble of every ship whose undock protection ended since the trees were last told:
    /// `isCompleted` when it ran its course, not when the pilot gave it up by flying the ship.
    fn tell_protection(&mut self, shared: &mut Shared) {
        let mut ended = Vec::new();
        let mut now_protected = BTreeMap::new();
        for pilot in self.scene.pilots.values() {
            let ship_id = pilot.ship.spec.ship_id;
            let protected = pilot.protected_until > self.now;
            if !protected && self.state.protected.get(&ship_id) == Some(&true) {
                let position = pilot.ship.position;
                ended.push((ship_id, [position.x, position.y, position.z], pilot.protected_until != 0));
            }
            now_protected.insert(ship_id, protected);
        }
        self.state.protected = now_protected;
        for (ship_id, position, completed) in ended {
            shared.tell_subscribers(
                Subject::Bubble(bubble_of(position)),
                message::INVULNERABILITY_CANCELED,
                &[Value::Int(ship_id), Value::Bool(completed), Value::None],
            );
        }
    }

    /// `TARGET_ADDED` and `TARGET_LOST` about every NPC whose locks changed.
    fn tell_locks(&mut self, shared: &mut Shared) {
        for session in self.scene.npc_sessions() {
            let ship_id = self.scene.pilots[&session].ship.spec.ship_id;
            let now_locked = self.scene.locked_by(session);
            let before = self.state.locks_seen.entry(ship_id).or_default().clone();
            for target in now_locked.iter().filter(|t| !before.contains(t)) {
                shared.tell_subscribers(
                    Subject::Item(ship_id),
                    message::TARGET_ADDED,
                    &[Value::Int(*target)],
                );
            }
            for target in before.iter().filter(|t| !now_locked.contains(t)) {
                shared.tell_subscribers(
                    Subject::Item(ship_id),
                    message::TARGET_LOST,
                    &[Value::Int(*target), Value::from("lost")],
                );
            }
            self.state.locks_seen.insert(ship_id, now_locked);
        }
    }

    /// `ON_PROXIMITY(ball, entering)` about every sensor that saw a ship come or go.
    fn tell_proximity(&mut self, shared: &mut Shared) {
        let items: Vec<i64> = self.state.sensors.keys().copied().collect();
        for item in items {
            let seen = self.ships_in_sensor_range(item);
            let Some(sensor) = self.state.sensors.get_mut(&item) else { continue };
            let entered: Vec<i64> = seen.difference(&sensor.in_range).copied().collect();
            let left: Vec<i64> = sensor.in_range.difference(&seen).copied().collect();
            sensor.in_range = seen;
            for ball in entered {
                shared.tell_subscribers(
                    Subject::Item(item),
                    message::ON_PROXIMITY,
                    &[Value::Int(ball), Value::Bool(true)],
                );
            }
            for ball in left {
                shared.tell_subscribers(
                    Subject::Item(item),
                    message::ON_PROXIMITY,
                    &[Value::Int(ball), Value::Bool(false)],
                );
            }
        }
    }

    /// `CRIMEWATCH_EVENT_STARTED(effectKey, effectInfo)` about every ship that was shot at.
    fn tell_aggressions(&mut self, shared: &mut Shared) {
        for act in std::mem::take(&mut self.scene.aggressions) {
            let key = Value::record(&[("shipID", Value::Int(act.attacker_ship))]);
            let info = Value::record(&[
                ("isAssistance", Value::Bool(false)),
                ("isOffensive", Value::Bool(true)),
                ("moduleTypeID", Value::Int(i64::from(act.module_type_id))),
                ("sourceOwnerID", Value::Int(act.source_owner_id)),
                ("targetTypeID", Value::Int(i64::from(act.target_type_id))),
            ]);
            shared.tell_subscribers(
                Subject::Item(act.target_ship),
                message::CRIMEWATCH_EVENT_STARTED,
                &[key, info],
            );
        }
    }

    pub(crate) fn notify_when_in_range(&mut self, notification: RangeNotification) {
        self.state.notifications.push(notification);
    }

    /// The ball callbacks whose range has been reached: taken off the list.
    pub(crate) fn take_due_notifications(&mut self) -> Vec<RangeNotification> {
        let waiting = std::mem::take(&mut self.state.notifications);
        let (due, waiting): (Vec<_>, Vec<_>) =
            waiting.into_iter().partition(|notification| self.in_notification_range(notification));
        self.state.notifications = waiting;
        due
    }

    fn in_notification_range(&self, notification: &RangeNotification) -> bool {
        let Some(ball) = self.ball_position(notification.ball) else { return true };
        match notification.target {
            NotifyTarget::Place(place) => distance(ball, place) <= notification.range,
            NotifyTarget::Ball(target) => match self.surface_distance(notification.ball, target) {
                Some(surface) => surface <= notification.range,
                None => true,
            },
        }
    }
}

pub(crate) fn distance(a: [f64; 3], b: [f64; 3]) -> f64 {
    ((a[0] - b[0]).powi(2) + (a[1] - b[1]).powi(2) + (a[2] - b[2]).powi(2)).sqrt()
}

impl Random for SceneWorld<'_> {
    fn random(&mut self) -> f64 {
        self.state.rng.random()
    }
}

impl Entities for SceneWorld<'_> {
    fn group_exists(&self, group_id: i64) -> bool {
        self.state.groups.contains_key(&group_id)
    }

    fn group_members(&self, group_id: i64) -> Option<Vec<i64>> {
        self.state.groups.get(&group_id).cloned()
    }

    fn group_count(&self, group_id: i64) -> Option<usize> {
        self.state.groups.get(&group_id).map(Vec::len)
    }

    fn group_has_entity(&self, group_id: i64, item_id: i64) -> Option<bool> {
        self.state.groups.get(&group_id).map(|members| members.contains(&item_id))
    }

    /// A group is made whole: nothing here spawns it in stages.
    fn group_initial_spawning_complete(&self, group_id: i64) -> Option<bool> {
        self.state.groups.contains_key(&group_id).then_some(true)
    }

    fn find_group_id_for_item(&self, item_id: i64) -> i64 {
        self.state
            .groups
            .iter()
            .find(|(_, members)| members.contains(&item_id))
            .map_or(crate::npc::NO_GROUP, |(group, _)| *group)
    }
}
/// The items of the scene the trees can look up: the ships (an NPC's weapon is its ship) and the modules the ships of players have fitted.
impl Inventory for SceneWorld<'_> {
    fn inventory_item(&self, item_id: i64) -> Option<InventoryItem> {
        let system = i64::from(self.scene.system().id);
        self.scene.pilots.values().find_map(|pilot| {
            let spec = &pilot.ship.spec;
            if spec.ship_id == item_id {
                return Some(InventoryItem {
                    item_id,
                    type_id: i64::from(spec.type_id),
                    owner_id: spec.owner_id,
                    location_id: system,
                });
            }
            spec.modules.iter().find(|module| module.item_id == item_id).map(|module| InventoryItem {
                item_id,
                type_id: i64::from(module.type_id),
                owner_id: spec.owner_id,
                location_id: spec.ship_id,
            })
        })
    }
}
impl Universe for SceneWorld<'_> {}
impl Logging for SceneWorld<'_> {}
impl Spawning for SceneWorld<'_> {}
