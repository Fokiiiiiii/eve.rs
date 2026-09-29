//! Carbon's analytic motion integration.
//!
//! The neutral analytic integration is adapted from carbonengine/destiny
//! v3.1.1, Copyright (c) 2026 CCP Games, MIT licence (the full text is in
//! `server/src/space/destiny/simulation/README.md`). This port follows the
//! JavaScript reference in `carbonIntegration.js` operation for operation,
//! including where it replaces non-finite values with fallbacks, so that its
//! results are bit-identical.

use crate::math::{exp, finite_or, js_max};

pub const CARBON_INTEGRATION_EPSILON: f64 = 1e-9;
/// Destiny's `DESTINY_SPACE_FRICTION`.
pub const DESTINY_SPACE_FRICTION: f64 = 1_000_000.0;

#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct Vec3 {
    pub x: f64,
    pub y: f64,
    pub z: f64,
}

impl Vec3 {
    pub const ZERO: Vec3 = Vec3 { x: 0.0, y: 0.0, z: 0.0 };

    pub const fn new(x: f64, y: f64, z: f64) -> Self {
        Self { x, y, z }
    }
}

/// Position and velocity after a step.
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct Motion {
    pub position: Vec3,
    pub velocity: Vec3,
}

// Every vector helper replaces a non-finite component of each operand with 0,
// exactly as the reference's `toFiniteNumber(v && v.x, 0)` does.
fn clean(vector: Vec3) -> Vec3 {
    Vec3::new(finite_or(vector.x, 0.0), finite_or(vector.y, 0.0), finite_or(vector.z, 0.0))
}

fn add(left: Vec3, right: Vec3) -> Vec3 {
    let (l, r) = (clean(left), clean(right));
    Vec3::new(l.x + r.x, l.y + r.y, l.z + r.z)
}

fn subtract(left: Vec3, right: Vec3) -> Vec3 {
    let (l, r) = (clean(left), clean(right));
    Vec3::new(l.x - r.x, l.y - r.y, l.z - r.z)
}

fn scale(vector: Vec3, scalar: f64) -> Vec3 {
    let (v, s) = (clean(vector), finite_or(scalar, 0.0));
    Vec3::new(v.x * s, v.y * s, v.z * s)
}

/// Advance a body by `seconds` under constant `acceleration` and space
/// friction (`integrateCarbonMotion`).
pub fn integrate_carbon_motion(
    position: Vec3,
    velocity: Vec3,
    acceleration: Vec3,
    inertial_mass: f64,
    friction: f64,
    seconds: f64,
) -> Motion {
    let step = js_max(0.0, finite_or(seconds, 0.0));
    if step <= 0.0 {
        return Motion { position: clean(position), velocity: clean(velocity) };
    }
    let mass = js_max(CARBON_INTEGRATION_EPSILON, finite_or(inertial_mass, 1.0));
    let drag = js_max(0.0, finite_or(friction, DESTINY_SPACE_FRICTION));
    let start_position = clean(position);
    let start_velocity = clean(velocity);
    let force = clean(acceleration);
    let lambda = drag / mass;

    if lambda <= 1e-12 {
        return Motion {
            position: add(start_position, add(scale(start_velocity, step), scale(force, 0.5 * step * step))),
            velocity: add(start_velocity, scale(force, step)),
        };
    }

    let time_factor = exp(-lambda * step);
    let equilibrium = scale(force, 1.0 / lambda);
    let offset = subtract(start_velocity, equilibrium);
    Motion {
        position: add(
            start_position,
            add(scale(equilibrium, step), scale(offset, (1.0 - time_factor) / lambda)),
        ),
        velocity: add(equilibrium, scale(offset, time_factor)),
    }
}

/// The constant acceleration that carries `start_velocity` to `end_velocity`
/// over `seconds` (`deriveCarbonIntrinsicAcceleration`).
pub fn derive_carbon_intrinsic_acceleration(
    start_velocity: Vec3,
    end_velocity: Vec3,
    inertial_mass: f64,
    friction: f64,
    seconds: f64,
) -> Vec3 {
    let step = js_max(CARBON_INTEGRATION_EPSILON, finite_or(seconds, 0.0));
    let mass = js_max(CARBON_INTEGRATION_EPSILON, finite_or(inertial_mass, 1.0));
    let drag = js_max(0.0, finite_or(friction, DESTINY_SPACE_FRICTION));
    let lambda = drag / mass;

    if lambda <= 1e-12 {
        return scale(subtract(clean(end_velocity), clean(start_velocity)), 1.0 / step);
    }
    let time_factor = exp(-lambda * step);
    let denominator = js_max(CARBON_INTEGRATION_EPSILON, 1.0 - time_factor);
    scale(subtract(clean(end_velocity), scale(clean(start_velocity), time_factor)), lambda / denominator)
}
