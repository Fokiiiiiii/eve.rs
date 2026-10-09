//! Modules that run: an effect of a fitted module (an afterburner, for now) is started, goes on cycle after cycle, costs capacitor at the start of
//! each cycle, changes the numbers of the ship while it runs, and is stopped at the end of the cycle it is in when the pilot asks.
//!
//! What an effect does to the ship is worked out by the host (dogma): it says, when the effect is started, what the ship's mass and top speed are
//! with and without it and which attributes the client is to be told changed. The scene keeps the time: the cycles, the capacitor and what the
//! client is sent at each moment, in the order of `oracle/fixtures/scenarios/module.json`. The capacitor recharges along CCP's curve and its charge
//! is told every half second while it is below the capacity, as the reference does.

use eve_marshal::Value;

use crate::health::Layer;
use crate::link::ChargeOp;
use crate::payload;
use crate::scene::{Out, Scene, SessionId, TICKS_PER_SECOND};
use crate::strike::{DISRUPTED_REMOTE_REPAIR, Strike};
use crate::targeting::{LockOrigin, Order};
use crate::weapons::{Spool, SpoolState, Turret};
use crate::wire::{dict, filetime, id, int, real, text, tuple, word};

/// The attribute of a ship that holds the capacitor's charge.
const CAPACITOR_CHARGE: i32 = 18;
/// The attribute of a module that holds the length of its cycle (told as a real, whatever its value).
const DURATION: i32 = 73;
/// The attribute of a module that holds the length of its cycle (`speed`).
const CYCLE: i32 = 51;
/// The capacitor's charge is told every half second, worked out at the tick a tenth of a second on from the moment (the reference's ticks).
pub(crate) const SAMPLE_EVERY: i64 = TICKS_PER_SECOND / 2;
pub(crate) const TICK: i64 = TICKS_PER_SECOND / 10;

/// The dogma effect of a microwarpdrive (`moduleBonusMicrowarpdrive`): what a warp scrambler that blocks one keeps from running.
pub(crate) const MICROWARPDRIVE: i32 = 6730;
/// A capacitor within this many GJ of full is full (the reference settles it so).
const FULL_SNAP: f64 = 1.0;

/// The numbers of a ship that a running effect changes.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Numbers {
    pub mass: f64,
    pub max_velocity: f64,
    pub signature_radius: f64,
}

/// A value the client is told an item now has.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Change {
    pub item_id: i64,
    pub attribute_id: i32,
    pub value: f64,
}

/// When in a cycle what an effect does to the scene is done: the repair of a shield booster is made as the cycle begins, that of an armor repairer as it ends
/// (`START_OF_CYCLE_EFFECTS` of the reference's `localCycleRuntime.js`).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Applies {
    CycleStart,
    CycleEnd,
}

/// How far an effect that is projected at a target reaches, from the target's surface to the ship's: whole up to `optimal`, then weaker through `falloff`
/// (`0.5 ^ (beyond / falloff)^2`, half at one falloff), and out of range past `optimal + falloff` (a module with no falloff reaches `optimal` and no more).
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Reach {
    pub optimal: f64,
    pub falloff: f64,
}

impl Reach {
    /// The share of a cycle's strength that lands on a target `apart` metres off (surface to surface), or None when it is out of range (the reference's
    /// `resolveAssistanceMultiplier` and `resolveHostileRangeMultiplier`: a metre of slack at each limit).
    pub fn share(&self, apart: f64) -> Option<f64> {
        let (optimal, falloff) = (self.optimal.max(0.0), self.falloff.max(0.0));
        if apart <= optimal + 1.0 {
            return Some(1.0);
        }
        let beyond = apart - optimal;
        if falloff <= 0.0 || beyond > falloff + 1.0 {
            return None;
        }
        let share = 0.5_f64.powf((beyond / falloff.max(1.0)).powi(2));
        Some((share * 1e6).round() / 1e6)
    }

    /// The farthest a target may be for the effect to reach it (what the client is told when a module is asked to reach too far).
    pub fn farthest(&self) -> f64 {
        self.optimal.max(0.0) + self.falloff.max(0.0)
    }
}

/// What a module that is projected at another ship does to it each cycle.
#[derive(Debug, Clone, PartialEq)]
pub enum Remote {
    /// A layer of the target is mended by `amount` points (a remote shield booster, armor repairer or hull repairer).
    Repair { layer: Layer, amount: f64 },
    /// The host settles one target-specific cycle and returns its spooled repair amount.
    MutadaptiveRepair { layer: Layer, amount: f64, bonus_per_cycle: f64, bonus_max: f64 },
    /// The target's capacitor is given `amount` GJ (a remote capacitor transmitter).
    Transfer { amount: f64 },
    /// `amount` GJ are taken from the target's capacitor (an energy neutralizer); one that is built for bigger ships is weaker against a target whose signature
    /// is smaller than its `signature_resolution` (in proportion, 0 for none).
    Neutralize { amount: f64, signature_resolution: f64 },
    /// `amount` GJ are taken from the target's capacitor and given to the ship that runs the effect, which must hold less than the target (an energy nosferatu).
    Drain { amount: f64, signature_resolution: f64 },
    /// The target's top speed is changed by `percent` of itself for as long as the effect lands (a stasis webifier: negative).
    Web { percent: f64 },
    /// The target's signature radius is changed by `percent` (a target painter: positive).
    Paint { percent: f64 },
    /// The target's lock range, scan resolution, and four sensor strengths are changed by these percentages.
    /// Sensor boosts are positive; dampeners reduce lock range and scan resolution.
    Damp {
        range_percent: f64,
        resolution_percent: f64,
        gravimetric_percent: f64,
        ladar_percent: f64,
        magnetometric_percent: f64,
        radar_percent: f64,
    },
    /// The turrets the target fires are changed by those percentages of their optimal range, falloff and tracking speed for as long as the effect lands (a tracking
    /// disruptor: negative).
    Disrupt { range_percent: f64, falloff_percent: f64, tracking_percent: f64 },
    /// The missiles the target launches are changed by those percentages of their speed, flight time, explosion velocity and explosion radius for as long as the effect
    /// lands (a guidance disruptor: negative, but for the radius).
    GuidanceDisrupt {
        velocity_percent: f64,
        delay_percent: f64,
        aoe_velocity_percent: f64,
        aoe_cloud_percent: f64,
    },
    /// The target is held out of warp: `strength` points of scramble (a warp scrambler 2, a warp disruptor 1; the ship's own warp core subtracts). `blocks_mwd`: and out
    /// of its microwarpdrive.
    Scramble { strength: f64, blocks_mwd: bool },
    /// A fighter's tackle: a warp scramble of `strength` points and a web of `web_percent` of the target's top speed, both for as long as the effect lands
    /// (one remote for both, so one module runs it: ADR 0015, FIG-D-3).
    Tackle { strength: f64, web_percent: f64 },
    /// The target's locks are broken and it cannot lock anything but the ships that jam it, for as long as a cycle, when `strength` for the kind of sensor the target has
    /// (gravimetric, ladar, magnetometric, radar) beats the roll against that sensor's strength (an ECM jammer).
    Jam { strength: [f64; 4] },
}

impl Remote {
    pub fn hostile(&self) -> bool {
        match self {
            Self::Repair { .. } | Self::MutadaptiveRepair { .. } | Self::Transfer { .. } => false,
            Self::Damp {
                range_percent,
                resolution_percent,
                gravimetric_percent,
                ladar_percent,
                magnetometric_percent,
                radar_percent,
            } => ![
                *range_percent,
                *resolution_percent,
                *gravimetric_percent,
                *ladar_percent,
                *magnetometric_percent,
                *radar_percent,
            ]
            .into_iter()
            .all(|percent| percent >= 0.0),
            _ => true,
        }
    }

    pub(crate) fn lasting(&self, share: f64) -> Option<crate::ewar::Lasting> {
        use crate::ewar::Lasting;
        match *self {
            Self::Web { percent } => Some(Lasting::Web { percent: percent * share }),
            Self::Paint { percent } => Some(Lasting::Paint { percent: percent * share }),
            Self::Damp {
                range_percent,
                resolution_percent,
                gravimetric_percent,
                ladar_percent,
                magnetometric_percent,
                radar_percent,
            } => Some(Lasting::Damp {
                range_percent: range_percent * share,
                resolution_percent: resolution_percent * share,
                gravimetric_percent: gravimetric_percent * share,
                ladar_percent: ladar_percent * share,
                magnetometric_percent: magnetometric_percent * share,
                radar_percent: radar_percent * share,
            }),
            Self::Scramble { strength, blocks_mwd } => {
                Some(Lasting::Scramble { strength: strength * share, blocks_mwd })
            }
            Self::Tackle { strength, web_percent } => {
                Some(Lasting::Tackle { strength: strength * share, web_percent: web_percent * share })
            }
            Self::Disrupt { range_percent, falloff_percent, tracking_percent } => Some(Lasting::Disrupt {
                range_percent: range_percent * share,
                falloff_percent: falloff_percent * share,
                tracking_percent: tracking_percent * share,
            }),
            Self::GuidanceDisrupt {
                velocity_percent,
                delay_percent,
                aoe_velocity_percent,
                aoe_cloud_percent,
            } => Some(Lasting::GuidanceDisrupt {
                velocity_percent: velocity_percent * share,
                delay_percent: delay_percent * share,
                aoe_velocity_percent: aoe_velocity_percent * share,
                aoe_cloud_percent: aoe_cloud_percent * share,
            }),
            _ => None,
        }
    }
}

impl Action {
    fn charge_use(&self) -> Option<(i32, i32, i64, i64)> {
        match self {
            Self::Charged { charge_type_id, charge_flag, charge_per_cycle, charges, .. } => {
                Some((*charge_type_id, *charge_flag, *charge_per_cycle, *charges))
            }
            _ => None,
        }
    }

    fn with_charges(&self, charged_this_cycle: bool) -> Option<&Action> {
        match self {
            Self::Charged { action, fallback, .. } => {
                if charged_this_cycle {
                    Some(action)
                } else {
                    fallback.as_deref()
                }
            }
            _ => Some(self),
        }
    }

    fn offensive(&self) -> bool {
        match self {
            Self::Remote { what, .. } => what.hostile(),
            Self::Smartbomb { .. } | Self::EcmBurst { .. } | Self::Strike { .. } | Self::Field { .. } | Self::Doomsday { .. } => true,
            Self::Cloak | Self::MicroJumpDrive { .. } | Self::ResistanceShift { .. } => false,
            Self::Charged { action, fallback, .. } => {
                action.offensive() || fallback.as_deref().is_some_and(Action::offensive)
            }
            Self::Repair { .. } => false,
            Self::Mining { .. } | Self::Salvage { .. } | Self::Tractor { .. } | Self::PassiveLock | Self::AutoTarget { .. } => false,
        }
    }

    /// Whether the simulation does not run the action yet (its package has not landed): such an activation is refused, never run as a no-op.
    fn not_implemented(&self) -> bool {
        match self {
            Self::Charged { action, fallback, .. } => {
                action.not_implemented() || fallback.as_deref().is_some_and(Action::not_implemented)
            }
            _ => false,
        }
    }

    /// Whether the cycle hands its outcome to the host (a work cycle, ADR 0013).
    fn is_work(&self) -> bool {
        matches!(self, Self::Mining { .. } | Self::Salvage { .. })
    }
}

/// The work command of a mining cycle (the drone command `MineRepeatedly`).
pub(crate) const WORK_MINE: u8 = 7;
/// The work command of a salvage cycle (the drone command `Salvage`).
pub(crate) const WORK_SALVAGE: u8 = 8;
/// The fuel of the next cycle of a module that burns fuel (the wire's `SimModuleWorkCommands.ConsumeFuel`): the host takes it from the ship, or refuses.
pub(crate) const WORK_FUEL: u8 = 9;
/// How long a parked work cycle waits before the same `cycle_id` is sent again (2 s of the simulation clock; DEVIATIONS.md, ADR 0013 rule 3).
pub(crate) const WORK_RESEND_TICKS: i64 = 2 * TICKS_PER_SECOND;

/// The ids of work cycles are drawn from one counter of the process, seeded from the clock, so an id is never sent twice by one run of the simulation
/// and never by a later run either: the host keeps the last cycle it settled for each owner, and a repeated id is answered from that record.
pub(crate) fn next_work_cycle_id() -> u64 {
    static NEXT: std::sync::LazyLock<std::sync::atomic::AtomicU64> = std::sync::LazyLock::new(|| {
        let seconds = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .map_or(1, |elapsed| elapsed.as_secs());
        // the top bit stays clear: the host stores the id as a signed 64-bit number
        std::sync::atomic::AtomicU64::new((seconds & 0x7FFF_FFFF) << 32)
    });
    NEXT.fetch_add(1, std::sync::atomic::Ordering::Relaxed) + 1
}

/// What a cycle of an effect does to the scene besides what it costs the capacitor and the numbers it changes.
#[derive(Debug, Clone, PartialEq)]
pub enum Action {
    Repair {
        layer: Layer,
        amount: f64,
        when: Applies,
    },
    Remote {
        what: Remote,
        reach: Reach,
        when: Applies,
        jam: String,
    },
    Smartbomb {
        range: f64,
        damage: crate::health::Damage,
        hurts_pilots: bool,
    },
    EcmBurst {
        range: f64,
        strength: [f64; 4],
    },
    Cloak,
    MicroJumpDrive {
        distance_meters: f64,
        cooldown_ms: i64,
    },
    Charged {
        action: Box<Action>,
        fallback: Option<Box<Action>>,
        charge_type_id: i32,
        charge_flag: i32,
        charge_per_cycle: i64,
        charges: i64,
    },
    /// A reactive armor hardener: the armor's resonances start at `seed` (the module's own values for em, thermal, kinetic and explosive) and move each
    /// cycle as the damage the armor took says (`crate::resistance_shift`), by `shift` of a resonance at most. The client is told the armor's resonances
    /// as the attributes `attribute_ids` (in the same order) when they change.
    ResistanceShift {
        seed: crate::health::Damage,
        shift: f64,
        attribute_ids: [i32; 4],
    },
    /// A mining laser's cycle (tag 7). At the end of each cycle the simulation sends the host the cycle's numbers and three rolls (waste, rounding,
    /// crit) as a work cycle and parks the module until the host answers (ADR 0013): the host decides the yield, the simulation decides nothing.
    Mining {
        family: MiningFamily,
        mining_amount_m3: f64,
        waste_multiplier: f64,
        waste_probability: f64,
        crit_probability: f64,
        crit_multiplier: f64,
    },
    /// A salvager's cycle (tag 8): as a mining cycle, with the wreck's access chance moved by `access_bonus` (percent) and one roll.
    Salvage { access_bonus: f64 },
    /// A tractor beam (tag 9, protocol 30): it pulls the wreck or can it is locked on, at `velocity` metres a second, until it is `hold` metres off the
    /// ship's surface. `range` is the module's reach (`maxRange`): a target further off is out of reach, and the pull ends when it is out of reach
    /// at a cycle's end.
    Tractor { velocity: f64, hold: f64, range: f64 },
    /// A passive lock (tag 10): when its cycle starts the target is locked with its notice suppressed (see `passive_lock`).
    PassiveLock,
    /// An auto-target sweep (tag 11): at each cycle start the ships that target the pilot within `range` are locked (see `auto_target_sweep`).
    AutoTarget { range: f64 },
    /// A doomsday's strike (tag 13, ADR 0013 epoch 25): the area in front of the ship is hit after a warning, pulse by pulse, and the ship is held for its windows.
    Strike { strike: Strike },
    /// A titan doomsday (tag 14, ADR 0013 epoch 26): the locked target is hit once, `delay_ms` after the cycle starts, with `damage` (the four kinds, not scaled
    /// by a signature). The ship is held for its windows from the start, as a strike's.
    Doomsday { delay_ms: f64, damage: crate::health::Damage, immobile_ms: f64, no_jump_ms: f64 },
    /// A gravitational transport field (the Gravitational Transportation Field Oscillator, tag 15, protocol 29): the strike's sphere and windows; at its
    /// pulse, every ship of `moved` inside the sphere is carried to `destination` (an offset from the firing ship's centre), and every ship of `held` inside
    /// it cannot warp for `warp_block_ms`. The ship ids are the host's: the simulation only tests their geometry.
    Field { strike: Strike, destination: [f64; 3], warp_block_ms: f64, moved: Vec<i64>, held: Vec<i64> },
}

