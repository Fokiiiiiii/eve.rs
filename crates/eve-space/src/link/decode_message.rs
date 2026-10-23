use super::*;

fn read_wreck_placement(r: &mut Reader) -> Result<WreckPlacement, DecodeError> {
    let scene = read_scene_key(r)?;
    let wreck_id = r.i64()?;
    let group_id = r.i32()?;
    let Some(kind) = read_wreck_kind(r)? else { return Err(DecodeError::BadValue("wreck")) };
    let position = read_vector(r)?;
    let direction = read_vector(r)?;
    let radius = r.f64()?;
    let launcher_id = r.i64()?;
    let credit = read_credit(r)?;
    let is_empty = match r.u8()? {
        0 => false,
        1 => true,
        _ => return Err(DecodeError::BadValue("is_empty")),
    };
    Ok(WreckPlacement {
        scene,
        wreck_id,
        group_id,
        kind,
        position,
        direction,
        radius,
        launcher_id,
        credit,
        is_empty,
    })
}

fn read_scene_key(reader: &mut Reader) -> Result<SceneKey, DecodeError> {
    let scene =
        SceneKey { location_id: reader.i32()?, instance_id: reader.i64()?, room_index: reader.i32()? };
    if !scene.is_valid() {
        return Err(DecodeError::BadValue("scene key"));
    }
    Ok(scene)
}

fn read_abyssal_count(
    reader: &mut Reader,
    maximum: usize,
    field: &'static str,
) -> Result<usize, DecodeError> {
    let count = reader.u32()? as usize;
    if count > maximum {
        return Err(DecodeError::BadValue(field));
    }
    Ok(count)
}

fn read_abyssal_modifiers(reader: &mut Reader) -> Result<Vec<AbyssalAttributeModifier>, DecodeError> {
    let count = read_abyssal_count(reader, MAX_ABYSSAL_MODIFIERS, "Abyssal modifiers")?;
    let mut modifiers = Vec::with_capacity(count);
    for _ in 0..count {
        let attribute_id = reader.i32()?;
        let operation = match reader.u8()? {
            0 => AbyssalModifierOperation::Add,
            1 => AbyssalModifierOperation::Multiply,
            _ => return Err(DecodeError::BadValue("Abyssal modifier operation")),
        };
        let value = reader.f64()?;
        if attribute_id <= 0 || !value.is_finite() {
            return Err(DecodeError::BadValue("Abyssal modifier"));
        }
        modifiers.push(AbyssalAttributeModifier { attribute_id, operation, value });
    }
    Ok(modifiers)
}

fn read_abyssal_scene_spec(reader: &mut Reader) -> Result<AbyssalSceneSpec, DecodeError> {
    let scene = read_scene_key(reader)?;
    let global_modifiers = read_abyssal_modifiers(reader)?;
    let field_count = read_abyssal_count(reader, MAX_ABYSSAL_FIELDS, "Abyssal environment fields")?;
    let mut fields = Vec::with_capacity(field_count);
    for _ in 0..field_count {
        let center = [reader.f64()?, reader.f64()?, reader.f64()?];
        let radius_meters = reader.f64()?;
        let modifiers = read_abyssal_modifiers(reader)?;
        if center.iter().any(|value| !value.is_finite()) || !radius_meters.is_finite() || radius_meters <= 0.0
        {
            return Err(DecodeError::BadValue("Abyssal environment field"));
        }
        fields.push(AbyssalEnvironmentField { center, radius_meters, modifiers });
    }
    let center = [reader.f64()?, reader.f64()?, reader.f64()?];
    let radius_meters = reader.f64()?;
    let tick_interval_ms = reader.i64()?;
    let point_count = read_abyssal_count(reader, MAX_ABYSSAL_BOUNDARY_POINTS, "Abyssal boundary points")?;
    let mut damage_curve = Vec::with_capacity(point_count);
    let mut previous_distance = -1.0;
    for _ in 0..point_count {
        let distance_outside_meters = reader.f64()?;
        let applied_damage_per_second = reader.f64()?;
        if !distance_outside_meters.is_finite()
            || distance_outside_meters < 0.0
            || distance_outside_meters <= previous_distance
            || !applied_damage_per_second.is_finite()
            || applied_damage_per_second < 0.0
        {
            return Err(DecodeError::BadValue("Abyssal boundary damage curve"));
        }
        previous_distance = distance_outside_meters;
        damage_curve.push(AbyssalBoundaryDamagePoint { distance_outside_meters, applied_damage_per_second });
    }
    let terminal_exposure_ms = reader.i64()?;
    let expiry_filetime = reader.i64()?;
    if scene.instance_id <= 0
        || center.iter().any(|value| !value.is_finite())
        || !radius_meters.is_finite()
        || radius_meters <= 0.0
        || tick_interval_ms <= 0
        || terminal_exposure_ms <= 0
        || damage_curve.is_empty()
        || expiry_filetime <= 0
    {
        return Err(DecodeError::BadValue("Abyssal scene specification"));
    }
    Ok(AbyssalSceneSpec {
        scene,
        environment: AbyssalEnvironmentProfile { global_modifiers, fields },
        boundary: AbyssalBoundaryProfile {
            center,
            radius_meters,
            tick_interval_ms,
            damage_curve,
            terminal_exposure_ms,
        },
        expiry_filetime,
    })
}

fn read_abyssal_scene_object(reader: &mut Reader, conduit: bool) -> Result<AbyssalSceneObject, DecodeError> {
    let scene_object = AbyssalSceneObject {
        scene: read_scene_key(reader)?,
        item_id: reader.i64()?,
        type_id: reader.i32()?,
        group_id: reader.i32()?,
        category_id: reader.i32()?,
        owner_id: reader.i64()?,
        radius: reader.f64()?,
        graphic_id: reader.i32()?,
        name: reader.text()?,
        position: [reader.f64()?, reader.f64()?, reader.f64()?],
        direction: [reader.f64()?, reader.f64()?, reader.f64()?],
        is_open: read_flag(reader, "Abyssal conduit open")?,
    };
    if conduit && !valid_abyssal_scene_object(&scene_object) {
        return Err(DecodeError::BadValue("Abyssal scene object"));
    }
    Ok(scene_object)
}

