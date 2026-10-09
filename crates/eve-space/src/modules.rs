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
            Self::Tractor { .. } | Self::PassiveLock | Self::AutoTarget { .. } => true,
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
    /// A tractor beam (tag 9). Its package has not landed: the activation is refused (`NOT_IMPLEMENTED`), so it never runs.
    Tractor { velocity: f64, hold: f64 },
    /// A passive lock (tag 10). Its package has not landed: the activation is refused (`NOT_IMPLEMENTED`), so it never runs.
    PassiveLock,
    /// An auto-target sweep (tag 11). Its package has not landed: the activation is refused (`NOT_IMPLEMENTED`), so it never runs.
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
pub(crate) mod tests {
    use std::sync::Arc;

    use super::*;
    use crate::scene::tests::{NOON, STATION, spec, system};
    use crate::scene::{Command, Out};
    use crate::ship::ShipSpec;

    const A: SessionId = 7;
    const B: SessionId = 8;
    const SECOND: i64 = 10_000_000;
    const MODULE: i64 = 9_988_400_001_940;
    const PLANET: i64 = 40_000_001;

    pub(crate) fn afterburner() -> Effect {
        Effect {
            burns_fuel: false,
            module_id: MODULE,
            module_type: 21857,
            effect_id: 6731,
            fx: "effects.Afterburner".to_string(),
            duration_ms: 10_000.0,
            capacitor_need: 5.0,
            repeat: 1000,
            target_id: 0,
            turret: None,
            action: None,
            on: Some(Numbers { mass: 1_663_000.0, max_velocity: 522.848918, signature_radius: 28.0 }),
            off: Some(Numbers { mass: 1_163_000.0, max_velocity: 339.25, signature_radius: 28.0 }),
            on_changes: vec![
                Change { item_id: MODULE, attribute_id: 20, value: 60.0 },
                Change { item_id: MODULE, attribute_id: 73, value: 10_000.0 },
                Change { item_id: spec().ship_id, attribute_id: 37, value: 522.848918 },
            ],
            off_changes: vec![
                Change { item_id: MODULE, attribute_id: 20, value: 60.0 },
                Change { item_id: MODULE, attribute_id: 73, value: 10_000.0 },
                Change { item_id: spec().ship_id, attribute_id: 37, value: 339.25 },
            ],
        }
    }

    fn micro_jump_drive() -> Effect {
        Effect {
            burns_fuel: false,
            module_id: MODULE + 200,
            module_type: 4383,
            effect_id: 4921,
            fx: "effects.MicroJumpDriveEngage".to_string(),
            duration_ms: 12_000.0,
            capacitor_need: 0.0,
            repeat: 1,
            action: Some(Action::MicroJumpDrive { distance_meters: 100_000.0, cooldown_ms: 180_000 }),
            on: None,
            off: None,
            on_changes: Vec::new(),
            off_changes: Vec::new(),
            ..afterburner()
        }
    }

    fn other_spec() -> ShipSpec {
        let mut other = spec();
        other.ship_id += 4;
        other.character_id += 1;
        other.owner_id += 1;
        other
    }

    /// A ship that has bound the ballpark, and one that looks on, thirty seconds after they undocked.
    fn crowd() -> (Scene, i64) {
        crowd_in((*system()).clone())
    }

    /// The same, in a system that has a planet (to warp to).
    fn crowd_with_a_planet() -> (Scene, i64) {
        let mut world = (*system()).clone();
        world.bodies.push(crate::world::Body {
            id: PLANET,
            type_id: 2015,
            group_id: 7,
            category_id: 2,
            owner_id: 0,
            name: "Testing III".to_string(),
            kind: crate::world::BodyKind::Planet,
            position: eve_destiny::Vec3::new(45_000_000_000.0, 0.0, 20_000_000_000.0),
            radius: 6_000_000.0,
            graphic_id: None,
            detail: crate::world::Detail::None,
        });
        crowd_in(world)
    }

