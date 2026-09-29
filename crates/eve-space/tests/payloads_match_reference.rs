//! The payloads the sim builds hold to the ones the reference sent for the same ship and system.

mod support;

use eve_destiny::Vec3;
use eve_space::payload;
use eve_space::sde::Sde;
use eve_space::ship::{Course, ModuleSpec, Ship, ShipSpec};
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

fn field(fields: &Value, name: &str) -> Value {
    fields
        .as_array()
        .unwrap()
        .iter()
        .find(|p| p[0] == name)
        .map(|p| p[1].clone())
        .unwrap_or_else(|| panic!("no field {name}"))
}

fn f64_at(bytes: &[u8], at: usize) -> f64 {
    f64::from_le_bytes(bytes[at..at + 8].try_into().unwrap())
}

fn vec_at(bytes: &[u8], at: usize) -> Vec3 {
    Vec3::new(f64_at(bytes, at), f64_at(bytes, at + 8), f64_at(bytes, at + 16))
}

/// The ship the reference undocked, put together from what it sent (the slim item and the ball).
fn recorded_ship(race: &Value, ball: &[u8], direction: Vec3) -> Ship {
    let bind = step(race, "bind the ballpark");
    let added = destiny_actions(bind).into_iter().find(|(_, name, _)| name == "AddBalls2").unwrap().2;
    let extra = &added["$tuple"][0]["$tuple"][1][0]["$tuple"];
    let slim = &extra[0]["$dict"];
    let modules = field(slim, "modules")
        .as_array()
        .unwrap()
        .iter()
        .map(|m| {
            let m = &m["$tuple"];
            ModuleSpec { item_id: number(&m[0]), type_id: number(&m[1]) as i32, flag: number(&m[2]) as i32 }
        })
        .collect();
    let shield_tau = extra[1]["$tuple"][0]["$tuple"][1].as_f64().unwrap();
    // the ball: id, mode, radius, position, flags, mass, cloak, harmonic, corporation, alliance, top speed, velocity, inertia, speed fraction
    let f32_at = |at: usize| f64::from(f32::from_le_bytes(ball[at..at + 4].try_into().unwrap()));
    let spec = ShipSpec {
        ship_id: number(&field(slim, "itemID")),
        character_id: number(&field(slim, "charID")),
        type_id: number(&field(slim, "typeID")) as i32,
        group_id: number(&field(slim, "groupID")) as i32,
        category_id: number(&field(slim, "categoryID")) as i32,
        name: field(slim, "name").as_str().unwrap().to_string(),
        owner_id: number(&field(slim, "ownerID")),
        corporation_id: number(&field(slim, "corpID")) as i32,
        alliance_id: number(&field(slim, "allianceID")) as i32,
        war_faction_id: number(&field(slim, "warFactionID")) as i32,
        security_status: 0.0,
        radius: f32_at(9),
        mass: f64_at(ball, 38),
        max_velocity: f32_at(63),
        agility: f32_at(91),
        warp_speed_au: 0.0,
        shield_recharge_ms: shield_tau * 5.0,
        modules,
    };
    Ship {
        spec,
        position: vec_at(ball, 13),
        velocity: vec_at(ball, 67),
        direction,
        speed_fraction: f32_at(95),
        course: Course::Goto,
    }
}

fn recorded_direction(race: &Value) -> Vec3 {
    let (_, _, args) = destiny_actions(step(race, "bind the ballpark"))
        .into_iter()
        .find(|(_, name, _)| name == "GotoDirection")
        .unwrap();
    let a = args["$tuple"].as_array().unwrap();
    Vec3::new(a[1].as_f64().unwrap(), a[2].as_f64().unwrap(), a[3].as_f64().unwrap())
}

/// A recorded action as it reads whole: `(stamp, (name, args))`.
fn recorded_action(stamp: i64, name: &str, args: &Value) -> Value {
    json!({"$tuple": [stamp, {"$tuple": [name, args]}]})
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

    let mine = payload::set_state(stamp as u32, &world.system(SYSTEM).unwrap(), &ship, SIM_TIME).unwrap();
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
