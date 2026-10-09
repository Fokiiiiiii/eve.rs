//! The simulation as the host sees it: messages in, messages out, one scene per solar system that has a ship in it.
//!
//! Nothing here reads a socket or a clock. [`Sim::handle`] takes a message and the time (a FILETIME) and answers with the
//! messages to send back; [`Sim::poll`] finishes what is due. `eve-sim` puts sockets and a timer around it.
mod dispatch;
mod spawn;
#[cfg(test)]
use self::dispatch::*;

use std::collections::{BTreeMap, HashMap};

use std::sync::{Arc, Mutex};

use eve_destiny::Vec3;

use eve_marshal::Value;

use crate::drones::{DroneCommand, DroneFlight};

use crate::link::{Enter, How, LaunchDrone, Message, PROTOCOL_VERSION, Spawn};

use crate::npc::{NpcPlace, is_npc_session};

use crate::scene::{Command, Departure, Out, Scene, SceneError, SessionId, Traveller};

use crate::ship::Course;

use crate::targeting::Order;

use crate::world::{SolarSystem, World};

use crate::wreck::{LootRights, WRECK_LIFETIME, Wreck};

/// How often the host's clock is polled for what is due (dockings); the link says it in `HelloAck`.
pub const POLL_HZ: u32 = 4;

/// The solar systems, built from the static data the first time each is asked for.
pub struct Universe {
    world: Option<World>,
    systems: Mutex<HashMap<i32, Arc<SolarSystem>>>,
}

impl Universe {
    pub fn new(world: World) -> Self {
        Self { world: Some(world), systems: Mutex::new(HashMap::new()) }
    }

    /// A universe of systems made by hand (tests).
    pub fn with_systems(systems: impl IntoIterator<Item = SolarSystem>) -> Self {
        Self { world: None, systems: Mutex::new(systems.into_iter().map(|s| (s.id, Arc::new(s))).collect()) }
    }

    /// A scene for a system: it knows which wreck each ship leaves when the universe has the static data to say.
    fn scene_of(&self, system: Arc<SolarSystem>) -> Scene {
        let mut scene = Scene::new(system);
        if let Some(world) = &self.world {
            scene.set_wreck_catalog(world.wreck_catalog());
        }
        scene
    }

    pub fn system(&self, id: i32) -> Result<Arc<SolarSystem>, String> {
        let mut cache = self.systems.lock().expect("the cache lock is never held across a panic");
        if let Some(system) = cache.get(&id) {
            return Ok(Arc::clone(system));
        }
        let world = self.world.as_ref().ok_or_else(|| format!("UNKNOWN_SYSTEM {id}"))?;
        let system = Arc::new(world.system(id).map_err(|e| format!("UNKNOWN_SYSTEM {id}: {e}"))?);
        cache.insert(id, Arc::clone(&system));
        Ok(system)
    }
}

/// What the host sent that the simulation does not take.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ProtocolError(pub String);

impl std::fmt::Display for ProtocolError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(&self.0)
    }
}

impl std::error::Error for ProtocolError {}

/// The scenes of one host connection.
pub struct Sim {
    universe: Arc<Universe>,
    scenes: BTreeMap<i32, Scene>,
    sessions: HashMap<SessionId, i32>,
    /// The sessions whose ship was destroyed and have none yet: the system whose scene keeps their ballpark until the host gives them a capsule.
    widowed: HashMap<SessionId, i32>,
}

impl Sim {
    pub fn new(universe: Arc<Universe>) -> Self {
        Self { universe, scenes: BTreeMap::new(), sessions: HashMap::new(), widowed: HashMap::new() }
    }

    /// The ships in space.
    pub fn ships(&self) -> usize {
        self.sessions.len()
    }

    /// What the host is told of what scenes have to say: nothing that is for an NPC (nobody is there to hear it) but that it was destroyed, and by whom: the
    /// host made the NPC (it knows what it was worth) and pays the bounty to whoever dealt the last of its hull.
    pub(crate) fn messages(outs: impl IntoIterator<Item = Out>) -> Vec<Message> {
        outs.into_iter()
            .filter(|out| !is_npc_session(out.session()) || matches!(out, Out::Destroyed { .. }))
            .map(Self::out_message)
            .collect()
    }

    fn scene_of(&mut self, session: SessionId) -> Option<&mut Scene> {
        let system = self.sessions.get(&session)?;
        self.scenes.get_mut(system)
    }
}

#[cfg(test)]
mod tests;
