use super::*;

pub(super) fn read_heat_profile(reader: &mut Reader) -> Result<HeatProfile, DecodeError> {
    let generation_multiplier = reader.f64()?;
    let damage_multiplier = reader.f64()?;
    let thermodynamics_level = reader.i32()?;
    if !(0..=5).contains(&thermodynamics_level) {
        return Err(DecodeError::BadValue("thermodynamics_level"));
    }
    let capacity = [reader.f64()?, reader.f64()?, reader.f64()?];
    let initial_rack_heat = [reader.f64()?, reader.f64()?, reader.f64()?];
    let dissipation_rate = [reader.f64()?, reader.f64()?, reader.f64()?];
    let attenuation = [reader.f64()?, reader.f64()?, reader.f64()?];
    let count = reader.i32()?;
    if !(0..=256).contains(&count) {
        return Err(DecodeError::BadValue("heat modules"));
    }
    let mut modules = Vec::with_capacity(count as usize);
    for _ in 0..count {
        let module_id = reader.i64()?;
        let flag = reader.i32()?;
        let structure_hp = reader.f64()?;
        let damage_hp = reader.f64()?;
        let heat_damage_hp = reader.f64()?;
        let heat_absorption_rate = reader.f64()?;
        let overloadable = reader.u8()?;
        let heat_immune = reader.u8()?;
        let online = reader.u8()?;
        if overloadable > 1 || heat_immune > 1 || online > 1 {
            return Err(DecodeError::BadValue("heat module flag"));
        }
        modules.push(HeatModuleProfile {
            module_id,
            flag,
            structure_hp,
            damage_hp,
            heat_damage_hp,
            heat_absorption_rate,
            overloadable: overloadable == 1,
            heat_immune: heat_immune == 1,
            online: online == 1,
        });
    }
    Ok(HeatProfile {
        generation_multiplier,
        damage_multiplier,
        thermodynamics_level: thermodynamics_level as u8,
        capacity,
        initial_rack_heat,
        dissipation_rate,
        attenuation,
        modules,
    })
}

pub(super) fn read_ship(reader: &mut Reader) -> Result<ShipSpec, DecodeError> {
    let ship_id = reader.i64()?;
    let character_id = reader.i64()?;
    let type_id = reader.i32()?;
    let group_id = reader.i32()?;
    let category_id = reader.i32()?;
    let name = reader.text()?;
    let owner_id = reader.i64()?;
    let corporation_id = reader.i32()?;
    let alliance_id = reader.i32()?;
    let war_faction_id = reader.i32()?;
    let [
        security_status,
        radius,
        mass,
        max_velocity,
        agility,
        warp_speed_au,
        shield_recharge_ms,
        max_target_range,
        scan_resolution,
        signature_radius,
        capacitor_capacity,
        capacitor_recharge_s,
    ] = [
        reader.f64()?,
        reader.f64()?,
        reader.f64()?,
        reader.f64()?,
        reader.f64()?,
        reader.f64()?,
        reader.f64()?,
        reader.f64()?,
        reader.f64()?,
        reader.f64()?,
        reader.f64()?,
        reader.f64()?,
    ];
    let max_locked_targets = reader.i32()?;
    let vitals = read_vitals(reader)?;
    let condition = read_condition(reader)?;
    let count = reader.u32()? as usize;
    // a module takes 16 bytes, so a count the frame cannot hold is refused before anything is allocated for it
    if count > reader.data.len() / 16 {
        return Err(DecodeError::Truncated);
    }
    let mut modules = Vec::with_capacity(count);
    for _ in 0..count {
        modules.push(ModuleSpec { item_id: reader.i64()?, type_id: reader.i32()?, flag: reader.i32()? });
    }
    let sensor_strength = [reader.f64()?, reader.f64()?, reader.f64()?, reader.f64()?];
    let ecm_resistance = reader.f64()?;
    let energy_warfare_resistance = reader.f64()?;
    let drone_damage_multiplier = reader.f64()?;
    if !drone_damage_multiplier.is_finite() || drone_damage_multiplier < 0.0 {
        return Err(DecodeError::BadValue("drone damage multiplier"));
    }
    Ok(ShipSpec {
        ship_id,
        character_id,
        type_id,
        group_id,
        category_id,
        name,
        owner_id,
        corporation_id,
        alliance_id,
        war_faction_id,
        security_status,
        radius,
        mass,
        max_velocity,
        agility,
        warp_speed_au,
        shield_recharge_ms,
        max_target_range,
        max_locked_targets,
        scan_resolution,
        signature_radius,
        capacitor_capacity,
        capacitor_recharge_s,
        vitals,
        condition,
        modules,
        sensor_strength,
        ecm_resistance,
        energy_warfare_resistance,
        drone_damage_multiplier,
    })
}

