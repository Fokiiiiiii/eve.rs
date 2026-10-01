//! Targeting: what a ship has locked, what is locking, and who has it locked.
//!
//! A lock takes time (the ship's scan resolution against the size of what it locks), can be given up before it is done, ends when either ship
//! warps or goes, and is refused for the reasons `oracle/fixtures/scenarios/target.json` records. Only ships and stations can be locked; a
//! planet, a moon, a belt, a gate or the star cannot. The client is told with `OnTarget` (`add`, `lost`, `clear` to the one that locks;
//! `otheradd`, `otherlost` to the one that is locked) and with the damage state of what has been locked, to everyone who can see it.

use eve_destiny::Vec3;

use crate::health::DamageState;
use crate::payload::{self, STATION_SHIELD_TAU_MS};
use crate::scene::{Out, Scene, SessionId, TICKS_PER_SECOND};
use crate::wire::{id, text, word};
use crate::world::BodyKind;

/// The longest a lock takes, and what it takes when the ship has no scan resolution to go by.
const MAX_LOCK_MS: f64 = 180_000.0;
const FALLBACK_LOCK_MS: f64 = 2_000.0;
/// A ship that has undocked cannot be locked (or lock) for this long, unless it does something.
pub(crate) const UNDOCK_PROTECTION: i64 = 30 * TICKS_PER_SECOND;

/// Groups whose members cannot be locked: the star, planets, moons, belts, gates, anomalies and secondary suns.
const UNTARGETABLE_GROUPS: [i32; 7] = [6, 7, 8, 9, 10, 885, 995];

/// What a player can ask about targets (the calls of the client's dogma location).
#[derive(Debug, Clone, PartialEq)]
pub enum Order {
    /// Start locking a target.
    Lock(i64),
    /// Give up a lock that is going.
    Cancel(i64),
    /// Let go of locked targets.
    Remove(Vec<i64>),
    /// Let go of everything.
    Clear,
    /// What the ship has locked.
    Targets,
    /// Who has locked the ship.
    Targeters,
}

impl Order {
    /// The code of the command on the link (ADR 0007).
    pub fn code(&self) -> u8 {
        match self {
            Self::Lock(_) => 10,
            Self::Cancel(_) => 11,
            Self::Remove(_) => 12,
            Self::Clear => 13,
            Self::Targets => 14,
            Self::Targeters => 15,
        }
    }
}

#[derive(Debug, Clone, PartialEq)]
struct Pending {
    target: i64,
    sequence: u64,
    complete_at: i64,
}

#[derive(Debug, Clone, PartialEq)]
struct Locked {
    target: i64,
    sequence: u64,
}

/// The locks of one ship.
#[derive(Debug, Clone, Default)]
pub(crate) struct Targeting {
    pending: Vec<Pending>,
    locked: Vec<Locked>,
}

impl Targeting {
    fn is_empty(&self) -> bool {
        self.pending.is_empty() && self.locked.is_empty()
    }

    /// The targets being locked, in the order they were asked for.
    pub(crate) fn pending_targets(&self) -> Vec<i64> {
        self.pending.iter().map(|p| p.target).collect()
    }

    fn targets(&self) -> Vec<i64> {
        let mut locked: Vec<&Locked> = self.locked.iter().collect();
        locked.sort_by_key(|l| (l.sequence, l.target));
        locked.into_iter().map(|l| l.target).collect()
    }
}

/// Something a ship can point at: the figures a lock depends on.
#[derive(Debug, Clone, Copy)]
pub(crate) struct Subject {
    id: i64,
    session: Option<SessionId>,
    position: Vec3,
    radius: f64,
    type_id: i32,
    group_id: i32,
    signature_radius: f64,
    state: DamageState,
    warping: bool,
    protected: bool,
}

/// Why a lock cannot be had, with what the client is told about the target (its type, and for a target too far off the range the ship locks at
/// and how far the target is).
#[derive(Debug, Clone, Copy, PartialEq)]
pub(crate) struct Refusal {
    pub reason: &'static str,
    pub target_type: i32,
    pub desired_range: f64,
    pub actual_distance: f64,
}

impl Refusal {
    fn of(reason: &'static str) -> Self {
        Self { reason, target_type: 0, desired_range: 0.0, actual_distance: 0.0 }
    }

    fn of_type(reason: &'static str, target_type: i32) -> Self {
        Self { target_type, ..Self::of(reason) }
    }
}

/// How long a ship takes to lock a target: `40 000 000 / scan resolution / asinh(signature radius)²` milliseconds.
pub(crate) fn lock_duration_ms(scan_resolution: f64, signature_radius: f64) -> f64 {
    if scan_resolution <= 0.0 {
        return FALLBACK_LOCK_MS;
    }
    let signature = signature_radius.max(1.0);
    let log = (signature + (signature * signature + 1.0).sqrt()).ln();
    if !log.is_finite() || log <= 0.0 {
        return FALLBACK_LOCK_MS;
    }
    ((40_000_000.0 / scan_resolution) / (log * log)).clamp(1.0, MAX_LOCK_MS)
}

fn on_target(session: SessionId, what: &'static str, target: Option<i64>) -> Out {
    let mut args = vec![word(what)];
    args.extend(target.map(id));
    Out::Notify { session, name: "OnTarget", id_type: "shipid", args }
}

impl Scene {
    pub(crate) fn subject(&self, target: i64, now: i64) -> Option<Subject> {
        if let Some((session, pilot)) = self.pilots.iter().find(|(_, p)| p.ship.spec.ship_id == target) {
            let spec = &pilot.ship.spec;
            return Some(Subject {
                id: target,
                session: Some(*session),
                position: pilot.ship.position,
                radius: spec.radius,
                type_id: spec.type_id,
                group_id: spec.group_id,
                signature_radius: spec.signature_radius,
                state: pilot.ship.health.state_at(now),
                warping: pilot.flight.as_ref().is_some_and(|f| f.is_warping()),
                protected: pilot.protected_until > now,
            });
        }
        let body = self.system().body(target)?;
        Some(Subject {
            id: target,
            session: None,
            position: body.position,
            radius: body.radius,
            type_id: body.type_id,
            group_id: body.group_id,
            signature_radius: body.radius,
            state: DamageState::whole(if body.kind == BodyKind::Station {
                STATION_SHIELD_TAU_MS
            } else {
                0.0
            }),
            warping: false,
            protected: false,
        })
    }

