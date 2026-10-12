use super::*;

impl Scene {
    /// Whether `observer` can see a wreck: it is in the same box of the grid (a wreck does not warp).
    pub(super) fn sees_place_of(&self, observer: SessionId, wreck: &Wreck) -> bool {
        self.presence(observer)
            .is_some_and(|o| can_see(o, Presence { position: wreck.position, warping: false }))
    }

    /// The wrecks in a bound view, in the order of their ids.
    pub(crate) fn wrecks_in_view(&self, session: SessionId) -> Vec<&Wreck> {
        let seen = if let Some(pilot) = self.pilots.get(&session) {
            &pilot.sees_wrecks
        } else if let Some(observer) = self.controlled_structures.get(&session) {
            &observer.sees_wrecks
        } else {
            return Vec::new();
        };
        self.wrecks.values().filter(|wreck| seen.contains(&wreck.id)).collect()
    }

    /// A wreck comes into the scene: whoever has bound the ballpark and can see the place is sent it.
    pub fn place_wreck(&mut self, wreck: Wreck, now: i64) -> Vec<Out> {
        let id = wreck.id;
        self.wrecks.insert(id, wreck);
        let stamp = self.next_stamp(now);
        let Some(wreck) = self.wrecks.get(&id) else { return Vec::new() };
        let Ok(added) = payload::add_wreck(stamp, wreck, now) else { return Vec::new() };
        self.watchers_of(wreck)
            .into_iter()
            .map(|watcher| {
                self.set_sees_wreck(watcher, id, true);
                Self::update(watcher, vec![added.clone()])
            })
            .collect()
    }

    /// The bound views that can see a wreck's place.
    pub(super) fn watchers_of(&self, wreck: &Wreck) -> Vec<SessionId> {
        self.view_sessions()
            .into_iter()
            .filter(|observer| self.sees_place_of(*observer, wreck))
            .collect()
    }

    /// A pilot jettisons cargo into a can (the host made the can, an item of its own): it comes into the scene `distance` metres ahead of the ship (at
    /// least fifty), at rest, and whoever has it in view is told in the reference's order: the ball, then the effect that plays the jettison. The place
    /// it came to is answered to the host (`Out::ContainerPlaced`), after what the watchers are told.
    pub fn place_container(
        &mut self,
        session: SessionId,
        can: crate::link::ContainerPlacement,
        now: i64,
    ) -> Vec<Out> {
        let Some(ship) = self.ship(session, now) else {
            return vec![Out::Refused {
                session,
                command: crate::link::kind::PLACE_CONTAINER,
                reason: "NO_SHIP_IN_SPACE",
            }];
        };
        let (origin, facing) = (ship.position, ship.direction);
        let length = (facing.x * facing.x + facing.y * facing.y + facing.z * facing.z).sqrt();
        let forward = if length > 0.0 && length.is_finite() {
            Vec3::new(facing.x / length, facing.y / length, facing.z / length)
        } else {
            Vec3::new(1.0, 0.0, 0.0)
        };
        let ahead = can.distance.max(MIN_JETTISON_DISTANCE);
        let position = Vec3::new(
            origin.x + forward.x * ahead,
            origin.y + forward.y * ahead,
            origin.z + forward.z * ahead,
        );
        let wreck = Wreck {
            id: can.container_id,
            group_id: crate::wreck::CONTAINER_GROUP,
            kind: can.kind,
            position,
            direction: forward,
            radius: can.radius,
            owner_id: can.credit.character_id,
            corporation_id: can.credit.corporation_id,
            alliance_id: can.credit.alliance_id,
            war_faction_id: can.credit.war_faction_id,
            rights: crate::wreck::LootRights {
                character_id: can.credit.character_id,
                corporation_id: can.credit.corporation_id,
            },
            launcher_id: can.launcher_id,
            is_empty: true,
            rights_known: false,
            pull: None,
            velocity: eve_destiny::Vec3::ZERO,
            expires_at: now + crate::wreck::WRECK_LIFETIME,
        };
        let id = wreck.id;
        self.wrecks.insert(id, wreck);
        let stamp = self.next_stamp(now);
        let Some(wreck) = self.wrecks.get(&id) else { return Vec::new() };
        let Ok(added) = payload::add_wreck(stamp, wreck, now) else {
            self.wrecks.remove(&id);
            return vec![Out::Refused {
                session,
                command: crate::link::kind::PLACE_CONTAINER,
                reason: "BAD_BALL",
            }];
        };
        let played = payload::jettison_effect(stamp, id);
        let watchers = self.watchers_of(wreck);
        let mut out = Vec::new();
        for watcher in watchers {
            if let Some(pilot) = self.pilots.get_mut(&watcher) {
                pilot.sees_wrecks.insert(id);
            }
            out.push(Self::update(watcher, vec![added.clone()]));
            out.push(Self::update(watcher, vec![played.clone()]));
        }
        out.push(Out::ContainerPlaced { session, container_id: id, position, direction: forward });
        out
    }

