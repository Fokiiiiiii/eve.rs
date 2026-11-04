//! Stargates: where a warp to one ends, how near a ship has to be to jump, and where a ship that has jumped appears.
//!
//! The reference's rules (`warpTargets.js`, `transitions.js`), which the client cannot check for itself: a ship warping to a gate ends at a
//! random point of a half sphere on the side of the gate it comes from, and one that has jumped appears in a cone in front of the gate it
//! comes out of. Both draw random numbers; what matters to the client is where the points may be, so the draws are the scene's own.
use std::f64::consts::PI;

use eve_destiny::Vec3;

use crate::warp::PythonRandom;
use crate::world::{Body, Detail};

/// How far off a gate's surface a ship may be and still jump.
pub const JUMP_RANGE: f64 = 2_500.0;
/// How far the landing point of a warp to a gate may be from the gate's sphere.
const LANDING_SCATTER: f64 = 2_500.0;
/// How far off the gate's surface a ship that has jumped appears.
pub(crate) const ARRIVAL_CLEARANCE: f64 = 12_000.0;
/// The half angle of the cone in front of a gate that ships appear in.
const ARRIVAL_CONE: f64 = PI / 4.0;
/// The radius given to a gate whose type has none.
const DEFAULT_RADIUS: f64 = 15_000.0;
/// The nearest a ship may land to the point a warp is aimed at when the pilot asked for no range.
const MIN_STOP_DISTANCE: f64 = 1.0;

/// How far short of the point a warp is aimed at the ship stops, from one draw in `[0, 1)`: up to [`LANDING_SCATTER`], never less than a metre.
fn stop_for(draw: f64) -> f64 {
    (draw * LANDING_SCATTER).max(MIN_STOP_DISTANCE)
}

/// The size of a gate as ships meet it.
pub fn radius(gate: &Body) -> f64 {
    if gate.radius > 0.0 { gate.radius } else { DEFAULT_RADIUS }
}

fn add(a: Vec3, b: Vec3) -> Vec3 {
    Vec3::new(a.x + b.x, a.y + b.y, a.z + b.z)
}

fn scale(v: Vec3, by: f64) -> Vec3 {
    Vec3::new(v.x * by, v.y * by, v.z * by)
}

fn dot(a: Vec3, b: Vec3) -> f64 {
    a.x * b.x + a.y * b.y + a.z * b.z
}

fn cross(a: Vec3, b: Vec3) -> Vec3 {
    Vec3::new(a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x)
}

fn unit(v: Vec3, otherwise: Vec3) -> Vec3 {
    let length = dot(v, v).sqrt();
    if length.is_finite() && length > 0.0 { scale(v, 1.0 / length) } else { otherwise }
}

/// The way a gate faces: along the line from its system to the one it leads to (the reference derives it so, as the data gives no rotation of
/// a gate's own; the rotation the client is sent is that direction turned into yaw and pitch, which is not a way back to it), or away from
/// the star for a gate whose destination is not known.
pub fn forward(gate: &Body) -> Vec3 {
    if let Detail::Stargate { heading: Some(heading), .. } = &gate.detail {
        return unit(*heading, Vec3::new(1.0, 0.0, 0.0));
    }
    unit(gate.position, Vec3::new(1.0, 0.0, 0.0))
}

/// Where a warp to a gate is aimed and how far short of it the ship stops: `(raw destination, stop distance)`. The ship ends at a random point
/// of a half sphere as wide as [`LANDING_SCATTER`] set on the gate's sphere on the side it comes from (further out when the pilot asked for
/// more than the jump range), and the point warped to is a little beyond it, so the ship never comes out nearer than it was aimed.
pub fn warp_landing(
    gate: &Body,
    ship_position: Vec3,
    ship_direction: Vec3,
    range: f64,
    random: &mut PythonRandom,
) -> (Vec3, f64) {
    let stop = stop_for(random.random());
    let from_gate = unit(
        Vec3::new(
            ship_position.x - gate.position.x,
            ship_position.y - gate.position.y,
            ship_position.z - gate.position.z,
        ),
        unit(ship_direction, Vec3::new(1.0, 0.0, 0.0)),
    );
    let range = range.max(0.0);
    let surface = radius(gate) + if range > JUMP_RANGE { range } else { 0.0 };
    let envelope = add(gate.position, scale(from_gate, surface));

    // a random point of the sphere, folded onto the side the ship is on
    let theta = random.random() * PI * 2.0;
    let vertical = random.random() * 2.0 - 1.0;
    let reach = LANDING_SCATTER * random.random().cbrt();
    let planar = (1.0 - vertical * vertical).max(0.0).sqrt() * reach;
    let mut point = Vec3::new(theta.cos() * planar, vertical * reach, theta.sin() * planar);
    let side = dot(point, from_gate);
    if side < 0.0 {
        point = Vec3::new(
            point.x - from_gate.x * side * 2.0,
            point.y - from_gate.y * side * 2.0,
            point.z - from_gate.z * side * 2.0,
        );
    }
    let landing = add(envelope, point);
    (add(landing, scale(from_gate, -stop)), stop)
}

