//! Two ships in one system, as the reference showed them to each other (`oracle/fixtures/scenarios/others.json`): what the one that is in space is
//! sent when the other undocks, binds, is given orders, warps off and docks, and what the newcomer is sent of the one that was there.

mod support;

use std::sync::Arc;

use eve_destiny::Vec3;
use eve_space::scene::{Command, Out, Scene};
use eve_space::sde::Sde;
use eve_space::ship::ShipSpec;
use eve_space::world::World;
use serde_json::{Value, json};
use support::*;

const SYSTEM: i32 = 30100032;
const WATCHER: u64 = 1;
const ACTOR: u64 = 2;
const SECOND: i64 = 10_000_000;
const T0: i64 = 134351527024310000;

struct Recording {
    race: Value,
    watcher: ShipSpec,
    actor: ShipSpec,
    world: Arc<eve_space::world::SolarSystem>,
}

/// A race of the recording seen as a plain one: the step with this label under the name the helpers look for.
fn as_plain(race: &Value, label: &str, events: Value) -> Value {
    let _ = race;
    json!({"steps": [{"label": "bind the ballpark", "events": events, "from": label}]})
}

fn recording() -> Option<Recording> {
    let Some(sde) = Sde::locate() else {
        eprintln!("no static data found (EVE_SDE_DIR or .cache/sde): skipped");
        return None;
    };
    let world = World::load(sde).expect("the static data loads");
    let race = fixture("others.json")["races"][0].clone();

    // the watcher's ship: what it was sent when it bound the ballpark
    let bind = step(&race, "the watcher binds the ballpark");
    let plain = as_plain(&race, "the watcher binds the ballpark", bind["seen"].clone());
    let added = destiny_actions(&json!({"events": bind["seen"]}))
        .into_iter()
        .find(|(_, name, _)| name == "AddBalls2")
        .unwrap()
        .2;
    let ball = unhex(added["$tuple"][0]["$tuple"][0]["$buffer"].as_str().unwrap());
    let mut watcher = recorded_ship(&plain, &ball[5..], recorded_direction(&plain)).spec;
    watcher.warp_speed_au = 3.0;

    // the actor's ship: what the watcher was sent of it when it undocked
    let undock = step(&race, "the actor undocks");
    let plain = as_plain(&race, "the actor undocks", undock["seen"].clone());
    let added = destiny_actions(&json!({"events": undock["seen"]}))
        .into_iter()
        .find(|(_, name, _)| name == "AddBalls2")
        .unwrap()
        .2;
    let ball = unhex(added["$tuple"][0]["$tuple"][0]["$buffer"].as_str().unwrap());
    let mut actor = recorded_ship(&plain, &ball[5..], Vec3::new(1.0, 0.0, 0.0)).spec;
    actor.warp_speed_au = 3.0;

    Some(Recording { race, watcher, actor, world: Arc::new(world.system(SYSTEM).unwrap()) })
}

fn blank_times(value: &mut Value) {
    match value {
        Value::Object(map) => {
            if let Some(long) = map.get("$long").and_then(Value::as_i64)
                && long > 100_000_000_000_000_000
            {
                map.insert("$long".to_string(), json!(0));
            }
            map.values_mut().for_each(blank_times);
        }
        Value::Array(items) => items.iter_mut().for_each(blank_times),
        _ => {}
    }
}

fn names(out: &Out) -> Vec<String> {
    let Out::Notify { .. } = out else { return Vec::new() };
    action_names_of(&out_args(out))
}

fn recorded_names(event: &Value) -> Vec<String> {
    action_names_of(&event_args(event))
}

fn destiny_events(step: &Value, who: &str) -> Vec<Value> {
    step[who].as_array().unwrap().iter().filter(|e| e["name"] == "DoDestinyUpdate").cloned().collect()
}

/// The recording's actions of an update with the stamps taken off.
fn without_stamps(args: &Value) -> Vec<Value> {
    args["$tuple"][0].as_array().unwrap().iter().map(|a| a["$tuple"][1].clone()).collect()
}

fn stamps_of(args: &Value) -> Vec<i64> {
    args["$tuple"][0].as_array().unwrap().iter().map(|a| number(&a["$tuple"][0])).collect()
}