    fn crowd_in(world: crate::world::SolarSystem) -> (Scene, i64) {
        let mut scene = Scene::new(Arc::new(world));
        scene.undock(A, spec(), STATION, NOON).unwrap();
        scene.bind(A, NOON);
        scene.undock(B, other_spec(), STATION, NOON).unwrap();
        scene.bind(B, NOON);

        let station = scene.system().body(STATION).unwrap().position;
        let ship_a = scene.pilots[&A].ship.position;
        let radial = (ship_a.x - station.x).hypot(ship_a.y - station.y).max(1.0);
        let ship_b = eve_destiny::Vec3::new(
            ship_a.x - (ship_a.y - station.y) / radial * 5_000.0,
            ship_a.y + (ship_a.x - station.x) / radial * 5_000.0,
            ship_a.z,
        );
        for (session, position) in [(A, ship_a), (B, ship_b)] {
            let ship = &mut scene.pilots.get_mut(&session).unwrap().ship;
            ship.position = position;
            ship.velocity = eve_destiny::Vec3::ZERO;
            ship.speed_fraction = 0.0;
            ship.course = crate::ship::Course::Stop;
        }

        let t = NOON + 40 * SECOND;
        scene.poll(t);
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

    fn label(out: &Out) -> String {
        match out {
            Out::Notify { session, name, args, .. } if *name == "DoDestinyUpdate" => {
                let Some(Value::List(actions)) = args.first() else { panic!("{args:?}") };
                let names: Vec<String> = actions
                    .iter()
                    .map(|a| {
                        let Value::Tuple(pair) = a else { panic!() };
                        let Value::Tuple(inner) = &pair[1] else { panic!() };
                        let Value::Str(name) = &inner[0] else { panic!() };
                        name.to_string()
                    })
                    .collect();
                format!("{session}:destiny[{}]", names.join(","))
            }
            Out::Notify { session, name, args, .. } if *name == "OnModuleAttributeChanges" => {
                let Some(Value::List(lines)) = args.first() else { panic!("{args:?}") };
                let attributes: Vec<String> = lines
                    .iter()
                    .map(|l| {
                        let Value::Tuple(t) = l else { panic!() };
                        match &t[3] {
                            Value::Int(n) => n.to_string(),
                            other => panic!("{other:?}"),
                        }
                    })
                    .collect();
                format!("{session}:attributes[{}]", attributes.join(","))
            }
            Out::Notify { session, name, .. } => format!("{session}:{name}"),
            Out::Accepted { session, command, .. } => format!("{session}:accepted {command}"),
            Out::Refused { session, command, reason } => format!("{session}:refused {command} {reason}"),
            other => format!("{other:?}"),
        }
    }

    /// What the client is told: the host's messages (the work cycles and the end reports) are not the client's, so they are left out.
    fn labels(outs: &[Out]) -> Vec<String> {
        outs.iter().filter(|out| !matches!(out, Out::ModuleWorkCycle { .. } | Out::ModuleEnded { .. })).map(label).collect()
    }

    fn charge_of(out: &Out) -> Option<(f64, f64)> {
        let Out::Notify { name: "OnModuleAttributeChanges", args, .. } = out else { return None };
        let Some(Value::List(lines)) = args.first() else { return None };
        let Value::Tuple(t) = &lines[0] else { return None };
        (t[3] == Value::Int(18)).then(|| match (&t[5], &t[6]) {
            (Value::Real(new), Value::Real(old)) => (*new, *old),
            other => panic!("{other:?}"),
        })
    }

    fn charges(outs: &[Out]) -> Vec<(f64, f64)> {
        outs.iter().filter_map(charge_of).collect()
    }

    #[test]
    fn the_capacitor_recharges_along_ccps_curve() {
        // from a charge a tenth of a second on: the recorded first sample of the afterburner's charge
        assert!(
            (recharged(144.5, 149.5, 55.25, 0.1) - 144.5449).abs() < 1e-3,
            "{}",
            recharged(144.5, 149.5, 55.25, 0.1)
        );
        // nothing to wait for when it is full, nothing to recharge without time or a recharge time, and never past the capacity
        assert_eq!(recharged(149.5, 149.5, 55.25, 10.0), 149.5);
        assert_eq!(recharged(100.0, 149.5, 55.25, 0.0), 100.0);
        assert_eq!(recharged(100.0, 149.5, 0.0, 10.0), 100.0);
        assert!(recharged(0.0, 149.5, 55.25, 1e6) <= 149.5);
        // it recharges fastest at about a quarter of the capacity, and a long time fills it
        let (low, mid) = (recharged(1.0, 149.5, 55.25, 1.0) - 1.0, recharged(37.0, 149.5, 55.25, 1.0) - 37.0);
        assert!(mid > low, "{mid} {low}");
        assert!((recharged(0.0, 149.5, 55.25, 600.0) - 149.5).abs() < 0.01);
        // the peak is 10 * capacity / (4 * recharge time) a second, 2.5 * capacity / recharge time
        assert!((recharged(37.375, 149.5, 55.25, 0.001) - 37.375) / 0.001 - 2.5 * 149.5 / 55.25 < 0.05);
    }

    #[test]
    fn an_effect_started_takes_capacitor_changes_the_ship_and_tells_the_client_in_the_reference_order() {
        let (mut scene, t) = crowd();
        let out = scene.activate(A, afterburner(), t);
        assert_eq!(
            labels(&out),
            [
                "7:accepted 17",
                "7:attributes[18]",
                "7:destiny[SetBallMass,SetMaxSpeed]",
                "8:destiny[SetBallMass,SetMaxSpeed]",
                "7:attributes[20,73,37]",
                "7:destiny[OnSpecialFX]",
                "8:destiny[OnSpecialFX]",
                "7:OnGodmaShipEffect",
            ]
        );
        assert_eq!(charges(&out), [(144.5, 149.5)]);
        let ship = scene.ship(A, t).unwrap();
        assert_eq!((ship.spec.mass, ship.spec.max_velocity), (1_663_000.0, 522.848918));
        // both updates are at one stamp
        let stamps: Vec<i64> = out
            .iter()
            .filter(|o| label(o).starts_with("7:destiny"))
            .flat_map(|o| match o {
                Out::Notify { args, .. } => {
                    let Some(Value::List(actions)) = args.first() else { panic!() };
                    actions
                        .iter()
                        .map(|a| match a {
                            Value::Tuple(pair) => match &pair[0] {
                                Value::Int(n) => *n,
                                other => panic!("{other:?}"),
                            },
                            other => panic!("{other:?}"),
                        })
                        .collect::<Vec<_>>()
                }
                _ => Vec::new(),
            })
            .collect();
        assert!(stamps.windows(2).all(|w| w[0] == w[1]), "{stamps:?}");
    }

    #[test]
    fn the_effect_says_which_module_which_ship_and_for_how_long_and_the_values_the_client_is_told_are_typed_as_the_reference_typed_them()
     {
        let (mut scene, t) = crowd();
        let out = scene.activate(A, afterburner(), t);
        let Some(Out::Notify { args, .. }) =
            out.iter().find(|o| matches!(o, Out::Notify { name: "OnGodmaShipEffect", .. }))
        else {
            panic!("{out:?}")
        };
        let ship = crate::wire::id(spec().ship_id);
        assert_eq!(args[0], crate::wire::id(MODULE));
        assert_eq!(args[1], Value::Int(6731));
        assert_eq!((&args[3], &args[4]), (&Value::Int(1), &Value::Int(1)), "started and active");
        assert_eq!(args[7], Value::Real(10_000.0));
        assert_eq!(args[8], Value::Int(1000), "the cycles asked for");
        let Value::Tuple(environment) = &args[5] else { panic!() };
        assert_eq!(environment[0], crate::wire::id(MODULE));
        assert_eq!(environment[2], ship);
        assert_eq!(environment[3], Value::None, "no target");
        // a whole value is an int, but the length of a cycle is always a real
        let Some(Out::Notify { args, .. }) = out.iter().find(|o| label(o) == "7:attributes[20,73,37]") else {
            panic!()
        };
        let Some(Value::List(lines)) = args.first() else { panic!() };
        let values: Vec<&Value> = lines
            .iter()
            .map(|l| match l {
                Value::Tuple(t) => &t[5],
                other => panic!("{other:?}"),
            })
            .collect();
        assert_eq!(values, [&Value::Int(60), &Value::Real(10_000.0), &Value::Real(522.848918)]);
    }

    #[test]
    fn the_effect_played_on_the_ship_says_it_is_on_when_it_starts_and_off_when_it_ends_and_names_the_module_the_target_and_the_length()
     {
        fn fx_args(out: &[Out], session: SessionId) -> Vec<Vec<Value>> {
            out.iter()
                .filter_map(|o| match o {
                    Out::Notify { session: s, name: "DoDestinyUpdate", args, .. } if *s == session => {
                        let Some(Value::List(actions)) = args.first() else { return None };
                        actions.iter().find_map(|a| {
                            let Value::Tuple(pair) = a else { return None };
                            let Value::Tuple(inner) = &pair[1] else { return None };
                            let Value::Str(name) = &inner[0] else { return None };
                            let Value::Tuple(arguments) = &inner[1] else { return None };
                            (name == "OnSpecialFX").then(|| arguments.clone())
                        })
                    }
                    _ => None,
                })
                .collect()
        }
        let (mut scene, t) = crowd();
        let mut effect = afterburner();
        effect.target_id = 9_988_400_002_215;
        let started = fx_args(&scene.activate(A, effect, t), 8);
        let ship = crate::wire::id(spec().ship_id);
        assert_eq!(
            started,
            [vec![
                ship.clone(),
                crate::wire::id(MODULE),
                Value::Int(21857),
                crate::wire::id(9_988_400_002_215),
                Value::None,
                Value::Str("effects.Afterburner".into()),
                Value::Int(0),
                Value::Int(1),
                Value::Int(1),
                Value::Int(10_000),
                Value::None,
                Value::None,
                Value::Int(0),
                Value::None,
            ]]
        );
        scene.deactivate(A, MODULE, t + SECOND);
        let (end, _) = poll_until(&mut scene, t, 10.5);
        let ended = fx_args(&end, 8);
        assert_eq!(ended.len(), 1, "{end:?}");
        assert_eq!(ended[0][1..3], [crate::wire::id(MODULE), Value::Int(21857)]);
        assert_eq!((&ended[0][7], &ended[0][8]), (&Value::Int(0), &Value::Int(0)), "off and not active");
        // the target is in the environment of the effect the pilot is told of
        let Some(Out::Notify { args, .. }) =
            end.iter().find(|o| matches!(o, Out::Notify { name: "OnGodmaShipEffect", .. }))
        else {
            panic!()
        };
        let Value::Tuple(environment) = &args[5] else { panic!() };
        assert_eq!(environment[3], crate::wire::id(9_988_400_002_215));
    }

    #[test]
    fn a_ship_that_is_told_to_start_an_effect_it_is_running_is_refused() {
        let (mut scene, t) = crowd();
        scene.activate(A, afterburner(), t);
        assert_eq!(labels(&scene.activate(A, afterburner(), t + SECOND)), ["7:refused 17 ALREADY_ACTIVE"]);
        // a ship that is not in space has no modules to start
        assert_eq!(labels(&scene.activate(99, afterburner(), t)), ["99:refused 17 NO_SHIP_IN_SPACE"]);
    }

    #[test]
    fn an_effect_the_capacitor_cannot_pay_for_is_refused_and_costs_nothing() {
        let (mut scene, t) = crowd();
        let mut greedy = afterburner();
        greedy.capacitor_need = 150.0;
        assert_eq!(labels(&scene.activate(A, greedy, t)), ["7:refused 17 NOT_ENOUGH_CAPACITOR"]);
        // it costs exactly what it holds, and no more
        let mut all = afterburner();
        all.capacitor_need = 149.5;
        assert_eq!(labels(&scene.activate(A, all, t))[0], "7:accepted 17");
    }

    #[test]
    fn an_effect_with_no_cycle_or_no_cycles_is_not_one() {
        let (mut scene, t) = crowd();
        let mut quick = afterburner();
        quick.duration_ms = 0.0;
        assert_eq!(labels(&scene.activate(A, quick, t)), ["7:refused 17 BAD_EFFECT"]);
        let mut none = afterburner();
        none.repeat = 0;
        assert_eq!(labels(&scene.activate(A, none, t)), ["7:refused 17 BAD_EFFECT"]);
    }

    #[test]
    fn starting_an_effect_ends_the_protection_of_a_ship_that_has_just_undocked() {
        let mut scene = Scene::new(Arc::new((*system()).clone()));
        scene.undock(A, spec(), STATION, NOON).unwrap();
        scene.undock(B, other_spec(), STATION, NOON).unwrap();
        let t = NOON + 5 * SECOND;
        assert!(scene.pilots[&A].protected_until > t);
        scene.activate(A, afterburner(), t);
        assert_eq!(scene.pilots[&A].protected_until, 0);
        assert!(scene.pilots[&B].protected_until > t);
    }

    #[test]
    fn a_cycle_that_ends_begins_the_next_and_takes_capacitor_again() {
        let (mut scene, t) = crowd();
        scene.activate(A, afterburner(), t);
        let (before, t2) = poll_until(&mut scene, t, 9.5);
        assert!(
            labels(&before).iter().all(|l| l == "7:attributes[18]"),
            "only the capacitor's charge until the cycle ends: {:?}",
            labels(&before)
        );
        let (after, _) = poll_until(&mut scene, t2, 1.0);
        let told = labels(&after);
        let at = told
            .iter()
            .position(|l| l == "7:OnGodmaShipEffect")
            .expect("the effect goes on for another cycle");
        assert_eq!(told[at - 1], "7:attributes[18]", "the capacitor is drawn just before");
        let drawn = charges(&after);
        let (new, old) = drawn[told[..at].iter().filter(|l| *l == "7:attributes[18]").count() - 1];
        assert!(
            (old - new - 5.0).abs() < 0.2,
            "a cycle costs 5, less what it recharged in between: {old} to {new}"
        );
        // the ship goes on with the effect: its numbers are the same
        assert_eq!(scene.ship(A, t + 11 * SECOND).unwrap().spec.mass, 1_663_000.0);
    }

    #[test]
    fn a_new_cycle_says_when_it_began_and_the_same_effect_again() {
        let (mut scene, t) = crowd();
        scene.activate(A, afterburner(), t);
        let (out, _) = poll_until(&mut scene, t, 10.5);
        let Some(Out::Notify { args, .. }) =
            out.iter().find(|o| matches!(o, Out::Notify { name: "OnGodmaShipEffect", .. }))
        else {
            panic!("{out:?}")
        };
        // when and started are the moment the cycle began: exactly ten seconds after the first
        assert_eq!(args[2], Value::Long(t + 10 * SECOND));
        assert_eq!(args[6], Value::Long(t + 10 * SECOND));
        assert_eq!((&args[3], &args[4], &args[8]), (&Value::Int(1), &Value::Int(1), &Value::Int(1000)));
    }

    #[test]
    fn an_effect_stops_when_it_has_run_the_cycles_asked_for() {
        let (mut scene, t) = crowd();
        let mut once = afterburner();
        once.repeat = 2;
        scene.activate(A, once, t);
        let (out, _) = poll_until(&mut scene, t, 22.0);
        let told = labels(&out);
        assert_eq!(
            told.iter().filter(|l| *l == "7:OnGodmaShipEffect").count(),
            2,
            "one more cycle, then it is off: {told:?}"
        );
        assert_eq!(scene.ship(A, t + 23 * SECOND).unwrap().spec.mass, 1_163_000.0);
    }

    #[test]
    fn an_effect_stops_when_the_capacitor_cannot_pay_for_the_next_cycle() {
        let (mut scene, t) = crowd();
        let mut all = afterburner();
        all.capacitor_need = 149.0; // the first cycle nearly empties it, and it recharges too slowly to pay for another in ten seconds
        scene.activate(A, all, t);
        let (out, _) = poll_until(&mut scene, t, 11.0);
        let told = labels(&out);
        assert!(told.iter().any(|l| l == "7:OnGodmaShipEffect"), "{told:?}");
        assert_eq!(
            scene.ship(A, t + 11 * SECOND).unwrap().spec.mass,
            1_163_000.0,
            "the ship has its own numbers again"
        );
    }

    #[test]
    fn asking_for_an_effect_to_stop_says_so_and_it_stops_at_the_end_of_the_cycle_it_is_in() {
        let (mut scene, t) = crowd();
        scene.activate(A, afterburner(), t);
        let (_, t2) = poll_until(&mut scene, t, 4.0);
        assert_eq!(labels(&scene.deactivate(A, MODULE, t2)), ["7:accepted 16"]);
        // nothing changes until the cycle is over, six seconds on
        let (during, t3) = poll_until(&mut scene, t2, 5.5);
        assert!(labels(&during).iter().all(|l| l == "7:attributes[18]"), "{:?}", labels(&during));
        assert_eq!(scene.ship(A, t3).unwrap().spec.max_velocity, 522.848918);
        let (end, _) = poll_until(&mut scene, t3, 1.0);
        let told: Vec<String> = labels(&end).into_iter().filter(|l| l != "7:attributes[18]").collect();
        assert_eq!(
            told,
            [
                "7:attributes[20,73,37]",
                "7:OnGodmaShipEffect",
                "7:destiny[SetBallMass,SetMaxSpeed,OnSpecialFX]",
                "8:destiny[SetBallMass,SetMaxSpeed,OnSpecialFX]"
            ]
        );
        let ship = scene.ship(A, t + 12 * SECOND).unwrap();
        assert_eq!((ship.spec.mass, ship.spec.max_velocity), (1_163_000.0, 339.25));
        // the effect that ended says it is off, with the start of the cycle it ended
        let Some(Out::Notify { args, .. }) =
            end.iter().find(|o| matches!(o, Out::Notify { name: "OnGodmaShipEffect", .. }))
        else {
            panic!()
        };
        assert_eq!((&args[3], &args[4], &args[8]), (&Value::Int(0), &Value::Int(0), &Value::Bool(false)));
        assert_eq!(args[2], Value::Long(t + 10 * SECOND));
        // and nothing costs capacitor any more
        assert_eq!(labels(&scene.deactivate(A, MODULE, t + 13 * SECOND)), ["7:refused 16 NOT_ACTIVE"]);
    }

    #[test]
    fn a_ship_that_is_not_running_the_effect_or_not_in_space_has_nothing_to_stop() {
        let (mut scene, t) = crowd();
        assert_eq!(labels(&scene.deactivate(A, MODULE, t)), ["7:refused 16 NOT_ACTIVE"]);
        assert_eq!(labels(&scene.deactivate(99, MODULE, t)), ["99:refused 16 NO_SHIP_IN_SPACE"]);
    }

    #[test]
    fn the_charge_is_told_at_once_and_then_every_half_second_a_tenth_of_a_second_on_until_it_is_full() {
        let (mut scene, t) = crowd();
        let mut once = afterburner();
        once.repeat = 1;
        let first = scene.activate(A, once, t);
        let (out, _) = poll_until(&mut scene, t, 3.0);
        let series: Vec<(f64, f64)> = charges(&first).into_iter().chain(charges(&out)).collect();
        assert!(series.len() >= 6, "{series:?}");
        assert_eq!(series[0], (144.5, 149.5));
        // then a sample of the curve at 0.1 s, 0.6 s, 1.1 s…, each with the value a tenth of a second before
        for (k, (new, old)) in series[1..6].iter().enumerate() {
            let seconds = 0.1 + 0.5 * k as f64;
            assert!((new - recharged(144.5, 149.5, 55.25, seconds)).abs() < 1e-6, "{k}: {new}");
            assert!(
                (old - recharged(144.5, 149.5, 55.25, (seconds - 0.1).max(0.0))).abs() < 1e-6,
                "{k}: {old}"
            );
        }
    }

    #[test]
    fn a_capacitor_within_a_gigajoule_of_full_is_full_and_that_is_not_told() {
        let cap =
            Capacitor { capacity: 149.5, recharge_s: 55.25, charge: 148.6, at: NOON, next_sample: None };
        assert_eq!(cap.charge_at(NOON), 149.5);
        let cap = Capacitor { charge: 148.4, ..cap };
        assert!(cap.charge_at(NOON) < 149.5);
        // a ship that draws a little and recharges past the threshold stops being told without a last word
        let (mut scene, t) = crowd();
        let mut small = afterburner();
        small.capacitor_need = 2.0;
        small.repeat = 1;
        scene.activate(A, small, t);
        let (out, _) = poll_until(&mut scene, t, 60.0);
        let told = charges(&out);
        assert!(told.iter().all(|(new, _)| *new < 149.5 - 1.0), "{told:?}");
        assert!(scene.pilots[&A].modules.is_idle());
    }

    #[test]
    fn a_full_capacitor_is_not_told_and_the_telling_stops_when_it_is_full() {
        let (mut scene, t) = crowd();
        let (quiet, _) = poll_until(&mut scene, t, 5.0);
        assert!(charges(&quiet).is_empty(), "{quiet:?}");
        // a small draw fills up again in a while, and then nothing more is told
        let mut small = afterburner();
        small.capacitor_need = 3.0;
        small.repeat = 1;
        scene.activate(A, small, t + 5 * SECOND);
        let (recharging, t2) = poll_until(&mut scene, t + 5 * SECOND, 600.0);
        assert!(!charges(&recharging).is_empty());
        let (after, _) = poll_until(&mut scene, t2, 5.0);
        assert!(charges(&after).is_empty(), "{after:?}");
        assert!(scene.pilots[&A].modules.is_idle());
    }

    #[test]
    fn only_the_pilot_is_told_of_the_effect_the_ones_who_look_on_see_the_ship_change() {
        let (mut scene, t) = crowd();
        let out = scene.activate(A, afterburner(), t);
        for o in &out {
            if let Out::Notify { session: 8, name, .. } = o {
                assert_eq!(*name, "DoDestinyUpdate");
            }
        }
        let (later, _) = poll_until(&mut scene, t, 3.0);
        assert!(later.iter().all(|o| !matches!(o, Out::Notify { session: 8, .. })), "{later:?}");
    }

    #[test]
    fn a_ship_that_goes_faster_with_the_effect_gathers_speed_for_the_new_top_speed() {
        let (mut scene, t) = crowd();
        scene.command(A, Command::GotoDirection(eve_destiny::Vec3::new(1.0, 0.0, 0.0)), t);
        let (_, t) = poll_until(&mut scene, t, 60.0);
        let before = scene.ship(A, t).unwrap().velocity;
        let speed = |v: eve_destiny::Vec3| (v.x * v.x + v.y * v.y + v.z * v.z).sqrt();
        assert!((speed(before) - 339.25).abs() < 1.0, "{}", speed(before));
        scene.activate(A, afterburner(), t);
        let (_, t) = poll_until(&mut scene, t, 9.0);
        assert!(
            speed(scene.ship(A, t).unwrap().velocity) > 450.0,
            "{}",
            speed(scene.ship(A, t).unwrap().velocity)
        );
    }

    #[test]
    fn a_ship_that_leaves_takes_its_effects_with_it() {
        let (mut scene, t) = crowd();
        scene.activate(A, afterburner(), t);
        scene.leave(A, crate::scene::Departure::LoggedOff, t + SECOND);
        let (out, _) = poll_until(&mut scene, t + SECOND, 12.0);
        assert!(out.iter().all(|o| !matches!(o, Out::Notify { session: 7, .. })), "{out:?}");
    }

    /// An armor repairer of the Ibis: 30 points a cycle of five seconds, applied at the end of the cycle.
    fn repairer(layer: Layer, amount: f64, when: Applies) -> Effect {
        Effect {
            burns_fuel: false,
            module_id: MODULE,
            module_type: 3530,
            effect_id: 27,
            fx: "effects.ArmorRepair".to_string(),
            duration_ms: 5_000.0,
            capacitor_need: 20.0,
            repeat: 1000,
            target_id: 0,
            turret: None,
            action: Some(Action::Repair { layer, amount, when }),
            on: None,
            off: None,
            on_changes: Vec::new(),
            off_changes: Vec::new(),
        }
    }

    /// A ship that has lost its shield and half its armor.
    fn hurt() -> (Scene, i64) {
        let (mut scene, t) = crowd();
        // raw damage the layers' resistances let through: enough to strip the 220 shield and 68.75 of the armor
        let raw = [0.0, 600.0, 0.0, 0.0];
        let (_, applied) = scene.damage(spec().ship_id, raw, t);
        assert!(applied.is_some());
        (scene, t)
    }

    fn layers(scene: &Scene, at: i64) -> crate::health::Layers {
        scene.pilots[&A].ship.health.layers_at(at)
    }

    #[test]
    fn an_armor_repairer_mends_the_armor_when_a_cycle_ends_and_tells_the_pilot_and_those_who_look_on() {
        let (mut scene, t) = hurt();
        let armor_before = layers(&scene, t).armor;
        assert!(armor_before < 137.5, "the armor took some: {armor_before}");
        let started = scene.activate(A, repairer(Layer::Armor, 30.0, Applies::CycleEnd), t);
        assert!(matches!(started.first(), Some(Out::Accepted { command: 17, .. })), "{started:?}");
        // nothing is mended while the cycle runs
        assert_eq!(layers(&scene, t + SECOND).armor, armor_before);
        let (out, after) = poll_until(&mut scene, t, 5.1);
        let armor = layers(&scene, after).armor;
        assert!((armor - (armor_before + 30.0).min(137.5)).abs() < 1e-3, "{armor} from {armor_before}");
        // the pilot is told the armor that is gone (attribute 266), those who look on its damage state
        let told = labels(&out);
        assert!(told.iter().any(|l| l.starts_with("7:attributes[") && l.contains("266")), "{told:?}");
        assert!(told.iter().any(|l| l.contains("OnDamageStateChange") || l.contains("destiny")), "{told:?}");
        assert!(told.iter().any(|l| l.starts_with("8:destiny[")), "the one who looks on is told: {told:?}");
    }

    #[test]
    fn ancillary_repair_consumes_loaded_charges_each_cycle_then_uses_its_fallback() {
        let (mut scene, t) = hurt();
        let before = layers(&scene, t).armor;
        let mut effect = repairer(Layer::Armor, 10.0, Applies::CycleEnd);
        effect.repeat = 3;
        effect.action = Some(Action::Charged {
            action: Box::new(Action::Repair { layer: Layer::Armor, amount: 10.0, when: Applies::CycleEnd }),
            fallback: Some(Box::new(Action::Repair {
                layer: Layer::Armor,
                amount: 5.0,
                when: Applies::CycleEnd,
            })),
            charge_type_id: 91001,
            charge_flag: 27,
            charge_per_cycle: 1,
            charges: 2,
        });
        let started = scene.activate(A, effect, t);
        assert!(
            started
                .iter()
                .any(|event| matches!(event, Out::ChargeSpent { module_id: MODULE, rounds_left: 1, .. }))
        );
        let (spent, first_cycle) = poll_until(&mut scene, t, 5.1);
        let after_first = layers(&scene, first_cycle).armor;
        assert!(
            spent
                .iter()
                .any(|event| matches!(event, Out::ChargeSpent { module_id: MODULE, rounds_left: 0, .. }))
        );
        assert!((after_first - (before + 10.0).min(137.5)).abs() < 1e-3);
        let (_, second_cycle) = poll_until(&mut scene, first_cycle, 5.1);
        let after_second = layers(&scene, second_cycle).armor;
        assert!((after_second - (after_first + 10.0).min(137.5)).abs() < 1e-3);
        let (third, third_cycle) = poll_until(&mut scene, second_cycle, 5.1);
        assert!(!third.iter().any(|event| matches!(event, Out::ChargeSpent { module_id: MODULE, .. })));
        let after_third = layers(&scene, third_cycle).armor;
        assert!((after_third - (after_second + 5.0).min(137.5)).abs() < 1e-3);
    }

    #[test]
    fn an_active_cloak_hides_a_ship_from_locking_until_the_module_ends() {
        let (mut scene, t) = reaching(5_000.0);
        let ship_id = scene.pilots[&A].ship.spec.ship_id;
        let target_id = scene.pilots[&B].ship.spec.ship_id;
        scene.target_order(A, crate::targeting::Order::Clear, t);
        scene.target_order(B, crate::targeting::Order::Clear, t);
        let mut effect = repairer(Layer::Armor, 1.0, Applies::CycleStart);
        effect.module_type = 11577;
        effect.effect_id = 197;
        effect.fx = "effects.Cloaking".to_string();
        effect.action = Some(Action::Cloak);
        let started = scene.activate(A, effect, t);
        assert!(
            started.iter().any(|event| matches!(event, Out::Accepted { command: 17, .. })),
            "{:?}",
            labels(&started)
        );
        assert_eq!(scene.pilots[&A].cloak, crate::visibility::CloakState::Cloaked);
        for (session, target) in [(B, ship_id), (A, target_id)] {
            let refused = scene.target_order(session, crate::targeting::Order::Lock(target), t);
            assert!(
                refused
                    .iter()
                    .any(|event| matches!(event, Out::TargetRefused { reason: "TARGET_NOT_FOUND", .. }))
            );
        }
        let (_, cloaked_at) = poll_until(&mut scene, t, 1.1);
        assert!(!scene.pilots[&B].sees.contains(&A));
        scene.deactivate(A, MODULE, cloaked_at);
        let (_, ended) = poll_until(&mut scene, cloaked_at, 5.1);
        assert_eq!(scene.pilots[&A].cloak, crate::visibility::CloakState::Uncloaked);
        assert!(scene.pilots[&B].sees.contains(&A));
        assert!(ended >= cloaked_at);
    }

    #[test]
    fn a_power_booster_transfers_to_its_own_ship_and_spends_one_charge_per_cycle() {
        let (mut scene, t) = reaching(2_000.0);
        let ship_id = scene.pilots[&A].ship.spec.ship_id;
        let effect = Effect {
            burns_fuel: false,
            module_id: MODULE,
            module_type: 3566,
            effect_id: 13,
            fx: "effects.CapacitorBooster".to_string(),
            duration_ms: 5_000.0,
            capacitor_need: 0.0,
            repeat: 2,
            target_id: ship_id,
            turret: None,
            action: Some(Action::Charged {
                action: Box::new(Action::Remote {
                    what: Remote::Transfer { amount: 25.0 },
                    reach: Reach { optimal: 0.0, falloff: 0.0 },
                    when: Applies::CycleStart,
                    jam: "energyTransfer".to_string(),
                }),
                fallback: None,
                charge_type_id: 91002,
                charge_flag: 27,
                charge_per_cycle: 1,
                charges: 2,
            }),
            on: None,
            off: None,
            on_changes: Vec::new(),
            off_changes: Vec::new(),
        };
        let started = scene.activate(A, effect, t);
        assert!(started.iter().any(|event| matches!(event, Out::Accepted { command: 17, .. })));
        assert!(
            started
                .iter()
                .any(|event| matches!(event, Out::ChargeSpent { module_id: MODULE, rounds_left: 1, .. }))
        );
        let (second, later) = poll_until(&mut scene, t, 5.1);
        assert!(
            second
                .iter()
                .any(|event| matches!(event, Out::ChargeSpent { module_id: MODULE, rounds_left: 0, .. })),
            "{:?}",
            labels(&second)
        );
        assert!(!scene.pilots[&A].modules.running.iter().any(|run| run.effect.module_id == MODULE));
        assert!(scene.pilots[&A].modules.capacitor.as_ref().unwrap().charge_at(later) > 0.0);
    }

    #[test]
    fn a_shield_booster_gives_its_points_as_the_cycle_begins_and_again_at_each_cycle() {
        let (mut scene, t) = hurt();
        let before = layers(&scene, t).shield;
        let out = scene.activate(A, repairer(Layer::Shield, 40.0, Applies::CycleStart), t);
        // at once, in the answer to the activation
        let shield = layers(&scene, t).shield;
        assert!((shield - (before + 40.0)).abs() < 1e-3, "{shield} from {before}");
        assert!(
            labels(&out).iter().any(|l| l.starts_with("7:attributes[") && l.contains("264")),
            "{:?}",
            labels(&out)
        );
        // and again as the next cycle begins
        let (_, after) = poll_until(&mut scene, t, 5.1);
        let second = layers(&scene, after).shield;
        assert!(second >= shield + 39.0, "{second} from {shield}");
    }

    #[test]
    fn a_repair_gives_back_no_more_than_the_layer_holds_and_a_whole_ship_is_told_nothing_of_it() {
        let (mut scene, t) = crowd();
        scene.activate(A, repairer(Layer::Armor, 30.0, Applies::CycleEnd), t);
        let (out, after) = poll_until(&mut scene, t, 5.1);
        assert_eq!(layers(&scene, after).armor, 137.5);
        let told = labels(&out);
        assert!(!told.iter().any(|l| l.contains("266") || l.contains("OnDamageStateChange")), "{told:?}");
        // the capacitor is still drawn for each cycle: a repairer that has nothing to mend still runs
        assert!(told.iter().any(|l| l == "7:OnGodmaShipEffect"), "{told:?}");
    }

    #[test]
    fn a_repairer_the_pilot_stops_still_mends_at_the_end_of_the_cycle_it_is_in() {
        let (mut scene, t) = hurt();
        let before = layers(&scene, t).armor;
        scene.activate(A, repairer(Layer::Armor, 30.0, Applies::CycleEnd), t);
        scene.deactivate(A, MODULE, t + SECOND);
        let (_, after) = poll_until(&mut scene, t, 5.1);
        assert!((layers(&scene, after).armor - (before + 30.0)).abs() < 1e-3);
        assert_eq!(scene.pilots[&A].modules.running_count(), 0, "and it has stopped");
    }

    // ---- effects projected at a ship ----

    fn partner() -> i64 {
        other_spec().ship_id
    }

    /// A module that runs `what` on the ship `target`, reaching 10 km and falling off through another 5 km.
    fn projected(what: Remote, when: Applies, target: i64) -> Effect {
        Effect {
            burns_fuel: false,
            module_id: MODULE,
            module_type: 3520,
            effect_id: 6186,
            fx: "effects.RemoteArmorRepair".to_string(),
            duration_ms: 5_000.0,
            capacitor_need: 20.0,
            repeat: 1000,
            target_id: target,
            turret: None,
            action: Some(Action::Remote {
                what,
                reach: Reach { optimal: 10_000.0, falloff: 5_000.0 },
                when,
                jam: "remoteArmorRepair".to_string(),
            }),
            on: None,
            off: None,
            on_changes: Vec::new(),
            off_changes: Vec::new(),
        }
    }

    /// B stands `gap` metres off A's surface and A has it locked: the scene, and the moment.
    fn reaching(gap: f64) -> (Scene, i64) {
        reaching_in(crowd(), gap)
    }

    fn reaching_in((mut scene, t): (Scene, i64), gap: f64) -> (Scene, i64) {
        // both ships are brought to rest so that nothing moves while the effect runs
        scene.command(A, Command::Stop, t);
        scene.command(B, Command::Stop, t);
        let (_, t) = poll_until(&mut scene, t, 40.0);
        let here = scene.pilots[&A].ship.position;
        let (ra, rb) = (scene.pilots[&A].ship.spec.radius, scene.pilots[&B].ship.spec.radius);
        let ship = &mut scene.pilots.get_mut(&B).unwrap().ship;
        ship.position = eve_destiny::Vec3 { x: here.x + gap + ra + rb, y: here.y, z: here.z };
        ship.velocity = eve_destiny::Vec3 { x: 0.0, y: 0.0, z: 0.0 };
        let asked = scene.target_order(A, crate::targeting::Order::Lock(partner()), t);
        assert!(matches!(asked.first(), Some(Out::Targets { .. })), "{asked:?}");
        let (_, t) = poll_until(&mut scene, t, 6.0);
        assert_eq!(scene.locked_by(A), vec![partner()]);
        (scene, t)
    }

    fn hurt_partner(scene: &mut Scene, t: i64) {
        let (_, applied) = scene.damage(partner(), [0.0, 600.0, 0.0, 0.0], t);
        assert!(applied.is_some());
    }

    fn partner_layers(scene: &Scene, at: i64) -> crate::health::Layers {
        scene.pilots[&B].ship.health.layers_at(at)
    }

    fn charge(scene: &Scene, who: SessionId, at: i64) -> f64 {
        scene.pilots[&who].modules.capacitor.as_ref().unwrap().charge_at(at)
    }

    #[test]
    fn a_projected_effect_is_whole_in_the_optimal_range_half_at_one_falloff_and_out_of_range_beyond_it() {
        let reach = Reach { optimal: 10_000.0, falloff: 5_000.0 };
        assert_eq!(reach.share(0.0), Some(1.0));
        assert_eq!(reach.share(10_001.0), Some(1.0), "a metre of slack");
        assert_eq!(reach.share(15_000.0), Some(0.5));
        assert_eq!(reach.share(12_500.0), Some(0.840896));
        assert_eq!(reach.share(15_002.0), None);
        assert_eq!(reach.farthest(), 15_000.0);
        // a module with no falloff reaches its optimal range and no more
        let sharp = Reach { optimal: 9_000.0, falloff: 0.0 };
        assert_eq!(sharp.share(9_001.0), Some(1.0));
        assert_eq!(sharp.share(9_002.0), None);
    }

    #[test]
    fn a_remote_armor_repairer_mends_the_target_when_the_cycle_ends_less_for_a_target_in_falloff() {
        // 12.5 km off: half way through the falloff, a share of 0.840896
        let (mut scene, t) = reaching(12_500.0);
        hurt_partner(&mut scene, t);
        let before = partner_layers(&scene, t).armor;
        let started = scene.activate(
            A,
            projected(Remote::Repair { layer: Layer::Armor, amount: 30.0 }, Applies::CycleEnd, partner()),
            t,
        );
        assert!(matches!(started.first(), Some(Out::Accepted { command: 17, .. })), "{started:?}");
        assert_eq!(
            partner_layers(&scene, t + SECOND).armor,
            before,
            "nothing is mended while the cycle runs"
        );
        let (out, after) = poll_until(&mut scene, t, 5.1);
        let armor = partner_layers(&scene, after).armor;
        assert!((armor - (before + 30.0 * 0.840896)).abs() < 1e-3, "{armor} from {before}");
        // the target's pilot is told the armor that came back, the one who runs the repairer is told nothing of it
        let told = labels(&out);
        assert!(told.iter().any(|l| l.starts_with("8:attributes[") && l.contains("266")), "{told:?}");
        assert!(!told.iter().any(|l| l.starts_with("7:attributes[") && l.contains("266")), "{told:?}");
        // its own armor is not what is mended
        assert_eq!(layers(&scene, after).armor, 137.5);
    }

    fn remote_repair_cycle_request(out: &[Out]) -> (u64, i32, i64, i64, bool) {
        out.iter()
            .find_map(|item| match item {
                Out::RemoteRepairCycle { cycle_id, cycle_number, cycle_at_ms, cycle_duration_ms, ends_effect_after_cycle, .. } => {
                    Some((*cycle_id, *cycle_number, *cycle_at_ms, *cycle_duration_ms, *ends_effect_after_cycle))
                }
                _ => None,
            })
            .expect("remote repair cycle request")
    }

    #[test]
    fn mutadaptive_remote_repair_waits_for_host_amount_on_every_cycle_and_marks_the_final_one() {
        let (mut scene, t) = reaching(10_000.0);
        hurt_partner(&mut scene, t);
        let before = partner_layers(&scene, t).armor;
        let mut effect = projected(
            Remote::MutadaptiveRepair { layer: Layer::Armor, amount: 10.0, bonus_per_cycle: 0.1, bonus_max: 1.5 },
            Applies::CycleEnd,
            partner(),
        );
        effect.duration_ms = 6_000.0;
        effect.capacitor_need = 0.0;
        effect.repeat = 3;
        let started = scene.activate(A, effect, t);
        assert!(matches!(started.first(), Some(Out::Accepted { command: 17, .. })), "{started:?}");

        let (out1, at1) = poll_until(&mut scene, t, 6.0);
        let (cycle1, number1, cycle_at1, duration1, ends1) = remote_repair_cycle_request(&out1);
        assert_eq!((number1, duration1, ends1), (1, 6_000, false));
        assert_eq!(partner_layers(&scene, at1).armor, before, "repair waits for the host result");
        assert!(scene.remote_repair_cycle_result(A, MODULE, partner(), cycle1 + 10, true, 999.0).is_empty());
        assert_eq!(partner_layers(&scene, at1).armor, before, "a stale result is ignored");
        scene.remote_repair_cycle_result(A, MODULE, partner(), cycle1, true, 11.0);
        assert!((partner_layers(&scene, at1).armor - (before + 11.0)).abs() < 1e-3);
        assert!(scene.remote_repair_cycle_result(A, MODULE, partner(), cycle1, true, 999.0).is_empty());
        assert!((partner_layers(&scene, at1).armor - (before + 11.0)).abs() < 1e-3, "a replay cannot settle twice");

        let (out2, at2) = poll_until(&mut scene, at1, 6.0);
        let (cycle2, number2, cycle_at2, duration2, ends2) = remote_repair_cycle_request(&out2);
        assert_eq!((number2, duration2, ends2), (2, 6_000, false));
        assert_eq!(cycle_at2 - cycle_at1, 6_000, "cycle timestamps use simulation milliseconds");
        scene.remote_repair_cycle_result(A, MODULE, partner(), cycle2, true, 12.0);
        let after_cycle2 = partner_layers(&scene, at2).armor;
        assert!((after_cycle2 - (before + 23.0)).abs() < 1e-3, "before={before}, after_cycle2={after_cycle2}");

        let (out3, at3) = poll_until(&mut scene, at2, 6.0);
        let (cycle3, number3, _, duration3, ends3) = remote_repair_cycle_request(&out3);
        assert_eq!((number3, duration3, ends3), (3, 6_000, true));
        scene.remote_repair_cycle_result(A, MODULE, partner(), cycle3, true, 13.0);
        assert!((partner_layers(&scene, at3).armor - (before + 36.0)).abs() < 1e-3);
        assert!(matches!(scene.deactivate(A, MODULE, at3).first(), Some(Out::Refused { reason: "NOT_ACTIVE", .. })));
    }

    #[test]
    fn deactivation_during_a_pending_mutadaptive_cycle_finishes_that_cycle_before_stopping() {
        let (mut scene, t) = reaching(10_000.0);
        hurt_partner(&mut scene, t);
        let before = partner_layers(&scene, t).armor;
        let mut effect = projected(
            Remote::MutadaptiveRepair { layer: Layer::Armor, amount: 100.0, bonus_per_cycle: 0.1, bonus_max: 1.5 },
            Applies::CycleEnd,
            partner(),
        );
        effect.duration_ms = 6_000.0;
        effect.capacitor_need = 0.0;
        let started = scene.activate(A, effect, t);
        assert!(matches!(started.first(), Some(Out::Accepted { .. })));
        let (out, at) = poll_until(&mut scene, t, 6.0);
        let (cycle_id, _, _, _, ends_after_cycle) = remote_repair_cycle_request(&out);
        assert!(!ends_after_cycle, "the stop request arrives after the cycle was dispatched");
        assert!(matches!(scene.deactivate(A, MODULE, at).first(), Some(Out::Accepted { command: 16, .. })));
        scene.remote_repair_cycle_result(A, MODULE, partner(), cycle_id, true, 110.0);
        assert!((partner_layers(&scene, at).armor - (before + 110.0)).abs() < 1e-3);
        assert!(matches!(scene.deactivate(A, MODULE, at).first(), Some(Out::Refused { reason: "NOT_ACTIVE", .. })));
    }

    #[test]
    fn deactivation_before_cycle_dispatch_marks_the_request_as_final() {
        let (mut scene, t) = reaching(10_000.0);
        hurt_partner(&mut scene, t);
        let before = partner_layers(&scene, t).armor;
        let mut effect = projected(
            Remote::MutadaptiveRepair { layer: Layer::Armor, amount: 100.0, bonus_per_cycle: 0.1, bonus_max: 1.5 },
            Applies::CycleEnd,
            partner(),
        );
        effect.duration_ms = 6_000.0;
        effect.capacitor_need = 0.0;
        let started = scene.activate(A, effect, t);
        assert!(matches!(started.first(), Some(Out::Accepted { .. })));
        assert!(matches!(scene.deactivate(A, MODULE, t + SECOND).first(), Some(Out::Accepted { .. })));
        let (out, at) = poll_until(&mut scene, t, 6.0);
        let (cycle_id, _, _, _, ends_after_cycle) = remote_repair_cycle_request(&out);
        assert!(ends_after_cycle);
        scene.remote_repair_cycle_result(A, MODULE, partner(), cycle_id, true, 110.0);
        assert!((partner_layers(&scene, at).armor - (before + 110.0)).abs() < 1e-3);
        assert!(matches!(scene.deactivate(A, MODULE, at).first(), Some(Out::Refused { reason: "NOT_ACTIVE", .. })));
    }

    #[test]
    fn offlining_a_mutadaptive_repairer_cancels_its_pending_callback_immediately() {
        let (mut scene, t) = reaching(10_000.0);
        hurt_partner(&mut scene, t);
        let before = partner_layers(&scene, t).armor;
        let mut effect = projected(
            Remote::MutadaptiveRepair { layer: Layer::Armor, amount: 100.0, bonus_per_cycle: 0.1, bonus_max: 1.5 },
            Applies::CycleEnd,
            partner(),
        );
        effect.duration_ms = 6_000.0;
        effect.capacitor_need = 0.0;
        let started = scene.activate(A, effect, t);
        assert!(matches!(started.first(), Some(Out::Accepted { .. })));
        let (out, at) = poll_until(&mut scene, t, 6.0);
        let (cycle_id, ..) = remote_repair_cycle_request(&out);
        scene.offline_module(A, MODULE, at);
        assert!(scene.remote_repair_cycle_result(A, MODULE, partner(), cycle_id, true, 999.0).is_empty());
        assert_eq!(partner_layers(&scene, at).armor, before, "offline cancels the in-flight repair immediately");
    }

    #[test]
    fn a_remote_shield_booster_gives_its_points_as_the_cycle_begins() {
        let (mut scene, t) = reaching(2_000.0);
        hurt_partner(&mut scene, t);
        let before = partner_layers(&scene, t).shield;
        scene.activate(
            A,
            projected(Remote::Repair { layer: Layer::Shield, amount: 40.0 }, Applies::CycleStart, partner()),
            t,
        );
        let shield = partner_layers(&scene, t).shield;
        assert!(shield >= before + 39.0, "{shield} from {before}");
    }

    #[test]
    fn an_effect_projected_at_a_ship_needs_a_target_that_is_there_locked_and_in_reach() {
        let (mut scene, t) = reaching(2_000.0);
        let refused = |out: &[Out]| match out.first() {
            Some(Out::Refused { reason, .. }) => (*reason).to_string(),
            other => panic!("{other:?}"),
        };
        let mend = |target| {
            projected(Remote::Repair { layer: Layer::Armor, amount: 30.0 }, Applies::CycleEnd, target)
        };
        assert_eq!(refused(&scene.activate(A, mend(0), t)), "NO_TARGET");
        assert_eq!(refused(&scene.activate(A, mend(42), t)), "TARGET_NOT_FOUND");
        // the ship that is not locked
        let (mut scene2, t2) = crowd();
        assert_eq!(refused(&scene2.activate(A, mend(partner()), t2)), "TARGET_NOT_LOCKED");
        // too far off: told with the farthest it reaches and how far the target is
        let (mut far, t3) = reaching(16_000.0);
        match far.activate(A, mend(partner()), t3).first() {
            Some(Out::TargetRefused {
                command: 17,
                reason,
                target_type,
                desired_range,
                actual_distance,
                ..
            }) => {
                assert_eq!(*reason, "TARGET_OUT_OF_RANGE");
                assert_eq!(*target_type, 601);
                assert_eq!(*desired_range, 15_000.0);
                assert!((actual_distance - 16_000.0).abs() < 1.0, "{actual_distance}");
            }
            other => panic!("{other:?}"),
        }
        assert_eq!(far.pilots[&A].modules.running_count(), 0);
    }

    #[test]
    fn a_target_that_drifts_out_of_reach_ends_the_effect_at_the_end_of_the_cycle_and_a_lost_lock_ends_it_at_once()
     {
        let (mut scene, t) = reaching(2_000.0);
        scene.activate(
            A,
            projected(Remote::Repair { layer: Layer::Armor, amount: 30.0 }, Applies::CycleEnd, partner()),
            t,
        );
        // the target is pulled out of reach during the cycle
        scene.pilots.get_mut(&B).unwrap().ship.position.x += 40_000.0;
        let before = partner_layers(&scene, t).armor;
        let (_, after) = poll_until(&mut scene, t, 5.1);
        assert_eq!(partner_layers(&scene, after).armor, before, "nothing lands out of reach");
        assert_eq!(scene.pilots[&A].modules.running_count(), 0, "and the effect has stopped");
        // a lock that is given up stops it at once
        let (mut scene, t) = reaching(2_000.0);
        scene.activate(
            A,
            projected(Remote::Repair { layer: Layer::Armor, amount: 30.0 }, Applies::CycleEnd, partner()),
            t,
        );
        scene.target_order(A, crate::targeting::Order::Remove(vec![partner()]), t + SECOND);
        assert_eq!(scene.pilots[&A].modules.running_count(), 0);
    }

    #[test]
    fn a_neutralizer_is_weakened_by_resistance_and_immunity_keeps_the_capacitor() {
        let (mut scene, t) = reaching(5_000.0);
        scene.pilots.get_mut(&B).unwrap().ship.spec.energy_warfare_resistance = 0.5;
        let before = charge(&scene, B, t);
        scene.activate(
            A,
            projected(
                Remote::Neutralize { amount: 40.0, signature_resolution: 0.0 },
                Applies::CycleStart,
                partner(),
            ),
            t,
        );
        let after = charge(&scene, B, t);
        assert!(
            (before - after - 20.0).abs() < 1e-6,
            "half of 40 against a resistance of 0.5: {before} -> {after}"
        );

        scene.pilots.get_mut(&B).unwrap().ship.spec.energy_warfare_resistance = 0.0;
        let immune_before = charge(&scene, B, t + SECOND);
        scene.activate(
            A,
            projected(
                Remote::Neutralize { amount: 40.0, signature_resolution: 0.0 },
                Applies::CycleStart,
                partner(),
            ),
            t + SECOND,
        );
        assert!(
            (charge(&scene, B, t + SECOND) - immune_before).abs() < 1e-6,
            "an immune target keeps its capacitor"
        );
    }

    #[test]
    fn an_energy_neutralizer_takes_capacitor_from_the_target_as_the_cycle_begins_and_is_an_attack() {
        let (mut scene, t) = reaching(5_000.0);
        let before = charge(&scene, B, t);
        let started = scene.activate(
            A,
            projected(
                Remote::Neutralize { amount: 40.0, signature_resolution: 0.0 },
                Applies::CycleStart,
                partner(),
            ),
            t,
        );
        let after = charge(&scene, B, t);
        assert!((before - after - 40.0).abs() < 1e-6, "{before} -> {after}");
        // the target's pilot is told its capacitor, and the effect is played as an attack
        assert!(started.iter().any(|o| o.session() == B && charge_of(o).is_some()), "{started:?}");
        assert_eq!(scene.aggressions.len(), 1);
        assert_eq!(
            (scene.aggressions[0].attacker_ship, scene.aggressions[0].target_ship),
            (spec().ship_id, partner())
        );
        // it takes no more than the target has
        let weak = scene.pilots[&B].modules.capacitor.as_ref().unwrap().capacity;
        assert!(weak > 0.0);
        let (mut scene, t) = reaching(5_000.0);
        scene.activate(
            A,
            projected(
                Remote::Neutralize { amount: 1.0e6, signature_resolution: 0.0 },
                Applies::CycleStart,
                partner(),
            ),
            t,
        );
        assert_eq!(charge(&scene, B, t), 0.0);
    }

    #[test]
    fn a_neutralizer_made_for_bigger_ships_is_weaker_against_a_smaller_target_in_proportion_to_its_signature()
    {
        // the Ibis has a signature of 28 m: against a resolution of 56 m it takes half
        let (mut scene, t) = reaching(1_000.0);
        let before = charge(&scene, B, t);
        scene.activate(
            A,
            projected(
                Remote::Neutralize { amount: 40.0, signature_resolution: 56.0 },
                Applies::CycleStart,
                partner(),
            ),
            t,
        );
        assert!((before - charge(&scene, B, t) - 20.0).abs() < 1e-6);
    }

    #[test]
    fn a_nosferatu_takes_from_a_ship_that_holds_more_than_it_does_and_nothing_from_one_that_holds_less() {
        let (mut scene, t) = reaching(5_000.0);
        // A holds less than B: it drains B and what is taken fills A
        scene.pilots.get_mut(&A).unwrap().modules.capacitor.as_mut().unwrap().draw(t, 100.0);
        let (own, theirs) = (charge(&scene, A, t), charge(&scene, B, t));
        scene.activate(
            A,
            projected(
                Remote::Drain { amount: 25.0, signature_resolution: 0.0 },
                Applies::CycleEnd,
                partner(),
            ),
            t,
        );
        // the cost of the cycle is paid as it starts; what is drained comes at its end
        let paid = charge(&scene, A, t);
        let (_, after) = poll_until(&mut scene, t, 5.1);
        assert!(
            (charge(&scene, B, after) - (theirs - 25.0)).abs() < 1.0,
            "{} from {theirs}",
            charge(&scene, B, after)
        );
        assert!(
            charge(&scene, A, after) > paid + 20.0,
            "{} from {own} ({paid} after the cost)",
            charge(&scene, A, after)
        );
        // A holds more than B: nothing is drained
        let (mut scene, t) = reaching(5_000.0);
        scene.pilots.get_mut(&B).unwrap().modules.capacitor.as_mut().unwrap().draw(t, 100.0);
        let theirs = charge(&scene, B, t);
        scene.activate(
            A,
            projected(
                Remote::Drain { amount: 25.0, signature_resolution: 0.0 },
                Applies::CycleEnd,
                partner(),
            ),
            t,
        );
        let (_, after) = poll_until(&mut scene, t, 5.1);
        // B only recharges
        assert!(charge(&scene, B, after) >= theirs, "{} from {theirs}", charge(&scene, B, after));
    }

    #[test]
    fn a_remote_capacitor_transmitter_gives_the_target_capacitor_up_to_what_it_holds() {
        let (mut scene, t) = reaching(5_000.0);
        scene.pilots.get_mut(&B).unwrap().modules.capacitor.as_mut().unwrap().draw(t, 100.0);
        let before = charge(&scene, B, t);
        scene.activate(A, projected(Remote::Transfer { amount: 60.0 }, Applies::CycleStart, partner()), t);
        assert!((charge(&scene, B, t) - (before + 60.0)).abs() < 1e-6);
        // a full capacitor takes nothing and is told nothing
        let (mut scene, t) = reaching(5_000.0);
        let out = scene.activate(
            A,
            projected(Remote::Transfer { amount: 60.0 }, Applies::CycleStart, partner()),
            t,
        );
        assert!(!out.iter().any(|o| o.session() == B && charge_of(o).is_some()), "{out:?}");
    }

    #[test]
    fn a_projected_effect_plays_to_those_who_look_on_with_its_target_and_for_as_long_as_it_can() {
        let (mut scene, t) = reaching(5_000.0);
        let out = scene.activate(
            A,
            projected(
                Remote::Neutralize { amount: 40.0, signature_resolution: 0.0 },
                Applies::CycleStart,
                partner(),
            ),
            t,
        );
        let effect = projected(
            Remote::Neutralize { amount: 40.0, signature_resolution: 0.0 },
            Applies::CycleStart,
            partner(),
        );
        assert!(effect.offensive());
        assert!(!projected(Remote::Transfer { amount: 1.0 }, Applies::CycleStart, partner()).offensive());
        // twelve hours of 5 s cycles
        assert_eq!(effect.fx_repeat(), Some(8_640));
        assert!(
            labels(&out).iter().any(|l| l.starts_with("8:destiny[")),
            "the one who looks on sees it: {:?}",
            labels(&out)
        );
    }

    #[test]
    fn the_target_is_told_of_an_effect_projected_at_it_for_its_hud_when_it_begins_each_cycle_and_ends() {
        let (mut scene, t) = reaching(5_000.0);
        let started = scene.activate(
            A,
            projected(Remote::Repair { layer: Layer::Armor, amount: 30.0 }, Applies::CycleEnd, partner()),
            t,
        );
        let told = labels(&started);
        let hud: Vec<&String> = told.iter().filter(|l| l.starts_with("8:On")).collect();
        assert!(hud.iter().any(|l| l.as_str() == "8:OnJamStart"), "{told:?}");
        assert!(hud.iter().any(|l| l.as_str() == "8:OnEwarStart"), "{told:?}");
        assert!(hud.iter().any(|l| l.as_str() == "8:OnRefreshBuffBar"), "{told:?}");
        // the timer carries who, the module, the target, what the HUD calls it, when, and a cycle and a tick
        let jam = started
            .iter()
            .find_map(|o| match o {
                Out::Notify { session: 8, name: "OnJamStart", args, .. } => Some(args.clone()),
                _ => None,
            })
            .expect("OnJamStart");
        assert_eq!(
            jam,
            vec![
                id(spec().ship_id),
                id(MODULE),
                id(partner()),
                text("remoteArmorRepair"),
                filetime(t),
                int(5_100)
            ]
        );
        // the one who runs it is not told
        assert!(!told.iter().any(|l| l.starts_with("7:OnJam") || l.starts_with("7:OnEwar")), "{told:?}");
        // the next cycle refreshes the timer only
        let (out, _) = poll_until(&mut scene, t, 5.1);
        let again = labels(&out);
        assert_eq!(again.iter().filter(|l| l.as_str() == "8:OnJamStart").count(), 1, "{again:?}");
        assert!(!again.iter().any(|l| l == "8:OnEwarStart"), "{again:?}");
        // it ends: the timer, the icon and the bar
        scene.deactivate(A, MODULE, t + 6 * SECOND);
        let (out, _) = poll_until(&mut scene, t + 6 * SECOND, 5.0);
        let ended = labels(&out);
        for expected in ["8:OnJamEnd", "8:OnEwarEnd", "8:OnRefreshBuffBar"] {
            assert!(ended.iter().any(|l| l == expected), "{expected} in {ended:?}");
        }
    }

    // ---- effects that last: webs, painters, dampeners, scramblers ----

    /// A module (its own id) of A that holds B with `what` for as long as it runs, reaching 10 km over a falloff of 5 km.
    fn holding(module: i64, what: Remote, jam: &str) -> Effect {
        let mut effect = projected(what, Applies::CycleStart, partner());
        effect.module_id = module;
        effect.fx = "effects.ModifyTargetSpeed".to_string();
        effect.capacitor_need = 5.0;
        if let Some(Action::Remote { jam: name, .. }) = effect.action.as_mut() {
            *name = jam.to_string();
        }
        effect
    }

    fn web(module: i64, percent: f64) -> Effect {
        holding(module, Remote::Web { percent }, "webify")
    }

    fn speed_of(scene: &Scene, who: SessionId) -> f64 {
        scene.pilots[&who].ship.spec.max_velocity
    }

    #[test]
    fn a_web_slows_the_ship_it_holds_for_as_long_as_it_runs_and_the_ship_and_those_who_look_on_are_told() {
        let (mut scene, t) = reaching(5_000.0);
        let base = speed_of(&scene, B);
        let started = scene.activate(A, web(MODULE, -60.0), t);
        assert!((speed_of(&scene, B) - base * 0.4).abs() < 1e-9, "{}", speed_of(&scene, B));
        let told = labels(&started);
        assert!(
            told.iter().any(|l| l == "8:attributes[37]"),
            "the target's pilot is told its top speed: {told:?}"
        );
        assert!(told.iter().any(|l| l == "8:destiny[SetMaxSpeed]"), "{told:?}");
        assert!(
            told.iter().any(|l| l == "7:destiny[SetMaxSpeed]")
                || told.iter().any(|l| l.starts_with("7:destiny")),
            "those who look on: {told:?}"
        );
        // it goes on holding the ship through the cycles, and lets go when the effect stops
        let (_, t2) = poll_until(&mut scene, t, 6.0);
        assert!((speed_of(&scene, B) - base * 0.4).abs() < 1e-9);
        scene.deactivate(A, MODULE, t2);
        let (out, _) = poll_until(&mut scene, t2, 5.5);
        assert_eq!(speed_of(&scene, B), base);
        assert!(labels(&out).iter().any(|l| l == "8:destiny[SetMaxSpeed]"), "{:?}", labels(&out));
        assert!(scene.pilots[&B].incoming.is_empty() && scene.pilots[&B].incoming.base.is_none());
    }

    #[test]
    fn a_web_in_falloff_slows_less_and_two_webs_stack_with_the_penalty() {
        let (mut scene, t) = reaching(12_500.0);
        let base = speed_of(&scene, B);
        scene.activate(A, web(MODULE, -60.0), t);
        // half way through the falloff: a share of 0.840896
        // (the ships stand still, to a few centimetres)
        assert!((speed_of(&scene, B) - base * (1.0 - 0.6 * 0.840896)).abs() < 0.1, "{}", speed_of(&scene, B));
        let (mut scene, t) = reaching(2_000.0);
        scene.activate(A, web(MODULE, -60.0), t);
        scene.activate(A, web(MODULE + 1, -60.0), t);
        let factor = 0.4 * (1.0 - 0.6 * 0.869_119);
        assert!((speed_of(&scene, B) / base - factor).abs() < 1e-4, "{}", speed_of(&scene, B) / base);
    }

    #[test]
    fn what_a_ship_does_to_its_own_speed_is_worked_on_before_the_web_and_is_still_told_when_it_stops() {
        let (mut scene, t) = reaching(2_000.0);
        let base = speed_of(&scene, B);
        scene.activate(A, web(MODULE, -50.0), t);
        // the webbed ship starts its afterburner: the speed it gives is halved
        scene.activate(B, afterburner(), t + SECOND);
        assert!((speed_of(&scene, B) - 522.848918 * 0.5).abs() < 1e-6, "{}", speed_of(&scene, B));
        scene.deactivate(B, MODULE, t + 2 * SECOND);
        let (_, after) = poll_until(&mut scene, t + 2 * SECOND, 11.0);
        assert!((speed_of(&scene, B) - base * 0.5).abs() < 1e-6, "{}", speed_of(&scene, B));
        assert!(after > t);
    }

    /// A jammer whose strength is `strength` against radar and which runs at the partner, with the partner's radar strength made `sensor` (and its resistance `resistance`).
    fn jammer(scene: &mut Scene, strength: f64, sensor: f64, resistance: f64) -> Effect {
        let spec = &mut scene.pilots.get_mut(&B).unwrap().ship.spec;
        spec.sensor_strength = [0.0, 0.0, 0.0, sensor];
        spec.ecm_resistance = resistance;
        let mut effect =
            projected(Remote::Jam { strength: [0.0, 0.0, 0.0, strength] }, Applies::CycleStart, partner());
        effect.fx = "effects.ElectronicAttributeModifyTarget".to_string();
        effect.duration_ms = 20_000.0;
        effect.capacitor_need = 5.0;
        effect
    }

    fn told_hud(outs: &[Out], session: SessionId, name: &str) -> bool {
        outs.iter()
            .any(|o| matches!(o, Out::Notify { session: s, name: n, .. } if *s == session && *n == name))
    }

    #[test]
    fn a_jammer_that_beats_the_sensor_breaks_the_locks_of_its_target_and_keeps_it_from_locking_but_the_jammer()
     {
        let (mut scene, t) = reaching(5_000.0);
        let mine = spec().ship_id;
        // the jammed ship holds a lock on the jammer and on a third party (the jammer's own partner is all there is: a lock on the jammer stays)
        let asked = scene.target_order(B, crate::targeting::Order::Lock(mine), t);
        assert!(matches!(asked.first(), Some(Out::Targets { .. })), "{asked:?}");
        let (_, t) = poll_until(&mut scene, t, 6.0);
        assert_eq!(scene.locked_by(B), vec![mine]);
        // a jammer that is twice as strong as the sensor always jams
        let effect = jammer(&mut scene, 10.0, 5.0, 1.0);
        let landed = scene.activate(A, effect, t);
        assert!(
            told_hud(&landed, B, "OnJamStart") && told_hud(&landed, B, "OnEwarStart"),
            "{:?}",
            labels(&landed)
        );
        assert!(scene.pilots[&B].incoming.jammed());
        // the ship that jams is the one it can still lock
        assert_eq!(scene.locked_by(B), vec![mine]);
        assert_eq!(scene.pilots[&B].incoming.jammers(), vec![mine]);
    }

    #[test]
    fn a_jammed_ship_loses_the_locks_that_are_not_on_its_jammer_and_cannot_make_new_ones() {
        let (mut scene, t) = reaching(5_000.0);
        assert_eq!(scene.locked_by(A), vec![partner()]);
        // a ship that is not the partner jams A (a made-up one: the scene holds two ships)
        let until = t + 20 * SECOND;
        scene.pilots.get_mut(&A).unwrap().incoming.upsert(777, 888, crate::ewar::Lasting::Jam, until);
        let broken = scene.break_locks_except(A, &[777], t);
        assert!(scene.locked_by(A).is_empty(), "{:?}", labels(&broken));
        assert!(told_hud(&broken, A, "OnTarget"), "{:?}", labels(&broken));
        assert!(
            told_hud(&broken, B, "OnTarget"),
            "the ship that was locked is told it is not: {:?}",
            labels(&broken)
        );
        // and it cannot lock the partner again until the jam lapses
        let refused = scene.target_order(A, crate::targeting::Order::Lock(partner()), t + SECOND);
        assert!(
            refused.iter().any(|o| matches!(o, Out::TargetRefused { reason: "TARGET_JAMMED", .. })),
            "{refused:?}"
        );
        let (_, t2) = poll_until(&mut scene, t, 22.0);
        assert!(!scene.pilots[&A].incoming.jammed());
        let asked = scene.target_order(A, crate::targeting::Order::Lock(partner()), t2);
        assert!(matches!(asked.first(), Some(Out::Targets { .. })), "{asked:?}");
    }

    #[test]
    fn a_jam_that_does_not_beat_the_sensor_does_nothing_and_a_ship_that_resists_it_is_not_jammed() {
        let (mut scene, t) = reaching(5_000.0);
        // no strength for the kind of sensor the ship has: no chance
        let effect = jammer(&mut scene, 0.0, 5.0, 1.0);
        let landed = scene.activate(A, effect, t);
        assert!(!scene.pilots[&B].incoming.jammed(), "{:?}", labels(&landed));
        assert!(!told_hud(&landed, B, "OnJamStart"));
        // a ship with no resistance to give is immune
        let (mut scene, t) = reaching(5_000.0);
        let effect = jammer(&mut scene, 100.0, 5.0, 0.0);
        scene.activate(A, effect, t);
        assert!(!scene.pilots[&B].incoming.jammed());
    }

    #[test]
    fn a_jam_lapses_when_the_jammer_stops_and_the_ship_is_told_it_ended() {
        let (mut scene, t) = reaching(5_000.0);
        let effect = jammer(&mut scene, 10.0, 5.0, 1.0);
        scene.activate(A, effect, t);
        assert!(scene.pilots[&B].incoming.jammed());
        scene.deactivate(A, MODULE, t + SECOND);
        // the cycle that is going is played out (twenty seconds), and the ship is let go at its end
        let (told, _) = poll_until(&mut scene, t + SECOND, 25.0);
        assert!(!scene.pilots[&B].incoming.jammed());
        assert!(told_hud(&told, B, "OnJamEnd") && told_hud(&told, B, "OnEwarEnd"), "{:?}", labels(&told));
    }

    #[test]
    fn the_chance_of_a_jam_is_the_strength_over_the_sensor_weakened_by_the_reach_and_the_resistance() {
        use crate::ewar::jam_chance;
        let jam = [0.0, 3.0, 0.0, 0.0];
        // the ship's strongest sensor is the kind that counts (a ladar ship against a ladar jammer)
        assert!((jam_chance(&jam, &[0.0, 6.0, 2.0, 0.0], 1.0, 1.0) - 0.5).abs() < 1e-9);
        // the strongest sensor is another kind than the jammer has: no chance
        assert_eq!(jam_chance(&jam, &[0.0, 2.0, 6.0, 0.0], 1.0, 1.0), 0.0);
        // two kinds that are alike: the first counts
        assert!((jam_chance(&[1.0, 0.0, 0.0, 0.0], &[4.0, 0.0, 0.0, 4.0], 1.0, 1.0) - 0.25).abs() < 1e-9);
        // out in the falloff and resisting
        assert!((jam_chance(&jam, &[0.0, 6.0, 0.0, 0.0], 0.5, 0.5) - 0.125).abs() < 1e-9);
        // a ship with no sensors, a jammer that is stronger than the sensor, a ship that cannot be jammed
        assert_eq!(jam_chance(&jam, &[0.0; 4], 1.0, 1.0), 0.0);
        assert_eq!(jam_chance(&jam, &[0.0, 1.0, 0.0, 0.0], 1.0, 1.0), 1.0);
        assert_eq!(jam_chance(&jam, &[0.0, 1.0, 0.0, 0.0], 0.0, 1.0), 0.0);
    }

    #[test]
    fn a_lock_the_dampened_ship_can_no_longer_reach_is_given_up() {
        let (mut scene, t) = reaching(5_000.0);
        let mine = spec().ship_id;
        // the ship that is to be dampened holds a lock on the one that dampens it
        let asked = scene.target_order(B, crate::targeting::Order::Lock(mine), t);
        assert!(matches!(asked.first(), Some(Out::Targets { .. })), "{asked:?}");
        let (_, t) = poll_until(&mut scene, t, 6.0);
        assert_eq!(scene.locked_by(B), vec![mine]);
        // a dampener that takes nine tenths of its range leaves it less than the distance between them
        scene.activate(
            A,
            holding(
                MODULE,
                Remote::Damp {
                    range_percent: -90.0,
                    resolution_percent: 0.0,
                    gravimetric_percent: 0.0,
                    ladar_percent: 0.0,
                    magnetometric_percent: 0.0,
                    radar_percent: 0.0,
                },
                "ewRemoteSensorDamp",
            ),
            t,
        );
        let (told, _) = poll_until(&mut scene, t, 2.0);
        assert!(scene.locked_by(B).is_empty(), "the lock is given up: {:?}", scene.locked_by(B));
        assert!(
            told.iter().any(|o| matches!(o, Out::Notify { session, name: "OnTarget", .. } if *session == B)),
            "{:?}",
            labels(&told)
        );
    }

    #[test]
    fn remote_sensor_boost_is_assistance_with_all_percentages_falloff_scaled_and_negative_or_mixed_damp_is_hostile()
     {
        let boost = Remote::Damp {
            range_percent: 20.0,
            resolution_percent: 10.0,
            gravimetric_percent: 30.0,
            ladar_percent: 40.0,
            magnetometric_percent: 50.0,
            radar_percent: 60.0,
        };
        assert!(!boost.hostile());
        assert_eq!(
            boost.lasting(0.5),
            Some(crate::ewar::Lasting::Damp {
                range_percent: 10.0,
                resolution_percent: 5.0,
                gravimetric_percent: 15.0,
                ladar_percent: 20.0,
                magnetometric_percent: 25.0,
                radar_percent: 30.0,
            })
        );
        assert!(
            Remote::Damp {
                range_percent: -25.0,
                resolution_percent: -25.0,
                gravimetric_percent: 0.0,
                ladar_percent: 0.0,
                magnetometric_percent: 0.0,
                radar_percent: 0.0,
            }
            .hostile()
        );
        assert!(
            Remote::Damp {
                range_percent: 25.0,
                resolution_percent: 25.0,
                gravimetric_percent: 20.0,
                ladar_percent: -1.0,
                magnetometric_percent: 20.0,
                radar_percent: 20.0,
            }
            .hostile()
        );
    }

    #[test]
    fn two_local_sensor_boosters_stack_and_stopping_one_keeps_the_other() {
        let (mut scene, t) = crowd();
        let ship_id = spec().ship_id;
        let (base_range, base_resolution) = {
            let ship = &scene.pilots[&A].ship.spec;
            (ship.max_target_range, ship.scan_resolution)
        };
        let self_boost = |module_id, range_percent, resolution_percent| {
            let mut effect = projected(
                Remote::Damp {
                    range_percent,
                    resolution_percent,
                    gravimetric_percent: 0.0,
                    ladar_percent: 0.0,
                    magnetometric_percent: 0.0,
                    radar_percent: 0.0,
                },
                Applies::CycleStart,
                ship_id,
            );
            effect.module_id = module_id;
            if let Some(Action::Remote { reach, jam, .. }) = effect.action.as_mut() {
                *reach = Reach { optimal: 1.0, falloff: 0.0 };
                *jam = "remoteSensorBoost".to_string();
            }
            effect
        };
        let first_module = MODULE + 20;
        let second_module = MODULE + 21;
        let first = scene.activate(A, self_boost(first_module, 50.0, 40.0), t);
        assert!(matches!(first.first(), Some(Out::Accepted { .. })), "{first:?}");
        let second = scene.activate(A, self_boost(second_module, 30.0, 20.0), t);
        assert!(matches!(second.first(), Some(Out::Accepted { .. })), "{second:?}");
        assert!(first.iter().any(|out| matches!(out, Out::Notify { session: A, name: "OnJamStart", .. })));
        assert!(scene.pilots[&A].ship.spec.max_target_range > base_range * 1.3);
        assert!(scene.pilots[&A].ship.spec.scan_resolution > base_resolution * 1.2);

        scene.deactivate(A, first_module, t + SECOND);
        let (ended, _) = poll_until(&mut scene, t + SECOND, 5.1);
        assert_eq!(scene.pilots[&A].modules.running_count(), 1, "{:?}", labels(&ended));
        assert!((scene.pilots[&A].ship.spec.max_target_range - base_range * 1.3).abs() < 1e-6);
        assert!((scene.pilots[&A].ship.spec.scan_resolution - base_resolution * 1.2).abs() < 1e-6);
    }

    #[test]
    fn a_target_painter_makes_the_ship_bigger_and_a_sensor_dampener_shortens_its_lock_range_and_slows_its_locks()
     {
        let (mut scene, t) = reaching(3_000.0);
        let (signature, range, resolution) = {
            let spec = &scene.pilots[&B].ship.spec;
            (spec.signature_radius, spec.max_target_range, spec.scan_resolution)
        };
        let painted = scene.activate(A, holding(MODULE, Remote::Paint { percent: 25.0 }, "ewTargetPaint"), t);
        assert!((scene.pilots[&B].ship.spec.signature_radius - signature * 1.25).abs() < 1e-9);
        assert!(labels(&painted).iter().any(|l| l == "8:attributes[552]"), "{:?}", labels(&painted));
        let damped = scene.activate(
            A,
            holding(
                MODULE + 1,
                Remote::Damp {
                    range_percent: -30.0,
                    resolution_percent: -20.0,
                    gravimetric_percent: 0.0,
                    ladar_percent: 0.0,
                    magnetometric_percent: 0.0,
                    radar_percent: 0.0,
                },
                "ewRemoteSensorDamp",
            ),
            t,
        );
        let spec = &scene.pilots[&B].ship.spec;
        assert!(
            (spec.max_target_range - range * 0.7).abs() < 1e-6
                && (spec.scan_resolution - resolution * 0.8).abs() < 1e-9
        );
        assert!(labels(&damped).iter().any(|l| l == "8:attributes[76,564]"), "{:?}", labels(&damped));
    }

    /// A smartbomb of `range` metres that does `damage` (em, thermal, kinetic, explosive) a cycle.
    fn smartbomb(range: f64, damage: [f64; 4], hurts_pilots: bool) -> Effect {
        Effect {
            burns_fuel: false,
            fx: "effects.EmpWave".to_string(),
            duration_ms: 10_000.0,
            capacitor_need: 5.0,
            target_id: 0,
            action: Some(Action::Smartbomb { range, damage, hurts_pilots }),
            ..projected(Remote::Web { percent: 0.0 }, Applies::CycleStart, 0)
        }
    }

    fn total(layers: &crate::health::Layers) -> f64 {
        layers.shield + layers.armor + layers.structure
    }

    #[test]
    fn a_smartbomb_hurts_every_ship_in_its_range_each_cycle_and_nothing_beyond_it() {
        // B stands two kilometres off A's surface: inside a three kilometre field, with no lock between them
        let (mut scene, t) = reaching(2_000.0);
        scene.target_order(A, crate::targeting::Order::Clear, t);
        let before = total(&partner_layers(&scene, t));
        let started = scene.activate(A, smartbomb(3_000.0, [0.0, 40.0, 0.0, 40.0], true), t);
        assert!(started.iter().any(|o| matches!(o, Out::Accepted { .. })), "{:?}", labels(&started));
        let after = total(&partner_layers(&scene, t));
        assert!(after < before - 1.0, "{before} then {after}");
        assert!(
            told_hud(&started, B, "OnDamageMessage") && told_hud(&started, A, "OnDamageMessage"),
            "{:?}",
            labels(&started)
        );
        // the next cycle is another blast
        let (_, t2) = poll_until(&mut scene, t, 10.5);
        assert!(total(&partner_layers(&scene, t2)) < after - 1.0);
        // beyond the field nothing is hurt
        let (mut far, tf) = reaching(5_000.0);
        far.target_order(A, crate::targeting::Order::Clear, tf);
        let whole = total(&partner_layers(&far, tf));
        far.activate(A, smartbomb(3_000.0, [0.0, 40.0, 0.0, 40.0], true), tf);
        assert_eq!(total(&partner_layers(&far, tf)), whole);
        // and a ship whose undock protection is on takes nothing
        let (mut safe, ts) = reaching(2_000.0);
        safe.target_order(A, crate::targeting::Order::Clear, ts);
        safe.pilots.get_mut(&B).unwrap().protected_until = ts + 60 * SECOND;
        let kept = total(&partner_layers(&safe, ts));
        safe.activate(A, smartbomb(3_000.0, [0.0, 40.0, 0.0, 40.0], true), ts);
        assert_eq!(total(&partner_layers(&safe, ts)), kept);
    }

    #[test]
    fn an_ecm_burst_breaks_nearby_locks_at_cycle_start_but_does_not_leave_a_jam() {
        let (mut scene, t) = reaching(2_000.0);
        scene.pilots.get_mut(&B).unwrap().ship.spec.sensor_strength = [10.0; 4];
        scene.pilots.get_mut(&B).unwrap().ship.spec.ecm_resistance = 1.0;
        let target_id = scene.pilots[&A].ship.spec.ship_id;
        scene.target_order(B, crate::targeting::Order::Lock(target_id), t);
        let (_, locked_at) = poll_until(&mut scene, t, 30.0);
        assert!(!scene.pilots[&B].targeting.targets().is_empty());

        let burst = Effect {
            burns_fuel: false,
            fx: "effects.EcmBurstJammer".to_string(),
            action: Some(Action::EcmBurst { range: 3_000.0, strength: [1_000.0; 4] }),
            ..smartbomb(0.0, [0.0; 4], true)
        };
        let out = scene.activate(A, burst, locked_at);
        assert!(out.iter().any(|event| matches!(event, Out::Accepted { .. })), "{:?}", labels(&out));
        assert!(
            scene.pilots[&B].targeting.pending_targets().is_empty()
                && scene.pilots[&B].targeting.targets().is_empty()
        );
        assert!(!scene.pilots[&B].incoming.jammed());
    }

    #[test]
    fn a_smartbomb_the_safety_forbids_does_not_start_with_a_pilot_in_range_and_stops_when_one_comes_in() {
        let (mut scene, t) = reaching(2_000.0);
        scene.target_order(A, crate::targeting::Order::Clear, t);
        let before = total(&partner_layers(&scene, t));
        let refused = scene.activate(A, smartbomb(3_000.0, [0.0, 40.0, 0.0, 40.0], false), t);
        assert!(
            refused.iter().any(|o| matches!(o, Out::Refused { reason: "SafetyActivated", .. })),
            "{:?}",
            labels(&refused)
        );
        assert_eq!(total(&partner_layers(&scene, t)), before);
        // with nobody in range it starts, and a pilot that comes in range stops it at the next cycle
        let (mut quiet, tq) = reaching(8_000.0);
        quiet.target_order(A, crate::targeting::Order::Clear, tq);
        let started = quiet.activate(A, smartbomb(3_000.0, [0.0, 40.0, 0.0, 40.0], false), tq);
        assert!(started.iter().any(|o| matches!(o, Out::Accepted { .. })), "{:?}", labels(&started));
        let here = quiet.pilots[&A].ship.position;
        let (ra, rb) = (quiet.pilots[&A].ship.spec.radius, quiet.pilots[&B].ship.spec.radius);
        quiet.pilots.get_mut(&B).unwrap().ship.position =
            eve_destiny::Vec3 { x: here.x + 1_000.0 + ra + rb, y: here.y, z: here.z };
        let whole = total(&partner_layers(&quiet, tq));
        let (told, t2) = poll_until(&mut quiet, tq, 11.0);
        assert_eq!(total(&partner_layers(&quiet, t2)), whole, "{:?}", labels(&told));
        assert!(!quiet.pilots[&A].modules.running.iter().any(|r| r.effect.module_id == MODULE), "it stopped");
    }

    /// The afterburner of the tests, as the dogma effect of a microwarpdrive.
    fn microwarpdrive() -> Effect {
        Effect { effect_id: MICROWARPDRIVE, module_id: MODULE + 100, ..afterburner() }
    }

    #[test]
    fn an_uninterrupted_mjd_jumps_at_spool_end_and_starts_cooldown() {
        let (mut scene, t) = crowd();
        let started = scene.activate(A, micro_jump_drive(), t);
        assert!(started.iter().any(|out| matches!(out, Out::Accepted { .. })));

        let deadline = t + 12 * SECOND;
        let (position, direction, speed) = {
            let ship = scene.ship(A, deadline).unwrap();
            (ship.position, ship.direction, ship.speed_fraction)
        };
        scene.poll(deadline);
        let after = scene.ship(A, deadline).unwrap();
        assert!((after.position.x - position.x - direction.x * 100_000.0).abs() < 0.001);
        assert!((after.position.y - position.y - direction.y * 100_000.0).abs() < 0.001);
        assert!((after.position.z - position.z - direction.z * 100_000.0).abs() < 0.001);
        assert_eq!(after.speed_fraction, speed);
        assert_eq!(
            (after.direction.x, after.direction.y, after.direction.z),
            (direction.x, direction.y, direction.z)
        );

        let cooling = scene.activate(A, micro_jump_drive(), deadline + SECOND);
        assert!(cooling.iter().any(|out| matches!(out, Out::Refused { reason: "MJD_COOLDOWN", .. })));
    }

    #[test]
    fn a_scram_at_mjd_spool_end_aborts_and_cooldown_expires() {
        let (mut scene, t) = reaching(3_000.0);
        let repeated = Effect { repeat: 2, ..micro_jump_drive() };
        let refused = scene.activate(B, repeated, t);
        assert!(refused.iter().any(|out| matches!(out, Out::Refused { reason: "BAD_MJD", .. })));

        let started = scene.activate(B, micro_jump_drive(), t);
        assert!(started.iter().any(|out| matches!(out, Out::Accepted { .. })));
        let steering = scene.command(
            B,
            crate::scene::Command::GotoDirection(eve_destiny::Vec3::new(1.0, 0.0, 0.0)),
            t + SECOND,
        );
        assert!(steering.iter().any(|out| matches!(out, Out::Refused { reason: "MJD_SPOOLING", .. })));

        let scram =
            holding(MODULE + 201, Remote::Scramble { strength: 2.0, blocks_mwd: true }, "warpScramblerMWD");
        let held = scene.activate(A, scram, t + SECOND);
        assert!(held.iter().any(|out| matches!(out, Out::Accepted { .. })));
        assert!(scene.pilots[&B].incoming.mwd_blocked());

        let deadline = t + 12 * SECOND;
        let position_before = scene.ship(B, deadline).unwrap().position;
        scene.poll(deadline);
        assert_eq!(scene.ship(B, deadline).unwrap().position, position_before);
        assert!(!scene.pilots[&B].modules.running.iter().any(|run| run.effect.module_id == MODULE + 200));

        let cooling = scene.activate(B, micro_jump_drive(), deadline + SECOND);
        assert!(cooling.iter().any(|out| matches!(out, Out::Refused { reason: "MJD_COOLDOWN", .. })));
        let expired = scene.activate(B, micro_jump_drive(), deadline + 180 * SECOND);
        assert!(expired.iter().any(|out| matches!(out, Out::Accepted { .. })));
    }

    #[test]
    fn a_scrambler_that_blocks_a_microwarpdrive_stops_the_one_that_runs_and_keeps_another_from_starting() {
        let (mut scene, t) = reaching(3_000.0);
        let started = scene.activate(B, microwarpdrive(), t);
        assert!(started.iter().any(|o| matches!(o, Out::Accepted { .. })), "{:?}", labels(&started));
        assert!(scene.pilots[&B].modules.running.iter().any(|r| r.effect.effect_id == MICROWARPDRIVE));
        // a disruptor does not block it, a scrambler does: the drive that runs is stopped at once and the ship is told it is off
        let disrupt = holding(MODULE, Remote::Scramble { strength: 1.0, blocks_mwd: false }, "warpScrambler");
        let free = scene.activate(A, disrupt, t + SECOND);
        assert!(
            scene.pilots[&B].modules.running.iter().any(|r| r.effect.effect_id == MICROWARPDRIVE),
            "{:?}",
            labels(&free)
        );
        scene.deactivate(A, MODULE, t + 2 * SECOND);
        let (_, t2) = poll_until(&mut scene, t + 2 * SECOND, 8.0);
        assert!(!scene.pilots[&B].incoming.scrambled());
        let scram =
            holding(MODULE + 1, Remote::Scramble { strength: 2.0, blocks_mwd: true }, "warpScramblerMWD");
        let stopped = scene.activate(A, scram, t2);
        assert!(
            !scene.pilots[&B].modules.running.iter().any(|r| r.effect.effect_id == MICROWARPDRIVE),
            "{:?}",
            labels(&stopped)
        );
        assert!(
            told_hud(&stopped, B, "OnGodmaShipEffect"),
            "the pilot is told the drive is off: {:?}",
            labels(&stopped)
        );
        // and another cannot be started while the scrambler holds the ship; an afterburner can
        let refused = scene.activate(B, microwarpdrive(), t2 + SECOND);
        assert!(
            refused.iter().any(|o| matches!(o, Out::Refused { reason: "MICROWARPDRIVE_BLOCKED", .. })),
            "{:?}",
            labels(&refused)
        );
        let ab = Effect { module_id: MODULE + 101, ..afterburner() };
        let accepted = scene.activate(B, ab, t2 + SECOND);
        assert!(accepted.iter().any(|o| matches!(o, Out::Accepted { .. })), "{:?}", labels(&accepted));
    }

    #[test]
    fn a_ship_that_is_scrambled_cannot_warp_and_a_scramble_stops_one_that_is_lining_up() {
        let (mut scene, t) = reaching_in(crowd_with_a_planet(), 3_000.0);
        scene.activate(
            A,
            holding(MODULE, Remote::Scramble { strength: 2.0, blocks_mwd: true }, "warpScramblerMWD"),
            t,
        );
        assert!(scene.pilots[&B].incoming.scrambled());
        let refused = scene.command(B, Command::WarpTo { target_id: PLANET, range: 0.0 }, t + SECOND);
        assert!(
            refused.iter().any(|o| matches!(o, Out::Refused { command: 8, reason: "WARP_SCRAMBLED", .. })),
            "{refused:?}"
        );
        // it is let go when the effect stops, and the ship warps
        scene.deactivate(A, MODULE, t + 2 * SECOND);
        let (_, t2) = poll_until(&mut scene, t + 2 * SECOND, 6.0);
        assert!(!scene.pilots[&B].incoming.scrambled());
        let accepted = scene.command(B, Command::WarpTo { target_id: PLANET, range: 0.0 }, t2);
        assert!(!accepted.iter().any(|o| matches!(o, Out::Refused { .. })), "{accepted:?}");
        assert!(scene.pilots[&B].flight.is_some());
        // a scramble that lands while it lines up stops it
        let (mut scene, t) = reaching_in(crowd_with_a_planet(), 3_000.0);
        scene.command(B, Command::WarpTo { target_id: PLANET, range: 0.0 }, t);
        assert!(scene.pilots[&B].flight.as_ref().is_some_and(|f| !f.is_warping()));
        scene.activate(
            A,
            holding(MODULE, Remote::Scramble { strength: 1.0, blocks_mwd: false }, "warpScrambler"),
            t + SECOND / 2,
        );
        assert!(scene.pilots[&B].flight.is_none(), "the warp is called off");
    }

    #[test]
    fn what_no_module_renews_lapses_by_itself_as_when_the_ship_that_held_it_is_gone() {
        let (mut scene, t) = reaching(3_000.0);
        let base = speed_of(&scene, B);
        scene.activate(A, web(MODULE, -60.0), t);
        // the ship that runs the web is gone: nothing renews the record
        scene.pilots.get_mut(&A).unwrap().modules.running.clear();
        let (out, _) = poll_until(&mut scene, t, 6.0);
        assert_eq!(speed_of(&scene, B), base, "{:?}", labels(&out));
        assert!(scene.pilots[&B].incoming.is_empty());
    }

    #[test]
    fn offlining_a_module_during_a_cycle_stops_it_immediately() {
        let (mut scene, t) = hurt();
        let before = layers(&scene, t).armor;
        scene.activate(A, repairer(Layer::Armor, 30.0, Applies::CycleEnd), t);

        let out = scene.offline_module(A, MODULE, t + SECOND);
        assert_eq!(scene.pilots[&A].modules.running_count(), 0);
        assert!((layers(&scene, t + SECOND).armor - before).abs() < 1e-3);
        let Some(Out::Notify { args, .. }) =
            out.iter().find(|out| matches!(out, Out::Notify { name: "OnGodmaShipEffect", .. }))
        else {
            panic!("the offlined effect is told to the client: {out:?}");
        };
        assert_eq!(args[8], Value::Bool(false));

        let (later, _) = poll_until(&mut scene, t + SECOND, 5.0);
        assert!((layers(&scene, t + 6 * SECOND).armor - before).abs() < 1e-3);
        assert!(later.iter().all(|out| !matches!(out, Out::Notify { name: "OnGodmaShipEffect", .. })));
    }

    // ---- a reactive armor hardener ----

    /// The Ibis's armor resonances (em, thermal, kinetic, explosive) before a hardener.
    const BASE_ARMOR: [f64; 4] = [0.5, 0.35, 0.25, 0.1];
    /// The attributes of the armor's resonances, in the order of the damage vector: em (267), thermal (270), kinetic (269), explosive (268).
    const ARMOR_IDS: [i32; 4] = [267, 270, 269, 268];

    /// A reactive armor hardener of the Ibis (type 4403): seeded at 0.85 each, 6 points a cycle, of ten seconds; told as the ship's armor resonances.
    fn hardener(module: i64) -> Effect {
        let ship = spec().ship_id;
        let seeded = |base: f64| base * 0.85;
        Effect {
            burns_fuel: false,
            module_id: module,
            module_type: 4403,
            effect_id: 4928,
            fx: "effects.AdaptiveArmorHardener".to_string(),
            duration_ms: 10_000.0,
            capacitor_need: 42.0,
            // a toggle: it runs until the pilot stops it (the type does not disallow repeating)
            repeat: 1000,
            target_id: 0,
            turret: None,
            action: Some(Action::ResistanceShift { seed: [0.85; 4], shift: 0.06, attribute_ids: ARMOR_IDS }),
            on: None,
            off: None,
            on_changes: ARMOR_IDS
                .iter()
                .zip(BASE_ARMOR)
                .map(|(&attribute_id, base)| Change { item_id: ship, attribute_id, value: round6(seeded(base)) })
                .collect(),
            off_changes: ARMOR_IDS
                .iter()
                .zip(BASE_ARMOR)
                .map(|(&attribute_id, base)| Change { item_id: ship, attribute_id, value: base })
                .collect(),
        }
    }

    /// The armor resonances the pilot of `A` was told of in `out`: `(attribute, new, old)`.
    fn armor_told(out: &[Out]) -> Vec<(i64, f64, f64)> {
        // what an effect's change tells has no old value (`None`): it is not a number
        let number = |value: &Value| match value {
            Value::Real(x) => *x,
            Value::Int(x) => *x as f64,
            Value::None => f64::NAN,
            other => panic!("{other:?}"),
        };
        out.iter()
            .filter_map(|o| match o {
                Out::Notify { session: A, name: "OnModuleAttributeChanges", args, .. } => {
                    let Some(Value::List(lines)) = args.first() else { panic!("{args:?}") };
                    Some(lines.clone())
                }
                _ => None,
            })
            .flatten()
            .filter_map(|line| {
                let Value::Tuple(t) = line else { panic!() };
                let Value::Int(attribute) = t[3] else { panic!() };
                ARMOR_IDS.contains(&(attribute as i32)).then(|| (attribute, number(&t[5]), number(&t[6])))
            })
            .collect()
    }

    fn round6(value: f64) -> f64 {
        crate::health::round6(value)
    }

    /// The Ibis with armor enough to take a long run of hits, so that the hits count in full.
    fn with_armor(scene: &mut Scene, t: i64) {
        let mut big = spec();
        big.vitals.armor_hp = 1.0e7;
        big.vitals.structure_hp = 1.0e7;
        scene.respec(A, big, t);
    }

    #[test]
    fn a_hardener_moves_the_armor_resonances_at_the_end_of_its_cycle_as_the_hit_says_and_tells_the_pilot() {
        let (mut scene, t) = crowd();
        with_armor(&mut scene, t);
        let started = scene.activate(A, hardener(MODULE), t);
        assert!(matches!(started.first(), Some(Out::Accepted { command: 17, .. })), "{started:?}");
        // the armor's resonances are the base times the seed while it runs (the host's told values, and the simulation's)
        assert!((scene.pilots[&A].ship.health.armor_resonance()[0] - 0.425).abs() < 1e-12);
        // a hit of 1000 em lands before the cycle ends: 1000 x 0.425 goes into the armor
        scene.damage(spec().ship_id, [1000.0, 0.0, 0.0, 0.0], t + SECOND);
        let (out, _) = poll_until(&mut scene, t, 10.5);
        // the cycle ended: em falls to 0.67 (0.5 x 0.67), the others rise to 0.91 (0.35 x 0.91 for thermal)
        let told = armor_told(&out);
        assert!(told.iter().any(|&(attribute, new, old)| attribute == 267 && new == 0.335 && old == 0.425), "{told:?}");
        assert!(told.iter().any(|&(attribute, new, old)| attribute == 270 && new == 0.3185 && old == 0.2975), "{told:?}");
        assert_eq!(scene.pilots[&A].ship.health.shift_profile(), Some([0.67, 0.91, 0.91, 0.91]));
    }

    #[test]
    fn a_hardener_is_told_nothing_at_a_cycle_in_which_the_armor_took_nothing() {
        let (mut scene, t) = crowd();
        with_armor(&mut scene, t);
        scene.activate(A, hardener(MODULE), t);
        let (out, _) = poll_until(&mut scene, t, 25.0);
        assert!(armor_told(&out).is_empty(), "{:?}", armor_told(&out));
        // the profile is the seed still, after two cycles with nothing in them
        assert_eq!(scene.pilots[&A].ship.health.shift_profile(), Some([0.85; 4]));
    }

    #[test]
    fn the_profile_keeps_its_total_and_its_bounds_over_many_cycles_of_hits() {
        let (mut scene, t) = crowd();
        with_armor(&mut scene, t);
        scene.activate(A, hardener(MODULE), t);
        let mut now = t;
        for cycle in 0..30 {
            // a hit in one kind a cycle, moving round the four
            let kind = cycle % 4;
            let mut raw = [0.0; 4];
            raw[kind] = 400.0;
            scene.damage(spec().ship_id, raw, now + SECOND);
            let (_, next) = poll_until(&mut scene, now, 10.5);
            now = next;
            let profile = scene.pilots[&A].ship.health.shift_profile().expect("the hardener runs");
            assert!((profile.iter().sum::<f64>() - 3.4).abs() < 1e-6, "cycle {cycle}: {profile:?}");
            assert!(profile.iter().all(|r| (0.0..=1.0).contains(r)), "cycle {cycle}: {profile:?}");
        }
    }

    #[test]
    fn a_second_reactive_armor_hardener_is_refused_while_one_runs() {
        let (mut scene, t) = crowd();
        scene.activate(A, hardener(MODULE), t);
        let second = scene.activate(A, hardener(MODULE + 1), t);
        assert!(
            matches!(second.first(), Some(Out::Refused { command: 17, reason: "MAX_GROUP_ACTIVE", .. })),
            "{second:?}"
        );
        assert_eq!(scene.pilots[&A].modules.running_count(), 1);
    }

    #[test]
    fn stopping_a_hardener_gives_the_armor_its_base_and_a_later_start_begins_from_the_seed() {
        let (mut scene, t) = crowd();
        with_armor(&mut scene, t);
        scene.activate(A, hardener(MODULE), t);
        scene.damage(spec().ship_id, [1000.0, 0.0, 0.0, 0.0], t + SECOND);
        scene.deactivate(A, MODULE, t + 2 * SECOND);
        let (out, t) = poll_until(&mut scene, t, 10.5);
        // the cycle ends with the hardener stopping: the armor is told its base (the off values) and nothing runs
        let told = armor_told(&out);
        assert!(told.iter().any(|&(attribute, new, _)| attribute == 267 && new == 0.5), "{told:?}");
        assert_eq!(scene.pilots[&A].ship.health.armor_resonance(), BASE_ARMOR);
        assert_eq!(scene.pilots[&A].ship.health.shift_profile(), None);
        // a later start is from the seed, not from where it stopped
        scene.activate(A, hardener(MODULE), t);
        assert_eq!(scene.pilots[&A].ship.health.shift_profile(), Some([0.85; 4]));
    }

    #[test]
    fn a_fitted_polarized_weapon_holds_the_armor_at_one_and_the_hardener_moves_nothing_until_it_is_taken_off() {
        let (mut scene, t) = crowd();
        with_armor(&mut scene, t);
        let mut pinned = spec();
        pinned.vitals.armor_pinned = true;
        pinned.vitals.armor_hp = 1.0e7;
        pinned.vitals.structure_hp = 1.0e7;
        // the Polarized weapon is fitted: the simulation is sent the pin with the ship
        scene.respec(A, pinned, t);
        let started = scene.activate(A, hardener(MODULE), t);
        assert!(matches!(started.first(), Some(Out::Accepted { command: 17, .. })), "{started:?}");
        assert_eq!(scene.pilots[&A].ship.health.armor_resonance(), [1.0; 4]);
        scene.damage(spec().ship_id, [1000.0, 0.0, 0.0, 0.0], t + SECOND);
        let (out, t) = poll_until(&mut scene, t, 10.5);
        // nothing moves while pinned: no shift is told and the profile is the seed
        assert!(armor_told(&out).is_empty(), "{:?}", armor_told(&out));
        assert_eq!(scene.pilots[&A].ship.health.shift_profile(), Some([0.85; 4]));
        // the Polarized weapon comes off: the armor is the base times the profile again, and the pilot is told it at once
        let mut unpinned = spec();
        unpinned.vitals.armor_hp = 1.0e7;
        unpinned.vitals.structure_hp = 1.0e7;
        let out = scene.respec(A, unpinned, t);
        let told = armor_told(&out);
        assert!(told.iter().any(|&(attribute, new, old)| attribute == 267 && new == 0.425 && old == 1.0), "{told:?}");
        assert!((scene.pilots[&A].ship.health.armor_resonance()[0] - 0.425).abs() < 1e-12);
    }

    #[test]
    fn a_refit_that_changes_the_base_moves_the_told_armor_and_keeps_the_profile() {
        let (mut scene, t) = crowd();
        with_armor(&mut scene, t);
        scene.activate(A, hardener(MODULE), t);
        let mut refit = spec();
        refit.vitals.armor_hp = 1.0e7;
        refit.vitals.structure_hp = 1.0e7;
        refit.vitals.resonances.armor = [0.4, 0.35, 0.25, 0.1];
        let out = scene.respec(A, refit, t + SECOND);
        let told = armor_told(&out);
        assert!(told.iter().any(|&(attribute, new, old)| attribute == 267 && new == 0.34 && old == 0.425), "{told:?}");
        assert_eq!(scene.pilots[&A].ship.health.shift_profile(), Some([0.85; 4]));
    }

    /// A mining laser on the partner (locked, in reach): a cycle of 2 s, `repeat` cycles, no capacitor.
    fn mining(repeat: i32) -> Effect {
        let mut effect = afterburner();
        effect.target_id = partner();
        effect.action = Some(Action::Mining {
            family: MiningFamily::Ore,
            mining_amount_m3: 20.0,
            waste_multiplier: 1.0,
            waste_probability: 0.1,
            crit_probability: 0.05,
            crit_multiplier: 2.0,
        });
        effect.effect_id = 4_025;
        effect.duration_ms = 2_000.0;
        effect.capacitor_need = 0.0;
        effect.repeat = repeat;
        effect
    }

    /// The mining laser of `mining`, on a rock instead of the partner.
    fn mining_on(rock: i64, repeat: i32) -> Effect {
        let mut effect = mining(repeat);
        effect.target_id = rock;
        effect
    }

    /// A rock of 100 units beside the miner, which has locked it. Both pilots are in one bubble, so both see the rock.
    fn mining_rock(rock: i64) -> (Scene, i64) {
        let (mut scene, t) = reaching(0.0);
        let mut position = scene.pilots[&A].ship.position;
        position.x += 1_000.0;
        scene.place_asteroid(
            crate::wreck::Asteroid {
                id: rock,
                type_id: 0,
                group_id: 0,
                category_id: 0,
                name: "Test ore".into(),
                graphic_id: None,
                position,
                direction: eve_destiny::Vec3::new(1.0, 0.0, 0.0),
                radius: 10.0,
                units_remaining: 100,
            },
            t,
        );
        let asked = scene.target_order(A, crate::targeting::Order::Lock(rock), t);
        let (_, t) = poll_until(&mut scene, t, 40.0);
        assert!(scene.locked_by(A).contains(&rock), "the miner locked the rock: {asked:?}");
        (scene, t)
    }

    #[test]
    fn a_granted_mining_cycle_tells_the_other_pilot_what_came_off_the_rock_and_not_the_miner() {
        let rock = 99_200_001;
        let (mut scene, t) = mining_rock(rock);
        scene.activate(A, mining_on(rock, 3), t);
        let (first, at) = poll_until(&mut scene, t, 2.5);
        let (cycle, ..) = work_cycles(&first)[0];

        let told = scene.module_mined(A, MODULE, rock, cycle, 90, at);
        assert!(
            matches!(told.as_slice(), [Out::Notify { session: B, name: "OnOreMinedByOther", id_type: "charid", args, .. }]
                if args == &[crate::wire::id(rock), crate::wire::int(10)]),
            "{told:?}"
        );
        assert_eq!(scene.asteroids[&rock].units_remaining, 90, "the rock's stock follows the grant");

        // the answer settles the cycle, so a repeat of the same grant tells nobody again
        scene.module_work_result(A, MODULE, rock, cycle, true, "", false, at);
        assert!(scene.module_mined(A, MODULE, rock, cycle, 90, at).is_empty(), "a replay tells nobody");
    }

    #[test]
    fn a_rock_mined_out_by_a_granted_cycle_is_told_to_the_other_pilot_and_removed() {
        let rock = 99_200_002;
        let (mut scene, t) = mining_rock(rock);
        scene.activate(A, mining_on(rock, 3), t);
        let (first, at) = poll_until(&mut scene, t, 2.5);
        let (cycle, ..) = work_cycles(&first)[0];

        let told = scene.module_mined(A, MODULE, rock, cycle, 0, at);
        assert!(
            told.iter().any(|o| matches!(o, Out::Notify { session: B, name: "OnOreMinedByOther", args, .. }
                if args == &[crate::wire::id(rock), crate::wire::int(100)])),
            "{told:?}"
        );
        assert!(!scene.asteroids.contains_key(&rock), "a rock mined out is removed");
    }

    #[test]
    fn a_short_cycled_ore_cycle_asks_for_what_it_mined_so_far_and_stops_when_the_host_answers() {
        let (mut scene, t) = reaching(0.0);
        scene.activate(A, mining(3), t);
        let (_, at) = poll_until(&mut scene, t, 1.0);
        let out = scene.deactivate(A, MODULE, at);
        let asked = work_cycles(&out);
        assert_eq!(asked.len(), 1, "a short cycle still asks the host for what it mined: {out:?}");
        // a 2 s cycle stopped about half way: about half of its 20 m3
        assert!(asked[0].2 > 8.0 && asked[0].2 < 12.0, "{asked:?}");
        assert!(ended(&out).is_empty(), "the effect ends with the answer, not before: {out:?}");
        let answer = scene.module_work_result(A, MODULE, partner(), asked[0].0, true, "", false, at);
        assert_eq!(ended(&answer), [1], "{answer:?}");
        assert!(scene.pilots[&A].modules.running.is_empty());
    }

    #[test]
    fn a_short_cycled_ice_cycle_mines_nothing_short_and_runs_to_its_boundary() {
        let (mut scene, t) = reaching(0.0);
        let mut effect = mining(3);
        effect.action = Some(Action::Mining {
            family: MiningFamily::Ice,
            mining_amount_m3: 20.0,
            waste_multiplier: 0.0,
            waste_probability: 0.0,
            crit_probability: 0.0,
            crit_multiplier: 0.0,
        });
        scene.activate(A, effect, t);
        let (_, at) = poll_until(&mut scene, t, 1.0);
        let out = scene.deactivate(A, MODULE, at);
        assert!(work_cycles(&out).is_empty(), "an ice cycle is not short-cycled: {out:?}");
        let (boundary, _) = poll_until(&mut scene, at, 1.5);
        let asked = work_cycles(&boundary);
        assert_eq!(asked.len(), 1, "the cycle ends at its boundary and asks in full: {boundary:?}");
        assert_eq!(asked[0].2, 20.0, "{asked:?}");
    }

    #[test]
    fn a_depleted_rock_stops_the_effect_as_its_target_is_lost_and_no_next_cycle_is_asked() {
        let (mut scene, t) = reaching(0.0);
        scene.activate(A, mining(3), t);
        let (first, at) = poll_until(&mut scene, t, 2.5);
        let asked = work_cycles(&first);
        assert_eq!(asked.len(), 1, "{first:?}");
        let answer = scene.module_work_result(A, MODULE, partner(), asked[0].0, true, "", true, at);
        assert_eq!(ended(&answer), [2], "a depleted target ends the effect as lost: {answer:?}");
        let (later, _) = poll_until(&mut scene, at, 3.0);
        assert!(work_cycles(&later).is_empty(), "{later:?}");
    }

    fn work_cycles(out: &[Out]) -> Vec<(u64, u8, f64, [f64; 3])> {
        out.iter()
            .filter_map(|item| match item {
                Out::ModuleWorkCycle { cycle_id, command, mining_amount_m3, waste_roll, rounding_roll, crit_roll, .. } => {
                    Some((*cycle_id, *command, *mining_amount_m3, [*waste_roll, *rounding_roll, *crit_roll]))
                }
                _ => None,
            })
            .collect()
    }

    fn ended(out: &[Out]) -> Vec<u8> {
        out.iter().filter_map(|item| match item { Out::ModuleEnded { reason, .. } => Some(*reason), _ => None }).collect()
    }

    fn cycles_of(scene: &Scene) -> i32 {
        scene.pilots[&A].modules.running.first().map_or(0, |run| run.cycles)
    }

    #[test]
    fn a_mining_cycle_asks_the_host_at_its_end_and_parks_until_the_answer() {
        let (mut scene, t) = reaching(0.0);
        assert!(matches!(scene.activate(A, mining(3), t).first(), Some(Out::Accepted { command: 17, .. })));

        let (first, at) = poll_until(&mut scene, t, 2.5);
        let asked = work_cycles(&first);
        assert_eq!(asked.len(), 1, "the cycle's end asks the host once: {first:?}");
        let (cycle, command, amount, rolls) = asked[0];
        assert_eq!((command, amount), (7, 20.0));
        assert!(cycle > 0 && rolls.iter().all(|roll| (0.0..1.0).contains(roll)));
        assert_eq!(cycles_of(&scene), 1);

        // parked: no cycle is timed while the host decides, and the same id goes again with the same rolls
        let (waiting, at) = poll_until(&mut scene, at, 9.0);
        assert_eq!(cycles_of(&scene), 1, "no cycle begins while parked");
        let again = work_cycles(&waiting);
        assert!(again.len() >= 3, "sent again every 2 s: {waiting:?}");
        assert!(again.iter().all(|sent| *sent == asked[0]), "a repeat is the same cycle with the same rolls");

        // the answer resumes the module: the next cycle begins from the answer and asks again under a new id
        let out = scene.module_work_result(A, MODULE, partner(), cycle, true, "", false, at);
        assert!(ended(&out).is_empty(), "an accepted cycle does not end the effect: {out:?}");
        assert_eq!(cycles_of(&scene), 2);
        let (next, _) = poll_until(&mut scene, at, 2.5);
        let next_cycles = work_cycles(&next);
        assert_eq!(next_cycles.len(), 1, "{next:?}");
        assert_ne!(next_cycles[0].0, cycle, "a new cycle is a new id");
    }

    #[test]
    fn a_repeated_or_stale_answer_settles_nothing() {
        let (mut scene, t) = reaching(0.0);
        scene.activate(A, mining(3), t);
        let (first, at) = poll_until(&mut scene, t, 2.5);
        let (cycle, ..) = work_cycles(&first)[0];

        assert!(scene.module_work_result(A, MODULE, partner(), cycle + 1, true, "", false, at).is_empty(), "another cycle");
        assert!(scene.module_work_result(A, MODULE, partner() + 1, cycle, true, "", false, at).is_empty(), "another target");
        assert!(scene.module_work_result(B, MODULE, partner(), cycle, true, "", false, at).is_empty(), "another pilot");
        assert_eq!(cycles_of(&scene), 1);

        scene.module_work_result(A, MODULE, partner(), cycle, true, "", false, at);
        assert_eq!(cycles_of(&scene), 2);
        assert!(scene.module_work_result(A, MODULE, partner(), cycle, true, "", false, at + SECOND).is_empty(), "a replay settles nothing");
        assert_eq!(cycles_of(&scene), 2, "the replay does not start a cycle either");
    }

    #[test]
    fn a_refused_work_cycle_stops_the_effect_and_tells_the_host_once() {
        let (mut scene, t) = reaching(0.0);
        scene.activate(A, mining(3), t);
        let (first, at) = poll_until(&mut scene, t, 2.5);
        let (cycle, ..) = work_cycles(&first)[0];

        let out = scene.module_work_result(A, MODULE, partner(), cycle, false, "", false, at);
        assert_eq!(ended(&out), [5], "the host refused: reason 5 once: {out:?}");
        assert!(out.iter().any(|item| matches!(item, Out::ModuleEnded { session: A, module_id: MODULE, .. })));
        assert_eq!(scene.pilots[&A].modules.running.len(), 0);
        let (later, _) = poll_until(&mut scene, at, 6.0);
        assert!(work_cycles(&later).is_empty() && ended(&later).is_empty(), "nothing follows a refusal: {later:?}");
    }

    #[test]
    fn a_parked_work_cycle_stops_at_once_when_its_target_is_lost() {
        let (mut scene, t) = reaching(0.0);
        scene.activate(A, mining(3), t);
        let (first, at) = poll_until(&mut scene, t, 2.5);
        assert_eq!(work_cycles(&first).len(), 1);

        let dropped = scene.target_order(A, crate::targeting::Order::Remove(vec![partner()]), at);
        assert!(!dropped.is_empty());
        let (out, _) = poll_until(&mut scene, at, 0.5);
        assert_eq!(ended(&out), [2], "the target is gone: reason 2 once: {out:?}");
        assert!(work_cycles(&out).is_empty(), "no resend once the target is gone");
        assert_eq!(scene.pilots[&A].modules.running.len(), 0);
    }

    #[test]
    fn deactivating_a_parked_work_cycle_stops_it_now_and_tells_the_host_once() {
        let (mut scene, t) = reaching(0.0);
        scene.activate(A, mining(3), t);
        let (first, at) = poll_until(&mut scene, t, 2.5);
        assert_eq!(work_cycles(&first).len(), 1);

        let out = scene.deactivate(A, MODULE, at);
        assert!(matches!(out.first(), Some(Out::Accepted { command: 16, .. })), "{out:?}");
        assert_eq!(ended(&out), [1], "stopped by the pilot: reason 1 once: {out:?}");
        assert_eq!(scene.pilots[&A].modules.running.len(), 0);
        let (later, _) = poll_until(&mut scene, at, 6.0);
        assert!(work_cycles(&later).is_empty(), "a stopped module asks for nothing: {later:?}");
    }

    #[test]
    fn a_work_cycle_that_ends_with_the_target_gone_is_not_asked_for() {
        let (mut scene, t) = reaching(0.0);
        scene.activate(A, mining(3), t);
        let dropped = scene.target_order(A, crate::targeting::Order::Remove(vec![partner()]), t + SECOND / 2);
        assert!(!dropped.is_empty());
        let (out, _) = poll_until(&mut scene, t, 3.0);
        assert!(work_cycles(&out).is_empty(), "no yield is owed for a cycle with no target: {out:?}");
        assert_eq!(ended(&out), [2]);
    }

    #[test]
    fn tractor_passive_lock_and_auto_target_are_refused_until_they_run() {
        let (mut scene, t) = crowd();
        let mut tractor = afterburner();
        tractor.action = Some(Action::Tractor { velocity: 500.0, hold: 500.0 });
        assert!(matches!(scene.activate(A, tractor, t).first(), Some(Out::Refused { reason: "NOT_IMPLEMENTED", .. })));
        let mut passive = afterburner();
        passive.action = Some(Action::PassiveLock);
        assert!(matches!(scene.activate(A, passive, t).first(), Some(Out::Refused { reason: "NOT_IMPLEMENTED", .. })));
        let mut auto = afterburner();
        auto.action = Some(Action::AutoTarget { range: 50_000.0 });
        assert!(matches!(scene.activate(A, auto, t).first(), Some(Out::Refused { reason: "NOT_IMPLEMENTED", .. })));
        assert_eq!(scene.pilots[&A].modules.running.len(), 0);
    }

    #[test]
    fn every_end_of_an_effect_is_told_to_the_host_once_natural_or_asked() {
        // the cycles are done: one end
        let (mut scene, t) = crowd();
        let mut once = afterburner();
        once.repeat = 1;
        scene.activate(A, once, t);
        let (out, t) = poll_until(&mut scene, t, 12.0);
        assert_eq!(ended(&out), [1], "a one-cycle run ends by itself: {out:?}");

        // the capacitor cannot pay for the next cycle: one end, as it was before
        let (mut scene, t) = crowd();
        let mut all = afterburner();
        all.capacitor_need = 149.0;
        scene.activate(A, all, t);
        let (out, t) = poll_until(&mut scene, t, 11.0);
        assert_eq!(ended(&out), [1], "a capacitor that is short ends the effect: {out:?}");

        // the pilot asks to stop: one end, when the cycle it is in ends
        let (mut scene, t) = crowd();
        let mut long = afterburner();
        long.repeat = 1000;
        scene.activate(A, long, t);
        let asked = scene.deactivate(A, MODULE, t + SECOND);
        assert!(matches!(asked.first(), Some(Out::Accepted { .. })));
        let (out, _) = poll_until(&mut scene, t + SECOND, 12.0);
        assert_eq!(ended(&out), [1], "a host-requested stop is told once: {out:?}");
    }

    /// An effect that burns fuel and has no target (a siege module's shape): cycles of 2 s, `repeat` of them, no capacitor.
    fn burning(repeat: i32) -> Effect {
        let mut effect = afterburner();
        effect.target_id = 0;
        effect.action = None;
        effect.burns_fuel = true;
        effect.duration_ms = 2_000.0;
        effect.capacitor_need = 0.0;
        effect.repeat = repeat;
        effect
    }

    /// The fuel the host is asked for (`ModuleWorkCycle` with the fuel command): (cycle id, module id, target id).
    fn fuel_asks(out: &[Out]) -> Vec<(u64, i64, i64)> {
        out.iter()
            .filter_map(|item| match item {
                Out::ModuleWorkCycle { cycle_id, command: WORK_FUEL, module_id, target_id, .. } => Some((*cycle_id, *module_id, *target_id)),
                _ => None,
            })
            .collect()
    }

    /// The reasons of the activation refusals in `out`.
    fn refusals(out: &[Out]) -> Vec<&'static str> {
        out.iter().filter_map(|item| match item { Out::Refused { command: 17, reason, .. } => Some(*reason), _ => None }).collect()
    }