    /// Whether `session`'s ship may lock `target` (what a lock already going or held has to keep meeting; `count` also checks room for another).
    fn check_lock(&self, session: SessionId, target: &Subject, count: bool) -> Result<(), Refusal> {
        let source = self.pilots.get(&session).ok_or(Refusal::of("NOT_IN_SPACE"))?;
        if target.protected {
            return Err(Refusal::of("TARGET_INVULNERABLE"));
        }
        if target.id == source.ship.spec.ship_id {
            return Err(Refusal::of("TARGET_SELF"));
        }
        if UNTARGETABLE_GROUPS.contains(&target.group_id) {
            return Err(Refusal::of_type("TARGET_NOT_TARGETABLE", target.type_id));
        }
        if source.flight.as_ref().is_some_and(|f| f.is_warping()) {
            return Err(Refusal::of("SOURCE_WARPING"));
        }
        if target.warping {
            return Err(Refusal::of_type("TARGET_WARPING", target.type_id));
        }
        let spec = &source.ship.spec;
        let apart = (distance(source.ship.position, target.position) - spec.radius - target.radius).max(0.0);
        if spec.max_target_range <= 0.0 || apart >= spec.max_target_range {
            return Err(Refusal {
                reason: "TARGET_OUT_OF_RANGE",
                target_type: target.type_id,
                desired_range: spec.max_target_range.max(0.0),
                actual_distance: apart,
            });
        }
        let limit = spec.max_locked_targets.max(0);
        // what the client is told is how many targets the ship may hold, which travels in the place of the range
        let full = Refusal { desired_range: f64::from(limit), ..Refusal::of("TARGET_LOCK_LIMIT_REACHED") };
        if limit == 0 {
            return Err(full);
        }
        if count && source.targeting.locked.len() + source.targeting.pending.len() >= limit as usize {
            return Err(full);
        }
        Ok(())
    }

    fn next_sequence(&mut self) -> u64 {
        self.target_sequence += 1;
        self.target_sequence
    }

    fn refused(session: SessionId, order: &Order, refusal: Refusal) -> Vec<Out> {
        vec![Out::TargetRefused {
            session,
            command: order.code(),
            reason: refusal.reason,
            target_type: refusal.target_type,
            desired_range: refusal.desired_range,
            actual_distance: refusal.actual_distance,
        }]
    }

    fn answer(session: SessionId, order: &Order, pending: bool, targets: Vec<i64>) -> Vec<Out> {
        vec![Out::Targets { session, command: order.code(), pending, targets }]
    }

    /// What a ship has locked, in the order it locked them.
    pub(crate) fn locked_by(&self, session: SessionId) -> Vec<i64> {
        self.pilots.get(&session).map(|p| p.targeting.targets()).unwrap_or_default()
    }

    /// The ships (and nothing else can, yet) that have `target` locked, by id.
    fn targeters_of(&self, target: i64) -> Vec<i64> {
        let mut by: Vec<i64> = self
            .pilots
            .values()
            .filter(|p| p.targeting.locked.iter().any(|l| l.target == target))
            .map(|p| p.ship.spec.ship_id)
            .collect();
        by.sort_unstable();
        by
    }

    /// A player's question or order about targets.
    pub fn target_order(&mut self, session: SessionId, order: Order, now: i64) -> Vec<Out> {
        if !self.pilots.contains_key(&session) {
            return vec![Out::Refused { session, command: order.code(), reason: "NO_SHIP_IN_SPACE" }];
        }
        self.settle_all(now);
        let told = match &order {
            Order::Lock(target) => self.lock(session, *target, &order, now),
            Order::Cancel(target) => {
                if let Some(pilot) = self.pilots.get_mut(&session) {
                    pilot.targeting.pending.retain(|p| p.target != *target);
                }
                Self::answer(session, &order, false, self.locked_by(session))
            }
            Order::Remove(targets) => {
                let mut out = Vec::new();
                for target in targets {
                    out.extend(self.remove_locked(session, *target, None, true, now));
                }
                out.extend(Self::answer(session, &order, false, self.locked_by(session)));
                out
            }
            Order::Clear => {
                let mut out = self.clear_locks(session, true, now);
                out.extend(Self::answer(session, &order, false, Vec::new()));
                out
            }
            Order::Targets => Self::answer(session, &order, false, self.locked_by(session)),
            Order::Targeters => {
                Self::answer(session, &order, false, self.targeters_of(self.ship_id_of(session)))
            }
        };
        self.with_backlog(told)
    }

    fn ship_id_of(&self, session: SessionId) -> i64 {
        self.pilots.get(&session).map(|p| p.ship.spec.ship_id).unwrap_or(0)
    }

    fn lock(&mut self, session: SessionId, target: i64, order: &Order, now: i64) -> Vec<Out> {
        let pilot = &self.pilots[&session];
        if pilot.targeting.locked.iter().any(|l| l.target == target) {
            return Self::answer(session, order, false, self.locked_by(session));
        }
        if pilot.targeting.pending.iter().any(|p| p.target == target) {
            return Self::answer(session, order, true, self.locked_by(session));
        }
        let Some(subject) = self.subject(target, now) else {
            return Self::refused(session, order, Refusal::of("TARGET_NOT_FOUND"));
        };
        if let Err(refusal) = self.check_lock(session, &subject, true) {
            return Self::refused(session, order, refusal);
        }
        let spec = &self.pilots[&session].ship.spec;
        let duration = lock_duration_ms(spec.scan_resolution, subject.signature_radius);
        // a validated lock request gives up the undocking protection (the host tells the client)
        let sequence = self.next_sequence();
        let pilot = self.pilots.get_mut(&session).expect("the pilot is in the scene");
        pilot.protected_until = 0;
        if duration <= 1.0 {
            let mut out = Self::answer(session, order, false, Vec::new());
            out.extend(self.finish_lock(session, subject, sequence, now));
            return out;
        }
        let complete_at = now + (duration * TICKS_PER_SECOND as f64 / 1000.0) as i64;
        pilot.targeting.pending.push(Pending { target, sequence, complete_at });
        Self::answer(session, order, true, self.locked_by(session))
    }

