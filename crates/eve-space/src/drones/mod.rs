//! Drones: ships that a pilot launches from the drone bay of its ship and that fly and fight for it, kept apart from the pilots. A drone is a pilot of the scene under a
//! session of its own like an NPC (its motion, health, damage and death are a ship's, and a player sees it as a ball of its own), tied to the ship that controls it
//! ([`DroneLink`](crate::ship::DroneLink)) and driven by [`DroneAi`] instead of a behavior tree: it circles its controller until it is sent at a target, flies to
//! it, fires its weapon each cycle (the pilot's combat log tells the shots as its own), goes home when called and is scooped into the bay when it comes near.
//!
//! What the reference does (`droneRuntime.js`, `droneDogma.js`, `oracle/fixtures/scenarios/drone_flight.json`): the launch is one update that adds the ball (and tells the
//! controller `OnDroneStateChange`) and another that sets it circling at 700 m at a fraction of its speed that is its cruise speed; an order to engage sets it circling the
//! target at full speed and it is told as pursuing (6), approaching (3) or fighting (1) by how far the target is from its weapon's range; a drone that has killed its
//! target goes idle (0) and circles the controller again; an order to return home sets it circling the controller (4) and one to return to the bay brings it in
//! until it is within 2,500 m of the ship, when it is scooped.
mod fight;
mod launch;
mod threats;
mod work;

use eve_destiny::Vec3;

use eve_marshal::Value;

use crate::modules::{Action, Applies, Effect, Jam, Reach, Remote};

use crate::npc::NpcPlace;

use crate::payload;

use crate::scene::{Out, Scene, SessionId};

use crate::ship::{CompanionKind, Course, DroneLink, NpcCombat, NpcIdentity, NpcWeapon, ShipSpec};

use crate::wire::{id, int, real, text};

/// How near the controller's surface a drone has to be to be taken into the bay.
pub const SCOOP_DISTANCE: f64 = 2_500.0;

/// How near the controller's surface a fighter squadron has to come to land in its tube (EVE.js `FIGHTER_RECOVERY_DISTANCE_METERS`).
pub const FIGHTER_RECOVERY_DISTANCE: f64 = 5_000.0;

/// How far ahead of the ship's surface a drone comes into space, metres.
const LAUNCH_OFFSET: f64 = 75.0;

/// How far from its controller a drone may be sent at a target it finds by itself or takes up on its own (the reference's base drone control range: 20 km).
const CONTROL_RANGE: f64 = 20_000.0;

const DRONE_RECONNECT_RANGE: f64 = 250_000.0;

/// How long a drone remembers who attacked its controller (the reference's `DRONE_AGGRESSION_THREAT_RETENTION_MS`: 30 s), in ticks.
const THREAT_MEMORY: i64 = 30 * crate::scene::TICKS_PER_SECOND;

/// The states the client numbers a drone's activity by (`const.entity*`).
pub const IDLE: i32 = 0;

pub const COMBAT: i32 = 1;

pub const APPROACHING: i32 = 3;

pub const DEPARTING: i32 = 4;

pub const PURSUIT: i32 = 6;

/// What a pilot tells its drones to do (the codes of the link).
#[derive(Debug, Clone, Copy, PartialEq)]
pub enum DroneCommand {
    /// Fly at the target (a ship of the scene) and shoot it.
    Engage {
        target: i64,
    },
    /// Circle the controller.
    ReturnHome,
    /// Come back to the ship and be scooped into the bay.
    ReturnBay,
    /// Let the drone go: it stays in space under nobody's control.
    Abandon,
    Assist {
        character_id: i64,
    },
    Guard {
        character_id: i64,
    },
    MineRepeatedly {
        target: i64,
    },
    Salvage {
        target: Option<i64>,
    },
    /// Reconnect to an owned abandoned drone that is still nearby in this scene.
    Reconnect,
}

impl DroneCommand {
    /// The code of the command on the link (ADR 0007).
    pub fn code(self) -> u8 {
        match self {
            Self::Engage { .. } => 1,
            Self::ReturnHome => 2,
            Self::ReturnBay => 3,
            Self::Abandon => 4,
            Self::Assist { .. } => 5,
            Self::Guard { .. } => 6,
            Self::MineRepeatedly { .. } => 7,
            Self::Salvage { .. } => 8,
            Self::Reconnect => 9,
        }
    }
}

/// A non-damage effect a drone runs at its target each cycle: the remote effect of its type (a stasis webifier, a remote repairer, an ECM jammer), as the host worked
/// it out from the type's dogma. Each cycle lands `action` on its target for `lasting_ms`; the effect runs on its own clock, beside a turret when its type has one.
#[derive(Debug, Clone, PartialEq)]
pub struct DroneEffect {
    /// The dogma effect: its id, and the name the destiny stream calls it (`effects.ModifyTargetSpeed`).
    pub effect_id: i32,
    pub fx: String,
    /// How long a cycle is, milliseconds.
    pub cycle_ms: f64,
    /// How long what a cycle lands lasts on its target, milliseconds (an ECM jam lasts less than its cycle).
    pub lasting_ms: f64,
    /// What a cycle does to its target: the remote effect, its reach, when in the cycle it lands and the name the client's HUD gives it.
    pub action: Action,
}

