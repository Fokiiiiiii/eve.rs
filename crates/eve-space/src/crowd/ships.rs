use super::*;

impl Scene {
    pub(crate) fn next_entry(&mut self) -> u64 {
        self.entries += 1;
        self.entries
    }

    pub(crate) fn presence(&self, session: SessionId) -> Option<Presence> {
        if let Some(pilot) = self.pilots.get(&session) {
            return Some(Presence {
                position: pilot.ship.position,
                warping: pilot.flight.as_ref().is_some_and(|f| f.is_warping()),
            });
        }
        let observer = self.controlled_structures.get(&session)?;
        let body = self.structure_body(observer.structure_id)?;
        Some(Presence { position: body.position, warping: false })
    }

    pub(super) fn visible_to(&self, observer: SessionId, subject: SessionId) -> bool {
        match (self.presence(observer), self.presence(subject)) {
            (Some(observer), Some(subject_presence)) => {
                let cloak = self.pilots.get(&subject).map_or(CloakState::Uncloaked, |pilot| pilot.cloak);
                can_see_subject(observer, subject_presence, cloak)
            }
            _ => false,
        }
    }

    /// The sessions of the pilots, in the order they came into the scene.
    pub(crate) fn in_entry_order(&self) -> Vec<SessionId> {
        let mut order: Vec<(u64, SessionId)> = self.pilots.iter().map(|(s, p)| (p.entered, *s)).collect();
        order.sort_unstable();
        order.into_iter().map(|(_, s)| s).collect()
    }

    /// Every client that currently has a scene view, ordered by pilot arrival then observer session.
    pub(crate) fn view_sessions(&self) -> Vec<SessionId> {
        let mut sessions: Vec<_> =
            self.in_entry_order().into_iter().filter(|session| self.pilots[session].bound).collect();
        sessions.extend(
            self.controlled_structures
                .iter()
                .filter(|(_, observer)| observer.bound)
                .map(|(session, _)| *session),
        );
        sessions
    }

    pub(super) fn sees_ship(&self, observer: SessionId, subject: SessionId) -> bool {
        self.pilots
            .get(&observer)
            .map(|pilot| pilot.sees.contains(&subject))
            .or_else(|| self.controlled_structures.get(&observer).map(|view| view.sees.contains(&subject)))
            .unwrap_or(false)
    }

    pub(super) fn set_sees_ship(&mut self, observer: SessionId, subject: SessionId, seen: bool) {
        let seen_set = self
            .pilots
            .get_mut(&observer)
            .map(|pilot| &mut pilot.sees)
            .or_else(|| self.controlled_structures.get_mut(&observer).map(|view| &mut view.sees));
        if let Some(seen_set) = seen_set {
            if seen {
                seen_set.insert(subject);
            } else {
                seen_set.remove(&subject);
            }
        }
    }

    pub(super) fn clear_ship_from_views(&mut self, subject: SessionId) {
        for pilot in self.pilots.values_mut() {
            pilot.sees.remove(&subject);
        }
        for observer in self.controlled_structures.values_mut() {
            observer.sees.remove(&subject);
        }
    }

    pub(super) fn sees_wreck(&self, observer: SessionId, wreck_id: i64) -> bool {
        self.pilots
            .get(&observer)
            .map(|pilot| pilot.sees_wrecks.contains(&wreck_id))
            .or_else(|| {
                self.controlled_structures.get(&observer).map(|view| view.sees_wrecks.contains(&wreck_id))
            })
            .unwrap_or(false)
    }

    pub(super) fn set_sees_wreck(&mut self, observer: SessionId, wreck_id: i64, seen: bool) {
        let seen_set = self
            .pilots
            .get_mut(&observer)
            .map(|pilot| &mut pilot.sees_wrecks)
            .or_else(|| self.controlled_structures.get_mut(&observer).map(|view| &mut view.sees_wrecks));
        if let Some(seen_set) = seen_set {
            if seen {
                seen_set.insert(wreck_id);
            } else {
                seen_set.remove(&wreck_id);
            }
        }
    }

