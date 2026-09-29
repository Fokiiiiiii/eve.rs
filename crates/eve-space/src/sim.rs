//! The simulation as the host sees it: messages in, messages out, one scene per solar system that has a ship in it.
//!
//! Nothing here reads a socket or a clock. [`Sim::handle`] takes a message and the time (a FILETIME) and answers with the
//! messages to send back; [`Sim::poll`] finishes what is due. `eve-sim` puts sockets and a timer around it.

use std::collections::{BTreeMap, HashMap};
use std::sync::{Arc, Mutex};

use eve_destiny::Vec3;
use eve_marshal::Value;

use crate::link::{Enter, How, Message, PROTOCOL_VERSION};
use crate::scene::{Command, Departure, Out, Scene, SceneError, SessionId};
use crate::world::{SolarSystem, World};

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
}

/// The command a code and its arguments say, if they say one.
fn command_of(code: u8, args: &[f64]) -> Option<Command> {
    match (code, args) {
        (1, [fraction]) => Some(Command::SetSpeedFraction(*fraction)),
        (2, [x, y, z]) => Some(Command::GotoDirection(Vec3::new(*x, *y, *z))),
        (3, []) => Some(Command::Stop),
        (4, [station]) if is_id(*station) => Some(Command::Dock { station_id: *station as i64 }),
        (5, [target, range]) if is_id(*target) && range.is_finite() => {
            Some(Command::FollowBall { target_id: *target as i64, range: *range })
        }
        (6, [target, distance]) if is_id(*target) && distance.is_finite() => {
            Some(Command::Orbit { target_id: *target as i64, distance: *distance })
        }
        (7, [target]) if is_id(*target) => Some(Command::AlignTo { target_id: *target as i64 }),
        (8, [target, range]) if is_id(*target) && range.is_finite() => {
            Some(Command::WarpTo { target_id: *target as i64, range: *range })
        }
        _ => None,
    }
}

/// Whether a link number is an id: a whole number the link's `f64` holds exactly.
fn is_id(value: f64) -> bool {
    value.is_finite() && value.fract() == 0.0 && value.abs() < 9_007_199_254_740_992.0
}

impl Sim {
    pub fn new(universe: Arc<Universe>) -> Self {
        Self { universe, scenes: BTreeMap::new(), sessions: HashMap::new() }
    }

    /// The ships in space.
    pub fn ships(&self) -> usize {
        self.sessions.len()
    }

    fn out_message(out: Out) -> Message {
        match out {
            Out::Notify { session, name, id_type, args } => {
                let args = eve_marshal::encode(&Value::Tuple(args)).expect("the sim's own values encode");
                Message::Notify {
                    session_id: session,
                    name: name.to_string(),
                    id_type: id_type.to_string(),
                    args,
                }
            }
            Out::Accepted { session, command, at } => Message::Accepted { session_id: session, command, at },
            Out::Approaching { session, command, distance } => {
                Message::Approaching { session_id: session, command, distance }
            }
            Out::Docked { session, station_id } => Message::Docked { session_id: session, station_id },
            Out::Refused { session, command, reason } => {
                Message::Refused { session_id: session, command, reason: reason.to_string() }
            }
        }
    }

    fn scene_of(&mut self, session: SessionId) -> Option<&mut Scene> {
        let system = self.sessions.get(&session)?;
        self.scenes.get_mut(system)
    }

    fn enter(&mut self, enter: Enter, now: i64) -> Message {
        let Enter { session_id, solar_system_id, how, station_id, ship } = enter;
        let reply = |outcome| Message::EnterReply { session_id, outcome };
        if how == How::InSpace {
            return reply(Err("NOT_SUPPORTED: only an undock puts a ship in space for now".to_string()));
        }
        if self.sessions.contains_key(&session_id) {
            return reply(Err(format!("ALREADY_IN_SPACE {session_id}")));
        }
        let system = match self.universe.system(solar_system_id) {
            Ok(system) => system,
            Err(reason) => return reply(Err(reason)),
        };
        let scene = self.scenes.entry(solar_system_id).or_insert_with(|| Scene::new(system));
        match scene.undock(session_id, ship, station_id, now) {
            Ok(()) => {
                self.sessions.insert(session_id, solar_system_id);
                reply(Ok(()))
            }
            Err(SceneError::UnknownStation(station)) => {
                if scene.is_empty() {
                    self.scenes.remove(&solar_system_id);
                }
                reply(Err(format!("UNKNOWN_STATION {station}")))
            }
            Err(SceneError::AlreadyInSpace(_)) => reply(Err(format!("ALREADY_IN_SPACE {session_id}"))),
        }
    }

