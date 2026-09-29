//! How a ship moves: it eases towards the speed it is asked for (the carbon integrator, as the client integrates it), along a heading that is
//! set, or that follows from a target (approaching it, circling it).
//!
//! The heading is the one asked for at once (docs/compat/DEVIATIONS.md #17). A course that depends on where the ship is (following,
//! orbiting) is worked out in steps of at most a quarter of a second.

use eve_destiny::{DESTINY_SPACE_FRICTION, Vec3, integrate_carbon_motion};

use crate::ship::{Course, Ship};

/// The longest step a course that depends on the ship's place is worked out in one go.
const MAX_STEP: f64 = 0.25;

/// What a ship follows or orbits: where it is and how big it is.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Target {
    pub position: Vec3,
    pub radius: f64,
}

fn sub(a: Vec3, b: Vec3) -> Vec3 {
    Vec3::new(a.x - b.x, a.y - b.y, a.z - b.z)
}

fn add(a: Vec3, b: Vec3) -> Vec3 {
    Vec3::new(a.x + b.x, a.y + b.y, a.z + b.z)
}

fn scale(a: Vec3, k: f64) -> Vec3 {
    Vec3::new(a.x * k, a.y * k, a.z * k)
}

fn length(a: Vec3) -> f64 {
    (a.x * a.x + a.y * a.y + a.z * a.z).sqrt()
}

fn cross(a: Vec3, b: Vec3) -> Vec3 {
    Vec3::new(a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x)
}

/// A vector of length 1 along `a`, if it has a direction.
pub fn unit(a: Vec3) -> Option<Vec3> {
    let l = length(a);
    (l.is_finite() && l > 1e-12).then(|| Vec3::new(a.x / l, a.y / l, a.z / l))
}

/// How long the ship takes to reach the speed it is asked for, in seconds: its mass times its inertia over the friction of space.
pub(crate) fn agility_seconds(ship: &Ship) -> f64 {
    (ship.spec.mass * ship.spec.agility / DESTINY_SPACE_FRICTION).max(0.05)
}

/// The speed the ship is asked to fly at, in metres a second.
fn cruise(ship: &Ship) -> f64 {
    ship.spec.max_velocity * ship.speed_fraction
}

/// Moves the ship for `seconds` under a thrust that carries it to `cruise` along `direction` (the acceleration is the steady speed over the time it takes).
pub(crate) fn integrate(ship: &mut Ship, thrust_direction: Vec3, speed: f64, seconds: f64) {
    let inertial_mass = ship.spec.mass * ship.spec.agility;
    let thrust = speed / agility_seconds(ship);
    let motion = integrate_carbon_motion(
        ship.position,
        ship.velocity,
        scale(thrust_direction, thrust),
        inertial_mass,
        DESTINY_SPACE_FRICTION,
        seconds,
    );
    ship.position = motion.position;
    ship.velocity = motion.velocity;
}

fn straight(ship: &mut Ship, seconds: f64) {
    let speed = match ship.course {
        Course::Goto => cruise(ship),
        _ => 0.0,
    };
    integrate(ship, ship.direction, speed, seconds);
}

/// How much of its range a following ship keeps: it settles a little inside it, so that a ship told to follow at the docking range is in it.
const FOLLOW_STANDOFF_SHARE: f64 = 0.999;
/// How many times the ship's reaction time it eases its approach over (the speed it asks for falls with its distance to the point it makes for).
const APPROACH_EASE: f64 = 3.0;
/// The distance and speed under which a following ship is taken to have arrived and is set down at the point.
const ARRIVED_DISTANCE: f64 = 1.0;
const ARRIVED_SPEED: f64 = 1.0;

/// One step of following: towards the point on the line from the target to the ship that is the standoff away from the target, at a speed that
/// falls as the point comes near, so that the ship comes to rest at it.
fn follow_step(ship: &mut Ship, range: f64, target: Target, seconds: f64) {
    let standoff = range * FOLLOW_STANDOFF_SHARE + ship.spec.radius + target.radius;
    let away = sub(ship.position, target.position);
    let outward = unit(away).unwrap_or(Vec3::new(1.0, 0.0, 0.0));
    let point = add(target.position, scale(outward, standoff));
    let separation = sub(point, ship.position);
    let distance = length(separation);
    if distance < ARRIVED_DISTANCE && length(ship.velocity) < ARRIVED_SPEED {
        ship.position = point;
        ship.velocity = Vec3::ZERO;
        return;
    }
    let Some(direction) = unit(separation) else {
        integrate(ship, ship.direction, 0.0, seconds);
        return;
    };
    ship.direction = direction;
    let speed = cruise(ship).min(distance / (APPROACH_EASE * agility_seconds(ship)));
    integrate(ship, direction, speed, seconds);
}

/// One step of orbiting: along the tangent of the circle about the target at the orbit's radius, drawn in or out to it.
fn orbit_step(ship: &mut Ship, distance: f64, target: Target, seconds: f64) {
    let radius = distance + ship.spec.radius + target.radius;
    let away = sub(ship.position, target.position);
    let outward = unit(away).unwrap_or(Vec3::new(1.0, 0.0, 0.0));
    let up = Vec3::new(0.0, 1.0, 0.0);
    let tangent = unit(cross(up, outward))
        .or_else(|| unit(cross(Vec3::new(1.0, 0.0, 0.0), outward)))
        .unwrap_or(ship.direction);
    let speed = cruise(ship);
    let radial_error = radius - length(away);
    let pull = scale(outward, radial_error.clamp(-speed, speed));
    let wanted = add(scale(tangent, speed), pull);
    let Some(direction) = unit(wanted) else {
        integrate(ship, ship.direction, 0.0, seconds);
        return;
    };
    ship.direction = direction;
    integrate(ship, direction, length(wanted).min(speed), seconds);
}