    /// The lock is had: the ship is told, the one locked is told, and the damage state of what is locked is sent to everyone who can see it.
    fn finish_lock(&mut self, session: SessionId, subject: Subject, sequence: u64, now: i64) -> Vec<Out> {
        let Some(pilot) = self.pilots.get_mut(&session) else { return Vec::new() };
        pilot.targeting.pending.retain(|p| p.target != subject.id);
        pilot.targeting.locked.push(Locked { target: subject.id, sequence });
        let source_id = pilot.ship.spec.ship_id;
        let mut out = vec![on_target(session, "add", Some(subject.id))];
        if let Some(target_session) = subject.session
            && target_session != session
        {
            out.push(on_target(target_session, "otheradd", Some(source_id)));
        }
        out.extend(self.announce_damage_state(&subject, now));
        out
    }

    /// The damage state of a ship or station told to the pilots that can see it, its own, and the ones that have it locked.
    pub(crate) fn announce_damage_state(&mut self, subject: &Subject, now: i64) -> Vec<Out> {
        let recipients: Vec<SessionId> = self
            .in_entry_order()
            .into_iter()
            .filter(|s| {
                let pilot = &self.pilots[s];
                pilot.bound
                    && (subject.session == Some(*s)
                        || subject.session.is_none()
                        || subject.session.is_some_and(|t| pilot.sees.contains(&t))
                        || pilot.targeting.locked.iter().any(|l| l.target == subject.id))
            })
            .collect();
        if recipients.is_empty() {
            return Vec::new();
        }
        let stamp = self.next_stamp(now);
        let action = payload::action(
            stamp,
            "OnDamageStateChange",
            [id(subject.id), payload::damage_state(&subject.state, now)],
        );
        recipients.into_iter().map(|s| Self::update(s, vec![action.clone()])).collect()
    }

    /// A ship lets go of a locked target: it is told (`lost`), and so is the target when it is a ship (`otherlost`).
    fn remove_locked(
        &mut self,
        session: SessionId,
        target: i64,
        reason: Option<&'static str>,
        notify_target: bool,
        now: i64,
    ) -> Vec<Out> {
        let Some(pilot) = self.pilots.get_mut(&session) else { return Vec::new() };
        let before = pilot.targeting.locked.len();
        pilot.targeting.locked.retain(|l| l.target != target);
        if pilot.targeting.locked.len() == before {
            return Vec::new();
        }
        let source_id = pilot.ship.spec.ship_id;
        // what shoots at the target stops before the pilot is told the lock is lost
        let mut out = self.stop_weapons_at(session, &[target], now);
        out.push(on_target_reason(session, "lost", Some(target), reason));
        if notify_target
            && let Some(other) = self.session_of_ship(target)
            && other != session
        {
            out.push(on_target_reason(other, "otherlost", Some(source_id), reason));
        }
        out
    }

    fn session_of_ship(&self, ship_id: i64) -> Option<SessionId> {
        self.pilots.iter().find(|(_, p)| p.ship.spec.ship_id == ship_id).map(|(s, _)| *s)
    }

    /// A ship lets go of everything: what is going is given up, what is held is let go and the ship is told once (`clear`); the ships that were
    /// locked are told they are not.
    fn clear_locks(&mut self, session: SessionId, notify_target: bool, now: i64) -> Vec<Out> {
        let Some(pilot) = self.pilots.get_mut(&session) else { return Vec::new() };
        pilot.targeting.pending.clear();
        let held = pilot.targeting.targets();
        pilot.targeting.locked.clear();
        let source_id = pilot.ship.spec.ship_id;
        let mut out = self.stop_weapons_at(session, &held, now);
        if notify_target {
            for target in &held {
                if let Some(other) = self.session_of_ship(*target)
                    && other != session
                {
                    out.push(on_target(other, "otherlost", Some(source_id)));
                }
            }
        }
        if !held.is_empty() {
            out.push(on_target(session, "clear", None));
        }
        out
    }

    /// A ship has gone (docked, jumped, logged off; it is out of the scene already): what it had locked is let go, and whoever had it locked is told.
    pub(crate) fn drop_targeting(&mut self, ship_id: i64, held: &Targeting, now: i64) -> Vec<Out> {
        let mut out = Vec::new();
        for locked in &held.locked {
            if let Some(other) = self.session_of_ship(locked.target) {
                out.push(on_target(other, "otherlost", Some(ship_id)));
            }
        }
        let sources: Vec<SessionId> = self
            .pilots
            .iter()
            .filter(|(_, p)| {
                p.targeting.locked.iter().any(|l| l.target == ship_id)
                    || p.targeting.pending.iter().any(|q| q.target == ship_id)
            })
            .map(|(s, _)| *s)
            .collect();
        for source in sources {
            if let Some(pilot) = self.pilots.get_mut(&source) {
                pilot.targeting.pending.retain(|p| p.target != ship_id);
            }
            out.extend(self.remove_locked(source, ship_id, None, false, now));
        }
        out
    }

    /// What is due at `now` among the locks: a lock that has come to its end is had; one that can no longer be had (either ship warps, the range
    /// is more than the ship can lock at, the ship it points at is gone) is dropped.
    pub(crate) fn settle_targets(&mut self, now: i64) -> Vec<Out> {
        let sessions: Vec<SessionId> =
            self.in_entry_order().into_iter().filter(|s| !self.pilots[s].targeting.is_empty()).collect();
        let mut out = Vec::new();
        for session in sessions {
            let Some(pilot) = self.pilots.get(&session) else { continue };
            if pilot.flight.as_ref().is_some_and(|f| f.is_warping()) {
                out.extend(self.clear_locks(session, true, now));
                continue;
            }
            let mut due: Vec<Pending> = pilot.targeting.pending.clone();
            due.sort_by_key(|p| (p.complete_at, p.sequence));
            for pending in due {
                let Some(subject) = self.subject(pending.target, now) else {
                    self.cancel_pending(session, pending.target);
                    continue;
                };
                if self.check_lock(session, &subject, false).is_err() {
                    self.cancel_pending(session, pending.target);
                    continue;
                }
                if pending.complete_at <= now {
                    out.extend(self.finish_lock(session, subject, pending.sequence, now));
                }
            }
            let held = self.pilots.get(&session).map(|p| p.targeting.targets()).unwrap_or_default();
            for target in held {
                let Some(subject) = self.subject(target, now) else {
                    out.extend(self.remove_locked(
                        session,
                        target,
                        Some("TargetingAttemptCancelled"),
                        true,
                        now,
                    ));
                    continue;
                };
                if let Err(refusal) = self.check_lock(session, &subject, false) {
                    let reason =
                        (refusal.reason == "TARGET_NOT_FOUND").then_some("TargetingAttemptCancelled");
                    out.extend(self.remove_locked(session, target, reason, true, now));
                }
            }
        }
        out
    }

