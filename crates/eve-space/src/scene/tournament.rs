//! Scene-owned evidence for a tournament's cutoff. The simulation dispatcher must
//! settle the scene at the cutoff before processing any later command.
use std::collections::{BTreeMap, BTreeSet};

/// Immutable participant identity, including the competing hull.
#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord)]
pub struct TournamentPilot {
    pub character_id: i64,
    pub ship_id: i64,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum TournamentEvidence {
    Alive,
    Destroyed,
    Unknown,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum TournamentCutoff {
    Pending,
    Ready(Vec<(TournamentPilot, TournamentEvidence)>),
    Unavailable,
}

#[derive(Debug, Clone, PartialEq, Eq)]
struct Match {
    start: i64,
    deadline: i64,
    roster: Vec<TournamentPilot>,
    evidence: BTreeMap<TournamentPilot, TournamentEvidence>,
    result: Option<TournamentCutoff>,
}

/// Bounded, process-local proof; a new scene cannot recover prior evidence.
#[derive(Debug, Default)]
pub struct TournamentTracker {
    matches: BTreeMap<(i64, i64), Match>,
    tombstones: BTreeMap<(i64, i64), (i64, i64)>,
}

impl TournamentTracker {
    pub const MAX_MATCHES: usize = 64;
    pub const MAX_PILOTS: usize = 256;
    pub const MAX_TOMBSTONES: usize = 4096;

    /// Identical retries are harmless; conflicting retries never replace proof.
    // The immutable wire identity, roster and observation time are independent protocol fields.
    #[allow(clippy::too_many_arguments)]
    pub fn register(
        &mut self,
        match_id: i64,
        generation: i64,
        start: i64,
        deadline: i64,
        now: i64,
        mut roster: Vec<TournamentPilot>,
        residents: &BTreeSet<TournamentPilot>,
    ) -> Result<TournamentCutoff, &'static str> {
        roster.sort_unstable();
        if match_id <= 0
            || generation <= 0
            || start <= 0
            || deadline <= start
            || roster.is_empty()
            || roster.len() > Self::MAX_PILOTS
            || roster.iter().any(|p| p.character_id <= 0 || p.ship_id <= 0)
            || roster.windows(2).any(|w| w[0] == w[1])
            || roster.iter().map(|p| p.character_id).collect::<BTreeSet<_>>().len() != roster.len()
            || roster.iter().map(|p| p.ship_id).collect::<BTreeSet<_>>().len() != roster.len()
        {
            return Err("invalid tournament roster");
        }
        let key = (match_id, generation);
        if let Some(&(old_start, old_deadline)) = self.tombstones.get(&key) {
            return if (old_start, old_deadline) == (start, deadline) {
                Ok(TournamentCutoff::Unavailable)
            } else {
                Err("conflicting tournament registration")
            };
        }
        if let Some(existing) = self.matches.get(&key) {
            if existing.start != start || existing.deadline != deadline || existing.roster != roster {
                return Err("conflicting tournament registration");
            }
            return Ok(existing.result.clone().unwrap_or(TournamentCutoff::Pending));
        }
        if self.matches.len() >= Self::MAX_MATCHES {
            return Err("tournament tracker capacity exceeded");
        }
        let unavailable = now >= start || roster.iter().any(|p| !residents.contains(p));
        let evidence = roster.iter().map(|p| (*p, TournamentEvidence::Alive)).collect();
        self.matches.insert(
            key,
            Match {
                start,
                deadline,
                roster,
                evidence,
                result: unavailable.then_some(TournamentCutoff::Unavailable),
            },
        );
        Ok(if unavailable { TournamentCutoff::Unavailable } else { TournamentCutoff::Pending })
    }

    /// Destruction is explicit proof, unlike a missing pilot or a disconnect.
    pub fn destroyed(&mut self, pilot: TournamentPilot, at: i64) {
        for m in self.matches.values_mut() {
            if m.result.is_none()
                && at <= m.deadline
                && let Some(evidence) = m.evidence.get_mut(&pilot)
                && *evidence == TournamentEvidence::Alive
            {
                *evidence =
                    if at < m.start { TournamentEvidence::Unknown } else { TournamentEvidence::Destroyed };
            }
        }
    }

    /// Losing the tracked identity invalidates proof even if it later returns.
    pub fn departed(&mut self, pilot: TournamentPilot, at: i64) {
        for m in self.matches.values_mut() {
            if m.result.is_none()
                && at <= m.deadline
                && let Some(evidence) = m.evidence.get_mut(&pilot)
                && *evidence == TournamentEvidence::Alive
            {
                *evidence = TournamentEvidence::Unknown;
            }
        }
    }

    /// Called at the exact logical cutoff after earlier scene work and before later work.
    pub fn freeze(
        &mut self,
        match_id: i64,
        generation: i64,
        at: i64,
        residents: &BTreeSet<TournamentPilot>,
    ) -> TournamentCutoff {
        let Some(m) = self.matches.get_mut(&(match_id, generation)) else {
            return TournamentCutoff::Unavailable;
        };
        if let Some(result) = &m.result {
            return result.clone();
        }
        if at < m.deadline {
            return TournamentCutoff::Pending;
        }
        if at != m.deadline {
            m.result = Some(TournamentCutoff::Unavailable);
            return TournamentCutoff::Unavailable;
        }
        let evidence: Vec<_> = m
            .roster
            .iter()
            .map(|p| {
                let prior = m.evidence[p];
                (
                    *p,
                    if prior == TournamentEvidence::Alive && !residents.contains(p) {
                        TournamentEvidence::Unknown
                    } else {
                        prior
                    },
                )
            })
            .collect();
        let result = if evidence.iter().any(|(_, e)| *e == TournamentEvidence::Unknown) {
            TournamentCutoff::Unavailable
        } else {
            TournamentCutoff::Ready(evidence)
        };
        m.result = Some(result.clone());
        result
    }

    pub fn read(
        &self,
        match_id: i64,
        generation: i64,
        start: i64,
        deadline: i64,
    ) -> Result<TournamentCutoff, &'static str> {
        let key = (match_id, generation);
        if let Some(m) = self.matches.get(&key) {
            if (m.start, m.deadline) != (start, deadline) {
                return Err("conflicting tournament identity");
            }
            return Ok(m.result.clone().unwrap_or(TournamentCutoff::Pending));
        }
        if let Some(&identity) = self.tombstones.get(&key)
            && identity != (start, deadline)
        {
            return Err("conflicting tournament identity");
        }
        Ok(TournamentCutoff::Unavailable)
    }

