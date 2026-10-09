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

/// What a guidance disruptor leaves a launcher's missiles with, as factors of their speed, flight time, explosion velocity and explosion radius (1 for none).
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Guidance {
    pub velocity: f64,
    pub flight_time: f64,
    pub explosion_velocity: f64,
    pub explosion_radius: f64,
}

impl Guidance {
    /// Nothing disrupts the launcher.
    pub const NONE: Self =
        Self { velocity: 1.0, flight_time: 1.0, explosion_velocity: 1.0, explosion_radius: 1.0 };
}

/// The sensitivity CCP uses for the damage reduction when a missile names none.
pub const DEFAULT_REDUCTION_SENSITIVITY: f64 = 5.5;

fn round6(value: f64) -> f64 {
    (value * 1_000_000.0).round() / 1_000_000.0
}

/// The exponent a target's speed is raised to, from the damage reduction factor and its sensitivity (`resolveMissileDamageReductionExponent`). Shared by
/// missiles and the explosions of the vorton projectors.
pub fn reduction_exponent_of(reduction_factor: f64, reduction_sensitivity: f64) -> f64 {
    let factor = if reduction_factor.is_finite() { reduction_factor } else { 1.0 };
    let sensitivity = reduction_sensitivity.max(1.000_001);
    if factor > 1.000_001 {
        round6(factor.ln() / sensitivity.ln())
    } else {
        round6(factor.clamp(0.000_001, 1.0))
    }
}

/// The part of an explosion's damage that reaches a target of this signature radius moving at this speed (`resolveMissileApplicationFactor`): the
/// signature against the explosion's cloud, and the speed against its velocity, raised to the reduction exponent. Shared by missiles and the vorton
/// projectors, whose explosions are the same curve.
pub fn application_factor_of(
    explosion_radius: f64,
    explosion_velocity: f64,
    reduction_factor: f64,
    reduction_sensitivity: f64,
    target_signature: f64,
    target_speed: f64,
) -> f64 {
    let radius = explosion_radius.max(0.000_001);
    let velocity = explosion_velocity.max(0.000_001);
    let signature_factor = target_signature.max(0.0) / radius;
    let speed = target_speed.max(0.0);
    let velocity_factor = if speed <= 0.000_001 {
        1.0
    } else {
        let base = (round6(signature_factor) * velocity) / speed.max(0.000_001);
        let raised = base.powf(reduction_exponent_of(reduction_factor, reduction_sensitivity));
        if raised.is_finite() { raised } else { 1.0 }
    };
    round6(1.0_f64.min(round6(signature_factor)).min(velocity_factor))
}

/// The explosion of a missile as its launcher's numbers give it (a fighter's missiles: the squadron's own, no missile in flight). What a target takes is the
/// damage times the application factor of `application_factor_of`.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Blast {
    pub explosion_radius: f64,
    pub explosion_velocity: f64,
    pub reduction_factor: f64,
    pub reduction_sensitivity: f64,
}

impl Blast {
    /// What the explosion does to a target of this signature radius moving at this speed.
    pub fn applied(&self, raw: Damage, target_signature: f64, target_speed: f64) -> Damage {
        let factor = application_factor_of(
            self.explosion_radius,
            self.explosion_velocity,
            self.reduction_factor,
            self.reduction_sensitivity,
            target_signature,
            target_speed,
        )
        .clamp(0.0, 1.0);
        raw.map(|amount| round6(amount.max(0.0) * factor))
    }
}

impl Missile {
    /// The missile as a disrupted launcher fires it: slower, shorter-lived, with a smaller explosion velocity and a bigger explosion radius.
    pub fn guided(&self, guidance: Guidance) -> Self {
        Self {
            max_velocity: round6(self.max_velocity * guidance.velocity.max(0.0)),
            flight_time_ms: round6(self.flight_time_ms * guidance.flight_time.max(0.0)),
            explosion_velocity: round6(self.explosion_velocity * guidance.explosion_velocity.max(0.0)),
            explosion_radius: round6(self.explosion_radius * guidance.explosion_radius.max(0.0)),
            ..self.clone()
        }
    }

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
        reduction_exponent_of(self.damage_reduction_factor, self.damage_reduction_sensitivity)
    }

    /// The part of its damage a missile does to a target of this signature radius that moves at this speed (`resolveMissileApplicationFactor`).
    pub fn application_factor(&self, target_signature: f64, target_speed: f64) -> f64 {
        application_factor_of(
            self.explosion_radius,
            self.explosion_velocity,
            self.damage_reduction_factor,
            self.damage_reduction_sensitivity,
            target_signature,
            target_speed,
        )
    }

    /// What the missile does to a target: its damage (em, thermal, kinetic, explosive) times the application factor.
    pub fn applied(&self, raw: Damage, target_signature: f64, target_speed: f64) -> Damage {
        let factor = self.application_factor(target_signature, target_speed).clamp(0.0, 1.0);
        raw.map(|amount| round6(amount.max(0.0) * factor))
    }
}