    /// A lock that was going is given up because it cannot be had (the client is not told: its own request lane fails with the service
    /// notification the host does not carry yet).
    fn cancel_pending(&mut self, session: SessionId, target: i64) {
        if let Some(pilot) = self.pilots.get_mut(&session) {
            pilot.targeting.pending.retain(|p| p.target != target);
        }
    }
}

fn on_target_reason(
    session: SessionId,
    what: &'static str,
    target: Option<i64>,
    reason: Option<&'static str>,
) -> Out {
    let mut out = on_target(session, what, target);
    if let (Out::Notify { args, .. }, Some(reason)) = (&mut out, reason) {
        args.push(text(reason));
    }
    out
}

fn distance(a: Vec3, b: Vec3) -> f64 {
    ((a.x - b.x).powi(2) + (a.y - b.y).powi(2) + (a.z - b.z).powi(2)).sqrt()
}

#[cfg(test)]
mod tests {
    use std::sync::Arc;

    use eve_marshal::Value;

    use super::*;
    use crate::scene::tests::{NOON, STATION, spec, system};
    use crate::scene::{Command, Departure};
    use crate::ship::ShipSpec;
    use crate::world::{Body, BodyKind, Detail};

    const A: SessionId = 7;
    const B: SessionId = 8;
    const C: SessionId = 9;
    const SECOND: i64 = 10_000_000;
    const PLANET: i64 = 40_000_001;

    fn spec_of(offset: i64) -> ShipSpec {
        let mut other = spec();
        other.ship_id += offset;
        other.character_id += offset;
        other.owner_id += offset;
        other.name = format!("Ship {offset}");
        other
    }

    fn ship_id(session: SessionId) -> i64 {
        spec_of(session as i64 - A as i64).ship_id
    }

    /// The system of the tests with a planet, and a station of a size that gives a lock time of its own.
    fn world() -> Arc<crate::world::SolarSystem> {
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
        Arc::new(world)
    }

    /// Ships A and B (and C when asked), bound, thirty seconds after they undocked one second apart: no one is protected.
    fn crowd(ships: usize) -> (Scene, i64) {
        let mut scene = Scene::new(world());
        for n in 0..ships {
            let session = A + n as u64;
            scene.undock(session, spec_of(n as i64), STATION, NOON + n as i64 * SECOND).unwrap();
            scene.bind(session, NOON + n as i64 * SECOND);
        }
        let mut t = NOON;
        while t < NOON + 40 * SECOND {
            t += SECOND / 4;
            scene.poll(t);
        }
        (scene, t)
    }

    fn poll_until(scene: &mut Scene, from: i64, seconds: f64) -> (Vec<Out>, i64) {
        let mut out = Vec::new();
        let mut t = from;
        while t < from + (seconds * SECOND as f64) as i64 {
            t += SECOND / 4;
            out.extend(scene.poll(t));
        }
        (out, t)
    }

    /// What each session was told by a notification of the given name: `(session, arguments)`.
    fn told(outs: &[Out], name: &str) -> Vec<(SessionId, Vec<Value>)> {
        outs.iter()
            .filter_map(|o| match o {
                Out::Notify { session, name: n, args, .. } if *n == name => Some((*session, args.clone())),
                _ => None,
            })
            .collect()
    }

    fn on_target(outs: &[Out]) -> Vec<(SessionId, Vec<String>)> {
        told(outs, "OnTarget")
            .into_iter()
            .map(|(s, args)| {
                (
                    s,
                    args.iter()
                        .map(|a| match a {
                            Value::Str(text) => text.to_string(),
                            Value::Int(n) | Value::Long(n) => n.to_string(),
                            other => panic!("{other:?}"),
                        })
                        .collect(),
                )
            })
            .collect()
    }

    fn words(items: &[&str]) -> Vec<String> {
        items.iter().map(|s| s.to_string()).collect()
    }

    fn answer(outs: &[Out]) -> (bool, Vec<i64>) {
        match outs.iter().find(|o| matches!(o, Out::Targets { .. })) {
            Some(Out::Targets { pending, targets, .. }) => (*pending, targets.clone()),
            other => panic!("no answer in {outs:?}: {other:?}"),
        }
    }

    fn refusal(outs: &[Out]) -> Refusal {
        match outs.iter().find(|o| matches!(o, Out::TargetRefused { .. })) {
            Some(Out::TargetRefused { reason, target_type, desired_range, actual_distance, .. }) => Refusal {
                reason,
                target_type: *target_type,
                desired_range: *desired_range,
                actual_distance: *actual_distance,
            },
            other => panic!("not refused in {outs:?}: {other:?}"),
        }
    }

    /// A lock asked for at `t` and let run until it is had.
    fn locked(scene: &mut Scene, from: i64, source: SessionId, target: i64) -> (Vec<Out>, i64) {
        let asked = scene.target_order(source, Order::Lock(target), from);
        assert!(answer(&asked).0, "{asked:?}");
        poll_until(scene, from, 6.0)
    }

    #[test]
    fn a_lock_takes_forty_million_over_the_scan_resolution_over_the_square_of_the_arc_sine_of_the_size() {
        // the Ibis: 475 mm and 52 m
        assert!((lock_duration_ms(475.0, 52.0) - 3_903.7).abs() < 1.0, "{}", lock_duration_ms(475.0, 52.0));
        // a bigger target is locked faster, a better scanner locks faster
        assert!(lock_duration_ms(475.0, 500.0) < lock_duration_ms(475.0, 52.0));
        assert!(lock_duration_ms(950.0, 52.0) < lock_duration_ms(475.0, 52.0));
        // a size below one metre counts as one
        assert_eq!(lock_duration_ms(475.0, 0.2), lock_duration_ms(475.0, 1.0));
        // no scan resolution is two seconds; nothing is longer than three minutes or shorter than a millisecond
        assert_eq!(lock_duration_ms(0.0, 52.0), 2_000.0);
        assert_eq!(lock_duration_ms(0.001, 52.0), 180_000.0);
        assert_eq!(lock_duration_ms(1.0e12, 52.0), 1.0);
    }

