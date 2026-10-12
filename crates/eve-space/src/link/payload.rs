use super::*;

impl Message {
    /// The frame's payload: the kind and the body, without the length prefix.
    pub fn payload(&self) -> Vec<u8> {
        let mut out = vec![self.kind()];
        match self {
            Self::Hello { version } => out.extend_from_slice(&version.to_le_bytes()),
            Self::HelloAck { version, poll_hz } => {
                out.extend_from_slice(&version.to_le_bytes());
                out.extend_from_slice(&poll_hz.to_le_bytes());
            }
            Self::Enter(enter) => {
                out.extend_from_slice(&enter.session_id.to_le_bytes());
                out.extend_from_slice(&enter.solar_system_id.to_le_bytes());
                out.push(match enter.how {
                    How::Undock => 0,
                    How::InSpace => 1,
                });
                out.extend_from_slice(&enter.station_id.to_le_bytes());
                put_ship(&mut out, &enter.ship);
                if enter.how == How::InSpace {
                    put_optional_vector(&mut out, enter.position);
                    put_optional_vector(&mut out, enter.direction);
                }
            }
            Self::EnterReply { session_id, outcome } => {
                out.extend_from_slice(&session_id.to_le_bytes());
                match outcome {
                    Ok(()) => {
                        out.push(0);
                        put_text(&mut out, "");
                    }
                    Err(reason) => {
                        out.push(1);
                        put_text(&mut out, reason);
                    }
                }
            }
            Self::Leave { session_id, why } => {
                out.extend_from_slice(&session_id.to_le_bytes());
                out.push(*why);
            }
            Self::Bind { session_id } | Self::UpdateState { session_id } => {
                out.extend_from_slice(&session_id.to_le_bytes())
            }
            Self::ControlStructure { session_id, solar_system_id, structure_id, character_id, active } => {
                out.extend_from_slice(&session_id.to_le_bytes());
                out.extend_from_slice(&solar_system_id.to_le_bytes());
                out.extend_from_slice(&structure_id.to_le_bytes());
                out.extend_from_slice(&character_id.to_le_bytes());
                out.push(u8::from(*active));
            }
            Self::Command { session_id, command, args } => {
                out.extend_from_slice(&session_id.to_le_bytes());
                out.push(*command);
                out.push(args.len() as u8);
                for arg in args {
                    out.extend_from_slice(&arg.to_le_bytes());
                }
            }
            Self::ShipSpec { session_id, ship } => {
                out.extend_from_slice(&session_id.to_le_bytes());
                put_ship(&mut out, ship);
            }
            Self::HeatProfile { session_id, profile } => {
                out.extend_from_slice(&session_id.to_le_bytes());
                put_heat_profile(&mut out, profile);
            }
            Self::ModuleOverheat { session_id, module_id, active } => {
                out.extend_from_slice(&session_id.to_le_bytes());
                out.extend_from_slice(&module_id.to_le_bytes());
                out.push(u8::from(*active));
            }
            Self::ModuleOffline { session_id, module_id } => {
                out.extend_from_slice(&session_id.to_le_bytes());
                out.extend_from_slice(&module_id.to_le_bytes());
            }
            Self::RackOverheat { session_id, rack, active } => {
                out.extend_from_slice(&session_id.to_le_bytes());
                out.push(*rack as u8);
                out.push(u8::from(*active));
            }
            Self::Notify { session_id, name, id_type, args } => {
                out.extend_from_slice(&session_id.to_le_bytes());
                put_text(&mut out, name);
                put_text(&mut out, id_type);
                put_bytes(&mut out, args);
            }
            Self::Docked { session_id, station_id, condition } => {
                out.extend_from_slice(&session_id.to_le_bytes());
                out.extend_from_slice(&station_id.to_le_bytes());
                put_condition(&mut out, condition);
            }
            Self::Arrived { session_id, belt_id, position } => {
                out.extend_from_slice(&session_id.to_le_bytes());
                out.extend_from_slice(&belt_id.to_le_bytes());
                put_vector(&mut out, position);
            }
            Self::Condition { session_id, command, condition } => {
                out.extend_from_slice(&session_id.to_le_bytes());
                out.push(*command);
                put_condition(&mut out, condition);
            }
            Self::Refused { session_id, command, reason } => {
                out.extend_from_slice(&session_id.to_le_bytes());
                out.push(*command);
                put_text(&mut out, reason);
            }
            Self::Accepted { session_id, command, at } => {
                out.extend_from_slice(&session_id.to_le_bytes());
                out.push(*command);
                out.extend_from_slice(&at.to_le_bytes());
            }
            Self::Position { session_id, command, position } => {
                out.extend_from_slice(&session_id.to_le_bytes());
                out.push(*command);
                put_vector(&mut out, position);
            }
            Self::DirectionalTargets { session_id, command, targets } => {
                out.extend_from_slice(&session_id.to_le_bytes());
                out.push(*command);
                out.extend_from_slice(&(targets.len() as u32).to_le_bytes());
                for target in targets {
                    out.extend_from_slice(&target.id.to_le_bytes());
                    out.push(target.kind);
                    out.extend_from_slice(&target.type_id.to_le_bytes());
                    put_vector(&mut out, &target.position);
                    out.extend_from_slice(&target.radius.to_le_bytes());
                    out.push(target.cloaked as u8);
                    out.push(target.warping as u8);
                }
            }
            Self::ScanTargets { session_id, command, targets } => {
                out.extend_from_slice(&session_id.to_le_bytes());
                out.push(*command);
                out.extend_from_slice(&(targets.len() as u32).to_le_bytes());
                for target in targets {
                    out.extend_from_slice(&target.id.to_le_bytes());
                    out.push(target.kind);
                    out.extend_from_slice(&target.type_id.to_le_bytes());
                    put_vector(&mut out, &target.position);
                }
            }
            Self::Approaching { session_id, command, distance } => {
                out.extend_from_slice(&session_id.to_le_bytes());
                out.push(*command);
                out.extend_from_slice(&distance.to_le_bytes());
            }
            Self::Jumping { session_id, command, destination_system, destination_gate } => {
                out.extend_from_slice(&session_id.to_le_bytes());
                out.push(*command);
                out.extend_from_slice(&destination_system.to_le_bytes());
                out.extend_from_slice(&destination_gate.to_le_bytes());
            }
            Self::Jumped { session_id, from_system, from_gate, to_system, to_gate } => {
                out.extend_from_slice(&session_id.to_le_bytes());
                out.extend_from_slice(&from_system.to_le_bytes());
                out.extend_from_slice(&from_gate.to_le_bytes());
                out.extend_from_slice(&to_system.to_le_bytes());
                out.extend_from_slice(&to_gate.to_le_bytes());
            }
            Self::Targets { session_id, command, pending, targets } => {
                out.extend_from_slice(&session_id.to_le_bytes());
                out.push(*command);
                out.push(u8::from(*pending));
                out.extend_from_slice(&(targets.len() as u32).to_le_bytes());
                for target in targets {
                    out.extend_from_slice(&target.to_le_bytes());
                }
            }
            Self::Activate { session_id, effect } => {
                out.extend_from_slice(&session_id.to_le_bytes());
                put_effect(&mut out, effect);
            }
            Self::Destroyed {
                session_id,
                ship_id,
                wreck_id,
                position,
                direction,
                radius,
                killer_ship_id,
                wreck_kind,
                ship_type_id,
                corporation_id,
            } => {
                out.extend_from_slice(&session_id.to_le_bytes());
                out.extend_from_slice(&ship_id.to_le_bytes());
                out.extend_from_slice(&wreck_id.to_le_bytes());
                for number in position.iter().chain(direction).chain([radius]) {
                    out.extend_from_slice(&number.to_le_bytes());
                }
                out.extend_from_slice(&killer_ship_id.to_le_bytes());
                put_wreck_kind(&mut out, wreck_kind.as_ref());
                out.extend_from_slice(&ship_type_id.to_le_bytes());
                out.extend_from_slice(&corporation_id.to_le_bytes());
            }
            Self::ChargeSpent { session_id, module_id, rounds_left } => {
                out.extend_from_slice(&session_id.to_le_bytes());
                out.extend_from_slice(&module_id.to_le_bytes());
                out.extend_from_slice(&rounds_left.to_le_bytes());
            }
            Self::PlaceContainer(can) => {
                out.extend_from_slice(&can.session_id.to_le_bytes());
                out.extend_from_slice(&can.container_id.to_le_bytes());
                put_wreck_kind(&mut out, Some(&can.kind));
                out.extend_from_slice(&can.radius.to_le_bytes());
                out.extend_from_slice(&can.launcher_id.to_le_bytes());
                put_credit(&mut out, &can.credit);
                out.extend_from_slice(&can.distance.to_le_bytes());
            }
            Self::ContainerPlaced { session_id, container_id, position, direction } => {
                out.extend_from_slice(&session_id.to_le_bytes());
                out.extend_from_slice(&container_id.to_le_bytes());
                put_vector(&mut out, position);
                put_vector(&mut out, direction);
            }
            Self::ChangeWreck { session_id, wreck_id, is_empty } => {
                out.extend_from_slice(&session_id.to_le_bytes());
                out.extend_from_slice(&wreck_id.to_le_bytes());
                out.push(u8::from(*is_empty));
            }
            Self::RemoveWreck { wreck_id, effect } => {
                out.extend_from_slice(&wreck_id.to_le_bytes());
                out.extend_from_slice(&effect.to_le_bytes());
            }
            Self::DroneWorkResult {
                session_id,
                drone_id,
                target_id,
                cycle_id,
                accepted,
                reason,
                units_remaining,
                delivered_units,
                complete,
            } => {
                out.extend_from_slice(&session_id.to_le_bytes());
                out.extend_from_slice(&drone_id.to_le_bytes());
                out.extend_from_slice(&target_id.to_le_bytes());
                out.extend_from_slice(&cycle_id.to_le_bytes());
                out.push(u8::from(*accepted));
                put_text(&mut out, reason);
                out.extend_from_slice(&units_remaining.to_le_bytes());
                out.extend_from_slice(&delivered_units.to_le_bytes());
                out.push(u8::from(*complete));
            }
            Self::RemoteRepairCycleResult {
                session_id,
                module_id,
                target_id,
                cycle_id,
                accepted,
                reason,
                amount,
            } => {
                out.extend_from_slice(&session_id.to_le_bytes());
                out.extend_from_slice(&module_id.to_le_bytes());
                out.extend_from_slice(&target_id.to_le_bytes());
                out.extend_from_slice(&cycle_id.to_le_bytes());
                out.push(u8::from(*accepted));
                put_text(&mut out, reason);
                out.extend_from_slice(&amount.to_le_bytes());
            }
            Self::ModuleWorkCycle {
                session_id,
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
                recipients,
            } => {
                out.extend_from_slice(&session_id.to_le_bytes());
                out.extend_from_slice(&character_id.to_le_bytes());
                out.extend_from_slice(&ship_id.to_le_bytes());
                out.extend_from_slice(&solar_system_id.to_le_bytes());
                out.extend_from_slice(&module_id.to_le_bytes());
                out.extend_from_slice(&target_id.to_le_bytes());
                out.extend_from_slice(&cycle_id.to_le_bytes());
                out.push(*command);
                for number in [
                    mining_amount_m3,
                    waste_multiplier,
                    waste_probability,
                    crit_probability,
                    crit_multiplier,
                    access_difficulty_bonus,
                    waste_roll,
                    rounding_roll,
                    crit_roll,
                ] {
                    out.extend_from_slice(&number.to_le_bytes());
                }
                out.extend_from_slice(&(recipients.len() as u32).to_le_bytes());
                for ship in recipients {
                    out.extend_from_slice(&ship.to_le_bytes());
                }
            }
            Self::ModuleWorkResult {
                session_id,
                module_id,
                target_id,
                cycle_id,
                accepted,
                reason,
                units_remaining,
                delivered_units,
                complete,
            } => {
                out.extend_from_slice(&session_id.to_le_bytes());
                out.extend_from_slice(&module_id.to_le_bytes());
                out.extend_from_slice(&target_id.to_le_bytes());
                out.extend_from_slice(&cycle_id.to_le_bytes());
                out.push(u8::from(*accepted));
                put_text(&mut out, reason);
                out.extend_from_slice(&units_remaining.to_le_bytes());
                out.extend_from_slice(&delivered_units.to_le_bytes());
                out.push(u8::from(*complete));
            }
            Self::ModuleEnded { session_id, module_id, reason } => {
                out.extend_from_slice(&session_id.to_le_bytes());
                out.extend_from_slice(&module_id.to_le_bytes());
                out.push(*reason);
            }
            Self::Measured { session_id, command, distance } => {
                out.extend_from_slice(&session_id.to_le_bytes());
                out.push(*command);
                match distance {
                    Some(distance) => {
                        out.push(1);
                        out.extend_from_slice(&distance.to_le_bytes());
                    }
                    None => out.push(0),
                }
            }
            Self::AdjustCharge { request_id, session_id, module_id, charge_type, op } => {
                out.extend_from_slice(&request_id.to_le_bytes());
                out.extend_from_slice(&session_id.to_le_bytes());
                out.extend_from_slice(&module_id.to_le_bytes());
                out.extend_from_slice(&charge_type.to_le_bytes());
                let (code, first, second) = match *op {
                    ChargeOp::Add { capacity, available } => (0u8, capacity, available),
                    ChargeOp::Remove { quantity } => (1u8, quantity, 0),
                };
                out.push(code);
                out.extend_from_slice(&first.to_le_bytes());
                out.extend_from_slice(&second.to_le_bytes());
            }
            Self::BuffState { session_id, ship_id, entries } => {
                out.extend_from_slice(&session_id.to_le_bytes());
                out.extend_from_slice(&ship_id.to_le_bytes());
                out.extend_from_slice(&(entries.len() as u32).to_le_bytes());
                for entry in entries {
                    out.extend_from_slice(&entry.collection_id.to_le_bytes());
                    out.extend_from_slice(&entry.value.to_le_bytes());
                    out.extend_from_slice(&entry.remaining_ms.to_le_bytes());
                }
            }
            Self::UpdateEffect { session_id, module_id, duration_ms, capacitor_need, turret, changes } => {
                out.extend_from_slice(&session_id.to_le_bytes());
                out.extend_from_slice(&module_id.to_le_bytes());
                out.extend_from_slice(&duration_ms.to_le_bytes());
                out.extend_from_slice(&capacitor_need.to_le_bytes());
                match turret {
                    Some(turret) => {
                        out.push(1);
                        put_turret(&mut out, turret);
                    }
                    None => out.push(0),
                }
                put_changes(&mut out, changes);
            }
            Self::ChargeAdjusted { request_id, running, before, after } => {
                out.extend_from_slice(&request_id.to_le_bytes());
                out.push(u8::from(*running));
                out.extend_from_slice(&before.to_le_bytes());
                out.extend_from_slice(&after.to_le_bytes());
            }
            Self::Respawn { session_id, ship, position, direction } => {
                out.extend_from_slice(&session_id.to_le_bytes());
                put_ship(&mut out, ship);
                put_vector(&mut out, position);
                put_vector(&mut out, direction);
            }
            Self::PlaceWreck(placement) => {
                out.extend_from_slice(&placement.solar_system_id.to_le_bytes());
                out.extend_from_slice(&placement.wreck_id.to_le_bytes());
                out.extend_from_slice(&placement.group_id.to_le_bytes());
                put_wreck_kind(&mut out, Some(&placement.kind));
                put_vector(&mut out, &placement.position);
                put_vector(&mut out, &placement.direction);
                out.extend_from_slice(&placement.radius.to_le_bytes());
                out.extend_from_slice(&placement.launcher_id.to_le_bytes());
                put_credit(&mut out, &placement.credit);
                out.push(u8::from(placement.is_empty));
            }
            Self::Deployed(placement) => {
                out.extend_from_slice(&placement.solar_system_id.to_le_bytes());
                out.extend_from_slice(&placement.deployable_id.to_le_bytes());
                put_text(&mut out, &placement.name);
                out.extend_from_slice(&placement.type_id.to_le_bytes());
                out.extend_from_slice(&placement.group_id.to_le_bytes());
                put_vector(&mut out, &placement.position);
                out.extend_from_slice(&placement.radius.to_le_bytes());
                out.extend_from_slice(&placement.owner_id.to_le_bytes());
                out.extend_from_slice(&placement.corporation_id.to_le_bytes());
                out.extend_from_slice(&placement.alliance_id.to_le_bytes());
                out.extend_from_slice(&placement.ship_id.to_le_bytes());
                out.extend_from_slice(&placement.lifetime_ms.to_le_bytes());
            }
            Self::RemoveDeployed { deployable_id } => out.extend_from_slice(&deployable_id.to_le_bytes()),
            Self::PlaceProbe(probe) => {
                out.extend_from_slice(&probe.probe_id.to_le_bytes());
                out.extend_from_slice(&probe.solar_system_id.to_le_bytes());
                put_text(&mut out, &probe.name);
                out.extend_from_slice(&probe.type_id.to_le_bytes());
                out.extend_from_slice(&probe.group_id.to_le_bytes());
                put_vector(&mut out, &probe.position);
                out.extend_from_slice(&probe.radius.to_le_bytes());
                out.extend_from_slice(&probe.owner_id.to_le_bytes());
                out.extend_from_slice(&probe.corporation_id.to_le_bytes());
                out.extend_from_slice(&probe.alliance_id.to_le_bytes());
                out.extend_from_slice(&probe.ship_id.to_le_bytes());
            }
            Self::PlaceStructure(structure) => {
                out.extend_from_slice(&structure.structure_id.to_le_bytes());
                out.extend_from_slice(&structure.solar_system_id.to_le_bytes());
                put_text(&mut out, &structure.name);
                out.extend_from_slice(&structure.type_id.to_le_bytes());
                out.extend_from_slice(&structure.group_id.to_le_bytes());
                put_vector(&mut out, &structure.position);
                out.extend_from_slice(&structure.radius.to_le_bytes());
                out.extend_from_slice(&structure.owner_id.to_le_bytes());
                out.extend_from_slice(&structure.corporation_id.to_le_bytes());
                out.extend_from_slice(&structure.alliance_id.to_le_bytes());
                put_structure_health(
                    &mut out,
                    &structure.vitals,
                    structure.shield_recharge_ms,
                    &structure.limits,
                );
                put_structure_status(&mut out, &structure.status);
            }
            Self::PlaceWormhole(wormhole) => {
                out.extend_from_slice(&wormhole.wormhole_id.to_le_bytes());
                out.extend_from_slice(&wormhole.solar_system_id.to_le_bytes());
                put_text(&mut out, &wormhole.name);
                out.extend_from_slice(&wormhole.type_id.to_le_bytes());
                out.extend_from_slice(&wormhole.group_id.to_le_bytes());
                put_vector(&mut out, &wormhole.position);
                out.extend_from_slice(&wormhole.radius.to_le_bytes());
                out.extend_from_slice(&wormhole.destination_system_id.to_le_bytes());
                out.extend_from_slice(&wormhole.destination_end.to_le_bytes());
                out.extend_from_slice(&wormhole.destination_class.to_le_bytes());
                put_vector(&mut out, &wormhole.destination_position);
                out.extend_from_slice(&wormhole.destination_radius.to_le_bytes());
                out.extend_from_slice(&wormhole.max_jump_mass.to_le_bytes());
            }
            Self::RemoveWormhole { wormhole_id } => out.extend_from_slice(&wormhole_id.to_le_bytes()),
            Self::RemoveStructure { structure_id } => out.extend_from_slice(&structure_id.to_le_bytes()),
            Self::StructureDamaged { structure_id, depleted, over_threshold } => {
                out.extend_from_slice(&structure_id.to_le_bytes());
                out.push(match depleted {
                    None => 0,
                    Some(Layer::Shield) => 1,
                    Some(Layer::Armor) => 2,
                    Some(Layer::Structure) => 3,
                });
                out.push(u8::from(*over_threshold));
            }
            Self::SetStructureState { solar_system_id, structure_id, status } => {
                out.extend_from_slice(&solar_system_id.to_le_bytes());
                out.extend_from_slice(&structure_id.to_le_bytes());
                put_structure_status(&mut out, status);
            }
            Self::RemoveProbe { probe_id } => out.extend_from_slice(&probe_id.to_le_bytes()),
            Self::PlaceDisruptor(disruptor)
            | Self::PlaceInhibitor(disruptor)
            | Self::PlaceCynoBeacon(disruptor) => {
                out.extend_from_slice(&disruptor.deployable_id.to_le_bytes());
                out.extend_from_slice(&disruptor.solar_system_id.to_le_bytes());
                put_text(&mut out, &disruptor.name);
                out.extend_from_slice(&disruptor.type_id.to_le_bytes());
                out.extend_from_slice(&disruptor.group_id.to_le_bytes());
                put_vector(&mut out, &disruptor.position);
                out.extend_from_slice(&disruptor.radius.to_le_bytes());
                out.extend_from_slice(&disruptor.range.to_le_bytes());
                out.push(u8::from(disruptor.active));
                out.extend_from_slice(&disruptor.owner_id.to_le_bytes());
                out.extend_from_slice(&disruptor.corporation_id.to_le_bytes());
                out.extend_from_slice(&disruptor.alliance_id.to_le_bytes());
                out.extend_from_slice(&disruptor.ship_id.to_le_bytes());
            }
            Self::SetDisruptorActive { deployable_id, active } => {
                out.extend_from_slice(&deployable_id.to_le_bytes());
                out.push(u8::from(*active));
            }
            Self::PlaceAnalysisBeacon(beacon) => {
                out.extend_from_slice(&beacon.deployable_id.to_le_bytes());
                out.extend_from_slice(&beacon.solar_system_id.to_le_bytes());
                put_text(&mut out, &beacon.name);
                out.extend_from_slice(&beacon.type_id.to_le_bytes());
                out.extend_from_slice(&beacon.group_id.to_le_bytes());
                put_vector(&mut out, &beacon.position);
                out.extend_from_slice(&beacon.radius.to_le_bytes());
                out.extend_from_slice(&beacon.owner_id.to_le_bytes());
                out.extend_from_slice(&beacon.corporation_id.to_le_bytes());
                out.extend_from_slice(&beacon.alliance_id.to_le_bytes());
                out.extend_from_slice(&beacon.ship_id.to_le_bytes());
                put_analysis_beacon_state(&mut out, &beacon.state);
            }
            Self::SetAnalysisBeaconState { deployable_id, state } => {
                out.extend_from_slice(&deployable_id.to_le_bytes());
                put_analysis_beacon_state(&mut out, state);
            }
            Self::PlaceAsteroid(asteroid) => {
                out.extend_from_slice(&asteroid.solar_system_id.to_le_bytes());
                out.extend_from_slice(&asteroid.asteroid_id.to_le_bytes());
                out.extend_from_slice(&asteroid.type_id.to_le_bytes());
                out.extend_from_slice(&asteroid.group_id.to_le_bytes());
                out.extend_from_slice(&asteroid.category_id.to_le_bytes());
                put_text(&mut out, &asteroid.name);
                match asteroid.graphic_id {
                    Some(id) => {
                        out.push(1);
                        out.extend_from_slice(&id.to_le_bytes());
                    }
                    None => out.push(0),
                }
                put_vector(&mut out, &asteroid.position);
                put_vector(&mut out, &asteroid.direction);
                out.extend_from_slice(&asteroid.radius.to_le_bytes());
                out.extend_from_slice(&asteroid.units_remaining.to_le_bytes());
            }
            Self::Spawn(spawn) => {
                out.extend_from_slice(&spawn.request_id.to_le_bytes());
                out.extend_from_slice(&spawn.solar_system_id.to_le_bytes());
                put_ship(&mut out, &spawn.ship);
                out.extend_from_slice(&spawn.anchor_ship_id.to_le_bytes());
                for number in spawn.position.iter().chain(&spawn.velocity).chain(&spawn.direction) {
                    out.extend_from_slice(&number.to_le_bytes());
                }
                put_text(&mut out, &spawn.behavior);
                put_identity(&mut out, &spawn.identity);
            }
            Self::SpawnReply { request_id, outcome } => {
                out.extend_from_slice(&request_id.to_le_bytes());
                match outcome {
                    Ok(session) => {
                        out.push(0);
                        out.extend_from_slice(&session.to_le_bytes());
                    }
                    Err(reason) => {
                        out.push(1);
                        put_text(&mut out, reason);
                    }
                }
            }
            Self::LaunchDrone(launch) => {
                out.extend_from_slice(&launch.request_id.to_le_bytes());
                out.extend_from_slice(&launch.session_id.to_le_bytes());
                put_ship(&mut out, &launch.ship);
                match &launch.weapon {
                    Some(weapon) => put_weapon(&mut out, weapon),
                    None => put_weapon(
                        &mut out,
                        &NpcWeapon {
                            module_id: 0,
                            effect_id: 0,
                            cycle_ms: 0.0,
                            capacitor_need: 0.0,
                            turret: Turret {
                                weapon_type_id: 0,
                                fx: String::new(),
                                raw_damage: [0.0; 4],
                                optimal_range: 0.0,
                                falloff: 0.0,
                                tracking_speed: 0.0,
                                signature_resolution: 0.0,
                                charge: None,
                                missile: None,
                                spool: None,
                                aoe: None,
                                arc: None,
                                requires_gunnery: false,
                            },
                        },
                    ),
                }
                out.extend_from_slice(&launch.graphic_id.to_le_bytes());
                out.extend_from_slice(&launch.orbit_distance.to_le_bytes());
                out.extend_from_slice(&launch.attack_range.to_le_bytes());
                out.extend_from_slice(&launch.cruise_fraction.to_le_bytes());
                out.extend_from_slice(&launch.mining_amount_m3.to_le_bytes());
                out.extend_from_slice(&launch.work_cycle_ms.to_le_bytes());
                out.extend_from_slice(&launch.mining_waste_multiplier.to_le_bytes());
                out.extend_from_slice(&launch.mining_waste_probability.to_le_bytes());
                out.extend_from_slice(&launch.salvage_access_bonus.to_le_bytes());
                match &launch.effect {
                    None => out.push(0),
                    Some(effect) => {
                        out.push(1);
                        out.extend_from_slice(&effect.effect_id.to_le_bytes());
                        put_text(&mut out, &effect.fx);
                        out.extend_from_slice(&effect.cycle_ms.to_le_bytes());
                        out.extend_from_slice(&effect.lasting_ms.to_le_bytes());
                        put_action(&mut out, &effect.action);
                    }
                }
            }
            Self::DroneCommand { session_id, command, target_id, drone_ids, control_range } => {
                out.extend_from_slice(&session_id.to_le_bytes());
                out.push(*command);
                out.extend_from_slice(&target_id.to_le_bytes());
                out.extend_from_slice(&(drone_ids.len() as u32).to_le_bytes());
                for drone in drone_ids {
                    out.extend_from_slice(&drone.to_le_bytes());
                }
                out.extend_from_slice(&control_range.to_le_bytes());
            }
            Self::DroneResults { session_id, command, results } => {
                out.extend_from_slice(&session_id.to_le_bytes());
                out.push(*command);
                out.extend_from_slice(&(results.len() as u32).to_le_bytes());
                for (drone, reason) in results {
                    out.extend_from_slice(&drone.to_le_bytes());
                    put_text(&mut out, reason);
                }
            }
            Self::DroneScooped { session_id, drone_id } => {
                out.extend_from_slice(&session_id.to_le_bytes());
                out.extend_from_slice(&drone_id.to_le_bytes());
            }
            Self::DroneReleased { session_id, drone_id, position } => {
                out.extend_from_slice(&session_id.to_le_bytes());
                out.extend_from_slice(&drone_id.to_le_bytes());
                put_vector(&mut out, position);
            }
            Self::LaunchFighter {
                request_id,
                session_id,
                ship,
                graphic_id,
                cruise_fraction,
                squadron_size,
            } => {
                out.extend_from_slice(&request_id.to_le_bytes());
                out.extend_from_slice(&session_id.to_le_bytes());
                put_ship(&mut out, ship);
                out.extend_from_slice(&graphic_id.to_le_bytes());
                out.extend_from_slice(&cruise_fraction.to_le_bytes());
                out.extend_from_slice(&squadron_size.to_le_bytes());
            }
            Self::FighterSize { session_id, fighter_id, size } => {
                out.extend_from_slice(&session_id.to_le_bytes());
                out.extend_from_slice(&fighter_id.to_le_bytes());
                out.extend_from_slice(&size.to_le_bytes());
            }
            Self::FighterOrder { session_id, command, target_id, fighter_ids, range } => {
                out.extend_from_slice(&session_id.to_le_bytes());
                out.push(*command);
                out.extend_from_slice(&target_id.to_le_bytes());
                out.extend_from_slice(&(fighter_ids.len() as u32).to_le_bytes());
                for fighter in fighter_ids {
                    out.extend_from_slice(&fighter.to_le_bytes());
                }
                out.extend_from_slice(&range.to_le_bytes());
            }
            Self::FighterGotoPoint { session_id, fighter_ids, point } => {
                out.extend_from_slice(&session_id.to_le_bytes());
                out.extend_from_slice(&(fighter_ids.len() as u32).to_le_bytes());
                for fighter in fighter_ids {
                    out.extend_from_slice(&fighter.to_le_bytes());
                }
                put_vector(&mut out, point);
            }
            Self::FighterResults { session_id, command, results } => {
                out.extend_from_slice(&session_id.to_le_bytes());
                out.push(*command);
                out.extend_from_slice(&(results.len() as u32).to_le_bytes());
                for (fighter, reason) in results {
                    out.extend_from_slice(&fighter.to_le_bytes());
                    put_text(&mut out, reason);
                }
            }
            Self::FighterLanded { session_id, fighter_id } => {
                out.extend_from_slice(&session_id.to_le_bytes());
                out.extend_from_slice(&fighter_id.to_le_bytes());
            }
            Self::EcmBurst { session_id, ship_id, reached } => {
                out.extend_from_slice(&session_id.to_le_bytes());
                out.extend_from_slice(&ship_id.to_le_bytes());
                out.extend_from_slice(&(reached.len() as u32).to_le_bytes());
                for (ship, jammed) in reached {
                    out.extend_from_slice(&ship.to_le_bytes());
                    out.push(u8::from(*jammed));
                }
            }
            Self::FighterReleased { session_id, fighter_id, position } => {
                out.extend_from_slice(&session_id.to_le_bytes());
                out.extend_from_slice(&fighter_id.to_le_bytes());
                put_vector(&mut out, position);
            }
            Self::DroneWorkCycle {
                session_id,
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
            } => {
                out.extend_from_slice(&session_id.to_le_bytes());
                out.extend_from_slice(&character_id.to_le_bytes());
                out.extend_from_slice(&ship_id.to_le_bytes());
                out.extend_from_slice(&solar_system_id.to_le_bytes());
                out.extend_from_slice(&drone_id.to_le_bytes());
                out.extend_from_slice(&target_id.to_le_bytes());
                out.extend_from_slice(&cycle_id.to_le_bytes());
                out.push(*command);
                out.extend_from_slice(&mining_amount_m3.to_le_bytes());
                out.extend_from_slice(&waste_multiplier.to_le_bytes());
                out.extend_from_slice(&waste_probability.to_le_bytes());
                out.extend_from_slice(&access_difficulty_bonus.to_le_bytes());
                out.extend_from_slice(&roll.to_le_bytes());
            }
            Self::RemoteRepairCycle {
                session_id,
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
            } => {
                out.extend_from_slice(&session_id.to_le_bytes());
                out.extend_from_slice(&module_id.to_le_bytes());
                out.extend_from_slice(&target_id.to_le_bytes());
                out.extend_from_slice(&cycle_id.to_le_bytes());
                out.extend_from_slice(&cycle_number.to_le_bytes());
                out.extend_from_slice(&cycle_at_ms.to_le_bytes());
                out.extend_from_slice(&cycle_duration_ms.to_le_bytes());
                out.push(u8::from(*ends_effect_after_cycle));
                out.extend_from_slice(&base_amount.to_le_bytes());
                out.extend_from_slice(&bonus_per_cycle.to_le_bytes());
                out.extend_from_slice(&bonus_max.to_le_bytes());
            }
            Self::TetherPolicy { session_id, blocked_for_ms, lease_ms, candidates } => {
                out.extend_from_slice(&session_id.to_le_bytes());
                out.extend_from_slice(&blocked_for_ms.to_le_bytes());
                out.extend_from_slice(&lease_ms.to_le_bytes());
                out.extend_from_slice(&(candidates.len() as u32).to_le_bytes());
                for candidate in candidates {
                    out.extend_from_slice(&(candidate.structure_id as u64).to_le_bytes());
                    out.extend_from_slice(&candidate.range.to_le_bytes());
                }
            }
            Self::TetherChanged { session_id, previous_structure_id, structure_id, repairing } => {
                out.extend_from_slice(&session_id.to_le_bytes());
                out.extend_from_slice(&(*previous_structure_id as u64).to_le_bytes());
                out.extend_from_slice(&(*structure_id as u64).to_le_bytes());
                out.push(u8::from(*repairing));
            }
            Self::Destroy { ship_id, credit } => {
                out.extend_from_slice(&ship_id.to_le_bytes());
                put_credit(&mut out, credit);
            }
            Self::TargetRefused {
                session_id,
                command,
                reason,
                target_type,
                desired_range,
                actual_distance,
            } => {
                out.extend_from_slice(&session_id.to_le_bytes());
                out.push(*command);
                put_text(&mut out, reason);
                out.extend_from_slice(&target_type.to_le_bytes());
                out.extend_from_slice(&desired_range.to_le_bytes());
                out.extend_from_slice(&actual_distance.to_le_bytes());
            }
        }
        out
    }

    /// The whole frame, length prefix included.
    pub fn encode(&self) -> Vec<u8> {
        let payload = self.payload();
        let mut frame = Vec::with_capacity(4 + payload.len());
        frame.extend_from_slice(&(payload.len() as u32).to_le_bytes());
        frame.extend_from_slice(&payload);
        frame
    }
}

/// A CRAB's state as the host sends it (protocol 61): the activation flag, the two FILETIME times, the wait in seconds, the link state, the link's end and the ship.
fn put_analysis_beacon_state(out: &mut Vec<u8>, state: &AnalysisBeaconState) {
    out.push(u8::from(state.active));
    out.extend_from_slice(&state.activate_at.to_le_bytes());
    out.extend_from_slice(&state.deployed_at.to_le_bytes());
    out.extend_from_slice(&state.activation_seconds.to_le_bytes());
    out.extend_from_slice(&state.link_state.to_le_bytes());
    out.extend_from_slice(&state.link_complete_at.to_le_bytes());
    out.extend_from_slice(&state.linked_ship_id.to_le_bytes());
}
