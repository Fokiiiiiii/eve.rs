//! The client's constants the behaviors read from other packages (`dogma.const`, `inventorycommon.const`, `eve.common.lib.appConst`,
//! `carbon.common.lib.const`, `ballpark.const`). Only those the tasks use.

// ---- carbon.common.lib.const ----

/// `SEC`: a second in the ticks of `gametime`.
pub const SEC: i64 = 10_000_000;
pub const SECONDS_IN_MILLISECOND: f64 = 0.001;

// ---- ballpark.const (the destiny modes the ball's `mode` holds) ----

pub const DESTINY_MODE_GOTO: i64 = 0;
pub const DESTINY_MODE_FOLLOW: i64 = 1;
pub const DESTINY_MODE_STOP: i64 = 2;
pub const DESTINY_MODE_WARP: i64 = 3;
pub const DESTINY_MODE_ORBIT: i64 = 4;

// ---- eve.common.lib.appConst ----

pub const MIN_WARP_DISTANCE: i64 = 150_000;
pub const SHIP_NOT_WARPING: i64 = 0;

// ---- inventorycommon.const ----

pub const CATEGORY_SHIP: i64 = 6;
pub const CATEGORY_STRUCTURE: i64 = 65;
pub const FLAG_CARGO: i64 = 5;
pub const GROUP_SOLAR_SYSTEM: i64 = 5;
pub const OWNER_NONE: i64 = 0;
pub const OWNER_UNKNOWN: i64 = 3006;
pub const MAX_SYSTEM_ITEM: i64 = 9_999;
pub const MIN_PLAYER_OWNER: i64 = 90_000_000;
pub const MIN_PLAYER_ITEM: i64 = 100_000_000;

/// `idCheckers.IsNPC`: an owner that is neither the system nor a player.
pub fn is_npc(owner_id: i64) -> bool {
    MAX_SYSTEM_ITEM < owner_id && owner_id < MIN_PLAYER_OWNER
}

// ---- dogma.const: attributes ----

pub const ATTRIBUTE_HP: i64 = 9;
pub const ATTRIBUTE_DAMAGE: i64 = 3;
pub const ATTRIBUTE_ARMOR_HP: i64 = 265;
pub const ATTRIBUTE_ARMOR_DAMAGE: i64 = 266;
pub const ATTRIBUTE_SHIELD_CHARGE: i64 = 264;
pub const ATTRIBUTE_SHIELD_CAPACITY: i64 = 263;
pub const ATTRIBUTE_SIGNATURE_RADIUS: i64 = 552;
pub const ATTRIBUTE_MAX_VELOCITY: i64 = 37;
pub const ATTRIBUTE_MAX_LOCKED_TARGETS: i64 = 192;
pub const ATTRIBUTE_NPC_DRONE_BANDWIDTH: i64 = 2785;
pub const ATTRIBUTE_DISALLOW_ASSISTANCE: i64 = 854;
pub const ATTRIBUTE_REMOTE_REPAIR_IMPEDANCE: i64 = 2116;
pub const ATTRIBUTE_EXPLOSION_DELAY: i64 = 281;
pub const ATTRIBUTE_ENTITY_MISSILE_TYPE_ID: i64 = 507;
pub const ATTRIBUTE_MISSILE_ENTITY_VELOCITY_MULTIPLIER: i64 = 645;
pub const ATTRIBUTE_MISSILE_ENTITY_FLIGHT_TIME_MULTIPLIER: i64 = 646;

// ---- dogma.const: effects ----

pub const EFFECT_TARGET_ABC_ATTACK: i64 = 6995;
pub const EFFECT_NPC_BEHAVIOR_REMOTE_ARMOR_REPAIRER: i64 = 6741;
pub const EFFECT_NPC_BEHAVIOR_REMOTE_CAPACITOR_TRANSMITTER: i64 = 12073;
pub const EFFECT_NPC_BEHAVIOR_REMOTE_SHIELD_BOOSTER: i64 = 6742;
pub const EFFECT_NPC_BEHAVIOR_WEBIFIER: i64 = 6743;
pub const EFFECT_NPC_BEHAVIOR_GUIDANCE_DISRUPTOR: i64 = 6746;
pub const EFFECT_NPC_BEHAVIOR_TRACKING_DISRUPTOR: i64 = 6747;
pub const EFFECT_BEHAVIOR_WARP_DISRUPT: i64 = 6744;
pub const EFFECT_BEHAVIOR_WARP_SCRAMBLE: i64 = 6745;
pub const EFFECT_NPC_BEHAVIOR_ENERGY_NEUTRALIZER: i64 = 6756;
pub const EFFECT_NPC_BEHAVIOR_ENERGY_NOSFERATU: i64 = 6882;
pub const EFFECT_BEHAVIOR_TARGET_PAINTER: i64 = 6754;
pub const EFFECT_BEHAVIOR_SENSOR_DAMPENER: i64 = 6755;
pub const EFFECT_BEHAVIOR_ECM: i64 = 6757;
pub const EFFECT_NPC_BEHAVIOR_MICRO_JUMP_ATTACK: i64 = 7187;
pub const EFFECT_NPC_BEHAVIOR_SMART_BOMB: i64 = 7188;
pub const EFFECT_ENTITY_CHAIN_LIGHTNING: i64 = 8088;