    #[test]
    fn a_lock_is_answered_at_once_as_going_and_had_when_its_time_is_up() {
        let (mut scene, t) = crowd(2);
        let asked = scene.target_order(B, Order::Lock(ship_id(A)), t);
        assert_eq!(answer(&asked), (true, vec![]));
        assert_eq!(asked.len(), 1, "nothing else is said: {asked:?}");
        // 28 m of signature and 660 mm: 3.74 s
        let (early, t2) = poll_until(&mut scene, t, 3.4);
        assert!(on_target(&early).is_empty(), "{early:?}");
        let (out, _) = poll_until(&mut scene, t2, 1.0);
        assert_eq!(
            on_target(&out),
            vec![
                (B, words(&["add", &ship_id(A).to_string()])),
                (A, words(&["otheradd", &ship_id(B).to_string()])),
            ]
        );
        assert_eq!(
            answer(&scene.target_order(B, Order::Targets, t + 10 * SECOND)),
            (false, vec![ship_id(A)])
        );
        assert_eq!(
            answer(&scene.target_order(A, Order::Targeters, t + 10 * SECOND)),
            (false, vec![ship_id(B)])
        );
        assert_eq!(answer(&scene.target_order(A, Order::Targets, t + 10 * SECOND)), (false, vec![]));
        assert_eq!(answer(&scene.target_order(B, Order::Targeters, t + 10 * SECOND)), (false, vec![]));
    }

    #[test]
    fn the_damage_state_of_what_is_locked_is_told_to_everyone_who_can_see_it_and_the_one_that_locks() {
        let (mut scene, t) = crowd(3);
        let (out, _) = locked(&mut scene, t, B, ship_id(A));
        let recipients: Vec<SessionId> = told(&out, "DoDestinyUpdate").into_iter().map(|(s, _)| s).collect();
        assert_eq!(
            recipients,
            [A, B, C],
            "the ship that is locked, the one that locks and the one that looks on, in the order they came"
        );
        let (_, args) = &told(&out, "DoDestinyUpdate")[0];
        let Value::List(actions) = &args[0] else { panic!("{args:?}") };
        let Value::Tuple(action) = &actions[0] else { panic!() };
        let Value::Tuple(inner) = &action[1] else { panic!() };
        assert_eq!(inner[0], Value::Str("OnDamageStateChange".into()));
        let Value::Tuple(arguments) = &inner[1] else { panic!() };
        assert_eq!(arguments[0], crate::wire::id(ship_id(A)));
        let Value::Tuple(state) = &arguments[1] else { panic!() };
        let Value::Tuple(shield) = &state[0] else { panic!() };
        assert_eq!(shield[0], Value::Real(1.0));
        assert_eq!(shield[1], Value::Real(172_000.0), "a fifth of the shield's recharge time");
        assert_eq!((&state[1], &state[2]), (&Value::Real(1.0), &Value::Real(1.0)));
    }

    #[test]
    fn a_lock_asked_for_again_says_whether_it_is_had_or_going_and_starts_nothing() {
        let (mut scene, t) = crowd(2);
        scene.target_order(B, Order::Lock(ship_id(A)), t);
        let going = scene.target_order(B, Order::Lock(ship_id(A)), t + SECOND);
        assert_eq!(answer(&going), (true, vec![]));
        let (out, t2) = poll_until(&mut scene, t, 5.0);
        assert_eq!(on_target(&out).len(), 2, "one lock, however often it was asked for: {out:?}");
        let had = scene.target_order(B, Order::Lock(ship_id(A)), t2);
        assert_eq!(answer(&had), (false, vec![ship_id(A)]));
        assert_eq!(had.len(), 1);
    }

    #[test]
    fn a_lock_given_up_says_nothing_and_is_never_had() {
        let (mut scene, t) = crowd(2);
        scene.target_order(B, Order::Lock(ship_id(A)), t);
        let out = scene.target_order(B, Order::Cancel(ship_id(A)), t + SECOND);
        assert_eq!(out.len(), 1, "{out:?}");
        assert_eq!(answer(&out), (false, vec![]));
        let (later, _) = poll_until(&mut scene, t, 8.0);
        assert!(on_target(&later).is_empty(), "{later:?}");
        assert_eq!(answer(&scene.target_order(B, Order::Targets, t + 9 * SECOND)), (false, vec![]));
    }

    #[test]
    fn a_target_let_go_is_lost_to_the_ship_and_other_lost_to_the_one_that_was_locked() {
        let (mut scene, t) = crowd(2);
        let (_, t) = locked(&mut scene, t, B, ship_id(A));
        let out = scene.target_order(B, Order::Remove(vec![ship_id(A)]), t);
        assert_eq!(
            on_target(&out),
            vec![
                (B, words(&["lost", &ship_id(A).to_string()])),
                (A, words(&["otherlost", &ship_id(B).to_string()])),
            ]
        );
        assert_eq!(answer(&out), (false, vec![]));
        // what is not locked is nothing to let go of
        let again = scene.target_order(B, Order::Remove(vec![ship_id(A), 12345]), t);
        assert!(on_target(&again).is_empty());
    }

    #[test]
    fn letting_go_of_a_lock_that_is_going_does_nothing_to_it() {
        let (mut scene, t) = crowd(2);
        scene.target_order(B, Order::Lock(ship_id(A)), t);
        let out = scene.target_order(B, Order::Remove(vec![ship_id(A)]), t + SECOND);
        assert!(on_target(&out).is_empty());
        let (later, _) = poll_until(&mut scene, t, 5.0);
        assert_eq!(on_target(&later).len(), 2, "the lock still comes");
    }

    #[test]
    fn clearing_says_clear_once_to_the_ship_and_other_lost_to_each_ship_that_was_locked_and_gives_up_what_is_going()
     {
        let (mut scene, t) = crowd(3);
        let (_, t) = locked(&mut scene, t, C, ship_id(A));
        let (_, t) = locked(&mut scene, t, C, ship_id(B));
        scene.target_order(C, Order::Lock(STATION), t);
        let out = scene.target_order(C, Order::Clear, t + SECOND / 10);
        assert_eq!(
            on_target(&out),
            vec![
                (A, words(&["otherlost", &ship_id(C).to_string()])),
                (B, words(&["otherlost", &ship_id(C).to_string()])),
                (C, words(&["clear"])),
            ]
        );
        let (later, _) = poll_until(&mut scene, t, 3.0);
        assert!(
            on_target(&later).is_empty(),
            "the lock on the station that was going is given up: {later:?}"
        );
        // nothing locked, nothing to clear
        assert!(on_target(&scene.target_order(C, Order::Clear, t + 5 * SECOND)).is_empty());
    }

