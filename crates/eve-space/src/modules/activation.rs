use super::*;
use crate::missile_entity::AUTO_TARGET_ID;

impl Scene {
    pub(crate) fn dogma_notify(session: SessionId, name: &'static str, args: Vec<Value>) -> Out {
        Out::Notify { session, name, id_type: "clientID", args }
    }

    /// The capacitor's charge, told: `(the ship's charge now, and a tenth of a second before)`.
    pub(crate) fn charge_told(
        session: SessionId,
        owner: i64,
        ship: i64,
        when: i64,
        new: f64,
        old: f64,
    ) -> Out {
        Self::dogma_notify(
            session,
            "OnModuleAttributeChanges",
            vec![Value::List(vec![attribute_change(
                owner,
                ship,
                CAPACITOR_CHARGE,
                when,
                number(new),
                number(old),
            )])],
        )
    }

    /// The values an effect changes, told to the pilot. A launcher's cycle is told as a real whatever its value (the recording of the reference's launcher: `4900.0`).
    pub(super) fn changes_told(
        session: SessionId,
        owner: i64,
        effect: &Effect,
        changes: &[Change],
        when: i64,
    ) -> Option<Out> {
        let launcher = effect.turret.as_ref().is_some_and(|turret| turret.missile.is_some());
        if changes.is_empty() {
            return None;
        }
        let lines: Vec<Value> = changes
            .iter()
            .map(|c| {
                let value = if c.attribute_id == DURATION || (launcher && c.attribute_id == CYCLE) {
                    real(c.value)
                } else {
                    number(c.value)
                };
                attribute_change(owner, c.item_id, c.attribute_id, when, value, Value::None)
            })
            .collect();
        Some(Self::dogma_notify(session, "OnModuleAttributeChanges", vec![Value::List(lines)]))
    }

    /// `OnGodmaShipEffect`: an effect went on (`start` and `active`) or came to an end, or began another cycle.
    #[allow(clippy::too_many_arguments)] // the arguments are the fields of the tuple the client reads
    pub(super) fn effect_told(
        session: SessionId,
        owner: i64,
        ship: i64,
        run: &Running,
        when: i64,
        on: bool,
        started: i64,
        error: Option<Value>,
    ) -> Out {
        let effect = &run.effect;
        let environment = tuple([
            id(effect.module_id),
            id(owner),
            id(ship),
            if effect.target_id > 0 { id(effect.target_id) } else { Value::None },
            effect.turret.as_ref().and_then(|t| t.charge).map_or(Value::None, |c| charge_key(ship, &c)),
            tuple([]),
            int(i64::from(effect.effect_id)),
            Value::None,
        ]);
        Self::dogma_notify(
            session,
            "OnGodmaShipEffect",
            vec![
                id(effect.module_id),
                int(i64::from(effect.effect_id)),
                filetime(when),
                int(i64::from(on)),
                int(i64::from(on)),
                environment,
                filetime(started),
                real(effect.duration_ms),
                // an effect that ends for want of rounds says so (and says it repeats)
                if on { int(i64::from(run.effect.repeat)) } else { Value::Bool(error.is_some()) },
                error.unwrap_or(Value::None),
            ],
        )
    }

    /// The action the client plays on the ship for the effect: `OnSpecialFX(ship, module, module type, target, charge, effect, offensive, start, active, duration, …)`.
    pub(super) fn fx_action(stamp: u32, ship: &Value, effect: &Effect, on: bool) -> Value {
        payload::action(
            stamp,
            "OnSpecialFX",
            [
                ship.clone(),
                id(effect.module_id),
                int(i64::from(effect.module_type)),
                if effect.target_id > 0 { id(effect.target_id) } else { Value::None },
                effect
                    .turret
                    .as_ref()
                    .and_then(|t| t.charge)
                    .map_or(Value::None, |c| int(i64::from(c.type_id))),
                crate::wire::text(effect.fx.clone()),
                int(i64::from(if on { effect.offensive() } else { effect.turret.is_some() })),
                int(i64::from(on)),
                int(i64::from(on)),
                number(effect.duration_ms),
                // a turret's start says how many cycles were asked for, its stop says nothing (the recording of the reference's gun)
                if on { effect.fx_repeat().map_or(Value::None, int) } else { Value::None },
                Value::None,
                int(0),
                Value::None,
            ],
        )
    }

    /// The ship's numbers become `numbers`; what the client is told: `SetBallMass` and `SetMaxSpeed` (the ones that changed).
    pub(super) fn renumber(&mut self, session: SessionId, numbers: Numbers, stamp: u32) -> Vec<Value> {
        let Some(pilot) = self.pilots.get_mut(&session) else { return Vec::new() };
        // what other ships' modules do to this ship is worked on the numbers its own effects give it
        let held = pilot.incoming.base.map(|mut base| {
            base.max_velocity = numbers.max_velocity;
            base.signature_radius = numbers.signature_radius;
            pilot.incoming.base = Some(base);
            pilot.incoming.worked(&base)
        });
        let wanted_velocity = held.map_or(numbers.max_velocity, |worked| worked.max_velocity);
        let wanted_signature = held.map_or(numbers.signature_radius, |worked| worked.signature_radius);
        let spec = &mut pilot.ship.spec;
        let ship = id(spec.ship_id);
        let mut actions = Vec::new();
        if numbers.mass != spec.mass {
            spec.mass = numbers.mass;
            actions.push(payload::action(stamp, "SetBallMass", [ship.clone(), real(numbers.mass)]));
        }
        if wanted_velocity != spec.max_velocity {
            spec.max_velocity = wanted_velocity;
            actions.push(payload::action(stamp, "SetMaxSpeed", [ship, real(wanted_velocity)]));
        }
        spec.signature_radius = wanted_signature;
        actions
    }

