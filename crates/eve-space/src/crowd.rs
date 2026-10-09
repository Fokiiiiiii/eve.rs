//! The ships of a scene as each other's audience: who is told when a ship comes, goes or is given an order, and who sees whom.
//!
//! A ship is in the ballpark of another when the rule of [`crate::visibility`] says so. Every pilot that has bound the ballpark keeps the set of
//! ships it has been told of (`sees`); what happens to a ship is told to the pilots whose set holds it, and the sets are brought into line with the
//! rule when a ship comes, goes or is in warp (`reconcile`). A pilot that has not bound the ballpark is told nothing (the client cannot take a
//! destiny update yet); it gets everything in view when it binds.

use std::collections::BTreeSet;

use eve_marshal::Value;

use crate::payload;
use crate::scene::{Out, Scene, SessionId};
use crate::ship::Ship;
use crate::visibility::{CloakState, Presence, can_see, can_see_subject, grid_of};
use crate::wire::{id, real};
use crate::wreck::Wreck;
use eve_destiny::Vec3;

/// The nearest to the ship a jettisoned can is put (the reference's `buildNearbySpawnState`).
const MIN_JETTISON_DISTANCE: f64 = 50.0;

impl Scene {
    pub(crate) fn next_entry(&mut self) -> u64 {
        self.entries += 1;
        self.entries
    }

    fn presence(&self, session: SessionId) -> Option<Presence> {
        let pilot = self.pilots.get(&session)?;
        Some(Presence {
            position: pilot.ship.position,
            warping: pilot.flight.as_ref().is_some_and(|f| f.is_warping()),
        })
    }

