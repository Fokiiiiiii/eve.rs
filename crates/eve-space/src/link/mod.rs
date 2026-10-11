//! The messages between the service host and the simulation (`docs/design/adr/0007-space-simulation-process.md`).
//!
//! A frame is `u32 length | u8 kind | body`, little endian, text as `u16 length + UTF-8` and byte strings as
//! `u32 length + bytes`, like the front's link (ADR 0002). [`Message::encode`] writes a whole frame and
//! [`Message::decode`] reads a frame's payload (the kind and the body), so the same functions serve any framing layer.
//! `oracle/fixtures/simlink/messages.json` pins the layout for both sides.
mod decode;
mod decode_action;
mod decode_message;
mod encode;
mod payload;
mod reader;
use self::decode::*;
use self::decode_action::*;
use self::encode::*;

use crate::death::Credit;

use crate::deployable::{BombKind, Spec};

use crate::health::{Condition, Layer, Resonances, Vitals};

use crate::missiles::Missile;

use crate::drones::DroneEffect;

use crate::modules::{Action, Applies, Change, Effect, MiningFamily, Numbers, Reach, Remote, Rules};

use crate::scene::{HeatModuleProfile, HeatProfile, HeatRack};

use crate::strike::{Strike, StrikeShape};

use crate::ship::{ModuleSpec, NpcCombat, NpcIdentity, NpcWeapon, ShipSpec, ShipStance};

use crate::weapons::{Aoe, Arc, Charge, Spool, Turret};

use crate::wreck::WreckKind;
use crate::world::StructureStatus;

pub const PROTOCOL_VERSION: u16 = 59;
/// The message kinds.
pub mod kind;

/// What the host changes in the rounds of a gun, in the order the simulation hears it (the host asks for it when a pilot loads or unloads a gun in space).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ChargeOp {
    /// Rounds go into the gun: up to `capacity` of them in all, and no more than `available` more (what the hold has of the kind).
    Add { capacity: i64, available: i64 },
    /// Rounds come out of the gun: `quantity` of them, all of them when it is 0.
    Remove { quantity: i64 },
}

/// The command that asks how far a wreck or a can is (`args`: its item id); answered with `Measured`.
pub const COMMAND_MEASURE: u8 = 20;

/// Prime the visual half of an in-space login before the client requests its full ballpark state.
pub const COMMAND_PRIME_LOGIN_BALLPARK_VISUALS: u8 = 24;

/// How a ship comes into space.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum How {
    /// It undocks from a station.
    Undock,
    /// It is already in space (a login in space, a jump arrival).
    InSpace,
}

/// An NPC the host asks a scene to spawn: the ship, where it comes into space and how it is moving, what it does (`behavior`: a scripted tree, or
/// none) and what it is (its reactions, its weapons, what it leaves).
#[derive(Debug, Clone, PartialEq)]
pub struct Spawn {
    pub request_id: u64,
    pub solar_system_id: i32,
    pub ship: ShipSpec,
    /// The ship the place is measured from (0: the place is where it is): the NPC comes into space at that ship's position plus `position`.
    pub anchor_ship_id: i64,
    pub position: [f64; 3],
    pub velocity: [f64; 3],
    pub direction: [f64; 3],
    pub behavior: String,
    pub identity: NpcIdentity,
}

/// A drone the host launches from the drone bay of the ship of `session_id`: the drone's ship, the weapon it fights with and how it flies (answered with `SpawnReply`).
#[derive(Debug, Clone, PartialEq)]
pub struct LaunchDrone {
    pub request_id: u64,
    pub session_id: u64,
    pub ship: ShipSpec,
    pub weapon: Option<NpcWeapon>,
    pub graphic_id: i32,
    pub orbit_distance: f64,
    /// How far off its target the drone can shoot, metres (the type's `entityAttackRange`, at least the weapon's optimal range): inside it the drone fights.
    pub attack_range: f64,
    pub cruise_fraction: f64,
    pub mining_amount_m3: f64,
    pub work_cycle_ms: f64,
    pub mining_waste_multiplier: f64,
    pub mining_waste_probability: f64,
    pub salvage_access_bonus: f64,
    /// The non-damage effect the drone runs at its target each cycle (a stasis webifier, a remote repairer, an ECM jammer), when its type has one.
    pub effect: Option<DroneEffect>,
}

