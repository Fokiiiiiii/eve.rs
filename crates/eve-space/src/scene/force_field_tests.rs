use eve_destiny::Vec3;

use super::force_field::{Access, BOUNDARY_CLEARANCE_METERS, NO_HARMONIC, denied_entry};
use super::tests::{NOON, SESSION, spec, system};
use super::{Scene, TICKS_PER_SECOND};
use crate::flight::Flight;
use crate::link::ForceFieldPlacement;
use crate::scene::SessionId;
use crate::ship::Course;

const TOWER: i64 = 1_050_000_099;
const FIELD: i64 = 1_050_000_100;
const FIELD_RADIUS: f64 = 100.0;
const PILOT_CORPORATION: i32 = 98_000_001;
const PILOT_ALLIANCE: i32 = 97_000_001;

/// The harmonic a pilot's ship has in the field: the tower it is admitted to inside, else none.
fn harmonic(scene: &Scene, session: SessionId) -> i64 {
    scene.force_field_harmonics.get(&session).copied().unwrap_or(NO_HARMONIC)
}

fn point(x: f64) -> Vec3 {
    Vec3::new(x, 0.0, 0.0)
}

/// A scene with one pilot 500 m out from a force field of `FIELD_RADIUS` at the origin, the pilot's corporation and alliance as set here.
fn field_scene() -> Scene {
    let mut scene = Scene::new(system());
    scene.enter_in_space(SESSION, spec(), [-500.0, 0.0, 0.0], [1.0, 0.0, 0.0], NOON).unwrap();
    {
        let ship = &mut scene.pilots.get_mut(&SESSION).unwrap().ship;
        ship.spec.corporation_id = PILOT_CORPORATION;
        ship.spec.alliance_id = PILOT_ALLIANCE;
    }
    let placement = ForceFieldPlacement {
        field_id: FIELD,
        tower_id: TOWER,
        solar_system_id: 30_000_001,
        type_id: 16_000,
        group_id: 411,
        name: "Force Field".to_string(),
        position: [0.0, 0.0, 0.0],
        radius: FIELD_RADIUS,
    };
    scene.place_force_field(placement, NOON).unwrap();
    scene
}

/// Moves the pilot to `to` at a speed of 1 000 m/s towards the field, from `from`, and runs the boundary on that move.
fn move_pilot(scene: &mut Scene, from: f64, to: f64) -> bool {
    {
        let ship = &mut scene.pilots.get_mut(&SESSION).unwrap().ship;
        ship.position = point(to);
        ship.velocity = Vec3::new(1_000.0, 0.0, 0.0);
        ship.direction = Vec3::new(1.0, 0.0, 0.0);
    }
    scene.enforce_force_fields(SESSION, point(from), false)
}

#[test]
fn a_move_that_crosses_the_sphere_is_stopped_on_its_surface() {
    assert_eq!(denied_entry(point(-200.0), point(-50.0), point(0.0), 100.0, false), Some(point(-100.0)));
}

#[test]
fn a_move_that_stays_outside_the_sphere_is_not_stopped() {
    assert_eq!(denied_entry(point(-200.0), point(-150.0), point(0.0), 100.0, false), None);
    assert_eq!(denied_entry(point(-200.0), point(-200.0), point(0.0), 100.0, false), None);
}

#[test]
fn a_ship_that_starts_inside_is_not_pulled_back_and_one_that_leaves_is_not_stopped() {
    assert_eq!(denied_entry(point(-50.0), point(-20.0), point(0.0), 100.0, false), Some(point(-20.0)));
    assert_eq!(denied_entry(point(-50.0), point(-150.0), point(0.0), 100.0, false), None);
}

#[test]
fn a_warp_that_completes_is_checked_only_at_its_end() {
    assert_eq!(denied_entry(point(-200.0), point(-50.0), point(0.0), 100.0, true), Some(point(-50.0)));
    assert_eq!(denied_entry(point(-200.0), point(-150.0), point(0.0), 100.0, true), None);
}

