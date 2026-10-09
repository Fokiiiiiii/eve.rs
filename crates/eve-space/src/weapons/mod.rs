//! Turrets: how a shot is resolved (does it hit, how well, how much damage) and what the client is told of it.
//!
//! The rules are the reference's (`server/src/space/combat/laserTurrets.js`), which are TQ's turret mechanics: the chance to hit is
//! `0.5 ^ (tracking² + range²)` with `tracking = angular velocity × turret signature resolution / (tracking speed × target signature radius)` and
//! `range = max(0, surface distance − optimal) / falloff`; one roll decides everything: at or above the chance the shot misses, below 1 % it is a
//! wrecking shot (three times the damage), otherwise the damage is multiplied by `0.49 + roll`. The hit quality the combat log shows is one of the
//! six bands of EVE University's table (`hit_quality_tier`).
mod fire;
mod math;
pub use self::math::angular_velocity;
pub use self::math::chance_to_hit;
pub use self::math::damage_message;
pub(crate) use self::math::explosion;
pub(crate) use self::math::explosion_shot;
pub use self::math::hit_quality_tier;
pub(crate) use self::math::quality_message;
pub use self::math::resolve_shot;
pub(crate) use self::math::spooled_damage;
pub use self::math::surface_distance;
use self::math::*;

use eve_marshal::Value;

use crate::health::Damage;

use crate::missiles::{Missile, application_factor_of};

use crate::modules::Effect;

use crate::npc::is_npc_session;

use crate::scene::{Out, Scene, SessionId};

use crate::wire::{dict, id, int, real, text, tuple};

/// The attributes the damage of a shot is told under in `OnDamageMessage`, in the order em, thermal, kinetic, explosive.
pub const DAMAGE_ATTRIBUTES: [i64; 4] = [114, 118, 117, 116];

/// The signature resolution a turret is taken to have when it says nothing (`optimalSigRadius`; a small turret's is 40, a medium's 125, a
/// large's 400).
pub const DEFAULT_SIGNATURE_RESOLUTION: f64 = 40_000.0;

/// What a turret does: the numbers dogma gives it, worked out by the host (or the NPC's type) and used by the scene.
#[derive(Debug, Clone, PartialEq)]
pub struct Turret {
    /// The type of the weapon (what `OnDamageMessage` calls `weapon` and `OnSpecialFX` the module type).
    pub weapon_type_id: i32,
    /// What the destiny stream calls the effect (`effects.Laser`, `effects.ProjectileFired`, `effects.HybridFired`).
    pub fx: String,
    /// The damage of a shot before the quality of the hit: em, thermal, kinetic, explosive (the damage multiplier is in it).
    pub raw_damage: Damage,
    pub optimal_range: f64,
    pub falloff: f64,
    /// Radians a second.
    pub tracking_speed: f64,
    pub signature_resolution: f64,
    /// The charge the turret fires when it takes one (the host works its damage into `raw_damage`): the scene keeps the rounds and spends them.
    pub charge: Option<Charge>,
    /// What a launcher flies (a launcher is a weapon that fires a missile in the place of a shot: its `raw_damage` is the missile's, its `optimal_range` how far the missile goes).
    pub missile: Option<Missile>,
    /// The damage ramp of a weapon that builds up its damage on the target it keeps shooting at (a precursor weapon); none for the others.
    pub spool: Option<Spool>,
    /// How a shot that explodes on its target does its damage (a vorton projector: no roll; the explosion's cloud and velocity decide how much of the
    /// damage reaches the target, as a missile's do); none for a weapon that rolls to hit.
    pub aoe: Option<Aoe>,
    /// The chain a shot links from its target to other ships (a vorton projector's arc); none for the others.
    pub arc: Option<Arc>,
    /// Whether the weapon requires Gunnery (its SDE `requiredSkill` list names it): a tracking disruptor or computer reaches only such a weapon
    /// (the client's `SKILL_MODIFIER_CHANGES_FOR_LOCATION` is keyed on `typeGunnery`); a vorton projector, which requires its own skill, is not reached.
    pub requires_gunnery: bool,
}