    fn visible_to(&self, observer: SessionId, subject: SessionId) -> bool {
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

    /// Whether `observer` can see a wreck: it is in the same box of the grid (a wreck does not warp).
    fn sees_place_of(&self, observer: SessionId, wreck: &Wreck) -> bool {
        self.presence(observer)
            .is_some_and(|o| can_see(o, Presence { position: wreck.position, warping: false }))
    }

    fn sees_asteroid(&self, observer: SessionId, asteroid: &crate::wreck::Asteroid) -> bool {
        self.presence(observer)
            .is_some_and(|o| can_see(o, Presence { position: asteroid.position, warping: false }))
    }

    /// The wrecks in the ballpark of a pilot, in the order of their ids.
    pub(crate) fn wrecks_in_view(&self, session: SessionId) -> Vec<&Wreck> {
        let Some(pilot) = self.pilots.get(&session) else { return Vec::new() };
        self.wrecks.values().filter(|wreck| pilot.sees_wrecks.contains(&wreck.id)).collect()
    }

    /// A wreck comes into the scene: whoever has bound the ballpark and can see the place is sent it.
    pub fn place_wreck(&mut self, wreck: Wreck, now: i64) -> Vec<Out> {
        let id = wreck.id;
        self.wrecks.insert(id, wreck);
        let stamp = self.next_stamp(now);
        let Some(wreck) = self.wrecks.get(&id) else { return Vec::new() };
        let Ok(added) = payload::add_wreck(stamp, wreck, now) else { return Vec::new() };
        let watchers: Vec<SessionId> = self
            .in_entry_order()
            .into_iter()
            .filter(|s| self.pilots[s].bound && self.sees_place_of(*s, wreck))
            .collect();
        let mut out = Vec::new();
        for watcher in watchers {
            if let Some(pilot) = self.pilots.get_mut(&watcher) {
                pilot.sees_wrecks.insert(id);
            }
            out.push(Self::update(watcher, vec![added.clone()]));
        }
        out
    }

    /// A host-created ore rock enters the scene independently of wreck/loot state.
    pub fn place_asteroid(&mut self, asteroid: crate::wreck::Asteroid, now: i64) -> Vec<Out> {
        let id = asteroid.id;
        self.asteroids.insert(id, asteroid);
        let stamp = self.next_stamp(now);
        let Some(asteroid) = self.asteroids.get(&id) else { return Vec::new() };
        let Ok(added) = payload::add_asteroid(stamp, asteroid, now) else { return Vec::new() };
        let watchers: Vec<SessionId> = self.in_entry_order().into_iter()
            .filter(|s| self.pilots[s].bound && self.sees_asteroid(*s, asteroid))
            .collect();
        watchers.into_iter().map(|watcher| {
            if let Some(pilot) = self.pilots.get_mut(&watcher) {
                pilot.sees_asteroids.insert(id);
            }
            Self::update(watcher, vec![added.clone()])
        }).collect()
    }

    /// The pilots that have bound the ballpark and can see the place of a wreck, in the order they came into the scene.
    fn watchers_of(&self, wreck: &Wreck) -> Vec<SessionId> {
        self.in_entry_order()
            .into_iter()
            .filter(|s| self.pilots[s].bound && self.sees_place_of(*s, wreck))
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
            rights_known: false, pull: None, velocity: eve_destiny::Vec3::ZERO,
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

    /// What is in a wreck or a can changed: its slim item is told again to whoever has it in view (`OnSlimItemChange`).
    pub fn change_wreck(&mut self, wreck_id: i64, is_empty: bool, now: i64) -> Vec<Out> {
        let Some(wreck) = self.wrecks.get_mut(&wreck_id) else { return Vec::new() };
        wreck.is_empty = is_empty;
        wreck.rights_known |= !is_empty;
        let stamp = self.next_stamp(now);
        let Some(wreck) = self.wrecks.get(&wreck_id) else { return Vec::new() };
        let changed = payload::slim_item_change(stamp, wreck);
        self.in_entry_order()
            .into_iter()
            .filter(|s| self.pilots[s].bound && self.pilots[s].sees_wrecks.contains(&wreck_id))
            .map(|watcher| Self::update(watcher, vec![changed.clone()]))
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
        self.in_entry_order().into_iter()
            .filter(|s| self.pilots[s].bound && self.pilots[s].sees_asteroids.contains(&asteroid_id))
            .map(|watcher| Self::update(watcher, vec![changed.clone()]))
            .collect()
    }

    /// A wreck or a can is taken out of space by the host (it was emptied, or its time ran out): whoever had it in view is told, with the destruction effect
    /// `effect` played first when there is one (`0`: none).
    pub fn remove_wreck(&mut self, wreck_id: i64, effect: i64, now: i64) -> Vec<Out> {
        if self.wrecks.remove(&wreck_id).is_none() {
            return Vec::new();
        }
        let watchers: Vec<SessionId> = self
            .in_entry_order()
            .into_iter()
            .filter(|s| self.pilots[s].bound && self.pilots[s].sees_wrecks.contains(&wreck_id))
            .collect();
        for pilot in self.pilots.values_mut() {
            pilot.sees_wrecks.remove(&wreck_id);
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
        watchers.into_iter().map(|w| Self::update(w, actions.clone())).collect()
    }

    /// A fully depleted rock dissolves using the existing Destiny terminal effect and RemoveBalls path.
    pub fn remove_asteroid(&mut self, asteroid_id: i64, depleted: bool, now: i64) -> Vec<Out> {
        if self.asteroids.remove(&asteroid_id).is_none() {
            return Vec::new();
        }
        let watchers: Vec<SessionId> = self.in_entry_order().into_iter()
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

    /// A pilot that moved into a box of the grid is sent the wrecks there and one that moved out is told they are gone.
    pub(crate) fn reconcile_wrecks(&mut self, now: i64) -> Vec<Out> {
        if self.wrecks.is_empty() {
            return Vec::new();
        }
        let mut out = Vec::new();
        for observer in self.in_entry_order() {
            if !self.pilots[&observer].bound {
                continue;
            }
            let seen = self.pilots[&observer].sees_wrecks.clone();
            let should: std::collections::BTreeSet<i64> =
                self.wrecks.values().filter(|w| self.sees_place_of(observer, w)).map(|w| w.id).collect();
            let gone: Vec<i64> = seen.difference(&should).copied().collect();
            let coming: Vec<i64> = should.difference(&seen).copied().collect();
            if !gone.is_empty() {
                let stamp = self.next_stamp(now);
                out.push(Self::update(observer, vec![payload::remove_ships(stamp, gone.iter().copied())]));
            }
            for id in coming {
                let stamp = self.next_stamp(now);
                let Some(wreck) = self.wrecks.get(&id) else { continue };
                if let Ok(added) = payload::add_wreck(stamp, wreck, now) {
                    out.push(Self::update(observer, vec![added]));
                }
            }
            if let Some(pilot) = self.pilots.get_mut(&observer) {
                pilot.sees_wrecks = should;
            }
        }
        out
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
            let should: std::collections::BTreeSet<i64> = self.asteroids.values()
                .filter(|asteroid| self.sees_asteroid(observer, asteroid))
                .map(|asteroid| asteroid.id).collect();
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

    /// Sets what a pilot has in view when it binds the ballpark: every ship the rule lets it see.
    pub(crate) fn take_in_view(&mut self, session: SessionId) {
        let order = self.in_entry_order();
        let seen: BTreeSet<SessionId> =
            order.into_iter().filter(|s| *s != session && self.visible_to(session, *s)).collect();
        let wrecks: BTreeSet<i64> =
            self.wrecks.values().filter(|w| self.sees_place_of(session, w)).map(|w| w.id).collect();
        if let Some(pilot) = self.pilots.get_mut(&session) {
            pilot.bound = true;
            pilot.sees = seen;
            pilot.sees_wrecks = wrecks;
        }
    }

    /// The numbers the bind sends for a ship that is not the pilot's own: agility, mass and top speed.
    pub(crate) fn numbers_of(ship: &Ship, stamp: u32) -> Vec<Value> {
        let ship_id = id(ship.spec.ship_id);
        vec![
            payload::action(stamp, "SetBallAgility", [ship_id.clone(), real(ship.spec.agility)]),
            payload::action(stamp, "SetBallMass", [ship_id.clone(), real(ship.spec.mass)]),
            payload::action(stamp, "SetMaxSpeed", [ship_id, real(ship.spec.max_velocity)]),
        ]
    }
}

#[cfg(test)]
mod tests;
