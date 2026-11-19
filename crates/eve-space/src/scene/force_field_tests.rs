use eve_destiny::Vec3;

use super::force_field::{Access, BOUNDARY_CLEARANCE_METERS, NO_HARMONIC, denied_entry};
use super::tests::{NOON, SESSION, spec, system};
use super::{Scene, TICKS_PER_SECOND};
use crate::deployable::{Role, ball_harmonic};
use crate::flight::Flight;
use crate::link::ForceFieldPlacement;
use crate::scene::Out;
use crate::scene::SessionId;
use crate::ship::Course;
use eve_destiny::IdRef;
use eve_marshal::Value;

const TOWER: i64 = 1_050_000_099;
const FIELD: i64 = 1_050_000_100;
const OTHER_FIELD: i64 = 1_050_000_101;
const FIELD_RADIUS: f64 = 100.0;
const PILOT_CORPORATION: i32 = 98_000_001;
const PILOT_ALLIANCE: i32 = 97_000_001;

/// The action names a notice to a client carries, in order.
fn action_names(out: &Out) -> Vec<String> {
    let Out::Notify { args, .. } = out else { return Vec::new() };
    let Some(Value::List(actions)) = args.first() else { return Vec::new() };
    actions
        .iter()
        .filter_map(|action| match action {
            Value::Tuple(pair) => match &pair[1] {
                Value::Tuple(inner) => match &inner[0] {
                    Value::Str(name) => Some(name.to_string()),
                    _ => None,
                },
                _ => None,
            },
            _ => None,
        })
        .collect()
}

/// Moves the pilot from `from` to `to` at 1 000 m/s towards the field, runs the boundary and tells what it did, as the settle path does; returns the
/// action names told to the clients, in order.
fn move_and_tell(scene: &mut Scene, from: f64, to: f64) -> Vec<String> {
    scene.backlog.clear();
    let harmonic_before = scene.force_field_harmonics.get(&SESSION).copied().unwrap_or(NO_HARMONIC);
    {
        let ship = &mut scene.pilots.get_mut(&SESSION).unwrap().ship;
        ship.position = point(to);
        ship.velocity = Vec3::new(1_000.0, 0.0, 0.0);
        ship.direction = Vec3::new(1.0, 0.0, 0.0);
    }
    let correction = scene.enforce_force_fields(SESSION, point(from), false);
    scene.publish_force_field(SESSION, NOON, harmonic_before, correction);
    scene.backlog.iter().flat_map(action_names).collect()
}

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
    // a bound pilot is told what happens to its ship (an unbound one only when someone watches it)
    scene.pilots.get_mut(&SESSION).unwrap().bound = true;
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
    scene.enforce_force_fields(SESSION, point(from), false).is_some()
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
    assert!(scene.enforce_force_fields(SESSION, point(-10.0), false).is_some());
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

/// A stopped ship is told its position and its velocity, and nothing about its harmonic, since it has none to show.
/// Whether the host was told the pilot's ship harmonic as `harmonic` (protocol 105), in the scene's output of this move.
fn told_harmonic(outs: &[Out], harmonic: i64) -> bool {
    outs.iter().any(|out| {
        matches!(out, Out::ShipForceFieldHarmonic { session, ship_id, harmonic: told }
            if *session == SESSION && *ship_id == spec().ship_id && *told == harmonic)
    })
}

/// A password set or cleared while the pilot is inside the field takes effect at once, without a move: the harmonic follows and the host is told each one.
#[test]
fn a_password_change_inside_the_field_takes_effect_at_once_and_the_host_is_told() {
    let mut scene = field_scene();
    // inside the field, which does not admit the pilot yet: no harmonic
    scene.pilots.get_mut(&SESSION).unwrap().ship.position = point(-50.0);
    scene.refresh_force_field_harmonic(SESSION, NOON);
    assert_eq!(harmonic(&scene, SESSION), NO_HARMONIC);

    scene.set_force_field_password(SESSION, TOWER, true);
    let told = scene.refresh_force_field_harmonic(SESSION, NOON);
    assert_eq!(harmonic(&scene, SESSION), TOWER);
    assert!(told_harmonic(&told, TOWER), "the host is told the admitted harmonic");

    scene.set_force_field_password(SESSION, TOWER, false);
    let told = scene.refresh_force_field_harmonic(SESSION, NOON);
    assert_eq!(harmonic(&scene, SESSION), NO_HARMONIC);
    assert!(told_harmonic(&told, NO_HARMONIC), "the host is told the harmonic is gone");
}

