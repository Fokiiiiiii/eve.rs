//! Reading what the reference recorded (`oracle/fixtures/scenarios/undock.json`) so the tests can hold the world to it.

#![allow(dead_code)]

use std::path::PathBuf;

use eve_destiny::Vec3;
use eve_space::health::{Condition, Resonances, Vitals};
use eve_space::ship::{Course, ModuleSpec, Ship, ShipSpec};
use serde_json::{Value, json};

pub fn fixture(name: &str) -> Value {
    let path = PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../../oracle/fixtures/scenarios").join(name);
    serde_json::from_str(&std::fs::read_to_string(path).expect("the recording is in the repository"))
        .expect("the recording is JSON")
}

pub fn unhex(text: &str) -> Vec<u8> {
    (0..text.len()).step_by(2).map(|i| u8::from_str_radix(&text[i..i + 2], 16).unwrap()).collect()
}

/// A recorded number: a plain JSON number, or `{"$long": n}`.
pub fn number(value: &Value) -> i64 {
    value.get("$long").and_then(Value::as_i64).or_else(|| value.as_i64()).expect("a recorded integer")
}

/// The steps of a recorded race by label.
pub fn step<'a>(race: &'a Value, label: &str) -> &'a Value {
    race["steps"]
        .as_array()
        .unwrap()
        .iter()
        .find(|s| s["label"] == label)
        .unwrap_or_else(|| panic!("no step {label}"))
}

/// The actions of every `DoDestinyUpdate` a step recorded, as `(stamp, name, args)`.
pub fn destiny_actions(step: &Value) -> Vec<(i64, String, Value)> {
    let mut out = Vec::new();
    for event in step["events"].as_array().unwrap() {
        if event["name"] != "DoDestinyUpdate" {
            continue;
        }
        let actions = &event["args"]["$tuple"][1]["$tuple"][1]["$tuple"][0];
        for action in actions.as_array().unwrap() {
            let pair = &action["$tuple"];
            let inner = &pair[1]["$tuple"];
            out.push((number(&pair[0]), inner[0].as_str().unwrap().to_string(), inner[1].clone()));
        }
    }
    out
}

/// The state a recorded `SetState` carries: its fields by name.
pub fn set_state(step: &Value) -> Value {
    let (_, _, args) =
        destiny_actions(step).into_iter().find(|(_, name, _)| name == "SetState").expect("a SetState");
    let key_val = &args["$tuple"][0]["args"]["$dict"];
    let mut fields = serde_json::Map::new();
    for pair in key_val.as_array().unwrap() {
        fields.insert(pair[0].as_str().unwrap().to_string(), pair[1].clone());
    }
    Value::Object(fields)
}

/// A rigid ball of a recorded state buffer.
#[derive(Debug, Clone, PartialEq)]
pub struct RecordedBall {
    pub id: i64,
    pub mode: u8,
    pub radius: f32,
    pub position: [f64; 3],
    pub flags: u8,
}

/// The rigid balls at the start of a state buffer (header of 5 bytes, then a rigid ball is 39 bytes); stops at the first ball of another mode.
pub fn rigid_balls(buffer: &[u8]) -> Vec<RecordedBall> {
    let mut balls = Vec::new();
    let mut at = 5;
    while at + 39 <= buffer.len() && buffer[at + 8] == 11 {
        let f64_at = |o: usize| f64::from_le_bytes(buffer[at + o..at + o + 8].try_into().unwrap());
        balls.push(RecordedBall {
            id: i64::from_le_bytes(buffer[at..at + 8].try_into().unwrap()),
            mode: buffer[at + 8],
            radius: f32::from_le_bytes(buffer[at + 9..at + 13].try_into().unwrap()),
            position: [f64_at(13), f64_at(21), f64_at(29)],
            flags: buffer[at + 37],
        });
        at += 39;
    }
    balls
}

/// The bytes of the rigid balls of a state buffer, one slice per ball, and what follows them (the ship's free ball).
pub fn split_state(buffer: &[u8]) -> (Vec<(i64, &[u8])>, &[u8]) {
    let mut balls = Vec::new();
    let mut at = 5;
    while at + 39 <= buffer.len() && buffer[at + 8] == 11 {
        balls.push((i64::from_le_bytes(buffer[at..at + 8].try_into().unwrap()), &buffer[at..at + 39]));
        at += 39;
    }
    (balls, &buffer[at..])
}

