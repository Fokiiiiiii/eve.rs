use super::*;

impl Scene {
    /// What is due at `now` among the locks: a lock that has come to its end is had; one that can no longer be had (either ship warps, the range
    /// is more than the ship can lock at, the ship it points at is gone) is dropped.
    pub(crate) fn settle_targets(&mut self, now: i64) -> Vec<Out> {
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
                    self.cancel_pending(session, pending.target);
                    continue;
                };
                if self.check_lock(session, &subject, false).is_err() {
                    self.cancel_pending(session, pending.target);
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
