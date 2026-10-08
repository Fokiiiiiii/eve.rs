//! The messages between the service host and the simulation (`docs/design/adr/0007-space-simulation-process.md`).
//!
//! A frame is `u32 length | u8 kind | body`, little endian, text as `u16 length + UTF-8` and byte strings as
//! `u32 length + bytes`, like the front's link (ADR 0002). [`Message::encode`] writes a whole frame and
//! [`Message::decode`] reads a frame's payload (the kind and the body), so the same functions serve any framing layer.
//! `oracle/fixtures/simlink/messages.json` pins the layout for both sides.

use crate::death::Credit;
use crate::health::{Condition, Layer, Resonances, Vitals};
use crate::missiles::Missile;
use crate::modules::{Action, Applies, Change, Effect, Numbers, Reach, Remote};
use crate::scene::{HeatModuleProfile, HeatProfile, HeatRack};
use crate::ship::{ModuleSpec, NpcCombat, NpcIdentity, NpcWeapon, ShipSpec};
use crate::weapons::{Arc, Aoe, Charge, Spool, Turret};
use crate::wreck::WreckKind;

pub const PROTOCOL_VERSION: u16 = 21;

/// The message kinds.
pub mod kind {
    pub const HELLO: u8 = 1;
    pub const HELLO_ACK: u8 = 2;
    pub const ENTER: u8 = 3;
    pub const LEAVE: u8 = 4;
    pub const BIND: u8 = 5;
    pub const UPDATE_STATE: u8 = 6;
    pub const COMMAND: u8 = 7;
    pub const SHIP_SPEC: u8 = 8;
    pub const NOTIFY: u8 = 9;
    pub const DOCKED: u8 = 10;
    pub const REFUSED: u8 = 11;
    pub const ACCEPTED: u8 = 12;
    pub const ENTER_REPLY: u8 = 13;
    pub const APPROACHING: u8 = 14;
    pub const JUMPING: u8 = 15;
    pub const JUMPED: u8 = 16;
    pub const TARGETS: u8 = 17;
    pub const TARGET_REFUSED: u8 = 18;
    pub const ACTIVATE: u8 = 19;
    pub const CONDITION: u8 = 20;
    pub const DESTROYED: u8 = 21;
    pub const SPAWN: u8 = 22;
    pub const SPAWN_REPLY: u8 = 23;
    pub const DESTROY: u8 = 24;
    pub const RESPAWN: u8 = 25;
    pub const PLACE_WRECK: u8 = 26;
    pub const CHARGE_SPENT: u8 = 27;
    pub const PLACE_CONTAINER: u8 = 28;
    pub const CONTAINER_PLACED: u8 = 29;
    pub const CHANGE_WRECK: u8 = 30;
    pub const REMOVE_WRECK: u8 = 31;
    pub const MEASURED: u8 = 32;
    pub const ADJUST_CHARGE: u8 = 33;
    pub const CHARGE_ADJUSTED: u8 = 34;
    pub const POSITION: u8 = 50;
    pub const ARRIVED: u8 = 35;
    pub const LAUNCH_DRONE: u8 = 36;
    pub const DRONE_COMMAND: u8 = 37;
    pub const DRONE_RESULTS: u8 = 38;
    pub const DRONE_SCOOPED: u8 = 39;
    pub const DRONE_RELEASED: u8 = 40;
    pub const PLACE_ASTEROID: u8 = 45;
    pub const DRONE_WORK_CYCLE: u8 = 46;
    pub const DRONE_WORK_RESULT: u8 = 47;
    pub const REMOTE_REPAIR_CYCLE: u8 = 48;
    pub const REMOTE_REPAIR_CYCLE_RESULT: u8 = 49;
    pub const HEAT_PROFILE: u8 = 41;
    pub const MODULE_OVERHEAT: u8 = 42;
    pub const MODULE_OFFLINE: u8 = 43;
    pub const RACK_OVERHEAT: u8 = 44;
}

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

impl std::fmt::Display for DecodeError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::Empty => write!(f, "empty frame"),
            Self::UnknownKind(kind) => write!(f, "unknown message kind {kind}"),
            Self::Truncated => write!(f, "the frame ends before the message does"),
            Self::Trailing => write!(f, "bytes follow the end of the message"),
            Self::Utf8 => write!(f, "text is not UTF-8"),
            Self::BadValue(what) => write!(f, "bad {what}"),
        }
    }
}

impl std::error::Error for DecodeError {}

struct Reader<'a> {
    data: &'a [u8],
}

impl<'a> Reader<'a> {
    fn take(&mut self, count: usize) -> Result<&'a [u8], DecodeError> {
        if self.data.len() < count {
            return Err(DecodeError::Truncated);
        }
        let (head, rest) = self.data.split_at(count);
        self.data = rest;
        Ok(head)
    }

    fn array<const N: usize>(&mut self) -> Result<[u8; N], DecodeError> {
        Ok(self.take(N)?.try_into().expect("took N bytes"))
    }

    fn u8(&mut self) -> Result<u8, DecodeError> {
        Ok(self.array::<1>()?[0])
    }

    fn u16(&mut self) -> Result<u16, DecodeError> {
        Ok(u16::from_le_bytes(self.array()?))
    }

    fn u32(&mut self) -> Result<u32, DecodeError> {
        Ok(u32::from_le_bytes(self.array()?))
    }

    fn i32(&mut self) -> Result<i32, DecodeError> {
        Ok(i32::from_le_bytes(self.array()?))
    }

    fn u64(&mut self) -> Result<u64, DecodeError> {
        Ok(u64::from_le_bytes(self.array()?))
    }

    fn i64(&mut self) -> Result<i64, DecodeError> {
        Ok(i64::from_le_bytes(self.array()?))
    }

    fn f64(&mut self) -> Result<f64, DecodeError> {
        Ok(f64::from_le_bytes(self.array()?))
    }

    fn text(&mut self) -> Result<String, DecodeError> {
        let length = self.u16()? as usize;
        String::from_utf8(self.take(length)?.to_vec()).map_err(|_| DecodeError::Utf8)
    }

    fn bytes(&mut self) -> Result<Vec<u8>, DecodeError> {
        let length = self.u32()? as usize;
        Ok(self.take(length)?.to_vec())
    }

    fn finish(&self) -> Result<(), DecodeError> {
        if self.data.is_empty() { Ok(()) } else { Err(DecodeError::Trailing) }
    }
}

fn put_text(out: &mut Vec<u8>, text: &str) {
    out.extend_from_slice(&(text.len() as u16).to_le_bytes());
    out.extend_from_slice(text.as_bytes());
}

fn put_bytes(out: &mut Vec<u8>, bytes: &[u8]) {
    out.extend_from_slice(&(bytes.len() as u32).to_le_bytes());
    out.extend_from_slice(bytes);
}

fn put_condition(out: &mut Vec<u8>, condition: &Condition) {
    for number in [condition.damage, condition.armor_damage, condition.shield_charge, condition.charge] {
        out.extend_from_slice(&number.to_le_bytes());
    }
}

fn put_vitals(out: &mut Vec<u8>, vitals: &Vitals) {
    let resonances = &vitals.resonances;
    let layers = [vitals.shield_capacity, vitals.armor_hp, vitals.structure_hp];
    for number in
        layers.iter().chain(&resonances.shield).chain(&resonances.armor).chain(&resonances.structure)
    {
        out.extend_from_slice(&number.to_le_bytes());
    }
}

fn put_heat_profile(out: &mut Vec<u8>, profile: &HeatProfile) {
    out.extend_from_slice(&profile.generation_multiplier.to_le_bytes());
    out.extend_from_slice(&profile.damage_multiplier.to_le_bytes());
    out.extend_from_slice(&i32::from(profile.thermodynamics_level).to_le_bytes());
    for value in profile
        .capacity
        .iter()
        .chain(&profile.initial_rack_heat)
        .chain(&profile.dissipation_rate)
        .chain(&profile.attenuation)
    {
        out.extend_from_slice(&value.to_le_bytes());
    }
    out.extend_from_slice(&(profile.modules.len() as i32).to_le_bytes());
    for module in &profile.modules {
        out.extend_from_slice(&module.module_id.to_le_bytes());
        out.extend_from_slice(&module.flag.to_le_bytes());
        for value in
            [module.structure_hp, module.damage_hp, module.heat_damage_hp, module.heat_absorption_rate]
        {
            out.extend_from_slice(&value.to_le_bytes());
        }
        out.extend_from_slice(&[
            u8::from(module.overloadable),
            u8::from(module.heat_immune),
            u8::from(module.online),
        ]);
    }
}

fn put_ship(out: &mut Vec<u8>, ship: &ShipSpec) {
    out.extend_from_slice(&ship.ship_id.to_le_bytes());
    out.extend_from_slice(&ship.character_id.to_le_bytes());
    out.extend_from_slice(&ship.type_id.to_le_bytes());
    out.extend_from_slice(&ship.group_id.to_le_bytes());
    out.extend_from_slice(&ship.category_id.to_le_bytes());
    put_text(out, &ship.name);
    out.extend_from_slice(&ship.owner_id.to_le_bytes());
    out.extend_from_slice(&ship.corporation_id.to_le_bytes());
    out.extend_from_slice(&ship.alliance_id.to_le_bytes());
    out.extend_from_slice(&ship.war_faction_id.to_le_bytes());
    for number in [
        ship.security_status,
        ship.radius,
        ship.mass,
        ship.max_velocity,
        ship.agility,
        ship.warp_speed_au,
        ship.shield_recharge_ms,
        ship.max_target_range,
        ship.scan_resolution,
        ship.signature_radius,
        ship.capacitor_capacity,
        ship.capacitor_recharge_s,
    ] {
        out.extend_from_slice(&number.to_le_bytes());
    }
    out.extend_from_slice(&ship.max_locked_targets.to_le_bytes());
    put_vitals(out, &ship.vitals);
    put_condition(out, &ship.condition);
    out.extend_from_slice(&(ship.modules.len() as u32).to_le_bytes());
    for module in &ship.modules {
        out.extend_from_slice(&module.item_id.to_le_bytes());
        out.extend_from_slice(&module.type_id.to_le_bytes());
        out.extend_from_slice(&module.flag.to_le_bytes());
    }
    for number in ship.sensor_strength.iter().chain([&ship.ecm_resistance]) {
        out.extend_from_slice(&number.to_le_bytes());
    }
}

