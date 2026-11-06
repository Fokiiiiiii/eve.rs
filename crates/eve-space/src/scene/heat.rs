use super::*;

impl Scene {
    pub fn set_heat_profile(
        &mut self,
        session: SessionId,
        mut profile: HeatProfile,
        now: i64,
    ) -> Result<(), HeatError> {
        let pilot = self.pilots.get_mut(&session).ok_or(HeatError::UnknownPilot)?;
        validate_heat_profile(&pilot.ship.spec, &profile)?;
        if pilot.heat.profile.is_none() {
            pilot.heat.levels = profile.initial_rack_heat;
        }

        let mut modules = BTreeMap::new();
        for module in std::mem::take(&mut profile.modules) {
            let module_id = module.module_id;
            let previous = pilot.heat.modules.get(&module_id);
            let damaged = module.damage_hp >= module.structure_hp;
            let available = module.overloadable && module.online && !damaged;
            let mut runtime = HeatModuleRuntime {
                profile: module,
                overloaded: previous.is_some_and(|old| old.overloaded) && available,
                pending_overload: previous.and_then(|old| old.pending_overload),
                cycle_start: previous.and_then(|old| old.cycle_start),
                next_cycle_at: previous.and_then(|old| old.next_cycle_at),
                completed_cycles: previous.map_or(0, |old| old.completed_cycles),
                was_running: previous.is_some_and(|old| old.was_running),
            };
            if !available {
                runtime.pending_overload = None;
            }
            modules.insert(module_id, runtime);
        }

        pilot.heat.profile = Some(profile);
        pilot.heat.modules = modules;
        pilot.heat.updated_at.get_or_insert(now);
        pilot.heat.force_notify |= pilot.heat.levels != pilot.heat.notified_levels;
        Ok(())
    }

    /// Request module overheat. A running module applies the change at its next cycle boundary;
    /// an idle module can be primed for its first cycle.
    pub fn set_module_overheat(
        &mut self,
        session: SessionId,
        module_id: i64,
        active: bool,
    ) -> Result<(), HeatError> {
        let pilot = self.pilots.get_mut(&session).ok_or(HeatError::UnknownPilot)?;
        let profile = pilot.heat.profile.as_ref().ok_or(HeatError::ProfileNotConfigured)?;
        let runtime = pilot.heat.modules.get_mut(&module_id).ok_or(HeatError::UnknownModule)?;
        if active && profile.thermodynamics_level == 0 {
            return Err(HeatError::ThermodynamicsRequired);
        }
        if active && !runtime.profile.overloadable {
            return Err(HeatError::ModuleNotOverloadable);
        }
        if active && !runtime.profile.online {
            return Err(HeatError::ModuleOffline);
        }
        if active && runtime.profile.damage_hp >= runtime.profile.structure_hp {
            return Err(HeatError::ModuleBurnedOut);
        }

        let cycling = pilot.modules.running_effects().any(|(effect, _)| effect.module_id == module_id);
        runtime.request_overheat(active, cycling);
        pilot.heat.force_notify = true;
        Ok(())
    }

    /// Request overheat for all overloadable modules in one rack.
    pub fn set_rack_overheat(
        &mut self,
        session: SessionId,
        rack: HeatRack,
        active: bool,
    ) -> Result<Vec<i64>, HeatError> {
        let pilot = self.pilots.get(&session).ok_or(HeatError::UnknownPilot)?;
        if pilot.heat.profile.is_none() {
            return Err(HeatError::ProfileNotConfigured);
        }
        let module_ids: Vec<i64> = pilot
            .heat
            .modules
            .iter()
            .filter_map(|(module_id, module)| {
                let in_rack = heat_rack_slot(module.profile.flag).is_some_and(|(which, _)| which == rack);
                let wanted = if active {
                    module.profile.overloadable
                        && module.profile.online
                        && module.profile.damage_hp < module.profile.structure_hp
                } else {
                    module.overloaded || module.pending_overload == Some(true)
                };
                (in_rack && wanted).then_some(*module_id)
            })
            .collect();

        for module_id in &module_ids {
            self.set_module_overheat(session, *module_id, active)?;
        }
        Ok(module_ids)
    }

    /// The current rack heat, in Dogma heat units (normally 0 through 100).
    pub fn rack_heat_level(&self, session: SessionId, rack: HeatRack) -> Option<f64> {
        let pilot = self.pilots.get(&session)?;
        pilot.heat.profile.as_ref()?;
        Some(pilot.heat.levels[rack.index()])
    }