impl DroneEffect {
    fn remote(&self) -> Option<(&Remote, &Reach, Applies)> {
        match &self.action {
            Action::Remote { what, reach, when, .. } => Some((what, reach, *when)),
            _ => None,
        }
    }

    /// Whether the effect mends ships rather than harms them (a remote repairer): it is run on a friendly ship only.
    pub fn repairs(&self) -> bool {
        matches!(self.remote(), Some((Remote::Repair { .. } | Remote::MutadaptiveRepair { .. }, ..)))
    }

    /// How far off its target the effect reaches, metres: a drone's effects have no falloff, so this is the whole of it.
    fn optimal(&self) -> f64 {
        self.remote().map_or(0.0, |(_, reach, _)| reach.optimal)
    }
}

/// What a drone is set on.
#[derive(Debug, Clone, Copy, PartialEq)]
pub(crate) enum Task {
    /// Circling its controller.
    Idle,
    Engage {
        target: i64,
    },
    /// Coming back to the controller (circling it) to wait there.
    Home,
    /// Coming back to be scooped.
    Bay,
    MineRepeatedly {
        target: i64,
    },
    Salvage {
        target: Option<i64>,
    },
}

#[derive(Debug, Clone, Copy, PartialEq)]
pub(crate) enum DroneFollow {
    Assist { character_id: i64, control_range: f64 },
    Guard { character_id: i64, control_range: f64 },
}

impl DroneFollow {
    fn character_id(self) -> i64 {
        match self {
            Self::Assist { character_id, .. } | Self::Guard { character_id, .. } => character_id,
        }
    }

    fn control_range(self) -> f64 {
        match self {
            Self::Assist { control_range, .. } | Self::Guard { control_range, .. } => control_range,
        }
    }
}

/// What the scene keeps of a drone besides its ship.
#[derive(Debug, Clone)]
pub(crate) struct DroneAi {
    pub(crate) task: Task,
    pub(crate) follow: Option<DroneFollow>,
    /// The next moment its weapon fires.
    next_shot: i64,
    /// The remote effect of its type (what each cycle lands on its target, for how long), which runs on its own clock beside its weapon.
    remote: Option<DroneEffect>,
    /// The next moment its remote effect runs.
    next_remote: i64,
    /// The cycle whose effect is landing on a target now: its HUD is told as it begins and ends, and the target is let go of when the drone stops.
    landing: Option<Effect>,
    /// A cycle of a remote repair that lands when that cycle ends (at the next cycle's moment).
    ending: Option<Effect>,
    /// How far it can shoot (metres off its target's surface), how near it circles its target, and how far it follows one that runs.
    engagement_range: f64,
    movement_range: f64,
    chase_range: f64,
    work: DroneWorkProfile,
    can_combat: bool,
    work_control_range: f64,
    work_ready_at: i64,
    work_cycle_id: u64,
    awaiting_work: bool,
    auto_salvage: bool,
    work_phase: DroneWorkPhase,
}

/// What a drone fights with and how it flies, as the host works them out from its type.
#[derive(Debug, Clone, PartialEq)]
pub struct DroneFlight {
    pub weapon: Option<NpcWeapon>,
    pub graphic_id: i32,
    /// How far off its controller (or its target) it circles, metres.
    pub orbit_distance: f64,
    /// How far off its target it can shoot, metres: inside it the drone fights (the reference: `entityAttackRange`, at least the weapon's optimal range). 0 for the weapon's optimal.
    pub attack_range: f64,
    /// The fraction of its top speed it flies at while it only follows its controller.
    pub cruise_fraction: f64,
    pub work: DroneWorkProfile,
    /// The remote effect of the type (a web, a repair, a jam), when it has one.
    pub effect: Option<DroneEffect>,
}

/// Effective mining/salvage dogma values, resolved by SDE attribute names by the host.
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct DroneWorkProfile {
    pub mining_amount_m3: f64,
    pub cycle_ms: f64,
    pub waste_multiplier: f64,
    pub waste_probability: f64,
    pub salvage_access_bonus: f64,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
enum DroneWorkPhase {
    #[default]
    ToTarget,
    Cycling,
    Returning,
}

/// A unit vector perpendicular to `direction`.
fn perpendicular(direction: Vec3) -> Vec3 {
    let helper = if direction.z.abs() < 0.9 { Vec3::new(0.0, 0.0, 1.0) } else { Vec3::new(1.0, 0.0, 0.0) };
    let cross = Vec3::new(
        direction.y * helper.z - direction.z * helper.y,
        direction.z * helper.x - direction.x * helper.z,
        direction.x * helper.y - direction.y * helper.x,
    );
    let length = (cross.x * cross.x + cross.y * cross.y + cross.z * cross.z).sqrt().max(1e-9);
    Vec3::new(cross.x / length, cross.y / length, cross.z / length)
}

#[cfg(test)]
mod tests;
