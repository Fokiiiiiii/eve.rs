use super::*;

impl Scene {
    pub(super) fn asteroid_visible_to(&self, observer: SessionId, asteroid: &crate::wreck::Asteroid) -> bool {
        self.presence(observer)
            .is_some_and(|o| can_see(o, Presence { position: asteroid.position, warping: false }))
    }

    /// A host-created ore rock enters independently of wreck/loot state.
    pub fn place_asteroid(&mut self, asteroid: crate::wreck::Asteroid, now: i64) -> Vec<Out> {
        let id = asteroid.id;
        self.asteroids.insert(id, asteroid);
        let stamp = self.next_stamp(now);
        let Some(asteroid) = self.asteroids.get(&id) else { return Vec::new() };
        let Ok(added) = payload::add_asteroid(stamp, asteroid, now) else { return Vec::new() };
        let watchers: Vec<SessionId> = self
            .view_sessions()
            .into_iter()
            .filter(|observer| self.asteroid_visible_to(*observer, asteroid))
            .collect();
        watchers
            .into_iter()
            .map(|watcher| {
                self.set_sees_asteroid(watcher, id, true);
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
        self.view_sessions()
            .into_iter()
            .filter(|observer| self.sees_asteroid(*observer, asteroid_id))
            .map(|watcher| Self::update(watcher, vec![changed.clone()]))
            .collect()
    }

    /// A fully depleted rock dissolves using the existing Destiny terminal effect and RemoveBalls path.
    pub fn remove_asteroid(&mut self, asteroid_id: i64, depleted: bool, now: i64) -> Vec<Out> {
        if self.asteroids.remove(&asteroid_id).is_none() {
            return Vec::new();
        }
        let watchers: Vec<SessionId> = self
            .view_sessions()
            .into_iter()
            .filter(|observer| self.sees_asteroid(*observer, asteroid_id))
            .collect();
        let viewers: Vec<_> = self.pilots.keys().chain(self.controlled_structures.keys()).copied().collect();
        for viewer in viewers {
            self.set_sees_asteroid(viewer, asteroid_id, false);
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

    pub fn has_asteroid(&self, asteroid_id: i64) -> bool {
        self.asteroids.contains_key(&asteroid_id)
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
        for observer in self.view_sessions() {
            let seen: std::collections::BTreeSet<i64> =
                self.asteroids.keys().copied().filter(|id| self.sees_asteroid(observer, *id)).collect();
            let should: std::collections::BTreeSet<i64> = self
                .asteroids
                .values()
                .filter(|asteroid| self.asteroid_visible_to(observer, asteroid))
                .map(|asteroid| asteroid.id)
                .collect();
            let gone: Vec<i64> = seen.difference(&should).copied().collect();
            let coming: Vec<i64> = should.difference(&seen).copied().collect();
            if !gone.is_empty() {
                let stamp = self.next_stamp(now);
                out.push(Self::update(observer, vec![payload::remove_ships(stamp, gone.iter().copied())]));
                for id in &gone {
                    self.set_sees_asteroid(observer, *id, false);
                }
            }
            for id in coming {
                let stamp = self.next_stamp(now);
                if let Some(asteroid) = self.asteroids.get(&id)
                    && let Ok(added) = payload::add_asteroid(stamp, asteroid, now)
                {
                    self.set_sees_asteroid(observer, id, true);
                    out.push(Self::update(observer, vec![added]));
                }
            }
        }
        out
    }
}
