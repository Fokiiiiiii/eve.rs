use super::*;

pub(super) fn put_text(out: &mut Vec<u8>, text: &str) {
    out.extend_from_slice(&(text.len() as u16).to_le_bytes());
    out.extend_from_slice(text.as_bytes());
}

pub(super) fn put_bytes(out: &mut Vec<u8>, bytes: &[u8]) {
    out.extend_from_slice(&(bytes.len() as u32).to_le_bytes());
    out.extend_from_slice(bytes);
}

pub(super) fn put_condition(out: &mut Vec<u8>, condition: &Condition) {
    for number in [condition.damage, condition.armor_damage, condition.shield_charge, condition.charge] {
        out.extend_from_slice(&number.to_le_bytes());
    }
}

pub(super) fn put_vitals(out: &mut Vec<u8>, vitals: &Vitals) {
    let resonances = &vitals.resonances;
    let layers = [vitals.shield_capacity, vitals.armor_hp, vitals.structure_hp];
    for number in
        layers.iter().chain(&resonances.shield).chain(&resonances.armor).chain(&resonances.structure)
    {
        out.extend_from_slice(&number.to_le_bytes());
    }
    out.push(u8::from(vitals.armor_pinned));
}

pub(super) fn put_heat_profile(out: &mut Vec<u8>, profile: &HeatProfile) {
    out.extend_from_slice(&profile.generation_multiplier.to_le_bytes());
    out.extend_from_slice(&profile.damage_multiplier.to_le_bytes());
    out.extend_from_slice(&i32::from(profile.thermodynamics_level).to_le_bytes());
    for value in profile
        .capacity
        .iter()
        .chain(&profile.initial_rack_heat)
        .chain(&profile.dissipation_rate)
        .chain(&profile.attenuation)
    {
        out.extend_from_slice(&value.to_le_bytes());
    }
    out.extend_from_slice(&(profile.modules.len() as i32).to_le_bytes());
    for module in &profile.modules {
        out.extend_from_slice(&module.module_id.to_le_bytes());
        out.extend_from_slice(&module.flag.to_le_bytes());
        for value in
            [module.structure_hp, module.damage_hp, module.heat_damage_hp, module.heat_absorption_rate]
        {
            out.extend_from_slice(&value.to_le_bytes());
        }
        out.extend_from_slice(&[
            u8::from(module.overloadable),
            u8::from(module.heat_immune),
            u8::from(module.online),
        ]);
    }
}

pub(super) fn put_ship(out: &mut Vec<u8>, ship: &ShipSpec) {
    out.extend_from_slice(&ship.ship_id.to_le_bytes());
    out.extend_from_slice(&ship.character_id.to_le_bytes());
    out.extend_from_slice(&ship.type_id.to_le_bytes());
    out.extend_from_slice(&ship.group_id.to_le_bytes());
    out.extend_from_slice(&ship.category_id.to_le_bytes());
    put_text(out, &ship.name);
    out.extend_from_slice(&ship.owner_id.to_le_bytes());
    out.extend_from_slice(&ship.corporation_id.to_le_bytes());
    out.extend_from_slice(&ship.alliance_id.to_le_bytes());
    out.extend_from_slice(&ship.war_faction_id.to_le_bytes());
    for number in [
        ship.security_status,
        ship.radius,
        ship.mass,
        ship.max_velocity,
        ship.agility,
        ship.warp_speed_au,
        ship.shield_recharge_ms,
        ship.max_target_range,
        ship.scan_resolution,
        ship.signature_radius,
        ship.capacitor_capacity,
        ship.capacitor_recharge_s,
    ] {
        out.extend_from_slice(&number.to_le_bytes());
    }
    out.extend_from_slice(&ship.max_locked_targets.to_le_bytes());
    put_vitals(out, &ship.vitals);
    put_condition(out, &ship.condition);
    out.extend_from_slice(&(ship.modules.len() as u32).to_le_bytes());
    for module in &ship.modules {
        out.extend_from_slice(&module.item_id.to_le_bytes());
        out.extend_from_slice(&module.type_id.to_le_bytes());
        out.extend_from_slice(&module.flag.to_le_bytes());
    }
    for number in ship.sensor_strength.iter().chain([
        &ship.ecm_resistance,
        &ship.energy_warfare_resistance,
        &ship.drone_damage_multiplier,
    ]) {
        out.extend_from_slice(&number.to_le_bytes());
    }
    // the stance: a flag, and when there is one its old stance, its switch time (a FILETIME) and its new stance (protocol 55)
    match ship.stance {
        None => out.push(0),
        Some(stance) => {
            out.push(1);
            out.extend_from_slice(&stance.old_stance.to_le_bytes());
            out.extend_from_slice(&stance.switched_at.to_le_bytes());
            out.extend_from_slice(&stance.stance.to_le_bytes());
        }
    }
}

