use super::*;
use crate::ship::ModuleSpec;
use crate::world::{Body, Detail};

pub(crate) const STATION: i64 = 60_000_001;
pub(crate) const SESSION: SessionId = 7;
/// 2026-09-29 12:00:00 UTC as a FILETIME.
pub(crate) const NOON: i64 = 134_351_688_000_000_000;

pub(crate) fn system() -> Arc<SolarSystem> {
    let station = Body {
        id: STATION,
        type_id: 1531,
        group_id: 15,
        category_id: 3,
        owner_id: 1_000_001,
        name: "Testing I - Trade Hub".to_string(),
        kind: BodyKind::Station,
        position: Vec3::new(1_000_000.0, 0.0, 0.0),
        radius: 1_000.0,
        graphic_id: None,
        detail: Detail::Station { corporation_id: 1_000_001 },
    };
    Arc::new(SolarSystem {
        id: 30_000_001,
        name: "Testing".to_string(),
        constellation_id: 20_000_001,
        region_id: 10_000_001,
        faction_id: None,
        security: 0.9,
        bodies: vec![station],
    })
}

pub(crate) fn spec() -> ShipSpec {
    ShipSpec {
        stance: None,
        ship_id: 9_988_400_001_939,
        character_id: 140_000_342,
        type_id: 601,
        group_id: 237,
        category_id: 6,
        name: "Ibis".to_string(),
        owner_id: 140_000_342,
        corporation_id: 1_000_006,
        alliance_id: 0,
        war_faction_id: 0,
        security_status: 0.0,
        radius: 47.0,
        mass: 1_163_000.0,
        max_velocity: 339.25,
        agility: 4.0185,
        warp_speed_au: 3.0,
        shield_recharge_ms: 860_000.0,
        max_target_range: 29_700.0,
        max_locked_targets: 4,
        scan_resolution: 660.0,
        signature_radius: 28.0,
        capacitor_capacity: 149.5,
        capacitor_recharge_s: 55.25,
        sensor_strength: [0.0, 0.0, 0.0, 0.0],
        ecm_resistance: 1.0,
        energy_warfare_resistance: 1.0,
        drone_damage_multiplier: 1.0,
        // an Ibis's points, with resistances made up so that each kind of damage does something different to each layer
        vitals: crate::health::Vitals {
            shield_capacity: 220.0,
            armor_hp: 137.5,
            structure_hp: 201.25,
            resonances: crate::health::Resonances {
                shield: [0.0, 0.8, 0.6, 0.5],
                armor: [0.5, 0.35, 0.25, 0.1],
                structure: [0.33, 0.33, 0.33, 0.33],
            },
            armor_pinned: false,
        },
        condition: Condition::FULL,
        modules: vec![ModuleSpec { item_id: 9_988_400_001_940, type_id: 21857, flag: 19 }],
    }
}

fn scene() -> Scene {
    let mut scene = Scene::new(system());
    scene.undock(SESSION, spec(), STATION, NOON).unwrap();
    scene
}

#[test]
fn a_space_login_primes_visuals_before_the_full_bind_without_undocking_again() {
    let mut scene = Scene::new(system());
    scene.enter_in_space(SESSION, spec(), [100.0, 200.0, 300.0], [0.0, 0.0, 1.0], NOON).unwrap();
    assert!(scene.pilots[&SESSION].jumped_in);

    let visuals = scene.prime_login_visuals(SESSION, NOON);
    let names: Vec<_> = visuals
        .iter()
        .filter_map(|out| match out {
            Out::Notify { name, .. } => Some(*name),
            _ => None,
        })
        .collect();
    assert!(names.contains(&"DoSimClockRebase"));
    assert!(names.contains(&"DoDestinyUpdate"));
    assert!(!scene.pilots[&SESSION].bound);
    assert!(scene.pilots[&SESSION].login_visuals_sent);

    let bound = scene.bind(SESSION, NOON);
    let names: Vec<_> = bound
        .iter()
        .filter_map(|out| match out {
            Out::Notify { name, .. } => Some(*name),
            _ => None,
        })
        .collect();
    assert!(scene.pilots[&SESSION].bound);
    assert!(!scene.pilots[&SESSION].login_visuals_sent);
    assert!(!names.contains(&"DoSimClockRebase"));
    assert_eq!(names.iter().filter(|name| **name == "DoDestinyUpdate").count(), 2);
}

fn seconds(n: i64) -> i64 {
    n * TICKS_PER_SECOND
}

fn speed(ship: &Ship) -> f64 {
    (ship.velocity.x.powi(2) + ship.velocity.y.powi(2) + ship.velocity.z.powi(2)).sqrt()
}

fn action_names(out: &Out) -> Vec<String> {
    let Out::Notify { args, .. } = out else { return Vec::new() };
    let Some(Value::List(actions)) = args.first() else { return Vec::new() };
    actions
        .iter()
        .map(|a| match a {
            Value::Tuple(pair) => match &pair[1] {
                Value::Tuple(inner) => match &inner[0] {
                    Value::Str(name) => name.to_string(),
                    other => panic!("{other:?}"),
                },
                other => panic!("{other:?}"),
            },
            other => panic!("{other:?}"),
        })
        .collect()
}

fn stamps(out: &Out) -> Vec<i64> {
    let Out::Notify { args, .. } = out else { return Vec::new() };
    let Some(Value::List(actions)) = args.first() else { return Vec::new() };
    actions
        .iter()
        .map(|a| match a {
            Value::Tuple(pair) => match &pair[0] {
                Value::Int(n) => *n,
                other => panic!("{other:?}"),
            },
            other => panic!("{other:?}"),
        })
        .collect()
}

#[test]
fn stamps_are_whole_seconds_of_the_unix_clock() {
    assert_eq!(stamp_at(134_351_527_024_310_000), 1_790_679_102);
}

/// The scene of `system()` with the station of the given type.
fn scene_with_station_type(station_type: i32, radius: f64) -> Scene {
    let mut world = (*system()).clone();
    world.bodies[0].type_id = station_type;
    world.bodies[0].radius = radius;
    let mut scene = Scene::new(Arc::new(world));
    scene.undock(SESSION, spec(), STATION, NOON).unwrap();
    scene
}

#[test]
fn an_undocked_ship_comes_out_at_the_undock_point_of_its_stations_type_and_moves_away_at_top_speed() {
    let mut scene = scene(); // a Caldari Trading Station (1531), a ship of the rookie group
    let ship = scene.ship(SESSION, NOON).unwrap().clone();
    let point = crate::locators::undock_point(1531, spec().group_id, [0.0; 3]).unwrap();
    assert_eq!(
        (ship.position.x, ship.position.y, ship.position.z),
        (1_000_000.0 + point.offset.x, point.offset.y, point.offset.z)
    );
    // it faces the point's direction (normalized: the table's has five decimals)
    let facing = motion::unit(point.direction).unwrap();
    assert_eq!((ship.direction.x, ship.direction.y, ship.direction.z), (facing.x, facing.y, facing.z));
    assert!((speed(&ship) - 339.25).abs() < 1e-9);
    assert_eq!((ship.course, ship.speed_fraction), (Course::Goto, 1.0));
}

#[test]
fn a_ship_of_a_big_hull_comes_out_at_a_point_for_big_hulls_and_a_station_turned_by_its_rotation_turns_the_point()
 {
    let mut big = spec();
    big.group_id = 27; // a battleship
    let mut world = (*system()).clone();
    world.bodies[0].type_id = 92_885; // a station type with points of more than one size
    let mut scene = Scene::new(Arc::new(world));
    scene.undock(SESSION, big, STATION, NOON).unwrap();
    let large = crate::locators::undock_point(92_885, 27, [0.0; 3]).unwrap();
    let small = crate::locators::undock_point(92_885, 237, [0.0; 3]).unwrap();
    assert_ne!(large.offset, small.offset, "the table has points of more than one size for this type");
    let ship = scene.ship(SESSION, NOON).unwrap();
    assert_eq!(ship.position.y, large.offset.y);

    // the station the reference authors an orientation for (the recorded one, 305 degrees of yaw) turns the point
    let mut turned = (*system()).clone();
    turned.bodies[0].id = 60_015_249;
    turned.bodies[0].type_id = 92_885;
    let mut scene = Scene::new(Arc::new(turned));
    scene.undock(SESSION, spec(), 60_015_249, NOON).unwrap();
    let ship = scene.ship(SESSION, NOON).unwrap();
    assert!(
        (ship.direction.x + 0.8191520442889917).abs() < 1e-9
            && (ship.direction.z - 0.5735764363510462).abs() < 1e-9
    );
    assert!((ship.position.x - (1_000_000.0 - 19_613.851392826436)).abs() < 1e-6, "{}", ship.position.x);
}

#[test]
fn a_ship_leaves_a_station_of_a_type_the_table_does_not_know_a_third_of_its_radius_out_along_the_star_line() {
    let mut scene = scene_with_station_type(1, 1_000.0);
    let ship = scene.ship(SESSION, NOON).unwrap().clone();
    let gap =
        ((ship.position.x - 1_000_000.0).powi(2) + ship.position.y.powi(2) + ship.position.z.powi(2)).sqrt();
    assert!((gap - 2_000.0).abs() < 1e-6, "{gap}");
    // away from the star at the origin: the station is on +x, so the ship is further out
    assert!(ship.direction.x > 0.99);
}

#[test]
fn a_ship_cannot_undock_twice_or_from_a_station_that_is_not_there() {
    let mut scene = scene();
    assert_eq!(scene.undock(SESSION, spec(), STATION, NOON), Err(SceneError::AlreadyInSpace(SESSION)));
    assert_eq!(scene.undock(8, spec(), 42, NOON), Err(SceneError::UnknownStation(42)));
}

#[test]
fn binding_sends_the_time_the_ship_then_the_state_then_the_ship_numbers() {
    let mut scene = scene();
    let out = scene.bind(SESSION, NOON);
    let names: Vec<_> = out
        .iter()
        .map(|o| match o {
            Out::Notify { name, .. } => *name,
            other => panic!("{other:?}"),
        })
        .collect();
    assert_eq!(
        names,
        [
            "OnSetTimeDilation",
            "DoSimClockRebase",
            "DoDestinyUpdate",
            "DoDestinyUpdate",
            "DoDestinyUpdate",
            "DoDestinyUpdate"
        ]
    );
    assert_eq!(action_names(&out[2]), ["AddBalls2"]);
    assert_eq!(
        action_names(&out[3]),
        [
            "OnSpecialFX",
            "SetBallPosition",
            "SetBallMassive",
            "SetBallMass",
            "SetBallVelocity",
            "GotoDirection"
        ]
    );
    assert_eq!(action_names(&out[4]), ["SetState"]);
    assert_eq!(action_names(&out[5]), ["SetBallAgility", "SetMaxSpeed"]);
    // the ship comes at the stamp of the moment and the state a stamp after (the reference's 102 and 103)
    let now_stamp = i64::from(stamp_at(NOON));
    assert_eq!(stamps(&out[2]), [now_stamp]);
    assert_eq!(stamps(&out[4]), [now_stamp + 1]);
    assert!(
        matches!(&out[2], Out::Notify { args, .. } if args[1] == Value::Bool(true)),
        "the client waits for the ship"
    );
}

#[test]
fn binding_a_session_without_a_ship_says_nothing() {
    assert!(Scene::new(system()).bind(1, NOON).is_empty());
}

#[test]
fn stamps_run_one_after_another_but_never_more_than_two_ahead() {
    let mut scene = scene();
    let now_stamp = i64::from(stamp_at(NOON));
    let sent: Vec<i64> = (0..5).map(|_| stamps(&scene.command(SESSION, Command::Stop, NOON)[0])[0]).collect();
    assert_eq!(sent, [now_stamp, now_stamp + 1, now_stamp + 2, now_stamp + 2, now_stamp + 2]);
    // and a stamp that has been overtaken by the clock starts from the clock again
    let later = stamps(&scene.command(SESSION, Command::Stop, NOON + seconds(10))[0])[0];
    assert_eq!(later, now_stamp + 10, "a stamp the clock has passed starts again from the clock");
}

#[test]
fn a_ship_told_to_stop_slows_down_and_one_told_to_go_gathers_speed() {
    let mut scene = scene();
    scene.command(SESSION, Command::Stop, NOON);
    assert!(speed(scene.ship(SESSION, NOON + seconds(60)).unwrap()) < 0.01);
    scene.command(SESSION, Command::SetSpeedFraction(0.5), NOON + seconds(60));
    let cruise = speed(scene.ship(SESSION, NOON + seconds(120)).unwrap());
    assert!((cruise - 339.25 * 0.5).abs() < 0.01, "{cruise}");
}

#[test]
fn a_ship_at_full_speed_keeps_its_place_on_a_straight_line() {
    let mut scene = scene();
    let before = scene.ship(SESSION, NOON).unwrap().clone();
    let after = scene.ship(SESSION, NOON + seconds(10)).unwrap().clone();
    let travelled = ((after.position.x - before.position.x).powi(2)
        + (after.position.y - before.position.y).powi(2)
        + (after.position.z - before.position.z).powi(2))
    .sqrt();
    assert!((travelled - 3392.5).abs() < 0.01, "{travelled}");
}

#[test]
fn poll_bumps_an_overlapping_ship_off_a_static_body() {
    let mut scene = scene();
    let body = scene.system.body(STATION).unwrap().clone();
    scene.bind(SESSION, NOON);
    let pilot = scene.pilots.get_mut(&SESSION).unwrap();
    pilot.protected_until = NOON;
    let ship = &mut pilot.ship;
    ship.position = Vec3::new(body.position.x + 500.0, body.position.y, body.position.z);
    ship.velocity = Vec3::ZERO;
    ship.speed_fraction = 0.0;
    ship.course = Course::Stop;

    let told = scene.poll(NOON + seconds(1));
    let ship = &scene.pilots.get(&SESSION).unwrap().ship;
    let separation = (ship.position.x - body.position.x)
        .hypot(ship.position.y - body.position.y)
        .hypot(ship.position.z - body.position.z);
    assert!(separation >= ship.spec.radius + body.radius - 1e-6);
    assert_eq!(ship.course, Course::Stop);
    assert!(told.iter().any(|out| action_names(out).iter().any(|name| name == "SetBallPosition")));
}

#[test]
fn poll_bumps_overlapping_ships_apart_and_reverses_closing_velocity() {
    let mut scene = scene();
    let second = add_other_ship(&mut scene);
    {
        let ship = &mut scene.pilots.get_mut(&SESSION).unwrap().ship;
        ship.position = Vec3::new(10_000.0, 0.0, 0.0);
        ship.velocity = Vec3::new(10.0, 0.0, 0.0);
        ship.direction = Vec3::new(1.0, 0.0, 0.0);
        ship.speed_fraction = 1.0;
        ship.course = Course::Goto;
    }
    {
        let ship = &mut scene.pilots.get_mut(&second).unwrap().ship;
        ship.position = Vec3::new(10_070.0, 0.0, 0.0);
        ship.velocity = Vec3::new(-10.0, 0.0, 0.0);
        ship.direction = Vec3::new(-1.0, 0.0, 0.0);
        ship.speed_fraction = 1.0;
        ship.course = Course::Goto;
    }

    scene.poll(NOON);
    let a = &scene.pilots.get(&SESSION).unwrap().ship;
    let b = &scene.pilots.get(&second).unwrap().ship;
    let distance =
        (b.position.x - a.position.x).hypot(b.position.y - a.position.y).hypot(b.position.z - a.position.z);
    assert!(distance >= a.spec.radius + b.spec.radius - 1e-6);
    assert!(a.velocity.x < 0.0 && b.velocity.x > 0.0);
}

#[test]
fn poll_leaves_non_overlapping_ships_unchanged() {
    let mut scene = scene();
    let other = add_other_ship(&mut scene);
    {
        let ship = &mut scene.pilots.get_mut(&SESSION).unwrap().ship;
        ship.position = Vec3::new(10_000.0, 0.0, 0.0);
        ship.velocity = Vec3::new(4.0, 2.0, 0.0);
        ship.direction = Vec3::new(0.0, 1.0, 0.0);
        ship.speed_fraction = 0.5;
        ship.course = Course::Goto;
    }
    {
        let ship = &mut scene.pilots.get_mut(&other).unwrap().ship;
        ship.position = Vec3::new(20_000.0, 0.0, 0.0);
        ship.velocity = Vec3::new(-3.0, 1.0, 0.0);
        ship.direction = Vec3::new(-1.0, 0.0, 0.0);
        ship.speed_fraction = 0.4;
        ship.course = Course::Goto;
    }
    let before = (scene.pilots[&SESSION].ship.clone(), scene.pilots[&other].ship.clone());

    scene.poll(NOON);

    assert_eq!(scene.pilots[&SESSION].ship, before.0);
    assert_eq!(scene.pilots[&other].ship, before.1);
}

#[test]
fn a_micro_jump_uses_heading_and_distance_without_changing_velocity() {
    let mut scene = scene();
    scene.command(SESSION, Command::GotoDirection(Vec3::new(0.0, 3.0, 4.0)), NOON);
    scene.command(SESSION, Command::SetSpeedFraction(0.35), NOON);
    let (position, direction, speed) = {
        let ship = scene.ship(SESSION, NOON).unwrap();
        (ship.position, ship.direction, ship.speed_fraction)
    };

    scene.perform_micro_jump(SESSION, 25_000.0, NOON);
    let after = scene.ship(SESSION, NOON).unwrap();
    assert!((after.position.x - position.x - direction.x * 25_000.0).abs() < 0.001);
    assert!((after.position.y - position.y - direction.y * 25_000.0).abs() < 0.001);
    assert!((after.position.z - position.z - direction.z * 25_000.0).abs() < 0.001);
    assert_eq!(
        (after.direction.x, after.direction.y, after.direction.z),
        (direction.x, direction.y, direction.z)
    );
    assert_eq!(after.speed_fraction, speed);
}

fn add_other_ship(scene: &mut Scene) -> SessionId {
    let mut other = spec();
    other.ship_id += 1;
    other.character_id += 1;
    other.owner_id += 1;
    other.modules[0].item_id += 1;
    other.name = "Other Ibis".to_string();
    let session = SESSION + 1;
    scene.undock(session, other, STATION, NOON).unwrap();
    session
}

#[test]
fn a_station_bump_deflects_heading_used_by_a_micro_jump() {
    let mut scene = scene();
    let station = scene.system.body(STATION).unwrap().clone();
    let radius = scene.pilots[&SESSION].ship.spec.radius;
    {
        let pilot = scene.pilots.get_mut(&SESSION).unwrap();
        pilot.protected_until = 0;
        pilot.leaving_station = None; // placed inside the station, not flying out of it: the station bumps it
        pilot.ship.position = Vec3::new(
            station.position.x + station.radius + radius - 10.0,
            station.position.y,
            station.position.z,
        );
        pilot.ship.velocity = Vec3::new(-10.0, 10.0, 0.0);
        pilot.ship.direction = Vec3::new(-1.0, 0.0, 0.0);
    }

    scene.settle_all(NOON);

    let bumped = &scene.pilots[&SESSION].ship;
    let from = bumped.position;
    let heading = bumped.direction;
    let dx = from.x - station.position.x;
    let dy = from.y - station.position.y;
    let dz = from.z - station.position.z;
    let surface_gap = dx.hypot(dy).hypot(dz) - radius - station.radius;
    assert!(surface_gap >= -1e-6);
    assert!(bumped.velocity.x > 0.0);
    assert!(heading.y > 0.0);

    scene.perform_micro_jump(SESSION, 100.0, NOON);
    let jumped = &scene.pilots[&SESSION].ship;
    assert!((jumped.position.x - from.x - heading.x * 100.0).abs() < 1e-6);
    assert!((jumped.position.y - from.y - heading.y * 100.0).abs() < 1e-6);
    assert!((jumped.position.z - from.z - heading.z * 100.0).abs() < 1e-6);
}