    pub fn next_deadline(&self) -> Option<i64> {
        self.matches.values().filter(|m| m.result.is_none()).map(|m| m.deadline).min()
    }

    pub fn freeze_due(&mut self, at: i64, residents: &BTreeSet<TournamentPilot>) {
        let due: Vec<_> = self
            .matches
            .iter()
            .filter(|(_, m)| m.result.is_none() && m.deadline <= at)
            .map(|(key, _)| *key)
            .collect();
        for (match_id, generation) in due {
            self.freeze(match_id, generation, at, residents);
        }
    }

    /// A full identity creates a durable-for-this-process cancellation fence.
    /// No tombstone is evicted; reaching the bound refuses further releases.
    pub fn release(
        &mut self,
        match_id: i64,
        generation: i64,
        start: i64,
        deadline: i64,
    ) -> Result<(), &'static str> {
        if match_id <= 0 || generation <= 0 || start <= 0 || deadline <= start {
            return Err("invalid tournament identity");
        }
        let key = (match_id, generation);
        if let Some(&identity) = self.tombstones.get(&key) {
            return if identity == (start, deadline) {
                Ok(())
            } else {
                Err("conflicting tournament identity")
            };
        }
        if let Some(m) = self.matches.get(&key)
            && (m.start, m.deadline) != (start, deadline)
        {
            return Err("conflicting tournament identity");
        }
        if self.tombstones.len() >= Self::MAX_TOMBSTONES {
            return Err("tournament tombstone capacity exceeded");
        }
        self.tombstones.insert(key, (start, deadline));
        self.matches.remove(&key);
        Ok(())
    }
}

