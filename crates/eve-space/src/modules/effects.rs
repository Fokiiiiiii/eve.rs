use super::*;

impl Scene {
    /// What the client of the ship an effect is projected at is told, for its HUD (`hudIconStates.js` of the reference): as the effect goes on `OnJamStart` (who, which module,
    /// the target, what the HUD calls it, when and for how long: a cycle and a tick over), `OnEwarStart` and the buff bar refreshed; as each cycle begins the timer again; as it ends
    /// `OnJamEnd`, `OnEwarEnd` and the buff bar. An NPC is told nothing.
    pub(crate) fn jam_told(&self, source: SessionId, effect: &Effect, at: i64, phase: Jam) -> Vec<Out> {
        let Some(jam) = effect.jam() else { return Vec::new() };
        let Some(target) = self.session_of(effect.target_id).filter(|t| !crate::npc::is_npc_session(*t))
        else {
            return Vec::new();
        };
        let Some(source_ship) = self.pilots.get(&source).map(|pilot| pilot.ship.spec.ship_id) else {
            return Vec::new();
        };
        let who = || vec![id(source_ship), id(effect.module_id), id(effect.target_id), text(jam)];
        let mut out = Vec::new();
        if phase != Jam::End {
            let mut args = who();
            args.push(filetime(at));
            args.push(int(effect.duration_ms as i64 + 100));
            out.push(Self::dogma_notify(target, "OnJamStart", args));
        }
        match phase {
            Jam::Start => {
                out.push(Self::dogma_notify(target, "OnEwarStart", who()));
                out.push(Self::dogma_notify(target, "OnRefreshBuffBar", Vec::new()));
            }
            Jam::Refresh => {}
            Jam::End => {
                out.push(Self::dogma_notify(target, "OnJamEnd", who()));
                out.push(Self::dogma_notify(target, "OnEwarEnd", who()));
                out.push(Self::dogma_notify(target, "OnRefreshBuffBar", Vec::new()));
            }
        }
        out
    }