    pub(super) fn prepare_overheating(&mut self, now: i64) -> HeatSettlement {
        let sessions: Vec<SessionId> = self.pilots.keys().copied().collect();
        let mut settlement = HeatSettlement::default();
        for session in sessions {
            let Some(pilot) = self.pilots.get_mut(&session) else { continue };
            let mut one = prepare_pilot_heat(pilot, session, now);
            settlement.attempts.append(&mut one.attempts);
            settlement.notifications.append(&mut one.notifications);
        }
        settlement
    }

    /// Apply the completed-cycle rolls after module actions, notify Dogma, then stop burned-out modules.
    pub(super) fn apply_overheating(&mut self, mut settlement: HeatSettlement, now: i64) -> Vec<Out> {
        let mut changes = BTreeMap::<SessionId, Vec<Value>>::new();
        for (session, args) in settlement.notifications.drain(..) {
            changes.entry(session).or_default().extend(args);
        }
        let mut burned = BTreeSet::<(SessionId, i64)>::new();

        for attempt in settlement.attempts {
            for (module_id, chance) in attempt.targets {
                let eligible = self.pilots.get(&attempt.session).is_some_and(|pilot| {
                    let source_alive = pilot
                        .heat
                        .modules
                        .get(&attempt.source_module_id)
                        .is_some_and(|source| source.profile.damage_hp < source.profile.structure_hp);
                    let target_alive = pilot.heat.modules.get(&module_id).is_some_and(|target| {
                        !target.profile.heat_immune && target.profile.damage_hp < target.profile.structure_hp
                    });
                    source_alive && target_alive && chance > 0.0
                });
                if !eligible {
                    continue;
                }
                if self.rng.random() >= chance {
                    continue;
                }

                let Some(pilot) = self.pilots.get_mut(&attempt.session) else { continue };
                let Some(target) = pilot.heat.modules.get_mut(&module_id) else { continue };
                let previous = target.profile.damage_hp;
                let (next, burned_out) =
                    heat_damage_after(previous, target.profile.structure_hp, attempt.damage_hp);
                if next <= previous {
                    continue;
                }
                target.profile.damage_hp = next;
                if burned_out {
                    target.profile.online = false;
                    target.overloaded = false;
                    target.pending_overload = None;
                    burned.insert((attempt.session, module_id));
                }
                let line = crate::modules::attribute_change(
                    pilot.ship.spec.character_id,
                    module_id,
                    HEAT_ATTRIBUTE_ITEM_DAMAGE,
                    attempt.at,
                    real(next),
                    real(previous),
                );
                changes.entry(attempt.session).or_default().push(line);
            }
        }

        let mut told = Vec::new();
        for (session, lines) in changes {
            if !lines.is_empty() {
                told.push(Out::Notify {
                    session,
                    name: "OnModuleAttributeChanges",
                    id_type: "clientID",
                    args: vec![Value::List(lines)],
                });
            }
        }

        for (session, module_id) in burned {
            told.extend(self.stop_module(session, module_id, now));
        }
        told
    }

    pub(super) fn next_heat_cycle_boundary(&self, now: i64) -> Option<i64> {
        self.pilots
            .values()
            .flat_map(|pilot| {
                pilot.modules.running_effects().filter_map(move |(effect, cycle_start)| {
                    let module = pilot.heat.modules.get(&effect.module_id)?;
                    if !module.overloaded && module.pending_overload.is_none() {
                        return None;
                    }
                    heat_duration_ticks(effect.duration_ms)
                        .and_then(|duration| cycle_start.checked_add(duration))
                })
            })
            .filter(|at| *at <= now)
            .min()
    }
}

impl HeatModuleRuntime {
    pub(super) fn request_overheat(&mut self, active: bool, cycling: bool) {
        if cycling {
            self.pending_overload = (self.overloaded != active).then_some(active);
        } else {
            self.overloaded = active;
            self.pending_overload = None;
        }
    }

    pub(super) fn settle_cycle_boundary(&mut self) {
        if let Some(active) = self.pending_overload.take() {
            self.overloaded = active;
        }
    }
}

pub(super) fn heat_rack_slot(flag: i32) -> Option<(HeatRack, u8)> {
    match flag {
        27..=34 => Some((HeatRack::High, (flag - 27) as u8)),
        19..=26 => Some((HeatRack::Medium, (flag - 19) as u8)),
        11..=18 => Some((HeatRack::Low, (flag - 11) as u8)),
        _ => None,
    }
}