    #[test]
    fn a_fuel_burning_effect_asks_the_host_for_the_fuel_of_its_first_cycle_before_it_starts() {
        let (mut scene, t) = reaching(0.0);
        let out = scene.activate(A, burning(3), t);
        let asks = fuel_asks(&out);
        assert_eq!(asks.len(), 1, "{out:?}");
        assert_eq!(asks[0].1, MODULE);
        assert!(!out.iter().any(|item| matches!(item, Out::Accepted { .. })), "nothing starts before the fuel is taken: {out:?}");
        assert!(scene.pilots[&A].modules.running.is_empty());
    }

    #[test]
    fn an_accepted_fuel_ask_starts_the_effect_and_a_repeated_answer_starts_nothing_more() {
        let (mut scene, t) = reaching(0.0);
        let (cycle, module, target) = fuel_asks(&scene.activate(A, burning(3), t))[0];
        let started = scene.module_work_result(A, module, target, cycle, true, "", false, t);
        assert!(matches!(started.first(), Some(Out::Accepted { command: 17, .. })), "{started:?}");
        assert_eq!(scene.pilots[&A].modules.running.len(), 1);
        assert!(scene.module_work_result(A, module, target, cycle, true, "", false, t + SECOND).is_empty(), "a replay settles nothing");
        assert_eq!(scene.pilots[&A].modules.running.len(), 1);
    }

