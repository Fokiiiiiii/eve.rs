//! A running solar system: the ships in it, what commands do to them, and what the client is told (ADR 0007).
//!
//! The scene has no clock of its own. Every call says what time it is (a FILETIME) and a ship's motion is worked out
//! from the time it was last settled to that time, so the same calls at the same times give the same bytes.

use std::collections::{BTreeMap, BTreeSet, HashMap};
use std::sync::Arc;

use eve_destiny::Vec3;
use eve_marshal::Value;

use crate::flight::{Flight, Milestone};
use crate::gate;
use crate::health::Condition;
use crate::modules::Modules;
use crate::motion::{self, Target};
use crate::payload;
use crate::ship::{Course, Ship, ShipSpec};
use crate::targeting::{Targeting, UNDOCK_PROTECTION};
use crate::warp::{self, PythonRandom};
use crate::wire::{filetime, id, int, real, text, tuple, vector};
use crate::world::{BodyKind, Detail, SolarSystem};
use crate::wreck::{Wreck, WreckKind};

/// The host's name for a player's connection.
pub type SessionId = u64;

const BUMP_RESTITUTION: f64 = 0.2;
/// FILETIME ticks in a second.
pub(crate) const TICKS_PER_SECOND: i64 = 10_000_000;
/// Seconds between 1601 and 1970.
const UNIX_EPOCH_SECONDS: i64 = 11_644_473_600;
/// How long a docking takes from the moment it is accepted.
const DOCKING_DELAY: i64 = 4 * TICKS_PER_SECOND;
/// How close (from surface to surface) a ship has to be to dock.
const DOCKING_RANGE: f64 = 2_500.0;
/// How long after a jump was accepted the ship leaves for the other system at the soonest, and how long after the jump-out effect begins.
const JUMP_DELAY: i64 = 4 * TICKS_PER_SECOND;
const JUMP_EFFECT_DELAY: i64 = 3 * TICKS_PER_SECOND;
/// How many ships deep a ship that follows a ship that follows a ship is moved after the one it follows.
const MAX_FOLLOW_CHAIN: u8 = 4;
/// How far ahead of the clock a stamp may be sent (the client holds updates until their stamp).
const MAX_STAMP_LEAD: u32 = 2;
/// How far from the station's centre a ship is put when the table of undock points does not know the station's type, as a share of the
/// station's radius (the recorded ship left its station at 0.34 of it, inside the sphere the docking range is measured from), and the least.
const UNDOCK_RADIUS_SHARE: f64 = 0.34;
const UNDOCK_MIN_DISTANCE: f64 = 2_000.0;
/// How far from a belt's surface a warp may end for the ship to count as having arrived at the belt (the reference's `landingRadiusMeters`).
pub const BELT_LANDING_RADIUS: f64 = 250_000.0;

/// What a player can ask of their ship (the numbers of the client's `CmdXxx` calls).
#[derive(Debug, Clone, Copy, PartialEq)]
pub enum Command {
    SetSpeedFraction(f64),
    GotoDirection(Vec3),
    Stop,
    Dock {
        station_id: i64,
    },
    /// Approach a body and stay `range` metres off its surface.
    FollowBall {
        target_id: i64,
        range: f64,
    },
    /// Circle a body `distance` metres off its surface.
    Orbit {
        target_id: i64,
        distance: f64,
    },
    /// Turn towards a body and fly at it.
    AlignTo {
        target_id: i64,
    },
    /// Warp to a body, ending `range` metres off it (further, for bodies that keep ships at a distance).
    WarpTo {
        target_id: i64,
        range: f64,
    },
    /// Warp to an explicit coordinate, ending `range` metres short of it.
    WarpToPoint {
        destination: Vec3,
        range: f64,
    },
    /// Resolve a body's warp-in point without beginning the warp (host query).
    ResolveWarpTarget {
        target_id: i64,
        range: f64,
    },
    /// Read a body's or ship's current position (host query).
    PositionOf {
        target_id: i64,
    },
    /// Jump through a stargate; `destination_gate` is the gate the client says it leads to (0 when it does not say).
    Jump {
        gate_id: i64,
        destination_gate: i64,
    },
}

impl Command {
    /// The code of the command on the link (ADR 0007).
    pub fn code(self) -> u8 {
        match self {
            Self::SetSpeedFraction(_) => 1,
            Self::GotoDirection(_) => 2,
            Self::Stop => 3,
            Self::Dock { .. } => 4,
            Self::FollowBall { .. } => 5,
            Self::Orbit { .. } => 6,
            Self::AlignTo { .. } => 7,
            Self::WarpTo { .. } => 8,
            Self::Jump { .. } => 9,
            Self::WarpToPoint { .. } => 21,
            Self::ResolveWarpTarget { .. } => 22,
            Self::PositionOf { .. } => 23,
        }
    }
}

/// Why a ship leaves a scene.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Departure {
    Docked,
    LoggedOff,
    Jumped,
    Ejected,
    Boarded,
}

/// What the scene has to tell the host.
#[derive(Debug, Clone, PartialEq)]
pub enum Out {
    /// A notification for the player's client; the host numbers it and sends it.
    Notify { session: SessionId, name: &'static str, id_type: &'static str, args: Vec<Value> },
    /// A command was accepted, after the update it made (a `Dock` is answered with the time it was accepted at).
    Accepted { session: SessionId, command: u8, at: i64 },