/// A value as the recordings describe it (`tools/headless-client/evecli/describe.py`), so it can be compared with one.
pub fn describe(value: &eve_marshal::Value) -> Value {
    use eve_marshal::Value as V;
    use serde_json::json;
    let pairs = |entries: &[(V, V)]| {
        Value::Array(entries.iter().map(|(k, v)| json!([describe(k), describe(v)])).collect())
    };
    match value {
        V::None => Value::Null,
        V::Bool(b) => json!(b),
        V::Int(n) if i32::try_from(*n).is_ok() => json!(n),
        V::Int(n) | V::Long(n) => json!({"$long": n}),
        V::Real(r) => json!(r),
        V::Str(s) => json!(s),
        V::Bytes(b) => json!(String::from_utf8_lossy(b)),
        V::Buffer(b) => json!({"$buffer": b.iter().map(|byte| format!("{byte:02x}")).collect::<String>()}),
        V::WString(s) => json!({"$unicode": s}),
        V::Token(t) => json!({"$token": t}),
        V::Tuple(items) => json!({"$tuple": items.iter().map(describe).collect::<Vec<_>>()}),
        V::List(items) => Value::Array(items.iter().map(describe).collect()),
        V::Dict(entries) => json!({"$dict": pairs(entries)}),
        V::Object { name, args } => json!({"$object": describe(name), "args": describe(args)}),
        V::PackedRow(row) => {
            let fields: serde_json::Map<String, Value> = row
                .descriptor
                .columns
                .iter()
                .zip(&row.values)
                // a fixed-width column reads back as a plain integer whatever its width
                .map(|(c, v)| (c.name.clone(), if let V::Long(n) = v { json!(n) } else { describe(v) }))
                .collect();
            json!({"$row": fields})
        }
        other => panic!("not described: {other:?}"),
    }
}

/// Whether two hex-written buffers are the same but for the last three doubles, which may differ in their last digits.
///
/// A ship going in a direction is sent with a point far along it (1e16 metres away); the reference works it out from a
/// direction it holds unrounded, so its low bits are not ours, and the client only reads the heading from it.
pub fn same_but_the_far_target(ours: &str, theirs: &str) -> bool {
    let tail = 48; // three doubles, two hex digits a byte
    if ours.len() != theirs.len() || ours.len() < tail {
        return false;
    }
    let (a, b) = (ours.len() - tail, theirs.len() - tail);
    let double = |hex: &str, i: usize| f64::from_le_bytes(unhex(&hex[i..i + 16]).try_into().unwrap());
    ours[..a] == theirs[..b]
        && (0..3).all(|k| {
            let (x, y) = (double(ours, a + 16 * k), double(theirs, b + 16 * k));
            (x - y).abs() <= 1e-12 * x.abs().max(y.abs())
        })
}

pub fn field(fields: &Value, name: &str) -> Value {
    fields
        .as_array()
        .unwrap()
        .iter()
        .find(|p| p[0] == name)
        .map(|p| p[1].clone())
        .unwrap_or_else(|| panic!("no field {name}"))
}

pub fn f64_at(bytes: &[u8], at: usize) -> f64 {
    f64::from_le_bytes(bytes[at..at + 8].try_into().unwrap())
}

pub fn vec_at(bytes: &[u8], at: usize) -> Vec3 {
    Vec3::new(f64_at(bytes, at), f64_at(bytes, at + 8), f64_at(bytes, at + 16))
}

/// A number the reference sent the client about the ship in an action of the bind (mass, top speed, agility).
fn number_of(race: &Value, action: &str) -> f64 {
    let (_, _, args) = destiny_actions(step(race, "bind the ballpark"))
        .into_iter()
        .find(|(_, name, _)| name == action)
        .unwrap();
    args["$tuple"][1].as_f64().unwrap()
}

