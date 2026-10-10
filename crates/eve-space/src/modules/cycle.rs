use super::*;

/// The host's answer to one module work cycle (`ModuleWorkResult`): the cycle it answers and what the host decided.
pub struct ModuleWorkAnswer<'a> {
    pub target_id: i64,
    pub cycle_id: u64,
    pub accepted: bool,
    pub reason: &'a str,
    pub depleted: bool,
}

impl Scene {
    /// A cycle ends: what it does at its end is done, then the next begins (and costs capacitor), or, when the pilot asked to stop or the capacitor cannot pay, the
    /// effect ends.
    pub(super) fn end_cycle(&mut self, session: SessionId, index: usize, at: i64) -> Vec<Out> {
        let Some(pilot) = self.pilots.get(&session) else { return Vec::new() };
        let run = pilot.modules.running[index].clone();
        if let Some(Action::MicroJumpDrive { distance_meters, cooldown_ms }) = run.effect.action.as_ref() {
            let module_id = run.effect.module_id;
            let cooldown_until = at.saturating_add((*cooldown_ms).saturating_mul(TICKS_PER_SECOND / 1000));
            let scrammed = self.pilots.get(&session).is_some_and(|pilot| pilot.incoming.mwd_blocked());
            let (owner, ship_id) = {
                let Some(pilot) = self.pilots.get_mut(&session) else { return Vec::new() };
                pilot.modules.reactivation.retain(|(id, _)| *id != module_id);
                pilot.modules.reactivation.push((module_id, cooldown_until));
                let identity = (pilot.ship.spec.character_id, pilot.ship.spec.ship_id);
                pilot.modules.running.remove(index);
                identity
            };
            let mut out =
                if scrammed { Vec::new() } else { self.perform_micro_jump(session, *distance_meters, at) };
            out.extend(self.effect_ended(session, run, owner, ship_id, at, Ending::Done));
            return out;
        }
        // a weapon goes on while its target is there and locked, an effect projected at a ship while the ship is also in reach
        let target_ok = self.cycle_target_ok(session, &run.effect, run.charged_this_cycle);
        // a work cycle that ends with its target there is handed to the host, and the module is parked until the answer (ADR 0013)
        if target_ok
            && let Some(work) = run
                .effect
                .action
                .as_ref()
                .and_then(|action| action.with_charges(run.charged_this_cycle))
                .filter(|action| action.is_work())
        {
            return self.park_work_cycle(session, run.effect.module_id, work, at);
        }
        if target_ok
            && let Some(Action::Remote {
                what: Remote::MutadaptiveRepair { amount, bonus_per_cycle, bonus_max, .. },
                when: Applies::CycleEnd,
                ..
            }) = run.effect.action.as_ref()
        {
            let range_share = run
                .effect
                .remote()
                .and_then(|(_, reach, _)| {
                    self.session_of(run.effect.target_id)
                        .and_then(|target| self.apart(session, target))
                        .and_then(|apart| reach.share(apart))
                })
                .expect("a valid remote-repair target has a range share");
            let Some(pilot) = self.pilots.get_mut(&session) else { return Vec::new() };
            let Some(running_index) = pilot
                .modules
                .running
                .iter()
                .position(|current| current.effect.module_id == run.effect.module_id)
            else {
                return Vec::new();
            };
            let current = &pilot.modules.running[running_index];
            if current.pending_remote_repair.is_some() {
                return Vec::new();
            }
            let cycle_number = current.cycles;
            let ends_effect_after_cycle = current.stopping
                || current.cycles >= current.effect.repeat
                || !pilot
                    .modules
                    .capacitor
                    .as_ref()
                    .is_some_and(|c| c.charge_at(at) >= run.effect.capacitor_need);
            let cycle_id = pilot.modules.allocate_remote_repair_cycle_id();
            pilot.modules.running[running_index].pending_remote_repair =
                Some(PendingRemoteRepairCycle { cycle_id, at, range_share });
            let cycle_at_ms = (i128::from(at) * 1_000 / i128::from(TICKS_PER_SECOND)) as i64;
            let cycle_duration_ms = run.effect.duration_ms.round().max(1.0) as i64;
            return vec![Out::RemoteRepairCycle {
                session,
                module_id: run.effect.module_id,
                target_id: run.effect.target_id,
                cycle_id,
                cycle_number,
                cycle_at_ms,
                cycle_duration_ms,
                ends_effect_after_cycle,
                base_amount: *amount,
                bonus_per_cycle: *bonus_per_cycle,
                bonus_max: *bonus_max,
            }];
        }
        // what the cycle that ends does at its end is done before the effect goes on or ends
        let out = self.act(session, &run.effect, Applies::CycleEnd, at);
        // an effect that burns fuel asks for the fuel of the next cycle before that cycle starts (`WORK_FUEL`), when there is one to start
        if target_ok && run.effect.burns_fuel && self.next_cycle_follows(session, run.effect.module_id, at) {
            return self.park_fuel_cycle(session, run.effect.module_id, at, out);
        }
        self.finish_cycle(session, run.effect.module_id, at, out, target_ok)
    }

