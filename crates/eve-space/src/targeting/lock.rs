use super::*;

impl Scene {
    pub(super) fn check_lock(
        &self,
        session: SessionId,
        target: &Subject,
        count: bool,
    ) -> Result<(), Refusal> {
        let source = self.pilots.get(&session).ok_or(Refusal::of("NOT_IN_SPACE"))?;
        if target.protected {
            return Err(Refusal::of("TARGET_INVULNERABLE"));
        }
        if target.id == source.ship.spec.ship_id {
            return Err(Refusal::of("TARGET_SELF"));
        }
        if UNTARGETABLE_GROUPS.contains(&target.group_id) {
            return Err(Refusal::of_type("TARGET_NOT_TARGETABLE", target.type_id));
        }
        // a jammed ship can lock only the ships that jam it
        if source.incoming.jammed() && !source.incoming.jammers().contains(&target.id) {
            return Err(Refusal::of("TARGET_JAMMED"));
        }
        if source.flight.as_ref().is_some_and(|f| f.is_warping()) {
            return Err(Refusal::of("SOURCE_WARPING"));
        }
        if target.warping {
            return Err(Refusal::of_type("TARGET_WARPING", target.type_id));
        }
        let observer = crate::visibility::Presence { position: source.ship.position, warping: false };
        let subject = crate::visibility::Presence { position: target.position, warping: false };
        let target_cloak = self
            .pilots
            .values()
            .find(|pilot| pilot.ship.spec.ship_id == target.id)
            .map_or(crate::visibility::CloakState::Uncloaked, |pilot| pilot.cloak);
        if !crate::visibility::can_select_target(observer, subject, source.cloak, target_cloak) {
            return Err(Refusal::of("TARGET_NOT_FOUND"));
        }
        let spec = &source.ship.spec;
        let apart = (distance(source.ship.position, target.position) - spec.radius - target.radius).max(0.0);
        if spec.max_target_range <= 0.0 || apart >= spec.max_target_range {
            return Err(Refusal {
                reason: "TARGET_OUT_OF_RANGE",
                target_type: target.type_id,
                desired_range: spec.max_target_range.max(0.0),
                actual_distance: apart,
            });
        }
        let limit = spec.max_locked_targets.max(0);
        // what the client is told is how many targets the ship may hold, which travels in the place of the range
        let full = Refusal { desired_range: f64::from(limit), ..Refusal::of("TARGET_LOCK_LIMIT_REACHED") };
        if limit == 0 {
            return Err(full);
        }
        if count && source.targeting.locked.len() + source.targeting.pending.len() >= limit as usize {
            return Err(full);
        }
        Ok(())
    }

    /// The one lock path: every origin is checked the same way; the origin only decides who is told (see `finish_lock`).
    /// The auto-target sweep of an activation or a cycle (EVE.js `executeAutoTargetingCycle`): each ship that has this pilot locked, within `range` of it and not
    /// locked by the pilot yet, is locked with the origin `Auto` (the ship is told, as any lock tells it). A ship the lock would refuse, and any ship beyond a
    /// full lock list, is passed over silently: a sweep asks only for what the pilot can have.
    pub(crate) fn auto_target_sweep(&mut self, session: SessionId, range: f64, now: i64) -> Vec<Out> {
        let Some(me) = self.pilots.get(&session).map(|p| p.ship.spec.ship_id) else { return Vec::new() };
        let mut out = Vec::new();
        for candidate in self.targeters_of(me) {
            let Some(pilot) = self.pilots.get(&session) else { break };
            if candidate == me
                || pilot.targeting.locked.iter().any(|l| l.target == candidate)
                || pilot.targeting.pending.iter().any(|p| p.target == candidate)
                || pilot.targeting.locked.len() + pilot.targeting.pending.len()
                    >= pilot.ship.spec.max_locked_targets.max(0) as usize
            {
                continue;
            }
            let Some(subject) = self.subject(candidate, now) else { continue };
            let apart =
                (distance(pilot.ship.position, subject.position) - pilot.ship.spec.radius - subject.radius)
                    .max(0.0);
            if apart >= range || self.check_lock(session, &subject, true).is_err() {
                continue;
            }
            out.extend(self.lock(session, candidate, &Order::Lock(candidate), LockOrigin::Auto, now));
        }
        out
    }