#[test]
fn access_admits_by_corporation_or_alliance_only_where_the_tower_allows_it() {
    let access = Access { corporation_id: 5, alliance_id: 6, allow_corporation: true, allow_alliance: false };
    assert!(access.admits_group(5, 0));
    assert!(!access.admits_group(0, 6), "the alliance is not admitted when the tower does not allow it");
    assert!(!access.admits_group(7, 0));
    let no_corporation =
        Access { corporation_id: 0, alliance_id: 0, allow_corporation: true, allow_alliance: true };
    assert!(!no_corporation.admits_group(0, 0), "a zero id admits no ship by that group");
}

#[test]
fn a_ship_the_field_does_not_admit_is_stopped_on_the_boundary_and_pushed_out() {
    let mut scene = field_scene();
    assert!(move_pilot(&mut scene, -500.0, -50.0));
    let boundary = FIELD_RADIUS + spec().radius + BOUNDARY_CLEARANCE_METERS;
    let ship = &scene.pilots[&SESSION].ship;
    assert!((ship.position.x + boundary).abs() < 1e-6, "stopped on the boundary, at {}", ship.position.x);
    // A ship that enters from outside is always in EVE.js's boundary contact band (its start is at or beyond the boundary), so its outward speed
    // is 10% of the 1 000 m/s inward speed, capped at 100 m/s: 1 000 m/s inward ends at -100 m/s. The 500 m/s push is for a ship stranded inside.
    assert!(
        (ship.velocity.x + 100.0).abs() < 1e-6,
        "pushed out at 100 m/s on contact, got {}",
        ship.velocity.x
    );
    assert_eq!(harmonic(&scene, SESSION), NO_HARMONIC);
}

#[test]
fn an_admitted_corporation_passes_the_field_and_takes_the_towers_harmonic() {
    let mut scene = field_scene();
    scene.set_force_field_access(
        TOWER,
        Access {
            corporation_id: PILOT_CORPORATION,
            alliance_id: 0,
            allow_corporation: true,
            allow_alliance: false,
        },
    );
    assert!(!move_pilot(&mut scene, -500.0, -50.0));
    assert_eq!(scene.pilots[&SESSION].ship.position.x, -50.0);
    assert_eq!(harmonic(&scene, SESSION), TOWER);
}

#[test]
fn a_corporation_the_tower_does_not_allow_is_stopped_and_a_password_admits_the_ship() {
    let mut scene = field_scene();
    scene.set_force_field_access(
        TOWER,
        Access {
            corporation_id: PILOT_CORPORATION + 1,
            alliance_id: 0,
            allow_corporation: true,
            allow_alliance: false,
        },
    );
    assert!(move_pilot(&mut scene, -500.0, -50.0), "another corporation is stopped");
    scene.set_force_field_password(SESSION, TOWER, true);
    assert!(!move_pilot(&mut scene, -500.0, -50.0), "the matching password admits the ship");
    assert_eq!(harmonic(&scene, SESSION), TOWER);
    scene.set_force_field_password(SESSION, TOWER, false);
    assert!(move_pilot(&mut scene, -500.0, -50.0), "a password that no longer matches stops the ship again");
    assert_eq!(harmonic(&scene, SESSION), NO_HARMONIC);
}

#[test]
fn an_alliance_is_admitted_only_when_the_tower_allows_the_alliance() {
    let mut scene = field_scene();
    scene.set_force_field_access(
        TOWER,
        Access {
            corporation_id: PILOT_CORPORATION + 1,
            alliance_id: PILOT_ALLIANCE,
            allow_corporation: true,
            allow_alliance: false,
        },
    );
    assert!(move_pilot(&mut scene, -500.0, -50.0), "an alliance the tower does not allow is stopped");
    scene.set_force_field_access(
        TOWER,
        Access {
            corporation_id: PILOT_CORPORATION + 1,
            alliance_id: PILOT_ALLIANCE,
            allow_corporation: true,
            allow_alliance: true,
        },
    );
    assert!(!move_pilot(&mut scene, -500.0, -50.0), "an alliance the tower allows is admitted");
}