/// The explosion of a shot that does not roll to hit (a vorton projector): the cloud's radius and velocity, and the damage reduction factor and its
/// sensitivity, as a missile's explosion has them (`missiles::application_factor_of`).
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Aoe {
    pub explosion_radius: f64,
    pub explosion_velocity: f64,
    pub damage_reduction_factor: f64,
    pub damage_reduction_sensitivity: f64,
}

/// The chain a shot links from its target (EVE.js `vortonArcChain`): other ships within `range` of the target, up to `targets` ships in all (the target
/// counts, so `targets - 1` links are made), each struck with the explosion of the shot.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Arc {
    pub range: f64,
    pub targets: i32,
}

/// The damage ramp of a weapon that builds up its damage on the target it keeps shooting at (a precursor weapon, EVE.js `precursorTurrets.js`): each
/// cycle the bonus grows by `per_cycle` up to `max`, and a shot is multiplied by `1 + bonus`, the bonus built up before that shot (the first has none).
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Spool {
    pub per_cycle: f64,
    pub max: f64,
}

/// What a running weapon has built up: the bonus its next shot takes, and the target it was built on.
#[derive(Debug, Clone, Copy, Default, PartialEq)]
pub(crate) struct SpoolState {
    pub(crate) bonus: f64,
    pub(crate) target_id: i64,
}

impl Spool {
    /// The bonus the next shot at `target_id` takes: a target other than the one it was built on starts it again from none.
    pub(crate) fn bonus_for(self, state: &mut SpoolState, target_id: i64) -> f64 {
        if state.target_id != target_id {
            *state = SpoolState { bonus: 0.0, target_id };
        }
        state.bonus = state.bonus.clamp(0.0, self.max.max(0.0));
        state.bonus
    }

    /// A cycle has been shot: the bonus grows by a cycle's worth, up to the maximum.
    pub(crate) fn advance(self, state: &mut SpoolState) {
        state.bonus = (state.bonus + self.per_cycle.max(0.0)).min(self.max.max(0.0));
    }
}

/// The ammunition in a turret: the kind, the slot it sits in (the client knows a loaded charge by the ship, the slot and the kind), the rounds it holds and the
/// rounds a cycle spends.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Charge {
    pub type_id: i32,
    pub flag: i32,
    pub rounds: i64,
    pub per_cycle: i64,
}

/// Where and how a ship moves, for the sake of a shot.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Kinematics {
    pub position: [f64; 3],
    pub velocity: [f64; 3],
    pub radius: f64,
    /// How big the ship looks to a turret (`signatureRadius`); the radius when it has none.
    pub signature_radius: f64,
}

/// An offensive act: a weapon of `attacker` was fired at `target` (what the crimewatch tells a tree that waits for aggression).
#[derive(Debug, Clone, PartialEq)]
pub struct Aggression {
    pub attacker_ship: i64,
    pub target_ship: i64,
    pub module_type_id: i32,
    /// The character that flies the attacker, or its owner.
    pub source_owner_id: i64,
    pub target_type_id: i32,
}

/// What came of a shot.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Shot {
    pub hit: bool,
    pub wrecking: bool,
    /// The multiplier of the damage: 0 for a miss, 3 for a wrecking shot, else `0.49 + roll`.
    pub quality: f64,
    pub chance_to_hit: f64,
    pub roll: f64,
    pub damage: Damage,
}

/// Who a shot is from: the pilot that is told of it (a drone's controller), the ship it counts against and the ship that is aggressed by it, the owner
/// the combat log names, and the weapon's type.
#[derive(Debug, Clone, Copy)]
struct Shooter {
    reporter: SessionId,
    attacker_ship: i64,
    aggressor_ship: i64,
    owner: i64,
    weapon: i32,
}

/// A missile that is this much further from its target than it can fly (metres) still gets there (the reference's `graceMeters`).
pub(crate) const MISSILE_RANGE_GRACE: f64 = 1.0;

#[cfg(test)]
pub(crate) mod tests;