#[test]
fn ships_cannot_pass_through_each_other_in_one_settlement() {
    let mut scene = scene();
    let other = add_other_ship(&mut scene);
    {
        let ship = &mut scene.pilots.get_mut(&SESSION).unwrap().ship;
        ship.position = Vec3::new(-100.0, 0.0, 0.0);
        ship.velocity = Vec3::new(100_000.0, 0.0, 0.0);
        ship.direction = Vec3::new(1.0, 0.0, 0.0);
        ship.course = Course::Stop;
    }
    {
        let ship = &mut scene.pilots.get_mut(&other).unwrap().ship;
        ship.position = Vec3::new(100.0, 0.0, 0.0);
        ship.velocity = Vec3::new(-100_000.0, 0.0, 0.0);
        ship.direction = Vec3::new(-1.0, 0.0, 0.0);
        ship.course = Course::Stop;
    }
    let mut unchecked_a = scene.pilots[&SESSION].ship.clone();
    let mut unchecked_b = scene.pilots[&other].ship.clone();
    motion::advance(&mut unchecked_a, 1.0, None);
    motion::advance(&mut unchecked_b, 1.0, None);
    assert!(unchecked_a.position.x > unchecked_b.position.x);

    scene.settle_all(NOON + seconds(1));

    let a = &scene.pilots[&SESSION].ship;
    let b = &scene.pilots[&other].ship;
    let dx = b.position.x - a.position.x;
    let dy = b.position.y - a.position.y;
    let dz = b.position.z - a.position.z;
    assert!(a.position.x < b.position.x);
    assert!(dx.hypot(dy).hypot(dz) >= a.spec.radius + b.spec.radius - 1e-6);
    assert!(a.velocity.x < 0.0 && b.velocity.x > 0.0);
}

#[test]
fn settling_at_an_older_time_does_not_rewind_or_double_advance_motion() {
    let mut actual = scene();
    let mut reference = scene();
    for moving in [&mut actual, &mut reference] {
        moving.command(SESSION, Command::GotoDirection(Vec3::new(1.0, 0.0, 0.0)), NOON);
        moving.command(SESSION, Command::SetSpeedFraction(0.75), NOON);
        moving.settle_all(NOON + seconds(10));
    }

    let settled_at = NOON + seconds(10);
    let position = actual.pilots[&SESSION].ship.position;
    let old_time_position = actual.ship(SESSION, NOON + seconds(5)).unwrap().position;
    assert_eq!(old_time_position, position);
    assert_eq!(actual.pilots[&SESSION].settled_at, settled_at);

    let next_tick = NOON + seconds(11);
    actual.settle_all(next_tick);
    reference.settle_all(next_tick);
    let actual_ship = &actual.pilots[&SESSION].ship;
    let reference_ship = &reference.pilots[&SESSION].ship;
    assert_ne!(actual_ship.position, position);
    assert_eq!(actual_ship.position, reference_ship.position);
    assert_eq!(actual_ship.velocity, reference_ship.velocity);
    assert_eq!(actual.pilots[&SESSION].settled_at, next_tick);
}

#[test]
fn a_ship_cannot_pass_through_a_station_in_one_settlement() {
    let mut scene = scene();
    let station = scene.system.body(STATION).unwrap().clone();
    {
        let pilot = scene.pilots.get_mut(&SESSION).unwrap();
        pilot.protected_until = 0;
        pilot.ship.position = Vec3::new(
            station.position.x - station.radius - pilot.ship.spec.radius - 100.0,
            station.position.y,
            station.position.z,
        );
        pilot.ship.velocity = Vec3::new(100_000.0, 0.0, 0.0);
        pilot.ship.direction = Vec3::new(1.0, 0.0, 0.0);
        pilot.ship.course = Course::Stop;
    }
    let mut unchecked = scene.pilots[&SESSION].ship.clone();
    motion::advance(&mut unchecked, 1.0, None);
    assert!(unchecked.position.x > station.position.x + station.radius + unchecked.spec.radius);

    scene.settle_all(NOON + seconds(1));

    let ship = &scene.pilots[&SESSION].ship;
    let gap = station.position.x - ship.position.x - station.radius - ship.spec.radius;
    assert!(gap >= -1e-6);
    assert!(ship.velocity.x < 0.0);
}

#[test]
fn a_ship_just_outside_a_station_does_not_bump() {
    let mut scene = scene();
    let station = scene.system.body(STATION).unwrap().clone();
    let pilot = scene.pilots.get_mut(&SESSION).unwrap();
    pilot.protected_until = 0;
    pilot.ship.position = Vec3::new(
        station.position.x + station.radius + pilot.ship.spec.radius + 1.0,
        station.position.y,
        station.position.z,
    );
    pilot.ship.velocity = Vec3::new(-10.0, 0.0, 0.0);
    let position = pilot.ship.position;
    let velocity = pilot.ship.velocity;

    scene.settle_all(NOON);

    let ship = &scene.pilots[&SESSION].ship;
    assert_eq!(ship.position.x, position.x);
    assert_eq!(ship.position.y, position.y);
    assert_eq!(ship.position.z, position.z);
    assert_eq!(ship.velocity.x, velocity.x);
    assert_eq!(ship.velocity.y, velocity.y);
    assert_eq!(ship.velocity.z, velocity.z);
}

#[test]
fn a_ship_leaving_its_station_is_not_bumped_by_it_when_it_first_moves() {
    // a station whose sphere holds the undock point of its type (about 5.4 km from the centre, 0.34 of a 16 km radius, as recorded)
    let mut scene = scene_with_station_type(1531, 16_000.0);
    let station = scene.system.body(STATION).unwrap().clone();
    let radius = scene.pilots[&SESSION].ship.spec.radius;
    // the surface gap to the station: negative while the ship is in its sphere
    let gap = |scene: &Scene| {
        let ship = &scene.pilots[&SESSION].ship;
        let (dx, dy, dz) = (
            ship.position.x - station.position.x,
            ship.position.y - station.position.y,
            ship.position.z - station.position.z,
        );
        dx.hypot(dy).hypot(dz) - radius - station.radius
    };
    assert!(gap(&scene) < 0.0, "the undock point is inside the station's sphere");

    let first_move = NOON + seconds(1);
    scene.command(SESSION, Command::SetSpeedFraction(0.5), first_move);
    let told = scene.poll(first_move + seconds(1));

    assert!(gap(&scene) < 0.0, "the ship is still leaving the station");
    let names: Vec<&str> = told
        .iter()
        .filter_map(|o| match o {
            Out::Notify { name, .. } => Some(*name),
            _ => None,
        })
        .collect();
    assert!(!names.contains(&"DoDestinyUpdate"), "{names:?}");
}

#[test]
fn non_finite_collision_positions_and_radii_are_ignored() {
    let mut scene = scene();
    let other = add_other_ship(&mut scene);
    {
        let ship = &mut scene.pilots.get_mut(&SESSION).unwrap().ship;
        ship.position = Vec3::ZERO;
        ship.velocity = Vec3::new(1.0, 2.0, 3.0);
    }
    {
        let ship = &mut scene.pilots.get_mut(&other).unwrap().ship;
        ship.position = Vec3::new(f64::NAN, 0.0, 0.0);
        ship.spec.radius = f64::INFINITY;
    }
    Arc::make_mut(&mut scene.system).bodies.push(celestial(60_000_002, BodyKind::Moon, 0, f64::NAN));

    scene.settle_all(NOON);

    let ship = &scene.pilots[&SESSION].ship;
    assert_eq!(ship.position, Vec3::ZERO);
    assert_eq!(ship.velocity, Vec3::new(1.0, 2.0, 3.0));
}

#[test]
fn coincident_ships_separate_along_a_deterministic_axis() {
    let mut scene = scene();
    let other = add_other_ship(&mut scene);
    scene.pilots.get_mut(&SESSION).unwrap().ship.position = Vec3::new(0.0, 0.0, 0.0);
    scene.pilots.get_mut(&other).unwrap().ship.position = Vec3::new(0.0, 0.0, 0.0);

    scene.settle_all(NOON);

    let a = &scene.pilots[&SESSION].ship;
    let b = &scene.pilots[&other].ship;
    let dx = b.position.x - a.position.x;
    let dy = b.position.y - a.position.y;
    let dz = b.position.z - a.position.z;
    let separation = dx.hypot(dy).hypot(dz);
    assert!(separation >= a.spec.radius + b.spec.radius - 1e-6);
    assert!(a.position.x < b.position.x);
    assert!(a.position.x.is_finite() && b.position.x.is_finite());
}

#[test]
fn collisions_between_unbound_ships_do_not_consume_output_stamps() {
    let mut scene = scene();
    let other = add_other_ship(&mut scene);
    for session in [SESSION, other] {
        let ship = &mut scene.pilots.get_mut(&session).unwrap().ship;
        ship.position = Vec3::ZERO;
        ship.velocity = Vec3::ZERO;
        ship.speed_fraction = 0.0;
        ship.course = Course::Stop;
    }
    let stamp = scene.next_stamp;

    scene.settle_all(NOON + seconds(1));

    let a = &scene.pilots[&SESSION].ship;
    let b = &scene.pilots[&other].ship;
    let distance = speed_of(vector_sub(b.position, a.position));
    assert!(distance >= a.spec.radius + b.spec.radius - 1e-6);
    assert_eq!(scene.next_stamp, stamp);
    assert!(scene.backlog.is_empty());
}

#[test]
fn a_direction_is_answered_as_a_unit_vector_and_turns_the_ship() {
    let mut scene = scene();
    let out = scene.command(SESSION, Command::GotoDirection(Vec3::new(0.0, 3.0, 4.0)), NOON);
    assert_eq!(action_names(&out[0]), ["GotoDirection"]);
    let ship = scene.ship(SESSION, NOON).unwrap();
    assert_eq!((ship.direction.x, ship.direction.y, ship.direction.z), (0.0, 0.6, 0.8));
    // a direction with no length is no direction
    let out = scene.command(SESSION, Command::GotoDirection(Vec3::ZERO), NOON);
    assert_eq!(out, [Out::Refused { session: SESSION, command: 2, reason: "NO_DIRECTION" }]);
}

#[test]
fn a_speed_fraction_stays_between_nothing_and_everything() {
    let mut scene = scene();
    scene.command(SESSION, Command::SetSpeedFraction(7.0), NOON);
    assert_eq!(scene.ship(SESSION, NOON).unwrap().speed_fraction, 1.0);
    scene.command(SESSION, Command::SetSpeedFraction(-1.0), NOON);
    assert_eq!(scene.ship(SESSION, NOON).unwrap().speed_fraction, 0.0);
    scene.command(SESSION, Command::SetSpeedFraction(f64::NAN), NOON);
    assert_eq!(scene.ship(SESSION, NOON).unwrap().speed_fraction, 0.0);
}

#[test]
fn a_command_from_a_session_with_no_ship_is_refused() {
    let out = Scene::new(system()).command(9, Command::Stop, NOON);
    assert_eq!(out, [Out::Refused { session: 9, command: 3, reason: "NO_SHIP_IN_SPACE" }]);
}

#[test]
fn docking_needs_the_station_to_be_close_and_to_exist() {
    let mut scene = scene();
    // 10 km off the station's surface
    scene.pilots.get_mut(&SESSION).unwrap().ship.position =
        Vec3::new(1_000_000.0 + 1_000.0 + 47.0 + 10_000.0, 0.0, 0.0);
    let out = scene.command(SESSION, Command::Dock { station_id: STATION }, NOON);
    assert_eq!(action_names(&out[0]), ["FollowBall"], "it goes there by itself");
    assert_eq!(out[1], Out::Approaching { session: SESSION, command: 4, distance: 10_000.0 });
    assert_eq!(out.len(), 2);
    let out = scene.command(SESSION, Command::Dock { station_id: 42 }, NOON);
    assert_eq!(out, [Out::Refused { session: SESSION, command: 4, reason: "STATION_NOT_FOUND" }]);
    assert!(scene.contains(SESSION));
}

#[test]
fn a_ship_in_a_doomsdays_windows_does_not_dock_until_they_pass() {
    let mut scene = near_the_station();
    let pilot = scene.pilots.get_mut(&SESSION).unwrap();
    pilot.immobile_until = NOON + seconds(30);
    pilot.no_jump_until = NOON + seconds(300);
    let held = scene.command(SESSION, Command::Dock { station_id: STATION }, NOON + seconds(1));
    assert_eq!(
        held,
        [Out::Refused { session: SESSION, command: 4, reason: "IMMOBILE" }],
        "held by the immobility window"
    );
    let no_jump = scene.command(SESSION, Command::Dock { station_id: STATION }, NOON + seconds(31));
    assert_eq!(
        no_jump,
        [Out::Refused { session: SESSION, command: 4, reason: "DOCKING_BLOCKED" }],
        "in the no-jump window"
    );
    let after = scene.command(SESSION, Command::Dock { station_id: STATION }, NOON + seconds(301));
    assert!(!after.iter().any(|o| matches!(o, Out::Refused { .. })), "after both windows: {after:?}");
}

#[test]
fn a_disrupted_ship_is_refused_warp_docking_and_jump_until_the_debuff_passes() {
    let mut scene = near_the_station();
    let pilot = scene.pilots.get_mut(&SESSION).unwrap();
    pilot.disrupted_until = NOON + seconds(60);
    let docked = scene.command(SESSION, Command::Dock { station_id: STATION }, NOON + seconds(1));
    assert_eq!(
        docked,
        [Out::Refused { session: SESSION, command: 4, reason: "DOCKING_DISRUPTED" }],
        "no docking while disrupted"
    );
    let warped = scene.command(
        SESSION,
        Command::WarpToPoint { destination: Vec3::new(5_000_000.0, 0.0, 0.0), range: 0.0 },
        NOON + seconds(1),
    );
    assert_eq!(warped.len(), 1, "{warped:?}");
    assert!(
        matches!(warped[0], Out::Refused { reason: "WARP_DISRUPTED", .. }),
        "no warp while disrupted: {warped:?}"
    );
    let jumped = scene.command(
        SESSION,
        Command::Jump { gate_id: STATION, destination_gate: STATION },
        NOON + seconds(1),
    );
    assert!(
        matches!(jumped.as_slice(), [Out::Refused { reason: "JUMP_DISRUPTED", .. }]),
        "no jump while disrupted: {jumped:?}"
    );
    let after = scene.command(SESSION, Command::Dock { station_id: STATION }, NOON + seconds(61));
    assert!(!after.iter().any(|o| matches!(o, Out::Refused { .. })), "after the debuff: {after:?}");
}

fn near_the_station() -> Scene {
    let mut scene = Scene::new(system());
    let mut near = spec();
    near.max_velocity = 0.0;
    scene.undock(SESSION, near, STATION, NOON).unwrap();
    // 8 km out at the reference's spawn distance is beyond range for a small station: stand 500 m off its surface
    let pilot = scene.pilots.get_mut(&SESSION).unwrap();
    pilot.ship.position = Vec3::new(1_000_000.0 + 1_000.0 + 47.0 + 500.0, 0.0, 0.0);
    scene
}

#[test]
fn an_accepted_docking_stops_the_ship_and_takes_four_seconds() {
    let mut scene = near_the_station();
    let out = scene.command(SESSION, Command::Dock { station_id: STATION }, NOON);
    assert!(
        matches!(&out[0], Out::Notify { name: "OnDockingAccepted", id_type: "charid", args, .. } if args == &[Value::Int(STATION)])
    );
    assert_eq!(action_names(&out[1]), ["SetBallMassive", "Stop"]);
    assert_eq!(out[2], Out::Accepted { session: SESSION, command: 4, at: NOON });

    assert!(scene.poll(NOON + seconds(3)).is_empty());
    assert!(scene.contains(SESSION));
    assert_eq!(
        scene.poll(NOON + seconds(4)),
        [Out::Docked { session: SESSION, station_id: STATION, condition: Condition::FULL }]
    );
    assert!(!scene.contains(SESSION));
    assert!(scene.poll(NOON + seconds(9)).is_empty(), "docked once");
}

#[test]
fn a_ship_that_docks_leaves_the_station_with_no_rack_heat() {
    // EVE.js resets the rack heat when the ship is healed on a dock (resetEntityRackHeat); here the ship is taken off the scene and the one
    // that undocks has none. Nothing of the heat is kept by the host.
    let mut scene = near_the_station();
    scene.pilots.get_mut(&SESSION).unwrap().heat.levels = [40.0, 10.0, 5.0];
    scene.command(SESSION, Command::Dock { station_id: STATION }, NOON);
    assert!(matches!(scene.poll(NOON + seconds(4)).first(), Some(Out::Docked { .. })));
    scene.undock(SESSION, spec(), STATION, NOON + seconds(10)).unwrap();
    assert_eq!(scene.pilots[&SESSION].heat.levels, [0.0, 0.0, 0.0]);
}

#[test]
fn a_docking_asked_for_again_is_accepted_with_the_first_time_and_orders_are_ignored_meanwhile() {
    let mut scene = near_the_station();
    scene.command(SESSION, Command::Dock { station_id: STATION }, NOON);
    let again = scene.command(SESSION, Command::Dock { station_id: STATION }, NOON + seconds(1));
    assert_eq!(again, [Out::Accepted { session: SESSION, command: 4, at: NOON }]);
    assert_eq!(
        scene.command(SESSION, Command::SetSpeedFraction(1.0), NOON + seconds(1)),
        [Out::Refused { session: SESSION, command: 1, reason: "DOCKING" }]
    );
    assert_eq!(scene.ship(SESSION, NOON + seconds(1)).unwrap().speed_fraction, 0.0);
}

#[test]
fn docking_range_is_measured_between_surfaces_and_two_and_a_half_kilometres_is_in() {
    let dock_from = |gap: f64| {
        let mut scene = near_the_station();
        scene.pilots.get_mut(&SESSION).unwrap().ship.position =
            Vec3::new(1_000_000.0 + 1_000.0 + 47.0 + gap, 0.0, 0.0);
        scene.command(SESSION, Command::Dock { station_id: STATION }, NOON)
    };
    assert!(matches!(dock_from(2_500.0).last(), Some(Out::Accepted { .. })));
    assert!(
        matches!(dock_from(2_500.5).last(), Some(Out::Approaching { distance, .. }) if *distance == 2_500.5)
    );
}

#[test]
fn a_ship_comes_out_of_a_big_station_within_docking_range_of_it_as_the_reference_ships_did() {
    // the docking range is measured from a sphere of the station's radius, and the undock points are inside it
    for station_type in [1531, 92_885, 1, 54] {
        let mut scene = scene_with_station_type(station_type, 70_000.0);
        let out = scene.command(SESSION, Command::Dock { station_id: STATION }, NOON);
        assert!(matches!(out.last(), Some(Out::Accepted { .. })), "type {station_type}: {out:?}");
    }
}

/// The arguments (without the ship's id) of the actions in an update, by name.
fn actions_of(out: &Out) -> Vec<(String, Vec<Value>)> {
    let Out::Notify { args, .. } = out else { return Vec::new() };
    let Some(Value::List(actions)) = args.first() else { return Vec::new() };
    actions
        .iter()
        .map(|a| {
            let Value::Tuple(pair) = a else { panic!("{a:?}") };
            let Value::Tuple(inner) = &pair[1] else { panic!("{:?}", pair[1]) };
            let Value::Str(name) = &inner[0] else { panic!("{:?}", inner[0]) };
            let Value::Tuple(args) = &inner[1] else { panic!("{:?}", inner[1]) };
            (name.to_string(), args[1..].to_vec())
        })
        .collect()
}

fn accepted(command: u8, at: i64) -> Out {
    Out::Accepted { session: SESSION, command, at }
}

#[test]
fn a_ship_told_to_follow_says_so_and_a_repeat_says_nothing() {
    let mut scene = scene();
    let out = scene.command(SESSION, Command::FollowBall { target_id: STATION, range: 3_000.0 }, NOON);
    assert_eq!(
        actions_of(&out[0]),
        [("FollowBall".to_string(), vec![Value::Int(STATION), Value::Int(3_000)])]
    );
    assert_eq!(out[1], accepted(5, NOON));
    assert_eq!(out.len(), 2);
    // the same again, or within a metre of it, is done already
    let again =
        scene.command(SESSION, Command::FollowBall { target_id: STATION, range: 3_000.5 }, NOON + seconds(1));
    assert_eq!(again, [accepted(5, NOON + seconds(1))]);
    // another range is a new order, and a fraction of a metre is cut off
    let other =
        scene.command(SESSION, Command::FollowBall { target_id: STATION, range: 1_000.9 }, NOON + seconds(2));
    assert_eq!(
        actions_of(&other[0]),
        [("FollowBall".to_string(), vec![Value::Int(STATION), Value::Int(1_000)])]
    );
}