fn put_numbers(out: &mut Vec<u8>, numbers: &Numbers) {
    for number in [numbers.mass, numbers.max_velocity, numbers.signature_radius] {
        out.extend_from_slice(&number.to_le_bytes());
    }
}

fn put_changes(out: &mut Vec<u8>, changes: &[Change]) {
    out.extend_from_slice(&(changes.len() as u32).to_le_bytes());
    for change in changes {
        out.extend_from_slice(&change.item_id.to_le_bytes());
        out.extend_from_slice(&change.attribute_id.to_le_bytes());
        out.extend_from_slice(&change.value.to_le_bytes());
    }
}

/// A weapon of an NPC (or a drone): the module (the ship), the effect, the cycle in milliseconds, what a cycle costs the capacitor, and the turret.
fn put_weapon(out: &mut Vec<u8>, weapon: &NpcWeapon) {
    out.extend_from_slice(&weapon.module_id.to_le_bytes());
    out.extend_from_slice(&weapon.effect_id.to_le_bytes());
    out.extend_from_slice(&weapon.cycle_ms.to_le_bytes());
    out.extend_from_slice(&weapon.capacitor_need.to_le_bytes());
    put_turret(out, &weapon.turret);
}

fn read_weapon(reader: &mut Reader) -> Result<NpcWeapon, DecodeError> {
    let (module_id, effect_id, cycle_ms, capacitor_need) =
        (reader.i64()?, reader.i32()?, reader.f64()?, reader.f64()?);
    Ok(NpcWeapon { module_id, effect_id, cycle_ms, capacitor_need, turret: read_turret(reader)? })
}

fn put_identity(out: &mut Vec<u8>, identity: &NpcIdentity) {
    out.extend_from_slice(&identity.hostile_response_threshold.to_le_bytes());
    out.extend_from_slice(&identity.friendly_response_threshold.to_le_bytes());
    out.extend_from_slice(&identity.combat.aggression_range.to_le_bytes());
    out.extend_from_slice(&(identity.combat.weapons.len() as u32).to_le_bytes());
    for weapon in &identity.combat.weapons {
        put_weapon(out, weapon);
    }
    put_wreck_kind(out, identity.wreck.as_ref());
    out.push(u8::from(identity.hull));
    out.push(u8::from(identity.host_wreck));
}

/// A wreck's kind, or none: a flag, the type, the name and the graphic (0 for none).
fn put_wreck_kind(out: &mut Vec<u8>, kind: Option<&WreckKind>) {
    match kind {
        None => out.push(0),
        Some(wreck) => {
            out.push(1);
            out.extend_from_slice(&wreck.type_id.to_le_bytes());
            put_text(out, &wreck.name);
            out.extend_from_slice(&wreck.graphic_id.unwrap_or(0).to_le_bytes());
        }
    }
}

fn read_wreck_kind(reader: &mut Reader) -> Result<Option<WreckKind>, DecodeError> {
    match reader.u8()? {
        0 => Ok(None),
        1 => {
            let (type_id, name, graphic) = (reader.i32()?, reader.text()?, reader.i32()?);
            Ok(Some(WreckKind { type_id, name, graphic_id: (graphic != 0).then_some(graphic) }))
        }
        _ => Err(DecodeError::BadValue("wreck")),
    }
}

fn put_vector(out: &mut Vec<u8>, vector: &[f64; 3]) {
    for number in vector {
        out.extend_from_slice(&number.to_le_bytes());
    }
}

fn read_vector(reader: &mut Reader) -> Result<[f64; 3], DecodeError> {
    Ok([reader.f64()?, reader.f64()?, reader.f64()?])
}

fn put_credit(out: &mut Vec<u8>, credit: &Credit) {
    for number in [credit.character_id, credit.corporation_id, credit.alliance_id, credit.war_faction_id] {
        out.extend_from_slice(&number.to_le_bytes());
    }
}

fn read_credit(reader: &mut Reader) -> Result<Credit, DecodeError> {
    Ok(Credit {
        character_id: reader.i64()?,
        corporation_id: reader.i64()?,
        alliance_id: reader.i64()?,
        war_faction_id: reader.i64()?,
    })
}

fn read_turret(reader: &mut Reader) -> Result<Turret, DecodeError> {
    let weapon_type_id = reader.i32()?;
    let fx = reader.text()?;
    let raw_damage = [reader.f64()?, reader.f64()?, reader.f64()?, reader.f64()?];
    let (optimal_range, falloff, tracking_speed, signature_resolution) =
        (reader.f64()?, reader.f64()?, reader.f64()?, reader.f64()?);
    let charge = match reader.u8()? {
        0 => None,
        1 => Some(Charge {
            type_id: reader.i32()?,
            flag: reader.i32()?,
            rounds: reader.i64()?,
            per_cycle: reader.i64()?,
        }),
        _ => return Err(DecodeError::BadValue("charge")),
    };
    let missile = match reader.u8()? {
        0 => None,
        1 => Some(Missile {
            charge_type_id: reader.i32()?,
            group_id: reader.i32()?,
            name: reader.text()?,
            radius: reader.f64()?,
            mass: reader.f64()?,
            agility: reader.f64()?,
            hit_points: reader.f64()?,
            max_velocity: reader.f64()?,
            flight_time_ms: reader.f64()?,
            explosion_radius: reader.f64()?,
            explosion_velocity: reader.f64()?,
            damage_reduction_factor: reader.f64()?,
            damage_reduction_sensitivity: reader.f64()?,
        }),
        _ => return Err(DecodeError::BadValue("missile")),
    };
    let spool = match reader.u8()? {
        0 => None,
        1 => {
            let (per_cycle, max) = (reader.f64()?, reader.f64()?);
            if !per_cycle.is_finite() || per_cycle < 0.0 || !max.is_finite() || max < 0.0 {
                return Err(DecodeError::BadValue("spool"));
            }
            Some(Spool { per_cycle, max })
        }
        _ => return Err(DecodeError::BadValue("spool")),
    };
    let aoe = match reader.u8()? {
        0 => None,
        1 => {
            let (explosion_radius, explosion_velocity) = (reader.f64()?, reader.f64()?);
            let (damage_reduction_factor, damage_reduction_sensitivity) = (reader.f64()?, reader.f64()?);
            let numbers = [explosion_radius, explosion_velocity, damage_reduction_factor, damage_reduction_sensitivity];
            if numbers.iter().any(|number| !number.is_finite() || *number < 0.0) {
                return Err(DecodeError::BadValue("aoe"));
            }
            Some(Aoe { explosion_radius, explosion_velocity, damage_reduction_factor, damage_reduction_sensitivity })
        }
        _ => return Err(DecodeError::BadValue("aoe")),
    };
    let arc = match reader.u8()? {
        0 => None,
        1 => {
            let (range, targets) = (reader.f64()?, reader.i32()?);
            if !range.is_finite() || range < 0.0 || targets < 0 {
                return Err(DecodeError::BadValue("arc"));
            }
            Some(Arc { range, targets })
        }
        _ => return Err(DecodeError::BadValue("arc")),
    };
    Ok(Turret {
        weapon_type_id,
        fx,
        raw_damage,
        optimal_range,
        falloff,
        tracking_speed,
        signature_resolution,
        charge,
        missile,
        spool,
        aoe,
        arc,
    })
}

fn read_identity(reader: &mut Reader) -> Result<NpcIdentity, DecodeError> {
    let hostile_response_threshold = reader.i64()?;
    let friendly_response_threshold = reader.i64()?;
    let aggression_range = reader.f64()?;
    let count = reader.u32()? as usize;
    // a weapon takes at least 100 bytes, so a count the frame cannot hold is refused before anything is allocated for it
    if count > reader.data.len() / 100 {
        return Err(DecodeError::Truncated);
    }
    let mut weapons = Vec::with_capacity(count);
    for _ in 0..count {
        weapons.push(read_weapon(reader)?);
    }
    let wreck = read_wreck_kind(reader)?;
    let hull = match reader.u8()? {
        0 => false,
        1 => true,
        _ => return Err(DecodeError::BadValue("hull")),
    };
    let host_wreck = match reader.u8()? {
        0 => false,
        1 => true,
        _ => return Err(DecodeError::BadValue("host wreck")),
    };
    Ok(NpcIdentity {
        hostile_response_threshold,
        friendly_response_threshold,
        combat: NpcCombat { weapons, aggression_range },
        wreck,
        hull,
        host_wreck,
        drone: None,
    })
}