    /// The host asks for an effect to run. Answered with `Accepted` (code 17) or `Refused`: `ALREADY_ACTIVE`, `NOT_ENOUGH_CAPACITOR`.
    pub fn activate(&mut self, session: SessionId, effect: Effect, now: i64) -> Vec<Out> {
        const CODE: u8 = 17;
        if !self.pilots.contains_key(&session) {
            return vec![Out::Refused { session, command: CODE, reason: "NO_SHIP_IN_SPACE" }];
        }
        if !effect.duration_ms.is_finite() || effect.duration_ms < 1.0 || effect.repeat < 1 {
            return vec![Out::Refused { session, command: CODE, reason: "BAD_EFFECT" }];
        }
        if effect.action.as_ref().is_some_and(Action::not_implemented) {
            return vec![Out::Refused { session, command: CODE, reason: "NOT_IMPLEMENTED" }];
        }
        self.settle_all(now);
        let pilot = &self.pilots[&session];
        if pilot.modules.running.iter().any(|r| r.effect.module_id == effect.module_id)
            || pilot.modules.pending_starts.iter().any(|start| start.effect.module_id == effect.module_id)
        {
            return vec![Out::Refused { session, command: CODE, reason: "ALREADY_ACTIVE" }];
        }
        // the reactive armor hardeners are one group, of which one runs at a time: a second one is refused (its resonances would not compose)
        if matches!(effect.action, Some(Action::ResistanceShift { .. }))
            && pilot
                .modules
                .running
                .iter()
                .any(|r| matches!(r.effect.action, Some(Action::ResistanceShift { .. })))
        {
            return vec![Out::Refused { session, command: CODE, reason: "MAX_GROUP_ACTIVE" }];
        }
        // a module of a group of which a limited number run at once (maxGroupActive 763: the afterburner and the microwarpdrive) is refused while that many
        // of its group run or are starting (EveJS MAX_GROUP_ACTIVE; the client's EffectCrowdedOut is shown as the notice, the count is not carried yet)
        if effect.rules.max_group_active > 0 && effect.rules.group_id != 0 {
            let same_group = pilot
                .modules
                .running
                .iter()
                .filter(|r| r.effect.rules.group_id == effect.rules.group_id)
                .count()
                + pilot
                    .modules
                    .pending_starts
                    .iter()
                    .filter(|s| s.effect.rules.group_id == effect.rules.group_id)
                    .count();
            if same_group as i64 >= i64::from(effect.rules.max_group_active) {
                return vec![Out::Refused { session, command: CODE, reason: "MAX_GROUP_ACTIVE" }];
            }
        }
        // a compressor (activationRequiresActiveIndustrialCore 3265) runs only while an industrial core runs on the ship
        if effect.rules.requires_industrial_core
            && !pilot.modules.running.iter().any(|r| r.effect.rules.provides_industrial_core)
        {
            return vec![Out::Refused { session, command: CODE, reason: "ACTIVE_INDUSTRIAL_CORE_REQUIRED" }];
        }
        if let Some(Action::MicroJumpDrive { distance_meters, cooldown_ms }) = effect.action.as_ref() {
            if !distance_meters.is_finite()
                || *distance_meters <= 0.0
                || *cooldown_ms <= 0
                || effect.repeat != 1
            {
                return vec![Out::Refused { session, command: CODE, reason: "BAD_MJD" }];
            }
            if pilot
                .modules
                .reactivation
                .iter()
                .any(|(module_id, until)| *module_id == effect.module_id && *until > now)
            {
                return vec![Out::Refused { session, command: CODE, reason: "MJD_COOLDOWN" }];
            }
        }
        // a module that waits after it stopped (its reactivation delay, 669) does not start again before the time has passed
        if !matches!(effect.action, Some(Action::MicroJumpDrive { .. }))
            && pilot
                .modules
                .reactivation
                .iter()
                .any(|(module_id, until)| *module_id == effect.module_id && *until > now)
        {
            return vec![Out::Refused { session, command: CODE, reason: "MODULE_REACTIVATING" }];
        }
        // a smartbomb that the safety forbids does not start with another pilot in its range
        if let Some(Action::Smartbomb { range, hurts_pilots: false, .. }) = effect.action.as_ref()
            && !self
                .in_smartbomb_range(session, *range)
                .iter()
                .all(|(other, _)| crate::npc::is_npc_session(*other))
        {
            return vec![Out::Refused { session, command: CODE, reason: "SafetyActivated" }];
        }
        // a warp scrambler that blocks a microwarpdrive keeps one from being started
        // a module burnt out by its own effect (an emergency hull energizer) does not start again (MODULE_INCAPACITATED)
        if pilot.modules.burnt_out.contains(&effect.module_id) {
            return vec![Out::Refused { session, command: CODE, reason: "MODULE_INCAPACITATED" }];
        }
        // a module that a running one blocks (activationBlocked 1349, from a sensor array on the capital micro jump drives) does not start
        if self.overlay(session, effect.module_id, ACTIVATION_BLOCKED) > 0.0 {
            return vec![Out::Refused { session, command: CODE, reason: "MODULE_BLOCKED" }];
        }
        if effect.effect_id == MICROWARPDRIVE && pilot.incoming.mwd_blocked() {
            return vec![Out::Refused { session, command: CODE, reason: "MICROWARPDRIVE_BLOCKED" }];
        }
        if let Some(Action::Strike { strike }) = effect.action.as_ref() {
            if !strike.is_valid() {
                return vec![Out::Refused { session, command: CODE, reason: "BAD_STRIKE" }];
            }
            if pilot.flight.as_ref().is_some_and(|flight| flight.is_warping()) {
                return vec![Out::Refused { session, command: CODE, reason: "CANNOT_ACTIVATE_IN_WARP" }];
            }
        }
        if let Some(Action::Tractor { range, .. }) = effect.action.as_ref() {
            if effect.target_id <= 0 {
                return vec![Out::Refused { session, command: CODE, reason: "TARGET_REQUIRED" }];
            }
            // a beam pulls a wreck or a can (EVE.js: anything else is TARGET_NOT_FOUND); the pilot must hold its lock, and it must be in reach
            let Some(wreck) = self
                .wrecks
                .get(&effect.target_id)
                .filter(|wreck| wreck.group_id != crate::wreck::CORPSE_GROUP)
            else {
                return vec![Out::Refused { session, command: CODE, reason: "TARGET_NOT_FOUND" }];
            };
            let target_type = wreck.kind.type_id;
            if !self.locked_by(session).contains(&effect.target_id) {
                return vec![Out::Refused { session, command: CODE, reason: "TARGET_NOT_LOCKED" }];
            }
            let apart = self.surface_distance_to_wreck(session, effect.target_id).unwrap_or(f64::INFINITY);
            if apart > range + crate::tractor::REACH_GRACE_METERS {
                return vec![Out::TargetRefused {
                    session,
                    command: CODE,
                    reason: "TARGET_OUT_OF_RANGE",
                    target_type,
                    desired_range: *range,
                    actual_distance: apart,
                }];
            }
        }
        if let Some(Action::Doomsday { delay_ms, damage, immobile_ms, no_jump_ms }) = effect.action.as_ref() {
            let numbers = [*delay_ms, *immobile_ms, *no_jump_ms];
            if !numbers.iter().all(|n| n.is_finite() && *n >= 0.0)
                || !damage.iter().all(|d| d.is_finite() && *d >= 0.0)
            {
                return vec![Out::Refused { session, command: CODE, reason: "BAD_DOOMSDAY" }];
            }
            if pilot.flight.as_ref().is_some_and(|flight| flight.is_warping()) {
                return vec![Out::Refused { session, command: CODE, reason: "CANNOT_ACTIVATE_IN_WARP" }];
            }
            // the target is a locked ship of the scene (the reference's TARGET_REQUIRED, TARGET_NOT_FOUND and TARGET_NOT_LOCKED)
            if effect.target_id <= 0 {
                return vec![Out::Refused { session, command: CODE, reason: "TARGET_REQUIRED" }];
            }
            if !self.turret_target_ok(session, &effect) {
                let reason = if self.session_of(effect.target_id).is_none() {
                    "TARGET_NOT_FOUND"
                } else {
                    "TARGET_NOT_LOCKED"
                };
                return vec![Out::Refused { session, command: CODE, reason }];
            }
        }
        if matches!(effect.action.as_ref(), Some(Action::Cloak { .. })) {
            // a module that cannot be deactivated early (disallowEarlyDeactivation 906) runs: the ship does not cloak (EveJS CLOAK_MODULE_ACTIVE)
            if pilot.modules.running.iter().any(|r| r.effect.rules.disallow_early_deactivation) {
                return vec![Out::Refused { session, command: CODE, reason: "CLOAK_MODULE_ACTIVE" }];
            }
            if pilot.no_jump_until > now {
                return vec![Out::Refused { session, command: CODE, reason: "NO_JUMP_OR_CLOAK" }];
            }
            if pilot.flight.as_ref().is_some_and(|flight| flight.is_warping()) {
                return vec![Out::Refused { session, command: CODE, reason: "CLOAK_WARP_BLOCKED" }];
            }
            if !pilot.targeting.pending_targets().is_empty()
                || !pilot.targeting.targets().is_empty()
                || !self.targeters_of(pilot.ship.spec.ship_id).is_empty()
            {
                return vec![Out::Refused { session, command: CODE, reason: "CLOAK_TARGET_LOCKED" }];
            }
            if !self.in_smartbomb_range(session, 2_000.0).is_empty() {
                return vec![Out::Refused { session, command: CODE, reason: "CLOAK_PROXIMITY_BLOCKED" }];
            }
        } else if pilot.cloak != crate::visibility::CloakState::Uncloaked {
            return vec![Out::Refused { session, command: CODE, reason: "MODULE_CLOAKED" }];
        }
        if effect.turret.is_some() {
            if effect.target_id <= 0 && effect.target_id != AUTO_TARGET_ID {
                return vec![Out::Refused { session, command: CODE, reason: "NO_TARGET" }];
            }
            // a target that is not in the scene is not there (the reference answers `TARGET_NOT_FOUND`), one that is must be locked
            if effect.target_id != AUTO_TARGET_ID && self.session_of(effect.target_id).is_none() {
                return vec![Out::Refused { session, command: CODE, reason: "TARGET_NOT_FOUND" }];
            }
            if !self.turret_target_ok(session, &effect) {
                return vec![Out::Refused { session, command: CODE, reason: "TARGET_NOT_LOCKED" }];
            }
            if effect.turret.as_ref().and_then(|t| t.charge).is_some_and(|c| c.rounds < c.per_cycle) {
                return vec![Out::Refused { session, command: CODE, reason: "NO_AMMO" }];
            }
            // a missile goes only as far as it flies: a target further off is out of reach (the client is told how far the missile goes and how far the target is)
            if let Some(missile) = effect.turret.as_ref().and_then(|t| t.missile.as_ref())
                && let Some(target) = self.session_of(effect.target_id)
                && let Some(apart) = self.apart(session, target)
                && apart > missile.effective_range() + crate::weapons::MISSILE_RANGE_GRACE
            {
                return vec![Out::TargetRefused {
                    session,
                    command: CODE,
                    reason: "TARGET_OUT_OF_RANGE",
                    target_type: self.pilots[&target].ship.spec.type_id,
                    desired_range: missile.effective_range(),
                    actual_distance: apart,
                }];
            }
        }
        // an effect that is projected at a ship needs it: there, locked and in reach (what is too far off is told with the range and how far it is)
        if let Some((what, reach, _)) = effect.remote() {
            if effect.target_id <= 0 {
                return vec![Out::Refused { session, command: CODE, reason: "NO_TARGET" }];
            }
            let Some(target) = self.session_of(effect.target_id) else {
                return vec![Out::Refused { session, command: CODE, reason: "TARGET_NOT_FOUND" }];
            };
            let self_capacitor_boost = matches!(what, Remote::Transfer { .. })
                && self.pilots.get(&session).is_some_and(|pilot| effect.target_id == pilot.ship.spec.ship_id);
            let self_sensor_boost =
                self.pilots.get(&session).is_some_and(|pilot| effect.target_id == pilot.ship.spec.ship_id)
                    && effect.jam() == Some("remoteSensorBoost")
                    && matches!(
                        what,
                        Remote::Damp {
                            range_percent,
                            resolution_percent,
                            gravimetric_percent,
                            ladar_percent,
                            magnetometric_percent,
                            radar_percent,
                        } if *range_percent >= 0.0
                            && *resolution_percent >= 0.0
                            && (*range_percent > 0.0 || *resolution_percent > 0.0)
                            && *gravimetric_percent == 0.0
                            && *ladar_percent == 0.0
                            && *magnetometric_percent == 0.0
                            && *radar_percent == 0.0
                    );
            if !self_capacitor_boost
                && !self_sensor_boost
                && !self.locked_by(session).contains(&effect.target_id)
            {
                return vec![Out::Refused { session, command: CODE, reason: "TARGET_NOT_LOCKED" }];
            }
            let apart = self.apart(session, target).unwrap_or(f64::INFINITY);
            if reach.share(apart).is_none() {
                return vec![Out::TargetRefused {
                    session,
                    command: CODE,
                    reason: "TARGET_OUT_OF_RANGE",
                    target_type: self.pilots[&target].ship.spec.type_id,
                    desired_range: reach.farthest(),
                    actual_distance: apart,
                }];
            }
        }
        let pilot = &self.pilots[&session];
        let Some(capacitor) = pilot.modules.capacitor.as_ref() else {
            return vec![Out::Refused { session, command: CODE, reason: "NO_CAPACITOR" }];
        };
        if capacitor.charge_at(now) < effect.capacitor_need {
            return vec![Out::Refused { session, command: CODE, reason: "NOT_ENOUGH_CAPACITOR" }];
        }
        // an effect that burns fuel starts when the host has taken the fuel of its first cycle (`WORK_FUEL`): it is parked until the answer
        if effect.burns_fuel {
            return self.park_start(session, effect, now);
        }
        self.start_effect(session, effect, now)
    }