#[derive(Debug, Clone, Copy, PartialEq)]
pub struct MissileThreat {
    /// The missile ball's centre and current velocity, in metres and metres per second.
    pub position: [f64; 3],
    pub velocity: [f64; 3],
    pub radius_m: f64,
    pub hit_points: f64,
    /// Seconds until its warhead reaches its ship target, measured from this snapshot.
    pub time_to_target_seconds: f64,
}

#[derive(Debug, Clone, Copy, PartialEq)]
pub struct MissileInterception {
    /// Seconds from firing until the interceptor touches the missile's surface.
    pub time_seconds: f64,
    pub distance_m: f64,
    /// The incoming missile's centre when the shot reaches it.
    pub position: [f64; 3],
    pub remaining_hit_points: f64,
    pub destroyed: bool,
}

impl MissileInterception {
    /// A destroyed incoming missile never delivers its warhead to its ship target.
    pub fn target_spared(self) -> bool {
        self.destroyed
    }
}

/// A defender charge which homes on an incoming missile using its charge's speed and flight time.
#[derive(Debug, Clone, PartialEq)]
pub struct DefenderMissile {
    speed_mps: f64,
    flight_time_seconds: f64,
    range_m: f64,
    damage: f64,
}

impl DefenderMissile {
    pub fn from_charge(charge: &Missile, damage: f64) -> Self {
        Self {
            speed_mps: charge.max_velocity,
            flight_time_seconds: charge.flight_time_ms / 1_000.0,
            range_m: charge.effective_range(),
            damage,
        }
    }

    pub fn intercept(&self, origin: [f64; 3], threat: &MissileThreat) -> Option<MissileInterception> {
        intercept_missile(origin, self.speed_mps, self.range_m, self.flight_time_seconds, self.damage, threat)
    }
}

/// One point-defense round. Its travel is limited by both projectile speed and module range.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct PointDefenseProjectile {
    pub projectile_speed_mps: f64,
    pub max_range_m: f64,
    pub damage: f64,
}

impl PointDefenseProjectile {
    /// Solve where a straight projectile meets the incoming missile's current trajectory.
    pub fn intercept(&self, origin: [f64; 3], threat: &MissileThreat) -> Option<MissileInterception> {
        let max_flight_seconds = self.max_range_m / self.projectile_speed_mps;
        intercept_missile(
            origin,
            self.projectile_speed_mps,
            self.max_range_m,
            max_flight_seconds,
            self.damage,
            threat,
        )
    }
}

fn intercept_missile(
    origin: [f64; 3],
    speed_mps: f64,
    range_m: f64,
    max_flight_seconds: f64,
    damage: f64,
    threat: &MissileThreat,
) -> Option<MissileInterception> {
    let finite = |values: &[f64]| values.iter().all(|value| value.is_finite());
    if !finite(&origin)
        || !finite(&threat.position)
        || !finite(&threat.velocity)
        || !finite(&[
            speed_mps,
            range_m,
            max_flight_seconds,
            damage,
            threat.radius_m,
            threat.hit_points,
            threat.time_to_target_seconds,
        ])
        || speed_mps <= 0.0
        || range_m < 0.0
        || max_flight_seconds < 0.0
        || damage <= 0.0
        || threat.radius_m < 0.0
        || threat.hit_points <= 0.0
        || threat.time_to_target_seconds <= 0.0
    {
        return None;
    }

    let relative_position =
        [threat.position[0] - origin[0], threat.position[1] - origin[1], threat.position[2] - origin[2]];
    let time_seconds = intercept_time(relative_position, threat.velocity, speed_mps, threat.radius_m)?;
    // The target missile's warhead resolves first if both events have the same timestamp.
    if !time_seconds.is_finite()
        || time_seconds < 0.0
        || time_seconds > max_flight_seconds
        || time_seconds >= threat.time_to_target_seconds
    {
        return None;
    }

    let distance_m = speed_mps * time_seconds;
    if !distance_m.is_finite() || distance_m > range_m {
        return None;
    }
    let position = [
        threat.position[0] + threat.velocity[0] * time_seconds,
        threat.position[1] + threat.velocity[1] * time_seconds,
        threat.position[2] + threat.velocity[2] * time_seconds,
    ];
    let remaining_hit_points = (threat.hit_points - damage).max(0.0);
    Some(MissileInterception {
        time_seconds,
        distance_m,
        position,
        remaining_hit_points,
        destroyed: remaining_hit_points <= 0.0,
    })
}