#[test]
fn a_ship_that_stands_still_is_set_going_by_the_command_that_follows_orbits_or_aligns() {
    let stopped = || {
        let mut scene = scene();
        scene.command(SESSION, Command::Stop, NOON);
        scene
    };
    let later = NOON + seconds(120);
    let speed_set = |out: &[Out]| {
        actions_of(&out[0]).into_iter().find(|(name, _)| name == "SetSpeedFraction").map(|(_, a)| a)
    };

    let follow =
        stopped().command(SESSION, Command::FollowBall { target_id: STATION, range: 1_000.0 }, later);
    assert_eq!(action_names(&follow[0]), ["FollowBall", "SetSpeedFraction"]);
    assert_eq!(speed_set(&follow), Some(vec![Value::Real(1.0)]));

    let orbit = stopped().command(SESSION, Command::Orbit { target_id: STATION, distance: 5_000.0 }, later);
    assert_eq!(action_names(&orbit[0]), ["Orbit", "SetSpeedFraction"]);
    assert_eq!(speed_set(&orbit), Some(vec![Value::Real(1.0)]));

    // aligning starts at three quarters
    let align = stopped().command(SESSION, Command::AlignTo { target_id: STATION }, later);
    assert_eq!(action_names(&align[0]), ["GotoDirection", "SetSpeedFraction"]);
    assert_eq!(speed_set(&align), Some(vec![Value::Real(0.75)]));

    // a ship already moving keeps its speed and says nothing of it
    let moving = scene().command(SESSION, Command::AlignTo { target_id: STATION }, later);
    assert_eq!(action_names(&moving[0]), ["GotoDirection"]);
}

#[test]
fn aligning_points_the_ship_at_the_body() {
    let mut scene = scene();
    let at = NOON + seconds(30);
    let position = scene.ship(SESSION, at).unwrap().position;
    let out = scene.command(SESSION, Command::AlignTo { target_id: STATION }, at);
    let (_, args) = actions_of(&out[0]).remove(0);
    let toward = motion::unit(Vec3::new(1_000_000.0 - position.x, -position.y, -position.z)).unwrap();
    let Some(Value::Real(x)) = args.first() else { panic!("{args:?}") };
    assert!((x - toward.x).abs() < 1e-12, "{x} against {}", toward.x);
    let ship = scene.ship(SESSION, at).unwrap();
    assert_eq!((ship.direction.x, ship.direction.y, ship.direction.z), (toward.x, toward.y, toward.z));
    assert_eq!(ship.course, Course::Goto);
}

#[test]
fn a_range_below_nothing_is_nothing() {
    let mut scene = scene();
    let out = scene.command(SESSION, Command::FollowBall { target_id: STATION, range: -100.0 }, NOON);
    assert_eq!(actions_of(&out[0]), [("FollowBall".to_string(), vec![Value::Int(STATION), Value::Int(0)])]);
    let out =
        scene.command(SESSION, Command::Orbit { target_id: STATION, distance: -1.0 }, NOON + seconds(1));
    assert_eq!(actions_of(&out[0]), [("Orbit".to_string(), vec![Value::Int(STATION), Value::Int(0)])]);
    assert_eq!(
        scene.ship(SESSION, NOON + seconds(1)).unwrap().course,
        Course::Orbit { target: STATION, distance: 0.0 }
    );
}

const PLANET: i64 = 40_000_001;

/// The test system with a planet 0.3 AU from the star, far from the station.
fn scene_with_a_planet() -> Scene {
    let mut world = (*system()).clone();
    world.bodies.push(Body {
        id: PLANET,
        type_id: 2015,
        group_id: 7,
        category_id: 2,
        owner_id: 0,
        name: "Testing III".to_string(),
        kind: BodyKind::Planet,
        position: Vec3::new(45_000_000_000.0, 0.0, 20_000_000_000.0),
        radius: 6_000_000.0,
        graphic_id: None,
        detail: Detail::None,
    });
    let mut scene = Scene::new(Arc::new(world));
    scene.undock(SESSION, spec(), STATION, NOON).unwrap();
    scene
}

#[test]
fn a_warp_to_what_is_not_there_or_too_near_is_refused() {
    let mut scene = scene_with_a_planet();
    let refused = |reason| vec![Out::Refused { session: SESSION, command: 8, reason }];
    assert_eq!(
        scene.command(SESSION, Command::WarpTo { target_id: 42, range: 0.0 }, NOON),
        refused("TARGET_NOT_FOUND")
    );
    // the station is a thousand kilometres away; 100 km off it, a warp there would end within 150 km of the ship
    scene.pilots.get_mut(&SESSION).unwrap().ship.position = Vec3::new(1_000_000.0 - 100_000.0, 0.0, 0.0);
    assert_eq!(
        scene.command(SESSION, Command::WarpTo { target_id: STATION, range: 0.0 }, NOON),
        refused("WARP_DISTANCE_TOO_CLOSE")
    );
    // and nothing was set going
    assert!(!matches!(scene.ship(SESSION, NOON).unwrap().course, Course::Warp { .. }));
}

#[test]
fn a_warp_to_an_arbitrary_coordinate_uses_the_supplied_point() {
    let mut scene = scene_with_a_planet();
    let destination = Vec3::new(2_000_000.0, 3_000_000.0, 4_000_000.0);

    let told = scene.command(SESSION, Command::WarpToPoint { destination, range: 0.0 }, NOON);

    assert!(matches!(told.last(), Some(Out::Accepted { command: 21, .. })));
    assert!(matches!(
        scene.ship(SESSION, NOON).unwrap().course,
        Course::Warp { raw_destination, target_point, .. }
            if raw_destination == destination && target_point == destination
    ));
}

#[test]
fn fleet_warp_queries_return_positions_without_moving_the_commander() {
    let mut scene = scene_with_a_planet();
    let body_position = scene.system.body(PLANET).unwrap().position;

    assert!(matches!(
        scene.command(SESSION, Command::PositionOf { target_id: PLANET }, NOON).last(),
        Some(Out::Position { command: 23, position, .. }) if *position == body_position
    ));
    let resolved = scene.command(SESSION, Command::ResolveWarpTarget { target_id: PLANET, range: 0.0 }, NOON);
    assert!(matches!(resolved.last(), Some(Out::Position { command: 22, .. })));
    assert!(!matches!(scene.ship(SESSION, NOON).unwrap().course, Course::Warp { .. }));
}

#[test]
fn a_fleet_member_ship_is_a_warp_target_in_its_system() {
    let mut scene = scene_with_a_planet();
    let mut target_spec = spec();
    target_spec.ship_id += 1;
    target_spec.character_id += 1;
    target_spec.owner_id += 1;
    target_spec.modules.clear();
    scene.undock(SESSION + 1, target_spec.clone(), STATION, NOON).unwrap();

    let start = scene.ship(SESSION, NOON).unwrap().position;
    let target_position = Vec3::new(start.x + 2_000_000.0, start.y, start.z);
    scene.pilots.get_mut(&(SESSION + 1)).unwrap().ship.position = target_position;

    let result = scene.command(SESSION, Command::WarpTo { target_id: target_spec.ship_id, range: 0.0 }, NOON);

    assert!(matches!(result.last(), Some(Out::Accepted { command: 8, .. })));
    assert!(matches!(
        scene.ship(SESSION, NOON).unwrap().course,
        Course::Warp { raw_destination, .. } if raw_destination == target_position
    ));
}

#[test]
fn a_ship_lining_up_for_warp_or_in_it_takes_no_orders() {
    let mut scene = scene_with_a_planet();
    let out = scene.command(SESSION, Command::WarpTo { target_id: PLANET, range: 0.0 }, NOON);
    assert_eq!(action_names(&out[0]), ["SetMaxSpeed", "WarpTo", "OnSpecialFX", "SetSpeedFraction"]);
    for command in [
        Command::Stop,
        Command::SetSpeedFraction(0.5),
        Command::GotoDirection(Vec3::new(0.0, 1.0, 0.0)),
        Command::Orbit { target_id: STATION, distance: 5_000.0 },
        Command::Dock { station_id: STATION },
        Command::WarpTo { target_id: PLANET, range: 0.0 },
    ] {
        let told = scene.command(SESSION, command, NOON + seconds(2));
        assert_eq!(
            told,
            [Out::Refused { session: SESSION, command: command.code(), reason: "IN_WARP" }],
            "{command:?}"
        );
    }
    // it is told to the client as a ball that is turning to warp, and, once in warp, as one in warp
    let bound = scene.bind(SESSION, NOON + seconds(3));
    assert_eq!(action_names(&bound[3]).last().map(String::as_str), Some("WarpTo"));
}

#[test]
fn a_warp_ends_with_the_ship_at_rest_by_the_planet_and_the_pilot_may_order_it_about_again() {
    let mut scene = scene_with_a_planet();
    let start = scene.ship(SESSION, NOON).unwrap().position;
    scene.command(SESSION, Command::WarpTo { target_id: PLANET, range: 0.0 }, NOON);
    let mut told = Vec::new();
    let mut t = NOON;
    while t < NOON + seconds(200) {
        t += 2_500_000;
        told.extend(scene.poll(t));
    }
    let names: Vec<String> =
        told.iter().filter(|o| matches!(o, Out::Notify { .. })).flat_map(action_names).collect();
    assert_eq!(
        names,
        [
            "SetBallMassive",
            "SetBallMassive",
            "SetBallPosition",
            "SetBallVelocity",
            "SetSpeedFraction",
            "Stop"
        ]
    );
    let ship = scene.ship(SESSION, t).unwrap().clone();
    assert_eq!(ship.course, Course::Stop);
    let point = crate::warp::warp_in_point(scene.system().body(PLANET).unwrap()).unwrap();
    let apart = ((ship.position.x - point.x).powi(2)
        + (ship.position.y - point.y).powi(2)
        + (ship.position.z - point.z).powi(2))
    .sqrt();
    assert!(apart < 2_000.0, "{apart} m from the point the client warps in at");
    assert!(
        ((point.x - start.x).powi(2) + (point.z - start.z).powi(2)).sqrt() > 1.0e10,
        "it went a long way"
    );
    // it takes orders again, and it is no longer in warp for another warp: the planet is where it stands
    let told = scene.command(SESSION, Command::SetSpeedFraction(1.0), t);
    assert_eq!(action_names(&told[0]), ["SetSpeedFraction"]);
    let told = scene.command(SESSION, Command::WarpTo { target_id: PLANET, range: 0.0 }, t + seconds(1));
    assert_eq!(told, [Out::Refused { session: SESSION, command: 8, reason: "WARP_DISTANCE_TOO_CLOSE" }]);
}

#[test]
fn a_ship_that_stands_still_enters_warp_only_when_it_has_speed_enough_and_faces_the_place() {
    let mut scene = scene_with_a_planet();
    let planet = scene.system().body(PLANET).unwrap().position;
    {
        let ship = &mut scene.pilots.get_mut(&SESSION).unwrap().ship;
        let toward =
            Vec3::new(planet.x - ship.position.x, planet.y - ship.position.y, planet.z - ship.position.z);
        ship.direction = motion::unit(toward).unwrap();
        ship.velocity = Vec3::ZERO;
        ship.speed_fraction = 0.0;
        ship.course = Course::Stop;
    }
    scene.command(SESSION, Command::WarpTo { target_id: PLANET, range: 0.0 }, NOON);
    let entered = |scene: &mut Scene, seconds_after: i64| {
        matches!(
            scene.ship(SESSION, NOON + seconds(seconds_after)).unwrap().course,
            Course::Warp { active: true, .. }
        )
    };
    // its speed builds with the time it takes a ship to get to 75 % (1.39 times its reaction time: about 6.5 s here)
    assert!(!entered(&mut scene, 3));
    assert!(!entered(&mut scene, 6));
    assert!(entered(&mut scene, 8));
}

#[test]
fn what_happened_to_a_ship_while_its_motion_was_worked_out_is_told_before_the_answer_to_the_next_call() {
    let mut scene = scene_with_a_planet();
    scene.command(SESSION, Command::WarpTo { target_id: PLANET, range: 0.0 }, NOON);
    // no poll in between: the next call finds the ship at rest and says so first
    let told = scene.command(SESSION, Command::SetSpeedFraction(1.0), NOON + seconds(600));
    let names: Vec<Vec<String>> =
        told.iter().filter(|o| matches!(o, Out::Notify { .. })).map(action_names).collect();
    assert_eq!(
        names,
        [
            vec!["SetBallMassive"],
            vec!["SetBallMassive"],
            vec!["SetBallPosition", "SetBallVelocity", "SetSpeedFraction", "Stop"],
            vec!["SetSpeedFraction"]
        ]
    );
}

#[test]
fn a_ship_told_to_orbit_says_so_and_a_repeat_says_nothing() {
    let mut scene = scene();
    let out = scene.command(SESSION, Command::Orbit { target_id: STATION, distance: 5_000.0 }, NOON);
    assert_eq!(actions_of(&out[0]), [("Orbit".to_string(), vec![Value::Int(STATION), Value::Int(5_000)])]);
    assert_eq!(out[1], accepted(6, NOON));
    let again =
        scene.command(SESSION, Command::Orbit { target_id: STATION, distance: 5_000.0 }, NOON + seconds(1));
    assert_eq!(again, [accepted(6, NOON + seconds(1))]);
    let closer =
        scene.command(SESSION, Command::Orbit { target_id: STATION, distance: 2_500.7 }, NOON + seconds(2));
    assert_eq!(actions_of(&closer[0]), [("Orbit".to_string(), vec![Value::Int(STATION), Value::Int(2_500)])]);
}

#[test]
fn a_target_that_is_not_there_or_a_range_that_is_no_number_is_refused() {
    let mut scene = scene();
    let refused = |reason| vec![Out::Refused { session: SESSION, command: 0, reason }];
    let mut check = |command: Command, reason: &'static str| {
        let mut expected = refused(reason);
        if let Out::Refused { command: code, .. } = &mut expected[0] {
            *code = command.code();
        }
        assert_eq!(scene.command(SESSION, command, NOON), expected);
    };
    check(Command::FollowBall { target_id: 42, range: 500.0 }, "UNKNOWN_TARGET");
    check(Command::Orbit { target_id: 42, distance: 500.0 }, "UNKNOWN_TARGET");
    check(Command::AlignTo { target_id: 42 }, "UNKNOWN_TARGET");
    check(Command::FollowBall { target_id: STATION, range: f64::NAN }, "BAD_RANGE");
    check(Command::Orbit { target_id: STATION, distance: f64::INFINITY }, "BAD_RANGE");
    // and nothing changed
    assert_eq!(scene.ship(SESSION, NOON).unwrap().course, Course::Goto);
}

#[test]
fn a_ship_that_stands_still_is_not_told_to_stop_again() {
    let mut scene = scene();
    let first = scene.command(SESSION, Command::Stop, NOON);
    assert_eq!(action_names(&first[0]), ["Stop"]);
    // it has not slowed yet, so it is told again; a minute later it stands
    assert_eq!(action_names(&scene.command(SESSION, Command::Stop, NOON + seconds(1))[0]), ["Stop"]);
    let later = scene.command(SESSION, Command::Stop, NOON + seconds(120));
    assert_eq!(later, [accepted(3, NOON + seconds(120))]);
    // an order to go at no speed at all is an order to stop
    let none = scene.command(SESSION, Command::SetSpeedFraction(0.0), NOON + seconds(121));
    assert_eq!(none, [accepted(1, NOON + seconds(121))]);
}

#[test]
fn a_direction_given_to_a_ship_that_stands_sets_it_going_and_the_same_direction_twice_says_nothing() {
    let mut scene = scene();
    let west = Vec3::new(-1.0, 0.0, 0.0);
    let first = scene.command(SESSION, Command::GotoDirection(west), NOON);
    assert_eq!(action_names(&first[0]), ["GotoDirection"], "it is going already");
    let again = scene.command(SESSION, Command::GotoDirection(west), NOON + seconds(1));
    assert_eq!(again, [accepted(2, NOON + seconds(1))]);

    scene.command(SESSION, Command::Stop, NOON + seconds(2));
    let restart = scene.command(SESSION, Command::GotoDirection(west), NOON + seconds(3));
    assert_eq!(action_names(&restart[0]), ["GotoDirection", "SetSpeedFraction"]);
    assert_eq!(scene.ship(SESSION, NOON + seconds(3)).unwrap().speed_fraction, 1.0);
}

#[test]
fn a_speed_fraction_keeps_the_course_but_wakes_a_stopped_ship() {
    let mut scene = scene();
    scene.command(SESSION, Command::Orbit { target_id: STATION, distance: 5_000.0 }, NOON);
    scene.command(SESSION, Command::SetSpeedFraction(0.5), NOON + seconds(1));
    let ship = scene.ship(SESSION, NOON + seconds(1)).unwrap();
    assert_eq!(
        (ship.course, ship.speed_fraction),
        (Course::Orbit { target: STATION, distance: 5_000.0 }, 0.5)
    );

    scene.command(SESSION, Command::Stop, NOON + seconds(2));
    scene.command(SESSION, Command::SetSpeedFraction(0.5), NOON + seconds(3));
    assert_eq!(scene.ship(SESSION, NOON + seconds(3)).unwrap().course, Course::Goto);
}

#[test]
fn a_ship_that_follows_the_station_comes_to_rest_in_docking_range_and_docks_when_asked_again() {
    let mut scene = scene();
    // 50 km off the station's surface, at rest
    scene.pilots.get_mut(&SESSION).unwrap().ship.position =
        Vec3::new(1_000_000.0 + 1_047.0 + 50_000.0, 0.0, 0.0);
    scene.pilots.get_mut(&SESSION).unwrap().ship.velocity = Vec3::ZERO;
    let first = scene.command(SESSION, Command::Dock { station_id: STATION }, NOON);
    assert_eq!(first.last(), Some(&Out::Approaching { session: SESSION, command: 4, distance: 50_000.0 }));
    assert!(scene.poll(NOON + seconds(400)).is_empty(), "it does not dock by itself");

    let ship = scene.ship(SESSION, NOON + seconds(400)).unwrap().clone();
    let gap = ((ship.position.x - 1_000_000.0).powi(2) + ship.position.y.powi(2)).sqrt() - 47.0 - 1_000.0;
    assert!((2_400.0..=2_500.0).contains(&gap), "it stands {gap} m off the surface");
    assert!(speed(&ship) < 1.0);

    let second = scene.command(SESSION, Command::Dock { station_id: STATION }, NOON + seconds(400));
    assert_eq!(action_names(&second[1]), ["SetBallMassive", "Stop"]);
    assert_eq!(second.last(), Some(&accepted(4, NOON + seconds(400))));
}

#[test]
fn binding_a_ship_that_follows_sends_the_follow() {
    let mut scene = scene();
    scene.command(SESSION, Command::FollowBall { target_id: STATION, range: 3_000.0 }, NOON);
    let out = scene.bind(SESSION, NOON);
    let heading = actions_of(&out[3]);
    assert_eq!(
        heading.last().unwrap(),
        &("FollowBall".to_string(), vec![Value::Int(STATION), Value::Int(3_000)])
    );
}

#[test]
fn a_change_of_the_ships_numbers_tells_the_client_what_differs() {
    let mut scene = scene();
    let mut faster = spec();
    faster.max_velocity = 400.0;
    faster.agility = 3.5;
    let out = scene.respec(SESSION, faster.clone(), NOON);
    assert_eq!(action_names(&out[0]), ["SetBallAgility", "SetMaxSpeed"]);
    assert_eq!(scene.ship(SESSION, NOON).unwrap().spec, faster);
    assert!(scene.respec(SESSION, faster.clone(), NOON).is_empty(), "nothing changed");
    assert!(scene.respec(9, faster, NOON).is_empty(), "no ship, nothing to say");
    let mut heavier = spec();
    heavier.mass = 2_000_000.0;
    assert_eq!(
        action_names(&scene.respec(SESSION, heavier, NOON)[0]),
        ["SetBallMass", "SetBallAgility", "SetMaxSpeed"]
    );
}

#[test]
fn a_switched_stance_is_shown_in_the_slim_item_with_the_stance_the_ship_moved_from() {
    let mut scene = scene();
    let mut before = spec();
    before.stance = Some(crate::ship::ShipStance { old_stance: 1, switched_at: NOON, stance: 1 });
    scene.respec(SESSION, before, NOON);
    // the host sends the new stance and its time only; the simulation keeps the stance the ship had
    let mut switched = spec();
    switched.stance =
        Some(crate::ship::ShipStance { old_stance: 3, switched_at: NOON + 10_000_000, stance: 3 });
    let out = scene.respec(SESSION, switched, NOON + 10_000_000);
    assert_eq!(action_names(&out[0]), ["OnSlimItemChange"]);
    let stance = scene.ship(SESSION, NOON).unwrap().spec.stance.unwrap();
    assert_eq!((stance.old_stance, stance.stance), (1, 3));
    let same = scene.ship(SESSION, NOON).unwrap().spec.clone();
    assert!(scene.respec(SESSION, same, NOON).is_empty(), "nothing changed");
}

