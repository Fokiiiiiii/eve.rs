//! NPC ships in a scene: ships that nobody flies. The scene knows them as pilots under sessions of their own (numbered from [`NPC_SESSION_BASE`], far
//! above any the host gives a player), so a ship's motion, warp, locks, modules and health work the same for both and a player sees an NPC as it sees
//! any other ship. What differs is what the client is told about one: its ball has no session and is not interactive, its slim item has no name, and
//! nothing is ever sent to an NPC (`Out`s for an NPC's session are dropped where the scene meets the host, `Sim`).

use eve_destiny::Vec3;

use crate::brains::{Brains, GiveError};
use crate::modules::Modules;
use crate::motion::Target;
use crate::scene::{Out, Pilot, Scene, SessionId};
use crate::ship::{Course, NpcIdentity, Ship, ShipSpec};

/// The session number the first NPC of a scene is known by.
pub const NPC_SESSION_BASE: SessionId = 1 << 62;

/// The id the first entity group of a scene has.
pub const NPC_GROUP_BASE: i64 = 9_000_000_000;

/// What `findGroupIdForItem` answers for an item that is in no group (`INVALID_GROUP_ID`).
pub const NO_GROUP: i64 = eve_behaviors::env::INVALID_GROUP_ID;

/// Whether a session is an NPC's.
pub fn is_npc_session(session: SessionId) -> bool {
    session >= NPC_SESSION_BASE
}

/// Where an NPC comes into a scene and how it is moving.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct NpcPlace {
    pub position: Vec3,
    pub velocity: Vec3,
    /// A unit vector.
    pub direction: Vec3,
    pub speed_fraction: f64,
    pub course: Course,
}

impl NpcPlace {
    /// At rest at a place.
    pub fn at_rest(position: Vec3) -> Self {
        Self {
            position,
            velocity: Vec3::ZERO,
            direction: Vec3::new(1.0, 0.0, 0.0),
            speed_fraction: 0.0,
            course: Course::Stop,
        }
    }
}

impl Scene {
    /// An NPC ship comes into the scene: the pilots that have bound the ballpark and can see it are sent it. The session it is known by, and what
    /// is told.
    pub fn spawn_npc(
        &mut self,
        spec: ShipSpec,
        identity: NpcIdentity,
        place: NpcPlace,
        now: i64,
    ) -> (SessionId, Vec<Out>) {
        let session = self.insert_npc(spec, identity, place, now);
        (session, self.announce_arrival(session, now))
    }

    /// As [`Scene::spawn_npc`], with the scripted behavior of that name running its brain (`ScriptedBehaviorLoader`, item scope: `"dps"`,
    /// `"sleep"`, ...). An NPC that cannot be given the behavior is not made.
    pub fn spawn_npc_with_behavior(
        &mut self,
        spec: ShipSpec,
        identity: NpcIdentity,
        place: NpcPlace,
        behavior: &str,
        now: i64,
    ) -> Result<(SessionId, Vec<Out>), GiveError> {
        self.spawn_npc_in_group(spec, identity, place, behavior, None, now)
    }

    /// As [`Scene::spawn_npc_with_behavior`], as a member of an entity group (`None`: a group of its own, made for it).
    pub fn spawn_npc_in_group(
        &mut self,
        spec: ShipSpec,
        identity: NpcIdentity,
        place: NpcPlace,
        behavior: &str,
        group: Option<i64>,
        now: i64,
    ) -> Result<(SessionId, Vec<Out>), GiveError> {
        let session = self.insert_npc(spec, identity, place, now);
        if let Err(error) = self.give_behavior_in_group(session, behavior, group) {
            self.pilots.remove(&session);
            return Err(error);
        }
        Ok((session, self.announce_arrival(session, now)))
    }

    /// Gives the NPC a brain that runs the scripted behavior of that name, in a group of its own.
    pub fn give_behavior(&mut self, session: SessionId, behavior: &str) -> Result<(), GiveError> {
        self.give_behavior_in_group(session, behavior, None)
    }

    /// The id of a new entity group of this scene.
    pub fn new_entity_group(&mut self) -> i64 {
        self.group_sequence += 1;
        NPC_GROUP_BASE + self.group_sequence as i64
    }