fn read_abyssal_result(reader: &mut Reader) -> Result<(u64, Result<(), String>), DecodeError> {
    let request_id = reader.u64()?;
    let status = reader.u8()?;
    let reason = reader.text()?;
    if request_id == 0 {
        return Err(DecodeError::BadValue("Abyssal request id"));
    }
    let outcome = match status {
        0 => Ok(()),
        1 => Err(reason),
        _ => return Err(DecodeError::BadValue("Abyssal request status")),
    };
    Ok((request_id, outcome))
}

/// A flag byte: 0 or 1, anything else is a bad value of `what`.
fn read_flag(reader: &mut Reader, what: &'static str) -> Result<bool, DecodeError> {
    match reader.u8()? {
        0 => Ok(false),
        1 => Ok(true),
        _ => Err(DecodeError::BadValue(what)),
    }
}

/// A structure's health: its shield, armor and hull points, the resonances of each layer (shield, armor, hull; four kinds each), the shield's recharge
/// in milliseconds, the limit each layer takes in a second and the repair pause of each layer.
fn read_structure_health(reader: &mut Reader) -> Result<(Vitals, f64, StructureLimits), DecodeError> {
    let mut point = || -> Result<f64, DecodeError> {
        let value = reader.f64()?;
        if !(value.is_finite() && value >= 0.0) {
            return Err(DecodeError::BadValue("structure health"));
        }
        Ok(value)
    };
    let shield_capacity = point()?;
    let armor_hp = point()?;
    let structure_hp = point()?;
    let mut damage = || -> Result<Damage, DecodeError> { Ok([point()?, point()?, point()?, point()?]) };
    let resonances = Resonances { shield: damage()?, armor: damage()?, structure: damage()? };
    let shield_recharge_ms = point()?;
    let per_second = [point()?, point()?, point()?];
    let repair_pause = [point()?, point()?, point()?];
    let vitals = Vitals { shield_capacity, armor_hp, structure_hp, resonances, armor_pinned: false };
    Ok((vitals, shield_recharge_ms, StructureLimits { per_second, repair_pause }))
}

