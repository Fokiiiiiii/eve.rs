//! What other ships' effects do to a ship's numbers for as long as they keep landing: a stasis webifier slows it, a target painter makes it bigger, a sensor dampener
//! shortens the range it locks at and slows its locks, a warp scrambler or disruptor keeps it out of warp.
//!
//! The effect is kept by the ship it lands on (`Incoming`), one record for each module of each ship, renewed as each cycle of the module begins and lasting a cycle and a
//! tick past that, so that a module that stops (or whose ship leaves the scene) lets go of the target by itself. The ship's own numbers (what its local effects make of them)
//! are the base: what the ship has is the base worked by what lands on it, the percentages of one attribute stacking with the game's penalty. The reference
//! (`hostileModuleRuntime.js`) keeps the same state in `incomingEffects` and rebuilds the ship's numbers from it.

use crate::modules::TICK;
use crate::scene::{Command, Out, Scene, SessionId, TICKS_PER_SECOND};
use crate::wire::{id, real};

/// The ship attributes the client is told of as they change.
const MAX_VELOCITY: i32 = 37;
const SIGNATURE_RADIUS: i32 = 552;
const MAX_TARGET_RANGE: i32 = 76;
const SCAN_RESOLUTION: i32 = 564;

/// What one module does to the ship it is projected at, already weakened by the distance (percentages of the attribute, the points of a scramble).
#[derive(Debug, Clone, Copy, PartialEq)]
pub(crate) enum Lasting {
    /// A percentage of the top speed (negative for a web).
    Web { percent: f64 },
    /// A percentage of the signature radius (positive for a target painter).
    Paint { percent: f64 },
    /// A percentage of the range the ship locks at and of its scan resolution (negative for a dampener).
    Damp { range_percent: f64, resolution_percent: f64 },
    /// Points of warp scramble: more than the ship's own warp core holds it out of warp (`blocks_mwd`: a microwarpdrive too).
    Scramble { strength: f64, blocks_mwd: bool },
}

#[derive(Debug, Clone, PartialEq)]
struct Record {
    /// The ship that runs the module and the module.
    source: i64,
    module: i64,
    what: Lasting,
    /// When the record lapses unless the module renews it.
    until: i64,
}

/// The numbers of a ship that what lands on it works on.
#[derive(Debug, Clone, Copy, PartialEq)]
pub(crate) struct Base {
    pub max_velocity: f64,
    pub signature_radius: f64,
    pub max_target_range: f64,
    pub scan_resolution: f64,
}

/// What lands on a ship.
#[derive(Debug, Clone, Default)]
pub(crate) struct Incoming {
    records: Vec<Record>,
    /// The ship's numbers without what lands on it, kept while something does.
    pub(crate) base: Option<Base>,
}

/// The penalty of the `n`th strongest modifier of an attribute (counting from 0): `e^(-(n / 2.67)^2)`.
fn penalty(n: usize) -> f64 {
    (-(n as f64 / 2.67).powi(2)).exp()
}

/// The factor percentages of one attribute make together: those that raise it and those that lower it are each worked from the strongest, which counts in full, down.
fn stacked(percents: impl IntoIterator<Item = f64>) -> f64 {
    let (mut up, mut down): (Vec<f64>, Vec<f64>) = percents.into_iter().partition(|p| *p >= 0.0);
    up.sort_by(|a, b| b.total_cmp(a));
    down.sort_by(|a, b| a.total_cmp(b));
    let work = |list: &[f64]| {
        list.iter().enumerate().fold(1.0, |factor, (n, p)| factor * (1.0 + p / 100.0 * penalty(n)))
    };
    work(&up) * work(&down)
}

impl Incoming {
    pub(crate) fn is_empty(&self) -> bool {
        self.records.is_empty()
    }

    /// Records or renews what a module does to the ship, until `until`.
    fn upsert(&mut self, source: i64, module: i64, what: Lasting, until: i64) {
        let record = Record { source, module, what, until };
        match self.records.iter_mut().find(|r| r.source == source && r.module == module) {
            Some(existing) => *existing = record,
            None => self.records.push(record),
        }
    }

    /// The points of warp scramble on the ship: it cannot warp while there are any.
    pub(crate) fn scramble_points(&self) -> f64 {
        self.records
            .iter()
            .filter_map(|r| match r.what {
                Lasting::Scramble { strength, .. } => Some(strength),
                _ => None,
            })
            .sum()
    }