fn intercept_time(
    relative_position: [f64; 3],
    target_velocity: [f64; 3],
    speed_mps: f64,
    target_radius_m: f64,
) -> Option<f64> {
    let dot = |left: [f64; 3], right: [f64; 3]| left[0] * right[0] + left[1] * right[1] + left[2] * right[2];
    let distance_squared = dot(relative_position, relative_position);
    let radius_squared = target_radius_m * target_radius_m;
    if distance_squared <= radius_squared {
        return Some(0.0);
    }

    // |relative_position + target_velocity * t| = speed * t + target_radius
    let a = dot(target_velocity, target_velocity) - speed_mps * speed_mps;
    let b = 2.0 * (dot(relative_position, target_velocity) - speed_mps * target_radius_m);
    let c = distance_squared - radius_squared;
    if a.abs() <= 1e-12 {
        return (b < 0.0).then_some(-c / b);
    }

    let discriminant = b * b - 4.0 * a * c;
    if discriminant < 0.0 || !discriminant.is_finite() {
        return None;
    }
    let root = discriminant.sqrt();
    let first = (-b - root) / (2.0 * a);
    let second = (-b + root) / (2.0 * a);
    [first, second].into_iter().filter(|time| time.is_finite() && *time >= 0.0).min_by(f64::total_cmp)
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

    fn threat(
        position: [f64; 3],
        velocity: [f64; 3],
        hit_points: f64,
        time_to_target_seconds: f64,
    ) -> MissileThreat {
        MissileThreat { position, velocity, radius_m: 0.0, hit_points, time_to_target_seconds }
    }

    #[test]
    fn a_defender_missile_destroys_the_incoming_missile_before_its_target_is_hit() {
        let missile = DefenderMissile::from_charge(&rocket(), 90.0);
        let hit = missile.intercept([0.0; 3], &threat([3_000.0, 0.0, 0.0], [0.0; 3], 90.0, 3.0)).unwrap();

        assert_eq!(hit.time_seconds, 2.0);
        assert_eq!(hit.distance_m, 3_000.0);
        assert_eq!(hit.position, [3_000.0, 0.0, 0.0]);
        assert!(hit.destroyed);
        assert!(hit.target_spared());
    }

    #[test]
    fn a_point_defense_projectile_hits_at_its_range_boundary() {
        let target = MissileThreat { radius_m: 1.0, ..threat([101.0, 0.0, 0.0], [0.0; 3], 90.0, 3.0) };
        let shot = PointDefenseProjectile { projectile_speed_mps: 50.0, max_range_m: 100.0, damage: 90.0 };

        let hit = shot.intercept([0.0; 3], &target).unwrap();
        assert_eq!(hit.time_seconds, 2.0);
        assert_eq!(hit.distance_m, 100.0);
        assert_eq!(hit.position, [101.0, 0.0, 0.0]);
        assert!(hit.target_spared());

        let out_of_range = PointDefenseProjectile { max_range_m: 99.999, ..shot };
        assert!(out_of_range.intercept([0.0; 3], &target).is_none());
    }

    #[test]
    fn point_defense_leads_a_moving_missile() {
        let target =
            MissileThreat { radius_m: 1.0, ..threat([100.0, 0.0, 0.0], [10.0, 0.0, 0.0], 90.0, 4.0) };
        let shot = PointDefenseProjectile { projectile_speed_mps: 50.0, max_range_m: 124.0, damage: 90.0 };

        let hit = shot.intercept([0.0; 3], &target).unwrap();
        assert!((hit.time_seconds - 2.475).abs() < 1e-9);
        assert!((hit.distance_m - 123.75).abs() < 1e-9);
        assert!((hit.position[0] - 124.75).abs() < 1e-9);
    }

    #[test]
    fn an_interceptor_arriving_at_the_warhead_deadline_is_too_late() {
        let shot = PointDefenseProjectile { projectile_speed_mps: 50.0, max_range_m: 200.0, damage: 90.0 };
        let target = threat([100.0, 0.0, 0.0], [0.0; 3], 90.0, 2.0);

        assert!(shot.intercept([0.0; 3], &target).is_none());
    }

    #[test]
    fn a_defender_missile_expires_at_its_flight_time_boundary() {
        let target = threat([100.0, 0.0, 0.0], [0.0; 3], 90.0, 3.0);
        let charge = Missile { max_velocity: 50.0, flight_time_ms: 2_000.0, ..rocket() };
        let reaches_at_expiry = DefenderMissile::from_charge(&charge, 90.0);
        assert!(reaches_at_expiry.intercept([0.0; 3], &target).is_some());

        let expires_just_before = Missile { flight_time_ms: 1_999.0, ..charge };
        assert!(
            DefenderMissile::from_charge(&expires_just_before, 90.0).intercept([0.0; 3], &target).is_none()
        );
    }

    #[test]
    fn a_partial_defense_hit_does_not_spare_the_target() {
        let shot = PointDefenseProjectile { projectile_speed_mps: 100.0, max_range_m: 500.0, damage: 89.0 };
        let target = threat([200.0, 0.0, 0.0], [0.0; 3], 90.0, 3.0);
        let hit = shot.intercept([0.0; 3], &target).unwrap();

        assert_eq!(hit.remaining_hit_points, 1.0);
        assert!(!hit.destroyed);
        assert!(!hit.target_spared());
    }
}
