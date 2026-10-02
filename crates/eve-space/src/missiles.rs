//! Missiles: how far one goes, how long it flies and how much of its damage reaches the target.
//!
//! The rules are the reference's (`server/src/space/combat/missiles/missileSolver.js`), which are TQ's missile mechanics. A missile flies at its top speed for
//! `explosionDelay` (so it reaches `maxVelocity × explosionDelay`), and what it does to the target it reaches is its damage times an application factor:
//! `min(1, signature / explosion radius, (signature / explosion radius × explosion velocity / target speed) ^ exponent)`. CCP stores the exponent of the damage reduction
//! as `ln(drf) / ln(5.5)` in the missile's `aoeDamageReductionFactor`; a value above one is taken as the raw factor and worked into that exponent.

use crate::health::Damage;

/// What a launcher flies: the type of the missile it fires, its speed, how long it flies and the numbers that decide how much of its damage a target takes.
#[derive(Debug, Clone, PartialEq)]
pub struct Missile {
    pub charge_type_id: i32,
    /// The group of the charge (`Rocket`, `Heavy Missile`, `Torpedo`...): what the slim item of the missile in space names.
    pub group_id: i32,
    /// What the client calls the missile (`Scourge Rocket`).
    pub name: String,
    /// The radius of the missile's ball, its mass and its agility (the inertia of the ball: a missile turns at once, so it is small).
    pub radius: f64,
    pub mass: f64,
    pub agility: f64,
    /// The hit points the missile has while it is in flight (the `hp` of the charge): what a smartbomb or a point defense has to do to it to shoot it down.
    pub hit_points: f64,
    /// Metres a second.
    pub max_velocity: f64,
    /// Milliseconds the missile flies before it explodes (`explosionDelay`).
    pub flight_time_ms: f64,
    pub explosion_radius: f64,
    pub explosion_velocity: f64,
    pub damage_reduction_factor: f64,
    pub damage_reduction_sensitivity: f64,
}

/// The sensitivity CCP uses for the damage reduction when a missile names none.
pub const DEFAULT_REDUCTION_SENSITIVITY: f64 = 5.5;

fn round6(value: f64) -> f64 {
    (value * 1_000_000.0).round() / 1_000_000.0
}

impl Missile {
    /// How far a missile goes: its speed for as long as it flies.
    pub fn effective_range(&self) -> f64 {
        round6(self.max_velocity.max(0.0) * (self.flight_time_ms.max(0.0) / 1000.0))
    }

    /// Seconds a missile at top speed takes to cover `distance` metres (none when it cannot fly).
    pub fn time_to_cover(&self, distance: f64) -> f64 {
        if self.max_velocity <= 0.000_001 { 0.0 } else { distance.max(0.0) / self.max_velocity }
    }

    /// The exponent the target's speed is raised to (`resolveMissileDamageReductionExponent`).
    pub fn reduction_exponent(&self) -> f64 {
        let factor =
            if self.damage_reduction_factor.is_finite() { self.damage_reduction_factor } else { 1.0 };
        let sensitivity = self.damage_reduction_sensitivity.max(1.000_001);
        if factor > 1.000_001 {
            round6(factor.ln() / sensitivity.ln())
        } else {
            round6(factor.clamp(0.000_001, 1.0))
        }
    }

    /// The part of its damage a missile does to a target of this signature radius that moves at this speed (`resolveMissileApplicationFactor`).
    pub fn application_factor(&self, target_signature: f64, target_speed: f64) -> f64 {
        let radius = self.explosion_radius.max(0.000_001);
        let velocity = self.explosion_velocity.max(0.000_001);
        let signature_factor = target_signature.max(0.0) / radius;
        let speed = target_speed.max(0.0);
        let velocity_factor = if speed <= 0.000_001 {
            1.0
        } else {
            let base = (round6(signature_factor) * velocity) / speed.max(0.000_001);
            let raised = base.powf(self.reduction_exponent());
            if raised.is_finite() { raised } else { 1.0 }
        };
        round6(1.0_f64.min(round6(signature_factor)).min(velocity_factor))
    }

    /// What the missile does to a target: its damage (em, thermal, kinetic, explosive) times the application factor.
    pub fn applied(&self, raw: Damage, target_signature: f64, target_speed: f64) -> Damage {
        let factor = self.application_factor(target_signature, target_speed).clamp(0.0, 1.0);
        raw.map(|amount| round6(amount.max(0.0) * factor))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn rocket() -> Missile {
        Missile {
            charge_type_id: 266,
            group_id: 387,
            name: "Scourge Rocket".to_string(),
            radius: 300.0,
            mass: 100.0,
            agility: 5.22,
            hit_points: 90.0,
            max_velocity: 1_500.0,
            flight_time_ms: 3_000.0,
            explosion_radius: 20.0,
            explosion_velocity: 170.0,
            damage_reduction_factor: 0.5,
            damage_reduction_sensitivity: DEFAULT_REDUCTION_SENSITIVITY,
        }
    }

    #[test]
    fn a_missile_goes_as_far_as_it_flies_at_its_top_speed() {
        assert_eq!(rocket().effective_range(), 4_500.0);
        assert_eq!(rocket().time_to_cover(3_000.0), 2.0);
        assert_eq!(Missile { max_velocity: 0.0, ..rocket() }.time_to_cover(3_000.0), 0.0);
    }

    #[test]
    fn a_target_that_stands_still_takes_all_the_missile_can_give_it_up_to_its_signature() {
        // a ship of the missile's explosion radius or more is hit whole; a smaller one takes its share of the explosion
        assert_eq!(rocket().application_factor(40.0, 0.0), 1.0);
        assert_eq!(rocket().application_factor(20.0, 0.0), 1.0);
        assert_eq!(rocket().application_factor(10.0, 0.0), 0.5);
    }

    #[test]
    fn a_target_that_outruns_the_explosion_takes_less_by_the_exponent() {
        // signature 40 against radius 20 is 2; 2 × 170 / 340 = 1: no loss. At 680 m/s: 0.5 ^ 0.5.
        assert_eq!(rocket().application_factor(40.0, 340.0), 1.0);
        let slow_to_hit = rocket().application_factor(40.0, 680.0);
        assert!((slow_to_hit - 0.5_f64.powf(0.5)).abs() < 1e-5, "{slow_to_hit}");
        // never more than the signature share
        assert!(rocket().application_factor(10.0, 10.0) <= 0.5);
    }

    #[test]
    fn a_reduction_factor_above_one_is_taken_as_the_raw_factor() {
        let raw = Missile { damage_reduction_factor: 5.5, ..rocket() };
        assert_eq!(raw.reduction_exponent(), 1.0);
        let stored = Missile { damage_reduction_factor: 0.0, ..rocket() };
        assert_eq!(stored.reduction_exponent(), 0.000_001, "a value below the floor is held at it");
        assert_eq!(rocket().reduction_exponent(), 0.5);
    }

    #[test]
    fn the_damage_is_scaled_by_the_factor_and_rounded() {
        let applied = rocket().applied([0.0, 0.0, 38.115, 0.0], 14.0, 0.0);
        assert_eq!(
            applied,
            [0.0, 0.0, 26.680_5, 0.0],
            "a signature of 14 against an explosion radius of 20 is 0.7 of 38.115"
        );
    }
}