/// The strike an action runs, if it runs one (a cone, a cylinder or a sphere; the transport field is a strike too).
fn strike_of(action: Option<&Action>) -> Option<Strike> {
    match action? {
        Action::Strike { strike } | Action::Field { strike, .. } => Some(*strike),
        _ => None,
    }
}

/// What a mining laser mines (the wire byte: 0 ore, 1 ice, 2 gas).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum MiningFamily {
    Ore,
    Ice,
    Gas,
}

/// An effect the host asks the scene to run, with everything the host has worked out for it.
#[derive(Debug, Clone, PartialEq)]
pub struct Effect {
    pub module_id: i64,
    pub module_type: i32,
    pub effect_id: i32,
    /// What the destiny stream calls the effect (`effects.Afterburner`).
    pub fx: String,
    pub duration_ms: f64,
    /// The capacitor a cycle takes, taken at its start.
    pub capacitor_need: f64,
    /// How many cycles the pilot asked for.
    pub repeat: i32,
    pub target_id: i64,
    /// A turret's numbers when the effect is a weapon that shoots at the target each cycle (`crate::weapons`).
    pub turret: Option<Turret>,
    /// What a cycle does to the scene besides what it costs and the numbers it changes (a repair).
    pub action: Option<Action>,
    /// The ship's numbers while the effect runs and when it does not (nothing changes when the effect does not touch them).
    pub on: Option<Numbers>,
    pub off: Option<Numbers>,
    /// What the client is told when the effect starts, and when it ends.
    pub on_changes: Vec<Change>,
    pub off_changes: Vec<Change>,
    /// The effect burns the fuel its consumption attributes name each cycle: a fuel cycle is asked of the host before each cycle that starts, and the cycle
    /// does not start when the host refuses it (`ConsumeFuel`, the host takes the fuel from the ship).
    pub burns_fuel: bool,
}

impl Effect {
    pub(crate) fn remote(&self) -> Option<(&Remote, &Reach, Applies)> {
        fn from(action: &Action) -> Option<(&Remote, &Reach, Applies)> {
            match action {
                Action::Remote { what, reach, when, .. } => Some((what, reach, *when)),
                Action::Charged { action, fallback, .. } => {
                    from(action).or_else(|| fallback.as_deref().and_then(from))
                }
                _ => None,
            }
        }
        from(self.action.as_ref()?)
    }

    pub(crate) fn jam(&self) -> Option<&str> {
        fn from(action: &Action) -> Option<&str> {
            match action {
                Action::Remote { what: Remote::Jam { .. }, .. } => None,
                Action::Remote { jam, .. } if !jam.is_empty() => Some(jam),
                Action::Charged { action, fallback, .. } => {
                    from(action).or_else(|| fallback.as_deref().and_then(from))
                }
                _ => None,
            }
        }
        from(self.action.as_ref()?)
    }

    pub(crate) fn offensive(&self) -> bool {
        self.turret.is_some() || self.action.as_ref().is_some_and(Action::offensive)
    }

    /// How many cycles the client is told the effect plays: a turret's are the ones asked for, those of a projected effect last as long as the effect can (the
    /// reference's `resolvePersistentRepeat`: twelve hours of cycles, so the beam is not drawn for one cycle only).
    fn fx_repeat(&self) -> Option<i64> {
        if self.turret.is_some() {
            Some(i64::from(self.repeat))
        } else if self.remote().is_some() {
            Some(((12.0 * 3_600_000.0) / self.duration_ms.max(1.0)).ceil().max(1.0) as i64)
        } else {
            None
        }
    }
}

/// The charge of a ship's capacitor as a function of time: `C1 / Cmax = (1 + (sqrt(C0 / Cmax) - 1) * e^(-5 * dt / T))^2`, `T` the recharge time.
pub fn recharged(charge: f64, capacity: f64, recharge_s: f64, seconds: f64) -> f64 {
    if capacity <= 0.0 || charge >= capacity || seconds <= 0.0 || recharge_s <= 0.0 {
        return charge.min(capacity.max(0.0));
    }
    let root = 1.0 + ((charge.max(0.0) / capacity).sqrt() - 1.0) * (-5.0 * seconds / recharge_s).exp();
    (capacity * root * root).min(capacity)
}

/// A ship's capacitor.
#[derive(Debug, Clone, PartialEq)]
pub(crate) struct Capacitor {
    capacity: f64,
    recharge_s: f64,
    /// The charge at `at`.
    charge: f64,
    at: i64,
    /// When the charge is next told, while it is below the capacity.
    next_sample: Option<i64>,
}

impl Capacitor {
    /// A capacitor that holds `ratio` of what it can (its charge is told from the start when it is below full).
    pub(crate) fn filled(capacity: f64, recharge_s: f64, ratio: f64, now: i64) -> Self {
        let ratio = ratio.clamp(0.0, 1.0);
        Self {
            capacity,
            recharge_s,
            charge: capacity * ratio,
            at: now,
            next_sample: (ratio < 1.0).then_some(now),
        }
    }

    /// How full it is at `now`, as a fraction (a ship with no capacitor is full).
    pub(crate) fn ratio_at(&self, now: i64) -> f64 {
        if self.capacity <= 0.0 { 1.0 } else { (self.charge_at(now) / self.capacity).clamp(0.0, 1.0) }
    }

    /// Fills it at `now` and returns the charge before and after.
    pub(crate) fn refill(&mut self, now: i64) -> (f64, f64) {
        let before = self.charge_at(now);
        self.charge = self.capacity;
        self.at = now;
        self.next_sample = None;
        (before, self.capacity)
    }

    pub(crate) fn charge_at(&self, now: i64) -> f64 {
        let charge = recharged(
            self.charge,
            self.capacity,
            self.recharge_s,
            (now - self.at) as f64 / TICKS_PER_SECOND as f64,
        );
        if self.capacity - charge <= FULL_SNAP { self.capacity } else { charge }
    }

    /// Adds `amount` at `now` (the charge never goes above the capacity) and returns the charge before and after.
    fn give(&mut self, now: i64, amount: f64) -> (f64, f64) {
        let before = self.charge_at(now);
        let after = (before + amount.max(0.0)).min(self.capacity);
        self.charge = after;
        self.at = now;
        self.next_sample = Some(now + TICK);
        (before, after)
    }

    /// Takes `amount` at `now` and returns the charge before and after.
    fn draw(&mut self, now: i64, amount: f64) -> (f64, f64) {
        let before = self.charge_at(now);
        let after = (before - amount).max(0.0);
        self.charge = after;
        self.at = now;
        // the charge is told at once and then at the ticks a tenth of a second past each half second
        self.next_sample = Some(now + TICK);
        (before, after)
    }
}

/// How an effect came to its end.
#[derive(Clone, Copy, PartialEq, Eq)]
pub(crate) enum Ending {
    /// The pilot asked, or the cycles were done, or the capacitor or the target was not there for another.
    Done,
    /// The target was lost: the weapon stops at once.
    TargetLost,
    /// Something forbids the effect while it runs (a warp scrambler on a microwarpdrive): it stops at once, as when the target is lost.
    Forced,
    /// The charge is spent: the client is told there is none.
    NoRounds,
    /// The target was not there at the end of a cycle: the client is told as for `Done`, the host as for a lost target.
    TargetGone,
    /// The host refused the work cycle that ended the effect's cycle: the client is told as for `Done`.
    WorkRefused,
    /// The fuel of the next cycle was not there (the host refused it): the client is told as for `Done`.
    NoFuel,
}

impl Ending {
    /// The reason the host is sent (`ModuleEnded`, ADR 0013 and FP-C1): 1 the cycles were done, the capacitor was short, or the pilot asked; 2 the target was
    /// lost; 3 something forbade the effect; 4 the charge was spent; 5 the host refused a work cycle; 6 the fuel of the next cycle was not there.
    pub(crate) fn wire_reason(self) -> u8 {
        match self {
            Self::Done => 1,
            Self::TargetLost | Self::TargetGone => 2,
            Self::Forced => 3,
            Self::NoRounds => 4,
            Self::WorkRefused => 5,
            Self::NoFuel => 6,
        }
    }
}

/// A work cycle sent to the host and not answered: `at` is when its cycle ended, `sent_at` when it was last sent (the same id is sent again after
/// `WORK_RESEND_TICKS`).
#[derive(Debug, Clone, Copy, PartialEq)]
struct PendingWorkCycle {
    cycle_id: u64,
    at: i64,
    sent_at: i64,
    /// The waste, rounding and crit rolls the cycle was sent with (drawn once, so a repeat carries the same ones).
    rolls: [f64; 3],
    /// Whether this is the fuel of the next cycle (`WORK_FUEL`), not a mining or salvage cycle.
    fuel: bool,
}

/// An effect that burns fuel and waits for the host to take the fuel of its first cycle (`WORK_FUEL`): it starts when the host accepts, and is refused
/// when the host refuses. `sent_at` is when the fuel was last asked for (the same id is asked again after `WORK_RESEND_TICKS`).
#[derive(Debug, Clone, PartialEq)]
struct PendingStart {
    effect: Effect,
    cycle_id: u64,
    sent_at: i64,
}

#[derive(Debug, Clone, Copy, PartialEq)]
struct PendingRemoteRepairCycle {
    cycle_id: u64,
    at: i64,
    range_share: f64,
}

/// An effect that is running.
#[derive(Debug, Clone, PartialEq)]
pub(crate) struct Running {
    effect: Effect,
    /// When the cycle that is on began.
    cycle_start: i64,
    cycles: i32,
    /// The pulses of a strike the cycle has made (the next one is due at its `pulse_offset_ms`).
    strike_done: i32,
    /// Whether a titan doomsday has hit its target in the cycle that is on.
    doomsday_hit: bool,
    /// The pilot asked for it to stop: it does when the cycle ends.
    stopping: bool,
    /// The rounds a turret or charge-using module has left.
    rounds: Option<i64>,
    /// Whether the cycle in progress started with a charge available.
    charged_this_cycle: bool,
    pending_remote_repair: Option<PendingRemoteRepairCycle>,
    /// The work cycle the host has not answered yet: the module is parked on it (no cycle is timed) until the answer, a stop or the loss of the target.
    pending_work: Option<PendingWorkCycle>,
    /// The damage ramp a precursor weapon has built on its target; it ends with the effect, so a new one starts from none.
    spool: SpoolState,
}

/// The modules of a ship that run, and its capacitor.
#[derive(Debug, Clone, Default)]
pub(crate) struct Modules {
    pub(crate) capacitor: Option<Capacitor>,
    running: Vec<Running>,
    mjd_cooldowns: Vec<(i64, i64)>,
    next_remote_repair_cycle_id: u64,
    /// Fuel-burning effects that wait for the host to take the fuel of their first cycle.
    pending_starts: Vec<PendingStart>,
}

impl Modules {
    fn allocate_remote_repair_cycle_id(&mut self) -> u64 {
        self.next_remote_repair_cycle_id = self.next_remote_repair_cycle_id.wrapping_add(1).max(1);
        self.next_remote_repair_cycle_id
    }
}

impl Modules {
    /// The modules of a ship whose capacitor holds `ratio` of what it can.
    pub(crate) fn with_charge(capacity: f64, recharge_s: f64, ratio: f64, now: i64) -> Self {
        Self {
            capacitor: Some(Capacitor::filled(capacity, recharge_s, ratio, now)),
            running: Vec::new(),
            mjd_cooldowns: Vec::new(),
            next_remote_repair_cycle_id: 0,
            pending_starts: Vec::new(),
        }
    }

    /// How many effects run.
    #[cfg(test)]
    pub(crate) fn running_count(&self) -> usize {
        self.running.len()
    }

    /// The effects that run, with the moment the cycle each is in began.
    pub(crate) fn running_effects(&self) -> impl Iterator<Item = (&Effect, i64)> {
        self.running.iter().map(|run| (&run.effect, run.cycle_start))
    }

    pub(crate) fn is_mjd_spooling(&self) -> bool {
        self.running
            .iter()
            .any(|run| matches!(run.effect.action.as_ref(), Some(Action::MicroJumpDrive { .. })))
    }

    pub(crate) fn is_idle(&self) -> bool {
        self.running.is_empty()
            && self.pending_starts.is_empty()
            && self.capacitor.as_ref().is_none_or(|c| c.next_sample.is_none())
    }
}

pub(crate) fn number(value: f64) -> Value {
    if value.fract() == 0.0 && value.abs() < 9.0e15 { int(value as i64) } else { real(value) }
}

/// How the client knows a charge in a slot: the ship, the slot's flag and the type of the charge.
fn charge_key(ship: i64, charge: &crate::weapons::Charge) -> Value {
    tuple([id(ship), int(i64::from(charge.flag)), int(i64::from(charge.type_id))])
}

/// What the client is told when a turret stops for want of rounds (`NoCharges`, naming the launcher by its type).
fn no_charges(module_type: i32) -> Value {
    tuple([text("NoCharges"), dict([("launcher", tuple([int(4), int(i64::from(module_type))]))])])
}

/// One line of an `OnModuleAttributeChanges`: `(name, owner, item, attribute, when, new value, old value, when)`.
/// The armor resonances that changed between two states, as `(attribute, new, old)` (each rounded as it is told). `attribute_ids` are in the order of the
/// damage vector, as the resonances are.
pub(crate) fn armor_changes(
    attribute_ids: &[i32; 4],
    before: &crate::health::Damage,
    after: &crate::health::Damage,
) -> Vec<(i32, f64, f64)> {
    (0..4)
        .filter_map(|i| {
            let (new, old) = (crate::health::round6(after[i]), crate::health::round6(before[i]));
            (new != old).then_some((attribute_ids[i], new, old))
        })
        .collect()
}

pub(crate) fn attribute_change(
    owner: i64,
    item: i64,
    attribute: i32,
    when: i64,
    value: Value,
    old: Value,
) -> Value {
    attribute_change_of(owner, id(item), attribute, when, value, old)
}