    fn give_behavior_in_group(
        &mut self,
        session: SessionId,
        behavior: &str,
        group: Option<i64>,
    ) -> Result<(), GiveError> {
        let Some(pilot) = self.pilots.get(&session) else {
            return Err(GiveError::Unknown(behavior.to_string()));
        };
        let ship = pilot.ship.clone();
        let system_id = i64::from(self.system().id);
        let group = group.unwrap_or_else(|| self.new_entity_group());
        // a Pochven NPC attacks the NPCs of the other Pochven factions here, and nothing outside Pochven (`pochven.rs`)
        let owners = if behavior == crate::brains::POCHVEN_NPC {
            let in_pochven = self.system().region_id == crate::pochven::POCHVEN_REGION_ID;
            crate::pochven::hostile_corporations(i64::from(ship.spec.corporation_id), in_pochven)
        } else {
            Vec::new()
        };
        self.brains
            .get_or_insert_with(Brains::default)
            .give(session, &ship, system_id, behavior, group, owners)
    }

    pub(crate) fn insert_npc(
        &mut self,
        spec: ShipSpec,
        identity: NpcIdentity,
        place: NpcPlace,
        now: i64,
    ) -> SessionId {
        self.npc_sequence += 1;
        let session = NPC_SESSION_BASE + self.npc_sequence;
        let modules = Modules::with_charge(
            spec.capacitor_capacity,
            spec.capacitor_recharge_s,
            spec.condition.charge,
            now,
        );
        let ship = Ship::new(
            spec,
            place.position,
            place.velocity,
            place.direction,
            place.speed_fraction,
            place.course,
            now,
        )
        .as_npc(identity);
        let entered = self.next_entry();
        self.pilots.insert(session, Pilot::new(ship, modules, entered, now, 0));
        session
    }

    /// The session of the ship with this item id.
    pub(crate) fn session_of(&self, ship_id: i64) -> Option<SessionId> {
        self.pilots.iter().find(|(_, pilot)| pilot.ship.spec.ship_id == ship_id).map(|(session, _)| *session)
    }

    /// What a ship of the scene is to a ship that follows or orbits it: where it is and how big.
    pub(crate) fn target_of_ship(&self, session: SessionId) -> Option<Target> {
        let ship = &self.pilots.get(&session)?.ship;
        Some(Target { position: ship.position, radius: ship.spec.radius, velocity: ship.velocity })
    }

    /// What a ship of the session may follow, orbit or align to: a body of the system, a wreck or a can that lies in the scene, or another ship of the scene.
    pub(crate) fn target_of(&self, target_id: i64, session: SessionId) -> Option<Target> {
        if let Some(body) = self.system().body(target_id) {
            return Some(Target::at_rest(body.position, body.radius));
        }
        if let Some(wreck) = self.wrecks.get(&target_id) {
            return Some(Target::at_rest(wreck.position, wreck.radius));
        }
        let other = self.session_of(target_id).filter(|other| *other != session)?;
        self.target_of_ship(other)
    }

    /// The item ids of the ships, in the order they came into the scene.
    pub(crate) fn in_entry_order_ships(&self) -> Vec<i64> {
        self.in_entry_order().into_iter().map(|session| self.pilots[&session].ship.spec.ship_id).collect()
    }

    /// The trees of the NPCs.
    pub fn brains(&self) -> Option<&Brains> {
        self.brains.as_ref()
    }

    /// Runs the brains (`Brains::run`): what they order is done to the ships, and what the host has to hear is given back.
    pub(crate) fn tick_brains(&mut self, now: i64) -> Vec<Out> {
        let Some(mut brains) = self.brains.take() else {
            self.aggressions.clear();
            self.destroyed.clear();
            return Vec::new();
        };
        if brains.is_empty() {
            // no tree to hear of the offensive acts and the ships lost since the last poll: they are of no use to anyone
            self.aggressions.clear();
            self.destroyed.clear();
            self.brains = Some(brains);
            return Vec::new();
        }
        let out = brains.run(self, now);
        if let Some(spawned) = self.brains.replace(brains).filter(|spawned| !spawned.is_empty())
            && let Some(mine) = self.brains.as_mut()
        {
            mine.absorb(spawned);
        }
        out
    }

    /// The sessions of the NPCs in the scene, in the order they came.
    pub fn npc_sessions(&self) -> Vec<SessionId> {
        self.in_entry_order().into_iter().filter(|session| is_npc_session(*session)).collect()
    }

    /// The sessions of the players in the scene, in the order they came.
    pub fn player_sessions(&self) -> Vec<SessionId> {
        self.in_entry_order().into_iter().filter(|session| !is_npc_session(*session)).collect()
    }

    /// An NPC ship leaves the scene (unspawned): those who had it in view are told. `None` when there is no such NPC.
    pub fn remove_npc(&mut self, session: SessionId, now: i64) -> Option<Vec<Out>> {
        if !is_npc_session(session) {
            return None;
        }
        self.leave(session, crate::scene::Departure::Docked, now)
    }
}

#[cfg(test)]
mod tests;
