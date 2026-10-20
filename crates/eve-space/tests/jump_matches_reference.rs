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
    let bind = step(&race, "bind the ballpark");
    let added = destiny_actions(bind).into_iter().find(|(_, name, _)| name == "AddBalls2").unwrap().2;
    let buffer = unhex(added["$tuple"][0]["$tuple"][0]["$buffer"].as_str().unwrap());
    let mut ship = recorded_ship(&race, &buffer[5..], recorded_direction(&race));
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

fn last_position(step: &Value) -> Vec3 {
    let (_, _, args) =
        destiny_actions(step).into_iter().rfind(|(_, name, _)| name == "SetBallPosition").unwrap();
    point(&args)
}

#[test]
fn the_recorded_ship_came_to_rest_by_the_gate_where_our_rule_lets_a_ship_come_to_rest() {
    let Some(r) = recorded() else { return };
    let gate = r.source.body(GATE).unwrap();
    let warp = step(&r.race, "warp to the stargate");
    let (_, _, aim) = destiny_actions(warp).into_iter().find(|(_, name, _)| name == "WarpTo").unwrap();
    let raw = point(&aim);
    let stop = aim["$tuple"][4].as_f64().unwrap();
    let rest = last_position(warp);

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
    let side = (rest.x - envelope.x) * from_gate.x
        + (rest.y - envelope.y) * from_gate.y
        + (rest.z - envelope.z) * from_gate.z;
    assert!(side >= -20.0, "on the far side of the sphere by {side} m");
    assert!(
        gate::surface_distance(gate, rest, r.spec.radius) <= gate::JUMP_RANGE,
        "within jump range of the gate"
    );
    // the stop distance is drawn from 1 to 2.5 km and the point warped to is about that far beyond where the ship ends (the ship drops out at a
    // speed of up to 100 m/s and coasts to rest, so it ends a few tens of metres further on)
    assert!((1.0..=2_500.0).contains(&stop), "{stop}");
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
    let appeared = last_position(step(&r.race, "bind the ballpark of the next system"));
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
    let out = there.bind(SESSION, arrived + SECOND);

    let bind = step(&r.race, "bind the ballpark of the next system");
    let theirs: Vec<&Value> = bind["events"].as_array().unwrap().iter().take(5).collect();
    assert_eq!(out.len(), 5);
    let name = |o: &Out| match o {
        Out::Notify { name, .. } => name.to_string(),
        other => panic!("{other:?}"),
    };
    assert_eq!(
        out.iter().map(name).collect::<Vec<_>>(),
        theirs.iter().map(|e| e["name"].as_str().unwrap().to_string()).collect::<Vec<_>>()
    );
    // the clock and the dilation are the reference's own numbers
    assert_eq!(out_args(&out[0]), event_args(theirs[0]));

    // the ship, then the state, then the numbers: the same actions on the same steps of the stamp, and the client is not made to wait
    let (first, second) = (stamps_of(&out_args(&out[2]))[0], stamps_of(&out_args(&out[3]))[0]);
    let recorded_first = stamps_of(&event_args(theirs[2]))[0];
    for (index, offset) in [(2, 0), (3, 1)] {
        let ours = out_args(&out[index]);
        let recorded = event_args(theirs[index]);
        assert_eq!(action_names_of(&ours), action_names_of(&recorded));
        assert_eq!(ours["$tuple"][1], recorded["$tuple"][1], "wait of event {index}");
        assert_eq!(stamps_of(&recorded)[0] - recorded_first, offset, "the recording's stamps");
    }
    assert_eq!(second - first, 1);

    // the ship's own numbers are the recorded ones (the reference sends those of its ambient ships too)
    let ship_id = r.spec.ship_id;
    let ours = out_args(&out[4]);
    assert_eq!(stamps_of(&ours), [second, second, second]);
    let recorded: Vec<Value> = without_stamps(&event_args(theirs[4]))
        .into_iter()
        .filter(|a| number(&a["$tuple"][1]["$tuple"][0]) == ship_id)
        .collect();
    assert_eq!(without_stamps(&ours), recorded);
}