    pub(super) fn act(&mut self, session: SessionId, effect: &Effect, when: Applies, at: i64) -> Vec<Out> {
        if let Some(Action::Charged { .. }) = effect.action.as_ref() {
            let charged_this_cycle = self
                .pilots
                .get(&session)
                .and_then(|pilot| {
                    pilot.modules.running.iter().find(|run| run.effect.module_id == effect.module_id)
                })
                .is_some_and(|run| run.charged_this_cycle);
            let Some(action) =
                effect.action.as_ref().and_then(|action| action.with_charges(charged_this_cycle))
            else {
                return Vec::new();
            };
            let mut selected = effect.clone();
            selected.action = Some(action.clone());
            return self.act(session, &selected, when, at);
        }
        match effect.action.as_ref() {
            Some(Action::Repair { layer, amount, when: applies }) if *applies == when => {
                let Some(ship_id) = self.pilots.get(&session).map(|pilot| pilot.ship.spec.ship_id) else {
                    return Vec::new();
                };
                self.mend(ship_id, *layer, *amount, at)
            }
            Some(Action::Remote { what, reach, when: applies, .. }) if *applies == when => {
                self.project(session, effect, what, reach, at)
            }
            Some(Action::Smartbomb { range, damage, hurts_pilots }) if when == Applies::CycleStart => {
                self.smartbomb(session, effect, *range, *damage, *hurts_pilots, at)
            }
            Some(Action::Strike { strike }) if when == Applies::CycleStart => {
                self.strike_start(session, effect, *strike, at)
            }
            Some(Action::Field { strike, .. }) if when == Applies::CycleStart => {
                self.strike_start(session, effect, *strike, at)
            }
            Some(Action::Doomsday { immobile_ms, no_jump_ms, .. }) if when == Applies::CycleStart => {
                self.hold_for_windows(session, *immobile_ms, *no_jump_ms, at);
                if let Some(run) = self.pilots.get_mut(&session).and_then(|pilot| {
                    pilot.modules.running.iter_mut().find(|run| run.effect.module_id == effect.module_id)
                }) {
                    run.doomsday_hit = false;
                }
                Vec::new()
            }
            Some(Action::EcmBurst { range, strength }) if when == Applies::CycleStart => {
                self.jam_burst(session, strength, *range, at)
            }
            Some(Action::ResistanceShift { attribute_ids, .. }) if when == Applies::CycleEnd => {
                self.shift_armor(session, *attribute_ids, at)
            }
            Some(Action::PassiveLock) if when == Applies::CycleStart => {
                self.passive_lock(session, effect.target_id, at)
            }
            Some(Action::AutoTarget { range }) if when == Applies::CycleStart => {
                self.auto_target_sweep(session, *range, at)
            }
            Some(Action::Tractor { velocity, hold, .. }) if when == Applies::CycleStart => {
                self.tractor_pull(session, effect.target_id, *velocity, *hold, at)
            }
            Some(Action::WarpProbe { ball, range, lifetime_ms }) if when == Applies::CycleStart => {
                let role = crate::deployable::Role::WarpSource { range: *range };
                self.release_deployable(session, effect, ball.clone(), role, *lifetime_ms, at)
            }
            Some(Action::Bomb { ball, kind, lifetime_ms, blast_radius, damage, ecm, neutralize })
                if when == Applies::CycleStart =>
            {
                let role = crate::deployable::Role::Bomb {
                    kind: *kind,
                    blast_radius: *blast_radius,
                    damage: *damage,
                    ecm: *ecm,
                    neutralize: *neutralize,
                };
                self.release_deployable(session, effect, ball.clone(), role, *lifetime_ms, at)
            }
            Some(Action::Defender { missile, damage }) if when == Applies::CycleStart => {
                self.defender_intercept(session, effect, missile, *damage, at)
            }
            Some(Action::StasisBurst { range, duration_ms, speed_factor }) if when == Applies::CycleStart => {
                self.stasis_burst(session, effect, *range, *duration_ms, *speed_factor, at)
            }
            Some(Action::Cloak { .. }) if when == Applies::CycleStart => {
                let changed = if let Some(pilot) = self.pilots.get_mut(&session) {
                    let changed = pilot.cloak != crate::visibility::CloakState::Cloaked;
                    pilot.cloak = crate::visibility::CloakState::Cloaked;
                    changed
                } else {
                    false
                };
                if changed {
                    self.boxes.clear();
                }
                Vec::new()
            }
            _ => Vec::new(),
        }
    }

    /// A passive targeter's cycle starts: its target is locked through the one lock path, with the target not told (EVE.js `suppressTargetNotice`). A lock the
    /// pilot has already is answered as it is; a refusal (range, capacity, a target that is not there) is told to the pilot as any lock's is.
    pub(super) fn passive_lock(&mut self, session: SessionId, target: i64, at: i64) -> Vec<Out> {
        self.lock(session, target, &Order::Lock(target), LockOrigin::Passive { suppress_notice: true }, at)
    }

    /// The attributes a running reactive armor hardener tells the armor's resonances as, if one runs.
    pub(crate) fn shift_attributes(&self, session: SessionId) -> Option<[i32; 4]> {
        self.pilots.get(&session)?.modules.running.iter().find_map(|run| match run.effect.action.as_ref() {
            Some(Action::ResistanceShift { attribute_ids, .. }) => Some(*attribute_ids),
            _ => None,
        })
    }

    /// A cycle of a reactive armor hardener ends: the armor's resonances move as the damage it took in the cycle says, and the pilot is told the ones that
    /// changed (the attributes of the ship, the values they now have, and the ones they had). Nothing is told when they do not move.
    pub(super) fn shift_armor(&mut self, session: SessionId, attribute_ids: [i32; 4], at: i64) -> Vec<Out> {
        let Some(pilot) = self.pilots.get_mut(&session) else { return Vec::new() };
        let (owner, ship_id) = (pilot.ship.spec.character_id, pilot.ship.spec.ship_id);
        let before = pilot.ship.health.armor_resonance();
        if pilot.ship.health.shift_cycle().is_none() {
            return Vec::new();
        }
        let after = pilot.ship.health.armor_resonance();
        let changes = armor_changes(&attribute_ids, &before, &after);
        Self::health_told(session, owner, ship_id, at, &changes).into_iter().collect()
    }

