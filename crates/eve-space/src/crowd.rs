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
use crate::visibility::{Presence, can_see, grid_of};
use crate::wire::{id, real};
use crate::wreck::Wreck;

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
            (Some(o), Some(s)) => can_see(o, s),
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
    fn introduction(&mut self, subject: SessionId, now: i64) -> Option<Vec<Value>> {
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
mod tests {
    use std::sync::Arc;

    use eve_destiny::Vec3;

    use super::*;
    use crate::scene::tests::{NOON, STATION, spec, system};
    use crate::scene::{Command, Departure};
    use crate::world::{Body, BodyKind, Detail};

    const A: SessionId = 7;
    const B: SessionId = 8;
    const SECOND: i64 = 10_000_000;
    const PLANET: i64 = 40_000_001;

    fn other_spec() -> crate::ship::ShipSpec {
        let mut other = spec();
        other.ship_id += 4;
        other.character_id += 1;
        other.owner_id += 1;
        other.name = "Second".to_string();
        other
    }

    /// The system of the tests with a planet far off (for warps).
    fn scene_with_a_planet() -> Scene {
        let mut world = (*system()).clone();
        world.bodies.push(Body {
            id: PLANET,
            type_id: 2015,
            group_id: 7,
            category_id: 2,
            owner_id: 0,
            name: "Testing III".to_string(),
            kind: BodyKind::Planet,
            position: Vec3::new(45_000_000_000.0, 0.0, 20_000_000_000.0),
            radius: 6_000_000.0,
            graphic_id: None,
            detail: Detail::None,
        });
        Scene::new(Arc::new(world))
    }

    /// A ship in space that has bound the ballpark.
    fn watcher() -> Scene {
        let mut scene = scene_with_a_planet();
        scene.undock(A, spec(), STATION, NOON).unwrap();
        scene.bind(A, NOON);
        scene
    }

    fn both() -> Scene {
        let mut scene = watcher();
        scene.undock(B, other_spec(), STATION, NOON + SECOND).unwrap();
        scene.bind(B, NOON + SECOND);
        scene
    }

    fn names(out: &Out) -> Vec<String> {
        let Out::Notify { args, .. } = out else { return Vec::new() };
        let Some(Value::List(actions)) = args.first() else { return Vec::new() };
        actions
            .iter()
            .map(|a| {
                let Value::Tuple(pair) = a else { panic!("{a:?}") };
                let Value::Tuple(inner) = &pair[1] else { panic!("{:?}", pair[1]) };
                let Value::Str(name) = &inner[0] else { panic!("{:?}", inner[0]) };
                name.to_string()
            })
            .collect()
    }

    fn stamps(out: &Out) -> Vec<i64> {
        let Out::Notify { args, .. } = out else { return Vec::new() };
        let Some(Value::List(actions)) = args.first() else { return Vec::new() };
        actions
            .iter()
            .map(|a| match a {
                Value::Tuple(pair) => match &pair[0] {
                    Value::Int(n) => *n,
                    other => panic!("{other:?}"),
                },
                other => panic!("{other:?}"),
            })
            .collect()
    }

    fn recipient(out: &Out) -> SessionId {
        match out {
            Out::Notify { session, .. } | Out::Accepted { session, .. } => *session,
            other => panic!("{other:?}"),
        }
    }

    /// The bytes of the ball buffer of the `SetState` an update carries.
    fn state_buffer(out: &Out) -> Vec<u8> {
        let Out::Notify { args, .. } = out else { panic!("{out:?}") };
        let Some(Value::List(actions)) = args.first() else { panic!("{out:?}") };
        let Value::Tuple(pair) = &actions[0] else { panic!() };
        let Value::Tuple(inner) = &pair[1] else { panic!() };
        let Value::Tuple(arguments) = &inner[1] else { panic!() };
        let Value::Object { args, .. } = &arguments[0] else { panic!("{:?}", arguments[0]) };
        let Value::Dict(fields) = &**args else { panic!() };
        let (_, buffer) = fields.iter().find(|(k, _)| matches!(k, Value::Str(s) if s == "state")).unwrap();
        let Value::Buffer(bytes) = buffer else { panic!("{buffer:?}") };
        bytes.to_vec()
    }

    fn holds_ball_of(buffer: &[u8], ship_id: i64) -> bool {
        let needle = ship_id.to_le_bytes();
        buffer.windows(8).any(|w| w == needle)
    }

    /// How many slim items an `AddBalls2` of an update carries, and the ids of their ships.
    fn added_ships(out: &Out) -> Vec<i64> {
        let Out::Notify { args, .. } = out else { panic!("{out:?}") };
        let Some(Value::List(actions)) = args.first() else { panic!("{out:?}") };
        let Value::Tuple(pair) = &actions[0] else { panic!() };
        let Value::Tuple(inner) = &pair[1] else { panic!() };
        let Value::Tuple(arguments) = &inner[1] else { panic!() };
        let Value::Tuple(added) = &arguments[0] else { panic!() };
        let Value::List(extras) = &added[1] else { panic!() };
        extras
            .iter()
            .map(|extra| {
                let Value::Tuple(entry) = extra else { panic!() };
                let Value::Dict(slim) = &entry[0] else { panic!() };
                let (_, item) =
                    slim.iter().find(|(k, _)| matches!(k, Value::Str(s) if s == "itemID")).unwrap();
                match item {
                    Value::Int(n) | Value::Long(n) => *n,
                    other => panic!("{other:?}"),
                }
            })
            .collect()
    }

    #[test]
    fn a_ship_that_undocks_is_shown_to_the_pilot_that_has_bound_the_ballpark_with_its_numbers_in_one_update()
    {
        let mut scene = watcher();
        let told = scene.undock(B, other_spec(), STATION, NOON + SECOND).unwrap();
        assert_eq!(told.len(), 1);
        assert_eq!(recipient(&told[0]), A);
        assert_eq!(names(&told[0]), ["AddBalls2", "SetBallAgility", "SetBallMass", "SetMaxSpeed"]);
        let stamps = stamps(&told[0]);
        assert!(stamps.iter().all(|s| *s == stamps[0]), "one stamp: {stamps:?}");
        assert_eq!(added_ships(&told[0]), [other_spec().ship_id]);
    }

    #[test]
    fn a_pilot_that_has_not_bound_the_ballpark_is_told_nothing_and_is_given_the_ships_when_it_binds() {
        let mut scene = scene_with_a_planet();
        scene.undock(A, spec(), STATION, NOON).unwrap();
        assert!(scene.undock(B, other_spec(), STATION, NOON).unwrap().is_empty());
        let bound = scene.bind(A, NOON + SECOND);
        assert_eq!(
            added_ships(&bound[2]),
            [spec().ship_id, other_spec().ship_id],
            "both, in the order they came"
        );
    }

    #[test]
    fn binding_gives_the_ships_in_view_in_the_order_they_came_and_the_numbers_of_each() {
        let mut scene = watcher();
        scene.undock(B, other_spec(), STATION, NOON + SECOND).unwrap();
        let bound = scene.bind(B, NOON + SECOND);
        assert_eq!(
            bound.len(),
            6,
            "the time, the clock, the ships, the ship's own start, the state, the numbers"
        );
        assert_eq!(added_ships(&bound[2]), [spec().ship_id, other_spec().ship_id]);
        assert_eq!(
            names(&bound[5]),
            [
                "SetBallAgility",
                "SetBallMass",
                "SetMaxSpeed", // the other ship's
                "SetBallAgility",
                "SetMaxSpeed", // its own: the mass came with the start
            ]
        );
        // the state holds the balls of both ships
        let buffer = state_buffer(&bound[4]);
        assert!(holds_ball_of(&buffer, spec().ship_id) && holds_ball_of(&buffer, other_spec().ship_id));
        // the state lists the ships in the same order
        let Out::Notify { args, .. } = &bound[4] else { panic!() };
        let text = format!("{args:?}");
        let (first, second) =
            (text.find(&spec().ship_id.to_string()), text.rfind(&other_spec().ship_id.to_string()));
        assert!(first.is_some() && second.is_some() && first < second, "{text:.200}");
    }

    #[test]
    fn an_order_is_told_to_the_pilot_and_to_the_watcher_at_one_stamp() {
        let mut scene = both();
        let out = scene.command(B, Command::SetSpeedFraction(0.5), NOON + 2 * SECOND);
        assert_eq!(
            out.iter().map(recipient).collect::<Vec<_>>(),
            [B, A, B],
            "the pilot, the watcher, and the word to the host"
        );
        assert_eq!(names(&out[0]), ["SetSpeedFraction"]);
        assert_eq!(names(&out[1]), ["SetSpeedFraction"]);
        assert_eq!(stamps(&out[0]), stamps(&out[1]));
        assert!(matches!(out[2], Out::Accepted { session: B, command: 1, .. }));
    }

    #[test]
    fn the_ships_are_listed_in_the_order_they_came_into_the_scene_not_in_the_order_of_their_sessions() {
        let (first, second): (SessionId, SessionId) = (9, 3);
        let mut scene = scene_with_a_planet();
        scene.undock(first, spec(), STATION, NOON).unwrap();
        scene.bind(first, NOON);
        scene.undock(second, other_spec(), STATION, NOON + SECOND).unwrap();
        let bound = scene.bind(second, NOON + SECOND);
        assert_eq!(
            added_ships(&bound[2]),
            [spec().ship_id, other_spec().ship_id],
            "the one that came first is first"
        );
    }

    #[test]
    fn a_ship_that_left_is_not_in_the_state_of_the_others_when_it_comes_back_out_of_their_view() {
        let mut scene = both();
        scene.leave(B, Departure::LoggedOff, NOON + 2 * SECOND).unwrap();
        // A goes off to another box, and B undocks again
        scene.pilots.get_mut(&A).unwrap().ship.position = Vec3::new(2.0e9, 0.0, 0.0);
        scene.undock(B, other_spec(), STATION, NOON + 3 * SECOND).unwrap();
        let state = scene.update_state(A, NOON + 3 * SECOND);
        assert!(!holds_ball_of(&state_buffer(&state[0]), other_spec().ship_id), "B is out of A's view");
    }

    #[test]
    fn a_pilot_that_has_not_bound_the_ballpark_is_told_nothing_of_the_others_orders() {
        let mut scene = watcher();
        scene.undock(B, other_spec(), STATION, NOON + SECOND).unwrap();
        // B is in space but has not bound: A's order is not told to B
        let out = scene.command(A, Command::SetSpeedFraction(0.5), NOON + 2 * SECOND);
        assert_eq!(out.iter().map(recipient).collect::<Vec<_>>(), [A, A]);
    }

    #[test]
    fn a_ship_is_told_to_those_who_watch_as_a_turn_when_it_asks_for_a_warp_and_as_the_warp_when_it_enters_it()
    {
        let mut scene = both();
        let at = NOON + 2 * SECOND;
        let out = scene.command(B, Command::WarpTo { target_id: PLANET, range: 0.0 }, at);
        assert_eq!(names(&out[0]), ["SetMaxSpeed", "WarpTo", "OnSpecialFX", "SetSpeedFraction"]);
        assert_eq!((recipient(&out[1]), names(&out[1])), (A, vec!["GotoDirection".to_string()]));

        let mut entered = None;
        let mut t = at;
        while t < at + 30 * SECOND && entered.is_none() {
            t += SECOND / 4;
            entered = scene
                .poll(t)
                .into_iter()
                .find(|o| names(o).first().map(String::as_str) == Some("OnSpecialFX"));
        }
        let entered = entered.expect("the ship enters warp");
        assert_eq!(recipient(&entered), A, "the pilot was told at the command");
        assert_eq!(names(&entered), ["OnSpecialFX", "SetSpeedFraction", "WarpTo"]);
    }

    #[test]
    fn a_ship_that_warps_off_is_taken_from_view_of_the_others_at_fifty_thousand_kilometres_and_they_from_its()
    {
        let mut scene = both();
        let at = NOON + 2 * SECOND;
        scene.command(B, Command::WarpTo { target_id: PLANET, range: 0.0 }, at);
        let mut removals: Vec<(SessionId, Vec<String>)> = Vec::new();
        let mut t = at;
        while t < at + 60 * SECOND && removals.len() < 2 {
            t += SECOND / 4;
            for out in scene.poll(t) {
                if names(&out).first().map(String::as_str) == Some("PackagedAction") {
                    removals.push((recipient(&out), names(&out)));
                }
            }
        }
        removals.sort();
        assert_eq!(
            removals.iter().map(|(s, _)| *s).collect::<Vec<_>>(),
            [A, B],
            "each was told the other is gone"
        );
        assert!(t < at + 30 * SECOND, "and it is a matter of seconds, as the recording's 7");
        // the ships no longer see each other, and the state says so
        let state = scene.update_state(A, t);
        let text = format!("{:?}", state[0]);
        assert!(
            !text.contains(&other_spec().ship_id.to_string()),
            "the ship that warped off is not in the state"
        );
    }

    #[test]
    fn a_ship_that_warps_to_a_place_in_another_box_within_the_envelope_is_taken_from_view_when_it_comes_to_rest()
     {
        const BELT: i64 = 40_000_009;
        let mut world = (*system()).clone();
        world.bodies.push(Body {
            id: BELT,
            type_id: 15,
            group_id: 9,
            category_id: 2,
            owner_id: 0,
            name: "Testing Belt".to_string(),
            kind: BodyKind::Belt,
            position: Vec3::new(20_000_000.0, 0.0, 0.0), // 20 000 km: two boxes off, and inside the 50 000 km of the warp envelope
            radius: 1_000.0,
            graphic_id: None,
            detail: Detail::None,
        });
        let mut scene = Scene::new(Arc::new(world));
        scene.undock(A, spec(), STATION, NOON).unwrap();
        scene.bind(A, NOON);
        scene.undock(B, other_spec(), STATION, NOON + SECOND).unwrap();
        scene.bind(B, NOON + SECOND);
        let at = NOON + 2 * SECOND;
        scene.command(B, Command::WarpTo { target_id: BELT, range: 0.0 }, at);

        let mut removals: Vec<(i64, SessionId)> = Vec::new();
        let mut massless = None;
        let mut rested = None;
        let mut t = at;
        while t < at + 300 * SECOND && (rested.is_none() || removals.len() < 2) {
            t += SECOND / 4;
            for out in scene.poll(t) {
                match names(&out).first().map(String::as_str) {
                    Some("PackagedAction") => removals.push((t, recipient(&out))),
                    Some("SetBallMassive") if recipient(&out) == B => massless = massless.or(Some(t)),
                    Some("SetBallPosition") if recipient(&out) == B => rested = Some(t),
                    _ => {}
                }
            }
        }
        let (massless, rested) =
            (massless.expect("the ship drops out"), rested.expect("the ship comes to rest"));
        assert_eq!(removals.len(), 2, "each is told the other is gone: {removals:?}");
        // it stayed in view while it warped and went from view at the poll in which the warp ended (the drop-out comes two seconds after the
        // warning), though neither ship had crossed into another box since the poll before: the ship comes to rest half a minute later
        assert!(
            removals.iter().all(|(when, _)| *when >= massless + 2 * SECOND && *when <= massless + 3 * SECOND),
            "removals {removals:?}, the warning at {massless}, the rest at {rested}"
        );
    }

    #[test]
    fn a_ship_that_docks_is_shown_to_stop_and_then_taken_from_view_when_it_has_docked() {
        // a station so big that a ship that has just come out of it is within docking range
        let mut world = (*system()).clone();
        world.bodies[0].radius = 70_000.0;
        let mut scene = Scene::new(Arc::new(world));
        scene.undock(A, spec(), STATION, NOON).unwrap();
        scene.bind(A, NOON);
        scene.undock(B, other_spec(), STATION, NOON + SECOND).unwrap();
        scene.bind(B, NOON + SECOND);
        let out = scene.command(B, Command::Dock { station_id: STATION }, NOON + 2 * SECOND);
        let told: Vec<(SessionId, Vec<String>)> = out
            .iter()
            .filter(|o| matches!(o, Out::Notify { .. }))
            .map(|o| (recipient(o), names(o)))
            .collect();
        assert_eq!(
            told,
            [
                (B, vec![]), // OnDockingAccepted is a named event, not an update
                (B, vec!["SetBallMassive".to_string(), "Stop".to_string()]),
                (A, vec!["SetBallMassive".to_string(), "Stop".to_string()]),
            ]
        );
        assert!(scene.poll(NOON + 5 * SECOND).is_empty(), "four seconds have not passed");
        let out = scene.poll(NOON + 6 * SECOND + 1);
        assert!(matches!(out[0], Out::Docked { session: B, .. }));
        assert_eq!((recipient(&out[1]), names(&out[1])), (A, vec!["RemoveBalls".to_string()]));
    }

    #[test]
    fn a_ship_that_leaves_is_taken_from_view() {
        let mut scene = both();
        let told = scene.leave(B, Departure::LoggedOff, NOON + 2 * SECOND).unwrap();
        assert_eq!(
            (told.len(), recipient(&told[0]), names(&told[0])),
            (1, A, vec!["RemoveBalls".to_string()])
        );
        // A is not told again when it asks for the state: the ship is not there
        let state = scene.update_state(A, NOON + 3 * SECOND);
        assert!(!format!("{:?}", state[0]).contains(&other_spec().ship_id.to_string()));
    }

    #[test]
    fn ships_in_different_boxes_of_the_grid_are_not_shown_to_each_other_until_they_share_one() {
        let mut scene = watcher();
        // A goes off to another box
        scene.pilots.get_mut(&A).unwrap().ship.position = Vec3::new(2.0e9, 0.0, 0.0);
        assert!(scene.undock(B, other_spec(), STATION, NOON + SECOND).unwrap().is_empty(), "A cannot see B");
        scene.bind(B, NOON + SECOND);
        assert!(scene.poll(NOON + 2 * SECOND).is_empty(), "nothing has changed");
        // A comes back to where B is
        let there = scene.pilots[&B].ship.position;
        scene.pilots.get_mut(&A).unwrap().ship.position = there;
        let told = scene.poll(NOON + 3 * SECOND);
        assert_eq!(told.len(), 2, "each is shown the other");
        assert_eq!(told.iter().map(recipient).collect::<Vec<_>>(), [A, B]);
        assert!(
            told.iter().all(|o| names(o) == ["AddBalls2", "SetBallAgility", "SetBallMass", "SetMaxSpeed"])
        );
    }

    #[test]
    fn the_same_calls_at_the_same_times_give_the_same_bytes() {
        let run = || {
            let mut scene = both();
            let mut out = scene.command(B, Command::SetSpeedFraction(0.5), NOON + 2 * SECOND);
            out.extend(scene.command(
                B,
                Command::WarpTo { target_id: PLANET, range: 0.0 },
                NOON + 3 * SECOND,
            ));
            let mut t = NOON + 3 * SECOND;
            while t < NOON + 60 * SECOND {
                t += SECOND / 4;
                out.extend(scene.poll(t));
            }
            out
        };
        assert_eq!(run(), run());
    }
}