pub(super) fn validate_heat_profile(spec: &ShipSpec, profile: &HeatProfile) -> Result<(), HeatError> {
    if !profile.generation_multiplier.is_finite() || profile.generation_multiplier < 0.0 {
        return Err(HeatError::InvalidProfile("generation multiplier"));
    }
    if !profile.damage_multiplier.is_finite() || profile.damage_multiplier < 0.0 {
        return Err(HeatError::InvalidProfile("damage multiplier"));
    }
    if profile.thermodynamics_level > 5 {
        return Err(HeatError::InvalidProfile("Thermodynamics level"));
    }
    for rack in HeatRack::ALL {
        let index = rack.index();
        if !profile.capacity[index].is_finite() || profile.capacity[index] <= 0.0 {
            return Err(HeatError::InvalidProfile("rack capacity"));
        }
        if !profile.initial_rack_heat[index].is_finite()
            || !(0.0..=profile.capacity[index]).contains(&profile.initial_rack_heat[index])
        {
            return Err(HeatError::InvalidProfile("initial rack heat"));
        }
        if !profile.dissipation_rate[index].is_finite() || profile.dissipation_rate[index] < 0.0 {
            return Err(HeatError::InvalidProfile("rack dissipation rate"));
        }
        if !profile.attenuation[index].is_finite() || !(0.0..=1.0).contains(&profile.attenuation[index]) {
            return Err(HeatError::InvalidProfile("rack attenuation"));
        }
    }

    let mut module_ids = BTreeSet::new();
    let mut slot_flags = BTreeSet::new();
    for module in &profile.modules {
        if module.module_id <= 0 || !module_ids.insert(module.module_id) {
            return Err(HeatError::InvalidProfile("duplicate or invalid module id"));
        }
        if heat_rack_slot(module.flag).is_none() || !slot_flags.insert(module.flag) {
            return Err(HeatError::InvalidProfile("duplicate or invalid rack slot"));
        }
        if !spec.modules.iter().any(|fitted| fitted.item_id == module.module_id && fitted.flag == module.flag)
        {
            return Err(HeatError::InvalidProfile("module does not match ship fitting"));
        }
        if !module.structure_hp.is_finite() || module.structure_hp <= 0.0 {
            return Err(HeatError::InvalidProfile("module structure HP"));
        }
        if !module.damage_hp.is_finite() || !(0.0..=module.structure_hp).contains(&module.damage_hp) {
            return Err(HeatError::InvalidProfile("module damage"));
        }
        if !module.heat_damage_hp.is_finite() || module.heat_damage_hp < 0.0 {
            return Err(HeatError::InvalidProfile("module heat damage"));
        }
        if !module.heat_absorption_rate.is_finite() || module.heat_absorption_rate < 0.0 {
            return Err(HeatError::InvalidProfile("module heat absorption"));
        }
    }

    for fitted in &spec.modules {
        if heat_rack_slot(fitted.flag).is_some() && !module_ids.contains(&fitted.item_id) {
            return Err(HeatError::InvalidProfile("missing fitted module profile"));
        }
    }
    Ok(())
}

pub(super) fn heat_duration_ticks(duration_ms: f64) -> Option<i64> {
    if !duration_ms.is_finite() || duration_ms <= 0.0 {
        return None;
    }
    let ticks = duration_ms * (TICKS_PER_SECOND as f64 / 1_000.0);
    (ticks <= i64::MAX as f64).then_some((ticks as i64).max(1))
}

pub(super) fn heat_damage_after(previous: f64, structure_hp: f64, damage_hp: f64) -> (f64, bool) {
    if !previous.is_finite() || !structure_hp.is_finite() || structure_hp <= 0.0 || !damage_hp.is_finite() {
        return (previous.max(0.0), false);
    }
    let previous = previous.clamp(0.0, structure_hp);
    let next = (previous + damage_hp.max(0.0)).clamp(0.0, structure_hp);
    (next, next >= structure_hp - 1e-9)
}

pub(super) fn heat_damage_chance(heat: f64, capacity: f64, attenuation: f64, distance: u8) -> f64 {
    if !heat.is_finite() || !capacity.is_finite() || capacity <= 0.0 {
        return 0.0;
    }
    let fraction = (heat / capacity).clamp(0.0, 1.0);
    let falloff = attenuation.clamp(0.0, 1.0).powi(i32::from(distance));
    (fraction * falloff).clamp(0.0, 1.0)
}