    pub(crate) fn apart(&self, a: SessionId, b: SessionId) -> Option<f64> {
        let (a, b) = (&self.pilots.get(&a)?.ship, &self.pilots.get(&b)?.ship);
        crate::geo::surface_gap(
            [a.position.x, a.position.y, a.position.z],
            a.spec.radius,
            [b.position.x, b.position.y, b.position.z],
            b.spec.radius,
        )
    }

    pub(crate) fn spatial_ship(
        &self,
        session: SessionId,
        pilot: &crate::scene::Pilot,
    ) -> crate::geo::SpatialEntity<SessionId> {
        let spec = &pilot.ship.spec;
        crate::geo::SpatialEntity {
            id: spec.ship_id,
            kind: if self.drones.contains_key(&session) {
                crate::geo::SpatialKind::Drone
            } else if pilot.ship.npc.is_some() {
                crate::geo::SpatialKind::NpcShip
            } else {
                crate::geo::SpatialKind::PlayerShip
            },
            character_id: (spec.character_id > 0).then_some(spec.character_id),
            owner_id: (spec.owner_id > 0).then_some(spec.owner_id),
            corporation_id: (spec.corporation_id > 0).then_some(spec.corporation_id),
            alliance_id: (spec.alliance_id > 0).then_some(spec.alliance_id),
            type_id: Some(spec.type_id),
            group_id: Some(spec.group_id),
            category_id: Some(spec.category_id),
            position: [pilot.ship.position.x, pilot.ship.position.y, pilot.ship.position.z],
            radius: spec.radius,
            warping: pilot.flight.as_ref().is_some_and(|flight| flight.is_warping()),
            item: session,
        }
    }

    /// A cycle of an effect that is projected at a ship: what lands is `what` weakened by how far the target is (nothing when it is out of reach). An attack is
    /// an aggression of the ship that runs the effect.
    pub(crate) fn project(
        &mut self,
        session: SessionId,
        effect: &Effect,
        what: &Remote,
        reach: &Reach,
        at: i64,
    ) -> Vec<Out> {
        let Some(target) = self.session_of(effect.target_id) else { return Vec::new() };
        let Some(share) = self.apart(session, target).and_then(|apart| reach.share(apart)) else {
            return Vec::new();
        };
        // a neutralizer made for bigger ships is weaker against a ship that is smaller than it resolves
        let resolved = |resolution: f64| {
            let signature = self.pilots.get(&target).map_or(0.0, |pilot| pilot.ship.spec.signature_radius);
            if resolution > 0.0 && signature > 0.0 { (signature / resolution).min(1.0) } else { 1.0 }
        };
        let out = match what {
            Remote::Web { .. }
            | Remote::Paint { .. }
            | Remote::Damp { .. }
            | Remote::Scramble { .. }
            | Remote::Tackle { .. }
            | Remote::Disrupt { .. }
            | Remote::GuidanceDisrupt { .. } => {
                let lasting = what.lasting(share).expect("these last");
                self.hold(session, effect, lasting, at)
            }
            Remote::Jam { strength } => self.jam(session, effect, strength, share, at),
            Remote::Repair { layer, amount } => {
                self.mend_remote(effect.target_id, *layer, amount * share, at)
            }
            Remote::MutadaptiveRepair { .. } => Vec::new(),
            Remote::Transfer { amount } => self.give_capacitor(target, amount * share, at),
            Remote::Neutralize { amount, signature_resolution } => {
                let strength = amount
                    * share
                    * resolved(*signature_resolution)
                    * self.capacitor_warfare_multiplier(target);
                self.take_capacitor(target, strength, at).0
            }
            Remote::Drain { amount, signature_resolution } => {
                let strength = amount * share * resolved(*signature_resolution);
                self.drain_capacitor(session, target, strength, at)
            }
        };
        if what.hostile() {
            let attacker_ship = self.pilots.get(&session).map_or(0, |pilot| pilot.ship.spec.ship_id);
            let target_type_id = self.pilots.get(&target).map_or(0, |pilot| pilot.ship.spec.type_id);
            let source_owner_id = self.combat_owner(session);
            self.aggressions.push(crate::weapons::Aggression {
                attacker_ship,
                target_ship: effect.target_id,
                module_type_id: effect.module_type,
                source_owner_id,
                target_type_id,
            });
        }
        out
    }