pub(super) fn put_numbers(out: &mut Vec<u8>, numbers: &Numbers) {
    for number in [numbers.mass, numbers.max_velocity, numbers.signature_radius] {
        out.extend_from_slice(&number.to_le_bytes());
    }
}

pub(super) fn put_changes(out: &mut Vec<u8>, changes: &[Change]) {
    out.extend_from_slice(&(changes.len() as u32).to_le_bytes());
    for change in changes {
        out.extend_from_slice(&change.item_id.to_le_bytes());
        out.extend_from_slice(&change.attribute_id.to_le_bytes());
        out.extend_from_slice(&change.value.to_le_bytes());
    }
}

/// A weapon of an NPC (or a drone): the module (the ship), the effect, the cycle in milliseconds, what a cycle costs the capacitor, and the turret.
pub(super) fn put_weapon(out: &mut Vec<u8>, weapon: &NpcWeapon) {
    out.extend_from_slice(&weapon.module_id.to_le_bytes());
    out.extend_from_slice(&weapon.effect_id.to_le_bytes());
    out.extend_from_slice(&weapon.cycle_ms.to_le_bytes());
    out.extend_from_slice(&weapon.capacitor_need.to_le_bytes());
    put_turret(out, &weapon.turret);
}

pub(super) fn put_identity(out: &mut Vec<u8>, identity: &NpcIdentity) {
    out.extend_from_slice(&identity.hostile_response_threshold.to_le_bytes());
    out.extend_from_slice(&identity.friendly_response_threshold.to_le_bytes());
    out.extend_from_slice(&identity.combat.aggression_range.to_le_bytes());
    out.extend_from_slice(&(identity.combat.weapons.len() as u32).to_le_bytes());
    for weapon in &identity.combat.weapons {
        put_weapon(out, weapon);
    }
    out.extend_from_slice(&(identity.self_repairs.len() as u32).to_le_bytes());
    for repair in &identity.self_repairs {
        out.extend_from_slice(&repair.effect_id.to_le_bytes());
        out.extend_from_slice(&repair.amount.to_le_bytes());
        out.extend_from_slice(&repair.cycle_ms.to_le_bytes());
        out.extend_from_slice(&repair.capacitor_cost.to_le_bytes());
        out.extend_from_slice(&repair.delay_chance.to_le_bytes());
    }
    put_wreck_kind(out, identity.wreck.as_ref());
    out.push(u8::from(identity.hull));
    out.push(u8::from(identity.host_wreck));
}

/// A wreck's kind, or none: a flag, the type, the name and the graphic (0 for none).
pub(super) fn put_wreck_kind(out: &mut Vec<u8>, kind: Option<&WreckKind>) {
    match kind {
        None => out.push(0),
        Some(wreck) => {
            out.push(1);
            out.extend_from_slice(&wreck.type_id.to_le_bytes());
            put_text(out, &wreck.name);
            out.extend_from_slice(&wreck.graphic_id.unwrap_or(0).to_le_bytes());
        }
    }
}

pub(super) fn put_vector(out: &mut Vec<u8>, vector: &[f64; 3]) {
    for number in vector {
        out.extend_from_slice(&number.to_le_bytes());
    }
}