    /// The result of a host position query; it does not move the ship.
    Position { session: SessionId, command: u8, position: Vec3 },
    /// A `Dock` could not be done from here: the ship now approaches the station, `distance` metres off its surface.
    Approaching { session: SessionId, command: u8, distance: f64 },
    /// A `Jump` was accepted: the ship's jump-out effect has begun and it will be in `destination_system` a few seconds on (the host is then told
    /// with a `Jumped`).
    Jumping { session: SessionId, command: u8, destination_system: i32, destination_gate: i64 },
    /// The ship has docked: the host moves the character into the station and keeps what the ship has taken.
    Docked { session: SessionId, station_id: i64, condition: Condition },
    /// A player's ship came to rest at a belt after a warp (the belt it warped to, or the nearest within [`BELT_LANDING_RADIUS`] of it): the host
    /// decides whether anything lives there. `position` is where the belt is (what the host places what lives there around).
    Arrived { session: SessionId, belt_id: i64, position: Vec3 },
    /// The answer to a question about the ship's condition.
    Condition { session: SessionId, command: u8, condition: Condition },
    /// A command the scene cannot do.
    Refused { session: SessionId, command: u8, reason: &'static str },
    /// The answer to a targeting order: whether a lock is going, and the ships and bodies locked (or, to `GetTargeters`, the ships that lock this one).
    Targets { session: SessionId, command: u8, pending: bool, targets: Vec<i64> },
    /// The ship of this session was destroyed (its hull was gone): it is no longer in the scene. `wreck` is the wreck the scene made of it, when it
    /// did (an NPC's; for a player's ship the host makes the wreck, and the pod).
    Destroyed {
        session: SessionId,
        ship_id: i64,
        wreck: Option<i64>,
        /// The wreck the ship's type leaves, whether or not the scene made it (the host makes a player's from this).
        wreck_kind: Option<WreckKind>,
        position: Vec3,
        direction: Vec3,
        radius: f64,
        killer_ship: Option<i64>,
    },
    /// A turret spent rounds: how many it has left (the host keeps the quantity of the charge item).
    ChargeSpent { session: SessionId, module_id: i64, rounds_left: i64 },
    /// The answer to `AdjustCharge`: whether the gun runs, the rounds it had and the rounds it has.
    ChargeAdjusted { session: SessionId, request_id: u64, running: bool, before: i64, after: i64 },
    /// A can the host asked for (`PlaceContainer`) is in space: where, and how it lies.
    ContainerPlaced { session: SessionId, container_id: i64, position: Vec3, direction: Vec3 },
    /// The answer to a question about how far a wreck or can is: metres from the ship's centre to its surface, or `None` when there is no such wreck.
    Measured { session: SessionId, command: u8, distance: Option<f64> },
    /// A drone was taken into the drone bay of its controller's ship (it came within reach after `ReturnBay`): the host moves its item back into the bay.
    DroneScooped { session: SessionId, drone_id: i64 },
    /// A drone is let go (`Abandon`, or its controller left the scene): it is out of the scene and the host decides what becomes of its item.
    DroneReleased { session: SessionId, drone_id: i64, position: Vec3 },
    /// A fighter squadron came back and landed (within reach of its ship after a recall): the host moves its item into the tube it came from.
    FighterLanded { session: SessionId, fighter_id: i64 },
    /// A fighter squadron is let go (abandoned, or its controller left the scene): the host decides what becomes of its item.
    FighterReleased { session: SessionId, fighter_id: i64, position: Vec3 },
    /// The answer to an order to fighter squadrons: for each `None` when it took the order, the reason when it did not.
    FighterResults { session: SessionId, command: u8, results: Vec<(i64, Option<&'static str>)> },
    /// A squadron out in space lost members: its size now (the squadron is never told a size of zero, its destruction is told instead).
    FighterSize { session: SessionId, fighter_id: i64, size: i32 },
    /// The answer to an order to drones: for each drone `None` when it took the order, the reason when it did not.
    DroneResults { session: SessionId, command: u8, results: Vec<(i64, Option<&'static str>)> },
    /// A completed drone work cycle waiting for authoritative inventory/cargo settlement by the host.
    DroneWorkCycle {
        session: SessionId,
        character_id: i64,
        ship_id: i64,
        solar_system_id: i32,
        drone_id: i64,
        target_id: i64,
        cycle_id: u64,
        command: u8,
        mining_amount_m3: f64,
        waste_multiplier: f64,
        waste_probability: f64,
        access_difficulty_bonus: f64,
        roll: f64,
    },
    /// A mutadaptive remote-repair cycle waits for the host's target-specific repair amount.
    RemoteRepairCycle {
        session: SessionId,
        module_id: i64,
        target_id: i64,
        cycle_id: u64,
        cycle_number: i32,
        cycle_at_ms: i64,
        cycle_duration_ms: i64,
        ends_effect_after_cycle: bool,
        base_amount: f64,
        bonus_per_cycle: f64,
        bonus_max: f64,
    },
    /// A work cycle of a mining laser or a salvager ended with its target there: the host decides its outcome, and the module waits (ADR 0013).
    ModuleWorkCycle {
        session: SessionId,
        character_id: i64,
        ship_id: i64,
        solar_system_id: i32,
        module_id: i64,
        target_id: i64,
        cycle_id: u64,
        command: u8,
        mining_amount_m3: f64,
        waste_multiplier: f64,
        waste_probability: f64,
        crit_probability: f64,
        crit_multiplier: f64,
        access_difficulty_bonus: f64,
        waste_roll: f64,
        rounding_roll: f64,
        crit_roll: f64,
    },
    /// An effect of a module ended (once per end): the host clears it from the ship it sends (`reason` is `Ending::wire_reason`, FP-C1).
    ModuleEnded { session: SessionId, module_id: i64, reason: u8 },
    /// A targeting order the scene refused, with what the client is told about the target.
    TargetRefused {
        session: SessionId,
        command: u8,
        reason: &'static str,
        target_type: i32,
        desired_range: f64,
        actual_distance: f64,
    },
}

impl Out {
    /// The session the message is for.
    pub fn session(&self) -> SessionId {
        match self {
            Out::Notify { session, .. }
            | Out::Accepted { session, .. }
            | Out::Approaching { session, .. }
            | Out::Jumping { session, .. }
            | Out::Docked { session, .. }
            | Out::Arrived { session, .. }
            | Out::Destroyed { session, .. }
            | Out::Condition { session, .. }
            | Out::Refused { session, .. }
            | Out::Targets { session, .. }
            | Out::ChargeSpent { session, .. }
            | Out::ChargeAdjusted { session, .. }
            | Out::ContainerPlaced { session, .. }
            | Out::Measured { session, .. }
            | Out::DroneScooped { session, .. }
            | Out::DroneReleased { session, .. }
            | Out::DroneWorkCycle { session, .. }
            | Out::RemoteRepairCycle { session, .. }
            | Out::ModuleWorkCycle { session, .. }
            | Out::ModuleEnded { session, .. }
            | Out::DroneResults { session, .. }
            | Out::FighterLanded { session, .. }
            | Out::FighterReleased { session, .. }
            | Out::FighterResults { session, .. }
            | Out::FighterSize { session, .. }
            | Out::TargetRefused { session, .. }
            | Out::Position { session, .. } => *session,
        }
    }
}

/// Why the scene cannot take a ship.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum SceneError {
    UnknownStation(i64),
    AlreadyInSpace(SessionId),
    /// The session's ship was not destroyed (a capsule is only given where a ship was lost).
    NotDestroyed(SessionId),
}

/// A module's high, medium or low power rack.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum HeatRack {
    High,
    Medium,
    Low,
}

impl HeatRack {
    const ALL: [Self; 3] = [Self::High, Self::Medium, Self::Low];

    const fn index(self) -> usize {
        match self {
            Self::High => 0,
            Self::Medium => 1,
            Self::Low => 2,
        }
    }

    const fn attribute_id(self) -> i32 {
        match self {
            Self::High => 1175,
            Self::Medium => 1176,
            Self::Low => 1177,
        }
    }
}

/// The dogma attributes needed by the simulation for one fitted module.
/// Values are supplied by the host from the fitted item and SDE.
#[derive(Debug, Clone, PartialEq)]
pub struct HeatModuleProfile {
    pub module_id: i64,
    pub flag: i32,
    /// The module's maximum structure HP (hp).
    pub structure_hp: f64,
    /// Existing module damage in HP (damage), not a 0..1 ratio.
    pub damage_hp: f64,
    /// The source module's heat damage per overloaded cycle (heatDamage).
    pub heat_damage_hp: f64,
    /// The source module's rack heat rate (heatAbsorbtionRateModifier).
    pub heat_absorption_rate: f64,
    /// True when its type has an overload effect (Dogma effect category 5).
    pub overloadable: bool,
    /// Mining modules are excluded from heat damage by the reference.
    pub heat_immune: bool,
    /// Whether the module is online and may be overloaded.
    pub online: bool,
}

/// SDE and character-derived values required by the heat simulation.
#[derive(Debug, Clone, PartialEq)]
pub struct HeatProfile {
    /// Hull heatGenerationMultiplier, defaulting to 1 when the SDE has no value.
    pub generation_multiplier: f64,
    /// Thermodynamics and hull/environment modifiers applied to heatDamage.
    pub damage_multiplier: f64,
    /// Thermodynamics level; level 1 is required to overload modules.
    pub thermodynamics_level: u8,
    /// Rack heat capacities in high, medium, low order.
    pub capacity: [f64; 3],
    /// Rack heat loaded from the host for a newly spawned ship.
    pub initial_rack_heat: [f64; 3],
    /// Rack heat dissipation rates in high, medium, low order.
    pub dissipation_rate: [f64; 3],
    /// Rack heat attenuation in high, medium, low order.
    pub attenuation: [f64; 3],
    /// All fitted high, medium and low slot modules, including non-overloadable modules.
    pub modules: Vec<HeatModuleProfile>,
}

impl Default for HeatProfile {
    fn default() -> Self {
        Self {
            generation_multiplier: 1.0,
            damage_multiplier: 1.0,
            thermodynamics_level: 0,
            capacity: [100.0; 3],
            initial_rack_heat: [0.0; 3],
            dissipation_rate: [0.01; 3],
            attenuation: [0.63; 3],
            modules: Vec::new(),
        }
    }
}

/// Invalid or unavailable module heat state.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum HeatError {
    UnknownPilot,
    ProfileNotConfigured,
    InvalidProfile(&'static str),
    UnknownModule,
    ModuleNotOverloadable,
    ModuleOffline,
    ModuleBurnedOut,
    ThermodynamicsRequired,
}

#[derive(Debug, Clone)]
struct PendingDock {
    station_id: i64,
    accepted_at: i64,
}

#[derive(Debug, Clone)]
pub(crate) struct Pilot {
    pub(crate) ship: Ship,
    /// The moment the ship's position and velocity are those of.
    settled_at: i64,
    docking: Option<PendingDock>,
    /// The warp the ship is in, from the command until it is at rest again.
    pub(crate) flight: Option<Flight>,
    /// What the ship's last warp was to (the body), kept until the next warp: where it came to rest is judged against it.
    pub(crate) warp_target: Option<i64>,
    /// The jump the ship has begun, until it leaves for the other system.
    pub(crate) jump: Option<PendingJump>,
    /// The ship arrived already in space (through a gate or on login), not by undocking.
    jumped_in: bool,
    /// The client has bound the ballpark: it can be sent destiny updates.
    pub(crate) bound: bool,
    /// The login List sent the visual half; the later bind still owes the authoritative state.
    login_visuals_sent: bool,
    /// When the ship came into the scene (the ships are listed in this order).
    pub(crate) entered: u64,
    /// The other ships this pilot has been told of and not told to forget: those in its ballpark.
    pub(crate) sees: BTreeSet<SessionId>,
    /// The wrecks this pilot has been told of and not told to forget.
    pub(crate) sees_wrecks: BTreeSet<i64>,
    pub(crate) sees_asteroids: BTreeSet<i64>,
    /// What the ship has locked and is locking.
    pub(crate) targeting: Targeting,
    /// The capacitor and the effects of the ship's modules that run.
    pub(crate) modules: Modules,
    /// Until when the ship cannot be locked (the undocking protection; it ends sooner when the ship is flown or locks something).
    pub(crate) protected_until: i64,
    /// Until when the ship cannot move, warp or jump: a doomsday's immobility window (`Strike::immobile_ms`).
    pub(crate) immobile_until: i64,
    /// Until when the ship is disrupted by a disruptive lance: it cannot warp, jump or dock, and takes half the remote repair (`Strike::debuff_ms`).
    pub(crate) disrupted_until: i64,
    /// Until when the ship cannot jump or cloak: a doomsday's no-jump window (`Strike::no_jump_ms`).
    pub(crate) no_jump_until: i64,
    /// Until when the ship cannot warp: the residual of a transport field on a capital ship (`Action::Field::warp_block_ms`, protocol 29).
    pub(crate) warp_blocked_until: i64,
    /// What other ships' modules do to this ship's numbers while they run (webs, painters, dampeners, scramblers).
    pub(crate) incoming: crate::ewar::Incoming,
    /// Cloak state used by target selection and public visibility.
    pub(crate) cloak: crate::visibility::CloakState,
    /// The overload state, per-rack heat and module heat damage carried by this ship.
    heat: HeatState,
}

impl Pilot {
    pub(crate) fn new(ship: Ship, modules: Modules, entered: u64, now: i64, protected_until: i64) -> Self {
        Self {
            ship,
            settled_at: now,
            docking: None,
            flight: None,
            warp_target: None,
            jump: None,
            jumped_in: false,
            bound: false,
            login_visuals_sent: false,
            entered,
            sees: BTreeSet::new(),
            sees_wrecks: BTreeSet::new(),
            sees_asteroids: BTreeSet::new(),
            targeting: Targeting::default(),
            modules,
            protected_until,
            immobile_until: 0,
            disrupted_until: 0,
            no_jump_until: 0,
            warp_blocked_until: 0,
            incoming: crate::ewar::Incoming::default(),
            cloak: crate::visibility::CloakState::Uncloaked,
            heat: HeatState::default(),
        }
    }

    /// Jumping clears a primed or active overheat request while rack heat continues to cool.
    fn clear_overheat_for_jump(&mut self) {
        for module in self.heat.modules.values_mut() {
            module.overloaded = false;
            module.pending_overload = None;
        }
        self.heat.force_notify = true;
    }
}

#[derive(Debug, Clone)]
pub(crate) struct PendingJump {
    gate_id: i64,
    destination_system: i32,
    destination_gate: i64,
    leaves_at: i64,
}

/// A ship whose jump is due: it has left the scene it was in and is to appear at the gate it leads to.
#[derive(Debug, Clone)]
pub struct Traveller {
    pub session: SessionId,
    pub from_system: i32,
    pub from_gate: i64,
    pub to_system: i32,
    pub to_gate: i64,
    pilot: Pilot,
}

/// One solar system with the ships in it.
#[derive(Debug)]
pub struct Scene {
    system: Arc<SolarSystem>,
    pub(crate) pilots: BTreeMap<SessionId, Pilot>,
    next_stamp: u32,
    /// How many ships have come into the scene.
    pub(crate) entries: u64,
    /// Who was in each box of the grid at the last look at what the ships see, and whether a ship was in warp then: while neither changes
    /// no ship can have come into view or left it, and the look is skipped.
    pub(crate) boxes: HashMap<(i64, i64, i64), Vec<SessionId>>,
    pub(crate) warped_last: bool,
    /// What happened to ships while their motion was worked out (a ship dropped out of warp), to be told with the next call.
    backlog: Vec<Out>,
    /// The number the last lock of a ship in this scene was given (locks are listed in the order they were asked for).
    pub(crate) target_sequence: u64,
    /// How many NPC ships have been made in this scene (the sessions they are known by are numbered from it, `crate::npc`).
    pub(crate) npc_sequence: u64,
    /// How many entity groups have been made in this scene.
    pub(crate) group_sequence: u64,
    /// The dice a shot is rolled with (`crate::weapons`).
    pub(crate) rng: PythonRandom,
    /// The offensive acts since the brains last looked (`crate::brains` tells the NPCs that were their targets).
    pub(crate) aggressions: Vec<crate::weapons::Aggression>,
    /// The missiles that are on their way to a ship, by id: entities of their own, apart from the ships (`crate::missile_entity`).
    pub(crate) missiles: BTreeMap<i64, crate::missile_entity::MissileEntity>,
    /// How many missiles have been fired in this scene (their ids are numbered from it).
    pub(crate) missile_sequence: u64,
    /// The drones of the scene, by the session each is known by (they are NPC-session pilots tied to the ship that controls them, `crate::drones`).
    pub(crate) drones: BTreeMap<SessionId, crate::drones::DroneAi>,
    /// Who attacked each controller lately (ship id and when), for the idle drones that take up a fight by themselves (`crate::drones`).
    pub(crate) drone_threats: BTreeMap<SessionId, Vec<(i64, i64)>>,
    /// The trees of the NPCs (`crate::brains`).
    pub(crate) brains: Option<crate::brains::Brains>,
    /// The wrecks in the scene, by item id (`crate::wreck`).
    pub(crate) wrecks: BTreeMap<i64, Wreck>,
    /// Mineable asteroid rocks in this system; their remaining stock is authoritative in the host inventory.
    pub(crate) asteroids: BTreeMap<i64, crate::wreck::Asteroid>,
    /// How many wrecks of ships that died in this scene have been given an id by it (`crate::wreck::TRANSIENT_ID_BASE`).
    pub(crate) wreck_sequence: u64,
    /// The ships destroyed since the brains last looked (`crate::brains` tells the trees that watched them).
    pub(crate) destroyed: Vec<i64>,
    /// Which wreck a ship of some type leaves (`crate::wreck_types`), when the static data is there to say.
    pub(crate) wreck_catalog: Option<Arc<crate::wreck_types::WreckCatalog>>,
    /// The sessions whose ship was destroyed and have no other yet (the host gives them a capsule with `respawn`): what they had bound and seen.
    pub(crate) widowed: BTreeMap<SessionId, Widow>,
    /// Unpiloted ships stay in the scene under synthetic sessions until someone boards or destroys them.
    pub(crate) unpiloted: BTreeMap<i64, SessionId>,
}

/// What the client of a destroyed ship still has of the ballpark: it keeps it when it is given a new ship.
#[derive(Debug, Default)]
pub(crate) struct Widow {
    pub(crate) bound: bool,
    pub(crate) sees: BTreeSet<SessionId>,
    pub(crate) sees_wrecks: BTreeSet<i64>,
    pub(crate) sees_asteroids: BTreeSet<i64>,
}

/// The destiny stamp of a moment: whole seconds of the Unix clock.
pub fn stamp_at(now: i64) -> u32 {
    (now.div_euclid(TICKS_PER_SECOND) - UNIX_EPOCH_SECONDS) as u32
}

impl Scene {
    pub fn new(system: Arc<SolarSystem>) -> Self {
        Self {
            system,
            pilots: BTreeMap::new(),
            next_stamp: 0,
            entries: 0,
            boxes: HashMap::new(),
            warped_last: false,
            backlog: Vec::new(),
            target_sequence: 0,
            npc_sequence: 0,
            group_sequence: 0,
            rng: PythonRandom::new(0x5348_4f54),
            aggressions: Vec::new(),
            missiles: BTreeMap::new(),
            missile_sequence: 0,
            drones: BTreeMap::new(),
            drone_threats: BTreeMap::new(),
            brains: None,
            wrecks: BTreeMap::new(),
            asteroids: BTreeMap::new(),
            wreck_sequence: 0,
            destroyed: Vec::new(),
            widowed: BTreeMap::new(),
            unpiloted: BTreeMap::new(),
            wreck_catalog: None,
        }
    }

pub(crate) fn solar_system_id(&self) -> i32 {
        self.system.id
    }

    /// Gives the scene the item types the wreck rules look up, so that a ship that is destroyed leaves the wreck of its type.
    pub fn set_wreck_catalog(&mut self, catalog: Arc<crate::wreck_types::WreckCatalog>) {
        self.wreck_catalog = Some(catalog);
    }

    pub fn system(&self) -> &SolarSystem {
        &self.system
    }

    /// Whether no player is in the scene (NPCs do not keep a scene: a system has one while somebody is there to see it).
    pub fn is_empty(&self) -> bool {
        self.widowed.is_empty() && self.pilots.keys().all(|session| crate::npc::is_npc_session(*session))
    }

    /// The wreck with this item id, if it is in the scene.
    pub fn wreck(&self, id: i64) -> Option<&Wreck> {
        self.wrecks.get(&id)
    }

    /// A session whose ship was destroyed will not be given another (it left): its ballpark is not kept.
    pub fn forget_widow(&mut self, session: SessionId) {
        self.widowed.remove(&session);
    }

    pub fn contains(&self, session: SessionId) -> bool {
        self.pilots.contains_key(&session)
    }

    /// The ship of a session, as it is at `now`.
    pub fn ship(&mut self, session: SessionId, now: i64) -> Option<&Ship> {
        self.pilots.contains_key(&session).then_some(())?;
        self.settle_pilot(session, now);
        self.pilots.get(&session).map(|pilot| &pilot.ship)
    }

    /// The next stamp a change can carry: one after the last, but never more than two ahead of the clock, and never behind it.
    pub(crate) fn next_stamp(&mut self, now: i64) -> u32 {
        let current = stamp_at(now);
        self.next_stamp = if self.next_stamp < current {
            current
        } else if self.next_stamp - current >= MAX_STAMP_LEAD {
            current + MAX_STAMP_LEAD
        } else {
            self.next_stamp + 1
        };
        self.next_stamp
    }

    /// Puts a ship in space at the station it undocks from, moving away at its top speed.
    pub fn undock(
        &mut self,
        session: SessionId,
        spec: ShipSpec,
        station_id: i64,
        now: i64,
    ) -> Result<Vec<Out>, SceneError> {
        if self.pilots.contains_key(&session) {
            return Err(SceneError::AlreadyInSpace(session));
        }
        let station = self
            .system
            .body(station_id)
            .filter(|b| b.kind == BodyKind::Station)
            .ok_or(SceneError::UnknownStation(station_id))?;
        let (position, direction) = undock_place(station, spec.group_id);
        let speed = spec.max_velocity;
        let modules = Modules::with_charge(
            spec.capacitor_capacity,
            spec.capacitor_recharge_s,
            spec.condition.charge,
            now,
        );
        let velocity = Vec3::new(direction.x * speed, direction.y * speed, direction.z * speed);
        let ship = Ship::new(spec, position, velocity, direction, 1.0, Course::Goto, now);
        let entered = self.next_entry();
        self.pilots.insert(session, Pilot::new(ship, modules, entered, now, now + UNDOCK_PROTECTION));
        Ok(self.announce_arrival(session, now))
    }

    /// Restores a pilot whose ship is already in space at its saved position.
    pub fn enter_in_space(
        &mut self,
        session: SessionId,
        spec: ShipSpec,
        position: [f64; 3],
        direction: [f64; 3],
        now: i64,
    ) -> Result<Vec<Out>, SceneError> {
        if self.pilots.contains_key(&session) {
            return Err(SceneError::AlreadyInSpace(session));
        }
        let position = Vec3::new(position[0], position[1], position[2]);
        let direction = Vec3::new(direction[0], direction[1], direction[2]);
        let modules = Modules::with_charge(
            spec.capacitor_capacity,
            spec.capacitor_recharge_s,
            spec.condition.charge,
            now,
        );
        let ship = Ship::new(
            spec,
            position,
            Vec3::new(0.0, 0.0, 0.0),
            direction,
            0.0,
            Course::Stop,
            now,
        );
        let entered = self.next_entry();
        let mut pilot = Pilot::new(ship, modules, entered, now, now);
        pilot.jumped_in = true;
        self.pilots.insert(session, pilot);
        Ok(self.announce_arrival(session, now))
    }

    pub fn leave(&mut self, session: SessionId, why: Departure, now: i64) -> Option<Vec<Out>> {
        let mut pilot = self.pilots.remove(&session)?;
        if let Some(brains) = self.brains.as_mut() {
            brains.forget(session);
        }
        let ship_id = pilot.ship.spec.ship_id;
        let mut out = self.drop_targeting(ship_id, &pilot.targeting, now);
        let logoff_at = matches!(why, Departure::LoggedOff).then_some(pilot.ship.position);
        out.extend(self.release_drones(session, logoff_at, now));

        if matches!(why, Departure::Ejected | Departure::Boarded) {
            // Preserve the client's ballpark while its active ship changes.
            self.widowed.insert(session, Widow {
                bound: pilot.bound,
                sees: pilot.sees.clone(),
                sees_wrecks: pilot.sees_wrecks.clone(),
                sees_asteroids: pilot.sees_asteroids.clone(),
            });
            // An ejected hull (or the capsule left when boarding) remains a moving,
            // targetable ship. Give it a simulation-only session until it is boarded.
            let fake_session = (1_u64 << 61).saturating_add(self.next_entry());
            pilot.bound = false;
            pilot.sees.clear();
            pilot.sees_wrecks.clear();
            pilot.sees_asteroids.clear();
            self.unpiloted.insert(ship_id, fake_session);
            self.pilots.insert(fake_session, pilot);
            out.extend(self.announce_departure(session, ship_id, now));
            out.extend(self.announce_arrival(fake_session, now));
            return Some(out);
        }

        out.extend(self.announce_departure(session, ship_id, now));
        Some(out)
    }

    pub(crate) fn notify(session: SessionId, name: &'static str, args: Vec<Value>) -> Out {
        Out::Notify { session, name, id_type: "clientID", args }
    }

    /// Send the visual half of a login-in-space bootstrap; the later ballpark bind sends the full SetState.
    pub fn prime_login_visuals(&mut self, session: SessionId, now: i64) -> Vec<Out> {
        if self.pilots.get(&session).is_none_or(|pilot| pilot.bound || pilot.login_visuals_sent) {
            return Vec::new();
        }
        self.settle_all(now);
        let stamp = self.next_stamp(now);
        let ships = self.ships_visible_to(session);
        let Ok(added) = payload::add_ships(stamp, &ships, now) else { return Vec::new() };
        let told = vec![
            Self::notify(session, "OnSetTimeDilation", vec![int(1), int(1), int(100_000_000)]),
            Self::notify(session, "DoSimClockRebase", vec![tuple([filetime(now), filetime(now)])]),
            Self::notify(session, "DoDestinyUpdate", payload::destiny_update([added], true)),
        ];
        if let Some(pilot) = self.pilots.get_mut(&session) {
            pilot.login_visuals_sent = true;
        }
        told
    }

    /// The client bound the system's ballpark: it is told the time, and given the ships in view (its own among them, in the order they came into
    /// the scene) and then everything around them.
    pub fn bind(&mut self, session: SessionId, now: i64) -> Vec<Out> {
        if !self.pilots.contains_key(&session) {
            return Vec::new();
        }
        let login_visuals_sent = self.pilots[&session].login_visuals_sent;
        self.settle_all(now);
        self.take_in_view(session);
        let first = self.next_stamp(now);
        let second = self.next_stamp(now);
        let system = Arc::clone(&self.system);
        let ships = self.ships_in_view(session);
        let Some(pilot) = self.pilots.get(&session) else { return Vec::new() };
        let ship = &pilot.ship;
        let ship_id = id(ship.spec.ship_id);
        let heading =
            |action_stamp: u32| payload::action(action_stamp, heading_name(ship), heading_args(ship));

        let added = if login_visuals_sent {
            None
        } else {
            let Ok(added) = payload::add_ships(first, &ships, now) else { return Vec::new() };
            Some(added)
        };
        let wrecks = self.wrecks_in_view(session);
        let Ok(state) = payload::set_state(second, &system, ship, &ships, &wrecks, now) else {
            return Vec::new();
        };
        let update = |actions: Vec<Value>| {
            Self::notify(session, "DoDestinyUpdate", payload::destiny_update(actions, false))
        };
        let mut told = Vec::new();
        if let Some(added) = added {
            told.extend([
                Self::notify(session, "OnSetTimeDilation", vec![int(1), int(1), int(100_000_000)]),
                Self::notify(session, "DoSimClockRebase", vec![tuple([filetime(now), filetime(now)])]),
                // An undocked ship waits for its ball; an in-space arrival does not.
                Self::notify(session, "DoDestinyUpdate", payload::destiny_update([added], !pilot.jumped_in)),
            ]);
        }
        if !pilot.jumped_in {
            // A ship that undocked moves as it is bound; an in-space arrival is already at rest.
            told.push(update(vec![
                payload::uncloak(first, ship.spec.ship_id),
                payload::action(
                    first,
                    "SetBallPosition",
                    with_id(&ship_id, vector(ship.position.x, ship.position.y, ship.position.z)),
                ),
                payload::action(first, "SetBallMassive", [ship_id.clone(), int(0)]),
                payload::action(first, "SetBallMass", [ship_id.clone(), real(ship.spec.mass)]),
                payload::action(
                    first,
                    "SetBallVelocity",
                    with_id(&ship_id, vector(ship.velocity.x, ship.velocity.y, ship.velocity.z)),
                ),
                heading(first),
            ]));
        }
        told.push(update(vec![state]));
        // The numbers of every ship in view; the pilot's own lack the mass when the bind has sent it already.
        let mut numbers = Vec::new();
        for other in &ships {
            if other.spec.ship_id != ship.spec.ship_id || pilot.jumped_in {
                numbers.extend(Self::numbers_of(other, second));
            } else {
                let own = id(ship.spec.ship_id);
                numbers.push(payload::action(
                    second,
                    "SetBallAgility",
                    [own.clone(), real(ship.spec.agility)],
                ));
                numbers.push(payload::action(second, "SetMaxSpeed", [own, real(ship.spec.max_velocity)]));
            }
        }
        told.push(update(numbers));
        if login_visuals_sent {
            if let Some(pilot) = self.pilots.get_mut(&session) {
                pilot.login_visuals_sent = false;
            }
        }
        self.with_backlog(told)
    }

    /// The client asked for the state again (`UpdateStateRequest`): every ball as it is now.
    pub fn update_state(&mut self, session: SessionId, now: i64) -> Vec<Out> {
        if !self.pilots.contains_key(&session) {
            return Vec::new();
        }
        self.settle_all(now);
        let stamp = self.next_stamp(now);
        let system = Arc::clone(&self.system);
        let ships = self.ships_in_view(session);
        let Some(pilot) = self.pilots.get(&session) else { return Vec::new() };
        let wrecks = self.wrecks_in_view(session);
        let told = match payload::set_state(stamp, &system, &pilot.ship, &ships, &wrecks, now) {
            Ok(state) => {
                vec![Self::notify(session, "DoDestinyUpdate", payload::destiny_update([state], false))]
            }
            Err(_) => Vec::new(),
        };
        self.with_backlog(told)
    }

    /// A command of the player's. Movement is applied from `now`; what the client is told is one update at the next stamp (none when the
    /// ship already does what it is asked), and the host is told the command was accepted, after the update.
    pub fn command(&mut self, session: SessionId, command: Command, now: i64) -> Vec<Out> {
        let code = command.code();
        if !self.pilots.contains_key(&session) {
            return vec![Out::Refused { session, command: code, reason: "NO_SHIP_IN_SPACE" }];
        }
        self.settle_pilot(session, now);
        let told = match command {
            Command::Dock { station_id } => self.dock(session, station_id, now),
            Command::WarpTo { target_id, range } => self.warp_to(session, target_id, range, now),
            Command::WarpToPoint { destination, range } => self.warp_to_point(session, destination, range, now),
            Command::ResolveWarpTarget { target_id, range } => self.resolve_warp_target(session, target_id, range, now),
            Command::PositionOf { target_id } => self.position_of(session, target_id),
            Command::Jump { gate_id, destination_gate } => self.jump(session, gate_id, destination_gate, now),
            other => self.steer_pilot(session, other, now),
        };
        // the ship was flown: the undocking protection is over (a docking does not end it)
        if told.iter().any(|o| matches!(o, Out::Accepted { command, .. } if *command != 4))
            && let Some(pilot) = self.pilots.get_mut(&session)
        {
            pilot.protected_until = 0;
        }
        self.with_backlog(told)
    }

    /// Tells what happened while ships were moved on, then `told`.
    pub(crate) fn with_backlog(&mut self, told: Vec<Out>) -> Vec<Out> {
        let mut out = std::mem::take(&mut self.backlog);
        out.extend(told);
        out
    }

    pub(crate) fn settle_pilot(&mut self, session: SessionId, now: i64) {
        if !self.pilots.contains_key(&session) {
            return;
        }
        let starts = self.settle_all_motion(now);
        self.resolve_collisions(now, Some(session), Some(&starts));
    }

    /// As [`Scene::settle_pilot`]; a ship that follows or orbits another ship is moved after the ship it follows (`depth` stops a ring of ships
    /// that follow one another).
    fn settle_pilot_after(&mut self, session: SessionId, now: i64, depth: u8) {
        let system = Arc::clone(&self.system);
        let course_target = match self.pilots.get(&session).map(|pilot| pilot.ship.course) {
            Some(Course::Follow { target, .. } | Course::Orbit { target, .. })
                if system.body(target).is_none() =>
            {
                Some(target)
            }
            _ => None,
        };
        let followed =
            course_target.and_then(|target| self.session_of(target).filter(|other| *other != session));
        let mut ship_target = None;
        if let Some(other) = followed {
            if depth < MAX_FOLLOW_CHAIN {
                self.settle_pilot_after(other, now, depth + 1);
            }
            ship_target = self.target_of_ship(other);
        } else if let Some(wreck) = course_target.and_then(|target| self.wrecks.get(&target)) {
            // a wreck or a can does not move
            ship_target = Some(Target::at_rest(wreck.position, wreck.radius));
        } else if let Some(asteroid) = course_target.and_then(|target| self.asteroids.get(&target)) {
            // mineable rocks are stationary targets in this scene.
            ship_target = Some(Target::at_rest(asteroid.position, asteroid.radius));
        }
        let Some(pilot) = self.pilots.get_mut(&session) else { return };
        let from = pilot.settled_at;
        let milestones = settle(pilot, &system, ship_target, now);
        // a wreck this ship's beam pulls moves over the same time as the ship
        self.advance_pulled_by(session, from, now);
        for milestone in milestones {
            let told = self.tell(session, milestone);
            self.backlog.extend(told);
        }
    }

    pub(crate) fn settle_all(&mut self, now: i64) {
        let starts = self.settle_all_motion(now);
        self.resolve_collisions(now, None, Some(&starts));
        self.release_orphaned_pulls(now);
    }

    fn settle_all_motion(&mut self, now: i64) -> BTreeMap<SessionId, (i64, Vec3, bool)> {
        let starts = self
            .pilots
            .iter()
            .map(|(session, pilot)| {
                (
                    *session,
                    (pilot.settled_at, pilot.ship.position, pilot.flight.is_some() || pilot.jump.is_some()),
                )
            })
            .collect();
        let sessions: Vec<SessionId> = self.pilots.keys().copied().collect();
        for session in sessions {
            self.settle_pilot_after(session, now, 0);
        }
        starts
    }

    /// Resolves swept contacts and remaining overlaps after ordinary movement settlement.
    fn resolve_collisions(
        &mut self,
        now: i64,
        only: Option<SessionId>,
        starts: Option<&BTreeMap<SessionId, (i64, Vec3, bool)>>,
    ) {
        // ponytail: all-pairs costs O(ships^2 + ships*bodies); use spatial buckets if populated systems make this tick expensive.
        let mut ships: Vec<(SessionId, i64, Ship)> = self
            .pilots
            .iter()
            .filter_map(|(session, pilot)| {
                (pilot.settled_at == now
                    && !self.drones.contains_key(session)
                    && pilot.flight.is_none()
                    && pilot.docking.is_none()
                    && pilot.jump.is_none())
                .then(|| (*session, pilot.protected_until, pilot.ship.clone()))
            })
            .collect();
        let mut active: BTreeSet<SessionId> = match only {
            Some(session) => [session].into_iter().collect(),
            None => ships.iter().map(|(session, _, _)| *session).collect(),
        };
        if !ships.iter().any(|(session, _, _)| active.contains(session)) {
            return;
        }
        let mut bumped = BTreeSet::new();
        if let Some(starts) = starts {
            resolve_swept_collisions(&mut ships, &self.system.bodies, starts, now, &mut active, &mut bumped);
        }

        for _ in 0..4 {
            let mut collided = BTreeSet::new();
            for left_index in 0..ships.len() {
                for right_index in left_index + 1..ships.len() {
                    let left_session = ships[left_index].0;
                    let right_session = ships[right_index].0;
                    if !active.contains(&left_session) && !active.contains(&right_session) {
                        continue;
                    }
                    let (left, right) = ships.split_at_mut(right_index);
                    if bump_ships(&mut left[left_index].2, &mut right[0].2) {
                        collided.insert(left_session);
                        collided.insert(right_session);
                    }
                }
            }
            for (session, protected_until, ship) in &mut ships {
                if !active.contains(session) {
                    continue;
                }
                for body in &self.system.bodies {
                    if body.kind == BodyKind::Belt
                        || (body.kind == BodyKind::Station && *protected_until > now)
                    {
                        continue;
                    }
                    if bump_ship_body(ship, body) {
                        collided.insert(*session);
                    }
                }
            }
            if collided.is_empty() {
                break;
            }
            active.extend(collided.iter().copied());
            bumped.extend(collided);
        }

        for (session, _, ship) in &mut ships {
            if !bumped.contains(session) {
                continue;
            }
            settle_bumped_ship(ship);
            if let Some(pilot) = self.pilots.get_mut(session) {
                pilot.ship = ship.clone();
            }
        }
        for session in bumped {
            let Some((ship_id, position, velocity, direction, speed_fraction, stopped)) =
                self.pilots.get(&session).map(|pilot| {
                    let ship = &pilot.ship;
                    (
                        id(ship.spec.ship_id),
                        ship.position,
                        ship.velocity,
                        ship.direction,
                        ship.speed_fraction,
                        matches!(ship.course, Course::Stop),
                    )
                })
            else {
                continue;
            };
            if !self.pilots.get(&session).is_some_and(|pilot| pilot.bound)
                && self.observers_of(session).is_empty()
            {
                continue;
            }
            let stamp = self.next_stamp(now);
            let mut actions = vec![
                payload::action(
                    stamp,
                    "SetBallPosition",
                    with_id(&ship_id, vector(position.x, position.y, position.z)),
                ),
                payload::action(
                    stamp,
                    "SetBallVelocity",
                    with_id(&ship_id, vector(velocity.x, velocity.y, velocity.z)),
                ),
                payload::action(stamp, "SetSpeedFraction", [ship_id.clone(), real(speed_fraction)]),
            ];
            if stopped {
                actions.push(payload::action(stamp, "Stop", [ship_id]));
            } else {
                actions.push(payload::action(
                    stamp,
                    "GotoDirection",
                    [ship_id, real(direction.x), real(direction.y), real(direction.z)],
                ));
            }
            let own = if self.pilots.get(&session).is_some_and(|pilot| pilot.bound) {
                actions.clone()
            } else {
                Vec::new()
            };
            self.backlog.extend(self.tell_split(session, own, actions));
        }
    }

    /// What is told of a milestone of a warp: to the pilot, and to the ones who watch it.
    fn tell(&mut self, session: SessionId, milestone: Milestone) -> Vec<Out> {
        let Some(pilot) = self.pilots.get(&session) else { return Vec::new() };
        let ship_id = id(pilot.ship.spec.ship_id);
        match milestone {
            Milestone::Entered { at } => {
                // the ship is in warp: those who watch it are told now (the pilot was told when it asked for the warp)
                let Course::Warp { raw_destination: r, stop_distance, factor, .. } = pilot.ship.course else {
                    return Vec::new();
                };
                let stamp = self.next_stamp(at);
                let watchers = vec![
                    warping_effect(stamp, &ship_id),
                    payload::action(stamp, "SetSpeedFraction", [ship_id.clone(), real(1.0)]),
                    payload::action(
                        stamp,
                        "WarpTo",
                        [ship_id, real(r.x), real(r.y), real(r.z), real(stop_distance), int(factor)],
                    ),
                ];
                self.tell_split(session, Vec::new(), watchers)
            }
            Milestone::Dropout { at } => {
                // the ship is set massless again, told twice: at the stamp and two on
                let stamp = self.next_stamp(at);
                let massless =
                    |stamp: u32| vec![payload::action(stamp, "SetBallMassive", [ship_id.clone(), int(0)])];
                let mut out = self.tell_all(session, massless(stamp));
                out.extend(self.tell_all(session, massless(stamp + 2)));
                out
            }
            Milestone::Rest { at, position, velocity } => {
                let stamp = self.next_stamp(at);
                let actions = vec![
                    payload::action(
                        stamp,
                        "SetBallPosition",
                        with_id(&ship_id, vector(position.x, position.y, position.z)),
                    ),
                    payload::action(
                        stamp,
                        "SetBallVelocity",
                        with_id(&ship_id, vector(velocity.x, velocity.y, velocity.z)),
                    ),
                    payload::action(stamp, "SetSpeedFraction", [ship_id.clone(), real(0.0)]),
                    payload::action(stamp, "Stop", [ship_id]),
                ];
                let mut out = self.tell_all(session, actions);
                if !crate::npc::is_npc_session(session)
                    && let Some((belt_id, position)) = self.belt_arrived_at(session, position)
                {
                    out.push(Out::Arrived { session, belt_id, position });
                }
                out
            }
        }
    }

    /// The belt a ship that came to rest at `position` has arrived at: the one it warped to, else the nearest within the landing radius (its id and
    /// where it is).
    fn belt_arrived_at(&self, session: SessionId, position: Vec3) -> Option<(i64, Vec3)> {
        let system = &self.system;
        let warped_to = self.pilots.get(&session).and_then(|pilot| pilot.warp_target);
        if let Some(belt) =
            warped_to.and_then(|id| system.body(id)).filter(|body| body.kind == BodyKind::Belt)
        {
            return Some((belt.id, belt.position));
        }
        let candidates = system.bodies.iter().filter(|body| body.kind == BodyKind::Belt).map(|body| {
            crate::geo::SpatialEntity {
                id: body.id,
                kind: crate::geo::SpatialKind::SystemBody,
                character_id: None,
                owner_id: None,
                corporation_id: None,
                alliance_id: None,
                type_id: None,
                group_id: None,
                category_id: None,
                position: [body.position.x, body.position.y, body.position.z],
                radius: body.radius,
                warping: false,
                item: body,
            }
        });
        crate::geo::SpatialQuery::radius(
            [position.x, position.y, position.z],
            f64::MAX,
            crate::geo::SpatialFilter::default(),
        )
        .execute(candidates)
        .into_iter()
        .find(|hit| hit.distance <= BELT_LANDING_RADIUS + hit.item.radius)
        .map(|hit| (hit.id, hit.item.position))
    }

    /// `CmdWarpToStuff`: resolve a body or ship to its warp-in point and start a warp.
    fn warp_to(&mut self, session: SessionId, target_id: i64, range: f64, now: i64) -> Vec<Out> {
        let code = Command::WarpTo { target_id, range }.code();
        let refused = |reason| vec![Out::Refused { session, command: code, reason }];
        if !range.is_finite() {
            return refused("BAD_RANGE");
        }
        let Some(pilot) = self.pilots.get(&session) else { return refused("NO_SHIP_IN_SPACE") };
        let Some((raw, stop)) = self.warp_target_point(pilot, target_id, range, now) else {
            return refused("TARGET_NOT_FOUND");
        };
        self.begin_warp(session, code, Some(target_id), raw, stop, now)
    }


    /// Warp to an explicit point, used by saved coordinates and formation placements.
    fn warp_to_point(&mut self, session: SessionId, destination: Vec3, range: f64, now: i64) -> Vec<Out> {
        let code = Command::WarpToPoint { destination, range }.code();
        let refused = |reason| vec![Out::Refused { session, command: code, reason }];
        if !finite_vector(destination) || !range.is_finite() {
            return refused("BAD_WARP_POINT");
        }
        self.begin_warp(session, code, None, destination, range.max(0.0), now)
    }

    fn begin_warp(
        &mut self,
        session: SessionId,
        code: u8,
        target_id: Option<i64>,
        raw: Vec3,
        stop: f64,
        now: i64,
    ) -> Vec<Out> {
        let refused = |reason| vec![Out::Refused { session, command: code, reason }];
        let Some(pilot) = self.pilots.get(&session) else { return refused("NO_SHIP_IN_SPACE") };
        if pilot.docking.is_some() {
            return refused("DOCKING");
        }
        if pilot.jump.is_some() {
            return refused("JUMPING");
        }
        if pilot.flight.is_some() {
            return refused("IN_WARP");
        }
        if pilot.immobile_until > now {
            return refused("IMMOBILE");
        }
        if pilot.disrupted_until > now {
            return refused("WARP_DISRUPTED");
        }
        if pilot.warp_blocked_until > now {
            return refused("WARP_BLOCKED");
        }
        if pilot.incoming.scrambled() {
            return refused("WARP_SCRAMBLED");
        }
        let ship = &pilot.ship;
        let toward = motion::unit(Vec3::new(
            raw.x - ship.position.x,
            raw.y - ship.position.y,
            raw.z - ship.position.z,
        ))
        .unwrap_or(ship.direction);
        let end = Vec3::new(raw.x - toward.x * stop, raw.y - toward.y * stop, raw.z - toward.z * stop);
        let total = ((end.x - ship.position.x).powi(2)
            + (end.y - ship.position.y).powi(2)
            + (end.z - ship.position.z).powi(2))
        .sqrt();
        if total < warp::MINIMUM_WARP_DISTANCE {
            return refused("WARP_DISTANCE_TOO_CLOSE");
        }

        let profile = warp::Profile::new(total, ship.spec.warp_speed_au, ship.spec.max_velocity);
        let ship_id = id(ship.spec.ship_id);
        let boosted = ship.spec.max_velocity * warp::START_SPEED_SCALE;
        let stamp = self.next_stamp(now);
        let pilot = self.pilots.get_mut(&session).expect("checked above");
        pilot.ship.course = Course::Warp {
            raw_destination: raw,
            target_point: end,
            stop_distance: stop,
            total_distance: total,
            factor: profile.factor,
            active: false,
        };
        pilot.ship.speed_fraction = 1.0;
        pilot.ship.direction = toward;
        pilot.flight = Some(Flight::begin(now, raw, stop));
        pilot.warp_target = target_id;
        let own = vec![
            payload::action(stamp, "SetMaxSpeed", [ship_id.clone(), real(boosted)]),
            payload::action(
                stamp,
                "WarpTo",
                [ship_id.clone(), real(raw.x), real(raw.y), real(raw.z), real(stop), int(profile.factor)],
            ),
            warping_effect(stamp, &ship_id),
            payload::action(stamp, "SetSpeedFraction", [ship_id.clone(), real(1.0)]),
        ];
        let watchers = vec![payload::action(
            stamp,
            "GotoDirection",
            with_id(&ship_id, vector(toward.x, toward.y, toward.z)),
        )];
        let mut out = self.tell_split(session, own, watchers);
        out.push(Out::Accepted { session, command: code, at: now });
        out
    }


    fn warp_target_point(&self, pilot: &Pilot, target_id: i64, range: f64, now: i64) -> Option<(Vec3, f64)> {
        if let Some(body) = self.system.body(target_id) {
            let ship = &pilot.ship;
            return Some(match warp::warp_in_point(body) {
                Some(point) => (point, range.max(0.0)),
                None if body.kind == BodyKind::Stargate => {
                    let mut random = PythonRandom::new(seed_of(ship.spec.ship_id, target_id, now));
                    gate::warp_landing(body, ship.position, ship.direction, range, &mut random)
                }
                None => (body.position, warp::stop_distance(body, ship.spec.radius, range)),
            });
        }

        self.pilots
            .values()
            .find(|target| target.ship.spec.ship_id == target_id)
            .map(|target| (
                target.ship.position,
                range.max(0.0) + target.ship.spec.radius + pilot.ship.spec.radius,
            ))
    }

    fn resolve_warp_target(&self, session: SessionId, target_id: i64, range: f64, now: i64) -> Vec<Out> {
        let command = Command::ResolveWarpTarget { target_id, range }.code();
        let refused = |reason| vec![Out::Refused { session, command, reason }];
        if !range.is_finite() {
            return refused("BAD_RANGE");
        }
        let Some(pilot) = self.pilots.get(&session) else { return refused("NO_SHIP_IN_SPACE") };
        let Some((raw, stop)) = self.warp_target_point(pilot, target_id, range, now) else {
            return refused("TARGET_NOT_FOUND");
        };
        let toward = motion::unit(Vec3::new(
            raw.x - pilot.ship.position.x,
            raw.y - pilot.ship.position.y,
            raw.z - pilot.ship.position.z,
        ))
        .unwrap_or(pilot.ship.direction);
        let position = Vec3::new(raw.x - toward.x * stop, raw.y - toward.y * stop, raw.z - toward.z * stop);
        let distance = ((position.x - pilot.ship.position.x).powi(2)
            + (position.y - pilot.ship.position.y).powi(2)
            + (position.z - pilot.ship.position.z).powi(2))
        .sqrt();
        if distance < warp::MINIMUM_WARP_DISTANCE {
            return refused("WARP_DISTANCE_TOO_CLOSE");
        }
        vec![Out::Position { session, command, position }]
    }

    fn position_of(&self, session: SessionId, target_id: i64) -> Vec<Out> {
        let command = Command::PositionOf { target_id }.code();
        let refused = |reason| vec![Out::Refused { session, command, reason }];
        let Some(position) = self.system.body(target_id).map(|body| body.position).or_else(|| {
            self.pilots.values()
                .find(|pilot| pilot.ship.spec.ship_id == target_id)
                .map(|pilot| pilot.ship.position)
        }) else {
            return refused("TARGET_NOT_FOUND");
        };
        vec![Out::Position { session, command, position }]
    }

    /// A movement command of a ship that is in the scene (settled first, at `now`).
    fn steer_pilot(&mut self, session: SessionId, command: Command, now: i64) -> Vec<Out> {
        let code = command.code();
        let target = match command {
            Command::FollowBall { target_id, .. }
            | Command::Orbit { target_id, .. }
            | Command::AlignTo { target_id } => self.target_of(target_id, session),
            _ => None,
        };
        let pilot = self.pilots.get_mut(&session).expect("the caller checked");
        if pilot.immobile_until > now {
            // a ship held by a doomsday's immobility window follows no orders
            return vec![Out::Refused { session, command: code, reason: "IMMOBILE" }];
        }
        if pilot.docking.is_some() {
            // a ship that is docking follows no orders
            return vec![Out::Refused { session, command: code, reason: "DOCKING" }];
        }
        if pilot.jump.is_some() {
            // nor does one that has gone into a gate
            return vec![Out::Refused { session, command: code, reason: "JUMPING" }];
        }
        if pilot.modules.is_mjd_spooling() {
            return vec![Out::Refused { session, command: code, reason: "MJD_SPOOLING" }];
        }
        if pilot.flight.is_some() {
            // a ship in warp, or lining up for it, follows none either
            return vec![Out::Refused { session, command: code, reason: "IN_WARP" }];
        }
        let ship_id = id(pilot.ship.spec.ship_id);
        match steer(&mut pilot.ship, target, command) {
            Steering::Refused(reason) => vec![Out::Refused { session, command: code, reason }],
            Steering::Nothing => vec![Out::Accepted { session, command: code, at: now }],
            Steering::Actions(actions) => {
                let stamp = self.next_stamp(now);
                let actions: Vec<Value> = actions
                    .into_iter()
                    .map(|(name, args)| {
                        let mut all = vec![ship_id.clone()];
                        all.extend(args);
                        payload::action(stamp, name, all)
                    })
                    .collect();
                let mut out = self.tell_all(session, actions);
                out.push(Out::Accepted { session, command: code, at: now });
                out
            }
        }
    }

    /// Applies a completed MJD translation along the heading at spool end; a space bump may change the result.
    pub(crate) fn perform_micro_jump(
        &mut self,
        session: SessionId,
        distance_meters: f64,
        now: i64,
    ) -> Vec<Out> {
        if !distance_meters.is_finite() || distance_meters <= 0.0 {
            return Vec::new();
        }
        self.settle_pilot_after(session, now, 0);
        {
            let Some(pilot) = self.pilots.get_mut(&session) else { return Vec::new() };
            let ship = &mut pilot.ship;
            let Some(direction) = motion::unit(ship.direction) else { return Vec::new() };
            ship.position = Vec3::new(
                ship.position.x + direction.x * distance_meters,
                ship.position.y + direction.y * distance_meters,
                ship.position.z + direction.z * distance_meters,
            );
        }
        self.resolve_collisions(now, Some(session), None);
        self.update_state(session, now)
    }

    fn dock(&mut self, session: SessionId, station_id: i64, now: i64) -> Vec<Out> {
        let code = Command::Dock { station_id }.code();
        let Some(station) = self.system.body(station_id).filter(|b| b.kind == BodyKind::Station).cloned()
        else {
            return vec![Out::Refused { session, command: code, reason: "STATION_NOT_FOUND" }];
        };
        let pilot = self.pilots.get(&session).expect("the caller checked");
        if let Some(pending) = pilot.docking.as_ref().filter(|p| p.station_id == station_id) {
            return vec![Out::Accepted { session, command: code, at: pending.accepted_at }];
        }
        // a doomsday's windows hold the ship: a held ship follows no orders (IMMOBILE), and a ship in its no-jump window does not dock either (the
        // reference's disallowDocking on the running module: the module runs for that window)
        if pilot.immobile_until > now {
            return vec![Out::Refused { session, command: code, reason: "IMMOBILE" }];
        }
        if pilot.no_jump_until > now {
            return vec![Out::Refused { session, command: code, reason: "DOCKING_BLOCKED" }];
        }
        if pilot.disrupted_until > now {
            return vec![Out::Refused { session, command: code, reason: "DOCKING_DISRUPTED" }];
        }
        if pilot.jump.is_some() {
            return vec![Out::Refused { session, command: code, reason: "JUMPING" }];
        }
        if pilot.flight.is_some() {
            return vec![Out::Refused { session, command: code, reason: "IN_WARP" }];
        }
        let distance = surface_distance(&pilot.ship, station.position, station.radius);
        if distance > DOCKING_RANGE {
            return self.approach(session, station_id, distance, now);
        }
        let mut out = self.accept_docking(session, station_id, now);
        out.push(Out::Accepted { session, command: code, at: now });
        out
    }

    /// The ship is in range and the docking begins: the client is told, and the ship stops and waits for the docking to take its time.
    fn accept_docking(&mut self, session: SessionId, station_id: i64, now: i64) -> Vec<Out> {
        let stamp = self.next_stamp(now);
        let pilot = self.pilots.get_mut(&session).expect("the caller checked");
        let ship = &mut pilot.ship;
        ship.speed_fraction = 0.0;
        ship.course = Course::Stop;
        pilot.docking = Some(PendingDock { station_id, accepted_at: now });
        let ship_id = id(ship.spec.ship_id);
        let mut out = vec![Self::notify_charid(session, "OnDockingAccepted", vec![id(station_id)])];
        out.extend(self.tell_all(
            session,
            vec![
                payload::action(stamp, "SetBallMassive", [ship_id.clone(), int(0)]),
                payload::action(stamp, "Stop", [ship_id]),
            ],
        ));
        out
    }

    /// A docking asked for from too far: the ship goes to the station on its own (following it to the docking range) and the host is told how far
    /// off it was, for the client's message. As in the reference the docking is not done on arrival: the pilot asks again.
    fn approach(&mut self, session: SessionId, station_id: i64, distance: f64, now: i64) -> Vec<Out> {
        let code = Command::Dock { station_id }.code();
        let follow = Command::FollowBall { target_id: station_id, range: DOCKING_RANGE };
        let mut out = self.steer_pilot(session, follow, now);
        out.retain(|o| !matches!(o, Out::Accepted { .. }));
        out.push(Out::Approaching { session, command: code, distance });
        out
    }

    /// `CmdStargateJump`: the ship stops where it is and the jump-out effect begins. The ship leaves for the other system a few seconds on
    /// (the later of four seconds from now and three from the effect's stamp, the reference's rule), which [`Scene::due_jumps`] finds.
    fn jump(&mut self, session: SessionId, gate_id: i64, destination_gate: i64, now: i64) -> Vec<Out> {
        let code = Command::Jump { gate_id, destination_gate }.code();
        let refused = |reason: &'static str| vec![Out::Refused { session, command: code, reason }];
        let system = Arc::clone(&self.system);
        let Some(pilot) = self.pilots.get(&session) else { return refused("NO_SHIP_IN_SPACE") };
        if pilot.immobile_until > now {
            return refused("IMMOBILE");
        }
        if pilot.no_jump_until > now {
            return refused("NO_JUMP_OR_CLOAK");
        }
        if pilot.disrupted_until > now {
            return refused("JUMP_DISRUPTED");
        }
        if pilot.jump.is_some() {
            return refused("STARGATE_JUMP_IN_PROGRESS");
        }
        if pilot.docking.is_some() {
            return refused("DOCKING");
        }
        if pilot.flight.is_some() {
            return refused("IN_WARP");
        }
        let Some((gate, Detail::Stargate { destination_system, destination_gate: leads_to, .. })) =
            system.body(gate_id).filter(|b| b.kind == BodyKind::Stargate).map(|b| (b, &b.detail))
        else {
            return refused("STARGATE_NOT_FOUND");
        };
        if destination_gate != 0 && destination_gate != *leads_to {
            return refused("STARGATE_DESTINATION_MISMATCH");
        }
        if gate::surface_distance(gate, pilot.ship.position, pilot.ship.spec.radius) > gate::JUMP_RANGE {
            return refused("TOO_FAR_FROM_STARGATE");
        }

        let stamp = self.next_stamp(now);
        let boundary = if stamp > stamp_at(now) {
            (i64::from(stamp) + UNIX_EPOCH_SECONDS) * TICKS_PER_SECOND
        } else {
            now
        };
        let leaves_at = (now + JUMP_DELAY).max(boundary + JUMP_EFFECT_DELAY);
        let pilot = self.pilots.get_mut(&session).expect("checked above");
        let ship = &mut pilot.ship;
        if !matches!(ship.course, Course::Stop) || ship.speed_fraction > 0.0 || speed_of(ship.velocity) > 0.5
        {
            ship.course = Course::Stop;
            ship.speed_fraction = 0.0;
            ship.velocity = Vec3::ZERO;
        }
        pilot.jump = Some(PendingJump {
            gate_id,
            destination_system: *destination_system,
            destination_gate: *leads_to,
            leaves_at,
        });
        let ship_id = id(ship.spec.ship_id);
        let actions = [
            payload::action(
                stamp,
                "OnSpecialFX",
                [
                    ship_id.clone(),
                    Value::None,
                    Value::None,
                    id(gate_id),
                    Value::None,
                    text("effects.JumpOut"),
                    int(0),
                    int(1),
                    int(0),
                    int(-1),
                    Value::None,
                    Value::None,
                    int(0),
                    tuple([int(i64::from(*destination_system)), Value::None]),
                ],
            ),
            payload::action(stamp, "Stop", [ship_id]),
        ];
        let mut out = self.tell_all(session, actions.into());
        out.push(Out::Jumping {
            session,
            command: code,
            destination_system: *destination_system,
            destination_gate: *leads_to,
        });
        out
    }

    /// The ships whose jump is due at `now`, and what the ones who watched them go are told. Each ship has left this scene; the caller puts it in
    /// the scene of the system it is going to ([`Scene::arrive`]), or back ([`Scene::restore`]) when that cannot be done.
    pub fn due_jumps(&mut self, now: i64) -> (Vec<Traveller>, Vec<Out>) {
        let due: Vec<SessionId> = self
            .pilots
            .iter()
            .filter(|(_, pilot)| pilot.jump.as_ref().is_some_and(|jump| now >= jump.leaves_at))
            .map(|(session, _)| *session)
            .collect();
        let mut travellers = Vec::new();
        let mut told = Vec::new();
        for session in due {
            self.settle_pilot(session, now);
            let Some(mut pilot) = self.pilots.remove(&session) else { continue };
            let Some(jump) = pilot.jump.take() else { continue };
            pilot.clear_overheat_for_jump();
            let held = std::mem::take(&mut pilot.targeting);
            told.extend(self.drop_targeting(pilot.ship.spec.ship_id, &held, now));
            told.extend(self.announce_departure(session, pilot.ship.spec.ship_id, now));
            travellers.push(Traveller {
                session,
                from_system: self.system.id,
                from_gate: jump.gate_id,
                to_system: jump.destination_system,
                to_gate: jump.destination_gate,
                pilot,
            });
        }
        (travellers, told)
    }

    /// A ship that has jumped appears in front of the gate it came out of, at rest, and is bound like any ship that arrives (the client binds
    /// the ballpark of the new system when its session has changed). Gives the ship back when this system has no such gate.
    pub fn arrive(&mut self, traveller: Traveller, now: i64) -> Result<Vec<Out>, Box<Traveller>> {
        let system = Arc::clone(&self.system);
        let Some(gate) = system.body(traveller.to_gate).filter(|b| b.kind == BodyKind::Stargate) else {
            return Err(Box::new(traveller));
        };
        if self.pilots.contains_key(&traveller.session) {
            return Err(Box::new(traveller));
        }
        let Traveller { session, mut pilot, .. } = traveller;
        let mut random = PythonRandom::new(seed_of(pilot.ship.spec.ship_id, gate.id, now));
        let (position, direction) = gate::arrival(gate, pilot.ship.spec.radius, &mut random);
        pilot.ship.position = position;
        pilot.ship.direction = direction;
        pilot.ship.velocity = Vec3::ZERO;
        pilot.ship.speed_fraction = 0.0;
        pilot.ship.course = Course::Stop;
        pilot.settled_at = now;
        pilot.docking = None;
        pilot.flight = None;
        pilot.jump = None;
        pilot.jumped_in = true;
        // the client binds the ballpark of this system afresh, and is then sent what is in view
        pilot.bound = false;
        pilot.sees.clear();
        pilot.entered = self.next_entry();
        self.pilots.insert(session, pilot);
        Ok(self.announce_arrival(session, now))
    }

    /// A ship whose jump could not be finished goes on in the scene it left (and those who saw it go are shown it again).
    pub fn restore(&mut self, traveller: Traveller, now: i64) -> Vec<Out> {
        let Traveller { session, mut pilot, .. } = traveller;
        pilot.entered = self.next_entry();
        self.pilots.insert(session, pilot);
        self.announce_arrival(session, now)
    }

    fn notify_charid(session: SessionId, name: &'static str, args: Vec<Value>) -> Out {
        Out::Notify { session, name, id_type: "charid", args }
    }

    /// The numbers of a ship in space changed (a skill finished, a module went on): the client is told the ones that differ.
    pub fn respec(&mut self, session: SessionId, spec: ShipSpec, now: i64) -> Vec<Out> {
        if !self.pilots.contains_key(&session) {
            return Vec::new();
        }
        self.settle_pilot(session, now);
        let stamp = self.next_stamp(now);
        let shift_attributes = self.shift_attributes(session);
        let Some(pilot) = self.pilots.get_mut(&session) else { return Vec::new() };
        let armor_before = pilot.ship.health.armor_resonance();
        let old = std::mem::replace(&mut pilot.ship.spec, spec);
        let new = &pilot.ship.spec;
        pilot.ship.health.respec(new.vitals, new.shield_recharge_ms, now);
        // a reactive armor hardener that runs is shown the armor's resonances as they now are (a pin, or a new base, moves them)
        let armor_changes = shift_attributes
            .map(|ids| crate::modules::armor_changes(&ids, &armor_before, &pilot.ship.health.armor_resonance()))
            .unwrap_or_default();
        let security_changed = new.security_status != old.security_status;
        let ship_id = id(new.ship_id);
        let (owner, ship) = (new.character_id, new.ship_id);
        let mut actions = Vec::new();
        if new.mass != old.mass {
            actions.push(payload::action(stamp, "SetBallMass", [ship_id.clone(), real(new.mass)]));
        }
        if new.agility != old.agility {
            actions.push(payload::action(stamp, "SetBallAgility", [ship_id.clone(), real(new.agility)]));
        }
        if new.max_velocity != old.max_velocity {
            actions.push(payload::action(stamp, "SetMaxSpeed", [ship_id, real(new.max_velocity)]));
        }
        let mut told: Vec<Out> = Self::health_told(session, owner, ship, now, &armor_changes).into_iter().collect();
        if !actions.is_empty() {
            told.extend(self.tell_all(session, actions));
        }
        // a security status that changed is in the slim item: the pilot and whoever sees the ship are shown it again (the reference does, when a crime costs a pilot some)
        if security_changed {
            let ship = &self.pilots[&session].ship;
            let change = payload::ship_slim_item_change(stamp, ship);
            told.extend(self.tell_all(session, vec![change]));
        }
        self.with_backlog(told)
    }

    pub fn set_heat_profile(
        &mut self,
        session: SessionId,
        mut profile: HeatProfile,
        now: i64,
    ) -> Result<(), HeatError> {
        let pilot = self.pilots.get_mut(&session).ok_or(HeatError::UnknownPilot)?;
        validate_heat_profile(&pilot.ship.spec, &profile)?;
        if pilot.heat.profile.is_none() {
            pilot.heat.levels = profile.initial_rack_heat;
        }

        let mut modules = BTreeMap::new();
        for module in std::mem::take(&mut profile.modules) {
            let module_id = module.module_id;
            let previous = pilot.heat.modules.get(&module_id);
            let damaged = module.damage_hp >= module.structure_hp;
            let available = module.overloadable && module.online && !damaged;
            let mut runtime = HeatModuleRuntime {
                profile: module,
                overloaded: previous.is_some_and(|old| old.overloaded) && available,
                pending_overload: previous.and_then(|old| old.pending_overload),
                cycle_start: previous.and_then(|old| old.cycle_start),
                next_cycle_at: previous.and_then(|old| old.next_cycle_at),
                completed_cycles: previous.map_or(0, |old| old.completed_cycles),
                was_running: previous.is_some_and(|old| old.was_running),
            };
            if !available {
                runtime.pending_overload = None;
            }
            modules.insert(module_id, runtime);
        }

        pilot.heat.profile = Some(profile);
        pilot.heat.modules = modules;
        pilot.heat.updated_at.get_or_insert(now);
        pilot.heat.force_notify |= pilot.heat.levels != pilot.heat.notified_levels;
        Ok(())
    }

    /// Request module overheat. A running module applies the change at its next cycle boundary;
    /// an idle module can be primed for its first cycle.
    pub fn set_module_overheat(
        &mut self,
        session: SessionId,
        module_id: i64,
        active: bool,
    ) -> Result<(), HeatError> {
        let pilot = self.pilots.get_mut(&session).ok_or(HeatError::UnknownPilot)?;
        let profile = pilot.heat.profile.as_ref().ok_or(HeatError::ProfileNotConfigured)?;
        let runtime = pilot.heat.modules.get_mut(&module_id).ok_or(HeatError::UnknownModule)?;
        if active && profile.thermodynamics_level == 0 {
            return Err(HeatError::ThermodynamicsRequired);
        }
        if active && !runtime.profile.overloadable {
            return Err(HeatError::ModuleNotOverloadable);
        }
        if active && !runtime.profile.online {
            return Err(HeatError::ModuleOffline);
        }
        if active && runtime.profile.damage_hp >= runtime.profile.structure_hp {
            return Err(HeatError::ModuleBurnedOut);
        }

        let cycling = pilot.modules.running_effects().any(|(effect, _)| effect.module_id == module_id);
        runtime.request_overheat(active, cycling);
        pilot.heat.force_notify = true;
        Ok(())
    }

    /// Request overheat for all overloadable modules in one rack.
    pub fn set_rack_overheat(
        &mut self,
        session: SessionId,
        rack: HeatRack,
        active: bool,
    ) -> Result<Vec<i64>, HeatError> {
        let pilot = self.pilots.get(&session).ok_or(HeatError::UnknownPilot)?;
        if pilot.heat.profile.is_none() {
            return Err(HeatError::ProfileNotConfigured);
        }
        let module_ids: Vec<i64> = pilot
            .heat
            .modules
            .iter()
            .filter_map(|(module_id, module)| {
                let in_rack = heat_rack_slot(module.profile.flag).is_some_and(|(which, _)| which == rack);
                let wanted = if active {
                    module.profile.overloadable
                        && module.profile.online
                        && module.profile.damage_hp < module.profile.structure_hp
                } else {
                    module.overloaded || module.pending_overload == Some(true)
                };
                (in_rack && wanted).then_some(*module_id)
            })
            .collect();

        for module_id in &module_ids {
            self.set_module_overheat(session, *module_id, active)?;
        }
        Ok(module_ids)
    }

    /// The current rack heat, in Dogma heat units (normally 0 through 100).
    pub fn rack_heat_level(&self, session: SessionId, rack: HeatRack) -> Option<f64> {
        let pilot = self.pilots.get(&session)?;
        pilot.heat.profile.as_ref()?;
        Some(pilot.heat.levels[rack.index()])
    }

    fn prepare_overheating(&mut self, now: i64) -> HeatSettlement {
        let sessions: Vec<SessionId> = self.pilots.keys().copied().collect();
        let mut settlement = HeatSettlement::default();
        for session in sessions {
            let Some(pilot) = self.pilots.get_mut(&session) else { continue };
            let mut one = prepare_pilot_heat(pilot, session, now);
            settlement.attempts.append(&mut one.attempts);
            settlement.notifications.append(&mut one.notifications);
        }
        settlement
    }

    /// Apply the completed-cycle rolls after module actions, notify Dogma, then stop burned-out modules.
    fn apply_overheating(&mut self, mut settlement: HeatSettlement, now: i64) -> Vec<Out> {
        let mut changes = BTreeMap::<SessionId, Vec<Value>>::new();
        for (session, args) in settlement.notifications.drain(..) {
            changes.entry(session).or_default().extend(args);
        }
        let mut burned = BTreeSet::<(SessionId, i64)>::new();

        for attempt in settlement.attempts {
            for (module_id, chance) in attempt.targets {
                let eligible = self.pilots.get(&attempt.session).is_some_and(|pilot| {
                    let source_alive = pilot
                        .heat
                        .modules
                        .get(&attempt.source_module_id)
                        .is_some_and(|source| source.profile.damage_hp < source.profile.structure_hp);
                    let target_alive = pilot.heat.modules.get(&module_id).is_some_and(|target| {
                        !target.profile.heat_immune && target.profile.damage_hp < target.profile.structure_hp
                    });
                    source_alive && target_alive && chance > 0.0
                });
                if !eligible {
                    continue;
                }
                if self.rng.random() >= chance {
                    continue;
                }

                let Some(pilot) = self.pilots.get_mut(&attempt.session) else { continue };
                let Some(target) = pilot.heat.modules.get_mut(&module_id) else { continue };
                let previous = target.profile.damage_hp;
                let (next, burned_out) =
                    heat_damage_after(previous, target.profile.structure_hp, attempt.damage_hp);
                if next <= previous {
                    continue;
                }
                target.profile.damage_hp = next;
                if burned_out {
                    target.profile.online = false;
                    target.overloaded = false;
                    target.pending_overload = None;
                    burned.insert((attempt.session, module_id));
                }
                let line = crate::modules::attribute_change(
                    pilot.ship.spec.character_id,
                    module_id,
                    HEAT_ATTRIBUTE_ITEM_DAMAGE,
                    attempt.at,
                    real(next),
                    real(previous),
                );
                changes.entry(attempt.session).or_default().push(line);
            }
        }

        let mut told = Vec::new();
        for (session, lines) in changes {
            if !lines.is_empty() {
                told.push(Out::Notify {
                    session,
                    name: "OnModuleAttributeChanges",
                    id_type: "clientID",
                    args: vec![Value::List(lines)],
                });
            }
        }

        for (session, module_id) in burned {
            told.extend(self.stop_module(session, module_id, now));
        }
        told
    }

    fn next_heat_cycle_boundary(&self, now: i64) -> Option<i64> {
        self.pilots
            .values()
            .flat_map(|pilot| {
                pilot.modules.running_effects().filter_map(move |(effect, cycle_start)| {
                    let module = pilot.heat.modules.get(&effect.module_id)?;
                    if !module.overloaded && module.pending_overload.is_none() {
                        return None;
                    }
                    heat_duration_ticks(effect.duration_ms)
                        .and_then(|duration| cycle_start.checked_add(duration))
                })
            })
            .filter(|at| *at <= now)
            .min()
    }

    pub fn poll(&mut self, now: i64) -> Vec<Out> {
        self.settle_all(now);
        let mut told = Vec::new();
        let due: Vec<(SessionId, i64)> = self
            .pilots
            .iter()
            .filter_map(|(session, pilot)| {
                pilot
                    .docking
                    .as_ref()
                    .filter(|d| now - d.accepted_at >= DOCKING_DELAY)
                    .map(|d| (*session, d.station_id))
            })
            .collect();
        for (session, station_id) in due {
            if let Some(pilot) = self.pilots.remove(&session) {
                told.push(Out::Docked { session, station_id, condition: Self::condition_of(&pilot, now) });
                told.extend(self.drop_targeting(pilot.ship.spec.ship_id, &pilot.targeting, now));
                told.extend(self.announce_departure(session, pilot.ship.spec.ship_id, now));
                told.extend(self.release_drones(session, None, now));
            }
        }
        told.extend(self.note_drone_threats(now));
        told.extend(self.tick_brains(now));
        told.extend(self.settle_targets(now));
        while let Some(at) = self.next_heat_cycle_boundary(now) {
            let heat = self.prepare_overheating(at);
            told.extend(self.settle_modules(at));
            told.extend(self.apply_overheating(heat, at));
        }
        let heat = self.prepare_overheating(now);
        told.extend(self.settle_modules(now));
        told.extend(self.apply_overheating(heat, now));
        told.extend(self.advance_missiles(now));
        told.extend(self.advance_drones(now));
        told.extend(self.reconcile(now));
        told.extend(self.reconcile_wrecks(now));
        told.extend(self.reconcile_asteroids(now));
        told.extend(self.expire_wrecks(now));
        self.with_backlog(told)
    }
}

/// The seed of the draws that pick a point for a ship at a gate: what and where and when, so the same calls at the same times give the same
/// points (the reference draws them from the clock).
fn seed_of(ship_id: i64, gate_id: i64, now: i64) -> u64 {
    (ship_id as u64).rotate_left(11) ^ (gate_id as u64).rotate_left(37) ^ (now as u64)
}

/// The effect the client plays on a ship that goes into warp.
fn warping_effect(stamp: u32, ship_id: &Value) -> Value {
    payload::action(
        stamp,
        "OnSpecialFX",
        [
            ship_id.clone(),
            Value::None,
            Value::None,
            Value::None,
            Value::None,
            Value::Str("effects.Warping".into()),
            int(0),
            int(1),
            int(0),
            int(-1),
            Value::None,
            Value::None,
            int(0),
            Value::None,
        ],
    )
}

fn with_id(ship_id: &Value, rest: [Value; 3]) -> Vec<Value> {
    let mut args = vec![ship_id.clone()];
    args.extend(rest);
    args
}

/// Where a ship comes out of a station and which way it faces: the station type's undock point that suits the ship, turned by the station's
/// orientation (the reference's own table); a type it does not know puts the ship a third of the station's radius from it, out along the
/// station's orientation or away from the star.
fn undock_place(station: &crate::world::Body, ship_group: i32) -> (Vec3, Vec3) {
    let rotation = crate::slim::station_rotation(station.id).unwrap_or([0.0; 3]);
    if let Some(point) = crate::locators::undock_point(station.type_id, ship_group, rotation) {
        let at = Vec3::new(
            station.position.x + point.offset.x,
            station.position.y + point.offset.y,
            station.position.z + point.offset.z,
        );
        return (at, motion::unit(point.direction).unwrap_or(point.direction));
    }
    let direction = undock_direction(station.id, station.position);
    let distance = UNDOCK_MIN_DISTANCE.max(station.radius * UNDOCK_RADIUS_SHARE);
    let at = Vec3::new(
        station.position.x + direction.x * distance,
        station.position.y + direction.y * distance,
        station.position.z + direction.z * distance,
    );
    (at, direction)
}

/// Which way a ship leaves a station with no known undock point: the station's own orientation when it has one, otherwise away from the star.
fn undock_direction(station_id: i64, position: Vec3) -> Vec3 {
    if let Some(angles) = crate::slim::station_rotation(station_id) {
        let (yaw, pitch) = (angles[0].to_radians(), angles[1].to_radians());
        return Vec3::new(yaw.sin() * pitch.cos(), -pitch.sin(), yaw.cos() * pitch.cos());
    }
    motion::unit(position).unwrap_or(Vec3::new(0.0, 0.0, 1.0))
}

fn surface_distance(ship: &Ship, station: Vec3, station_radius: f64) -> f64 {
    let (dx, dy, dz) =
        (ship.position.x - station.x, ship.position.y - station.y, ship.position.z - station.z);
    ((dx * dx + dy * dy + dz * dz).sqrt() - ship.spec.radius - station_radius).max(0.0)
}

fn finite_vector(vector: Vec3) -> bool {
    vector.x.is_finite() && vector.y.is_finite() && vector.z.is_finite()
}

fn collision_contact(from: Vec3, to: Vec3, from_radius: f64, to_radius: f64) -> Option<(Vec3, f64)> {
    if !finite_vector(from)
        || !finite_vector(to)
        || !from_radius.is_finite()
        || !to_radius.is_finite()
        || from_radius <= 0.0
        || to_radius <= 0.0
    {
        return None;
    }
    let radii = from_radius + to_radius;
    if !radii.is_finite() {
        return None;
    }
    let delta = vector_sub(to, from);
    if !finite_vector(delta) {
        return None;
    }
    let distance = delta.x.hypot(delta.y).hypot(delta.z);
    if !distance.is_finite() || distance >= radii {
        return None;
    }
    let normal = if distance == 0.0 {
        // Identical centers use a fixed axis; BTreeMap order makes ship pairs deterministic.
        Vec3::new(1.0, 0.0, 0.0)
    } else {
        Vec3::new(delta.x / distance, delta.y / distance, delta.z / distance)
    };
    Some((normal, radii - distance))
}

fn collision_mass_shares(a_mass: f64, b_mass: f64) -> (f64, f64) {
    let a_mass = if a_mass.is_finite() && a_mass > 0.0 { a_mass } else { 1.0 };
    let b_mass = if b_mass.is_finite() && b_mass > 0.0 { b_mass } else { 1.0 };
    let scale = a_mass.max(b_mass);
    let a = a_mass / scale;
    let b = b_mass / scale;
    let total = a + b;
    (b / total, a / total)
}

fn bump_ships(a: &mut Ship, b: &mut Ship) -> bool {
    let Some((normal, penetration)) = collision_contact(a.position, b.position, a.spec.radius, b.spec.radius)
    else {
        return false;
    };
    let (a_share, b_share) = collision_mass_shares(a.spec.mass, b.spec.mass);
    let a_position = Vec3::new(
        a.position.x - normal.x * penetration * a_share,
        a.position.y - normal.y * penetration * a_share,
        a.position.z - normal.z * penetration * a_share,
    );
    let b_position = Vec3::new(
        b.position.x + normal.x * penetration * b_share,
        b.position.y + normal.y * penetration * b_share,
        b.position.z + normal.z * penetration * b_share,
    );
    if !finite_vector(a_position) || !finite_vector(b_position) {
        return false;
    }
    a.position = a_position;
    b.position = b_position;
    let (a_velocity, b_velocity) = bumped_ship_pair_velocity(a, b, normal);
    a.velocity = a_velocity;
    b.velocity = b_velocity;
    true
}

fn bump_ship_body(ship: &mut Ship, body: &crate::world::Body) -> bool {
    let Some((normal, _)) = collision_contact(body.position, ship.position, body.radius, ship.spec.radius)
    else {
        return false;
    };
    let position = Vec3::new(
        body.position.x + normal.x * (body.radius + ship.spec.radius),
        body.position.y + normal.y * (body.radius + ship.spec.radius),
        body.position.z + normal.z * (body.radius + ship.spec.radius),
    );
    if !finite_vector(position) {
        return false;
    }
    ship.position = position;
    ship.velocity = bumped_ship_body_velocity(ship.velocity, normal);
    true
}

fn collision_dot(a: Vec3, b: Vec3) -> f64 {
    a.x * b.x + a.y * b.y + a.z * b.z
}

fn collision_lerp(from: Vec3, to: Vec3, fraction: f64) -> Vec3 {
    let delta = vector_sub(to, from);
    Vec3::new(from.x + delta.x * fraction, from.y + delta.y * fraction, from.z + delta.z * fraction)
}

fn swept_collision_contact(
    from_a: Vec3,
    to_a: Vec3,
    from_b: Vec3,
    to_b: Vec3,
    radius_a: f64,
    radius_b: f64,
) -> Option<(Vec3, f64)> {
    let radii = radius_a + radius_b;
    if !finite_vector(from_a)
        || !finite_vector(to_a)
        || !finite_vector(from_b)
        || !finite_vector(to_b)
        || !radius_a.is_finite()
        || !radius_b.is_finite()
        || radius_a <= 0.0
        || radius_b <= 0.0
        || !radii.is_finite()
    {
        return None;
    }
    let start = vector_sub(from_b, from_a);
    let end = vector_sub(to_b, to_a);
    let travel = vector_sub(end, start);
    if !finite_vector(start) || !finite_vector(end) || !finite_vector(travel) {
        return None;
    }
    let distance_squared = collision_dot(start, start);
    let travel_squared = collision_dot(travel, travel);
    let approach = collision_dot(start, travel);
    let c = distance_squared - radii * radii;
    if !distance_squared.is_finite()
        || !travel_squared.is_finite()
        || !approach.is_finite()
        || !c.is_finite()
        || travel_squared <= 0.0
        || c < 0.0
        || approach >= 0.0
    {
        return None;
    }
    let fraction = if c == 0.0 {
        0.0
    } else {
        let b = 2.0 * approach;
        let discriminant = b * b - 4.0 * travel_squared * c;
        if !discriminant.is_finite() || discriminant < 0.0 {
            return None;
        }
        let denominator = -b + discriminant.sqrt();
        if denominator <= 0.0 {
            return None;
        }
        2.0 * c / denominator
    };
    if !fraction.is_finite() || !(0.0..=1.0).contains(&fraction) {
        return None;
    }
    let at_contact = Vec3::new(
        start.x + travel.x * fraction,
        start.y + travel.y * fraction,
        start.z + travel.z * fraction,
    );
    let distance = at_contact.x.hypot(at_contact.y).hypot(at_contact.z);
    if !distance.is_finite() || distance == 0.0 {
        return None;
    }
    Some((Vec3::new(at_contact.x / distance, at_contact.y / distance, at_contact.z / distance), fraction))
}

fn bumped_ship_pair_velocity(a: &Ship, b: &Ship, normal: Vec3) -> (Vec3, Vec3) {
    let a_velocity = if finite_vector(a.velocity) { a.velocity } else { Vec3::ZERO };
    let b_velocity = if finite_vector(b.velocity) { b.velocity } else { Vec3::ZERO };
    let normal_speed = collision_dot(vector_sub(b_velocity, a_velocity), normal);
    if !normal_speed.is_finite() || normal_speed >= 0.0 {
        return (a_velocity, b_velocity);
    }
    let (a_share, b_share) = collision_mass_shares(a.spec.mass, b.spec.mass);
    let impulse = -normal_speed * (1.0 + BUMP_RESTITUTION);
    let next_a = Vec3::new(
        a_velocity.x - normal.x * impulse * a_share,
        a_velocity.y - normal.y * impulse * a_share,
        a_velocity.z - normal.z * impulse * a_share,
    );
    let next_b = Vec3::new(
        b_velocity.x + normal.x * impulse * b_share,
        b_velocity.y + normal.y * impulse * b_share,
        b_velocity.z + normal.z * impulse * b_share,
    );
    if finite_vector(next_a) && finite_vector(next_b) { (next_a, next_b) } else { (a_velocity, b_velocity) }
}

fn bumped_ship_body_velocity(velocity: Vec3, normal: Vec3) -> Vec3 {
    let velocity = if finite_vector(velocity) { velocity } else { Vec3::ZERO };
    let into_surface = collision_dot(velocity, normal);
    if !into_surface.is_finite() || into_surface >= 0.0 {
        return velocity;
    }
    let impulse = (1.0 + BUMP_RESTITUTION) * into_surface;
    let bounced = Vec3::new(
        velocity.x - normal.x * impulse,
        velocity.y - normal.y * impulse,
        velocity.z - normal.z * impulse,
    );
    if finite_vector(bounced) { bounced } else { Vec3::ZERO }
}

fn bump_ships_swept(a: &mut Ship, b: &mut Ship, a_start: Vec3, b_start: Vec3, seconds: f64) -> bool {
    let a_end = a.position;
    let b_end = b.position;
    let Some((normal, fraction)) =
        swept_collision_contact(a_start, a_end, b_start, b_end, a.spec.radius, b.spec.radius)
    else {
        return false;
    };
    let mut a_contact = collision_lerp(a_start, a_end, fraction);
    let mut b_contact = collision_lerp(b_start, b_end, fraction);
    let distance = speed_of(vector_sub(b_contact, a_contact));
    let penetration = (a.spec.radius + b.spec.radius - distance).max(0.0);
    if penetration > 0.0 {
        let (a_share, b_share) = collision_mass_shares(a.spec.mass, b.spec.mass);
        a_contact = Vec3::new(
            a_contact.x - normal.x * penetration * a_share,
            a_contact.y - normal.y * penetration * a_share,
            a_contact.z - normal.z * penetration * a_share,
        );
        b_contact = Vec3::new(
            b_contact.x + normal.x * penetration * b_share,
            b_contact.y + normal.y * penetration * b_share,
            b_contact.z + normal.z * penetration * b_share,
        );
    }
    let (a_velocity, b_velocity) = bumped_ship_pair_velocity(a, b, normal);
    let remaining = seconds * (1.0 - fraction);
    let a_position = Vec3::new(
        a_contact.x + a_velocity.x * remaining,
        a_contact.y + a_velocity.y * remaining,
        a_contact.z + a_velocity.z * remaining,
    );
    let b_position = Vec3::new(
        b_contact.x + b_velocity.x * remaining,
        b_contact.y + b_velocity.y * remaining,
        b_contact.z + b_velocity.z * remaining,
    );
    if !seconds.is_finite() || seconds <= 0.0 || !finite_vector(a_position) || !finite_vector(b_position) {
        return false;
    }
    a.position = a_position;
    b.position = b_position;
    a.velocity = a_velocity;
    b.velocity = b_velocity;
    true
}

fn bump_ship_body_swept(ship: &mut Ship, body: &crate::world::Body, start: Vec3, seconds: f64) -> bool {
    let end = ship.position;
    let Some((normal, fraction)) =
        swept_collision_contact(body.position, body.position, start, end, body.radius, ship.spec.radius)
    else {
        return false;
    };
    let contact_distance = body.radius + ship.spec.radius;
    let contact = Vec3::new(
        body.position.x + normal.x * contact_distance,
        body.position.y + normal.y * contact_distance,
        body.position.z + normal.z * contact_distance,
    );
    let velocity = bumped_ship_body_velocity(ship.velocity, normal);
    let remaining = seconds * (1.0 - fraction);
    let position = Vec3::new(
        contact.x + velocity.x * remaining,
        contact.y + velocity.y * remaining,
        contact.z + velocity.z * remaining,
    );
    if !seconds.is_finite() || seconds <= 0.0 || !finite_vector(position) {
        return false;
    }
    ship.position = position;
    ship.velocity = velocity;
    true
}

// ponytail: one swept impact per ship per settlement; iterate events further if multi-contact ticks appear in play.
fn resolve_swept_collisions(
    ships: &mut [(SessionId, i64, Ship)],
    bodies: &[crate::world::Body],
    starts: &BTreeMap<SessionId, (i64, Vec3, bool)>,
    now: i64,
    active: &mut BTreeSet<SessionId>,
    bumped: &mut BTreeSet<SessionId>,
) {
    let mut swept = BTreeSet::new();
    for left_index in 0..ships.len() {
        for right_index in left_index + 1..ships.len() {
            let left_session = ships[left_index].0;
            let right_session = ships[right_index].0;
            if (!active.contains(&left_session) && !active.contains(&right_session))
                || swept.contains(&left_session)
                || swept.contains(&right_session)
            {
                continue;
            }
            let (Some((left_at, left_start, left_warping)), Some((right_at, right_start, right_warping))) =
                (starts.get(&left_session).copied(), starts.get(&right_session).copied())
            else {
                continue;
            };
            if left_at >= now || right_at >= now || left_warping || right_warping {
                continue;
            }
            let common_at = left_at.max(right_at);
            if common_at >= now {
                continue;
            }
            let left_fraction = (common_at - left_at) as f64 / (now - left_at) as f64;
            let right_fraction = (common_at - right_at) as f64 / (now - right_at) as f64;
            let left_from = collision_lerp(left_start, ships[left_index].2.position, left_fraction);
            let right_from = collision_lerp(right_start, ships[right_index].2.position, right_fraction);
            let seconds = (now - common_at) as f64 / TICKS_PER_SECOND as f64;
            let (left, right) = ships.split_at_mut(right_index);
            if bump_ships_swept(&mut left[left_index].2, &mut right[0].2, left_from, right_from, seconds) {
                swept.insert(left_session);
                swept.insert(right_session);
                active.insert(left_session);
                active.insert(right_session);
                bumped.insert(left_session);
                bumped.insert(right_session);
            }
        }
    }
    for (session, protected_until, ship) in ships {
        if !active.contains(session) || swept.contains(session) {
            continue;
        }
        let Some((start_at, start, warping)) = starts.get(session).copied() else { continue };
        if start_at >= now || warping {
            continue;
        }
        let seconds = (now - start_at) as f64 / TICKS_PER_SECOND as f64;
        for body in bodies {
            if body.kind == BodyKind::Belt || (body.kind == BodyKind::Station && *protected_until > now) {
                continue;
            }
            if bump_ship_body_swept(ship, body, start, seconds) {
                swept.insert(*session);
                bumped.insert(*session);
                break;
            }
        }
    }
}

fn settle_bumped_ship(ship: &mut Ship) {
    if !finite_vector(ship.velocity) {
        ship.velocity = Vec3::ZERO;
    }
    let speed = ship.velocity.x.hypot(ship.velocity.y).hypot(ship.velocity.z);
    if !speed.is_finite() {
        ship.velocity = Vec3::ZERO;
    }
    let speed = ship.velocity.x.hypot(ship.velocity.y).hypot(ship.velocity.z);
    if speed == 0.0 {
        ship.speed_fraction = 0.0;
        ship.course = Course::Stop;
        return;
    }
    ship.direction = Vec3::new(ship.velocity.x / speed, ship.velocity.y / speed, ship.velocity.z / speed);
    ship.speed_fraction = if ship.spec.max_velocity.is_finite() && ship.spec.max_velocity > 0.0 {
        (speed / ship.spec.max_velocity).clamp(0.0, 1.0)
    } else {
        0.0
    };
    ship.course = Course::Goto;
}

/// Brings a ship's position and velocity to `now`: it eases towards the speed it is asked for, the way the client integrates it, and a ship
/// that follows or orbits a body is steered by where the body is.
fn settle(pilot: &mut Pilot, system: &SolarSystem, ship_target: Option<Target>, now: i64) -> Vec<Milestone> {
    let from = pilot.settled_at;
    if now <= from {
        return Vec::new();
    }
    pilot.settled_at = now;
    let seconds = (now - from) as f64 / TICKS_PER_SECOND as f64;
    if let Some(flight) = pilot.flight.as_mut() {
        let advance = flight.advance(&mut pilot.ship, from, now);
        if let Some(rested) = advance.finished_at {
            // the warp is over: what is left of the time is a stopped ship's
            pilot.flight = None;
            pilot.ship.course = Course::Stop;
            pilot.ship.speed_fraction = 0.0;
            motion::advance(&mut pilot.ship, (now - rested) as f64 / TICKS_PER_SECOND as f64, None);
        }
        return advance.milestones;
    }
    let target = match pilot.ship.course {
        Course::Follow { target, .. } | Course::Orbit { target, .. } => ship_target
            .or_else(|| system.body(target).map(|body| Target::at_rest(body.position, body.radius))),
        Course::Goto | Course::GotoPoint { .. } | Course::Stop | Course::Warp { .. } => None,
    };
    motion::advance(&mut pilot.ship, seconds, target);
    Vec::new()
}

/// What a movement command comes to once the ship has looked at it.
enum Steering {
    /// Accepted, and the ship already does it: the client has nothing to be told.
    Nothing,
    /// Accepted: the actions of one update, each with what follows the ship's id.
    Actions(Vec<(&'static str, Vec<Value>)>),
    Refused(&'static str),
}

/// The heading of a ship as the client's action names it.
fn heading_name(ship: &Ship) -> &'static str {
    match ship.course {
        Course::Goto => "GotoDirection",
        Course::GotoPoint { .. } => "GotoPoint",
        Course::Stop => "Stop",
        Course::Follow { .. } => "FollowBall",
        Course::Orbit { .. } => "Orbit",
        Course::Warp { .. } => "WarpTo",
    }
}

/// The arguments of that action, the ship's id first.
fn heading_args(ship: &Ship) -> Vec<Value> {
    let ship_id = id(ship.spec.ship_id);
    match ship.course {
        Course::Goto => {
            with_id(&ship_id, vector(ship.direction.x, ship.direction.y, ship.direction.z)).to_vec()
        }
        Course::Stop => vec![ship_id],
        Course::GotoPoint { point } => vec![ship_id, real(point.x), real(point.y), real(point.z)],
        Course::Follow { target, range } => vec![ship_id, id(target), int(whole(range))],
        Course::Orbit { target, distance } => vec![ship_id, id(target), int(whole(distance))],
        Course::Warp { raw_destination: r, stop_distance, factor, .. } => {
            vec![ship_id, real(r.x), real(r.y), real(r.z), real(stop_distance), int(factor)]
        }
    }
}

/// A distance as the client's actions carry it: whole metres, cut off.
fn whole(metres: f64) -> i64 {
    metres.trunc().clamp(i32::MIN as f64, i32::MAX as f64) as i64
}

fn speed_of(velocity: Vec3) -> f64 {
    (velocity.x * velocity.x + velocity.y * velocity.y + velocity.z * velocity.z).sqrt()
}

fn vector_sub(a: Vec3, b: Vec3) -> Vec3 {
    Vec3::new(a.x - b.x, a.y - b.y, a.z - b.z)
}

/// The speed a ship that is not moving at one has to be asked for to move: what it had, or `otherwise`.
fn resumed_speed(ship: &Ship, otherwise: f64) -> (f64, bool) {
    let before = ship.speed_fraction;
    let after = if before > 0.0 { before } else { otherwise };
    (after, (after - before).abs() > 1e-6)
}

fn speed_action(ship: &Ship, changed: bool, actions: &mut Vec<(&'static str, Vec<Value>)>) {
    if changed {
        actions.push(("SetSpeedFraction", vec![real(ship.speed_fraction)]));
    }
}

/// Puts a movement command into effect on a ship whose motion is settled, and says what the client is to be told.
/// `target` is what the command's target is (a body or another ship), when the scene has it.
fn steer(ship: &mut Ship, target: Option<Target>, command: Command) -> Steering {
    match command {
        Command::SetSpeedFraction(fraction) => {
            let fraction = if fraction.is_finite() { fraction.clamp(0.0, 1.0) } else { 0.0 };
            if fraction <= 0.0 {
                return stop(ship);
            }
            ship.speed_fraction = fraction;
            if matches!(ship.course, Course::Stop) {
                ship.course = Course::Goto;
            }
            Steering::Actions(vec![("SetSpeedFraction", vec![real(fraction)])])
        }
        Command::GotoDirection(direction) => {
            let Some(unit) = motion::unit(direction) else { return Steering::Refused("NO_DIRECTION") };
            let stopped = ship.speed_fraction <= 0.0 || matches!(ship.course, Course::Stop);
            let same = matches!(ship.course, Course::Goto)
                && ship.direction.x * unit.x + ship.direction.y * unit.y + ship.direction.z * unit.z
                    >= 0.999_999;
            if same && !stopped {
                return Steering::Nothing;
            }
            ship.direction = unit;
            ship.course = Course::Goto;
            if stopped {
                ship.speed_fraction = 1.0;
            }
            let mut actions = vec![("GotoDirection", vector(unit.x, unit.y, unit.z).to_vec())];
            speed_action(ship, stopped, &mut actions);
            Steering::Actions(actions)
        }
        Command::Stop => stop(ship),
        Command::Dock { .. }
        | Command::WarpTo { .. }
        | Command::WarpToPoint { .. }
        | Command::ResolveWarpTarget { .. }
        | Command::PositionOf { .. }
        | Command::Jump { .. } => Steering::Refused("NOT_A_MOVEMENT"),
        Command::FollowBall { target_id, range } => {
            if !range.is_finite() {
                return Steering::Refused("BAD_RANGE");
            }
            if target.is_none() {
                return Steering::Refused("UNKNOWN_TARGET");
            }
            let range = range.max(0.0);
            if matches!(ship.course, Course::Follow { target, range: had } if target == target_id && (had - range).abs() < 1.0)
            {
                return Steering::Nothing;
            }
            let (speed, changed) = resumed_speed(ship, 1.0);
            ship.course = Course::Follow { target: target_id, range };
            ship.speed_fraction = speed;
            let mut actions = vec![("FollowBall", vec![id(target_id), int(whole(range))])];
            speed_action(ship, changed, &mut actions);
            Steering::Actions(actions)
        }
        Command::Orbit { target_id, distance } => {
            if !distance.is_finite() {
                return Steering::Refused("BAD_RANGE");
            }
            if target.is_none() {
                return Steering::Refused("UNKNOWN_TARGET");
            }
            let distance = distance.max(0.0);
            if matches!(ship.course, Course::Orbit { target, distance: had } if target == target_id && (had - distance).abs() < 1.0)
                && ship.speed_fraction > 0.0
            {
                return Steering::Nothing;
            }
            let (speed, changed) = resumed_speed(ship, 1.0);
            ship.course = Course::Orbit { target: target_id, distance };
            ship.speed_fraction = speed;
            let mut actions = vec![("Orbit", vec![id(target_id), int(whole(distance))])];
            speed_action(ship, changed, &mut actions);
            Steering::Actions(actions)
        }
        Command::AlignTo { .. } => {
            let Some(target) = target else { return Steering::Refused("UNKNOWN_TARGET") };
            let towards = Vec3::new(
                target.position.x - ship.position.x,
                target.position.y - ship.position.y,
                target.position.z - ship.position.z,
            );
            let direction = motion::unit(towards).unwrap_or(ship.direction);
            let (speed, changed) = resumed_speed(ship, 0.75);
            ship.direction = direction;
            ship.course = Course::Goto;
            ship.speed_fraction = speed;
            let mut actions = vec![("GotoDirection", vector(direction.x, direction.y, direction.z).to_vec())];
            speed_action(ship, changed, &mut actions);
            Steering::Actions(actions)
        }
    }
}

/// A ship told to stop: it says so unless it stands still already.
fn stop(ship: &mut Ship) -> Steering {
    let standing =
        matches!(ship.course, Course::Stop) && ship.speed_fraction <= 0.0 && speed_of(ship.velocity) < 0.1;
    ship.course = Course::Stop;
    ship.speed_fraction = 0.0;
    if standing { Steering::Nothing } else { Steering::Actions(vec![("Stop", Vec::new())]) }
}

/// The module damage Dogma attribute expected by the client.
const HEAT_ATTRIBUTE_ITEM_DAMAGE: i32 = 3;
const HEAT_NOTIFY_INTERVAL: i64 = 5 * TICKS_PER_SECOND;
const HEAT_NOTIFY_MIN_DELTA: f64 = 1.0;
const HEAT_NOTIFY_INTERVAL_MIN_DELTA: f64 = 0.2;
const HEAT_MAX_ADVANCE_SECONDS: f64 = 60.0;
const HEAT_ZERO_EPSILON: f64 = 0.05;

#[derive(Debug, Default, Clone)]
struct HeatState {
    profile: Option<HeatProfile>,
    modules: BTreeMap<i64, HeatModuleRuntime>,
    levels: [f64; 3],
    updated_at: Option<i64>,
    notified_levels: [f64; 3],
    last_notify_at: Option<i64>,
    force_notify: bool,
}

#[derive(Debug, Clone)]
struct HeatModuleRuntime {
    profile: HeatModuleProfile,
    overloaded: bool,
    pending_overload: Option<bool>,
    was_running: bool,
    cycle_start: Option<i64>,
    next_cycle_at: Option<i64>,
    completed_cycles: i32,
}

impl HeatModuleRuntime {
    fn request_overheat(&mut self, active: bool, cycling: bool) {
        if cycling {
            self.pending_overload = (self.overloaded != active).then_some(active);
        } else {
            self.overloaded = active;
            self.pending_overload = None;
        }
    }

    fn settle_cycle_boundary(&mut self) {
        if let Some(active) = self.pending_overload.take() {
            self.overloaded = active;
        }
    }
}

#[derive(Debug, Default)]
struct HeatSettlement {
    attempts: Vec<HeatDamageAttempt>,
    notifications: Vec<(SessionId, Vec<Value>)>,
}

#[derive(Debug)]
struct HeatDamageAttempt {
    session: SessionId,
    source_module_id: i64,
    at: i64,
    damage_hp: f64,
    targets: Vec<(i64, f64)>,
}

#[derive(Debug, Clone, Copy)]
struct ActiveHeatCycle {
    cycle_start: i64,
    duration_ticks: i64,
    repeat: i32,
}

fn heat_rack_slot(flag: i32) -> Option<(HeatRack, u8)> {
    match flag {
        27..=34 => Some((HeatRack::High, (flag - 27) as u8)),
        19..=26 => Some((HeatRack::Medium, (flag - 19) as u8)),
        11..=18 => Some((HeatRack::Low, (flag - 11) as u8)),
        _ => None,
    }
}

fn validate_heat_profile(spec: &ShipSpec, profile: &HeatProfile) -> Result<(), HeatError> {
    if !profile.generation_multiplier.is_finite() || profile.generation_multiplier < 0.0 {
        return Err(HeatError::InvalidProfile("generation multiplier"));
    }
    if !profile.damage_multiplier.is_finite() || profile.damage_multiplier < 0.0 {
        return Err(HeatError::InvalidProfile("damage multiplier"));
    }
    if profile.thermodynamics_level > 5 {
        return Err(HeatError::InvalidProfile("Thermodynamics level"));
    }
    for rack in HeatRack::ALL {
        let index = rack.index();
        if !profile.capacity[index].is_finite() || profile.capacity[index] <= 0.0 {
            return Err(HeatError::InvalidProfile("rack capacity"));
        }
        if !profile.initial_rack_heat[index].is_finite()
            || !(0.0..=profile.capacity[index]).contains(&profile.initial_rack_heat[index])
        {
            return Err(HeatError::InvalidProfile("initial rack heat"));
        }
        if !profile.dissipation_rate[index].is_finite() || profile.dissipation_rate[index] < 0.0 {
            return Err(HeatError::InvalidProfile("rack dissipation rate"));
        }
        if !profile.attenuation[index].is_finite() || !(0.0..=1.0).contains(&profile.attenuation[index]) {
            return Err(HeatError::InvalidProfile("rack attenuation"));
        }
    }

    let mut module_ids = BTreeSet::new();
    let mut slot_flags = BTreeSet::new();
    for module in &profile.modules {
        if module.module_id <= 0 || !module_ids.insert(module.module_id) {
            return Err(HeatError::InvalidProfile("duplicate or invalid module id"));
        }
        if heat_rack_slot(module.flag).is_none() || !slot_flags.insert(module.flag) {
            return Err(HeatError::InvalidProfile("duplicate or invalid rack slot"));
        }
        if !spec.modules.iter().any(|fitted| fitted.item_id == module.module_id && fitted.flag == module.flag)
        {
            return Err(HeatError::InvalidProfile("module does not match ship fitting"));
        }
        if !module.structure_hp.is_finite() || module.structure_hp <= 0.0 {
            return Err(HeatError::InvalidProfile("module structure HP"));
        }
        if !module.damage_hp.is_finite() || !(0.0..=module.structure_hp).contains(&module.damage_hp) {
            return Err(HeatError::InvalidProfile("module damage"));
        }
        if !module.heat_damage_hp.is_finite() || module.heat_damage_hp < 0.0 {
            return Err(HeatError::InvalidProfile("module heat damage"));
        }
        if !module.heat_absorption_rate.is_finite() || module.heat_absorption_rate < 0.0 {
            return Err(HeatError::InvalidProfile("module heat absorption"));
        }
    }

    for fitted in &spec.modules {
        if heat_rack_slot(fitted.flag).is_some() && !module_ids.contains(&fitted.item_id) {
            return Err(HeatError::InvalidProfile("missing fitted module profile"));
        }
    }
    Ok(())
}

fn heat_duration_ticks(duration_ms: f64) -> Option<i64> {
    if !duration_ms.is_finite() || duration_ms <= 0.0 {
        return None;
    }
    let ticks = duration_ms * (TICKS_PER_SECOND as f64 / 1_000.0);
    (ticks <= i64::MAX as f64).then_some((ticks as i64).max(1))
}

fn heat_damage_after(previous: f64, structure_hp: f64, damage_hp: f64) -> (f64, bool) {
    if !previous.is_finite() || !structure_hp.is_finite() || structure_hp <= 0.0 || !damage_hp.is_finite() {
        return (previous.max(0.0), false);
    }
    let previous = previous.clamp(0.0, structure_hp);
    let next = (previous + damage_hp.max(0.0)).clamp(0.0, structure_hp);
    (next, next >= structure_hp - 1e-9)
}

fn heat_damage_chance(heat: f64, capacity: f64, attenuation: f64, distance: u8) -> f64 {
    if !heat.is_finite() || !capacity.is_finite() || capacity <= 0.0 {
        return 0.0;
    }
    let fraction = (heat / capacity).clamp(0.0, 1.0);
    let falloff = attenuation.clamp(0.0, 1.0).powi(i32::from(distance));
    (fraction * falloff).clamp(0.0, 1.0)
}

fn rack_heat_after(
    previous: f64,
    capacity: f64,
    incoming_rate: f64,
    generation_multiplier: f64,
    dissipation_rate: f64,
    delta_seconds: f64,
) -> f64 {
    if !capacity.is_finite() || capacity <= 0.0 || !delta_seconds.is_finite() || delta_seconds <= 0.0 {
        return previous.max(0.0);
    }
    let previous = previous.clamp(0.0, capacity);
    let next = if incoming_rate > 0.0 && generation_multiplier > 0.0 {
        capacity - (capacity - previous) * (-(incoming_rate * generation_multiplier * delta_seconds)).exp()
    } else if dissipation_rate > 0.0 {
        previous * (-(dissipation_rate * delta_seconds)).exp()
    } else {
        previous
    };
    next.clamp(0.0, capacity)
}

fn prepare_pilot_heat(pilot: &mut Pilot, session: SessionId, now: i64) -> HeatSettlement {
    let mut settlement = HeatSettlement::default();
    if pilot.heat.profile.is_none() {
        return settlement;
    }

    let active: BTreeMap<i64, ActiveHeatCycle> = pilot
        .modules
        .running_effects()
        .filter_map(|(effect, cycle_start)| {
            Some((
                effect.module_id,
                ActiveHeatCycle {
                    cycle_start,
                    duration_ticks: heat_duration_ticks(effect.duration_ms)?,
                    repeat: effect.repeat,
                },
            ))
        })
        .collect();
    let active_ids: BTreeSet<i64> = active.keys().copied().collect();

    for (module_id, runtime) in &mut pilot.heat.modules {
        if !active_ids.contains(module_id) {
            runtime.was_running = false;
            runtime.cycle_start = None;
            runtime.next_cycle_at = None;
            runtime.completed_cycles = 0;
            runtime.settle_cycle_boundary();
        }
    }

    for (module_id, cycle) in &active {
        let Some(runtime) = pilot.heat.modules.get_mut(module_id) else { continue };
        if !runtime.profile.online || runtime.profile.damage_hp >= runtime.profile.structure_hp {
            runtime.overloaded = false;
            runtime.pending_overload = None;
            continue;
        }
        let start = cycle.cycle_start;
        if !runtime.was_running {
            runtime.was_running = true;
            runtime.cycle_start = Some(start);
            runtime.next_cycle_at = start.checked_add(cycle.duration_ticks);
            runtime.completed_cycles = 0;
        } else if runtime.cycle_start != Some(start) {
            runtime.cycle_start = Some(start);
            runtime.next_cycle_at = start.checked_add(cycle.duration_ticks);
        }
    }

    let mut cycling = active_ids;
    loop {
        let next = cycling
            .iter()
            .filter_map(|module_id| {
                pilot
                    .heat
                    .modules
                    .get(module_id)
                    .and_then(|runtime| runtime.next_cycle_at.map(|at| (at, *module_id)))
            })
            .filter(|(at, _)| *at <= now)
            .min();
        let Some((at, source_module_id)) = next else { break };

        advance_rack_heat(&mut pilot.heat, &cycling, at);
        let source_snapshot = pilot.heat.modules.get(&source_module_id).cloned();
        if let Some(source) = source_snapshot
            && source.overloaded
            && source.profile.overloadable
            && source.profile.heat_damage_hp > 0.0
            && source.profile.damage_hp < source.profile.structure_hp
            && let Some((rack, source_slot)) = heat_rack_slot(source.profile.flag)
        {
            let profile = pilot.heat.profile.as_ref().expect("heat profile checked above");
            let skill_multiplier = (1.0 - 0.05 * f64::from(profile.thermodynamics_level)).max(0.0);
            let damage_hp = source.profile.heat_damage_hp * skill_multiplier * profile.damage_multiplier;
            let index = rack.index();
            let heat = pilot.heat.levels[index];
            let capacity = profile.capacity[index];
            let attenuation = profile.attenuation[index];
            let mut candidates: Vec<(i32, i64, f64)> = pilot
                .heat
                .modules
                .iter()
                .filter_map(|(module_id, candidate)| {
                    if candidate.profile.heat_immune
                        || candidate.profile.damage_hp >= candidate.profile.structure_hp
                    {
                        return None;
                    }
                    let (candidate_rack, candidate_slot) = heat_rack_slot(candidate.profile.flag)?;
                    if candidate_rack != rack {
                        return None;
                    }
                    let distance = source_slot.abs_diff(candidate_slot);
                    let chance = heat_damage_chance(heat, capacity, attenuation, distance);
                    (chance > 0.0).then_some((candidate.profile.flag, *module_id, chance))
                })
                .collect();
            candidates.sort_by_key(|(flag, _, _)| *flag);
            if damage_hp > 0.0 {
                settlement.attempts.push(HeatDamageAttempt {
                    session,
                    source_module_id,
                    at,
                    damage_hp,
                    targets: candidates
                        .into_iter()
                        .map(|(_, module_id, chance)| (module_id, chance))
                        .collect(),
                });
            }
        }

        if let Some(runtime) = pilot.heat.modules.get_mut(&source_module_id) {
            runtime.completed_cycles = runtime.completed_cycles.saturating_add(1);
            runtime.cycle_start = Some(at);
            runtime.next_cycle_at = at.checked_add(active[&source_module_id].duration_ticks);
            runtime.settle_cycle_boundary();
            if active[&source_module_id].repeat > 0
                && runtime.completed_cycles >= active[&source_module_id].repeat
            {
                runtime.was_running = false;
                runtime.cycle_start = None;
                runtime.next_cycle_at = None;
                cycling.remove(&source_module_id);
            }
        }
    }

    advance_rack_heat(&mut pilot.heat, &cycling, now);
    let gauge_lines = heat_gauge_changes(pilot, session, now);
    if !gauge_lines.is_empty() {
        settlement.notifications.push((session, gauge_lines));
    }
    settlement
}

fn advance_rack_heat(state: &mut HeatState, cycling: &BTreeSet<i64>, now: i64) {
    let Some(profile) = state.profile.as_ref() else { return };
    let Some(previous_at) = state.updated_at else {
        state.updated_at = Some(now);
        return;
    };
    if now <= previous_at {
        return;
    }
    state.updated_at = Some(now);
    let delta_seconds =
        (((now - previous_at) as f64) / TICKS_PER_SECOND as f64).min(HEAT_MAX_ADVANCE_SECONDS);

    let mut incoming = [0.0; 3];
    for module_id in cycling {
        let Some(module) = state.modules.get(module_id) else { continue };
        if !module.overloaded
            || !module.profile.online
            || module.profile.damage_hp >= module.profile.structure_hp
        {
            continue;
        }
        let Some((rack, _)) = heat_rack_slot(module.profile.flag) else { continue };
        incoming[rack.index()] += module.profile.heat_absorption_rate;
    }

    let generation = profile.generation_multiplier;
    let capacities = profile.capacity;
    let dissipation = profile.dissipation_rate;
    for rack in HeatRack::ALL {
        let index = rack.index();
        let next = rack_heat_after(
            state.levels[index],
            capacities[index],
            incoming[index],
            generation,
            dissipation[index],
            delta_seconds,
        );
        state.levels[index] = if next < HEAT_ZERO_EPSILON { 0.0 } else { next };
    }
}

fn heat_gauge_changes(pilot: &mut Pilot, session: SessionId, now: i64) -> Vec<Value> {
    if !pilot.bound {
        return Vec::new();
    }
    let interval_due =
        pilot.heat.last_notify_at.is_none_or(|last| now.saturating_sub(last) >= HEAT_NOTIFY_INTERVAL);
    let force = pilot.heat.force_notify;
    let mut lines = Vec::new();
    for rack in HeatRack::ALL {
        let index = rack.index();
        let level = pilot.heat.levels[index];
        let notified = pilot.heat.notified_levels[index];
        let delta = (level - notified).abs();
        let due = force
            || (level <= 0.0 && notified > 0.0)
            || delta >= HEAT_NOTIFY_MIN_DELTA
            || (interval_due && delta >= HEAT_NOTIFY_INTERVAL_MIN_DELTA);
        if !due {
            continue;
        }
        pilot.heat.notified_levels[index] = level;
        lines.push(crate::modules::attribute_change(
            pilot.ship.spec.character_id,
            pilot.ship.spec.ship_id,
            rack.attribute_id(),
            now,
            real((level * 1_000.0).round() / 1_000.0),
            Value::None,
        ));
    }
    if !lines.is_empty() {
        pilot.heat.last_notify_at = Some(now);
    }
    pilot.heat.force_notify = false;
    let _ = session;
    lines
}
#[cfg(test)]
pub(crate)  mod tests;