    /// `amount` GJ are put into the capacitor of the ship of `target`; its pilot is told the charge. A ship that has none, or is full, gets nothing.
    pub(super) fn give_capacitor(&mut self, target: SessionId, amount: f64, at: i64) -> Vec<Out> {
        let Some(pilot) = self.pilots.get_mut(&target) else { return Vec::new() };
        let (owner, ship) = (pilot.ship.spec.character_id, pilot.ship.spec.ship_id);
        let Some(capacitor) = pilot.modules.capacitor.as_mut() else { return Vec::new() };
        if amount <= 0.0 || capacitor.charge_at(at) >= capacitor.capacity {
            return Vec::new();
        }
        let (before, after) = capacitor.give(at, amount);
        vec![Self::charge_told(target, owner, ship, at, after, before)]
    }

    /// Up to `amount` GJ are taken out of the capacitor of the ship of `target` (never more than it holds); its pilot is told the charge. Answers what was taken.
    /// A strike's cycle starts: the ship stops and is held for its windows, every ship in the neutralising radius loses its capacitor, and the pulses are
    /// counted from zero (the first is due at the warning).
    /// Remote repair from another ship's module: half of it lands on a ship the disruptive lance has disrupted (`DISRUPTED_REMOTE_REPAIR`, protocol 28).
    pub(crate) fn mend_remote(&mut self, target_id: i64, layer: Layer, amount: f64, now: i64) -> Vec<Out> {
        let disrupted = self
            .session_of(target_id)
            .and_then(|session| self.pilots.get(&session))
            .is_some_and(|pilot| pilot.disrupted_until > now);
        let factor = if disrupted { DISRUPTED_REMOTE_REPAIR } else { 1.0 };
        self.mend(target_id, layer, amount * factor, now)
    }

    pub(super) fn strike_start(
        &mut self,
        session: SessionId,
        effect: &Effect,
        strike: Strike,
        at: i64,
    ) -> Vec<Out> {
        self.hold_for_windows(session, strike.immobile_ms, strike.no_jump_ms, at);
        if let Some(run) = self.pilots.get_mut(&session).and_then(|pilot| {
            pilot.modules.running.iter_mut().find(|run| run.effect.module_id == effect.module_id)
        }) {
            run.strike_done = 0;
        }
        self.strike_neutralise(session, strike, at)
    }

    /// The ship of a doomsday's cycle is held: it stops, follows no order and cannot warp or jump for `immobile_ms`, and cannot jump or cloak for `no_jump_ms`
    /// (both from the start of the cycle, the reference's `immobilityDuration` and `noJumpOrCloakDuration`).
    pub(super) fn hold_for_windows(
        &mut self,
        session: SessionId,
        immobile_ms: f64,
        no_jump_ms: f64,
        at: i64,
    ) {
        let per_ms = TICKS_PER_SECOND as f64 / 1000.0;
        let Some(pilot) = self.pilots.get_mut(&session) else { return };
        pilot.immobile_until = pilot.immobile_until.max(at + (immobile_ms * per_ms) as i64);
        pilot.no_jump_until = pilot.no_jump_until.max(at + (no_jump_ms * per_ms) as i64);
        pilot.ship.velocity = eve_destiny::Vec3::new(0.0, 0.0, 0.0);
    }

