//! Process-local, bounded observation of tournament combat. No history is inferred after restart.
use super::TICKS_PER_SECOND;
use crate::health::Applied;
use std::collections::{BTreeMap, BTreeSet, VecDeque};

#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord)]
pub(crate) struct Participant {
    pub character: i64,
    pub ship: i64,
    pub team: u8,
}
#[derive(Debug, Clone, Copy)]
pub(crate) enum Source {
    Ship(i64),
}
#[derive(Debug)]
struct Event {
    at: i64,
    team: u8,
    taken: f64,
}
#[derive(Debug)]
struct Match {
    start: i64,
    roster: Vec<Participant>,
    events: VecDeque<Event>,
    complete: bool,
    destroyed: BTreeSet<(i64, i64)>,
    last_snapshot: i64,
    observed_from: i64,
}

/// Internal, same-scene read model. `None` for a field means its authoritative state is not loaded.
#[derive(Debug, Clone)]
pub(crate) struct RunningSample {
    pub module_id: i64,
    pub effect_id: i32,
    pub target_id: i64,
    pub charge_type: Option<i32>,
    pub rounds: Option<i64>,
}
#[derive(Debug, Clone)]
pub(crate) struct FittedSample {
    pub item_id: i64,
    pub type_id: i32,
    pub flag: i32,
    pub overloaded: Option<bool>,
}
#[derive(Debug, Clone)]
pub(crate) struct CompanionSample {
    pub ship_id: i64,
    pub type_id: i32,
    pub controller_ship_id: i64,
    pub activity: i32,
    pub kind: crate::ship::CompanionKind,
    pub members: Option<i32>,
}
#[derive(Debug, Clone)]
pub(crate) struct PilotSample {
    pub participant: Participant,
    pub destroyed: bool,
    pub fitted: Vec<FittedSample>,
    pub running: Vec<RunningSample>,
    pub companions: Vec<CompanionSample>,
    pub ehp: crate::health::RemainingEffectiveHp,
}
#[derive(Debug, Clone)]
pub(crate) struct SceneSample {
    pub match_id: i64,
    pub generation: i64,
    pub start: i64,
    pub at: i64,
    pub applied: Option<[f64; 2]>,
    pub pilots: Vec<PilotSample>,
}