    pub(crate) fn scrambled(&self) -> bool {
        self.scramble_points() > 1e-6
    }

    fn factor(&self, pick: impl Fn(&Lasting) -> Option<f64>) -> f64 {
        stacked(self.records.iter().filter_map(|r| pick(&r.what)))
    }

    /// The numbers of the ship with what lands on it worked into `base`.
    pub(crate) fn worked(&self, base: &Base) -> Base {
        Base {
            max_velocity: base.max_velocity
                * self.factor(|w| match w {
                    Lasting::Web { percent } => Some(*percent),
                    _ => None,
                }),
            signature_radius: base.signature_radius
                * self.factor(|w| match w {
                    Lasting::Paint { percent } => Some(*percent),
                    _ => None,
                }),
            max_target_range: base.max_target_range
                * self.factor(|w| match w {
                    Lasting::Damp { range_percent, .. } => Some(*range_percent),
                    _ => None,
                }),
            scan_resolution: base.scan_resolution
                * self.factor(|w| match w {
                    Lasting::Damp { resolution_percent, .. } => Some(*resolution_percent),
                    _ => None,
                }),
        }
    }
}

impl Scene {
    /// A module of the ship of `source` lands what it does on the ship it is aimed at: recorded for a cycle and a tick, and the ship's numbers worked out again.
    pub(crate) fn hold(
        &mut self,
        source: SessionId,
        effect: &crate::modules::Effect,
        what: Lasting,
        at: i64,
    ) -> Vec<Out> {
        let Some(target) = self.session_of(effect.target_id) else { return Vec::new() };
        let Some(source_ship) = self.pilots.get(&source).map(|pilot| pilot.ship.spec.ship_id) else {
            return Vec::new();
        };
        let until = at + (effect.duration_ms * TICKS_PER_SECOND as f64 / 1000.0) as i64 + TICK;
        let Some(pilot) = self.pilots.get_mut(&target) else { return Vec::new() };
        if pilot.incoming.base.is_none() {
            let spec = &pilot.ship.spec;
            pilot.incoming.base = Some(Base {
                max_velocity: spec.max_velocity,
                signature_radius: spec.signature_radius,
                max_target_range: spec.max_target_range,
                scan_resolution: spec.scan_resolution,
            });
        }
        pilot.incoming.upsert(source_ship, effect.module_id, what, until);
        let mut out = self.retune(target, at);
        // a ship that is still lining up for a warp it can no longer make is stopped (the reference stops it as the scramble lands)
        if matches!(what, Lasting::Scramble { .. }) {
            out.extend(self.cancel_alignment(target, at));
        }
        out
    }

    /// A module stopped (or its target was lost): what it did to the ship it was aimed at is lifted.
    pub(crate) fn release(&mut self, source_ship: i64, module: i64, target_ship: i64, at: i64) -> Vec<Out> {
        let Some(target) = self.session_of(target_ship) else { return Vec::new() };
        let Some(pilot) = self.pilots.get_mut(&target) else { return Vec::new() };
        let before = pilot.incoming.records.len();
        pilot.incoming.records.retain(|r| !(r.source == source_ship && r.module == module));
        if pilot.incoming.records.len() == before {
            return Vec::new();
        }
        self.retune(target, at)
    }

    /// What no module renewed lapses (a module whose ship has left the scene lets go of its target by itself).
    pub(crate) fn expire_incoming(&mut self, session: SessionId, now: i64) -> Vec<Out> {
        let Some(pilot) = self.pilots.get_mut(&session) else { return Vec::new() };
        let before = pilot.incoming.records.len();
        pilot.incoming.records.retain(|r| r.until > now);
        if pilot.incoming.records.len() == before {
            return Vec::new();
        }
        self.retune(session, now)
    }

