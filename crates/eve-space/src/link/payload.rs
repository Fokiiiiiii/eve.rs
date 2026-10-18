use super::*;

fn put_wreck_placement(out: &mut Vec<u8>, placement: &WreckPlacement) {
    put_scene_key(out, placement.scene);
    out.extend_from_slice(&placement.wreck_id.to_le_bytes());
    out.extend_from_slice(&placement.group_id.to_le_bytes());
    put_wreck_kind(out, Some(&placement.kind));
    put_vector(out, &placement.position);
    put_vector(out, &placement.direction);
    out.extend_from_slice(&placement.radius.to_le_bytes());
    out.extend_from_slice(&placement.launcher_id.to_le_bytes());
    put_credit(out, &placement.credit);
    out.push(u8::from(placement.is_empty));
}

#[allow(clippy::too_many_arguments)]
fn write_structure_damage_correlation(
    out: &mut Vec<u8>,
    session_id: u64,
    structure_id: i64,
    module_id: i64,
    effect_id: i32,
    activation_nonce: u64,
    control_lease_generation: i64,
    cycle_id: u64,
    pulse_hop_ordinal: u32,
) {
    out.extend_from_slice(&session_id.to_le_bytes());
    out.extend_from_slice(&structure_id.to_le_bytes());
    out.extend_from_slice(&module_id.to_le_bytes());
    out.extend_from_slice(&effect_id.to_le_bytes());
    out.extend_from_slice(&activation_nonce.to_le_bytes());
    out.extend_from_slice(&control_lease_generation.to_le_bytes());
    out.extend_from_slice(&cycle_id.to_le_bytes());
    out.extend_from_slice(&pulse_hop_ordinal.to_le_bytes());
}

fn write_structure_damage_ids(out: &mut Vec<u8>, ids: &[i64]) {
    assert!(ids.len() <= MAX_STRUCTURE_DAMAGE_CANDIDATES, "structure-damage ID list exceeds wire limit");
    let mut seen = std::collections::HashSet::with_capacity(ids.len());
    assert!(
        ids.iter().all(|id| *id > 0 && seen.insert(*id)),
        "structure-damage IDs must be positive and unique"
    );
    out.extend_from_slice(&(ids.len() as u32).to_le_bytes());
    for id in ids {
        out.extend_from_slice(&id.to_le_bytes());
    }
}

fn put_scene_key(out: &mut Vec<u8>, scene: SceneKey) {
    assert!(scene.is_valid(), "invalid simulation scene key");
    out.extend_from_slice(&scene.location_id.to_le_bytes());
    out.extend_from_slice(&scene.instance_id.to_le_bytes());
    out.extend_from_slice(&scene.room_index.to_le_bytes());
}

fn valid_abyssal_modifiers(modifiers: &[AbyssalAttributeModifier]) -> bool {
    modifiers.len() <= MAX_ABYSSAL_MODIFIERS
        && modifiers.iter().all(|modifier| modifier.attribute_id > 0 && modifier.value.is_finite())
}

fn put_abyssal_modifiers(out: &mut Vec<u8>, modifiers: &[AbyssalAttributeModifier]) {
    assert!(valid_abyssal_modifiers(modifiers), "invalid Abyssal attribute modifiers");
    out.extend_from_slice(&(modifiers.len() as u32).to_le_bytes());
    for modifier in modifiers {
        out.extend_from_slice(&modifier.attribute_id.to_le_bytes());
        out.push(match modifier.operation {
            AbyssalModifierOperation::Add => 0,
            AbyssalModifierOperation::Multiply => 1,
        });
        out.extend_from_slice(&modifier.value.to_le_bytes());
    }
}