    /// The titan doomsday's hit: the locked target takes the damage once, if it is still in the scene and not under its undocking protection (the
    /// reference's `tickDoomsdayEffect`, which hits the primary target alone: a titan has no lightning chain).
    pub(super) fn doomsday_hit(&mut self, session: SessionId, index: usize, at: i64) -> Vec<Out> {
        let (module_type, target_id, damage) = {
            let Some(pilot) = self.pilots.get_mut(&session) else { return Vec::new() };
            let Some(run) = pilot.modules.running.get_mut(index) else { return Vec::new() };
            let Some(Action::Doomsday { damage, .. }) = run.effect.action.clone() else { return Vec::new() };
            if run.doomsday_hit {
                return Vec::new();
            }
            run.doomsday_hit = true;
            (run.effect.module_type, run.effect.target_id, damage)
        };
        let Some(target_session) = self.session_of(target_id) else { return Vec::new() };
        if self.pilots.get(&target_session).is_some_and(|target| target.protected_until > at) {
            return Vec::new();
        }
        self.damage_ship(session, module_type, (target_session, target_id), damage, at)
    }

    /// The energy neutralisation of a strike: each other ship whose surface is within `neut_radius` of the firing ship loses `neut_amount` times its
    /// signature factor (the same factor a neutralizer uses). Target resistances are not applied (the existing neutralizers do not apply them either).
    pub(super) fn strike_neutralise(&mut self, session: SessionId, strike: Strike, at: i64) -> Vec<Out> {
        if strike.neut_amount <= 0.0 || strike.neut_radius <= 0.0 {
            return Vec::new();
        }
        let Some(centre) = self.pilots.get(&session).map(|pilot| pilot.ship.position) else {
            return Vec::new();
        };
        let targets: Vec<(SessionId, f64)> = self
            .in_entry_order()
            .into_iter()
            .filter(|other| *other != session)
            .filter_map(|other| {
                let pilot = self.pilots.get(&other)?;
                let p = pilot.ship.position;
                let gap = ((p.x - centre.x).powi(2) + (p.y - centre.y).powi(2) + (p.z - centre.z).powi(2))
                    .sqrt()
                    - pilot.ship.spec.radius.max(0.0);
                let signature = pilot.ship.spec.signature_radius;
                let factor =
                    if signature > 0.0 { (signature / strike.neut_signature_radius).min(1.0) } else { 1.0 };
                let factor = factor * capacitor_warfare_multiplier(pilot.ship.spec.energy_warfare_resistance);
                (gap <= strike.neut_radius).then_some((other, factor))
            })
            .collect();
        let mut out = Vec::new();
        for (target, factor) in targets {
            out.extend(self.take_capacitor(target, strike.neut_amount * factor, at).0);
        }
        out
    }

    /// What the target's capacitor warfare resistance leaves of a neutralisation (`energyWarfareResistance`, 2045).
    pub(crate) fn capacitor_warfare_multiplier(&self, target: SessionId) -> f64 {
        self.pilots
            .get(&target)
            .map_or(1.0, |pilot| capacitor_warfare_multiplier(pilot.ship.spec.energy_warfare_resistance))
    }

    /// The next pulse of a strike is due: every ship in the area is hit by the pulse's damage times its signature factor, and the pulse is counted.
    /// A transport field's pulse (protocol 29): the ships of `moved` inside its sphere are carried to `destination` (an offset from the firing ship's
    /// centre) and stop; the ships of `held` inside it cannot warp for `warp_block_ms`. The sphere is centred on the aim point, `centre + direction *
    /// length`. A protected ship (undocking) is not reached, and a ship that is warping or jumping is not carried.
    #[allow(clippy::too_many_arguments)]
    pub(super) fn transport_field(
        &mut self,
        session: SessionId,
        strike: Strike,
        centre: eve_destiny::Vec3,
        destination: [f64; 3],
        warp_block_ms: f64,
        moved: &[i64],
        held: &[i64],
        at: i64,
    ) -> Vec<Out> {
        let sphere = [
            centre.x + strike.direction[0] * strike.length,
            centre.y + strike.direction[1] * strike.length,
            centre.z + strike.direction[2] * strike.length,
        ];
        let target = [centre.x + destination[0], centre.y + destination[1], centre.z + destination[2]];
        let per_ms = TICKS_PER_SECOND as f64 / 1000.0;
        let warp_until = at + (warp_block_ms * per_ms) as i64;
        let reached: Vec<SessionId> = self
            .in_entry_order()
            .into_iter()
            .filter(|other| *other != session)
            .filter(|other| {
                self.pilots.get(other).is_some_and(|pilot| {
                    let p = pilot.ship.position;
                    pilot.protected_until <= at
                        && strike.covers(sphere, [p.x, p.y, p.z], pilot.ship.spec.radius.max(0.0))
                })
            })
            .collect();
        for other in reached {
            let Some(pilot) = self.pilots.get_mut(&other) else { continue };
            let ship_id = pilot.ship.spec.ship_id;
            if moved.contains(&ship_id) {
                if pilot.flight.is_some() || pilot.jump.is_some() {
                    continue;
                }
                pilot.ship.position = eve_destiny::Vec3::new(target[0], target[1], target[2]);
                pilot.ship.velocity = eve_destiny::Vec3::new(0.0, 0.0, 0.0);
            } else if held.contains(&ship_id) {
                pilot.warp_blocked_until = pilot.warp_blocked_until.max(warp_until);
            }
        }
        Vec::new()
    }

