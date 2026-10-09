use super::*;

impl Scene {
    pub(super) fn sees_asteroid(&self, observer: SessionId, asteroid: &crate::wreck::Asteroid) -> bool {
        self.presence(observer)
            .is_some_and(|o| can_see(o, Presence { position: asteroid.position, warping: false }))
    }

    /// A host-created ore rock enters the scene independently of wreck/loot state.
    pub fn place_asteroid(&mut self, asteroid: crate::wreck::Asteroid, now: i64) -> Vec<Out> {
        let id = asteroid.id;
        self.asteroids.insert(id, asteroid);
        let stamp = self.next_stamp(now);
        let Some(asteroid) = self.asteroids.get(&id) else { return Vec::new() };
        let Ok(added) = payload::add_asteroid(stamp, asteroid, now) else { return Vec::new() };
        let watchers: Vec<SessionId> = self
            .in_entry_order()
            .into_iter()
            .filter(|s| self.pilots[s].bound && self.sees_asteroid(*s, asteroid))
            .collect();
        watchers
            .into_iter()
            .map(|watcher| {
                if let Some(pilot) = self.pilots.get_mut(&watcher) {
                    pilot.sees_asteroids.insert(id);
                }
                Self::update(watcher, vec![added.clone()])
            })
            .collect()
    }

    /// A mining cycle changed a rock's authoritative stock.
    pub fn change_asteroid(&mut self, asteroid_id: i64, units_remaining: i64, now: i64) -> Vec<Out> {
        if units_remaining <= 0 {
            return self.remove_asteroid(asteroid_id, true, now);
        }
        let Some(asteroid) = self.asteroids.get_mut(&asteroid_id) else { return Vec::new() };
        asteroid.units_remaining = units_remaining;
        let stamp = self.next_stamp(now);
        let Some(asteroid) = self.asteroids.get(&asteroid_id) else { return Vec::new() };
        let changed = payload::slim_asteroid_change(stamp, asteroid);
        self.in_entry_order()
            .into_iter()
            .filter(|s| self.pilots[s].bound && self.pilots[s].sees_asteroids.contains(&asteroid_id))
            .map(|watcher| Self::update(watcher, vec![changed.clone()]))
            .collect()
    }

    /// A fully depleted rock dissolves using the existing Destiny terminal effect and RemoveBalls path.
    pub fn remove_asteroid(&mut self, asteroid_id: i64, depleted: bool, now: i64) -> Vec<Out> {
        if self.asteroids.remove(&asteroid_id).is_none() {
            return Vec::new();
        }
        let watchers: Vec<SessionId> = self
            .in_entry_order()
            .into_iter()
            .filter(|s| self.pilots[s].bound && self.pilots[s].sees_asteroids.contains(&asteroid_id))
            .collect();
        for pilot in self.pilots.values_mut() {
            pilot.sees_asteroids.remove(&asteroid_id);
        }
        if watchers.is_empty() {
            return Vec::new();
        }
        let stamp = self.next_stamp(now);
        let actions = if depleted {
            payload::dissolve(stamp, asteroid_id)
        } else {
            vec![payload::remove_ships(stamp, [asteroid_id])]
        };
        watchers.into_iter().map(|watcher| Self::update(watcher, actions.clone())).collect()
    }

    /// The distance from the controller's ship to an ore rock's surface.
    pub fn distance_to_asteroid(&mut self, session: SessionId, asteroid_id: i64, now: i64) -> Option<f64> {
        let (position, radius) = {
            let asteroid = self.asteroids.get(&asteroid_id)?;
            (asteroid.position, asteroid.radius)
        };
        let ship = self.ship(session, now)?.position;
        let centre =
            ((ship.x - position.x).powi(2) + (ship.y - position.y).powi(2) + (ship.z - position.z).powi(2))
                .sqrt();
        Some((centre - radius.max(0.0)).max(0.0))
    }

    /// Keeps mineable rock visibility in sync independently of wreck contents.
    pub(crate) fn reconcile_asteroids(&mut self, now: i64) -> Vec<Out> {
        if self.asteroids.is_empty() {
            return Vec::new();
        }
        let mut out = Vec::new();
        for observer in self.in_entry_order() {
            if !self.pilots[&observer].bound {
                continue;
            }
            let seen = self.pilots[&observer].sees_asteroids.clone();
            let should: std::collections::BTreeSet<i64> = self
                .asteroids
                .values()
                .filter(|asteroid| self.sees_asteroid(observer, asteroid))
                .map(|asteroid| asteroid.id)
                .collect();
            let gone: Vec<i64> = seen.difference(&should).copied().collect();
            let coming: Vec<i64> = should.difference(&seen).copied().collect();
            if !gone.is_empty() {
                let stamp = self.next_stamp(now);
                out.push(Self::update(observer, vec![payload::remove_ships(stamp, gone.iter().copied())]));
            }
            for id in coming {
                let stamp = self.next_stamp(now);
                if let Some(asteroid) = self.asteroids.get(&id) {
                    if let Ok(added) = payload::add_asteroid(stamp, asteroid, now) {
                        out.push(Self::update(observer, vec![added]));
                    }
                }
            }
            if let Some(pilot) = self.pilots.get_mut(&observer) {
                pilot.sees_asteroids = should;
            }
        }
        out
    }
}