    #[test]
    fn a_refused_fuel_ask_starts_nothing_and_says_the_host_reason() {
        let (mut scene, t) = reaching(0.0);
        let (cycle, module, target) = fuel_asks(&scene.activate(A, burning(3), t))[0];
        let out = scene.module_work_result(A, module, target, cycle, false, "NO_FUEL", false, t);
        assert_eq!(refusals(&out), ["NO_FUEL"], "{out:?}");
        assert!(scene.pilots[&A].modules.running.is_empty());
        assert!(scene.pilots[&A].modules.pending_starts.is_empty());
    }

    #[test]
    fn a_second_activation_while_the_first_waits_for_its_fuel_is_already_active() {
        let (mut scene, t) = reaching(0.0);
        scene.activate(A, burning(3), t);
        let again = scene.activate(A, burning(3), t);
        assert_eq!(refusals(&again), ["ALREADY_ACTIVE"], "{again:?}");
    }

    #[test]
    fn a_parked_start_is_asked_again_with_the_same_cycle_id() {
        let (mut scene, t) = reaching(0.0);
        let (cycle, ..) = fuel_asks(&scene.activate(A, burning(3), t))[0];
        let (again, _) = poll_until(&mut scene, t, 2.5);
        assert_eq!(fuel_asks(&again).first().map(|ask| ask.0), Some(cycle), "{again:?}");
    }