/// The same for an item the client knows by a key of its own (a charge in a slot: the ship, the slot's flag and the type).
pub(crate) fn attribute_change_of(
    owner: i64,
    item: Value,
    attribute: i32,
    when: i64,
    value: Value,
    old: Value,
) -> Value {
    tuple([
        word("OnModuleAttributeChange"),
        id(owner),
        item,
        int(i64::from(attribute)),
        filetime(when),
        value,
        old,
        filetime(when),
    ])
}

impl Scene {
    pub(crate) fn dogma_notify(session: SessionId, name: &'static str, args: Vec<Value>) -> Out {
        Out::Notify { session, name, id_type: "clientID", args }
    }

    /// The capacitor's charge, told: `(the ship's charge now, and a tenth of a second before)`.
    pub(crate) fn charge_told(
        session: SessionId,
        owner: i64,
        ship: i64,
        when: i64,
        new: f64,
        old: f64,
    ) -> Out {
        Self::dogma_notify(
            session,
            "OnModuleAttributeChanges",
            vec![Value::List(vec![attribute_change(
                owner,
                ship,
                CAPACITOR_CHARGE,
                when,
                number(new),
                number(old),
            )])],
        )
    }

    /// The values an effect changes, told to the pilot. A launcher's cycle is told as a real whatever its value (the recording of the reference's launcher: `4900.0`).
    fn changes_told(
        session: SessionId,
        owner: i64,
        effect: &Effect,
        changes: &[Change],
        when: i64,
    ) -> Option<Out> {
        let launcher = effect.turret.as_ref().is_some_and(|turret| turret.missile.is_some());
        if changes.is_empty() {
            return None;
        }
        let lines: Vec<Value> = changes
            .iter()
            .map(|c| {
                let value = if c.attribute_id == DURATION || (launcher && c.attribute_id == CYCLE) {
                    real(c.value)
                } else {
                    number(c.value)
                };
                attribute_change(owner, c.item_id, c.attribute_id, when, value, Value::None)
            })
            .collect();
        Some(Self::dogma_notify(session, "OnModuleAttributeChanges", vec![Value::List(lines)]))
    }

    /// `OnGodmaShipEffect`: an effect went on (`start` and `active`) or came to an end, or began another cycle.
    #[allow(clippy::too_many_arguments)] // the arguments are the fields of the tuple the client reads
    fn effect_told(
        session: SessionId,
        owner: i64,
        ship: i64,
        run: &Running,
        when: i64,
        on: bool,
        started: i64,
        error: Option<Value>,
    ) -> Out {
        let effect = &run.effect;
        let environment = tuple([
            id(effect.module_id),
            id(owner),
            id(ship),
            if effect.target_id > 0 { id(effect.target_id) } else { Value::None },
            effect.turret.as_ref().and_then(|t| t.charge).map_or(Value::None, |c| charge_key(ship, &c)),
            tuple([]),
            int(i64::from(effect.effect_id)),
            Value::None,
        ]);
        Self::dogma_notify(
            session,
            "OnGodmaShipEffect",
            vec![
                id(effect.module_id),
                int(i64::from(effect.effect_id)),
                filetime(when),
                int(i64::from(on)),
                int(i64::from(on)),
                environment,
                filetime(started),
                real(effect.duration_ms),
                // an effect that ends for want of rounds says so (and says it repeats)
                if on { int(i64::from(run.effect.repeat)) } else { Value::Bool(error.is_some()) },
                error.unwrap_or(Value::None),
            ],
        )
    }

    /// The action the client plays on the ship for the effect: `OnSpecialFX(ship, module, module type, target, charge, effect, offensive, start, active, duration, …)`.
    fn fx_action(stamp: u32, ship: &Value, effect: &Effect, on: bool) -> Value {
        payload::action(
            stamp,
            "OnSpecialFX",
            [
                ship.clone(),
                id(effect.module_id),
                int(i64::from(effect.module_type)),
                if effect.target_id > 0 { id(effect.target_id) } else { Value::None },
                effect
                    .turret
                    .as_ref()
                    .and_then(|t| t.charge)
                    .map_or(Value::None, |c| int(i64::from(c.type_id))),
                crate::wire::text(effect.fx.clone()),
                int(i64::from(if on { effect.offensive() } else { effect.turret.is_some() })),
                int(i64::from(on)),
                int(i64::from(on)),
                number(effect.duration_ms),
                // a turret's start says how many cycles were asked for, its stop says nothing (the recording of the reference's gun)
                if on { effect.fx_repeat().map_or(Value::None, int) } else { Value::None },
                Value::None,
                int(0),
                Value::None,
            ],
        )
    }

    /// The ship's numbers become `numbers`; what the client is told: `SetBallMass` and `SetMaxSpeed` (the ones that changed).
    fn renumber(&mut self, session: SessionId, numbers: Numbers, stamp: u32) -> Vec<Value> {
        let Some(pilot) = self.pilots.get_mut(&session) else { return Vec::new() };
        // what other ships' modules do to this ship is worked on the numbers its own effects give it
        let held = pilot.incoming.base.map(|mut base| {
            base.max_velocity = numbers.max_velocity;
            base.signature_radius = numbers.signature_radius;
            pilot.incoming.base = Some(base);
            pilot.incoming.worked(&base)
        });
        let wanted_velocity = held.map_or(numbers.max_velocity, |worked| worked.max_velocity);
        let wanted_signature = held.map_or(numbers.signature_radius, |worked| worked.signature_radius);
        let spec = &mut pilot.ship.spec;
        let ship = id(spec.ship_id);
        let mut actions = Vec::new();
        if numbers.mass != spec.mass {
            spec.mass = numbers.mass;
            actions.push(payload::action(stamp, "SetBallMass", [ship.clone(), real(numbers.mass)]));
        }
        if wanted_velocity != spec.max_velocity {
            spec.max_velocity = wanted_velocity;
            actions.push(payload::action(stamp, "SetMaxSpeed", [ship, real(wanted_velocity)]));
        }
        spec.signature_radius = wanted_signature;
        actions
    }

    /// The host asks for an effect to run. Answered with `Accepted` (code 17) or `Refused`: `ALREADY_ACTIVE`, `NOT_ENOUGH_CAPACITOR`.
    pub fn activate(&mut self, session: SessionId, effect: Effect, now: i64) -> Vec<Out> {
        const CODE: u8 = 17;
        if !self.pilots.contains_key(&session) {
            return vec![Out::Refused { session, command: CODE, reason: "NO_SHIP_IN_SPACE" }];
        }
        if !effect.duration_ms.is_finite() || effect.duration_ms < 1.0 || effect.repeat < 1 {
            return vec![Out::Refused { session, command: CODE, reason: "BAD_EFFECT" }];
        }
        if effect.action.as_ref().is_some_and(Action::not_implemented) {
            return vec![Out::Refused { session, command: CODE, reason: "NOT_IMPLEMENTED" }];
        }
        self.settle_all(now);
        let pilot = &self.pilots[&session];
        if pilot.modules.running.iter().any(|r| r.effect.module_id == effect.module_id)
            || pilot.modules.pending_starts.iter().any(|start| start.effect.module_id == effect.module_id)
        {
            return vec![Out::Refused { session, command: CODE, reason: "ALREADY_ACTIVE" }];
        }
        // the reactive armor hardeners are one group, of which one runs at a time: a second one is refused (its resonances would not compose)
        if matches!(effect.action, Some(Action::ResistanceShift { .. }))
            && pilot.modules.running.iter().any(|r| matches!(r.effect.action, Some(Action::ResistanceShift { .. })))
        {
            return vec![Out::Refused { session, command: CODE, reason: "MAX_GROUP_ACTIVE" }];
        }
        if let Some(Action::MicroJumpDrive { distance_meters, cooldown_ms }) = effect.action.as_ref() {
            if !distance_meters.is_finite()
                || *distance_meters <= 0.0
                || *cooldown_ms <= 0
                || effect.repeat != 1
            {
                return vec![Out::Refused { session, command: CODE, reason: "BAD_MJD" }];
            }
            if pilot
                .modules
                .mjd_cooldowns
                .iter()
                .any(|(module_id, until)| *module_id == effect.module_id && *until > now)
            {
                return vec![Out::Refused { session, command: CODE, reason: "MJD_COOLDOWN" }];
            }
        }
        // a smartbomb that the safety forbids does not start with another pilot in its range
        if let Some(Action::Smartbomb { range, hurts_pilots: false, .. }) = effect.action.as_ref()
            && !self
                .in_smartbomb_range(session, *range)
                .iter()
                .all(|(other, _)| crate::npc::is_npc_session(*other))
        {
            return vec![Out::Refused { session, command: CODE, reason: "SafetyActivated" }];
        }
        // a warp scrambler that blocks a microwarpdrive keeps one from being started
        if effect.effect_id == MICROWARPDRIVE && pilot.incoming.mwd_blocked() {
            return vec![Out::Refused { session, command: CODE, reason: "MICROWARPDRIVE_BLOCKED" }];
        }
        if let Some(Action::Strike { strike }) = effect.action.as_ref() {
            if !strike.is_valid() {
                return vec![Out::Refused { session, command: CODE, reason: "BAD_STRIKE" }];
            }
            if pilot.flight.as_ref().is_some_and(|flight| flight.is_warping()) {
                return vec![Out::Refused { session, command: CODE, reason: "CANNOT_ACTIVATE_IN_WARP" }];
            }
        }
        if let Some(Action::Tractor { range, .. }) = effect.action.as_ref() {
            if effect.target_id <= 0 {
                return vec![Out::Refused { session, command: CODE, reason: "TARGET_REQUIRED" }];
            }
            // a beam pulls a wreck or a can (EVE.js: anything else is TARGET_NOT_FOUND); the pilot must hold its lock, and it must be in reach
            let Some(wreck) = self.wrecks.get(&effect.target_id).filter(|wreck| wreck.group_id != crate::wreck::CORPSE_GROUP) else {
                return vec![Out::Refused { session, command: CODE, reason: "TARGET_NOT_FOUND" }];
            };
            let target_type = wreck.kind.type_id;
            if !self.locked_by(session).contains(&effect.target_id) {
                return vec![Out::Refused { session, command: CODE, reason: "TARGET_NOT_LOCKED" }];
            }
            let apart = self.surface_distance_to_wreck(session, effect.target_id).unwrap_or(f64::INFINITY);
            if apart > range + crate::tractor::REACH_GRACE_METERS {
                return vec![Out::TargetRefused {
                    session,
                    command: CODE,
                    reason: "TARGET_OUT_OF_RANGE",
                    target_type,
                    desired_range: *range,
                    actual_distance: apart,
                }];
            }
        }
        if let Some(Action::Doomsday { delay_ms, damage, immobile_ms, no_jump_ms }) = effect.action.as_ref() {
            let numbers = [*delay_ms, *immobile_ms, *no_jump_ms];
            if !numbers.iter().all(|n| n.is_finite() && *n >= 0.0) || !damage.iter().all(|d| d.is_finite() && *d >= 0.0) {
                return vec![Out::Refused { session, command: CODE, reason: "BAD_DOOMSDAY" }];
            }
            if pilot.flight.as_ref().is_some_and(|flight| flight.is_warping()) {
                return vec![Out::Refused { session, command: CODE, reason: "CANNOT_ACTIVATE_IN_WARP" }];
            }
            // the target is a locked ship of the scene (the reference's TARGET_REQUIRED, TARGET_NOT_FOUND and TARGET_NOT_LOCKED)
            if effect.target_id <= 0 {
                return vec![Out::Refused { session, command: CODE, reason: "TARGET_REQUIRED" }];
            }
            if !self.turret_target_ok(session, &effect) {
                let reason = if self.session_of(effect.target_id).is_none() { "TARGET_NOT_FOUND" } else { "TARGET_NOT_LOCKED" };
                return vec![Out::Refused { session, command: CODE, reason }];
            }
        }
        if matches!(effect.action.as_ref(), Some(Action::Cloak)) {
            if pilot.no_jump_until > now {
                return vec![Out::Refused { session, command: CODE, reason: "NO_JUMP_OR_CLOAK" }];
            }
            if pilot.flight.as_ref().is_some_and(|flight| flight.is_warping()) {
                return vec![Out::Refused { session, command: CODE, reason: "CLOAK_WARP_BLOCKED" }];
            }
            if !pilot.targeting.pending_targets().is_empty()
                || !pilot.targeting.targets().is_empty()
                || !self.targeters_of(pilot.ship.spec.ship_id).is_empty()
            {
                return vec![Out::Refused { session, command: CODE, reason: "CLOAK_TARGET_LOCKED" }];
            }
            if !self.in_smartbomb_range(session, 2_000.0).is_empty() {
                return vec![Out::Refused { session, command: CODE, reason: "CLOAK_PROXIMITY_BLOCKED" }];
            }
        } else if pilot.cloak != crate::visibility::CloakState::Uncloaked {
            return vec![Out::Refused { session, command: CODE, reason: "MODULE_CLOAKED" }];
        }
        if effect.turret.is_some() {
            if effect.target_id <= 0 {
                return vec![Out::Refused { session, command: CODE, reason: "NO_TARGET" }];
            }
            // a target that is not in the scene is not there (the reference answers `TARGET_NOT_FOUND`), one that is must be locked
            if self.session_of(effect.target_id).is_none() {
                return vec![Out::Refused { session, command: CODE, reason: "TARGET_NOT_FOUND" }];
            }
            if !self.turret_target_ok(session, &effect) {
                return vec![Out::Refused { session, command: CODE, reason: "TARGET_NOT_LOCKED" }];
            }
            if effect.turret.as_ref().and_then(|t| t.charge).is_some_and(|c| c.rounds < c.per_cycle) {
                return vec![Out::Refused { session, command: CODE, reason: "NO_AMMO" }];
            }
            // a missile goes only as far as it flies: a target further off is out of reach (the client is told how far the missile goes and how far the target is)
            if let Some(missile) = effect.turret.as_ref().and_then(|t| t.missile.as_ref())
                && let Some(target) = self.session_of(effect.target_id)
                && let Some(apart) = self.apart(session, target)
                && apart > missile.effective_range() + crate::weapons::MISSILE_RANGE_GRACE
            {
                return vec![Out::TargetRefused {
                    session,
                    command: CODE,
                    reason: "TARGET_OUT_OF_RANGE",
                    target_type: self.pilots[&target].ship.spec.type_id,
                    desired_range: missile.effective_range(),
                    actual_distance: apart,
                }];
            }
        }
        // an effect that is projected at a ship needs it: there, locked and in reach (what is too far off is told with the range and how far it is)
        if let Some((what, reach, _)) = effect.remote() {
            if effect.target_id <= 0 {
                return vec![Out::Refused { session, command: CODE, reason: "NO_TARGET" }];
            }
            let Some(target) = self.session_of(effect.target_id) else {
                return vec![Out::Refused { session, command: CODE, reason: "TARGET_NOT_FOUND" }];
            };
            let self_capacitor_boost = matches!(what, Remote::Transfer { .. })
                && self.pilots.get(&session).is_some_and(|pilot| effect.target_id == pilot.ship.spec.ship_id);
            let self_sensor_boost =
                self.pilots.get(&session).is_some_and(|pilot| effect.target_id == pilot.ship.spec.ship_id)
                    && effect.jam() == Some("remoteSensorBoost")
                    && matches!(
                        what,
                        Remote::Damp {
                            range_percent,
                            resolution_percent,
                            gravimetric_percent,
                            ladar_percent,
                            magnetometric_percent,
                            radar_percent,
                        } if *range_percent >= 0.0
                            && *resolution_percent >= 0.0
                            && (*range_percent > 0.0 || *resolution_percent > 0.0)
                            && *gravimetric_percent == 0.0
                            && *ladar_percent == 0.0
                            && *magnetometric_percent == 0.0
                            && *radar_percent == 0.0
                    );
            if !self_capacitor_boost
                && !self_sensor_boost
                && !self.locked_by(session).contains(&effect.target_id)
            {
                return vec![Out::Refused { session, command: CODE, reason: "TARGET_NOT_LOCKED" }];
            }
            let apart = self.apart(session, target).unwrap_or(f64::INFINITY);
            if reach.share(apart).is_none() {
                return vec![Out::TargetRefused {
                    session,
                    command: CODE,
                    reason: "TARGET_OUT_OF_RANGE",
                    target_type: self.pilots[&target].ship.spec.type_id,
                    desired_range: reach.farthest(),
                    actual_distance: apart,
                }];
            }
        }
        let pilot = &self.pilots[&session];
        let Some(capacitor) = pilot.modules.capacitor.as_ref() else {
            return vec![Out::Refused { session, command: CODE, reason: "NO_CAPACITOR" }];
        };
        if capacitor.charge_at(now) < effect.capacitor_need {
            return vec![Out::Refused { session, command: CODE, reason: "NOT_ENOUGH_CAPACITOR" }];
        }
        // an effect that burns fuel starts when the host has taken the fuel of its first cycle (`WORK_FUEL`): it is parked until the answer
        if effect.burns_fuel {
            return self.park_start(session, effect, now);
        }
        self.start_effect(session, effect, now)
    }