    pub(super) fn strike_pulse(&mut self, session: SessionId, index: usize, at: i64) -> Vec<Out> {
        let (module_type, pulse, strike, centre, radius, transport) = {
            let Some(pilot) = self.pilots.get_mut(&session) else { return Vec::new() };
            let Some(run) = pilot.modules.running.get_mut(index) else { return Vec::new() };
            let Some(action) = run.effect.action.clone() else { return Vec::new() };
            let Some(strike) = strike_of(Some(&action)) else { return Vec::new() };
            if run.strike_done >= strike.pulses {
                return Vec::new();
            }
            let pulse = run.strike_done;
            run.strike_done += 1;
            let transport = match action {
                Action::Field { destination, warp_block_ms, moved, held, .. } => {
                    Some((destination, warp_block_ms, moved, held))
                }
                _ => None,
            };
            (
                run.effect.module_type,
                pulse,
                strike,
                pilot.ship.position,
                pilot.ship.spec.radius.max(0.0),
                transport,
            )
        };
        // a transport field carries its ships at its pulse and deals no damage
        if let Some((destination, warp_block_ms, moved, held)) = transport {
            return self.transport_field(
                session,
                strike,
                centre,
                destination,
                warp_block_ms,
                &moved,
                &held,
                at,
            );
        }
        // a sweep (a reaper) has its axis at this pulse; a pulse whose axis has no length hits nothing
        let Some(strike) = strike.at_pulse(pulse) else { return Vec::new() };
        let direction = strike.direction;
        // the area starts at the hull, along the aim (the reference's hull surface origin); a sweep starts at the centre (`resolveSlashDamageVolume`)
        let origin = if strike.sweep_end.is_some() {
            [centre.x, centre.y, centre.z]
        } else {
            [
                centre.x + direction[0] * radius,
                centre.y + direction[1] * radius,
                centre.z + direction[2] * radius,
            ]
        };
        let targets: Vec<(SessionId, i64, f64)> = self
            .in_entry_order()
            .into_iter()
            .filter(|other| *other != session)
            .filter_map(|other| {
                let target = self.pilots.get(&other)?;
                if target.protected_until > at {
                    return None;
                }
                let p = target.ship.position;
                let target_radius = target.ship.spec.radius.max(0.0);
                if !strike.covers(origin, [p.x, p.y, p.z], target_radius) {
                    return None;
                }
                let signature = target.ship.spec.signature_radius;
                let factor =
                    if signature > 0.0 { (signature / strike.signature_radius).min(1.0) } else { 1.0 };
                Some((other, target.ship.spec.ship_id, factor))
            })
            .collect();
        // the debuff lands on every ship the pulse covers, whatever its signature takes of the damage (`applyDisruptiveLanceDebuff`)
        if strike.debuff_ms > 0.0 {
            let until = at + (strike.debuff_ms * TICKS_PER_SECOND as f64 / 1000.0) as i64;
            for (target_session, _, _) in &targets {
                if let Some(pilot) = self.pilots.get_mut(target_session) {
                    pilot.disrupted_until = pilot.disrupted_until.max(until);
                }
            }
        }
        let mut out = Vec::new();
        for (target_session, target_id, factor) in targets {
            let damage = strike.damage.map(|amount| amount * factor);
            out.extend(self.damage_ship(session, module_type, (target_session, target_id), damage, at));
        }
        out
    }