/// A structure's health, in the order `decode_message::read_structure_health` reads it.
pub(super) fn put_structure_health(
    out: &mut Vec<u8>,
    vitals: &crate::health::Vitals,
    shield_recharge_ms: f64,
    limits: &crate::health::StructureLimits,
) {
    for value in [vitals.shield_capacity, vitals.armor_hp, vitals.structure_hp] {
        out.extend_from_slice(&value.to_le_bytes());
    }
    for layer in [vitals.resonances.shield, vitals.resonances.armor, vitals.resonances.structure] {
        for value in layer {
            out.extend_from_slice(&value.to_le_bytes());
        }
    }
    out.extend_from_slice(&shield_recharge_ms.to_le_bytes());
    for value in limits.per_second.iter().chain(limits.repair_pause.iter()) {
        out.extend_from_slice(&value.to_le_bytes());
    }
}

/// A structure's slim fields, in the order `decode_message::read_structure_status` reads them.
pub(super) fn put_structure_status(out: &mut Vec<u8>, status: &crate::world::StructureStatus) {
    out.extend_from_slice(&status.state.to_le_bytes());
    out.extend_from_slice(&status.upkeep_state.to_le_bytes());
    match status.timer {
        Some((start, end, paused)) => {
            out.push(1);
            out.extend_from_slice(&start.to_le_bytes());
            out.extend_from_slice(&end.to_le_bytes());
            match paused {
                Some(paused) => {
                    out.push(1);
                    out.extend_from_slice(&paused.to_le_bytes());
                }
                None => out.push(0),
            }
        }
        None => out.push(0),
    }
    match status.unanchoring {
        Some(time) => {
            out.push(1);
            out.extend_from_slice(&time.to_le_bytes());
        }
        None => out.push(0),
    }
    out.push(match status.repairing {
        None => 0,
        Some(false) => 1,
        Some(true) => 2,
    });
}

pub(super) fn put_optional_vector(out: &mut Vec<u8>, vector: Option<[f64; 3]>) {
    match vector {
        Some(vector) => {
            out.push(1);
            put_vector(out, &vector);
        }
        None => out.push(0),
    }
}

pub(super) fn put_credit(out: &mut Vec<u8>, credit: &Credit) {
    for number in [credit.character_id, credit.corporation_id, credit.alliance_id, credit.war_faction_id] {
        out.extend_from_slice(&number.to_le_bytes());
    }
}

/// A turret's numbers: the type of the weapon, the effect the client plays, the damage of a shot (em, thermal, kinetic, explosive), the range, the falloff,
/// the tracking and the signature resolution, then the charge it fires (a flag byte, and when set the type, the flag it sits at, the rounds left and the rounds a cycle takes), then the missile it
/// flies (a flag byte, and when set the type of the missile, its speed, its flight time in milliseconds, the explosion radius and velocity and the damage reduction factor and sensitivity).
/// What a launcher flies: the charge's type and group and name, then the ball's radius, mass and agility, the hit points, the speed, the flight time, and the
/// explosion's radius, velocity, damage reduction factor and sensitivity, then the damage-over-time flag and its three numbers (`decode::read_missile` reads them in this order).
pub(super) fn put_missile(out: &mut Vec<u8>, missile: &Missile) {
    out.extend_from_slice(&missile.charge_type_id.to_le_bytes());
    out.extend_from_slice(&missile.group_id.to_le_bytes());
    put_text(out, &missile.name);
    for number in [
        missile.radius,
        missile.mass,
        missile.agility,
        missile.hit_points,
        missile.max_velocity,
        missile.flight_time_ms,
        missile.explosion_radius,
        missile.explosion_velocity,
        missile.damage_reduction_factor,
        missile.damage_reduction_sensitivity,
    ] {
        out.extend_from_slice(&number.to_le_bytes());
    }
    // the damage-over-time of a breacher pod: a flag, then its duration, its cap per tick and its percentage per tick (protocol 42)
    match missile.dot {
        None => out.push(0),
        Some(dot) => {
            out.push(1);
            for number in [dot.duration_ms, dot.max_damage_per_tick, dot.max_hp_percent_per_tick] {
                out.extend_from_slice(&number.to_le_bytes());
            }
        }
    }
}