pub(super) fn rack_heat_after(
    previous: f64,
    capacity: f64,
    incoming_rate: f64,
    generation_multiplier: f64,
    dissipation_rate: f64,
    delta_seconds: f64,
) -> f64 {
    if !capacity.is_finite() || capacity <= 0.0 || !delta_seconds.is_finite() || delta_seconds <= 0.0 {
        return previous.max(0.0);
    }
    let previous = previous.clamp(0.0, capacity);
    let next = if incoming_rate > 0.0 && generation_multiplier > 0.0 {
        capacity - (capacity - previous) * (-(incoming_rate * generation_multiplier * delta_seconds)).exp()
    } else if dissipation_rate > 0.0 {
        previous * (-(dissipation_rate * delta_seconds)).exp()
    } else {
        previous
    };
    next.clamp(0.0, capacity)
}

pub(super) fn prepare_pilot_heat(pilot: &mut Pilot, session: SessionId, now: i64) -> HeatSettlement {
    let mut settlement = HeatSettlement::default();
    if pilot.heat.profile.is_none() {
        return settlement;
    }

    let active: BTreeMap<i64, ActiveHeatCycle> = pilot
        .modules
        .running_effects()
        .filter_map(|(effect, cycle_start)| {
            Some((
                effect.module_id,
                ActiveHeatCycle {
                    cycle_start,
                    duration_ticks: heat_duration_ticks(effect.duration_ms)?,
                    repeat: effect.repeat,
                },
            ))
        })
        .collect();
    let active_ids: BTreeSet<i64> = active.keys().copied().collect();

    for (module_id, runtime) in &mut pilot.heat.modules {
        if !active_ids.contains(module_id) {
            runtime.was_running = false;
            runtime.cycle_start = None;
            runtime.next_cycle_at = None;
            runtime.completed_cycles = 0;
            runtime.settle_cycle_boundary();
        }
    }

    for (module_id, cycle) in &active {
        let Some(runtime) = pilot.heat.modules.get_mut(module_id) else { continue };
        if !runtime.profile.online || runtime.profile.damage_hp >= runtime.profile.structure_hp {
            runtime.overloaded = false;
            runtime.pending_overload = None;
            continue;
        }
        let start = cycle.cycle_start;
        if !runtime.was_running {
            runtime.was_running = true;
            runtime.cycle_start = Some(start);
            runtime.next_cycle_at = start.checked_add(cycle.duration_ticks);
            runtime.completed_cycles = 0;
        } else if runtime.cycle_start != Some(start) {
            runtime.cycle_start = Some(start);
            runtime.next_cycle_at = start.checked_add(cycle.duration_ticks);
        }
    }

    let mut cycling = active_ids;
    loop {
        let next = cycling
            .iter()
            .filter_map(|module_id| {
                pilot
                    .heat
                    .modules
                    .get(module_id)
                    .and_then(|runtime| runtime.next_cycle_at.map(|at| (at, *module_id)))
            })
            .filter(|(at, _)| *at <= now)
            .min();
        let Some((at, source_module_id)) = next else { break };

        advance_rack_heat(&mut pilot.heat, &cycling, at);
        let source_snapshot = pilot.heat.modules.get(&source_module_id).cloned();
        if let Some(source) = source_snapshot
            && source.overloaded
            && source.profile.overloadable
            && source.profile.heat_damage_hp > 0.0
            && source.profile.damage_hp < source.profile.structure_hp
            && let Some((rack, source_slot)) = heat_rack_slot(source.profile.flag)
        {
            let profile = pilot.heat.profile.as_ref().expect("heat profile checked above");
            let skill_multiplier = (1.0 - 0.05 * f64::from(profile.thermodynamics_level)).max(0.0);
            let damage_hp = source.profile.heat_damage_hp * skill_multiplier * profile.damage_multiplier;
            let index = rack.index();
            let heat = pilot.heat.levels[index];
            let capacity = profile.capacity[index];
            let attenuation = profile.attenuation[index];
            let mut candidates: Vec<(i32, i64, f64)> = pilot
                .heat
                .modules
                .iter()
                .filter_map(|(module_id, candidate)| {
                    if candidate.profile.heat_immune
                        || candidate.profile.damage_hp >= candidate.profile.structure_hp
                    {
                        return None;
                    }
                    let (candidate_rack, candidate_slot) = heat_rack_slot(candidate.profile.flag)?;
                    if candidate_rack != rack {
                        return None;
                    }
                    let distance = source_slot.abs_diff(candidate_slot);
                    let chance = heat_damage_chance(heat, capacity, attenuation, distance);
                    (chance > 0.0).then_some((candidate.profile.flag, *module_id, chance))
                })
                .collect();
            candidates.sort_by_key(|(flag, _, _)| *flag);
            if damage_hp > 0.0 {
                settlement.attempts.push(HeatDamageAttempt {
                    session,
                    source_module_id,
                    at,
                    damage_hp,
                    targets: candidates
                        .into_iter()
                        .map(|(_, module_id, chance)| (module_id, chance))
                        .collect(),
                });
            }
        }

        if let Some(runtime) = pilot.heat.modules.get_mut(&source_module_id) {
            runtime.completed_cycles = runtime.completed_cycles.saturating_add(1);
            runtime.cycle_start = Some(at);
            runtime.next_cycle_at = at.checked_add(active[&source_module_id].duration_ticks);
            runtime.settle_cycle_boundary();
            if active[&source_module_id].repeat > 0
                && runtime.completed_cycles >= active[&source_module_id].repeat
            {
                runtime.was_running = false;
                runtime.cycle_start = None;
                runtime.next_cycle_at = None;
                cycling.remove(&source_module_id);
            }
        }
    }

    advance_rack_heat(&mut pilot.heat, &cycling, now);
    let gauge_lines = heat_gauge_changes(pilot, session, now);
    if !gauge_lines.is_empty() {
        settlement.notifications.push((session, gauge_lines));
    }
    settlement
}