/// Where a ship that has jumped appears and which way it faces: along a direction in the cone in front of the gate, at the gate's radius, the
/// ship's radius and [`ARRIVAL_CLEARANCE`] from the gate's centre. The direction is uniform over the cap of the cone.
pub fn arrival(gate: &Body, ship_radius: f64, random: &mut PythonRandom) -> (Vec3, Vec3) {
    let facing = forward(gate);
    let cos_limit = ARRIVAL_CONE.cos();
    let cos_theta = 1.0 - random.random() * (1.0 - cos_limit);
    let sin_theta = (1.0 - cos_theta * cos_theta).max(0.0).sqrt();
    let phi = random.random() * PI * 2.0;
    let reference = if facing.x.abs() < 0.9 { Vec3::new(1.0, 0.0, 0.0) } else { Vec3::new(0.0, 1.0, 0.0) };
    let right = unit(cross(reference, facing), Vec3::new(0.0, 1.0, 0.0));
    let up = unit(cross(facing, right), Vec3::new(0.0, 0.0, 1.0));
    let direction = unit(
        add(
            scale(facing, cos_theta),
            add(scale(right, sin_theta * phi.cos()), scale(up, sin_theta * phi.sin())),
        ),
        facing,
    );
    let offset = radius(gate) + ship_radius.max(0.0) + ARRIVAL_CLEARANCE;
    (add(gate.position, scale(direction, offset)), direction)
}

