//! Damage over time (the breacher pods). A hit does no damage: it starts a `Dot` on the ship it reaches. A ship can carry several applications, but one
//! works on it at a time: the strongest (the most a tick does at the ship's current total hit points; the one already working wins a tie, then the one
//! strongest breacher pod effect will deal damage to the target, and the weaker effect stays inert until the stronger one expires").
//!
//! ship's total hit points (shield, armor and hull, their capacities) and its flat cap, rounded down (`int(min(...))` in eve-logic `dotUtil.py`), and it
//!
//! Nothing is persisted: a restart drops the applications, as it drops the missiles in flight.
use std::borrow::Cow;
use std::cmp::Ordering;

use eve_marshal::{ObjectExKind, Value};

use crate::missile_entity::Shooter;
use crate::missiles::Dot;
use crate::payload;
use crate::scene::{Out, Scene, SessionId, TICKS_PER_SECOND};
use crate::wire::{dict, filetime, id, int, real, tuple};

/// One second of a tick, in the milliseconds a `Dot`'s duration is in.
const TICK_MS: f64 = 1_000.0;

/// The client's `DOT_IDLE` and `DOT_ACTIVE` (eve-logic `dotWeapons/common/dotConst.py`): the states of an application in its `activityState`.
const DOT_IDLE: i64 = 1;
const DOT_ACTIVE: i64 = 2;

/// One hit's application of a `Dot` to a ship.
#[derive(Debug, Clone, PartialEq)]
pub struct Application {
    /// Its place in the order the hits landed (the earlier one wins a tie).
    pub id: u64,
    pub dot: Dot,
    /// Who fired the pod (the pilot, its ship and launcher): the killer of a ship that the effect destroys.
    pub shooter: Shooter,
    /// The milliseconds it has left. It counts down only while it is the one working on its ship.
    pub remaining_ms: f64,
}

/// The applications on one ship, which of them works, when the next tick is due, and the percentage of its hit points the slim item shows (0 for none).
#[derive(Debug, Clone, PartialEq, Default)]
pub struct Target {
    pub applications: Vec<Application>,
    pub working: Option<u64>,
    pub next_tick: i64,
    pub shown: f64,
}

/// What a `Dot` does to a ship with `total` hit points: the lower of its percentage of them and its cap, rounded down.
pub fn tick_damage(dot: &Dot, total: f64) -> f64 {
    let ratio = (dot.max_hp_percent_per_tick / 100.0).clamp(0.0, 1.0);
    (ratio * total).min(dot.max_damage_per_tick).max(0.0).floor()
}

impl Scene {
    /// A breacher pod hit lands on the ship `ship_id` at `at`: the application joins the ship's, and its first tick is a second on (when it is the first).
    pub(crate) fn apply_dot(&mut self, ship_id: i64, dot: Dot, shooter: Shooter, at: i64) -> Vec<Out> {
        let sequence = self.dot_sequence;
        self.dot_sequence += 1;
        let target = self.dots.entry(ship_id).or_default();
        if target.applications.is_empty() {
            target.next_tick = at + TICKS_PER_SECOND;
        }
        target.applications.push(Application { id: sequence, dot, shooter, remaining_ms: dot.duration_ms });
        self.dot_victim_update(ship_id, at)
    }

    /// The client's list of the applications on `ship` (`OnDotVictimUpdated`, a destiny action on the ship, told to whoever has it in view). The set
    /// holds each application as a `DotApplicationInfo`: the working one expires at the end of its remaining time; an inert one expires at the end of
    /// the queue, because its clock stops while it waits and the queue ends when the working one does. Both times stay right until the next change,
    /// which is when this is told again (on a hit, when the working application changes, and when one ends).
    fn dot_victim_update(&mut self, ship: i64, at: i64) -> Vec<Out> {
        let Some(subject) = self.session_of(ship) else { return Vec::new() };
        let (working, apps): (Option<u64>, &[Application]) = match self.dots.get(&ship) {
            Some(target) => (target.working, &target.applications),
            None => (None, &[]),
        };
        let ticks = |ms: f64| (ms * (TICKS_PER_SECOND as f64 / 1_000.0)).round() as i64;
        let queue_end = at + ticks(apps.iter().map(|app| app.remaining_ms).sum());
        let infos: Vec<Value> = apps
            .iter()
            .map(|app| {
                let is_working = working == Some(app.id);
                let expiry = if is_working { at + ticks(app.remaining_ms) } else { queue_end };
                application_info(app, ship, expiry, is_working)
            })
            .collect();
        let stamp = self.next_stamp(at);
        let action = payload::action(stamp, "OnDotVictimUpdated", [id(ship), Value::python_set(infos)]);
        self.tell_all(subject, vec![action])
    }