fn valid_abyssal_scene_spec(spec: &AbyssalSceneSpec) -> bool {
    let valid_vector = |vector: &[f64; 3]| vector.iter().all(|value| value.is_finite());
    if !spec.scene.is_valid()
        || spec.scene.instance_id <= 0
        || spec.expiry_filetime <= 0
        || spec.environment.fields.len() > MAX_ABYSSAL_FIELDS
        || !valid_abyssal_modifiers(&spec.environment.global_modifiers)
        || !valid_vector(&spec.boundary.center)
        || !spec.boundary.radius_meters.is_finite()
        || spec.boundary.radius_meters <= 0.0
        || spec.boundary.tick_interval_ms <= 0
        || spec.boundary.terminal_exposure_ms <= 0
        || spec.boundary.damage_curve.is_empty()
        || spec.boundary.damage_curve.len() > MAX_ABYSSAL_BOUNDARY_POINTS
    {
        return false;
    }
    for field in &spec.environment.fields {
        if !valid_vector(&field.center)
            || !field.radius_meters.is_finite()
            || field.radius_meters <= 0.0
            || !valid_abyssal_modifiers(&field.modifiers)
        {
            return false;
        }
    }
    let mut previous_distance = -1.0;
    for point in &spec.boundary.damage_curve {
        if !point.distance_outside_meters.is_finite()
            || point.distance_outside_meters < 0.0
            || point.distance_outside_meters <= previous_distance
            || !point.applied_damage_per_second.is_finite()
            || point.applied_damage_per_second < 0.0
        {
            return false;
        }
        previous_distance = point.distance_outside_meters;
    }
    true
}

fn put_abyssal_scene_spec(out: &mut Vec<u8>, spec: &AbyssalSceneSpec) {
    assert!(valid_abyssal_scene_spec(spec), "invalid Abyssal scene specification");
    put_scene_key(out, spec.scene);
    put_abyssal_modifiers(out, &spec.environment.global_modifiers);
    out.extend_from_slice(&(spec.environment.fields.len() as u32).to_le_bytes());
    for field in &spec.environment.fields {
        for value in field.center {
            out.extend_from_slice(&value.to_le_bytes());
        }
        out.extend_from_slice(&field.radius_meters.to_le_bytes());
        put_abyssal_modifiers(out, &field.modifiers);
    }
    for value in spec.boundary.center {
        out.extend_from_slice(&value.to_le_bytes());
    }
    out.extend_from_slice(&spec.boundary.radius_meters.to_le_bytes());
    out.extend_from_slice(&spec.boundary.tick_interval_ms.to_le_bytes());
    out.extend_from_slice(&(spec.boundary.damage_curve.len() as u32).to_le_bytes());
    for point in &spec.boundary.damage_curve {
        out.extend_from_slice(&point.distance_outside_meters.to_le_bytes());
        out.extend_from_slice(&point.applied_damage_per_second.to_le_bytes());
    }
    out.extend_from_slice(&spec.boundary.terminal_exposure_ms.to_le_bytes());
    out.extend_from_slice(&spec.expiry_filetime.to_le_bytes());
}

fn put_abyssal_scene_object(out: &mut Vec<u8>, object: &AbyssalSceneObject) {
    put_scene_key(out, object.scene);
    out.extend_from_slice(&object.item_id.to_le_bytes());
    out.extend_from_slice(&object.type_id.to_le_bytes());
    out.extend_from_slice(&object.group_id.to_le_bytes());
    out.extend_from_slice(&object.category_id.to_le_bytes());
    out.extend_from_slice(&object.owner_id.to_le_bytes());
    out.extend_from_slice(&object.radius.to_le_bytes());
    out.extend_from_slice(&object.graphic_id.to_le_bytes());
    put_text(out, &object.name);
    for value in object.position {
        out.extend_from_slice(&value.to_le_bytes());
    }
    for value in object.direction {
        out.extend_from_slice(&value.to_le_bytes());
    }
    out.push(u8::from(object.is_open));
}

fn put_abyssal_result(out: &mut Vec<u8>, request_id: u64, outcome: &Result<(), String>) {
    assert!(request_id > 0, "invalid Abyssal request id");
    out.extend_from_slice(&request_id.to_le_bytes());
    match outcome {
        Ok(()) => {
            out.push(0);
            put_text(out, "");
        }
        Err(reason) => {
            out.push(1);
            put_text(out, reason);
        }
    }
}

