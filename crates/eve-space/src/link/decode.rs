use super::*;

use crate::missiles::Dot;

pub(super) fn read_weapon(reader: &mut Reader) -> Result<NpcWeapon, DecodeError> {
    let (module_id, effect_id, cycle_ms, capacitor_need) =
        (reader.i64()?, reader.i32()?, reader.f64()?, reader.f64()?);
    Ok(NpcWeapon { module_id, effect_id, cycle_ms, capacitor_need, turret: read_turret(reader)? })
}

pub(super) fn read_wreck_kind(reader: &mut Reader) -> Result<Option<WreckKind>, DecodeError> {
    match reader.u8()? {
        0 => Ok(None),
        1 => {
            let (type_id, name, graphic) = (reader.i32()?, reader.text()?, reader.i32()?);
            Ok(Some(WreckKind { type_id, name, graphic_id: (graphic != 0).then_some(graphic) }))
        }
        _ => Err(DecodeError::BadValue("wreck")),
    }
}

pub(super) fn read_vector(reader: &mut Reader) -> Result<[f64; 3], DecodeError> {
    Ok([reader.f64()?, reader.f64()?, reader.f64()?])
}

pub(super) fn read_optional_vector(reader: &mut Reader) -> Result<Option<[f64; 3]>, DecodeError> {
    match reader.u8()? {
        0 => Ok(None),
        1 => Ok(Some(read_vector(reader)?)),
        _ => Err(DecodeError::BadValue("optional vector")),
    }
}

pub(super) fn read_credit(reader: &mut Reader) -> Result<Credit, DecodeError> {
    Ok(Credit {
        character_id: reader.i64()?,
        corporation_id: reader.i64()?,
        alliance_id: reader.i64()?,
        war_faction_id: reader.i64()?,
    })
}

/// What a launcher flies (a missile's spec, after its charge's type and group and name): see `put_missile` for the order of the fields.
pub(super) fn read_missile(reader: &mut Reader) -> Result<Missile, DecodeError> {
    Ok(Missile {
        charge_type_id: reader.i32()?,
        group_id: reader.i32()?,
        name: reader.text()?,
        radius: reader.f64()?,
        mass: reader.f64()?,
        agility: reader.f64()?,
        hit_points: reader.f64()?,
        max_velocity: reader.f64()?,
        flight_time_ms: reader.f64()?,
        explosion_radius: reader.f64()?,
        explosion_velocity: reader.f64()?,
        damage_reduction_factor: reader.f64()?,
        damage_reduction_sensitivity: reader.f64()?,
        dot: match reader.u8()? {
            0 => None,
            1 => Some(Dot {
                duration_ms: reader.f64()?,
                max_damage_per_tick: reader.f64()?,
                max_hp_percent_per_tick: reader.f64()?,
            }),
            _ => return Err(DecodeError::BadValue("dot")),
        },
    })
}

pub(super) fn read_turret(reader: &mut Reader) -> Result<Turret, DecodeError> {
    let weapon_type_id = reader.i32()?;
    let fx = reader.text()?;
    let raw_damage = [reader.f64()?, reader.f64()?, reader.f64()?, reader.f64()?];
    let (optimal_range, falloff, tracking_speed, signature_resolution) =
        (reader.f64()?, reader.f64()?, reader.f64()?, reader.f64()?);
    let charge = match reader.u8()? {
        0 => None,
        1 => {
            let (type_id, flag, rounds, per_cycle) =
                (reader.i32()?, reader.i32()?, reader.i64()?, reader.i64()?);
            let reloads = match reader.u8()? {
                0 => false,
                1 => true,
                _ => return Err(DecodeError::BadValue("charge reloads")),
            };
            Some(Charge { type_id, flag, rounds, per_cycle, reloads })
        }
        _ => return Err(DecodeError::BadValue("charge")),
    };
    let missile = match reader.u8()? {
        0 => None,
        1 => Some(read_missile(reader)?),
        _ => return Err(DecodeError::BadValue("missile")),
    };
    let spool = match reader.u8()? {
        0 => None,
        1 => {
            let (per_cycle, max) = (reader.f64()?, reader.f64()?);
            if !per_cycle.is_finite() || per_cycle < 0.0 || !max.is_finite() || max < 0.0 {
                return Err(DecodeError::BadValue("spool"));
            }
            Some(Spool { per_cycle, max })
        }
        _ => return Err(DecodeError::BadValue("spool")),
    };
    let aoe = match reader.u8()? {
        0 => None,
        1 => {
            let (explosion_radius, explosion_velocity) = (reader.f64()?, reader.f64()?);
            let (damage_reduction_factor, damage_reduction_sensitivity) = (reader.f64()?, reader.f64()?);
            let numbers =
                [explosion_radius, explosion_velocity, damage_reduction_factor, damage_reduction_sensitivity];
            if numbers.iter().any(|number| !number.is_finite() || *number < 0.0) {
                return Err(DecodeError::BadValue("aoe"));
            }
            Some(Aoe {
                explosion_radius,
                explosion_velocity,
                damage_reduction_factor,
                damage_reduction_sensitivity,
            })
        }
        _ => return Err(DecodeError::BadValue("aoe")),
    };
    let arc = match reader.u8()? {
        0 => None,
        1 => {
            let (range, targets) = (reader.f64()?, reader.i32()?);
            if !range.is_finite() || range < 0.0 || targets < 0 {
                return Err(DecodeError::BadValue("arc"));
            }
            Some(Arc { range, targets })
        }
        _ => return Err(DecodeError::BadValue("arc")),
    };
    let requires_gunnery = match reader.u8()? {
        0 => false,
        1 => true,
        _ => return Err(DecodeError::BadValue("requires_gunnery")),
    };
    Ok(Turret {
        weapon_type_id,
        fx,
        raw_damage,
        optimal_range,
        falloff,
        tracking_speed,
        signature_resolution,
        charge,
        missile,
        spool,
        aoe,
        arc,
        requires_gunnery,
    })
}