pub(super) fn put_turret(out: &mut Vec<u8>, turret: &Turret) {
    out.extend_from_slice(&turret.weapon_type_id.to_le_bytes());
    put_text(out, &turret.fx);
    for number in turret.raw_damage.iter().chain(&[
        turret.optimal_range,
        turret.falloff,
        turret.tracking_speed,
        turret.signature_resolution,
    ]) {
        out.extend_from_slice(&number.to_le_bytes());
    }
    match &turret.charge {
        None => out.push(0),
        Some(charge) => {
            out.push(1);
            out.extend_from_slice(&charge.type_id.to_le_bytes());
            out.extend_from_slice(&charge.flag.to_le_bytes());
            out.extend_from_slice(&charge.rounds.to_le_bytes());
            out.extend_from_slice(&charge.per_cycle.to_le_bytes());
            out.push(u8::from(charge.reloads));
        }
    }
    match &turret.missile {
        None => out.push(0),
        Some(missile) => {
            out.push(1);
            put_missile(out, missile);
        }
    }
    match &turret.spool {
        None => out.push(0),
        Some(spool) => {
            out.push(1);
            out.extend_from_slice(&spool.per_cycle.to_le_bytes());
            out.extend_from_slice(&spool.max.to_le_bytes());
        }
    }
    match &turret.aoe {
        None => out.push(0),
        Some(aoe) => {
            out.push(1);
            for number in [
                aoe.explosion_radius,
                aoe.explosion_velocity,
                aoe.damage_reduction_factor,
                aoe.damage_reduction_sensitivity,
            ] {
                out.extend_from_slice(&number.to_le_bytes());
            }
        }
    }
    match &turret.arc {
        None => out.push(0),
        Some(arc) => {
            out.push(1);
            out.extend_from_slice(&arc.range.to_le_bytes());
            out.extend_from_slice(&arc.targets.to_le_bytes());
            match arc.damage_loss_target {
                None => out.push(0),
                Some(damage_loss_target) => {
                    assert!(
                        damage_loss_target.is_finite() && damage_loss_target >= 0.0,
                        "invalid arc damage loss target"
                    );
                    out.push(1);
                    out.extend_from_slice(&damage_loss_target.to_le_bytes());
                }
            }
        }
    }
    out.push(u8::from(turret.requires_gunnery));
}

pub(super) fn put_effect(out: &mut Vec<u8>, effect: &Effect) {
    out.extend_from_slice(&effect.module_id.to_le_bytes());
    out.extend_from_slice(&effect.module_type.to_le_bytes());
    out.extend_from_slice(&effect.effect_id.to_le_bytes());
    put_text(out, &effect.fx);
    out.extend_from_slice(&effect.duration_ms.to_le_bytes());
    out.extend_from_slice(&effect.capacitor_need.to_le_bytes());
    out.extend_from_slice(&effect.repeat.to_le_bytes());
    out.extend_from_slice(&effect.target_id.to_le_bytes());
    out.push(
        u8::from(effect.on.is_some())
            | (u8::from(effect.off.is_some()) << 1)
            | (u8::from(effect.turret.is_some()) << 2)
            | (u8::from(effect.action.is_some()) << 3)
            | (u8::from(effect.burns_fuel) << 4)
            | (u8::from(effect.rules != Rules::default()) << 5),
    );
    for numbers in [&effect.on, &effect.off].into_iter().flatten() {
        put_numbers(out, numbers);
    }
    put_changes(out, &effect.on_changes);
    put_changes(out, &effect.off_changes);
    if let Some(turret) = &effect.turret {
        put_turret(out, turret);
    }
    if let Some(action) = &effect.action {
        put_action(out, action);
    }
    // the module rules (flag 32), after the action: the group, its limit, the rule bits and the reactivation delay
    if effect.rules != Rules::default() {
        let rules = &effect.rules;
        out.extend_from_slice(&rules.group_id.to_le_bytes());
        out.extend_from_slice(&rules.max_group_active.to_le_bytes());
        out.push(
            u8::from(rules.disallow_early_deactivation)
                | (u8::from(rules.requires_industrial_core) << 1)
                | (u8::from(rules.provides_industrial_core) << 2),
        );
        out.extend_from_slice(&rules.reactivation_delay_ms.to_le_bytes());
        // the speed limits (protocol 38, WP19): the highest and the lowest speed a module starts at (0 for none)
        out.extend_from_slice(&rules.max_velocity_limit.to_le_bytes());
        out.extend_from_slice(&rules.min_velocity_limit.to_le_bytes());
    }
}