fn put_abyssal_trace(out: &mut Vec<u8>, trace: &AbyssalTrace) {
    assert!(valid_abyssal_trace(trace), "invalid Abyssal trace");
    out.extend_from_slice(&trace.solar_system_id.to_le_bytes());
    out.extend_from_slice(&trace.item_id.to_le_bytes());
    out.extend_from_slice(&trace.type_id.to_le_bytes());
    out.extend_from_slice(&trace.group_id.to_le_bytes());
    out.extend_from_slice(&trace.category_id.to_le_bytes());
    out.extend_from_slice(&trace.owner_id.to_le_bytes());
    out.extend_from_slice(&trace.radius.to_le_bytes());
    for value in trace.position.iter().chain(&trace.velocity).chain(&trace.direction) {
        out.extend_from_slice(&value.to_le_bytes());
    }
    match trace.target_point {
        Some(target) => {
            out.push(1);
            for value in target {
                out.extend_from_slice(&value.to_le_bytes());
            }
        }
        None => out.push(0),
    }
    out.extend_from_slice(&trace.speed_fraction.to_le_bytes());
    out.extend_from_slice(&trace.game_mode_id.to_le_bytes());
    out.extend_from_slice(&trace.filament_type_id.to_le_bytes());
    out.extend_from_slice(&trace.difficulty_tier.to_le_bytes());
    out.extend_from_slice(&trace.expires_at_ms.to_le_bytes());
}