impl super::Scene {
    pub(crate) fn tournament_metrics_register(
        &mut self,
        id: i64,
        generation: i64,
        start: i64,
        now: i64,
        roster: Vec<Participant>,
    ) -> Result<(), &'static str> {
        let identity: Vec<_> = roster.iter().map(|p| (p.character, p.ship)).collect();
        let prior = self.tournament_metrics_prior_deaths(id, start, &identity).unwrap_or_default();
        if roster.iter().any(|p| {
            !prior.contains(&(p.character, p.ship))
                && !self.pilots.values().any(|pilot| {
                    pilot.ship.spec.ship_id == p.ship && pilot.ship.spec.character_id == p.character
                })
        }) {
            return Err("telemetry participant unavailable");
        }
        self.tournament_metrics.register(id, generation, start, now, roster)?;
        for (character, ship) in prior {
            self.tournament_metrics.destroyed(character, ship, now);
        }
        Ok(())
    }
    pub(crate) fn tournament_has_metrics(&self) -> bool {
        !self.tournament_metrics.matches.is_empty() || !self.tournament_metrics.tombstones.is_empty()
    }
    /// Identity is checked against the immutable registered roster before returning any sample.
    pub(crate) fn tournament_metrics_sample(
        &mut self,
        match_id: i64,
        generation: i64,
        start: i64,
        at: i64,
        roster: &[Participant],
    ) -> Option<SceneSample> {
        let mut expected = roster.to_vec();
        expected.sort_unstable();
        let registered = self.tournament_metrics.matches.get(&(match_id, generation))?;
        if registered.start != start || registered.roster != expected {
            return None;
        }
        if self.tournament_has_pending_host_work(at) {
            return None;
        }
        let destroyed = registered.destroyed.clone();
        let mut pilots = Vec::with_capacity(expected.len());
        let mut total = 0usize;
        for participant in expected {
            if destroyed.contains(&(participant.character, participant.ship)) {
                pilots.push(PilotSample {
                    participant,
                    destroyed: true,
                    fitted: Vec::new(),
                    running: Vec::new(),
                    companions: Vec::new(),
                    ehp: crate::health::RemainingEffectiveHp::Finite(0.0),
                });
                continue;
            }
            let (session, pilot) = self.pilots.iter().find(|(_, p)| {
                p.ship.spec.ship_id == participant.ship && p.ship.spec.character_id == participant.character
            })?;
            if pilot.ship.spec.modules.len() > 256 || pilot.modules.running.len() > 256 {
                return None;
            }
            total += pilot.ship.spec.modules.len() + pilot.modules.running.len();
            if total > 8192 {
                return None;
            }
            let fitted = pilot
                .ship
                .spec
                .modules
                .iter()
                .map(|m| FittedSample {
                    item_id: m.item_id,
                    type_id: m.type_id,
                    flag: m.flag,
                    overloaded: pilot.heat.modules.get(&m.item_id).map(|h| h.overloaded),
                })
                .collect();
            let running = pilot
                .modules
                .running
                .iter()
                .map(|r| RunningSample {
                    module_id: r.effect.module_id,
                    effect_id: r.effect.effect_id,
                    target_id: r.effect.target_id,
                    charge_type: r.effect.turret.as_ref().and_then(|t| t.charge.map(|c| c.type_id)),
                    rounds: r.telemetry_rounds(),
                })
                .collect();
            let companions = self
                .pilots
                .values()
                .filter_map(|p| {
                    let d = p.ship.npc.as_ref()?.drone.as_ref()?;
                    (d.controller == *session && d.controller_ship_id == participant.ship).then_some(
                        CompanionSample {
                            ship_id: p.ship.spec.ship_id,
                            type_id: p.ship.spec.type_id,
                            controller_ship_id: d.controller_ship_id,
                            activity: d.activity,
                            kind: d.kind,
                            members: d.squadron.as_ref().map(|s| {
                                if p.ship.health.layers_at(at).structure <= 1e-9 { 0 } else { s.told_size }
                            }),
                        },
                    )
                })
                .take(257)
                .collect::<Vec<_>>();
            if companions.len() > 256 {
                return None;
            }
            total += companions.len();
            if total > 8192 {
                return None;
            }
            pilots.push(PilotSample {
                participant,
                destroyed: false,
                fitted,
                running,
                companions,
                ehp: self.remaining_effective_hp_at(*session, at),
            });
        }
        let applied = self.tournament_metrics.snapshot(match_id, generation, at);
        Some(SceneSample { match_id, generation, start, at, applied, pilots })
    }
}