    /// Parks an effect that burns fuel until the host takes the fuel of its first cycle: the host is asked for it (`ModuleWorkCycle`, `WORK_FUEL`) and answers.
    fn park_start(&mut self, session: SessionId, effect: Effect, now: i64) -> Vec<Out> {
        let cycle_id = next_work_cycle_id();
        let (module_id, target_id) = (effect.module_id, effect.target_id);
        let Some(pilot) = self.pilots.get_mut(&session) else { return Vec::new() };
        pilot.modules.pending_starts.push(PendingStart { effect, cycle_id, sent_at: now });
        self.fuel_cycle_out(session, module_id, target_id, cycle_id).into_iter().collect()
    }

    /// The effect starts now: the capacitor pays for its first cycle and the numbers, the effect and its first cycle are told. Refused when the capacitor
    /// is short (it can be short again when a parked start is answered).
    fn start_effect(&mut self, session: SessionId, effect: Effect, now: i64) -> Vec<Out> {
        const CODE: u8 = 17;
        let Some(pilot) = self.pilots.get(&session) else {
            return vec![Out::Refused { session, command: CODE, reason: "NO_SHIP_IN_SPACE" }];
        };
        if pilot.modules.capacitor.as_ref().is_none_or(|capacitor| capacitor.charge_at(now) < effect.capacitor_need) {
            return vec![Out::Refused { session, command: CODE, reason: "NOT_ENOUGH_CAPACITOR" }];
        }
        // the pilot has given up the undocking protection by flying the ship (the host tells the client)
        let (owner, ship_id) = (pilot.ship.spec.character_id, pilot.ship.spec.ship_id);
        let pilot = self.pilots.get_mut(&session).expect("the pilot is in the scene");
        pilot.protected_until = 0;
        let rounds = effect.turret.as_ref().and_then(|t| t.charge).map(|c| c.rounds).or_else(|| {
            effect.action.as_ref().and_then(Action::charge_use).map(|(_, _, _, charges)| charges)
        });
        let charged_this_cycle = rounds.is_some_and(|left| left > 0);
        let run =
            Running {
                effect,
                cycle_start: now,
                cycles: 1,
                strike_done: 0,
                doomsday_hit: false,
                stopping: false,
                rounds,
                charged_this_cycle,
                pending_remote_repair: None,
                pending_work: None,
                spool: SpoolState::default(),
            };
        let (before, after) = match pilot.modules.capacitor.as_mut() {
            Some(capacitor) => capacitor.draw(now, run.effect.capacitor_need),
            None => (0.0, 0.0),
        };
        // a reactive armor hardener's resonances start at its seed, before anything is told
        if let Some(Action::ResistanceShift { seed, shift, .. }) = run.effect.action.as_ref() {
            pilot.ship.health.start_shift(*seed, *shift);
        }
        let mut out = vec![Out::Accepted { session, command: CODE, at: now }];
        out.push(Self::charge_told(session, owner, ship_id, now, after, before));
        let stamp = self.next_stamp(now);
        let ship = id(ship_id);
        if let Some(numbers) = run.effect.on {
            let actions = self.renumber(session, numbers, stamp);
            if !actions.is_empty() {
                out.extend(self.tell_all(session, actions));
            }
        }
        // a turret's special effect is played before its numbers are told (the recording of the reference's gun), a propulsion module's after
        let fx = self.tell_all(session, vec![Self::fx_action(stamp, &ship, &run.effect, true)]);
        if run.effect.turret.is_some() {
            out.extend(fx);
            out.extend(Self::changes_told(session, owner, &run.effect, &run.effect.on_changes, now));
        } else {
            out.extend(Self::changes_told(session, owner, &run.effect, &run.effect.on_changes, now));
            out.extend(fx);
        }
        out.push(Self::effect_told(session, owner, ship_id, &run, now, true, now, None));
        let effect = run.effect.clone();
        if let Some(pilot) = self.pilots.get_mut(&session) {
            pilot.modules.running.push(run);
        }
        out.extend(self.act(session, &effect, Applies::CycleStart, now));
        out.extend(self.jam_told(session, &effect, now, Jam::Start));
        out.extend(self.fire_turret(session, &effect, now));
        // the round of the first shot is spent after the shot; a turret that has no round for the next cycle stops
        let (spent, goes_on) = self.spend_round(session, effect.module_id, now);
        out.extend(spent);
        let stop_without_charge =
            matches!(effect.action.as_ref(), Some(Action::Charged { fallback: None, .. }));
        if !goes_on
            && (effect.turret.is_some() || stop_without_charge)
            && let Some(pilot) = self.pilots.get_mut(&session)
            && let Some(at) =
                pilot.modules.running.iter().position(|r| r.effect.module_id == effect.module_id)
        {
            let run = pilot.modules.running.remove(at);
            out.extend(self.effect_ended(session, run, owner, ship_id, now, Ending::NoRounds));
        }
        out
    }

    /// The host changes the rounds of a gun of this session's ship (a pilot loaded it or unloaded it in space): a gun that runs keeps its rounds here, so what they
    /// come to is decided here and the host is told (`ChargeAdjusted`). A gun that does not run is not here: the host has its rounds and is told so. A gun that
    /// runs a charge of another kind than the one the host loads is left as it is (the rounds it fires are those it began the cycle with). The client is told by
    /// the host; the next shot tells it the rounds that are in the slot then.
    pub fn adjust_charge(
        &mut self,
        request_id: u64,
        session: SessionId,
        module_id: i64,
        charge_type: i32,
        op: ChargeOp,
    ) -> Vec<Out> {
        let answer = |running, before, after| {
            vec![Out::ChargeAdjusted { session, request_id, running, before, after }]
        };
        let Some(pilot) = self.pilots.get_mut(&session) else { return answer(false, 0, 0) };
        let Some(run) = pilot.modules.running.iter_mut().find(|r| r.effect.module_id == module_id) else {
            return answer(false, 0, 0);
        };
        let (Some(charge), Some(rounds)) =
            (run.effect.turret.as_ref().and_then(|t| t.charge), run.rounds.as_mut())
        else {
            return answer(false, 0, 0);
        };
        let before = *rounds;
        *rounds = match op {
            ChargeOp::Add { capacity, available } if charge.type_id == charge_type => {
                before + (capacity - before).max(0).min(available.max(0))
            }
            ChargeOp::Add { .. } => before,
            ChargeOp::Remove { quantity } if quantity > 0 => (before - quantity).max(0),
            ChargeOp::Remove { .. } => 0,
        };
        answer(true, before, *rounds)
    }

    /// The running effect of a module of this session, if it runs.
    fn running_mut(&mut self, session: SessionId, module_id: i64) -> Option<&mut Running> {
        self.pilots.get_mut(&session)?.modules.running.iter_mut().find(|run| run.effect.module_id == module_id)
    }

    /// The bonus a weapon's shot at `target_id` takes, from the ramp its running effect has built (0 when the module does not run).
    pub(crate) fn spool_bonus(&mut self, session: SessionId, module_id: i64, target_id: i64, spool: Spool) -> f64 {
        self.running_mut(session, module_id).map_or(0.0, |run| spool.bonus_for(&mut run.spool, target_id))
    }

    /// A cycle has been shot: the ramp of the running effect grows by a cycle's worth.
    pub(crate) fn spool_advance(&mut self, session: SessionId, module_id: i64, spool: Spool) {
        if let Some(run) = self.running_mut(session, module_id) {
            spool.advance(&mut run.spool);
        }
    }

    /// What the ramp of a running effect has built, for the tests.
    #[cfg(test)]
    pub(crate) fn spool_of(&self, session: SessionId, module_id: i64) -> Option<SpoolState> {
        self.pilots.get(&session)?.modules.running.iter().find(|run| run.effect.module_id == module_id).map(|run| run.spool)
    }

    fn spend_round(&mut self, session: SessionId, module_id: i64, at: i64) -> (Vec<Out>, bool) {
        let Some(pilot) = self.pilots.get_mut(&session) else { return (Vec::new(), false) };
        let (owner, ship_id) = (pilot.ship.spec.character_id, pilot.ship.spec.ship_id);
        let Some(run) = pilot.modules.running.iter_mut().find(|r| r.effect.module_id == module_id) else {
            return (Vec::new(), true);
        };
        let action_charge = run.effect.action.as_ref().and_then(Action::charge_use);
        let use_charge = run
            .effect
            .turret
            .as_ref()
            .and_then(|turret| turret.charge)
            .map(|charge| (charge.type_id, charge.flag, charge.per_cycle))
            .or_else(|| action_charge.map(|(type_id, flag, per_cycle, _)| (type_id, flag, per_cycle)));
        let (Some((type_id, flag, per_cycle)), Some(rounds)) = (use_charge, run.rounds.as_mut()) else {
            return (Vec::new(), true);
        };
        let (old, new) = (*rounds, (*rounds - per_cycle).max(0));
        if old <= 0 {
            let has_fallback = run
                .effect
                .action
                .as_ref()
                .is_some_and(|action| matches!(action, Action::Charged { fallback: Some(_), .. }));
            return (Vec::new(), has_fallback);
        }
        *rounds = new;
        let line = attribute_change_of(
            owner,
            charge_key(ship_id, &crate::weapons::Charge { type_id, flag, rounds: old, per_cycle }),
            805,
            at,
            number(new as f64),
            number(old as f64),
        );
        (
            vec![
                Self::dogma_notify(session, "OnModuleAttributeChanges", vec![Value::List(vec![line])]),
                Out::ChargeSpent { session, module_id, rounds_left: new },
            ],
            action_charge.is_some() && new > 0 || action_charge.is_none() && new >= per_cycle,
        )
    }

    /// The pilot asks for an effect to stop: it does at the end of the cycle it is in. `Refused` (`NOT_ACTIVE`) when it is not running.
    pub fn deactivate(&mut self, session: SessionId, module_id: i64, now: i64) -> Vec<Out> {
        const CODE: u8 = 16;
        let Some(pilot) = self.pilots.get_mut(&session) else {
            return vec![Out::Refused { session, command: CODE, reason: "NO_SHIP_IN_SPACE" }];
        };
        // an effect still waiting for the fuel of its first cycle is cancelled: its activation is refused, and the answer to the fuel ask no longer starts it
        if let Some(position) = pilot.modules.pending_starts.iter().position(|start| start.effect.module_id == module_id) {
            pilot.modules.pending_starts.remove(position);
            return vec![
                Out::Refused { session, command: 17, reason: "DEACTIVATED" },
                Out::Accepted { session, command: CODE, at: now },
            ];
        }
        let Some(index) = pilot.modules.running.iter().position(|r| r.effect.module_id == module_id) else {
            return vec![Out::Refused { session, command: CODE, reason: "NOT_ACTIVE" }];
        };
        if pilot.modules.running[index].pending_work.is_some() {
            // parked on a work cycle: there is no cycle left to finish, so the effect stops now
            let (owner, ship_id) = (pilot.ship.spec.character_id, pilot.ship.spec.ship_id);
            let run = pilot.modules.running.remove(index);
            let mut out = vec![Out::Accepted { session, command: CODE, at: now }];
            out.extend(self.effect_ended(session, run, owner, ship_id, now, Ending::Done));
            return out;
        }
        // A mining cycle that is short-cycled by the pilot pays what it has mined so far, as the reference does (`deactivateGenericModule`). An ice cycle is not
        // short-cycled: it mines nothing short, so it runs to its boundary as the reference does.
        let run = &pilot.modules.running[index];
        let efficiency = ((now - run.cycle_start) as f64 / (run.effect.duration_ms * TICKS_PER_SECOND as f64 / 1000.0)).clamp(0.0, 1.0);
        let short = match run.effect.action.as_ref().and_then(|action| action.with_charges(run.charged_this_cycle)) {
            Some(Action::Mining { family, mining_amount_m3, waste_multiplier, waste_probability, crit_probability, crit_multiplier })
                if *family != MiningFamily::Ice =>
            {
                Some(Action::Mining {
                    family: *family,
                    mining_amount_m3: mining_amount_m3 * efficiency,
                    waste_multiplier: *waste_multiplier,
                    waste_probability: *waste_probability,
                    crit_probability: *crit_probability,
                    crit_multiplier: *crit_multiplier,
                })
            }
            _ => None,
        };
        let (owner, ship_id) = (pilot.ship.spec.character_id, pilot.ship.spec.ship_id);
        match short {
            Some(work) if efficiency > 0.0 => {
                pilot.modules.running[index].stopping = true;
                let mut out = vec![Out::Accepted { session, command: CODE, at: now }];
                out.extend(self.park_work_cycle(session, module_id, &work, now));
                out
            }
            // nothing was mined yet: the effect stops at once, with no cycle to pay for
            Some(_) => {
                let run = pilot.modules.running.remove(index);
                let mut out = vec![Out::Accepted { session, command: CODE, at: now }];
                out.extend(self.effect_ended(session, run, owner, ship_id, now, Ending::Done));
                out
            }
            None => {
                pilot.modules.running[index].stopping = true;
                vec![Out::Accepted { session, command: CODE, at: now }]
            }
        }
    }

    /// What the running effects and the capacitors have come to by `now`: cycles that end (the next begins, or the effect stops), and the charge told.
    pub(crate) fn settle_modules(&mut self, now: i64) -> Vec<Out> {
        let sessions: Vec<SessionId> = self
            .in_entry_order()
            .into_iter()
            .filter(|s| {
                !self.pilots[s].modules.is_idle()
                    || self.pilots[s].ship.health.next_sample.is_some()
                    || !self.pilots[s].incoming.is_empty()
            })
            .collect();
        let mut out = Vec::new();
        for session in sessions {
            // what no module renewed lapses
            out.extend(self.expire_incoming(session, now));
            // a cycle that ends is followed by the charge told at the moments in between, in the order they fall
            while let Some((at, kind)) = self.next_module_moment(session, now) {
                out.extend(self.module_moment(session, at, kind));
            }
        }
        out
    }

