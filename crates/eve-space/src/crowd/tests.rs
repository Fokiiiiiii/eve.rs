use std::sync::Arc;

use eve_destiny::Vec3;

use super::*;
use crate::scene::tests::{NOON, STATION, spec, system};
use crate::scene::{Command, Departure};
use crate::world::{Body, BodyKind, Detail};

const A: SessionId = 7;
const B: SessionId = 8;
const SECOND: i64 = 10_000_000;
const PLANET: i64 = 40_000_001;

#[test]
fn a_structure_observer_tracks_asteroid_visibility_and_seen_state_separately() {
    let mut scene = watcher();
    let placement = crate::scene::tests::structure_placement(&scene, NOON);
    let structure_id = placement.structure_id;
    scene.place_structure(placement, NOON);
    scene.control_structure(A, scene.solar_system_id(), structure_id, spec().character_id, true, NOON);
    scene.bind(A, NOON);
    let asteroid_id = 1_050_000_000_001;
    let position = scene.structure_body(structure_id).unwrap().position;
    let added = scene.place_asteroid(crate::wreck::Asteroid {
        id: asteroid_id, type_id: 1230, group_id: 450, category_id: 25,
        name: "Veldspar".into(), graphic_id: None,
        position, direction: Vec3::new(0.0, 0.0, 1.0), radius: 100.0, units_remaining: 100,
    }, NOON);
    assert!(added.iter().any(|out| matches!(out, Out::Notify { session: A, .. })));
    assert!(scene.sees_asteroid(A, asteroid_id));
    assert!(!scene.change_asteroid(asteroid_id, 99, NOON).is_empty());

    scene.asteroids.get_mut(&asteroid_id).unwrap().position = Vec3::new(crate::visibility::GRID_BOX * 2.0, 0.0, 0.0);
    let removed = scene.reconcile_asteroids(NOON);
    assert!(removed.iter().any(|out| matches!(out, Out::Notify { session: A, .. })));
    assert!(!scene.sees_asteroid(A, asteroid_id));
    assert!(scene.change_asteroid(asteroid_id, 98, NOON).is_empty());
}

fn other_spec() -> crate::ship::ShipSpec {
    let mut other = spec();
    other.ship_id += 4;
    other.character_id += 1;
    other.owner_id += 1;
    other.name = "Second".to_string();
    other
}

/// The system of the tests with a planet far off (for warps).
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
    Scene::new(Arc::new(world))
}

/// A ship in space that has bound the ballpark.
fn watcher() -> Scene {
    let mut scene = scene_with_a_planet();
    scene.undock(A, spec(), STATION, NOON).unwrap();
    scene.bind(A, NOON);
    scene
}

fn both() -> Scene {
    let mut scene = watcher();
    scene.undock(B, other_spec(), STATION, NOON + SECOND).unwrap();
    scene.bind(B, NOON + SECOND);
    scene
}