/// The scene with the watcher in space and having bound the ballpark, and the actor in space too, not yet bound.
fn scene_with_both(r: &Recording) -> Scene {
    let mut scene = Scene::new(Arc::clone(&r.world));
    let station = r.race["stationID"].as_i64().unwrap();
    scene.undock(WATCHER, r.watcher.clone(), station, T0).unwrap();
    scene.bind(WATCHER, T0 + 2 * SECOND);
    scene
}

#[test]
fn the_watcher_is_shown_the_actor_that_undocks_as_the_reference_showed_it() {
    let Some(r) = recording() else { return };
    let mut scene = scene_with_both(&r);
    let station = r.race["stationID"].as_i64().unwrap();
    let told = scene.undock(ACTOR, r.actor.clone(), station, T0 + 8 * SECOND).unwrap();
    assert_eq!(told.len(), 1, "one update, to the watcher");

    let undock = step(&r.race, "the actor undocks");
    let recorded = &destiny_events(undock, "seen")[0];
    let (ours, theirs) = (out_args(&told[0]), event_args(recorded));
    assert_eq!(action_names_of(&ours), recorded_names(recorded));
    // one stamp for the four actions, as recorded
    let stamps = stamps_of(&ours);
    assert!(stamps.iter().all(|s| *s == stamps[0]), "{stamps:?}");
    assert_eq!(theirs["$tuple"][1], ours["$tuple"][1], "the client does not wait");

    // the slim item and the damage state (but the time), and the ball but for where the ship is and how it is going
    let (mut mine, mut recorded_args) = (ours.clone(), theirs.clone());
    blank_times(&mut mine);
    blank_times(&mut recorded_args);
    let added = |v: &Value| v["$tuple"][0][0]["$tuple"][1]["$tuple"][1]["$tuple"][0]["$tuple"][1].clone();
    assert_eq!(added(&mine), added(&recorded_args), "the slim item and the damage state");
    let ball = |v: &Value| {
        v["$tuple"][0][0]["$tuple"][1]["$tuple"][1]["$tuple"][0]["$tuple"][0]["$buffer"]
            .as_str()
            .unwrap()
            .to_string()
    };
    let (ours_ball, theirs_ball) = (unhex(&ball(&ours)), unhex(&ball(&theirs)));
    assert_eq!(ours_ball.len(), theirs_ball.len());
    // header (kind, stamp), the ball's id, mode and radius, then the place (24 bytes) and the flags, mass and cloak up to the velocity (24 bytes)
    assert_eq!(&ours_ball[5..18], &theirs_ball[5..18], "the id, the mode and the radius");
    assert_eq!(&ours_ball[42..72], &theirs_ball[42..72], "the flags and the mass");
    // the numbers that follow the ball
    assert_eq!(without_stamps(&ours)[1..], without_stamps(&theirs)[1..]);
}

#[test]
fn the_actor_that_binds_is_given_both_ships_and_the_numbers_of_both_as_the_reference_gave_them() {
    let Some(r) = recording() else { return };
    let mut scene = scene_with_both(&r);
    let station = r.race["stationID"].as_i64().unwrap();
    scene.undock(ACTOR, r.actor.clone(), station, T0 + 8 * SECOND).unwrap();
    let out = scene.bind(ACTOR, T0 + 9 * SECOND);

    let bind = step(&r.race, "the actor binds the ballpark");
    let recorded = destiny_events(bind, "events");
    assert_eq!(out.len() - 2, recorded.len() - 1, "what the reference sent, without its props");
    let notification_names: Vec<&str> = out
        .iter()
        .map(|o| match o {
            Out::Notify { name, .. } => *name,
            other => panic!("{other:?}"),
        })
        .collect();
    assert_eq!(&notification_names[..2], ["OnSetTimeDilation", "DoSimClockRebase"]);
    for (index, expected) in recorded.iter().take(4).enumerate() {
        assert_eq!(names(&out[index + 2]), recorded_names(expected), "update {index}");
    }

    // the ships of the first update, watcher then actor, with their slim items
    let (ours, theirs) = (out_args(&out[2]), event_args(&recorded[0]));
    let slims = |v: &Value| v["$tuple"][0][0]["$tuple"][1]["$tuple"][1]["$tuple"][0]["$tuple"][1].clone();
    let (mut mine, mut recorded_slims) = (slims(&ours), slims(&theirs));
    blank_times(&mut mine);
    blank_times(&mut recorded_slims);
    assert_eq!(mine, recorded_slims);
    assert_eq!(theirs["$tuple"][1], ours["$tuple"][1], "wait");

    // the numbers: the watcher's three, then the actor's own two (the mass came with its start)
    let numbers = |args: &Value| without_stamps(args);
    assert_eq!(numbers(&out_args(&out[5])), numbers(&event_args(&recorded[3])));
    assert_eq!(stamps_of(&out_args(&out[5])).len(), 5);
}