    /// Parks an effect that burns fuel until the host takes the fuel of its first cycle: the host is asked for it (`ModuleWorkCycle`, `WORK_FUEL`) and answers.
    pub(super) fn park_start(&mut self, session: SessionId, effect: Effect, now: i64) -> Vec<Out> {
        let cycle_id = next_work_cycle_id();
        let (module_id, target_id) = (effect.module_id, effect.target_id);
        let Some(pilot) = self.pilots.get_mut(&session) else { return Vec::new() };
        pilot.modules.pending_starts.push(PendingStart { effect, cycle_id, sent_at: now });
        self.fuel_cycle_out(session, module_id, target_id, cycle_id).into_iter().collect()
    }

    /// The effect starts now: the capacitor pays for its first cycle and the numbers, the effect and its first cycle are told. Refused when the capacitor
    /// is short (it can be short again when a parked start is answered).
    pub(super) fn start_effect(&mut self, session: SessionId, effect: Effect, now: i64) -> Vec<Out> {
        const CODE: u8 = 17;
        let Some(pilot) = self.pilots.get(&session) else {
            return vec![Out::Refused { session, command: CODE, reason: "NO_SHIP_IN_SPACE" }];
        };
        if pilot
            .modules
            .capacitor
            .as_ref()
            .is_none_or(|capacitor| capacitor.charge_at(now) < effect.capacitor_need)
        {
            return vec![Out::Refused { session, command: CODE, reason: "NOT_ENOUGH_CAPACITOR" }];
        }
        // the pilot has given up the undocking protection by flying the ship (the host tells the client)
        let (owner, ship_id) = (pilot.ship.spec.character_id, pilot.ship.spec.ship_id);
        let pilot = self.pilots.get_mut(&session).expect("the pilot is in the scene");
        pilot.protected_until = 0;
        let rounds = effect.turret.as_ref().and_then(|t| t.charge).map(|c| c.rounds).or_else(|| {
            effect.action.as_ref().and_then(Action::charge_use).map(|(_, _, _, charges)| charges)
        });
        let charged_this_cycle = rounds.is_some_and(|left| left > 0);
        let run = Running {
            effect,
            cycle_start: now,
            cycles: 1,
            strike_done: 0,
            doomsday_hit: false,
            stopping: false,
            rounds,
            charged_this_cycle,
            pending_remote_repair: None,
            pending_work: None,
            spool: SpoolState::default(),
        };
        let (before, after) = match pilot.modules.capacitor.as_mut() {
            Some(capacitor) => capacitor.draw(now, run.effect.capacitor_need),
            None => (0.0, 0.0),
        };
        // a reactive armor hardener's resonances start at its seed, before anything is told
        if let Some(Action::ResistanceShift { seed, shift, .. }) = run.effect.action.as_ref() {
            pilot.ship.health.start_shift(*seed, *shift);
        }
        let mut out = vec![Out::Accepted { session, command: CODE, at: now }];
        out.push(Self::charge_told(session, owner, ship_id, now, after, before));
        let stamp = self.next_stamp(now);
        let ship = id(ship_id);
        if let Some(numbers) = run.effect.on {
            let actions = self.renumber(session, numbers, stamp);
            if !actions.is_empty() {
                out.extend(self.tell_all(session, actions));
            }
        }
        // a turret's special effect is played before its numbers are told (the recording of the reference's gun), a propulsion module's after
        let fx = self.tell_all(session, vec![Self::fx_action(stamp, &ship, &run.effect, true)]);
        if run.effect.turret.is_some() {
            out.extend(fx);
            out.extend(Self::changes_told(session, owner, &run.effect, &run.effect.on_changes, now));
        } else {
            out.extend(Self::changes_told(session, owner, &run.effect, &run.effect.on_changes, now));
            out.extend(fx);
        }
        out.push(Self::effect_told(session, owner, ship_id, &run, now, true, now, None));
        let effect = run.effect.clone();
        if let Some(pilot) = self.pilots.get_mut(&session) {
            pilot.modules.running.push(run);
        }
        out.extend(self.act(session, &effect, Applies::CycleStart, now));
        out.extend(self.jam_told(session, &effect, now, Jam::Start));
        out.extend(self.fire_turret(session, &effect, now));
        // the round of the first shot is spent after the shot; a turret that has no round for the next cycle stops
        let (spent, goes_on) = self.spend_round(session, effect.module_id, now);
        out.extend(spent);
        let stop_without_charge =
            matches!(effect.action.as_ref(), Some(Action::Charged { fallback: None, .. }));
        if !goes_on
            && (effect.turret.is_some() || stop_without_charge)
            && let Some(pilot) = self.pilots.get_mut(&session)
            && let Some(at) =
                pilot.modules.running.iter().position(|r| r.effect.module_id == effect.module_id)
        {
            let run = pilot.modules.running.remove(at);
            out.extend(self.effect_ended(session, run, owner, ship_id, now, Ending::NoRounds));
        }
        out
    }

