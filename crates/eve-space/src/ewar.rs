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

/// What the client's HUD calls an ECM jam (the reference's `jammingType` of an ECM module).
const JAMMING: &str = "electronic";

pub(crate) fn jam_chance(strength: &[f64; 4], sensor: &[f64; 4], resistance: f64, share: f64) -> f64 {
    let mut best = None;
    for (n, s) in sensor.iter().enumerate() {
        let sensor_strength = if s.is_finite() { s.max(0.0) } else { 0.0 };
        if sensor_strength > 0.0 && best.is_none_or(|(_, top)| sensor_strength > top) {
            best = Some((n, sensor_strength));
        }
    }
    let Some((kind, held)) = best else { return 0.0 };
    let jammer_strength = if strength[kind].is_finite() { strength[kind].max(0.0) } else { 0.0 };
    let reach = if share.is_finite() { share.max(0.0) } else { 0.0 };
    // Dogma's ECMResistance is an effectiveness multiplier: 1 is ordinary, 0 is immune.
    // EVE.js falls back to the Dogma default (1) when the target value is not finite.
    let resistance = if resistance.is_finite() { resistance.clamp(0.0, 1.0) } else { 1.0 };
    if jammer_strength == 0.0 || reach == 0.0 || resistance == 0.0 {
        return 0.0;
    }
    (jammer_strength / held.max(1e-6) * reach * resistance).clamp(0.0, 1.0)
}

#[cfg(test)]
fn burst_reaches(
    source_position: [f64; 3],
    source_radius: f64,
    target_position: [f64; 3],
    target_radius: f64,
    burst_radius: f64,
) -> bool {
    burst_radius.is_finite()
        && burst_radius > 0.0
        && crate::geo::surface_gap(source_position, source_radius, target_position, target_radius)
            .is_some_and(|distance| distance <= burst_radius + 1.0)
}

/// A draw in `[0, 1)` is a jam when it falls under the chance.
fn rolls_under(roll: f64, chance: f64) -> bool {
    chance > 0.0 && roll < chance
}

/// The ship attributes the client is told of as they change.
const MAX_VELOCITY: i32 = 37;
const SIGNATURE_RADIUS: i32 = 552;
const MAX_TARGET_RANGE: i32 = 76;
const SCAN_RESOLUTION: i32 = 564;

pub(crate) const SCAN_GRAVIMETRIC_STRENGTH_PERCENT: i32 = 1027;
pub(crate) const SCAN_LADAR_STRENGTH_PERCENT: i32 = 1028;
pub(crate) const SCAN_MAGNETOMETRIC_STRENGTH_PERCENT: i32 = 1029;
pub(crate) const SCAN_RADAR_STRENGTH_PERCENT: i32 = 1030;