#[test]
fn the_watcher_is_told_of_each_order_as_the_reference_told_it() {
    let Some(r) = recording() else { return };
    let mut scene = scene_with_both(&r);
    let station = r.race["stationID"].as_i64().unwrap();
    scene.undock(ACTOR, r.actor.clone(), station, T0 + 8 * SECOND).unwrap();
    scene.bind(ACTOR, T0 + 9 * SECOND);
    let orders = [
        ("the actor goes in a direction", Command::GotoDirection(Vec3::new(1.0, 0.0, 0.0))),
        ("the actor slows down", Command::SetSpeedFraction(0.5)),
        ("the actor orbits the station", Command::Orbit { target_id: station, distance: 5000.0 }),
        ("the actor stops", Command::Stop),
    ];
    let mut t = T0 + 12 * SECOND;
    for (label, command) in orders {
        let out = scene.command(ACTOR, command, t);
        let recorded = &destiny_events(step(&r.race, label), "seen")[0];
        let expected_names = recorded_names(recorded);
        let told: Vec<&Out> =
            out.iter().filter(|o| matches!(o, Out::Notify { session: WATCHER, .. })).collect();
        let mut command_updates = Vec::new();
        let mut collision_updates = Vec::new();
        for update in &told {
            let args = out_args(update);
            if action_names_of(&args) == expected_names {
                command_updates.push(args);
            } else {
                collision_updates.push(args);
            }
        }
        assert_eq!(command_updates.len(), 1, "{label}: command output {out:?}");
        for collision in &collision_updates {
            let names = action_names_of(collision);
            assert_eq!(names.len(), 4, "{label}: {collision:?}");
            assert_eq!(&names[..3], ["SetBallPosition", "SetBallVelocity", "SetSpeedFraction"]);
            assert!(names[3] == "Stop" || names[3] == "GotoDirection", "{label}: {collision:?}");
        }
        if !collision_updates.is_empty() {
            let (actor_position, actor_radius) = {
                let actor = scene.ship(ACTOR, t).unwrap();
                (actor.position, actor.spec.radius)
            };
            let (watcher_position, watcher_radius) = {
                let watcher = scene.ship(WATCHER, t).unwrap();
                (watcher.position, watcher.spec.radius)
            };
            let delta = Vec3::new(
                actor_position.x - watcher_position.x,
                actor_position.y - watcher_position.y,
                actor_position.z - watcher_position.z,
            );
            let separation = delta.x.hypot(delta.y).hypot(delta.z);
            assert!(separation >= actor_radius + watcher_radius - 1e-6, "{label}: {separation}");
        }
        let ours = &command_updates[0];
        let theirs = event_args(recorded);
        assert_eq!(action_names_of(ours), expected_names, "{label}");
        assert_eq!(theirs["$tuple"][1], ours["$tuple"][1], "{label}: wait");
        if label != "the actor goes in a direction" {
            // the values are the same (a direction is worked out from where the recorded ship pointed)
            assert_eq!(without_stamps(ours), without_stamps(&theirs), "{label}");
        }
        t += 3 * SECOND;
    }
}