    /// The host changes the rounds of a gun of this session's ship (a pilot loaded it or unloaded it in space): a gun that runs keeps its rounds here, so what they
    /// come to is decided here and the host is told (`ChargeAdjusted`). A gun that does not run is not here: the host has its rounds and is told so. A gun that
    /// runs a charge of another kind than the one the host loads is left as it is (the rounds it fires are those it began the cycle with). The client is told by
    /// the host; the next shot tells it the rounds that are in the slot then.
    pub fn adjust_charge(
        &mut self,
        request_id: u64,
        session: SessionId,
        module_id: i64,
        charge_type: i32,
        op: ChargeOp,
    ) -> Vec<Out> {
        let answer = |running, before, after| {
            vec![Out::ChargeAdjusted { session, request_id, running, before, after }]
        };
        let Some(pilot) = self.pilots.get_mut(&session) else { return answer(false, 0, 0) };
        let Some(run) = pilot.modules.running.iter_mut().find(|r| r.effect.module_id == module_id) else {
            return answer(false, 0, 0);
        };
        let (Some(charge), Some(rounds)) =
            (run.effect.turret.as_ref().and_then(|t| t.charge), run.rounds.as_mut())
        else {
            return answer(false, 0, 0);
        };
        let before = *rounds;
        *rounds = match op {
            ChargeOp::Add { capacity, available } if charge.type_id == charge_type => {
                before + (capacity - before).max(0).min(available.max(0))
            }
            ChargeOp::Add { .. } => before,
            ChargeOp::Remove { quantity } if quantity > 0 => (before - quantity).max(0),
            ChargeOp::Remove { .. } => 0,
        };
        answer(true, before, *rounds)
    }