#[test]
fn a_change_of_the_security_status_shows_the_slim_item_again_to_the_pilot() {
    let mut scene = scene();
    let mut penalised = spec();
    penalised.security_status = -0.25;
    let out = scene.respec(SESSION, penalised.clone(), NOON);
    assert_eq!(out.len(), 1, "only the pilot is in the scene");
    assert_eq!(action_names(&out[0]), ["OnSlimItemChange"]);
    let told = format!("{:?}", out[0]);
    assert!(
        told.contains("foo.SlimItem") && told.contains("Real(-0.25)"),
        "the slim item with the new status: {told}"
    );
    assert!(told.contains(&spec().ship_id.to_string()), "the item is the ship");
    assert!(scene.respec(SESSION, penalised, NOON).is_empty(), "the same status says nothing more");
}

#[test]
fn leaving_removes_the_ship() {
    let mut scene = scene();
    assert!(scene.leave(SESSION, Departure::LoggedOff, NOON).is_some());
    assert!(scene.leave(SESSION, Departure::LoggedOff, NOON).is_none());
    assert!(scene.is_empty());
}

#[test]
fn the_same_calls_at_the_same_times_give_the_same_bytes() {
    let run = || {
        let mut scene = scene();
        let mut out = scene.bind(SESSION, NOON);
        out.extend(scene.command(SESSION, Command::SetSpeedFraction(0.5), NOON + seconds(3)));
        out.extend(scene.command(
            SESSION,
            Command::GotoDirection(Vec3::new(1.0, 0.0, 0.0)),
            NOON + seconds(5),
        ));
        out.extend(scene.update_state(SESSION, NOON + seconds(9)));
        out
    };
    assert_eq!(run(), run());
}

// ── stargates ────────────────────────────────────────────────────────────

pub(crate) const NEAR_GATE: i64 = 50_000_001;
pub(crate) const FAR_GATE: i64 = 50_000_003;
pub(crate) const ARRIVAL_GATE: i64 = 50_000_002;
pub(crate) const OTHER_SYSTEM: i32 = 30_000_002;

fn gate(id: i64, position: Vec3, radius: f64, to: (i32, i64), facing: Option<[f64; 3]>) -> Body {
    Body {
        id,
        type_id: 16_922,
        group_id: 10,
        category_id: 2,
        owner_id: 500_006,
        name: format!("Stargate ({})", to.0),
        kind: BodyKind::Stargate,
        position,
        radius,
        graphic_id: None,
        detail: Detail::Stargate {
            destination_system: to.0,
            destination_gate: to.1,
            destination_name: "Elsewhere".to_string(),
            destination_security: 0.5,
            destination_faction: None,
            hazardous: false,
            facing,
            heading: facing.map(|_| Vec3::new(0.0, 0.0, -1.0)),
        },
    }
}

/// The test system with a gate a few kilometres from the station and another a very long way off, and the system they lead to.
pub(crate) fn gated_systems() -> (SolarSystem, SolarSystem) {
    let mut here = (*system()).clone();
    // a few kilometres from where a ship comes out of the station
    let mut undocked = Scene::new(system());
    undocked.undock(SESSION, spec(), STATION, NOON).unwrap();
    let out = undocked.ship(SESSION, NOON).unwrap().position;
    here.bodies.push(gate(
        NEAR_GATE,
        Vec3::new(out.x + 8_000.0, out.y, out.z),
        6_000.0,
        (OTHER_SYSTEM, ARRIVAL_GATE),
        None,
    ));
    here.bodies.push(gate(
        FAR_GATE,
        Vec3::new(9.0e9, 0.0, 2.0e9),
        12_000.0,
        (OTHER_SYSTEM, 50_000_004),
        None,
    ));
    let mut there = (*system()).clone();
    there.id = OTHER_SYSTEM;
    there.name = "Elsewhere".to_string();
    there.bodies = vec![
        gate(
            ARRIVAL_GATE,
            Vec3::new(2.0e9, 1.0e8, 5.0e8),
            10_000.0,
            (30_000_001, NEAR_GATE),
            Some([180.0, 0.0, 0.0]),
        ),
        gate(50_000_004, Vec3::new(-3.0e9, 0.0, 4.0e9), 10_000.0, (30_000_001, FAR_GATE), None),
    ];
    (here, there)
}

fn scene_by_a_gate() -> Scene {
    let mut scene = Scene::new(Arc::new(gated_systems().0));
    scene.undock(SESSION, spec(), STATION, NOON).unwrap();
    scene
}

fn jump(gate_id: i64, leads_to: i64) -> Command {
    Command::Jump { gate_id, destination_gate: leads_to }
}

fn refusal(out: &[Out]) -> &'static str {
    match out {
        [Out::Refused { command: 9, reason, .. }] => reason,
        other => panic!("{other:?}"),
    }
}

const BELT_A: i64 = 40_000_101;
const BELT_B: i64 = 40_000_102;
const MOON_BY_THE_BELTS: i64 = 40_000_201;
const MOON_FAR_OFF: i64 = 40_000_202;

fn celestial(id: i64, kind: BodyKind, group_id: i32, x: f64) -> Body {
    Body {
        id,
        type_id: 15,
        group_id,
        category_id: 2,
        owner_id: 0,
        name: format!("Body {id}"),
        kind,
        position: Vec3::new(x, 0.0, 0.0),
        radius: 1_000.0,
        graphic_id: None,
        detail: Detail::None,
    }
}

/// Two belts 100 km apart a long way from the station, a moon between them (90 km off the first) and another moon far from both.
fn scene_with_belts() -> Scene {
    let mut world = (*system()).clone();
    world.bodies.push(celestial(BELT_A, BodyKind::Belt, 9, 20_000_000.0));
    world.bodies.push(celestial(BELT_B, BodyKind::Belt, 9, 20_100_000.0));
    // of a kind that has no warp-in point and no wide berth of its own: a ship stops about a kilometre off it
    world.bodies.push(celestial(MOON_BY_THE_BELTS, BodyKind::Moon, 0, 20_090_000.0));
    world.bodies.push(celestial(MOON_FAR_OFF, BodyKind::Moon, 0, 60_000_000.0));
    let mut scene = Scene::new(Arc::new(world));
    scene.undock(SESSION, spec(), STATION, NOON).unwrap();
    scene.bind(SESSION, NOON);
    scene
}

/// Warps to `target` and polls until the ship is at rest again; what the scene told of an arrival on the way.
fn arrivals_of_a_warp(scene: &mut Scene, target: i64, from: i64) -> (Vec<Out>, i64) {
    scene.command(SESSION, Command::SetSpeedFraction(0.0), from);
    scene.command(SESSION, Command::WarpTo { target_id: target, range: 0.0 }, from + seconds(1));
    let mut arrived = Vec::new();
    let mut t = from + seconds(1);
    while t < from + seconds(600) {
        t += 2_500_000;
        arrived.extend(scene.poll(t).into_iter().filter(|out| matches!(out, Out::Arrived { .. })));
        if scene.ship(SESSION, t).is_some_and(|ship| ship.course == Course::Stop) && !arrived.is_empty() {
            break;
        }
    }
    (arrived, t)
}

#[test]
fn a_warp_to_a_belt_ends_with_an_arrival_at_that_belt_told_where_it_is() {
    let mut scene = scene_with_belts();
    let (arrived, _) = arrivals_of_a_warp(&mut scene, BELT_B, NOON);
    assert_eq!(
        arrived,
        [Out::Arrived { session: SESSION, belt_id: BELT_B, position: Vec3::new(20_100_000.0, 0.0, 0.0) }],
        "the position is the belt's, not where the ship stopped"
    );
}

#[test]
fn a_warp_to_what_lies_by_belts_arrives_at_the_nearest_of_them() {
    let mut scene = scene_with_belts();
    let (arrived, _) = arrivals_of_a_warp(&mut scene, MOON_BY_THE_BELTS, NOON);
    assert!(matches!(arrived[..], [Out::Arrived { belt_id: BELT_B, .. }]), "{arrived:?}");
}

#[test]
fn a_warp_that_ends_far_from_every_belt_is_no_arrival() {
    let mut scene = scene_with_belts();
    let mut told = Vec::new();
    scene.command(SESSION, Command::SetSpeedFraction(0.0), NOON);
    scene.command(SESSION, Command::WarpTo { target_id: MOON_FAR_OFF, range: 0.0 }, NOON + seconds(1));
    let mut t = NOON + seconds(1);
    while t < NOON + seconds(600) {
        t += 2_500_000;
        told.extend(scene.poll(t));
    }
    assert!(scene.pilots[&SESSION].flight.is_none(), "the warp is over");
    assert!(told.iter().all(|out| !matches!(out, Out::Arrived { .. })), "{told:?}");
}

#[test]
fn a_belt_just_out_of_the_landing_radius_is_not_arrived_at_and_one_just_inside_is() {
    // 250 km (plus the belt's radius) off belt A, on the side away from belt B, is the edge of it
    let scene = scene_with_belts();
    let at = |x: f64| Vec3::new(x, 0.0, 0.0);
    let edge = 20_000_000.0 - BELT_LANDING_RADIUS - 1_000.0;
    assert_eq!(scene.belt_arrived_at(SESSION, at(edge)).map(|(id, _)| id), Some(BELT_A));
    assert_eq!(scene.belt_arrived_at(SESSION, at(edge - 1.0)), None);
    assert_eq!(scene.belt_arrived_at(SESSION, at(60_000_000.0)), None);
}

#[test]
fn a_jump_needs_a_gate_that_leads_where_the_client_says_and_a_ship_within_reach_of_it() {
    let mut scene = scene_by_a_gate();
    assert_eq!(refusal(&scene.command(SESSION, jump(42, 0), NOON)), "STARGATE_NOT_FOUND");
    assert_eq!(
        refusal(&scene.command(SESSION, jump(STATION, 0), NOON)),
        "STARGATE_NOT_FOUND",
        "a station is not a gate"
    );
    assert_eq!(
        refusal(&scene.command(SESSION, jump(NEAR_GATE, 50_000_009), NOON)),
        "STARGATE_DESTINATION_MISMATCH"
    );
    assert_eq!(refusal(&scene.command(SESSION, jump(FAR_GATE, 0), NOON)), "TOO_FAR_FROM_STARGATE");
    assert_eq!(refusal(&Scene::new(system()).command(SESSION, jump(NEAR_GATE, 0), NOON)), "NO_SHIP_IN_SPACE");
    // none of it touched the ship
    assert!(matches!(scene.ship(SESSION, NOON).unwrap().course, Course::Goto));
}

#[test]
fn a_ship_may_jump_from_two_and_a_half_kilometres_off_the_gates_surface_and_not_from_further() {
    let at = |gap: f64| {
        let mut scene = scene_by_a_gate();
        let gate = scene.system().body(NEAR_GATE).unwrap().clone();
        let pilot = scene.pilots.get_mut(&SESSION).unwrap();
        let offset = gate.radius + pilot.ship.spec.radius + gap;
        pilot.ship.position = Vec3::new(gate.position.x, gate.position.y + offset, gate.position.z);
        scene.command(SESSION, jump(NEAR_GATE, 0), NOON)
    };
    assert!(matches!(at(2_499.0)[1], Out::Jumping { .. }));
    assert!(matches!(at(2_500.0)[1], Out::Jumping { .. }), "the range itself is in");
    assert_eq!(refusal(&at(2_501.0)), "TOO_FAR_FROM_STARGATE");
}

#[test]
fn a_jump_stops_the_ship_and_tells_the_client_the_jump_out_effect_and_the_stop_in_one_update() {
    let mut scene = scene_by_a_gate();
    let out = scene.command(SESSION, jump(NEAR_GATE, ARRIVAL_GATE), NOON);
    assert_eq!(out.len(), 2);
    assert_eq!(action_names(&out[0]), ["OnSpecialFX", "Stop"]);
    let stamps = stamps(&out[0]);
    assert_eq!(stamps[0], stamps[1], "at one stamp, as the reference sends them");
    let (_, fx) = &actions_of(&out[0])[0];
    assert_eq!(
        fx[..],
        [
            Value::None,
            Value::None,
            Value::Int(NEAR_GATE),
            Value::None,
            Value::Str("effects.JumpOut".into()),
            Value::Int(0),
            Value::Int(1),
            Value::Int(0),
            Value::Int(-1),
            Value::None,
            Value::None,
            Value::Int(0),
            Value::Tuple(vec![Value::Int(i64::from(OTHER_SYSTEM)), Value::None]),
        ]
    );
    assert_eq!(
        out[1],
        Out::Jumping {
            session: SESSION,
            command: 9,
            destination_system: OTHER_SYSTEM,
            destination_gate: ARRIVAL_GATE
        }
    );
    let ship = scene.ship(SESSION, NOON + seconds(1)).unwrap();
    assert_eq!((ship.course, speed(ship)), (Course::Stop, 0.0), "a ship that went in flying stands still");
}

#[test]
fn a_ship_that_is_told_the_gate_only_is_taken_too() {
    let mut scene = scene_by_a_gate();
    let out = scene.command(SESSION, jump(NEAR_GATE, 0), NOON);
    assert!(matches!(out[1], Out::Jumping { destination_gate: ARRIVAL_GATE, .. }));
}

#[test]
fn the_ship_leaves_four_seconds_after_the_command_and_not_before() {
    let mut scene = scene_by_a_gate();
    let module_id = spec().modules[0].item_id;
    let mut profile = heat_profile_for_spec(1);
    profile.initial_rack_heat[HeatRack::Medium.index()] = 42.0;
    scene.set_heat_profile(SESSION, profile, NOON).unwrap();
    scene.set_module_overheat(SESSION, module_id, true).unwrap();

    scene.command(SESSION, jump(NEAR_GATE, 0), NOON);
    assert!(scene.due_jumps(NOON + seconds(3)).0.is_empty());
    assert!(scene.due_jumps(NOON + seconds(4) - 1).0.is_empty());
    let (gone, _) = scene.due_jumps(NOON + seconds(4));
    assert_eq!(gone.len(), 1);
    let traveller = &gone[0];
    assert_eq!(
        (
            traveller.session,
            traveller.from_system,
            traveller.from_gate,
            traveller.to_system,
            traveller.to_gate
        ),
        (SESSION, 30_000_001, NEAR_GATE, OTHER_SYSTEM, ARRIVAL_GATE)
    );
    let heat_module = &traveller.pilot.heat.modules[&module_id];
    assert!(!heat_module.overloaded);
    assert_eq!(heat_module.pending_overload, None);
    assert_eq!(traveller.pilot.heat.levels[HeatRack::Medium.index()], 42.0);
    assert!(scene.is_empty(), "it is no longer here");
    assert!(scene.due_jumps(NOON + seconds(9)).0.is_empty(), "and goes once");
}

#[test]
fn the_effects_stamp_can_hold_the_ship_back_beyond_the_four_seconds() {
    // stamps run at most two ahead of the clock: when the effect is stamped ahead, the ship leaves three seconds after that stamp begins
    let mut scene = scene_by_a_gate();
    scene.next_stamp = stamp_at(NOON) + 2; // the update for the effect will be the last that may be sent ahead of the clock
    let out = scene.command(SESSION, jump(NEAR_GATE, 0), NOON);
    let stamp = stamps(&out[0])[0];
    assert_eq!(stamp, i64::from(stamp_at(NOON)) + 2);
    let leaves = (stamp + UNIX_EPOCH_SECONDS + 3) * TICKS_PER_SECOND;
    assert!(leaves > NOON + seconds(4), "a later moment than the four seconds");
    assert!(scene.due_jumps(leaves - 1).0.is_empty());
    assert_eq!(scene.due_jumps(leaves).0.len(), 1);
}

#[test]
fn a_ship_that_has_gone_into_a_gate_takes_no_orders_and_cannot_jump_again() {
    let mut scene = scene_by_a_gate();
    scene.command(SESSION, jump(NEAR_GATE, 0), NOON);
    assert_eq!(
        refusal(&scene.command(SESSION, jump(NEAR_GATE, 0), NOON + seconds(1))),
        "STARGATE_JUMP_IN_PROGRESS"
    );
    for command in [
        Command::SetSpeedFraction(1.0),
        Command::Stop,
        Command::Dock { station_id: STATION },
        Command::WarpTo { target_id: FAR_GATE, range: 0.0 },
    ] {
        let out = scene.command(SESSION, command, NOON + seconds(1));
        assert_eq!(
            out,
            [Out::Refused { session: SESSION, command: command.code(), reason: "JUMPING" }],
            "{command:?}"
        );
    }
}

fn other_scene() -> Scene {
    Scene::new(Arc::new(gated_systems().1))
}

fn jumped(scene: &mut Scene) -> Traveller {
    scene.command(SESSION, jump(NEAR_GATE, 0), NOON);
    // (a scene that has been bound has stamped ahead: the ship may leave a second or two after the four)
    scene.due_jumps(NOON + seconds(6)).0.pop().unwrap()
}

/// A reactive armor hardener (type 4403, effect 4928) and a second module the pilot runs, both started on the ship at rest.
fn a_running_hardener_and_a_module(scene: &mut Scene) -> (i64, i64) {
    let hardener = crate::modules::Effect {
        rules: Default::default(),
        burns_fuel: false,
        module_id: 9_001,
        module_type: 4403,
        effect_id: 4928,
        fx: "effects.AdaptiveArmorHardener".to_string(),
        duration_ms: 10_000.0,
        capacitor_need: 0.0,
        repeat: 1000,
        target_id: 0,
        turret: None,
        action: Some(crate::modules::Action::ResistanceShift {
            seed: [0.85; 4],
            shift: 0.06,
            attribute_ids: [267, 270, 269, 268],
        }),
        on: None,
        off: None,
        on_changes: Vec::new(),
        off_changes: Vec::new(),
    };
    let started = scene.activate(SESSION, hardener, NOON);
    assert!(matches!(started.first(), Some(Out::Accepted { .. })), "{started:?}");
    assert!(scene.pilots[&SESSION].ship.health.shift_profile().is_some());
    (9_001, 9_002)
}

#[test]
fn a_stargate_jump_stops_every_module_the_ship_runs_and_the_hardener_profile_with_it() {
    let mut scene = scene_by_a_gate();
    let (hardener, _) = a_running_hardener_and_a_module(&mut scene);
    scene.command(SESSION, jump(NEAR_GATE, 0), NOON);
    let (travellers, told) = scene.due_jumps(NOON + seconds(6));
    let traveller = travellers.into_iter().next().expect("the ship jumps");
    // the host is told each module ended once, for the reason a forbidden effect has
    let ended: Vec<(i64, u8)> = told
        .iter()
        .filter_map(|o| match o {
            Out::ModuleEnded { session, module_id, reason } if *session == SESSION => {
                Some((*module_id, *reason))
            }
            _ => None,
        })
        .collect();
    assert_eq!(ended, [(hardener, 3)]);
    assert_eq!(traveller.pilot.modules.running_count(), 0, "nothing the pilot ran goes through the gate");
    assert_eq!(
        traveller.pilot.ship.health.shift_profile(),
        None,
        "the profile is reset, not kept for a later start"
    );
}

