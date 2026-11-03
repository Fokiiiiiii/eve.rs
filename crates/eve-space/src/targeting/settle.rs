use super::*;

impl Scene {
    pub(crate) fn rebase_pending_target_locks(
        &mut self,
        changed_session: SessionId,
        scan_resolution_changed: bool,
        signature_radius_changed: bool,
        now: i64,
    ) {
        let Some(changed_ship_id) = self.pilots.get(&changed_session).map(|pilot| pilot.ship.spec.ship_id)
        else {
            return;
        };
        let sources = self.in_entry_order();
        for source in sources {
            let Some(scan_resolution) = self.pilots.get(&source).map(|pilot| pilot.ship.spec.scan_resolution)
            else {
                continue;
            };
            let targets: Vec<i64> = self.pilots.get(&source).map_or_else(Vec::new, |pilot| {
                pilot
                    .targeting
                    .pending
                    .iter()
                    .filter(|pending| {
                        (source == changed_session && scan_resolution_changed)
                            || (signature_radius_changed && pending.target == changed_ship_id)
                    })
                    .map(|pending| pending.target)
                    .collect()
            });
            for target in targets {
                let Some(subject) = self.subject(target, now) else { continue };
                let duration_ms = lock_duration_ms(scan_resolution, subject.signature_radius);
                let Some(pending) = self.pilots.get_mut(&source).and_then(|pilot| {
                    pilot.targeting.pending.iter_mut().find(|pending| pending.target == target)
                }) else {
                    continue;
                };
                let old_duration_ms = pending.total_duration_ms.max(1.0);
                let elapsed_ms =
                    now.saturating_sub(pending.requested_at).max(0) as f64 * 1000.0 / TICKS_PER_SECOND as f64;
                let progress = (elapsed_ms / old_duration_ms).clamp(0.0, 1.0);
                let elapsed_ticks = (duration_ms * progress * TICKS_PER_SECOND as f64 / 1000.0) as i64;
                let duration_ticks = (duration_ms * TICKS_PER_SECOND as f64 / 1000.0) as i64;
                pending.requested_at = now.saturating_sub(elapsed_ticks);
                pending.total_duration_ms = duration_ms;
                pending.complete_at = pending.requested_at.saturating_add(duration_ticks);
            }
        }
    }

    /// Drops newest pending and then held locks when a ship's target limit shrinks, matching its observable lock order.
    pub(crate) fn enforce_target_limit(&mut self, session: SessionId, now: i64) -> Vec<Out> {
        let Some(pilot) = self.pilots.get(&session) else { return Vec::new() };
        let limit = pilot.ship.spec.max_locked_targets.max(0) as usize;
        let excess = (pilot.targeting.pending.len() + pilot.targeting.locked.len()).saturating_sub(limit);
        if excess == 0 {
            return Vec::new();
        }
        let mut pending: Vec<_> =
            pilot.targeting.pending.iter().map(|lock| (lock.sequence, lock.target)).collect();
        pending.sort_unstable_by_key(|(sequence, _)| std::cmp::Reverse(*sequence));
        let cancel = pending.len().min(excess);
        let mut out = Vec::new();
        for (_, target) in pending.into_iter().take(cancel) {
            if let Some(notice) = self.cancel_pending(session, target, true) {
                out.push(notice);
            }
        }
        let release = excess - cancel;
        if release > 0 {
            let mut locked: Vec<_> = self.pilots[&session]
                .targeting
                .locked
                .iter()
                .map(|lock| (lock.sequence, lock.target))
                .collect();
            locked.sort_unstable_by_key(|(sequence, _)| std::cmp::Reverse(*sequence));
            for (_, target) in locked.into_iter().take(release) {
                out.extend(self.remove_locked(session, target, None, true, now));
            }
        }
        out
    }

    pub(crate) fn settle_targets(&mut self, now: i64) -> Vec<Out> {
        self.with_controlled_structure_modules(now, |scene| scene.settle_targets_for_pilots(now))
    }

    /// What is due at `now` among the locks: a lock that has come to its end is had; one that can no longer be had (either ship warps, the range
    /// is more than the ship can lock at, the ship it points at is gone) is dropped.
    fn settle_targets_for_pilots(&mut self, now: i64) -> Vec<Out> {
        let sessions: Vec<SessionId> =
            self.in_entry_order().into_iter().filter(|s| !self.pilots[s].targeting.is_empty()).collect();
        let mut out = Vec::new();
        for session in sessions {
            let Some(pilot) = self.pilots.get(&session) else { continue };
            if pilot.flight.as_ref().is_some_and(|f| f.is_warping()) {
                out.extend(self.clear_locks(session, true, now));
                continue;
            }
            let mut due: Vec<Pending> = pilot.targeting.pending.clone();
            due.sort_by_key(|p| (p.complete_at, p.sequence));
            for pending in due {
                let Some(subject) = self.subject(pending.target, now) else {
                    out.extend(self.cancel_pending(session, pending.target, true));
                    continue;
                };
                if self.check_lock(session, &subject, false).is_err() {
                    out.extend(self.cancel_pending(session, pending.target, true));
                    continue;
                }
                if pending.complete_at <= now {
                    out.extend(self.finish_lock(session, subject, pending.sequence, pending.origin, now));
                }
            }
            let held = self.pilots.get(&session).map(|p| p.targeting.targets()).unwrap_or_default();
            for target in held {
                let Some(subject) = self.subject(target, now) else {
                    out.extend(self.remove_locked(
                        session,
                        target,
                        Some("TargetingAttemptCancelled"),
                        true,
                        now,
                    ));
                    continue;
                };
                if let Err(refusal) = self.check_lock(session, &subject, false) {
                    let reason =
                        (refusal.reason == "TARGET_NOT_FOUND").then_some("TargetingAttemptCancelled");
                    out.extend(self.remove_locked(session, target, reason, true, now));
                }
            }
        }
        out
    }
}