    /// The running effect of a module of this session, if it runs.
    pub(super) fn running_mut(&mut self, session: SessionId, module_id: i64) -> Option<&mut Running> {
        self.pilots
            .get_mut(&session)?
            .modules
            .running
            .iter_mut()
            .find(|run| run.effect.module_id == module_id)
    }

    /// The bonus a weapon's shot at `target_id` takes, from the ramp its running effect has built (0 when the module does not run).
    pub(crate) fn spool_bonus(
        &mut self,
        session: SessionId,
        module_id: i64,
        target_id: i64,
        spool: Spool,
    ) -> f64 {
        self.running_mut(session, module_id).map_or(0.0, |run| spool.bonus_for(&mut run.spool, target_id))
    }

    /// A cycle has been shot: the ramp of the running effect grows by a cycle's worth.
    pub(crate) fn spool_advance(&mut self, session: SessionId, module_id: i64, spool: Spool) {
        if let Some(run) = self.running_mut(session, module_id) {
            spool.advance(&mut run.spool);
        }
    }

    /// What the ramp of a running effect has built, for the tests.
    #[cfg(test)]
    pub(crate) fn spool_of(&self, session: SessionId, module_id: i64) -> Option<SpoolState> {
        self.pilots
            .get(&session)?
            .modules
            .running
            .iter()
            .find(|run| run.effect.module_id == module_id)
            .map(|run| run.spool)
    }