    fn forget(&mut self, session: SessionId) {
        if let Some(system) = self.sessions.remove(&session)
            && self.scenes.get(&system).is_some_and(Scene::is_empty)
        {
            self.scenes.remove(&system);
        }
    }

    /// One message from the host.
    pub fn handle(&mut self, message: Message, now: i64) -> Result<Vec<Message>, ProtocolError> {
        let outs = match message {
            Message::Hello { .. } => {
                return Ok(vec![Message::HelloAck { version: PROTOCOL_VERSION, poll_hz: POLL_HZ }]);
            }
            Message::Enter(enter) => return Ok(vec![self.enter(*enter, now)]),
            Message::Leave { session_id, why } => {
                let why = match why {
                    0 => Departure::Docked,
                    1 => Departure::LoggedOff,
                    _ => Departure::Jumped,
                };
                if let Some(scene) = self.scene_of(session_id) {
                    scene.leave(session_id, why);
                }
                self.forget(session_id);
                Vec::new()
            }
            Message::Bind { session_id } => {
                self.scene_of(session_id).map(|s| s.bind(session_id, now)).unwrap_or_default()
            }
            Message::UpdateState { session_id } => {
                self.scene_of(session_id).map(|s| s.update_state(session_id, now)).unwrap_or_default()
            }
            Message::Command { session_id, command, args } => match command_of(command, &args) {
                Some(cmd) => match self.scene_of(session_id) {
                    Some(scene) => scene.command(session_id, cmd, now),
                    None => vec![Out::Refused { session: session_id, command, reason: "NO_SHIP_IN_SPACE" }],
                },
                None => vec![Out::Refused { session: session_id, command, reason: "UNKNOWN_COMMAND" }],
            },
            Message::ShipSpec { session_id, ship } => {
                self.scene_of(session_id).map(|s| s.respec(session_id, ship, now)).unwrap_or_default()
            }
            other => {
                return Err(ProtocolError(format!(
                    "a message of kind {} is for the host, not the simulation",
                    other.kind()
                )));
            }
        };
        Ok(outs.into_iter().map(Self::out_message).collect())
    }