#[derive(Debug, Default)]
pub(crate) struct DamageLedger {
    matches: BTreeMap<(i64, i64), Match>,
    tombstones: BTreeMap<(i64, i64), (i64, Vec<Participant>)>,
}
impl DamageLedger {
    const MAX_MATCHES: usize = 64;
    const MAX_EVENTS: usize = 4096;
    pub(crate) fn register(
        &mut self,
        id: i64,
        generation: i64,
        start: i64,
        now: i64,
        mut roster: Vec<Participant>,
    ) -> Result<(), &'static str> {
        roster.sort_unstable();
        if id <= 0
            || generation <= 0
            || start <= 0
            || roster.is_empty()
            || roster.len() > 256
            || roster.iter().any(|p| p.character <= 0 || p.ship <= 0 || p.team > 1)
            || roster.iter().map(|p| p.character).collect::<BTreeSet<_>>().len() != roster.len()
            || roster.iter().map(|p| p.ship).collect::<BTreeSet<_>>().len() != roster.len()
        {
            return Err("invalid telemetry roster");
        }
        let key = (id, generation);
        if self.tombstones.contains_key(&key) {
            return Err("telemetry registration released");
        }
        if let Some(old) = self.matches.get(&key) {
            return if old.start == start && old.roster == roster {
                Ok(())
            } else {
                Err("conflicting telemetry registration")
            };
        }
        if self.matches.len() >= Self::MAX_MATCHES {
            return Err("telemetry capacity exceeded");
        }
        self.matches.insert(
            key,
            Match {
                start,
                roster,
                events: VecDeque::new(),
                complete: true,
                destroyed: BTreeSet::new(),
                last_snapshot: now,
                observed_from: now,
            },
        );
        Ok(())
    }
    pub(crate) fn destroyed(&mut self, character: i64, ship: i64, at: i64) {
        for m in self.matches.values_mut() {
            if m.roster.iter().any(|p| p.character == character && p.ship == ship) {
                if at < m.start {
                    m.complete = false;
                } else {
                    m.destroyed.insert((character, ship));
                }
            }
        }
    }
    pub(crate) fn departed(&mut self, character: i64, ship: i64, at: i64) {
        for m in self.matches.values_mut() {
            if at >= m.start && m.roster.iter().any(|p| p.character == character && p.ship == ship) {
                m.complete = false;
            }
        }
    }
    pub(crate) fn release(
        &mut self,
        id: i64,
        generation: i64,
        start: i64,
        mut roster: Vec<Participant>,
    ) -> Result<(), &'static str> {
        roster.sort_unstable();
        let key = (id, generation);
        if let Some(old) = self.tombstones.get(&key) {
            return if *old == (start, roster) { Ok(()) } else { Err("conflicting telemetry release") };
        }
        if let Some(old) = self.matches.get(&key)
            && (old.start != start || old.roster != roster)
        {
            return Err("conflicting telemetry release");
        }
        if id <= 0
            || generation <= 0
            || start <= 0
            || roster.is_empty()
            || roster.len() > 256
            || roster.iter().any(|p| p.character <= 0 || p.ship <= 0 || p.team > 1)
            || roster.iter().map(|p| p.character).collect::<BTreeSet<_>>().len() != roster.len()
            || roster.iter().map(|p| p.ship).collect::<BTreeSet<_>>().len() != roster.len()
            || self.tombstones.len() >= 4096
        {
            return Err("invalid or full telemetry release");
        }
        self.tombstones.insert(key, (start, roster));
        self.matches.remove(&key);
        Ok(())
    }
    pub(crate) fn record(&mut self, source: Source, target: i64, at: i64, applied: Option<Applied>) {
        let Some(hit) = applied else { return };
        let taken = hit.taken.shield + hit.taken.armor + hit.taken.structure;
        for m in self.matches.values_mut() {
            if at < m.start || !m.roster.iter().any(|p| p.ship == target) {
                continue;
            }
            let source = m.roster.iter().find(|p| match source {
                Source::Ship(id) => p.ship == id,
            });
            let Some(source) = source else {
                m.complete = false;
                continue;
            };
            if !taken.is_finite() || taken < 0.0 || at < m.last_snapshot {
                m.complete = false;
                continue;
            }
            if m.events.len() == Self::MAX_EVENTS {
                m.complete = false;
                continue;
            }
            m.events.push_back(Event { at, team: source.team, taken });
        }
    }
    /// Fixed one-second denominator, including the initial partial window. None means incomplete.
    pub(crate) fn snapshot(&mut self, id: i64, generation: i64, now: i64) -> Option<[f64; 2]> {
        let m = self.matches.get_mut(&(id, generation))?;
        if now < m.last_snapshot {
            m.complete = false;
        }
        m.last_snapshot = m.last_snapshot.max(now);
        if !m.complete
            || (m.observed_from > m.start && now < m.observed_from.saturating_add(TICKS_PER_SECOND))
        {
            return None;
        }
        let from = now.saturating_sub(TICKS_PER_SECOND).max(m.start.saturating_sub(1));
        m.events.retain(|e| e.at > from);
        let mut sums = [0.0; 2];
        for e in &m.events {
            if e.at <= now {
                sums[e.team as usize] += e.taken;
            }
        }
        if sums.iter().all(|x| x.is_finite()) {
            Some(sums)
        } else {
            m.complete = false;
            None
        }
    }
}