fn put_abyssal_trace_result(
    out: &mut Vec<u8>,
    request_id: u64,
    status: AbyssalTraceStatus,
    recipients: i32,
    actor_notified: Option<bool>,
    error: Option<&str>,
) {
    assert!(request_id > 0 && recipients >= 0, "invalid Abyssal trace receipt");
    assert!(
        (status == AbyssalTraceStatus::Failed) == error.is_some(),
        "Abyssal trace failure must carry its reason"
    );
    if let Some(actor_notified) = actor_notified {
        out.extend_from_slice(&request_id.to_le_bytes());
        out.push(status as u8);
        out.extend_from_slice(&recipients.to_le_bytes());
        out.push(u8::from(actor_notified));
        put_text(out, error.unwrap_or(""));
    } else {
        out.extend_from_slice(&request_id.to_le_bytes());
        out.push(status as u8);
        out.extend_from_slice(&recipients.to_le_bytes());
        put_text(out, error.unwrap_or(""));
    }
}

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
            Self::EnsureAbyssalScene { request_id, spec } => {
                assert!(*request_id > 0, "invalid Abyssal request id");
                out.extend_from_slice(&request_id.to_le_bytes());
                put_abyssal_scene_spec(&mut out, spec);
            }
            Self::EnsureAbyssalSceneObject { request_id, object } => {
                assert!(
                    *request_id > 0 && valid_abyssal_scene_object(object),
                    "invalid Abyssal scene object request id"
                );
                out.extend_from_slice(&request_id.to_le_bytes());
                put_abyssal_scene_object(&mut out, object);
            }
            Self::EnsureAbyssalSceneObjectReply { request_id, outcome }
            | Self::EnsureAbyssalSceneReply { request_id, outcome }
            | Self::MoveAbyssalParticipantReply { request_id, outcome }
            | Self::RebindAbyssalSessionReply { request_id, outcome }
            | Self::RemoveAbyssalSceneReply { request_id, outcome } => {
                put_abyssal_result(&mut out, *request_id, outcome);
            }
            Self::EnsureAbyssalTracePublished { request_id, actor_session_id, trace } => {
                assert!(
                    *request_id > 0 && actor_session_id.is_none_or(|session| session > 0),
                    "invalid Abyssal trace publication"
                );
                out.extend_from_slice(&request_id.to_le_bytes());
                match actor_session_id {
                    Some(session_id) => {
                        out.push(1);
                        out.extend_from_slice(&session_id.to_le_bytes());
                    }
                    None => out.push(0),
                }
                put_abyssal_trace(&mut out, trace);
            }
            Self::EnsureAbyssalTracePublishedReply {
                request_id,
                status,
                recipient_count,
                actor_notified,
                error,
            } => {
                put_abyssal_trace_result(
                    &mut out,
                    *request_id,
                    *status,
                    *recipient_count,
                    Some(*actor_notified),
                    error.as_deref(),
                );
            }
            Self::RemoveAbyssalTrace { request_id, solar_system_id, item_id } => {
                assert!(
                    *request_id > 0 && *solar_system_id > 0 && *item_id > 0,
                    "invalid Abyssal trace removal"
                );
                out.extend_from_slice(&request_id.to_le_bytes());
                out.extend_from_slice(&solar_system_id.to_le_bytes());
                out.extend_from_slice(&item_id.to_le_bytes());
            }
            Self::RemoveAbyssalTraceReply { request_id, status, recipient_count, error } => {
                put_abyssal_trace_result(
                    &mut out,
                    *request_id,
                    *status,
                    *recipient_count,
                    None,
                    error.as_deref(),
                );
            }
            Self::RemoveAbyssalParticipant { request_id, scene, ship_id } => {
                assert!(
                    *request_id > 0 && scene.is_valid() && scene.instance_id > 0 && *ship_id > 0,
                    "invalid Abyssal participant removal"
                );
                out.extend_from_slice(&request_id.to_le_bytes());
                put_scene_key(&mut out, *scene);
                out.extend_from_slice(&ship_id.to_le_bytes());
            }
            Self::RemoveAbyssalParticipantReply { request_id, already_absent, error } => {
                assert!(
                    *request_id > 0 && !(*already_absent && error.is_some()),
                    "invalid Abyssal participant removal reply"
                );
                out.extend_from_slice(&request_id.to_le_bytes());
                out.push(u8::from(*already_absent));
                put_text(&mut out, error.as_deref().unwrap_or(""));
            }
            Self::GetAbyssalParticipantPose { request_id, scene, ship_id } => {
                assert!(*request_id > 0 && scene.is_valid() && *ship_id > 0, "invalid resident pose request");
                out.extend_from_slice(&request_id.to_le_bytes());
                put_scene_key(&mut out, *scene);
                out.extend_from_slice(&ship_id.to_le_bytes());
            }
            Self::GetAbyssalParticipantPoseReply { request_id, scene, ship_id, outcome, deployment } => {
                assert!(
                    *request_id > 0 && scene.is_valid() && *ship_id > 0,
                    "invalid resident pose reply identity"
                );
                out.extend_from_slice(&request_id.to_le_bytes());
                put_scene_key(&mut out, *scene);
                out.extend_from_slice(&ship_id.to_le_bytes());
                match outcome {
                    Ok((position, direction)) => {
                        assert!(
                            position.iter().chain(direction).all(|value| value.is_finite())
                                && direction.iter().any(|value| *value != 0.0),
                            "invalid Abyssal resident pose"
                        );
                        out.push(0);
                        for value in position.iter().chain(direction) {
                            out.extend_from_slice(&value.to_le_bytes());
                        }
                        put_text(&mut out, "");
                    }
                    Err(error) => {
                        assert!(!error.is_empty(), "empty Abyssal pose failure reason");
                        out.push(1);
                        put_text(&mut out, error);
                    }
                }
                out.push(u8::from(deployment.is_some()));
                if let Some(snapshot) = deployment {
                    assert!(outcome.is_ok() && scene.instance_id == 0);
                    for flag in [
                        snapshot.warping,
                        snapshot.cloaked,
                        snapshot.cyno_active,
                        snapshot.undock_invulnerable,
                        snapshot.warp_scrambled,
                    ] {
                        out.push(u8::from(flag));
                    }
                    out.extend_from_slice(&(snapshot.nearby_objects.len() as u32).to_le_bytes());
                    let mut seen = std::collections::HashSet::new();
                    for object in &snapshot.nearby_objects {
                        assert!(
                            object.item_id > 0
                                && object.item_id != *ship_id
                                && seen.insert(object.item_id)
                                && object.type_id > 0
                                && object.center_distance_meters.is_finite()
                                && (0.0..=1_000_000.0).contains(&object.center_distance_meters)
                        );
                        out.extend_from_slice(&object.item_id.to_le_bytes());
                        out.extend_from_slice(&object.type_id.to_le_bytes());
                        out.extend_from_slice(&object.center_distance_meters.to_le_bytes());
                    }
                }
            }
            Self::MoveAbyssalParticipant {
                request_id,
                ship_id,
                source,
                destination,
                position,
                preserve_source_pose,
                session_handoff,
                direction,
            } => {
                let room_transition = source.instance_id > 0
                    && source.instance_id == destination.instance_id
                    && source.room_index != destination.room_index;
                let entry = source.instance_id == 0 && destination.instance_id > 0;
                let origin_return = source.instance_id > 0 && destination.instance_id == 0;
                assert!(*request_id > 0 && *ship_id > 0, "invalid Abyssal participant identity");
                assert!(
                    source.is_valid()
                        && destination.is_valid()
                        && (room_transition || entry || origin_return),
                    "invalid Abyssal scene transition"
                );
                assert!(
                    !*session_handoff || entry || origin_return,
                    "only an Abyssal boundary move can hand off the session"
                );
                if *preserve_source_pose {
                    assert!(room_transition, "only a room transition can carry the source pose");
                    assert!(
                        position == &[0.0; 3] && direction == &[0.0; 3],
                        "preserved-pose moves carry no explicit pose"
                    );
                } else {
                    assert!(
                        position.iter().chain(direction).all(|value| value.is_finite())
                            && direction.iter().any(|value| *value != 0.0),
                        "invalid Abyssal participant pose"
                    );
                }
                out.extend_from_slice(&request_id.to_le_bytes());
                out.extend_from_slice(&ship_id.to_le_bytes());
                put_scene_key(&mut out, *source);
                put_scene_key(&mut out, *destination);
                out.push((u8::from(*preserve_source_pose)) | (u8::from(*session_handoff) << 1));
                if !preserve_source_pose {
                    for value in position.iter().chain(direction) {
                        out.extend_from_slice(&value.to_le_bytes());
                    }
                }
            }
            Self::RebindAbyssalSession { request_id, session_id, ship_id, scene } => {
                assert!(
                    *request_id > 0 && *session_id > 0 && *ship_id > 0 && scene.is_valid(),
                    "invalid Abyssal session rebind"
                );
                out.extend_from_slice(&request_id.to_le_bytes());
                out.extend_from_slice(&session_id.to_le_bytes());
                out.extend_from_slice(&ship_id.to_le_bytes());
                put_scene_key(&mut out, *scene);
            }
            Self::RemoveAbyssalScene { request_id, scene, settled_npc_ids, settled_loot_ids } => {
                assert!(
                    *request_id > 0 && scene.is_valid() && scene.instance_id > 0,
                    "invalid Abyssal scene removal"
                );
                assert!(
                    !settled_npc_ids.iter().any(|id| settled_loot_ids.contains(id)),
                    "overlapping cleanup IDs"
                );
                out.extend_from_slice(&request_id.to_le_bytes());
                put_scene_key(&mut out, *scene);
                write_structure_damage_ids(&mut out, settled_npc_ids);
                write_structure_damage_ids(&mut out, settled_loot_ids);
            }
            Self::Enter(enter) => {
                out.extend_from_slice(&enter.session_id.to_le_bytes());
                put_scene_key(&mut out, enter.scene);
                out.push(match enter.how {
                    How::Undock => 0,
                    How::InSpace => 1,
                    How::Anchor => 2,
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
            Self::ControlStructure {
                session_id,
                solar_system_id,
                structure_id,
                character_id,
                active,
                control_lease_generation,
            } => {
                out.extend_from_slice(&session_id.to_le_bytes());
                out.extend_from_slice(&solar_system_id.to_le_bytes());
                out.extend_from_slice(&structure_id.to_le_bytes());
                out.extend_from_slice(&character_id.to_le_bytes());
                out.push(u8::from(*active));
                out.extend_from_slice(&control_lease_generation.to_le_bytes());
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
                out.push(u8::from(can.expected_scene.is_some()));
                if let Some(scene) = can.expected_scene {
                    put_scene_key(&mut out, scene);
                }
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
            Self::StructureDamageCandidatesRequest {
                session_id,
                structure_id,
                module_id,
                effect_id,
                activation_nonce,
                control_lease_generation,
                cycle_id,
                pulse_hop_ordinal,
                candidate_ids,
            } => {
                assert!(
                    *session_id > 0
                        && *structure_id > 0
                        && *module_id > 0
                        && *effect_id > 0
                        && *activation_nonce > 0
                        && *control_lease_generation > 0
                        && *cycle_id > 0,
                    "invalid structure-damage correlation"
                );
                write_structure_damage_correlation(
                    &mut out,
                    *session_id,
                    *structure_id,
                    *module_id,
                    *effect_id,
                    *activation_nonce,
                    *control_lease_generation,
                    *cycle_id,
                    *pulse_hop_ordinal,
                );
                write_structure_damage_ids(&mut out, candidate_ids);
            }
            Self::StructureDamageCandidatesResult {
                session_id,
                structure_id,
                module_id,
                effect_id,
                activation_nonce,
                control_lease_generation,
                cycle_id,
                pulse_hop_ordinal,
                allowed_target_ids,
            } => {
                assert!(
                    *session_id > 0
                        && *structure_id > 0
                        && *module_id > 0
                        && *effect_id > 0
                        && *activation_nonce > 0
                        && *control_lease_generation > 0
                        && *cycle_id > 0,
                    "invalid structure-damage correlation"
                );
                write_structure_damage_correlation(
                    &mut out,
                    *session_id,
                    *structure_id,
                    *module_id,
                    *effect_id,
                    *activation_nonce,
                    *control_lease_generation,
                    *cycle_id,
                    *pulse_hop_ordinal,
                );
                write_structure_damage_ids(&mut out, allowed_target_ids);
            }
            Self::StructureDamageCandidatesFieldResult {
                session_id,
                structure_id,
                module_id,
                effect_id,
                activation_nonce,
                control_lease_generation,
                cycle_id,
                pulse_hop_ordinal,
                allowed_target_ids,
                field_moved_target_ids,
                field_held_target_ids,
            } => {
                assert!(
                    *session_id > 0
                        && *structure_id > 0
                        && *module_id > 0
                        && *effect_id > 0
                        && *activation_nonce > 0
                        && *control_lease_generation > 0
                        && *cycle_id > 0,
                    "invalid structure-damage correlation"
                );
                assert!(
                    structure_field_partition_is_valid(
                        allowed_target_ids,
                        field_moved_target_ids,
                        field_held_target_ids
                    ),
                    "invalid structure-field target partition"
                );
                write_structure_damage_correlation(
                    &mut out,
                    *session_id,
                    *structure_id,
                    *module_id,
                    *effect_id,
                    *activation_nonce,
                    *control_lease_generation,
                    *cycle_id,
                    *pulse_hop_ordinal,
                );
                write_structure_damage_ids(&mut out, allowed_target_ids);
                if !field_moved_target_ids.is_empty() || !field_held_target_ids.is_empty() {
                    out.push(STRUCTURE_FIELD_PARTITION_EXTENSION_VERSION);
                    write_structure_damage_ids(&mut out, field_moved_target_ids);
                    write_structure_damage_ids(&mut out, field_held_target_ids);
                }
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
            Self::PlaceWreck(placement) => put_wreck_placement(&mut out, placement),
            Self::EnsureAbyssalEnvironmentObject { request_id, object } => {
                assert!(
                    *request_id > 0 && valid_abyssal_environment_object(object),
                    "invalid Abyssal environment object"
                );
                out.extend_from_slice(&request_id.to_le_bytes());
                put_text(&mut out, &object.content_id);
                put_abyssal_scene_object(&mut out, &object.body);
                out.extend_from_slice(&object.effect_radius_meters.to_le_bytes());
                out.push(u8::from(object.point_defense.is_some()));
                if let Some(pulse) = &object.point_defense {
                    out.extend_from_slice(&pulse.tick_interval_ms.to_le_bytes());
                    for channel in pulse.damage {
                        out.extend_from_slice(&channel.to_le_bytes());
                    }
                    out.extend_from_slice(&pulse.type_list_id.to_le_bytes());
                    for ids in &pulse.targets {
                        out.extend_from_slice(&(ids.len() as u32).to_le_bytes());
                        for id in ids {
                            out.extend_from_slice(&id.to_le_bytes());
                        }
                    }
                    put_text(&mut out, &pulse.effect_name);
                }
            }
            Self::EnsureAbyssalEnvironmentObjectReply { request_id, outcome }
            | Self::RefreshAbyssalNpcReply { request_id, outcome } => {
                put_abyssal_result(&mut out, *request_id, outcome)
            }
            Self::AbyssalFieldMembership { scene, source_item_id, ship_id, session_id, inside } => {
                put_scene_key(&mut out, *scene);
                out.extend_from_slice(&source_item_id.to_le_bytes());
                out.extend_from_slice(&ship_id.to_le_bytes());
                out.extend_from_slice(&session_id.to_le_bytes());
                out.push(u8::from(*inside));
            }
            Self::RefreshAbyssalNpc { request_id, scene, session_id, ship, identity } => {
                out.extend_from_slice(&request_id.to_le_bytes());
                put_scene_key(&mut out, *scene);
                out.extend_from_slice(&session_id.to_le_bytes());
                put_ship(&mut out, ship);
                put_identity(&mut out, identity);
            }
            Self::EnsureAbyssalRewardCache { request_id, cache } => {
                assert!(*request_id > 0 && valid_abyssal_reward_cache(cache), "invalid Abyssal cache");
                out.extend_from_slice(&request_id.to_le_bytes());
                put_text(&mut out, &cache.content_id);
                put_scene_key(&mut out, cache.scene);
                put_ship(&mut out, &cache.ship);
                put_vector(&mut out, &cache.position);
                put_vector(&mut out, &cache.direction);
            }
            Self::EnsureAbyssalLootContainer { request_id, content_id, placement } => {
                assert!(*request_id > 0 && valid_abyssal_loot(content_id, placement), "invalid Abyssal loot");
                out.extend_from_slice(&request_id.to_le_bytes());
                put_text(&mut out, content_id);
                put_wreck_placement(&mut out, placement);
            }
            Self::EnsureAbyssalRewardCacheReply { request_id, outcome }
            | Self::EnsureAbyssalLootContainerReply { request_id, outcome } => {
                put_abyssal_result(&mut out, *request_id, outcome)
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
            Self::RemoveCynoBeacon { solar_system_id, structure_beacon_id } => {
                out.extend_from_slice(&solar_system_id.to_le_bytes());
                out.extend_from_slice(&structure_beacon_id.to_le_bytes());
            }
            Self::RemoteDecloakPing { solar_system_id, observatory_id } => {
                out.extend_from_slice(&solar_system_id.to_le_bytes());
                out.extend_from_slice(&observatory_id.to_le_bytes());
            }
            Self::DamageableEntityUpserted {
                solar_system_id,
                entity_id,
                type_id,
                owner_id,
                corporation_id,
                alliance_id,
                war_faction_id,
            } => {
                assert!(
                    *solar_system_id > 0
                        && *entity_id > 0
                        && *type_id > 0
                        && *owner_id >= 0
                        && *corporation_id >= 0
                        && *alliance_id >= 0
                        && *war_faction_id >= 0,
                    "invalid damageable entity identity"
                );
                out.extend_from_slice(&solar_system_id.to_le_bytes());
                out.extend_from_slice(&entity_id.to_le_bytes());
                out.extend_from_slice(&type_id.to_le_bytes());
                out.extend_from_slice(&owner_id.to_le_bytes());
                out.extend_from_slice(&corporation_id.to_le_bytes());
                out.extend_from_slice(&alliance_id.to_le_bytes());
                out.extend_from_slice(&war_faction_id.to_le_bytes());
            }
            Self::DamageableEntityRemoved { solar_system_id, entity_id } => {
                assert!(*solar_system_id > 0 && *entity_id > 0, "invalid damageable entity removal");
                out.extend_from_slice(&solar_system_id.to_le_bytes());
                out.extend_from_slice(&entity_id.to_le_bytes());
            }
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
                out.extend_from_slice(&structure.planet_id.to_le_bytes());
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
            | Self::PlaceCynoBeacon(disruptor)
            | Self::PlaceObservatory(disruptor)
            | Self::PlaceControlTower(disruptor)
            | Self::PlaceDeadspacePocket(disruptor)
            | Self::PlaceAccelerationGate(disruptor) => {
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
            Self::SetSlimField { item_id, name, value } => {
                out.extend_from_slice(&item_id.to_le_bytes());
                put_text(&mut out, name);
                put_slim_value(&mut out, value);
            }
            Self::PlaceCapturePoint(point) => {
                out.extend_from_slice(&point.deployable_id.to_le_bytes());
                out.extend_from_slice(&point.solar_system_id.to_le_bytes());
                put_text(&mut out, &point.name);
                out.extend_from_slice(&point.type_id.to_le_bytes());
                out.extend_from_slice(&point.group_id.to_le_bytes());
                put_vector(&mut out, &point.position);
                out.extend_from_slice(&point.radius.to_le_bytes());
                out.extend_from_slice(&point.attacker_faction.to_le_bytes());
                out.extend_from_slice(&point.occupier_faction.to_le_bytes());
                out.extend_from_slice(&point.next_point_time.to_le_bytes());
            }
            Self::PlaceBattlefieldScoreboard(scoreboard) => {
                out.extend_from_slice(&scoreboard.deployable_id.to_le_bytes());
                out.extend_from_slice(&scoreboard.solar_system_id.to_le_bytes());
                put_text(&mut out, &scoreboard.name);
                out.extend_from_slice(&scoreboard.type_id.to_le_bytes());
                out.extend_from_slice(&scoreboard.group_id.to_le_bytes());
                put_vector(&mut out, &scoreboard.position);
                out.extend_from_slice(&scoreboard.radius.to_le_bytes());
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
                match &spawn.abyssal_content_id {
                    Some(content_id) => {
                        out.push(1);
                        put_text(&mut out, content_id);
                    }
                    None => out.push(0),
                }
                put_scene_key(&mut out, spawn.scene);
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
fn put_text(out: &mut Vec<u8>, text: &str) {
    out.extend_from_slice(&(text.len() as u16).to_le_bytes());
    out.extend_from_slice(text.as_bytes());
}

/// A dynamic slim-item field value: a tag (0 none, 1 int, 2 real, 3 bool, 4 text, 5 tuple), then its payload.
fn put_slim_value(out: &mut Vec<u8>, value: &SlimValue) {
    match value {
        SlimValue::None => out.push(0),
        SlimValue::Int(number) => {
            out.push(1);
            out.extend_from_slice(&number.to_le_bytes());
        }
        SlimValue::Real(number) => {
            out.push(2);
            out.extend_from_slice(&number.to_le_bytes());
        }
        SlimValue::Bool(flag) => out.extend_from_slice(&[3, u8::from(*flag)]),
        SlimValue::Text(text) => {
            out.push(4);
            put_text(out, text);
        }
        SlimValue::Tuple(items) => {
            out.push(5);
            out.extend_from_slice(&(items.len() as u16).to_le_bytes());
            for item in items {
                put_slim_value(out, item);
            }
        }
    }
}

fn put_analysis_beacon_state(out: &mut Vec<u8>, state: &AnalysisBeaconState) {
    out.push(u8::from(state.active));
    out.extend_from_slice(&state.activate_at.to_le_bytes());
    out.extend_from_slice(&state.deployed_at.to_le_bytes());
    out.extend_from_slice(&state.activation_seconds.to_le_bytes());
    out.extend_from_slice(&state.link_state.to_le_bytes());
    out.extend_from_slice(&state.link_complete_at.to_le_bytes());
    out.extend_from_slice(&state.linked_ship_id.to_le_bytes());
}
