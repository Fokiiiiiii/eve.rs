//! A scene answers the client as the reference did in the recorded undock: what it says at the bind, to each command and to a docking.

mod support;

use std::sync::Arc;

use eve_destiny::Vec3;
use eve_space::scene::{Command, Out, Scene, stamp_at};
use eve_space::sde::Sde;
use eve_space::world::World;
use serde_json::{Value, json};
use support::*;

const SYSTEM: i32 = 30100032;
const SESSION: u64 = 1;
const SIM_TIME: i64 = 134351527024310000;

/// The FILETIME of the start of a stamp's second.
fn at_stamp(stamp: i64) -> i64 {
    (stamp + 11_644_473_600) * 10_000_000
}

struct Recorded {
    recording: Value,
}

impl Recorded {
    fn race(&self) -> &Value {
        &self.recording["races"][0]
    }
}

/// The scene the recording was made in, with the recorded ship in space at `now`.
fn scene_at(now: i64) -> Option<(Scene, Recorded)> {
    let Some(sde) = Sde::locate() else {
        eprintln!("no static data found (EVE_SDE_DIR or .cache/sde): skipped");
        return None;
    };
    let world = World::load(sde).expect("the static data loads");
    let recorded = Recorded { recording: fixture("undock.json") };
    let race = recorded.race().clone();
    let bind = step(&race, "bind the ballpark");
    let added = destiny_actions(bind).into_iter().find(|(_, name, _)| name == "AddBalls2").unwrap().2;
    let buffer = unhex(added["$tuple"][0]["$tuple"][0]["$buffer"].as_str().unwrap());
    let spec = recorded_ship(&race, &buffer[5..], recorded_direction(&race)).spec;
    let mut scene = Scene::new(Arc::new(world.system(SYSTEM).unwrap()));
    scene.undock(SESSION, spec, race["stationID"].as_i64().unwrap(), now).unwrap();
    Some((scene, recorded))
}

#[test]
fn binding_says_what_the_reference_said() {
    let Some((mut scene, recorded)) = scene_at(SIM_TIME) else { return };
    let out = scene.bind(SESSION, SIM_TIME);
    let bind = step(recorded.race(), "bind the ballpark");
    let events: Vec<&Value> = bind["events"].as_array().unwrap().iter().collect();
    assert_eq!(out.len(), 6, "what the reference sent, without the props it adds");

    // the clock and the dilation are exactly the reference's
    assert_eq!(out_args(&out[0]), event_args(events[0]));
    assert_eq!(out_args(&out[1]), event_args(events[1]));

    // the ship and its numbers: the same actions at the same stamps; the place it is put at is ours
    let added = event_args(events[2]);
    assert_eq!(shape(&out_args(&out[2])), shape(&added));
    assert_eq!(
        out_args(&out[2])["$tuple"][0][0]["$tuple"][0],
        added["$tuple"][0][0]["$tuple"][0],
        "the stamp"
    );
    let placed = event_args(events[3]);
    let mine = out_args(&out[3]);
    assert_eq!(action_names_of(&mine), action_names_of(&placed));
    assert_eq!(shape(&mine), shape(&placed));
    for (ours, theirs) in
        mine["$tuple"][0].as_array().unwrap().iter().zip(placed["$tuple"][0].as_array().unwrap())
    {
        let name = ours["$tuple"][1]["$tuple"][0].as_str().unwrap();
        assert_eq!(ours["$tuple"][0], theirs["$tuple"][0], "the stamp of {name}");
        if !["SetBallPosition", "SetBallVelocity", "GotoDirection"].contains(&name) {
            assert_eq!(ours, theirs, "{name}");
        }
    }

    // the state: the same fields at the same stamp
    let state = event_args(events[4]);
    let mine = out_args(&out[4]);
    let key_names = |args: &Value| -> Vec<String> {
        args["$tuple"][0][0]["$tuple"][1]["$tuple"][1]["$tuple"][0]["args"]["$dict"]
            .as_array()
            .unwrap()
            .iter()
            .map(|p| p[0].as_str().unwrap().to_string())
            .collect()
    };
    assert_eq!(key_names(&mine), key_names(&state));
    assert_eq!(
        mine["$tuple"][0][0]["$tuple"][0], state["$tuple"][0][0]["$tuple"][0],
        "the stamp of the state"
    );

    // the ship's numbers come exactly as the reference gave them
    assert_eq!(out_args(&out[5]), event_args(events[5]));
}

/// The recorded update a command was answered with and the command it must have been.
fn recorded_command(recorded: &Recorded, label: &str) -> (i64, Command, Value) {
    let step = step(recorded.race(), label);
    let (stamp, name, args) = destiny_actions(step).into_iter().next().unwrap();
    let a = args["$tuple"].as_array().unwrap();
    let real = |i: usize| a[i].as_f64().unwrap();
    let command = match name.as_str() {
        "SetSpeedFraction" => Command::SetSpeedFraction(real(1)),
        "GotoDirection" => Command::GotoDirection(Vec3::new(real(1), real(2), real(3))),
        "Stop" => Command::Stop,
        other => panic!("{other}"),
    };
    (stamp, command, event_args(events_named(step, "DoDestinyUpdate")[0]))
}

