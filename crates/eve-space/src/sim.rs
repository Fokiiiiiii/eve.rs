//! The simulation as the host sees it: messages in, messages out, one scene per solar system that has a ship in it.
//!
//! Nothing here reads a socket or a clock. [`Sim::handle`] takes a message and the time (a FILETIME) and answers with the
//! messages to send back; [`Sim::poll`] finishes what is due. `eve-sim` puts sockets and a timer around it.

use std::collections::{BTreeMap, HashMap};
use std::sync::{Arc, Mutex};

use eve_destiny::Vec3;
use eve_marshal::Value;

use crate::link::{Enter, How, Message, PROTOCOL_VERSION};
use crate::npc::is_npc_session;
use crate::scene::{Command, Departure, Out, Scene, SceneError, SessionId, Traveller};
use crate::targeting::Order;
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
        (9, [gate]) if is_id(*gate) => Some(Command::Jump { gate_id: *gate as i64, destination_gate: 0 }),
        (9, [gate, leads_to]) if is_id(*gate) && is_id(*leads_to) => {
            Some(Command::Jump { gate_id: *gate as i64, destination_gate: *leads_to as i64 })
        }
        _ => None,
    }
}

/// The targeting order a code and its arguments say, if they say one (codes 10 to 15).
fn order_of(code: u8, args: &[f64]) -> Option<Order> {
    match (code, args) {
        (10, [target]) if is_id(*target) => Some(Order::Lock(*target as i64)),
        (11, [target]) if is_id(*target) => Some(Order::Cancel(*target as i64)),
        (12, targets) if targets.iter().all(|t| is_id(*t)) => {
            Some(Order::Remove(targets.iter().map(|t| *t as i64).collect()))
        }
        (13, []) => Some(Order::Clear),
        (14, []) => Some(Order::Targets),
        (15, []) => Some(Order::Targeters),
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

    /// What the host is told of what scenes have to say: nothing that is for an NPC (nobody is there to hear it).
    pub(crate) fn messages(outs: impl IntoIterator<Item = Out>) -> Vec<Message> {
        outs.into_iter().filter(|out| !is_npc_session(out.session())).map(Self::out_message).collect()
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
            Out::Jumping { session, command, destination_system, destination_gate } => {
                Message::Jumping { session_id: session, command, destination_system, destination_gate }
            }
            Out::Docked { session, station_id, condition } => {
                Message::Docked { session_id: session, station_id, condition }
            }
            Out::Condition { session, command, condition } => {
                Message::Condition { session_id: session, command, condition }
            }
            Out::Refused { session, command, reason } => {
                Message::Refused { session_id: session, command, reason: reason.to_string() }
            }
            Out::Targets { session, command, pending, targets } => {
                Message::Targets { session_id: session, command, pending, targets }
            }
            Out::TargetRefused { session, command, reason, target_type, desired_range, actual_distance } => {
                Message::TargetRefused {
                    session_id: session,
                    command,
                    reason: reason.to_string(),
                    target_type,
                    desired_range,
                    actual_distance,
                }
            }
        }
    }

    fn scene_of(&mut self, session: SessionId) -> Option<&mut Scene> {
        let system = self.sessions.get(&session)?;
        self.scenes.get_mut(system)
    }

    /// An undock: the answer, and the notifications for the ships that are in view of the one that came out.
    fn enter(&mut self, enter: Enter, now: i64) -> Vec<Message> {
        let Enter { session_id, solar_system_id, how, station_id, ship } = enter;
        let reply = |outcome| vec![Message::EnterReply { session_id, outcome }];
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
            Ok(told) => {
                self.sessions.insert(session_id, solar_system_id);
                let mut messages = reply(Ok(()));
                messages.extend(Self::messages(told));
                messages
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
            Message::Enter(enter) => return Ok(self.enter(*enter, now)),
            Message::Leave { session_id, why } => {
                let why = match why {
                    0 => Departure::Docked,
                    1 => Departure::LoggedOff,
                    _ => Departure::Jumped,
                };
                let told = match self.scene_of(session_id) {
                    Some(scene) => scene.leave(session_id, why, now).unwrap_or_default(),
                    None => Vec::new(),
                };
                self.forget(session_id);
                told
            }
            Message::Bind { session_id } => {
                self.scene_of(session_id).map(|s| s.bind(session_id, now)).unwrap_or_default()
            }
            Message::UpdateState { session_id } => {
                self.scene_of(session_id).map(|s| s.update_state(session_id, now)).unwrap_or_default()
            }
            Message::Activate { session_id, effect } => match self.scene_of(session_id) {
                Some(scene) => scene.activate(session_id, *effect, now),
                None => vec![Out::Refused { session: session_id, command: 17, reason: "NO_SHIP_IN_SPACE" }],
            },
            Message::Command { session_id, command: 16, args } => {
                match (self.scene_of(session_id), args.as_slice()) {
                    (Some(scene), [module]) if is_id(*module) => {
                        scene.deactivate(session_id, *module as i64, now)
                    }
                    (Some(_), _) => {
                        vec![Out::Refused { session: session_id, command: 16, reason: "UNKNOWN_COMMAND" }]
                    }
                    (None, _) => {
                        vec![Out::Refused { session: session_id, command: 16, reason: "NO_SHIP_IN_SPACE" }]
                    }
                }
            }
            Message::Command { session_id, command: 18, .. } => match self.scene_of(session_id) {
                Some(scene) => scene.heal(session_id, now),
                None => vec![Out::Refused { session: session_id, command: 18, reason: "NO_SHIP_IN_SPACE" }],
            },
            Message::Command { session_id, command: 19, .. } => match self.scene_of(session_id) {
                Some(scene) => scene.condition(session_id, now),
                None => vec![Out::Refused { session: session_id, command: 19, reason: "NO_SHIP_IN_SPACE" }],
            },
            Message::Command { session_id, command, args } => {
                if let Some(order) = order_of(command, &args) {
                    match self.scene_of(session_id) {
                        Some(scene) => scene.target_order(session_id, order, now),
                        None => {
                            vec![Out::Refused { session: session_id, command, reason: "NO_SHIP_IN_SPACE" }]
                        }
                    }
                } else {
                    match command_of(command, &args) {
                        Some(cmd) => match self.scene_of(session_id) {
                            Some(scene) => scene.command(session_id, cmd, now),
                            None => vec![Out::Refused {
                                session: session_id,
                                command,
                                reason: "NO_SHIP_IN_SPACE",
                            }],
                        },
                        None => {
                            vec![Out::Refused { session: session_id, command, reason: "UNKNOWN_COMMAND" }]
                        }
                    }
                }
            }
            Message::ShipSpec { session_id, ship } => {
                self.scene_of(session_id).map(|s| s.respec(session_id, *ship, now)).unwrap_or_default()
            }
            other => {
                return Err(ProtocolError(format!(
                    "a message of kind {} is for the host, not the simulation",
                    other.kind()
                )));
            }
        };
        Ok(Self::messages(outs))
    }

    /// What is due at `now`: ships that finished docking leave their scenes, ships whose jump is due arrive in the scene of the other system,
    /// and ships that have come into view or left it are told of one another.
    pub fn poll(&mut self, now: i64) -> Vec<Message> {
        let outs: Vec<Out> = self.scenes.values_mut().flat_map(|scene| scene.poll(now)).collect();
        for out in &outs {
            if let Out::Docked { session, .. } = out {
                self.forget(*session);
            }
        }
        let mut messages: Vec<Message> = Self::messages(outs);
        let mut travellers: Vec<Traveller> = Vec::new();
        for scene in self.scenes.values_mut() {
            let (gone, told) = scene.due_jumps(now);
            travellers.extend(gone);
            messages.extend(Self::messages(told));
        }
        for traveller in travellers {
            messages.extend(self.arrive(traveller, now));
        }
        self.scenes.retain(|_, scene| !scene.is_empty());
        messages
    }

    /// A ship whose jump is due appears at the gate it leads to (the scene of that system is made when it has none): the host is told it has
    /// gone. A jump that cannot be finished leaves the ship where it was, and the host is told the command was refused.
    fn arrive(&mut self, traveller: Traveller, now: i64) -> Vec<Message> {
        let (session, from_system, from_gate, to_system, to_gate) = (
            traveller.session,
            traveller.from_system,
            traveller.from_gate,
            traveller.to_system,
            traveller.to_gate,
        );
        let failed = |sim: &mut Self, traveller: Traveller| {
            let mut messages = vec![Message::Refused {
                session_id: session,
                command: 9,
                reason: "DESTINATION_UNAVAILABLE".to_string(),
            }];
            if let Some(scene) = sim.scenes.get_mut(&traveller.from_system) {
                messages.extend(Self::messages(scene.restore(traveller, now)));
            }
            messages
        };
        let Ok(system) = self.universe.system(to_system) else { return failed(self, traveller) };
        let scene = self.scenes.entry(to_system).or_insert_with(|| Scene::new(system));
        match scene.arrive(traveller, now) {
            Ok(told) => {
                self.sessions.insert(session, to_system);
                let mut messages =
                    vec![Message::Jumped { session_id: session, from_system, from_gate, to_system, to_gate }];
                messages.extend(Self::messages(told));
                messages
            }
            Err(traveller) => {
                if scene.is_empty() {
                    self.scenes.remove(&to_system);
                }
                failed(self, *traveller)
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::health::Condition;
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

    fn afterburner(module: i64) -> crate::modules::Effect {
        crate::modules::Effect {
            module_id: module,
            module_type: 21857,
            effect_id: 6731,
            fx: "effects.Afterburner".to_string(),
            duration_ms: 10_000.0,
            capacitor_need: 5.0,
            repeat: 1000,
            target_id: 0,
            on: None,
            off: None,
            on_changes: Vec::new(),
            off_changes: Vec::new(),
        }
    }

    #[test]
    fn a_ship_is_healed_and_asked_what_it_has_taken_by_commands_18_and_19() {
        let mut sim = undocked();
        let asked =
            sim.handle(Message::Command { session_id: SESSION, command: 19, args: vec![] }, NOON).unwrap();
        assert_eq!(
            asked,
            [Message::Condition { session_id: SESSION, command: 19, condition: Condition::FULL }]
        );
        let healed =
            sim.handle(Message::Command { session_id: SESSION, command: 18, args: vec![] }, NOON).unwrap();
        assert_eq!(
            healed.last(),
            Some(&Message::Accepted { session_id: SESSION, command: 18, at: NOON }),
            "the answer comes after what the pilot is told"
        );
        for command in [18, 19] {
            let none = sim.handle(Message::Command { session_id: 99, command, args: vec![] }, NOON).unwrap();
            assert_eq!(
                none,
                [Message::Refused { session_id: 99, command, reason: "NO_SHIP_IN_SPACE".to_string() }]
            );
        }
    }

    #[test]
    fn an_effect_is_started_by_a_message_and_stopped_by_the_command_and_each_is_answered_under_its_code() {
        let mut sim = undocked();
        let started = sim
            .handle(
                Message::Activate { session_id: SESSION, effect: Box::new(afterburner(77)) },
                NOON + 40 * SECOND,
            )
            .unwrap();
        assert!(
            started.contains(&Message::Accepted { session_id: SESSION, command: 17, at: NOON + 40 * SECOND }),
            "{started:?}"
        );
        let again = sim
            .handle(
                Message::Activate { session_id: SESSION, effect: Box::new(afterburner(77)) },
                NOON + 41 * SECOND,
            )
            .unwrap();
        assert_eq!(
            again,
            [Message::Refused { session_id: SESSION, command: 17, reason: "ALREADY_ACTIVE".to_string() }]
        );
        let stopped = sim
            .handle(
                Message::Command { session_id: SESSION, command: 16, args: vec![77.0] },
                NOON + 42 * SECOND,
            )
            .unwrap();
        assert_eq!(stopped, [Message::Accepted { session_id: SESSION, command: 16, at: NOON + 42 * SECOND }]);
        let unknown = sim
            .handle(
                Message::Command { session_id: SESSION, command: 16, args: vec![77.0] },
                NOON + 43 * SECOND,
            )
            .unwrap();
        assert_eq!(
            unknown,
            [Message::Accepted { session_id: SESSION, command: 16, at: NOON + 43 * SECOND }],
            "the effect is still running until its cycle ends"
        );
        let after = sim
            .handle(
                Message::Command { session_id: SESSION, command: 16, args: vec![99.0] },
                NOON + 43 * SECOND,
            )
            .unwrap();
        assert_eq!(
            after,
            [Message::Refused { session_id: SESSION, command: 16, reason: "NOT_ACTIVE".to_string() }]
        );
        // a command that is not one is refused, and so is one for a ship that is not there
        let bad =
            sim.handle(Message::Command { session_id: SESSION, command: 16, args: vec![] }, NOON).unwrap();
        assert_eq!(
            bad,
            [Message::Refused { session_id: SESSION, command: 16, reason: "UNKNOWN_COMMAND".to_string() }]
        );
        let gone =
            sim.handle(Message::Command { session_id: 555, command: 16, args: vec![77.0] }, NOON).unwrap();
        assert_eq!(
            gone,
            [Message::Refused { session_id: 555, command: 16, reason: "NO_SHIP_IN_SPACE".to_string() }]
        );
        let none = sim
            .handle(Message::Activate { session_id: 555, effect: Box::new(afterburner(77)) }, NOON)
            .unwrap();
        assert_eq!(
            none,
            [Message::Refused { session_id: 555, command: 17, reason: "NO_SHIP_IN_SPACE".to_string() }]
        );
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
        assert_eq!(
            command_of(9, &[50_016_630.0, 50_016_839.0]),
            Some(Command::Jump { gate_id: 50_016_630, destination_gate: 50_016_839 })
        );
        assert_eq!(
            command_of(9, &[50_016_630.0]),
            Some(Command::Jump { gate_id: 50_016_630, destination_gate: 0 }),
            "the gate alone is enough"
        );
        for (code, args) in [
            (1, vec![]),
            (1, vec![1.0, 2.0]),
            (2, vec![1.0]),
            (3, vec![1.0]),
            (4, vec![1.5]),
            (4, vec![f64::NAN]),
            (4, vec![1e300]),
            (9, vec![]),
            (9, vec![1.5]),
            (9, vec![50_016_630.0, f64::NAN]),
            (9, vec![1.0, 2.0, 3.0]),
            (10, vec![]),
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
            sim.handle(Message::Command { session_id: SESSION, command: 10, args: vec![] }, NOON).unwrap();
        assert_eq!(refused(out[0].clone()), (10, "UNKNOWN_COMMAND".to_string()));
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
            [Message::Docked { session_id: SESSION, station_id: STATION, condition: Condition::FULL }]
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
        let out =
            sim.handle(Message::ShipSpec { session_id: SESSION, ship: Box::new(faster) }, NOON).unwrap();
        assert_eq!(names(&out), ["DoDestinyUpdate"]);
    }

    #[test]
    fn a_message_meant_for_the_host_is_a_protocol_error() {
        let error = sim()
            .handle(Message::Docked { session_id: 1, station_id: 2, condition: Condition::FULL }, NOON)
            .unwrap_err();
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

    // ── jumps ────────────────────────────────────────────────────────────────

    fn gated_sim() -> Sim {
        let (here, there) = crate::scene::tests::gated_systems();
        Sim::new(Arc::new(Universe::with_systems([here, there])))
    }

    fn jump_command(gate: i64, leads_to: i64) -> Message {
        Message::Command { session_id: SESSION, command: 9, args: vec![gate as f64, leads_to as f64] }
    }

    #[test]
    fn a_jump_takes_the_ship_to_the_scene_of_the_other_system_when_it_is_due() {
        use crate::scene::tests::{ARRIVAL_GATE, NEAR_GATE, OTHER_SYSTEM};
        let mut sim = gated_sim();
        sim.handle(enter(SESSION, STATION, SYSTEM), NOON).unwrap();
        let out = sim.handle(jump_command(NEAR_GATE, ARRIVAL_GATE), NOON).unwrap();
        assert_eq!(names(&out[..1]), ["DoDestinyUpdate"]);
        assert_eq!(
            out[1],
            Message::Jumping {
                session_id: SESSION,
                command: 9,
                destination_system: OTHER_SYSTEM,
                destination_gate: ARRIVAL_GATE
            }
        );
        assert!(sim.poll(NOON + 3 * SECOND).is_empty(), "the ship is still where it was");
        assert_eq!(sim.scenes.keys().copied().collect::<Vec<_>>(), [SYSTEM]);

        let gone = sim.poll(NOON + 4 * SECOND);
        assert_eq!(
            gone,
            [Message::Jumped {
                session_id: SESSION,
                from_system: SYSTEM,
                from_gate: NEAR_GATE,
                to_system: OTHER_SYSTEM,
                to_gate: ARRIVAL_GATE
            }]
        );
        assert_eq!(sim.ships(), 1);
        assert_eq!(
            sim.scenes.keys().copied().collect::<Vec<_>>(),
            [OTHER_SYSTEM],
            "the scene it left has no one in it and is dropped"
        );
        assert!(sim.poll(NOON + 9 * SECOND).is_empty(), "it goes once");

        // the client binds the ballpark of the new system and is sent it
        let bound = sim.handle(Message::Bind { session_id: SESSION }, NOON + 5 * SECOND).unwrap();
        assert_eq!(bound.len(), 5);
        // and orders go to the ship where it is
        let out = sim
            .handle(Message::Command { session_id: SESSION, command: 1, args: vec![1.0] }, NOON + 6 * SECOND)
            .unwrap();
        assert_eq!(names(&out[..1]), ["DoDestinyUpdate"]);
        // it is far from the gate it came out of, so it cannot go back at once
        let out = sim.handle(jump_command(ARRIVAL_GATE, NEAR_GATE), NOON + 6 * SECOND).unwrap();
        assert_eq!(
            out,
            [Message::Refused {
                session_id: SESSION,
                command: 9,
                reason: "TOO_FAR_FROM_STARGATE".to_string()
            }]
        );
    }

    #[test]
    fn a_ship_that_is_asked_to_leave_while_it_jumps_leaves_from_the_scene_it_is_in() {
        use crate::scene::tests::{ARRIVAL_GATE, NEAR_GATE};
        let mut sim = gated_sim();
        sim.handle(enter(SESSION, STATION, SYSTEM), NOON).unwrap();
        sim.handle(jump_command(NEAR_GATE, ARRIVAL_GATE), NOON).unwrap();
        sim.poll(NOON + 4 * SECOND);
        assert!(
            sim.handle(Message::Leave { session_id: SESSION, why: 1 }, NOON + 5 * SECOND).unwrap().is_empty()
        );
        assert_eq!((sim.ships(), sim.scenes.len()), (0, 0));
    }

    #[test]
    fn a_jump_to_a_system_that_cannot_be_built_leaves_the_ship_where_it_was_and_says_so() {
        use crate::scene::tests::{ARRIVAL_GATE, NEAR_GATE};
        let (here, _) = crate::scene::tests::gated_systems();
        let mut sim = Sim::new(Arc::new(Universe::with_systems([here])));
        sim.handle(enter(SESSION, STATION, SYSTEM), NOON).unwrap();
        sim.handle(jump_command(NEAR_GATE, ARRIVAL_GATE), NOON).unwrap();
        assert_eq!(
            sim.poll(NOON + 4 * SECOND),
            [Message::Refused {
                session_id: SESSION,
                command: 9,
                reason: "DESTINATION_UNAVAILABLE".to_string()
            }]
        );
        assert_eq!((sim.ships(), sim.scenes.keys().copied().collect::<Vec<_>>()), (1, vec![SYSTEM]));
        // the ship takes orders and may try again
        let out = sim.handle(jump_command(NEAR_GATE, ARRIVAL_GATE), NOON + 5 * SECOND).unwrap();
        assert!(matches!(out[1], Message::Jumping { .. }), "{out:?}");
    }

    #[test]
    fn two_simulations_given_the_same_jump_send_the_same_bytes() {
        use crate::scene::tests::{ARRIVAL_GATE, NEAR_GATE};
        let run = || {
            let mut sim = gated_sim();
            sim.handle(enter(SESSION, STATION, SYSTEM), NOON).unwrap();
            let mut out = sim.handle(jump_command(NEAR_GATE, ARRIVAL_GATE), NOON).unwrap();
            out.extend(sim.poll(NOON + 4 * SECOND));
            out.extend(sim.handle(Message::Bind { session_id: SESSION }, NOON + 5 * SECOND).unwrap());
            out.iter().flat_map(Message::encode).collect::<Vec<u8>>()
        };
        assert_eq!(run(), run());
    }

    // ── other ships ──────────────────────────────────────────────────────────

    const OTHER: SessionId = 9;

    fn other_enter() -> Message {
        let mut ship = spec();
        ship.ship_id += 4;
        ship.character_id += 1;
        Message::Enter(Box::new(Enter {
            session_id: OTHER,
            solar_system_id: SYSTEM,
            how: How::Undock,
            station_id: STATION,
            ship,
        }))
    }

    fn recipients(messages: &[Message]) -> Vec<(u64, String)> {
        messages
            .iter()
            .map(|m| match m {
                Message::Notify { session_id, name, .. } => (*session_id, name.clone()),
                Message::EnterReply { session_id, .. } => (*session_id, "EnterReply".to_string()),
                Message::Docked { session_id, .. } => (*session_id, "Docked".to_string()),
                other => panic!("{other:?}"),
            })
            .collect()
    }

    #[test]
    fn a_second_ship_that_undocks_is_answered_and_shown_to_the_first_that_has_bound() {
        let mut sim = undocked();
        // the first has not bound: it is told nothing
        let reply = sim.handle(other_enter(), NOON).unwrap();
        assert_eq!(recipients(&reply), [(OTHER, "EnterReply".to_string())]);
        sim.handle(Message::Leave { session_id: OTHER, why: 1 }, NOON).unwrap();

        sim.handle(Message::Bind { session_id: SESSION }, NOON).unwrap();
        let reply = sim.handle(other_enter(), NOON + SECOND).unwrap();
        assert_eq!(
            recipients(&reply),
            [(OTHER, "EnterReply".to_string()), (SESSION, "DoDestinyUpdate".to_string())],
            "the answer first, then what the first ship is told"
        );
        assert_eq!(sim.ships(), 2);
    }

    #[test]
    fn a_ship_that_leaves_is_taken_from_the_view_of_the_ones_that_saw_it() {
        let mut sim = undocked();
        sim.handle(Message::Bind { session_id: SESSION }, NOON).unwrap();
        sim.handle(other_enter(), NOON + SECOND).unwrap();
        let told = sim.handle(Message::Leave { session_id: OTHER, why: 1 }, NOON + 2 * SECOND).unwrap();
        assert_eq!(recipients(&told), [(SESSION, "DoDestinyUpdate".to_string())]);
        assert_eq!(sim.ships(), 1);
    }

    #[test]
    fn a_ship_that_has_docked_is_taken_from_the_view_of_the_others_by_the_poll() {
        let mut sim = undocked();
        sim.handle(Message::Bind { session_id: SESSION }, NOON).unwrap();
        sim.handle(other_enter(), NOON + SECOND).unwrap();
        sim.handle(Message::Bind { session_id: OTHER }, NOON + SECOND).unwrap();
        sim.handle(
            Message::Command { session_id: OTHER, command: 4, args: vec![STATION as f64] },
            NOON + 2 * SECOND,
        )
        .unwrap();
        let gone = sim.poll(NOON + 6 * SECOND);
        assert_eq!(
            recipients(&gone),
            [(OTHER, "Docked".to_string()), (SESSION, "DoDestinyUpdate".to_string())]
        );
        assert_eq!(sim.ships(), 1, "the first ship is still there");
    }
}