/// A wreck the host made, to be put in space: the item it is (the host's own id), what it is, where, and who may take what is in it.
#[derive(Debug, Clone, PartialEq)]
pub struct WreckPlacement {
    pub solar_system_id: i32,
    pub wreck_id: i64,
    /// `WRECK_GROUP` for a ship's wreck, `CORPSE_GROUP` for the corpse of a pilot whose capsule was destroyed.
    pub group_id: i32,
    pub kind: WreckKind,
    pub position: [f64; 3],
    pub direction: [f64; 3],
    pub radius: f64,
    /// The ship that was destroyed.
    pub launcher_id: i64,
    pub credit: Credit,
    /// Nothing in it to take.
    pub is_empty: bool,
}

/// A deployable the host keeps a record of (a cynosural field, protocol 44): where it lies, whose it is and how long it lives from now. The simulation
/// says so when it is lit, and the host sends the same message back to put it in space again after a restart (with what is left of its life).
#[derive(Debug, Clone, PartialEq)]
pub struct DeployedPlacement {
    pub solar_system_id: i32,
    pub deployable_id: i64,
    pub name: String,
    pub type_id: i32,
    pub group_id: i32,
    pub position: [f64; 3],
    pub radius: f64,
    pub owner_id: i64,
    pub corporation_id: i32,
    pub alliance_id: i32,
    pub ship_id: i64,
    pub lifetime_ms: f64,
}

/// A mobile warp disruptor the host keeps a record of (protocol 48): its id, type and group, where it lies, the radius of its ball, the range of its warp
/// disruption in metres, whether it is anchored (`active`), and whose it is.
#[derive(Debug, Clone, PartialEq)]
pub struct DisruptorPlacement {
    pub deployable_id: i64,
    pub solar_system_id: i32,
    pub name: String,
    pub type_id: i32,
    pub group_id: i32,
    pub position: [f64; 3],
    pub radius: f64,
    pub range: f64,
    pub active: bool,
    pub owner_id: i64,
    pub corporation_id: i32,
    pub alliance_id: i32,
    pub ship_id: i64,
}

/// Something a probe scan can find, as the scene knows it (command 27, protocol 52): the ship, drone, fighter squadron or deployable's item id, its kind (1 ship,
/// 2 drone, 3 fighter, 4 deployable), its type and where it is.
#[derive(Debug, Clone, PartialEq)]
pub struct ScanTarget {
    pub id: i64,
    pub kind: u8,
    pub type_id: i32,
    pub position: [f64; 3],
}

/// A scanner probe the host launched and keeps the record of: its id, its type and group, where it is and the radius of its ball. The simulation puts it
/// in space as a ball (`AddBalls2`) for those who see the place; it is never ended by the simulation (the host recalls it or its time is up).
/// Something a directional scan can see (command 28, protocol 54): its item id, its kind (see `scene::directional`), its type, where it is, its radius, and whether it is
/// cloaked or in warp (the host decides what the scan shows from these).
#[derive(Debug, Clone, PartialEq)]
pub struct DirectionalTarget {
    pub id: i64,
    pub kind: u8,
    pub type_id: i32,
    pub position: [f64; 3],
    pub radius: f64,
    pub cloaked: bool,
    pub warping: bool,
}

/// An Upwell structure the host puts in space (protocol 59): its ball, its owner and the slim fields it has now. It stays until the host takes it out.
#[derive(Debug, Clone, PartialEq)]
pub struct StructurePlacement {
    pub structure_id: i64,
    pub solar_system_id: i32,
    pub name: String,
    pub type_id: i32,
    pub group_id: i32,
    pub position: [f64; 3],
    pub radius: f64,
    pub owner_id: i64,
    pub corporation_id: i32,
    pub alliance_id: i32,
    pub status: crate::world::StructureStatus,
}