#[test]
fn each_command_is_answered_with_the_update_the_reference_sent() {
    let Some((mut scene, recorded)) = scene_at(SIM_TIME) else { return };
    scene.bind(SESSION, SIM_TIME);
    for label in ["speed fraction", "go in a direction", "go in another direction", "stop"] {
        let (stamp, command, expected) = recorded_command(&recorded, label);
        let out = scene.command(SESSION, command, at_stamp(stamp));
        assert_eq!(out.len(), 2, "{label}: the update and the word that the command was accepted");
        assert_eq!(out_args(&out[0]), expected, "{label}");
        let Out::Notify { name, id_type, .. } = &out[0] else { unreachable!() };
        assert_eq!((*name, *id_type), ("DoDestinyUpdate", "clientID"));
        assert_eq!(
            out[1],
            Out::Accepted { session: SESSION, command: command.code(), at: at_stamp(stamp) },
            "{label}"
        );
        assert_eq!(stamp_at(at_stamp(stamp)), stamp as u32);
    }
}

/// The actions of an update as `(name, arguments after the ship's id)`.
fn actions_without_the_ship(actions: &[Value]) -> Vec<(String, Vec<Value>)> {
    actions
        .iter()
        .map(|a| {
            let inner = &a["$tuple"][1]["$tuple"];
            let args = inner[1]["$tuple"].as_array().unwrap();
            (inner[0].as_str().unwrap().to_string(), args[1..].to_vec())
        })
        .collect()
}

#[test]
fn each_order_is_answered_with_the_update_the_reference_sent() {
    let Some((mut scene, _)) = scene_at(SIM_TIME) else { return };
    let movement = fixture("movement.json");
    let race = &movement["races"][0];
    let station = race["stationID"].as_i64().unwrap();
    let nowhere = 99_999_999;
    scene.bind(SESSION, SIM_TIME);
    let orders = [
        ("align to the station", Command::AlignTo { target_id: station }),
        ("align to what is not there", Command::AlignTo { target_id: nowhere }),
        ("orbit the station", Command::Orbit { target_id: station, distance: 5000.0 }),
        ("orbit it again", Command::Orbit { target_id: station, distance: 5000.0 }),
        ("orbit closer", Command::Orbit { target_id: station, distance: 2500.7 }),
        ("follow the station", Command::FollowBall { target_id: station, range: 3000.0 }),
        ("follow it again", Command::FollowBall { target_id: station, range: 3000.0 }),
        ("stop", Command::Stop),
        ("follow from a standstill", Command::FollowBall { target_id: station, range: 1000.0 }),
        ("speed fraction", Command::SetSpeedFraction(0.0)),
        ("align from a standstill", Command::AlignTo { target_id: station }),
        ("follow what is not there", Command::FollowBall { target_id: nowhere, range: 500.0 }),
    ];
    let mut now = SIM_TIME + 20_000_000;
    for (label, command) in orders {
        now += 15_000_000; // the recorder waited a second and a half after each order
        let out = scene.command(SESSION, command, now);
        let recorded = step(race, label);
        let recorded_update = events_named(recorded, "DoDestinyUpdate");
        let code = command.code();
        match recorded_update.first() {
            Some(update) => {
                assert_eq!(out.len(), 2, "{label}");
                let ours = out_args(&out[0]);
                let ours = actions_without_the_ship(ours["$tuple"][0].as_array().unwrap());
                let theirs = actions_without_the_ship(event_args(update)["$tuple"][0].as_array().unwrap());
                assert_eq!(
                    ours.iter().map(|a| &a.0).collect::<Vec<_>>(),
                    theirs.iter().map(|a| &a.0).collect::<Vec<_>>(),
                    "{label}"
                );
                for ((name, mine), (_, expected)) in ours.iter().zip(&theirs) {
                    if name == "GotoDirection" {
                        assert_eq!(shape(&json!(mine)), shape(&json!(expected)), "{label}");
                    } else {
                        assert_eq!(mine, expected, "{label}: {name}");
                    }
                }
                assert_eq!(out[1], Out::Accepted { session: SESSION, command: code, at: now }, "{label}");
            }
            None if label.ends_with("not there") => {
                assert_eq!(
                    out,
                    [Out::Refused { session: SESSION, command: code, reason: "UNKNOWN_TARGET" }],
                    "{label}"
                );
            }
            None => assert_eq!(
                out,
                [Out::Accepted { session: SESSION, command: code, at: now }],
                "{label}: nothing to say"
            ),
        }
    }
}

#[test]
fn a_docking_is_accepted_and_finished_as_the_reference_did() {
    let recording = fixture("undock.json");
    let dock = step(&recording["races"][0], "dock").clone();
    let stamp = destiny_actions(&dock).into_iter().next().unwrap().0;
    let now = at_stamp(stamp);
    let Some((mut scene, recorded)) = scene_at(now) else { return };
    let station = recorded.race()["stationID"].as_i64().unwrap();
    let out = scene.command(SESSION, Command::Dock { station_id: station }, now);

    let accepted = events_named(&dock, "OnDockingAccepted")[0];
    assert_eq!(out_args(&out[0]), event_args(accepted));
    let Out::Notify { id_type, .. } = &out[0] else { unreachable!() };
    assert_eq!(*id_type, "charid");
    assert_eq!(out_args(&out[1]), event_args(events_named(&dock, "DoDestinyUpdate")[0]));
    assert_eq!(out[2], Out::Accepted { session: SESSION, command: 4, at: now });

    assert_eq!(scene.poll(now + 40_000_000), [Out::Docked { session: SESSION, station_id: station }]);
    assert_eq!(json!(scene.contains(SESSION)), json!(false));
}
