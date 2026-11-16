//! A ship warps to a stargate and jumps as the reference's ship did (`oracle/fixtures/scenarios/jump.json`): where the warp ends, what the
//! client is told when the ship goes into the gate, how long it is before the ship is in the other system, where it appears and what it is sent
//! when it binds the ballpark there.
mod support;

use std::sync::Arc;

use eve_destiny::Vec3;
use eve_space::gate;
use eve_space::scene::{Command, Out, Scene};
use eve_space::sde::Sde;
use eve_space::ship::ShipSpec;
use eve_space::world::{SolarSystem, World};
use serde_json::Value;
use support::*;

const SOURCE: i32 = 30100032;
const DESTINATION: i32 = 30100034;
const GATE: i64 = 50016630;
const ARRIVAL_GATE: i64 = 50016839;
const SESSION: u64 = 1;
const NOW: i64 = 134351527024310000;
const SECOND: i64 = 10_000_000;

struct Recorded {
    race: Value,
    spec: ShipSpec,
    start: Vec3,
    source: Arc<SolarSystem>,
    destination: Arc<SolarSystem>,
}

fn recorded() -> Option<Recorded> {
    let Some(sde) = Sde::locate() else {
        eprintln!("no static data found (EVE_SDE_DIR or .cache/sde): skipped");
        return None;
    };
    let world = World::load(sde).expect("the static data loads");
    let race = fixture("jump.json")["races"][0].clone();
    let (_, _, buffer) = initial_ship_addition(&race);
    let mut ship = recorded_ship(&race, &buffer, recorded_direction(&race));
    ship.spec.warp_speed_au = 3.0;
    Some(Recorded {
        race,
        spec: ship.spec,
        start: ship.position,
        source: Arc::new(world.system(SOURCE).unwrap()),
        destination: Arc::new(world.system(DESTINATION).unwrap()),
    })
}

fn gap(a: Vec3, b: Vec3) -> f64 {
    ((a.x - b.x).powi(2) + (a.y - b.y).powi(2) + (a.z - b.z).powi(2)).sqrt()
}

fn point(args: &Value) -> Vec3 {
    let a = args["$tuple"].as_array().unwrap();
    Vec3::new(a[1].as_f64().unwrap(), a[2].as_f64().unwrap(), a[3].as_f64().unwrap())
}

fn recorded_ship_position(step: &Value, ship_id: i64) -> Vec3 {
    let state = set_state(step);
    let buffer = unhex(state["state"]["$buffer"].as_str().unwrap());
    let offset = buffer
        .windows(8)
        .position(|bytes| bytes == ship_id.to_le_bytes())
        .expect("SetState contains the ship's ball");
    Vec3::new(f64_at(&buffer, offset + 13), f64_at(&buffer, offset + 21), f64_at(&buffer, offset + 29))
}

#[test]
fn the_recorded_ship_came_to_rest_by_the_gate_where_our_rule_lets_a_ship_come_to_rest() {
    let Some(r) = recorded() else { return };
    let gate = r.source.body(GATE).unwrap();
    let warp = step(&r.race, "warp to the stargate");
    let (_, _, aim) = destiny_actions(warp).into_iter().find(|(_, name, _)| name == "WarpTo").unwrap();
    let raw = point(&aim);
    let stop = aim["$tuple"][4].as_f64().unwrap();
    let rest = recorded_ship_position(step(&r.race, "look at the state"), r.spec.ship_id);

    // the point on the gate's sphere on the side the ship came from, and a half sphere of 2.5 km round it
    let away =
        Vec3::new(r.start.x - gate.position.x, r.start.y - gate.position.y, r.start.z - gate.position.z);
    let length = gap(r.start, gate.position);
    let from_gate = Vec3::new(away.x / length, away.y / length, away.z / length);
    let radius = gate::radius(gate);
    let envelope = Vec3::new(
        gate.position.x + from_gate.x * radius,
        gate.position.y + from_gate.y * radius,
        gate.position.z + from_gate.z * radius,
    );
    assert!(gap(rest, envelope) <= 2_500.0 + 20.0, "{} m from the envelope point", gap(rest, envelope));
    // The V24 client approaches and activates a gate by surface distance, with no side-of-sphere test
    // (autopilot.py:338-358; appConst.py:1467). The recorded stop is 745.3 m inside this SDE radius.
    assert!(
        gate::surface_distance(gate, rest, r.spec.radius) <= gate::JUMP_RANGE,
        "within jump range of the gate"
    );
    // The captured V24 request uses a zero stop distance; the ship comes to rest within the recorded 150 m movement tolerance.
    assert!((0.0..=2_500.0).contains(&stop), "{stop}");
    assert!(
        (gap(raw, rest) - stop).abs() < 150.0,
        "{} m past the landing point for a stop distance of {stop}",
        gap(raw, rest)
    );
}