#[test]
fn a_ship_that_docks_is_shown_as_the_reference_showed_it_and_is_gone_when_it_has_docked() {
    let Some(r) = recording() else { return };
    let mut world = (*r.world).clone();
    // the recorded station is 70 km across: a ship that has just come out is within docking range
    let station = r.race["stationID"].as_i64().unwrap();
    assert!(world.body(station).unwrap().radius > 30_000.0);
    world.bodies.retain(|_| true);
    let mut scene = Scene::new(Arc::new(world));
    scene.undock(WATCHER, r.watcher.clone(), station, T0).unwrap();
    scene.bind(WATCHER, T0 + 2 * SECOND);
    scene.undock(ACTOR, r.actor.clone(), station, T0 + 8 * SECOND).unwrap();
    scene.bind(ACTOR, T0 + 9 * SECOND);
    let at = T0 + 10 * SECOND;
    let out = scene.command(ACTOR, Command::Dock { station_id: station }, at);
    let recorded = step(&r.race, "the actor docks");
    let seen = destiny_events(recorded, "seen");

    let told: Vec<&Out> = out.iter().filter(|o| matches!(o, Out::Notify { session: WATCHER, .. })).collect();
    assert_eq!(told.len(), 1);
    assert_eq!(action_names_of(&out_args(told[0])), recorded_names(&seen[0]));
    assert_eq!(without_stamps(&out_args(told[0])), without_stamps(&event_args(&seen[0])));

    let gone = scene.poll(at + 4 * SECOND);
    let removal: Vec<&Out> =
        gone.iter().filter(|o| matches!(o, Out::Notify { session: WATCHER, .. })).collect();
    assert_eq!(removal.len(), 1);
    assert_eq!(action_names_of(&out_args(removal[0])), recorded_names(&seen[1]));
    // it is the actor's ball that is removed, as an action of its own (not packaged)
    assert_eq!(without_stamps(&out_args(removal[0])), without_stamps(&event_args(&seen[1])));
    let seconds = seen[1]["at"].as_f64().unwrap();
    assert!((seconds - 4.0).abs() < 1.0, "recorded at {seconds} s");
}

#[test]
fn a_warp_is_shown_as_a_turn_then_the_warp_and_the_ship_is_taken_from_view_when_it_has_gone_far_enough() {
    let Some(r) = recording() else { return };
    let mut scene = scene_with_both(&r);
    let station = r.race["stationID"].as_i64().unwrap();
    scene.undock(ACTOR, r.actor.clone(), station, T0 + 8 * SECOND).unwrap();
    scene.bind(ACTOR, T0 + 9 * SECOND);
    let planet = r.race["planetID"].as_i64().unwrap();
    let recorded = step(&r.race, "the actor warps to a planet");
    let seen = destiny_events(recorded, "seen");
    assert_eq!(seen.len(), 3, "the turn, the warp, the removal");

    let at = T0 + 12 * SECOND;
    let out = scene.command(ACTOR, Command::WarpTo { target_id: planet, range: 0.0 }, at);
    let turn: Vec<&Out> = out.iter().filter(|o| matches!(o, Out::Notify { session: WATCHER, .. })).collect();
    assert_eq!(turn.len(), 1);
    assert_eq!(action_names_of(&out_args(turn[0])), recorded_names(&seen[0]));

    let mut entered = None;
    let mut removed = None;
    let mut t = at;
    while t < at + 60 * SECOND && (entered.is_none() || removed.is_none()) {
        t += SECOND / 4;
        for out in scene.poll(t) {
            let Out::Notify { session: WATCHER, .. } = out else { continue };
            match names(&out).first().map(String::as_str) {
                Some("OnSpecialFX") => entered = Some((out_args(&out), (t - at) as f64 / SECOND as f64)),
                Some("PackagedAction") => removed = Some((out_args(&out), (t - at) as f64 / SECOND as f64)),
                _ => {}
            }
        }
    }
    let (warp, entered_after) = entered.expect("the watcher is told the ship is in warp");
    assert_eq!(action_names_of(&warp), recorded_names(&seen[1]));
    // the same warp: the place it is aimed at and the factor
    let warp_to = |args: &Value| args["$tuple"][0][2]["$tuple"][1].clone();
    assert_eq!(warp_to(&warp), warp_to(&event_args(&seen[1])));
    let recorded_entry = seen[1]["at"].as_f64().unwrap();
    assert!(
        (entered_after - recorded_entry).abs() < 2.0,
        "entered after {entered_after} s, recorded {recorded_entry} s"
    );

    let (gone, removed_after) = removed.expect("the watcher is told the ship has gone");
    assert_eq!(action_names_of(&gone), recorded_names(&seen[2]));
    let recorded_removal = seen[2]["at"].as_f64().unwrap();
    assert!(
        (removed_after - recorded_removal).abs() < 2.5,
        "removed after {removed_after} s, recorded {recorded_removal} s"
    );
}
