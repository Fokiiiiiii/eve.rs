//! The payloads the sim builds hold to the ones the reference sent for the same ship and system.

mod support;

use eve_space::payload;
use eve_space::sde::Sde;
use eve_space::world::World;
use serde_json::{Value, json};
use support::*;

const SYSTEM: i32 = 30100032;
const SIM_TIME: i64 = 134351527024310000;

fn world() -> Option<World> {
    let Some(sde) = Sde::locate() else {
        eprintln!("no static data found (EVE_SDE_DIR or .cache/sde): skipped");
        return None;
    };
    Some(World::load(sde).expect("the static data loads"))
}

#[test]
fn the_ship_is_added_as_the_reference_added_it() {
    let recording = fixture("undock.json");
    let race = &recording["races"][0];
    let (stamp, _, added) = destiny_actions(step(race, "bind the ballpark"))
        .into_iter()
        .find(|(_, name, _)| name == "AddBalls2")
        .unwrap();
    let buffer = unhex(added["$tuple"][0]["$tuple"][0]["$buffer"].as_str().unwrap());
    let ship = recorded_ship(race, &buffer[5..], recorded_direction(race));

    let mine = payload::add_ship(stamp as u32, &ship, SIM_TIME).unwrap();
    let mut mine = describe(&mine);
    let recorded = recorded_action(stamp, "AddBalls2", &added);
    let ours =
        mine["$tuple"][1]["$tuple"][1]["$tuple"][0]["$tuple"][0]["$buffer"].as_str().unwrap().to_string();
    let theirs =
        recorded["$tuple"][1]["$tuple"][1]["$tuple"][0]["$tuple"][0]["$buffer"].as_str().unwrap().to_string();
    assert!(same_but_the_far_target(&ours, &theirs), "the buffers differ:\n{ours}\n{theirs}");
    mine["$tuple"][1]["$tuple"][1]["$tuple"][0]["$tuple"][0]["$buffer"] = json!(theirs);
    assert_eq!(mine, recorded);
}

#[test]
fn the_state_is_what_the_reference_sent_apart_from_what_only_it_adds() {
    let Some(world) = world() else { return };
    let recording = fixture("undock.json");
    let race = &recording["races"][0];
    let bind = step(race, "bind the ballpark");
    let (stamp, _, recorded) =
        destiny_actions(bind).into_iter().find(|(_, name, _)| name == "SetState").unwrap();
    let state = &recorded["$tuple"][0]["args"]["$dict"];
    let buffer = unhex(field(state, "state")["$buffer"].as_str().unwrap());
    let (rigid, tail) = split_state(&buffer);
    let ship = recorded_ship(race, tail, recorded_direction(race));

    let mine =
        payload::set_state(stamp as u32, &world.system(SYSTEM).unwrap(), &ship, &[&ship], &[], SIM_TIME)
            .unwrap();
    let mine = describe(&mine);
    let mine_state = &mine["$tuple"][1]["$tuple"][1]["$tuple"][0]["args"]["$dict"];

    // the reference's customs offices and props have ids from 1.2e12; the sim does not build them (deviation)
    let ego = ship.spec.ship_id;
    let ours = |id: i64| id < 1_000_000_000_000 || id == ego;
    let mut expected_buffer: Vec<u8> = buffer[..5].to_vec();
    for (id, bytes) in &rigid {
        if ours(*id) {
            expected_buffer.extend_from_slice(bytes);
        }
    }
    expected_buffer.extend_from_slice(tail);
    let key = |k: &Value| number(&k["$long"].as_i64().map_or_else(|| k.clone(), |n| json!(n)));
    let keep_slim = |slims: &Value| -> Vec<Value> {
        slims
            .as_array()
            .unwrap()
            .iter()
            .filter(|s| ours(number(&field(&s["args"]["$dict"], "itemID"))))
            .cloned()
            .collect()
    };
    let keep_damage = |damage: &Value| -> Vec<Value> {
        damage["$dict"].as_array().unwrap().iter().filter(|p| ours(key(&p[0]))).cloned().collect()
    };

    for name in [
        "stamp",
        "ego",
        "industryLevel",
        "researchLevel",
        "dbuffState",
        "aggressors",
        "droneState",
        "solItem",
        "effectStates",
        "allianceBridges",
    ] {
        assert_eq!(field(mine_state, name), field(state, name), "{name}");
    }
    let expected: String = expected_buffer.iter().map(|b| format!("{b:02x}")).collect();
    let ours = field(mine_state, "state")["$buffer"].as_str().unwrap().to_string();
    assert!(same_but_the_far_target(&ours, &expected), "the state buffers differ");
    let (ours, theirs) = (keep_slim(&field(mine_state, "slims")), keep_slim(&field(state, "slims")));
    assert_eq!(ours.len(), theirs.len(), "slim count");
    for (a, b) in ours.iter().zip(&theirs) {
        let (a, b) = (a["args"]["$dict"].as_array().unwrap(), b["args"]["$dict"].as_array().unwrap());
        let item = &b[0][1];
        assert_eq!(a.len(), b.len(), "fields of slim item {item}");
        for (x, y) in a.iter().zip(b) {
            assert_eq!(x, y, "field of slim item {item}");
        }
    }
    assert_eq!(keep_damage(&field(mine_state, "damageState")), keep_damage(&field(state, "damageState")));
    assert_eq!(
        field(mine_state, "slims").as_array().unwrap().len(),
        theirs.len(),
        "no slim beyond the reference's"
    );
}
