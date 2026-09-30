//! A ship in space: what the host says about it (`ShipSpec`, ADR 0007) and where it is and how it is moving.

use eve_destiny::ball::{LegacyMode, WarpState};
use eve_destiny::{BallEntity, IdRef, Kind, Vec3};

use crate::health::{Condition, Health, Vitals};
use crate::weapons::Turret;

/// A fitted module, as the ship's slim item lists it.
#[derive(Debug, Clone, PartialEq)]
pub struct ModuleSpec {
    pub item_id: i64,
    pub type_id: i32,
    pub flag: i32,
}

/// What motion and the slim item need to know of a ship. The host works these out (dogma, skills, fitting); the sim only uses them.
#[derive(Debug, Clone, PartialEq)]
pub struct ShipSpec {
    pub ship_id: i64,
    pub character_id: i64,
    pub type_id: i32,
    pub group_id: i32,
    pub category_id: i32,
    pub name: String,
    pub owner_id: i64,
    pub corporation_id: i32,
    pub alliance_id: i32,
    pub war_faction_id: i32,
    pub security_status: f64,
    pub radius: f64,
    pub mass: f64,
    pub max_velocity: f64,
    /// The ship's `agility` attribute: with the mass it sets how quickly speed and heading change.
    pub agility: f64,
    pub warp_speed_au: f64,
    /// How long the shield takes to recharge, in milliseconds (`shieldRechargeRate`): the damage state shows a fifth of it.
    pub shield_recharge_ms: f64,
    /// How far the ship can lock a target, in metres, measured between surfaces (`maxTargetRange`).
    pub max_target_range: f64,
    /// How many targets it can lock at once (`maxLockedTargets`, the lower of the ship's and the pilot's).
    pub max_locked_targets: i32,
    /// How fast it locks (`scanResolution`, in mm) and how big it looks to a ship that locks it (`signatureRadius`, in metres).
    pub scan_resolution: f64,
    pub signature_radius: f64,
    /// The capacitor: how much it holds (GJ) and how long it takes to recharge (seconds).
    pub capacitor_capacity: f64,
    pub capacitor_recharge_s: f64,
    /// What the ship can take (shield, armor and hull in points, and how each resists) and what it has taken as it comes into space.
    pub vitals: Vitals,
    pub condition: Condition,
    pub modules: Vec<ModuleSpec>,
}

/// What the ship is doing.
#[derive(Debug, Clone, Copy, PartialEq)]
pub enum Course {
    /// Heading along `direction` at `speed_fraction` of the top speed.
    Goto,
    /// Slowing to a halt.
    Stop,
    /// Approaching a body and staying `range` metres off its surface.
    Follow { target: i64, range: f64 },
    /// Circling a body `distance` metres off its surface.
    Orbit { target: i64, distance: f64 },
    /// Warping (or, while `active` is false, turning to enter warp) to `raw_destination`, ending `stop_distance` short of it.
    Warp {
        raw_destination: Vec3,
        target_point: Vec3,
        stop_distance: f64,
        total_distance: f64,
        factor: i64,
        active: bool,
    },
}

/// A weapon an NPC fights with. An NPC's weapons are effects of the ship itself (the client's `targetAttack`: the ship's own attributes are the
/// weapon's), so the module of the effect is the ship.
#[derive(Debug, Clone, PartialEq)]
pub struct NpcWeapon {
    pub module_id: i64,
    pub effect_id: i32,
    /// The length of a cycle, milliseconds.
    pub cycle_ms: f64,
    pub capacitor_need: f64,
    pub turret: Turret,
}

/// What an NPC fights with and when it starts to.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct NpcCombat {
    pub weapons: Vec<NpcWeapon>,
    /// How near a ship has to come for the NPC to attack it, metres (`entityAttackRange`); 0 for an NPC that only answers.
    pub aggression_range: f64,
}