    pub(crate) fn take_capacitor(&mut self, target: SessionId, amount: f64, at: i64) -> (Vec<Out>, f64) {
        let Some(pilot) = self.pilots.get_mut(&target) else { return (Vec::new(), 0.0) };
        let (owner, ship) = (pilot.ship.spec.character_id, pilot.ship.spec.ship_id);
        let Some(capacitor) = pilot.modules.capacitor.as_mut() else { return (Vec::new(), 0.0) };
        let taken = capacitor.charge_at(at).min(amount.max(0.0));
        if taken <= 0.0 {
            return (Vec::new(), 0.0);
        }
        let (before, after) = capacitor.draw(at, taken);
        (vec![Self::charge_told(target, owner, ship, at, after, before)], taken)
    }

    /// A nosferatu: what it can take from the capacitor of `target` goes into that of the ship that runs it. It takes nothing from a ship that holds no more than
    /// it does (by amount, not by share: the reference's `resolveNosTransferAllowed`), and no more than the ship has room for.
    pub(super) fn drain_capacitor(
        &mut self,
        session: SessionId,
        target: SessionId,
        amount: f64,
        at: i64,
    ) -> Vec<Out> {
        let level = |scene: &Self, who: SessionId| {
            scene
                .pilots
                .get(&who)
                .and_then(|pilot| pilot.modules.capacitor.as_ref())
                .map(|c| (c.charge_at(at), c.capacity))
        };
        let (Some((own, room)), Some((theirs, _))) = (level(self, session), level(self, target)) else {
            return Vec::new();
        };
        if own + 1e-6 >= theirs {
            return Vec::new();
        }
        let wanted = amount.min(theirs).min((room - own).max(0.0));
        let (mut out, taken) = self.take_capacitor(target, wanted, at);
        if taken > 0.0 {
            out.extend(self.give_capacitor(session, taken, at));
        }
        out
    }

    /// An effect is over: the client is told the values it gave back, that it is off, and the ship's numbers and the effect are taken off in one update.
    /// An effect cut short by the loss of its target (`target_lost`) is told in the other order: the update first, then the values, then that it is off
    /// (the recording of the reference's gun).
    pub(crate) fn effect_ended(
        &mut self,
        session: SessionId,
        run: Running,
        owner: i64,
        ship_id: i64,
        at: i64,
        ending: Ending,
    ) -> Vec<Out> {
        let (target_lost, out_of_rounds) =
            (matches!(ending, Ending::TargetLost | Ending::Forced), ending == Ending::NoRounds);
        // a reactive armor hardener that stops leaves the armor its own resonances, and keeps nothing for a later start
        if matches!(run.effect.action, Some(Action::ResistanceShift { .. }))
            && let Some(pilot) = self.pilots.get_mut(&session)
        {
            pilot.ship.health.stop_shift();
        }
        let mut out = Vec::new();
        let changes = Self::changes_told(session, owner, &run.effect, &run.effect.off_changes, at);
        let error = out_of_rounds.then(|| no_charges(run.effect.module_type));
        let off = Self::effect_told(session, owner, ship_id, &run, at, false, run.cycle_start, error);
        let stamp = self.next_stamp(at);
        let mut actions = match run.effect.off {
            Some(numbers) => self.renumber(session, numbers, stamp),
            None => Vec::new(),
        };
        actions.push(Self::fx_action(stamp, &id(ship_id), &run.effect, false));
        let update = self.tell_all(session, actions);
        if target_lost {
            out.extend(update);
            out.extend(changes);
            out.push(off);
        } else {
            out.extend(changes);
            out.push(off);
            out.extend(update);
        }
        out.extend(self.jam_told(session, &run.effect, at, Jam::End));
        // a tractor beam that stops lets go of the wreck it pulled (another ship's pull on that wreck is left alone)
        if let Some(Action::Tractor { .. }) = run.effect.action {
            out.extend(self.tractor_release(run.effect.target_id, ship_id, at));
        }
        // what it did to its target for as long as it ran is lifted
        if run
            .effect
            .remote()
            .is_some_and(|(what, ..)| what.lasting(1.0).is_some() || matches!(what, Remote::Jam { .. }))
        {
            out.extend(self.release(ship_id, run.effect.module_id, run.effect.target_id, at));
        }
        if matches!(run.effect.action.as_ref(), Some(Action::Cloak { .. })) {
            let changed = if let Some(pilot) = self.pilots.get_mut(&session) {
                let changed = pilot.cloak != crate::visibility::CloakState::Uncloaked;
                pilot.cloak = crate::visibility::CloakState::Uncloaked;
                changed
            } else {
                false
            };
            if changed {
                self.boxes.clear();
            }
        }
        // the host is told every end once, so the ship it sends no longer has the module running (FP-C1)
        out.push(Out::ModuleEnded { session, module_id: run.effect.module_id, reason: ending.wire_reason() });
        out
    }