pub(super) fn read_identity(reader: &mut Reader) -> Result<NpcIdentity, DecodeError> {
    let hostile_response_threshold = reader.i64()?;
    let friendly_response_threshold = reader.i64()?;
    let aggression_range = reader.f64()?;
    let count = reader.u32()? as usize;
    // a weapon takes at least 100 bytes, so a count the frame cannot hold is refused before anything is allocated for it
    if count > reader.data.len() / 100 {
        return Err(DecodeError::Truncated);
    }
    let mut weapons = Vec::with_capacity(count);
    for _ in 0..count {
        weapons.push(read_weapon(reader)?);
    }
    let wreck = read_wreck_kind(reader)?;
    let hull = match reader.u8()? {
        0 => false,
        1 => true,
        _ => return Err(DecodeError::BadValue("hull")),
    };
    let host_wreck = match reader.u8()? {
        0 => false,
        1 => true,
        _ => return Err(DecodeError::BadValue("host wreck")),
    };
    Ok(NpcIdentity {
        hostile_response_threshold,
        friendly_response_threshold,
        combat: NpcCombat { weapons, aggression_range },
        wreck,
        hull,
        host_wreck,
        drone: None,
    })
}

pub(super) fn read_layer(reader: &mut Reader) -> Result<Layer, DecodeError> {
    match reader.u8()? {
        0 => Ok(Layer::Shield),
        1 => Ok(Layer::Armor),
        2 => Ok(Layer::Structure),
        _ => Err(DecodeError::BadValue("layer")),
    }
}

pub(super) fn read_applies(reader: &mut Reader) -> Result<Applies, DecodeError> {
    match reader.u8()? {
        0 => Ok(Applies::CycleStart),
        1 => Ok(Applies::CycleEnd),
        _ => Err(DecodeError::BadValue("applies")),
    }
}

/// A strike's body after its tag (13 and 15), as `write_strike` writes it.
pub(super) fn read_strike(reader: &mut Reader) -> Result<Strike, DecodeError> {
    let shape = StrikeShape::from_wire(reader.u8()?).ok_or(DecodeError::BadValue("strike shape"))?;
    let direction = [reader.f64()?, reader.f64()?, reader.f64()?];
    let length = reader.f64()?;
    let radius = reader.f64()?;
    let warning_ms = reader.f64()?;
    let pulse_ms = reader.f64()?;
    let pulses = reader.i32()?;
    let damage = [reader.f64()?, reader.f64()?, reader.f64()?, reader.f64()?];
    let signature_radius = reader.f64()?;
    let immobile_ms = reader.f64()?;
    let no_jump_ms = reader.f64()?;
    let neut_amount = reader.f64()?;
    let neut_radius = reader.f64()?;
    let neut_signature_radius = reader.f64()?;
    let debuff_ms = reader.f64()?;
    let sweep_end = match reader.u8()? {
        0 => {
            let _unused = [reader.f64()?, reader.f64()?, reader.f64()?];
            None
        }
        1 => Some([reader.f64()?, reader.f64()?, reader.f64()?]),
        _ => return Err(DecodeError::BadValue("strike sweep flag")),
    };
    let beacon = match reader.u8()? {
        0 => {
            let _unused = [reader.f64()?, reader.f64()?, reader.f64()?];
            None
        }
        1 => Some([reader.f64()?, reader.f64()?, reader.f64()?]),
        _ => return Err(DecodeError::BadValue("strike beacon flag")),
    };
    let strike = Strike {
        debuff_ms,
        sweep_end,
        beacon,
        shape,
        direction,
        length,
        radius,
        warning_ms,
        pulse_ms,
        pulses,
        damage,
        signature_radius,
        immobile_ms,
        no_jump_ms,
        neut_amount,
        neut_radius,
        neut_signature_radius,
    };
    if !strike.is_valid() {
        return Err(DecodeError::BadValue("strike"));
    }
    Ok(strike)
}

/// A list of ship ids, as `write_ids` writes it (at most 4096 of them).
pub(super) fn read_ids(reader: &mut Reader) -> Result<Vec<i64>, DecodeError> {
    let count = reader.u32()? as usize;
    if count > 4096 {
        return Err(DecodeError::BadValue("transport ids"));
    }
    (0..count).map(|_| reader.i64()).collect()
}