/// What one module does to the ship it is projected at, already weakened by the distance (percentages of the attribute, the points of a scramble).
#[derive(Debug, Clone, Copy, PartialEq)]
pub(crate) enum Lasting {
    /// A percentage of the top speed (negative for a web).
    Web { percent: f64 },
    /// A percentage of the signature radius (positive for a target painter).
    Paint { percent: f64 },
    /// Percentages of lock range, scan resolution and the four sensor strengths (negative values are hostile).
    Damp {
        range_percent: f64,
        resolution_percent: f64,
        gravimetric_percent: f64,
        ladar_percent: f64,
        magnetometric_percent: f64,
        radar_percent: f64,
    },
    /// Points of warp scramble: more than the ship's own warp core holds it out of warp (`blocks_mwd`: a microwarpdrive too).
    Scramble { strength: f64, blocks_mwd: bool },
    /// A fighter's tackle: a warp scramble of `strength` points and a web of `web_percent` of the top speed, both for as long as it lands (the SDE's
    /// `fighterAbilityTackle`: warp disruption point strength and web speed penalty, both on the one effect).
    Tackle { strength: f64, web_percent: f64 },
    /// Percentages of the optimal range, the falloff and the tracking speed of the turrets the ship fires (negative for a tracking disruptor).
    Disrupt { range_percent: f64, falloff_percent: f64, tracking_percent: f64 },
    /// Percentages of the speed, flight time, explosion velocity and explosion radius of the missiles the ship launches (negative but for the radius for a guidance disruptor).
    GuidanceDisrupt {
        velocity_percent: f64,
        delay_percent: f64,
        aoe_velocity_percent: f64,
        aoe_cloud_percent: f64,
    },
    /// Held by an ECM jammer: the ship's locks are broken and it can lock only the ships that jam it.
    Jam,
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
    pub sensor_strength: [f64; 4],
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
    pub(crate) fn upsert(&mut self, source: i64, module: i64, what: Lasting, until: i64) {
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
                Lasting::Scramble { strength, .. } | Lasting::Tackle { strength, .. } => Some(strength),
                _ => None,
            })
            .sum()
    }

    /// Whether a warp scrambler that blocks a microwarpdrive holds the ship.
    pub(crate) fn mwd_blocked(&self) -> bool {
        self.records.iter().any(|r| matches!(r.what, Lasting::Scramble { blocks_mwd: true, .. }))
    }

    /// Whether the scrambles on the ship, with the change its own modules make to its scramble status (`status`, a warp core stabilizer's -2), leave it
    /// scrambled: a point the status cancels is not a scramble.
    pub(crate) fn scrambled_with(&self, status: f64) -> bool {
        self.scramble_points() + status > 1e-6
    }

    /// The ships that jam this one (it can lock these and nothing else for as long as any does).
    pub(crate) fn jammers(&self) -> Vec<i64> {
        let mut by: Vec<i64> =
            self.records.iter().filter(|r| r.what == Lasting::Jam).map(|r| r.source).collect();
        by.sort_unstable();
        by.dedup();
        by
    }

    /// What lands on the ship's turrets: the factors of the optimal range, the falloff and the tracking speed (1 for a ship nothing disrupts), each stacking with the
    /// game's penalty.
    pub(crate) fn weapon_factors(&self) -> (f64, f64, f64) {
        let pick = |choose: fn(&Lasting) -> Option<f64>| self.factor(choose);
        (
            pick(
                |w| if let Lasting::Disrupt { range_percent, .. } = w { Some(*range_percent) } else { None },
            ),
            pick(|w| {
                if let Lasting::Disrupt { falloff_percent, .. } = w { Some(*falloff_percent) } else { None }
            }),
            pick(|w| {
                if let Lasting::Disrupt { tracking_percent, .. } = w { Some(*tracking_percent) } else { None }
            }),
        )
    }

    /// What lands on the ship's missiles: the factors of their speed, flight time, explosion velocity and explosion radius, each stacking with the game's penalty.
    pub(crate) fn missile_factors(&self) -> crate::missiles::Guidance {
        let pick = |choose: fn(&Lasting) -> Option<f64>| self.factor(choose);
        crate::missiles::Guidance {
            velocity: pick(|w| {
                if let Lasting::GuidanceDisrupt { velocity_percent, .. } = w {
                    Some(*velocity_percent)
                } else {
                    None
                }
            }),
            flight_time: pick(|w| {
                if let Lasting::GuidanceDisrupt { delay_percent, .. } = w {
                    Some(*delay_percent)
                } else {
                    None
                }
            }),
            explosion_velocity: pick(|w| {
                if let Lasting::GuidanceDisrupt { aoe_velocity_percent, .. } = w {
                    Some(*aoe_velocity_percent)
                } else {
                    None
                }
            }),
            explosion_radius: pick(|w| {
                if let Lasting::GuidanceDisrupt { aoe_cloud_percent, .. } = w {
                    Some(*aoe_cloud_percent)
                } else {
                    None
                }
            }),
        }
    }

    pub(crate) fn jammed(&self) -> bool {
        self.records.iter().any(|r| r.what == Lasting::Jam)
    }

    fn factor(&self, pick: impl Fn(&Lasting) -> Option<f64>) -> f64 {
        stacked(self.records.iter().filter_map(|r| pick(&r.what)))
    }

    /// The numbers of the ship with what lands on it worked into `base`.
    pub(crate) fn worked(&self, base: &Base) -> Base {
        Base {
            max_velocity: base.max_velocity
                * self.factor(|w| match w {
                    Lasting::Web { percent } | Lasting::Tackle { web_percent: percent, .. } => Some(*percent),
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
            sensor_strength: std::array::from_fn(|index| {
                let factor = self.factor(|w| match w {
                    Lasting::Damp {
                        gravimetric_percent,
                        ladar_percent,
                        magnetometric_percent,
                        radar_percent,
                        ..
                    } => Some(
                        [*gravimetric_percent, *ladar_percent, *magnetometric_percent, *radar_percent][index],
                    ),
                    _ => None,
                });
                base.sensor_strength[index] * factor
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
        self.hold_for(source, target, effect.module_id, what, effect.duration_ms, at)
    }

    /// What `hold` does for a ship that is not the target of the effect (an area's recipients): the module `module_id` of the ship of `source` lands `what` on
    /// the ship of `target` for `duration_ms`.
    pub(crate) fn hold_for(
        &mut self,
        source: SessionId,
        target: SessionId,
        module_id: i64,
        what: Lasting,
        duration_ms: f64,
        at: i64,
    ) -> Vec<Out> {
        let Some(source_ship) = self.pilots.get(&source).map(|pilot| pilot.ship.spec.ship_id) else {
            return Vec::new();
        };
        let until = at + (duration_ms * TICKS_PER_SECOND as f64 / 1000.0) as i64 + TICK;
        let Some(pilot) = self.pilots.get_mut(&target) else { return Vec::new() };
        if pilot.incoming.base.is_none() {
            let spec = &pilot.ship.spec;
            pilot.incoming.base = Some(Base {
                max_velocity: spec.max_velocity,
                signature_radius: spec.signature_radius,
                max_target_range: spec.max_target_range,
                scan_resolution: spec.scan_resolution,
                sensor_strength: spec.sensor_strength,
            });
        }
        pilot.incoming.upsert(source_ship, module_id, what, until);
        let mut out = self.retune(target, at);
        // a ship that is still lining up for a warp it can no longer make is stopped (the reference stops it as the scramble lands)
        if matches!(what, Lasting::Scramble { .. } | Lasting::Tackle { .. }) {
            if self
                .pilots
                .get(&target)
                .is_some_and(|pilot| pilot.incoming.scrambled_with(self.scramble_status(target)))
            {
                out.extend(self.break_tether(target, at));
            }
            out.extend(self.cancel_alignment(target, at));
        }
        // and a microwarpdrive that is running on it is stopped when the scrambler blocks one
        if matches!(what, Lasting::Scramble { blocks_mwd: true, .. }) {
            out.extend(self.stop_microwarpdrives(target, at));
        }
        out
    }

    /// A module stopped (or its target was lost): what it did to the ship it was aimed at is lifted.
    pub(crate) fn release(&mut self, source_ship: i64, module: i64, target_ship: i64, at: i64) -> Vec<Out> {
        let Some(target) = self.session_of(target_ship) else { return Vec::new() };
        let Some(pilot) = self.pilots.get_mut(&target) else { return Vec::new() };
        let before = pilot.incoming.records.len();
        let (lifted, kept): (Vec<Record>, Vec<Record>) = std::mem::take(&mut pilot.incoming.records)
            .into_iter()
            .partition(|r| r.source == source_ship && r.module == module);
        pilot.incoming.records = kept;
        if pilot.incoming.records.len() == before {
            return Vec::new();
        }
        let mut out = self.jams_lifted(target, &lifted);
        out.extend(self.retune(target, at));
        out
    }

    /// What no module renewed lapses (a module whose ship has left the scene lets go of its target by itself).
    pub(crate) fn expire_incoming(&mut self, session: SessionId, now: i64) -> Vec<Out> {
        let Some(pilot) = self.pilots.get_mut(&session) else { return Vec::new() };
        let before = pilot.incoming.records.len();
        let (kept, lapsed): (Vec<Record>, Vec<Record>) =
            std::mem::take(&mut pilot.incoming.records).into_iter().partition(|r| r.until > now);
        pilot.incoming.records = kept;
        if pilot.incoming.records.len() == before {
            return Vec::new();
        }
        let mut out = self.jams_lifted(session, &lapsed);
        out.extend(self.retune(session, now));
        out
    }

    /// The pilot of a ship an ECM jam has let go of is told it ended on its HUD (an NPC is told nothing).
    fn jams_lifted(&self, target: SessionId, lifted: &[Record]) -> Vec<Out> {
        if crate::npc::is_npc_session(target) {
            return Vec::new();
        }
        let Some(target_ship) = self.pilots.get(&target).map(|pilot| pilot.ship.spec.ship_id) else {
            return Vec::new();
        };
        let mut out = Vec::new();
        for record in lifted.iter().filter(|r| r.what == Lasting::Jam) {
            let who =
                || vec![id(record.source), id(record.module), id(target_ship), crate::wire::text(JAMMING)];
            out.push(Self::dogma_notify(target, "OnJamEnd", who()));
            out.push(Self::dogma_notify(target, "OnEwarEnd", who()));
            out.push(Self::dogma_notify(target, "OnRefreshBuffBar", Vec::new()));
        }
        out
    }

    /// A cycle of an ECM jammer begins: what the last cycle held is let go, and the target is jammed when the roll is under the chance (the jammer's strength for the kind of
    /// sensor the target has, over the strength of that sensor, weakened by the distance and by the target's resistance to ECM). A jammed ship loses every lock it has
    /// or is making but those on the ships that jam it, and is told for its HUD.
    pub(crate) fn jam(
        &mut self,
        source: SessionId,
        effect: &crate::modules::Effect,
        strength: &[f64; 4],
        share: f64,
        at: i64,
    ) -> Vec<Out> {
        let Some(target) = self.session_of(effect.target_id) else { return Vec::new() };
        let Some(source_ship) = self.pilots.get(&source).map(|pilot| pilot.ship.spec.ship_id) else {
            return Vec::new();
        };
        let Some(chance) = self.pilots.get(&target).map(|pilot| {
            jam_chance(strength, &pilot.ship.spec.sensor_strength, pilot.ship.spec.ecm_resistance, share)
        }) else {
            return Vec::new();
        };
        let jammed = rolls_under(self.rng.random(), chance);
        let until = at + (effect.duration_ms * TICKS_PER_SECOND as f64 / 1000.0) as i64 + TICK;
        let Some(pilot) = self.pilots.get_mut(&target) else { return Vec::new() };
        let before = pilot.incoming.records.len();
        let (previous, kept): (Vec<Record>, Vec<Record>) = std::mem::take(&mut pilot.incoming.records)
            .into_iter()
            .partition(|r| r.source == source_ship && r.module == effect.module_id && r.what == Lasting::Jam);
        pilot.incoming.records = kept;
        debug_assert!(pilot.incoming.records.len() + previous.len() == before);
        if !jammed {
            return self.jams_lifted(target, &previous);
        }
        pilot.incoming.upsert(source_ship, effect.module_id, Lasting::Jam, until);
        let allowed = pilot.incoming.jammers();
        let mut out = self.break_locks_except(target, &allowed, at);
        if !crate::npc::is_npc_session(target) {
            let who = || {
                vec![id(source_ship), id(effect.module_id), id(effect.target_id), crate::wire::text(JAMMING)]
            };
            let mut args = who();
            args.push(crate::wire::filetime(at));
            args.push(crate::wire::int(effect.duration_ms as i64 + 100));
            out.push(Self::dogma_notify(target, "OnJamStart", args));
            if previous.is_empty() {
                out.push(Self::dogma_notify(target, "OnEwarStart", who()));
                out.push(Self::dogma_notify(target, "OnRefreshBuffBar", Vec::new()));
            }
        }
        out
    }

    /// An ECM burst of a ship: each ship it reaches, but its own and a cloaked one, rolls its sensors and loses its locks when the roll lands. The host is told
    /// the ship that ran it and each ship reached with whether its roll jammed it (`Out::EcmBurst`; EVE.js `executeJammerBurstCycle` records the aggression
    /// on each reached ship before its roll). Nothing is rolled for a radius that is not a number above zero.
    pub(crate) fn jam_burst(
        &mut self,
        source: SessionId,
        strength: &[f64; 4],
        radius: f64,
        at: i64,
    ) -> Vec<Out> {
        if !radius.is_finite() || radius <= 0.0 {
            return Vec::new();
        }
        let Some(source_pilot) = self.pilots.get(&source) else { return Vec::new() };
        let source_ship_id = source_pilot.ship.spec.ship_id;
        let source_position =
            [source_pilot.ship.position.x, source_pilot.ship.position.y, source_pilot.ship.position.z];
        let source_radius = source_pilot.ship.spec.radius;
        let reached: Vec<SessionId> = self
            .burst_reach(source_position, source_radius, Some(source_ship_id), radius)
            .into_iter()
            .filter(|target| {
                !self.is_tethered_session(*target, at)
                    && self
                        .pilots
                        .get(target)
                        .is_some_and(|pilot| pilot.cloak != crate::visibility::CloakState::Cloaked)
            })
            .collect();
        let mut out = Vec::new();
        let mut told = Vec::with_capacity(reached.len());
        for target in reached {
            let chance = self.pilots.get(&target).map_or(0.0, |pilot| {
                jam_chance(strength, &pilot.ship.spec.sensor_strength, pilot.ship.spec.ecm_resistance, 1.0)
            });
            let jammed = rolls_under(self.rng.random(), chance);
            if jammed {
                out.extend(self.break_locks_except(target, &[], at));
            }
            if let Some(pilot) = self.pilots.get(&target) {
                told.push((pilot.ship.spec.ship_id, jammed));
            }
        }
        if !told.is_empty() {
            out.push(Out::EcmBurst { session: source, ship_id: source_ship_id, reached: told });
        }
        out
    }

    /// A burst of ECM from a point (a bomb that goes off): each ship within `radius` of its surface, but `exclude_ship`, rolls its sensors against `strength`
    /// and loses its locks when the roll lands. Nothing is rolled for a radius that is not a number above zero.
    pub(crate) fn jam_at(
        &mut self,
        centre: [f64; 3],
        centre_radius: f64,
        exclude_ship: Option<i64>,
        strength: &[f64; 4],
        radius: f64,
        at: i64,
    ) -> Vec<Out> {
        if !radius.is_finite() || radius <= 0.0 {
            return Vec::new();
        }
        let mut out = Vec::new();
        for target in self.burst_reach(centre, centre_radius, exclude_ship, radius) {
            if self.is_tethered_session(target, at) {
                continue;
            }
            let chance = self.pilots.get(&target).map_or(0.0, |pilot| {
                jam_chance(strength, &pilot.ship.spec.sensor_strength, pilot.ship.spec.ecm_resistance, 1.0)
            });
            if rolls_under(self.rng.random(), chance) {
                out.extend(self.break_locks_except(target, &[], at));
            }
        }
        out
    }

    /// The pilots within `radius` of the surface of a point (but `exclude_ship`), in the order they entered the scene: the order of the reference's dynamic
    /// entities, so the rolls come in the same order.
    fn burst_reach(
        &self,
        centre: [f64; 3],
        centre_radius: f64,
        exclude_ship: Option<i64>,
        radius: f64,
    ) -> Vec<SessionId> {
        let candidates = self.pilots.iter().map(|(target, pilot)| self.spatial_ship(*target, pilot));
        let mut targets: Vec<(u64, SessionId)> = crate::geo::SpatialQuery::surface_radius(
            centre,
            centre_radius,
            radius,
            1.0,
            crate::geo::SpatialFilter { exclude_id: exclude_ship, ..crate::geo::SpatialFilter::default() },
        )
        .execute(candidates)
        .into_iter()
        .filter_map(|hit| self.pilots.get(&hit.item).map(|pilot| (pilot.entered, hit.item)))
        .collect();
        targets.sort_unstable();
        targets.into_iter().map(|(_, target)| target).collect()
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
        set(SCAN_GRAVIMETRIC_STRENGTH_PERCENT, &mut spec.sensor_strength[0], worked.sensor_strength[0]);
        set(SCAN_LADAR_STRENGTH_PERCENT, &mut spec.sensor_strength[1], worked.sensor_strength[1]);
        set(SCAN_MAGNETOMETRIC_STRENGTH_PERCENT, &mut spec.sensor_strength[2], worked.sensor_strength[2]);
        set(SCAN_RADAR_STRENGTH_PERCENT, &mut spec.sensor_strength[3], worked.sensor_strength[3]);
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
mod tests;
