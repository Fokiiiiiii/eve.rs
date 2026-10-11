//! How a ship moves: it eases towards the speed it is asked for (the carbon integrator, as the client integrates it), along a heading that is
//! set, or that follows from a target (approaching it, circling it).
//!
//! The heading is the one asked for at once (docs/reference/compat/DEVIATIONS.md #17). A course that depends on where the ship is (following,
//! orbiting) is worked out in steps of at most a quarter of a second.

use eve_destiny::{DESTINY_SPACE_FRICTION, Vec3, integrate_carbon_motion};

use crate::ship::{Course, Ship};

/// The longest step a course that depends on the ship's place is worked out in one go.
const MAX_STEP: f64 = 0.25;

/// What a ship follows or orbits: where it is, how it moves and how big it is.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Target {
    pub position: Vec3,
    pub radius: f64,
    /// Zero for a body; a ship that is followed is followed as it moves (the follower keeps up instead of lagging by the time it takes to ease in).
    pub velocity: Vec3,
}

impl Target {
    /// Something that does not move.
    pub fn at_rest(position: Vec3, radius: f64) -> Self {
        Self { position, radius, velocity: Vec3::ZERO }
    }
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

/// What a body moves with: where it is, how it moves and what moves it. A ship has these from its spec and its motion; a wreck or a can that a tractor beam
/// pulls has the mass and top speed the beam gives it (EVE.js `applyTractorActivationCommand`: mass 10, the beam's speed, speed fraction 1).
#[derive(Debug, Clone, Copy, PartialEq)]
pub(crate) struct Kinematics {
    pub position: Vec3,
    pub velocity: Vec3,
    /// A unit vector.
    pub direction: Vec3,
    pub speed_fraction: f64,
    pub radius: f64,
    pub mass: f64,
    pub agility: f64,
    pub max_velocity: f64,
}

impl Kinematics {
    /// The motion of a ship, as the steps read and write it.
    pub(crate) fn of_ship(ship: &Ship) -> Self {
        Self {
            position: ship.position,
            velocity: ship.velocity,
            direction: ship.direction,
            speed_fraction: ship.speed_fraction,
            radius: ship.spec.radius,
            mass: ship.spec.mass,
            agility: ship.spec.agility,
            max_velocity: ship.spec.max_velocity,
        }
    }

