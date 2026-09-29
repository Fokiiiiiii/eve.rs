//! A ship in space: what the host says about it (`ShipSpec`, ADR 0007) and where it is and how it is moving.

use eve_destiny::ball::{LegacyMode, WarpState};
use eve_destiny::{BallEntity, IdRef, Kind, Vec3};

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

/// A ship in a scene: its spec and its motion.
#[derive(Debug, Clone, PartialEq)]
pub struct Ship {
    pub spec: ShipSpec,
    pub position: Vec3,
    pub velocity: Vec3,
    /// A unit vector.
    pub direction: Vec3,
    pub speed_fraction: f64,
    pub course: Course,
}

impl Ship {
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
            has_session: true,
            pilot_positive: self.spec.character_id > 0,
            owner_positive: self.spec.owner_id > 0,
            ..BallEntity::default()
        }
    }
}