#[test]
fn a_ship_appears_in_front_of_the_gate_it_comes_out_of_at_rest() {
    let mut scene = scene_by_a_gate();
    let traveller = jumped(&mut scene);
    let mut there = other_scene();
    there.arrive(traveller, NOON + seconds(5)).unwrap();
    let gate = there.system().body(ARRIVAL_GATE).unwrap().clone();
    let ship = there.ship(SESSION, NOON + seconds(5)).unwrap().clone();
    assert_eq!((ship.course, ship.velocity, ship.speed_fraction), (Course::Stop, Vec3::ZERO, 0.0));
    let apart = ((ship.position.x - gate.position.x).powi(2)
        + (ship.position.y - gate.position.y).powi(2)
        + (ship.position.z - gate.position.z).powi(2))
    .sqrt();
    assert!((apart - (10_000.0 + 47.0 + 12_000.0)).abs() < 1e-6, "{apart} m from the gate's centre");
    let facing = crate::gate::forward(&gate);
    let along = ((ship.position.x - gate.position.x) * facing.x
        + (ship.position.y - gate.position.y) * facing.y
        + (ship.position.z - gate.position.z) * facing.z)
        / apart;
    assert!(along >= (std::f64::consts::PI / 4.0).cos() - 1e-9, "in the cone in front of the gate: {along}");
    let away = motion::unit(Vec3::new(
        ship.position.x - gate.position.x,
        ship.position.y - gate.position.y,
        ship.position.z - gate.position.z,
    ))
    .unwrap();
    let alike = ship.direction.x * away.x + ship.direction.y * away.y + ship.direction.z * away.z;
    assert!(alike > 1.0 - 1e-9, "the ship faces away from the gate: {alike}");
}

#[test]
fn binding_a_ship_that_came_through_a_gate_sends_the_ship_the_state_and_the_numbers_and_no_undock_priming() {
    let mut scene = scene_by_a_gate();
    let traveller = jumped(&mut scene);
    let mut there = other_scene();
    there.arrive(traveller, NOON + seconds(5)).unwrap();
    let bound = there.bind(SESSION, NOON + seconds(6));
    let names: Vec<Vec<String>> = bound.iter().map(action_names).collect();
    let notifications: Vec<&str> = bound
        .iter()
        .map(|o| match o {
            Out::Notify { name, .. } => *name,
            other => panic!("{other:?}"),
        })
        .collect();
    assert_eq!(
        notifications,
        ["OnSetTimeDilation", "DoSimClockRebase", "DoDestinyUpdate", "DoDestinyUpdate", "DoDestinyUpdate"]
    );
    assert_eq!(names[2], ["AddBalls2"]);
    assert_eq!(names[3], ["SetState"]);
    assert_eq!(names[4], ["SetBallAgility", "SetBallMass", "SetMaxSpeed"]);
    // the ship's own ball says it is stopped
    assert_eq!(there.ship(SESSION, NOON + seconds(6)).unwrap().course, Course::Stop);
}

#[test]
fn a_ship_that_has_jumped_is_not_told_of_the_others_until_it_binds_the_ballpark_there() {
    let mut scene = scene_by_a_gate();
    scene.bind(SESSION, NOON); // it had bound the ballpark of the system it leaves
    let traveller = jumped(&mut scene);
    let mut there = other_scene();
    there.arrive(traveller, NOON + seconds(5)).unwrap();
    // the ship arrived: it has bound nothing here, so it is told nothing and holds nothing in view until it does
    let pilot = there.pilots.get(&SESSION).unwrap();
    assert!(!pilot.bound && pilot.sees.is_empty());
    there.bind(SESSION, NOON + seconds(6));
    assert!(there.pilots.get(&SESSION).unwrap().bound);
}

#[test]
fn a_ship_sent_to_a_system_without_the_gate_is_given_back_and_goes_on_where_it_was() {
    let mut scene = scene_by_a_gate();
    let traveller = jumped(&mut scene);
    // a system that has no gate with that id
    let mut wrong = Scene::new(system());
    let back = wrong.arrive(traveller, NOON + seconds(5)).unwrap_err();
    assert!(wrong.is_empty());
    scene.restore(*back, NOON + seconds(6));
    let told = scene.command(SESSION, Command::SetSpeedFraction(1.0), NOON + seconds(6));
    assert!(matches!(told[0], Out::Notify { .. }), "it takes orders again: {told:?}");
    // and it can try the jump again
    assert!(matches!(scene.command(SESSION, jump(NEAR_GATE, 0), NOON + seconds(7))[1], Out::Jumping { .. }));
}

#[test]
fn a_warp_to_a_gate_ends_within_jump_range_of_it_and_the_jump_is_accepted() {
    let mut scene = scene_by_a_gate();
    scene.command(SESSION, Command::SetSpeedFraction(0.0), NOON);
    let told = scene.command(SESSION, Command::WarpTo { target_id: FAR_GATE, range: 0.0 }, NOON + seconds(1));
    assert_eq!(
        action_names(&told[0]),
        ["SetMaxSpeed", "WarpTo", "OnSpecialFX", "SetSpeedFraction"],
        "{told:?}"
    );
    let mut t = NOON + seconds(1);
    while t < NOON + seconds(300) {
        t += 2_500_000;
        scene.poll(t);
    }
    let ship = scene.ship(SESSION, t).unwrap().clone();
    assert_eq!(ship.course, Course::Stop, "the warp is over");
    let gate = scene.system().body(FAR_GATE).unwrap();
    let surface = crate::gate::surface_distance(gate, ship.position, ship.spec.radius);
    assert!(surface <= crate::gate::JUMP_RANGE, "{surface} m off the gate's surface");
    let out = scene.command(SESSION, jump(FAR_GATE, 0), t + seconds(1));
    assert!(matches!(out[1], Out::Jumping { .. }), "{out:?}");
}

#[test]
fn a_warp_to_a_gate_with_the_same_calls_ends_at_the_same_point() {
    let land = || {
        let mut scene = scene_by_a_gate();
        scene.command(SESSION, Command::SetSpeedFraction(0.0), NOON);
        let told =
            scene.command(SESSION, Command::WarpTo { target_id: FAR_GATE, range: 0.0 }, NOON + seconds(1));
        actions_of(&told[0])[1].1.clone()
    };
    assert_eq!(land(), land());
}

fn heat_profile_for_spec(thermodynamics_level: u8) -> HeatProfile {
    let module = spec().modules[0].clone();
    HeatProfile {
        thermodynamics_level,
        modules: vec![HeatModuleProfile {
            module_id: module.item_id,
            flag: module.flag,
            structure_hp: 40.0,
            damage_hp: 0.0,
            heat_damage_hp: 4.0,
            heat_absorption_rate: 0.01,
            overloadable: true,
            heat_immune: false,
            online: true,
        }],
        ..HeatProfile::default()
    }
}

#[test]
fn an_initial_zero_heat_profile_sends_no_module_attribute_change_on_bind() {
    let mut scene = scene();
    scene.set_heat_profile(SESSION, heat_profile_for_spec(0), NOON).unwrap();
    scene.bind(SESSION, NOON);

    let told = scene.poll(NOON + TICKS_PER_SECOND);
    assert!(
        !told.iter().any(|out| matches!(out, Out::Notify { name: "OnModuleAttributeChanges", .. })),
        "{:?}",
        told
    );
}

#[test]
fn rack_heat_uses_exponential_generation_and_dissipation() {
    let generated = rack_heat_after(0.0, 100.0, 0.01, 1.0, 0.01, 10.0);
    assert!((generated - (100.0 * (1.0 - (-0.1_f64).exp()))).abs() < 1e-9);

    let cooled = rack_heat_after(50.0, 100.0, 0.0, 1.0, 0.01, 10.0);
    assert!((cooled - (50.0 * (-0.1_f64).exp())).abs() < 1e-9);
    assert_eq!(rack_heat_after(100.0, 100.0, 0.01, 1.0, 0.01, 10.0), 100.0);
}

#[test]
fn heat_damage_chance_is_zero_when_cold_and_falls_off_by_slot_distance() {
    assert_eq!(heat_damage_chance(0.0, 100.0, 0.63, 0), 0.0);
    assert_eq!(heat_damage_chance(100.0, 100.0, 0.63, 0), 1.0);
    let adjacent = heat_damage_chance(50.0, 100.0, 0.63, 1);
    let far_edge = heat_damage_chance(50.0, 100.0, 0.63, 7);
    assert!((adjacent - 0.315).abs() < 1e-12);
    assert!(far_edge > 0.0 && far_edge < adjacent);
    assert_eq!(heat_damage_chance(50.0, 100.0, 0.63, 0), 0.5);
}

#[test]
fn heat_damage_clamps_to_module_structure_and_reports_burnout_at_the_boundary() {
    assert_eq!(heat_damage_after(38.0, 40.0, 1.0), (39.0, false));
    assert_eq!(heat_damage_after(39.0, 40.0, 4.0), (40.0, true));
    assert_eq!(heat_damage_after(0.0, 40.0, 100.0), (40.0, true));
    assert_eq!(heat_damage_after(40.0, 40.0, 1.0), (40.0, true));
}

#[test]
fn overheat_requests_apply_at_cycle_boundaries_and_idle_modules_can_be_primed() {
    let mut runtime = HeatModuleRuntime {
        profile: heat_profile_for_spec(1).modules.remove(0),
        overloaded: false,
        pending_overload: None,
        was_running: true,
        cycle_start: Some(NOON),
        next_cycle_at: Some(NOON + TICKS_PER_SECOND),
        completed_cycles: 0,
    };
    runtime.request_overheat(true, true);
    assert!(!runtime.overloaded);
    assert_eq!(runtime.pending_overload, Some(true));
    runtime.settle_cycle_boundary();
    assert!(runtime.overloaded);

    runtime.request_overheat(false, true);
    assert!(runtime.overloaded);
    runtime.settle_cycle_boundary();
    assert!(!runtime.overloaded);

    runtime.request_overheat(true, false);
    assert!(runtime.overloaded);
    assert_eq!(runtime.pending_overload, None);
}

#[test]
fn scene_requires_thermodynamics_and_supports_idle_module_preheat() {
    let mut scene = Scene::new(system());
    let ship = spec();
    let module_id = ship.modules[0].item_id;
    scene.undock(SESSION, ship, STATION, NOON).unwrap();

    let mut initial = heat_profile_for_spec(0);
    initial.initial_rack_heat[HeatRack::Medium.index()] = 37.25;
    scene.set_heat_profile(SESSION, initial, NOON).unwrap();
    assert_eq!(scene.rack_heat_level(SESSION, HeatRack::Medium), Some(37.25));
    assert_eq!(scene.set_module_overheat(SESSION, module_id, true), Err(HeatError::ThermodynamicsRequired));

    scene.set_heat_profile(SESSION, heat_profile_for_spec(1), NOON).unwrap();
    assert_eq!(scene.rack_heat_level(SESSION, HeatRack::Medium), Some(37.25));
    assert_eq!(scene.set_module_overheat(SESSION, module_id, true), Ok(()));
    assert!(scene.pilots[&SESSION].heat.modules[&module_id].overloaded);
    assert_eq!(scene.set_rack_overheat(SESSION, HeatRack::Medium, false).unwrap(), vec![module_id]);
    assert!(!scene.pilots[&SESSION].heat.modules[&module_id].overloaded);
}

#[test]
fn jumping_clears_overheat_requests_without_cooling_the_rack_instantly() {
    let mut scene = Scene::new(system());
    let ship = spec();
    let module_id = ship.modules[0].item_id;
    scene.undock(SESSION, ship, STATION, NOON).unwrap();

    let mut profile = heat_profile_for_spec(1);
    profile.initial_rack_heat[HeatRack::Medium.index()] = 42.0;
    scene.set_heat_profile(SESSION, profile, NOON).unwrap();
    scene.set_module_overheat(SESSION, module_id, true).unwrap();
    let pilot = scene.pilots.get_mut(&SESSION).unwrap();
    pilot.heat.modules.get_mut(&module_id).unwrap().pending_overload = Some(false);

    pilot.clear_overheat_for_jump();

    let module = &pilot.heat.modules[&module_id];
    assert!(!module.overloaded);
    assert_eq!(module.pending_overload, None);
    assert_eq!(pilot.heat.levels[HeatRack::Medium.index()], 42.0);
}

#[test]
fn an_overheated_cycle_damages_and_burns_a_fitted_module_in_its_rack() {
    let mut scene = Scene::new(system());
    let ship = spec();
    let module_id = ship.modules[0].item_id;
    scene.undock(SESSION, ship, STATION, NOON).unwrap();

    let mut profile = heat_profile_for_spec(1);
    profile.modules[0].structure_hp = 4.0;
    profile.modules[0].heat_damage_hp = 5.0;
    scene.set_heat_profile(SESSION, profile, NOON).unwrap();
    scene.pilots.get_mut(&SESSION).unwrap().heat.levels[HeatRack::Medium.index()] = 100.0;
    scene.set_module_overheat(SESSION, module_id, true).unwrap();

    let effect = crate::modules::Effect {
        rules: Default::default(),
        burns_fuel: false,
        module_id,
        module_type: 21_857,
        effect_id: 1,
        fx: "effects.Test".to_string(),
        duration_ms: 1_000.0,
        capacitor_need: 0.0,
        repeat: 1,
        target_id: 0,
        turret: None,
        action: None,
        on: None,
        off: None,
        on_changes: Vec::new(),
        off_changes: Vec::new(),
    };
    assert!(
        scene
            .activate(SESSION, effect, NOON)
            .iter()
            .any(|out| matches!(out, Out::Accepted { command: 17, .. }))
    );

    let told = scene.poll(NOON + TICKS_PER_SECOND);
    assert!(told.iter().any(|out| matches!(out, Out::Notify { name: "OnModuleAttributeChanges", .. })));
    let damaged = &scene.pilots[&SESSION].heat.modules[&module_id].profile;
    assert_eq!(damaged.damage_hp, damaged.structure_hp);
    assert!(!damaged.online);
}

/// Moves every ship of the scene by one offset, out past the station's surface, so that none is within a smartbomb's restriction of it
/// (the ships keep their places among themselves).
pub(crate) fn clear_of_the_station(scene: &mut Scene) {
    let (sx, sy, sz) = {
        let station = scene.system().body(STATION).unwrap();
        (station.position.x, station.position.y, station.position.z)
    };
    let ship = scene.pilots.values().next().unwrap().ship.position;
    let (dx, dy, dz) = (ship.x - sx, ship.y - sy, ship.z - sz);
    let length = (dx * dx + dy * dy + dz * dz).sqrt().max(1.0);
    let away = 20_000.0;
    let offset = (dx / length * away, dy / length * away, dz / length * away);
    for pilot in scene.pilots.values_mut() {
        let p = pilot.ship.position;
        pilot.ship.position = eve_destiny::Vec3::new(p.x + offset.0, p.y + offset.1, p.z + offset.2);
    }
}

/// A cynosural field lit in the destination system (the host's record, put back in space with what is left of its life).
const CYNO_FIELD: i64 = crate::deployable::DEPLOYABLE_ID_BASE + 900;

fn lit_field_in(scene: &mut Scene, at: Vec3, now: i64) {
    let placement = crate::link::DeployedPlacement {
        solar_system_id: scene.solar_system_id(),
        deployable_id: CYNO_FIELD,
        name: "Cynosural Field".to_string(),
        type_id: 21_094,
        group_id: 2_020,
        position: [at.x, at.y, at.z],
        radius: 250.0,
        owner_id: 4_242,
        corporation_id: 0,
        alliance_id: 0,
        ship_id: 0,
        lifetime_ms: 600_000.0,
    };
    scene.restore_field(placement, now);
    assert!(scene.deployables.contains_key(&CYNO_FIELD), "the field is in space");
}

/// The jump as the host sends it: the hull's 95 % of the capacitor (SDE attribute 898), with no Jump Drive Operation.
const JUMP_CAPACITOR: f64 = 0.95;

fn cyno_jump(field_id: i64, destination_system: i32, spool_ms: f64) -> Command {
    Command::CynoJump { field_id, destination_system, spool_ms, capacitor: JUMP_CAPACITOR }
}

fn distance_between(a: Vec3, b: Vec3) -> f64 {
    ((a.x - b.x).powi(2) + (a.y - b.y).powi(2) + (a.z - b.z).powi(2)).sqrt()
}

#[test]
fn a_cyno_jump_leaves_when_its_spool_is_over_and_appears_on_the_ring_round_the_field_it_lit() {
    let mut scene = scene_by_a_gate();
    let destination = other_scene().solar_system_id();
    let told = scene.command(SESSION, cyno_jump(CYNO_FIELD, destination, 300_000.0), NOON);
    assert!(
        told.iter().any(|o| matches!(o, Out::Jumping { destination_system, destination_gate: 0, .. } if *destination_system == destination)),
        "the spool is told: {told:?}"
    );
    assert!(scene.due_jumps(NOON + seconds(299)).0.is_empty(), "the drive is still spooling");
    let (travellers, _) = scene.due_jumps(NOON + seconds(301));
    let traveller = travellers.into_iter().next().expect("the ship leaves when the spool is over");
    assert_eq!(
        (traveller.to_system, traveller.to_field, traveller.to_gate),
        (destination, Some(CYNO_FIELD), 0)
    );

    let field = Vec3::new(1.0e6, 0.0, -2.0e6);
    let mut there = other_scene();
    lit_field_in(&mut there, field, NOON + seconds(301));
    there.arrive(traveller, NOON + seconds(302)).expect("the field is lit there");
    let ship = there.ship(SESSION, NOON + seconds(302)).unwrap().clone();
    let apart = distance_between(ship.position, field);
    assert!((apart - super::jump::CYNO_ARRIVAL_RADIUS).abs() < 1e-6, "{apart} m from the field");
    assert_eq!((ship.course, ship.speed_fraction), (Course::Stop, 0.0));
}

#[test]
fn a_cyno_jump_takes_its_share_of_the_capacitor_when_the_drive_spools() {
    let mut scene = scene_by_a_gate();
    let destination = other_scene().solar_system_id();
    let capacity = scene.pilots[&SESSION].modules.capacitor.as_ref().unwrap().capacity();
    scene.pilots.get_mut(&SESSION).unwrap().modules.capacitor =
        Some(crate::modules::Capacitor::filled(capacity, 55.25, 1.0, NOON));
    let told = scene.command(SESSION, cyno_jump(CYNO_FIELD, destination, 300_000.0), NOON);
    assert!(told.iter().any(|o| matches!(o, Out::Jumping { .. })), "the spool starts: {told:?}");
    let left = scene.pilots[&SESSION].modules.capacitor.as_ref().unwrap().charge_at(NOON);
    assert!((left - capacity * (1.0 - JUMP_CAPACITOR)).abs() < 1e-6, "{left} GJ left of {capacity}");
}

#[test]
fn a_cyno_jump_is_refused_when_the_capacitor_cannot_pay_and_takes_nothing() {
    let mut scene = scene_by_a_gate();
    let destination = other_scene().solar_system_id();
    let capacity = scene.pilots[&SESSION].modules.capacitor.as_ref().unwrap().capacity();
    scene.pilots.get_mut(&SESSION).unwrap().modules.capacitor =
        Some(crate::modules::Capacitor::filled(capacity, 55.25, 0.9, NOON));
    let refused = scene.command(SESSION, cyno_jump(CYNO_FIELD, destination, 300_000.0), NOON);
    assert!(
        matches!(refused.as_slice(), [Out::Refused { command: 26, reason: "NOT_ENOUGH_CAPACITOR", .. }]),
        "{refused:?}"
    );
    assert!(scene.due_jumps(NOON + seconds(400)).0.is_empty(), "a refused jump leaves nothing pending");
    let left = scene.pilots[&SESSION].modules.capacitor.as_ref().unwrap().charge_at(NOON);
    assert!((left - capacity * 0.9).abs() < 1e-6, "the charge is untouched: {left}");
}

#[test]
fn a_cyno_jump_is_refused_while_disrupted_or_to_the_system_the_ship_is_in() {
    let mut scene = scene_by_a_gate();
    let here = scene.solar_system_id();
    let refused_here = scene.command(SESSION, cyno_jump(CYNO_FIELD, here, 300_000.0), NOON);
    assert!(
        matches!(refused_here.as_slice(), [Out::Refused { command: 26, reason: "WRONG_SOLAR_SYSTEM", .. }]),
        "{refused_here:?}"
    );

    scene.pilots.get_mut(&SESSION).unwrap().disrupted_until = NOON + seconds(60);
    let destination = other_scene().solar_system_id();
    let disrupted = scene.command(SESSION, cyno_jump(CYNO_FIELD, destination, 300_000.0), NOON + seconds(1));
    assert!(
        matches!(disrupted.as_slice(), [Out::Refused { command: 26, reason: "JUMP_DISRUPTED", .. }]),
        "{disrupted:?}"
    );
    assert!(scene.due_jumps(NOON + seconds(400)).0.is_empty(), "a refused jump leaves nothing pending");
}

