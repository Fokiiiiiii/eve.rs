//! A running solar system: the ships in it, what commands do to them, and what the client is told (ADR 0007).
//!
//! The scene has no clock of its own. Every call says what time it is (a FILETIME) and a ship's motion is worked out
//! from the time it was last settled to that time, so the same calls at the same times give the same bytes.
mod bridge;

/// The most passengers one conduit jump takes (the Black Ops limit is 30; the array leaves room for the carriers).
pub const MAX_CONDUIT_PASSENGERS: usize = 64;
mod collision;
mod decloak;
mod directional;
mod docking;
mod heat;
mod jump;
mod movement;
mod scan_targets;
mod session;
mod structures;
mod tether;
mod warping;
mod wormholes;
use self::collision::*;
pub use self::decloak::{CLOAK_PROXIMITY_METERS, CLOAK_STABILIZATION_MS, CloakTrigger, DecloakCause, OBSERVATORY_DECLOAK_PROBABILITY};
#[cfg(test)]
use self::heat::*;
use self::movement::*;
use self::warping::*;

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
// the conduit's passenger array makes one variant large; boxing it would end the Copy that every command is passed by
#[allow(clippy::large_enum_variant)]
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
    /// The point `distance` metres ahead of the ship along its heading: where a mobile warp disruptor is dropped (host query, code 25, protocol 49).
    DropPoint {
        distance: f64,
    },
    /// The scan targets of the scene for a probe scan (host query, code 27, protocol 52): the ships and companions the pilot can see, and the deployables a
    /// probe can scan. A cloaked ship is not one; the pilot's own ship is not.
    ScanTargets,
    /// The things a directional scan can see in the scene (host query, code 28, protocol 54); the host takes the cone out of them.
    DirectionalScan,
    /// Jump through a stargate; `destination_gate` is the gate the client says it leads to (0 when it does not say).
    Jump {
        gate_id: i64,
        destination_gate: i64,
    },
    /// Jump to a cynosural field lit in another system (code 26): the ship's drive spools for `spool_ms`, then the ship leaves for
    /// `destination_system` and appears at the field `field_id` there ([`Scene::arrive`]). The activation takes `capacitor` of the ship's full
    /// capacitor (the host's fraction: the hull's 95 % less the pilot's Jump Drive Operation), and is refused when the charge is short.
    CynoJump {
        field_id: i64,
        destination_system: i32,
        spool_ms: f64,
        capacitor: f64,
    },
    /// Opens a jump bridge where the ship is, to the field `field_id` lit in `destination_system` (code 30, protocol 55): the bridge lives `lifetime_ms`,
    /// the ship holds still for `immobile_ms` (zero when it is not held), and the activation takes `capacitor` GJ.
    OpenBridge {
        field_id: i64,
        destination_system: i32,
        lifetime_ms: f64,
        immobile_ms: f64,
        range_meters: f64,
        capacitor: f64,
    },
    /// The ship passes through the bridge of the ship `bridge_ship_id` to the field `field_id` in `destination_system` (code 31, protocol 55).
    BridgeJump {
        bridge_ship_id: i64,
        field_id: i64,
        destination_system: i32,
    },
    /// A Black Ops conduit jump (code 32, protocol 57): the ship and the `passenger_count` ships in `passengers` (ship ids) jump to the field `field_id` at once;
    /// the conduit reaches `range_meters`.
    ConduitJump {
        field_id: i64,
        destination_system: i32,
        range_meters: f64,
        passenger_count: u8,
        passengers: [i64; MAX_CONDUIT_PASSENGERS],
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
            Self::CynoJump { .. } => 26,
            Self::OpenBridge { .. } => 30,
            Self::BridgeJump { .. } => 31,
            Self::ConduitJump { .. } => 32,
            Self::WarpToPoint { .. } => 21,
            Self::ResolveWarpTarget { .. } => 22,
            Self::PositionOf { .. } => 23,
            Self::DropPoint { .. } => 25,
            Self::ScanTargets => 27,
            Self::DirectionalScan => 28,
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
    /// The scan targets of the scene for the host's probe scan (command 27); it does not move anything.
    ScanTargets { session: SessionId, command: u8, targets: Vec<crate::link::ScanTarget> },
    /// The things a directional scan can see (command 28); it does not move anything.
    DirectionalTargets { session: SessionId, command: u8, targets: Vec<crate::link::DirectionalTarget> },
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
    /// A cynosural field was lit by the ship of `session`: the host keeps its record (`Message::Deployed`).
    DeployedLit { session: SessionId, placement: Box<crate::link::DeployedPlacement> },
    /// A hit emptied a layer of a structure or paused its repair; the host decides what that means (protocol 60).
    StructureDamaged { structure_id: i64, depleted: Option<crate::health::Layer>, over_threshold: bool },
    /// A tether transition; the host owns its client FX and access policy (protocol 62).
    TetherChanged { session: SessionId, previous_structure_id: i64, structure_id: i64, repairing: bool },
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
        /// The type and the corporation of the ship (the host prices an NPC's kill by its type, `NpcBounty`).
        ship_type_id: i32,
        corporation_id: i32,
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
    /// An ECM burst reached the ships about it (not cloaked ones): the ship that runs it, and each ship reached with whether its roll jammed it.
    EcmBurst { session: SessionId, ship_id: i64, reached: Vec<(i64, bool)> },
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
/// Fresh candidates for one controlled-structure damage pulse or chain hop; Host returns an owner-corp-approved subset using the complete correlation tuple.
    StructureDamageCandidatesRequest {
        session: SessionId,
        structure_id: i64,
        module_id: i64,
        effect_id: i32,
        activation_nonce: u64,
        control_lease_generation: i64,
        cycle_id: u64,
        pulse_hop_ordinal: u32,
        candidate_ids: Vec<i64>,
    },
    /// A Sim-owned damageable entity was inserted or had its authoritative identity updated.
    SimDamageableEntityUpserted {
        solar_system_id: i32,
        entity_id: i64,
        type_id: i32,
        owner_id: i64,
        corporation_id: i64,
        alliance_id: i64,
        war_faction_id: i64,
    },
    /// A Sim-owned damageable entity left the scene without a death event.
    SimDamageableEntityRemoved { solar_system_id: i32, entity_id: i64 },
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
        /// The ships a command burst reaches (a burst only; empty for every other command), as the scene measures them.
        recipients: Vec<i64>,
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
            | Out::TetherChanged { session, .. }
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
            | Out::StructureDamageCandidatesRequest { session, .. }
            | Out::ModuleWorkCycle { session, .. }
            | Out::ModuleEnded { session, .. }
            | Out::DroneResults { session, .. }
            | Out::FighterLanded { session, .. }
            | Out::FighterReleased { session, .. }
            | Out::FighterResults { session, .. }
            | Out::FighterSize { session, .. }
            | Out::EcmBurst { session, .. }
            | Out::TargetRefused { session, .. }
            | Out::DeployedLit { session, .. }
            | Out::Position { session, .. }
            | Out::ScanTargets { session, .. }
            | Out::DirectionalTargets { session, .. } => *session,
            // a structure's notice is for the host, not a pilot: no pilot has this session
            Out::StructureDamaged { .. }
            | Out::SimDamageableEntityUpserted { .. }
            | Out::SimDamageableEntityRemoved { .. } => 0,
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
    /// The station the ship undocked from while it is still inside its sphere: the undock point lies in it, so the station does not bump the
    /// ship until it is out (`collision::spares`).
    pub(crate) leaving_station: Option<i64>,
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
    /// Until when the module cloak is stabilized: a Mobile Observatory's ping cannot decloak the ship until then (`decloak::CLOAK_STABILIZATION_MS`).
    pub(crate) cloak_stabilized_until: i64,
    /// When the post-decloak targeting delay ends: until then the ship acquires no lock (the cloak's cloakingTargetingDelay, from the decloak).
    pub(crate) targeting_blocked_until: i64,
    /// The overload state, per-rack heat and module heat damage carried by this ship.
    heat: HeatState,
    tether: tether::TetherState,
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
            leaving_station: None,
            immobile_until: 0,
            disrupted_until: 0,
            no_jump_until: 0,
            warp_blocked_until: 0,
            incoming: crate::ewar::Incoming::default(),
            cloak: crate::visibility::CloakState::Uncloaked,
            cloak_stabilized_until: 0,
            targeting_blocked_until: 0,
            heat: HeatState::default(),
            tether: tether::TetherState::default(),
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
    /// The cynosural field a jump drive goes to (0 for a stargate jump); the arrival is at the field, not at a gate.
    field_id: Option<i64>,
    /// The far end of a wormhole the ship came through: its position and radius (the arrival is by it, not by a gate).
    far_end: Option<(Vec3, f64)>,
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
    /// The cynosural field the ship jumped to, when it did not go through a gate (the arrival is at the field).
    pub to_field: Option<i64>,
    /// The far end of the wormhole the ship came through, when it did (the arrival is by the far end).
    pub to_far_end: Option<(Vec3, f64)>,
    pilot: Pilot,
}

