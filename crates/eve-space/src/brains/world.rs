//! The scene as the world an NPC's tree acts in (`eve_behaviors::env::World`): what a task asks of the server's ballpark, dogma engine, inventory and
//! the rest is answered from the scene, and what it orders is done to the scene's ships. Each trait is answered as far as the scene has the thing
//! (`ballpark.rs`); what it has not is left at the trait's default, the answer of a world with nothing of the kind.

use eve_behaviors::env::{Dogma, Entities, Inventory, Logging, Random, Spawning, Universe};

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

impl Dogma for SceneWorld<'_> {}
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
impl Inventory for SceneWorld<'_> {}
impl Universe for SceneWorld<'_> {}
impl Logging for SceneWorld<'_> {}
impl Spawning for SceneWorld<'_> {}