    /// The next thing due for a ship's modules by `now`: a cycle's end or a charge to tell.
    fn next_module_moment(&self, session: SessionId, now: i64) -> Option<(i64, Moment)> {
        let pilot = self.pilots.get(&session)?;
        let mut next: Option<(i64, Moment)> = None;
        let mut consider = |at: i64, moment: Moment| {
            if at <= now && next.as_ref().is_none_or(|(best, _)| at < *best) {
                next = Some((at, moment));
            }
        };
        for (index, start) in pilot.modules.pending_starts.iter().enumerate() {
            // a start parked for its fuel: the same id is asked again after a while
            consider(start.sent_at + WORK_RESEND_TICKS, Moment::StartResend(index));
        }
        for (index, run) in pilot.modules.running.iter().enumerate() {
            if let Some(pending) = run.pending_work {
                // parked on the host's answer: no cycle is timed; the same id is sent again after a while, and the effect stops when its target is gone
                let target_ok = if pending.fuel {
                    self.cycle_target_ok(session, &run.effect, run.charged_this_cycle)
                } else {
                    self.work_target_ok(session, &run.effect)
                };
                if target_ok {
                    consider(pending.sent_at + WORK_RESEND_TICKS, Moment::WorkResend(index));
                } else {
                    consider(now, Moment::WorkStop(index));
                }
                continue;
            }
            if run.pending_remote_repair.is_some() {
                continue;
            }
            if let Some(Action::Doomsday { delay_ms, .. }) = run.effect.action.as_ref()
                && !run.doomsday_hit
            {
                consider(
                    run.cycle_start + (delay_ms * TICKS_PER_SECOND as f64 / 1000.0) as i64,
                    Moment::DoomsdayHit(index),
                );
            }
            if let Some(strike) = strike_of(run.effect.action.as_ref())
                && run.strike_done < strike.pulses
            {
                consider(
                    run.cycle_start + (strike.pulse_offset_ms(run.strike_done) * TICKS_PER_SECOND as f64 / 1000.0) as i64,
                    Moment::StrikePulse(index),
                );
            }
            consider(
                run.cycle_start + (run.effect.duration_ms * TICKS_PER_SECOND as f64 / 1000.0) as i64,
                Moment::CycleEnd(index),
            );
        }
        if let Some(sample) = pilot.modules.capacitor.as_ref().and_then(|c| c.next_sample) {
            consider(sample, Moment::Charge);
        }
        // the shield's charge follows the capacitor's at the same tick (the reference works them out in that order)
        if let Some(sample) = pilot.ship.health.next_sample {
            consider(sample, Moment::Shield);
        }
        next
    }

    fn module_moment(&mut self, session: SessionId, at: i64, moment: Moment) -> Vec<Out> {
        match moment {
            Moment::Charge => self.tell_charge(session, at),
            Moment::Shield => self.tell_shield(session, at),
            Moment::CycleEnd(index) => self.end_cycle(session, index, at),
            Moment::WorkResend(index) => self.resend_work_cycle(session, index, at),
            Moment::WorkStop(index) => self.stop_parked(session, index, at),
            Moment::StartResend(index) => self.resend_start(session, index, at),
            Moment::StrikePulse(index) => self.strike_pulse(session, index, at),
            Moment::DoomsdayHit(index) => self.doomsday_hit(session, index, at),
        }
    }

    /// The charge is told: now, and a tenth of a second before; the next time is half a second on while the charge is below the capacity.
    fn tell_charge(&mut self, session: SessionId, at: i64) -> Vec<Out> {
        let Some(pilot) = self.pilots.get_mut(&session) else { return Vec::new() };
        let (owner, ship) = (pilot.ship.spec.character_id, pilot.ship.spec.ship_id);
        let Some(capacitor) = pilot.modules.capacitor.as_mut() else { return Vec::new() };
        let (new, old) = (capacitor.charge_at(at), capacitor.charge_at(at - TICK));
        if old >= capacitor.capacity {
            // it was full a tick ago: nothing more is told
            capacitor.next_sample = None;
            return Vec::new();
        }
        // the tick that fills it is told when it is a tick that tells (the reference tells the change on every fifth tick)
        capacitor.next_sample = (new < capacitor.capacity).then_some(at + SAMPLE_EVERY);
        vec![Self::charge_told(session, owner, ship, at, new, old)]
    }

    /// A cycle ends: what it does at its end is done, then the next begins (and costs capacitor), or, when the pilot asked to stop or the capacitor cannot pay, the
    /// effect ends.
    fn end_cycle(&mut self, session: SessionId, index: usize, at: i64) -> Vec<Out> {
        let Some(pilot) = self.pilots.get(&session) else { return Vec::new() };
        let run = pilot.modules.running[index].clone();
        if let Some(Action::MicroJumpDrive { distance_meters, cooldown_ms }) = run.effect.action.as_ref() {
            let module_id = run.effect.module_id;
            let cooldown_until = at.saturating_add((*cooldown_ms).saturating_mul(TICKS_PER_SECOND / 1000));
            let scrammed = self.pilots.get(&session).is_some_and(|pilot| pilot.incoming.mwd_blocked());
            let (owner, ship_id) = {
                let Some(pilot) = self.pilots.get_mut(&session) else { return Vec::new() };
                pilot.modules.mjd_cooldowns.retain(|(id, _)| *id != module_id);
                pilot.modules.mjd_cooldowns.push((module_id, cooldown_until));
                let identity = (pilot.ship.spec.character_id, pilot.ship.spec.ship_id);
                pilot.modules.running.remove(index);
                identity
            };
            let mut out =
                if scrammed { Vec::new() } else { self.perform_micro_jump(session, *distance_meters, at) };
            out.extend(self.effect_ended(session, run, owner, ship_id, at, Ending::Done));
            return out;
        }
        // a weapon goes on while its target is there and locked, an effect projected at a ship while the ship is also in reach
        let target_ok = self.cycle_target_ok(session, &run.effect, run.charged_this_cycle);
        // a work cycle that ends with its target there is handed to the host, and the module is parked until the answer (ADR 0013)
        if target_ok
            && let Some(work) = run.effect.action.as_ref().and_then(|action| action.with_charges(run.charged_this_cycle)).filter(|action| action.is_work())
        {
            return self.park_work_cycle(session, run.effect.module_id, work, at);
        }
        if target_ok
            && let Some(Action::Remote {
                what: Remote::MutadaptiveRepair { amount, bonus_per_cycle, bonus_max, .. },
                when: Applies::CycleEnd,
                ..
            }) = run.effect.action.as_ref()
        {
            let range_share = run.effect.remote().and_then(|(_, reach, _)| {
                self.session_of(run.effect.target_id)
                    .and_then(|target| self.apart(session, target))
                    .and_then(|apart| reach.share(apart))
            }).expect("a valid remote-repair target has a range share");
            let Some(pilot) = self.pilots.get_mut(&session) else { return Vec::new() };
            let Some(running_index) =
                pilot.modules.running.iter().position(|current| current.effect.module_id == run.effect.module_id)
            else {
                return Vec::new();
            };
            let current = &pilot.modules.running[running_index];
            if current.pending_remote_repair.is_some() {
                return Vec::new();
            }
            let cycle_number = current.cycles;
            let ends_effect_after_cycle = current.stopping
                || current.cycles >= current.effect.repeat
                || !pilot.modules.capacitor.as_ref().is_some_and(|c| c.charge_at(at) >= run.effect.capacitor_need);
            let cycle_id = pilot.modules.allocate_remote_repair_cycle_id();
            pilot.modules.running[running_index].pending_remote_repair =
                Some(PendingRemoteRepairCycle { cycle_id, at, range_share });
            let cycle_at_ms = (i128::from(at) * 1_000 / i128::from(TICKS_PER_SECOND)) as i64;
            let cycle_duration_ms = run.effect.duration_ms.round().max(1.0) as i64;
            return vec![Out::RemoteRepairCycle {
                session,
                module_id: run.effect.module_id,
                target_id: run.effect.target_id,
                cycle_id,
                cycle_number,
                cycle_at_ms,
                cycle_duration_ms,
                ends_effect_after_cycle,
                base_amount: *amount,
                bonus_per_cycle: *bonus_per_cycle,
                bonus_max: *bonus_max,
            }];
        }
        // what the cycle that ends does at its end is done before the effect goes on or ends
        let out = self.act(session, &run.effect, Applies::CycleEnd, at);
        // an effect that burns fuel asks for the fuel of the next cycle before that cycle starts (`WORK_FUEL`), when there is one to start
        if target_ok && run.effect.burns_fuel && self.next_cycle_follows(session, run.effect.module_id, at) {
            return self.park_fuel_cycle(session, run.effect.module_id, at, out);
        }
        self.finish_cycle(session, run.effect.module_id, at, out, target_ok)
    }

    fn finish_cycle(&mut self, session: SessionId, module_id: i64, at: i64, mut out: Vec<Out>, target_ok: bool) -> Vec<Out> {
        let Some(pilot) = self.pilots.get_mut(&session) else { return out };
        let Some(index) = pilot.modules.running.iter().position(|run| run.effect.module_id == module_id) else {
            return out;
        };
        let run = pilot.modules.running[index].clone();
        let (owner, ship_id) = (pilot.ship.spec.character_id, pilot.ship.spec.ship_id);
        let affordable =
            pilot.modules.capacitor.as_ref().is_some_and(|c| c.charge_at(at) >= run.effect.capacitor_need);
        let more = run.cycles < run.effect.repeat;
        if run.stopping || !more || !affordable || !target_ok {
            let ending = if target_ok { Ending::Done } else { Ending::TargetGone };
            pilot.modules.running.remove(index);
            out.extend(self.effect_ended(session, run, owner, ship_id, at, ending));
            return out;
        }
        // the host took the rounds out while the gun ran (a pilot unloaded it in space): it stops with no shot
        let no_action_charge = matches!(
            run.effect.action.as_ref(),
            Some(Action::Charged { fallback: None, .. }) if run.rounds.unwrap_or(0) <= 0
        );
        if no_action_charge
            || run
                .effect
                .turret
                .as_ref()
                .and_then(|t| t.charge)
                .zip(run.rounds)
                .is_some_and(|(charge, rounds)| rounds < charge.per_cycle)
        {
            pilot.modules.running.remove(index);
            out.extend(self.effect_ended(session, run, owner, ship_id, at, Ending::NoRounds));
            return out;
        }
        let (before, after) = match pilot.modules.capacitor.as_mut() {
            Some(capacitor) => capacitor.draw(at, run.effect.capacitor_need),
            None => (0.0, 0.0),
        };
        let next = &mut pilot.modules.running[index];
        next.cycle_start = at;
        next.cycles += 1;
        next.charged_this_cycle = next.rounds.is_some_and(|left| left > 0);
        next.pending_remote_repair = None;
        let next = next.clone();
        out.push(Self::charge_told(session, owner, ship_id, at, after, before));
        if next.effect.turret.is_none() {
            out.push(Self::effect_told(session, owner, ship_id, &next, at, true, at, None));
            out.extend(self.act(session, &next.effect, Applies::CycleStart, at));
            out.extend(self.jam_told(session, &next.effect, at, Jam::Refresh));
            let (spent, goes_on) = self.spend_round(session, next.effect.module_id, at);
            out.extend(spent);
            let stop_without_charge =
                matches!(next.effect.action.as_ref(), Some(Action::Charged { fallback: None, .. }));
            if !goes_on
                && stop_without_charge
                && let Some(pilot) = self.pilots.get_mut(&session)
                && let Some(position) =
                    pilot.modules.running.iter().position(|run| run.effect.module_id == next.effect.module_id)
            {
                let run = pilot.modules.running.remove(position);
                out.extend(self.effect_ended(session, run, owner, ship_id, at, Ending::NoRounds));
            }
            return out;
        }
        // a turret shoots, spends its round and then is told to have begun another cycle (the recording of the reference's gun); one that is out of rounds stops
        out.extend(self.fire_turret(session, &next.effect, at));
        let (spent, goes_on) = self.spend_round(session, next.effect.module_id, at);
        out.extend(spent);
        if goes_on {
            // a launcher is not told again each cycle: the recording of the reference's launcher has the effect told on at its start and off at its end, with the rounds between
            if next.effect.turret.as_ref().is_none_or(|turret| turret.missile.is_none()) {
                out.push(Self::effect_told(session, owner, ship_id, &next, at, true, at, None));
            }
        } else if let Some(pilot) = self.pilots.get_mut(&session)
            && let Some(position) =
                pilot.modules.running.iter().position(|run| run.effect.module_id == next.effect.module_id)
        {
            let run = pilot.modules.running.remove(position);
            out.extend(self.effect_ended(session, run, owner, ship_id, at, Ending::NoRounds));
        }
        out
    }

    /// Settles the host's amount for the exact pending module/target cycle; stale or replayed results do nothing.
    pub fn remote_repair_cycle_result(
        &mut self,
        session: SessionId,
        module_id: i64,
        target_id: i64,
        cycle_id: u64,
        accepted: bool,
        amount: f64,
    ) -> Vec<Out> {
        let Some(pilot) = self.pilots.get(&session) else { return Vec::new() };
        let Some(index) = pilot.modules.running.iter().position(|run| {
            run.effect.module_id == module_id
                && run.effect.target_id == target_id
                && run.pending_remote_repair.is_some_and(|pending| pending.cycle_id == cycle_id)
        }) else {
            return Vec::new();
        };
        let run = pilot.modules.running[index].clone();
        let pending = run.pending_remote_repair.expect("matched pending remote-repair cycle");
        if !accepted || !amount.is_finite() || amount < 0.0 {
            let (owner, ship_id) = (pilot.ship.spec.character_id, pilot.ship.spec.ship_id);
            let Some(pilot) = self.pilots.get_mut(&session) else { return Vec::new() };
            let run = pilot.modules.running.remove(index);
            return self.effect_ended(session, run, owner, ship_id, pending.at, Ending::Done);
        }
        let Some(Action::Remote {
            what: Remote::MutadaptiveRepair { layer, .. },
            ..
        }) = run.effect.action.as_ref()
        else {
            let (owner, ship_id) = (pilot.ship.spec.character_id, pilot.ship.spec.ship_id);
            let Some(pilot) = self.pilots.get_mut(&session) else { return Vec::new() };
            let run = pilot.modules.running.remove(index);
            return self.effect_ended(session, run, owner, ship_id, pending.at, Ending::Done);
        };
        let mut out = self.mend_remote(target_id, *layer, amount * pending.range_share, pending.at);
        if let Some(pilot) = self.pilots.get_mut(&session)
            && let Some(current) = pilot.modules.running.get_mut(index)
            && current.pending_remote_repair.is_some_and(|current| current.cycle_id == cycle_id)
        {
            current.pending_remote_repair = None;
        } else {
            return out;
        }
        out.extend(self.finish_cycle(session, module_id, pending.at, Vec::new(), true));
        out
    }

    /// A work target (a rock, a wreck) is there while the pilot holds its lock: a depleted rock drops its locks.
    fn work_target_ok(&self, session: SessionId, effect: &Effect) -> bool {
        self.locked_by(session).contains(&effect.target_id)
    }