    pub(super) fn finish_cycle(
        &mut self,
        session: SessionId,
        module_id: i64,
        at: i64,
        mut out: Vec<Out>,
        target_ok: bool,
    ) -> Vec<Out> {
        let Some(pilot) = self.pilots.get_mut(&session) else { return out };
        let Some(index) = pilot.modules.running.iter().position(|run| run.effect.module_id == module_id)
        else {
            return out;
        };
        let (owner, ship_id) = (pilot.ship.spec.character_id, pilot.ship.spec.ship_id);
        // a refresh the host sent takes effect at this cycle's start, before what the cycle costs is worked out
        let refresh = refresh_running(&mut pilot.modules.running[index], session, owner, at);
        let run = pilot.modules.running[index].clone();
        let affordable =
            pilot.modules.capacitor.as_ref().is_some_and(|c| c.charge_at(at) >= run.effect.capacitor_need);
        let more = run.cycles < run.effect.repeat;
        if run.stopping || !more || !affordable || !target_ok {
            let ending = if target_ok { Ending::Done } else { Ending::TargetGone };
            pilot.modules.running.remove(index);
            out.extend(self.effect_ended(session, run, owner, ship_id, at, ending));
            return out;
        }
        // the host took the rounds out while the gun ran (a pilot unloaded it in space): it stops with no shot
        let no_action_charge = matches!(
            run.effect.action.as_ref(),
            Some(Action::Charged { fallback: None, .. }) if run.rounds.unwrap_or(0) <= 0
        );
        if no_action_charge
            || run
                .effect
                .turret
                .as_ref()
                .and_then(|t| t.charge)
                .zip(run.rounds)
                .is_some_and(|(charge, rounds)| rounds < charge.per_cycle)
        {
            pilot.modules.running.remove(index);
            out.extend(self.effect_ended(session, run, owner, ship_id, at, Ending::NoRounds));
            return out;
        }
        out.extend(refresh);
        let (before, after) = match pilot.modules.capacitor.as_mut() {
            Some(capacitor) => capacitor.draw(at, run.effect.capacitor_need),
            None => (0.0, 0.0),
        };
        let next = &mut pilot.modules.running[index];
        next.cycle_start = at;
        next.cycles += 1;
        next.charged_this_cycle = next.rounds.is_some_and(|left| left > 0);
        next.pending_remote_repair = None;
        let next = next.clone();
        out.push(Self::charge_told(session, owner, ship_id, at, after, before));
        if next.effect.turret.is_none() {
            out.push(Self::effect_told(session, owner, ship_id, &next, at, true, at, None));
            out.extend(self.act(session, &next.effect, Applies::CycleStart, at));
            out.extend(self.jam_told(session, &next.effect, at, Jam::Refresh));
            let (spent, goes_on) = self.spend_round(session, next.effect.module_id, at);
            out.extend(spent);
            let stop_without_charge =
                matches!(next.effect.action.as_ref(), Some(Action::Charged { fallback: None, .. }));
            if !goes_on
                && stop_without_charge
                && let Some(pilot) = self.pilots.get_mut(&session)
                && let Some(position) =
                    pilot.modules.running.iter().position(|run| run.effect.module_id == next.effect.module_id)
            {
                let run = pilot.modules.running.remove(position);
                out.extend(self.effect_ended(session, run, owner, ship_id, at, Ending::NoRounds));
            }
            return out;
        }
        // a turret shoots, spends its round and then is told to have begun another cycle (the recording of the reference's gun); one that is out of rounds stops
        out.extend(self.fire_turret(session, &next.effect, at));
        let (spent, goes_on) = self.spend_round(session, next.effect.module_id, at);
        out.extend(spent);
        if goes_on {
            // a launcher is not told again each cycle: the recording of the reference's launcher has the effect told on at its start and off at its end, with the rounds between
            if next.effect.turret.as_ref().is_none_or(|turret| turret.missile.is_none()) {
                out.push(Self::effect_told(session, owner, ship_id, &next, at, true, at, None));
            }
        } else if !self.park_for_reload(session, next.effect.module_id)
            && let Some(pilot) = self.pilots.get_mut(&session)
            && let Some(position) =
                pilot.modules.running.iter().position(|run| run.effect.module_id == next.effect.module_id)
        {
            let run = pilot.modules.running.remove(position);
            out.extend(self.effect_ended(session, run, owner, ship_id, at, Ending::NoRounds));
        }
        out
    }