    /// Every tick due by `now`, for each ship that carries an application. A ship that is no longer in the scene carries none.
    pub(crate) fn advance_dots(&mut self, now: i64) -> Vec<Out> {
        if self.dots.is_empty() {
            return Vec::new();
        }
        let ships: Vec<i64> = self.dots.keys().copied().collect();
        let mut out = Vec::new();
        for ship in ships {
            let Some(session) = self.session_of(ship) else {
                self.dots.remove(&ship);
                continue;
            };
            while self.dots.get(&ship).is_some_and(|t| !t.applications.is_empty() && t.next_tick <= now) {
                let at = self.dots[&ship].next_tick;
                out.extend(self.dot_tick(ship, session, at));
                if let Some(target) = self.dots.get_mut(&ship) {
                    target.next_tick += TICKS_PER_SECOND;
                }
            }
            if self.dots.get(&ship).is_some_and(|t| t.applications.is_empty()) {
                self.dots.remove(&ship);
            }
        }
        out
    }

    /// One tick on `ship` at `at`: the strongest application works and takes a second off its effect. A ship it destroys is gone, with the pilot who fired it
    /// as the killer when it is still here.
    fn dot_tick(&mut self, ship: i64, session: SessionId, at: i64) -> Vec<Out> {
        let Some(pilot) = self.pilots.get(&session) else { return Vec::new() };
        let max = pilot.ship.health.vitals().layers();
        let total = max.shield + max.armor + max.structure;
        let Some(target) = self.dots.get_mut(&ship) else { return Vec::new() };
        let previous = target.working;
        let strongest = target
            .applications
            .iter()
            .max_by(|a, b| {
                tick_damage(&a.dot, total)
                    .partial_cmp(&tick_damage(&b.dot, total))
                    .unwrap_or(Ordering::Equal)
                    .then_with(|| (Some(a.id) == previous).cmp(&(Some(b.id) == previous)))
                    .then_with(|| b.id.cmp(&a.id))
            })
            .map(|a| a.id);
        target.working = strongest;
        let switched = previous != strongest;
        let Some(index) = target.applications.iter().position(|a| Some(a.id) == strongest) else {
            return Vec::new();
        };
        let damage = tick_damage(&target.applications[index].dot, total);
        let shooter = target.applications[index].shooter.clone();
        target.applications[index].remaining_ms -= TICK_MS;
        let ended = target.applications[index].remaining_ms <= 0.0;
        if ended {
            target.applications.remove(index);
            target.working = None;
        }
        // the slim item shows what the tick does, as a percentage of the hit points (V24.01: 100 * damage / total), and nothing once the effect is over
        let shown = if target.applications.is_empty() || total <= 0.0 { 0.0 } else { 100.0 * damage / total };
        let changed = shown != target.shown;
        target.shown = shown;
        let mut out = Vec::new();
        if damage > 0.0 {
            let (told, applied) = self.damage_unresisted(ship, [damage, 0.0, 0.0, 0.0], at);
            out.extend(told);
            if applied.is_some_and(|applied| applied.destroyed) {
                self.dots.remove(&ship);
                let killer = self.pilots.contains_key(&shooter.session).then_some(shooter.session);
                out.extend(self.destroy(session, killer, at));
                return out;
            }
        }
        if switched || ended {
            out.extend(self.dot_victim_update(ship, at));
        }
        if changed {
            out.extend(self.dot_slim(session, at, (shown > 0.0).then_some(shown)));
        }
        out
    }

    /// The slim item of the ship `session` flies is told again to whoever has it in view: with the damage over time it takes (`Some` percentage), or without (`None`).
    /// shortcut: a ship that comes into view mid-effect is not given the field until its next change, since the slim item is built from the ship alone; upgrade when `slim::ship_entries` reads the applications.
    fn dot_slim(&mut self, session: SessionId, at: i64, percent: Option<f64>) -> Vec<Out> {
        let stamp = self.next_stamp(at);
        let Some(pilot) = self.pilots.get(&session) else { return Vec::new() };
        let change = match percent {
            Some(percent) => payload::ship_slim_item_change_with(
                stamp,
                &pilot.ship,
                vec![("dotHpPercentageApplied", real(percent))],
            ),
            None => payload::ship_slim_item_change(stamp, &pilot.ship),
        };
        self.tell_all(session, vec![change])
    }
}

/// One application as the client's `DotApplicationInfo` (eve-logic `dotWeapons/common/dotAppInfo.py`): an instance of the class, whose state
/// is the properties' backing attributes. The attacker is the character that fired the pod; the weapon is its launcher's type.
fn application_info(app: &Application, ship: i64, expiry: i64, working: bool) -> Value {
    let ratio = (app.dot.max_hp_percent_per_tick / 100.0).clamp(0.0, 1.0);
    let state = dict([
        ("_attackerID", id(app.shooter.owner_id)),
        ("_weaponTypeID", int(i64::from(app.shooter.weapon_type_id))),
        ("_expiryTime", filetime(expiry)),
        ("_maxDamage", real(app.dot.max_damage_per_tick)),
        ("_maxHPNormalizedRatio", real(ratio)),
        ("_activityState", int(if working { DOT_ACTIVE } else { DOT_IDLE })),
        ("_targetID", id(ship)),
    ]);
    let class = Value::Token(Cow::Borrowed("dotWeapons.common.dotAppInfo.DotApplicationInfo"));
    Value::ObjectEx {
        kind: ObjectExKind::Ex1,
        header: Box::new(tuple([class, tuple(Vec::new()), state])),
        list: Vec::new(),
        dict: Vec::new(),
    }
}

#[cfg(test)]
mod tests;