#[test]
fn a_field_with_no_access_admits_no_ship_by_group() {
    let mut scene = field_scene();
    assert!(move_pilot(&mut scene, -500.0, -50.0));
    assert_eq!(harmonic(&scene, SESSION), NO_HARMONIC);
}

#[test]
fn an_order_that_pulls_the_ship_back_in_is_cancelled_and_an_outward_one_stays() {
    let mut scene = field_scene();
    scene.pilots.get_mut(&SESSION).unwrap().ship.course = Course::GotoPoint { point: point(50.0) };
    assert!(move_pilot(&mut scene, -500.0, -50.0));
    assert_eq!(scene.pilots[&SESSION].ship.course, Course::Stop);

    let mut outward = field_scene();
    outward.pilots.get_mut(&SESSION).unwrap().ship.course = Course::GotoPoint { point: point(-1_000.0) };
    assert!(move_pilot(&mut outward, -500.0, -50.0));
    assert_eq!(outward.pilots[&SESSION].ship.course, Course::GotoPoint { point: point(-1_000.0) });
}

#[test]
fn a_ship_stranded_inside_is_pushed_out_at_the_repulsion_speed() {
    let mut scene = field_scene();
    {
        let ship = &mut scene.pilots.get_mut(&SESSION).unwrap().ship;
        ship.position = point(-10.0);
        ship.velocity = Vec3::new(0.0, 0.0, 0.0);
    }
    assert!(scene.enforce_force_fields(SESSION, point(-10.0), false));
    let ship = &scene.pilots[&SESSION].ship;
    assert_eq!(ship.position.x, -10.0, "a ship already inside is not moved back to the boundary");
    assert!((ship.velocity.x + 500.0).abs() < 1e-6, "pushed out at 500 m/s, got {}", ship.velocity.x);
}

/// A pilot that leaves the scene takes its password and harmonic with it when the scene next settles.
#[test]
fn a_pilot_that_leaves_takes_its_password_and_harmonic_with_it() {
    let mut scene = field_scene();
    scene.set_force_field_password(SESSION, TOWER, true);
    scene.set_force_field_access(
        TOWER,
        Access {
            corporation_id: PILOT_CORPORATION,
            alliance_id: 0,
            allow_corporation: true,
            allow_alliance: false,
        },
    );
    assert!(!move_pilot(&mut scene, -500.0, -50.0));
    assert_eq!(harmonic(&scene, SESSION), TOWER);
    scene.pilots.remove(&SESSION);
    scene.prune_force_field_sessions();
    assert!(scene.force_field_passwords.is_empty());
    assert!(scene.force_field_harmonics.is_empty());
}

#[test]
fn a_ship_in_warp_is_not_stopped_by_a_field() {
    let mut scene = field_scene();
    scene.pilots.get_mut(&SESSION).unwrap().flight = Some(Flight::begin(NOON, point(0.0), 0.0));
    assert!(!move_pilot(&mut scene, -500.0, -50.0));
    assert_eq!(scene.pilots[&SESSION].ship.position.x, -50.0);
}

/// The scene's own settlement, not a direct call, holds a pilot out of the field: a pilot flying at the field for a second is stopped on its boundary.
#[test]
fn a_pilot_the_scene_settles_is_held_outside_the_field() {
    let mut scene = field_scene();
    {
        let ship = &mut scene.pilots.get_mut(&SESSION).unwrap().ship;
        ship.position = point(-500.0);
        ship.velocity = Vec3::new(1_000.0, 0.0, 0.0);
        ship.direction = Vec3::new(1.0, 0.0, 0.0);
        ship.course = Course::Goto;
        ship.speed_fraction = 1.0;
    }
    scene.settle_all(NOON + TICKS_PER_SECOND);
    let boundary = FIELD_RADIUS + spec().radius + BOUNDARY_CLEARANCE_METERS;
    let position = scene.pilots[&SESSION].ship.position;
    let distance = (position.x * position.x + position.y * position.y + position.z * position.z).sqrt();
    assert!(
        distance >= boundary - 1e-6,
        "held at the boundary, not inside the field: {} < {}",
        distance,
        boundary
    );
    assert_eq!(harmonic(&scene, SESSION), NO_HARMONIC);
}