impl super::Scene {
    fn tournament_residents(&self) -> BTreeSet<TournamentPilot> {
        self.pilots
            .values()
            .map(|pilot| TournamentPilot {
                character_id: pilot.ship.spec.character_id,
                ship_id: pilot.ship.spec.ship_id,
            })
            .collect()
    }

    pub fn tournament_register(
        &mut self,
        match_id: i64,
        generation: i64,
        start: i64,
        deadline: i64,
        now: i64,
        roster: Vec<TournamentPilot>,
    ) -> Result<TournamentCutoff, &'static str> {
        let residents = self.tournament_residents();
        self.tournament.register(match_id, generation, start, deadline, now, roster, &residents)
    }

    pub fn tournament_freeze(&mut self, match_id: i64, generation: i64, at: i64) -> TournamentCutoff {
        let residents = self.tournament_residents();
        self.tournament.freeze(match_id, generation, at, &residents)
    }

    pub fn tournament_read(
        &self,
        match_id: i64,
        generation: i64,
        start: i64,
        deadline: i64,
    ) -> Result<TournamentCutoff, &'static str> {
        self.tournament.read(match_id, generation, start, deadline)
    }

    /// Called after the scene has been advanced through cutoff. Incomplete
    /// inventory remains fail-closed at the separate freeze gate.
    pub fn tournament_has_pending_host_work(&self, cutoff: i64) -> bool {
        self.pilots.values().any(|pilot| pilot.modules.tournament_has_pending_host_work(cutoff))
            || self
                .structure_modules
                .values()
                .any(|state| state.modules.tournament_has_pending_host_work(cutoff))
            || self.drones.values().any(|drone| drone.awaiting_work)
            || self.bomb_impacts.iter().any(|(id, impact)| {
                impact.pending.is_some() || self.deployables.get(id).is_none_or(|bomb| bomb.ends_at <= cutoff)
            })
    }

    /// Only remote repair has a proven ordered production fence. Other
    /// in-flight host work must fail closed until its completion is proven.
    pub fn tournament_can_fence_remote_only(&self, cutoff: i64) -> bool {
        let unsupported_modules =
            self.pilots.values().any(|pilot| pilot.modules.tournament_has_unsupported_host_work())
                || self
                    .structure_modules
                    .values()
                    .any(|state| state.modules.tournament_has_unsupported_host_work());
        let unsupported_scene = self.drones.values().any(|drone| drone.awaiting_work)
            || self.bomb_impacts.iter().any(|(id, impact)| {
                impact.pending.is_some() || self.deployables.get(id).is_none_or(|bomb| bomb.ends_at <= cutoff)
            });
        let repair =
            self.pilots.values().any(|pilot| pilot.modules.tournament_has_pending_remote_repair(cutoff))
                || self
                    .structure_modules
                    .values()
                    .any(|state| state.modules.tournament_has_pending_remote_repair(cutoff));
        repair && !unsupported_modules && !unsupported_scene
    }

    pub fn tournament_next_deadline(&self) -> Option<i64> {
        self.tournament.next_deadline()
    }

    pub(crate) fn tournament_metrics_prior_deaths(
        &self,
        match_id: i64,
        start: i64,
        roster: &[(i64, i64)],
    ) -> Option<BTreeSet<(i64, i64)>> {
        let expected: BTreeSet<_> = roster.iter().copied().collect();
        if expected.len() != roster.len() {
            return None;
        }
        let mut matching = self.tournament.matches.iter().filter(|((id, _), m)| {
            *id == match_id
                && m.start == start
                && m.roster.iter().map(|p| (p.character_id, p.ship_id)).collect::<BTreeSet<_>>() == expected
        });
        let (_, m) = matching.next()?;
        if matching.next().is_some() {
            return None;
        }
        Some(
            m.evidence
                .iter()
                .filter_map(|(pilot, state)| {
                    (*state == TournamentEvidence::Destroyed).then_some((pilot.character_id, pilot.ship_id))
                })
                .collect(),
        )
    }
    pub fn tournament_has_tracking(&self) -> bool {
        !self.tournament.matches.is_empty() || !self.tournament.tombstones.is_empty()
    }

    /// Freeze evidence after the scene has settled all known pre-cutoff work.
    pub fn tournament_settle_due(&mut self, at: i64) {
        let residents = self.tournament_residents();
        self.tournament.freeze_due(at, &residents);
    }

    pub fn tournament_freeze_due(&mut self, at: i64) {
        // Host-correlated effects may still be in flight at this boundary.
        // Until their ordered completion fence exists, never publish a result.
        for entry in self.tournament.matches.values_mut() {
            if entry.result.is_none() && entry.deadline <= at {
                entry.result = Some(TournamentCutoff::Unavailable);
            }
        }
    }

    pub fn tournament_release(
        &mut self,
        match_id: i64,
        generation: i64,
        start: i64,
        deadline: i64,
    ) -> Result<(), &'static str> {
        self.tournament.release(match_id, generation, start, deadline)
    }

    pub fn tournament_departed(&mut self, character_id: i64, ship_id: i64, at: i64) {
        self.tournament.departed(TournamentPilot { character_id, ship_id }, at);
        self.tournament_metrics.departed(character_id, ship_id, at);
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    fn pilot(n: i64) -> TournamentPilot {
        TournamentPilot { character_id: n, ship_id: n + 100 }
    }
    #[test]
    fn cutoff_uses_explicit_deaths_and_stays_immutable() {
        let (a, b) = (pilot(1), pilot(2));
        let residents = BTreeSet::from([a, b]);
        let mut t = TournamentTracker::default();
        assert_eq!(t.register(1, 2, 50, 100, 10, vec![b, a], &residents), Ok(TournamentCutoff::Pending));
        assert_eq!(t.register(1, 2, 50, 100, 20, vec![a, b], &residents), Ok(TournamentCutoff::Pending));
        assert!(t.register(1, 2, 50, 101, 20, vec![a, b], &residents).is_err());
        t.destroyed(b, 99);
        assert_eq!(t.freeze(1, 2, 99, &BTreeSet::from([a])), TournamentCutoff::Pending);
        let expected =
            TournamentCutoff::Ready(vec![(a, TournamentEvidence::Alive), (b, TournamentEvidence::Destroyed)]);
        assert_eq!(t.freeze(1, 2, 100, &BTreeSet::from([a])), expected);
        t.destroyed(a, 101);
        assert_eq!(t.read(1, 2, 50, 100), Ok(expected));
        assert_eq!(TournamentTracker::default().read(1, 2, 50, 100), Ok(TournamentCutoff::Unavailable));
    }
    #[test]
    fn shared_deadline_freezes_once_and_overdue_never_invents_history() {
        let a = pilot(1);
        let residents = BTreeSet::from([a]);
        let mut t = TournamentTracker::default();
        assert_eq!(t.register(1, 1, 50, 100, 10, vec![a], &residents), Ok(TournamentCutoff::Pending));
        assert_eq!(t.register(2, 1, 50, 100, 10, vec![a], &residents), Ok(TournamentCutoff::Pending));
        assert_eq!(t.register(3, 1, 60, 110, 10, vec![a], &residents), Ok(TournamentCutoff::Pending));
        assert_eq!(t.next_deadline(), Some(100));
        t.freeze_due(100, &residents);
        let ready = Ok(TournamentCutoff::Ready(vec![(a, TournamentEvidence::Alive)]));
        assert_eq!(t.read(1, 1, 50, 100), ready);
        assert_eq!(t.read(2, 1, 50, 100), ready);
        assert!(t.read(1, 1, 51, 100).is_err());
        assert_eq!(t.next_deadline(), Some(110));
        t.freeze_due(111, &residents);
        assert_eq!(t.read(3, 1, 60, 110), Ok(TournamentCutoff::Unavailable));
        assert_eq!(t.next_deadline(), None);
    }
    #[test]
    fn release_fences_late_registration_and_conflicts_without_eviction() {
        let a = pilot(1);
        let residents = BTreeSet::from([a]);
        let mut t = TournamentTracker::default();
        assert_eq!(t.release(1, 1, 50, 100), Ok(()));
        assert_eq!(t.release(1, 1, 50, 100), Ok(()));
        assert_eq!(t.register(1, 1, 50, 100, 10, vec![a], &residents), Ok(TournamentCutoff::Unavailable));
        assert!(t.release(1, 1, 51, 100).is_err());
        assert!(t.register(1, 1, 51, 100, 10, vec![a], &residents).is_err());
        assert_eq!(t.register(2, 1, 50, 100, 10, vec![a], &residents), Ok(TournamentCutoff::Pending));
        assert!(t.release(2, 1, 50, 101).is_err());
        assert_eq!(t.read(2, 1, 50, 100), Ok(TournamentCutoff::Pending));
        assert_eq!(t.release(2, 1, 50, 100), Ok(()));
        for id in 3..=(TournamentTracker::MAX_TOMBSTONES as i64) {
            assert_eq!(t.release(id, 1, 50, 100), Ok(()));
        }
        assert!(t.release(9000, 1, 50, 100).is_err());
        assert_eq!(t.release(1, 1, 50, 100), Ok(()));
    }
    #[test]
    fn a_ship_destroyed_before_start_is_not_scored_as_a_match_loss() {
        let a = pilot(1);
        let residents = BTreeSet::from([a]);
        let mut t = TournamentTracker::default();
        assert_eq!(t.register(1, 1, 50, 100, 10, vec![a], &residents), Ok(TournamentCutoff::Pending));
        t.destroyed(a, 49);
        assert_eq!(t.freeze(1, 1, 100, &BTreeSet::new()), TournamentCutoff::Unavailable);
        for (id, at) in [(2, 50), (3, 100)] {
            assert_eq!(t.register(id, 1, 50, 100, 10, vec![a], &residents), Ok(TournamentCutoff::Pending));
            t.destroyed(a, at);
            assert_eq!(
                t.freeze(id, 1, 100, &BTreeSet::new()),
                TournamentCutoff::Ready(vec![(a, TournamentEvidence::Destroyed)])
            );
        }
    }

    #[test]
    fn absent_late_conflicting_and_unobserved_cannot_prove_death() {
        let a = pilot(1);
        let residents = BTreeSet::from([a]);
        let mut t = TournamentTracker::default();
        assert_eq!(t.register(1, 1, 50, 100, 70, vec![a], &residents), Ok(TournamentCutoff::Unavailable));
        assert_eq!(
            t.register(2, 1, 50, 100, 10, vec![a], &BTreeSet::new()),
            Ok(TournamentCutoff::Unavailable)
        );
        assert_eq!(t.register(3, 1, 50, 100, 10, vec![a], &residents), Ok(TournamentCutoff::Pending));
        t.departed(a, 20);
        t.destroyed(a, 30);
        assert_eq!(t.freeze(3, 1, 100, &residents), TournamentCutoff::Unavailable);
        assert_eq!(t.register(4, 1, 50, 100, 10, vec![a], &residents), Ok(TournamentCutoff::Pending));
        assert_eq!(t.freeze(4, 1, 101, &residents), TournamentCutoff::Unavailable);
    }
}