/// Moves `ship` for `seconds`. `target` is what the ship follows or orbits, when its course has one and the scene knows it.
pub fn advance(ship: &mut Ship, seconds: f64, target: Option<Target>) {
    // no time, a step back in time and not a number all leave the ship where it is
    if seconds.is_nan() || seconds <= 0.0 {
        return;
    }
    let mut remaining = seconds;
    match (ship.course, target) {
        (Course::Follow { range, .. }, Some(target)) => {
            while remaining > 1e-9 {
                let step = remaining.min(MAX_STEP);
                follow_step(ship, range, target, step);
                remaining -= step;
            }
        }
        (Course::Orbit { distance, .. }, Some(target)) => {
            while remaining > 1e-9 {
                let step = remaining.min(MAX_STEP);
                orbit_step(ship, distance, target, step);
                remaining -= step;
            }
        }
        _ => straight(ship, seconds),
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::ship::ShipSpec;

    fn ship(course: Course) -> Ship {
        Ship {
            spec: ShipSpec {
                ship_id: 1,
                character_id: 2,
                type_id: 601,
                group_id: 237,
                category_id: 6,
                name: "Ibis".to_string(),
                owner_id: 2,
                corporation_id: 3,
                alliance_id: 0,
                war_faction_id: 0,
                security_status: 0.0,
                radius: 47.0,
                mass: 1_163_000.0,
                max_velocity: 339.25,
                agility: 4.0185,
                warp_speed_au: 3.0,
                shield_recharge_ms: 860_000.0,
                modules: Vec::new(),
            },
            position: Vec3::ZERO,
            velocity: Vec3::ZERO,
            direction: Vec3::new(1.0, 0.0, 0.0),
            speed_fraction: 1.0,
            course,
        }
    }

    fn distance(ship: &Ship, target: Target) -> f64 {
        length(sub(ship.position, target.position))
    }

    #[test]
    fn a_ship_going_straight_covers_its_speed_times_the_time() {
        let mut s = ship(Course::Goto);
        s.velocity = Vec3::new(339.25, 0.0, 0.0);
        advance(&mut s, 10.0, None);
        assert!((s.position.x - 3392.5).abs() < 1e-6, "{}", s.position.x);
    }

    #[test]
    fn a_ship_following_a_target_closes_to_the_standoff_and_stays_there() {
        let target = Target { position: Vec3::new(50_000.0, 0.0, 0.0), radius: 1_000.0 };
        let mut s = ship(Course::Follow { target: 7, range: 2_500.0 });
        advance(&mut s, 200.0, Some(target));
        let standoff = 2_500.0 + 47.0 + 1_000.0;
        assert!((distance(&s, target) - standoff).abs() < 5.0, "{}", distance(&s, target));
        assert!(length(s.velocity) < 1.0, "it has stopped: {}", length(s.velocity));
        // and a long time later it is still there
        advance(&mut s, 200.0, Some(target));
        assert!((distance(&s, target) - standoff).abs() < 5.0);
    }

    #[test]
    fn a_ship_following_heads_for_its_target() {
        let target = Target { position: Vec3::new(0.0, 0.0, 90_000.0), radius: 0.0 };
        let mut s = ship(Course::Follow { target: 7, range: 0.0 });
        advance(&mut s, 1.0, Some(target));
        assert!(s.direction.z > 0.999, "{:?}", s.direction);
    }

    #[test]
    fn a_ship_orbiting_keeps_the_radius_it_was_asked_for() {
        let target = Target { position: Vec3::ZERO, radius: 500.0 };
        let radius = 5_000.0 + 47.0 + 500.0;
        let mut s = ship(Course::Orbit { target: 7, distance: 5_000.0 });
        s.position = Vec3::new(radius, 0.0, 0.0);
        s.velocity = Vec3::new(0.0, 0.0, 339.25);
        for _ in 0..240 {
            advance(&mut s, 1.0, Some(target));
            let error = (distance(&s, target) - radius).abs();
            assert!(error < radius * 0.05, "left the orbit by {error}");
        }
        // it has gone round: it is not where it began
        assert!(
            sub(s.position, Vec3::new(radius, 0.0, 0.0)).z.abs() > 100.0 || s.position.x < radius - 100.0
        );
    }

    #[test]
    fn a_ship_whose_target_is_unknown_slows_down_where_it_is() {
        let mut s = ship(Course::Follow { target: 7, range: 0.0 });
        s.velocity = Vec3::new(339.25, 0.0, 0.0);
        advance(&mut s, 60.0, None);
        assert!(length(s.velocity) < 0.01);
    }

    #[test]
    fn no_time_is_no_motion() {
        let mut s = ship(Course::Goto);
        advance(&mut s, 0.0, None);
        advance(&mut s, -1.0, None);
        advance(&mut s, f64::NAN, None);
        assert_eq!(s.position, Vec3::ZERO);
    }
}