/// What sets an NPC's ship apart from a player's: nobody flies it (its ball has no session and is not interactive), its slim item has no name and
/// carries the limits the NPC's owner reacts to (`hostile_response_threshold`, `friendly_response_threshold`: the standings at which it treats others
/// as enemies or friends), and it fights with what `combat` says.
#[derive(Debug, Clone, PartialEq)]
pub struct NpcIdentity {
    pub hostile_response_threshold: i64,
    pub friendly_response_threshold: i64,
    pub combat: NpcCombat,
    /// What the ship leaves when it is destroyed (`None`: nothing).
    pub wreck: Option<crate::wreck::WreckKind>,
}

impl Default for NpcIdentity {
    /// The limits the reference gives every NPC it spawns (`-11` and `11`: no standing reaches them, so the NPC is hostile to whoever the
    /// aggression rules say and to nobody else), and no weapons.
    fn default() -> Self {
        Self {
            hostile_response_threshold: -11,
            friendly_response_threshold: 11,
            combat: NpcCombat::default(),
            wreck: None,
        }
    }
}

/// A ship in a scene: its spec and its motion.
#[derive(Debug, Clone, PartialEq)]
pub struct Ship {
    pub spec: ShipSpec,
    /// Set for an NPC's ship.
    pub npc: Option<NpcIdentity>,
    pub position: Vec3,
    pub velocity: Vec3,
    /// A unit vector.
    pub direction: Vec3,
    pub speed_fraction: f64,
    pub course: Course,
    /// What it can take and has taken.
    pub health: Health,
}

impl Ship {
    /// A ship of this spec in this place, moving so, with the health the spec says at `now`.
    pub fn new(
        spec: ShipSpec,
        position: Vec3,
        velocity: Vec3,
        direction: Vec3,
        speed_fraction: f64,
        course: Course,
        now: i64,
    ) -> Self {
        let health = Health::new(spec.vitals, spec.shield_recharge_ms, spec.condition, now);
        Self { spec, npc: None, position, velocity, direction, speed_fraction, course, health }
    }

    /// The same ship as an NPC's.
    pub fn as_npc(mut self, identity: NpcIdentity) -> Self {
        self.npc = Some(identity);
        self
    }

    /// The ball the destiny stream describes the ship with.
    pub fn ball(&self) -> BallEntity {
        BallEntity {
            item_id: IdRef::Valid(self.spec.ship_id),
            kind: Kind::Ship,
            legacy_mode: match self.course {
                Course::Goto => LegacyMode::Goto,
                Course::Stop => LegacyMode::Stop,
                Course::Follow { .. } => LegacyMode::Follow,
                Course::Orbit { .. } => LegacyMode::Orbit,
                Course::Warp { .. } => LegacyMode::Warp,
            },
            follow_target: match self.course {
                Course::Follow { target, .. } | Course::Orbit { target, .. } => IdRef::Valid(target),
                _ => IdRef::Absent,
            },
            follow_range: match self.course {
                Course::Follow { range, .. } => range,
                Course::Orbit { distance, .. } => distance,
                _ => 0.0,
            },
            warp_state: match self.course {
                Course::Warp {
                    raw_destination,
                    target_point,
                    stop_distance,
                    total_distance,
                    factor,
                    active,
                } => Some(WarpState {
                    effect_stamp: if active { f64::NAN } else { -1.0 },
                    total_distance,
                    stop_distance,
                    warp_speed: Some(factor as f64),
                    native_command_is_goto: false,
                    raw_destination: Some(raw_destination),
                    target_point: Some(target_point),
                }),
                _ => None,
            },
            pending_warp: matches!(self.course, Course::Warp { active: false, .. }),
            radius: self.spec.radius,
            position: self.position,
            velocity: self.velocity,
            direction: self.direction,
            mass: Some(self.spec.mass),
            inertia: self.spec.agility,
            max_velocity: self.spec.max_velocity,
            speed_fraction: self.speed_fraction,
            corporation_id: IdRef::Valid(i64::from(self.spec.corporation_id)),
            alliance_id: IdRef::Valid(i64::from(self.spec.alliance_id)),
            has_session: self.npc.is_none(),
            pilot_positive: self.npc.is_none() && self.spec.character_id > 0,
            owner_positive: self.spec.owner_id > 0,
            ..BallEntity::default()
        }
    }
}