    #[test]
    fn the_targets_of_a_ship_are_listed_in_the_order_they_were_asked_for_not_the_order_they_were_had() {
        let (mut scene, t) = crowd(3);
        // the station is quick to lock (it is big), the ship is slow: asked for first, had last
        scene.target_order(C, Order::Lock(ship_id(A)), t);
        scene.target_order(C, Order::Lock(STATION), t);
        poll_until(&mut scene, t, 6.0);
        assert_eq!(
            answer(&scene.target_order(C, Order::Targets, t + 7 * SECOND)),
            (false, vec![ship_id(A), STATION])
        );
    }

    #[test]
    fn a_station_can_be_locked_and_is_told_to_no_one_but_the_ones_who_look_on() {
        let (mut scene, t) = crowd(2);
        let (out, _) = locked(&mut scene, t, B, STATION);
        assert_eq!(on_target(&out), vec![(B, words(&["add", &STATION.to_string()]))]);
        let updates = told(&out, "DoDestinyUpdate");
        assert_eq!(
            updates.iter().map(|(s, _)| *s).collect::<Vec<_>>(),
            [A, B],
            "every ship in the ballpark hears of the station"
        );
        let Value::List(actions) = &updates[0].1[0] else { panic!() };
        let Value::Tuple(action) = &actions[0] else { panic!() };
        let Value::Tuple(inner) = &action[1] else { panic!() };
        let Value::Tuple(arguments) = &inner[1] else { panic!() };
        let Value::Tuple(state) = &arguments[1] else { panic!() };
        let Value::Tuple(shield) = &state[0] else { panic!() };
        assert_eq!(
            shield[1],
            Value::Real(110_000.0),
            "a station answers with the same recharge whatever it is"
        );
    }

    #[test]
    fn some_things_cannot_be_locked_and_the_client_is_told_which() {
        let (mut scene, t) = crowd(2);
        let refused =
            |scene: &mut Scene, target: i64| refusal(&scene.target_order(B, Order::Lock(target), t));
        assert_eq!(refused(&mut scene, ship_id(B)).reason, "TARGET_SELF");
        assert_eq!(refused(&mut scene, 987654321).reason, "TARGET_NOT_FOUND");
        assert_eq!(
            refused(&mut scene, PLANET),
            Refusal {
                reason: "TARGET_NOT_TARGETABLE",
                target_type: 2015,
                desired_range: 0.0,
                actual_distance: 0.0
            }
        );
        for (group, kind) in
            [(6, BodyKind::Star), (8, BodyKind::Moon), (9, BodyKind::Belt), (10, BodyKind::Stargate)]
        {
            let mut world = (*world()).clone();
            world.bodies.push(Body {
                id: 41_000_000 + i64::from(group),
                type_id: 77,
                group_id: group,
                category_id: 2,
                owner_id: 0,
                name: "Scenery".to_string(),
                kind,
                position: Vec3::new(1_000.0, 0.0, 0.0),
                radius: 100.0,
                graphic_id: None,
                detail: Detail::None,
            });
            let mut scene = Scene::new(Arc::new(world));
            scene.undock(B, spec_of(1), STATION, NOON).unwrap();
            let out = scene.target_order(B, Order::Lock(41_000_000 + i64::from(group)), NOON + 40 * SECOND);
            assert_eq!(refusal(&out).reason, "TARGET_NOT_TARGETABLE", "group {group}");
        }
    }

    #[test]
    fn a_target_out_of_range_is_refused_with_the_range_and_the_distance_between_the_surfaces() {
        let (mut scene, t) = crowd(2);
        // the second ship goes on at top speed for a long while, the first stops
        scene.command(A, Command::Stop, t);
        let (_, t) = poll_until(&mut scene, t, 300.0);
        let out = scene.target_order(B, Order::Lock(ship_id(A)), t);
        let refusal = refusal(&out);
        assert_eq!(refusal.reason, "TARGET_OUT_OF_RANGE");
        assert_eq!(refusal.target_type, spec().type_id);
        assert_eq!(refusal.desired_range, 29_700.0);
        let apart = {
            let (a, b) = (scene.ship(A, t).unwrap().position, scene.ship(B, t).unwrap().position);
            distance(a, b) - 2.0 * spec().radius
        };
        assert!((refusal.actual_distance - apart).abs() < 1e-6, "{} m, {apart} m", refusal.actual_distance);
        assert!(apart > 29_700.0);
    }

    #[test]
    fn range_is_measured_between_surfaces_and_the_range_itself_is_out() {
        let (mut scene, t) = crowd(2);
        scene.command(A, Command::Stop, t);
        scene.command(B, Command::Stop, t);
        let (_, t) = poll_until(&mut scene, t, 60.0);
        // put the ships a set distance apart (surfaces) on a line
        for (session, x) in [(A, 0.0), (B, 29_700.0 + 2.0 * spec().radius)] {
            scene.pilots.get_mut(&session).unwrap().ship.position = Vec3::new(x, 0.0, 0.0);
        }
        assert_eq!(refusal(&scene.target_order(B, Order::Lock(ship_id(A)), t)).reason, "TARGET_OUT_OF_RANGE");
        scene.pilots.get_mut(&B).unwrap().ship.position = Vec3::new(29_699.0 + 2.0 * spec().radius, 0.0, 0.0);
        assert!(answer(&scene.target_order(B, Order::Lock(ship_id(A)), t)).0);
    }