#[derive(Debug, Clone, PartialEq)]
pub struct ProbePlacement {
    pub probe_id: i64,
    pub solar_system_id: i32,
    pub name: String,
    pub type_id: i32,
    pub group_id: i32,
    pub position: [f64; 3],
    pub radius: f64,
    pub owner_id: i64,
    pub corporation_id: i32,
    pub alliance_id: i32,
    pub ship_id: i64,
}

#[derive(Debug, Clone, PartialEq)]
pub struct AsteroidPlacement {
    pub solar_system_id: i32,
    pub asteroid_id: i64,
    pub type_id: i32,
    pub group_id: i32,
    pub category_id: i32,
    pub name: String,
    pub graphic_id: Option<i32>,
    pub position: [f64; 3],
    pub direction: [f64; 3],
    pub radius: f64,
    pub units_remaining: i64,
}

/// A can the host made (an item of its own) for a pilot who jettisons cargo: the scene puts it `distance` metres ahead of the pilot's ship.
#[derive(Debug, Clone, PartialEq)]
pub struct ContainerPlacement {
    pub session_id: u64,
    pub container_id: i64,
    pub kind: WreckKind,
    pub radius: f64,
    /// The ship that jettisoned it.
    pub launcher_id: i64,
    pub credit: Credit,
    pub distance: f64,
}

/// A ship enters a solar system.
#[derive(Debug, Clone, PartialEq)]
pub struct Enter {
    pub session_id: u64,
    pub solar_system_id: i32,
    pub how: How,
    /// The station undocked from (0 when the ship was already in space).
    pub station_id: i64,
    pub ship: ShipSpec,
    pub position: Option<[f64; 3]>,
    pub direction: Option<[f64; 3]>,
}

/// One buff of a ship (`BuffState`): its buff collection, the value the buff gives the ship, and the time left, in milliseconds.
#[derive(Debug, Clone, PartialEq)]
pub struct BuffEntry {
    pub collection_id: i32,
    pub value: f64,
    pub remaining_ms: f64,
}

#[derive(Debug, Clone, PartialEq)]
pub enum Message {
    Hello {
        version: u16,
    },
    HelloAck {
        version: u16,
        poll_hz: u32,
    },
    Enter(Box<Enter>),
    /// Whether an `Enter` was done (`Err` carries why not).
    EnterReply {
        session_id: u64,
        outcome: Result<(), String>,
    },
    /// `why`: 0 docked, 1 logged off, 2 jumped.
    Leave {
        session_id: u64,
        why: u8,
    },
    Bind {
        session_id: u64,
    },
    UpdateState {
        session_id: u64,
    },
    Command {
        session_id: u64,
        command: u8,
        args: Vec<f64>,
    },
    ShipSpec {
        session_id: u64,
        ship: Box<ShipSpec>,
    },
    HeatProfile {
        session_id: u64,
        profile: HeatProfile,
    },
    ModuleOverheat {
        session_id: u64,
        module_id: i64,
        active: bool,
    },
    ModuleOffline {
        session_id: u64,
        module_id: i64,
    },
    RackOverheat {
        session_id: u64,
        rack: HeatRack,
        active: bool,
    },
    Notify {
        session_id: u64,
        name: String,
        id_type: String,
        args: Vec<u8>,
    },
    /// The ship docked: the host moves the character in and keeps what the ship has taken.
    Docked {
        session_id: u64,
        station_id: i64,
        condition: Condition,
    },
    /// A ship's warp ended at a belt (the one it warped to, or the nearest within 250 km of where it stopped): `belt_id` is the belt body and `position`
    /// where the belt is (what the host places what lives there around). The host decides whether anything does (belt rats). Sent for player ships only.
    Arrived {
        session_id: u64,
        belt_id: i64,
        position: [f64; 3],
    },
    /// What the ship has taken and how full its capacitor is (the answer to command 19).
    Condition {
        session_id: u64,
        command: u8,
        condition: Condition,
    },
    Refused {
        session_id: u64,
        command: u8,
        reason: String,
    },
    Accepted {
        session_id: u64,
        command: u8,
        at: i64,
    },