pub(super) fn read_numbers(reader: &mut Reader) -> Result<Numbers, DecodeError> {
    Ok(Numbers { mass: reader.f64()?, max_velocity: reader.f64()?, signature_radius: reader.f64()? })
}

pub(super) fn read_changes(reader: &mut Reader) -> Result<Vec<Change>, DecodeError> {
    let count = reader.u32()? as usize;
    // a change takes 20 bytes, so a count the frame cannot hold is refused before anything is allocated for it
    if count > reader.data.len() / 20 {
        return Err(DecodeError::Truncated);
    }
    let mut changes = Vec::with_capacity(count);
    for _ in 0..count {
        changes.push(Change { item_id: reader.i64()?, attribute_id: reader.i32()?, value: reader.f64()? });
    }
    Ok(changes)
}

/// The ships a command burst reaches (`ModuleWorkCycle`, protocol 43): a count, then each ship's id. A count the frame cannot hold is refused before it is read.
pub(super) fn read_recipients(reader: &mut Reader) -> Result<Vec<i64>, DecodeError> {
    let count = reader.u32()? as usize;
    if count > reader.data.len() / 8 {
        return Err(DecodeError::Truncated);
    }
    (0..count).map(|_| reader.i64()).collect()
}

pub(super) fn read_effect(reader: &mut Reader) -> Result<Effect, DecodeError> {
    let module_id = reader.i64()?;
    let module_type = reader.i32()?;
    let effect_id = reader.i32()?;
    let fx = reader.text()?;
    let duration_ms = reader.f64()?;
    let capacitor_need = reader.f64()?;
    let repeat = reader.i32()?;
    let target_id = reader.i64()?;
    let flags = reader.u8()?;
    // bits: 1 on, 2 off, 4 turret, 8 action, 16 burns fuel (a fuel cycle is asked of the host before each cycle that starts), 32 the module rules
    if flags > 63 {
        return Err(DecodeError::BadValue("flags"));
    }
    let on = if flags & 1 != 0 { Some(read_numbers(reader)?) } else { None };
    let off = if flags & 2 != 0 { Some(read_numbers(reader)?) } else { None };
    let on_changes = read_changes(reader)?;
    let off_changes = read_changes(reader)?;
    let turret = if flags & 4 != 0 { Some(read_turret(reader)?) } else { None };
    let action = if flags & 8 != 0 { Some(read_action(reader)?) } else { None };
    let rules = if flags & 32 != 0 { read_rules(reader)? } else { Rules::default() };
    Ok(Effect {
        rules,
        burns_fuel: flags & 16 != 0,
        module_id,
        module_type,
        effect_id,
        fx,
        duration_ms,
        capacitor_need,
        repeat,
        target_id,
        turret,
        action,
        on,
        off,
        on_changes,
        off_changes,
    })
}

/// The module rules of an effect (flag 32, after its action): the group and its limit, the rule bits (1 early deactivation, 2 needs a core, 4 is a core), and the
/// reactivation delay.
pub(super) fn read_rules(reader: &mut Reader) -> Result<Rules, DecodeError> {
    let group_id = reader.i32()?;
    let max_group_active = reader.i32()?;
    let bits = reader.u8()?;
    if bits > 7 {
        return Err(DecodeError::BadValue("rule bits"));
    }
    let reactivation_delay_ms = reader.f64()?;
    let (max_velocity_limit, min_velocity_limit) = (reader.f64()?, reader.f64()?);
    if !(max_velocity_limit.is_finite()
        && max_velocity_limit >= 0.0
        && min_velocity_limit.is_finite()
        && min_velocity_limit >= 0.0)
    {
        return Err(DecodeError::BadValue("velocity limit"));
    }
    Ok(Rules {
        group_id,
        max_group_active,
        disallow_early_deactivation: bits & 1 != 0,
        requires_industrial_core: bits & 2 != 0,
        provides_industrial_core: bits & 4 != 0,
        reactivation_delay_ms,
        max_velocity_limit,
        min_velocity_limit,
    })
}

pub(super) fn read_condition(reader: &mut Reader) -> Result<Condition, DecodeError> {
    Ok(Condition {
        damage: reader.f64()?,
        armor_damage: reader.f64()?,
        shield_charge: reader.f64()?,
        charge: reader.f64()?,
    })
}

pub(super) fn read_damage(reader: &mut Reader) -> Result<[f64; 4], DecodeError> {
    Ok([reader.f64()?, reader.f64()?, reader.f64()?, reader.f64()?])
}

pub(super) fn read_vitals(reader: &mut Reader) -> Result<Vitals, DecodeError> {
    Ok(Vitals {
        shield_capacity: reader.f64()?,
        armor_hp: reader.f64()?,
        structure_hp: reader.f64()?,
        resonances: Resonances {
            shield: read_damage(reader)?,
            armor: read_damage(reader)?,
            structure: read_damage(reader)?,
        },
        armor_pinned: match reader.u8()? {
            0 => false,
            1 => true,
            _ => return Err(DecodeError::BadValue("armor pinned")),
        },
    })
}