    /// Puts the place, velocity and heading that the steps made back into the ship.
    pub(crate) fn write_ship(self, ship: &mut Ship) {
        ship.position = self.position;
        ship.velocity = self.velocity;
        ship.direction = self.direction;
    }
}

/// How long a body takes to reach the speed it is asked for, in seconds: its mass times its inertia over the friction of space.
pub(crate) fn agility_seconds(body: &Kinematics) -> f64 {
    (body.mass * body.agility / DESTINY_SPACE_FRICTION).max(0.05)
}

/// The speed the body is asked to fly at, in metres a second.
fn cruise(body: &Kinematics) -> f64 {
    body.max_velocity * body.speed_fraction
}

/// Moves the body for `seconds` under a thrust that carries it to `speed` along `thrust_direction` (the acceleration is the steady speed over the time
/// it takes).
pub(crate) fn integrate(body: &mut Kinematics, thrust_direction: Vec3, speed: f64, seconds: f64) {
    let inertial_mass = body.mass * body.agility;
    // the thrust whose equilibrium is `speed` (drag over inertia), not clamped to the agility floor of `agility_seconds`: a light body (a wreck a
    // beam pulls, 10 kg) is not held to the floor a ship's inertia never reaches
    let thrust = speed * DESTINY_SPACE_FRICTION / inertial_mass.max(1e-9);
    let motion = integrate_carbon_motion(
        body.position,
        body.velocity,
        scale(thrust_direction, thrust),
        inertial_mass,
        DESTINY_SPACE_FRICTION,
        seconds,
    );
    body.position = motion.position;
    body.velocity = motion.velocity;
}

/// [`integrate`] for a ship: its own numbers, and its place, velocity and heading written back.
pub(crate) fn integrate_ship(ship: &mut Ship, thrust_direction: Vec3, speed: f64, seconds: f64) {
    let mut body = Kinematics::of_ship(ship);
    integrate(&mut body, thrust_direction, speed, seconds);
    body.write_ship(ship);
}

/// How much of its range a following body keeps: it settles a little inside it, so that a body told to follow at the docking range is in it.
const FOLLOW_STANDOFF_SHARE: f64 = 0.999;
/// How many times the body's reaction time it eases its approach over (the speed it asks for falls with its distance to the point it makes for).
const APPROACH_EASE: f64 = 3.0;
/// The distance and speed under which a following body is taken to have arrived and is set down at the point.
const ARRIVED_DISTANCE: f64 = 1.0;
const ARRIVED_SPEED: f64 = 1.0;

/// One step of following: towards the point on the line from the target to the body that is the standoff away from the target, at a speed that falls
/// as the point comes near, so that the body comes to rest at it.
fn follow_step(body: &mut Kinematics, range: f64, target: Target, seconds: f64) {
    let standoff = range * FOLLOW_STANDOFF_SHARE + body.radius + target.radius;
    approach_step(body, standoff, target, seconds);
}

/// One step of approaching a point `standoff` metres from the target's centre (the follow step, with the standoff given; a goto point is a target at
/// rest with a standoff of nothing, so the body stops on the point).
fn approach_step(body: &mut Kinematics, standoff: f64, target: Target, seconds: f64) {
    let away = sub(body.position, target.position);
    let outward = unit(away).unwrap_or(Vec3::new(1.0, 0.0, 0.0));
    let point = add(target.position, scale(outward, standoff));
    let separation = sub(point, body.position);
    let distance = length(separation);
    if distance < ARRIVED_DISTANCE && length(body.velocity) < ARRIVED_SPEED {
        body.position = point;
        body.velocity = Vec3::ZERO;
        return;
    }
    let Some(direction) = unit(separation) else {
        integrate(body, body.direction, 0.0, seconds);
        return;
    };
    body.direction = direction;
    let closing = cruise(body).min(distance / (APPROACH_EASE * agility_seconds(body)));
    // a target that moves is kept up with: the body flies at the speed it closes at plus the target's own
    let wanted = add(scale(direction, closing), target.velocity);
    let speed = length(wanted).min(cruise(body));
    let Some(course) = unit(wanted) else {
        integrate(body, direction, 0.0, seconds);
        return;
    };
    body.direction = course;
    integrate(body, course, speed, seconds);
}

/// One step of orbiting: along the tangent of the circle about the target at the orbit's radius, drawn in or out to it.
fn orbit_step(body: &mut Kinematics, distance: f64, target: Target, seconds: f64) {
    let radius = distance + body.radius + target.radius;
    let away = sub(body.position, target.position);
    let outward = unit(away).unwrap_or(Vec3::new(1.0, 0.0, 0.0));
    let up = Vec3::new(0.0, 1.0, 0.0);
    let tangent = unit(cross(up, outward))
        .or_else(|| unit(cross(Vec3::new(1.0, 0.0, 0.0), outward)))
        .unwrap_or(body.direction);
    let speed = cruise(body);
    let radial_error = radius - length(away);
    let pull = scale(outward, radial_error.clamp(-speed, speed));
    let wanted = add(add(scale(tangent, speed), pull), target.velocity);
    let Some(direction) = unit(wanted) else {
        integrate(body, body.direction, 0.0, seconds);
        return;
    };
    body.direction = direction;
    integrate(body, direction, length(wanted).min(speed), seconds);
}

/// Where a target that moves was `remaining` seconds before the place it is given at (a step steers by the positions at its start, as the body
/// and the target then are the same time apart).
fn target_at_start(target: Target, remaining: f64) -> Target {
    Target { position: sub(target.position, scale(target.velocity, remaining)), ..target }
}

/// Moves a body that a tractor beam pulls for `seconds`: it follows its puller at `range` (the follow step, in steps of at most [`MAX_STEP`]).
pub(crate) fn pull(body: &mut Kinematics, range: f64, target: Target, seconds: f64) {
    let mut remaining = seconds;
    while remaining > 1e-9 {
        let step = remaining.min(MAX_STEP);
        follow_step(body, range, target_at_start(target, remaining), step);
        remaining -= step;
    }
}

/// Moves `ship` for `seconds`. `target` is what the ship follows or orbits, when its course has one and the scene knows it.
pub fn advance(ship: &mut Ship, seconds: f64, target: Option<Target>) {
    // no time, a step back in time and not a number all leave the ship where it is
    if seconds.is_nan() || seconds <= 0.0 {
        return;
    }
    let mut body = Kinematics::of_ship(ship);
    match (ship.course, target) {
        (Course::Follow { range, .. }, Some(target)) => pull(&mut body, range, target, seconds),
        (Course::Orbit { distance, .. }, Some(target)) => {
            let mut remaining = seconds;
            while remaining > 1e-9 {
                let step = remaining.min(MAX_STEP);
                orbit_step(&mut body, distance, target_at_start(target, remaining), step);
                remaining -= step;
            }
        }
        (Course::GotoPoint { point }, _) => {
            // the point is a place at rest: the body flies to its centre and stays there
            let goal = Target::at_rest(point, 0.0);
            let mut remaining = seconds;
            while remaining > 1e-9 {
                let step = remaining.min(MAX_STEP);
                approach_step(&mut body, 0.0, goal, step);
                remaining -= step;
            }
        }
        _ => {
            let speed = match ship.course {
                Course::Goto => cruise(&body),
                _ => 0.0,
            };
            integrate(&mut body, ship.direction, speed, seconds);
        }
    }
    body.write_ship(ship);
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::ship::ShipSpec;

    fn ship(course: Course) -> Ship {
        let spec = ShipSpec {
            stance: None,
            ship_id: 1,
            character_id: 2,
            owner_id: 2,
            corporation_id: 3,
            modules: Vec::new(),
            ..crate::scene::tests::spec()
        };
        Ship::new(spec, Vec3::ZERO, Vec3::ZERO, Vec3::new(1.0, 0.0, 0.0), 1.0, course, 0)
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
        let target = Target::at_rest(Vec3::new(50_000.0, 0.0, 0.0), 1_000.0);
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
    fn a_ship_sent_to_a_point_flies_there_and_stops_on_it() {
        let point = Vec3::new(20_000.0, 0.0, 0.0);
        let mut s = ship(Course::GotoPoint { point });
        s.speed_fraction = 1.0;
        advance(&mut s, 120.0, None);
        // it settles on the point as a follower settles on its standoff (a few metres, as the follow test allows)
        assert!(distance(&s, Target::at_rest(point, 0.0)) < 5.0, "{:?}", s.position);
        assert!(length(s.velocity) < 1.0, "it has stopped: {}", length(s.velocity));
        // and a long time later it is still there; a point it has not reached is not reached in one tick
        advance(&mut s, 120.0, None);
        assert!(distance(&s, Target::at_rest(point, 0.0)) < 5.0);
        let mut far = ship(Course::GotoPoint { point });
        far.speed_fraction = 1.0;
        advance(&mut far, 1.0, None);
        assert!(distance(&far, Target::at_rest(point, 0.0)) > 1_000.0, "{:?}", far.position);
    }

    #[test]
    fn a_ship_following_heads_for_its_target() {
        let target = Target::at_rest(Vec3::new(0.0, 0.0, 90_000.0), 0.0);
        let mut s = ship(Course::Follow { target: 7, range: 0.0 });
        advance(&mut s, 1.0, Some(target));
        assert!(s.direction.z > 0.999, "{:?}", s.direction);
    }

    #[test]
    fn a_ship_orbiting_keeps_the_radius_it_was_asked_for() {
        let target = Target::at_rest(Vec3::ZERO, 500.0);
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
    fn a_ship_following_a_target_that_moves_keeps_up_instead_of_lagging_behind_it() {
        let mut s = ship(Course::Follow { target: 7, range: 500.0 });
        s.spec.max_velocity = 500.0;
        let mut target = Target {
            position: Vec3::new(3_000.0, 0.0, 0.0),
            radius: 30.0,
            velocity: Vec3::new(339.0, 0.0, 0.0),
        };
        for _ in 0..960 {
            target.position.x += 339.0 * 0.25;
            advance(&mut s, 0.25, Some(target));
        }
        let gap = distance(&s, target);
        let standoff = 500.0 * FOLLOW_STANDOFF_SHARE + 47.0 + 30.0;
        assert!((gap - standoff).abs() < 50.0, "it holds its range: {gap} for {standoff}");
        assert!((s.velocity.x - 339.0).abs() < 5.0, "at the target's speed: {:?}", s.velocity);
    }

    #[test]
    fn a_ship_that_is_slower_than_the_target_it_follows_falls_behind() {
        let mut s = ship(Course::Follow { target: 7, range: 500.0 });
        s.spec.max_velocity = 200.0;
        let mut target = Target {
            position: Vec3::new(1_000.0, 0.0, 0.0),
            radius: 30.0,
            velocity: Vec3::new(339.0, 0.0, 0.0),
        };
        for _ in 0..240 {
            target.position.x += 339.0 * 0.25;
            advance(&mut s, 0.25, Some(target));
        }
        assert!(distance(&s, target) > 10_000.0);
        assert!(length(s.velocity) <= 200.0 + 1.0);
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
