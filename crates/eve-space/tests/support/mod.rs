//! Reading what the reference recorded (`oracle/fixtures/scenarios/undock.json`) so the tests can hold the world to it.

#![allow(dead_code)]

use std::path::PathBuf;

use serde_json::Value;

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
