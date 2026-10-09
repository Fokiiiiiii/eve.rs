use super::*;

impl Scene {
    /// A ship lets go of a locked target: it is told (`lost`), and so is the target when it is a ship (`otherlost`).
    pub(super) fn remove_locked(
        &mut self,
        session: SessionId,
        target: i64,
        reason: Option<&'static str>,
        notify_target: bool,
        now: i64,
    ) -> Vec<Out> {
        let Some(pilot) = self.pilots.get_mut(&session) else { return Vec::new() };
        let before = pilot.targeting.locked.len();
        pilot.targeting.locked.retain(|l| l.target != target);
        if pilot.targeting.locked.len() == before {
            return Vec::new();
        }
        let source_id = pilot.ship.spec.ship_id;
        // what shoots at the target stops before the pilot is told the lock is lost
        let mut out = self.stop_weapons_at(session, &[target], now);
        out.push(on_target_reason(session, "lost", Some(target), reason));
        if notify_target
            && let Some(other) = self.session_of_ship(target)
            && other != session
        {
            out.push(on_target_reason(other, "otherlost", Some(source_id), reason));
        }
        out
    }

    pub(super) fn session_of_ship(&self, ship_id: i64) -> Option<SessionId> {
        self.pilots.iter().find(|(_, p)| p.ship.spec.ship_id == ship_id).map(|(s, _)| *s)
    }

    /// A ship lets go of everything: what is going is given up, what is held is let go and the ship is told once (`clear`); the ships that were
    /// locked are told they are not.
    pub(super) fn clear_locks(&mut self, session: SessionId, notify_target: bool, now: i64) -> Vec<Out> {
        let Some(pilot) = self.pilots.get_mut(&session) else { return Vec::new() };
        pilot.targeting.pending.clear();
        let held = pilot.targeting.targets();
        pilot.targeting.locked.clear();
        let source_id = pilot.ship.spec.ship_id;
        let mut out = self.stop_weapons_at(session, &held, now);
        if notify_target {
            for target in &held {
                if let Some(other) = self.session_of_ship(*target)
                    && other != session
                {
                    out.push(on_target(other, "otherlost", Some(source_id)));
                }
            }
        }
        if !held.is_empty() {
            out.push(on_target(session, "clear", None));
        }
        out
    }

    /// A ship's locks are broken but those on the ships in `allowed` (the ones that jam it): what is going is given up and what is held is let go, with the reason `target`.
    pub(crate) fn break_locks_except(&mut self, session: SessionId, allowed: &[i64], now: i64) -> Vec<Out> {
        let Some(pilot) = self.pilots.get_mut(&session) else { return Vec::new() };
        let going: Vec<i64> =
            pilot.targeting.pending.iter().map(|p| p.target).filter(|t| !allowed.contains(t)).collect();
        let mut out = Vec::new();
        for target in going {
            self.cancel_pending(session, target);
            out.push(on_target_reason(session, "lost", Some(target), Some("target")));
        }
        for target in self.locked_by(session).into_iter().filter(|t| !allowed.contains(t)) {
            out.extend(self.remove_locked(session, target, Some("target"), true, now));
        }
        out
    }

    /// A ship has gone (docked, jumped, logged off; it is out of the scene already): what it had locked is let go, and whoever had it locked is told.
    pub(crate) fn drop_targeting(&mut self, ship_id: i64, held: &Targeting, now: i64) -> Vec<Out> {
        let mut out = Vec::new();
        for locked in &held.locked {
            if let Some(other) = self.session_of_ship(locked.target) {
                out.push(on_target(other, "otherlost", Some(ship_id)));
            }
        }
        let sources: Vec<SessionId> = self
            .pilots
            .iter()
            .filter(|(_, p)| {
                p.targeting.locked.iter().any(|l| l.target == ship_id)
                    || p.targeting.pending.iter().any(|q| q.target == ship_id)
            })
            .map(|(s, _)| *s)
            .collect();
        for source in sources {
            if let Some(pilot) = self.pilots.get_mut(&source) {
                pilot.targeting.pending.retain(|p| p.target != ship_id);
            }
            out.extend(self.remove_locked(source, ship_id, None, false, now));
        }
        out
    }
}