/// What a cycle of an effect does: a tag and its numbers (`0`: a repair of the ship's own: the layer, when in the cycle, the points; `1`: an effect
/// projected at the target: when in the cycle, the optimal range and the falloff, the name the target's HUD gives it (a text), then a kind and its numbers: `0` a repair of the target's layer and the
/// points, `1` a capacitor transfer and its GJ, `2` a neutralizer and `3` a nosferatu: the GJ and the signature resolution, `4` a web and `5` a target painter: a percentage, `6` a sensor dampener: the percentages of the lock range and of the scan resolution, `7` a warp scrambler: the points and whether a microwarpdrive is held too).
/// The strike's body after its tag (13 and 15): its shape, its numbers and its sweep (see `read_strike`).
pub(super) fn write_strike(out: &mut Vec<u8>, strike: &Strike) {
    out.push(strike.shape.wire());
    for component in strike.direction {
        out.extend_from_slice(&component.to_le_bytes());
    }
    for number in [strike.length, strike.radius, strike.warning_ms, strike.pulse_ms] {
        out.extend_from_slice(&number.to_le_bytes());
    }
    out.extend_from_slice(&strike.pulses.to_le_bytes());
    for number in strike.damage {
        out.extend_from_slice(&number.to_le_bytes());
    }
    for number in [
        strike.signature_radius,
        strike.immobile_ms,
        strike.no_jump_ms,
        strike.neut_amount,
        strike.neut_radius,
        strike.neut_signature_radius,
        strike.debuff_ms,
    ] {
        out.extend_from_slice(&number.to_le_bytes());
    }
    match strike.sweep_end {
        Some(end) => {
            out.push(1);
            for component in end {
                out.extend_from_slice(&component.to_le_bytes());
            }
        }
        None => {
            out.push(0);
            out.extend_from_slice(&[0u8; 24]);
        }
    }
    // the beacon: a flag, then its offset from the firing ship (zeros without a beacon)
    match strike.beacon {
        Some(offset) => {
            out.push(1);
            for component in offset {
                out.extend_from_slice(&component.to_le_bytes());
            }
        }
        None => {
            out.push(0);
            out.extend_from_slice(&[0u8; 24]);
        }
    }
}

/// A list of ship ids: a count, then each id (as the protocol's i64).
/// A deployable's charge (see `decode_action::read_deployable`).
pub(super) fn put_deployable(out: &mut Vec<u8>, spec: &Spec) {
    out.extend_from_slice(&spec.charge_type_id.to_le_bytes());
    out.extend_from_slice(&spec.group_id.to_le_bytes());
    put_text(out, &spec.name);
    out.extend_from_slice(&spec.radius.to_le_bytes());
}

pub(super) fn write_ids(out: &mut Vec<u8>, ids: &[i64]) {
    out.extend_from_slice(&(ids.len() as u32).to_le_bytes());
    for id in ids {
        out.extend_from_slice(&id.to_le_bytes());
    }
}