fn names(out: &Out) -> Vec<String> {
    let Out::Notify { args, .. } = out else { return Vec::new() };
    let Some(Value::List(actions)) = args.first() else { return Vec::new() };
    actions
        .iter()
        .map(|a| {
            let Value::Tuple(pair) = a else { panic!("{a:?}") };
            let Value::Tuple(inner) = &pair[1] else { panic!("{:?}", pair[1]) };
            let Value::Str(name) = &inner[0] else { panic!("{:?}", inner[0]) };
            name.to_string()
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

fn recipient(out: &Out) -> SessionId {
    match out {
        Out::Notify { session, .. } | Out::Accepted { session, .. } => *session,
        other => panic!("{other:?}"),
    }
}

/// The bytes of the ball buffer of the `SetState` an update carries.
fn state_buffer(out: &Out) -> Vec<u8> {
    let Out::Notify { args, .. } = out else { panic!("{out:?}") };
    let Some(Value::List(actions)) = args.first() else { panic!("{out:?}") };
    let Value::Tuple(pair) = &actions[0] else { panic!() };
    let Value::Tuple(inner) = &pair[1] else { panic!() };
    let Value::Tuple(arguments) = &inner[1] else { panic!() };
    let Value::Object { args, .. } = &arguments[0] else { panic!("{:?}", arguments[0]) };
    let Value::Dict(fields) = &**args else { panic!() };
    let (_, buffer) = fields.iter().find(|(k, _)| matches!(k, Value::Str(s) if s == "state")).unwrap();
    let Value::Buffer(bytes) = buffer else { panic!("{buffer:?}") };
    bytes.to_vec()
}

fn holds_ball_of(buffer: &[u8], ship_id: i64) -> bool {
    let needle = ship_id.to_le_bytes();
    buffer.windows(8).any(|w| w == needle)
}

/// How many slim items an `AddBalls2` of an update carries, and the ids of their ships.
fn added_ships(out: &Out) -> Vec<i64> {
    let Out::Notify { args, .. } = out else { panic!("{out:?}") };
    let Some(Value::List(actions)) = args.first() else { panic!("{out:?}") };
    let Value::Tuple(pair) = &actions[0] else { panic!() };
    let Value::Tuple(inner) = &pair[1] else { panic!() };
    let Value::Tuple(arguments) = &inner[1] else { panic!() };
    let Value::Tuple(added) = &arguments[0] else { panic!() };
    let Value::List(extras) = &added[1] else { panic!() };
    extras
        .iter()
        .map(|extra| {
            let Value::Tuple(entry) = extra else { panic!() };
            let Value::Dict(slim) = &entry[0] else { panic!() };
            let (_, item) = slim.iter().find(|(k, _)| matches!(k, Value::Str(s) if s == "itemID")).unwrap();
            match item {
                Value::Int(n) | Value::Long(n) => *n,
                other => panic!("{other:?}"),
            }
        })
        .collect()
}

#[test]
fn a_ship_that_undocks_is_shown_to_the_pilot_that_has_bound_the_ballpark_with_its_numbers_in_one_update() {
    let mut scene = watcher();
    let told = scene.undock(B, other_spec(), STATION, NOON + SECOND).unwrap();
    assert_eq!(told.len(), 1);
    assert_eq!(recipient(&told[0]), A);
    assert_eq!(names(&told[0]), ["AddBalls2", "SetBallAgility", "SetBallMass", "SetMaxSpeed"]);
    let stamps = stamps(&told[0]);
    assert!(stamps.iter().all(|s| *s == stamps[0]), "one stamp: {stamps:?}");
    assert_eq!(added_ships(&told[0]), [other_spec().ship_id]);
}

#[test]
fn a_pilot_that_has_not_bound_the_ballpark_is_told_nothing_and_is_given_the_ships_when_it_binds() {
    let mut scene = scene_with_a_planet();
    scene.undock(A, spec(), STATION, NOON).unwrap();
    assert!(scene.undock(B, other_spec(), STATION, NOON).unwrap().is_empty());
    let bound = scene.bind(A, NOON + SECOND);
    assert_eq!(
        added_ships(&bound[2]),
        [spec().ship_id, other_spec().ship_id],
        "both, in the order they came"
    );
}

#[test]
fn binding_gives_the_ships_in_view_in_the_order_they_came_and_the_numbers_of_each() {
    let mut scene = watcher();
    scene.undock(B, other_spec(), STATION, NOON + SECOND).unwrap();
    let bound = scene.bind(B, NOON + SECOND);
    assert_eq!(
        bound.len(),
        6,
        "the time, the clock, the ships, the ship's own start, the state, the numbers"
    );
    assert_eq!(added_ships(&bound[2]), [spec().ship_id, other_spec().ship_id]);
    assert_eq!(
        names(&bound[5]),
        [
            "SetBallAgility",
            "SetBallMass",
            "SetMaxSpeed", // the other ship's
            "SetBallAgility",
            "SetMaxSpeed", // its own: the mass came with the start
        ]
    );
    // the state holds the balls of both ships
    let buffer = state_buffer(&bound[4]);
    assert!(holds_ball_of(&buffer, spec().ship_id) && holds_ball_of(&buffer, other_spec().ship_id));
    // the state lists the ships in the same order
    let Out::Notify { args, .. } = &bound[4] else { panic!() };
    let text = format!("{args:?}");
    let (first, second) =
        (text.find(&spec().ship_id.to_string()), text.rfind(&other_spec().ship_id.to_string()));
    assert!(first.is_some() && second.is_some() && first < second, "{text:.200}");
}

#[test]
fn an_order_is_told_to_the_pilot_and_to_the_watcher_at_one_stamp() {
    let mut scene = both();
    let out = scene.command(B, Command::SetSpeedFraction(0.5), NOON + 2 * SECOND);
    assert_eq!(
        out.iter().map(recipient).collect::<Vec<_>>(),
        [B, A, B],
        "the pilot, the watcher, and the word to the host"
    );
    assert_eq!(names(&out[0]), ["SetSpeedFraction"]);
    assert_eq!(names(&out[1]), ["SetSpeedFraction"]);
    assert_eq!(stamps(&out[0]), stamps(&out[1]));
    assert!(matches!(out[2], Out::Accepted { session: B, command: 1, .. }));
}

#[test]
fn the_ships_are_listed_in_the_order_they_came_into_the_scene_not_in_the_order_of_their_sessions() {
    let (first, second): (SessionId, SessionId) = (9, 3);
    let mut scene = scene_with_a_planet();
    scene.undock(first, spec(), STATION, NOON).unwrap();
    scene.bind(first, NOON);
    scene.undock(second, other_spec(), STATION, NOON + SECOND).unwrap();
    let bound = scene.bind(second, NOON + SECOND);
    assert_eq!(
        added_ships(&bound[2]),
        [spec().ship_id, other_spec().ship_id],
        "the one that came first is first"
    );
}

#[test]
fn a_ship_that_left_is_not_in_the_state_of_the_others_when_it_comes_back_out_of_their_view() {
    let mut scene = both();
    scene.leave(B, Departure::LoggedOff, NOON + 2 * SECOND).unwrap();
    // A goes off to another box, and B undocks again
    scene.pilots.get_mut(&A).unwrap().ship.position = Vec3::new(2.0e9, 0.0, 0.0);
    scene.undock(B, other_spec(), STATION, NOON + 3 * SECOND).unwrap();
    let state = scene.update_state(A, NOON + 3 * SECOND);
    assert!(!holds_ball_of(&state_buffer(&state[0]), other_spec().ship_id), "B is out of A's view");
}

#[test]
fn a_pilot_that_has_not_bound_the_ballpark_is_told_nothing_of_the_others_orders() {
    let mut scene = watcher();
    scene.undock(B, other_spec(), STATION, NOON + SECOND).unwrap();
    // B is in space but has not bound: A's order is not told to B
    let out = scene.command(A, Command::SetSpeedFraction(0.5), NOON + 2 * SECOND);
    assert_eq!(out.iter().map(recipient).collect::<Vec<_>>(), [A, A]);
}

#[test]
fn a_ship_is_told_to_those_who_watch_as_a_turn_when_it_asks_for_a_warp_and_as_the_warp_when_it_enters_it() {
    let mut scene = both();
    let at = NOON + 2 * SECOND;
    let out = scene.command(B, Command::WarpTo { target_id: PLANET, range: 0.0 }, at);
    assert_eq!(names(&out[0]), ["SetMaxSpeed", "WarpTo", "OnSpecialFX", "SetSpeedFraction"]);
    assert_eq!((recipient(&out[1]), names(&out[1])), (A, vec!["GotoDirection".to_string()]));

    let mut entered = None;
    let mut t = at;
    while t < at + 30 * SECOND && entered.is_none() {
        t += SECOND / 4;
        entered =
            scene.poll(t).into_iter().find(|o| names(o).first().map(String::as_str) == Some("OnSpecialFX"));
    }
    let entered = entered.expect("the ship enters warp");
    assert_eq!(recipient(&entered), A, "the pilot was told at the command");
    assert_eq!(names(&entered), ["OnSpecialFX", "SetSpeedFraction", "WarpTo"]);
}

#[test]
fn a_ship_that_warps_off_is_taken_from_view_of_the_others_at_fifty_thousand_kilometres_and_they_from_its() {
    let mut scene = both();
    let at = NOON + 2 * SECOND;
    scene.command(B, Command::WarpTo { target_id: PLANET, range: 0.0 }, at);
    let mut removals: Vec<(SessionId, Vec<String>)> = Vec::new();
    let mut t = at;
    while t < at + 60 * SECOND && removals.len() < 2 {
        t += SECOND / 4;
        for out in scene.poll(t) {
            if names(&out).first().map(String::as_str) == Some("PackagedAction") {
                removals.push((recipient(&out), names(&out)));
            }
        }
    }
    removals.sort();
    assert_eq!(
        removals.iter().map(|(s, _)| *s).collect::<Vec<_>>(),
        [A, B],
        "each was told the other is gone"
    );
    assert!(t < at + 30 * SECOND, "and it is a matter of seconds, as the recording's 7");
    // the ships no longer see each other, and the state says so
    let state = scene.update_state(A, t);
    let text = format!("{:?}", state[0]);
    assert!(
        !text.contains(&other_spec().ship_id.to_string()),
        "the ship that warped off is not in the state"
    );
}

#[test]
fn a_ship_that_warps_to_a_place_in_another_box_within_the_envelope_is_taken_from_view_when_it_comes_to_rest()
{
    const BELT: i64 = 40_000_009;
    let mut world = (*system()).clone();
    world.bodies.push(Body {
        id: BELT,
        type_id: 15,
        group_id: 9,
        category_id: 2,
        owner_id: 0,
        name: "Testing Belt".to_string(),
        kind: BodyKind::Belt,
        position: Vec3::new(20_000_000.0, 0.0, 0.0), // 20 000 km: two boxes off, and inside the 50 000 km of the warp envelope
        radius: 1_000.0,
        graphic_id: None,
        detail: Detail::None,
    });
    let mut scene = Scene::new(Arc::new(world));
    scene.undock(A, spec(), STATION, NOON).unwrap();
    scene.bind(A, NOON);
    scene.undock(B, other_spec(), STATION, NOON + SECOND).unwrap();
    scene.bind(B, NOON + SECOND);
    let at = NOON + 2 * SECOND;
    scene.command(B, Command::WarpTo { target_id: BELT, range: 0.0 }, at);

    let mut removals: Vec<(i64, SessionId)> = Vec::new();
    let mut massless = None;
    let mut rested = None;
    let mut t = at;
    while t < at + 300 * SECOND && (rested.is_none() || removals.len() < 2) {
        t += SECOND / 4;
        for out in scene.poll(t) {
            match names(&out).first().map(String::as_str) {
                Some("PackagedAction") => removals.push((t, recipient(&out))),
                Some("SetBallMassive") if recipient(&out) == B => massless = massless.or(Some(t)),
                Some("SetBallPosition") if recipient(&out) == B => rested = Some(t),
                _ => {}
            }
        }
    }
    let (massless, rested) = (massless.expect("the ship drops out"), rested.expect("the ship comes to rest"));
    assert_eq!(removals.len(), 2, "each is told the other is gone: {removals:?}");
    // it stayed in view while it warped and went from view at the poll in which the warp ended (the drop-out comes two seconds after the
    // warning), though neither ship had crossed into another box since the poll before: the ship comes to rest half a minute later
    assert!(
        removals.iter().all(|(when, _)| *when >= massless + 2 * SECOND && *when <= massless + 3 * SECOND),
        "removals {removals:?}, the warning at {massless}, the rest at {rested}"
    );
}

#[test]
fn a_ship_that_docks_is_shown_to_stop_and_then_taken_from_view_when_it_has_docked() {
    // a station so big that a ship that has just come out of it is within docking range
    let mut world = (*system()).clone();
    world.bodies[0].radius = 70_000.0;
    let mut scene = Scene::new(Arc::new(world));
    scene.undock(A, spec(), STATION, NOON).unwrap();
    scene.bind(A, NOON);
    scene.undock(B, other_spec(), STATION, NOON + SECOND).unwrap();
    scene.bind(B, NOON + SECOND);
    let out = scene.command(B, Command::Dock { station_id: STATION }, NOON + 2 * SECOND);
    let told: Vec<(SessionId, Vec<String>)> =
        out.iter().filter(|o| matches!(o, Out::Notify { .. })).map(|o| (recipient(o), names(o))).collect();
    assert_eq!(
        told,
        [
            (B, vec![]), // OnDockingAccepted is a named event, not an update
            (B, vec!["SetBallMassive".to_string(), "Stop".to_string()]),
            (A, vec!["SetBallMassive".to_string(), "Stop".to_string()]),
        ]
    );
    assert!(scene.poll(NOON + 5 * SECOND).is_empty(), "four seconds have not passed");
    let out = scene.poll(NOON + 6 * SECOND + 1);
    assert!(matches!(out[0], Out::Docked { session: B, .. }));
    assert_eq!((recipient(&out[1]), names(&out[1])), (A, vec!["RemoveBalls".to_string()]));
}

#[test]
fn a_ship_that_leaves_is_taken_from_view() {
    let mut scene = both();
    let told = scene.leave(B, Departure::LoggedOff, NOON + 2 * SECOND).unwrap();
    assert_eq!((told.len(), recipient(&told[0]), names(&told[0])), (1, A, vec!["RemoveBalls".to_string()]));
    // A is not told again when it asks for the state: the ship is not there
    let state = scene.update_state(A, NOON + 3 * SECOND);
    assert!(!format!("{:?}", state[0]).contains(&other_spec().ship_id.to_string()));
}

#[test]
fn ships_in_different_boxes_of_the_grid_are_not_shown_to_each_other_until_they_share_one() {
    let mut scene = watcher();
    // A goes off to another box
    scene.pilots.get_mut(&A).unwrap().ship.position = Vec3::new(2.0e9, 0.0, 0.0);
    assert!(scene.undock(B, other_spec(), STATION, NOON + SECOND).unwrap().is_empty(), "A cannot see B");
    scene.bind(B, NOON + SECOND);
    assert!(scene.poll(NOON + 2 * SECOND).is_empty(), "nothing has changed");
    // A comes back inside B's grid box but outside its hull
    let there = scene.pilots[&B].ship.position;
    let separation = scene.pilots[&A].ship.spec.radius + scene.pilots[&B].ship.spec.radius + 1.0;
    scene.pilots.get_mut(&A).unwrap().ship.position = Vec3::new(there.x - separation, there.y, there.z);
    let told = scene.poll(NOON + 3 * SECOND);
    assert_eq!(told.len(), 2, "each is shown the other");
    assert_eq!(told.iter().map(recipient).collect::<Vec<_>>(), [A, B]);
    assert!(told.iter().all(|o| names(o) == ["AddBalls2", "SetBallAgility", "SetBallMass", "SetMaxSpeed"]));
}

#[test]
fn the_same_calls_at_the_same_times_give_the_same_bytes() {
    let run = || {
        let mut scene = both();
        let mut out = scene.command(B, Command::SetSpeedFraction(0.5), NOON + 2 * SECOND);
        out.extend(scene.command(B, Command::WarpTo { target_id: PLANET, range: 0.0 }, NOON + 3 * SECOND));
        let mut t = NOON + 3 * SECOND;
        while t < NOON + 60 * SECOND {
            t += SECOND / 4;
            out.extend(scene.poll(t));
        }
        out
    };
    assert_eq!(run(), run());
}

fn can_kind() -> crate::wreck::WreckKind {
    crate::wreck::WreckKind { type_id: 23, name: "Cargo Container".to_string(), graphic_id: Some(1176) }
}

fn credit() -> crate::death::Credit {
    crate::death::Credit {
        character_id: 140_000_342,
        corporation_id: 1_000_006,
        alliance_id: 0,
        war_faction_id: 0,
    }
}

fn can_for(session: SessionId, id: i64, distance: f64) -> crate::link::ContainerPlacement {
    crate::link::ContainerPlacement {
        session_id: session,
        container_id: id,
        kind: can_kind(),
        radius: 14.0,
        launcher_id: spec().ship_id,
        credit: credit(),
        distance,
    }
}

fn slim_of(out: &Out, action_name: &str) -> Option<Vec<(String, Value)>> {
    let Out::Notify { args, .. } = out else { return None };
    let Some(Value::List(actions)) = args.first() else { return None };
    for action in actions {
        let Value::Tuple(pair) = action else { continue };
        let Value::Tuple(inner) = &pair[1] else { continue };
        if !matches!(&inner[0], Value::Str(name) if &**name == action_name) {
            continue;
        }
        let Value::Tuple(arguments) = &inner[1] else { continue };
        // AddBalls2 holds (buffer, [(slim, damage)]); OnSlimItemChange holds the id and the slim item
        let slim = match (action_name, &arguments[0]) {
            ("AddBalls2", Value::Tuple(parts)) => match &parts[1] {
                Value::List(items) => match &items[0] {
                    Value::Tuple(pair) => pair[0].clone(),
                    other => other.clone(),
                },
                other => other.clone(),
            },
            _ => arguments[1].clone(),
        };
        let entries = match slim {
            Value::Dict(entries) => entries,
            Value::Object { args, .. } => match *args {
                Value::Dict(entries) => entries,
                _ => return None,
            },
            _ => return None,
        };
        return Some(
            entries
                .into_iter()
                .map(|(k, v)| match k {
                    Value::Str(name) => (name.to_string(), v),
                    other => (format!("{other:?}"), v),
                })
                .collect(),
        );
    }
    None
}

fn keys(entries: &[(String, Value)]) -> Vec<&str> {
    entries.iter().map(|(k, _)| k.as_str()).collect()
}

#[test]
fn a_jettisoned_can_comes_ahead_of_the_ship_and_the_pilot_is_told_the_ball_then_the_effect_and_the_host_where_it_lies()
 {
    let mut scene = watcher();
    let ship = scene.ship(A, NOON + SECOND).unwrap().clone();
    let out = scene.place_container(A, can_for(A, 9_988_400_002_948, 275.0), NOON + SECOND);

    // the pilot is sent two updates, one stamp, and the host the place
    assert_eq!(out.len(), 3, "{out:?}");
    assert_eq!(names(&out[0]), ["AddBalls2"]);
    assert_eq!(names(&out[1]), ["OnSpecialFX"]);
    assert_eq!(stamps(&out[0]), stamps(&out[1]));
    let Out::ContainerPlaced { session, container_id, position, direction } = &out[2] else {
        panic!("{out:?}")
    };
    assert_eq!((*session, *container_id), (A, 9_988_400_002_948));
    let ahead = ((position.x - ship.position.x).powi(2)
        + (position.y - ship.position.y).powi(2)
        + (position.z - ship.position.z).powi(2))
    .sqrt();
    assert!((ahead - 275.0).abs() < 1e-6, "{ahead}");
    assert!(
        (direction.x * direction.x + direction.y * direction.y + direction.z * direction.z - 1.0).abs()
            < 1e-9
    );
    // the slim item of the ball has no rights and no turn: the reference defers the rights until something is in the can
    let slim = slim_of(&out[0], "AddBalls2").expect("the slim item");
    assert_eq!(
        keys(&slim),
        [
            "itemID",
            "typeID",
            "ownerID",
            "name",
            "groupID",
            "categoryID",
            "graphicID",
            "corpID",
            "allianceID",
            "warFactionID",
            "securityStatus",
            "isEmpty",
            "launcherID"
        ]
    );
    assert_eq!(
        scene.wreck(9_988_400_002_948).map(|w| (w.group_id, w.is_empty)),
        Some((crate::wreck::CONTAINER_GROUP, true))
    );
}

#[test]
fn a_can_is_never_nearer_than_fifty_metres_and_a_pilot_with_no_ship_gets_none() {
    let mut scene = watcher();
    let ship = scene.ship(A, NOON).unwrap().clone();
    let out = scene.place_container(A, can_for(A, 9_988_400_002_949, 3.0), NOON);
    let Some(Out::ContainerPlaced { position, .. }) = out.last() else { panic!("{out:?}") };
    let ahead = ((position.x - ship.position.x).powi(2)
        + (position.y - ship.position.y).powi(2)
        + (position.z - ship.position.z).powi(2))
    .sqrt();
    assert!((ahead - 50.0).abs() < 1e-6, "{ahead}");

    let none = scene.place_container(99, can_for(99, 9_988_400_002_950, 275.0), NOON);
    assert!(matches!(&none[..], [Out::Refused { session: 99, reason: "NO_SHIP_IN_SPACE", .. }]), "{none:?}");
    assert!(scene.wreck(9_988_400_002_950).is_none());
}

#[test]
fn what_is_put_in_a_can_is_told_as_its_slim_item_with_the_rights_and_what_is_taken_out_of_it_ends_with_the_ball_going()
 {
    let mut scene = watcher();
    let id = 9_988_400_002_948;
    scene.place_container(A, can_for(A, id, 275.0), NOON);

    let filled = scene.change_wreck(id, false, NOON + SECOND);
    assert_eq!(filled.len(), 1);
    assert_eq!(names(&filled[0]), ["OnSlimItemChange"]);
    let slim = slim_of(&filled[0], "OnSlimItemChange").expect("the slim item");
    assert_eq!(
        keys(&slim),
        [
            "itemID",
            "typeID",
            "ownerID",
            "name",
            "groupID",
            "categoryID",
            "graphicID",
            "corpID",
            "allianceID",
            "warFactionID",
            "securityStatus",
            "isEmpty",
            "lootRights",
            "launcherID"
        ]
    );

    // the host takes the can out when it is empty: the destruction effect plays, then the ball goes, in one stamp
    let gone = scene.remove_wreck(id, 3, NOON + 2 * SECOND);
    assert_eq!(gone.len(), 1);
    assert_eq!(names(&gone[0]), ["TerminalPlayDestructionEffect", "RemoveBalls"]);
    assert!(scene.wreck(id).is_none());
    assert!(scene.pilots[&A].sees_wrecks.is_empty());
    // a can that is not there is nothing to tell
    assert!(scene.remove_wreck(id, 3, NOON + 3 * SECOND).is_empty());
    assert!(scene.change_wreck(id, true, NOON + 3 * SECOND).is_empty());
}

#[test]
fn a_can_that_was_filled_and_is_emptied_again_keeps_telling_who_may_take_what_is_in_it() {
    // recorded (`loot.json`): "Loot All" empties the can, and the slim item that says so (`isEmpty` 1) still has the rights
    let mut scene = watcher();
    let id = 9_988_400_002_948;
    scene.place_container(A, can_for(A, id, 275.0), NOON);
    let before = scene.change_wreck(id, true, NOON + SECOND);
    let slim = slim_of(&before[0], "OnSlimItemChange").expect("the slim item");
    assert!(!keys(&slim).contains(&"lootRights"), "nothing has been in it yet");

    scene.change_wreck(id, false, NOON + 2 * SECOND);
    let emptied = scene.change_wreck(id, true, NOON + 3 * SECOND);
    let slim = slim_of(&emptied[0], "OnSlimItemChange").expect("the slim item");
    assert!(keys(&slim).contains(&"lootRights"), "{:?}", keys(&slim));
}

#[test]
fn how_far_a_can_is_is_measured_from_the_centre_of_the_ship_to_the_surface_of_the_can() {
    let mut scene = watcher();
    let id = 9_988_400_002_948;
    scene.place_container(A, can_for(A, id, 275.0), NOON);
    let distance = scene.distance_to_wreck(A, id, NOON).expect("the can is there");
    // 275 ahead, the can's radius is 14
    assert!((distance - 261.0).abs() < 1e-6, "{distance}");
    assert_eq!(scene.distance_to_wreck(A, 12, NOON), None);
    assert_eq!(scene.distance_to_wreck(99, id, NOON), None);
}
