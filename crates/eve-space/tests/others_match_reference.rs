//! Two ships in one system, as the reference showed them to each other (`oracle/fixtures/scenarios/others.json`): what the one that is in space is
//! sent when the other undocks, binds, is given orders, warps off and docks, and what the newcomer is sent of the one that was there.
mod support;

use std::collections::{BTreeMap, BTreeSet};
use std::sync::Arc;

use eve_destiny::Vec3;
use eve_space::scene::{Command, Out, Scene};
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

fn recording() -> Recording {
    let world = World::load(reference_sde()).expect("the static data loads");
    let race = fixture("others.json")["races"][0].clone();

    // A system bind now sends `SetState`, whose free-ball stream contains the pilot's ball;
    // build the initial ship from the preceding undock's `PackagedAction` instead.
    let watcher_race = recorded_undock(&race, "watcher");
    let (_, _, watcher_ball) = initial_ship_addition(&watcher_race);
    let mut watcher = recorded_ship(&watcher_race, &watcher_ball, recorded_direction(&watcher_race)).spec;
    watcher.warp_speed_au = 3.0;

    // the actor's ship: what the watcher was sent of it when it undocked
    let actor_race = recorded_undock(&race, "actor");
    let (_, _, actor_ball) = initial_ship_addition(&actor_race);
    let mut actor = recorded_ship(&actor_race, &actor_ball, Vec3::new(1.0, 0.0, 0.0)).spec;
    actor.warp_speed_au = 3.0;

    Recording { race, watcher, actor, world: Arc::new(world.system(SYSTEM).unwrap()) }
}