    /// Whether the target of the cycle that ends is still there, as the effect needs it: a turret's, a projected effect's, a work target's (or none).
    fn cycle_target_ok(&self, session: SessionId, effect: &Effect, charged: bool) -> bool {
        let self_capacitor_boost = effect
            .remote()
            .is_some_and(|(what, ..)| matches!(what, Remote::Transfer { .. }))
            && self.pilots.get(&session).is_some_and(|pilot| effect.target_id == pilot.ship.spec.ship_id);
        let self_sensor_boost =
            self.pilots.get(&session).is_some_and(|pilot| effect.target_id == pilot.ship.spec.ship_id)
                && effect.jam() == Some("remoteSensorBoost")
                && effect.remote().is_some_and(|(what, ..)| {
                    matches!(
                        what,
                        Remote::Damp {
                            range_percent,
                            resolution_percent,
                            gravimetric_percent,
                            ladar_percent,
                            magnetometric_percent,
                            radar_percent,
                        } if *range_percent >= 0.0
                            && *resolution_percent >= 0.0
                            && (*range_percent > 0.0 || *resolution_percent > 0.0)
                            && *gravimetric_percent == 0.0
                            && *ladar_percent == 0.0
                            && *magnetometric_percent == 0.0
                            && *radar_percent == 0.0
                    )
                });
        if self_capacitor_boost || self_sensor_boost {
            true
        } else if effect.turret.is_some() {
            self.turret_target_ok(session, &effect)
        } else if let Some((_, reach, _)) = effect.remote() {
            self.turret_target_ok(session, &effect)
                && self
                    .session_of(effect.target_id)
                    .and_then(|target| self.apart(session, target))
                    .is_some_and(|apart| reach.share(apart).is_some())
        } else if let Some(Action::Tractor { range, .. }) = effect.action.as_ref() {
            // a tractor's target is a wreck or a can it holds the lock on, within reach
            self.tractor_target_ok(session, effect.target_id, *range)
        } else if effect.action.as_ref().and_then(|action| action.with_charges(charged)).is_some_and(Action::is_work) {
            // a work target (a rock, a wreck) is there while the pilot holds its lock
            self.work_target_ok(session, &effect)
        } else {
            true
        }
    }

    /// The `ModuleWorkCycle` that asks the host for the fuel of a cycle (`WORK_FUEL`): nothing is mined or salvaged, the host takes the fuel or refuses.
    fn fuel_cycle_out(&self, session: SessionId, module_id: i64, target_id: i64, cycle_id: u64) -> Option<Out> {
        let pilot = self.pilots.get(&session)?;
        Some(Out::ModuleWorkCycle {
            session,
            character_id: pilot.ship.spec.character_id,
            ship_id: pilot.ship.spec.ship_id,
            solar_system_id: self.solar_system_id(),
            module_id,
            target_id,
            cycle_id,
            command: WORK_FUEL,
            mining_amount_m3: 0.0,
            waste_multiplier: 0.0,
            waste_probability: 0.0,
            crit_probability: 0.0,
            crit_multiplier: 0.0,
            access_difficulty_bonus: 0.0,
            waste_roll: 0.0,
            rounding_roll: 0.0,
            crit_roll: 0.0,
        })
    }

    /// The effect of a running cycle goes on past its end only when the next cycle starts: the cycles are not done, the effect is not stopping, the capacitor
    /// pays for the next one. Only then is its fuel asked for.
    fn next_cycle_follows(&self, session: SessionId, module_id: i64, at: i64) -> bool {
        self.pilots.get(&session).is_some_and(|pilot| {
            pilot.modules.running.iter().any(|run| {
                run.effect.module_id == module_id
                    && !run.stopping
                    && run.cycles < run.effect.repeat
                    && pilot.modules.capacitor.as_ref().is_some_and(|c| c.charge_at(at) >= run.effect.capacitor_need)
            })
        })
    }

    /// The end of a cycle of an effect that burns fuel, with the next cycle to come: the host is asked for its fuel first, and the module is parked on the answer.
    fn park_fuel_cycle(&mut self, session: SessionId, module_id: i64, at: i64, mut out: Vec<Out>) -> Vec<Out> {
        let cycle_id = next_work_cycle_id();
        let Some(pilot) = self.pilots.get_mut(&session) else { return out };
        let Some(run) = pilot.modules.running.iter_mut().find(|run| run.effect.module_id == module_id) else { return out };
        let target_id = run.effect.target_id;
        run.pending_work = Some(PendingWorkCycle { cycle_id, at, sent_at: at, rolls: [0.0; 3], fuel: true });
        out.extend(self.fuel_cycle_out(session, module_id, target_id, cycle_id));
        out
    }

    /// A parked start is due again: the same id is asked once more (the host answers a repeat from its record).
    fn resend_start(&mut self, session: SessionId, index: usize, at: i64) -> Vec<Out> {
        let Some(pilot) = self.pilots.get_mut(&session) else { return Vec::new() };
        let Some(start) = pilot.modules.pending_starts.get_mut(index) else { return Vec::new() };
        start.sent_at = at;
        let (module_id, target_id, cycle_id) = (start.effect.module_id, start.effect.target_id, start.cycle_id);
        self.fuel_cycle_out(session, module_id, target_id, cycle_id).into_iter().collect()
    }

