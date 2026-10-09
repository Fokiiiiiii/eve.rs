use super::*;

/// The command a code and its arguments say, if they say one.
pub(super) fn command_of(code: u8, args: &[f64]) -> Option<Command> {
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
pub(super) fn order_of(code: u8, args: &[f64]) -> Option<Order> {
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
pub(super) fn is_id(value: f64) -> bool {
    value.is_finite() && value.fract() == 0.0 && value.abs() < 9_007_199_254_740_992.0
}

impl Sim {
    pub(super) fn out_message(out: Out) -> Message {
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
            Out::Position { session, command, position } => Message::Position {
                session_id: session,
                command,
                position: [position.x, position.y, position.z],
            },
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
            Out::FighterLanded { session, fighter_id } => {
                Message::FighterLanded { session_id: session, fighter_id }
            }
            Out::FighterReleased { session, fighter_id, position } => Message::FighterReleased {
                session_id: session,
                fighter_id,
                position: [position.x, position.y, position.z],
            },
            Out::FighterSize { session, fighter_id, size } => {
                Message::FighterSize { session_id: session, fighter_id, size }
            }
            Out::FighterResults { session, command, results } => Message::FighterResults {
                session_id: session,
                command,
                results: results
                    .into_iter()
                    .map(|(fighter, reason)| (fighter, reason.unwrap_or("").to_string()))
                    .collect(),
            },
            Out::DroneResults { session, command, results } => Message::DroneResults {
                session_id: session,
                command,
                results: results
                    .into_iter()
                    .map(|(drone, reason)| (drone, reason.unwrap_or("").to_string()))
                    .collect(),
            },
            Out::DroneWorkCycle {
                session,
                character_id,
                ship_id,
                solar_system_id,
                drone_id,
                target_id,
                cycle_id,
                command,
                mining_amount_m3,
                waste_multiplier,
                waste_probability,
                access_difficulty_bonus,
                roll,
            } => Message::DroneWorkCycle {
                session_id: session,
                character_id,
                ship_id,
                solar_system_id,
                drone_id,
                target_id,
                cycle_id,
                command,
                mining_amount_m3,
                waste_multiplier,
                waste_probability,
                access_difficulty_bonus,
                roll,
            },
            Out::RemoteRepairCycle {
                session,
                module_id,
                target_id,
                cycle_id,
                cycle_number,
                cycle_at_ms,
                cycle_duration_ms,
                ends_effect_after_cycle,
                base_amount,
                bonus_per_cycle,
                bonus_max,
            } => Message::RemoteRepairCycle {
                session_id: session,
                module_id,
                target_id,
                cycle_id,
                cycle_number,
                cycle_at_ms,
                cycle_duration_ms,
                ends_effect_after_cycle,
                base_amount,
                bonus_per_cycle,
                bonus_max,
            },
            Out::ModuleWorkCycle {
                session,
                character_id,
                ship_id,
                solar_system_id,
                module_id,
                target_id,
                cycle_id,
                command,
                mining_amount_m3,
                waste_multiplier,
                waste_probability,
                crit_probability,
                crit_multiplier,
                access_difficulty_bonus,
                waste_roll,
                rounding_roll,
                crit_roll,
            } => Message::ModuleWorkCycle {
                session_id: session,
                character_id,
                ship_id,
                solar_system_id,
                module_id,
                target_id,
                cycle_id,
                command,
                mining_amount_m3,
                waste_multiplier,
                waste_probability,
                crit_probability,
                crit_multiplier,
                access_difficulty_bonus,
                waste_roll,
                rounding_roll,
                crit_roll,
            },
            Out::ModuleEnded { session, module_id, reason } => {
                Message::ModuleEnded { session_id: session, module_id, reason }
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
            Message::Command {
                session_id,
                command: crate::link::COMMAND_PRIME_LOGIN_BALLPARK_VISUALS,
                args,
            } => match (self.scene_of(session_id), args.as_slice()) {
                (Some(scene), []) => {
                    let mut told = scene.prime_login_visuals(session_id, now);
                    told.push(Out::Accepted {
                        session: session_id,
                        command: crate::link::COMMAND_PRIME_LOGIN_BALLPARK_VISUALS,
                        at: now,
                    });
                    told
                }
                (Some(_), _) => vec![Out::Refused {
                    session: session_id,
                    command: crate::link::COMMAND_PRIME_LOGIN_BALLPARK_VISUALS,
                    reason: "UNKNOWN_COMMAND",
                }],
                (None, _) => vec![Out::Refused {
                    session: session_id,
                    command: crate::link::COMMAND_PRIME_LOGIN_BALLPARK_VISUALS,
                    reason: "NO_SHIP_IN_SPACE",
                }],
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
            Message::LaunchFighter {
                request_id,
                session_id,
                ship,
                graphic_id,
                cruise_fraction,
                squadron_size,
            } => {
                let reply = |outcome| Message::SpawnReply { request_id, outcome };
                let Some(scene) = self.scene_of(session_id) else {
                    return Ok(vec![reply(Err(format!("NO_SHIP {session_id}")))]);
                };
                return Ok(
                    match scene.launch_fighter(
                        session_id,
                        ship,
                        graphic_id,
                        cruise_fraction,
                        squadron_size,
                        now,
                    ) {
                        Ok((session, told)) => {
                            let mut messages = Self::messages(told);
                            messages.push(reply(Ok(session)));
                            messages
                        }
                        Err(reason) => vec![reply(Err(reason.to_string()))],
                    },
                );
            }
            Message::FighterOrder { session_id, command, target_id, fighter_ids, range } => {
                use crate::fighters::FighterOrder;
                let order = match command {
                    1 => FighterOrder::Orbit { target: target_id, distance: range },
                    2 => FighterOrder::Follow { target: target_id, range },
                    3 => FighterOrder::Stop,
                    4 => FighterOrder::Recall,
                    5 => FighterOrder::Abandon,
                    7 => FighterOrder::Scoop,
                    other => return Err(ProtocolError(format!("unknown fighter order {other}"))),
                };
                // no ship in space: nothing is under this pilot's control, and each squadron is told so
                let Some(scene) = self.scene_of(session_id) else {
                    return Ok(vec![Message::FighterResults {
                        session_id,
                        command,
                        results: fighter_ids
                            .into_iter()
                            .map(|id| (id, "NOT_CONTROLLED".to_string()))
                            .collect(),
                    }]);
                };
                return Ok(Self::messages(scene.fighter_order(session_id, &fighter_ids, order, now)));
            }
            Message::FighterGotoPoint { session_id, fighter_ids, point } => {
                use crate::fighters::FighterOrder;
                // no ship in space: nothing is under this pilot's control, and each squadron is told so (as for the other fighter orders)
                let Some(scene) = self.scene_of(session_id) else {
                    return Ok(vec![Message::FighterResults {
                        session_id,
                        command: 6,
                        results: fighter_ids
                            .into_iter()
                            .map(|id| (id, "NOT_CONTROLLED".to_string()))
                            .collect(),
                    }]);
                };
                return Ok(Self::messages(scene.fighter_order(
                    session_id,
                    &fighter_ids,
                    FighterOrder::GotoPoint { point },
                    now,
                )));
            }
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
                    scene.place_asteroid(
                        crate::wreck::Asteroid {
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
                        },
                        now,
                    )
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
                        pull: None,
                        velocity: eve_destiny::Vec3::ZERO,
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
            Message::DroneWorkResult {
                session_id,
                drone_id,
                target_id,
                cycle_id,
                accepted,
                reason: _,
                units_remaining,
                delivered_units: _,
                complete,
            } => self
                .scenes
                .values_mut()
                .find(|scene| scene.session_of(drone_id).is_some())
                .map(|scene| {
                    scene.drone_work_result(
                        session_id,
                        drone_id,
                        target_id,
                        cycle_id,
                        accepted,
                        units_remaining,
                        complete,
                        now,
                    )
                })
                .unwrap_or_default(),
            Message::ModuleWorkResult {
                session_id,
                module_id,
                target_id,
                cycle_id,
                accepted,
                reason,
                units_remaining,
                delivered_units: _,
                complete,
            } => {
                // a granted cycle moves the rock's stock and tells the other miners while the cycle is still parked; then the module takes its answer
                let mut told = if accepted {
                    self.scene_of(session_id)
                        .map(|scene| {
                            scene.module_mined(
                                session_id,
                                module_id,
                                target_id,
                                cycle_id,
                                units_remaining,
                                now,
                            )
                        })
                        .unwrap_or_default()
                } else {
                    Vec::new()
                };
                told.extend(
                    self.scene_of(session_id)
                        .map(|scene| {
                            scene.module_work_result(
                                session_id, module_id, target_id, cycle_id, accepted, &reason, complete, now,
                            )
                        })
                        .unwrap_or_default(),
                );
                told
            }
            Message::RemoteRepairCycleResult {
                session_id,
                module_id,
                target_id,
                cycle_id,
                accepted,
                reason: _,
                amount,
            } => self
                .scene_of(session_id)
                .map(|scene| {
                    scene.remote_repair_cycle_result(
                        session_id, module_id, target_id, cycle_id, accepted, amount,
                    )
                })
                .unwrap_or_default(),
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
}