/// A turret's numbers: the type of the weapon, the effect the client plays, the damage of a shot (em, thermal, kinetic, explosive), the range, the falloff,
/// the tracking and the signature resolution, then the charge it fires (a flag byte, and when set the type, the flag it sits at, the rounds left and the rounds a cycle takes), then the missile it
/// flies (a flag byte, and when set the type of the missile, its speed, its flight time in milliseconds, the explosion radius and velocity and the damage reduction factor and sensitivity).
fn put_turret(out: &mut Vec<u8>, turret: &Turret) {
    out.extend_from_slice(&turret.weapon_type_id.to_le_bytes());
    put_text(out, &turret.fx);
    for number in turret.raw_damage.iter().chain(&[
        turret.optimal_range,
        turret.falloff,
        turret.tracking_speed,
        turret.signature_resolution,
    ]) {
        out.extend_from_slice(&number.to_le_bytes());
    }
    match &turret.charge {
        None => out.push(0),
        Some(charge) => {
            out.push(1);
            out.extend_from_slice(&charge.type_id.to_le_bytes());
            out.extend_from_slice(&charge.flag.to_le_bytes());
            out.extend_from_slice(&charge.rounds.to_le_bytes());
            out.extend_from_slice(&charge.per_cycle.to_le_bytes());
        }
    }
    match &turret.missile {
        None => out.push(0),
        Some(missile) => {
            out.push(1);
            out.extend_from_slice(&missile.charge_type_id.to_le_bytes());
            out.extend_from_slice(&missile.group_id.to_le_bytes());
            put_text(out, &missile.name);
            for number in [
                missile.radius,
                missile.mass,
                missile.agility,
                missile.hit_points,
                missile.max_velocity,
                missile.flight_time_ms,
                missile.explosion_radius,
                missile.explosion_velocity,
                missile.damage_reduction_factor,
                missile.damage_reduction_sensitivity,
            ] {
                out.extend_from_slice(&number.to_le_bytes());
            }
        }
    }
    match &turret.spool {
        None => out.push(0),
        Some(spool) => {
            out.push(1);
            out.extend_from_slice(&spool.per_cycle.to_le_bytes());
            out.extend_from_slice(&spool.max.to_le_bytes());
        }
    }
    match &turret.aoe {
        None => out.push(0),
        Some(aoe) => {
            out.push(1);
            for number in [
                aoe.explosion_radius,
                aoe.explosion_velocity,
                aoe.damage_reduction_factor,
                aoe.damage_reduction_sensitivity,
            ] {
                out.extend_from_slice(&number.to_le_bytes());
            }
        }
    }
    match &turret.arc {
        None => out.push(0),
        Some(arc) => {
            out.push(1);
            out.extend_from_slice(&arc.range.to_le_bytes());
            out.extend_from_slice(&arc.targets.to_le_bytes());
        }
    }
}

fn put_effect(out: &mut Vec<u8>, effect: &Effect) {
    out.extend_from_slice(&effect.module_id.to_le_bytes());
    out.extend_from_slice(&effect.module_type.to_le_bytes());
    out.extend_from_slice(&effect.effect_id.to_le_bytes());
    put_text(out, &effect.fx);
    out.extend_from_slice(&effect.duration_ms.to_le_bytes());
    out.extend_from_slice(&effect.capacitor_need.to_le_bytes());
    out.extend_from_slice(&effect.repeat.to_le_bytes());
    out.extend_from_slice(&effect.target_id.to_le_bytes());
    out.push(
        u8::from(effect.on.is_some())
            | (u8::from(effect.off.is_some()) << 1)
            | (u8::from(effect.turret.is_some()) << 2)
            | (u8::from(effect.action.is_some()) << 3),
    );
    for numbers in [&effect.on, &effect.off].into_iter().flatten() {
        put_numbers(out, numbers);
    }
    put_changes(out, &effect.on_changes);
    put_changes(out, &effect.off_changes);
    if let Some(turret) = &effect.turret {
        put_turret(out, turret);
    }
    if let Some(action) = &effect.action {
        put_action(out, action);
    }
}

/// What a cycle of an effect does: a tag and its numbers (`0`: a repair of the ship's own: the layer, when in the cycle, the points; `1`: an effect
/// projected at the target: when in the cycle, the optimal range and the falloff, the name the target's HUD gives it (a text), then a kind and its numbers: `0` a repair of the target's layer and the
/// points, `1` a capacitor transfer and its GJ, `2` a neutralizer and `3` a nosferatu: the GJ and the signature resolution, `4` a web and `5` a target painter: a percentage, `6` a sensor dampener: the percentages of the lock range and of the scan resolution, `7` a warp scrambler: the points and whether a microwarpdrive is held too).
fn put_action(out: &mut Vec<u8>, action: &Action) {
    let layer_byte = |layer: &Layer| match layer {
        Layer::Shield => 0,
        Layer::Armor => 1,
        Layer::Structure => 2,
    };
    let when_byte = |when: &Applies| match when {
        Applies::CycleStart => 0,
        Applies::CycleEnd => 1,
    };
    match action {
        Action::Repair { layer, amount, when } => {
            out.push(0);
            out.push(layer_byte(layer));
            out.push(when_byte(when));
            out.extend_from_slice(&amount.to_le_bytes());
        }
        Action::Smartbomb { range, damage, hurts_pilots } => {
            out.push(2);
            out.extend_from_slice(&range.to_le_bytes());
            for number in damage {
                out.extend_from_slice(&number.to_le_bytes());
            }
            out.push(u8::from(*hurts_pilots));
        }
        Action::Charged { action, fallback, charge_type_id, charge_flag, charge_per_cycle, charges } => {
            out.push(4);
            out.extend_from_slice(&charge_type_id.to_le_bytes());
            out.extend_from_slice(&charge_flag.to_le_bytes());
            out.extend_from_slice(&charge_per_cycle.to_le_bytes());
            out.extend_from_slice(&charges.to_le_bytes());
            put_action(out, action);
            out.push(u8::from(fallback.is_some()));
            if let Some(fallback) = fallback {
                put_action(out, fallback);
            }
        }
        Action::EcmBurst { range, strength } => {
            out.push(3);
            out.extend_from_slice(&range.to_le_bytes());
            for number in strength {
                out.extend_from_slice(&number.to_le_bytes());
            }
        }
        Action::Cloak => out.push(5),
        Action::MicroJumpDrive { distance_meters, cooldown_ms } => {
            out.push(6);
            out.extend_from_slice(&distance_meters.to_le_bytes());
            out.extend_from_slice(&cooldown_ms.to_le_bytes());
        }
        Action::Remote { what, reach, when, jam } => {
            out.push(1);
            out.push(when_byte(when));
            out.extend_from_slice(&reach.optimal.to_le_bytes());
            out.extend_from_slice(&reach.falloff.to_le_bytes());
            put_text(out, jam);
            match what {
                Remote::Repair { layer, amount } => {
                    out.push(0);
                    out.push(layer_byte(layer));
                    out.extend_from_slice(&amount.to_le_bytes());
                }
                Remote::MutadaptiveRepair { layer, amount, bonus_per_cycle, bonus_max } => {
                    out.push(11);
                    out.push(layer_byte(layer));
                    out.extend_from_slice(&amount.to_le_bytes());
                    out.extend_from_slice(&bonus_per_cycle.to_le_bytes());
                    out.extend_from_slice(&bonus_max.to_le_bytes());
                }
                Remote::Transfer { amount } => {
                    out.push(1);
                    out.extend_from_slice(&amount.to_le_bytes());
                }
                Remote::Neutralize { amount, signature_resolution } => {
                    out.push(2);
                    out.extend_from_slice(&amount.to_le_bytes());
                    out.extend_from_slice(&signature_resolution.to_le_bytes());
                }
                Remote::Drain { amount, signature_resolution } => {
                    out.push(3);
                    out.extend_from_slice(&amount.to_le_bytes());
                    out.extend_from_slice(&signature_resolution.to_le_bytes());
                }
                Remote::Web { percent } => {
                    out.push(4);
                    out.extend_from_slice(&percent.to_le_bytes());
                }
                Remote::Paint { percent } => {
                    out.push(5);
                    out.extend_from_slice(&percent.to_le_bytes());
                }
                Remote::Damp {
                    range_percent,
                    resolution_percent,
                    gravimetric_percent,
                    ladar_percent,
                    magnetometric_percent,
                    radar_percent,
                } => {
                    out.push(6);
                    out.extend_from_slice(&range_percent.to_le_bytes());
                    out.extend_from_slice(&resolution_percent.to_le_bytes());
                    out.extend_from_slice(&gravimetric_percent.to_le_bytes());
                    out.extend_from_slice(&ladar_percent.to_le_bytes());
                    out.extend_from_slice(&magnetometric_percent.to_le_bytes());
                    out.extend_from_slice(&radar_percent.to_le_bytes());
                }
                Remote::Scramble { strength, blocks_mwd } => {
                    out.push(7);
                    out.extend_from_slice(&strength.to_le_bytes());
                    out.push(u8::from(*blocks_mwd));
                }
                Remote::Disrupt { range_percent, falloff_percent, tracking_percent } => {
                    out.push(9);
                    out.extend_from_slice(&range_percent.to_le_bytes());
                    out.extend_from_slice(&falloff_percent.to_le_bytes());
                    out.extend_from_slice(&tracking_percent.to_le_bytes());
                }
                Remote::GuidanceDisrupt {
                    velocity_percent,
                    delay_percent,
                    aoe_velocity_percent,
                    aoe_cloud_percent,
                } => {
                    out.push(10);
                    out.extend_from_slice(&velocity_percent.to_le_bytes());
                    out.extend_from_slice(&delay_percent.to_le_bytes());
                    out.extend_from_slice(&aoe_velocity_percent.to_le_bytes());
                    out.extend_from_slice(&aoe_cloud_percent.to_le_bytes());
                }
                Remote::Jam { strength } => {
                    out.push(8);
                    for number in strength {
                        out.extend_from_slice(&number.to_le_bytes());
                    }
                }
            }
        }
    }
}