    pub(super) fn spend_round(&mut self, session: SessionId, module_id: i64, at: i64) -> (Vec<Out>, bool) {
        let Some(pilot) = self.pilots.get_mut(&session) else { return (Vec::new(), false) };
        let (owner, ship_id) = (pilot.ship.spec.character_id, pilot.ship.spec.ship_id);
        let Some(run) = pilot.modules.running.iter_mut().find(|r| r.effect.module_id == module_id) else {
            return (Vec::new(), true);
        };
        let action_charge = run.effect.action.as_ref().and_then(Action::charge_use);
        let use_charge = run
            .effect
            .turret
            .as_ref()
            .and_then(|turret| turret.charge)
            .map(|charge| (charge.type_id, charge.flag, charge.per_cycle))
            .or_else(|| action_charge.map(|(type_id, flag, per_cycle, _)| (type_id, flag, per_cycle)));
        let (Some((type_id, flag, per_cycle)), Some(rounds)) = (use_charge, run.rounds.as_mut()) else {
            return (Vec::new(), true);
        };
        let (old, new) = (*rounds, (*rounds - per_cycle).max(0));
        if old <= 0 {
            let has_fallback = run
                .effect
                .action
                .as_ref()
                .is_some_and(|action| matches!(action, Action::Charged { fallback: Some(_), .. }));
            return (Vec::new(), has_fallback);
        }
        *rounds = new;
        let line = attribute_change_of(
            owner,
            charge_key(ship_id, &crate::weapons::Charge { type_id, flag, rounds: old, per_cycle }),
            805,
            at,
            number(new as f64),
            number(old as f64),
        );
        (
            vec![
                Self::dogma_notify(session, "OnModuleAttributeChanges", vec![Value::List(vec![line])]),
                Out::ChargeSpent { session, module_id, rounds_left: new },
            ],
            action_charge.is_some() && new > 0 || action_charge.is_none() && new >= per_cycle,
        )
    }