    /// Settles the host's amount for the exact pending module/target cycle; stale or replayed results do nothing.
    pub fn remote_repair_cycle_result(
        &mut self,
        session: SessionId,
        module_id: i64,
        target_id: i64,
        cycle_id: u64,
        accepted: bool,
        amount: f64,
    ) -> Vec<Out> {
        let Some(pilot) = self.pilots.get(&session) else { return Vec::new() };
        let Some(index) = pilot.modules.running.iter().position(|run| {
            run.effect.module_id == module_id
                && run.effect.target_id == target_id
                && run.pending_remote_repair.is_some_and(|pending| pending.cycle_id == cycle_id)
        }) else {
            return Vec::new();
        };
        let run = pilot.modules.running[index].clone();
        let pending = run.pending_remote_repair.expect("matched pending remote-repair cycle");
        if !accepted || !amount.is_finite() || amount < 0.0 {
            let (owner, ship_id) = (pilot.ship.spec.character_id, pilot.ship.spec.ship_id);
            let Some(pilot) = self.pilots.get_mut(&session) else { return Vec::new() };
            let run = pilot.modules.running.remove(index);
            return self.effect_ended(session, run, owner, ship_id, pending.at, Ending::Done);
        }
        let Some(Action::Remote { what: Remote::MutadaptiveRepair { layer, .. }, .. }) =
            run.effect.action.as_ref()
        else {
            let (owner, ship_id) = (pilot.ship.spec.character_id, pilot.ship.spec.ship_id);
            let Some(pilot) = self.pilots.get_mut(&session) else { return Vec::new() };
            let run = pilot.modules.running.remove(index);
            return self.effect_ended(session, run, owner, ship_id, pending.at, Ending::Done);
        };
        let mut out = self.mend_remote(target_id, *layer, amount * pending.range_share, pending.at);
        if let Some(pilot) = self.pilots.get_mut(&session)
            && let Some(current) = pilot.modules.running.get_mut(index)
            && current.pending_remote_repair.is_some_and(|current| current.cycle_id == cycle_id)
        {
            current.pending_remote_repair = None;
        } else {
            return out;
        }
        out.extend(self.finish_cycle(session, module_id, pending.at, Vec::new(), true));
        out
    }

