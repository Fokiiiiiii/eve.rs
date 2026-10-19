use super::*;
use crate::missile_entity::AUTO_TARGET_ID;
use crate::wire::{filetime, id, int, list, real, tuple};

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

    /// Effect attribute changes and the current cycle duration, sent to the pilot.
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
    /// The action the client plays on the ship for the effect, on `target_id` (the module's target, or a lance's beacon; 0 for none).
    pub(super) fn fx_action(stamp: u32, ship: &Value, effect: &Effect, on: bool, target_id: i64) -> Value {
        payload::action(
            stamp,
            "OnSpecialFX",
            [
                ship.clone(),
                id(effect.module_id),
                int(i64::from(effect.module_type)),
                if target_id > 0 { id(target_id) } else { Value::None },
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
                // A turret start reports the requested cycle count; its stop reports none.
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
            let Some(structure_id) = self.controlled_structure_id(session) else {
                return vec![Out::Refused { session, command: CODE, reason: "NO_SHIP_IN_SPACE" }];
            };
            if self.structure_body(structure_id).is_none()
                || !self.structure_health.contains_key(&structure_id)
            {
                return vec![Out::Refused { session, command: CODE, reason: "STRUCTURE_NOT_FOUND" }];
            }
            if !self.structure_modules.contains_key(&structure_id) {
                return vec![Out::Refused { session, command: CODE, reason: "STRUCTURE_SPEC_NOT_READY" }];
            }
            return self.with_controlled_structure_modules(now, |scene| scene.activate(session, effect, now));
        }
        if !effect.duration_ms.is_finite() || effect.duration_ms < 1.0 || effect.repeat < 1 {
            return vec![Out::Refused { session, command: CODE, reason: "BAD_EFFECT" }];
        }
        if effect.action.as_ref().is_some_and(Action::not_implemented) {
            return vec![Out::Refused { session, command: CODE, reason: "NOT_IMPLEMENTED" }];
        }
        self.settle_all(now);
        if matches!(effect.action.as_ref(), Some(Action::Cloak { .. }))
            && self.link_blocks_cloak_or_tether(session, now)
        {
            return vec![Out::Refused { session, command: CODE, reason: "LINK_HOLD" }];
        }
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
        // SDE attribute 763 limits concurrent or pending activations within a module group.
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
        // SDE attributes 1334 and 2608 bound activation speed using the ship's current velocity.
        // A stopped ship has speed 0.
        let v = pilot.ship.velocity;
        if let Some(reason) = velocity_refusal(&effect.rules, (v.x * v.x + v.y * v.y + v.z * v.z).sqrt()) {
            return vec![Out::Refused { session, command: CODE, reason }];
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
        // Pochven's regional jammer blocks cynosural fields.
        if matches!(effect.action.as_ref(), Some(Action::CynoField { .. }))
            && self.system().region_id == POCHVEN_REGION_ID
        {
            return vec![Out::Refused { session, command: CODE, reason: "CYNOSURAL_FIELD_JAMMED" }];
        }
        // Cynosural fields cannot start within 200 km of an anchored Mobile Cynosural Inhibitor; covert fields are exempt.
        if let Some(Action::CynoField { ball, .. }) = effect.action.as_ref()
            && ball.charge_type_id != COVERT_CYNOSURAL_FIELD_TYPE
            && self.inhibited_at(pilot.ship.position)
        {
            return vec![Out::Refused { session, command: CODE, reason: "CYNOSURAL_FIELD_INHIBITED" }];
        }
        // Smartbombs cannot start within max(5 km, range) of a station or stargate; cap range at the largest listed value.
        if let Some(Action::Smartbomb { range, .. }) = effect.action.as_ref()
            && self.station_or_gate_within(
                pilot.ship.position,
                SMARTBOMB_NEAR_METERS.max(range.min(SMARTBOMB_NEAR_SOURCED_MAX_METERS)),
            )
        {
            return vec![Out::Refused { session, command: CODE, reason: "SMARTBOMB_NEAR_STATION" }];
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
        if let Some(Action::Strike { strike }) = effect.action.as_ref()
            && !strike.is_valid()
        {
            return vec![Out::Refused { session, command: CODE, reason: "BAD_STRIKE" }];
        }
        if let Some(Action::Tractor { range, .. }) = effect.action.as_ref() {
            if effect.target_id <= 0 {
                return vec![Out::Refused { session, command: CODE, reason: "TARGET_REQUIRED" }];
            }
            // Tractor beams require a locked non-corpse wreck or container in range; other target types return TARGET_NOT_FOUND.
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
        // Scanning requires a locked ship in range; the Host resolves the reveal when the cycle ends.
        if let Some(Action::Scan { reach, .. }) = effect.action.as_ref() {
            if effect.target_id <= 0 {
                return vec![Out::Refused { session, command: CODE, reason: "TARGET_REQUIRED" }];
            }
            if effect.target_id == self.pilots[&session].ship.spec.ship_id {
                return vec![Out::Refused { session, command: CODE, reason: "TARGET_SELF" }];
            }
            let Some(target) = self.session_of(effect.target_id) else {
                return vec![Out::Refused { session, command: CODE, reason: "TARGET_NOT_FOUND" }];
            };
            if !self.locked_by(session).contains(&effect.target_id) {
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
        // Entosis links require a locked structure in activation range.
        if let Some(Action::Entosis { range_meters }) = effect.action.as_ref() {
            if effect.target_id <= 0 {
                return vec![Out::Refused { session, command: CODE, reason: "TARGET_REQUIRED" }];
            }
            let Some(structure) = self
                .system()
                .body(effect.target_id)
                .filter(|body| body.kind == crate::world::BodyKind::Structure)
            else {
                return vec![Out::Refused { session, command: CODE, reason: "TARGET_NOT_FOUND" }];
            };
            if !self.locked_by(session).contains(&effect.target_id) {
                return vec![Out::Refused { session, command: CODE, reason: "TARGET_NOT_LOCKED" }];
            }
            let apart =
                self.surface_distance_to_structure(session, effect.target_id).unwrap_or(f64::INFINITY);
            if apart > range_meters + crate::entosis::REACH_GRACE_METERS {
                return vec![Out::TargetRefused {
                    session,
                    command: CODE,
                    reason: "TARGET_OUT_OF_RANGE",
                    target_type: structure.type_id,
                    desired_range: *range_meters,
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
            // Doomsdays require a locked ship target in this scene.
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
        if self.abyssal.is_some() {
            fn restriction(action: &Action) -> Option<&'static str> {
                match action {
                    Action::Cloak { .. } => Some("ABYSSAL_CLOAK_RESTRICTED"),
                    Action::CynoField { .. } => Some("CYNOSURAL_FIELD_NOT_ALLOWED_IN_ABYSSAL_DEADSPACE"),
                    Action::Charged { action, .. } => restriction(action),
                    _ => None,
                }
            }
            if let Some(reason) = effect.action.as_ref().and_then(restriction) {
                return vec![Out::Refused { session, command: CODE, reason }];
            }
        }
        if matches!(effect.action.as_ref(), Some(Action::Cloak { .. })) {
            // A cloaked ship cannot activate another cloaking module.
            if pilot.cloak != crate::visibility::CloakState::Uncloaked {
                return vec![Out::Refused { session, command: CODE, reason: "MODULE_ALREADY_ACTIVE" }];
            }
            // SDE attribute 906 blocks cloaking while a non-early-deactivating module runs.
            if pilot.modules.running.iter().any(|r| r.effect.rules.disallow_early_deactivation) {
                return vec![Out::Refused { session, command: CODE, reason: "CLOAK_MODULE_ACTIVE" }];
            }
            if pilot.no_jump_until > now {
                return vec![Out::Refused { session, command: CODE, reason: "NO_JUMP_OR_CLOAK" }];
            }
            if pilot.flight.as_ref().is_some_and(|flight| flight.is_warping()) {
                return vec![Out::Refused { session, command: CODE, reason: "CLOAK_WARP_BLOCKED" }];
            }
            if !self.targeters_of(pilot.ship.spec.ship_id).is_empty() {
                return vec![Out::Refused { session, command: CODE, reason: "CLOAK_TARGET_LOCKED" }];
            }
            if self.cloak_trigger(session, crate::scene::CLOAK_PROXIMITY_METERS).is_some() {
                return vec![Out::Refused { session, command: CODE, reason: "CLOAK_PROXIMITY_BLOCKED" }];
            }
        } else if pilot.cloak != crate::visibility::CloakState::Uncloaked {
            // While cloaked, no other module may start.
            return vec![Out::Refused { session, command: CODE, reason: "MODULE_CLOAKED" }];
        }
        if effect.turret.is_some() {
            if effect.target_id <= 0 && effect.target_id != AUTO_TARGET_ID {
                return vec![Out::Refused { session, command: CODE, reason: "NO_TARGET" }];
            }
            // Reject targets absent from the scene or missing a lock.
            if effect.target_id != AUTO_TARGET_ID && !self.target_in_scene(effect.target_id) {
                return vec![Out::Refused { session, command: CODE, reason: "TARGET_NOT_FOUND" }];
            }
            if !self.turret_target_ok(session, &effect) {
                return vec![Out::Refused { session, command: CODE, reason: "TARGET_NOT_LOCKED" }];
            }
            if effect.turret.as_ref().and_then(|t| t.charge).is_some_and(|c| c.rounds < c.per_cycle) {
                return vec![Out::Refused { session, command: CODE, reason: "NO_AMMO" }];
            }
            // A missile launcher may start beyond missile flight range; the missile expires when its flight ends.
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
        // a P.A.N.I.C. needs an asteroid its pilot holds the lock on, and starts when the host has granted its fleet the buffs (`WORK_PANIC`)
        if matches!(effect.action, Some(Action::Panic { .. })) {
            if !self.locked_by(session).iter().any(|id| self.asteroids.contains_key(id)) {
                return vec![Out::Refused { session, command: CODE, reason: "TARGET_REQUIRED" }];
            }
            return self.park_panic(session, effect, now);
        }
        // an effect that burns fuel starts when the host has taken the fuel of its first cycle (`WORK_FUEL`): it is parked until the answer
        if effect.burns_fuel {
            return self.park_start(session, effect, now);
        }
        self.start_effect(session, effect, now)
    }

    /// Parks a P.A.N.I.C. until the host grants the ships in reach their buffs (`ModuleWorkCycle`, `WORK_PANIC`) and answers: the effect starts then.
    pub(super) fn park_panic(&mut self, session: SessionId, effect: Effect, now: i64) -> Vec<Out> {
        let cycle_id = next_work_cycle_id();
        let (module_id, target_id) = (effect.module_id, effect.target_id);
        let range = match effect.action {
            Some(Action::Panic { range_meters }) => range_meters,
            _ => 0.0,
        };
        let recipients = self.burst_recipients(session, range);
        let Some(pilot) = self.pilots.get_mut(&session) else { return Vec::new() };
        pilot.modules.pending_starts.push(PendingStart { effect, cycle_id, sent_at: now });
        self.panic_cycle_out(session, module_id, target_id, cycle_id, recipients).into_iter().collect()
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
        let charged_for_action =
            effect.action.as_ref().and_then(Action::charge_use).is_some_and(|(_, _, _, charges)| charges > 0);
        let active_action = effect.action.as_ref().and_then(|action| action.with_charges(charged_for_action));
        let controlled_structure = self.controlled_structure_id(session);
        if let Some(Action::PointDefense { range_meters, damage }) = active_action
            && (!range_meters.is_finite()
                || *range_meters < 0.0
                || damage.iter().any(|channel| !channel.is_finite() || *channel < 0.0))
        {
            return vec![Out::Refused { session, command: CODE, reason: "INVALID_POINT_DEFENSE_PROFILE" }];
        }
        if matches!(active_action, Some(Action::PointDefense { .. }))
            && !controlled_structure.is_some_and(|structure_id| {
                structure_id == pilot.ship.spec.ship_id
                    && self.control_lease_generation(session, structure_id).is_some()
            })
        {
            return vec![Out::Refused { session, command: CODE, reason: "STRUCTURE_CONTROL_LEASE_REQUIRED" }];
        }
        if matches!(active_action, Some(Action::Field { .. }))
            && controlled_structure.is_some_and(|structure_id| {
                structure_id != pilot.ship.spec.ship_id
                    || self.control_lease_generation(session, structure_id).is_none()
            })
        {
            return vec![Out::Refused { session, command: CODE, reason: "STRUCTURE_CONTROL_LEASE_REQUIRED" }];
        }
        if matches!(active_action, Some(Action::Strike { .. }))
            && controlled_structure
                .is_some_and(|structure_id| self.control_lease_generation(session, structure_id).is_none())
        {
            return vec![Out::Refused { session, command: CODE, reason: "STRUCTURE_CONTROL_LEASE_REQUIRED" }];
        }
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
        let mut out = if effect.offensive() {
            // Revoke the cached grant until the host supplies its authoritative weapons timer.
            let controller = self.controller_of_drone(session).filter(|id| *id != 0).unwrap_or(session);
            self.clear_tether_policy(controller, now)
        } else if self.effect_breaks_tether(session, &effect) {
            self.break_tether(session, now)
        } else {
            Vec::new()
        };
        let pilot = self.pilots.get_mut(&session).expect("the pilot is in the scene");
        pilot.protected_until = 0;
        let rounds = effect.turret.as_ref().and_then(|t| t.charge).map(|c| c.rounds).or_else(|| {
            effect.action.as_ref().and_then(Action::charge_use).map(|(_, _, _, charges)| charges)
        });
        let charged_this_cycle = rounds.is_some_and(|left| left > 0);
        let run = Running {
            effect,
            activation_nonce: next_work_cycle_id(),
            cycle_id: next_work_cycle_id(),
            cycle_start: now,
            cycles: 1,
            strike_done: 0,
            doomsday_hit: false,
            reach_poll_at: now,
            stopping: false,
            rounds,
            charged_this_cycle,
            structure_damage_charge_committed: false,
            pending_remote_repair: None,
            pending_work: None,
            pending_structure_damage: None,
            spool: SpoolState::default(),
            pending_reload: false,
            pending_update: None,
        };
        let (before, after) = match pilot.modules.capacitor.as_mut() {
            Some(capacitor) => capacitor.draw(now, run.effect.capacitor_need),
            None => (0.0, 0.0),
        };
        // a reactive armor hardener's resonances start at its seed, before anything is told
        if let Some(Action::ResistanceShift { seed, shift, .. }) = run.effect.action.as_ref() {
            pilot.ship.health.start_shift(*seed, *shift);
        }
        out.push(Out::Accepted { session, command: CODE, at: now });
        out.push(Self::charge_told(session, owner, ship_id, now, after, before));
        let stamp = self.next_stamp(now);
        let ship = id(ship_id);
        if let Some(numbers) = run.effect.on {
            let actions = self.renumber(session, numbers, stamp);
            if !actions.is_empty() {
                out.extend(self.tell_all(session, actions));
            }
        }
        // Spawn a lance beacon before its effect so clients can draw the beam; strikes without a beam still need the beacon.
        let beacon = match run.effect.action.as_ref() {
            Some(Action::Strike { strike }) => strike.beacon.map(|offset| {
                let lifetime = run.effect.duration_ms;
                self.release_beacon(session, &run.effect, offset, lifetime, now)
            }),
            _ => None,
        };
        let beacon_id = beacon.as_ref().map_or(0, |(beacon_id, _)| *beacon_id);
        if let Some((_, beacon_out)) = beacon {
            out.extend(beacon_out);
        }
        // Tell turret effects before their attribute changes.
        let fx_target = if beacon_id > 0 { beacon_id } else { run.effect.target_id };
        let fx = self.tell_all(session, vec![Self::fx_action(stamp, &ship, &run.effect, true, fx_target)]);
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
        if self.pilots.get(&session).is_some_and(|pilot| {
            pilot
                .modules
                .running
                .iter()
                .any(|run| run.effect.module_id == effect.module_id && run.pending_structure_damage.is_some())
        }) {
            return out;
        }
        // A controlled structure's area strike spends its first charge only after the Host authorizes pulse 0.
        if self.defer_structure_damage_charge(session, &effect, charged_this_cycle) {
            return out;
        }
        // the round of the first shot is spent after the shot; a turret that has no round for the next cycle stops
        let (spent, goes_on) = self.spend_round(session, effect.module_id, now);
        out.extend(spent);
        let stop_without_charge =
            matches!(effect.action.as_ref(), Some(Action::Charged { fallback: None, .. }));
        if !goes_on
            && (effect.turret.is_some() || stop_without_charge)
            && !self.park_for_reload(session, effect.module_id)
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
        now: i64,
    ) -> Vec<Out> {
        let answer = |running, before, after| {
            vec![Out::ChargeAdjusted { session, request_id, running, before, after }]
        };
        if !self.pilots.contains_key(&session)
            && self
                .controlled_structure_id(session)
                .is_some_and(|id| self.structure_modules.contains_key(&id))
        {
            return self.with_controlled_structure_modules(now, |scene| {
                scene.adjust_charge(request_id, session, module_id, charge_type, op, now)
            });
        }
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
        let after = *rounds;
        // A gun waiting on reload starts its next cycle with the loaded rounds, or stops if none arrived.
        let waiting = std::mem::take(&mut run.pending_reload);
        let mut out = answer(true, before, after);
        if waiting {
            out.extend(if after > 0 {
                self.finish_cycle(session, module_id, now, Vec::new(), true)
            } else {
                self.end_without_rounds(session, module_id, now)
            });
        }
        out
    }

    /// A turret that has spent its last round and reloads from its hold waits for the host's load (`AdjustCharge`); nothing is ended. Returns whether it waits.
    pub(super) fn park_for_reload(&mut self, session: SessionId, module_id: i64) -> bool {
        let Some(run) = self.running_mut(session, module_id) else { return false };
        let reloads =
            run.effect.turret.as_ref().and_then(|turret| turret.charge).is_some_and(|charge| charge.reloads);
        if reloads {
            run.pending_reload = true;
        }
        reloads
    }

    /// The effect of a module ends for want of rounds (the host gave none to a gun that waited for them).
    fn end_without_rounds(&mut self, session: SessionId, module_id: i64, at: i64) -> Vec<Out> {
        let Some(pilot) = self.pilots.get_mut(&session) else { return Vec::new() };
        let (owner, ship_id) = (pilot.ship.spec.character_id, pilot.ship.spec.ship_id);
        let Some(index) = pilot.modules.running.iter().position(|run| run.effect.module_id == module_id)
        else {
            return Vec::new();
        };
        let run = pilot.modules.running.remove(index);
        self.effect_ended(session, run, owner, ship_id, at, Ending::NoRounds)
    }

    /// The host refreshes the numbers of a running effect (`UpdateEffect`): they are kept and take effect at the start of its next cycle. An effect that is not
    /// running is not changed, and nothing is answered.
    pub(crate) fn update_effect(
        &mut self,
        session: SessionId,
        module_id: i64,
        update: PendingUpdate,
    ) -> Vec<Out> {
        if !self.pilots.contains_key(&session)
            && self
                .controlled_structure_id(session)
                .is_some_and(|id| self.structure_modules.contains_key(&id))
        {
            return self.with_controlled_structure_modules(0, |scene| {
                scene.update_effect(session, module_id, update)
            });
        }
        if let Some(run) = self.running_mut(session, module_id) {
            run.pending_update = Some(update);
        }
        Vec::new()
    }

    /// Protocol 43 applies the Host's buff state, notifies the client with `OnDbuffUpdated`, and uses the Host's ship attributes.
    /// State for another ship is ignored.
    pub(crate) fn set_buffs(
        &mut self,
        session: SessionId,
        ship_id: i64,
        entries: Vec<crate::link::BuffEntry>,
        now: i64,
    ) -> Vec<Out> {
        let Some(pilot) = self.pilots.get(&session) else {
            eprintln!("eve-sim: refused buff state session={session} ship={ship_id} reason=no_pilot");
            return Vec::new();
        };
        if pilot.ship.spec.ship_id != ship_id {
            eprintln!(
                "eve-sim: refused buff state session={session} ship={ship_id} pilot_ship={}",
                pilot.ship.spec.ship_id
            );
            return Vec::new();
        }
        eprintln!("eve-sim: accepted buff state session={session} ship={ship_id} entries={}", entries.len());
        let stamp = self.next_stamp(now);
        // a remaining time in milliseconds is a span of ticks (a tick is 100 ns, as the FILETIME is)
        let entries = entries.into_iter().map(|entry| {
            let expires_at = now + (entry.remaining_ms * (TICKS_PER_SECOND as f64 / 1000.0)).round() as i64;
            tuple([int(i64::from(entry.collection_id)), tuple([real(entry.value), filetime(expires_at)])])
        });
        vec![Scene::update(
            session,
            vec![payload::action(stamp, "OnDbuffUpdated", [id(ship_id), list(entries)])],
        )]
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
            charge_key(
                ship_id,
                &crate::weapons::Charge { type_id, flag, rounds: old, per_cycle, reloads: false },
            ),
            805,
            at,
            number(new as f64),
            number(old as f64),
        );
        // a charged cycle that takes effect at its end (a command burst, a charged repair at the cycle's end) goes on to that end on the round it spent
        let ends_cycle = matches!(run.effect.action.as_ref(), Some(Action::Charged { action, .. }) if action.ends_its_cycle());
        (
            vec![
                Self::dogma_notify(session, "OnModuleAttributeChanges", vec![Value::List(vec![line])]),
                Out::ChargeSpent { session, module_id, rounds_left: new },
            ],
            action_charge.is_some() && (new > 0 || ends_cycle) || action_charge.is_none() && new >= per_cycle,
        )
    }

    /// Keep a controlled structure's strike charge loaded until its first damage pulse is authorized.
    pub(super) fn defer_structure_damage_charge(
        &self,
        session: SessionId,
        effect: &Effect,
        charged_this_cycle: bool,
    ) -> bool {
        let Some(structure_id) = self.controlled_structure_id(session) else { return false };
        if self.control_lease_generation(session, structure_id).is_none()
            || self.pilots.get(&session).is_none_or(|pilot| pilot.ship.spec.ship_id != structure_id)
        {
            return false;
        }
        effect
            .action
            .as_ref()
            .and_then(|action| action.with_charges(charged_this_cycle))
            .is_some_and(|action| matches!(action, Action::Strike { .. } | Action::Field { .. }))
    }

    /// The pilot asks for an effect to stop: it does at the end of the cycle it is in. `Refused` (`NOT_ACTIVE`) when it is not running.
    pub fn deactivate(&mut self, session: SessionId, module_id: i64, now: i64) -> Vec<Out> {
        const CODE: u8 = 16;
        if !self.pilots.contains_key(&session) {
            if let Some(structure_id) = self.controlled_structure_id(session)
                && self.structure_modules.contains_key(&structure_id)
            {
                return self.with_controlled_structure_modules(now, |scene| {
                    scene.deactivate(session, module_id, now)
                });
            }
            return vec![Out::Refused { session, command: CODE, reason: "NO_SHIP_IN_SPACE" }];
        }
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
        if pilot.modules.running[index].pending_work.is_some() || pilot.modules.running[index].pending_reload
        {
            // parked on a work cycle or a reload: there is no cycle left to finish, so the effect stops now
            let (owner, ship_id) = (pilot.ship.spec.character_id, pilot.ship.spec.ship_id);
            let run = pilot.modules.running.remove(index);
            let mut out = vec![Out::Accepted { session, command: CODE, at: now }];
            out.extend(self.effect_ended(session, run, owner, ship_id, now, Ending::Done));
            return out;
        }
        // A short mining cycle pays its partial yield; an ice cycle runs to its boundary without partial yield.
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

    pub(crate) fn stop_controlled_structure_modules(&mut self, session: SessionId, now: i64) -> Vec<Out> {
        let Some(pilot) = self.pilots.get_mut(&session) else { return Vec::new() };
        let (owner, ship_id) = (pilot.ship.spec.character_id, pilot.ship.spec.ship_id);
        let pending = std::mem::take(&mut pilot.modules.pending_starts);
        let running = std::mem::take(&mut pilot.modules.running);
        let mut out: Vec<Out> = pending
            .into_iter()
            .map(|_| Out::Refused { session, command: 17, reason: "DEACTIVATED" })
            .collect();
        for run in running {
            out.extend(self.effect_ended(session, run, owner, ship_id, now, Ending::Done));
        }
        out
    }

    pub(crate) fn stop_unfitted_structure_modules(&mut self, session: SessionId, now: i64) -> Vec<Out> {
        let Some(pilot) = self.pilots.get(&session) else { return Vec::new() };
        let fitted = |module_id: i64, module_type: i32| {
            pilot
                .ship
                .spec
                .modules
                .iter()
                .any(|module| module.item_id == module_id && module.type_id == module_type)
        };
        let pending: Vec<i64> = pilot
            .modules
            .pending_starts
            .iter()
            .filter(|start| !fitted(start.effect.module_id, start.effect.module_type))
            .map(|start| start.effect.module_id)
            .collect();
        let running: Vec<i64> = pilot
            .modules
            .running
            .iter()
            .filter(|run| !fitted(run.effect.module_id, run.effect.module_type))
            .map(|run| run.effect.module_id)
            .collect();
        let mut out = Vec::new();
        for module_id in pending {
            out.extend(self.deactivate(session, module_id, now));
        }
        for module_id in running {
            out.extend(self.offline_module(session, module_id, now));
        }
        out
    }

    pub(crate) fn settle_modules(&mut self, now: i64) -> Vec<Out> {
        self.with_controlled_structure_modules(now, |scene| scene.settle_modules_for_pilots(now))
    }

    /// Settles effects and capacitors through `now`, sending completed cycles and charge updates in event order.
    /// Each settle is bounded by `MAX_MOMENTS_PER_SETTLE`; a stalled moment ends its effect to guarantee progress.
    fn settle_modules_for_pilots(&mut self, now: i64) -> Vec<Out> {
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
            | Moment::StructureDamageResend(index)
            | Moment::WorkStop(index)
            | Moment::StrikePulse(index)
            | Moment::DoomsdayHit(index)
            | Moment::ReachCheck(index) => {
                pilot.modules.running.get(index).map_or(-1, |run| run.effect.module_id)
            }
            Moment::StartResend(index) => {
                pilot.modules.pending_starts.get(index).map_or(-1, |start| start.effect.module_id)
            }
            Moment::Charge | Moment::Shield => -1,
        }
    }

    /// A stalled moment ends its effect or drops its pending start; returns `None` when there is no effect to end.
    pub(super) fn stalled(&mut self, session: SessionId, kind: Moment, at: i64) -> Option<Vec<Out>> {
        let module_id = match kind {
            Moment::CycleEnd(index)
            | Moment::WorkResend(index)
            | Moment::StructureDamageResend(index)
            | Moment::WorkStop(index)
            | Moment::StrikePulse(index)
            | Moment::DoomsdayHit(index)
            | Moment::ReachCheck(index) => {
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
            if let Some(pending) = run.pending_structure_damage.as_ref() {
                let lease_is_current = self.controlled_structure_id(session) == Some(pending.structure_id)
                    && self.control_lease_generation(session, pending.structure_id)
                        == Some(pending.control_lease_generation);
                let next_at = if lease_is_current { pending.sent_at + WORK_RESEND_TICKS } else { now };
                consider(next_at, Moment::StructureDamageResend(index));
                continue;
            }
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
            // parked on a reload: no cycle is timed until the host's load answers it
            if run.pending_remote_repair.is_some() || run.pending_reload {
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
            if matches!(run.effect.action.as_ref(), Some(Action::Kamikaze { .. } | Action::Missile { .. }))
                && !run.doomsday_hit
                && run.reach_poll_at
                    < run.cycle_start + (run.effect.duration_ms * TICKS_PER_SECOND as f64 / 1000.0) as i64
            {
                consider(run.reach_poll_at, Moment::ReachCheck(index));
            }
            let active_action =
                run.effect.action.as_ref().and_then(|action| action.with_charges(run.charged_this_cycle));
            if let Some(strike) = strike_of(active_action)
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
        // Tell the shield charge after the capacitor charge when both change at the same tick.
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
            Moment::StructureDamageResend(index) => {
                self.resend_structure_damage_candidates(session, index, at)
            }
            Moment::WorkStop(index) => self.stop_parked(session, index, at),
            Moment::StartResend(index) => self.resend_start(session, index, at),
            Moment::StrikePulse(index) => self.strike_pulse(session, index, at),
            Moment::DoomsdayHit(index) => self.doomsday_hit(session, index, at),
            Moment::ReachCheck(index) => self.reach_check(session, index, at),
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
        // Report a charge change on every fifth tick, including the tick that fills the capacitor.
        capacitor.next_sample = (new < capacitor.capacity).then_some(at + SAMPLE_EVERY);
        vec![Self::charge_told(session, owner, ship, at, new, old)]
    }
}

/// Smartbombs are refused within max(5 km, their range) of a station or stargate; range is capped at the largest listed value.
const SMARTBOMB_NEAR_METERS: f64 = 5_000.0;
const SMARTBOMB_NEAR_SOURCED_MAX_METERS: f64 = 6_000.0;
/// Mobile Cynosural Inhibitors do not stop covert cynosural fields.
const COVERT_CYNOSURAL_FIELD_TYPE: i32 = 28_650;

/// Pochven (SDE region 10000070) blocks cynosural fields.
const POCHVEN_REGION_ID: i32 = 10_000_070;

impl Scene {
    /// Whether the surface of a station or a stargate of this system is less than `metres` from `at`.
    pub(super) fn station_or_gate_within(&self, at: eve_destiny::Vec3, metres: f64) -> bool {
        self.system().bodies.iter().any(|body| {
            matches!(body.kind, crate::world::BodyKind::Station | crate::world::BodyKind::Stargate) && {
                let (dx, dy, dz) = (body.position.x - at.x, body.position.y - at.y, body.position.z - at.z);
                (dx * dx + dy * dy + dz * dz).sqrt() - body.radius < metres
            }
        })
    }
}

/// Refuses activation outside the allowed ship speed in m/s; a limit of 0 disables that bound.
pub(crate) fn velocity_refusal(rules: &Rules, speed: f64) -> Option<&'static str> {
    if rules.max_velocity_limit > 0.0 && speed > rules.max_velocity_limit {
        return Some("VELOCITY_TOO_HIGH");
    }
    if rules.min_velocity_limit > 0.0 && speed < rules.min_velocity_limit {
        return Some("VELOCITY_TOO_LOW");
    }
    None
}

#[cfg(test)]
mod velocity_tests {
    use super::*;

    #[test]
    fn a_module_with_a_speed_band_starts_only_within_it() {
        // a cynosural field generator: up to 500 m/s; a bomb launcher: at least 1 m/s; neither: any speed
        let cyno = Rules { max_velocity_limit: 500.0, ..Default::default() };
        let launcher = Rules { min_velocity_limit: 1.0, ..Default::default() };
        assert_eq!(velocity_refusal(&cyno, 0.0), None);
        assert_eq!(velocity_refusal(&cyno, 500.0), None, "the limit itself is within");
        assert_eq!(velocity_refusal(&cyno, 500.5), Some("VELOCITY_TOO_HIGH"));
        assert_eq!(velocity_refusal(&launcher, 0.0), Some("VELOCITY_TOO_LOW"));
        assert_eq!(velocity_refusal(&launcher, 1.0), None);
        assert_eq!(velocity_refusal(&launcher, 250.0), None);
        assert_eq!(velocity_refusal(&Rules::default(), 9_999.0), None, "no limit: any speed starts it");
    }
}