    /// The ship's numbers are what its base is with what lands on it worked in; those that changed are told to its pilot and, for the speed, to all who see it.
    pub(crate) fn retune(&mut self, session: SessionId, at: i64) -> Vec<Out> {
        let Some(pilot) = self.pilots.get_mut(&session) else { return Vec::new() };
        let Some(base) = pilot.incoming.base else { return Vec::new() };
        let worked = if pilot.incoming.is_empty() { base } else { pilot.incoming.worked(&base) };
        let (owner, ship_id) = (pilot.ship.spec.character_id, pilot.ship.spec.ship_id);
        // a ship in warp keeps the numbers it has until it is out (what it was given is worked in then)
        let in_warp = pilot.flight.is_some();
        let spec = &mut pilot.ship.spec;
        let mut changes = Vec::new();
        let mut set = |attribute: i32, old: &mut f64, new: f64| {
            if (*old - new).abs() > 1e-9 {
                changes.push((attribute, new, *old));
                *old = new;
            }
        };
        if !in_warp {
            set(MAX_VELOCITY, &mut spec.max_velocity, worked.max_velocity);
        }
        set(SIGNATURE_RADIUS, &mut spec.signature_radius, worked.signature_radius);
        set(MAX_TARGET_RANGE, &mut spec.max_target_range, worked.max_target_range);
        set(SCAN_RESOLUTION, &mut spec.scan_resolution, worked.scan_resolution);
        let velocity = spec.max_velocity;
        if pilot.incoming.is_empty() {
            pilot.incoming.base = None;
        }
        let mut out: Vec<Out> =
            Self::health_told(session, owner, ship_id, at, &changes).into_iter().collect();
        if changes.iter().any(|(attribute, ..)| *attribute == MAX_VELOCITY) {
            let stamp = self.next_stamp(at);
            let action = crate::payload::action(stamp, "SetMaxSpeed", [id(ship_id), real(velocity)]);
            out.extend(self.tell_all(session, vec![action]));
        }
        out
    }

    /// A ship that is lining up for a warp (not yet in it) is stopped.
    pub(crate) fn cancel_alignment(&mut self, session: SessionId, at: i64) -> Vec<Out> {
        let Some(pilot) = self.pilots.get_mut(&session) else { return Vec::new() };
        if pilot.flight.as_ref().is_none_or(|flight| flight.is_warping()) {
            return Vec::new();
        }
        pilot.flight = None;
        pilot.warp_target = None;
        self.command(session, Command::Stop, at)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn modifiers_of_one_attribute_stack_with_the_games_penalty_from_the_strongest_down() {
        // one web is the whole of it, a second counts 86.9%, a third 57.1%, a fourth 28.3%
        assert!((stacked([-50.0]) - 0.5).abs() < 1e-9);
        let two = stacked([-50.0, -50.0]);
        assert!((two - 0.5 * (1.0 - 0.5 * penalty(1))).abs() < 1e-9, "{two}");
        assert!(
            (penalty(1) - 0.869_119).abs() < 1e-5
                && (penalty(2) - 0.570_584).abs() < 1e-5
                && (penalty(3) - 0.282_950).abs() < 1e-5
        );
        // the strongest counts in full whatever the order, and what raises and what lowers are worked apart
        assert_eq!(stacked([-30.0, -60.0]), stacked([-60.0, -30.0]));
        let mixed = stacked([25.0, -50.0]);
        assert!((mixed - 1.25 * 0.5).abs() < 1e-9, "{mixed}");
        assert_eq!(stacked([]), 1.0);
    }

    #[test]
    fn a_record_is_renewed_by_the_same_module_and_the_numbers_are_worked_from_the_base() {
        let base = Base {
            max_velocity: 400.0,
            signature_radius: 40.0,
            max_target_range: 30_000.0,
            scan_resolution: 500.0,
        };
        let mut incoming = Incoming::default();
        incoming.upsert(1, 10, Lasting::Web { percent: -60.0 }, 100);
        incoming.upsert(1, 10, Lasting::Web { percent: -40.0 }, 200);
        incoming.upsert(2, 10, Lasting::Paint { percent: 25.0 }, 100);
        incoming.upsert(3, 11, Lasting::Damp { range_percent: -20.0, resolution_percent: -30.0 }, 100);
        let worked = incoming.worked(&base);
        assert!((worked.max_velocity - 240.0).abs() < 1e-9, "{worked:?}");
        assert!((worked.signature_radius - 50.0).abs() < 1e-9);
        assert!(
            (worked.max_target_range - 24_000.0).abs() < 1e-9
                && (worked.scan_resolution - 350.0).abs() < 1e-9
        );
        assert!(!incoming.scrambled());
        incoming.upsert(4, 12, Lasting::Scramble { strength: 2.0, blocks_mwd: true }, 100);
        incoming.upsert(5, 13, Lasting::Scramble { strength: 1.0, blocks_mwd: false }, 100);
        assert_eq!(incoming.scramble_points(), 3.0);
        assert!(incoming.scrambled());
    }
}