    #[test]
    fn the_fuel_of_each_following_cycle_is_asked_at_the_cycle_end_and_taken_before_it_starts() {
        let (mut scene, t) = reaching(0.0);
        let (cycle, module, target) = fuel_asks(&scene.activate(A, burning(3), t))[0];
        scene.module_work_result(A, module, target, cycle, true, "", false, t);
        // the first cycle ends after 2 s: the fuel of the next is asked, and the next cycle does not start until it is taken
        let (first, at) = poll_until(&mut scene, t, 2.5);
        let asks = fuel_asks(&first);
        assert_eq!(asks.len(), 1, "{first:?}");
        assert_eq!(cycles_of(&scene), 1, "the next cycle has not started");
        scene.module_work_result(A, MODULE, target, asks[0].0, true, "", false, at);
        assert_eq!(cycles_of(&scene), 2);
    }

    #[test]
    fn a_refused_fuel_ask_at_a_cycle_end_stops_the_effect_with_the_fuel_reason_and_nothing_follows() {
        let (mut scene, t) = reaching(0.0);
        let (cycle, module, target) = fuel_asks(&scene.activate(A, burning(3), t))[0];
        scene.module_work_result(A, module, target, cycle, true, "", false, t);
        let (first, at) = poll_until(&mut scene, t, 2.5);
        let ask = fuel_asks(&first)[0].0;
        let out = scene.module_work_result(A, MODULE, target, ask, false, "NO_FUEL", false, at);
        assert_eq!(ended(&out), [6], "the stop names the fuel: {out:?}");
        assert!(scene.pilots[&A].modules.running.is_empty());
        let (later, _) = poll_until(&mut scene, at, 3.0);
        assert!(fuel_asks(&later).is_empty() && work_cycles(&later).is_empty(), "{later:?}");
    }