    /// What is in a wreck or a can changed: its slim item is told again to whoever has it in view.
    pub fn change_wreck(&mut self, wreck_id: i64, is_empty: bool, now: i64) -> Vec<Out> {
        let Some(wreck) = self.wrecks.get_mut(&wreck_id) else { return Vec::new() };
        wreck.is_empty = is_empty;
        wreck.rights_known |= !is_empty;
        let stamp = self.next_stamp(now);
        let Some(wreck) = self.wrecks.get(&wreck_id) else { return Vec::new() };
        let changed = payload::slim_item_change(stamp, wreck);
        self.view_sessions()
            .into_iter()
            .filter(|observer| self.sees_wreck(*observer, wreck_id))
            .map(|watcher| Self::update(watcher, vec![changed.clone()]))
            .collect()
    }

    /// A wreck or a can is taken out of space: whoever had it in view is told, with the destruction effect first when there is one.
    pub fn remove_wreck(&mut self, wreck_id: i64, effect: i64, now: i64) -> Vec<Out> {
        if self.wrecks.remove(&wreck_id).is_none() {
            return Vec::new();
        }
        let watchers: Vec<SessionId> = self
            .view_sessions()
            .into_iter()
            .filter(|observer| self.sees_wreck(*observer, wreck_id))
            .collect();
        let viewers: Vec<_> = self
            .pilots
            .keys()
            .chain(self.controlled_structures.keys())
            .copied()
            .collect();
        for viewer in viewers {
            self.set_sees_wreck(viewer, wreck_id, false);
        }
        if watchers.is_empty() {
            return Vec::new();
        }
        let stamp = self.next_stamp(now);
        let mut actions = Vec::new();
        if effect != 0 {
            actions.push(payload::action(
                stamp,
                "TerminalPlayDestructionEffect",
                [id(wreck_id), crate::wire::int(effect)],
            ));
        }
        actions.push(payload::remove_ships(stamp, [wreck_id]));
        watchers.into_iter().map(|watcher| Self::update(watcher, actions.clone())).collect()
    }

    /// How far the surface of a wreck or a can is from the centre of the pilot's ship (what the reference measures a container's range by), or `None`
    /// when the pilot has no ship or the wreck is not in the scene.
    pub fn distance_to_wreck(&mut self, session: SessionId, wreck_id: i64, now: i64) -> Option<f64> {
        let (position, radius) = {
            let wreck = self.wrecks.get(&wreck_id)?;
            (wreck.position, wreck.radius)
        };
        let ship = self.ship(session, now)?.position;
        let centre =
            ((ship.x - position.x).powi(2) + (ship.y - position.y).powi(2) + (ship.z - position.z).powi(2))
                .sqrt();
        Some((centre - radius.max(0.0)).max(0.0))
    }

    /// The wrecks that were due to go by `now` go: whoever had one in view is told.
    pub(crate) fn expire_wrecks(&mut self, now: i64) -> Vec<Out> {
        let gone: Vec<i64> = self.wrecks.values().filter(|w| w.expires_at <= now).map(|w| w.id).collect();
        if gone.is_empty() {
            return Vec::new();
        }
        let mut out = Vec::new();
        for id in gone {
            self.wrecks.remove(&id);
            let watchers: Vec<SessionId> = self
                .in_entry_order()
                .into_iter()
                .filter(|s| self.pilots[s].bound && self.pilots[s].sees_wrecks.contains(&id))
                .collect();
            for pilot in self.pilots.values_mut() {
                pilot.sees_wrecks.remove(&id);
            }
            if !watchers.is_empty() {
                let removal = payload::remove_ships(self.next_stamp(now), [id]);
                out.extend(watchers.into_iter().map(|w| Self::update(w, vec![removal.clone()])));
            }
        }
        out
    }

    /// A view that moved into a box is sent its wrecks, and one that moved out is told they are gone.
    pub(crate) fn reconcile_wrecks(&mut self, now: i64) -> Vec<Out> {
        if self.wrecks.is_empty() {
            return Vec::new();
        }
        let mut out = Vec::new();
        for observer in self.view_sessions() {
            let seen: std::collections::BTreeSet<i64> = self
                .wrecks
                .keys()
                .copied()
                .filter(|id| self.sees_wreck(observer, *id))
                .collect();
            let should: std::collections::BTreeSet<i64> =
                self.wrecks.values().filter(|wreck| self.sees_place_of(observer, wreck)).map(|wreck| wreck.id).collect();
            let gone: Vec<i64> = seen.difference(&should).copied().collect();
            let coming: Vec<i64> = should.difference(&seen).copied().collect();
            if !gone.is_empty() {
                let stamp = self.next_stamp(now);
                out.push(Self::update(observer, vec![payload::remove_ships(stamp, gone.iter().copied())]));
                for id in &gone {
                    self.set_sees_wreck(observer, *id, false);
                }
            }
            for id in coming {
                let stamp = self.next_stamp(now);
                let Some(wreck) = self.wrecks.get(&id) else { continue };
                if let Ok(added) = payload::add_wreck(stamp, wreck, now) {
                    self.set_sees_wreck(observer, id, true);
                    out.push(Self::update(observer, vec![added]));
                }
            }
        }
        out
    }
}
