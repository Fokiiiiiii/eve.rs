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

/// Who asks for a lock, and so whether the ship that is locked is told. A player's order and an auto-targeting sweep tell it (`otheradd`); a
/// passive targeter may keep it quiet (EveJS `suppressTargetNotice`). Every origin takes the same checks, range, capacity and lock time.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum LockOrigin {
    /// The pilot asked for the lock.
    Player,
    /// A passive targeter locks the target; `suppress_notice` keeps the target from being told.
    Passive { suppress_notice: bool },
    /// An auto-targeting sweep locks a ship that is already targeting the pilot.
    Auto,
}

impl LockOrigin {
    /// Whether the ship that is locked is told (`otheradd`).
    fn tells_target(self) -> bool {
        !matches!(self, Self::Passive { suppress_notice: true })
    }
}

#[derive(Debug, Clone, PartialEq)]
struct Pending {
    target: i64,
    sequence: u64,
    complete_at: i64,
    origin: LockOrigin,
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

    pub(crate) fn targets(&self) -> Vec<i64> {
        let mut locked: Vec<&Locked> = self.locked.iter().collect();
        locked.sort_by_key(|l| (l.sequence, l.target));
        locked.into_iter().map(|l| l.target).collect()
    }
}

pub(crate) struct Subject {
    id: i64,
    session: Option<SessionId>,
    position: Vec3,
    radius: f64,
    type_id: i32,
    group_id: i32,
    signature_radius: f64,
    state: DamageState,
    has_damage_state: bool,
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
                has_damage_state: true,
                warping: pilot.flight.as_ref().is_some_and(|f| f.is_warping()),
                protected: pilot.protected_until > now,
            });
        }
        if let Some(wreck) = self.wrecks.get(&target) {
            return Some(Subject {
                id: target,
                session: None,
                position: wreck.position,
                radius: wreck.radius,
                type_id: wreck.kind.type_id,
                group_id: wreck.group_id,
                signature_radius: wreck.radius,
                state: DamageState::whole(0.0),
                has_damage_state: false,
                warping: false,
                protected: false,
            });
        }
        // a mineable rock is locked through the same path as a wreck. Its id is an item id (the host allocates them from 1,990,000,000);
        // the id spaces of rocks and system bodies are not yet verified against the SDE, so a rock is looked up before the bodies (UNVERIFIED, DEVIATIONS).
        // A depleted rock is removed from the map, so it stops resolving and its locks are dropped by the upkeep in `target_order`.
        if let Some(asteroid) = self.asteroids.get(&target) {
            return Some(Subject {
                id: target,
                session: None,
                position: asteroid.position,
                radius: asteroid.radius,
                type_id: asteroid.type_id,
                group_id: asteroid.group_id,
                signature_radius: asteroid.radius,
                state: DamageState::whole(0.0),
                has_damage_state: false,
                warping: false,
                protected: false,
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
            has_damage_state: body.kind == BodyKind::Station,
            warping: false,
            protected: false,
        })
    }

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
        // a jammed ship can lock only the ships that jam it
        if source.incoming.jammed() && !source.incoming.jammers().contains(&target.id) {
            return Err(Refusal::of("TARGET_JAMMED"));
        }
        if source.flight.as_ref().is_some_and(|f| f.is_warping()) {
            return Err(Refusal::of("SOURCE_WARPING"));
        }
        if target.warping {
            return Err(Refusal::of_type("TARGET_WARPING", target.type_id));
        }
        let observer = crate::visibility::Presence { position: source.ship.position, warping: false };
        let subject = crate::visibility::Presence { position: target.position, warping: false };
        let target_cloak = self
            .pilots
            .values()
            .find(|pilot| pilot.ship.spec.ship_id == target.id)
            .map_or(crate::visibility::CloakState::Uncloaked, |pilot| pilot.cloak);
        if !crate::visibility::can_select_target(observer, subject, source.cloak, target_cloak) {
            return Err(Refusal::of("TARGET_NOT_FOUND"));
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
    pub(crate) fn targeters_of(&self, target: i64) -> Vec<i64> {
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
            Order::Lock(target) => self.lock(session, *target, &order, LockOrigin::Player, now),
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

    pub(crate) fn ship_id_of(&self, session: SessionId) -> i64 {
        self.pilots.get(&session).map(|p| p.ship.spec.ship_id).unwrap_or(0)
    }

    /// The one lock path: every origin is checked the same way; the origin only decides who is told (see `finish_lock`).
    /// The auto-target sweep of an activation or a cycle (EVE.js `executeAutoTargetingCycle`): each ship that has this pilot locked, within `range` of it and not
    /// locked by the pilot yet, is locked with the origin `Auto` (the ship is told, as any lock tells it). A ship the lock would refuse, and any ship beyond a
    /// full lock list, is passed over silently: a sweep asks only for what the pilot can have.
    pub(crate) fn auto_target_sweep(&mut self, session: SessionId, range: f64, now: i64) -> Vec<Out> {
        let Some(me) = self.pilots.get(&session).map(|p| p.ship.spec.ship_id) else { return Vec::new() };
        let mut out = Vec::new();
        for candidate in self.targeters_of(me) {
            let Some(pilot) = self.pilots.get(&session) else { break };
            if candidate == me
                || pilot.targeting.locked.iter().any(|l| l.target == candidate)
                || pilot.targeting.pending.iter().any(|p| p.target == candidate)
                || pilot.targeting.locked.len() + pilot.targeting.pending.len() >= pilot.ship.spec.max_locked_targets.max(0) as usize
            {
                continue;
            }
            let Some(subject) = self.subject(candidate, now) else { continue };
            let apart = (distance(pilot.ship.position, subject.position) - pilot.ship.spec.radius - subject.radius).max(0.0);
            if apart >= range || self.check_lock(session, &subject, true).is_err() {
                continue;
            }
            out.extend(self.lock(session, candidate, &Order::Lock(candidate), LockOrigin::Auto, now));
        }
        out
    }

    pub(crate) fn lock(
        &mut self,
        session: SessionId,
        target: i64,
        order: &Order,
        origin: LockOrigin,
        now: i64,
    ) -> Vec<Out> {
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
            out.extend(self.finish_lock(session, subject, sequence, origin, now));
            return out;
        }
        let complete_at = now + (duration * TICKS_PER_SECOND as f64 / 1000.0) as i64;
        pilot.targeting.pending.push(Pending { target, sequence, complete_at, origin });
        Self::answer(session, order, true, self.locked_by(session))
    }

    fn finish_lock(
        &mut self,
        session: SessionId,
        subject: Subject,
        sequence: u64,
        origin: LockOrigin,
        now: i64,
    ) -> Vec<Out> {
        let Some(pilot) = self.pilots.get_mut(&session) else { return Vec::new() };
        pilot.targeting.pending.retain(|p| p.target != subject.id);
        pilot.targeting.locked.push(Locked { target: subject.id, sequence });
        let source_id = pilot.ship.spec.ship_id;
        let mut out = vec![on_target(session, "add", Some(subject.id))];
        if origin.tells_target()
            && let Some(target_session) = subject.session
            && target_session != session
        {
            out.push(on_target(target_session, "otheradd", Some(source_id)));
        }
        if subject.has_damage_state {
            out.extend(self.announce_damage_state(&subject, now));
        }
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

    /// A ship's locks are broken but those on the ships in `allowed` (the ones that jam it): what is going is given up and what is held is let go, with the reason `target`.
    pub(crate) fn break_locks_except(&mut self, session: SessionId, allowed: &[i64], now: i64) -> Vec<Out> {
        let Some(pilot) = self.pilots.get_mut(&session) else { return Vec::new() };
        let going: Vec<i64> =
            pilot.targeting.pending.iter().map(|p| p.target).filter(|t| !allowed.contains(t)).collect();
        let mut out = Vec::new();
        for target in going {
            self.cancel_pending(session, target);
            out.push(on_target_reason(session, "lost", Some(target), Some("target")));
        }
        for target in self.locked_by(session).into_iter().filter(|t| !allowed.contains(t)) {
            out.extend(self.remove_locked(session, target, Some("target"), true, now));
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
                    out.extend(self.finish_lock(session, subject, pending.sequence, pending.origin, now));
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
mod tests;