#[test]
fn a_ship_whose_field_is_no_longer_lit_at_its_destination_comes_back_to_where_it_was() {
    let mut scene = scene_by_a_gate();
    let destination = other_scene().solar_system_id();
    scene.command(SESSION, cyno_jump(CYNO_FIELD, destination, 300_000.0), NOON);
    let traveller = scene.due_jumps(NOON + seconds(301)).0.pop().expect("the ship leaves");
    let mut there = other_scene();
    let back = there.arrive(traveller, NOON + seconds(302)).expect_err("no field is lit there");
    let restored = scene.restore(*back, NOON + seconds(302));
    assert!(
        scene.ship(SESSION, NOON + seconds(302)).is_some(),
        "the ship is back in the system it left: {restored:?}"
    );
    assert!(there.ship(SESSION, NOON + seconds(302)).is_none());
}

/// The reason of the one refusal an answer holds, whatever command it refuses.
fn refused_by(out: &[Out]) -> &'static str {
    match out {
        [Out::Refused { reason, .. }] => reason,
        other => panic!("{other:?}"),
    }
}

fn open_bridge(field: i64, destination: i32, lifetime_ms: f64, immobile_ms: f64) -> Command {
    Command::OpenBridge {
        field_id: field,
        destination_system: destination,
        lifetime_ms,
        immobile_ms,
        range_meters: 2_500.0,
        capacitor: 0.0,
    }
}

fn bridge_jump(bridge_ship: i64, field: i64, destination: i32) -> Command {
    Command::BridgeJump { bridge_ship_id: bridge_ship, field_id: field, destination_system: destination }
}

/// A warp disruption source (an interdiction probe) of 16 km at `at`: the bubble a ship in it cannot jump out of.
fn interdiction_at(scene: &mut Scene, at: Vec3, now: i64) {
    let probe = crate::deployable::Deployable {
        id: 9_000_001,
        spec: crate::deployable::Spec {
            charge_type_id: 0,
            group_id: 0,
            name: "Interdiction Probe".to_string(),
            radius: 0.0,
        },
        role: crate::deployable::Role::WarpSource { range: 16_000.0 },
        position: at,
        ends_at: now + seconds(3600),
        launcher: 0,
        ship_id: 0,
        owner_id: 0,
        corporation_id: 0,
        alliance_id: 0,
        module_id: 0,
        module_type: 0,
        watchers: std::collections::BTreeSet::new(),
    };
    scene.deployables.insert(probe.id, probe);
}

#[test]
fn a_titan_bridge_opens_where_the_ship_is_and_holds_the_ship_for_its_minute() {
    let mut scene = scene_by_a_gate();
    let destination = other_scene().solar_system_id();
    let told = scene.command(SESSION, open_bridge(CYNO_FIELD, destination, 60_000.0, 60_000.0), NOON);
    assert!(told.iter().any(|o| matches!(o, Out::Accepted { command: 30, .. })), "{told:?}");
    assert!(
        scene
            .deployables
            .values()
            .any(|d| matches!(d.role, crate::deployable::Role::JumpBridge { .. })
                && d.ship_id == spec().ship_id),
        "the bridge is in space"
    );
    assert_eq!(refused_by(&scene.command(SESSION, jump(NEAR_GATE, 0), NOON + seconds(30))), "IMMOBILE");
    scene.advance_deployables(NOON + seconds(61));
    assert!(
        scene.deployables.values().all(|d| !matches!(d.role, crate::deployable::Role::JumpBridge { .. })),
        "the bridge ends with its minute"
    );
}

#[test]
fn a_fleet_ship_in_range_passes_through_the_bridge_and_leaves_at_once_for_the_field() {
    let mut scene = scene_by_a_gate();
    let passenger = add_other_ship(&mut scene);
    let destination = other_scene().solar_system_id();
    scene.command(SESSION, open_bridge(CYNO_FIELD, destination, 60_000.0, 60_000.0), NOON);
    let told =
        scene.command(passenger, bridge_jump(spec().ship_id, CYNO_FIELD, destination), NOON + seconds(1));
    assert!(
        told.iter().any(
            |o| matches!(o, Out::Jumping { destination_system, .. } if *destination_system == destination)
        ),
        "{told:?}"
    );
    let (travellers, _) = scene.due_jumps(NOON + seconds(2));
    let traveller =
        travellers.into_iter().find(|t| t.session == passenger).expect("the passenger leaves at once");
    assert_eq!((traveller.to_system, traveller.to_field), (destination, Some(CYNO_FIELD)));
}

#[test]
fn a_passenger_beyond_2500_metres_or_inside_a_bubble_or_after_the_bridge_is_gone_is_refused() {
    let mut scene = scene_by_a_gate();
    let passenger = add_other_ship(&mut scene);
    let destination = other_scene().solar_system_id();
    scene.command(SESSION, open_bridge(CYNO_FIELD, destination, 60_000.0, 60_000.0), NOON);
    scene.pilots.get_mut(&passenger).unwrap().ship.position = Vec3::new(10_000.0, 0.0, 0.0);
    assert_eq!(
        refused_by(&scene.command(
            passenger,
            bridge_jump(spec().ship_id, CYNO_FIELD, destination),
            NOON + seconds(1)
        )),
        "OUT_OF_BRIDGE_RANGE"
    );
    scene.pilots.get_mut(&passenger).unwrap().ship.position = scene.pilots[&SESSION].ship.position;
    let here = scene.pilots[&passenger].ship.position;
    interdiction_at(&mut scene, here, NOON);
    assert_eq!(
        refused_by(&scene.command(
            passenger,
            bridge_jump(spec().ship_id, CYNO_FIELD, destination),
            NOON + seconds(1)
        )),
        "WARP_DISRUPTED_BY_BUBBLE"
    );
    scene.deployables.remove(&9_000_001);
    assert_eq!(
        refused_by(&scene.command(
            passenger,
            bridge_jump(spec().ship_id, CYNO_FIELD + 1, destination),
            NOON + seconds(1)
        )),
        "BRIDGE_NOT_FOUND"
    );
    scene.deployables.clear();
    scene.command(SESSION, open_bridge(CYNO_FIELD, destination, 20_000.0, 0.0), NOON + seconds(2));
    scene.advance_deployables(NOON + seconds(30));
    assert_eq!(
        refused_by(&scene.command(
            passenger,
            bridge_jump(spec().ship_id, CYNO_FIELD, destination),
            NOON + seconds(30)
        )),
        "BRIDGE_NOT_FOUND"
    );
}

#[test]
fn a_cynosural_jump_is_refused_inside_a_warp_bubble() {
    let mut scene = scene_by_a_gate();
    let destination = other_scene().solar_system_id();
    let here = scene.pilots[&SESSION].ship.position;
    interdiction_at(&mut scene, here, NOON);
    assert_eq!(
        refused_by(&scene.command(SESSION, cyno_jump(CYNO_FIELD, destination, 300_000.0), NOON)),
        "WARP_DISRUPTED_BY_BUBBLE"
    );
}

/// A conduit jump of the ships `passengers` (ship ids) to the field `field_id`, with the conduit's 10 km.
fn conduit_jump(field_id: i64, destination_system: i32, passengers: &[i64]) -> Command {
    let mut ships = [0_i64; crate::scene::MAX_CONDUIT_PASSENGERS];
    ships[..passengers.len()].copy_from_slice(passengers);
    Command::ConduitJump {
        field_id,
        destination_system,
        range_meters: 10_000.0,
        passenger_count: passengers.len() as u8,
        passengers: ships,
    }
}

#[test]
fn a_conduit_jump_takes_the_fleet_ships_in_its_range_with_it_at_once() {
    let mut scene = scene_by_a_gate();
    let passenger = add_other_ship(&mut scene);
    let passenger_ship = spec().ship_id + 1;
    let destination = other_scene().solar_system_id();
    let told = scene.command(SESSION, conduit_jump(CYNO_FIELD, destination, &[passenger_ship]), NOON);
    assert!(
        told.iter().any(|o| matches!(o, Out::Jumping { session, command: 32, .. } if *session == SESSION)),
        "{told:?}"
    );
    assert!(
        told.iter().any(|o| matches!(o, Out::Jumping { session, command: 32, .. } if *session == passenger)),
        "{told:?}"
    );
    let (travellers, _) = scene.due_jumps(NOON + seconds(1));
    let sessions: Vec<SessionId> = travellers.iter().map(|t| t.session).collect();
    assert!(sessions.contains(&SESSION) && sessions.contains(&passenger), "both leave at once: {sessions:?}");
    assert!(travellers.iter().all(|t| t.to_field == Some(CYNO_FIELD) && t.to_system == destination));
}

#[test]
fn a_conduit_jump_is_refused_for_a_passenger_beyond_its_range_or_not_in_space() {
    let mut scene = scene_by_a_gate();
    let passenger = add_other_ship(&mut scene);
    let destination = other_scene().solar_system_id();
    scene.pilots.get_mut(&passenger).unwrap().ship.position = Vec3::new(20_000.0, 0.0, 0.0);
    assert_eq!(
        refused_by(&scene.command(
            SESSION,
            conduit_jump(CYNO_FIELD, destination, &[spec().ship_id + 1]),
            NOON
        )),
        "OUT_OF_CONDUIT_RANGE"
    );
    assert_eq!(
        refused_by(&scene.command(SESSION, conduit_jump(CYNO_FIELD, destination, &[424_242]), NOON)),
        "PASSENGER_NOT_IN_SPACE"
    );
    assert!(scene.due_jumps(NOON + seconds(400)).0.is_empty(), "a refused conduit jump takes nobody");
}

#[test]
fn a_bridge_takes_only_the_ships_within_the_range_it_was_opened_with() {
    let mut scene = scene_by_a_gate();
    let passenger = add_other_ship(&mut scene);
    let destination = other_scene().solar_system_id();
    let bridger_at = scene.pilots[&SESSION].ship.position;
    scene.command(SESSION, open_bridge(CYNO_FIELD, destination, 60_000.0, 0.0), NOON);
    // a bridge of 2 500 metres: a passenger 500 metres away passes
    scene.pilots.get_mut(&passenger).unwrap().ship.position =
        Vec3::new(bridger_at.x + 500.0, bridger_at.y, bridger_at.z);
    let told =
        scene.command(passenger, bridge_jump(spec().ship_id, CYNO_FIELD, destination), NOON + seconds(1));
    assert!(told.iter().any(|o| matches!(o, Out::Jumping { .. })), "{told:?}");
}

#[test]
fn a_bridge_range_is_refused_beyond_it_and_a_restarted_scene_has_no_bridge() {
    let mut scene = scene_by_a_gate();
    let passenger = add_other_ship(&mut scene);
    let destination = other_scene().solar_system_id();
    let bridger_at = scene.pilots[&SESSION].ship.position;
    // a bridge of 100 metres, opened with the range as the host gives it
    let open = Command::OpenBridge {
        field_id: CYNO_FIELD,
        destination_system: destination,
        lifetime_ms: 60_000.0,
        immobile_ms: 0.0,
        range_meters: 100.0,
        capacitor: 0.0,
    };
    scene.command(SESSION, open, NOON);
    scene.pilots.get_mut(&passenger).unwrap().ship.position =
        Vec3::new(bridger_at.x + 500.0, bridger_at.y, bridger_at.z);
    assert_eq!(
        refused_by(&scene.command(
            passenger,
            bridge_jump(spec().ship_id, CYNO_FIELD, destination),
            NOON + seconds(1)
        )),
        "OUT_OF_BRIDGE_RANGE"
    );
    // a restart: the simulation starts with no bridge, so the passage is refused
    let mut restarted = scene_by_a_gate();
    let back = add_other_ship(&mut restarted);
    assert_eq!(
        refused_by(&restarted.command(
            back,
            bridge_jump(spec().ship_id, CYNO_FIELD, destination),
            NOON + seconds(2)
        )),
        "BRIDGE_NOT_FOUND"
    );
}

const STRUCTURE: i64 = 1_040_000_000_001;
const SECOND: i64 = 10_000_000;

pub(crate) fn structure_placement(scene: &Scene, at: i64) -> crate::link::StructurePlacement {
    crate::link::StructurePlacement {
        structure_id: STRUCTURE,
        solar_system_id: scene.solar_system_id(),
        name: "Astrahus".to_string(),
        type_id: 35_832,
        group_id: 1_657,
        position: [10_000.0, 0.0, 0.0],
        radius: 60_000.0,
        owner_id: 98_000_001,
        corporation_id: 98_000_001,
        alliance_id: 0,
        vitals: crate::health::Vitals {
            shield_capacity: 1_000.0,
            armor_hp: 500.0,
            structure_hp: 300.0,
            ..Default::default()
        },
        shield_recharge_ms: 1.0e15,
        limits: crate::health::StructureLimits {
            per_second: [5.0e6, 5.0e3, 5.0e3],
            repair_pause: [500.0, 500.0, 500.0],
        },
        status: crate::world::StructureStatus {
            state: 110,
            upkeep_state: 3,
            timer: Some((at, at + 3_600 * 10_000_000, None)),
            unanchoring: None,
            repairing: None,
        },
    }
}

fn fields_in_state(out: &[Out]) -> Option<&[(Value, Value)]> {
    for out in out {
        let Out::Notify { name: "DoDestinyUpdate", args, .. } = out else { continue };
        let Some(Value::List(actions)) = args.first() else { continue };
        for action in actions {
            let Value::Tuple(pair) = action else { continue };
            let Some(Value::Tuple(inner)) = pair.get(1) else { continue };
            if inner.first() != Some(&crate::wire::word("SetState")) {
                continue;
            }
            let Some(Value::Tuple(arguments)) = inner.get(1) else { continue };
            let Some(Value::Object { args: state, .. }) = arguments.first() else { continue };
            let Value::Dict(fields) = state.as_ref() else { continue };
            return Some(fields);
        }
    }
    None
}

fn ego_in_state(out: &[Out]) -> Option<Value> {
    let key = crate::wire::word("ego");
    fields_in_state(out)?.iter().find(|(field, _)| field == &key).map(|(_, value)| value.clone())
}

fn structure_damage_in_state(out: &[Out], structure_id: i64) -> Option<Value> {
    let damage_key = crate::wire::word("damageState");
    let structure_key = crate::wire::id(structure_id);
    let (_, damage) = fields_in_state(out)?.iter().find(|(key, _)| key == &damage_key)?;
    let Value::Dict(entries) = damage else { return None };
    entries.iter().find(|(key, _)| key == &structure_key).map(|(_, value)| value.clone())
}

#[test]
fn a_structure_put_in_space_is_a_body_with_its_slim_item_and_is_in_the_bind_of_a_pilot_who_comes_later() {
    let mut scene = scene();
    let placement = structure_placement(&scene, NOON);
    let placed = scene.place_structure(placement.clone(), NOON);
    assert!(placed.is_empty(), "no pilot is bound yet: nobody has it in view");
    assert!(scene.structure_index(STRUCTURE).is_some(), "the structure is a body of the system");

    let bound = scene.bind(SESSION, NOON);
    assert!(
        format!("{bound:?}").contains(&STRUCTURE.to_string()),
        "the structure is in the state a pilot is given when it binds"
    );
    let body = scene.system.bodies[scene.structure_index(STRUCTURE).unwrap()].clone();
    let entries = crate::slim::body_entries(&scene.system, &body);
    let category = entries.iter().find(|(k, _)| *k == "categoryID").map(|(_, v)| v.clone());
    assert_eq!(category, Some(crate::wire::int(65)), "a structure is category 65");
    let state = entries.iter().find(|(k, _)| *k == "state").map(|(_, v)| v.clone());
    assert_eq!(state, Some(crate::wire::int(110)), "the state is the host's");
    assert!(
        scene.place_structure(placement, NOON).is_empty(),
        "a structure already in space is not put in twice"
    );
}

#[test]
fn a_structure_placed_while_a_pilot_is_in_the_system_is_added_to_its_view_and_its_state_change_and_removal_are_told()
 {
    let mut scene = scene();
    scene.bind(SESSION, NOON);
    let placement = structure_placement(&scene, NOON);
    let placed = scene.place_structure(placement.clone(), NOON);
    assert_eq!(placed.len(), 1, "the bound pilot is told of the structure");
    assert_eq!(action_names(&placed[0]), ["AddBalls2"]);
    let added = format!("{:?}", placed[0]);
    assert!(added.contains("Astrahus") && added.contains(&STRUCTURE.to_string()), "{added}");

    // a state the host already has is not told again
    assert!(scene.set_structure_state(STRUCTURE, placement.status.clone(), NOON + SECOND).is_empty());
    let mut paused = placement.status.clone();
    paused.timer = Some((NOON, NOON + 3_600 * SECOND, Some(NOON + 60 * SECOND)));
    paused.repairing = Some(true);
    let changed = scene.set_structure_state(STRUCTURE, paused.clone(), NOON + 2 * SECOND);
    assert_eq!(changed.len(), 1);
    assert_eq!(action_names(&changed[0]), ["OnSlimItemChange"]);
    let idx = scene.structure_index(STRUCTURE).unwrap();
    assert!(
        matches!(&scene.system.bodies[idx].detail, Detail::Structure { status, .. } if *status == paused),
        "the scene now has the state the host gave"
    );
    assert!(
        scene.set_structure_state(STRUCTURE + 1, paused, NOON).is_empty(),
        "a structure that is not in space is left alone"
    );

    let removed = scene.remove_structure(STRUCTURE, NOON + 3 * SECOND);
    assert_eq!(removed.len(), 1);
    assert_eq!(action_names(&removed[0]), ["RemoveBalls"]);
    assert!(scene.structure_index(STRUCTURE).is_none(), "the body is gone");
    assert!(
        scene.remove_structure(STRUCTURE, NOON + 3 * SECOND).is_empty(),
        "a structure already gone is left alone"
    );
}

#[test]
fn a_hit_on_a_structure_is_cut_to_its_layers_limit_each_second_and_an_emptied_shield_is_told_to_the_host_once()
 {
    let mut scene = scene();
    scene.bind(SESSION, NOON);
    let mut placement = structure_placement(&scene, NOON);
    placement.vitals = crate::health::Vitals {
        shield_capacity: 1_000.0,
        armor_hp: 500.0,
        structure_hp: 300.0,
        ..Default::default()
    };
    placement.limits = crate::health::StructureLimits {
        per_second: [400.0, 200.0, 100.0],
        repair_pause: [300.0, 150.0, 80.0],
    };
    scene.place_structure(placement, NOON);
    let shield = |scene: &Scene, at: i64| scene.structure_health[&STRUCTURE].state_at(at).shield;

    let told = scene.damage_structure(STRUCTURE, [1_000.0, 0.0, 0.0, 0.0], NOON);
    assert!(
        told.iter().any(|o| matches!(o, Out::StructureDamaged { structure_id, over_threshold: true, depleted: None } if *structure_id == STRUCTURE)),
        "a shield that took 400 in a second is above its pause: {told:?}"
    );
    assert_eq!(shield(&scene, NOON), 0.6, "a hit of 1000 takes the shield's 400 of this second");
    scene.damage_structure(STRUCTURE, [1_000.0, 0.0, 0.0, 0.0], NOON);
    assert_eq!(shield(&scene, NOON), 0.6, "the second hit in the same second takes nothing from the shield");

    // the shield empties in the second second; a third hit finds it recharged a little and empties it again, which is told as well
    let mut emptied = 0;
    for second in 1..=2 {
        let at = NOON + second * SECOND;
        emptied += scene
            .damage_structure(STRUCTURE, [400.0, 0.0, 0.0, 0.0], at)
            .iter()
            .filter(|o| {
                matches!(o, Out::StructureDamaged { depleted: Some(crate::health::Layer::Shield), .. })
            })
            .count();
    }
    assert_eq!(emptied, 1, "the shield is told it is empty once");
    assert!(
        scene.damage_structure(STRUCTURE + 1, [1_000.0, 0.0, 0.0, 0.0], NOON).is_empty(),
        "a structure not in space is left alone"
    );
}

/// A scene with a structure of `type_id` in the given state, 5 000 km out, and a ship of `group` in its docking range.
fn near_the_structure(type_id: i32, group: i32, state: i32) -> Scene {
    let mut scene = Scene::new(system());
    let mut placement = structure_placement(&scene, NOON);
    placement.position = [5_000_000.0, 0.0, 0.0];
    placement.type_id = type_id;
    placement.status.state = state;
    scene.place_structure(placement, NOON);
    let mut near = spec();
    near.max_velocity = 0.0;
    near.group_id = group;
    scene
        .enter_in_space(
            SESSION,
            near,
            [5_000_000.0 + 60_000.0 + 47.0 + 500.0, 0.0, 0.0],
            [0.0, 0.0, 1.0],
            NOON,
        )
        .unwrap();
    scene
}

