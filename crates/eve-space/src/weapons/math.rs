use super::*;

/// A shot that does not roll (a vorton projector's primary, EVE.js `resolveVortonShot`): it hits its target whole when the target is within the weapon's
/// optimal range (nothing beyond it, no falloff, no tracking), and what the explosion does to the target is `explosion`.
pub(crate) fn explosion_shot(
    aoe: &Aoe,
    raw: Damage,
    optimal: f64,
    attacker: &Kinematics,
    target: &Kinematics,
) -> Shot {
    let out_of_range = surface_distance(attacker, target) > optimal.max(0.0) + 1e-6;
    if out_of_range || raw.iter().sum::<f64>() <= 0.0 {
        let chance_to_hit = if out_of_range { 0.0 } else { 1.0 };
        return Shot {
            hit: false,
            wrecking: false,
            quality: 0.0,
            chance_to_hit,
            roll: 0.0,
            damage: [0.0; 4],
        };
    }
    explosion(aoe, raw, target)
}

/// What an explosion does to one ship, with no range check (an arc's victim, EVE.js `resolveVortonArcShot`): the raw damage times the application factor
/// of the ship's signature and speed, the missile's curve. A hit whenever there is damage to do.
pub(crate) fn explosion(aoe: &Aoe, raw: Damage, target: &Kinematics) -> Shot {
    if raw.iter().sum::<f64>() <= 0.0 {
        return Shot {
            hit: false,
            wrecking: false,
            quality: 0.0,
            chance_to_hit: 0.0,
            roll: 0.0,
            damage: [0.0; 4],
        };
    }
    let signature =
        if target.signature_radius > 0.0 { target.signature_radius } else { target.radius.max(1.0) }.max(1.0);
    let speed = length(target.velocity);
    let factor = application_factor_of(
        aoe.explosion_radius,
        aoe.explosion_velocity,
        aoe.damage_reduction_factor,
        aoe.damage_reduction_sensitivity,
        signature,
        speed,
    )
    .clamp(0.0, 1.0);
    Shot {
        hit: true,
        wrecking: false,
        quality: 1.0,
        chance_to_hit: 1.0,
        roll: 0.0,
        damage: raw.map(|amount| round6(amount.max(0.0) * factor)),
    }
}

/// The raw damage of a shot with the bonus a spool has built up: each em, thermal, kinetic and explosive part times `1 + bonus`, rounded as the reference
/// rounds it. No bonus leaves the damage as it is.
pub(crate) fn spooled_damage(raw: Damage, bonus: f64) -> Damage {
    if bonus <= 0.0 {
        return raw;
    }
    let multiplier = round6(1.0 + bonus);
    raw.map(|damage| round6(damage * multiplier))
}

pub(super) fn round6(value: f64) -> f64 {
    (value * 1_000_000.0).round() / 1_000_000.0
}

pub(super) fn sub(a: [f64; 3], b: [f64; 3]) -> [f64; 3] {
    [a[0] - b[0], a[1] - b[1], a[2] - b[2]]
}

pub(super) fn dot(a: [f64; 3], b: [f64; 3]) -> f64 {
    a[0] * b[0] + a[1] * b[1] + a[2] * b[2]
}

pub(super) fn length(a: [f64; 3]) -> f64 {
    dot(a, a).sqrt()
}

/// The distance between the surfaces of two ships.
pub fn surface_distance(attacker: &Kinematics, target: &Kinematics) -> f64 {
    (length(sub(target.position, attacker.position)) - attacker.radius.max(0.0) - target.radius.max(0.0))
        .max(0.0)
}

/// How fast the target crosses the attacker's line of sight, in radians a second.
pub fn angular_velocity(attacker: &Kinematics, target: &Kinematics) -> f64 {
    let relative = sub(target.position, attacker.position);
    let centre = length(relative).max(1.0);
    let unit = [relative[0] / centre, relative[1] / centre, relative[2] / centre];
    let velocity = sub(target.velocity, attacker.velocity);
    let radial = dot(velocity, unit);
    let tangential =
        [velocity[0] - unit[0] * radial, velocity[1] - unit[1] * radial, velocity[2] - unit[2] * radial];
    length(tangential) / centre
}