fn read_layer(reader: &mut Reader) -> Result<Layer, DecodeError> {
    match reader.u8()? {
        0 => Ok(Layer::Shield),
        1 => Ok(Layer::Armor),
        2 => Ok(Layer::Structure),
        _ => Err(DecodeError::BadValue("layer")),
    }
}

fn read_applies(reader: &mut Reader) -> Result<Applies, DecodeError> {
    match reader.u8()? {
        0 => Ok(Applies::CycleStart),
        1 => Ok(Applies::CycleEnd),
        _ => Err(DecodeError::BadValue("applies")),
    }
}

fn read_action(reader: &mut Reader) -> Result<Action, DecodeError> {
    match reader.u8()? {
        0 => {
            let layer = read_layer(reader)?;
            let when = read_applies(reader)?;
            Ok(Action::Repair { layer, when, amount: reader.f64()? })
        }
        1 => {
            let when = read_applies(reader)?;
            let reach = Reach { optimal: reader.f64()?, falloff: reader.f64()? };
            let jam = reader.text()?;
            let what = match reader.u8()? {
                0 => {
                    let layer = read_layer(reader)?;
                    Remote::Repair { layer, amount: reader.f64()? }
                }
                11 => {
                    let layer = read_layer(reader)?;
                    let amount = reader.f64()?;
                    let bonus_per_cycle = reader.f64()?;
                    let bonus_max = reader.f64()?;
                    if !amount.is_finite() || amount < 0.0 || !bonus_per_cycle.is_finite() || bonus_per_cycle < 0.0 || !bonus_max.is_finite() || bonus_max < 0.0 {
                        return Err(DecodeError::BadValue("mutadaptive repair"));
                    }
                    Remote::MutadaptiveRepair { layer, amount, bonus_per_cycle, bonus_max }
                }
                1 => Remote::Transfer { amount: reader.f64()? },
                2 => Remote::Neutralize { amount: reader.f64()?, signature_resolution: reader.f64()? },
                3 => Remote::Drain { amount: reader.f64()?, signature_resolution: reader.f64()? },
                4 => Remote::Web { percent: reader.f64()? },
                5 => Remote::Paint { percent: reader.f64()? },
                6 => Remote::Damp {
                    range_percent: reader.f64()?,
                    resolution_percent: reader.f64()?,
                    gravimetric_percent: reader.f64()?,
                    ladar_percent: reader.f64()?,
                    magnetometric_percent: reader.f64()?,
                    radar_percent: reader.f64()?,
                },
                7 => Remote::Scramble {
                    strength: reader.f64()?,
                    blocks_mwd: match reader.u8()? {
                        0 => false,
                        1 => true,
                        _ => return Err(DecodeError::BadValue("blocks_mwd")),
                    },
                },
                9 => Remote::Disrupt {
                    range_percent: reader.f64()?,
                    falloff_percent: reader.f64()?,
                    tracking_percent: reader.f64()?,
                },
                10 => Remote::GuidanceDisrupt {
                    velocity_percent: reader.f64()?,
                    delay_percent: reader.f64()?,
                    aoe_velocity_percent: reader.f64()?,
                    aoe_cloud_percent: reader.f64()?,
                },
                8 => Remote::Jam { strength: [reader.f64()?, reader.f64()?, reader.f64()?, reader.f64()?] },
                _ => return Err(DecodeError::BadValue("remote")),
            };
            if matches!(&what, Remote::MutadaptiveRepair { .. }) && when != Applies::CycleEnd {
                return Err(DecodeError::BadValue("mutadaptive repair timing"));
            }
            Ok(Action::Remote { what, reach, when, jam })
        }
        2 => {
            let range = reader.f64()?;
            let damage = [reader.f64()?, reader.f64()?, reader.f64()?, reader.f64()?];
            let hurts_pilots = match reader.u8()? {
                0 => false,
                1 => true,
                _ => return Err(DecodeError::BadValue("hurts_pilots")),
            };
            Ok(Action::Smartbomb { range, damage, hurts_pilots })
        }
        3 => Ok(Action::EcmBurst {
            range: reader.f64()?,
            strength: [reader.f64()?, reader.f64()?, reader.f64()?, reader.f64()?],
        }),
        5 => Ok(Action::Cloak),
        6 => {
            let distance_meters = reader.f64()?;
            let cooldown_ms = reader.i64()?;
            if !distance_meters.is_finite() || distance_meters <= 0.0 || cooldown_ms <= 0 {
                return Err(DecodeError::BadValue("micro jump drive"));
            }
            Ok(Action::MicroJumpDrive { distance_meters, cooldown_ms })
        }
        4 => {
            let charge_type_id = reader.i32()?;
            let charge_flag = reader.i32()?;
            let charge_per_cycle = reader.i64()?;
            let charges = reader.i64()?;
            if charge_per_cycle <= 0 || charges < 0 {
                return Err(DecodeError::BadValue("charged action"));
            }
            let action = Box::new(read_action(reader)?);
            let fallback = match reader.u8()? {
                0 => None,
                1 => Some(Box::new(read_action(reader)?)),
                _ => return Err(DecodeError::BadValue("charged action fallback")),
            };
            Ok(Action::Charged { action, fallback, charge_type_id, charge_flag, charge_per_cycle, charges })
        }
        _ => Err(DecodeError::BadValue("action")),
    }
}

fn read_numbers(reader: &mut Reader) -> Result<Numbers, DecodeError> {
    Ok(Numbers { mass: reader.f64()?, max_velocity: reader.f64()?, signature_radius: reader.f64()? })
}

fn read_changes(reader: &mut Reader) -> Result<Vec<Change>, DecodeError> {
    let count = reader.u32()? as usize;
    // a change takes 20 bytes, so a count the frame cannot hold is refused before anything is allocated for it
    if count > reader.data.len() / 20 {
        return Err(DecodeError::Truncated);
    }
    let mut changes = Vec::with_capacity(count);
    for _ in 0..count {
        changes.push(Change { item_id: reader.i64()?, attribute_id: reader.i32()?, value: reader.f64()? });
    }
    Ok(changes)
}

fn read_effect(reader: &mut Reader) -> Result<Effect, DecodeError> {
    let module_id = reader.i64()?;
    let module_type = reader.i32()?;
    let effect_id = reader.i32()?;
    let fx = reader.text()?;
    let duration_ms = reader.f64()?;
    let capacitor_need = reader.f64()?;
    let repeat = reader.i32()?;
    let target_id = reader.i64()?;
    let flags = reader.u8()?;
    if flags > 15 {
        return Err(DecodeError::BadValue("flags"));
    }
    let on = if flags & 1 != 0 { Some(read_numbers(reader)?) } else { None };
    let off = if flags & 2 != 0 { Some(read_numbers(reader)?) } else { None };
    let on_changes = read_changes(reader)?;
    let off_changes = read_changes(reader)?;
    let turret = if flags & 4 != 0 { Some(read_turret(reader)?) } else { None };
    let action = if flags & 8 != 0 { Some(read_action(reader)?) } else { None };
    Ok(Effect {
        module_id,
        module_type,
        effect_id,
        fx,
        duration_ms,
        capacitor_need,
        repeat,
        target_id,
        turret,
        action,
        on,
        off,
        on_changes,
        off_changes,
    })
}

fn read_condition(reader: &mut Reader) -> Result<Condition, DecodeError> {
    Ok(Condition {
        damage: reader.f64()?,
        armor_damage: reader.f64()?,
        shield_charge: reader.f64()?,
        charge: reader.f64()?,
    })
}

fn read_damage(reader: &mut Reader) -> Result<[f64; 4], DecodeError> {
    Ok([reader.f64()?, reader.f64()?, reader.f64()?, reader.f64()?])
}

fn read_vitals(reader: &mut Reader) -> Result<Vitals, DecodeError> {
    Ok(Vitals {
        shield_capacity: reader.f64()?,
        armor_hp: reader.f64()?,
        structure_hp: reader.f64()?,
        resonances: Resonances {
            shield: read_damage(reader)?,
            armor: read_damage(reader)?,
            structure: read_damage(reader)?,
        },
    })
}

fn read_heat_profile(reader: &mut Reader) -> Result<HeatProfile, DecodeError> {
    let generation_multiplier = reader.f64()?;
    let damage_multiplier = reader.f64()?;
    let thermodynamics_level = reader.i32()?;
    if !(0..=5).contains(&thermodynamics_level) {
        return Err(DecodeError::BadValue("thermodynamics_level"));
    }
    let capacity = [reader.f64()?, reader.f64()?, reader.f64()?];
    let initial_rack_heat = [reader.f64()?, reader.f64()?, reader.f64()?];
    let dissipation_rate = [reader.f64()?, reader.f64()?, reader.f64()?];
    let attenuation = [reader.f64()?, reader.f64()?, reader.f64()?];
    let count = reader.i32()?;
    if !(0..=256).contains(&count) {
        return Err(DecodeError::BadValue("heat modules"));
    }
    let mut modules = Vec::with_capacity(count as usize);
    for _ in 0..count {
        let module_id = reader.i64()?;
        let flag = reader.i32()?;
        let structure_hp = reader.f64()?;
        let damage_hp = reader.f64()?;
        let heat_damage_hp = reader.f64()?;
        let heat_absorption_rate = reader.f64()?;
        let overloadable = reader.u8()?;
        let heat_immune = reader.u8()?;
        let online = reader.u8()?;
        if overloadable > 1 || heat_immune > 1 || online > 1 {
            return Err(DecodeError::BadValue("heat module flag"));
        }
        modules.push(HeatModuleProfile {
            module_id,
            flag,
            structure_hp,
            damage_hp,
            heat_damage_hp,
            heat_absorption_rate,
            overloadable: overloadable == 1,
            heat_immune: heat_immune == 1,
            online: online == 1,
        });
    }
    Ok(HeatProfile {
        generation_multiplier,
        damage_multiplier,
        thermodynamics_level: thermodynamics_level as u8,
        capacity,
        initial_rack_heat,
        dissipation_rate,
        attenuation,
        modules,
    })
}