    /// What is due at `now`: ships that finished docking leave their scenes.
    pub fn poll(&mut self, now: i64) -> Vec<Message> {
        let outs: Vec<Out> = self.scenes.values_mut().flat_map(|scene| scene.poll(now)).collect();
        for out in &outs {
            if let Out::Docked { session, .. } = out {
                self.forget(*session);
            }
        }
        self.scenes.retain(|_, scene| !scene.is_empty());
        outs.into_iter().map(Self::out_message).collect()
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::scene::tests::{NOON, SESSION, STATION, spec, system};
    use crate::world::SolarSystem;

    const SYSTEM: i32 = 30_000_001;
    const SECOND: i64 = 10_000_000;

    /// A system whose station is big enough that a ship leaving it is at once within docking range, as in the reference.
    fn big_station() -> SolarSystem {
        let mut big = (*system()).clone();
        big.bodies[0].radius = 70_000.0;
        big
    }

    fn sim() -> Sim {
        Sim::new(Arc::new(Universe::with_systems([big_station()])))
    }

    fn enter(session: SessionId, station_id: i64, system: i32) -> Message {
        Message::Enter(Box::new(Enter {
            session_id: session,
            solar_system_id: system,
            how: How::Undock,
            station_id,
            ship: spec(),
        }))
    }

    fn undocked() -> Sim {
        let mut sim = sim();
        let reply = sim.handle(enter(SESSION, STATION, SYSTEM), NOON).unwrap();
        assert_eq!(reply, [Message::EnterReply { session_id: SESSION, outcome: Ok(()) }]);
        sim
    }

    fn names(messages: &[Message]) -> Vec<String> {
        messages
            .iter()
            .map(|m| match m {
                Message::Notify { name, .. } => name.clone(),
                other => format!("{other:?}"),
            })
            .collect()
    }

    #[test]
    fn hello_is_answered_with_the_version_and_the_poll_rate() {
        let reply = sim().handle(Message::Hello { version: 1 }, NOON).unwrap();
        assert_eq!(reply, [Message::HelloAck { version: PROTOCOL_VERSION, poll_hz: POLL_HZ }]);
    }

    #[test]
    fn an_undock_puts_the_ship_in_space_and_binding_sends_it_the_scene_as_marshalled_notifications() {
        let mut sim = undocked();
        assert_eq!(sim.ships(), 1);
        let out = sim.handle(Message::Bind { session_id: SESSION }, NOON).unwrap();
        assert_eq!(
            names(&out),
            [
                "OnSetTimeDilation",
                "DoSimClockRebase",
                "DoDestinyUpdate",
                "DoDestinyUpdate",
                "DoDestinyUpdate",
                "DoDestinyUpdate"
            ]
        );
        let Message::Notify { id_type, args, session_id, .. } = &out[0] else { panic!() };
        assert_eq!((id_type.as_str(), *session_id), ("clientID", SESSION));
        // the arguments are a marshalled tuple
        let decoded = eve_marshal::decode_exact(args).unwrap();
        assert_eq!(decoded, Value::Tuple(vec![Value::Int(1), Value::Int(1), Value::Int(100_000_000)]));
    }

    #[test]
    fn an_undock_that_cannot_be_done_says_why_and_leaves_nothing_behind() {
        let mut sim = sim();
        let reason = |message: Vec<Message>| match &message[0] {
            Message::EnterReply { outcome: Err(reason), .. } => reason.clone(),
            other => panic!("{other:?}"),
        };
        assert!(reason(sim.handle(enter(SESSION, STATION, 42), NOON).unwrap()).starts_with("UNKNOWN_SYSTEM"));
        assert!(reason(sim.handle(enter(SESSION, 42, SYSTEM), NOON).unwrap()).starts_with("UNKNOWN_STATION"));
        assert_eq!((sim.ships(), sim.scenes.len()), (0, 0));
        sim.handle(enter(SESSION, STATION, SYSTEM), NOON).unwrap();
        assert!(
            reason(sim.handle(enter(SESSION, STATION, SYSTEM), NOON).unwrap())
                .starts_with("ALREADY_IN_SPACE")
        );
        let in_space = Message::Enter(Box::new(Enter {
            session_id: 9,
            solar_system_id: SYSTEM,
            how: How::InSpace,
            station_id: 0,
            ship: spec(),
        }));
        assert!(reason(sim.handle(in_space, NOON).unwrap()).starts_with("NOT_SUPPORTED"));
    }

    #[test]
    fn commands_are_read_from_their_codes_and_arguments() {
        assert_eq!(command_of(1, &[0.5]), Some(Command::SetSpeedFraction(0.5)));
        assert_eq!(command_of(2, &[1.0, 0.0, 0.0]), Some(Command::GotoDirection(Vec3::new(1.0, 0.0, 0.0))));
        assert_eq!(command_of(3, &[]), Some(Command::Stop));
        assert_eq!(command_of(4, &[60_015_249.0]), Some(Command::Dock { station_id: 60_015_249 }));
        for (code, args) in [
            (1, vec![]),
            (1, vec![1.0, 2.0]),
            (2, vec![1.0]),
            (3, vec![1.0]),
            (4, vec![1.5]),
            (4, vec![f64::NAN]),
            (4, vec![1e300]),
            (9, vec![]),
        ] {
            assert_eq!(command_of(code, &args), None, "{code} {args:?}");
        }
    }

    #[test]
    fn a_command_the_scene_cannot_take_is_refused_with_the_reason() {
        let mut sim = undocked();
        let refused = |message: Message| match message {
            Message::Refused { command, reason, .. } => (command, reason),
            other => panic!("{other:?}"),
        };
        let out =
            sim.handle(Message::Command { session_id: SESSION, command: 9, args: vec![] }, NOON).unwrap();
        assert_eq!(refused(out[0].clone()), (9, "UNKNOWN_COMMAND".to_string()));
        let out = sim.handle(Message::Command { session_id: 99, command: 3, args: vec![] }, NOON).unwrap();
        assert_eq!(refused(out[0].clone()), (3, "NO_SHIP_IN_SPACE".to_string()));
        let out =
            sim.handle(Message::Command { session_id: SESSION, command: 4, args: vec![42.0] }, NOON).unwrap();
        assert_eq!(refused(out[0].clone()), (4, "STATION_NOT_FOUND".to_string()));
    }

    #[test]
    fn a_command_is_answered_with_the_update_and_a_docking_is_finished_by_the_poll() {
        let mut sim = undocked();
        let out =
            sim.handle(Message::Command { session_id: SESSION, command: 1, args: vec![1.0] }, NOON).unwrap();
        assert_eq!(names(&out[..1]), ["DoDestinyUpdate"]);
        assert_eq!(out[1], Message::Accepted { session_id: SESSION, command: 1, at: NOON });

        // the ship has not gone anywhere yet, so it is still within range of the station
        let out = sim
            .handle(Message::Command { session_id: SESSION, command: 4, args: vec![STATION as f64] }, NOON)
            .unwrap();
        assert_eq!(names(&out[..2]), ["OnDockingAccepted", "DoDestinyUpdate"]);
        assert_eq!(out[2], Message::Accepted { session_id: SESSION, command: 4, at: NOON });
        assert!(sim.poll(NOON + 3 * SECOND).is_empty(), "three seconds are not enough");
        assert_eq!(
            sim.poll(NOON + 4 * SECOND),
            [Message::Docked { session_id: SESSION, station_id: STATION }]
        );
        assert_eq!((sim.ships(), sim.scenes.len()), (0, 0), "an empty scene is dropped");
        assert!(sim.poll(NOON + 9 * SECOND).is_empty());
    }

    #[test]
    fn leaving_takes_the_ship_out_and_the_scene_with_it() {
        let mut sim = undocked();
        assert!(sim.handle(Message::Leave { session_id: SESSION, why: 1 }, NOON).unwrap().is_empty());
        assert_eq!((sim.ships(), sim.scenes.len()), (0, 0));
        assert!(
            sim.handle(Message::Leave { session_id: SESSION, why: 1 }, NOON).unwrap().is_empty(),
            "leaving twice is harmless"
        );
        assert!(sim.handle(Message::Bind { session_id: SESSION }, NOON).unwrap().is_empty());
    }

    #[test]
    fn new_numbers_for_a_ship_are_sent_on() {
        let mut sim = undocked();
        let mut faster = spec();
        faster.max_velocity = 500.0;
        let out = sim.handle(Message::ShipSpec { session_id: SESSION, ship: faster }, NOON).unwrap();
        assert_eq!(names(&out), ["DoDestinyUpdate"]);
    }

    #[test]
    fn a_message_meant_for_the_host_is_a_protocol_error() {
        let error = sim().handle(Message::Docked { session_id: 1, station_id: 2 }, NOON).unwrap_err();
        assert!(error.0.contains("kind 10"), "{error}");
    }

    #[test]
    fn two_simulations_given_the_same_messages_send_the_same_bytes() {
        let run = || {
            let mut sim = undocked();
            let mut out = sim.handle(Message::Bind { session_id: SESSION }, NOON).unwrap();
            out.extend(
                sim.handle(
                    Message::Command { session_id: SESSION, command: 2, args: vec![0.0, 0.0, 1.0] },
                    NOON + 3 * SECOND,
                )
                .unwrap(),
            );
            out.extend(sim.handle(Message::UpdateState { session_id: SESSION }, NOON + 6 * SECOND).unwrap());
            out.iter().flat_map(Message::encode).collect::<Vec<u8>>()
        };
        assert_eq!(run(), run());
    }
}