    /// The result of a host position query.
    Position {
        session_id: u64,
        command: u8,
        position: [f64; 3],
    },
    /// The scan targets of the scene, for the host's probe scan (answers command 27, protocol 52).
    ScanTargets {
        session_id: u64,
        command: u8,
        targets: Vec<ScanTarget>,
    },
    /// What the scene has in a cone of space, for the host's directional scan (answers command 28, protocol 54).
    DirectionalTargets {
        session_id: u64,
        command: u8,
        targets: Vec<DirectionalTarget>,
    },
    Approaching {
        session_id: u64,
        command: u8,
        distance: f64,
    },
    /// A `Jump` was accepted: the jump-out effect has begun, and the ship leaves for `destination_system` a few seconds on.
    Jumping {
        session_id: u64,
        command: u8,
        destination_system: i32,
        destination_gate: i64,
    },
    /// The ship has left its system through a gate and is in the scene of the other one, in front of `to_gate`: the host moves the character.
    Jumped {
        session_id: u64,
        from_system: i32,
        from_gate: i64,
        to_system: i32,
        to_gate: i64,
    },
    /// What a targeting command answers: `pending` is set when a lock is going (an `AddTarget`), `targets` are the ships and bodies locked (or,
    /// for `GetTargeters`, those that have this ship locked).
    Targets {
        session_id: u64,
        command: u8,
        pending: bool,
        targets: Vec<i64>,
    },
    /// A module effect the host asks the scene to run (answered with `Accepted` or `Refused` of command 17).
    Activate {
        session_id: u64,
        effect: Box<Effect>,
    },
    /// The ship of this session was destroyed (its hull was gone): where it was and how it faced, the wreck the scene made of it (0 when it made
    /// none: a player's wreck is the host's to make, and so is that of an NPC that asked for it, `NpcIdentity::host_wreck`), and the ship that took
    /// the last of the hull (0 when nobody did).
    Destroyed {
        session_id: u64,
        ship_id: i64,
        wreck_id: i64,
        position: [f64; 3],
        direction: [f64; 3],
        radius: f64,
        killer_ship_id: i64,
        /// The wreck the ship's type leaves (a wreck the host makes is made from it).
        wreck_kind: Option<WreckKind>,
    },
    /// A turret of this session's ship spent rounds: how many it has left (the host keeps the quantity of the charge item).
    ChargeSpent {
        session_id: u64,
        module_id: i64,
        rounds_left: i64,
    },
    /// The host asks a scene to spawn an NPC (answered with `SpawnReply`).
    Spawn(Box<Spawn>),
    /// Whether a `Spawn` was done: the session the NPC is known by, or why not.
    SpawnReply {
        request_id: u64,
        outcome: Result<u64, String>,
    },
    /// The host launches a drone beside the ship of a pilot (answered with `SpawnReply`: the session the drone is known by, or why not).
    LaunchDrone(Box<LaunchDrone>),
    /// A pilot gives its drones an order: the command (`DroneCommand::code`), the target of an engage, the drones (their item ids), and their control range.
    DroneCommand {
        session_id: u64,
        command: u8,
        target_id: i64,
        drone_ids: Vec<i64>,
        control_range: f64,
    },
    /// What each drone did with the order: its item id and the reason it did not take it (empty when it did).
    DroneResults {
        session_id: u64,
        command: u8,
        results: Vec<(i64, String)>,
    },
    /// A drone came within reach of its ship after `ReturnBay` and was taken in: the host moves its item back into the drone bay.
    DroneScooped {
        session_id: u64,
        drone_id: i64,
    },
    /// A drone was let go (abandoned, or its controller left the scene): it is out of the scene and the host decides what becomes of its item.
    DroneReleased {
        session_id: u64,
        drone_id: i64,
        position: [f64; 3],
    },
    /// The host launches a fighter squadron beside the ship of a pilot, circling it at the launch distance (answered with `SpawnReply`).
    LaunchFighter {
        request_id: u64,
        session_id: u64,
        /// The squadron, whose vitals are those of all its members together (`squadron_size` of them, each as tough as one).
        ship: Box<ShipSpec>,
        graphic_id: i32,
        cruise_fraction: f64,
        squadron_size: i32,
    },
    /// A squadron out in space lost members (or got back some): its size now, for the pilot who controls it.
    FighterSize {
        session_id: u64,
        fighter_id: i64,
        size: i32,
    },
    /// A pilot gives its fighter squadrons an order: the command (`FighterOrder::code`), the target (orbit and follow), the range (the orbit distance or the follow
    /// range, metres) and the squadrons (their item ids).
    FighterOrder {
        session_id: u64,
        command: u8,
        target_id: i64,
        fighter_ids: Vec<i64>,
        range: f64,
    },
    /// A pilot sends its fighter squadrons to a point of the scene (GOTO_POINT, metres): the squadrons (their item ids) and the point. What each did is
    /// answered with `FighterResults` under the order code 6.
    FighterGotoPoint {
        session_id: u64,
        fighter_ids: Vec<i64>,
        point: [f64; 3],
    },
    /// What each squadron did with the order: its item id and the reason it did not take it (empty when it did).
    FighterResults {
        session_id: u64,
        command: u8,
        results: Vec<(i64, String)>,
    },
    /// A squadron came back after a recall and landed: the host moves its item into the tube it came from.
    FighterLanded {
        session_id: u64,
        fighter_id: i64,
    },
    /// A squadron was let go (abandoned, or its controller left the scene): the host decides what becomes of its item.
    FighterReleased {
        session_id: u64,
        fighter_id: i64,
        position: [f64; 3],
    },
    DroneWorkCycle {
        session_id: u64,
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
    /// The host asks for the ship to be destroyed: its pilot's session is told of it (`Destroyed`) and the ship's wreck is made, for `credit` to loot.
    Destroy {
        ship_id: i64,
        credit: Credit,
    },
    /// The host gives a session whose ship was destroyed another (the capsule it made), at rest at the place (answered with `EnterReply`).
    Respawn {
        session_id: u64,
        ship: Box<ShipSpec>,
        position: [f64; 3],
        direction: [f64; 3],
    },
    /// The host has made a wreck (an item with contents) of a ship that was destroyed, and asks the scene to put it in space.
    PlaceWreck(Box<WreckPlacement>),
    PlaceAsteroid(Box<AsteroidPlacement>),
    /// The host has made a can for cargo a pilot jettisons, and asks the scene to put it ahead of the pilot's ship (answered with `ContainerPlaced`).
    PlaceContainer(Box<ContainerPlacement>),
    /// A can the host asked for is in space: where it is and how it lies.
    ContainerPlaced {
        session_id: u64,
        container_id: i64,
        position: [f64; 3],
        direction: [f64; 3],
    },
    /// What is in a wreck or a can changed (it has something to take, or nothing): whoever has it in view is told its slim item again, and the host
    /// (`session_id`: the pilot who changed it) is answered with `Accepted` under the command code `kind::CHANGE_WRECK` after what they are told.
    ChangeWreck {
        session_id: u64,
        wreck_id: i64,
        is_empty: bool,
    },
    /// The host takes a wreck or a can out of space (`effect`: the destruction effect the client plays first, 0 for none).
    RemoveWreck {
        wreck_id: i64,
        effect: i32,
    },
    /// A cynosural field the host keeps a record of: the simulation says it was lit (`DeployedLit`), and the host puts it back in space after a restart.
    Deployed(Box<DeployedPlacement>),
    /// The host takes a cynosural field out of space when its record's time is up; a deployable that is not one, or is not in space, is left alone.
    RemoveDeployed {
        deployable_id: i64,
    },
    /// The host puts a scanner probe in space where it was launched (protocol 45).
    PlaceStructure(Box<StructurePlacement>),
    RemoveStructure {
        structure_id: i64,
    },
    SetStructureState {
        solar_system_id: i32,
        structure_id: i64,
        status: crate::world::StructureStatus,
    },
    PlaceProbe(Box<ProbePlacement>),
    /// The host takes a scanner probe out of space (protocol 45); a deployable that is not a probe, or not in space, is left alone.
    RemoveProbe {
        probe_id: i64,
    },
    /// The host puts a mobile warp disruptor in space where its pilot dropped it (protocol 48).
    PlaceDisruptor(Box<DisruptorPlacement>),
    /// The host puts a Mobile Cynosural Inhibitor in space where its pilot dropped it (protocol 50); the range is the inhibitor's.
    PlaceInhibitor(Box<DisruptorPlacement>),
    /// The host puts a Mobile Cynosural Beacon in space where its pilot dropped it (protocol 58); the range is 0.
    PlaceCynoBeacon(Box<DisruptorPlacement>),
    /// The host says a mobile warp disruptor is anchored (`active`) or not (protocol 48); one not in space is left alone.
    SetDisruptorActive {
        deployable_id: i64,
        active: bool,
    },
    DroneWorkResult {
        session_id: u64,
        drone_id: i64,
        target_id: i64,
        cycle_id: u64,
        accepted: bool,
        reason: String,
        units_remaining: i64,
        delivered_units: i64,
        complete: bool,
    },
    /// A mutadaptive remote-repair cycle whose amount the host calculates from its target-specific spool state.
    RemoteRepairCycle {
        session_id: u64,
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
    /// The host's result for one pending mutadaptive remote-repair cycle.
    RemoteRepairCycleResult {
        session_id: u64,
        module_id: i64,
        target_id: i64,
        cycle_id: u64,
        accepted: bool,
        reason: String,
        amount: f64,
    },
    /// A work cycle of a module (a mining laser's or a salvager's) ended with its target there: the numbers of the cycle and the three rolls the
    /// simulation drew (waste, rounding, crit). The module is parked until a `ModuleWorkResult`, and the same `cycle_id` is sent again while it waits.
    ModuleWorkCycle {
        session_id: u64,
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
        /// The ships a command burst reaches (`WORK_BURST` only, empty for every other command): the ships of the scene within reach, the host decides.
        recipients: Vec<i64>,
    },
    /// The timed buffs of a ship of this session (a command burst's, protocol 43): the host sends them after each change, the simulation tells the client
    /// `OnDbuffUpdated` with them. An empty list clears the client's buffs. Nothing is answered.
    BuffState {
        session_id: u64,
        ship_id: i64,
        entries: Vec<BuffEntry>,
    },
    /// The host's decision for one module work cycle (the same fields as a drone's `DroneWorkResult`).
    ModuleWorkResult {
        session_id: u64,
        module_id: i64,
        target_id: i64,
        cycle_id: u64,
        accepted: bool,
        reason: String,
        units_remaining: i64,
        delivered_units: i64,
        complete: bool,
    },
    /// An effect of a module ended on the simulation's account (cycles done, capacitor short, target gone, forbidden, charge spent, or a refused work cycle).
    ModuleEnded {
        session_id: u64,
        module_id: i64,
        reason: u8,
    },
    /// An ECM burst reached ships on the simulation's account: the ship that runs it, and each ship it reached (a cloaked ship is not reached) with whether its
    /// roll jammed it. The host records the attack on each pilot reached (EVE.js `executeJammerBurstCycle`: aggression before the roll).
    EcmBurst {
        session_id: u64,
        ship_id: i64,
        reached: Vec<(i64, bool)>,
    },
    /// The answer to `COMMAND_MEASURE`: metres from the centre of the ship to the surface of the wreck or can, or none when there is no such wreck.
    Measured {
        session_id: u64,
        command: u8,
        distance: Option<f64>,
    },
    /// The host changes the rounds of a gun of this session's ship (a pilot loaded it or unloaded it in space). The simulation keeps the rounds of a gun that
    /// runs, so it is the one that says what they come to; it answers with `ChargeAdjusted`, after every `ChargeSpent` it told before.
    AdjustCharge {
        request_id: u64,
        session_id: u64,
        module_id: i64,
        /// The kind of charge the host loads (a gun that runs a charge of another kind is left as it is).
        charge_type: i32,
        op: ChargeOp,
    },
    /// The host refreshes the numbers of an effect that runs (FP-C4b, FP-C5): an overheat, or a tracking computer that starts, stops or overheats, changes
    /// the cycle, what a cycle costs the capacitor, the turret's numbers and the attributes the client is told. The simulation applies them at the start of
    /// the next cycle, as it applies an overheat (no effect on the cycle that is on). Nothing is answered.
    UpdateEffect {
        session_id: u64,
        module_id: i64,
        duration_ms: f64,
        capacitor_need: f64,
        turret: Option<Box<Turret>>,
        /// The attributes the client is told at the change (its item is the module).
        changes: Vec<Change>,
    },
    /// The answer to `AdjustCharge`: whether the gun runs (when it does not, the host has the rounds and nothing was changed here), the rounds it had and the
    /// rounds it has.
    ChargeAdjusted {
        request_id: u64,
        running: bool,
        before: i64,
        after: i64,
    },
    /// A targeting command the scene refused, with what the client is told of the target: its type, the range the ship can lock at and how far
    /// away the target is (both 0 unless the target was out of range; for `TARGET_LOCK_LIMIT_REACHED` the range is the number of targets the ship
    /// may hold).
    TargetRefused {
        session_id: u64,
        command: u8,
        reason: String,
        target_type: i32,
        desired_range: f64,
        actual_distance: f64,
    },
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum DecodeError {
    Empty,
    UnknownKind(u8),
    Truncated,
    Trailing,
    Utf8,
    BadValue(&'static str),
}

impl std::error::Error for DecodeError {}

struct Reader<'a> {
    data: &'a [u8],
}

impl Message {
    /// The kind byte of the message.
    pub fn kind(&self) -> u8 {
        match self {
            Self::Hello { .. } => kind::HELLO,
            Self::HelloAck { .. } => kind::HELLO_ACK,
            Self::Enter(_) => kind::ENTER,
            Self::EnterReply { .. } => kind::ENTER_REPLY,
            Self::Leave { .. } => kind::LEAVE,
            Self::Bind { .. } => kind::BIND,
            Self::UpdateState { .. } => kind::UPDATE_STATE,
            Self::Command { .. } => kind::COMMAND,
            Self::ShipSpec { .. } => kind::SHIP_SPEC,
            Self::HeatProfile { .. } => kind::HEAT_PROFILE,
            Self::ModuleOverheat { .. } => kind::MODULE_OVERHEAT,
            Self::ModuleOffline { .. } => kind::MODULE_OFFLINE,
            Self::RackOverheat { .. } => kind::RACK_OVERHEAT,
            Self::Notify { .. } => kind::NOTIFY,
            Self::Docked { .. } => kind::DOCKED,
            Self::Arrived { .. } => kind::ARRIVED,
            Self::Refused { .. } => kind::REFUSED,
            Self::Accepted { .. } => kind::ACCEPTED,
            Self::Position { .. } => kind::POSITION,
            Self::ScanTargets { .. } => kind::SCAN_TARGETS,
            Self::DirectionalTargets { .. } => kind::DIRECTIONAL_TARGETS,
            Self::Approaching { .. } => kind::APPROACHING,
            Self::Jumping { .. } => kind::JUMPING,
            Self::Jumped { .. } => kind::JUMPED,
            Self::Targets { .. } => kind::TARGETS,
            Self::TargetRefused { .. } => kind::TARGET_REFUSED,
            Self::Activate { .. } => kind::ACTIVATE,
            Self::Condition { .. } => kind::CONDITION,
            Self::Destroyed { .. } => kind::DESTROYED,
            Self::Spawn(_) => kind::SPAWN,
            Self::SpawnReply { .. } => kind::SPAWN_REPLY,
            Self::LaunchDrone(_) => kind::LAUNCH_DRONE,
            Self::DroneCommand { .. } => kind::DRONE_COMMAND,
            Self::DroneResults { .. } => kind::DRONE_RESULTS,
            Self::DroneScooped { .. } => kind::DRONE_SCOOPED,
            Self::DroneReleased { .. } => kind::DRONE_RELEASED,
            Self::LaunchFighter { .. } => kind::LAUNCH_FIGHTER,
            Self::FighterOrder { .. } => kind::FIGHTER_ORDER,
            Self::FighterResults { .. } => kind::FIGHTER_RESULTS,
            Self::FighterLanded { .. } => kind::FIGHTER_LANDED,
            Self::FighterReleased { .. } => kind::FIGHTER_RELEASED,
            Self::DroneWorkCycle { .. } => kind::DRONE_WORK_CYCLE,
            Self::Destroy { .. } => kind::DESTROY,
            Self::Respawn { .. } => kind::RESPAWN,
            Self::PlaceWreck(_) => kind::PLACE_WRECK,
            Self::PlaceAsteroid(_) => kind::PLACE_ASTEROID,
            Self::ChargeSpent { .. } => kind::CHARGE_SPENT,
            Self::PlaceContainer(_) => kind::PLACE_CONTAINER,
            Self::ContainerPlaced { .. } => kind::CONTAINER_PLACED,
            Self::ChangeWreck { .. } => kind::CHANGE_WRECK,
            Self::RemoveWreck { .. } => kind::REMOVE_WRECK,
            Self::DroneWorkResult { .. } => kind::DRONE_WORK_RESULT,
            Self::RemoteRepairCycle { .. } => kind::REMOTE_REPAIR_CYCLE,
            Self::RemoteRepairCycleResult { .. } => kind::REMOTE_REPAIR_CYCLE_RESULT,
            Self::ModuleWorkCycle { .. } => kind::MODULE_WORK_CYCLE,
            Self::ModuleWorkResult { .. } => kind::MODULE_WORK_RESULT,
            Self::ModuleEnded { .. } => kind::MODULE_ENDED,
            Self::EcmBurst { .. } => kind::ECM_BURST,
            Self::Measured { .. } => kind::MEASURED,
            Self::AdjustCharge { .. } => kind::ADJUST_CHARGE,
            Self::UpdateEffect { .. } => kind::UPDATE_EFFECT,
            Self::BuffState { .. } => kind::BUFF_STATE,
            Self::ChargeAdjusted { .. } => kind::CHARGE_ADJUSTED,
            Self::FighterGotoPoint { .. } => kind::FIGHTER_GOTO_POINT,
            Self::FighterSize { .. } => kind::FIGHTER_SIZE,
            Self::Deployed(_) => kind::DEPLOYED,
            Self::RemoveDeployed { .. } => kind::REMOVE_DEPLOYED,
            Self::PlaceStructure(_) => kind::PLACE_STRUCTURE,
            Self::RemoveStructure { .. } => kind::REMOVE_STRUCTURE,
            Self::SetStructureState { .. } => kind::SET_STRUCTURE_STATE,
            Self::PlaceProbe(_) => kind::PLACE_PROBE,
            Self::RemoveProbe { .. } => kind::REMOVE_PROBE,
            Self::PlaceDisruptor(_) => kind::PLACE_DISRUPTOR,
            Self::PlaceInhibitor(_) => kind::PLACE_INHIBITOR,
            Self::PlaceCynoBeacon(_) => kind::PLACE_CYNO_BEACON,
            Self::SetDisruptorActive { .. } => kind::SET_DISRUPTOR_ACTIVE,
        }
    }
}