pub(super) fn advance_rack_heat(state: &mut HeatState, cycling: &BTreeSet<i64>, now: i64) {
    let Some(profile) = state.profile.as_ref() else { return };
    let Some(previous_at) = state.updated_at else {
        state.updated_at = Some(now);
        return;
    };
    if now <= previous_at {
        return;
    }
    state.updated_at = Some(now);
    let delta_seconds =
        (((now - previous_at) as f64) / TICKS_PER_SECOND as f64).min(HEAT_MAX_ADVANCE_SECONDS);

    let mut incoming = [0.0; 3];
    for module_id in cycling {
        let Some(module) = state.modules.get(module_id) else { continue };
        if !module.overloaded
            || !module.profile.online
            || module.profile.damage_hp >= module.profile.structure_hp
        {
            continue;
        }
        let Some((rack, _)) = heat_rack_slot(module.profile.flag) else { continue };
        incoming[rack.index()] += module.profile.heat_absorption_rate;
    }

    let generation = profile.generation_multiplier;
    let capacities = profile.capacity;
    let dissipation = profile.dissipation_rate;
    for rack in HeatRack::ALL {
        let index = rack.index();
        let next = rack_heat_after(
            state.levels[index],
            capacities[index],
            incoming[index],
            generation,
            dissipation[index],
            delta_seconds,
        );
        state.levels[index] = if next < HEAT_ZERO_EPSILON { 0.0 } else { next };
    }
}

pub(super) fn heat_gauge_changes(pilot: &mut Pilot, session: SessionId, now: i64) -> Vec<Value> {
    if !pilot.bound {
        return Vec::new();
    }
    let interval_due =
        pilot.heat.last_notify_at.is_none_or(|last| now.saturating_sub(last) >= HEAT_NOTIFY_INTERVAL);
    let force = pilot.heat.force_notify;
    let mut lines = Vec::new();
    for rack in HeatRack::ALL {
        let index = rack.index();
        let level = pilot.heat.levels[index];
        let notified = pilot.heat.notified_levels[index];
        let delta = (level - notified).abs();
        let due = force
            || (level <= 0.0 && notified > 0.0)
            || delta >= HEAT_NOTIFY_MIN_DELTA
            || (interval_due && delta >= HEAT_NOTIFY_INTERVAL_MIN_DELTA);
        if !due {
            continue;
        }
        pilot.heat.notified_levels[index] = level;
        lines.push(crate::modules::attribute_change(
            pilot.ship.spec.character_id,
            pilot.ship.spec.ship_id,
            rack.attribute_id(),
            now,
            real((level * 1_000.0).round() / 1_000.0),
            Value::None,
        ));
    }
    if !lines.is_empty() {
        pilot.heat.last_notify_at = Some(now);
    }
    pilot.heat.force_notify = false;
    let _ = session;
    lines
}