/// The chance that a shot hits (0 for a turret that cannot track, or that shoots past its optimal range with no falloff).
pub fn chance_to_hit(turret: &Turret, attacker: &Kinematics, target: &Kinematics) -> f64 {
    let distance = surface_distance(attacker, target);
    let angular = angular_velocity(attacker, target);
    let target_signature =
        if target.signature_radius > 0.0 { target.signature_radius } else { target.radius.max(1.0) };
    let resolution = turret.signature_resolution.max(1.0);
    let tracking_term = if turret.tracking_speed > 0.0 {
        angular * resolution / (turret.tracking_speed * target_signature.max(1.0))
    } else {
        f64::INFINITY
    };
    let optimal = turret.optimal_range.max(0.0);
    let range_term = if distance > optimal {
        if turret.falloff > 0.0 { (distance - optimal) / turret.falloff } else { f64::INFINITY }
    } else {
        0.0
    };
    if !tracking_term.is_finite() || !range_term.is_finite() {
        return 0.0;
    }
    0.5_f64.powf(tracking_term.powi(2) + range_term.powi(2)).clamp(0.0, 1.0)
}

/// One shot, decided by `roll` (a number of `[0, 1)`).
pub fn resolve_shot(turret: &Turret, attacker: &Kinematics, target: &Kinematics, roll: f64) -> Shot {
    let chance = chance_to_hit(turret, attacker, target);
    let roll = roll.clamp(0.0, 0.999_999_999);
    let total: f64 = turret.raw_damage.iter().sum();
    if total <= 0.0 || roll >= chance {
        return Shot {
            hit: false,
            wrecking: false,
            quality: 0.0,
            chance_to_hit: round6(chance),
            roll: round6(roll),
            damage: [0.0; 4],
        };
    }
    let wrecking = roll < 0.01;
    let quality = if wrecking { 3.0 } else { round6(0.49 + roll) };
    Shot {
        hit: true,
        wrecking,
        quality,
        chance_to_hit: round6(chance),
        roll: round6(roll),
        damage: turret.raw_damage.map(|d| round6(d * quality)),
    }
}

/// damage multiplier).
pub fn hit_quality_tier(shot: &Shot) -> u8 {
    if !shot.hit {
        return 0;
    }
    match shot.quality {
        q if q >= 3.0 => 6,
        q if q >= 1.25 => 5,
        q if q >= 1.0 => 4,
        q if q >= 0.75 => 3,
        q if q >= 0.625 => 2,
        _ => 1,
    }
}

/// The `OnDamageMessage` a shot makes: for the pilot of the ship that shot (`attackType` `me`) or for the pilot of the ship that was shot

#[allow(clippy::too_many_arguments)]
pub fn damage_message(
    for_the_attacker: bool,
    attacker_id: i64,
    target_id: i64,
    weapon_type_id: i32,
    applied: f64,
    shot: &Shot,
    owner_id: i64,
) -> Value {
    quality_message(
        for_the_attacker,
        attacker_id,
        target_id,
        weapon_type_id,
        applied,
        shot.damage,
        hit_quality_tier(shot),
        owner_id,
    )
}

/// The `OnDamageMessage` of whatever damage reached a ship, with the hit quality the combat log is to show (a missile that lands is always `3`).
#[allow(clippy::too_many_arguments)]
pub(crate) fn quality_message(
    for_the_attacker: bool,
    attacker_id: i64,
    target_id: i64,
    weapon_type_id: i32,
    applied: f64,
    damage: Damage,
    tier: u8,
    owner_id: i64,
) -> Value {
    let attributes = Value::Dict(
        DAMAGE_ATTRIBUTES
            .iter()
            .zip(damage)
            .map(|(attribute, amount)| (int(*attribute), number6(amount)))
            .collect(),
    );
    let mut entries: Vec<(&str, Value)> = vec![
        ("attackType", text(if for_the_attacker { "me" } else { "otherPlayerWeapons" })),
        ("source", id(attacker_id)),
        ("target", id(target_id)),
        ("weapon", int(i64::from(weapon_type_id))),
        ("damage", number6(applied)),
        ("damageAttributes", attributes),
        ("splash", text("")),
        ("hitQuality", int(i64::from(tier))),
        ("isBanked", Value::Bool(false)),
    ];
    if owner_id > 0 {
        entries.push(("owner", tuple([int(2), id(owner_id)])));
    }
    if !for_the_attacker && attacker_id > 0 {
        entries.push(("attackerID", id(attacker_id)));
    }
    dict(entries)
}

/// A number as the reference marshals it: whole numbers are ints, the rest are rounded to six places.
pub(super) fn number6(value: f64) -> Value {
    let rounded = round6(value);
    if rounded.fract() == 0.0 && rounded.abs() < 9.0e15 { int(rounded as i64) } else { real(rounded) }
}