    /// A work target (a rock, a wreck) is there while the pilot holds its lock: a depleted rock drops its locks.
    pub(super) fn work_target_ok(&self, session: SessionId, effect: &Effect) -> bool {
        // a command burst reaches its ships, not a target: it is there while its pilot runs it
        if effect.action.as_ref().is_some_and(Action::targets_nothing) {
            return true;
        }
        let locked = self.locked_by(session).contains(&effect.target_id);
        // a scan's target is also in its reach: the lock alone is not enough once the ship has moved off (scanner range, cycle end)
        let in_reach = match effect.action.as_ref() {
            Some(Action::Scan { reach, .. }) => self
                .session_of(effect.target_id)
                .and_then(|target| self.apart(session, target))
                .is_some_and(|apart| reach.share(apart).is_some()),
            _ => true,
        };
        locked && in_reach
    }

    /// Whether the target of the cycle that ends is still there, as the effect needs it: a turret's, a projected effect's, a work target's (or none).
    pub(super) fn cycle_target_ok(&self, session: SessionId, effect: &Effect, charged: bool) -> bool {
        let self_capacitor_boost =
            effect.remote().is_some_and(|(what, ..)| matches!(what, Remote::Transfer { .. }))
                && self.pilots.get(&session).is_some_and(|pilot| effect.target_id == pilot.ship.spec.ship_id);
        let self_sensor_boost =
            self.pilots.get(&session).is_some_and(|pilot| effect.target_id == pilot.ship.spec.ship_id)
                && effect.jam() == Some("remoteSensorBoost")
                && effect.remote().is_some_and(|(what, ..)| {
                    matches!(
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
                    )
                });
        if self_capacitor_boost || self_sensor_boost {
            true
        } else if effect.turret.is_some() {
            // shortcut: a spool turret is checked at the cycle boundary, not on every tick; it stops at most one cycle after the target leaves optimal
            self.turret_target_ok(session, effect) && !self.spool_target_out_of_optimal(session, effect)
        } else if let Some((_, reach, _)) = effect.remote() {
            self.turret_target_ok(session, effect)
                && self
                    .session_of(effect.target_id)
                    .and_then(|target| self.apart(session, target))
                    .is_some_and(|apart| reach.share(apart).is_some())
        } else if let Some(Action::Tractor { range, .. }) = effect.action.as_ref() {
            // a tractor's target is a wreck or a can it holds the lock on, within reach
            self.tractor_target_ok(session, effect.target_id, *range)
        } else if effect
            .action
            .as_ref()
            .and_then(|action| action.with_charges(charged))
            .is_some_and(Action::is_work)
        {
            // a work target (a rock, a wreck) is there while the pilot holds its lock
            self.work_target_ok(session, effect)
        } else {
            true
        }
    }

    /// The ships a command burst of `range_meters` reaches from this ship, as the scene measures them: every ship of the scene within the range plus one metre
    /// (surface to surface), the source among them. The host decides which of them its fleet has, and caps the list (`BurstRecipients`).
    pub(super) fn burst_recipients(&self, session: SessionId, range_meters: f64) -> Vec<i64> {
        let mut ships: Vec<i64> = self
            .pilots
            .iter()
            .filter(|(other, _)| {
                **other == session
                    || self.apart(session, **other).is_some_and(|apart| apart <= range_meters + 1.0)
            })
            .map(|(_, pilot)| pilot.ship.spec.ship_id)
            .collect();
        ships.sort_unstable();
        ships
    }

    /// The `ModuleWorkCycle` that asks the host to grant a P.A.N.I.C. its fleet's buffs before it starts (`WORK_PANIC`), with the ships in reach (the source among them).
    pub(super) fn panic_cycle_out(
        &self,
        session: SessionId,
        module_id: i64,
        target_id: i64,
        cycle_id: u64,
        recipients: Vec<i64>,
    ) -> Option<Out> {
        let pilot = self.pilots.get(&session)?;
        Some(Out::ModuleWorkCycle {
            session,
            character_id: pilot.ship.spec.character_id,
            ship_id: pilot.ship.spec.ship_id,
            solar_system_id: self.solar_system_id(),
            module_id,
            target_id,
            cycle_id,
            command: WORK_PANIC,
            mining_amount_m3: 0.0,
            waste_multiplier: 0.0,
            waste_probability: 0.0,
            crit_probability: 0.0,
            crit_multiplier: 0.0,
            access_difficulty_bonus: 0.0,
            waste_roll: 0.0,
            rounding_roll: 0.0,
            crit_roll: 0.0,
            recipients,
        })
    }

