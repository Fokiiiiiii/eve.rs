//! Modules that run: an effect of a fitted module (an afterburner, for now) is started, goes on cycle after cycle, costs capacitor at the start of
//! each cycle, changes the numbers of the ship while it runs, and is stopped at the end of the cycle it is in when the pilot asks.
//!
//! What an effect does to the ship is worked out by the host (dogma): it says, when the effect is started, what the ship's mass and top speed are
//! with and without it and which attributes the client is to be told changed. The scene keeps the time: the cycles, the capacitor and what the
//! client is sent at each moment, in the order of `oracle/fixtures/scenarios/module.json`. The capacitor recharges along CCP's curve and its charge
//! is told every half second while it is below the capacity, as the reference does.
mod activation;
mod cycle;
pub use cycle::ModuleWorkAnswer;
mod effects;

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

/// The attributes a running module's modifiers set on the ship or on another module (its `on_changes`): the ship's warp scramble (104, a siege, a sensor array),
/// the ship's docking lock (2354), and a module's activation block (1349: a sensor array blocks the capital micro jump drives).
pub(crate) const WARP_SCRAMBLE_STATUS: i32 = 104;
pub(crate) const DISALLOW_DOCKING: i32 = 2354;
pub(crate) const ACTIVATION_BLOCKED: i32 = 1349;
/// A module's damage (dogma attribute `damage`, 3): a module whose damage reaches its hit points (9) is burnt out.
pub(crate) const MODULE_DAMAGE: i32 = 3;

/// The most module moments one settle takes for a ship (M-1). What is still due is taken by the next settle, so one ship cannot hold the sim thread.
pub(crate) const MAX_MOMENTS_PER_SETTLE: usize = 10_000;

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
            // a computer (a tracking or guidance computer) gives every percentage its own way; a disruptor takes some of them the other way (FP-3: a
            // positive tracking disruption is assistance, not an attack)
            Self::Disrupt { range_percent, falloff_percent, tracking_percent } => {
                [*range_percent, *falloff_percent, *tracking_percent].into_iter().any(|percent| percent < 0.0)
            }
            Self::GuidanceDisrupt {
                velocity_percent,
                delay_percent,
                aoe_velocity_percent,
                aoe_cloud_percent,
            } => {
                *velocity_percent < 0.0
                    || *delay_percent > 0.0
                    || *aoe_velocity_percent < 0.0
                    || *aoe_cloud_percent > 0.0
            }
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
            Self::Smartbomb { .. }
            | Self::EcmBurst { .. }
            | Self::Strike { .. }
            | Self::Field { .. }
            | Self::Doomsday { .. }
            | Self::Kamikaze { .. }
            | Self::Missile { .. }
            | Self::Bomb { .. } => true,
            Self::Cloak { .. }
            | Self::MicroJumpDrive { .. }
            | Self::ResistanceShift { .. }
            | Self::WarpBubble { .. }
            | Self::WarpImmunity
            | Self::ScrambleStatus { .. }
            | Self::WarpProbe { .. }
            | Self::StasisBurst { .. }
            | Self::Defender { .. } => false,
            Self::Charged { action, fallback, .. } => {
                action.offensive() || fallback.as_deref().is_some_and(Action::offensive)
            }
            Self::Repair { .. } => false,
            Self::Mining { .. }
            | Self::Salvage { .. }
            | Self::Tractor { .. }
            | Self::PassiveLock
            | Self::AutoTarget { .. } => false,
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
    /// Engages the ship's cloaking device (tag 5). A cloak that is not warp-safe (a standard cloaking device) holds the ship out of warp (`warp_safe` false).
    Cloak {
        warp_safe: bool,
    },
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
    Salvage {
        access_bonus: f64,
    },
    /// A tractor beam (tag 9, protocol 30): it pulls the wreck or can it is locked on, at `velocity` metres a second, until it is `hold` metres off the
    /// ship's surface. `range` is the module's reach (`maxRange`): a target further off is out of reach, and the pull ends when it is out of reach
    /// at a cycle's end.
    Tractor {
        velocity: f64,
        hold: f64,
        range: f64,
    },
    /// A warp disruption field generator's bubble (tag 16): while the effect runs, the ship is a warp-disruption source of `range` metres around its own
    /// position (`crate::warp_source`). A ship in it cannot start a warp; the bubble ends when the effect stops.
    WarpBubble {
        range: f64,
    },
    /// The ship is immune to warp bubbles while the effect runs (tag 17; an Interdiction Nullifier, attribute 1538).
    WarpImmunity,
    /// The ship's warp scramble status changes by `delta` while the effect runs (tag 18; a Warp Core Stabilizer, attribute 104, a ModAdd of -2).
    ScrambleStatus {
        delta: f64,
    },
    /// An interdiction probe (tag 19): as each cycle starts a probe is put in space at the ship, a warp-disruption source of `range` metres for
    /// `lifetime_ms`, and then it is gone (`crate::deployable`; the simulation's own timer, so a restart drops it).
    WarpProbe {
        ball: crate::deployable::Spec,
        range: f64,
        lifetime_ms: f64,
    },
    /// A bomb (tag 20): as each cycle starts it is released at the ship; `lifetime_ms` later it detonates where it is (`crate::deployable`). The ships within
    /// `blast_radius` take its effect, by its kind (its damage, or its ECM strength, or the amount it neutralises).
    Bomb {
        ball: crate::deployable::Spec,
        kind: crate::deployable::BombKind,
        lifetime_ms: f64,
        blast_radius: f64,
        damage: crate::health::Damage,
        ecm: [f64; 4],
        neutralize: f64,
    },
    /// A stasis webification burst (tag 21, the Stasis Webification Probe): as each cycle starts, every ship within `range` metres of the ship that runs it is webbed
    /// by `speed_factor` percent (a negative number slows it) for `duration_ms`. The probe is spent by the charge; no ball is put in space.
    StasisBurst {
        range: f64,
        duration_ms: f64,
        speed_factor: f64,
    },
    /// A defender launcher's cycle (tag 22, the Defender Launcher I): as the cycle starts its defender missile (`missile`, the charge's flight) goes for the nearest hostile
    /// missile in its reach and shoots it down, or hurts it by `damage` when it does not destroy it (`Scene::defender_intercept`). Target-less.
    Defender {
        missile: crate::missiles::Missile,
        damage: crate::health::Damage,
    },
    /// A passive lock (tag 10): when its cycle starts the target is locked with its notice suppressed (see `passive_lock`).
    PassiveLock,
    /// An auto-target sweep (tag 11): at each cycle start the ships that target the pilot within `range` are locked (see `auto_target_sweep`).
    AutoTarget {
        range: f64,
    },
    /// A doomsday's strike (tag 13, ADR 0013 epoch 25): the area in front of the ship is hit after a warning, pulse by pulse, and the ship is held for its windows.
    Strike {
        strike: Strike,
    },
    /// A titan doomsday (tag 14, ADR 0013 epoch 26): the locked target is hit once, `delay_ms` after the cycle starts, with `damage` (the four kinds, not scaled
    /// by a signature). The ship is held for its windows from the start, as a strike's.
    Doomsday {
        delay_ms: f64,
        damage: crate::health::Damage,
        immobile_ms: f64,
        no_jump_ms: f64,
    },
    /// A gravitational transport field (the Gravitational Transportation Field Oscillator, tag 15, protocol 29): the strike's sphere and windows; at its
    /// pulse, every ship of `moved` inside the sphere is carried to `destination` (an offset from the firing ship's centre), and every ship of `held` inside
    /// it cannot warp for `warp_block_ms`. The ship ids are the host's: the simulation only tests their geometry.
    Field {
        strike: Strike,
        destination: [f64; 3],
        warp_block_ms: f64,
        moved: Vec<i64>,
        held: Vec<i64>,
    },
    /// A kamikaze squadron's detonation (tag 23, protocol 35): it closes in on its locked target to `range` metres and, once there, hits it with `damage`
    /// (the four kinds) and is destroyed. Its cycle ends without a detonation when the window runs out first.
    Kamikaze {
        damage: crate::health::Damage,
        range: f64,
    },
    /// A fighter's missiles (tag 24, protocol 35): the locked target takes `damage` once, scaled by the application factor of `blast` for its signature and
    /// speed, when it is within `reach` metres (the range gate of the reference's `executeFighterOffensiveCycle`).
    Missile {
        damage: crate::health::Damage,
        reach: f64,
        blast: crate::missiles::Blast,
    },
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