#[test]
fn a_structure_in_a_state_that_takes_ships_docks_a_ship_as_a_station_does() {
    let mut scene = near_the_structure(35_832, 27, 111);
    let out = scene.command(SESSION, Command::Dock { station_id: STRUCTURE }, NOON);
    assert!(
        matches!(&out[0], Out::Notify { name: "OnDockingAccepted", id_type: "charid", args, .. } if args == &[Value::Long(STRUCTURE)]),
        "{out:?}"
    );
    assert_eq!(
        scene.poll(NOON + seconds(4)),
        [Out::Docked { session: SESSION, station_id: STRUCTURE, condition: Condition::FULL }]
    );
}

#[test]
fn a_structure_in_a_state_that_disables_docking_refuses_the_ship() {
    for state in [0, 1, 2, 102, 113, 114, 115, 116] {
        let mut scene = near_the_structure(35_832, 27, state);
        let out = scene.command(SESSION, Command::Dock { station_id: STRUCTURE }, NOON);
        assert_eq!(
            out,
            [Out::Refused { session: SESSION, command: 4, reason: "STRUCTURE_DISABLED" }],
            "state {state}"
        );
    }
}

#[test]
fn a_structure_takes_the_hulls_its_docks_are_for_and_no_bigger() {
    // (structure type, ship group, is taken): Astrahus subcapital, Fortizar capital, Keepstar supercapital
    let cases = [
        (35_832, 27, true),
        (35_832, 547, false),
        (35_832, 30, false),
        (35_833, 547, true),
        (35_833, 30, false),
        (35_834, 30, true),
    ];
    for (type_id, group, taken) in cases {
        let mut scene = near_the_structure(type_id, group, 111);
        let out = scene.command(SESSION, Command::Dock { station_id: STRUCTURE }, NOON);
        let refused = matches!(out.first(), Some(Out::Refused { reason: "SHIP_TOO_LARGE", .. }));
        assert_eq!(!refused, taken, "type {type_id}, group {group}: {out:?}");
    }
}

#[test]
fn a_ship_undocks_from_a_structure_as_it_does_from_a_station() {
    let mut scene = Scene::new(system());
    let mut placement = structure_placement(&scene, NOON);
    placement.status.state = 1;
    scene.place_structure(placement, NOON);
    scene.undock(SESSION, spec(), STRUCTURE, NOON).expect("a structure is a dock, whatever its state");
    assert!(scene.contains(SESSION));
}

#[test]
fn a_docked_controller_observes_the_real_structure_and_is_released_when_it_is_removed() {
    let mut scene = Scene::new(system());
    let placement = structure_placement(&scene, NOON);
    scene.place_structure(placement.clone(), NOON);

    assert_eq!(
        refused_by(&scene.control_structure(
            SESSION,
            scene.solar_system_id(),
            STRUCTURE + 1,
            spec().character_id,
            true,
            NOON
        )),
        "STRUCTURE_NOT_FOUND"
    );
    let attached =
        scene.control_structure(SESSION, scene.solar_system_id(), STRUCTURE, spec().character_id, true, NOON);
    assert!(attached.iter().any(|out| matches!(out,
        Out::Accepted { session, command: crate::link::COMMAND_STRUCTURE_CONTROL, .. } if *session == SESSION)));
    assert!(!scene.pilots.contains_key(&SESSION), "a docked observer needs no flying pilot");

    let bound = scene.bind(SESSION, NOON);
    assert_eq!(ego_in_state(&bound), Some(crate::wire::id(STRUCTURE)));
    assert_eq!(
        structure_damage_in_state(&bound, STRUCTURE),
        Some(crate::payload::damage_state(&scene.structure_health[&STRUCTURE].state_at(NOON), NOON)),
        "the observer reads damage from the scene's existing structure health owner"
    );
    assert_eq!(scene.system.bodies.iter().filter(|body| body.id == STRUCTURE).count(), 1);
    assert!(!scene.ships_in_view(SESSION).iter().any(|ship| ship.spec.ship_id == STRUCTURE));
    assert!(scene.controlled_structures[&SESSION].bound);

    let position = scene.system.bodies[scene.structure_index(STRUCTURE).unwrap()].position;
    assert_eq!(refused_by(&scene.command(SESSION, Command::Stop, NOON + SECOND)), "NO_SHIP_IN_SPACE");
    assert_eq!(scene.system.bodies[scene.structure_index(STRUCTURE).unwrap()].position, position);
    assert!(scene.structure_health.contains_key(&STRUCTURE));

    let mut status = placement.status.clone();
    status.state += 1;
    let changed = scene.set_structure_state(STRUCTURE, status, NOON + SECOND);
    assert!(
        changed.iter().any(|out| matches!(out,
        Out::Notify { session, name: "DoDestinyUpdate", .. } if *session == SESSION)),
        "{changed:?}"
    );

    let removed = scene.remove_structure(STRUCTURE, NOON + 2 * SECOND);
    assert!(scene.controlled_structure_id(SESSION).is_none());
    assert!(scene.structure_index(STRUCTURE).is_none());
    assert!(!scene.structure_health.contains_key(&STRUCTURE));
    assert!(
        removed.iter().any(|out| matches!(out,
        Out::Notify { session, name: "DoDestinyUpdate", .. } if *session == SESSION)),
        "{removed:?}"
    );
}

#[test]
fn boarding_a_structure_preserves_the_physical_ship_and_release_rebinds_it() {
    let mut scene = scene();
    scene.bind(SESSION, NOON);
    let ship_id = scene.pilots[&SESSION].ship.spec.ship_id;
    let placement = structure_placement(&scene, NOON);
    scene.place_structure(placement, NOON);

    let attached =
        scene.control_structure(SESSION, scene.solar_system_id(), STRUCTURE, spec().character_id, true, NOON);
    assert!(attached.iter().any(|out| matches!(out,
        Out::Accepted { session, command: crate::link::COMMAND_STRUCTURE_CONTROL, .. } if *session == SESSION)));
    assert!(!scene.pilots.contains_key(&SESSION));
    assert!(scene.unpiloted.contains_key(&ship_id), "the old ship remains a physical scene entity");
    assert_eq!(ego_in_state(&scene.bind(SESSION, NOON)), Some(crate::wire::id(STRUCTURE)));

    let fake_session = scene.unpiloted[&ship_id];
    scene.settle_pilot(fake_session, NOON + SECOND);
    let position_at_release = scene.pilots[&fake_session].ship.position;
    let detached = scene.control_structure(SESSION, scene.solar_system_id(), 0, 0, false, NOON + SECOND);
    assert!(detached.iter().any(|out| matches!(out,
        Out::Accepted { session, command: crate::link::COMMAND_STRUCTURE_CONTROL, .. } if *session == SESSION)));
    assert!(scene.controlled_structure_id(SESSION).is_none());
    assert_eq!(scene.pilots[&SESSION].ship.spec.ship_id, ship_id);
    assert_eq!(scene.pilots[&SESSION].ship.position, position_at_release);
    assert!(!scene.unpiloted.contains_key(&ship_id));
    assert_eq!(ego_in_state(&scene.bind(SESSION, NOON + SECOND)), Some(crate::wire::id(ship_id)));
}

#[test]
fn a_structure_controller_survives_destruction_of_the_ship_it_left() {
    let mut scene = scene();
    scene.bind(SESSION, NOON);
    let ship_id = scene.pilots[&SESSION].ship.spec.ship_id;
    let placement = structure_placement(&scene, NOON);
    scene.place_structure(placement, NOON);
    scene.control_structure(SESSION, scene.solar_system_id(), STRUCTURE, spec().character_id, true, NOON);

    let fake_session = scene.unpiloted[&ship_id];
    let _ = scene.destroy_credited(
        fake_session,
        crate::death::Credit { character_id: 0, corporation_id: 0, alliance_id: 0, war_faction_id: 0 },
        None,
        NOON + SECOND,
    );

    assert_eq!(scene.controlled_structure_id(SESSION), Some(STRUCTURE));
    assert!(!scene.pilots.contains_key(&fake_session));
    assert!(!scene.unpiloted.contains_key(&ship_id));

    let detached = scene.control_structure(SESSION, scene.solar_system_id(), 0, 0, false, NOON + 2 * SECOND);
    assert!(detached.iter().any(|out| matches!(out,
        Out::Accepted { session, command: crate::link::COMMAND_STRUCTURE_CONTROL, .. } if *session == SESSION)));
    assert!(scene.controlled_structure_id(SESSION).is_none());
    assert!(!scene.pilots.contains_key(&SESSION), "a destroyed ship must not be rebound");
}

#[test]
fn transferring_structure_control_releases_the_old_owner_and_leave_clears_the_new_observer() {
    let mut scene = scene();
    scene.bind(SESSION, NOON);
    let ship_id = scene.pilots[&SESSION].ship.spec.ship_id;
    let placement = structure_placement(&scene, NOON);
    scene.place_structure(placement, NOON);
    scene.control_structure(SESSION, scene.solar_system_id(), STRUCTURE, spec().character_id, true, NOON);
    scene.bind(SESSION, NOON);

    let next = 8;
    let transferred =
        scene.control_structure(next, scene.solar_system_id(), STRUCTURE, 9002, true, NOON + SECOND);
    assert!(transferred.iter().any(|out| matches!(out,
        Out::Accepted { session, command: crate::link::COMMAND_STRUCTURE_CONTROL, .. } if *session == next)));
    assert_eq!(scene.controller_of_structure(STRUCTURE), Some(next));
    assert!(scene.controlled_structure_id(SESSION).is_none());
    assert!(scene.controlled_structures.contains_key(&next));
    assert_eq!(scene.pilots[&SESSION].ship.spec.ship_id, ship_id);
    assert!(!scene.unpiloted.contains_key(&ship_id));

    let old_release = scene.control_structure(
        SESSION,
        scene.solar_system_id(),
        0,
        spec().character_id,
        false,
        NOON + SECOND,
    );
    assert!(matches!(old_release.first(), Some(Out::Refused { reason: "CONTROL_STRUCTURE_NOT_ACTIVE", .. })));
    assert_eq!(
        scene.controller_of_structure(STRUCTURE),
        Some(next),
        "the former controller cannot release the new owner"
    );
    assert!(scene.leave(next, Departure::LoggedOff, NOON + 2 * SECOND).is_some());
    assert!(scene.controlled_structure_id(next).is_none());
    assert!(scene.controller_of_structure(STRUCTURE).is_none());
}

fn tether_scene() -> Scene {
    near_the_structure(35_832, 27, 111)
}

fn tether_candidates(range: f64) -> Vec<crate::link::TetherCandidate> {
    vec![crate::link::TetherCandidate { structure_id: STRUCTURE, range }]
}

fn tether_changes(out: &[Out]) -> Vec<(SessionId, i64, i64, bool)> {
    out.iter()
        .filter_map(|out| match out {
            Out::TetherChanged { session, previous_structure_id, structure_id, repairing } => {
                Some((*session, *previous_structure_id, *structure_id, *repairing))
            }
            _ => None,
        })
        .collect()
}

fn grant_tether(scene: &mut Scene, now: i64) -> Vec<Out> {
    scene.set_tether_policy(SESSION, 0, 60_000, tether_candidates(1_000.0), now)
}

fn tether_other(scene: &mut Scene, offset: f64) -> (SessionId, i64) {
    let mut other = spec();
    other.ship_id += 1;
    other.character_id += 1;
    other.max_velocity = 0.0;
    other.modules.clear();
    let ship_id = other.ship_id;
    let p = scene.pilots[&SESSION].ship.position;
    scene.enter_in_space(SESSION + 1, other, [p.x + offset, p.y, p.z], [0.0, 0.0, 1.0], NOON).unwrap();
    (SESSION + 1, ship_id)
}

fn tether_effect(action: Option<crate::modules::Action>) -> crate::modules::Effect {
    crate::modules::Effect {
        module_id: spec().modules[0].item_id,
        module_type: spec().modules[0].type_id,
        effect_id: 1,
        fx: String::new(),
        duration_ms: 60_000.0,
        capacitor_need: 0.0,
        repeat: 1,
        target_id: 0,
        turret: None,
        action,
        on: None,
        off: None,
        on_changes: Vec::new(),
        off_changes: Vec::new(),
        burns_fuel: false,
        rules: Default::default(),
    }
}

fn tether_test_ball() -> crate::deployable::Spec {
    crate::deployable::Spec {
        charge_type_id: 21_096,
        group_id: 897,
        name: "Test field".to_string(),
        radius: 1.0,
    }
}

#[test]
fn tether_requires_host_permission_uses_surface_range_and_replaces_the_whole_list() {
    let mut scene = tether_scene();
    assert!(scene.tick_tethers(NOON).is_empty());
    assert!(!scene.is_tethered(spec().ship_id, NOON));
    assert!(scene.set_tether_policy(SESSION, 0, 60_000, tether_candidates(499.0), NOON).is_empty());
    let engaged = scene.set_tether_policy(SESSION, 0, 60_000, tether_candidates(500.0), NOON);
    assert_eq!(tether_changes(&engaged), [(SESSION, 0, STRUCTURE, false)]);
    assert!(scene.set_tether_policy(SESSION, 0, 60_000, tether_candidates(500.0), NOON).is_empty());

    let mut nearer = structure_placement(&scene, NOON);
    nearer.structure_id += 1;
    nearer.position = [5_000_200.0, 0.0, 0.0];
    scene.place_structure(nearer, NOON);
    let switched = scene.set_tether_policy(
        SESSION,
        0,
        60_000,
        vec![
            crate::link::TetherCandidate { structure_id: STRUCTURE, range: 1_000.0 },
            crate::link::TetherCandidate { structure_id: STRUCTURE + 1, range: 1_000.0 },
        ],
        NOON,
    );
    assert_eq!(tether_changes(&switched), [(SESSION, STRUCTURE, STRUCTURE + 1, false)]);
    let revoked = scene.set_tether_policy(SESSION, 0, 60_000, Vec::new(), NOON);
    assert_eq!(tether_changes(&revoked), [(SESSION, STRUCTURE + 1, 0, false)]);
    assert!(scene.tick_tethers(NOON).is_empty());
    assert!(tether_scene().tick_tethers(NOON).is_empty(), "a restarted scene inherits no permission");
}

#[test]
fn tether_block_deadline_and_lease_expiry_fail_closed_at_the_boundary() {
    let mut scene = tether_scene();
    assert!(scene.set_tether_policy(SESSION, 1_000, 2_000, tether_candidates(1_000.0), NOON).is_empty());
    assert!(scene.tick_tethers(NOON + SECOND - 1).is_empty());
    assert_eq!(tether_changes(&scene.tick_tethers(NOON + SECOND)), [(SESSION, 0, STRUCTURE, false)]);
    assert!(scene.is_tethered(spec().ship_id, NOON + 2 * SECOND - 1));
    assert!(!scene.is_tethered(spec().ship_id, NOON + 2 * SECOND));
    assert_eq!(tether_changes(&scene.tick_tethers(NOON + 2 * SECOND)), [(SESSION, STRUCTURE, 0, false)]);
    assert!(scene.tick_tethers(NOON + 3 * SECOND).is_empty());
    let later = NOON + 3 * SECOND;
    assert!(scene.set_tether_policy(999, 0, 60_000, tether_candidates(1_000.0), later).is_empty());
    assert!(scene.set_tether_policy(SESSION, 0, 0, tether_candidates(1_000.0), later).is_empty());
    grant_tether(&mut scene, later);
    let invalid = scene.set_tether_policy(SESSION, 0, 60_000, tether_candidates(f64::NAN), later);
    assert_eq!(tether_changes(&invalid), [(SESSION, STRUCTURE, 0, false)]);
}

#[test]
fn tether_prevents_locking_and_a_valid_lock_start_breaks_its_own_tether_once() {
    use crate::targeting::Order;
    let mut scene = tether_scene();
    let (other, other_ship) = tether_other(&mut scene, 1_000.0);
    grant_tether(&mut scene, NOON);
    let refused = scene.target_order(other, Order::Lock(spec().ship_id), NOON);
    assert!(
        refused.iter().any(|out| matches!(out, Out::TargetRefused { reason: "TARGET_INVULNERABLE", .. }))
    );
    assert!(scene.pilots[&other].targeting.pending_targets().is_empty());
    let started = scene.target_order(SESSION, Order::Lock(other_ship), NOON);
    assert_eq!(tether_changes(&started), [(SESSION, STRUCTURE, 0, false)]);
    assert!(!scene.pilots[&SESSION].targeting.pending_targets().is_empty());
    assert!(scene.tick_tethers(NOON).is_empty());
    scene.target_order(SESSION, Order::Cancel(other_ship), NOON);
    assert_eq!(tether_changes(&scene.tick_tethers(NOON)), [(SESSION, 0, STRUCTURE, false)]);
}

#[test]
fn tether_does_not_engage_while_another_ship_is_locking_it() {
    use crate::targeting::Order;
    let mut scene = tether_scene();
    let (other, _) = tether_other(&mut scene, 1_000.0);
    scene.target_order(other, Order::Lock(spec().ship_id), NOON);
    assert!(grant_tether(&mut scene, NOON).is_empty());
    scene.target_order(other, Order::Cancel(spec().ship_id), NOON);
    assert_eq!(tether_changes(&scene.tick_tethers(NOON)), [(SESSION, 0, STRUCTURE, false)]);
}

#[test]
fn tether_ends_on_warp_removal_and_ejection_without_reusing_the_old_pilots_policy() {
    let mut scene = tether_scene();
    grant_tether(&mut scene, NOON);
    let warped = scene.begin_warp(SESSION, 3, None, Vec3::new(9_000_000.0, 0.0, 0.0), 0.0, NOON);
    assert_eq!(tether_changes(&warped), [(SESSION, STRUCTURE, 0, false)]);
    assert!(scene.pilots[&SESSION].flight.is_some());
    assert!(scene.tick_tethers(NOON).is_empty());

    let mut scene = tether_scene();
    grant_tether(&mut scene, NOON);
    assert_eq!(tether_changes(&scene.remove_structure(STRUCTURE, NOON)), [(SESSION, STRUCTURE, 0, false)]);
    assert!(scene.tick_tethers(NOON).is_empty());

    let mut scene = tether_scene();
    grant_tether(&mut scene, NOON);
    let ejected = scene.leave(SESSION, Departure::Ejected, NOON).unwrap();
    assert_eq!(tether_changes(&ejected), [(SESSION, STRUCTURE, 0, false)]);
    assert!(!scene.unpiloted.is_empty());
    assert!(scene.tick_tethers(NOON).is_empty());
}

#[test]
fn tether_ship_collisions_remain_active_and_leaving_surface_range_ends_the_link() {
    let mut scene = tether_scene();
    grant_tether(&mut scene, NOON);
    tether_other(&mut scene, 0.0);
    let before = scene.pilots[&SESSION].ship.position;
    scene.poll(NOON + SECOND / 10);
    let after = scene.pilots[&SESSION].ship.position;
    assert_ne!((before.x, before.y, before.z), (after.x, after.y, after.z));
    assert!(scene.is_tethered(spec().ship_id, NOON + SECOND / 10));
    scene.pilots.get_mut(&SESSION).unwrap().ship.position.x += 2_000.0;
    assert_eq!(tether_changes(&scene.tick_tethers(NOON + SECOND / 10)), [(SESSION, STRUCTURE, 0, false)]);
}