    /// The `ModuleWorkCycle` that asks the host for the fuel of a cycle (`WORK_FUEL`): nothing is mined or salvaged, the host takes the fuel or refuses.
    pub(super) fn fuel_cycle_out(
        &self,
        session: SessionId,
        module_id: i64,
        target_id: i64,
        cycle_id: u64,
    ) -> Option<Out> {
        let pilot = self.pilots.get(&session)?;
        Some(Out::ModuleWorkCycle {
            session,
            character_id: pilot.ship.spec.character_id,
            ship_id: pilot.ship.spec.ship_id,
            solar_system_id: self.solar_system_id(),
            module_id,
            target_id,
            cycle_id,
            command: WORK_FUEL,
            recipients: Vec::new(),
            mining_amount_m3: 0.0,
            waste_multiplier: 0.0,
            waste_probability: 0.0,
            crit_probability: 0.0,
            crit_multiplier: 0.0,
            access_difficulty_bonus: 0.0,
            waste_roll: 0.0,
            rounding_roll: 0.0,
            crit_roll: 0.0,
        })
    }

    /// The effect of a running cycle goes on past its end only when the next cycle starts: the cycles are not done, the effect is not stopping, the capacitor
    /// pays for the next one. Only then is its fuel asked for.
    pub(super) fn next_cycle_follows(&self, session: SessionId, module_id: i64, at: i64) -> bool {
        self.pilots.get(&session).is_some_and(|pilot| {
            pilot.modules.running.iter().any(|run| {
                run.effect.module_id == module_id
                    && !run.stopping
                    && run.cycles < run.effect.repeat
                    && pilot
                        .modules
                        .capacitor
                        .as_ref()
                        .is_some_and(|c| c.charge_at(at) >= run.effect.capacitor_need)
            })
        })
    }

    /// The end of a cycle of an effect that burns fuel, with the next cycle to come: the host is asked for its fuel first, and the module is parked on the answer.
    pub(super) fn park_fuel_cycle(
        &mut self,
        session: SessionId,
        module_id: i64,
        at: i64,
        mut out: Vec<Out>,
    ) -> Vec<Out> {
        let cycle_id = next_work_cycle_id();
        let Some(pilot) = self.pilots.get_mut(&session) else { return out };
        let Some(run) = pilot.modules.running.iter_mut().find(|run| run.effect.module_id == module_id) else {
            return out;
        };
        let target_id = run.effect.target_id;
        run.pending_work = Some(PendingWorkCycle { cycle_id, at, sent_at: at, rolls: [0.0; 3], fuel: true });
        out.extend(self.fuel_cycle_out(session, module_id, target_id, cycle_id));
        out
    }

    /// A parked start is due again: the same id is asked once more (the host answers a repeat from its record).
    pub(super) fn resend_start(&mut self, session: SessionId, index: usize, at: i64) -> Vec<Out> {
        let Some(pilot) = self.pilots.get_mut(&session) else { return Vec::new() };
        let Some(start) = pilot.modules.pending_starts.get_mut(index) else { return Vec::new() };
        start.sent_at = at;
        let (module_id, target_id, cycle_id) =
            (start.effect.module_id, start.effect.target_id, start.cycle_id);
        match start.effect.action {
            // a P.A.N.I.C. asks the ships in reach again, under the same id (the host answers a repeat from its record)
            Some(Action::Panic { range_meters }) => {
                let recipients = self.burst_recipients(session, range_meters);
                self.panic_cycle_out(session, module_id, target_id, cycle_id, recipients)
                    .into_iter()
                    .collect()
            }
            _ => self.fuel_cycle_out(session, module_id, target_id, cycle_id).into_iter().collect(),
        }
    }