/// How far a ship is from a gate's surface.
pub fn surface_distance(gate: &Body, ship_position: Vec3, ship_radius: f64) -> f64 {
    let (dx, dy, dz) = (
        ship_position.x - gate.position.x,
        ship_position.y - gate.position.y,
        ship_position.z - gate.position.z,
    );
    ((dx * dx + dy * dy + dz * dz).sqrt() - radius(gate) - ship_radius).max(0.0)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::world::BodyKind;

    fn gate() -> Body {
        Body {
            id: 50_016_630,
            type_id: 16_922,
            group_id: 10,
            category_id: 2,
            owner_id: 500_006,
            name: "Stargate (Test)".into(),
            kind: BodyKind::Stargate,
            position: Vec3::new(1.0e9, -2.0e8, 3.0e9),
            radius: 12_000.0,
            graphic_id: None,
            detail: Detail::Stargate {
                destination_system: 30_100_034,
                destination_gate: 50_016_839,
                destination_name: "Test".into(),
                destination_security: 0.5,
                destination_faction: None,
                hazardous: false,
                facing: Some([90.0, 0.0, 0.0]),
                heading: Some(Vec3::new(0.6, 0.0, 0.8)),
            },
        }
    }

    fn distance(a: Vec3, b: Vec3) -> f64 {
        dot(Vec3::new(a.x - b.x, a.y - b.y, a.z - b.z), Vec3::new(a.x - b.x, a.y - b.y, a.z - b.z)).sqrt()
    }

    fn close(a: Vec3, b: Vec3) -> bool {
        distance(a, b) < 1e-9
    }

    #[test]
    fn a_gate_faces_along_the_line_to_the_system_it_leads_to() {
        let facing = |heading: Option<Vec3>| {
            let mut gate = gate();
            let Detail::Stargate { heading: slot, .. } = &mut gate.detail else { unreachable!() };
            *slot = heading;
            forward(&gate)
        };
        assert!(close(facing(Some(Vec3::new(0.0, 0.6, 0.8))), Vec3::new(0.0, 0.6, 0.8)));
        assert!(
            close(facing(Some(Vec3::new(0.0, 3.0, 4.0))), Vec3::new(0.0, 0.6, 0.8)),
            "a vector that is not a unit one is made one"
        );
        let expected = unit(gate().position, Vec3::new(1.0, 0.0, 0.0));
        assert!(close(facing(None), expected), "no destination: the way out from the star");
    }

    #[test]
    fn a_ship_that_has_jumped_appears_in_the_cone_at_the_radii_and_the_clearance() {
        let gate = gate();
        for seed in 0..200 {
            let (position, direction) = arrival(&gate, 47.0, &mut PythonRandom::new(seed));
            assert!((dot(direction, direction) - 1.0).abs() < 1e-12);
            assert!(
                dot(direction, forward(&gate)) >= (PI / 4.0).cos() - 1e-12,
                "seed {seed} is outside the cone"
            );
            let gap = distance(position, gate.position);
            assert!((gap - (12_000.0 + 47.0 + 12_000.0)).abs() < 1e-6, "seed {seed}: {gap}");
            let along = add(gate.position, scale(direction, gap));
            assert!(distance(position, along) < 1e-6, "the point is along the direction");
        }
    }

    #[test]
    fn the_arrivals_fill_the_cone_rather_than_crowd_its_axis() {
        // uniform over the cap: the mean of cos(theta) is (1 + cos limit) / 2, not near 1
        let gate = gate();
        let mean = (0..2_000)
            .map(|seed| dot(arrival(&gate, 0.0, &mut PythonRandom::new(seed)).1, forward(&gate)))
            .sum::<f64>()
            / 2_000.0;
        let expected = (1.0 + (PI / 4.0).cos()) / 2.0;
        assert!((mean - expected).abs() < 0.01, "{mean} against {expected}");
    }

    #[test]
    fn a_warp_to_a_gate_ends_within_the_scatter_of_a_point_on_its_sphere_on_the_side_the_ship_comes_from() {
        let gate = gate();
        let ship = Vec3::new(-5.0e10, 1.0e10, -2.0e10);
        let from_gate = unit(
            Vec3::new(ship.x - gate.position.x, ship.y - gate.position.y, ship.z - gate.position.z),
            Vec3::ZERO,
        );
        for seed in 0..200 {
            let (raw, stop) =
                warp_landing(&gate, ship, Vec3::new(1.0, 0.0, 0.0), 0.0, &mut PythonRandom::new(seed));
            assert!((1.0..=2_500.0).contains(&stop), "stop distance {stop}");
            // the landing point: the aim pulled back towards the ship by the stop distance
            let landing = add(raw, scale(from_gate, stop));
            let envelope = add(gate.position, scale(from_gate, 12_000.0));
            assert!(distance(landing, envelope) <= 2_500.0 + 1e-6, "seed {seed}");
            assert!(
                dot(
                    Vec3::new(landing.x - envelope.x, landing.y - envelope.y, landing.z - envelope.z),
                    from_gate
                ) >= -1e-6,
                "seed {seed}: the point is on the gate's far side"
            );
            // and it is within jump range of the gate's surface
            assert!(surface_distance(&gate, landing, 0.0) <= JUMP_RANGE + 1e-6, "seed {seed}");
        }
    }

    #[test]
    fn a_range_within_the_jump_range_changes_nothing_and_one_beyond_it_sets_the_sphere_that_far_out() {
        let gate = gate();
        let ship = Vec3::new(-5.0e10, 0.0, 0.0);
        let land = |range: f64| warp_landing(&gate, ship, Vec3::ZERO, range, &mut PythonRandom::new(3));
        assert_eq!(land(0.0), land(1_000.0));
        assert_eq!(land(0.0), land(2_500.0), "the jump range itself is not beyond it");
        // the same draws, so the aims differ by the range alone: the sphere is the gate's radius plus the range out from the gate
        let (near, far) = (land(0.0), land(20_000.0));
        let moved = distance(near.0, far.0);
        assert!((moved - 20_000.0).abs() < 1e-3, "the sphere went 20 000 m further out: {moved} m");
        assert_eq!(near.1, far.1, "the stop distance is the same draw");
    }

    #[test]
    fn a_stop_distance_is_up_to_the_scatter_and_never_below_a_metre() {
        assert_eq!(stop_for(0.0), 1.0);
        assert_eq!(stop_for(0.0001), 1.0);
        assert_eq!(stop_for(0.5), 1_250.0);
        assert!(stop_for(0.999_999) < 2_500.0);
    }

    #[test]
    fn the_same_seed_gives_the_same_points() {
        let gate = gate();
        let ship = Vec3::new(-5.0e10, 1.0e10, -2.0e10);
        assert_eq!(
            warp_landing(&gate, ship, Vec3::ZERO, 0.0, &mut PythonRandom::new(9)),
            warp_landing(&gate, ship, Vec3::ZERO, 0.0, &mut PythonRandom::new(9))
        );
        assert_eq!(
            arrival(&gate, 30.0, &mut PythonRandom::new(9)),
            arrival(&gate, 30.0, &mut PythonRandom::new(9))
        );
    }

    #[test]
    fn range_is_measured_between_surfaces() {
        let gate = gate();
        let at = |gap: f64| add(gate.position, Vec3::new(12_000.0 + 47.0 + gap, 0.0, 0.0));
        assert!((surface_distance(&gate, at(2_500.0), 47.0) - 2_500.0).abs() < 1e-6);
        assert_eq!(surface_distance(&gate, gate.position, 47.0), 0.0, "inside is no distance");
        let mut bare = gate.clone();
        bare.radius = 0.0;
        assert_eq!(radius(&bare), 15_000.0, "a type with no radius has the default");
    }
}