/// The ship the reference undocked, put together from what it sent (the slim item and the ball).
pub fn recorded_ship(race: &Value, ball: &[u8], direction: Vec3) -> Ship {
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
        mass: number_of(race, "SetBallMass"),
        max_velocity: number_of(race, "SetMaxSpeed"),
        agility: number_of(race, "SetBallAgility"),
        warp_speed_au: 0.0,
        shield_recharge_ms: shield_tau * 5.0,
        max_target_range: 29_700.0,
        max_locked_targets: 4,
        scan_resolution: 660.0,
        signature_radius: 28.0,
        capacitor_capacity: 149.5,
        capacitor_recharge_s: 55.25,
        sensor_strength: [0.0, 0.0, 0.0, 0.0],
        ecm_resistance: 1.0,
        vitals: Vitals {
            shield_capacity: 220.0,
            armor_hp: 137.5,
            structure_hp: 201.25,
            resonances: Resonances {
                shield: [0.92, 0.736, 0.552, 0.46],
                armor: [0.5, 0.55, 0.75, 0.9],
                structure: [0.67, 0.67, 0.67, 0.67],
            },
            armor_pinned: false,
        },
        condition: Condition::FULL,
        modules,
    };
    Ship::new(spec, vec_at(ball, 13), vec_at(ball, 67), direction, f32_at(95), Course::Goto, 0)
}

pub fn recorded_direction(race: &Value) -> Vec3 {
    let (_, _, args) = destiny_actions(step(race, "bind the ballpark"))
        .into_iter()
        .find(|(_, name, _)| name == "GotoDirection")
        .unwrap();
    let a = args["$tuple"].as_array().unwrap();
    Vec3::new(a[1].as_f64().unwrap(), a[2].as_f64().unwrap(), a[3].as_f64().unwrap())
}

/// A recorded action as it reads whole: `(stamp, (name, args))`.
pub fn recorded_action(stamp: i64, name: &str, args: &Value) -> Value {
    json!({"$tuple": [stamp, {"$tuple": [name, args]}]})
}

/// The arguments a recorded notification carried, as a `$tuple`.
pub fn event_args(event: &Value) -> Value {
    event["args"]["$tuple"][1]["$tuple"][1].clone()
}

/// The recorded events of a step with this name.
pub fn events_named<'a>(step: &'a Value, name: &str) -> Vec<&'a Value> {
    step["events"].as_array().unwrap().iter().filter(|e| e["name"] == name).collect()
}

/// A description reduced to its structure and the types of its leaves.
pub fn shape(value: &Value) -> Value {
    match value {
        Value::Null => json!("none"),
        Value::Bool(_) => json!("bool"),
        Value::Number(n) => json!(if n.is_f64() { "float" } else { "int" }),
        Value::String(_) => json!("str"),
        Value::Array(items) => Value::Array(items.iter().map(shape).collect()),
        Value::Object(map) => {
            if let Some(items) = map.get("$tuple") {
                json!({"$tuple": shape(items)})
            } else if map.contains_key("$long") {
                json!("long")
            } else if map.contains_key("$buffer") {
                json!("buffer")
            } else if let Some(pairs) = map.get("$dict") {
                json!({"$dict": pairs.as_array().unwrap().iter().map(|p| json!([shape(&p[0]), shape(&p[1])])).collect::<Vec<_>>()})
            } else if let Some(name) = map.get("$object") {
                json!({"$object": name, "args": shape(&map["args"])})
            } else if let Some(row) = map.get("$row") {
                json!({"$row": row.as_object().unwrap().iter().map(|(k, v)| (k.clone(), shape(v))).collect::<serde_json::Map<_, _>>()})
            } else {
                map.iter().map(|(k, v)| (k.clone(), shape(v))).collect::<serde_json::Map<_, _>>().into()
            }
        }
    }
}

/// A notification of the scene as a recording describes the arguments of one.
pub fn out_args(out: &eve_space::scene::Out) -> Value {
    let eve_space::scene::Out::Notify { args, .. } = out else { panic!("not a notification: {out:?}") };
    json!({"$tuple": args.iter().map(describe).collect::<Vec<_>>()})
}

/// The names of the actions of a `DoDestinyUpdate` described as arguments.
pub fn action_names_of(args: &Value) -> Vec<String> {
    args["$tuple"][0]
        .as_array()
        .unwrap()
        .iter()
        .map(|a| a["$tuple"][1]["$tuple"][0].as_str().unwrap().to_string())
        .collect()
}