/// A structure's slim fields: its state and upkeep, a flagged timer (start, end and a flagged pause), a flagged unanchoring time and repairing
/// (0 none, 1 not repairing, 2 repairing).
fn read_structure_status(reader: &mut Reader) -> Result<StructureStatus, DecodeError> {
    let state = reader.i32()?;
    let upkeep_state = reader.i32()?;
    let timer = if read_flag(reader, "structure timer")? {
        let start = reader.i64()?;
        let end = reader.i64()?;
        let paused = if read_flag(reader, "structure timer pause")? { Some(reader.i64()?) } else { None };
        Some((start, end, paused))
    } else {
        None
    };
    let unanchoring = if read_flag(reader, "structure unanchoring")? { Some(reader.i64()?) } else { None };
    let repairing = match reader.u8()? {
        0 => None,
        1 => Some(false),
        2 => Some(true),
        _ => return Err(DecodeError::BadValue("structure repairing")),
    };
    Ok(StructureStatus { state, upkeep_state, timer, unanchoring, repairing })
}

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
    let stance = match reader.u8()? {
        0 => None,
        1 => {
            Some(ShipStance { old_stance: reader.i32()?, switched_at: reader.i64()?, stance: reader.i32()? })
        }
        _ => return Err(DecodeError::BadValue("ship stance flag")),
    };
    Ok(ShipSpec {
        stance,
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

fn read_structure_damage_ids(reader: &mut Reader) -> Result<Vec<i64>, DecodeError> {
    let count = reader.u32()?;
    if count as usize > MAX_STRUCTURE_DAMAGE_CANDIDATES {
        return Err(DecodeError::BadValue("structure-damage candidate count"));
    }
    let mut ids = Vec::with_capacity(count as usize);
    let mut seen = std::collections::HashSet::with_capacity(count as usize);
    for _ in 0..count {
        let id = reader.i64()?;
        if id <= 0 || !seen.insert(id) {
            return Err(DecodeError::BadValue("structure-damage target IDs"));
        }
        ids.push(id);
    }
    Ok(ids)
}

impl Message {
    /// Reads a frame's payload.
    pub fn decode(payload: &[u8]) -> Result<Message, DecodeError> {
        let (&kind, body) = payload.split_first().ok_or(DecodeError::Empty)?;
        let mut r = Reader { data: body };
        let message = match kind {
            kind::HELLO => Message::Hello { version: r.u16()? },
            kind::HELLO_ACK => Message::HelloAck { version: r.u16()?, poll_hz: r.u32()? },
            kind::ENSURE_ABYSSAL_SCENE => {
                let request_id = r.u64()?;
                let spec = read_abyssal_scene_spec(&mut r)?;
                if request_id == 0 {
                    return Err(DecodeError::BadValue("Abyssal request id"));
                }
                Message::EnsureAbyssalScene { request_id, spec }
            }
            kind::ENSURE_ABYSSAL_SCENE_REPLY => {
                let (request_id, outcome) = read_abyssal_result(&mut r)?;
                Message::EnsureAbyssalSceneReply { request_id, outcome }
            }
            kind::ENSURE_ABYSSAL_SCENE_OBJECT => {
                let request_id = r.u64()?;
                if request_id == 0 {
                    return Err(DecodeError::BadValue("Abyssal scene object request id"));
                }
                let object = read_abyssal_scene_object(&mut r, true)?;
                Message::EnsureAbyssalSceneObject { request_id, object }
            }
            kind::ENSURE_ABYSSAL_SCENE_OBJECT_REPLY => {
                let (request_id, outcome) = read_abyssal_result(&mut r)?;
                Message::EnsureAbyssalSceneObjectReply { request_id, outcome }
            }
            kind::MOVE_ABYSSAL_PARTICIPANT => {
                let request_id = r.u64()?;
                let ship_id = r.i64()?;
                let source = read_scene_key(&mut r)?;
                let destination = read_scene_key(&mut r)?;
                let move_flags = r.u8()?;
                if move_flags > 3 {
                    return Err(DecodeError::BadValue("Abyssal move flags"));
                }
                let preserve_source_pose = move_flags & 1 != 0;
                let session_handoff = move_flags & 2 != 0;
                let (position, direction) = if preserve_source_pose {
                    ([0.0; 3], [0.0; 3])
                } else {
                    ([r.f64()?, r.f64()?, r.f64()?], [r.f64()?, r.f64()?, r.f64()?])
                };
                let room_transition = source.instance_id > 0
                    && source.instance_id == destination.instance_id
                    && source.location_id == destination.location_id
                    && source.room_index != destination.room_index;
                let entry = source.instance_id == 0 && destination.instance_id > 0;
                let origin_return = source.instance_id > 0 && destination.instance_id == 0;
                let explicit_pose_valid = position.iter().chain(&direction).all(|value| value.is_finite())
                    && direction.iter().any(|value| *value != 0.0);
                if request_id == 0
                    || ship_id <= 0
                    || !source.is_valid()
                    || !destination.is_valid()
                    || !(room_transition || entry || origin_return)
                    || (preserve_source_pose && !room_transition)
                    || (session_handoff && !(entry || origin_return))
                    || (!preserve_source_pose && !explicit_pose_valid)
                {
                    return Err(DecodeError::BadValue("Abyssal participant move"));
                }
                Message::MoveAbyssalParticipant {
                    request_id,
                    ship_id,
                    source,
                    destination,
                    preserve_source_pose,
                    session_handoff,
                    position,
                    direction,
                }
            }
            kind::MOVE_ABYSSAL_PARTICIPANT_REPLY => {
                let (request_id, outcome) = read_abyssal_result(&mut r)?;
                Message::MoveAbyssalParticipantReply { request_id, outcome }
            }
            kind::REBIND_ABYSSAL_SESSION => {
                let request_id = r.u64()?;
                let session_id = r.u64()?;
                let ship_id = r.i64()?;
                let scene = read_scene_key(&mut r)?;
                if request_id == 0 || session_id == 0 || ship_id <= 0 {
                    return Err(DecodeError::BadValue("Abyssal session rebind"));
                }
                Message::RebindAbyssalSession { request_id, session_id, ship_id, scene }
            }
            kind::REBIND_ABYSSAL_SESSION_REPLY => {
                let (request_id, outcome) = read_abyssal_result(&mut r)?;
                Message::RebindAbyssalSessionReply { request_id, outcome }
            }
            kind::REMOVE_ABYSSAL_SCENE => {
                let request_id = r.u64()?;
                let scene = read_scene_key(&mut r)?;
                let settled_npc_ids = read_structure_damage_ids(&mut r)?;
                let settled_loot_ids = read_structure_damage_ids(&mut r)?;
                if request_id == 0
                    || scene.instance_id <= 0
                    || settled_npc_ids.iter().any(|id| settled_loot_ids.contains(id))
                {
                    return Err(DecodeError::BadValue("Abyssal scene removal"));
                }
                Message::RemoveAbyssalScene { request_id, scene, settled_npc_ids, settled_loot_ids }
            }
            kind::REMOVE_ABYSSAL_SCENE_REPLY => {
                let (request_id, outcome) = read_abyssal_result(&mut r)?;
                Message::RemoveAbyssalSceneReply { request_id, outcome }
            }
            kind::ENSURE_ABYSSAL_TRACE_PUBLISHED => {
                let request_id = r.u64()?;
                let actor_session_id = match r.u8()? {
                    0 => None,
                    1 => {
                        let session = r.u64()?;
                        if session == 0 {
                            return Err(DecodeError::BadValue("Abyssal trace actor session"));
                        }
                        Some(session)
                    }
                    _ => return Err(DecodeError::BadValue("Abyssal trace actor flag")),
                };
                let solar_system_id = r.i32()?;
                let item_id = r.i64()?;
                let type_id = r.i32()?;
                let group_id = r.i32()?;
                let category_id = r.i32()?;
                let owner_id = r.i64()?;
                let radius = r.f64()?;
                let position = [r.f64()?, r.f64()?, r.f64()?];
                let velocity = [r.f64()?, r.f64()?, r.f64()?];
                let direction = [r.f64()?, r.f64()?, r.f64()?];
                let target_point = match r.u8()? {
                    0 => None,
                    1 => Some([r.f64()?, r.f64()?, r.f64()?]),
                    _ => return Err(DecodeError::BadValue("Abyssal trace target flag")),
                };
                let trace = AbyssalTrace {
                    solar_system_id,
                    item_id,
                    type_id,
                    group_id,
                    category_id,
                    owner_id,
                    radius,
                    position,
                    velocity,
                    direction,
                    target_point,
                    speed_fraction: r.f64()?,
                    game_mode_id: r.i32()?,
                    filament_type_id: r.i32()?,
                    difficulty_tier: r.i32()?,
                    expires_at_ms: r.i64()?,
                };
                if request_id == 0 || !super::valid_abyssal_trace(&trace) {
                    return Err(DecodeError::BadValue("Abyssal trace publication"));
                }
                Message::EnsureAbyssalTracePublished { request_id, actor_session_id, trace }
            }
            kind::ENSURE_ABYSSAL_TRACE_PUBLISHED_REPLY => {
                let request_id = r.u64()?;
                let status = match r.u8()? {
                    0 => AbyssalTraceStatus::Created,
                    1 => AbyssalTraceStatus::AlreadyPresent,
                    2 => AbyssalTraceStatus::Removed,
                    3 => AbyssalTraceStatus::AlreadyAbsent,
                    255 => AbyssalTraceStatus::Failed,
                    _ => return Err(DecodeError::BadValue("Abyssal trace publication status")),
                };
                let recipient_count = r.i32()?;
                let actor_notified = match r.u8()? {
                    0 => false,
                    1 => true,
                    _ => return Err(DecodeError::BadValue("Abyssal trace actor receipt")),
                };
                let error_text = r.text()?;
                let error = (!error_text.is_empty()).then_some(error_text);
                if request_id == 0
                    || recipient_count < 0
                    || (status == AbyssalTraceStatus::Failed) != error.is_some()
                {
                    return Err(DecodeError::BadValue("Abyssal trace publication receipt"));
                }
                Message::EnsureAbyssalTracePublishedReply {
                    request_id,
                    status,
                    recipient_count,
                    actor_notified,
                    error,
                }
            }
            kind::REMOVE_ABYSSAL_TRACE => {
                let request_id = r.u64()?;
                let solar_system_id = r.i32()?;
                let item_id = r.i64()?;
                if request_id == 0 || solar_system_id <= 0 || item_id <= 0 {
                    return Err(DecodeError::BadValue("Abyssal trace removal"));
                }
                Message::RemoveAbyssalTrace { request_id, solar_system_id, item_id }
            }
            kind::REMOVE_ABYSSAL_TRACE_REPLY => {
                let request_id = r.u64()?;
                let status = match r.u8()? {
                    0 => AbyssalTraceStatus::Created,
                    1 => AbyssalTraceStatus::AlreadyPresent,
                    2 => AbyssalTraceStatus::Removed,
                    3 => AbyssalTraceStatus::AlreadyAbsent,
                    255 => AbyssalTraceStatus::Failed,
                    _ => return Err(DecodeError::BadValue("Abyssal trace removal status")),
                };
                let recipient_count = r.i32()?;
                let error_text = r.text()?;
                let error = (!error_text.is_empty()).then_some(error_text);
                if request_id == 0
                    || recipient_count < 0
                    || (status == AbyssalTraceStatus::Failed) != error.is_some()
                {
                    return Err(DecodeError::BadValue("Abyssal trace removal receipt"));
                }
                Message::RemoveAbyssalTraceReply { request_id, status, recipient_count, error }
            }
            kind::REMOVE_ABYSSAL_PARTICIPANT => {
                let request_id = r.u64()?;
                let scene = read_scene_key(&mut r)?;
                let ship_id = r.i64()?;
                if request_id == 0 || !scene.is_valid() || scene.instance_id <= 0 || ship_id <= 0 {
                    return Err(DecodeError::BadValue("Abyssal participant removal"));
                }
                Message::RemoveAbyssalParticipant { request_id, scene, ship_id }
            }
            kind::REMOVE_ABYSSAL_PARTICIPANT_REPLY => {
                let request_id = r.u64()?;
                let already_absent = match r.u8()? {
                    0 => false,
                    1 => true,
                    _ => return Err(DecodeError::BadValue("Abyssal participant removal state")),
                };
                let error_text = r.text()?;
                if request_id == 0 || (already_absent && !error_text.is_empty()) {
                    return Err(DecodeError::BadValue("Abyssal participant removal result"));
                }
                Message::RemoveAbyssalParticipantReply {
                    request_id,
                    already_absent,
                    error: (!error_text.is_empty()).then_some(error_text),
                }
            }
            kind::GET_ABYSSAL_PARTICIPANT_POSE => {
                let request_id = r.u64()?;
                let scene = read_scene_key(&mut r)?;
                let ship_id = r.i64()?;
                if request_id == 0 || !scene.is_valid() || ship_id <= 0 {
                    return Err(DecodeError::BadValue("resident pose request"));
                }
                Message::GetAbyssalParticipantPose { request_id, scene, ship_id }
            }
            kind::GET_ABYSSAL_PARTICIPANT_POSE_REPLY => {
                let request_id = r.u64()?;
                let scene = read_scene_key(&mut r)?;
                let ship_id = r.i64()?;
                let result = r.u8()?;
                let outcome = match result {
                    0 => {
                        let position = [r.f64()?, r.f64()?, r.f64()?];
                        let direction = [r.f64()?, r.f64()?, r.f64()?];
                        let error = r.text()?;
                        if !error.is_empty()
                            || position.iter().chain(&direction).any(|value| !value.is_finite())
                            || direction.iter().all(|value| *value == 0.0)
                        {
                            return Err(DecodeError::BadValue("Abyssal pose"));
                        }
                        Ok((position, direction))
                    }
                    1 => {
                        let error = r.text()?;
                        if error.is_empty() {
                            return Err(DecodeError::BadValue("Abyssal pose failure"));
                        }
                        Err(error)
                    }
                    _ => return Err(DecodeError::BadValue("Abyssal pose result flag")),
                };
                if request_id == 0 || !scene.is_valid() || ship_id <= 0 {
                    return Err(DecodeError::BadValue("resident pose reply identity"));
                }
                let deployment = if read_flag(&mut r, "deployment snapshot")? {
                    if outcome.is_err() || scene.instance_id != 0 {
                        return Err(DecodeError::BadValue("deployment snapshot scene"));
                    }
                    let warping = read_flag(&mut r, "deployment warping")?;
                    let cloaked = read_flag(&mut r, "deployment cloaked")?;
                    let cyno_active = read_flag(&mut r, "deployment cyno")?;
                    let undock_invulnerable = read_flag(&mut r, "deployment invulnerability")?;
                    let warp_scrambled = read_flag(&mut r, "deployment scramble")?;
                    let count = r.u32()? as usize;
                    let mut nearby_objects = Vec::new();
                    let mut seen = std::collections::HashSet::new();
                    for _ in 0..count {
                        let item_id = r.i64()?;
                        let type_id = r.i32()?;
                        let center_distance_meters = r.f64()?;
                        if item_id <= 0
                            || item_id == ship_id
                            || type_id <= 0
                            || !seen.insert(item_id)
                            || !center_distance_meters.is_finite()
                            || !(0.0..=1_000_000.0).contains(&center_distance_meters)
                        {
                            return Err(DecodeError::BadValue("deployment object"));
                        }
                        nearby_objects.push(AbyssalDeploymentObject {
                            item_id,
                            type_id,
                            center_distance_meters,
                        });
                    }
                    Some(AbyssalDeploymentSnapshot {
                        warping,
                        cloaked,
                        cyno_active,
                        undock_invulnerable,
                        warp_scrambled,
                        nearby_objects,
                    })
                } else {
                    None
                };
                Message::GetAbyssalParticipantPoseReply { request_id, scene, ship_id, outcome, deployment }
            }
            kind::ENTER => {
                let session_id = r.u64()?;
                let scene = read_scene_key(&mut r)?;
                let how = match r.u8()? {
                    0 => How::Undock,
                    1 => How::InSpace,
                    2 => How::Anchor,
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
                    scene,
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
            kind::CONTROL_STRUCTURE => {
                let session_id = r.u64()?;
                let solar_system_id = r.i32()?;
                let structure_id = r.i64()?;
                let character_id = r.i64()?;
                let active = read_flag(&mut r, "control structure active")?;
                let control_lease_generation = r.i64()?;
                if session_id == 0
                    || solar_system_id <= 0
                    || structure_id < 0
                    || character_id < 0
                    || control_lease_generation < 0
                    || (active && (structure_id == 0 || character_id == 0 || control_lease_generation == 0))
                {
                    return Err(DecodeError::BadValue("control structure IDs or generation"));
                }
                Message::ControlStructure {
                    session_id,
                    solar_system_id,
                    structure_id,
                    character_id,
                    active,
                    control_lease_generation,
                }
            }
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
            kind::DIRECTIONAL_TARGETS => {
                let (session_id, command) = (r.u64()?, r.u8()?);
                let count = r.u32()? as usize;
                // each target is 47 bytes: an id, a kind, a type, a position, a radius and two flags
                if count > r.data.len() / 47 {
                    return Err(DecodeError::Truncated);
                }
                let targets = (0..count)
                    .map(|_| {
                        Ok(DirectionalTarget {
                            id: r.i64()?,
                            kind: r.u8()?,
                            type_id: r.i32()?,
                            position: read_vector(&mut r)?,
                            radius: r.f64()?,
                            cloaked: r.u8()? != 0,
                            warping: r.u8()? != 0,
                        })
                    })
                    .collect::<Result<Vec<_>, DecodeError>>()?;
                Message::DirectionalTargets { session_id, command, targets }
            }
            kind::SCAN_TARGETS => {
                let (session_id, command) = (r.u64()?, r.u8()?);
                let count = r.u32()? as usize;
                // each target is 37 bytes: an id, a kind, a type and a position
                if count > r.data.len() / 37 {
                    return Err(DecodeError::Truncated);
                }
                let targets = (0..count)
                    .map(|_| {
                        Ok(ScanTarget {
                            id: r.i64()?,
                            kind: r.u8()?,
                            type_id: r.i32()?,
                            position: read_vector(&mut r)?,
                        })
                    })
                    .collect::<Result<Vec<_>, DecodeError>>()?;
                Message::ScanTargets { session_id, command, targets }
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
                ship_type_id: r.i32()?,
                corporation_id: r.i32()?,
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
                    expected_scene: match r.u8()? {
                        0 => None,
                        1 => Some(read_scene_key(&mut r)?),
                        _ => return Err(DecodeError::BadValue("expected scene flag")),
                    },
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
            kind::REMOVE_CYNO_BEACON => {
                let solar_system_id = r.i32()?;
                let structure_beacon_id = r.i64()?;
                if solar_system_id <= 0 || structure_beacon_id <= 0 {
                    return Err(DecodeError::BadValue("cyno beacon removal"));
                }
                Message::RemoveCynoBeacon { solar_system_id, structure_beacon_id }
            }
            kind::REMOTE_DECLOAK_PING => {
                let solar_system_id = r.i32()?;
                let observatory_id = r.i64()?;
                if solar_system_id <= 0 || observatory_id <= 0 {
                    return Err(DecodeError::BadValue("decloak ping"));
                }
                Message::RemoteDecloakPing { solar_system_id, observatory_id }
            }
            kind::DAMAGEABLE_ENTITY_UPSERTED => {
                let solar_system_id = r.i32()?;
                let entity_id = r.i64()?;
                let type_id = r.i32()?;
                let owner_id = r.i64()?;
                let corporation_id = r.i64()?;
                let alliance_id = r.i64()?;
                let war_faction_id = r.i64()?;
                if solar_system_id <= 0
                    || entity_id <= 0
                    || type_id <= 0
                    || owner_id < 0
                    || corporation_id < 0
                    || alliance_id < 0
                    || war_faction_id < 0
                {
                    return Err(DecodeError::BadValue("damageable entity identity"));
                }
                Message::DamageableEntityUpserted {
                    solar_system_id,
                    entity_id,
                    type_id,
                    owner_id,
                    corporation_id,
                    alliance_id,
                    war_faction_id,
                }
            }
            kind::DAMAGEABLE_ENTITY_REMOVED => {
                let solar_system_id = r.i32()?;
                let entity_id = r.i64()?;
                if solar_system_id <= 0 || entity_id <= 0 {
                    return Err(DecodeError::BadValue("damageable entity removal"));
                }
                Message::DamageableEntityRemoved { solar_system_id, entity_id }
            }
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
            kind::PLACE_STRUCTURE => {
                let structure_id = r.i64()?;
                let solar_system_id = r.i32()?;
                let name = r.text()?;
                let type_id = r.i32()?;
                let group_id = r.i32()?;
                let position = read_vector(&mut r)?;
                let radius = r.f64()?;
                let owner_id = r.i64()?;
                let corporation_id = r.i32()?;
                let alliance_id = r.i32()?;
                let (vitals, shield_recharge_ms, limits) = read_structure_health(&mut r)?;
                let status = read_structure_status(&mut r)?;
                let planet_id = r.i64()?;
                if !(radius.is_finite() && radius >= 0.0) {
                    return Err(DecodeError::BadValue("structure"));
                }
                Message::PlaceStructure(Box::new(StructurePlacement {
                    structure_id,
                    solar_system_id,
                    name,
                    type_id,
                    group_id,
                    position,
                    radius,
                    owner_id,
                    corporation_id,
                    alliance_id,
                    vitals,
                    shield_recharge_ms,
                    limits,
                    status,
                    planet_id,
                }))
            }
            kind::STRUCTURE_DAMAGED => {
                let structure_id = r.i64()?;
                let depleted = match r.u8()? {
                    0 => None,
                    1 => Some(Layer::Shield),
                    2 => Some(Layer::Armor),
                    3 => Some(Layer::Structure),
                    _ => return Err(DecodeError::BadValue("structure layer")),
                };
                let over_threshold = read_flag(&mut r, "structure damage")?;
                Message::StructureDamaged { structure_id, depleted, over_threshold }
            }
            kind::PLACE_WORMHOLE => {
                let wormhole_id = r.i64()?;
                let solar_system_id = r.i32()?;
                let name = r.text()?;
                let type_id = r.i32()?;
                let group_id = r.i32()?;
                let position = read_vector(&mut r)?;
                let radius = r.f64()?;
                let destination_system_id = r.i32()?;
                let destination_end = r.i64()?;
                let destination_class = r.i32()?;
                let destination_position = read_vector(&mut r)?;
                let destination_radius = r.f64()?;
                let max_jump_mass = r.f64()?;
                if ![radius, destination_radius, max_jump_mass].iter().all(|v| v.is_finite() && *v >= 0.0) {
                    return Err(DecodeError::BadValue("wormhole"));
                }
                Message::PlaceWormhole(Box::new(WormholePlacement {
                    wormhole_id,
                    solar_system_id,
                    name,
                    type_id,
                    group_id,
                    position,
                    radius,
                    destination_system_id,
                    destination_end,
                    destination_class,
                    destination_position,
                    destination_radius,
                    max_jump_mass,
                }))
            }
            kind::REMOVE_WORMHOLE => Message::RemoveWormhole { wormhole_id: r.i64()? },
            kind::REMOVE_STRUCTURE => Message::RemoveStructure { structure_id: r.i64()? },
            kind::SET_STRUCTURE_STATE => {
                let solar_system_id = r.i32()?;
                let structure_id = r.i64()?;
                let status = read_structure_status(&mut r)?;
                Message::SetStructureState { solar_system_id, structure_id, status }
            }
            kind::REMOVE_PROBE => Message::RemoveProbe { probe_id: r.i64()? },
            // a disruptor and an inhibitor have the same placement (the kind says which of the two it is)
            kind::PLACE_DISRUPTOR
            | kind::PLACE_INHIBITOR
            | kind::PLACE_CYNO_BEACON
            | kind::PLACE_OBSERVATORY
            | kind::PLACE_CONTROL_TOWER
            | kind::PLACE_DEADSPACE_POCKET
            | kind::PLACE_ACCELERATION_GATE => {
                let deployable_id = r.i64()?;
                let solar_system_id = r.i32()?;
                let name = r.text()?;
                let type_id = r.i32()?;
                let group_id = r.i32()?;
                let position = read_vector(&mut r)?;
                let radius = r.f64()?;
                let range = r.f64()?;
                let active = read_flag(&mut r, "disruptor")?;
                let owner_id = r.i64()?;
                let corporation_id = r.i32()?;
                let alliance_id = r.i32()?;
                let ship_id = r.i64()?;
                if !(radius.is_finite() && radius >= 0.0 && range.is_finite() && range >= 0.0) {
                    return Err(DecodeError::BadValue("disruptor"));
                }
                let placement = Box::new(DisruptorPlacement {
                    deployable_id,
                    solar_system_id,
                    name,
                    type_id,
                    group_id,
                    position,
                    radius,
                    range,
                    active,
                    owner_id,
                    corporation_id,
                    alliance_id,
                    ship_id,
                });
                if kind == kind::PLACE_INHIBITOR {
                    Message::PlaceInhibitor(placement)
                } else if kind == kind::PLACE_CYNO_BEACON {
                    Message::PlaceCynoBeacon(placement)
                } else if kind == kind::PLACE_OBSERVATORY {
                    Message::PlaceObservatory(placement)
                } else if kind == kind::PLACE_CONTROL_TOWER {
                    Message::PlaceControlTower(placement)
                } else if kind == kind::PLACE_DEADSPACE_POCKET {
                    Message::PlaceDeadspacePocket(placement)
                } else if kind == kind::PLACE_ACCELERATION_GATE {
                    Message::PlaceAccelerationGate(placement)
                } else {
                    Message::PlaceDisruptor(placement)
                }
            }
            kind::SET_DISRUPTOR_ACTIVE => {
                let deployable_id = r.i64()?;
                let active = read_flag(&mut r, "disruptor")?;
                Message::SetDisruptorActive { deployable_id, active }
            }
            kind::PLACE_ANALYSIS_BEACON => {
                let deployable_id = r.i64()?;
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
                let state = read_analysis_beacon_state(&mut r)?;
                if !(radius.is_finite() && radius >= 0.0) {
                    return Err(DecodeError::BadValue("analysis beacon"));
                }
                Message::PlaceAnalysisBeacon(Box::new(AnalysisBeaconPlacement {
                    deployable_id,
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
                    state,
                }))
            }
            kind::SET_ANALYSIS_BEACON_STATE => {
                let deployable_id = r.i64()?;
                let state = read_analysis_beacon_state(&mut r)?;
                Message::SetAnalysisBeaconState { deployable_id, state }
            }
            kind::SET_SLIM_FIELD => {
                let item_id = r.i64()?;
                let name = r.text()?;
                if name.is_empty() || name.len() > SLIM_FIELD_NAME_MAX {
                    return Err(DecodeError::BadValue("slim field name"));
                }
                let value = read_slim_value(&mut r, 0)?;
                Message::SetSlimField { item_id, name, value }
            }
            kind::PLACE_CAPTURE_POINT => {
                let deployable_id = r.i64()?;
                let solar_system_id = r.i32()?;
                let name = r.text()?;
                let type_id = r.i32()?;
                let group_id = r.i32()?;
                let position = read_vector(&mut r)?;
                let radius = r.f64()?;
                let attacker_faction = r.i32()?;
                let occupier_faction = r.i32()?;
                let next_point_time = r.i64()?;
                if !(radius.is_finite() && radius >= 0.0)
                    || attacker_faction < 0
                    || occupier_faction < 0
                    || next_point_time < 0
                {
                    return Err(DecodeError::BadValue("capture point"));
                }
                Message::PlaceCapturePoint(Box::new(CapturePointPlacement {
                    deployable_id,
                    solar_system_id,
                    name,
                    type_id,
                    group_id,
                    position,
                    radius,
                    attacker_faction,
                    occupier_faction,
                    next_point_time,
                }))
            }
            kind::PLACE_BATTLEFIELD_SCOREBOARD => {
                let deployable_id = r.i64()?;
                let solar_system_id = r.i32()?;
                let name = r.text()?;
                let type_id = r.i32()?;
                let group_id = r.i32()?;
                let position = read_vector(&mut r)?;
                let radius = r.f64()?;
                if !(radius.is_finite() && radius >= 0.0) {
                    return Err(DecodeError::BadValue("scoreboard"));
                }
                Message::PlaceBattlefieldScoreboard(Box::new(ScoreboardPlacement {
                    deployable_id,
                    solar_system_id,
                    name,
                    type_id,
                    group_id,
                    position,
                    radius,
                }))
            }
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
            kind::PLACE_WRECK => Message::PlaceWreck(Box::new(read_wreck_placement(&mut r)?)),
            kind::ENSURE_ABYSSAL_ENVIRONMENT_OBJECT => {
                let request_id = r.u64()?;
                let content_id = r.text()?;
                let body = read_abyssal_scene_object(&mut r, false)?;
                let effect_radius_meters = r.f64()?;
                let point_defense = if read_flag(&mut r, "Abyssal point defense")? {
                    let tick_interval_ms = r.i64()?;
                    let damage = [r.f64()?, r.f64()?, r.f64()?, r.f64()?];
                    let type_list_id = r.i32()?;
                    let mut targets: [Vec<i32>; 6] = Default::default();
                    for ids in &mut targets {
                        let count = r.u32()? as usize;
                        if count > 4096 {
                            return Err(DecodeError::BadValue("Abyssal target count"));
                        }
                        for _ in 0..count {
                            ids.push(r.i32()?);
                        }
                    }
                    Some(AbyssalPointDefense {
                        tick_interval_ms,
                        damage,
                        type_list_id,
                        targets,
                        effect_name: r.text()?,
                    })
                } else {
                    None
                };
                let object =
                    AbyssalEnvironmentObject { content_id, body, effect_radius_meters, point_defense };
                if request_id == 0 || !valid_abyssal_environment_object(&object) {
                    return Err(DecodeError::BadValue("Abyssal environment object"));
                }
                Message::EnsureAbyssalEnvironmentObject { request_id, object: Box::new(object) }
            }
            kind::ENSURE_ABYSSAL_ENVIRONMENT_OBJECT_REPLY => {
                let (request_id, outcome) = read_abyssal_result(&mut r)?;
                Message::EnsureAbyssalEnvironmentObjectReply { request_id, outcome }
            }
            kind::ABYSSAL_FIELD_MEMBERSHIP => {
                let scene = read_scene_key(&mut r)?;
                let source_item_id = r.i64()?;
                let ship_id = r.i64()?;
                let session_id = r.u64()?;
                let inside = read_flag(&mut r, "Abyssal membership")?;
                if scene.instance_id <= 0 || source_item_id <= 0 || ship_id <= 0 || session_id == 0 {
                    return Err(DecodeError::BadValue("Abyssal field membership"));
                }
                Message::AbyssalFieldMembership { scene, source_item_id, ship_id, session_id, inside }
            }
            kind::REFRESH_ABYSSAL_NPC => {
                let request_id = r.u64()?;
                let scene = read_scene_key(&mut r)?;
                let session_id = r.u64()?;
                let ship = Box::new(read_ship(&mut r)?);
                let identity = Box::new(read_identity(&mut r)?);
                if request_id == 0
                    || scene.instance_id <= 0
                    || !crate::npc::is_npc_session(session_id)
                    || ship.character_id != 0
                    || identity.wreck.is_some()
                    || identity.host_wreck
                {
                    return Err(DecodeError::BadValue("Abyssal NPC refresh"));
                }
                Message::RefreshAbyssalNpc { request_id, scene, session_id, ship, identity }
            }
            kind::REFRESH_ABYSSAL_NPC_REPLY => {
                let (request_id, outcome) = read_abyssal_result(&mut r)?;
                Message::RefreshAbyssalNpcReply { request_id, outcome }
            }
            kind::ENSURE_ABYSSAL_REWARD_CACHE => {
                let request_id = r.u64()?;
                let cache = AbyssalRewardCachePlacement {
                    content_id: r.text()?,
                    scene: read_scene_key(&mut r)?,
                    ship: read_ship(&mut r)?,
                    position: read_vector(&mut r)?,
                    direction: read_vector(&mut r)?,
                };
                if request_id == 0 || !valid_abyssal_reward_cache(&cache) {
                    return Err(DecodeError::BadValue("Abyssal cache"));
                }
                Message::EnsureAbyssalRewardCache { request_id, cache: Box::new(cache) }
            }
            kind::ENSURE_ABYSSAL_REWARD_CACHE_REPLY => {
                let (request_id, outcome) = read_abyssal_result(&mut r)?;
                Message::EnsureAbyssalRewardCacheReply { request_id, outcome }
            }
            kind::ENSURE_ABYSSAL_LOOT_CONTAINER => {
                let request_id = r.u64()?;
                let content_id = r.text()?;
                let placement = read_wreck_placement(&mut r)?;
                if request_id == 0 || !valid_abyssal_loot(&content_id, &placement) {
                    return Err(DecodeError::BadValue("Abyssal loot"));
                }
                Message::EnsureAbyssalLootContainer { request_id, content_id, placement: Box::new(placement) }
            }
            kind::ENSURE_ABYSSAL_LOOT_CONTAINER_REPLY => {
                let (request_id, outcome) = read_abyssal_result(&mut r)?;
                Message::EnsureAbyssalLootContainerReply { request_id, outcome }
            }
            kind::SPAWN => {
                let request_id = r.u64()?;
                let abyssal_content_id = match r.u8()? {
                    0 => None,
                    1 => Some(r.text()?),
                    _ => return Err(DecodeError::BadValue("Abyssal content id flag")),
                };
                let scene = read_scene_key(&mut r)?;
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
                    abyssal_content_id,
                    scene,
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
            kind::STRUCTURE_DAMAGE_CANDIDATES_REQUEST | kind::STRUCTURE_DAMAGE_CANDIDATES_RESULT => {
                let session_id = r.u64()?;
                let structure_id = r.i64()?;
                let module_id = r.i64()?;
                let effect_id = r.i32()?;
                let activation_nonce = r.u64()?;
                let control_lease_generation = r.i64()?;
                let cycle_id = r.u64()?;
                let pulse_hop_ordinal = r.u32()?;
                if session_id == 0
                    || structure_id <= 0
                    || module_id <= 0
                    || effect_id <= 0
                    || activation_nonce == 0
                    || control_lease_generation <= 0
                    || cycle_id == 0
                {
                    return Err(DecodeError::BadValue("structure-damage correlation"));
                }
                let ids = read_structure_damage_ids(&mut r)?;
                if kind == kind::STRUCTURE_DAMAGE_CANDIDATES_REQUEST {
                    Message::StructureDamageCandidatesRequest {
                        session_id,
                        structure_id,
                        module_id,
                        effect_id,
                        activation_nonce,
                        control_lease_generation,
                        cycle_id,
                        pulse_hop_ordinal,
                        candidate_ids: ids,
                    }
                } else if r.data.is_empty() {
                    Message::StructureDamageCandidatesResult {
                        session_id,
                        structure_id,
                        module_id,
                        effect_id,
                        activation_nonce,
                        control_lease_generation,
                        cycle_id,
                        pulse_hop_ordinal,
                        allowed_target_ids: ids,
                    }
                } else {
                    if r.u8()? != STRUCTURE_FIELD_PARTITION_EXTENSION_VERSION {
                        return Err(DecodeError::BadValue("structure-field partition extension"));
                    }
                    let field_moved_target_ids = read_structure_damage_ids(&mut r)?;
                    let field_held_target_ids = read_structure_damage_ids(&mut r)?;
                    if !structure_field_partition_is_valid(
                        &ids,
                        &field_moved_target_ids,
                        &field_held_target_ids,
                    ) {
                        return Err(DecodeError::BadValue("structure-field target partition"));
                    }
                    Message::StructureDamageCandidatesFieldResult {
                        session_id,
                        structure_id,
                        module_id,
                        effect_id,
                        activation_nonce,
                        control_lease_generation,
                        cycle_id,
                        pulse_hop_ordinal,
                        allowed_target_ids: ids,
                        field_moved_target_ids,
                        field_held_target_ids,
                    }
                }
            }
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
                let charge_type_id = r.i32()?;
                let numbers = [mining_amount_m3, waste_multiplier, crit_multiplier, access_difficulty_bonus];
                // probabilities are at most one, the rolls below one, and every number finite and not negative; a mining cycle mines something
                if cycle_id == 0
                    || !matches!(command, 7..=14)
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
                    || (matches!(command, 9..=14)
                        && (mining_amount_m3 != 0.0
                            || waste_multiplier != 0.0
                            || waste_probability != 0.0
                            || crit_probability != 0.0
                            || crit_multiplier != 0.0
                            || access_difficulty_bonus != 0.0))
                    || (!matches!(command, 12 | 13) && !recipients.is_empty())
                    || (command == 12 && charge_type_id <= 0)
                    || (command != 12 && charge_type_id != 0)
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
                    charge_type_id,
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
            kind::TETHER_POLICY => {
                let session_id = r.u64()?;
                let blocked_for_ms = r.u64()?;
                let lease_ms = r.u64()?;
                let count = r.u32()? as usize;
                if count > r.data.len() / 16 {
                    return Err(DecodeError::Truncated);
                }
                let mut candidates = Vec::with_capacity(count);
                for _ in 0..count {
                    let structure_id = r.u64()? as i64;
                    let range = r.f64()?;
                    if structure_id <= 0 {
                        return Err(DecodeError::BadValue("tether structure"));
                    }
                    if !(range.is_finite() && range > 0.0) {
                        return Err(DecodeError::BadValue("tether range"));
                    }
                    candidates.push(TetherCandidate { structure_id, range });
                }
                Message::TetherPolicy { session_id, blocked_for_ms, lease_ms, candidates }
            }
            kind::TETHER_CHANGED => {
                let session_id = r.u64()?;
                let previous_structure_id = r.u64()? as i64;
                let structure_id = r.u64()? as i64;
                let repairing = read_flag(&mut r, "tether repairing")?;
                if previous_structure_id < 0 || structure_id < 0 || (structure_id == 0 && repairing) {
                    return Err(DecodeError::BadValue("tether state"));
                }
                Message::TetherChanged { session_id, previous_structure_id, structure_id, repairing }
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

/// A CRAB's state (see `put_analysis_beacon_state`): the link state must be one the client knows (1 to 4).
/// A dynamic slim-item field value (see `put_slim_value`): finite reals, texts and tuples within their limits.
fn read_slim_value(reader: &mut Reader, depth: usize) -> Result<SlimValue, DecodeError> {
    Ok(match reader.u8()? {
        0 => SlimValue::None,
        1 => SlimValue::Int(reader.i64()?),
        2 => {
            let number = reader.f64()?;
            if !number.is_finite() {
                return Err(DecodeError::BadValue("slim field real"));
            }
            SlimValue::Real(number)
        }
        3 => SlimValue::Bool(read_flag(reader, "slim field bool")?),
        4 => {
            let text = reader.text()?;
            if text.len() > SLIM_FIELD_TEXT_MAX {
                return Err(DecodeError::BadValue("slim field text"));
            }
            SlimValue::Text(text)
        }
        5 => {
            let count = reader.u16()? as usize;
            if depth >= SLIM_FIELD_DEPTH_MAX || count > SLIM_FIELD_TUPLE_MAX {
                return Err(DecodeError::BadValue("slim field tuple"));
            }
            let mut items = Vec::with_capacity(count);
            for _ in 0..count {
                items.push(read_slim_value(reader, depth + 1)?);
            }
            SlimValue::Tuple(items)
        }
        _ => return Err(DecodeError::BadValue("slim field value")),
    })
}

fn read_analysis_beacon_state(reader: &mut Reader) -> Result<AnalysisBeaconState, DecodeError> {
    let active = read_flag(reader, "analysis beacon")?;
    let activate_at = reader.i64()?;
    let deployed_at = reader.i64()?;
    let activation_seconds = reader.i32()?;
    let link_state = reader.i32()?;
    let link_complete_at = reader.i64()?;
    let linked_ship_id = reader.i64()?;
    if activation_seconds < 0 || !(1..=4).contains(&link_state) {
        return Err(DecodeError::BadValue("analysis beacon"));
    }
    Ok(AnalysisBeaconState {
        active,
        activate_at,
        deployed_at,
        activation_seconds,
        link_state,
        link_complete_at,
        linked_ship_id,
    })
}