fn read_ship(reader: &mut Reader) -> Result<ShipSpec, DecodeError> {
    let ship_id = reader.i64()?;
    let character_id = reader.i64()?;
    let type_id = reader.i32()?;
    let group_id = reader.i32()?;
    let category_id = reader.i32()?;
    let name = reader.text()?;
    let owner_id = reader.i64()?;
    let corporation_id = reader.i32()?;
    let alliance_id = reader.i32()?;
    let war_faction_id = reader.i32()?;
    let [
        security_status,
        radius,
        mass,
        max_velocity,
        agility,
        warp_speed_au,
        shield_recharge_ms,
        max_target_range,
        scan_resolution,
        signature_radius,
        capacitor_capacity,
        capacitor_recharge_s,
    ] = [
        reader.f64()?,
        reader.f64()?,
        reader.f64()?,
        reader.f64()?,
        reader.f64()?,
        reader.f64()?,
        reader.f64()?,
        reader.f64()?,
        reader.f64()?,
        reader.f64()?,
        reader.f64()?,
        reader.f64()?,
    ];
    let max_locked_targets = reader.i32()?;
    let vitals = read_vitals(reader)?;
    let condition = read_condition(reader)?;
    let count = reader.u32()? as usize;
    // a module takes 16 bytes, so a count the frame cannot hold is refused before anything is allocated for it
    if count > reader.data.len() / 16 {
        return Err(DecodeError::Truncated);
    }
    let mut modules = Vec::with_capacity(count);
    for _ in 0..count {
        modules.push(ModuleSpec { item_id: reader.i64()?, type_id: reader.i32()?, flag: reader.i32()? });
    }
    let sensor_strength = [reader.f64()?, reader.f64()?, reader.f64()?, reader.f64()?];
    let ecm_resistance = reader.f64()?;
    Ok(ShipSpec {
        ship_id,
        character_id,
        type_id,
        group_id,
        category_id,
        name,
        owner_id,
        corporation_id,
        alliance_id,
        war_faction_id,
        security_status,
        radius,
        mass,
        max_velocity,
        agility,
        warp_speed_au,
        shield_recharge_ms,
        max_target_range,
        max_locked_targets,
        scan_resolution,
        signature_radius,
        capacitor_capacity,
        capacitor_recharge_s,
        vitals,
        condition,
        modules,
        sensor_strength,
        ecm_resistance,
    })
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
            Self::Measured { .. } => kind::MEASURED,
            Self::AdjustCharge { .. } => kind::ADJUST_CHARGE,
            Self::ChargeAdjusted { .. } => kind::CHARGE_ADJUSTED,
        }
    }

    /// The frame's payload: the kind and the body, without the length prefix.
    pub fn payload(&self) -> Vec<u8> {
        let mut out = vec![self.kind()];
        match self {
            Self::Hello { version } => out.extend_from_slice(&version.to_le_bytes()),
            Self::HelloAck { version, poll_hz } => {
                out.extend_from_slice(&version.to_le_bytes());
                out.extend_from_slice(&poll_hz.to_le_bytes());
            }
            Self::Enter(enter) => {
                out.extend_from_slice(&enter.session_id.to_le_bytes());
                out.extend_from_slice(&enter.solar_system_id.to_le_bytes());
                out.push(match enter.how {
                    How::Undock => 0,
                    How::InSpace => 1,
                });
                out.extend_from_slice(&enter.station_id.to_le_bytes());
                put_ship(&mut out, &enter.ship);
            }
            Self::EnterReply { session_id, outcome } => {
                out.extend_from_slice(&session_id.to_le_bytes());
                match outcome {
                    Ok(()) => {
                        out.push(0);
                        put_text(&mut out, "");
                    }
                    Err(reason) => {
                        out.push(1);
                        put_text(&mut out, reason);
                    }
                }
            }
            Self::Leave { session_id, why } => {
                out.extend_from_slice(&session_id.to_le_bytes());
                out.push(*why);
            }
            Self::Bind { session_id } | Self::UpdateState { session_id } => {
                out.extend_from_slice(&session_id.to_le_bytes())
            }
            Self::Command { session_id, command, args } => {
                out.extend_from_slice(&session_id.to_le_bytes());
                out.push(*command);
                out.push(args.len() as u8);
                for arg in args {
                    out.extend_from_slice(&arg.to_le_bytes());
                }
            }
            Self::ShipSpec { session_id, ship } => {
                out.extend_from_slice(&session_id.to_le_bytes());
                put_ship(&mut out, ship);
            }
            Self::HeatProfile { session_id, profile } => {
                out.extend_from_slice(&session_id.to_le_bytes());
                put_heat_profile(&mut out, profile);
            }
            Self::ModuleOverheat { session_id, module_id, active } => {
                out.extend_from_slice(&session_id.to_le_bytes());
                out.extend_from_slice(&module_id.to_le_bytes());
                out.push(u8::from(*active));
            }
            Self::ModuleOffline { session_id, module_id } => {
                out.extend_from_slice(&session_id.to_le_bytes());
                out.extend_from_slice(&module_id.to_le_bytes());
            }
            Self::RackOverheat { session_id, rack, active } => {
                out.extend_from_slice(&session_id.to_le_bytes());
                out.push(*rack as u8);
                out.push(u8::from(*active));
            }
            Self::Notify { session_id, name, id_type, args } => {
                out.extend_from_slice(&session_id.to_le_bytes());
                put_text(&mut out, name);
                put_text(&mut out, id_type);
                put_bytes(&mut out, args);
            }
            Self::Docked { session_id, station_id, condition } => {
                out.extend_from_slice(&session_id.to_le_bytes());
                out.extend_from_slice(&station_id.to_le_bytes());
                put_condition(&mut out, condition);
            }
            Self::Arrived { session_id, belt_id, position } => {
                out.extend_from_slice(&session_id.to_le_bytes());
                out.extend_from_slice(&belt_id.to_le_bytes());
                put_vector(&mut out, position);
            }
            Self::Condition { session_id, command, condition } => {
                out.extend_from_slice(&session_id.to_le_bytes());
                out.push(*command);
                put_condition(&mut out, condition);
            }
            Self::Refused { session_id, command, reason } => {
                out.extend_from_slice(&session_id.to_le_bytes());
                out.push(*command);
                put_text(&mut out, reason);
            }
            Self::Accepted { session_id, command, at } => {
                out.extend_from_slice(&session_id.to_le_bytes());
                out.push(*command);
                out.extend_from_slice(&at.to_le_bytes());
            }
            Self::Position { session_id, command, position } => {
                out.extend_from_slice(&session_id.to_le_bytes());
                out.push(*command);
                put_vector(&mut out, position);
            }
            Self::Approaching { session_id, command, distance } => {
                out.extend_from_slice(&session_id.to_le_bytes());
                out.push(*command);
                out.extend_from_slice(&distance.to_le_bytes());
            }
            Self::Jumping { session_id, command, destination_system, destination_gate } => {
                out.extend_from_slice(&session_id.to_le_bytes());
                out.push(*command);
                out.extend_from_slice(&destination_system.to_le_bytes());
                out.extend_from_slice(&destination_gate.to_le_bytes());
            }
            Self::Jumped { session_id, from_system, from_gate, to_system, to_gate } => {
                out.extend_from_slice(&session_id.to_le_bytes());
                out.extend_from_slice(&from_system.to_le_bytes());
                out.extend_from_slice(&from_gate.to_le_bytes());
                out.extend_from_slice(&to_system.to_le_bytes());
                out.extend_from_slice(&to_gate.to_le_bytes());
            }
            Self::Targets { session_id, command, pending, targets } => {
                out.extend_from_slice(&session_id.to_le_bytes());
                out.push(*command);
                out.push(u8::from(*pending));
                out.extend_from_slice(&(targets.len() as u32).to_le_bytes());
                for target in targets {
                    out.extend_from_slice(&target.to_le_bytes());
                }
            }
            Self::Activate { session_id, effect } => {
                out.extend_from_slice(&session_id.to_le_bytes());
                put_effect(&mut out, effect);
            }
            Self::Destroyed {
                session_id,
                ship_id,
                wreck_id,
                position,
                direction,
                radius,
                killer_ship_id,
                wreck_kind,
            } => {
                out.extend_from_slice(&session_id.to_le_bytes());
                out.extend_from_slice(&ship_id.to_le_bytes());
                out.extend_from_slice(&wreck_id.to_le_bytes());
                for number in position.iter().chain(direction).chain([radius]) {
                    out.extend_from_slice(&number.to_le_bytes());
                }
                out.extend_from_slice(&killer_ship_id.to_le_bytes());
                put_wreck_kind(&mut out, wreck_kind.as_ref());
            }
            Self::ChargeSpent { session_id, module_id, rounds_left } => {
                out.extend_from_slice(&session_id.to_le_bytes());
                out.extend_from_slice(&module_id.to_le_bytes());
                out.extend_from_slice(&rounds_left.to_le_bytes());
            }
            Self::PlaceContainer(can) => {
                out.extend_from_slice(&can.session_id.to_le_bytes());
                out.extend_from_slice(&can.container_id.to_le_bytes());
                put_wreck_kind(&mut out, Some(&can.kind));
                out.extend_from_slice(&can.radius.to_le_bytes());
                out.extend_from_slice(&can.launcher_id.to_le_bytes());
                put_credit(&mut out, &can.credit);
                out.extend_from_slice(&can.distance.to_le_bytes());
            }
            Self::ContainerPlaced { session_id, container_id, position, direction } => {
                out.extend_from_slice(&session_id.to_le_bytes());
                out.extend_from_slice(&container_id.to_le_bytes());
                put_vector(&mut out, position);
                put_vector(&mut out, direction);
            }
            Self::ChangeWreck { session_id, wreck_id, is_empty } => {
                out.extend_from_slice(&session_id.to_le_bytes());
                out.extend_from_slice(&wreck_id.to_le_bytes());
                out.push(u8::from(*is_empty));
            }
            Self::RemoveWreck { wreck_id, effect } => {
                out.extend_from_slice(&wreck_id.to_le_bytes());
                out.extend_from_slice(&effect.to_le_bytes());
            }
            Self::DroneWorkResult { session_id, drone_id, target_id, cycle_id, accepted, reason, units_remaining, delivered_units, complete } => {
                out.extend_from_slice(&session_id.to_le_bytes());
                out.extend_from_slice(&drone_id.to_le_bytes());
                out.extend_from_slice(&target_id.to_le_bytes());
                out.extend_from_slice(&cycle_id.to_le_bytes());
                out.push(u8::from(*accepted));
                put_text(&mut out, reason);
                out.extend_from_slice(&units_remaining.to_le_bytes());
                out.extend_from_slice(&delivered_units.to_le_bytes());
                out.push(u8::from(*complete));
            }
            Self::RemoteRepairCycleResult { session_id, module_id, target_id, cycle_id, accepted, reason, amount } => {
                out.extend_from_slice(&session_id.to_le_bytes());
                out.extend_from_slice(&module_id.to_le_bytes());
                out.extend_from_slice(&target_id.to_le_bytes());
                out.extend_from_slice(&cycle_id.to_le_bytes());
                out.push(u8::from(*accepted));
                put_text(&mut out, reason);
                out.extend_from_slice(&amount.to_le_bytes());
            }
            Self::Measured { session_id, command, distance } => {
                out.extend_from_slice(&session_id.to_le_bytes());
                out.push(*command);
                match distance {
                    Some(distance) => {
                        out.push(1);
                        out.extend_from_slice(&distance.to_le_bytes());
                    }
                    None => out.push(0),
                }
            }
            Self::AdjustCharge { request_id, session_id, module_id, charge_type, op } => {
                out.extend_from_slice(&request_id.to_le_bytes());
                out.extend_from_slice(&session_id.to_le_bytes());
                out.extend_from_slice(&module_id.to_le_bytes());
                out.extend_from_slice(&charge_type.to_le_bytes());
                let (code, first, second) = match *op {
                    ChargeOp::Add { capacity, available } => (0u8, capacity, available),
                    ChargeOp::Remove { quantity } => (1u8, quantity, 0),
                };
                out.push(code);
                out.extend_from_slice(&first.to_le_bytes());
                out.extend_from_slice(&second.to_le_bytes());
            }
            Self::ChargeAdjusted { request_id, running, before, after } => {
                out.extend_from_slice(&request_id.to_le_bytes());
                out.push(u8::from(*running));
                out.extend_from_slice(&before.to_le_bytes());
                out.extend_from_slice(&after.to_le_bytes());
            }
            Self::Respawn { session_id, ship, position, direction } => {
                out.extend_from_slice(&session_id.to_le_bytes());
                put_ship(&mut out, ship);
                put_vector(&mut out, position);
                put_vector(&mut out, direction);
            }
            Self::PlaceWreck(placement) => {
                out.extend_from_slice(&placement.solar_system_id.to_le_bytes());
                out.extend_from_slice(&placement.wreck_id.to_le_bytes());
                out.extend_from_slice(&placement.group_id.to_le_bytes());
                put_wreck_kind(&mut out, Some(&placement.kind));
                put_vector(&mut out, &placement.position);
                put_vector(&mut out, &placement.direction);
                out.extend_from_slice(&placement.radius.to_le_bytes());
                out.extend_from_slice(&placement.launcher_id.to_le_bytes());
                put_credit(&mut out, &placement.credit);
                out.push(u8::from(placement.is_empty));
            }
            Self::PlaceAsteroid(asteroid) => {
                out.extend_from_slice(&asteroid.solar_system_id.to_le_bytes());
                out.extend_from_slice(&asteroid.asteroid_id.to_le_bytes());
                out.extend_from_slice(&asteroid.type_id.to_le_bytes());
                out.extend_from_slice(&asteroid.group_id.to_le_bytes());
                out.extend_from_slice(&asteroid.category_id.to_le_bytes());
                put_text(&mut out, &asteroid.name);
                match asteroid.graphic_id {
                    Some(id) => { out.push(1); out.extend_from_slice(&id.to_le_bytes()); }
                    None => out.push(0),
                }
                put_vector(&mut out, &asteroid.position);
                put_vector(&mut out, &asteroid.direction);
                out.extend_from_slice(&asteroid.radius.to_le_bytes());
                out.extend_from_slice(&asteroid.units_remaining.to_le_bytes());
            }
            Self::Spawn(spawn) => {
                out.extend_from_slice(&spawn.request_id.to_le_bytes());
                out.extend_from_slice(&spawn.solar_system_id.to_le_bytes());
                put_ship(&mut out, &spawn.ship);
                out.extend_from_slice(&spawn.anchor_ship_id.to_le_bytes());
                for number in spawn.position.iter().chain(&spawn.velocity).chain(&spawn.direction) {
                    out.extend_from_slice(&number.to_le_bytes());
                }
                put_text(&mut out, &spawn.behavior);
                put_identity(&mut out, &spawn.identity);
            }
            Self::SpawnReply { request_id, outcome } => {
                out.extend_from_slice(&request_id.to_le_bytes());
                match outcome {
                    Ok(session) => {
                        out.push(0);
                        out.extend_from_slice(&session.to_le_bytes());
                    }
                    Err(reason) => {
                        out.push(1);
                        put_text(&mut out, reason);
                    }
                }
            }
            Self::LaunchDrone(launch) => {
                out.extend_from_slice(&launch.request_id.to_le_bytes());
                out.extend_from_slice(&launch.session_id.to_le_bytes());
                put_ship(&mut out, &launch.ship);
                match &launch.weapon {
                    Some(weapon) => put_weapon(&mut out, weapon),
                    None => put_weapon(&mut out, &NpcWeapon {
                        module_id: 0,
                        effect_id: 0,
                        cycle_ms: 0.0,
                        capacitor_need: 0.0,
                        turret: Turret {
                            weapon_type_id: 0,
                            fx: String::new(),
                            raw_damage: [0.0; 4],
                            optimal_range: 0.0,
                            falloff: 0.0,
                            tracking_speed: 0.0,
                            signature_resolution: 0.0,
                            charge: None,
                            missile: None,
                            spool: None,
                            aoe: None,
                            arc: None,
                        },
                    }),
                }
                out.extend_from_slice(&launch.graphic_id.to_le_bytes());
                out.extend_from_slice(&launch.orbit_distance.to_le_bytes());
                out.extend_from_slice(&launch.attack_range.to_le_bytes());
                out.extend_from_slice(&launch.cruise_fraction.to_le_bytes());
                out.extend_from_slice(&launch.mining_amount_m3.to_le_bytes());
                out.extend_from_slice(&launch.work_cycle_ms.to_le_bytes());
                out.extend_from_slice(&launch.mining_waste_multiplier.to_le_bytes());
                out.extend_from_slice(&launch.mining_waste_probability.to_le_bytes());
                out.extend_from_slice(&launch.salvage_access_bonus.to_le_bytes());
            }
            Self::DroneCommand { session_id, command, target_id, drone_ids, control_range } => {
                out.extend_from_slice(&session_id.to_le_bytes());
                out.push(*command);
                out.extend_from_slice(&target_id.to_le_bytes());
                out.extend_from_slice(&(drone_ids.len() as u32).to_le_bytes());
                for drone in drone_ids {
                    out.extend_from_slice(&drone.to_le_bytes());
                }
                out.extend_from_slice(&control_range.to_le_bytes());
            }
            Self::DroneResults { session_id, command, results } => {
                out.extend_from_slice(&session_id.to_le_bytes());
                out.push(*command);
                out.extend_from_slice(&(results.len() as u32).to_le_bytes());
                for (drone, reason) in results {
                    out.extend_from_slice(&drone.to_le_bytes());
                    put_text(&mut out, reason);
                }
            }
            Self::DroneScooped { session_id, drone_id } => {
                out.extend_from_slice(&session_id.to_le_bytes());
                out.extend_from_slice(&drone_id.to_le_bytes());
            }
            Self::DroneReleased { session_id, drone_id, position } => {
                out.extend_from_slice(&session_id.to_le_bytes());
                out.extend_from_slice(&drone_id.to_le_bytes());
                put_vector(&mut out, position);
            }
            Self::DroneWorkCycle { session_id, character_id, ship_id, solar_system_id, drone_id, target_id, cycle_id, command, mining_amount_m3, waste_multiplier, waste_probability, access_difficulty_bonus, roll } => {
                out.extend_from_slice(&session_id.to_le_bytes());
                out.extend_from_slice(&character_id.to_le_bytes());
                out.extend_from_slice(&ship_id.to_le_bytes());
                out.extend_from_slice(&solar_system_id.to_le_bytes());
                out.extend_from_slice(&drone_id.to_le_bytes());
                out.extend_from_slice(&target_id.to_le_bytes());
                out.extend_from_slice(&cycle_id.to_le_bytes());
                out.push(*command);
                out.extend_from_slice(&mining_amount_m3.to_le_bytes());
                out.extend_from_slice(&waste_multiplier.to_le_bytes());
                out.extend_from_slice(&waste_probability.to_le_bytes());
                out.extend_from_slice(&access_difficulty_bonus.to_le_bytes());
                out.extend_from_slice(&roll.to_le_bytes());
            }
            Self::RemoteRepairCycle { session_id, module_id, target_id, cycle_id, cycle_number, cycle_at_ms, cycle_duration_ms, ends_effect_after_cycle, base_amount, bonus_per_cycle, bonus_max } => {
                out.extend_from_slice(&session_id.to_le_bytes());
                out.extend_from_slice(&module_id.to_le_bytes());
                out.extend_from_slice(&target_id.to_le_bytes());
                out.extend_from_slice(&cycle_id.to_le_bytes());
                out.extend_from_slice(&cycle_number.to_le_bytes());
                out.extend_from_slice(&cycle_at_ms.to_le_bytes());
                out.extend_from_slice(&cycle_duration_ms.to_le_bytes());
                out.push(u8::from(*ends_effect_after_cycle));
                out.extend_from_slice(&base_amount.to_le_bytes());
                out.extend_from_slice(&bonus_per_cycle.to_le_bytes());
                out.extend_from_slice(&bonus_max.to_le_bytes());
            }
            Self::Destroy { ship_id, credit } => {
                out.extend_from_slice(&ship_id.to_le_bytes());
                put_credit(&mut out, credit);
            }
            Self::TargetRefused {
                session_id,
                command,
                reason,
                target_type,
                desired_range,
                actual_distance,
            } => {
                out.extend_from_slice(&session_id.to_le_bytes());
                out.push(*command);
                put_text(&mut out, reason);
                out.extend_from_slice(&target_type.to_le_bytes());
                out.extend_from_slice(&desired_range.to_le_bytes());
                out.extend_from_slice(&actual_distance.to_le_bytes());
            }
        }
        out
    }

    /// The whole frame, length prefix included.
    pub fn encode(&self) -> Vec<u8> {
        let payload = self.payload();
        let mut frame = Vec::with_capacity(4 + payload.len());
        frame.extend_from_slice(&(payload.len() as u32).to_le_bytes());
        frame.extend_from_slice(&payload);
        frame
    }

    /// Reads a frame's payload.
    pub fn decode(payload: &[u8]) -> Result<Message, DecodeError> {
        let (&kind, body) = payload.split_first().ok_or(DecodeError::Empty)?;
        let mut r = Reader { data: body };
        let message = match kind {
            kind::HELLO => Message::Hello { version: r.u16()? },
            kind::HELLO_ACK => Message::HelloAck { version: r.u16()?, poll_hz: r.u32()? },
            kind::ENTER => {
                let session_id = r.u64()?;
                let solar_system_id = r.i32()?;
                let how = match r.u8()? {
                    0 => How::Undock,
                    1 => How::InSpace,
                    _ => return Err(DecodeError::BadValue("how")),
                };
                let station_id = r.i64()?;
                let ship = read_ship(&mut r)?;
                Message::Enter(Box::new(Enter { session_id, solar_system_id, how, station_id, ship }))
            }
            kind::ENTER_REPLY => {
                let session_id = r.u64()?;
                let status = r.u8()?;
                let reason = r.text()?;
                let outcome = match status {
                    0 => Ok(()),
                    1 => Err(reason),
                    _ => return Err(DecodeError::BadValue("status")),
                };
                Message::EnterReply { session_id, outcome }
            }
            kind::LEAVE => Message::Leave { session_id: r.u64()?, why: r.u8()? },
            kind::BIND => Message::Bind { session_id: r.u64()? },
            kind::UPDATE_STATE => Message::UpdateState { session_id: r.u64()? },
            kind::COMMAND => {
                let session_id = r.u64()?;
                let command = r.u8()?;
                let count = r.u8()? as usize;
                let mut args = Vec::with_capacity(count);
                for _ in 0..count {
                    args.push(r.f64()?);
                }
                Message::Command { session_id, command, args }
            }
            kind::SHIP_SPEC => Message::ShipSpec { session_id: r.u64()?, ship: Box::new(read_ship(&mut r)?) },
            kind::HEAT_PROFILE => {
                Message::HeatProfile { session_id: r.u64()?, profile: read_heat_profile(&mut r)? }
            }
            kind::MODULE_OVERHEAT => {
                let session_id = r.u64()?;
                let module_id = r.i64()?;
                let active = match r.u8()? {
                    0 => false,
                    1 => true,
                    _ => return Err(DecodeError::BadValue("module overheat")),
                };
                Message::ModuleOverheat { session_id, module_id, active }
            }
            kind::MODULE_OFFLINE => Message::ModuleOffline { session_id: r.u64()?, module_id: r.i64()? },
            kind::RACK_OVERHEAT => {
                let session_id = r.u64()?;
                let rack = match r.u8()? {
                    0 => HeatRack::High,
                    1 => HeatRack::Medium,
                    2 => HeatRack::Low,
                    _ => return Err(DecodeError::BadValue("heat rack")),
                };
                let active = match r.u8()? {
                    0 => false,
                    1 => true,
                    _ => return Err(DecodeError::BadValue("rack overheat")),
                };
                Message::RackOverheat { session_id, rack, active }
            }
            kind::NOTIFY => Message::Notify {
                session_id: r.u64()?,
                name: r.text()?,
                id_type: r.text()?,
                args: r.bytes()?,
            },
            kind::DOCKED => Message::Docked {
                session_id: r.u64()?,
                station_id: r.i64()?,
                condition: read_condition(&mut r)?,
            },
            kind::ARRIVED => {
                Message::Arrived { session_id: r.u64()?, belt_id: r.i64()?, position: read_vector(&mut r)? }
            }
            kind::CONDITION => Message::Condition {
                session_id: r.u64()?,
                command: r.u8()?,
                condition: read_condition(&mut r)?,
            },
            kind::REFUSED => Message::Refused { session_id: r.u64()?, command: r.u8()?, reason: r.text()? },
            kind::ACCEPTED => Message::Accepted { session_id: r.u64()?, command: r.u8()?, at: r.i64()? },
            kind::POSITION => Message::Position { session_id: r.u64()?, command: r.u8()?, position: read_vector(&mut r)? },
            kind::APPROACHING => {
                Message::Approaching { session_id: r.u64()?, command: r.u8()?, distance: r.f64()? }
            }
            kind::JUMPING => Message::Jumping {
                session_id: r.u64()?,
                command: r.u8()?,
                destination_system: r.i32()?,
                destination_gate: r.i64()?,
            },
            kind::JUMPED => Message::Jumped {
                session_id: r.u64()?,
                from_system: r.i32()?,
                from_gate: r.i64()?,
                to_system: r.i32()?,
                to_gate: r.i64()?,
            },
            kind::TARGETS => {
                let session_id = r.u64()?;
                let command = r.u8()?;
                let pending = match r.u8()? {
                    0 => false,
                    1 => true,
                    _ => return Err(DecodeError::BadValue("pending")),
                };
                let count = r.u32()? as usize;
                // a target takes 8 bytes: a count the frame cannot hold is refused before anything is allocated for it
                if count > r.data.len() / 8 {
                    return Err(DecodeError::Truncated);
                }
                let mut targets = Vec::with_capacity(count);
                for _ in 0..count {
                    targets.push(r.i64()?);
                }
                Message::Targets { session_id, command, pending, targets }
            }
            kind::ACTIVATE => {
                Message::Activate { session_id: r.u64()?, effect: Box::new(read_effect(&mut r)?) }
            }
            kind::DESTROYED => Message::Destroyed {
                session_id: r.u64()?,
                ship_id: r.i64()?,
                wreck_id: r.i64()?,
                position: [r.f64()?, r.f64()?, r.f64()?],
                direction: [r.f64()?, r.f64()?, r.f64()?],
                radius: r.f64()?,
                killer_ship_id: r.i64()?,
                wreck_kind: read_wreck_kind(&mut r)?,
            },
            kind::CHARGE_SPENT => {
                Message::ChargeSpent { session_id: r.u64()?, module_id: r.i64()?, rounds_left: r.i64()? }
            }
            kind::PLACE_CONTAINER => {
                let session_id = r.u64()?;
                let container_id = r.i64()?;
                let Some(kind) = read_wreck_kind(&mut r)? else {
                    return Err(DecodeError::BadValue("container"));
                };
                Message::PlaceContainer(Box::new(ContainerPlacement {
                    session_id,
                    container_id,
                    kind,
                    radius: r.f64()?,
                    launcher_id: r.i64()?,
                    credit: read_credit(&mut r)?,
                    distance: r.f64()?,
                }))
            }
            kind::CONTAINER_PLACED => Message::ContainerPlaced {
                session_id: r.u64()?,
                container_id: r.i64()?,
                position: read_vector(&mut r)?,
                direction: read_vector(&mut r)?,
            },
            kind::CHANGE_WRECK => {
                let session_id = r.u64()?;
                let wreck_id = r.i64()?;
                let is_empty = match r.u8()? {
                    0 => false,
                    1 => true,
                    _ => return Err(DecodeError::BadValue("is_empty")),
                };
                Message::ChangeWreck { session_id, wreck_id, is_empty }
            }
            kind::REMOVE_WRECK => Message::RemoveWreck { wreck_id: r.i64()?, effect: r.i32()? },
            kind::DRONE_WORK_RESULT => {
                let session_id = r.u64()?;
                let drone_id = r.i64()?;
                let target_id = r.i64()?;
                let cycle_id = r.u64()?;
                let accepted = match r.u8()? { 0 => false, 1 => true, _ => return Err(DecodeError::BadValue("accepted")) };
                let reason = r.text()?;
                let units_remaining = r.i64()?;
                let delivered_units = r.i64()?;
                let complete = match r.u8()? { 0 => false, 1 => true, _ => return Err(DecodeError::BadValue("complete")) };
                Message::DroneWorkResult { session_id, drone_id, target_id, cycle_id, accepted, reason, units_remaining, delivered_units, complete }
            }
            kind::REMOTE_REPAIR_CYCLE_RESULT => {
                let session_id = r.u64()?;
                let module_id = r.i64()?;
                let target_id = r.i64()?;
                let cycle_id = r.u64()?;
                let accepted = match r.u8()? { 0 => false, 1 => true, _ => return Err(DecodeError::BadValue("accepted")) };
                let reason = r.text()?;
                let amount = r.f64()?;
                if cycle_id == 0 || !amount.is_finite() || amount < 0.0 {
                    return Err(DecodeError::BadValue("remote repair cycle result"));
                }
                Message::RemoteRepairCycleResult { session_id, module_id, target_id, cycle_id, accepted, reason, amount }
            }
            kind::MEASURED => {
                let session_id = r.u64()?;
                let command = r.u8()?;
                let distance = match r.u8()? {
                    0 => None,
                    1 => Some(r.f64()?),
                    _ => return Err(DecodeError::BadValue("distance")),
                };
                Message::Measured { session_id, command, distance }
            }
            kind::ADJUST_CHARGE => {
                let request_id = r.u64()?;
                let session_id = r.u64()?;
                let module_id = r.i64()?;
                let charge_type = r.i32()?;
                let code = r.u8()?;
                let (first, second) = (r.i64()?, r.i64()?);
                let op = match code {
                    0 => ChargeOp::Add { capacity: first, available: second },
                    1 => ChargeOp::Remove { quantity: first },
                    _ => return Err(DecodeError::BadValue("charge op")),
                };
                Message::AdjustCharge { request_id, session_id, module_id, charge_type, op }
            }
            kind::CHARGE_ADJUSTED => {
                let request_id = r.u64()?;
                let running = match r.u8()? {
                    0 => false,
                    1 => true,
                    _ => return Err(DecodeError::BadValue("running")),
                };
                Message::ChargeAdjusted { request_id, running, before: r.i64()?, after: r.i64()? }
            }
            kind::RESPAWN => Message::Respawn {
                session_id: r.u64()?,
                ship: Box::new(read_ship(&mut r)?),
                position: read_vector(&mut r)?,
                direction: read_vector(&mut r)?,
            },
            kind::PLACE_ASTEROID => {
                let solar_system_id = r.i32()?;
                let asteroid_id = r.i64()?;
                let type_id = r.i32()?;
                let group_id = r.i32()?;
                let category_id = r.i32()?;
                let name = r.text()?;
                let graphic_id = match r.u8()? {
                    0 => None,
                    1 => Some(r.i32()?),
                    _ => return Err(DecodeError::BadValue("graphic_id")),
                };
                let position = read_vector(&mut r)?;
                let direction = read_vector(&mut r)?;
                let radius = r.f64()?;
                let units_remaining = r.i64()?;
                Message::PlaceAsteroid(Box::new(AsteroidPlacement {
                    solar_system_id, asteroid_id, type_id, group_id, category_id, name, graphic_id, position, direction, radius, units_remaining,
                }))
            }
            kind::PLACE_WRECK => {
                let solar_system_id = r.i32()?;
                let wreck_id = r.i64()?;
                let group_id = r.i32()?;
                let Some(kind) = read_wreck_kind(&mut r)? else { return Err(DecodeError::BadValue("wreck")) };
                let position = read_vector(&mut r)?;
                let direction = read_vector(&mut r)?;
                let radius = r.f64()?;
                let launcher_id = r.i64()?;
                let credit = read_credit(&mut r)?;
                let is_empty = match r.u8()? {
                    0 => false,
                    1 => true,
                    _ => return Err(DecodeError::BadValue("is_empty")),
                };
                Message::PlaceWreck(Box::new(WreckPlacement {
                    solar_system_id,
                    wreck_id,
                    group_id,
                    kind,
                    position,
                    direction,
                    radius,
                    launcher_id,
                    credit,
                    is_empty,
                }))
            }
            kind::SPAWN => {
                let request_id = r.u64()?;
                let solar_system_id = r.i32()?;
                let ship = read_ship(&mut r)?;
                let anchor_ship_id = r.i64()?;
                let mut vectors = [[0.0; 3]; 3];
                for vector in &mut vectors {
                    *vector = [r.f64()?, r.f64()?, r.f64()?];
                }
                let behavior = r.text()?;
                let identity = read_identity(&mut r)?;
                Message::Spawn(Box::new(Spawn {
                    request_id,
                    solar_system_id,
                    ship,
                    anchor_ship_id,
                    position: vectors[0],
                    velocity: vectors[1],
                    direction: vectors[2],
                    behavior,
                    identity,
                }))
            }
            kind::SPAWN_REPLY => {
                let request_id = r.u64()?;
                let outcome = match r.u8()? {
                    0 => Ok(r.u64()?),
                    1 => Err(r.text()?),
                    _ => return Err(DecodeError::BadValue("status")),
                };
                Message::SpawnReply { request_id, outcome }
            }
            kind::LAUNCH_DRONE => Message::LaunchDrone(Box::new(LaunchDrone {
                request_id: r.u64()?,
                session_id: r.u64()?,
                ship: read_ship(&mut r)?,
                weapon: {
                    let weapon = read_weapon(&mut r)?;
                    (weapon.module_id != 0).then_some(weapon)
                },
                graphic_id: r.i32()?,
                orbit_distance: r.f64()?,
                attack_range: r.f64()?,
                cruise_fraction: r.f64()?,
                mining_amount_m3: r.f64()?,
                work_cycle_ms: r.f64()?,
                mining_waste_multiplier: r.f64()?,
                mining_waste_probability: r.f64()?,
                salvage_access_bonus: r.f64()?,
            })),
            kind::DRONE_COMMAND => {
                let (session_id, command, target_id) = (r.u64()?, r.u8()?, r.i64()?);
                let count = r.u32()? as usize;
                if count > r.data.len().saturating_sub(8) / 8 {
                    return Err(DecodeError::Truncated);
                }
                let drone_ids = (0..count).map(|_| r.i64()).collect::<Result<Vec<_>, _>>()?;
                let control_range = r.f64()?;
                Message::DroneCommand { session_id, command, target_id, drone_ids, control_range }
            }
            kind::DRONE_RESULTS => {
                let (session_id, command) = (r.u64()?, r.u8()?);
                let count = r.u32()? as usize;
                if count > r.data.len() / 12 {
                    return Err(DecodeError::Truncated);
                }
                let results =
                    (0..count).map(|_| Ok((r.i64()?, r.text()?))).collect::<Result<Vec<_>, DecodeError>>()?;
                Message::DroneResults { session_id, command, results }
            }
            kind::DRONE_SCOOPED => Message::DroneScooped { session_id: r.u64()?, drone_id: r.i64()? },
            kind::DRONE_RELEASED => Message::DroneReleased {
                session_id: r.u64()?,
                drone_id: r.i64()?,
                position: read_vector(&mut r)?,
            },
            kind::DRONE_WORK_CYCLE => Message::DroneWorkCycle {
                session_id: r.u64()?,
                character_id: r.i64()?,
                ship_id: r.i64()?,
                solar_system_id: r.i32()?,
                drone_id: r.i64()?,
                target_id: r.i64()?,
                cycle_id: r.u64()?,
                command: r.u8()?,
                mining_amount_m3: r.f64()?,
                waste_multiplier: r.f64()?,
                waste_probability: r.f64()?,
                access_difficulty_bonus: r.f64()?,
                roll: r.f64()?,
            },
            kind::REMOTE_REPAIR_CYCLE => {
                let session_id = r.u64()?;
                let module_id = r.i64()?;
                let target_id = r.i64()?;
                let cycle_id = r.u64()?;
                let cycle_number = r.i32()?;
                let cycle_at_ms = r.i64()?;
                let cycle_duration_ms = r.i64()?;
                let ends_effect_after_cycle = match r.u8()? { 0 => false, 1 => true, _ => return Err(DecodeError::BadValue("ends effect after cycle")) };
                let base_amount = r.f64()?;
                let bonus_per_cycle = r.f64()?;
                let bonus_max = r.f64()?;
                if cycle_id == 0 || cycle_number <= 0 || cycle_duration_ms <= 0 || !base_amount.is_finite() || base_amount < 0.0 || !bonus_per_cycle.is_finite() || bonus_per_cycle < 0.0 || !bonus_max.is_finite() || bonus_max < 0.0 {
                    return Err(DecodeError::BadValue("remote repair cycle"));
                }
                Message::RemoteRepairCycle { session_id, module_id, target_id, cycle_id, cycle_number, cycle_at_ms, cycle_duration_ms, ends_effect_after_cycle, base_amount, bonus_per_cycle, bonus_max }
            }
            kind::DESTROY => Message::Destroy { ship_id: r.i64()?, credit: read_credit(&mut r)? },
            kind::TARGET_REFUSED => Message::TargetRefused {
                session_id: r.u64()?,
                command: r.u8()?,
                reason: r.text()?,
                target_type: r.i32()?,
                desired_range: r.f64()?,
                actual_distance: r.f64()?,
            },
            other => return Err(DecodeError::UnknownKind(other)),
        };
        r.finish()?;
        Ok(message)
    }
}