pub(super) fn put_action(out: &mut Vec<u8>, action: &Action) {
    let layer_byte = |layer: &Layer| match layer {
        Layer::Shield => 0,
        Layer::Armor => 1,
        Layer::Structure => 2,
    };
    let when_byte = |when: &Applies| match when {
        Applies::CycleStart => 0,
        Applies::CycleEnd => 1,
    };
    match action {
        Action::Repair { layer, amount, when } => {
            out.push(0);
            out.push(layer_byte(layer));
            out.push(when_byte(when));
            out.extend_from_slice(&amount.to_le_bytes());
        }
        Action::Smartbomb { range, damage, hurts_pilots } => {
            out.push(2);
            out.extend_from_slice(&range.to_le_bytes());
            for number in damage {
                out.extend_from_slice(&number.to_le_bytes());
            }
            out.push(u8::from(*hurts_pilots));
        }
        Action::Charged { action, fallback, charge_type_id, charge_flag, charge_per_cycle, charges } => {
            out.push(4);
            out.extend_from_slice(&charge_type_id.to_le_bytes());
            out.extend_from_slice(&charge_flag.to_le_bytes());
            out.extend_from_slice(&charge_per_cycle.to_le_bytes());
            out.extend_from_slice(&charges.to_le_bytes());
            put_action(out, action);
            out.push(u8::from(fallback.is_some()));
            if let Some(fallback) = fallback {
                put_action(out, fallback);
            }
        }
        Action::EcmBurst { range, strength } => {
            out.push(3);
            out.extend_from_slice(&range.to_le_bytes());
            for number in strength {
                out.extend_from_slice(&number.to_le_bytes());
            }
        }
        Action::Cloak { warp_safe, targeting_delay_ms } => {
            out.extend([5, u8::from(*warp_safe)]);
            out.extend_from_slice(&targeting_delay_ms.to_le_bytes());
        }
        Action::WarpBubble { range } => {
            out.push(16);
            out.extend_from_slice(&range.to_le_bytes());
        }
        Action::WarpImmunity => out.push(17),
        Action::Defender { missile, damage } => {
            out.push(22);
            put_missile(out, missile);
            for number in damage {
                out.extend_from_slice(&number.to_le_bytes());
            }
        }
        Action::StasisBurst { range, duration_ms, speed_factor } => {
            out.push(21);
            out.extend_from_slice(&range.to_le_bytes());
            out.extend_from_slice(&duration_ms.to_le_bytes());
            out.extend_from_slice(&speed_factor.to_le_bytes());
        }
        Action::ScrambleStatus { delta } => {
            out.push(18);
            out.extend_from_slice(&delta.to_le_bytes());
        }
        Action::WarpProbe { ball, range, lifetime_ms } => {
            out.push(19);
            put_deployable(out, ball);
            out.extend_from_slice(&range.to_le_bytes());
            out.extend_from_slice(&lifetime_ms.to_le_bytes());
        }
        Action::Bomb { ball, kind, lifetime_ms, blast_radius, damage, ecm, neutralize } => {
            out.push(20);
            put_deployable(out, ball);
            out.push(match kind {
                BombKind::Damage => 0,
                BombKind::Ecm => 1,
                BombKind::Neutralize => 2,
            });
            out.extend_from_slice(&lifetime_ms.to_le_bytes());
            out.extend_from_slice(&blast_radius.to_le_bytes());
            for number in damage.iter().chain(ecm.iter()).chain([neutralize]) {
                out.extend_from_slice(&number.to_le_bytes());
            }
        }
        Action::Panic { range_meters } => {
            out.push(29);
            out.extend_from_slice(&range_meters.to_le_bytes());
        }
        Action::Entosis { range_meters } => {
            out.push(31);
            out.extend_from_slice(&range_meters.to_le_bytes());
        }
        Action::CommandBurst { range_meters } => {
            out.push(28);
            out.extend_from_slice(&range_meters.to_le_bytes());
        }
        Action::CynoField { ball, lifetime_ms } => {
            out.push(27);
            put_deployable(out, ball);
            out.extend_from_slice(&lifetime_ms.to_le_bytes());
        }
        Action::MicroJumpDrive { distance_meters, cooldown_ms } => {
            out.push(6);
            out.extend_from_slice(&distance_meters.to_le_bytes());
            out.extend_from_slice(&cooldown_ms.to_le_bytes());
        }
        Action::ResistanceShift { seed, shift, attribute_ids } => {
            out.push(12);
            for number in seed {
                out.extend_from_slice(&number.to_le_bytes());
            }
            out.extend_from_slice(&shift.to_le_bytes());
            for attribute in attribute_ids {
                out.extend_from_slice(&attribute.to_le_bytes());
            }
        }
        Action::Strike { strike } => {
            out.push(13);
            write_strike(out, strike);
        }
        Action::Field { strike, destination, warp_block_ms, moved, held } => {
            out.push(15);
            write_strike(out, strike);
            for component in destination {
                out.extend_from_slice(&component.to_le_bytes());
            }
            out.extend_from_slice(&warp_block_ms.to_le_bytes());
            write_ids(out, moved);
            write_ids(out, held);
        }
        Action::Doomsday { delay_ms, damage, immobile_ms, no_jump_ms } => {
            out.push(14);
            for number in [*delay_ms, damage[0], damage[1], damage[2], damage[3], *immobile_ms, *no_jump_ms] {
                out.extend_from_slice(&number.to_le_bytes());
            }
        }
        Action::Kamikaze { damage, range } => {
            out.push(23);
            for number in [damage[0], damage[1], damage[2], damage[3], *range] {
                out.extend_from_slice(&number.to_le_bytes());
            }
        }
        Action::Blast { centre, radius, damage } => {
            out.push(25);
            for number in
                [centre[0], centre[1], centre[2], *radius, damage[0], damage[1], damage[2], damage[3]]
            {
                out.extend_from_slice(&number.to_le_bytes());
            }
        }
        Action::Missile { damage, reach, blast } => {
            out.push(24);
            for number in [
                damage[0],
                damage[1],
                damage[2],
                damage[3],
                *reach,
                blast.explosion_radius,
                blast.explosion_velocity,
                blast.reduction_factor,
                blast.reduction_sensitivity,
            ] {
                out.extend_from_slice(&number.to_le_bytes());
            }
        }
        Action::PointDefense { range_meters, damage } => {
            out.push(30);
            out.extend_from_slice(&range_meters.to_le_bytes());
            for amount in damage {
                out.extend_from_slice(&amount.to_le_bytes());
            }
        }
        Action::Scan { cargo, reach } => {
            out.push(26);
            out.push(u8::from(!*cargo));
            out.extend_from_slice(&reach.optimal.to_le_bytes());
            out.extend_from_slice(&reach.falloff.to_le_bytes());
        }
        Action::Mining {
            family,
            mining_amount_m3,
            waste_multiplier,
            waste_probability,
            crit_probability,
            crit_multiplier,
        } => {
            out.push(7);
            out.push(match family {
                MiningFamily::Ore => 0,
                MiningFamily::Ice => 1,
                MiningFamily::Gas => 2,
            });
            for number in
                [mining_amount_m3, waste_multiplier, waste_probability, crit_probability, crit_multiplier]
            {
                out.extend_from_slice(&number.to_le_bytes());
            }
        }
        Action::Salvage { access_bonus } => {
            out.push(8);
            out.extend_from_slice(&access_bonus.to_le_bytes());
        }
        Action::Tractor { velocity, hold, range } => {
            out.push(9);
            out.extend_from_slice(&velocity.to_le_bytes());
            out.extend_from_slice(&hold.to_le_bytes());
            out.extend_from_slice(&range.to_le_bytes());
        }
        Action::PassiveLock => out.push(10),
        Action::AutoTarget { range } => {
            out.push(11);
            out.extend_from_slice(&range.to_le_bytes());
        }
        Action::StructureBurst { range, duration_ms, what } => {
            out.push(32);
            out.extend_from_slice(&range.to_le_bytes());
            out.extend_from_slice(&duration_ms.to_le_bytes());
            put_remote_what(out, what);
        }
        Action::Remote { what, reach, when, jam } => {
            out.push(1);
            out.push(when_byte(when));
            out.extend_from_slice(&reach.optimal.to_le_bytes());
            out.extend_from_slice(&reach.falloff.to_le_bytes());
            put_text(out, jam);
            put_remote_what(out, what);
        }
    }
}

