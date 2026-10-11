//! Targeting: what a ship has locked, what is locking, and who has it locked.
//!
//! A lock takes time (the ship's scan resolution against the size of what it locks), can be given up before it is done, ends when either ship
//! warps or goes, and is refused for the reasons `oracle/fixtures/scenarios/target.json` records. Only ships and stations can be locked; a
//! planet, a moon, a belt, a gate or the star cannot. The client is told with `OnTarget` (`add`, `lost`, `clear` to the one that locks;
//! `otheradd`, `otherlost` to the one that is locked) and with the damage state of what has been locked, to everyone who can see it.
mod lock;
mod settle;
mod unlock;

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
    pub(crate) fn is_empty(&self) -> bool {
        self.pending.is_empty() && self.locked.is_empty()
    }

    /// Held and pending targets without allocating a sorted snapshot.
    pub(crate) fn all_targets(&self) -> impl Iterator<Item = i64> + '_ {
        self.locked.iter().map(|lock| lock.target).chain(self.pending.iter().map(|lock| lock.target))
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
                protected: pilot.protected_until > now || self.is_tethered_session(*session, now),
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