    pub(crate) fn lock(
        &mut self,
        session: SessionId,
        target: i64,
        order: &Order,
        origin: LockOrigin,
        now: i64,
    ) -> Vec<Out> {
        let pilot = &self.pilots[&session];
        if pilot.targeting.locked.iter().any(|l| l.target == target) {
            return Self::answer(session, order, false, self.locked_by(session));
        }
        if pilot.targeting.pending.iter().any(|p| p.target == target) {
            return Self::answer(session, order, true, self.locked_by(session));
        }
        let Some(subject) = self.subject(target, now) else {
            return Self::refused(session, order, Refusal::of("TARGET_NOT_FOUND"));
        };
        if let Err(refusal) = self.check_lock(session, &subject, true) {
            return Self::refused(session, order, refusal);
        }
        let spec = &self.pilots[&session].ship.spec;
        let duration = lock_duration_ms(spec.scan_resolution, subject.signature_radius);
        let mut tether_out = self.break_tether(session, now);
        // a validated lock request gives up the undocking protection (the host tells the client)
        let sequence = self.next_sequence();
        let pilot = self.pilots.get_mut(&session).expect("the pilot is in the scene");
        pilot.protected_until = 0;
        if duration <= 1.0 {
            tether_out.extend(Self::answer(session, order, false, Vec::new()));
            tether_out.extend(self.finish_lock(session, subject, sequence, origin, now));
            return tether_out;
        }
        let complete_at = now + (duration * TICKS_PER_SECOND as f64 / 1000.0) as i64;
        pilot.targeting.pending.push(Pending { target, sequence, complete_at, origin });
        tether_out.extend(Self::answer(session, order, true, self.locked_by(session)));
        tether_out
    }

    pub(super) fn finish_lock(
        &mut self,
        session: SessionId,
        subject: Subject,
        sequence: u64,
        origin: LockOrigin,
        now: i64,
    ) -> Vec<Out> {
        let Some(pilot) = self.pilots.get_mut(&session) else { return Vec::new() };
        pilot.targeting.pending.retain(|p| p.target != subject.id);
        pilot.targeting.locked.push(Locked { target: subject.id, sequence });
        let source_id = pilot.ship.spec.ship_id;
        let mut out = vec![on_target(session, "add", Some(subject.id))];
        if origin.tells_target()
            && let Some(target_session) = subject.session
            && target_session != session
        {
            out.push(on_target(target_session, "otheradd", Some(source_id)));
        }
        if subject.has_damage_state {
            out.extend(self.announce_damage_state(&subject, now));
        }
        out
    }

    /// The damage state of a ship or station told to the pilots that can see it, its own, and the ones that have it locked.
    pub(crate) fn announce_damage_state(&mut self, subject: &Subject, now: i64) -> Vec<Out> {
        let recipients: Vec<SessionId> = self
            .in_entry_order()
            .into_iter()
            .filter(|s| {
                let pilot = &self.pilots[s];
                pilot.bound
                    && (subject.session == Some(*s)
                        || subject.session.is_none()
                        || subject.session.is_some_and(|t| pilot.sees.contains(&t))
                        || pilot.targeting.locked.iter().any(|l| l.target == subject.id))
            })
            .collect();
        if recipients.is_empty() {
            return Vec::new();
        }
        let stamp = self.next_stamp(now);
        let action = payload::action(
            stamp,
            "OnDamageStateChange",
            [id(subject.id), payload::damage_state(&subject.state, now)],
        );
        recipients.into_iter().map(|s| Self::update(s, vec![action.clone()])).collect()
    }

    /// A lock that was going is given up because it cannot be had (the client is not told: its own request lane fails with the service
    /// notification the host does not carry yet).
    pub(super) fn cancel_pending(&mut self, session: SessionId, target: i64) {
        if let Some(pilot) = self.pilots.get_mut(&session) {
            pilot.targeting.pending.retain(|p| p.target != target);
        }
    }
}