    #[test]
    fn a_ship_that_has_just_undocked_cannot_be_locked_and_the_reason_comes_before_the_one_for_locking_itself()
    {
        let mut scene = Scene::new(world());
        scene.undock(A, spec_of(0), STATION, NOON).unwrap();
        scene.undock(B, spec_of(1), STATION, NOON).unwrap();
        let t = NOON + 5 * SECOND;
        assert_eq!(refusal(&scene.target_order(B, Order::Lock(ship_id(A)), t)).reason, "TARGET_INVULNERABLE");
        assert_eq!(refusal(&scene.target_order(B, Order::Lock(ship_id(B)), t)).reason, "TARGET_INVULNERABLE");
        // thirty seconds after it undocked it can
        assert_eq!(
            refusal(&scene.target_order(B, Order::Lock(ship_id(B)), NOON + 30 * SECOND)).reason,
            "TARGET_SELF"
        );
        assert!(answer(&scene.target_order(B, Order::Lock(ship_id(A)), NOON + 30 * SECOND)).0);
    }

    #[test]
    fn a_ship_that_is_flown_is_no_longer_protected_and_a_ship_that_locks_gives_up_its_protection() {
        let mut scene = Scene::new(world());
        for (session, n) in [(A, 0), (B, 1), (C, 2)] {
            scene.undock(session, spec_of(n), STATION, NOON).unwrap();
        }
        let t = NOON + 5 * SECOND;
        // flown: a ship that is told to go is no longer protected, one that is not is
        scene.command(A, Command::GotoDirection(Vec3::new(0.0, 1.0, 0.0)), t);
        assert!(answer(&scene.target_order(B, Order::Lock(ship_id(A)), t + SECOND)).0);
        let refused = scene.target_order(A, Order::Lock(ship_id(C)), t + SECOND);
        assert_eq!(refusal(&refused).reason, "TARGET_INVULNERABLE", "C is still protected");
        // a lock that is asked for (and valid) ends the locker's protection, one that is refused does not
        let mut scene = Scene::new(world());
        for (session, n) in [(A, 0), (B, 1)] {
            scene.undock(session, spec_of(n), STATION, NOON - 40 * SECOND).unwrap();
        }
        scene.undock(C, spec_of(2), STATION, NOON).unwrap();
        let t = NOON + 5 * SECOND;
        assert_eq!(refusal(&scene.target_order(A, Order::Lock(ship_id(C)), t)).reason, "TARGET_INVULNERABLE");
        assert!(answer(&scene.target_order(C, Order::Lock(ship_id(A)), t)).0, "C is protected and locks");
        assert!(
            answer(&scene.target_order(A, Order::Lock(ship_id(C)), t)).0,
            "and is not protected any more"
        );
    }

    #[test]
    fn a_ship_that_is_docking_is_still_protected() {
        let mut world = (*world()).clone();
        world.bodies[0].radius = 70_000.0;
        let mut scene = Scene::new(Arc::new(world));
        for (session, n) in [(A, 0), (B, 1)] {
            scene.undock(session, spec_of(n), STATION, NOON).unwrap();
        }
        let t = NOON + 2 * SECOND;
        let accepted = scene.command(A, Command::Dock { station_id: STATION }, t);
        assert!(accepted.iter().any(|o| matches!(o, Out::Accepted { command: 4, .. })), "{accepted:?}");
        assert_eq!(
            refusal(&scene.target_order(B, Order::Lock(ship_id(A)), t + SECOND)).reason,
            "TARGET_INVULNERABLE"
        );
    }

    #[test]
    fn the_damage_state_reaches_a_ship_that_locks_though_it_does_not_have_the_target_in_its_ballpark() {
        let (mut scene, t) = crowd(2);
        scene.pilots.get_mut(&B).unwrap().sees.remove(&A);
        let (out, _) = locked(&mut scene, t, B, ship_id(A));
        let recipients: Vec<SessionId> = told(&out, "DoDestinyUpdate").into_iter().map(|(s, _)| s).collect();
        assert!(recipients.contains(&B), "{recipients:?}");
    }

    #[test]
    fn a_ship_that_warps_lets_go_of_all_it_has_locked_when_the_warp_is_entered_and_cannot_lock_meanwhile() {
        let (mut scene, t) = crowd(2);
        let (_, t) = locked(&mut scene, t, B, ship_id(A));
        let (_, t) = locked(&mut scene, t, B, STATION);
        let out = scene.command(B, Command::WarpTo { target_id: PLANET, range: 0.0 }, t);
        assert!(on_target(&out).is_empty(), "the ship is only aligning: {out:?}");
        // the ship enters warp some seconds on
        let (later, t) = poll_until(&mut scene, t, 20.0);
        assert_eq!(
            on_target(&later),
            vec![(A, words(&["otherlost", &ship_id(B).to_string()])), (B, words(&["clear"])),]
        );
        assert_eq!(refusal(&scene.target_order(B, Order::Lock(ship_id(A)), t)).reason, "SOURCE_WARPING");
        assert_eq!(refusal(&scene.target_order(A, Order::Lock(ship_id(B)), t)).reason, "TARGET_WARPING");
        assert_eq!(answer(&scene.target_order(B, Order::Targets, t)), (false, vec![]));
    }

    #[test]
    fn a_ship_that_is_locked_and_warps_is_let_go_of_without_a_reason() {
        let (mut scene, t) = crowd(2);
        let (_, t) = locked(&mut scene, t, B, ship_id(A));
        scene.command(A, Command::WarpTo { target_id: PLANET, range: 0.0 }, t);
        let (later, _) = poll_until(&mut scene, t, 20.0);
        assert_eq!(
            on_target(&later),
            vec![
                (B, words(&["lost", &ship_id(A).to_string()])),
                (A, words(&["otherlost", &ship_id(B).to_string()])),
            ]
        );
    }

    #[test]
    fn a_ship_that_leaves_is_lost_to_the_ones_that_had_it_locked_before_they_are_told_it_is_gone() {
        let (mut scene, t) = crowd(2);
        let (_, t) = locked(&mut scene, t, B, ship_id(A));
        let out = scene.leave(A, Departure::Docked, t).unwrap();
        let order: Vec<String> = out
            .iter()
            .map(|o| match o {
                Out::Notify { name, session, .. } => format!("{name} to {session}"),
                other => format!("{other:?}"),
            })
            .collect();
        assert_eq!(order, ["OnTarget to 8", "DoDestinyUpdate to 8"]);
        assert_eq!(on_target(&out), vec![(B, words(&["lost", &ship_id(A).to_string()]))]);
        assert_eq!(answer(&scene.target_order(B, Order::Targets, t)), (false, vec![]));
    }