    /// The reason a refused start is shown with: the host's `NO_FUEL` as it is; a ship the host no longer has in space as that; anything else as a refused fuel.
    fn fuel_refusal(reason: &str) -> &'static str {
        match reason {
            "NO_FUEL" => "NO_FUEL",
            "NO_PILOT" | "NO_SHIP" | "NOT_CONTROLLED" => "NO_SHIP_IN_SPACE",
            _ => "FUEL_REFUSED",
        }
    }

    /// The work cycle of a running effect as the host is told it (`ModuleWorkCycle`), for `work` (the action that runs now) and the cycle sent.
    fn work_cycle_out(&self, session: SessionId, module_id: i64, target_id: i64, work: &Action, pending: PendingWorkCycle) -> Option<Out> {
        let pilot = self.pilots.get(&session)?;
        let (character_id, ship_id) = (pilot.ship.spec.character_id, pilot.ship.spec.ship_id);
        let (command, mining_amount_m3, waste_multiplier, waste_probability, crit_probability, crit_multiplier, access_difficulty_bonus) = match work {
            Action::Mining { mining_amount_m3, waste_multiplier, waste_probability, crit_probability, crit_multiplier, .. } => {
                (WORK_MINE, *mining_amount_m3, *waste_multiplier, *waste_probability, *crit_probability, *crit_multiplier, 0.0)
            }
            Action::Salvage { access_bonus } => (WORK_SALVAGE, 0.0, 0.0, 0.0, 0.0, 0.0, *access_bonus),
            _ => return None,
        };
        Some(Out::ModuleWorkCycle {
            session,
            character_id,
            ship_id,
            solar_system_id: self.solar_system_id(),
            module_id,
            target_id,
            cycle_id: pending.cycle_id,
            command,
            mining_amount_m3,
            waste_multiplier,
            waste_probability,
            crit_probability,
            crit_multiplier,
            access_difficulty_bonus,
            waste_roll: pending.rolls[0],
            rounding_roll: pending.rolls[1],
            crit_roll: pending.rolls[2],
        })
    }

    /// A work cycle ends with its target there: the host is asked for its outcome, and the module is parked until the answer (ADR 0013). Nothing of the
    /// cycle's end is done until then; the next cycle is timed from the answer.
    fn park_work_cycle(&mut self, session: SessionId, module_id: i64, work: &Action, at: i64) -> Vec<Out> {
        let rolls = [self.rng.random(), self.rng.random(), self.rng.random()];
        let pending = PendingWorkCycle { cycle_id: next_work_cycle_id(), at, sent_at: at, rolls, fuel: false };
        let Some(pilot) = self.pilots.get_mut(&session) else { return Vec::new() };
        let Some(run) = pilot.modules.running.iter_mut().find(|run| run.effect.module_id == module_id) else {
            return Vec::new();
        };
        run.pending_work = Some(pending);
        let target_id = run.effect.target_id;
        self.work_cycle_out(session, module_id, target_id, work, pending).into_iter().collect()
    }

    /// A parked work cycle is due again: the same cycle, with the same rolls, is sent once more (the host answers a repeat from its record).
    fn resend_work_cycle(&mut self, session: SessionId, index: usize, at: i64) -> Vec<Out> {
        let Some(pilot) = self.pilots.get_mut(&session) else { return Vec::new() };
        let Some(run) = pilot.modules.running.get_mut(index) else { return Vec::new() };
        let Some(pending) = run.pending_work else { return Vec::new() };
        if pending.fuel {
            let (module_id, target_id) = (run.effect.module_id, run.effect.target_id);
            run.pending_work = Some(PendingWorkCycle { sent_at: at, ..pending });
            return self.fuel_cycle_out(session, module_id, target_id, pending.cycle_id).into_iter().collect();
        }
        let Some(work) = run.effect.action.as_ref().and_then(|action| action.with_charges(run.charged_this_cycle)).filter(|action| action.is_work()).cloned()
        else {
            return Vec::new();
        };
        let (module_id, target_id) = (run.effect.module_id, run.effect.target_id);
        let pending = PendingWorkCycle { sent_at: at, ..pending };
        run.pending_work = Some(pending);
        self.work_cycle_out(session, module_id, target_id, &work, pending).into_iter().collect()
    }

    /// A parked effect loses its target: it stops at once, with no outcome for the cycle it was on (no grant is owed for it).
    fn stop_parked(&mut self, session: SessionId, index: usize, now: i64) -> Vec<Out> {
        let Some(pilot) = self.pilots.get_mut(&session) else { return Vec::new() };
        if pilot.modules.running.get(index).is_none_or(|run| run.pending_work.is_none()) {
            return Vec::new();
        }
        let (owner, ship_id) = (pilot.ship.spec.character_id, pilot.ship.spec.ship_id);
        let run = pilot.modules.running.remove(index);
        self.effect_ended(session, run, owner, ship_id, now, Ending::TargetLost)
    }

    /// The host's answer to a work cycle (`ModuleWorkResult`). An accepted one ends that cycle and the effect goes on, or ends when it must; a refused one
    /// stops the effect (the host has granted nothing). An answer that matches no parked cycle (a repeat, or a cycle the module has left) does nothing.
    /// `depleted` is the host's `complete`: the rock is mined out (or the wreck is gone), so the effect stops as its target is lost.
    pub fn module_work_result(
        &mut self, session: SessionId, module_id: i64, target_id: i64, cycle_id: u64, accepted: bool, reason: &str, depleted: bool, now: i64,
    ) -> Vec<Out> {
        let Some(pilot) = self.pilots.get_mut(&session) else { return Vec::new() };
        // a start parked for its fuel: accepted, it starts; refused, it is refused with the host's reason
        if let Some(position) = pilot.modules.pending_starts.iter().position(|start| {
            start.effect.module_id == module_id && start.effect.target_id == target_id && start.cycle_id == cycle_id
        }) {
            let start = pilot.modules.pending_starts.remove(position);
            if !accepted {
                return vec![Out::Refused { session, command: 17, reason: Self::fuel_refusal(reason) }];
            }
            return self.start_effect(session, start.effect, now);
        }
        let Some(pilot) = self.pilots.get(&session) else { return Vec::new() };
        let Some(index) = pilot.modules.running.iter().position(|run| {
            run.effect.module_id == module_id
                && run.effect.target_id == target_id
                && run.pending_work.is_some_and(|pending| pending.cycle_id == cycle_id)
        }) else {
            return Vec::new();
        };
        let (owner, ship_id) = (pilot.ship.spec.character_id, pilot.ship.spec.ship_id);
        let Some(pilot) = self.pilots.get_mut(&session) else { return Vec::new() };
        let fuel = pilot.modules.running[index].pending_work.is_some_and(|pending| pending.fuel);
        if !accepted {
            let run = pilot.modules.running.remove(index);
            let ending = if fuel && reason == "NO_FUEL" { Ending::NoFuel } else { Ending::WorkRefused };
            return self.effect_ended(session, run, owner, ship_id, now, ending);
        }
        pilot.modules.running[index].pending_work = None;
        let run = pilot.modules.running[index].clone();
        let target_ok = if fuel {
            self.cycle_target_ok(session, &run.effect, run.charged_this_cycle)
        } else {
            self.work_target_ok(session, &run.effect) && !depleted
        };
        self.finish_cycle(session, module_id, now, Vec::new(), target_ok)
    }

    /// A module's mining cycle that the host granted (`accepted`) took `units_remaining` from its rock. The other pilots who see the rock are told how much came
    /// off it (`OnOreMinedByOther`, the whole amount taken, as the reference sends it); the miner is told by its own `OnOreMined`. The rock's stock is set
    /// without a change to its destiny (the reference's clients keep the scanned quantity and take it down from this message), and a rock mined out is removed.
    /// Only a cycle still parked counts: a repeat, or a cycle the module has left, tells nobody.
    pub fn module_mined(
        &mut self, session: SessionId, module_id: i64, target_id: i64, cycle_id: u64, units_remaining: i64, now: i64,
    ) -> Vec<Out> {
        let parked = self.pilots.get(&session).is_some_and(|pilot| {
            pilot.modules.running.iter().any(|run| {
                run.effect.module_id == module_id
                    && run.effect.target_id == target_id
                    && matches!(run.effect.action, Some(Action::Mining { .. }))
                    && run.pending_work.is_some_and(|pending| pending.cycle_id == cycle_id && !pending.fuel)
            })
        });
        let Some(stock) = self.asteroids.get(&target_id).map(|asteroid| asteroid.units_remaining) else {
            return Vec::new();
        };
        if !parked || units_remaining >= stock {
            return Vec::new();
        }
        let taken = stock - units_remaining;
        let mut out: Vec<Out> = self
            .in_entry_order()
            .into_iter()
            .filter(|s| *s != session && self.pilots[s].bound && self.pilots[s].sees_asteroids.contains(&target_id))
            .map(|watcher| Out::Notify {
                session: watcher,
                name: "OnOreMinedByOther",
                id_type: "charid",
                args: vec![crate::wire::id(target_id), crate::wire::int(taken)],
            })
            .collect();
        if units_remaining <= 0 {
            out.extend(self.remove_asteroid(target_id, true, now));
        } else if let Some(asteroid) = self.asteroids.get_mut(&target_id) {
            asteroid.units_remaining = units_remaining;
        }
        out
    }

    /// What the client of the ship an effect is projected at is told, for its HUD (`hudIconStates.js` of the reference): as the effect goes on `OnJamStart` (who, which module,
    /// the target, what the HUD calls it, when and for how long: a cycle and a tick over), `OnEwarStart` and the buff bar refreshed; as each cycle begins the timer again; as it ends
    /// `OnJamEnd`, `OnEwarEnd` and the buff bar. An NPC is told nothing.
    pub(crate) fn jam_told(&self, source: SessionId, effect: &Effect, at: i64, phase: Jam) -> Vec<Out> {
        let Some(jam) = effect.jam() else { return Vec::new() };
        let Some(target) = self.session_of(effect.target_id).filter(|t| !crate::npc::is_npc_session(*t))
        else {
            return Vec::new();
        };
        let Some(source_ship) = self.pilots.get(&source).map(|pilot| pilot.ship.spec.ship_id) else {
            return Vec::new();
        };
        let who = || vec![id(source_ship), id(effect.module_id), id(effect.target_id), text(jam)];
        let mut out = Vec::new();
        if phase != Jam::End {
            let mut args = who();
            args.push(filetime(at));
            args.push(int(effect.duration_ms as i64 + 100));
            out.push(Self::dogma_notify(target, "OnJamStart", args));
        }
        match phase {
            Jam::Start => {
                out.push(Self::dogma_notify(target, "OnEwarStart", who()));
                out.push(Self::dogma_notify(target, "OnRefreshBuffBar", Vec::new()));
            }
            Jam::Refresh => {}
            Jam::End => {
                out.push(Self::dogma_notify(target, "OnJamEnd", who()));
                out.push(Self::dogma_notify(target, "OnEwarEnd", who()));
                out.push(Self::dogma_notify(target, "OnRefreshBuffBar", Vec::new()));
            }
        }
        out
    }

    fn act(&mut self, session: SessionId, effect: &Effect, when: Applies, at: i64) -> Vec<Out> {
        if let Some(Action::Charged { .. }) = effect.action.as_ref() {
            let charged_this_cycle = self
                .pilots
                .get(&session)
                .and_then(|pilot| {
                    pilot.modules.running.iter().find(|run| run.effect.module_id == effect.module_id)
                })
                .is_some_and(|run| run.charged_this_cycle);
            let Some(action) =
                effect.action.as_ref().and_then(|action| action.with_charges(charged_this_cycle))
            else {
                return Vec::new();
            };
            let mut selected = effect.clone();
            selected.action = Some(action.clone());
            return self.act(session, &selected, when, at);
        }
        match effect.action.as_ref() {
            Some(Action::Repair { layer, amount, when: applies }) if *applies == when => {
                let Some(ship_id) = self.pilots.get(&session).map(|pilot| pilot.ship.spec.ship_id) else {
                    return Vec::new();
                };
                self.mend(ship_id, *layer, *amount, at)
            }
            Some(Action::Remote { what, reach, when: applies, .. }) if *applies == when => {
                self.project(session, effect, what, reach, at)
            }
            Some(Action::Smartbomb { range, damage, hurts_pilots }) if when == Applies::CycleStart => {
                self.smartbomb(session, effect, *range, *damage, *hurts_pilots, at)
            }
            Some(Action::Strike { strike }) if when == Applies::CycleStart => self.strike_start(session, effect, *strike, at),
            Some(Action::Field { strike, .. }) if when == Applies::CycleStart => self.strike_start(session, effect, *strike, at),
            Some(Action::Doomsday { immobile_ms, no_jump_ms, .. }) if when == Applies::CycleStart => {
                self.hold_for_windows(session, *immobile_ms, *no_jump_ms, at);
                if let Some(run) = self.pilots.get_mut(&session).and_then(|pilot| pilot.modules.running.iter_mut().find(|run| run.effect.module_id == effect.module_id)) {
                    run.doomsday_hit = false;
                }
                Vec::new()
            }
            Some(Action::EcmBurst { range, strength }) if when == Applies::CycleStart => {
                self.jam_burst(session, strength, *range, at)
            }
            Some(Action::ResistanceShift { attribute_ids, .. }) if when == Applies::CycleEnd => {
                self.shift_armor(session, *attribute_ids, at)
            }
            Some(Action::PassiveLock) if when == Applies::CycleStart => self.passive_lock(session, effect.target_id, at),
            Some(Action::AutoTarget { range }) if when == Applies::CycleStart => self.auto_target_sweep(session, *range, at),
            Some(Action::Tractor { velocity, hold, .. }) if when == Applies::CycleStart => {
                self.tractor_pull(session, effect.target_id, *velocity, *hold, at)
            }
            Some(Action::Cloak) if when == Applies::CycleStart => {
                let changed = if let Some(pilot) = self.pilots.get_mut(&session) {
                    let changed = pilot.cloak != crate::visibility::CloakState::Cloaked;
                    pilot.cloak = crate::visibility::CloakState::Cloaked;
                    changed
                } else {
                    false
                };
                if changed {
                    self.boxes.clear();
                }
                Vec::new()
            }
            _ => Vec::new(),
        }
    }

    /// A passive targeter's cycle starts: its target is locked through the one lock path, with the target not told (EVE.js `suppressTargetNotice`). A lock the
    /// pilot has already is answered as it is; a refusal (range, capacity, a target that is not there) is told to the pilot as any lock's is.
    fn passive_lock(&mut self, session: SessionId, target: i64, at: i64) -> Vec<Out> {
        self.lock(session, target, &Order::Lock(target), LockOrigin::Passive { suppress_notice: true }, at)
    }

    /// The attributes a running reactive armor hardener tells the armor's resonances as, if one runs.
    pub(crate) fn shift_attributes(&self, session: SessionId) -> Option<[i32; 4]> {
        self.pilots.get(&session)?.modules.running.iter().find_map(|run| match run.effect.action.as_ref() {
            Some(Action::ResistanceShift { attribute_ids, .. }) => Some(*attribute_ids),
            _ => None,
        })
    }

    /// A cycle of a reactive armor hardener ends: the armor's resonances move as the damage it took in the cycle says, and the pilot is told the ones that
    /// changed (the attributes of the ship, the values they now have, and the ones they had). Nothing is told when they do not move.
    fn shift_armor(&mut self, session: SessionId, attribute_ids: [i32; 4], at: i64) -> Vec<Out> {
        let Some(pilot) = self.pilots.get_mut(&session) else { return Vec::new() };
        let (owner, ship_id) = (pilot.ship.spec.character_id, pilot.ship.spec.ship_id);
        let before = pilot.ship.health.armor_resonance();
        if pilot.ship.health.shift_cycle().is_none() {
            return Vec::new();
        }
        let after = pilot.ship.health.armor_resonance();
        let changes = armor_changes(&attribute_ids, &before, &after);
        Self::health_told(session, owner, ship_id, at, &changes).into_iter().collect()
    }

    pub(crate) fn apart(&self, a: SessionId, b: SessionId) -> Option<f64> {
        let (a, b) = (&self.pilots.get(&a)?.ship, &self.pilots.get(&b)?.ship);
        crate::geo::surface_gap(
            [a.position.x, a.position.y, a.position.z],
            a.spec.radius,
            [b.position.x, b.position.y, b.position.z],
            b.spec.radius,
        )
    }

    pub(crate) fn spatial_ship(
        &self,
        session: SessionId,
        pilot: &crate::scene::Pilot,
    ) -> crate::geo::SpatialEntity<SessionId> {
        let spec = &pilot.ship.spec;
        crate::geo::SpatialEntity {
            id: spec.ship_id,
            kind: if self.drones.contains_key(&session) {
                crate::geo::SpatialKind::Drone
            } else if pilot.ship.npc.is_some() {
                crate::geo::SpatialKind::NpcShip
            } else {
                crate::geo::SpatialKind::PlayerShip
            },
            character_id: (spec.character_id > 0).then_some(spec.character_id),
            owner_id: (spec.owner_id > 0).then_some(spec.owner_id),
            corporation_id: (spec.corporation_id > 0).then_some(spec.corporation_id),
            alliance_id: (spec.alliance_id > 0).then_some(spec.alliance_id),
            type_id: Some(spec.type_id),
            group_id: Some(spec.group_id),
            category_id: Some(spec.category_id),
            position: [pilot.ship.position.x, pilot.ship.position.y, pilot.ship.position.z],
            radius: spec.radius,
            warping: pilot.flight.as_ref().is_some_and(|flight| flight.is_warping()),
            item: session,
        }
    }

    /// A cycle of an effect that is projected at a ship: what lands is `what` weakened by how far the target is (nothing when it is out of reach). An attack is
    /// an aggression of the ship that runs the effect.
    pub(crate) fn project(
        &mut self,
        session: SessionId,
        effect: &Effect,
        what: &Remote,
        reach: &Reach,
        at: i64,
    ) -> Vec<Out> {
        let Some(target) = self.session_of(effect.target_id) else { return Vec::new() };
        let Some(share) = self.apart(session, target).and_then(|apart| reach.share(apart)) else {
            return Vec::new();
        };
        // a neutralizer made for bigger ships is weaker against a ship that is smaller than it resolves
        let resolved = |resolution: f64| {
            let signature = self.pilots.get(&target).map_or(0.0, |pilot| pilot.ship.spec.signature_radius);
            if resolution > 0.0 && signature > 0.0 { (signature / resolution).min(1.0) } else { 1.0 }
        };
        let out = match what {
            Remote::Web { .. }
            | Remote::Paint { .. }
            | Remote::Damp { .. }
            | Remote::Scramble { .. }
            | Remote::Tackle { .. }
            | Remote::Disrupt { .. }
            | Remote::GuidanceDisrupt { .. } => {
                let lasting = what.lasting(share).expect("these last");
                self.hold(session, effect, lasting, at)
            }
            Remote::Jam { strength } => self.jam(session, effect, strength, share, at),
            Remote::Repair { layer, amount } => self.mend_remote(effect.target_id, *layer, amount * share, at),
            Remote::MutadaptiveRepair { .. } => Vec::new(),
            Remote::Transfer { amount } => self.give_capacitor(target, amount * share, at),
            Remote::Neutralize { amount, signature_resolution } => {
                let strength = amount
                    * share
                    * resolved(*signature_resolution)
                    * self.capacitor_warfare_multiplier(target);
                self.take_capacitor(target, strength, at).0
            }
            Remote::Drain { amount, signature_resolution } => {
                let strength = amount * share * resolved(*signature_resolution);
                self.drain_capacitor(session, target, strength, at)
            }
        };
        if what.hostile() {
            let attacker_ship = self.pilots.get(&session).map_or(0, |pilot| pilot.ship.spec.ship_id);
            let target_type_id = self.pilots.get(&target).map_or(0, |pilot| pilot.ship.spec.type_id);
            let source_owner_id = self.combat_owner(session);
            self.aggressions.push(crate::weapons::Aggression {
                attacker_ship,
                target_ship: effect.target_id,
                module_type_id: effect.module_type,
                source_owner_id,
                target_type_id,
            });
        }
        out
    }

    /// `amount` GJ are put into the capacitor of the ship of `target`; its pilot is told the charge. A ship that has none, or is full, gets nothing.
    fn give_capacitor(&mut self, target: SessionId, amount: f64, at: i64) -> Vec<Out> {
        let Some(pilot) = self.pilots.get_mut(&target) else { return Vec::new() };
        let (owner, ship) = (pilot.ship.spec.character_id, pilot.ship.spec.ship_id);
        let Some(capacitor) = pilot.modules.capacitor.as_mut() else { return Vec::new() };
        if amount <= 0.0 || capacitor.charge_at(at) >= capacitor.capacity {
            return Vec::new();
        }
        let (before, after) = capacitor.give(at, amount);
        vec![Self::charge_told(target, owner, ship, at, after, before)]
    }

    /// Up to `amount` GJ are taken out of the capacitor of the ship of `target` (never more than it holds); its pilot is told the charge. Answers what was taken.
    /// A strike's cycle starts: the ship stops and is held for its windows, every ship in the neutralising radius loses its capacitor, and the pulses are
    /// counted from zero (the first is due at the warning).
    /// Remote repair from another ship's module: half of it lands on a ship the disruptive lance has disrupted (`DISRUPTED_REMOTE_REPAIR`, protocol 28).
    pub(crate) fn mend_remote(&mut self, target_id: i64, layer: Layer, amount: f64, now: i64) -> Vec<Out> {
        let disrupted = self.session_of(target_id).and_then(|session| self.pilots.get(&session)).is_some_and(|pilot| pilot.disrupted_until > now);
        let factor = if disrupted { DISRUPTED_REMOTE_REPAIR } else { 1.0 };
        self.mend(target_id, layer, amount * factor, now)
    }

    fn strike_start(&mut self, session: SessionId, effect: &Effect, strike: Strike, at: i64) -> Vec<Out> {
        self.hold_for_windows(session, strike.immobile_ms, strike.no_jump_ms, at);
        if let Some(run) = self.pilots.get_mut(&session).and_then(|pilot| pilot.modules.running.iter_mut().find(|run| run.effect.module_id == effect.module_id)) {
            run.strike_done = 0;
        }
        self.strike_neutralise(session, strike, at)
    }

    /// The ship of a doomsday's cycle is held: it stops, follows no order and cannot warp or jump for `immobile_ms`, and cannot jump or cloak for `no_jump_ms`
    /// (both from the start of the cycle, the reference's `immobilityDuration` and `noJumpOrCloakDuration`).
    fn hold_for_windows(&mut self, session: SessionId, immobile_ms: f64, no_jump_ms: f64, at: i64) {
        let per_ms = TICKS_PER_SECOND as f64 / 1000.0;
        let Some(pilot) = self.pilots.get_mut(&session) else { return };
        pilot.immobile_until = pilot.immobile_until.max(at + (immobile_ms * per_ms) as i64);
        pilot.no_jump_until = pilot.no_jump_until.max(at + (no_jump_ms * per_ms) as i64);
        pilot.ship.velocity = eve_destiny::Vec3::new(0.0, 0.0, 0.0);
    }

    /// The titan doomsday's hit: the locked target takes the damage once, if it is still in the scene and not under its undocking protection (the
    /// reference's `tickDoomsdayEffect`, which hits the primary target alone: a titan has no lightning chain).
    fn doomsday_hit(&mut self, session: SessionId, index: usize, at: i64) -> Vec<Out> {
        let (module_type, target_id, damage) = {
            let Some(pilot) = self.pilots.get_mut(&session) else { return Vec::new() };
            let Some(run) = pilot.modules.running.get_mut(index) else { return Vec::new() };
            let Some(Action::Doomsday { damage, .. }) = run.effect.action.clone() else { return Vec::new() };
            if run.doomsday_hit {
                return Vec::new();
            }
            run.doomsday_hit = true;
            (run.effect.module_type, run.effect.target_id, damage)
        };
        let Some(target_session) = self.session_of(target_id) else { return Vec::new() };
        if self.pilots.get(&target_session).is_some_and(|target| target.protected_until > at) {
            return Vec::new();
        }
        self.damage_ship(session, module_type, (target_session, target_id), damage, at)
    }

    /// The energy neutralisation of a strike: each other ship whose surface is within `neut_radius` of the firing ship loses `neut_amount` times its
    /// signature factor (the same factor a neutralizer uses). Target resistances are not applied (the existing neutralizers do not apply them either).
    fn strike_neutralise(&mut self, session: SessionId, strike: Strike, at: i64) -> Vec<Out> {
        if strike.neut_amount <= 0.0 || strike.neut_radius <= 0.0 {
            return Vec::new();
        }
        let Some(centre) = self.pilots.get(&session).map(|pilot| pilot.ship.position) else { return Vec::new() };
        let targets: Vec<(SessionId, f64)> = self
            .in_entry_order()
            .into_iter()
            .filter(|other| *other != session)
            .filter_map(|other| {
                let pilot = self.pilots.get(&other)?;
                let p = pilot.ship.position;
                let gap = ((p.x - centre.x).powi(2) + (p.y - centre.y).powi(2) + (p.z - centre.z).powi(2)).sqrt() - pilot.ship.spec.radius.max(0.0);
                let signature = pilot.ship.spec.signature_radius;
                let factor = if signature > 0.0 { (signature / strike.neut_signature_radius).min(1.0) } else { 1.0 };
                let factor = factor * capacitor_warfare_multiplier(pilot.ship.spec.energy_warfare_resistance);
                (gap <= strike.neut_radius).then_some((other, factor))
            })
            .collect();
        let mut out = Vec::new();
        for (target, factor) in targets {
            out.extend(self.take_capacitor(target, strike.neut_amount * factor, at).0);
        }
        out
    }

    /// What the target's capacitor warfare resistance leaves of a neutralisation (`energyWarfareResistance`, 2045).
    fn capacitor_warfare_multiplier(&self, target: SessionId) -> f64 {
        self.pilots
            .get(&target)
            .map_or(1.0, |pilot| capacitor_warfare_multiplier(pilot.ship.spec.energy_warfare_resistance))
    }

    /// The next pulse of a strike is due: every ship in the area is hit by the pulse's damage times its signature factor, and the pulse is counted.
    /// A transport field's pulse (protocol 29): the ships of `moved` inside its sphere are carried to `destination` (an offset from the firing ship's
    /// centre) and stop; the ships of `held` inside it cannot warp for `warp_block_ms`. The sphere is centred on the aim point, `centre + direction *
    /// length`. A protected ship (undocking) is not reached, and a ship that is warping or jumping is not carried.
    #[allow(clippy::too_many_arguments)]
    fn transport_field(
        &mut self,
        session: SessionId,
        strike: Strike,
        centre: eve_destiny::Vec3,
        destination: [f64; 3],
        warp_block_ms: f64,
        moved: &[i64],
        held: &[i64],
        at: i64,
    ) -> Vec<Out> {
        let sphere = [
            centre.x + strike.direction[0] * strike.length,
            centre.y + strike.direction[1] * strike.length,
            centre.z + strike.direction[2] * strike.length,
        ];
        let target = [centre.x + destination[0], centre.y + destination[1], centre.z + destination[2]];
        let per_ms = TICKS_PER_SECOND as f64 / 1000.0;
        let warp_until = at + (warp_block_ms * per_ms) as i64;
        let reached: Vec<SessionId> = self
            .in_entry_order()
            .into_iter()
            .filter(|other| *other != session)
            .filter(|other| {
                self.pilots.get(other).is_some_and(|pilot| {
                    let p = pilot.ship.position;
                    pilot.protected_until <= at
                        && strike.covers(sphere, [p.x, p.y, p.z], pilot.ship.spec.radius.max(0.0))
                })
            })
            .collect();
        for other in reached {
            let Some(pilot) = self.pilots.get_mut(&other) else { continue };
            let ship_id = pilot.ship.spec.ship_id;
            if moved.contains(&ship_id) {
                if pilot.flight.is_some() || pilot.jump.is_some() {
                    continue;
                }
                pilot.ship.position = eve_destiny::Vec3::new(target[0], target[1], target[2]);
                pilot.ship.velocity = eve_destiny::Vec3::new(0.0, 0.0, 0.0);
            } else if held.contains(&ship_id) {
                pilot.warp_blocked_until = pilot.warp_blocked_until.max(warp_until);
            }
        }
        Vec::new()
    }

    fn strike_pulse(&mut self, session: SessionId, index: usize, at: i64) -> Vec<Out> {
        let (module_type, pulse, strike, centre, radius, transport) = {
            let Some(pilot) = self.pilots.get_mut(&session) else { return Vec::new() };
            let Some(run) = pilot.modules.running.get_mut(index) else { return Vec::new() };
            let Some(action) = run.effect.action.clone() else { return Vec::new() };
            let Some(strike) = strike_of(Some(&action)) else { return Vec::new() };
            if run.strike_done >= strike.pulses {
                return Vec::new();
            }
            let pulse = run.strike_done;
            run.strike_done += 1;
            let transport = match action {
                Action::Field { destination, warp_block_ms, moved, held, .. } => {
                    Some((destination, warp_block_ms, moved, held))
                }
                _ => None,
            };
            (
                run.effect.module_type,
                pulse,
                strike,
                pilot.ship.position,
                pilot.ship.spec.radius.max(0.0),
                transport,
            )
        };
        // a transport field carries its ships at its pulse and deals no damage
        if let Some((destination, warp_block_ms, moved, held)) = transport {
            return self.transport_field(
                session,
                strike,
                centre,
                destination,
                warp_block_ms,
                &moved,
                &held,
                at,
            );
        }
        // a sweep (a reaper) has its axis at this pulse; a pulse whose axis has no length hits nothing
        let Some(strike) = strike.at_pulse(pulse) else { return Vec::new() };
        let direction = strike.direction;
        // the area starts at the hull, along the aim (the reference's hull surface origin); a sweep starts at the centre (`resolveSlashDamageVolume`)
        let origin = if strike.sweep_end.is_some() {
            [centre.x, centre.y, centre.z]
        } else {
            [centre.x + direction[0] * radius, centre.y + direction[1] * radius, centre.z + direction[2] * radius]
        };
        let targets: Vec<(SessionId, i64, f64)> = self
            .in_entry_order()
            .into_iter()
            .filter(|other| *other != session)
            .filter_map(|other| {
                let target = self.pilots.get(&other)?;
                if target.protected_until > at {
                    return None;
                }
                let p = target.ship.position;
                let target_radius = target.ship.spec.radius.max(0.0);
                if !strike.covers(origin, [p.x, p.y, p.z], target_radius) {
                    return None;
                }
                let signature = target.ship.spec.signature_radius;
                let factor = if signature > 0.0 { (signature / strike.signature_radius).min(1.0) } else { 1.0 };
                Some((other, target.ship.spec.ship_id, factor))
            })
            .collect();
        // the debuff lands on every ship the pulse covers, whatever its signature takes of the damage (`applyDisruptiveLanceDebuff`)
        if strike.debuff_ms > 0.0 {
            let until = at + (strike.debuff_ms * TICKS_PER_SECOND as f64 / 1000.0) as i64;
            for (target_session, _, _) in &targets {
                if let Some(pilot) = self.pilots.get_mut(target_session) {
                    pilot.disrupted_until = pilot.disrupted_until.max(until);
                }
            }
        }
        let mut out = Vec::new();
        for (target_session, target_id, factor) in targets {
            let damage = strike.damage.map(|amount| amount * factor);
            out.extend(self.damage_ship(session, module_type, (target_session, target_id), damage, at));
        }
        out
    }

    fn take_capacitor(&mut self, target: SessionId, amount: f64, at: i64) -> (Vec<Out>, f64) {
        let Some(pilot) = self.pilots.get_mut(&target) else { return (Vec::new(), 0.0) };
        let (owner, ship) = (pilot.ship.spec.character_id, pilot.ship.spec.ship_id);
        let Some(capacitor) = pilot.modules.capacitor.as_mut() else { return (Vec::new(), 0.0) };
        let taken = capacitor.charge_at(at).min(amount.max(0.0));
        if taken <= 0.0 {
            return (Vec::new(), 0.0);
        }
        let (before, after) = capacitor.draw(at, taken);
        (vec![Self::charge_told(target, owner, ship, at, after, before)], taken)
    }

    /// A nosferatu: what it can take from the capacitor of `target` goes into that of the ship that runs it. It takes nothing from a ship that holds no more than
    /// it does (by amount, not by share: the reference's `resolveNosTransferAllowed`), and no more than the ship has room for.
    fn drain_capacitor(&mut self, session: SessionId, target: SessionId, amount: f64, at: i64) -> Vec<Out> {
        let level = |scene: &Self, who: SessionId| {
            scene
                .pilots
                .get(&who)
                .and_then(|pilot| pilot.modules.capacitor.as_ref())
                .map(|c| (c.charge_at(at), c.capacity))
        };
        let (Some((own, room)), Some((theirs, _))) = (level(self, session), level(self, target)) else {
            return Vec::new();
        };
        if own + 1e-6 >= theirs {
            return Vec::new();
        }
        let wanted = amount.min(theirs).min((room - own).max(0.0));
        let (mut out, taken) = self.take_capacitor(target, wanted, at);
        if taken > 0.0 {
            out.extend(self.give_capacitor(session, taken, at));
        }
        out
    }

    /// An effect is over: the client is told the values it gave back, that it is off, and the ship's numbers and the effect are taken off in one update.
    /// An effect cut short by the loss of its target (`target_lost`) is told in the other order: the update first, then the values, then that it is off
    /// (the recording of the reference's gun).
    pub(crate) fn effect_ended(
        &mut self,
        session: SessionId,
        run: Running,
        owner: i64,
        ship_id: i64,
        at: i64,
        ending: Ending,
    ) -> Vec<Out> {
        let (target_lost, out_of_rounds) =
            (matches!(ending, Ending::TargetLost | Ending::Forced), ending == Ending::NoRounds);
        // a reactive armor hardener that stops leaves the armor its own resonances, and keeps nothing for a later start
        if matches!(run.effect.action, Some(Action::ResistanceShift { .. }))
            && let Some(pilot) = self.pilots.get_mut(&session)
        {
            pilot.ship.health.stop_shift();
        }
        let mut out = Vec::new();
        let changes = Self::changes_told(session, owner, &run.effect, &run.effect.off_changes, at);
        let error = out_of_rounds.then(|| no_charges(run.effect.module_type));
        let off = Self::effect_told(session, owner, ship_id, &run, at, false, run.cycle_start, error);
        let stamp = self.next_stamp(at);
        let mut actions = match run.effect.off {
            Some(numbers) => self.renumber(session, numbers, stamp),
            None => Vec::new(),
        };
        actions.push(Self::fx_action(stamp, &id(ship_id), &run.effect, false));
        let update = self.tell_all(session, actions);
        if target_lost {
            out.extend(update);
            out.extend(changes);
            out.push(off);
        } else {
            out.extend(changes);
            out.push(off);
            out.extend(update);
        }
        out.extend(self.jam_told(session, &run.effect, at, Jam::End));
        // a tractor beam that stops lets go of the wreck it pulled (another ship's pull on that wreck is left alone)
        if let Some(Action::Tractor { .. }) = run.effect.action {
            out.extend(self.tractor_release(run.effect.target_id, ship_id, at));
        }
        // what it did to its target for as long as it ran is lifted
        if run
            .effect
            .remote()
            .is_some_and(|(what, ..)| what.lasting(1.0).is_some() || matches!(what, Remote::Jam { .. }))
        {
            out.extend(self.release(ship_id, run.effect.module_id, run.effect.target_id, at));
        }
        if matches!(run.effect.action.as_ref(), Some(Action::Cloak)) {
            let changed = if let Some(pilot) = self.pilots.get_mut(&session) {
                let changed = pilot.cloak != crate::visibility::CloakState::Uncloaked;
                pilot.cloak = crate::visibility::CloakState::Uncloaked;
                changed
            } else {
                false
            };
            if changed {
                self.boxes.clear();
            }
        }
        // the host is told every end once, so the ship it sends no longer has the module running (FP-C1)
        out.push(Out::ModuleEnded { session, module_id: run.effect.module_id, reason: ending.wire_reason() });
        out
    }

    /// A module whose effect is forbidden while it runs is stopped immediately, mid-cycle.
    pub(crate) fn stop_module(&mut self, session: SessionId, module_id: i64, now: i64) -> Vec<Out> {
        self.offline_module(session, module_id, now)
    }

    /// Taking a module offline interrupts its running effect immediately, even during a cycle.
    /// Ported from EVE.js moduleOperation.js at commit 828823a75f211fdecb945b087a26c225f111491f:
    /// https://github.com/Fokiiiiiii/evejs-v0.12.9/blob/828823a75f211fdecb945b087a26c225f111491f/server/src/services/dogma/dogmaService/moduleOperation.js#L2607-L2618
    /// The caller remains responsible for persisting the module's online state.
    pub(crate) fn offline_module(&mut self, session: SessionId, module_id: i64, now: i64) -> Vec<Out> {
        let Some(pilot) = self.pilots.get_mut(&session) else { return Vec::new() };
        let (owner, ship_id) = (pilot.ship.spec.character_id, pilot.ship.spec.ship_id);
        let Some(index) = pilot.modules.running.iter().position(|run| run.effect.module_id == module_id)
        else {
            return Vec::new();
        };
        let run = pilot.modules.running.remove(index);
        self.effect_ended(session, run, owner, ship_id, now, Ending::Forced)
    }

    /// A microwarpdrive that runs on the ship is stopped at once (a warp scrambler that blocks it has landed).
    pub(crate) fn stop_microwarpdrives(&mut self, session: SessionId, now: i64) -> Vec<Out> {
        let Some(pilot) = self.pilots.get_mut(&session) else { return Vec::new() };
        let (owner, ship_id) = (pilot.ship.spec.character_id, pilot.ship.spec.ship_id);
        let (stopped, kept): (Vec<Running>, Vec<Running>) = std::mem::take(&mut pilot.modules.running)
            .into_iter()
            .partition(|run| run.effect.effect_id == MICROWARPDRIVE);
        pilot.modules.running = kept;
        let mut out = Vec::new();
        for run in stopped {
            out.extend(self.effect_ended(session, run, owner, ship_id, now, Ending::Forced));
        }
        out
    }

    pub(crate) fn stop_weapons_at(&mut self, session: SessionId, targets: &[i64], now: i64) -> Vec<Out> {
        let Some(pilot) = self.pilots.get_mut(&session) else { return Vec::new() };
        let (owner, ship_id) = (pilot.ship.spec.character_id, pilot.ship.spec.ship_id);
        let (stopped, kept): (Vec<Running>, Vec<Running>) =
            std::mem::take(&mut pilot.modules.running).into_iter().partition(|run| {
                (run.effect.turret.is_some() || run.effect.remote().is_some())
                    && targets.contains(&run.effect.target_id)
            });
        pilot.modules.running = kept;
        let mut out = Vec::new();
        for run in stopped {
            out.extend(self.effect_ended(session, run, owner, ship_id, now, Ending::TargetLost));
        }
        out
    }
}

/// Where an effect projected at a ship is in what the target's client is told of it.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub(crate) enum Jam {
    /// The effect went on: the timer and the icon.
    Start,
    /// Another cycle began: the timer again.
    Refresh,
    /// The effect is over.
    End,
}

#[derive(Debug, Clone, Copy)]
enum Moment {
    CycleEnd(usize),
    Charge,
    Shield,
    /// A parked work cycle is sent again (same id, same rolls).
    WorkResend(usize),
    /// The target of a parked work cycle is gone: the effect stops.
    WorkStop(usize),
    /// A start parked for fuel is asked again (same id), at this index of the pending starts.
    StartResend(usize),
    /// The next pulse of a strike is due (the running effect at this index).
    StrikePulse(usize),
    /// The hit of a titan doomsday is due (the running effect at this index).
    DoomsdayHit(usize),
}

/// The share of a neutralisation a target takes for its resistance: the value itself, never below 0 (immune), and whole if the value is not a number.
fn capacitor_warfare_multiplier(resistance: f64) -> f64 {
    if resistance.is_finite() { resistance.max(0.0) } else { 1.0 }
}

#[cfg(test)]
pub(crate)  mod tests;