/// The module rules the host works out from a module's type, for the rules the scene enforces (`activate`, `effect_ended`): the group a module belongs to and
/// how many of that group run at once (maxGroupActive 763, 0 for no limit), whether it holds the ship's early deactivation (906) or needs an active industrial
/// core (3265), whether it is one (`industrialCoreEffect2`, its compact variant), and how long it waits after it stops before it starts again (669, 0 for none).
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct Rules {
    pub group_id: i32,
    pub max_group_active: i32,
    pub disallow_early_deactivation: bool,
    pub requires_industrial_core: bool,
    pub provides_industrial_core: bool,
    pub reactivation_delay_ms: f64,
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
    /// The module rules of the module's type (`Rules`); the default is none (a module the host sends no rules for).
    pub rules: Rules,
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
    pub(crate) effect: Effect,
    /// When the cycle that is on began.
    cycle_start: i64,
    cycles: i32,
    /// The pulses of a strike the cycle has made (the next one is due at its `pulse_offset_ms`).
    strike_done: i32,
    /// Whether a titan doomsday has hit its target in the cycle that is on (a kamikaze or a missile hit lands once in it too).
    doomsday_hit: bool,
    /// When a kamikaze or a missile next checks whether its target is within reach (the cycle's polls, every quarter second).
    reach_poll_at: i64,
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
    pub(crate) running: Vec<Running>,
    /// The modules that wait after they stopped: the micro jump drives' cooldowns and the reactivation delay of any module (669), until the tick given.
    reactivation: Vec<(i64, i64)>,
    /// The modules burned out by an effect that sets their damage as it ends (an emergency hull energizer sets its module's damage to its hit points): they
    /// cannot start again (the reference's MODULE_INCAPACITATED).
    burnt_out: Vec<i64>,
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
            reactivation: Vec::new(),
            burnt_out: Vec::new(),
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

#[derive(Debug, Clone, Copy, PartialEq)]
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
    ReachCheck(usize),
}

/// The share of a neutralisation a target takes for its resistance: the value itself, never below 0 (immune), and whole if the value is not a number.
fn capacitor_warfare_multiplier(resistance: f64) -> f64 {
    if resistance.is_finite() { resistance.max(0.0) } else { 1.0 }
}

#[cfg(test)]
pub(crate) mod tests;