    #[test]
    fn a_ship_that_leaves_lets_go_of_what_it_locked_and_the_ship_that_was_locked_is_told() {
        let (mut scene, t) = crowd(2);
        let (_, t) = locked(&mut scene, t, B, ship_id(A));
        let out = scene.leave(B, Departure::LoggedOff, t).unwrap();
        assert_eq!(on_target(&out), vec![(A, words(&["otherlost", &ship_id(B).to_string()]))]);
        assert_eq!(answer(&scene.target_order(A, Order::Targeters, t)), (false, vec![]));
    }

    #[test]
    fn a_ship_that_docks_is_lost_when_it_has_docked() {
        let mut world = (*world()).clone();
        world.bodies[0].radius = 70_000.0;
        let mut scene = Scene::new(Arc::new(world));
        for (session, n) in [(A, 0), (B, 1)] {
            scene.undock(session, spec_of(n), STATION, NOON).unwrap();
            scene.bind(session, NOON);
        }
        let (_, t) = poll_until(&mut scene, NOON, 40.0);
        let (_, t) = locked(&mut scene, t, B, ship_id(A));
        let accepted = scene.command(A, Command::Dock { station_id: STATION }, t);
        assert!(accepted.iter().any(|o| matches!(o, Out::Accepted { command: 4, .. })), "{accepted:?}");
        let (out, _) = poll_until(&mut scene, t, 6.0);
        let kinds: Vec<String> = out
            .iter()
            .filter(|o| matches!(o, Out::Docked { .. }) || matches!(o, Out::Notify { name: "OnTarget", .. }))
            .map(|o| match o {
                Out::Docked { .. } => "docked".to_string(),
                _ => "OnTarget".to_string(),
            })
            .collect();
        assert_eq!(kinds, ["docked", "OnTarget"]);
        assert_eq!(on_target(&out), vec![(B, words(&["lost", &ship_id(A).to_string()]))]);
    }

    #[test]
    fn a_lock_that_is_going_is_dropped_when_the_target_gets_out_of_range_and_one_that_is_held_is_lost() {
        let (mut scene, t) = crowd(2);
        scene.command(A, Command::Stop, t);
        scene.command(B, Command::Stop, t);
        let (_, t) = poll_until(&mut scene, t, 60.0);
        for (session, x) in [(A, 0.0), (B, 100.0)] {
            let pilot = scene.pilots.get_mut(&session).unwrap();
            pilot.ship.position = Vec3::new(x, 0.0, 0.0);
        }
        scene.target_order(B, Order::Lock(ship_id(A)), t);
        scene.pilots.get_mut(&A).unwrap().ship.position = Vec3::new(50_000.0, 0.0, 0.0);
        let (out, t) = poll_until(&mut scene, t, 6.0);
        assert!(on_target(&out).is_empty(), "{out:?}");
        assert_eq!(answer(&scene.target_order(B, Order::Targets, t)), (false, vec![]));

        scene.pilots.get_mut(&A).unwrap().ship.position = Vec3::new(1_000.0, 0.0, 0.0);
        let (_, t) = locked(&mut scene, t, B, ship_id(A));
        scene.pilots.get_mut(&A).unwrap().ship.position = Vec3::new(50_000.0, 0.0, 0.0);
        let (out, _) = poll_until(&mut scene, t, 1.0);
        assert_eq!(
            on_target(&out),
            vec![
                (B, words(&["lost", &ship_id(A).to_string()])),
                (A, words(&["otherlost", &ship_id(B).to_string()])),
            ]
        );
    }

    #[test]
    fn a_ship_can_lock_no_more_than_it_may_and_a_lock_that_is_going_counts() {
        let (mut scene, t) = crowd(3);
        scene.pilots.get_mut(&C).unwrap().ship.spec.max_locked_targets = 2;
        assert!(answer(&scene.target_order(C, Order::Lock(ship_id(A)), t)).0);
        assert!(answer(&scene.target_order(C, Order::Lock(ship_id(B)), t)).0);
        let full = refusal(&scene.target_order(C, Order::Lock(STATION), t));
        assert_eq!(
            (full.reason, full.desired_range),
            ("TARGET_LOCK_LIMIT_REACHED", 2.0),
            "the limit is told in the place of the range"
        );
        scene.target_order(C, Order::Cancel(ship_id(B)), t);
        assert!(answer(&scene.target_order(C, Order::Lock(STATION), t)).0);
        // none at all
        scene.pilots.get_mut(&C).unwrap().ship.spec.max_locked_targets = 0;
        scene.target_order(C, Order::Clear, t);
        assert_eq!(
            refusal(&scene.target_order(C, Order::Lock(ship_id(B)), t)).reason,
            "TARGET_LOCK_LIMIT_REACHED"
        );
    }

    #[test]
    fn a_ship_that_is_not_in_space_is_refused_every_order() {
        let mut scene = Scene::new(world());
        for order in [
            Order::Lock(1),
            Order::Cancel(1),
            Order::Remove(vec![1]),
            Order::Clear,
            Order::Targets,
            Order::Targeters,
        ] {
            let code = order.code();
            let out = scene.target_order(A, order, NOON);
            assert_eq!(out, [Out::Refused { session: A, command: code, reason: "NO_SHIP_IN_SPACE" }]);
        }
    }

    #[test]
    fn the_codes_of_the_orders_are_those_of_the_link() {
        let codes: Vec<u8> = [
            Order::Lock(1),
            Order::Cancel(1),
            Order::Remove(vec![]),
            Order::Clear,
            Order::Targets,
            Order::Targeters,
        ]
        .iter()
        .map(Order::code)
        .collect();
        assert_eq!(codes, [10, 11, 12, 13, 14, 15]);
    }

    #[test]
    fn a_pilot_that_has_not_bound_the_ballpark_is_told_nothing_of_damage() {
        let mut scene = Scene::new(world());
        scene.undock(A, spec_of(0), STATION, NOON).unwrap();
        scene.undock(B, spec_of(1), STATION, NOON).unwrap();
        // neither has bound: the lock is had and the client is told with the notification, but no update is sent
        let t = NOON + 40 * SECOND;
        scene.target_order(B, Order::Lock(ship_id(A)), t);
        let (out, _) = poll_until(&mut scene, t, 6.0);
        assert_eq!(on_target(&out).len(), 2);
        assert!(told(&out, "DoDestinyUpdate").is_empty(), "{out:?}");
    }
}