    pub(super) fn sees_asteroid(&self, observer: SessionId, asteroid_id: i64) -> bool {
        self.pilots
            .get(&observer)
            .map(|pilot| pilot.sees_asteroids.contains(&asteroid_id))
            .or_else(|| {
                self.controlled_structures
                    .get(&observer)
                    .map(|view| view.sees_asteroids.contains(&asteroid_id))
            })
            .unwrap_or(false)
    }

    pub(super) fn set_sees_asteroid(&mut self, observer: SessionId, asteroid_id: i64, seen: bool) {
        let seen_set =
            self.pilots.get_mut(&observer).map(|pilot| &mut pilot.sees_asteroids).or_else(|| {
                self.controlled_structures.get_mut(&observer).map(|view| &mut view.sees_asteroids)
            });
        if let Some(seen_set) = seen_set {
            if seen {
                seen_set.insert(asteroid_id);
            } else {
                seen_set.remove(&asteroid_id);
            }
        }
    }

    /// The bound clients that currently have a ship in their ballpark.
    pub(crate) fn observers_of(&self, subject: SessionId) -> Vec<SessionId> {
        self.view_sessions()
            .into_iter()
            .filter(|observer| *observer != subject && self.sees_ship(*observer, subject))
            .collect()
    }

    pub(crate) fn ships_in_view(&self, session: SessionId) -> Vec<&Ship> {
        let (own, seen) = if let Some(pilot) = self.pilots.get(&session) {
            (true, &pilot.sees)
        } else if let Some(observer) = self.controlled_structures.get(&session) {
            (false, &observer.sees)
        } else {
            return Vec::new();
        };
        self.in_entry_order()
            .into_iter()
            .filter(|other| (own && *other == session) || seen.contains(other))
            .map(|other| &self.pilots[&other].ship)
            .collect()
    }

    pub(crate) fn ships_visible_to(&self, session: SessionId) -> Vec<&Ship> {
        if !self.pilots.contains_key(&session) {
            return Vec::new();
        }
        self.in_entry_order()
            .into_iter()
            .filter(|other| *other == session || self.visible_to(session, *other))
            .map(|other| &self.pilots[&other].ship)
            .collect()
    }

    pub(crate) fn update(session: SessionId, actions: Vec<Value>) -> Out {
        Scene::notify(session, "DoDestinyUpdate", payload::destiny_update(actions, false))
    }

    /// What happened to a ship, told to it and to everyone who has it in view: one update each, the same stamp.
    pub(crate) fn tell_all(&self, subject: SessionId, actions: Vec<Value>) -> Vec<Out> {
        self.tell_split(subject, actions.clone(), actions)
    }

    /// As [`Scene::tell_all`], with one set of actions for the ship's own client and another for those who watch it (an empty set says nothing).
    pub(crate) fn tell_split(&self, subject: SessionId, own: Vec<Value>, watchers: Vec<Value>) -> Vec<Out> {
        let mut out = Vec::new();
        if !own.is_empty() {
            out.push(Self::update(subject, own));
        }
        if !watchers.is_empty() {
            out.extend(self.observers_of(subject).into_iter().map(|o| Self::update(o, watchers.clone())));
        }
        out
    }

    /// `AddBalls2` of a ship followed by its numbers, as a watcher is sent a ship that has come into view.
    pub(crate) fn introduction(&mut self, subject: SessionId, now: i64) -> Option<Vec<Value>> {
        let stamp = self.next_stamp(now);
        let ship = &self.pilots.get(&subject)?.ship;
        let ship_id = id(ship.spec.ship_id);
        let added = payload::add_ships(stamp, &[ship], now).ok()?;
        Some(vec![
            added,
            payload::action(stamp, "SetBallAgility", [ship_id.clone(), real(ship.spec.agility)]),
            payload::action(stamp, "SetBallMass", [ship_id.clone(), real(ship.spec.mass)]),
            payload::action(stamp, "SetMaxSpeed", [ship_id, real(ship.spec.max_velocity)]),
        ])
    }