#[test]
fn the_recorded_ship_appeared_in_the_cone_in_front_of_the_gate_where_our_rule_puts_a_ship() {
    let Some(r) = recorded() else { return };
    let gate = r.destination.body(ARRIVAL_GATE).unwrap();
    let appeared = recorded_ship_position(step(&r.race, "look at the state there"), r.spec.ship_id);
    let apart = gap(appeared, gate.position);
    // the radii and the clearance
    let expected = gate::radius(gate) + r.spec.radius + 12_000.0;
    assert!((apart - expected).abs() < 1.0, "{apart} m from the gate, expected {expected}");
    // the cone
    let facing = gate::forward(gate);
    let along = ((appeared.x - gate.position.x) * facing.x
        + (appeared.y - gate.position.y) * facing.y
        + (appeared.z - gate.position.z) * facing.z)
        / apart;
    assert!(
        along >= (std::f64::consts::PI / 4.0).cos() - 1e-6,
        "cos of the angle off the gate's axis: {along}"
    );
}

/// Everything of a `DoDestinyUpdate` but its stamps: the actions' names and arguments.
fn without_stamps(args: &Value) -> Vec<Value> {
    args["$tuple"][0].as_array().unwrap().iter().map(|a| a["$tuple"][1].clone()).collect()
}

fn stamps_of(args: &Value) -> Vec<i64> {
    args["$tuple"][0].as_array().unwrap().iter().map(|a| number(&a["$tuple"][0])).collect()
}

/// The scene of the source system with the recorded ship at rest by the gate after a warp to it.
fn by_the_gate(r: &Recorded) -> (Scene, i64) {
    let mut scene = Scene::new(Arc::clone(&r.source));
    scene.undock(SESSION, r.spec.clone(), r.race["stationID"].as_i64().unwrap(), NOW).unwrap();
    let out = scene.command(SESSION, Command::WarpTo { target_id: GATE, range: 0.0 }, NOW);
    assert!(matches!(out.last(), Some(Out::Accepted { command: 8, .. })), "{out:?}");
    let mut t = NOW;
    while t < NOW + 200 * SECOND {
        t += SECOND / 4;
        scene.poll(t);
    }
    (scene, t)
}

#[test]
fn a_jump_is_told_as_the_reference_told_it_and_the_ship_leaves_when_the_reference_s_did() {
    let Some(r) = recorded() else { return };
    let (mut scene, t) = by_the_gate(&r);
    let out = scene.command(SESSION, Command::Jump { gate_id: GATE, destination_gate: ARRIVAL_GATE }, t);
    assert_eq!(out.len(), 2, "the update, and the word that the ship is going: {out:?}");
    assert_eq!(
        out[1],
        Out::Jumping {
            session: SESSION,
            command: 9,
            destination_system: DESTINATION,
            destination_gate: ARRIVAL_GATE
        }
    );

    // the effect and the stop in one update, the same arguments to the last
    let jump = step(&r.race, "jump");
    let first = events_named(jump, "DoDestinyUpdate")[0];
    let theirs = event_args(first);
    let ours = out_args(&out[0]);
    assert_eq!(without_stamps(&ours), without_stamps(&theirs));
    assert_eq!(stamps_of(&ours)[0], stamps_of(&ours)[1]);
    assert_eq!(theirs["$tuple"][1], ours["$tuple"][1], "the client does not wait");

    // the ship is in the other system when the reference's was, within a second (its time is the later of four seconds and three past the
    // effect's stamp, which is one or two seconds ahead of its clock)
    let recorded_change = events_named(jump, "$sessionchange")[0]["at"].as_f64().unwrap();
    let ours_at = 4.0;
    assert!((recorded_change - ours_at).abs() < 1.0, "recorded {recorded_change} s, ours {ours_at} s");
    assert!(scene.due_jumps(t + 4 * SECOND - 1).0.is_empty());
    assert_eq!(scene.due_jumps(t + 4 * SECOND).0.len(), 1);
}