    /// The reason a P.A.N.I.C. whose grant the host refused is shown with: a ship the host no longer has in space as that, anything else as a refused grant.
    pub(super) fn panic_refusal(reason: &str) -> &'static str {
        match reason {
            "NO_PILOT" | "NO_SHIP" | "NOT_CONTROLLED" => "NO_SHIP_IN_SPACE",
            _ => "PANIC_REFUSED",
        }
    }

    /// The reason a refused start is shown with: the host's `NO_FUEL` as it is; a ship the host no longer has in space as that; anything else as a refused fuel.
    pub(super) fn fuel_refusal(reason: &str) -> &'static str {
        match reason {
            "NO_FUEL" => "NO_FUEL",
            "NO_PILOT" | "NO_SHIP" | "NOT_CONTROLLED" => "NO_SHIP_IN_SPACE",
            _ => "FUEL_REFUSED",
        }
    }

    /// The work cycle of a running effect as the host is told it (`ModuleWorkCycle`), for `work` (the action that runs now) and the cycle sent.
    pub(super) fn work_cycle_out(
        &self,
        session: SessionId,
        module_id: i64,
        target_id: i64,
        work: &Action,
        pending: PendingWorkCycle,
    ) -> Option<Out> {
        let pilot = self.pilots.get(&session)?;
        let (character_id, ship_id) = (pilot.ship.spec.character_id, pilot.ship.spec.ship_id);
        let (
            command,
            mining_amount_m3,
            waste_multiplier,
            waste_probability,
            crit_probability,
            crit_multiplier,
            access_difficulty_bonus,
        ) = match work {
            Action::Mining {
                mining_amount_m3,
                waste_multiplier,
                waste_probability,
                crit_probability,
                crit_multiplier,
                ..
            } => (
                WORK_MINE,
                *mining_amount_m3,
                *waste_multiplier,
                *waste_probability,
                *crit_probability,
                *crit_multiplier,
                0.0,
            ),
            Action::CommandBurst { .. } => (WORK_BURST, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0),
            Action::Salvage { access_bonus } => (WORK_SALVAGE, 0.0, 0.0, 0.0, 0.0, 0.0, *access_bonus),
            // a scan carries no mining numbers: the host reads the target and decides what it reveals
            Action::Scan { cargo, .. } => {
                (if *cargo { WORK_SCAN_CARGO } else { WORK_SCAN_SHIP }, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0)
            }
            _ => return None,
        };
        let recipients = match work {
            Action::CommandBurst { range_meters } => self.burst_recipients(session, *range_meters),
            _ => Vec::new(),
        };
        Some(Out::ModuleWorkCycle {
            session,
            character_id,
            ship_id,
            solar_system_id: self.solar_system_id(),
            module_id,
            target_id,
            cycle_id: pending.cycle_id,
            command,
            mining_amount_m3,
            waste_multiplier,
            waste_probability,
            crit_probability,
            crit_multiplier,
            access_difficulty_bonus,
            waste_roll: pending.rolls[0],
            rounding_roll: pending.rolls[1],
            crit_roll: pending.rolls[2],
            recipients,
        })
    }

    /// A work cycle ends with its target there: the host is asked for its outcome, and the module is parked until the answer (ADR 0013). Nothing of the
    /// cycle's end is done until then; the next cycle is timed from the answer.
    pub(super) fn park_work_cycle(
        &mut self,
        session: SessionId,
        module_id: i64,
        work: &Action,
        at: i64,
    ) -> Vec<Out> {
        let rolls = [self.rng.random(), self.rng.random(), self.rng.random()];
        let pending =
            PendingWorkCycle { cycle_id: next_work_cycle_id(), at, sent_at: at, rolls, fuel: false };
        let Some(pilot) = self.pilots.get_mut(&session) else { return Vec::new() };
        let Some(run) = pilot.modules.running.iter_mut().find(|run| run.effect.module_id == module_id) else {
            return Vec::new();
        };
        run.pending_work = Some(pending);
        let target_id = run.effect.target_id;
        self.work_cycle_out(session, module_id, target_id, work, pending).into_iter().collect()
    }

    /// A parked work cycle is due again: the same cycle, with the same rolls, is sent once more (the host answers a repeat from its record).
    pub(super) fn resend_work_cycle(&mut self, session: SessionId, index: usize, at: i64) -> Vec<Out> {
        let Some(pilot) = self.pilots.get_mut(&session) else { return Vec::new() };
        let Some(run) = pilot.modules.running.get_mut(index) else { return Vec::new() };
        let Some(pending) = run.pending_work else { return Vec::new() };
        if pending.fuel {
            let (module_id, target_id) = (run.effect.module_id, run.effect.target_id);
            run.pending_work = Some(PendingWorkCycle { sent_at: at, ..pending });
            return self
                .fuel_cycle_out(session, module_id, target_id, pending.cycle_id)
                .into_iter()
                .collect();
        }
        let Some(work) = run
            .effect
            .action
            .as_ref()
            .and_then(|action| action.with_charges(run.charged_this_cycle))
            .filter(|action| action.is_work())
            .cloned()
        else {
            // the effect has no work cycle left to send (its action changed under the parked cycle): it stops, as one that loses its target does (M-1)
            return self.stop_parked(session, index, at);
        };
        let (module_id, target_id) = (run.effect.module_id, run.effect.target_id);
        let pending = PendingWorkCycle { sent_at: at, ..pending };
        run.pending_work = Some(pending);
        self.work_cycle_out(session, module_id, target_id, &work, pending).into_iter().collect()
    }

    /// A parked effect loses its target: it stops at once, with no outcome for the cycle it was on (no grant is owed for it).
    pub(super) fn stop_parked(&mut self, session: SessionId, index: usize, now: i64) -> Vec<Out> {
        let Some(pilot) = self.pilots.get_mut(&session) else { return Vec::new() };
        if pilot.modules.running.get(index).is_none_or(|run| run.pending_work.is_none()) {
            return Vec::new();
        }
        let (owner, ship_id) = (pilot.ship.spec.character_id, pilot.ship.spec.ship_id);
        let run = pilot.modules.running.remove(index);
        self.effect_ended(session, run, owner, ship_id, now, Ending::TargetLost)
    }

    /// The host's answer to a work cycle (`ModuleWorkResult`). An accepted one ends that cycle and the effect goes on, or ends when it must; a refused one
    /// stops the effect (the host has granted nothing). An answer that matches no parked cycle (a repeat, or a cycle the module has left) does nothing.
    /// `depleted` is the host's `complete`: the rock is mined out (or the wreck is gone), so the effect stops as its target is lost.
    pub fn module_work_result(
        &mut self,
        session: SessionId,
        module_id: i64,
        answer: ModuleWorkAnswer<'_>,
        now: i64,
    ) -> Vec<Out> {
        let ModuleWorkAnswer { target_id, cycle_id, accepted, reason, depleted } = answer;
        let Some(pilot) = self.pilots.get_mut(&session) else { return Vec::new() };
        // a start parked for its fuel: accepted, it starts; refused, it is refused with the host's reason
        if let Some(position) = pilot.modules.pending_starts.iter().position(|start| {
            start.effect.module_id == module_id
                && start.effect.target_id == target_id
                && start.cycle_id == cycle_id
        }) {
            let start = pilot.modules.pending_starts.remove(position);
            if !accepted {
                // a P.A.N.I.C.'s grant is refused under the host's reason, a fuel ask under the fuel's (see `fuel_refusal`)
                let reason = if matches!(start.effect.action, Some(Action::Panic { .. })) {
                    Self::panic_refusal(reason)
                } else {
                    Self::fuel_refusal(reason)
                };
                return vec![Out::Refused { session, command: 17, reason }];
            }
            return self.start_effect(session, start.effect, now);
        }
        let Some(pilot) = self.pilots.get(&session) else { return Vec::new() };
        let Some(index) = pilot.modules.running.iter().position(|run| {
            run.effect.module_id == module_id
                && run.effect.target_id == target_id
                && run.pending_work.is_some_and(|pending| pending.cycle_id == cycle_id)
        }) else {
            return Vec::new();
        };
        let (owner, ship_id) = (pilot.ship.spec.character_id, pilot.ship.spec.ship_id);
        let Some(pilot) = self.pilots.get_mut(&session) else { return Vec::new() };
        let fuel = pilot.modules.running[index].pending_work.is_some_and(|pending| pending.fuel);
        if !accepted {
            let run = pilot.modules.running.remove(index);
            let ending = if fuel && reason == "NO_FUEL" { Ending::NoFuel } else { Ending::WorkRefused };
            return self.effect_ended(session, run, owner, ship_id, now, ending);
        }
        pilot.modules.running[index].pending_work = None;
        let run = pilot.modules.running[index].clone();
        let target_ok = if fuel {
            self.cycle_target_ok(session, &run.effect, run.charged_this_cycle)
        } else {
            self.work_target_ok(session, &run.effect) && !depleted
        };
        self.finish_cycle(session, module_id, now, Vec::new(), target_ok)
    }

    /// A module's mining cycle that the host granted (`accepted`) took `units_remaining` from its rock. The other pilots who see the rock are told how much came
    /// off it (`OnOreMinedByOther`, the whole amount taken, as the reference sends it); the miner is told by its own `OnOreMined`. The rock's stock is set
    /// without a change to its destiny (the reference's clients keep the scanned quantity and take it down from this message), and a rock mined out is removed.
    /// Only a cycle still parked counts: a repeat, or a cycle the module has left, tells nobody.
    pub fn module_mined(
        &mut self,
        session: SessionId,
        module_id: i64,
        target_id: i64,
        cycle_id: u64,
        units_remaining: i64,
        now: i64,
    ) -> Vec<Out> {
        let parked = self.pilots.get(&session).is_some_and(|pilot| {
            pilot.modules.running.iter().any(|run| {
                run.effect.module_id == module_id
                    && run.effect.target_id == target_id
                    && matches!(run.effect.action, Some(Action::Mining { .. }))
                    && run.pending_work.is_some_and(|pending| pending.cycle_id == cycle_id && !pending.fuel)
            })
        });
        let Some(stock) = self.asteroids.get(&target_id).map(|asteroid| asteroid.units_remaining) else {
            return Vec::new();
        };
        if !parked || units_remaining >= stock {
            return Vec::new();
        }
        let taken = stock - units_remaining;
        let mut out: Vec<Out> = self
            .in_entry_order()
            .into_iter()
            .filter(|s| {
                *s != session && self.pilots[s].bound && self.pilots[s].sees_asteroids.contains(&target_id)
            })
            .map(|watcher| Out::Notify {
                session: watcher,
                name: "OnOreMinedByOther",
                id_type: "charid",
                args: vec![crate::wire::id(target_id), crate::wire::int(taken)],
            })
            .collect();
        if units_remaining <= 0 {
            out.extend(self.remove_asteroid(target_id, true, now));
        } else if let Some(asteroid) = self.asteroids.get_mut(&target_id) {
            asteroid.units_remaining = units_remaining;
        }
        out
    }
}

/// Brings a refresh the host sent (`UpdateEffect`) to a running effect: the cycle, the capacitor it costs, the turret and the attributes the client is told.
/// Returns the attribute change the client is told, if the refresh changes any.
fn refresh_running(run: &mut Running, session: SessionId, owner: i64, at: i64) -> Option<Out> {
    let update = run.pending_update.take()?;
    let module_id = run.effect.module_id;
    let lines: Vec<Value> = update
        .changes
        .iter()
        .map(|change| {
            let old = run
                .effect
                .on_changes
                .iter()
                .rev()
                .find(|known| known.attribute_id == change.attribute_id)
                .map_or(0.0, |known| known.value);
            attribute_change(owner, module_id, change.attribute_id, at, real(change.value), real(old))
        })
        .collect();
    run.effect.duration_ms = update.duration_ms;
    run.effect.capacitor_need = update.capacitor_need;
    run.effect.turret = update.turret;
    run.effect.on_changes = update.changes;
    (!lines.is_empty()).then(|| Out::Notify {
        session,
        name: "OnModuleAttributeChanges",
        id_type: "clientID",
        args: vec![Value::List(lines)],
    })
}