/// The kind and numbers of a remote effect as the remote action carries them (and a structure burst, tag 32).
fn put_remote_what(out: &mut Vec<u8>, what: &Remote) {
    let layer_byte = |layer: &Layer| match layer {
        Layer::Shield => 0,
        Layer::Armor => 1,
        Layer::Structure => 2,
    };
    match what {
        Remote::Repair { layer, amount } => {
            out.push(0);
            out.push(layer_byte(layer));
            out.extend_from_slice(&amount.to_le_bytes());
        }
        Remote::MutadaptiveRepair { layer, amount, bonus_per_cycle, bonus_max } => {
            out.push(11);
            out.push(layer_byte(layer));
            out.extend_from_slice(&amount.to_le_bytes());
            out.extend_from_slice(&bonus_per_cycle.to_le_bytes());
            out.extend_from_slice(&bonus_max.to_le_bytes());
        }
        Remote::Transfer { amount } => {
            out.push(1);
            out.extend_from_slice(&amount.to_le_bytes());
        }
        Remote::Neutralize { amount, signature_resolution } => {
            out.push(2);
            out.extend_from_slice(&amount.to_le_bytes());
            out.extend_from_slice(&signature_resolution.to_le_bytes());
        }
        Remote::Drain { amount, signature_resolution } => {
            out.push(3);
            out.extend_from_slice(&amount.to_le_bytes());
            out.extend_from_slice(&signature_resolution.to_le_bytes());
        }
        Remote::Web { percent } => {
            out.push(4);
            out.extend_from_slice(&percent.to_le_bytes());
        }
        Remote::Paint { percent } => {
            out.push(5);
            out.extend_from_slice(&percent.to_le_bytes());
        }
        Remote::Damp {
            range_percent,
            resolution_percent,
            gravimetric_percent,
            ladar_percent,
            magnetometric_percent,
            radar_percent,
        } => {
            out.push(6);
            out.extend_from_slice(&range_percent.to_le_bytes());
            out.extend_from_slice(&resolution_percent.to_le_bytes());
            out.extend_from_slice(&gravimetric_percent.to_le_bytes());
            out.extend_from_slice(&ladar_percent.to_le_bytes());
            out.extend_from_slice(&magnetometric_percent.to_le_bytes());
            out.extend_from_slice(&radar_percent.to_le_bytes());
        }
        Remote::Scramble { strength, blocks_mwd } => {
            out.push(7);
            out.extend_from_slice(&strength.to_le_bytes());
            out.push(u8::from(*blocks_mwd));
        }
        Remote::Tackle { strength, web_percent } => {
            out.push(12);
            out.extend_from_slice(&strength.to_le_bytes());
            out.extend_from_slice(&web_percent.to_le_bytes());
        }
        Remote::Disrupt { range_percent, falloff_percent, tracking_percent } => {
            out.push(9);
            out.extend_from_slice(&range_percent.to_le_bytes());
            out.extend_from_slice(&falloff_percent.to_le_bytes());
            out.extend_from_slice(&tracking_percent.to_le_bytes());
        }
        Remote::GuidanceDisrupt {
            velocity_percent,
            delay_percent,
            aoe_velocity_percent,
            aoe_cloud_percent,
        } => {
            out.push(10);
            out.extend_from_slice(&velocity_percent.to_le_bytes());
            out.extend_from_slice(&delay_percent.to_le_bytes());
            out.extend_from_slice(&aoe_velocity_percent.to_le_bytes());
            out.extend_from_slice(&aoe_cloud_percent.to_le_bytes());
        }
        Remote::Jam { strength } => {
            out.push(8);
            for number in strength {
                out.extend_from_slice(&number.to_le_bytes());
            }
        }
    }
}