    /// A module whose effect is forbidden while it runs is stopped immediately, mid-cycle.
    pub(crate) fn stop_module(&mut self, session: SessionId, module_id: i64, now: i64) -> Vec<Out> {
        self.offline_module(session, module_id, now)
    }

    /// Taking a module offline interrupts its running effect immediately, even during a cycle.
    /// Ported from EVE.js moduleOperation.js at commit 828823a75f211fdecb945b087a26c225f111491f:
    /// https://github.com/Fokiiiiiii/evejs-v0.12.9/blob/828823a75f211fdecb945b087a26c225f111491f/server/src/services/dogma/dogmaService/moduleOperation.js#L2607-L2618
    /// The caller remains responsible for persisting the module's online state.
    pub(crate) fn offline_module(&mut self, session: SessionId, module_id: i64, now: i64) -> Vec<Out> {
        let Some(pilot) = self.pilots.get_mut(&session) else { return Vec::new() };
        let (owner, ship_id) = (pilot.ship.spec.character_id, pilot.ship.spec.ship_id);
        let Some(index) = pilot.modules.running.iter().position(|run| run.effect.module_id == module_id)
        else {
            return Vec::new();
        };
        let run = pilot.modules.running.remove(index);
        self.effect_ended(session, run, owner, ship_id, now, Ending::Forced)
    }

    /// A microwarpdrive that runs on the ship is stopped at once (a warp scrambler that blocks it has landed).
    pub(crate) fn stop_microwarpdrives(&mut self, session: SessionId, now: i64) -> Vec<Out> {
        let Some(pilot) = self.pilots.get_mut(&session) else { return Vec::new() };
        let (owner, ship_id) = (pilot.ship.spec.character_id, pilot.ship.spec.ship_id);
        let (stopped, kept): (Vec<Running>, Vec<Running>) = std::mem::take(&mut pilot.modules.running)
            .into_iter()
            .partition(|run| run.effect.effect_id == MICROWARPDRIVE);
        pilot.modules.running = kept;
        let mut out = Vec::new();
        for run in stopped {
            out.extend(self.effect_ended(session, run, owner, ship_id, now, Ending::Forced));
        }
        out
    }

    pub(crate) fn stop_weapons_at(&mut self, session: SessionId, targets: &[i64], now: i64) -> Vec<Out> {
        let Some(pilot) = self.pilots.get_mut(&session) else { return Vec::new() };
        let (owner, ship_id) = (pilot.ship.spec.character_id, pilot.ship.spec.ship_id);
        let (stopped, kept): (Vec<Running>, Vec<Running>) =
            std::mem::take(&mut pilot.modules.running).into_iter().partition(|run| {
                (run.effect.turret.is_some() || run.effect.remote().is_some())
                    && targets.contains(&run.effect.target_id)
            });
        pilot.modules.running = kept;
        let mut out = Vec::new();
        for run in stopped {
            out.extend(self.effect_ended(session, run, owner, ship_id, now, Ending::TargetLost));
        }
        out
    }
}