fn recorded_undock(race: &Value, pilot: &str) -> Value {
    let mut individual = race.clone();
    individual["shipID"] = race[pilot]["shipID"].clone();
    let undock = step(race, &format!("the {pilot} undocks"));
    individual["steps"] = json!([
        {"label": "bind the ballpark", "events": []},
        {"label": "undock", "events": undock["seen"]},
    ]);
    individual
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

fn absent_affiliations(items: &mut Value) {
    for item in items.as_array_mut().unwrap() {
        let fields = item["$tuple"][0]["$dict"].as_array_mut().unwrap();
        for field in fields {
            let pair = field.as_array_mut().unwrap();
            if matches!(pair[0].as_str(), Some("allianceID" | "warFactionID")) && pair[1] == 0 {
                pair[1] = Value::Null;
            }
        }
    }
}

fn destination_of_warp(action: &Value) -> Vec3 {
    let values = action["$tuple"][1]["$tuple"].as_array().unwrap();
    Vec3::new(values[1].as_f64().unwrap(), values[2].as_f64().unwrap(), values[3].as_f64().unwrap())
}

fn distance(left: Vec3, right: Vec3) -> f64 {
    (left.x - right.x).hypot(left.y - right.y).hypot(left.z - right.z)
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
    let actions = args["$tuple"][0]
        .as_array()
        .unwrap_or_else(|| panic!("expected Destiny actions, got {:?}", args["$tuple"][0]));
    actions.iter().map(|a| a["$tuple"][1].clone()).collect()
}

fn stamps_of(args: &Value) -> Vec<i64> {
    args["$tuple"][0].as_array().unwrap().iter().map(|a| number(&a["$tuple"][0])).collect()
}

fn state_fields(args: &Value) -> &Value {
    &args["$tuple"][0][0]["$tuple"][1]["$tuple"][1]["$tuple"][0]["args"]["$dict"]
}

fn packaged_actions(args: &Value) -> Value {
    let package_args = &args["$tuple"][0][0]["$tuple"][1]["$tuple"][1];
    if let Some(marshalled) = package_args.get("$marshalled") {
        return marshalled.clone();
    }
    let bytes = unhex(package_args["$buffer"].as_str().expect("PackagedAction contains a buffer"));
    support::describe(&eve_marshal::decode_exact(&bytes).expect("PackagedAction buffer decodes"))
}

fn packaged_add_args(args: &Value) -> Value {
    let package = packaged_actions(args);
    let update = &package["$tuple"][0];
    assert_eq!(update["$tuple"][1]["$tuple"][0], "AddBalls2");
    update["$tuple"][1]["$tuple"][1].clone()
}

fn own_ball(buffer: &[u8], ship_id: i64) -> &[u8] {
    let offset = buffer
        .windows(8)
        .position(|bytes| bytes == ship_id.to_le_bytes())
        .expect("SetState contains its ego ball");
    &buffer[offset..]
}

fn slim_fields(slim: &Value) -> &Value {
    &slim["args"]["$dict"]
}

fn state_slim(slims: &Value, ship_id: i64) -> Option<Value> {
    slims.as_array()?.iter().find(|slim| number(&field(slim_fields(slim), "itemID")) == ship_id).cloned()
}

fn normalized_slim(mut slim: Value) -> Value {
    blank_times(&mut slim);
    if let Some(fields) = slim["args"]["$dict"].as_array_mut() {
        for pair in fields {
            if matches!(pair[0].as_str(), Some("allianceID" | "warFactionID")) && pair[1] == 0 {
                pair[1] = Value::Null;
            }
        }
    }
    slim
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
    let r = recording();
    let mut scene = scene_with_both(&r);
    let station = r.race["stationID"].as_i64().unwrap();
    let told = scene.undock(ACTOR, r.actor.clone(), station, T0 + 8 * SECOND).unwrap();
    let undock = step(&r.race, "the actor undocks");
    let recorded_updates = destiny_events(undock, "seen");
    assert_eq!(
        told.len(),
        recorded_updates.len(),
        "the watcher receives the AddBalls and movement-prime updates"
    );
    assert_eq!(told.len(), 2);

    for (ours, theirs) in told.iter().zip(&recorded_updates) {
        let (ours, theirs) = (out_args(ours), event_args(theirs));
        assert_eq!(action_names_of(&ours), action_names_of(&theirs));
        assert_eq!(theirs["$tuple"][1], ours["$tuple"][1], "the client does not wait");
    }

    let recorded = &recorded_updates[0];
    let ours = out_args(&told[0]);
    let theirs = event_args(recorded);
    // One stamp covers the packaged AddBalls action.
    let stamps = stamps_of(&ours);
    assert!(stamps.iter().all(|s| *s == stamps[0]), "{stamps:?}");

    // the slim item and the damage state (but the time), and the ball but for where the ship is and how it is going
    let (mut mine, mut recorded_args) = (ours.clone(), theirs.clone());
    blank_times(&mut mine);
    blank_times(&mut recorded_args);
    let added = |v: &Value| packaged_add_args(v)["$tuple"][0]["$tuple"][1].clone();
    let (mut mine_added, mut expected_added) = (added(&mine), added(&recorded_args));
    blank_times(&mut mine_added);
    blank_times(&mut expected_added);
    absent_affiliations(&mut expected_added);
    assert_eq!(mine_added, expected_added, "the slim item and the damage state");
    let ball =
        |v: &Value| packaged_add_args(v)["$tuple"][0]["$tuple"][0]["$buffer"].as_str().unwrap().to_string();
    let (ours_ball, theirs_ball) = (unhex(&ball(&ours)), unhex(&ball(&theirs)));
    assert_eq!(ours_ball.len(), theirs_ball.len());
    // header (kind, stamp), the ball's id, mode and radius, then the place (24 bytes) and the flags, mass and cloak up to the velocity (24 bytes)
    assert_eq!(&ours_ball[5..18], &theirs_ball[5..18], "the id, the mode and the radius");
    assert_eq!(&ours_ball[42..72], &theirs_ball[42..72], "the flags and the mass");
    // the numbers that follow the packaged ball
    assert_eq!(without_stamps(&ours)[1..], without_stamps(&theirs)[1..]);
    assert_eq!(
        without_stamps(&out_args(&told[1])),
        without_stamps(&event_args(&recorded_updates[1])),
        "the movement prime is a separate update"
    );
}

#[test]
fn the_actor_that_binds_is_given_both_ships_and_the_numbers_of_both_as_the_reference_gave_them() {
    let r = recording();
    let mut scene = scene_with_both(&r);
    let station = r.race["stationID"].as_i64().unwrap();
    scene.undock(ACTOR, r.actor.clone(), station, T0 + 8 * SECOND).unwrap();
    let out = scene.bind(ACTOR, T0 + 9 * SECOND);

    let bind = step(&r.race, "the actor binds the ballpark");
    let recorded = destiny_events(bind, "events");
    let reference_events = bind["events"].as_array().unwrap();
    let actor_updates: Vec<&Out> =
        out.iter().filter(|message| matches!(message, Out::Notify { session: ACTOR, .. })).collect();
    assert_eq!(actor_updates.len(), reference_events.len());
    assert_eq!(actor_updates.len(), 3, "clock, rebase, and one SetState update");
    assert!(matches!(actor_updates[0], Out::Notify { name: "OnSetTimeDilation", .. }));
    assert!(matches!(actor_updates[1], Out::Notify { name: "DoSimClockRebase", .. }));
    assert_eq!(names(actor_updates[2]), recorded_names(&recorded[0]));

    // Michelle.SetState clears the old ballpark and rebuilds it from its state buffer and slims.
    // Compare the system bodies and the other pilot; the actor ego is checked in the binary ball below.
    let (ours, theirs) = (out_args(actor_updates[2]), event_args(&recorded[0]));
    let (ours_state, theirs_state) = (state_fields(&ours), state_fields(&theirs));
    let actor_id = number(&r.race["actor"]["shipID"]);
    assert_eq!(number(&field(ours_state, "ego")), actor_id);
    assert_eq!(number(&field(theirs_state, "ego")), actor_id);
    assert_eq!(theirs["$tuple"][1], ours["$tuple"][1], "SetState wait flag");

    let actual_slims = field(ours_state, "slims");
    let reference_slims = field(theirs_state, "slims");
    let watcher_id = number(&r.race["watcher"]["shipID"]);
    let actual_watcher = state_slim(&actual_slims, watcher_id).expect("SetState contains the watcher ship");
    let reference_watcher =
        state_slim(&reference_slims, watcher_id).expect("EveJS SetState contains the watcher ship");
    assert_eq!(normalized_slim(actual_watcher), normalized_slim(reference_watcher), "watcher ship slim");
    let actual_actor = state_slim(&actual_slims, actor_id).expect("SetState contains the ego ship slim");
    let reference_actor =
        state_slim(&reference_slims, actor_id).expect("EveJS SetState contains the ego ship slim");
    for key in ["itemID", "typeID", "groupID", "categoryID", "ownerID", "charID", "name", "corpID", "modules"]
    {
        if reference_actor["args"]["$dict"]
            .as_array()
            .is_some_and(|fields| fields.iter().any(|pair| pair[0] == key))
        {
            assert_eq!(
                field(slim_fields(&actual_actor), key),
                field(slim_fields(&reference_actor), key),
                "ego slim {key}"
            );
        }
    }

    let static_ids: BTreeSet<i64> = actual_slims
        .as_array()
        .unwrap()
        .iter()
        .filter_map(|slim| {
            let fields = slim_fields(slim);
            (number(&field(fields, "itemID")) != actor_id
                && number(&field(fields, "itemID")) != watcher_id
                && number(&field(fields, "groupID")) != 99)
                .then(|| number(&field(fields, "itemID")))
        })
        .collect();
    let expected_static_ids: BTreeSet<i64> = r.world.bodies.iter().map(|body| body.id).collect();
    assert_eq!(static_ids, expected_static_ids, "SetState carries every SDE body exactly once");

    let sentry_counts = actual_slims
        .as_array()
        .unwrap()
        .iter()
        .filter(|slim| number(&field(slim_fields(slim), "groupID")) == 99)
        .fold(BTreeMap::<i64, usize>::new(), |mut counts, slim| {
            let fields = slim_fields(slim);
            assert_eq!(number(&field(fields, "categoryID")), 11, "group-99 sentry category");
            *counts.entry(number(&field(fields, "typeID"))).or_default() += 1;
            counts
        });
    let reference_sentry_counts = reference_slims
        .as_array()
        .unwrap()
        .iter()
        .filter_map(|slim| {
            let fields = slim_fields(slim);
            (number(&field(fields, "groupID")) == 99).then(|| number(&field(fields, "typeID")))
        })
        .fold(BTreeMap::<i64, usize>::new(), |mut counts, type_id| {
            *counts.entry(type_id).or_default() += 1;
            counts
        });
    assert_eq!(
        sentry_counts, reference_sentry_counts,
        "visible active sentries in this recorded system view"
    );
    let sde = reference_sde();
    let groups = sde.table("groups").expect("SDE groups load");
    let types_sde = sde.table("types").expect("SDE types load");
    assert_eq!(groups[&99]["categoryID"], 11);
    for type_id in sentry_counts.keys() {
        assert_eq!(types_sde[type_id]["groupID"], 99, "type {type_id} is an SDE sentry");
    }

    let state_buffer = |fields: &Value| unhex(field(fields, "state")["$buffer"].as_str().unwrap());
    let (ours_buffer, recorded_buffer) = (state_buffer(ours_state), state_buffer(theirs_state));
    let (ours_ball, recorded_ball) = (own_ball(&ours_buffer, actor_id), own_ball(&recorded_buffer, actor_id));
    assert_eq!(&ours_ball[9..13], &recorded_ball[9..13], "ego radius");
    assert_eq!(&ours_ball[38..46], &recorded_ball[38..46], "ego mass");
    assert_eq!(&ours_ball[63..67], &recorded_ball[63..67], "ego maximum speed");
    assert_eq!(&ours_ball[91..95], &recorded_ball[91..95], "ego agility");
}

#[test]
fn the_watcher_is_told_of_each_order_as_the_reference_told_it() {
    let r = recording();
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
    let r = recording();
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
    assert_eq!(seen.len(), 1, "EveJS records the docking stop; V24 removes the departed ship separately");

    let told: Vec<&Out> = out.iter().filter(|o| matches!(o, Out::Notify { session: WATCHER, .. })).collect();
    assert_eq!(told.len(), 1);
    assert_eq!(action_names_of(&out_args(told[0])), recorded_names(&seen[0]));
    assert_eq!(without_stamps(&out_args(told[0])), without_stamps(&event_args(&seen[0])));

    let ready = scene.poll(at + 4 * SECOND);
    assert!(ready.iter().any(|out| matches!(
        out,
        Out::Accepted { session: ACTOR, command: eve_space::scene::DOCKING_READY_COMMAND, at: ready_station }
            if *ready_station == station
    )));
    let gone = scene.command(
        ACTOR,
        Command::DockingDecision { station_id: station, allowed: true },
        at + 4 * SECOND,
    );
    let removal: Vec<&Out> =
        gone.iter().filter(|o| matches!(o, Out::Notify { session: WATCHER, .. })).collect();
    assert_eq!(removal.len(), 1);
    let removal_actions = without_stamps(&out_args(removal[0]));
    assert_eq!(action_names_of(&out_args(removal[0])), ["RemoveBalls"]);
    assert_eq!(
        number(&removal_actions[0]["$tuple"][1]["$tuple"][0][0]),
        number(&r.race["actor"]["shipID"]),
        "the V24 ballpark must remove the docked actor's ship"
    );
    assert!(scene.ship(ACTOR, at + 4 * SECOND).is_none(), "the docked pilot left authoritative space state");
}

#[test]
fn a_warp_is_shown_to_the_watcher_and_the_ship_is_taken_from_view_when_it_has_gone_far_enough() {
    let r = recording();
    let mut scene = scene_with_both(&r);
    let station = r.race["stationID"].as_i64().unwrap();
    scene.undock(ACTOR, r.actor.clone(), station, T0 + 8 * SECOND).unwrap();
    scene.bind(ACTOR, T0 + 9 * SECOND);
    let planet = r.race["planetID"].as_i64().unwrap();
    let recorded = step(&r.race, "the actor warps to a planet");
    let seen = destiny_events(recorded, "seen");
    assert_eq!(seen.len(), 2, "the recorded warp entry and removal");

    let at = T0 + 12 * SECOND;
    let out = scene.command(ACTOR, Command::WarpTo { target_id: planet, range: 0.0 }, at);
    let warp_order: Vec<&Out> =
        out.iter().filter(|o| matches!(o, Out::Notify { session: WATCHER, .. })).collect();
    assert_eq!(warp_order.len(), 1);
    assert_eq!(action_names_of(&out_args(warp_order[0])), recorded_names(&seen[0]));

    let actor_events = destiny_events(recorded, "events");
    let reference_dropout = actor_events
        .iter()
        .find(|event| recorded_names(event).iter().any(|name| name == "SetBallMassive"))
        .unwrap()["at"]
        .as_f64()
        .unwrap();
    let mut removed = None;
    let mut dropout = None;
    let mut exited = None;
    let mut actor_updates = Vec::new();
    let mut t = at;
    while t < at + 120 * SECOND && (removed.is_none() || dropout.is_none() || exited.is_none()) {
        t += SECOND / 4;
        for out in scene.poll(t) {
            let elapsed = (t - at) as f64 / SECOND as f64;
            if matches!(out, Out::WarpExited { session: ACTOR }) {
                exited = Some(elapsed);
                continue;
            }
            let Out::Notify { session, .. } = &out else { continue };
            let args = out_args(&out);
            let actions = action_names_of(&args);
            if *session == ACTOR {
                if actions.contains(&"SetBallMassive".to_string()) && dropout.is_none() {
                    dropout = Some(elapsed);
                }
                actor_updates.push(args.clone());
            }
            if *session == WATCHER
                && let Some("PackagedAction") = actions.first().map(String::as_str)
            {
                removed = Some((args, elapsed));
            }
        }
    }
    let warp = out_args(warp_order[0]);
    assert_eq!(action_names_of(&warp), recorded_names(&seen[0]));
    // Planet warp-in points have a per-warp offset inside the client's 2,500 m jitter radius.
    let warp_to = |args: &Value| args["$tuple"][0][2]["$tuple"][1].clone();
    let (actual_warp, recorded_warp) = (warp_to(&warp), warp_to(&event_args(&seen[0])));
    let actual_values = actual_warp["$tuple"][1]["$tuple"].as_array().unwrap();
    let recorded_values = recorded_warp["$tuple"][1]["$tuple"].as_array().unwrap();
    assert_eq!(actual_values[4], recorded_values[4], "warp stop distance");
    assert_eq!(actual_values[5], recorded_values[5], "warp factor");
    let planet_point = eve_space::warp::warp_in_point(r.world.body(planet).unwrap()).unwrap();
    assert!(distance(destination_of_warp(&actual_warp), planet_point) <= 2_500.0);
    assert!(distance(destination_of_warp(&recorded_warp), planet_point) <= 2_500.0);
    let recorded_entry = seen[0]["at"].as_f64().unwrap();
    assert!(
        recorded_entry < 2.0,
        "the EveJS action is broadcast at its next Destiny tick, {recorded_entry} s after the order"
    );

    let (gone, removed_after) = removed.expect("the watcher is told the ship has gone");
    assert_eq!(action_names_of(&gone), recorded_names(&seen[1]));
    let recorded_removal = seen[1]["at"].as_f64().unwrap();
    assert!(
        (removed_after - recorded_removal).abs() < 2.5,
        "removed after {removed_after} s, recorded {recorded_removal} s"
    );

    let dropout = dropout.expect("the pilot gets the massless warp-exit warning");
    let exited = exited.expect("the pilot exits warp");
    // Rust sends SetBallMassive two seconds before warp exit; EveJS records it at the drop.
    assert!((exited - dropout - 2.0).abs() <= 0.3, "warning {dropout} s, exit {exited} s");
    // The live walk uses this three-second tolerance for Destiny timing comparisons.
    assert!(
        (exited - reference_dropout).abs() <= 3.0,
        "exited after {exited} s, EveJS recorded {reference_dropout} s"
    );
    let massless: Vec<&Value> = actor_updates
        .iter()
        .filter(|args| action_names_of(args).contains(&"SetBallMassive".to_string()))
        .collect();
    assert_eq!(massless.len(), 2, "the two massless warnings bracket warp exit");
    let stamp = |args: &Value| args["$tuple"][0][0]["$tuple"][0].as_i64().unwrap();
    assert_eq!(stamp(massless[1]) - stamp(massless[0]), 2, "warning stamp separation");
}