    /// A ship has come into the scene: every bound view that can see it is sent the ship.
    pub(crate) fn announce_arrival(&mut self, subject: SessionId, now: i64) -> Vec<Out> {
        let watchers: Vec<SessionId> = self
            .view_sessions()
            .into_iter()
            .filter(|observer| *observer != subject && self.visible_to(*observer, subject))
            .collect();
        if watchers.is_empty() {
            return Vec::new();
        }
        let Some(actions) = self.introduction(subject, now) else { return Vec::new() };
        let mut out = Vec::new();
        for watcher in watchers {
            self.set_sees_ship(watcher, subject, true);
            out.push(Self::update(watcher, actions.clone()));
        }
        out
    }

    /// A ship has left the scene: every bound view that had it is told it is gone.
    pub(crate) fn announce_departure(&mut self, subject: SessionId, ship_id: i64, now: i64) -> Vec<Out> {
        let watchers: Vec<SessionId> =
            self.view_sessions().into_iter().filter(|observer| self.sees_ship(*observer, subject)).collect();
        self.clear_ship_from_views(subject);
        if watchers.is_empty() {
            return Vec::new();
        }
        let stamp = self.next_stamp(now);
        let removal = payload::remove_ships(stamp, [ship_id]);
        watchers.into_iter().map(|watcher| Self::update(watcher, vec![removal.clone()])).collect()
    }

    /// Brings each bound pilot and structure view into line with the ships it can see.
    pub(crate) fn reconcile(&mut self, now: i64) -> Vec<Out> {
        let order = self.in_entry_order();
        let viewers = self.view_sessions();
        if order.is_empty() {
            self.boxes.clear();
            self.warped_last = false;
            return Vec::new();
        }
        if order.len() < 2 && !self.controlled_structures.values().any(|observer| observer.bound) {
            self.boxes.clear();
            self.warped_last = false;
            return Vec::new();
        }
        // A ship crossing a box, arriving, leaving, or warping can change visibility.
        let mut boxes: std::collections::HashMap<(i64, i64, i64), Vec<SessionId>> =
            std::collections::HashMap::new();
        let mut warping = false;
        for session in order.iter().chain(self.controlled_structures.keys()) {
            if let Some(presence) = self.presence(*session) {
                boxes.entry(grid_of(presence.position)).or_default().push(*session);
                warping |= presence.warping;
            }
        }
        if !warping && !self.warped_last && boxes == self.boxes {
            return Vec::new();
        }
        self.boxes = boxes;
        self.warped_last = warping;
        let mut out = Vec::new();
        let mut entering: Vec<(SessionId, SessionId)> = Vec::new();
        let mut leaving: Vec<(SessionId, Vec<SessionId>)> = Vec::new();
        for observer in viewers {
            let mut gone = Vec::new();
            for subject in &order {
                if *subject == observer {
                    continue;
                }
                let sees = self.sees_ship(observer, *subject);
                let should = self.visible_to(observer, *subject);
                if should && !sees {
                    entering.push((observer, *subject));
                } else if !should && sees {
                    gone.push(*subject);
                }
            }
            if !gone.is_empty() {
                leaving.push((observer, gone));
            }
        }
        for (observer, gone) in leaving {
            let stamp = self.next_stamp(now);
            let ids: Vec<i64> = gone.iter().map(|session| self.pilots[session].ship.spec.ship_id).collect();
            for subject in &gone {
                self.set_sees_ship(observer, *subject, false);
            }
            out.push(Self::update(observer, vec![payload::remove_ships_packaged(stamp, ids)]));
        }
        for (observer, subject) in entering {
            let Some(actions) = self.introduction(subject, now) else { continue };
            self.set_sees_ship(observer, subject, true);
            out.push(Self::update(observer, actions));
        }
        out
    }
}