#[test]
fn tether_repairs_ship_capacitor_and_fitted_heat_damage_only_on_engage() {
    let mut scene = tether_scene();
    let p = scene.pilots[&SESSION].ship.position;
    scene.leave(SESSION, Departure::LoggedOff, NOON).unwrap();
    let mut hurt = spec();
    hurt.max_velocity = 0.0;
    hurt.condition = Condition { damage: 0.2, armor_damage: 0.3, shield_charge: 0.4, charge: 0.5 };
    scene.enter_in_space(SESSION, hurt, [p.x, p.y, p.z], [0.0, 0.0, 1.0], NOON).unwrap();
    let mut profile = heat_profile_for_spec(1);
    profile.modules[0].damage_hp = 10.0;
    profile.initial_rack_heat = [20.0; 3];
    scene.set_heat_profile(SESSION, profile, NOON).unwrap();
    let engaged = grant_tether(&mut scene, NOON);
    assert_eq!(tether_changes(&engaged), [(SESSION, 0, STRUCTURE, true)]);
    assert!(
        engaged
            .iter()
            .any(|out| matches!(out, Out::Condition { condition, .. } if *condition == Condition::FULL))
    );
    assert_eq!(Scene::condition_of(&scene.pilots[&SESSION], NOON), Condition::FULL);
    let module_id = spec().modules[0].item_id;
    assert_eq!(scene.pilots[&SESSION].heat.modules[&module_id].profile.damage_hp, 0.0);
    assert_eq!(scene.pilots[&SESSION].heat.levels, [20.0; 3], "repair does not invent rack cooling");

    let pilot = scene.pilots.get_mut(&SESSION).unwrap();
    pilot.modules.capacitor.as_mut().unwrap().draw(NOON, 10.0);
    pilot.heat.modules.get_mut(&module_id).unwrap().profile.damage_hp = 5.0;
    assert!(grant_tether(&mut scene, NOON).is_empty(), "a policy renewal is not another engage");
    assert!(scene.tick_tethers(NOON).is_empty());
    assert!(Scene::condition_of(&scene.pilots[&SESSION], NOON).charge < 1.0);
    assert_eq!(scene.pilots[&SESSION].heat.modules[&module_id].profile.damage_hp, 5.0);
    scene.set_tether_policy(SESSION, 1, 60_000, tether_candidates(1_000.0), NOON);
    let repaired = scene.tick_tethers(NOON + SECOND / 1_000);
    assert_eq!(tether_changes(&repaired), [(SESSION, 0, STRUCTURE, true)]);
    assert_eq!(scene.pilots[&SESSION].heat.modules[&module_id].profile.damage_hp, 0.0);
}

#[test]
fn tether_refuses_smartbomb_and_bomb_activation_without_spending_or_fake_damage() {
    use crate::modules::Action;
    let actions = [
        Action::Smartbomb { range: 5_000.0, damage: [100.0; 4], hurts_pilots: true },
        Action::Bomb {
            ball: tether_test_ball(),
            kind: crate::deployable::BombKind::Damage,
            lifetime_ms: 10_000.0,
            blast_radius: 15_000.0,
            damage: [100.0; 4],
            ecm: [0.0; 4],
            neutralize: 0.0,
        },
    ];
    for action in actions {
        let mut scene = tether_scene();
        grant_tether(&mut scene, NOON);
        let before = Scene::condition_of(&scene.pilots[&SESSION], NOON);
        let refused = scene.activate(SESSION, tether_effect(Some(action)), NOON);
        assert_eq!(refused, [Out::Refused { session: SESSION, command: 17, reason: "TETHERED" }]);
        assert_eq!(Scene::condition_of(&scene.pilots[&SESSION], NOON), before);
        assert!(scene.deployables.is_empty());
        assert!(scene.pilots[&SESSION].modules.running_count() == 0);
        assert!(scene.is_tethered(spec().ship_id, NOON));
    }
}

#[test]
fn tether_nonoffensive_modules_keep_the_link_and_restricted_families_break_and_block_it() {
    use crate::modules::Action;
    let mut scene = tether_scene();
    grant_tether(&mut scene, NOON);
    let mut utility = tether_effect(None);
    utility.capacitor_need = 5.0;
    let out = scene.activate(SESSION, utility, NOON);
    assert!(tether_changes(&out).is_empty());
    assert!(scene.is_tethered(spec().ship_id, NOON));
    assert!(grant_tether(&mut scene, NOON).is_empty());
    assert!(Scene::condition_of(&scene.pilots[&SESSION], NOON).charge < 1.0);

    for action in [
        Action::CommandBurst { range_meters: 5_000.0 },
        Action::EcmBurst { range: 5_000.0, strength: [1.0; 4] },
        Action::CynoField { ball: tether_test_ball(), lifetime_ms: 600_000.0 },
        Action::ScrambleStatus { delta: 1.0 },
    ] {
        let mut scene = tether_scene();
        grant_tether(&mut scene, NOON);
        let started = scene.activate(SESSION, tether_effect(Some(action)), NOON);
        assert_eq!(tether_changes(&started), [(SESSION, STRUCTURE, 0, false)], "{started:?}");
        assert!(grant_tether(&mut scene, NOON).is_empty());
        assert!(!scene.is_tethered(spec().ship_id, NOON));
    }
    for mode in ["siege", "triage", "bastion"] {
        let mut scene = tether_scene();
        grant_tether(&mut scene, NOON);
        let mut effect = tether_effect(None);
        effect.on_changes.push(crate::modules::Change {
            item_id: spec().ship_id,
            attribute_id: crate::modules::WARP_SCRAMBLE_STATUS,
            value: 1.0,
        });
        assert_eq!(
            tether_changes(&scene.activate(SESSION, effect, NOON)),
            [(SESSION, STRUCTURE, 0, false)],
            "{mode}"
        );
        assert!(grant_tether(&mut scene, NOON).is_empty(), "{mode} cannot retether while running");
    }
}

#[test]
fn tether_cloak_and_lance_debuff_block_until_the_local_state_allows_it() {
    let mut scene = tether_scene();
    grant_tether(&mut scene, NOON);
    scene.pilots.get_mut(&SESSION).unwrap().cloak = crate::visibility::CloakState::Cloaked;
    assert_eq!(tether_changes(&scene.tick_tethers(NOON)), [(SESSION, STRUCTURE, 0, false)]);
    scene.pilots.get_mut(&SESSION).unwrap().cloak = crate::visibility::CloakState::Uncloaked;
    assert_eq!(tether_changes(&scene.tick_tethers(NOON)), [(SESSION, 0, STRUCTURE, false)]);
    scene.pilots.get_mut(&SESSION).unwrap().disrupted_until = NOON + SECOND;
    assert_eq!(tether_changes(&scene.tick_tethers(NOON)), [(SESSION, STRUCTURE, 0, false)]);
    assert_eq!(tether_changes(&scene.tick_tethers(NOON + SECOND)), [(SESSION, 0, STRUCTURE, false)]);
    scene.pilots.get_mut(&SESSION).unwrap().cloak = crate::visibility::CloakState::Decloaking;
    assert!(scene.is_tethered(spec().ship_id, NOON + SECOND));
}

#[test]
fn tether_scram_breaks_only_when_incoming_points_overcome_the_active_core_stabilizer() {
    use crate::ewar::Lasting;
    use crate::modules::Action;
    let mut scene = tether_scene();
    let (other, _) = tether_other(&mut scene, 2_000.0);
    scene.activate(SESSION, tether_effect(Some(Action::ScrambleStatus { delta: -2.0 })), NOON);
    grant_tether(&mut scene, NOON);
    let resisted = scene.hold_for(
        other,
        SESSION,
        999,
        Lasting::Scramble { strength: 1.0, blocks_mwd: false },
        1_000.0,
        NOON,
    );
    assert!(tether_changes(&resisted).is_empty());
    assert!(scene.is_tethered(spec().ship_id, NOON));
    let scrambled = scene.hold_for(
        other,
        SESSION,
        999,
        Lasting::Scramble { strength: 3.0, blocks_mwd: true },
        1_000.0,
        NOON,
    );
    assert_eq!(tether_changes(&scrambled), [(SESSION, STRUCTURE, 0, false)]);
    assert!(grant_tether(&mut scene, NOON).is_empty());
    assert_eq!(tether_changes(&scene.poll(NOON + 2 * SECOND)), [(SESSION, 0, STRUCTURE, false)]);
}

#[test]
fn tether_fighter_launch_breaks_and_only_nonabandoned_deployed_fighters_block_reengage() {
    let mut scene = tether_scene();
    grant_tether(&mut scene, NOON);
    let mut fighter = spec();
    fighter.ship_id += 100;
    fighter.character_id = 0;
    fighter.type_id = 23_055;
    fighter.group_id = 1_652;
    fighter.category_id = 87;
    fighter.radius = 6.0;
    fighter.modules.clear();
    let fighter_id = fighter.ship_id;
    assert!(scene.launch_fighter(SESSION, fighter.clone(), 1_200, 0.5, 0, NOON).is_err());
    assert!(scene.is_tethered(spec().ship_id, NOON));
    let (_, launched) = scene.launch_fighter(SESSION, fighter, 1_200, 0.5, 6, NOON).unwrap();
    assert_eq!(tether_changes(&launched), [(SESSION, STRUCTURE, 0, false)]);
    assert!(grant_tether(&mut scene, NOON).is_empty());
    scene.fighter_order(SESSION, &[fighter_id], crate::fighters::FighterOrder::Abandon, NOON);
    assert_eq!(tether_changes(&scene.tick_tethers(NOON)), [(SESSION, 0, STRUCTURE, false)]);
}

#[test]
fn tether_blocks_direct_unresisted_and_area_damage_but_expired_permission_protects_nothing() {
    let mut scene = tether_scene();
    let (other, _) = tether_other(&mut scene, 2_000.0);
    grant_tether(&mut scene, NOON);
    assert!(scene.is_tethered(spec().ship_id, NOON));
    assert!(scene.damage(spec().ship_id, [100.0; 4], NOON).1.is_none());
    assert!(scene.damage_unresisted(spec().ship_id, [100.0; 4], NOON).1.is_none());
    assert!(scene.damage_ship(other, 1, (SESSION, spec().ship_id), [100.0; 4], NOON).is_empty());
    let p = scene.pilots[&SESSION].ship.position;
    assert!(scene.blast_at(other, 1, [p.x, p.y, p.z], 1_000.0, [100.0; 4], NOON).is_empty());
    assert!(scene.aggressions.is_empty());
    assert_eq!(Scene::condition_of(&scene.pilots[&SESSION], NOON), Condition::FULL);
    assert!(scene.damage(spec().ship_id, [100.0; 4], NOON + 60 * SECOND).1.is_some());
}

#[test]
fn tether_jump_departure_clears_permission_before_the_ship_enters_another_scene() {
    let mut origin = scene_by_a_gate();
    let gate = origin.system().body(NEAR_GATE).unwrap().clone();
    let pilot = origin.pilots.get_mut(&SESSION).unwrap();
    pilot.ship.position = Vec3::new(
        gate.position.x + gate.radius + pilot.ship.spec.radius + 500.0,
        gate.position.y,
        gate.position.z,
    );
    pilot.ship.velocity = Vec3::ZERO;
    pilot.ship.speed_fraction = 0.0;
    pilot.ship.course = Course::Stop;
    pilot.protected_until = NOON;
    let p = pilot.ship.position;
    let mut structure = structure_placement(&origin, NOON);
    structure.position = [p.x + structure.radius + spec().radius + 500.0, p.y, p.z];
    origin.place_structure(structure, NOON);
    assert_eq!(tether_changes(&grant_tether(&mut origin, NOON)), [(SESSION, 0, STRUCTURE, false)]);
    let jumping = origin.jump(SESSION, NEAR_GATE, 0, NOON);
    assert_eq!(tether_changes(&jumping), [(SESSION, STRUCTURE, 0, false)]);
    let (mut travellers, told) = origin.due_jumps(NOON + 10 * SECOND);
    assert_eq!(travellers.len(), 1);
    assert!(tether_changes(&told).is_empty());
    let mut destination = other_scene();
    destination.arrive(travellers.remove(0), NOON + 10 * SECOND).unwrap();
    let p = destination.pilots[&SESSION].ship.position;
    let mut same_id = structure_placement(&destination, NOON + 10 * SECOND);
    same_id.position = [p.x + same_id.radius + spec().radius + 500.0, p.y, p.z];
    destination.place_structure(same_id, NOON + 10 * SECOND);
    assert!(destination.tick_tethers(NOON + 10 * SECOND).is_empty());
    assert!(!destination.is_tethered(spec().ship_id, NOON + 10 * SECOND));
}

#[test]
fn tether_docking_reports_disengage_before_the_docked_condition() {
    let mut scene = tether_scene();
    grant_tether(&mut scene, NOON);
    scene.command(SESSION, Command::Dock { station_id: STRUCTURE }, NOON);
    assert!(
        scene.is_tethered(spec().ship_id, NOON + SECOND),
        "docking keeps tether until the ship leaves space"
    );
    let docked = scene.poll(NOON + 4 * SECOND);
    assert_eq!(tether_changes(&docked), [(SESSION, STRUCTURE, 0, false)]);
    let off =
        docked.iter().position(|out| matches!(out, Out::TetherChanged { structure_id: 0, .. })).unwrap();
    let dock = docked.iter().position(|out| matches!(out, Out::Docked { .. })).unwrap();
    assert!(off < dock);
}

#[test]
fn tether_offensive_start_revokes_cached_permission_until_the_host_refreshes_it() {
    let mut scene = tether_scene();
    grant_tether(&mut scene, NOON);
    let effect = tether_effect(Some(crate::modules::Action::EcmBurst { range: 5_000.0, strength: [1.0; 4] }));
    let module_id = effect.module_id;
    let started = scene.activate(SESSION, effect, NOON);
    assert_eq!(tether_changes(&started), [(SESSION, STRUCTURE, 0, false)]);
    scene.stop_module(SESSION, module_id, NOON);
    assert!(scene.tick_tethers(NOON).is_empty(), "the old grant cannot race the host's weapons timer");
    assert!(!scene.is_tethered(spec().ship_id, NOON));
    assert_eq!(tether_changes(&grant_tether(&mut scene, NOON)), [(SESSION, 0, STRUCTURE, false)]);
}

// ── wormhole ends (protocol 63) ─────────────────────────────────────────────

const WORMHOLE_END: i64 = 1_050_000_001;
const FAR_END: i64 = 1_050_000_002;

fn wormhole_placement(at: Vec3, far: Vec3, far_radius: f64) -> crate::link::WormholePlacement {
    crate::link::WormholePlacement {
        wormhole_id: WORMHOLE_END,
        solar_system_id: 30_000_001,
        name: "Wormhole E004".to_string(),
        type_id: 34_134,
        group_id: 988,
        position: [at.x, at.y, at.z],
        radius: 3_000.0,
        destination_system_id: OTHER_SYSTEM,
        destination_end: FAR_END,
        destination_class: 2,
        destination_position: [far.x, far.y, far.z],
        destination_radius: far_radius,
        max_jump_mass: 62_000_000.0,
    }
}

#[test]
fn a_wormhole_end_the_host_places_takes_a_ship_through_to_its_far_end_and_places_it_there() {
    let mut scene = scene_by_a_gate();
    let out = scene.ship(SESSION, NOON).unwrap().position;
    let far = Vec3::new(2.0e9, 1.0e8, 5.0e8);
    scene.place_wormhole(wormhole_placement(Vec3::new(out.x + 5_000.0, out.y, out.z), far, 5_000.0), NOON);
    assert!(scene.wormhole_index(WORMHOLE_END).is_some());

    let answer = scene.command(SESSION, jump(WORMHOLE_END, FAR_END), NOON);
    match &answer[1] {
        Out::Jumping { destination_system, destination_gate, .. } => {
            assert_eq!((*destination_system, *destination_gate), (OTHER_SYSTEM, FAR_END));
        }
        other => panic!("expected a jump, got {other:?}"),
    }

    let later = NOON + seconds(6);
    let traveller = scene.due_jumps(later).0.pop().unwrap();
    assert_eq!(traveller.to_far_end, Some((far, 5_000.0)));

    let mut there = Scene::new(Arc::new(gated_systems().1));
    there.arrive(traveller, later).unwrap();
    let ship = there.ship(SESSION, later).unwrap();
    let (dx, dy, dz) = (ship.position.x - far.x, ship.position.y - far.y, ship.position.z - far.z);
    let distance = (dx * dx + dy * dy + dz * dz).sqrt();
    assert!(
        (distance - (5_000.0 + spec().radius + 12_000.0)).abs() < 1e-6,
        "the ship lands clear of the far end: {distance}"
    );
    // it faces the far end
    let facing = (-dx * ship.direction.x) + (-dy * ship.direction.y) + (-dz * ship.direction.z);
    assert!(facing > 0.0, "the ship faces the far end");
}

#[test]
fn a_warp_to_a_wormhole_end_under_the_minimum_warp_distance_is_refused_and_one_beyond_it_ends_within_jump_range()
 {
    let mut scene = scene_by_a_gate();
    let out = scene.ship(SESSION, NOON).unwrap().position;
    // an end 50 km off is inside the 150 km minimum warp distance: refused, as the game refuses it
    scene.place_wormhole(
        wormhole_placement(Vec3::new(out.x + 50_000.0, out.y, out.z), Vec3::ZERO, 5_000.0),
        NOON,
    );
    let near =
        scene.command(SESSION, Command::WarpTo { target_id: WORMHOLE_END, range: 0.0 }, NOON + seconds(1));
    assert!(matches!(near.as_slice(), [Out::Refused { reason: "WARP_DISTANCE_TOO_CLOSE", .. }]), "{near:?}");
    scene.remove_wormhole(WORMHOLE_END, NOON);

    // an end 200,000 km off: the warp is made and ends within the jump range of the end, and the jump is accepted
    scene.place_wormhole(
        wormhole_placement(Vec3::new(out.x + 2.0e11, out.y, out.z), Vec3::ZERO, 5_000.0),
        NOON,
    );
    let told =
        scene.command(SESSION, Command::WarpTo { target_id: WORMHOLE_END, range: 0.0 }, NOON + seconds(1));
    assert_eq!(
        action_names(&told[0]),
        ["SetMaxSpeed", "WarpTo", "OnSpecialFX", "SetSpeedFraction"],
        "{told:?}"
    );
    let mut t = NOON + seconds(1);
    while t < NOON + seconds(300) {
        t += 2_500_000;
        scene.poll(t);
    }
    let ship = scene.ship(SESSION, t).unwrap().clone();
    assert!(!matches!(ship.course, Course::Warp { .. }), "the warp is over: {:?}", ship.course);
    let end = scene.system().body(WORMHOLE_END).unwrap();
    let surface = crate::gate::surface_distance(end, ship.position, ship.spec.radius);
    assert!(surface <= crate::gate::JUMP_RANGE, "{surface} m off the wormhole's surface");
    let jumped = scene.command(SESSION, jump(WORMHOLE_END, FAR_END), t + seconds(1));
    assert!(matches!(jumped[1], Out::Jumping { .. }), "{jumped:?}");
}

#[test]
fn a_wormhole_end_inside_a_star_is_placed_outside_it_on_its_bearing() {
    // the host places an end 50 km from its system's centre; in this system the star's body (5e8 m) holds that point
    let mut star_system = (*system()).clone();
    star_system.bodies.push(Body {
        id: 40_000_900,
        type_id: 6_000,
        group_id: 6,
        category_id: 2,
        owner_id: 0,
        name: "Test Star".to_string(),
        kind: BodyKind::Star,
        position: Vec3::ZERO,
        radius: 5.0e8,
        graphic_id: None,
        detail: Detail::None,
    });
    let mut scene = Scene::new(Arc::new(star_system));
    scene.place_wormhole(wormhole_placement(Vec3::new(50_000.0, 0.0, 0.0), Vec3::ZERO, 5_000.0), NOON);
    let placed = scene.system().body(WORMHOLE_END).unwrap().position;
    let distance = (placed.x * placed.x + placed.y * placed.y + placed.z * placed.z).sqrt();
    assert!(distance >= 5.0e8 + 3_000.0 + 50_000.0 - 1e-3, "{distance} m from the star's centre");
    assert!(placed.x > 0.0 && placed.y.abs() < 1e-6 && placed.z.abs() < 1e-6, "on its bearing: {placed:?}");
}

#[test]
fn a_wormhole_end_out_of_reach_refuses_the_jump_and_a_removed_one_is_not_found() {
    let mut scene = scene_by_a_gate();
    let out = scene.ship(SESSION, NOON).unwrap().position;
    scene.place_wormhole(
        wormhole_placement(Vec3::new(out.x + 60_000.0, out.y, out.z), Vec3::ZERO, 5_000.0),
        NOON,
    );
    assert_eq!(refusal(&scene.command(SESSION, jump(WORMHOLE_END, FAR_END), NOON)), "TOO_FAR_FROM_STARGATE");

    scene.remove_wormhole(WORMHOLE_END, NOON);
    assert!(scene.wormhole_index(WORMHOLE_END).is_none());
    assert_eq!(refusal(&scene.command(SESSION, jump(WORMHOLE_END, FAR_END), NOON)), "STARGATE_NOT_FOUND");
}
