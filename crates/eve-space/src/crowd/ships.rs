use super::*;

impl Scene {
    pub(crate) fn next_entry(&mut self) -> u64 {
        self.entries += 1;
        self.entries
    }

    pub(super) fn presence(&self, session: SessionId) -> Option<Presence> {
        let pilot = self.pilots.get(&session)?;
        Some(Presence {
            position: pilot.ship.position,
            warping: pilot.flight.as_ref().is_some_and(|f| f.is_warping()),
        })
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

    /// The pilots that have the ship in their ballpark, in the order they came.
    pub(crate) fn observers_of(&self, subject: SessionId) -> Vec<SessionId> {
        self.in_entry_order()
            .into_iter()
            .filter(|s| *s != subject && self.pilots[s].bound && self.pilots[s].sees.contains(&subject))
            .collect()
    }

    /// The ships in the ballpark of a pilot, itself included, in the order they came into the scene.
    pub(crate) fn ships_in_view(&self, session: SessionId) -> Vec<&Ship> {
        let Some(pilot) = self.pilots.get(&session) else { return Vec::new() };
        self.in_entry_order()
            .into_iter()
            .filter(|s| *s == session || pilot.sees.contains(s))
            .map(|s| &self.pilots[&s].ship)
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

    /// A ship has come into the scene (an undock, a jump): whoever has bound the ballpark and can see it is sent the ship.
    pub(crate) fn announce_arrival(&mut self, subject: SessionId, now: i64) -> Vec<Out> {
        let watchers: Vec<SessionId> = self
            .in_entry_order()
            .into_iter()
            .filter(|s| *s != subject && self.pilots[s].bound && self.visible_to(*s, subject))
            .collect();
        if watchers.is_empty() {
            return Vec::new();
        }
        let Some(actions) = self.introduction(subject, now) else { return Vec::new() };
        let mut out = Vec::new();
        for watcher in watchers {
            if let Some(pilot) = self.pilots.get_mut(&watcher) {
                pilot.sees.insert(subject);
            }
            out.push(Self::update(watcher, actions.clone()));
        }
        out
    }

    /// A ship has left the scene (docked, jumped, gone offline; its pilot is already out): those who had it in view are told it is gone.
    pub(crate) fn announce_departure(&mut self, subject: SessionId, ship_id: i64, now: i64) -> Vec<Out> {
        let watchers: Vec<SessionId> = self
            .in_entry_order()
            .into_iter()
            .filter(|s| self.pilots[s].bound && self.pilots[s].sees.contains(&subject))
            .collect();
        for pilot in self.pilots.values_mut() {
            pilot.sees.remove(&subject);
        }
        if watchers.is_empty() {
            return Vec::new();
        }
        let stamp = self.next_stamp(now);
        let removal = payload::remove_ships(stamp, [ship_id]);
        watchers.into_iter().map(|w| Self::update(w, vec![removal.clone()])).collect()
    }

    /// Brings what each pilot has in view into line with the rule, at `now`: a ship that has come into view is sent (`AddBalls2` and its
    /// numbers) and one that has gone out of view is taken away, in the packaged form the reference uses for a ship in warp. Only pilots that
    /// have bound the ballpark are told.
    pub(crate) fn reconcile(&mut self, now: i64) -> Vec<Out> {
        let order = self.in_entry_order();
        if order.len() < 2 {
            self.boxes.clear();
            self.warped_last = false;
            return Vec::new();
        }
        // what can change who sees whom is a ship crossing into or out of a box, coming or going, or a warp: when none of that has happened since
        // the last look there is nothing to do (the look costs a pass over the ships, not over the pairs)
        let mut boxes: std::collections::HashMap<(i64, i64, i64), Vec<SessionId>> =
            std::collections::HashMap::new();
        let mut warping = false;
        for session in &order {
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
        for observer in &order {
            if !self.pilots[observer].bound {
                continue;
            }
            let mut gone = Vec::new();
            for subject in &order {
                if subject == observer {
                    continue;
                }
                let sees = self.pilots[observer].sees.contains(subject);
                let should = self.visible_to(*observer, *subject);
                if should && !sees {
                    entering.push((*observer, *subject));
                } else if !should && sees {
                    gone.push(*subject);
                }
            }
            if !gone.is_empty() {
                leaving.push((*observer, gone));
            }
        }
        for (observer, gone) in leaving {
            let stamp = self.next_stamp(now);
            let ids: Vec<i64> = gone.iter().map(|s| self.pilots[s].ship.spec.ship_id).collect();
            if let Some(pilot) = self.pilots.get_mut(&observer) {
                for subject in &gone {
                    pilot.sees.remove(subject);
                }
            }
            out.push(Self::update(observer, vec![payload::remove_ships_packaged(stamp, ids)]));
        }
        for (observer, subject) in entering {
            let Some(actions) = self.introduction(subject, now) else { continue };
            if let Some(pilot) = self.pilots.get_mut(&observer) {
                pilot.sees.insert(subject);
            }
            out.push(Self::update(observer, actions));
        }
        out
    }
}