    #[test]
    fn the_last_cycle_of_a_fuel_effect_asks_for_no_more_fuel() {
        let (mut scene, t) = reaching(0.0);
        let (cycle, module, target) = fuel_asks(&scene.activate(A, burning(1), t))[0];
        scene.module_work_result(A, module, target, cycle, true, "", false, t);
        let (out, _) = poll_until(&mut scene, t, 2.5);
        assert!(fuel_asks(&out).is_empty(), "{out:?}");
        assert_eq!(ended(&out), [1], "the cycles were done: {out:?}");
    }

    #[test]
    fn an_effect_that_burns_no_fuel_is_never_asked_for_any() {
        let (mut scene, t) = reaching(0.0);
        let mut free = burning(3);
        free.burns_fuel = false;
        let out = scene.activate(A, free, t);
        assert!(fuel_asks(&out).is_empty() && matches!(out.first(), Some(Out::Accepted { .. })), "{out:?}");
        let (later, _) = poll_until(&mut scene, t, 5.0);
        assert!(fuel_asks(&later).is_empty(), "{later:?}");
    }

    #[test]
    fn a_deactivation_while_the_fuel_of_the_first_cycle_is_asked_cancels_the_start() {
        let (mut scene, t) = reaching(0.0);
        let (cycle, module, target) = fuel_asks(&scene.activate(A, burning(3), t))[0];
        let out = scene.deactivate(A, module, t);
        assert_eq!(refusals(&out), ["DEACTIVATED"], "{out:?}");
        assert!(out.iter().any(|item| matches!(item, Out::Accepted { command: 16, .. })), "{out:?}");
        // the host's answer to the cancelled ask starts nothing
        assert!(scene.module_work_result(A, module, target, cycle, true, "", false, t + SECOND).is_empty());
        assert!(scene.pilots[&A].modules.running.is_empty());
    }
}