#[derive(Debug)]
pub(crate) struct ControlledStructureObserver {
    pub(crate) structure_id: i64,
    pub(crate) previous_ship_id: Option<i64>,
    pub(crate) needs_initial_ballpark: bool,
    pub(crate) bound: bool,
    pub(crate) sees: BTreeSet<SessionId>,
    pub(crate) sees_wrecks: BTreeSet<i64>,
    pub(crate) sees_asteroids: BTreeSet<i64>,
}

/// State owned by a structure, retained while control changes hands.
#[derive(Debug, Clone)]
pub(crate) struct ControlledStructureModules {
    pub(crate) spec: crate::ship::ShipSpec,
    pub(crate) modules: crate::modules::Modules,
    pub(crate) targeting: crate::targeting::Targeting,
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
    /// The damage-over-time applications (breacher pods) on each ship, by ship id (`dot_weapons`).
    pub(crate) dots: BTreeMap<i64, crate::dot_weapons::Target>,
    pub(crate) dot_sequence: u64,
    /// The probes and bombs in space, by id (`crate::deployable`), and how many have been released in this scene.
    pub(crate) deployables: BTreeMap<i64, crate::deployable::Deployable>,
    /// The structures' bombs and the authorization each blast waits on (keyed by the bomb's deployable id).
    pub(crate) bomb_impacts: BTreeMap<i64, crate::deployable::BombImpact>,
    pub(crate) deployable_sequence: u64,
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
    /// SDE capital-size by type ID for live transport-field participants.
    pub(crate) field_capital_size_by_type: Option<Arc<HashMap<i32, bool>>>,
    /// The sessions whose ship was destroyed and have no other yet (the host gives them a capsule with `respawn`): what they had bound and seen.
    pub(crate) widowed: BTreeMap<SessionId, Widow>,
    /// Unpiloted ships stay in the scene under synthetic sessions until someone boards or destroys them.
    pub(crate) unpiloted: BTreeMap<i64, SessionId>,
    /// The session viewing each controlled structure, without a second physical ship.
    pub(crate) controlled_structures: BTreeMap<SessionId, ControlledStructureObserver>,
/// The latest Host StructurePilotRecord.Version for each controlled-structure lease; requests echo this value verbatim.
    pub(crate) control_lease_generations: BTreeMap<i64, i64>,
    /// IDs whose authoritative identities this Sim published for Host damage-target resolution.
    pub(crate) sim_damageable_entities: BTreeSet<i64>,
    /// The health of each structure in space, by id (`crate::health::StructureHealth`).
    pub(crate) structure_health: BTreeMap<i64, crate::health::StructureHealth>,
    /// Module capacitor and targeting state keyed by physical structure id.
    pub(crate) structure_modules: BTreeMap<i64, ControlledStructureModules>,
    /// The Pochven population of the system (`crate::pochven_population`).
    pub(crate) pochven: crate::pochven_population::Population,
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
            dots: BTreeMap::new(),
            dot_sequence: 0,
            deployables: BTreeMap::new(),
            bomb_impacts: BTreeMap::new(),
            deployable_sequence: 0,
            drones: BTreeMap::new(),
            drone_threats: BTreeMap::new(),
            brains: None,
            wrecks: BTreeMap::new(),
            asteroids: BTreeMap::new(),
            wreck_sequence: 0,
            destroyed: Vec::new(),
            widowed: BTreeMap::new(),
            unpiloted: BTreeMap::new(),
            controlled_structures: BTreeMap::new(),
            control_lease_generations: BTreeMap::new(),
            sim_damageable_entities: BTreeSet::new(),
            structure_health: BTreeMap::new(),
            structure_modules: BTreeMap::new(),
            pochven: Default::default(),
            wreck_catalog: None,
            field_capital_size_by_type: None,
        }
    }

    pub(crate) fn solar_system_id(&self) -> i32 {
        self.system.id
    }


    /// Publish a Sim-owned entity identity after the scene has successfully inserted it.
    pub(crate) fn publish_sim_damageable_entity(
        &mut self,
        entity_id: i64,
        type_id: i32,
        owner_id: i64,
        corporation_id: i64,
        alliance_id: i64,
        war_faction_id: i64,
    ) -> Out {
        self.sim_damageable_entities.insert(entity_id);
        Out::SimDamageableEntityUpserted {
            solar_system_id: self.solar_system_id(),
            entity_id,
            type_id,
            owner_id,
            corporation_id,
            alliance_id,
            war_faction_id,
        }
    }

    /// Remove a Sim-owned profile only when this scene previously published it.
    pub(crate) fn remove_sim_damageable_entity(&mut self, entity_id: i64) -> Option<Out> {
        self.sim_damageable_entities.remove(&entity_id).then_some(Out::SimDamageableEntityRemoved {
            solar_system_id: self.solar_system_id(),
            entity_id,
        })
    }

    /// End the lifecycle of every Sim-owned profile before its scene is discarded.
    pub(crate) fn take_sim_damageable_entity_removals(&mut self) -> Vec<Out> {
        std::mem::take(&mut self.sim_damageable_entities)
            .into_iter()
            .map(|entity_id| Out::SimDamageableEntityRemoved {
                solar_system_id: self.solar_system_id(),
                entity_id,
            })
            .collect()
    }

    /// Gives the scene the item types the wreck rules look up, so that a ship that is destroyed leaves the wreck of its type.
    pub fn set_wreck_catalog(&mut self, catalog: Arc<crate::wreck_types::WreckCatalog>) {
        self.wreck_catalog = Some(catalog);
    }

    pub(crate) fn set_field_capital_size_by_type(&mut self, types: Arc<HashMap<i32, bool>>) {
        self.field_capital_size_by_type = Some(types);
    }

    pub fn system(&self) -> &SolarSystem {
        &self.system
    }

    /// Whether no player or persistent structure state keeps this scene; NPCs alone do not.
    pub fn is_empty(&self) -> bool {
        // a scene with a wormhole end in it is kept: the host placed the end for a ship that is coming (the poll drops empty scenes)
        self.widowed.is_empty()
            && self.controlled_structures.is_empty()
            && self.structure_health.is_empty()
            && self.pilots.keys().all(|session| crate::npc::is_npc_session(*session))
            && !self.system.bodies.iter().any(|body| body.kind == BodyKind::Wormhole)
    }

    /// The wreck with this item id, if it is in the scene.
    pub fn wreck(&self, id: i64) -> Option<&Wreck> {
        self.wrecks.get(&id)
    }

    /// A session whose ship was destroyed will not be given another (it left): its ballpark is not kept.
    pub fn forget_widow(&mut self, session: SessionId) {
        self.widowed.remove(&session);
    }

    pub(crate) fn awaiting_respawn(&self, session: SessionId) -> bool {
        self.widowed.contains_key(&session)
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

    pub(crate) fn notify(session: SessionId, name: &'static str, args: Vec<Value>) -> Out {
        Out::Notify { session, name, id_type: "clientID", args }
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
}

/// What a movement command comes to once the ship has looked at it.
enum Steering {
    /// Accepted, and the ship already does it: the client has nothing to be told.
    Nothing,
    /// Accepted: the actions of one update, each with what follows the ship's id.
    Actions(Vec<(&'static str, Vec<Value>)>),
    Refused(&'static str),
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

#[cfg(test)]
pub(crate) mod tests;