    /// The pilot asks for an effect to stop: it does at the end of the cycle it is in. `Refused` (`NOT_ACTIVE`) when it is not running.
    pub fn deactivate(&mut self, session: SessionId, module_id: i64, now: i64) -> Vec<Out> {
        const CODE: u8 = 16;
        let Some(pilot) = self.pilots.get_mut(&session) else {
            return vec![Out::Refused { session, command: CODE, reason: "NO_SHIP_IN_SPACE" }];
        };
        // an effect still waiting for the fuel of its first cycle is cancelled: its activation is refused, and the answer to the fuel ask no longer starts it
        if let Some(position) =
            pilot.modules.pending_starts.iter().position(|start| start.effect.module_id == module_id)
        {
            pilot.modules.pending_starts.remove(position);
            return vec![
                Out::Refused { session, command: 17, reason: "DEACTIVATED" },
                Out::Accepted { session, command: CODE, at: now },
            ];
        }
        let Some(index) = pilot.modules.running.iter().position(|r| r.effect.module_id == module_id) else {
            return vec![Out::Refused { session, command: CODE, reason: "NOT_ACTIVE" }];
        };
        if pilot.modules.running[index].pending_work.is_some() {
            // parked on a work cycle: there is no cycle left to finish, so the effect stops now
            let (owner, ship_id) = (pilot.ship.spec.character_id, pilot.ship.spec.ship_id);
            let run = pilot.modules.running.remove(index);
            let mut out = vec![Out::Accepted { session, command: CODE, at: now }];
            out.extend(self.effect_ended(session, run, owner, ship_id, now, Ending::Done));
            return out;
        }
        // A mining cycle that is short-cycled by the pilot pays what it has mined so far, as the reference does (`deactivateGenericModule`). An ice cycle is not
        // short-cycled: it mines nothing short, so it runs to its boundary as the reference does.
        let run = &pilot.modules.running[index];
        let efficiency = ((now - run.cycle_start) as f64
            / (run.effect.duration_ms * TICKS_PER_SECOND as f64 / 1000.0))
            .clamp(0.0, 1.0);
        let short =
            match run.effect.action.as_ref().and_then(|action| action.with_charges(run.charged_this_cycle)) {
                Some(Action::Mining {
                    family,
                    mining_amount_m3,
                    waste_multiplier,
                    waste_probability,
                    crit_probability,
                    crit_multiplier,
                }) if *family != MiningFamily::Ice => Some(Action::Mining {
                    family: *family,
                    mining_amount_m3: mining_amount_m3 * efficiency,
                    waste_multiplier: *waste_multiplier,
                    waste_probability: *waste_probability,
                    crit_probability: *crit_probability,
                    crit_multiplier: *crit_multiplier,
                }),
                _ => None,
            };
        let (owner, ship_id) = (pilot.ship.spec.character_id, pilot.ship.spec.ship_id);
        match short {
            Some(work) if efficiency > 0.0 => {
                pilot.modules.running[index].stopping = true;
                let mut out = vec![Out::Accepted { session, command: CODE, at: now }];
                out.extend(self.park_work_cycle(session, module_id, &work, now));
                out
            }
            // nothing was mined yet: the effect stops at once, with no cycle to pay for
            Some(_) => {
                let run = pilot.modules.running.remove(index);
                let mut out = vec![Out::Accepted { session, command: CODE, at: now }];
                out.extend(self.effect_ended(session, run, owner, ship_id, now, Ending::Done));
                out
            }
            None => {
                pilot.modules.running[index].stopping = true;
                vec![Out::Accepted { session, command: CODE, at: now }]
            }
        }
    }

    /// What the running effects and the capacitors have come to by `now`: cycles that end (the next begins, or the effect stops), and the charge told.
    /// At most `MAX_MOMENTS_PER_SETTLE` moments are taken for a ship in one settle, and a moment that comes back at the same tick with nothing done is a stall
    /// (the progress guarantee, M-1): the effect it belongs to is ended, so the loop always moves on.
    pub(crate) fn settle_modules(&mut self, now: i64) -> Vec<Out> {
        let sessions: Vec<SessionId> = self
            .in_entry_order()
            .into_iter()
            .filter(|s| {
                !self.pilots[s].modules.is_idle()
                    || self.pilots[s].ship.health.next_sample.is_some()
                    || !self.pilots[s].incoming.is_empty()
            })
            .collect();
        let mut out = Vec::new();
        for session in sessions {
            // what no module renewed lapses
            out.extend(self.expire_incoming(session, now));
            // a cycle that ends is followed by the charge told at the moments in between, in the order they fall
            let mut taken = 0;
            let mut last: Option<(i64, Moment, i64)> = None;
            while taken < MAX_MOMENTS_PER_SETTLE
                && let Some((at, kind)) = self.next_module_moment(session, now)
            {
                taken += 1;
                // the module is part of the key: when an effect ends, the one after it takes its index and may be due at the same tick
                let key = (at, kind, self.moment_module(session, kind));
                if last == Some(key) {
                    match self.stalled(session, kind, at) {
                        Some(stalled) => out.extend(stalled),
                        None => break,
                    }
                    last = None;
                    continue;
                }
                last = Some(key);
                out.extend(self.module_moment(session, at, kind));
            }
        }
        out
    }

    /// The value a running effect of this session sets on an attribute of an item (its `on_changes`: what its modifiers give while it runs), 0 when none does.
    pub(crate) fn overlay(&self, session: SessionId, item_id: i64, attribute_id: i32) -> f64 {
        self.pilots.get(&session).map_or(0.0, |pilot| {
            pilot
                .modules
                .running
                .iter()
                .flat_map(|run| run.effect.on_changes.iter())
                .filter(|change| change.item_id == item_id && change.attribute_id == attribute_id)
                .fold(0.0, |most, change| most.max(change.value))
        })
    }

    /// The module a moment belongs to (-1 when it has none, or its index no longer names one).
    fn moment_module(&self, session: SessionId, kind: Moment) -> i64 {
        let Some(pilot) = self.pilots.get(&session) else { return -1 };
        match kind {
            Moment::CycleEnd(index)
            | Moment::WorkResend(index)
            | Moment::WorkStop(index)
            | Moment::StrikePulse(index)
            | Moment::DoomsdayHit(index) => {
                pilot.modules.running.get(index).map_or(-1, |run| run.effect.module_id)
            }
            Moment::StartResend(index) => {
                pilot.modules.pending_starts.get(index).map_or(-1, |start| start.effect.module_id)
            }
            Moment::Charge | Moment::Shield => -1,
        }
    }

