use super::*;
use crate::link::AbyssalTrace;

impl Scene {
    pub(super) fn sees_place_of_trace(&self, observer: SessionId, trace: &AbyssalTrace) -> bool {
        self.presence(observer).is_some_and(|presence| {
            can_see(
                presence,
                Presence {
                    position: Vec3::new(trace.position[0], trace.position[1], trace.position[2]),
                    warping: false,
                },
            )
        })
    }

    pub(crate) fn abyssal_traces_in_view(&self, session: SessionId) -> Vec<&AbyssalTrace> {
        let seen = if let Some(pilot) = self.pilots.get(&session) {
            &pilot.sees_abyssal_traces
        } else if let Some(observer) = self.controlled_structures.get(&session) {
            &observer.sees_abyssal_traces
        } else {
            return Vec::new();
        };
        self.abyssal_traces.values().filter(|trace| seen.contains(&trace.item_id)).collect()
    }

    fn sees_abyssal_trace(&self, observer: SessionId, item_id: i64) -> bool {
        self.pilots
            .get(&observer)
            .map(|pilot| pilot.sees_abyssal_traces.contains(&item_id))
            .or_else(|| {
                self.controlled_structures
                    .get(&observer)
                    .map(|view| view.sees_abyssal_traces.contains(&item_id))
            })
            .unwrap_or(false)
    }

    fn set_sees_abyssal_trace(&mut self, observer: SessionId, item_id: i64, seen: bool) {
        let seen_set =
            self.pilots.get_mut(&observer).map(|pilot| &mut pilot.sees_abyssal_traces).or_else(|| {
                self.controlled_structures.get_mut(&observer).map(|view| &mut view.sees_abyssal_traces)
            });
        if let Some(seen_set) = seen_set {
            if seen {
                seen_set.insert(item_id);
            } else {
                seen_set.remove(&item_id);
            }
        }
    }

    /// Inserts or confirms a public, dynamic trace entity in a normal solar-system scene.
    pub(crate) fn ensure_abyssal_trace(
        &mut self,
        trace: AbyssalTrace,
        now: i64,
    ) -> Result<(bool, Vec<Out>), String> {
        if self.abyssal.is_some() || trace.solar_system_id != self.solar_system_id() {
            return Err("ABYSSAL_TRACE_SCENE_MISMATCH".to_string());
        }
        if let Some(existing) = self.abyssal_traces.get(&trace.item_id) {
            if existing != &trace {
                return Err("ABYSSAL_TRACE_IDENTITY_CONFLICT".to_string());
            }
            return Ok((true, self.reconcile_abyssal_traces(now)));
        }
        let collision = self.wrecks.contains_key(&trace.item_id)
            || self.asteroids.contains_key(&trace.item_id)
            || self.deployables.contains_key(&trace.item_id)
            || self.structure_health.contains_key(&trace.item_id)
            || self.sim_damageable_entities.contains(&trace.item_id)
            || self.pilots.values().any(|pilot| pilot.ship.spec.ship_id == trace.item_id)
            || self.system().bodies.iter().any(|body| body.id == trace.item_id);
        if collision {
            return Err("ABYSSAL_TRACE_ENTITY_ID_CONFLICT".to_string());
        }
        if payload::add_abyssal_trace(crate::scene::stamp_at(now), &trace).is_err() {
            return Err("ABYSSAL_TRACE_BALL_INVALID".to_string());
        }
        self.abyssal_traces.insert(trace.item_id, trace);
        Ok((false, self.reconcile_abyssal_traces(now)))
    }

    /// Removes one public trace, telling only bound viewers who currently have its ball.
    pub(crate) fn remove_abyssal_trace(&mut self, item_id: i64, now: i64) -> (bool, Vec<Out>) {
        if self.abyssal_traces.remove(&item_id).is_none() {
            return (true, Vec::new());
        }
        let watchers: Vec<_> = self
            .view_sessions()
            .into_iter()
            .filter(|observer| self.sees_abyssal_trace(*observer, item_id))
            .collect();
        let viewers: Vec<_> = self.pilots.keys().chain(self.controlled_structures.keys()).copied().collect();
        for viewer in viewers {
            self.set_sees_abyssal_trace(viewer, item_id, false);
        }
        if watchers.is_empty() {
            return (false, Vec::new());
        }
        let removal = payload::remove_ships(self.next_stamp(now), [item_id]);
        (false, watchers.into_iter().map(|watcher| Self::update(watcher, vec![removal.clone()])).collect())
    }

    /// Reconciles traces with each bound ballpark as ships move into or out of their visible grid.
    pub(crate) fn reconcile_abyssal_traces(&mut self, now: i64) -> Vec<Out> {
        if self.abyssal_traces.is_empty() {
            return Vec::new();
        }
        let mut out = Vec::new();
        for observer in self.view_sessions() {
            let seen: BTreeSet<i64> = self
                .abyssal_traces
                .keys()
                .copied()
                .filter(|id| self.sees_abyssal_trace(observer, *id))
                .collect();
            let should: BTreeSet<i64> = self
                .abyssal_traces
                .values()
                .filter(|trace| self.sees_place_of_trace(observer, trace))
                .map(|trace| trace.item_id)
                .collect();
            let gone: Vec<_> = seen.difference(&should).copied().collect();
            let coming: Vec<_> = should.difference(&seen).copied().collect();
            if !gone.is_empty() {
                let stamp = self.next_stamp(now);
                out.push(Self::update(observer, vec![payload::remove_ships(stamp, gone.iter().copied())]));
                for id in gone {
                    self.set_sees_abyssal_trace(observer, id, false);
                }
            }
            for id in coming {
                let Some(trace) = self.abyssal_traces.get(&id).cloned() else { continue };
                let stamp = self.next_stamp(now);
                if let Ok(added) = payload::add_abyssal_trace(stamp, &trace) {
                    self.set_sees_abyssal_trace(observer, id, true);
                    out.push(Self::update(observer, vec![added]));
                }
            }
        }
        out
    }
}