/// A ship admitted inside the field that moves out of it loses its harmonic on that move, and the host is told so.
#[test]
fn a_ship_admitted_inside_that_leaves_the_field_loses_its_harmonic_and_the_host_is_told() {
    let mut scene = field_scene();
    scene.set_force_field_password(SESSION, TOWER, true);
    scene.pilots.get_mut(&SESSION).unwrap().ship.position = point(-50.0);
    scene.refresh_force_field_harmonic(SESSION, NOON);
    assert_eq!(harmonic(&scene, SESSION), TOWER);

    let names = move_and_tell(&mut scene, -50.0, -150.0);
    assert_eq!(harmonic(&scene, SESSION), NO_HARMONIC, "outside the field it has no harmonic");
    assert_eq!(
        names,
        vec!["SetBallHarmonic".to_string()],
        "the clients are told the harmonic, and no correction"
    );
    assert!(told_harmonic(&scene.backlog, NO_HARMONIC));
}

/// A pilot admitted inside the field by its corporation loses the harmonic when its ship is given again without that corporation (the pilot left it while flying):
/// the host is told, and the field no longer admits the pilot.
#[test]
fn a_pilot_who_leaves_the_admitting_corporation_inside_the_field_loses_its_harmonic_when_its_ship_is_given_again() {
    let mut scene = field_scene();
    scene.set_force_field_access(TOWER, Access { corporation_id: PILOT_CORPORATION, alliance_id: 0, allow_corporation: true, allow_alliance: false });
    scene.pilots.get_mut(&SESSION).unwrap().ship.position = point(-50.0);
    scene.refresh_force_field_harmonic(SESSION, NOON);
    assert_eq!(harmonic(&scene, SESSION), TOWER);

    // the host gives the ship again with the pilot's corporation now another one (protocol 8, as it does on an affiliation change)
    let mut spec = scene.pilots.get(&SESSION).unwrap().ship.spec.clone();
    spec.corporation_id = PILOT_CORPORATION + 1;
    let told = scene.respec(SESSION, spec, NOON);
    assert_eq!(harmonic(&scene, SESSION), NO_HARMONIC, "the corporation that admitted the pilot is no longer its own");
    assert!(told_harmonic(&told, NO_HARMONIC), "the host is told the harmonic is gone");
}

#[test]
fn a_stopped_ship_is_told_its_position_and_velocity_but_no_harmonic() {
    let mut scene = field_scene();
    scene.pilots.get_mut(&SESSION).unwrap().ship.course = Course::Stop;
    let told = move_and_tell(&mut scene, -500.0, -50.0);
    assert!(told.iter().any(|name| name == "SetBallPosition"));
    assert!(told.iter().any(|name| name == "SetBallVelocity"));
    assert!(!told.iter().any(|name| name == "SetBallHarmonic"));
}

/// An order that is cancelled is told first (the speed fraction and the stop), then the position and the velocity, as EVE.js orders it.
#[test]
fn a_cancelled_order_is_told_before_the_position_and_velocity() {
    let mut scene = field_scene();
    scene.pilots.get_mut(&SESSION).unwrap().ship.course = Course::GotoPoint { point: point(50.0) };
    let told = move_and_tell(&mut scene, -500.0, -50.0);
    assert_eq!(told[..4], ["SetSpeedFraction", "Stop", "SetBallPosition", "SetBallVelocity"]);
}

/// An admitted ship that comes inside is told the tower's harmonic, and it is not stopped.
#[test]
fn an_admitted_ship_inside_is_told_the_towers_harmonic() {
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
    let told = move_and_tell(&mut scene, -500.0, -50.0);
    assert!(told.iter().any(|name| name == "SetBallHarmonic"));
    assert!(!told.iter().any(|name| name == "SetBallPosition"));
}

/// A field carries the time it was placed at as `forcefieldStartTime`, which the client animates from (`forceField.py`), and a field placed again starts again.
#[test]
fn a_force_field_carries_the_time_it_was_placed_at_for_the_client() {
    let mut scene = field_scene();
    let start = |scene: &Scene| scene.deployables[&FIELD].slim_fields.get("forcefieldStartTime").cloned();
    assert_eq!(start(&scene), Some(crate::link::SlimValue::Int(NOON)));
    let replaced = ForceFieldPlacement {
        field_id: OTHER_FIELD,
        tower_id: TOWER,
        solar_system_id: 30_000_001,
        type_id: 16_000,
        group_id: 411,
        name: "Force Field".to_string(),
        position: [0.0, 0.0, 0.0],
        radius: FIELD_RADIUS,
    };
    scene.place_force_field(replaced, NOON + TICKS_PER_SECOND).unwrap();
    assert_eq!(
        scene.deployables[&OTHER_FIELD].slim_fields.get("forcefieldStartTime").cloned(),
        Some(crate::link::SlimValue::Int(NOON + TICKS_PER_SECOND))
    );
}

/// A force field's ball shows its tower as its harmonic, and any other ball shows none.
#[test]
fn a_force_field_ball_has_its_tower_as_its_harmonic() {
    assert_eq!(ball_harmonic(&Role::ForceField { tower_id: TOWER }), IdRef::Valid(TOWER));
    assert_eq!(ball_harmonic(&Role::ControlTower { active: true }), IdRef::Absent);
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
