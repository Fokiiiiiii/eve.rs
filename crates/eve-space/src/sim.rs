//! The simulation as the host sees it: messages in, messages out, one scene per solar system that has a ship in it.
//!
//! Nothing here reads a socket or a clock. [`Sim::handle`] takes a message and the time (a FILETIME) and answers with the
//! messages to send back; [`Sim::poll`] finishes what is due. `eve-sim` puts sockets and a timer around it.

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
            Out::Position { session, command, position } => Message::Position { session_id: session, command, position: [position.x, position.y, position.z] },
            Out::Approaching { session, command, distance } => {
                Message::Approaching { session_id: session, command, distance }
            }
            Out::Jumping { session, command, destination_system, destination_gate } => {
                Message::Jumping { session_id: session, command, destination_system, destination_gate }
            }
            Out::Docked { session, station_id, condition } => {
                Message::Docked { session_id: session, station_id, condition }
            }
            Out::Arrived { session, belt_id, position } => Message::Arrived {
                session_id: session,
                belt_id,
                position: [position.x, position.y, position.z],
            },
            Out::Condition { session, command, condition } => {
                Message::Condition { session_id: session, command, condition }
            }
            Out::Refused { session, command, reason } => {
                Message::Refused { session_id: session, command, reason: reason.to_string() }
            }
            Out::Destroyed {
                session,
                ship_id,
                wreck,
                wreck_kind,
                position,
                direction,
                radius,
                killer_ship,
            } => Message::Destroyed {
                session_id: session,
                ship_id,
                wreck_id: wreck.unwrap_or(0),
                wreck_kind,
                position: [position.x, position.y, position.z],
                direction: [direction.x, direction.y, direction.z],
                radius,
                killer_ship_id: killer_ship.unwrap_or(0),
            },
            Out::ChargeSpent { session, module_id, rounds_left } => {
                Message::ChargeSpent { session_id: session, module_id, rounds_left }
            }
            Out::ChargeAdjusted { request_id, running, before, after, .. } => {
                Message::ChargeAdjusted { request_id, running, before, after }
            }
            Out::ContainerPlaced { session, container_id, position, direction } => Message::ContainerPlaced {
                session_id: session,
                container_id,
                position: [position.x, position.y, position.z],
                direction: [direction.x, direction.y, direction.z],
            },
            Out::Measured { session, command, distance } => {
                Message::Measured { session_id: session, command, distance }
            }
            Out::Targets { session, command, pending, targets } => {
                Message::Targets { session_id: session, command, pending, targets }
            }
            Out::DroneScooped { session, drone_id } => {
                Message::DroneScooped { session_id: session, drone_id }
            }
            Out::DroneReleased { session, drone_id, position } => Message::DroneReleased {
                session_id: session,
                drone_id,
                position: [position.x, position.y, position.z],
            },
            Out::DroneResults { session, command, results } => Message::DroneResults {
                session_id: session,
                command,
                results: results
                    .into_iter()
                    .map(|(drone, reason)| (drone, reason.unwrap_or("").to_string()))
                    .collect(),
            },
            Out::DroneWorkCycle { session, character_id, ship_id, solar_system_id, drone_id, target_id, cycle_id, command, mining_amount_m3, waste_multiplier, waste_probability, access_difficulty_bonus, roll } => Message::DroneWorkCycle {
                session_id: session, character_id, ship_id, solar_system_id, drone_id, target_id, cycle_id, command,
                mining_amount_m3, waste_multiplier, waste_probability, access_difficulty_bonus, roll,
            },
            Out::RemoteRepairCycle { session, module_id, target_id, cycle_id, cycle_number, cycle_at_ms, cycle_duration_ms, ends_effect_after_cycle, base_amount, bonus_per_cycle, bonus_max } => Message::RemoteRepairCycle {
                session_id: session, module_id, target_id, cycle_id, cycle_number, cycle_at_ms, cycle_duration_ms, ends_effect_after_cycle, base_amount, bonus_per_cycle, bonus_max,
            },
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

    fn enter(&mut self, enter: Enter, now: i64) -> Vec<Message> {
        let Enter { session_id, solar_system_id, how, station_id, ship, position, direction } = enter;
        let reply = |outcome| vec![Message::EnterReply { session_id, outcome }];
        if self.sessions.contains_key(&session_id) {
            return reply(Err(format!("ALREADY_IN_SPACE {session_id}")));
        }
        let system = match self.universe.system(solar_system_id) {
            Ok(system) => system,
            Err(reason) => return reply(Err(reason)),
        };
        let scene = self.scenes.entry(solar_system_id).or_insert_with(|| self.universe.scene_of(system));
        let entered = match how {
            How::Undock => scene.undock(session_id, ship, station_id, now),
            How::InSpace => {
                let (Some(position), Some(direction)) = (position, direction) else {
                    return reply(Err("SPACE_STATE_REQUIRED".to_string()));
                };
                if position.iter().chain(direction.iter()).any(|value| !value.is_finite()) ||
                    direction.iter().all(|value| *value == 0.0)
                {
                    return reply(Err("BAD_SPACE_STATE".to_string()));
                }
                scene.enter_in_space(session_id, ship, position, direction, now)
            }
        };
        match entered {
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
            Err(SceneError::NotDestroyed(_)) => reply(Err(format!("NOT_DESTROYED {session_id}"))),
        }
    }

    fn forget(&mut self, session: SessionId) {
        let system = self.sessions.remove(&session).or_else(|| {
            let system = self.widowed.remove(&session)?;
            if let Some(scene) = self.scenes.get_mut(&system) {
                scene.forget_widow(session);
            }
            Some(system)
        });
        if let Some(system) = system
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
            Message::HeatProfile { session_id, profile } => {
                let Some(scene) = self.scene_of(session_id) else {
                    return Err(ProtocolError("a heat profile names a session not in space".to_string()));
                };
                scene
                    .set_heat_profile(session_id, profile, now)
                    .map_err(|error| ProtocolError(format!("invalid heat profile: {error:?}")))?;
                return Ok(Vec::new());
            }
            Message::ModuleOverheat { session_id, module_id, active } => {
                let Some(scene) = self.scene_of(session_id) else {
                    return Err(ProtocolError(
                        "a module overheat request names a session not in space".to_string(),
                    ));
                };
                scene
                    .set_module_overheat(session_id, module_id, active)
                    .map_err(|error| ProtocolError(format!("module overheat request failed: {error:?}")))?;
                return Ok(Vec::new());
            }
            Message::ModuleOffline { session_id, module_id } => {
                let Some(scene) = self.scene_of(session_id) else {
                    return Err(ProtocolError(
                        "a module offline request names a session not in space".to_string(),
                    ));
                };
                let out = scene.offline_module(session_id, module_id, now);
                return Ok(Self::messages(out));
            }
            Message::RackOverheat { session_id, rack, active } => {
                let Some(scene) = self.scene_of(session_id) else {
                    return Err(ProtocolError(
                        "a rack overheat request names a session not in space".to_string(),
                    ));
                };
                scene
                    .set_rack_overheat(session_id, rack, active)
                    .map_err(|error| ProtocolError(format!("rack overheat request failed: {error:?}")))?;
                return Ok(Vec::new());
            }
            Message::Leave { session_id, why } => {
                let why = match why {
                    0 => Departure::Docked,
                    1 => Departure::LoggedOff,
                    2 => Departure::Jumped,
                    3 => Departure::Ejected,
                    _ => Departure::Boarded,
                };
                let told = match self.scene_of(session_id) {
                    Some(scene) => scene.leave(session_id, why, now).unwrap_or_default(),
                    None => Vec::new(),
                };
                if matches!(why, Departure::Ejected | Departure::Boarded) {
                    if let Some(system) = self.sessions.remove(&session_id) {
                        self.widowed.insert(session_id, system);
                    }
                } else {
                    self.forget(session_id);
                }
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
            Message::Command { session_id, command: crate::link::COMMAND_MEASURE, args } => {
                match (self.scene_of(session_id), args.as_slice()) {
                    (Some(scene), [wreck]) if is_id(*wreck) => vec![Out::Measured {
                        session: session_id,
                        command: crate::link::COMMAND_MEASURE,
                        distance: scene.distance_to_wreck(session_id, *wreck as i64, now),
                    }],
                    (Some(_), _) => vec![Out::Refused {
                        session: session_id,
                        command: crate::link::COMMAND_MEASURE,
                        reason: "UNKNOWN_COMMAND",
                    }],
                    (None, _) => vec![Out::Refused {
                        session: session_id,
                        command: crate::link::COMMAND_MEASURE,
                        reason: "NO_SHIP_IN_SPACE",
                    }],
                }
            }
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
            Message::Spawn(spawn) => return Ok(self.spawn(*spawn, now)),
            Message::LaunchDrone(launch) => return Ok(self.launch_drone(*launch, now)),
            Message::DroneCommand { session_id, command, target_id, drone_ids, control_range } => {
                let command = match command {
                    1 => DroneCommand::Engage { target: target_id },
                    2 => DroneCommand::ReturnHome,
                    3 => DroneCommand::ReturnBay,
                    4 => DroneCommand::Abandon,
                    5 => DroneCommand::Assist { character_id: target_id },
                    6 => DroneCommand::Guard { character_id: target_id },
                    7 => DroneCommand::MineRepeatedly { target: target_id },
                    8 => DroneCommand::Salvage { target: (target_id > 0).then_some(target_id) },
                    9 => DroneCommand::Reconnect,
                    other => {
                        return Err(ProtocolError(format!("unknown drone command {other}")));
                    }
                };
                match self.scene_of(session_id) {
                    Some(scene) => {
                        scene.drone_command_with_range(session_id, &drone_ids, command, control_range, now)
                    }
                    None => Vec::new(),
                }
            }
            Message::Destroy { ship_id, credit } => {
                let target = self
                    .scenes
                    .values_mut()
                    .find_map(|scene| scene.session_of(ship_id).map(|session| (scene, session)));
                match target {
                    Some((scene, session)) => scene.destroy_credited(session, credit, None, now),
                    None => Vec::new(),
                }
            }
            Message::Respawn { session_id, ship, position, direction } => {
                let reply = |outcome| vec![Message::EnterReply { session_id, outcome }];
                let Some(system) = self
                    .widowed
                    .get(&session_id)
                    .copied()
                    .or_else(|| self.sessions.get(&session_id).copied())
                else {
                    return Ok(reply(Err(format!("NOT_DESTROYED {session_id}"))));
                };
                let Some(scene) = self.scenes.get_mut(&system) else {
                    return Ok(reply(Err(format!("NO_SCENE {session_id}"))));
                };
                let (position, direction) = (
                    Vec3::new(position[0], position[1], position[2]),
                    Vec3::new(direction[0], direction[1], direction[2]),
                );
                let respawned = scene.respawn(session_id, *ship, position, direction, now);
                if respawned.is_ok() {
                    self.widowed.remove(&session_id);
                    self.sessions.insert(session_id, system);
                }
                return Ok(match respawned {
                    // the answer comes after what the pilot is told, so that the host goes on when the client has been sent the ball
                    Ok(told) => {
                        let mut messages = Self::messages(told);
                        messages.extend(reply(Ok(())));
                        messages
                    }
                    Err(SceneError::NotDestroyed(_)) => reply(Err(format!("NOT_DESTROYED {session_id}"))),
                    Err(SceneError::AlreadyInSpace(_)) => {
                        reply(Err(format!("ALREADY_IN_SPACE {session_id}")))
                    }
                    Err(SceneError::UnknownStation(station)) => {
                        reply(Err(format!("UNKNOWN_STATION {station}")))
                    }
                });
            }
            Message::PlaceAsteroid(placement) => match self.scenes.get_mut(&placement.solar_system_id) {
                Some(scene) => {
                    let p = *placement;
                    scene.place_asteroid(crate::wreck::Asteroid {
                        id: p.asteroid_id,
                        type_id: p.type_id,
                        group_id: p.group_id,
                        category_id: p.category_id,
                        name: p.name,
                        graphic_id: p.graphic_id,
                        position: Vec3::new(p.position[0], p.position[1], p.position[2]),
                        direction: Vec3::new(p.direction[0], p.direction[1], p.direction[2]),
                        radius: p.radius,
                        units_remaining: p.units_remaining,
                    }, now)
                }
                None => Vec::new(),
            },
            Message::PlaceWreck(placement) => match self.scenes.get_mut(&placement.solar_system_id) {
                Some(scene) => {
                    let placement = *placement;
                    let wreck = Wreck {
                        id: placement.wreck_id,
                        group_id: placement.group_id,
                        kind: placement.kind,
                        position: Vec3::new(
                            placement.position[0],
                            placement.position[1],
                            placement.position[2],
                        ),
                        direction: Vec3::new(
                            placement.direction[0],
                            placement.direction[1],
                            placement.direction[2],
                        ),
                        radius: placement.radius,
                        owner_id: placement.credit.character_id,
                        corporation_id: placement.credit.corporation_id,
                        alliance_id: placement.credit.alliance_id,
                        war_faction_id: placement.credit.war_faction_id,
                        rights: LootRights {
                            character_id: placement.credit.character_id,
                            corporation_id: placement.credit.corporation_id,
                        },
                        launcher_id: placement.launcher_id,
                        is_empty: placement.is_empty,
                        rights_known: !placement.is_empty,
                        expires_at: now + WRECK_LIFETIME,
                    };
                    scene.place_wreck(wreck, now)
                }
                None => Vec::new(),
            },
            Message::PlaceContainer(can) => {
                let session_id = can.session_id;
                match self.scene_of(session_id) {
                    Some(scene) => scene.place_container(session_id, *can, now),
                    None => vec![Out::Refused {
                        session: session_id,
                        command: crate::link::kind::PLACE_CONTAINER,
                        reason: "NO_SHIP_IN_SPACE",
                    }],
                }
            }
            Message::ChangeWreck { session_id, wreck_id, is_empty } => {
                let changed = self
                    .scenes
                    .values_mut()
                    .find(|scene| scene.wreck(wreck_id).is_some())
                    .map(|scene| scene.change_wreck(wreck_id, is_empty, now));
                // the answer comes after what those who see the wreck are told, so that the host goes on when the clients have been sent it
                match changed {
                    Some(mut told) => {
                        told.push(Out::Accepted {
                            session: session_id,
                            command: crate::link::kind::CHANGE_WRECK,
                            at: now,
                        });
                        told
                    }
                    None => vec![Out::Refused {
                        session: session_id,
                        command: crate::link::kind::CHANGE_WRECK,
                        reason: "NO_SUCH_WRECK",
                    }],
                }
            }
            Message::RemoveWreck { wreck_id, effect } => self
                .scenes
                .values_mut()
                .find(|scene| scene.wreck(wreck_id).is_some())
                .map(|scene| scene.remove_wreck(wreck_id, i64::from(effect), now))
                .unwrap_or_default(),
            Message::DroneWorkResult { session_id, drone_id, target_id, cycle_id, accepted, reason: _, units_remaining, delivered_units: _, complete } => {
                self.scenes.values_mut()
                    .find(|scene| scene.session_of(drone_id).is_some())
                    .map(|scene| scene.drone_work_result(session_id, drone_id, target_id, cycle_id, accepted, units_remaining, complete, now))
                    .unwrap_or_default()
            },
            Message::RemoteRepairCycleResult { session_id, module_id, target_id, cycle_id, accepted, reason: _, amount } => {
                self.scene_of(session_id)
                    .map(|scene| scene.remote_repair_cycle_result(session_id, module_id, target_id, cycle_id, accepted, amount))
                    .unwrap_or_default()
            },
            Message::AdjustCharge { request_id, session_id, module_id, charge_type, op } => {
                match self.scene_of(session_id) {
                    Some(scene) => scene.adjust_charge(request_id, session_id, module_id, charge_type, op),
                    // a session that is not in space has no gun that runs: the host has its rounds
                    None => vec![Out::ChargeAdjusted {
                        session: session_id,
                        request_id,
                        running: false,
                        before: 0,
                        after: 0,
                    }],
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
        self.forget_the_destroyed(&outs);
        Ok(Self::messages(outs))
    }

    /// A player whose ship was destroyed is not in space any more; its scene keeps its ballpark until the host gives it a capsule (`Respawn`) or it leaves.
    fn forget_the_destroyed(&mut self, outs: &[Out]) {
        for out in outs {
            if let Out::Destroyed { session, .. } = out
                && !is_npc_session(*session)
                && let Some(system) = self.sessions.remove(session)
            {
                self.widowed.insert(*session, system);
            }
        }
    }

    /// The host asks for an NPC: it comes into the scene of the system if there is one (a system has a scene while somebody is in it).
    fn spawn(&mut self, spawn: Spawn, now: i64) -> Vec<Message> {
        let Spawn {
            request_id,
            solar_system_id,
            ship,
            anchor_ship_id,
            position,
            velocity,
            direction,
            behavior,
            identity,
        } = spawn;
        let reply = |outcome| Message::SpawnReply { request_id, outcome };
        let Some(scene) = self.scenes.get_mut(&solar_system_id) else {
            return vec![reply(Err(format!("NO_SCENE {solar_system_id}")))];
        };
        let mut position = Vec3::new(position[0], position[1], position[2]);
        if anchor_ship_id != 0 {
            let Some(anchor) = scene
                .session_of(anchor_ship_id)
                .and_then(|session| scene.ship(session, now))
                .map(|anchor| anchor.position)
            else {
                return vec![reply(Err(format!("NO_SHIP {anchor_ship_id}")))];
            };
            position = Vec3::new(anchor.x + position.x, anchor.y + position.y, anchor.z + position.z);
        }
        let place = NpcPlace {
            position,
            velocity: Vec3::new(velocity[0], velocity[1], velocity[2]),
            direction: Vec3::new(direction[0], direction[1], direction[2]),
            speed_fraction: 0.0,
            course: Course::Stop,
        };
        let spawned = if behavior.is_empty() {
            Ok(scene.spawn_npc(ship, identity, place, now))
        } else {
            scene.spawn_npc_with_behavior(ship, identity, place, &behavior, now)
        };
        match spawned {
            Ok((session, told)) => {
                let mut messages = vec![reply(Ok(session))];
                messages.extend(Self::messages(told));
                messages
            }
            Err(error) => vec![reply(Err(format!("{error:?}")))],
        }
    }

    /// A drone comes into the scene of the pilot's ship: the answer carries the session the drone is known by.
    fn launch_drone(&mut self, launch: LaunchDrone, now: i64) -> Vec<Message> {
        let LaunchDrone {
            request_id,
            session_id,
            ship,
            weapon,
            graphic_id,
            orbit_distance,
            attack_range,
            cruise_fraction,
            mining_amount_m3,
            work_cycle_ms,
            mining_waste_multiplier,
            mining_waste_probability,
            salvage_access_bonus,
        } = launch;
        let reply = |outcome| Message::SpawnReply { request_id, outcome };
        let Some(scene) = self.scene_of(session_id) else {
            return vec![reply(Err(format!("NO_SHIP {session_id}")))];
        };
        match scene.launch_drone(
            session_id,
            ship,
            DroneFlight {
                weapon, graphic_id, orbit_distance, attack_range, cruise_fraction,
                work: crate::drones::DroneWorkProfile {
                    mining_amount_m3, cycle_ms: work_cycle_ms, waste_multiplier: mining_waste_multiplier,
                    waste_probability: mining_waste_probability, salvage_access_bonus,
                },
            },
            now,
        ) {
            Ok((session, told)) => {
                let mut messages = Self::messages(told);
                messages.push(reply(Ok(session)));
                messages
            }
            Err(reason) => vec![reply(Err(reason.to_string()))],
        }
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
        self.forget_the_destroyed(&outs);
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
        let scene = self.scenes.entry(to_system).or_insert_with(|| self.universe.scene_of(system));
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
            position: None,
            direction: None,
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
            turret: None,
            action: None,
            on: None,
            off: None,
            on_changes: Vec::new(),
            off_changes: Vec::new(),
        }
    }

    /// A request for an NPC that leaves a wreck, `at` a place.
    fn spawn(request_id: u64, system: i32, ship_id: i64, at: [f64; 3]) -> Message {
        let mut ship = spec();
        ship.ship_id = ship_id;
        ship.character_id = 0;
        ship.owner_id = 1_000_125;
        ship.category_id = 11;
        Message::Spawn(Box::new(crate::link::Spawn {
            request_id,
            solar_system_id: system,
            ship,
            anchor_ship_id: 0,
            position: at,
            velocity: [0.0; 3],
            direction: [1.0, 0.0, 0.0],
            behavior: String::new(),
            identity: crate::ship::NpcIdentity {
                wreck: Some(crate::wreck::WreckKind {
                    type_id: 26_506,
                    name: "Caldari Frigate Wreck".into(),
                    graphic_id: Some(3117),
                }),
                ..crate::ship::NpcIdentity::default()
            },
        }))
    }

    /// Where the player's ship is, plus 500 m along x.
    fn beside_the_player(sim: &mut Sim) -> [f64; 3] {
        let at = sim.scenes.get_mut(&SYSTEM).unwrap().ship(SESSION, NOON).unwrap().position;
        [at.x + 500.0, at.y, at.z]
    }

    fn place_player_outside_station(sim: &mut Sim) {
        let scene = sim.scenes.get_mut(&SYSTEM).unwrap();
        let station = scene.system().body(STATION).unwrap().clone();
        let pilot = scene.pilots.get_mut(&SESSION).unwrap();
        let direction = pilot.ship.direction;
        let distance = station.radius + pilot.ship.spec.radius + 1_000.0;
        pilot.ship.position = eve_destiny::Vec3::new(
            station.position.x + direction.x * distance,
            station.position.y + direction.y * distance,
            station.position.z + direction.z * distance,
        );
        pilot.ship.velocity = eve_destiny::Vec3::ZERO;
        pilot.ship.speed_fraction = 0.0;
        pilot.ship.course = crate::ship::Course::Stop;
    }

    #[test]
    fn an_npc_the_host_asks_for_comes_into_the_scene_of_the_system_and_is_answered_with_its_session() {
        let mut sim = undocked();
        sim.handle(Message::Bind { session_id: SESSION }, NOON).unwrap();
        let at = beside_the_player(&mut sim);
        let reply = sim.handle(spawn(5, SYSTEM, 980_050_000_001, at), NOON + 60 * SECOND).unwrap();
        assert!(
            matches!(reply[0], Message::SpawnReply { request_id: 5, outcome: Ok(session) } if is_npc_session(session)),
            "{reply:?}"
        );
        // the player who sees it is told; nobody is told anything for the NPC
        assert!(
            reply[1..].iter().all(|m| matches!(m, Message::Notify { session_id: SESSION, .. })),
            "{reply:?}"
        );
        assert_eq!(names(&reply[1..]), ["DoDestinyUpdate"]);
        assert_eq!(sim.ships(), 1, "an NPC is not a session of the host's");
    }

    #[test]
    fn an_npc_the_host_places_from_a_ship_comes_that_far_from_where_the_ship_is_and_one_placed_from_a_ship_that_is_not_there_is_refused()
     {
        let mut sim = undocked();
        place_player_outside_station(&mut sim);
        let there = sim.scenes.get_mut(&SYSTEM).unwrap().ship(SESSION, NOON).unwrap().position;
        let Message::Spawn(mut request) = spawn(9, SYSTEM, 980_050_000_005, [0.0; 3]) else { unreachable!() };
        request.anchor_ship_id = spec().ship_id;
        request.position = [3_000.0, -200.0, 50.0];
        let reply = sim.handle(Message::Spawn(request.clone()), NOON).unwrap();
        assert!(
            matches!(reply[0], Message::SpawnReply { outcome: Ok(session), .. } if is_npc_session(session)),
            "{reply:?}"
        );
        let scene = sim.scenes.get_mut(&SYSTEM).unwrap();
        let npc = scene.session_of(980_050_000_005).unwrap();
        let at = scene.ship(npc, NOON).unwrap().position;
        assert_eq!((at.x - there.x, at.y - there.y, at.z - there.z), (3_000.0, -200.0, 50.0));
        request.anchor_ship_id = 12;
        request.ship.ship_id = 980_050_000_006;
        let refused = sim.handle(Message::Spawn(request), NOON).unwrap();
        assert_eq!(refused, [Message::SpawnReply { request_id: 9, outcome: Err("NO_SHIP 12".to_string()) }]);
    }

    #[test]
    fn an_npc_is_refused_in_a_system_where_nobody_is_and_a_scripted_behavior_that_is_not_there_makes_none() {
        let mut sim = sim();
        let none = sim.handle(spawn(6, SYSTEM, 980_050_000_002, [0.0; 3]), NOON).unwrap();
        assert_eq!(none, [Message::SpawnReply { request_id: 6, outcome: Err(format!("NO_SCENE {SYSTEM}")) }]);
        let mut sim = undocked();
        let Message::Spawn(mut request) = spawn(7, SYSTEM, 980_050_000_003, [0.0; 3]) else { unreachable!() };
        request.behavior = "there is no such tree".to_string();
        let refused = sim.handle(Message::Spawn(request), NOON).unwrap();
        assert!(
            matches!(&refused[..], [Message::SpawnReply { request_id: 7, outcome: Err(_) }]),
            "{refused:?}"
        );
    }

    #[test]
    fn a_ship_the_host_asks_to_destroy_is_gone_and_seen_to_go_and_a_player_is_told_to_the_host() {
        let mut sim = undocked();
        place_player_outside_station(&mut sim);
        sim.handle(Message::Bind { session_id: SESSION }, NOON).unwrap();
        let at = beside_the_player(&mut sim);
        let reply = sim.handle(spawn(8, SYSTEM, 980_050_000_004, at), NOON + 60 * SECOND).unwrap();
        assert!(matches!(reply[0], Message::SpawnReply { outcome: Ok(_), .. }));
        let told = sim
            .handle(
                Message::Destroy { ship_id: 980_050_000_004, credit: Default::default() },
                NOON + 61 * SECOND,
            )
            .unwrap();
        // the explosion and wreck update the bound observer; the host receives the NPC's Destroyed record
        assert_eq!(&names(&told)[..2], ["DoDestinyUpdate", "DoDestinyUpdate"]);
        let gone: Vec<_> = told.iter().filter(|m| matches!(m, Message::Destroyed { .. })).collect();
        assert!(
            matches!(gone[..], [Message::Destroyed { ship_id: 980_050_000_004, wreck_id, killer_ship_id: 0, .. }] if *wreck_id != 0),
            "{told:?}"
        );
        assert_eq!(told.len(), 3);
        // a player's ship: the host is told with `Destroyed`, and the session is no longer in space
        let told = sim
            .handle(
                Message::Destroy { ship_id: spec().ship_id, credit: Default::default() },
                NOON + 62 * SECOND,
            )
            .unwrap();
        assert!(
            told.iter().any(|m| matches!(m, Message::Destroyed { session_id: SESSION, ship_id, wreck_id: 0, killer_ship_id: 0, .. } if *ship_id == spec().ship_id)),
            "{told:?}"
        );
        assert_eq!(sim.ships(), 0);
        let none = sim
            .handle(Message::Destroy { ship_id: 12, credit: Default::default() }, NOON + 63 * SECOND)
            .unwrap();
        assert_eq!(none, []);
    }

    fn capsule() -> Box<crate::ship::ShipSpec> {
        let mut capsule = spec();
        capsule.ship_id = 9_988_400_002_416;
        capsule.type_id = 670;
        capsule.group_id = 29;
        capsule.name = "Capsule".to_string();
        Box::new(capsule)
    }

    fn lose_the_ship(sim: &mut Sim, at: i64) -> Message {
        let told =
            sim.handle(Message::Destroy { ship_id: spec().ship_id, credit: Default::default() }, at).unwrap();
        told.into_iter().find(|m| matches!(m, Message::Destroyed { .. })).expect("the host is told")
    }

    #[test]
    fn a_session_that_lost_its_ship_keeps_its_ballpark_and_is_given_a_capsule_by_the_host() {
        let mut sim = undocked();
        sim.handle(Message::Bind { session_id: SESSION }, NOON).unwrap();
        let Message::Destroyed { position, direction, .. } = lose_the_ship(&mut sim, NOON + 61 * SECOND)
        else {
            unreachable!()
        };
        assert_eq!(sim.ships(), 0, "no ship of it is in space");
        assert_eq!(sim.scenes.len(), 1, "the scene keeps its ballpark");

        let told = sim
            .handle(
                Message::Respawn { session_id: SESSION, ship: capsule(), position, direction },
                NOON + 62 * SECOND,
            )
            .unwrap();
        assert_eq!(told.last(), Some(&Message::EnterReply { session_id: SESSION, outcome: Ok(()) }));
        let notified = &told[..told.len() - 1];
        assert!(
            notified.iter().all(|m| matches!(m, Message::Notify { session_id: SESSION, .. })),
            "{told:?}"
        );
        assert_eq!(names(notified), ["DoDestinyUpdate"], "the ball of the capsule, before the answer");
        assert_eq!(sim.ships(), 1);

        // and commands reach the new ship
        let stop = sim
            .handle(Message::Command { session_id: SESSION, command: 3, args: vec![] }, NOON + 63 * SECOND)
            .unwrap();
        assert!(!stop.iter().any(|m| matches!(m, Message::Refused { .. })), "{stop:?}");
    }

    #[test]
    fn a_capsule_is_refused_to_a_session_that_did_not_lose_its_ship_and_one_that_left_loses_the_scene() {
        let mut sim = undocked();
        let refused = sim
            .handle(
                Message::Respawn {
                    session_id: SESSION,
                    ship: capsule(),
                    position: [0.0; 3],
                    direction: [1.0, 0.0, 0.0],
                },
                NOON,
            )
            .unwrap();
        assert!(
            matches!(&refused[..], [Message::EnterReply { outcome: Err(reason), .. }] if reason.starts_with("ALREADY_IN_SPACE")),
            "{refused:?}"
        );
        let none = sim
            .handle(
                Message::Respawn {
                    session_id: 99,
                    ship: capsule(),
                    position: [0.0; 3],
                    direction: [1.0, 0.0, 0.0],
                },
                NOON,
            )
            .unwrap();
        assert!(matches!(&none[..], [Message::EnterReply { session_id: 99, outcome: Err(_) }]), "{none:?}");

        lose_the_ship(&mut sim, NOON + 61 * SECOND);
        assert_eq!(sim.scenes.len(), 1);
        sim.handle(Message::Leave { session_id: SESSION, why: 1 }, NOON + 62 * SECOND).unwrap();
        assert_eq!(sim.scenes.len(), 0, "nobody is there: no scene");
        let late = sim
            .handle(
                Message::Respawn {
                    session_id: SESSION,
                    ship: capsule(),
                    position: [0.0; 3],
                    direction: [1.0, 0.0, 0.0],
                },
                NOON + 63 * SECOND,
            )
            .unwrap();
        assert!(matches!(&late[..], [Message::EnterReply { outcome: Err(_), .. }]), "{late:?}");
    }

    fn wreck_placement(system: i32, id: i64, at: [f64; 3]) -> Message {
        Message::PlaceWreck(Box::new(crate::link::WreckPlacement {
            solar_system_id: system,
            wreck_id: id,
            group_id: crate::wreck::WRECK_GROUP,
            kind: crate::wreck::WreckKind {
                type_id: 26_506,
                name: "Caldari Frigate Wreck".into(),
                graphic_id: Some(3117),
            },
            position: at,
            direction: [1.0, 0.0, 0.0],
            radius: 47.0,
            launcher_id: spec().ship_id,
            credit: crate::death::Credit {
                character_id: 140_000_342,
                corporation_id: 1_000_006,
                alliance_id: 0,
                war_faction_id: 500_001,
            },
            is_empty: false,
        }))
    }

    #[test]
    fn a_wreck_the_host_made_is_put_in_space_with_its_id_for_whoever_can_see_it_and_kept_for_two_hours() {
        let mut sim = undocked();
        sim.handle(Message::Bind { session_id: SESSION }, NOON).unwrap();
        let at = beside_the_player(&mut sim);
        let told = sim.handle(wreck_placement(SYSTEM, 9_988_400_002_417, at), NOON + 5 * SECOND).unwrap();
        assert_eq!(names(&told), ["DoDestinyUpdate"], "{told:?}");
        assert_eq!(
            sim.scenes[&SYSTEM].wreck(9_988_400_002_417).map(|w| w.rights.character_id),
            Some(140_000_342)
        );
        assert_eq!(
            sim.scenes[&SYSTEM].wreck(9_988_400_002_417).map(|w| w.expires_at),
            Some(NOON + 5 * SECOND + crate::wreck::WRECK_LIFETIME)
        );
        // a system nobody is in has no scene to put a wreck in
        let none = sim.handle(wreck_placement(30_000_142, 9_988_400_002_418, at), NOON + 6 * SECOND).unwrap();
        assert_eq!(none, []);
    }

    fn can_placement(id: i64) -> Message {
        Message::PlaceContainer(Box::new(crate::link::ContainerPlacement {
            session_id: SESSION,
            container_id: id,
            kind: crate::wreck::WreckKind {
                type_id: 23,
                name: "Cargo Container".into(),
                graphic_id: Some(1176),
            },
            radius: 14.0,
            launcher_id: spec().ship_id,
            credit: crate::death::Credit {
                character_id: 140_000_342,
                corporation_id: 1_000_006,
                alliance_id: 0,
                war_faction_id: 0,
            },
            distance: 275.0,
        }))
    }

    #[test]
    fn a_can_is_put_ahead_of_the_pilot_changed_when_something_is_in_it_measured_and_taken_out_of_space_with_each_answered_to_its_pilot()
     {
        let mut sim = undocked();
        sim.handle(Message::Bind { session_id: SESSION }, NOON).unwrap();
        let can = 9_988_400_002_948;

        let placed = sim.handle(can_placement(can), NOON + SECOND).unwrap();
        // the ball and the effect, then where it lies (after what the pilot is told)
        assert_eq!(names(&placed[..2]), ["DoDestinyUpdate", "DoDestinyUpdate"], "{placed:?}");
        assert!(
            matches!(placed[2], Message::ContainerPlaced { session_id: SESSION, container_id, .. } if container_id == can),
            "{placed:?}"
        );
        assert_eq!(placed.len(), 3);

        // a pilot that is not in space cannot jettison
        let none = sim
            .handle(
                Message::PlaceContainer(Box::new(match can_placement(can + 1) {
                    Message::PlaceContainer(mut other) => {
                        other.session_id = 99;
                        *other
                    }
                    _ => unreachable!(),
                })),
                NOON + SECOND,
            )
            .unwrap();
        assert_eq!(
            none,
            [Message::Refused {
                session_id: 99,
                command: crate::link::kind::PLACE_CONTAINER,
                reason: "NO_SHIP_IN_SPACE".to_string()
            }]
        );

        let changed = sim
            .handle(
                Message::ChangeWreck { session_id: SESSION, wreck_id: can, is_empty: false },
                NOON + 2 * SECOND,
            )
            .unwrap();
        assert_eq!(names(&changed[..1]), ["DoDestinyUpdate"]);
        assert_eq!(
            changed.last(),
            Some(&Message::Accepted {
                session_id: SESSION,
                command: crate::link::kind::CHANGE_WRECK,
                at: NOON + 2 * SECOND
            }),
            "the answer comes after what the pilot is told"
        );
        let missing = sim
            .handle(
                Message::ChangeWreck { session_id: SESSION, wreck_id: can + 7, is_empty: true },
                NOON + 2 * SECOND,
            )
            .unwrap();
        assert_eq!(
            missing,
            [Message::Refused {
                session_id: SESSION,
                command: crate::link::kind::CHANGE_WRECK,
                reason: "NO_SUCH_WRECK".to_string()
            }]
        );

        let measured = sim
            .handle(
                Message::Command {
                    session_id: SESSION,
                    command: crate::link::COMMAND_MEASURE,
                    args: vec![can as f64],
                },
                NOON + SECOND,
            )
            .unwrap();
        assert!(
            matches!(&measured[..], [Message::Measured { session_id: SESSION, command: 20, distance: Some(d) }] if (*d - 261.0).abs() < 1e-3),
            "{measured:?}"
        );
        let nothing = sim
            .handle(
                Message::Command {
                    session_id: SESSION,
                    command: crate::link::COMMAND_MEASURE,
                    args: vec![12.0],
                },
                NOON + SECOND,
            )
            .unwrap();
        assert_eq!(nothing, [Message::Measured { session_id: SESSION, command: 20, distance: None }]);

        let gone = sim.handle(Message::RemoveWreck { wreck_id: can, effect: 3 }, NOON + 3 * SECOND).unwrap();
        assert_eq!(names(&gone), ["DoDestinyUpdate"]);
        assert!(
            sim.handle(Message::RemoveWreck { wreck_id: can, effect: 3 }, NOON + 4 * SECOND)
                .unwrap()
                .is_empty(),
            "nothing is told twice"
        );
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

    #[test]
    fn an_offline_message_stops_a_running_module_immediately_and_idempotently() {
        let mut sim = undocked();
        sim.handle(
            Message::Activate { session_id: SESSION, effect: Box::new(afterburner(77)) },
            NOON + 40 * SECOND,
        )
        .unwrap();
        let stopped = sim
            .handle(Message::ModuleOffline { session_id: SESSION, module_id: 77 }, NOON + 41 * SECOND)
            .unwrap();
        assert!(!stopped.is_empty(), "the running effect is ended immediately");
        let repeated = sim
            .handle(Message::ModuleOffline { session_id: SESSION, module_id: 77 }, NOON + 42 * SECOND)
            .unwrap();
        assert!(repeated.is_empty(), "repeating offline after it stopped is idempotent");
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
        let reply = sim().handle(Message::Hello { version: PROTOCOL_VERSION }, NOON).unwrap();
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
    fn an_undock_rejects_bad_locations_and_in_space_login_restores_the_saved_pose() {
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

        let ship = spec();
        let ship_id = ship.ship_id;
        let position = [100.0, 200.0, 300.0];
        let messages = sim.handle(
            Message::Enter(Box::new(Enter {
                session_id: 9,
                solar_system_id: SYSTEM,
                how: How::InSpace,
                station_id: 0,
                ship,
                position: Some(position),
                direction: Some([0.0, 0.0, 1.0]),
            })),
            NOON,
        ).unwrap();
        assert!(matches!(
            messages.first(),
            Some(Message::EnterReply { session_id: 9, outcome: Ok(()) })
        ));
        let restored = sim.scenes.get_mut(&SYSTEM).unwrap().ship(9, NOON).unwrap();
        assert_eq!(restored.spec.ship_id, ship_id);
        assert_eq!(restored.position, Vec3::new(position[0], position[1], position[2]));
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
    fn a_players_warp_to_a_belt_is_told_to_the_host_as_an_arrival() {
        const BELT: i64 = 40_000_101;
        let mut world = big_station();
        world.bodies.push(crate::world::Body {
            id: BELT,
            type_id: 15,
            group_id: 9,
            category_id: 2,
            owner_id: 0,
            name: "Testing Belt".to_string(),
            kind: crate::world::BodyKind::Belt,
            position: eve_destiny::Vec3::new(20_000_000.0, 0.0, 0.0),
            radius: 1_000.0,
            graphic_id: None,
            detail: crate::world::Detail::None,
        });
        let mut sim = Sim::new(Arc::new(Universe::with_systems([world])));
        sim.handle(enter(SESSION, STATION, SYSTEM), NOON).unwrap();
        sim.handle(Message::Command { session_id: SESSION, command: 3, args: vec![0.0] }, NOON).unwrap();
        let warp = sim
            .handle(
                Message::Command { session_id: SESSION, command: 8, args: vec![BELT as f64, 0.0] },
                NOON + SECOND,
            )
            .unwrap();
        assert!(!warp.iter().any(|m| matches!(m, Message::Refused { .. })), "{warp:?}");
        let mut arrived = Vec::new();
        let mut t = NOON + SECOND;
        while t < NOON + 600 * SECOND {
            t += SECOND / 4;
            arrived.extend(sim.poll(t).into_iter().filter(|m| matches!(m, Message::Arrived { .. })));
        }
        let [Message::Arrived { session_id, belt_id, position }] = &arrived[..] else {
            panic!("{arrived:?}")
        };
        assert_eq!((*session_id, *belt_id), (SESSION, BELT));
        assert!(
            (position[0] - 20_000_000.0).abs() < 4_000.0,
            "{position:?}: it stops 2.5 km short of the belt"
        );
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
    fn a_heat_profile_link_message_installs_dogma_module_data_for_scene_overload() {
        let mut simulation = sim();
        let entering = enter(SESSION, STATION, SYSTEM);
        let fitted = match &entering {
            Message::Enter(entry) => entry.ship.modules.clone(),
            _ => unreachable!(),
        };
        simulation.handle(entering, NOON).unwrap();
        let profile = crate::scene::HeatProfile {
            generation_multiplier: 1.0,
            damage_multiplier: 0.95,
            thermodynamics_level: 1,
            capacity: [100.0; 3],
            initial_rack_heat: [0.0; 3],
            dissipation_rate: [0.01; 3],
            attenuation: [0.71; 3],
            modules: fitted
                .iter()
                .filter(|module| (11..=34).contains(&module.flag))
                .map(|module| crate::scene::HeatModuleProfile {
                    module_id: module.item_id,
                    flag: module.flag,
                    structure_hp: 100.0,
                    damage_hp: 0.0,
                    heat_damage_hp: 6.0,
                    heat_absorption_rate: 0.01,
                    overloadable: true,
                    heat_immune: false,
                    online: true,
                })
                .collect(),
        };
        let module_id = profile.modules[0].module_id;
        simulation.handle(Message::HeatProfile { session_id: SESSION, profile }, NOON).unwrap();
        assert!(
            simulation
                .handle(Message::ModuleOverheat { session_id: SESSION, module_id, active: true }, NOON)
                .unwrap()
                .is_empty()
        );
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
            position: None,
            direction: None,
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