impl Message {
    /// Reads a frame's payload.
    pub fn decode(payload: &[u8]) -> Result<Message, DecodeError> {
        let (&kind, body) = payload.split_first().ok_or(DecodeError::Empty)?;
        let mut r = Reader { data: body };
        let message = match kind {
            kind::HELLO => Message::Hello { version: r.u16()? },
            kind::HELLO_ACK => Message::HelloAck { version: r.u16()?, poll_hz: r.u32()? },
            kind::ENTER => {
                let session_id = r.u64()?;
                let solar_system_id = r.i32()?;
                let how = match r.u8()? {
                    0 => How::Undock,
                    1 => How::InSpace,
                    _ => return Err(DecodeError::BadValue("how")),
                };
                let station_id = r.i64()?;
                let ship = read_ship(&mut r)?;
                let (position, direction) = if how == How::InSpace {
                    (read_optional_vector(&mut r)?, read_optional_vector(&mut r)?)
                } else {
                    (None, None)
                };
                if how == How::InSpace && (position.is_none() || direction.is_none()) {
                    return Err(DecodeError::BadValue("in-space pose"));
                }
                Message::Enter(Box::new(Enter {
                    session_id,
                    solar_system_id,
                    how,
                    station_id,
                    ship,
                    position,
                    direction,
                }))
            }
            kind::ENTER_REPLY => {
                let session_id = r.u64()?;
                let status = r.u8()?;
                let reason = r.text()?;
                let outcome = match status {
                    0 => Ok(()),
                    1 => Err(reason),
                    _ => return Err(DecodeError::BadValue("status")),
                };
                Message::EnterReply { session_id, outcome }
            }
            kind::LEAVE => Message::Leave { session_id: r.u64()?, why: r.u8()? },
            kind::BIND => Message::Bind { session_id: r.u64()? },
            kind::UPDATE_STATE => Message::UpdateState { session_id: r.u64()? },
            kind::COMMAND => {
                let session_id = r.u64()?;
                let command = r.u8()?;
                let count = r.u8()? as usize;
                let mut args = Vec::with_capacity(count);
                for _ in 0..count {
                    args.push(r.f64()?);
                }
                Message::Command { session_id, command, args }
            }
            kind::SHIP_SPEC => Message::ShipSpec { session_id: r.u64()?, ship: Box::new(read_ship(&mut r)?) },
            kind::HEAT_PROFILE => {
                Message::HeatProfile { session_id: r.u64()?, profile: read_heat_profile(&mut r)? }
            }
            kind::MODULE_OVERHEAT => {
                let session_id = r.u64()?;
                let module_id = r.i64()?;
                let active = match r.u8()? {
                    0 => false,
                    1 => true,
                    _ => return Err(DecodeError::BadValue("module overheat")),
                };
                Message::ModuleOverheat { session_id, module_id, active }
            }
            kind::MODULE_OFFLINE => Message::ModuleOffline { session_id: r.u64()?, module_id: r.i64()? },
            kind::RACK_OVERHEAT => {
                let session_id = r.u64()?;
                let rack = match r.u8()? {
                    0 => HeatRack::High,
                    1 => HeatRack::Medium,
                    2 => HeatRack::Low,
                    _ => return Err(DecodeError::BadValue("heat rack")),
                };
                let active = match r.u8()? {
                    0 => false,
                    1 => true,
                    _ => return Err(DecodeError::BadValue("rack overheat")),
                };
                Message::RackOverheat { session_id, rack, active }
            }
            kind::NOTIFY => Message::Notify {
                session_id: r.u64()?,
                name: r.text()?,
                id_type: r.text()?,
                args: r.bytes()?,
            },
            kind::DOCKED => Message::Docked {
                session_id: r.u64()?,
                station_id: r.i64()?,
                condition: read_condition(&mut r)?,
            },
            kind::ARRIVED => {
                Message::Arrived { session_id: r.u64()?, belt_id: r.i64()?, position: read_vector(&mut r)? }
            }
            kind::CONDITION => Message::Condition {
                session_id: r.u64()?,
                command: r.u8()?,
                condition: read_condition(&mut r)?,
            },
            kind::REFUSED => Message::Refused { session_id: r.u64()?, command: r.u8()?, reason: r.text()? },
            kind::ACCEPTED => Message::Accepted { session_id: r.u64()?, command: r.u8()?, at: r.i64()? },
            kind::POSITION => {
                Message::Position { session_id: r.u64()?, command: r.u8()?, position: read_vector(&mut r)? }
            }
            kind::APPROACHING => {
                Message::Approaching { session_id: r.u64()?, command: r.u8()?, distance: r.f64()? }
            }
            kind::JUMPING => Message::Jumping {
                session_id: r.u64()?,
                command: r.u8()?,
                destination_system: r.i32()?,
                destination_gate: r.i64()?,
            },
            kind::JUMPED => Message::Jumped {
                session_id: r.u64()?,
                from_system: r.i32()?,
                from_gate: r.i64()?,
                to_system: r.i32()?,
                to_gate: r.i64()?,
            },
            kind::TARGETS => {
                let session_id = r.u64()?;
                let command = r.u8()?;
                let pending = match r.u8()? {
                    0 => false,
                    1 => true,
                    _ => return Err(DecodeError::BadValue("pending")),
                };
                let count = r.u32()? as usize;
                // a target takes 8 bytes: a count the frame cannot hold is refused before anything is allocated for it
                if count > r.data.len() / 8 {
                    return Err(DecodeError::Truncated);
                }
                let mut targets = Vec::with_capacity(count);
                for _ in 0..count {
                    targets.push(r.i64()?);
                }
                Message::Targets { session_id, command, pending, targets }
            }
            kind::ACTIVATE => {
                Message::Activate { session_id: r.u64()?, effect: Box::new(read_effect(&mut r)?) }
            }
            kind::DESTROYED => Message::Destroyed {
                session_id: r.u64()?,
                ship_id: r.i64()?,
                wreck_id: r.i64()?,
                position: [r.f64()?, r.f64()?, r.f64()?],
                direction: [r.f64()?, r.f64()?, r.f64()?],
                radius: r.f64()?,
                killer_ship_id: r.i64()?,
                wreck_kind: read_wreck_kind(&mut r)?,
            },
            kind::CHARGE_SPENT => {
                Message::ChargeSpent { session_id: r.u64()?, module_id: r.i64()?, rounds_left: r.i64()? }
            }
            kind::PLACE_CONTAINER => {
                let session_id = r.u64()?;
                let container_id = r.i64()?;
                let Some(kind) = read_wreck_kind(&mut r)? else {
                    return Err(DecodeError::BadValue("container"));
                };
                Message::PlaceContainer(Box::new(ContainerPlacement {
                    session_id,
                    container_id,
                    kind,
                    radius: r.f64()?,
                    launcher_id: r.i64()?,
                    credit: read_credit(&mut r)?,
                    distance: r.f64()?,
                }))
            }
            kind::CONTAINER_PLACED => Message::ContainerPlaced {
                session_id: r.u64()?,
                container_id: r.i64()?,
                position: read_vector(&mut r)?,
                direction: read_vector(&mut r)?,
            },
            kind::CHANGE_WRECK => {
                let session_id = r.u64()?;
                let wreck_id = r.i64()?;
                let is_empty = match r.u8()? {
                    0 => false,
                    1 => true,
                    _ => return Err(DecodeError::BadValue("is_empty")),
                };
                Message::ChangeWreck { session_id, wreck_id, is_empty }
            }
            kind::REMOVE_WRECK => Message::RemoveWreck { wreck_id: r.i64()?, effect: r.i32()? },
            kind::DEPLOYED => {
                let solar_system_id = r.i32()?;
                let deployable_id = r.i64()?;
                let name = r.text()?;
                let type_id = r.i32()?;
                let group_id = r.i32()?;
                let position = read_vector(&mut r)?;
                let radius = r.f64()?;
                let owner_id = r.i64()?;
                let corporation_id = r.i32()?;
                let alliance_id = r.i32()?;
                let ship_id = r.i64()?;
                let lifetime_ms = r.f64()?;
                if !(lifetime_ms.is_finite() && lifetime_ms >= 0.0 && radius.is_finite() && radius >= 0.0) {
                    return Err(DecodeError::BadValue("deployed"));
                }
                Message::Deployed(Box::new(DeployedPlacement {
                    solar_system_id,
                    deployable_id,
                    name,
                    type_id,
                    group_id,
                    position,
                    radius,
                    owner_id,
                    corporation_id,
                    alliance_id,
                    ship_id,
                    lifetime_ms,
                }))
            }
            kind::REMOVE_DEPLOYED => Message::RemoveDeployed { deployable_id: r.i64()? },
            kind::PLACE_PROBE => {
                let probe_id = r.i64()?;
                let solar_system_id = r.i32()?;
                let name = r.text()?;
                let type_id = r.i32()?;
                let group_id = r.i32()?;
                let position = read_vector(&mut r)?;
                let radius = r.f64()?;
                let owner_id = r.i64()?;
                let corporation_id = r.i32()?;
                let alliance_id = r.i32()?;
                let ship_id = r.i64()?;
                if !(radius.is_finite() && radius >= 0.0) {
                    return Err(DecodeError::BadValue("probe"));
                }
                Message::PlaceProbe(Box::new(ProbePlacement {
                    probe_id,
                    solar_system_id,
                    name,
                    type_id,
                    group_id,
                    position,
                    radius,
                    owner_id,
                    corporation_id,
                    alliance_id,
                    ship_id,
                }))
            }
            kind::REMOVE_PROBE => Message::RemoveProbe { probe_id: r.i64()? },
            kind::DRONE_WORK_RESULT => {
                let session_id = r.u64()?;
                let drone_id = r.i64()?;
                let target_id = r.i64()?;
                let cycle_id = r.u64()?;
                let accepted = match r.u8()? {
                    0 => false,
                    1 => true,
                    _ => return Err(DecodeError::BadValue("accepted")),
                };
                let reason = r.text()?;
                let units_remaining = r.i64()?;
                let delivered_units = r.i64()?;
                let complete = match r.u8()? {
                    0 => false,
                    1 => true,
                    _ => return Err(DecodeError::BadValue("complete")),
                };
                Message::DroneWorkResult {
                    session_id,
                    drone_id,
                    target_id,
                    cycle_id,
                    accepted,
                    reason,
                    units_remaining,
                    delivered_units,
                    complete,
                }
            }
            kind::REMOTE_REPAIR_CYCLE_RESULT => {
                let session_id = r.u64()?;
                let module_id = r.i64()?;
                let target_id = r.i64()?;
                let cycle_id = r.u64()?;
                let accepted = match r.u8()? {
                    0 => false,
                    1 => true,
                    _ => return Err(DecodeError::BadValue("accepted")),
                };
                let reason = r.text()?;
                let amount = r.f64()?;
                if cycle_id == 0 || !amount.is_finite() || amount < 0.0 {
                    return Err(DecodeError::BadValue("remote repair cycle result"));
                }
                Message::RemoteRepairCycleResult {
                    session_id,
                    module_id,
                    target_id,
                    cycle_id,
                    accepted,
                    reason,
                    amount,
                }
            }
            kind::MEASURED => {
                let session_id = r.u64()?;
                let command = r.u8()?;
                let distance = match r.u8()? {
                    0 => None,
                    1 => Some(r.f64()?),
                    _ => return Err(DecodeError::BadValue("distance")),
                };
                Message::Measured { session_id, command, distance }
            }
            kind::ADJUST_CHARGE => {
                let request_id = r.u64()?;
                let session_id = r.u64()?;
                let module_id = r.i64()?;
                let charge_type = r.i32()?;
                let code = r.u8()?;
                let (first, second) = (r.i64()?, r.i64()?);
                let op = match code {
                    0 => ChargeOp::Add { capacity: first, available: second },
                    1 => ChargeOp::Remove { quantity: first },
                    _ => return Err(DecodeError::BadValue("charge op")),
                };
                Message::AdjustCharge { request_id, session_id, module_id, charge_type, op }
            }
            kind::BUFF_STATE => {
                let session_id = r.u64()?;
                let ship_id = r.i64()?;
                let count = r.u32()? as usize;
                // an entry takes 20 bytes, so a count the frame cannot hold is refused before anything is allocated for it
                if count > r.data.len() / 20 {
                    return Err(DecodeError::Truncated);
                }
                let mut entries = Vec::with_capacity(count);
                for _ in 0..count {
                    let (collection_id, value, remaining_ms) = (r.i32()?, r.f64()?, r.f64()?);
                    if !value.is_finite() || !remaining_ms.is_finite() || remaining_ms < 0.0 {
                        return Err(DecodeError::BadValue("buff state"));
                    }
                    entries.push(BuffEntry { collection_id, value, remaining_ms });
                }
                Message::BuffState { session_id, ship_id, entries }
            }
            kind::UPDATE_EFFECT => {
                let session_id = r.u64()?;
                let module_id = r.i64()?;
                let (duration_ms, capacitor_need) = (r.f64()?, r.f64()?);
                if !duration_ms.is_finite()
                    || duration_ms <= 0.0
                    || !capacitor_need.is_finite()
                    || capacitor_need < 0.0
                {
                    return Err(DecodeError::BadValue("update effect"));
                }
                let turret = match r.u8()? {
                    0 => None,
                    1 => Some(Box::new(read_turret(&mut r)?)),
                    _ => return Err(DecodeError::BadValue("update turret")),
                };
                let changes = read_changes(&mut r)?;
                Message::UpdateEffect { session_id, module_id, duration_ms, capacitor_need, turret, changes }
            }
            kind::CHARGE_ADJUSTED => {
                let request_id = r.u64()?;
                let running = match r.u8()? {
                    0 => false,
                    1 => true,
                    _ => return Err(DecodeError::BadValue("running")),
                };
                Message::ChargeAdjusted { request_id, running, before: r.i64()?, after: r.i64()? }
            }
            kind::RESPAWN => Message::Respawn {
                session_id: r.u64()?,
                ship: Box::new(read_ship(&mut r)?),
                position: read_vector(&mut r)?,
                direction: read_vector(&mut r)?,
            },
            kind::PLACE_ASTEROID => {
                let solar_system_id = r.i32()?;
                let asteroid_id = r.i64()?;
                let type_id = r.i32()?;
                let group_id = r.i32()?;
                let category_id = r.i32()?;
                let name = r.text()?;
                let graphic_id = match r.u8()? {
                    0 => None,
                    1 => Some(r.i32()?),
                    _ => return Err(DecodeError::BadValue("graphic_id")),
                };
                let position = read_vector(&mut r)?;
                let direction = read_vector(&mut r)?;
                let radius = r.f64()?;
                let units_remaining = r.i64()?;
                Message::PlaceAsteroid(Box::new(AsteroidPlacement {
                    solar_system_id,
                    asteroid_id,
                    type_id,
                    group_id,
                    category_id,
                    name,
                    graphic_id,
                    position,
                    direction,
                    radius,
                    units_remaining,
                }))
            }
            kind::PLACE_WRECK => {
                let solar_system_id = r.i32()?;
                let wreck_id = r.i64()?;
                let group_id = r.i32()?;
                let Some(kind) = read_wreck_kind(&mut r)? else { return Err(DecodeError::BadValue("wreck")) };
                let position = read_vector(&mut r)?;
                let direction = read_vector(&mut r)?;
                let radius = r.f64()?;
                let launcher_id = r.i64()?;
                let credit = read_credit(&mut r)?;
                let is_empty = match r.u8()? {
                    0 => false,
                    1 => true,
                    _ => return Err(DecodeError::BadValue("is_empty")),
                };
                Message::PlaceWreck(Box::new(WreckPlacement {
                    solar_system_id,
                    wreck_id,
                    group_id,
                    kind,
                    position,
                    direction,
                    radius,
                    launcher_id,
                    credit,
                    is_empty,
                }))
            }
            kind::SPAWN => {
                let request_id = r.u64()?;
                let solar_system_id = r.i32()?;
                let ship = read_ship(&mut r)?;
                let anchor_ship_id = r.i64()?;
                let mut vectors = [[0.0; 3]; 3];
                for vector in &mut vectors {
                    *vector = [r.f64()?, r.f64()?, r.f64()?];
                }
                let behavior = r.text()?;
                let identity = read_identity(&mut r)?;
                Message::Spawn(Box::new(Spawn {
                    request_id,
                    solar_system_id,
                    ship,
                    anchor_ship_id,
                    position: vectors[0],
                    velocity: vectors[1],
                    direction: vectors[2],
                    behavior,
                    identity,
                }))
            }
            kind::SPAWN_REPLY => {
                let request_id = r.u64()?;
                let outcome = match r.u8()? {
                    0 => Ok(r.u64()?),
                    1 => Err(r.text()?),
                    _ => return Err(DecodeError::BadValue("status")),
                };
                Message::SpawnReply { request_id, outcome }
            }
            kind::LAUNCH_DRONE => Message::LaunchDrone(Box::new(LaunchDrone {
                request_id: r.u64()?,
                session_id: r.u64()?,
                ship: read_ship(&mut r)?,
                weapon: {
                    let weapon = read_weapon(&mut r)?;
                    (weapon.module_id != 0).then_some(weapon)
                },
                graphic_id: r.i32()?,
                orbit_distance: r.f64()?,
                attack_range: r.f64()?,
                cruise_fraction: r.f64()?,
                mining_amount_m3: r.f64()?,
                work_cycle_ms: r.f64()?,
                mining_waste_multiplier: r.f64()?,
                mining_waste_probability: r.f64()?,
                salvage_access_bonus: r.f64()?,
                effect: match r.u8()? {
                    0 => None,
                    1 => Some(DroneEffect {
                        effect_id: r.i32()?,
                        fx: r.text()?,
                        cycle_ms: r.f64()?,
                        lasting_ms: r.f64()?,
                        action: read_action(&mut r)?,
                    }),
                    _ => return Err(DecodeError::BadValue("drone effect flag")),
                },
            })),
            kind::DRONE_COMMAND => {
                let (session_id, command, target_id) = (r.u64()?, r.u8()?, r.i64()?);
                let count = r.u32()? as usize;
                if count > r.data.len().saturating_sub(8) / 8 {
                    return Err(DecodeError::Truncated);
                }
                let drone_ids = (0..count).map(|_| r.i64()).collect::<Result<Vec<_>, _>>()?;
                let control_range = r.f64()?;
                Message::DroneCommand { session_id, command, target_id, drone_ids, control_range }
            }
            kind::DRONE_RESULTS => {
                let (session_id, command) = (r.u64()?, r.u8()?);
                let count = r.u32()? as usize;
                if count > r.data.len() / 12 {
                    return Err(DecodeError::Truncated);
                }
                let results =
                    (0..count).map(|_| Ok((r.i64()?, r.text()?))).collect::<Result<Vec<_>, DecodeError>>()?;
                Message::DroneResults { session_id, command, results }
            }
            kind::DRONE_SCOOPED => Message::DroneScooped { session_id: r.u64()?, drone_id: r.i64()? },
            kind::DRONE_RELEASED => Message::DroneReleased {
                session_id: r.u64()?,
                drone_id: r.i64()?,
                position: read_vector(&mut r)?,
            },
            kind::LAUNCH_FIGHTER => {
                let request_id = r.u64()?;
                let session_id = r.u64()?;
                let ship = read_ship(&mut r)?;
                let graphic_id = r.i32()?;
                let cruise_fraction = r.f64()?;
                let squadron_size = r.i32()?;
                if !(1..=64).contains(&squadron_size) {
                    return Err(DecodeError::BadValue("squadron size"));
                }
                Message::LaunchFighter {
                    request_id,
                    session_id,
                    ship: Box::new(ship),
                    graphic_id,
                    cruise_fraction,
                    squadron_size,
                }
            }
            kind::FIGHTER_SIZE => {
                Message::FighterSize { session_id: r.u64()?, fighter_id: r.i64()?, size: r.i32()? }
            }
            kind::FIGHTER_ORDER => {
                let (session_id, command, target_id) = (r.u64()?, r.u8()?, r.i64()?);
                let count = r.u32()? as usize;
                if count > r.data.len().saturating_sub(8) / 8 {
                    return Err(DecodeError::Truncated);
                }
                let fighter_ids = (0..count).map(|_| r.i64()).collect::<Result<Vec<_>, _>>()?;
                let range = r.f64()?;
                Message::FighterOrder { session_id, command, target_id, fighter_ids, range }
            }
            kind::FIGHTER_GOTO_POINT => {
                let session_id = r.u64()?;
                let count = r.u32()? as usize;
                if count > r.data.len().saturating_sub(8) / 8 {
                    return Err(DecodeError::Truncated);
                }
                let fighter_ids = (0..count).map(|_| r.i64()).collect::<Result<Vec<_>, _>>()?;
                Message::FighterGotoPoint { session_id, fighter_ids, point: read_vector(&mut r)? }
            }
            kind::FIGHTER_RESULTS => {
                let (session_id, command) = (r.u64()?, r.u8()?);
                let count = r.u32()? as usize;
                if count > r.data.len() / 12 {
                    return Err(DecodeError::Truncated);
                }
                let results =
                    (0..count).map(|_| Ok((r.i64()?, r.text()?))).collect::<Result<Vec<_>, DecodeError>>()?;
                Message::FighterResults { session_id, command, results }
            }
            kind::FIGHTER_LANDED => Message::FighterLanded { session_id: r.u64()?, fighter_id: r.i64()? },
            kind::FIGHTER_RELEASED => Message::FighterReleased {
                session_id: r.u64()?,
                fighter_id: r.i64()?,
                position: read_vector(&mut r)?,
            },
            kind::DRONE_WORK_CYCLE => Message::DroneWorkCycle {
                session_id: r.u64()?,
                character_id: r.i64()?,
                ship_id: r.i64()?,
                solar_system_id: r.i32()?,
                drone_id: r.i64()?,
                target_id: r.i64()?,
                cycle_id: r.u64()?,
                command: r.u8()?,
                mining_amount_m3: r.f64()?,
                waste_multiplier: r.f64()?,
                waste_probability: r.f64()?,
                access_difficulty_bonus: r.f64()?,
                roll: r.f64()?,
            },
            kind::MODULE_WORK_CYCLE => {
                let session_id = r.u64()?;
                let character_id = r.i64()?;
                let ship_id = r.i64()?;
                let solar_system_id = r.i32()?;
                let module_id = r.i64()?;
                let target_id = r.i64()?;
                let cycle_id = r.u64()?;
                let command = r.u8()?;
                let mining_amount_m3 = r.f64()?;
                let waste_multiplier = r.f64()?;
                let waste_probability = r.f64()?;
                let crit_probability = r.f64()?;
                let crit_multiplier = r.f64()?;
                let access_difficulty_bonus = r.f64()?;
                let waste_roll = r.f64()?;
                let rounding_roll = r.f64()?;
                let crit_roll = r.f64()?;
                let recipients = read_recipients(&mut r)?;
                let numbers = [mining_amount_m3, waste_multiplier, crit_multiplier, access_difficulty_bonus];
                // probabilities are at most one, the rolls below one, and every number finite and not negative; a mining cycle mines something
                if cycle_id == 0
                    || !matches!(command, 7..=13)
                    || numbers
                        .iter()
                        .chain([
                            &waste_probability,
                            &crit_probability,
                            &waste_roll,
                            &rounding_roll,
                            &crit_roll,
                        ])
                        .any(|n| !n.is_finite() || *n < 0.0)
                    || waste_probability > 1.0
                    || crit_probability > 1.0
                    || [waste_roll, rounding_roll, crit_roll].iter().any(|roll| *roll >= 1.0)
                    || (command == 7 && mining_amount_m3 <= 0.0)
                    || (matches!(command, 9..=13)
                        && (mining_amount_m3 != 0.0
                            || waste_multiplier != 0.0
                            || waste_probability != 0.0
                            || crit_probability != 0.0
                            || crit_multiplier != 0.0
                            || access_difficulty_bonus != 0.0))
                    || (!matches!(command, 12 | 13) && !recipients.is_empty())
                {
                    return Err(DecodeError::BadValue("module work cycle"));
                }
                Message::ModuleWorkCycle {
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
                }
            }
            kind::MODULE_WORK_RESULT => {
                let session_id = r.u64()?;
                let module_id = r.i64()?;
                let target_id = r.i64()?;
                let cycle_id = r.u64()?;
                let accepted = match r.u8()? {
                    0 => false,
                    1 => true,
                    _ => return Err(DecodeError::BadValue("accepted")),
                };
                let reason = r.text()?;
                let units_remaining = r.i64()?;
                let delivered_units = r.i64()?;
                let complete = match r.u8()? {
                    0 => false,
                    1 => true,
                    _ => return Err(DecodeError::BadValue("complete")),
                };
                if cycle_id == 0 {
                    return Err(DecodeError::BadValue("module work result"));
                }
                Message::ModuleWorkResult {
                    session_id,
                    module_id,
                    target_id,
                    cycle_id,
                    accepted,
                    reason,
                    units_remaining,
                    delivered_units,
                    complete,
                }
            }
            kind::MODULE_ENDED => {
                let session_id = r.u64()?;
                let module_id = r.i64()?;
                let reason = r.u8()?;
                if !(1..=6).contains(&reason) {
                    return Err(DecodeError::BadValue("module ended reason"));
                }
                Message::ModuleEnded { session_id, module_id, reason }
            }
            kind::ECM_BURST => {
                let (session_id, ship_id) = (r.u64()?, r.i64()?);
                let count = r.u32()? as usize;
                if count > r.data.len() / 9 {
                    return Err(DecodeError::Truncated);
                }
                let mut reached = Vec::with_capacity(count);
                for _ in 0..count {
                    let ship = r.i64()?;
                    let jammed = match r.u8()? {
                        0 => false,
                        1 => true,
                        _ => return Err(DecodeError::BadValue("ecm burst jam flag")),
                    };
                    reached.push((ship, jammed));
                }
                Message::EcmBurst { session_id, ship_id, reached }
            }
            kind::REMOTE_REPAIR_CYCLE => {
                let session_id = r.u64()?;
                let module_id = r.i64()?;
                let target_id = r.i64()?;
                let cycle_id = r.u64()?;
                let cycle_number = r.i32()?;
                let cycle_at_ms = r.i64()?;
                let cycle_duration_ms = r.i64()?;
                let ends_effect_after_cycle = match r.u8()? {
                    0 => false,
                    1 => true,
                    _ => return Err(DecodeError::BadValue("ends effect after cycle")),
                };
                let base_amount = r.f64()?;
                let bonus_per_cycle = r.f64()?;
                let bonus_max = r.f64()?;
                if cycle_id == 0
                    || cycle_number <= 0
                    || cycle_duration_ms <= 0
                    || !base_amount.is_finite()
                    || base_amount < 0.0
                    || !bonus_per_cycle.is_finite()
                    || bonus_per_cycle < 0.0
                    || !bonus_max.is_finite()
                    || bonus_max < 0.0
                {
                    return Err(DecodeError::BadValue("remote repair cycle"));
                }
                Message::RemoteRepairCycle {
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
                }
            }
            kind::DESTROY => Message::Destroy { ship_id: r.i64()?, credit: read_credit(&mut r)? },
            kind::TARGET_REFUSED => Message::TargetRefused {
                session_id: r.u64()?,
                command: r.u8()?,
                reason: r.text()?,
                target_type: r.i32()?,
                desired_range: r.f64()?,
                actual_distance: r.f64()?,
            },
            other => return Err(DecodeError::UnknownKind(other)),
        };
        r.finish()?;
        Ok(message)
    }
}