    /// A moment that came back unchanged (M-1): the effect it belongs to is ended (`Ending::Forced`) or the stalled pending start is dropped. `None` when the
    /// moment has no effect to end (the charge and the shield are told as their ticks come), so the settle is left there.
    pub(super) fn stalled(&mut self, session: SessionId, kind: Moment, at: i64) -> Option<Vec<Out>> {
        let module_id = match kind {
            Moment::CycleEnd(index)
            | Moment::WorkResend(index)
            | Moment::WorkStop(index)
            | Moment::StrikePulse(index)
            | Moment::DoomsdayHit(index) => {
                self.pilots.get(&session)?.modules.running.get(index)?.effect.module_id
            }
            Moment::StartResend(index) => {
                let pilot = self.pilots.get_mut(&session)?;
                if index >= pilot.modules.pending_starts.len() {
                    return None;
                }
                pilot.modules.pending_starts.remove(index);
                return Some(Vec::new());
            }
            Moment::Charge | Moment::Shield => return None,
        };
        Some(self.offline_module(session, module_id, at))
    }

    /// The next thing due for a ship's modules by `now`: a cycle's end or a charge to tell.
    pub(super) fn next_module_moment(&self, session: SessionId, now: i64) -> Option<(i64, Moment)> {
        let pilot = self.pilots.get(&session)?;
        let mut next: Option<(i64, Moment)> = None;
        let mut consider = |at: i64, moment: Moment| {
            if at <= now && next.as_ref().is_none_or(|(best, _)| at < *best) {
                next = Some((at, moment));
            }
        };
        for (index, start) in pilot.modules.pending_starts.iter().enumerate() {
            // a start parked for its fuel: the same id is asked again after a while
            consider(start.sent_at + WORK_RESEND_TICKS, Moment::StartResend(index));
        }
        for (index, run) in pilot.modules.running.iter().enumerate() {
            if let Some(pending) = run.pending_work {
                // parked on the host's answer: no cycle is timed; the same id is sent again after a while, and the effect stops when its target is gone
                let target_ok = if pending.fuel {
                    self.cycle_target_ok(session, &run.effect, run.charged_this_cycle)
                } else {
                    self.work_target_ok(session, &run.effect)
                };
                if target_ok {
                    consider(pending.sent_at + WORK_RESEND_TICKS, Moment::WorkResend(index));
                } else {
                    consider(now, Moment::WorkStop(index));
                }
                continue;
            }
            if run.pending_remote_repair.is_some() {
                continue;
            }
            if let Some(Action::Doomsday { delay_ms, .. }) = run.effect.action.as_ref()
                && !run.doomsday_hit
            {
                consider(
                    run.cycle_start + (delay_ms * TICKS_PER_SECOND as f64 / 1000.0) as i64,
                    Moment::DoomsdayHit(index),
                );
            }
            if let Some(strike) = strike_of(run.effect.action.as_ref())
                && run.strike_done < strike.pulses
            {
                consider(
                    run.cycle_start
                        + (strike.pulse_offset_ms(run.strike_done) * TICKS_PER_SECOND as f64 / 1000.0) as i64,
                    Moment::StrikePulse(index),
                );
            }
            consider(
                run.cycle_start + (run.effect.duration_ms * TICKS_PER_SECOND as f64 / 1000.0) as i64,
                Moment::CycleEnd(index),
            );
        }
        if let Some(sample) = pilot.modules.capacitor.as_ref().and_then(|c| c.next_sample) {
            consider(sample, Moment::Charge);
        }
        // the shield's charge follows the capacitor's at the same tick (the reference works them out in that order)
        if let Some(sample) = pilot.ship.health.next_sample {
            consider(sample, Moment::Shield);
        }
        next
    }

    pub(super) fn module_moment(&mut self, session: SessionId, at: i64, moment: Moment) -> Vec<Out> {
        match moment {
            Moment::Charge => self.tell_charge(session, at),
            Moment::Shield => self.tell_shield(session, at),
            Moment::CycleEnd(index) => self.end_cycle(session, index, at),
            Moment::WorkResend(index) => self.resend_work_cycle(session, index, at),
            Moment::WorkStop(index) => self.stop_parked(session, index, at),
            Moment::StartResend(index) => self.resend_start(session, index, at),
            Moment::StrikePulse(index) => self.strike_pulse(session, index, at),
            Moment::DoomsdayHit(index) => self.doomsday_hit(session, index, at),
        }
    }

    /// The charge is told: now, and a tenth of a second before; the next time is half a second on while the charge is below the capacity.
    pub(super) fn tell_charge(&mut self, session: SessionId, at: i64) -> Vec<Out> {
        let Some(pilot) = self.pilots.get_mut(&session) else { return Vec::new() };
        let (owner, ship) = (pilot.ship.spec.character_id, pilot.ship.spec.ship_id);
        let Some(capacitor) = pilot.modules.capacitor.as_mut() else { return Vec::new() };
        let (new, old) = (capacitor.charge_at(at), capacitor.charge_at(at - TICK));
        if old >= capacitor.capacity {
            // it was full a tick ago: nothing more is told
            capacitor.next_sample = None;
            return Vec::new();
        }
        // the tick that fills it is told when it is a tick that tells (the reference tells the change on every fifth tick)
        capacitor.next_sample = (new < capacitor.capacity).then_some(at + SAMPLE_EVERY);
        vec![Self::charge_told(session, owner, ship, at, new, old)]
    }
}