#[test]
fn the_ship_that_has_jumped_is_bound_in_the_other_system_as_the_reference_bound_it() {
    let Some(r) = recorded() else { return };
    let (mut scene, t) = by_the_gate(&r);
    scene.command(SESSION, Command::Jump { gate_id: GATE, destination_gate: ARRIVAL_GATE }, t);
    let traveller = scene.due_jumps(t + 4 * SECOND).0.pop().unwrap();
    let mut there = Scene::new(Arc::clone(&r.destination));
    let arrived = t + 5 * SECOND;
    there.arrive(traveller, arrived).unwrap();
    let all_out = there.bind(SESSION, arrived + SECOND);
    let out: Vec<Out> = all_out
        .into_iter()
        .filter(|message| matches!(message, Out::Notify { session: SESSION, .. }))
        .collect();

    let bind = step(&r.race, "bind the ballpark of the next system");
    let theirs: Vec<&Value> = bind["events"].as_array().unwrap().iter().collect();
    assert_eq!(theirs.len(), 4);
    assert_eq!(out.len(), 4);
    let name = |o: &Out| match o {
        Out::Notify { name, .. } => name.to_string(),
        other => panic!("{other:?}"),
    };
    assert_eq!(
        out.iter().map(name).collect::<Vec<_>>(),
        [0, 1, 2, 3].map(|index| theirs[index]["name"].as_str().unwrap().to_string())
    );
    // The client gets the reference's clock and rebase, then the complete state and ship numbers.
    assert_eq!(out_args(&out[0]), event_args(theirs[0]));
    // Michelle applies newSimTime - oldSimTime (michelle.py:699-703); absolute filetimes belong to the capture.
    let rebase = out_args(&out[1]);
    let recorded_rebase = event_args(theirs[1]);
    assert_eq!(shape(&rebase), shape(&recorded_rebase));
    let rebase_delta = |args: &Value| {
        let times = args["$tuple"][0]["$tuple"].as_array().expect("clock rebase time pair");
        number(&times[1]) - number(&times[0])
    };
    assert_eq!(rebase_delta(&rebase), rebase_delta(&recorded_rebase));

    // The SetState's leading actions and wait flag are stable; the capture also includes numbers for ambient NPCs.
    let state = out_args(&out[2]);
    let recorded_state = event_args(theirs[2]);
    let ours_initial_actions = without_stamps(&state);
    let reference_initial_actions = without_stamps(&recorded_state);
    assert_eq!(&action_names_of(&state)[..2], &action_names_of(&recorded_state)[..2]);
    assert_eq!(ours_initial_actions[1], reference_initial_actions[1], "JumpIn wire args");
    assert_eq!(state["$tuple"][1], recorded_state["$tuple"][1], "wait of SetState");

    // The next same-stamp update releases the waiting SetState on the client.
    let (state_stamp, numbers_stamp) = (stamps_of(&state)[0], stamps_of(&out_args(&out[3]))[0]);
    assert_eq!(numbers_stamp, state_stamp);
    let numbers = out_args(&out[3]);
    assert_eq!(numbers["$tuple"][1], Value::Bool(false));

    // Compare the traveller's own numbers; the capture also contains independently running ambient NPCs.
    let ship_id = r.spec.ship_id;
    let ours = numbers;
    assert_eq!(stamps_of(&ours), vec![numbers_stamp; stamps_of(&ours).len()]);
    let is_own_number = |action: &Value| {
        matches!(action["$tuple"][0].as_str(), Some("SetBallAgility" | "SetBallMass" | "SetMaxSpeed"))
            && number(&action["$tuple"][1]["$tuple"][0]) == ship_id
    };
    let recorded: Vec<Value> = reference_initial_actions.into_iter().filter(is_own_number).collect();
    let ours: Vec<Value> = without_stamps(&ours).into_iter().filter(is_own_number).collect();
    assert_eq!(ours, recorded);
}
