//! Replays oracle/fixtures/simlink/messages.json, the golden frames of docs/adr/0007 (the C# host replays the same
//! file): every valid frame encodes to its bytes and decodes back, every invalid one is refused for the stated reason.

use eve_space::link::{DecodeError, Enter, How, Message};
use eve_space::ship::{ModuleSpec, ShipSpec};
use serde_json::Value;

fn fixtures() -> Value {
    let path = concat!(env!("CARGO_MANIFEST_DIR"), "/../../oracle/fixtures/simlink/messages.json");
    serde_json::from_str(&std::fs::read_to_string(path).unwrap()).unwrap()
}

fn hex(text: &str) -> Vec<u8> {
    (0..text.len() / 2).map(|i| u8::from_str_radix(&text[2 * i..2 * i + 2], 16).unwrap()).collect()
}

fn text(case: &Value, field: &str) -> String {
    case[field].as_str().unwrap_or_else(|| panic!("{field} in {case}")).to_owned()
}

fn wide(case: &Value, field: &str) -> i64 {
    text(case, field).parse().unwrap()
}

fn ship_of(case: &Value) -> ShipSpec {
    let number = |field: &str| case[field].as_f64().unwrap_or_else(|| panic!("{field} in {case}"));
    let int = |field: &str| case[field].as_i64().unwrap_or_else(|| panic!("{field} in {case}")) as i32;
    ShipSpec {
        ship_id: wide(case, "shipId"),
        character_id: wide(case, "characterId"),
        type_id: int("typeId"),
        group_id: int("groupId"),
        category_id: int("categoryId"),
        name: text(case, "name"),
        owner_id: wide(case, "ownerId"),
        corporation_id: int("corporationId"),
        alliance_id: int("allianceId"),
        war_faction_id: int("warFactionId"),
        security_status: number("securityStatus"),
        radius: number("radius"),
        mass: number("mass"),
        max_velocity: number("maxVelocity"),
        agility: number("agility"),
        warp_speed_au: number("warpSpeedAu"),
        shield_recharge_ms: number("shieldRechargeMs"),
        modules: case["modules"]
            .as_array()
            .unwrap()
            .iter()
            .map(|m| ModuleSpec {
                item_id: wide(m, "itemId"),
                type_id: m["typeId"].as_i64().unwrap() as i32,
                flag: m["flag"].as_i64().unwrap() as i32,
            })
            .collect(),
    }
}

fn message_of(case: &Value) -> Message {
    let number = |field: &str| case[field].as_u64().unwrap_or_else(|| panic!("{field} in {case}"));
    let session_id = || wide(case, "sessionId") as u64;
    match case["kind"].as_str().unwrap() {
        "Hello" => Message::Hello { version: number("version") as u16 },
        "HelloAck" => {
            Message::HelloAck { version: number("version") as u16, poll_hz: number("pollHz") as u32 }
        }
        "Enter" => Message::Enter(Box::new(Enter {
            session_id: session_id(),
            solar_system_id: number("solarSystemId") as i32,
            how: if number("how") == 0 { How::Undock } else { How::InSpace },
            station_id: wide(case, "stationId"),
            ship: ship_of(&case["ship"]),
        })),
        "EnterReply" => Message::EnterReply {
            session_id: session_id(),
            outcome: if case["ok"].as_bool().unwrap() { Ok(()) } else { Err(text(case, "reason")) },
        },
        "Leave" => Message::Leave { session_id: session_id(), why: number("why") as u8 },
        "Bind" => Message::Bind { session_id: session_id() },
        "UpdateState" => Message::UpdateState { session_id: session_id() },
        "Command" => Message::Command {
            session_id: session_id(),
            command: number("command") as u8,
            args: case["args"].as_array().unwrap().iter().map(|a| a.as_f64().unwrap()).collect(),
        },
        "ShipSpec" => Message::ShipSpec { session_id: session_id(), ship: ship_of(&case["ship"]) },
        "Notify" => Message::Notify {
            session_id: session_id(),
            name: text(case, "notification"),
            id_type: text(case, "idType"),
            args: hex(case["args"].as_str().unwrap()),
        },
        "Docked" => Message::Docked { session_id: session_id(), station_id: wide(case, "stationId") },
        "Refused" => Message::Refused {
            session_id: session_id(),
            command: number("command") as u8,
            reason: text(case, "reason"),
        },
        "Accepted" => Message::Accepted {
            session_id: session_id(),
            command: number("command") as u8,
            at: wide(case, "at"),
        },
        "Approaching" => Message::Approaching {
            session_id: session_id(),
            command: number("command") as u8,
            distance: case["distance"].as_f64().unwrap(),
        },
        "Jumping" => Message::Jumping {
            session_id: session_id(),
            command: number("command") as u8,
            destination_system: number("destinationSystem") as i32,
            destination_gate: wide(case, "destinationGate"),
        },
        "Jumped" => Message::Jumped {
            session_id: session_id(),
            from_system: number("fromSystem") as i32,
            from_gate: wide(case, "fromGate"),
            to_system: number("toSystem") as i32,
            to_gate: wide(case, "toGate"),
        },
        other => panic!("unknown kind {other}"),
    }
}

fn reason_of(error: DecodeError) -> &'static str {
    match error {
        DecodeError::Empty => "empty",
        DecodeError::UnknownKind(_) => "unknown-kind",
        DecodeError::Truncated => "truncated",
        DecodeError::Trailing => "trailing",
        DecodeError::Utf8 => "utf8",
        DecodeError::BadValue(_) => "bad-value",
    }
}

#[test]
fn every_valid_frame_encodes_to_its_bytes_and_decodes_back() {
    let fixtures = fixtures();
    let cases = fixtures["valid"].as_array().unwrap();
    assert!(cases.len() >= 20);
    for case in cases {
        let name = text(case, "name");
        let expected = hex(case["hex"].as_str().unwrap());
        let message = message_of(case);
        assert_eq!(message.encode(), expected, "encoding of {name}");
        // the frame is its length, then the payload
        let payload = &expected[4..];
        assert_eq!(
            u32::from_le_bytes(expected[..4].try_into().unwrap()) as usize,
            payload.len(),
            "length of {name}"
        );
        assert_eq!(Message::decode(payload).unwrap(), message, "decoding of {name}");
    }
}

#[test]
fn every_invalid_frame_is_refused_for_the_stated_reason() {
    let fixtures = fixtures();
    for case in fixtures["invalid"].as_array().unwrap() {
        let name = text(case, "name");
        let frame = hex(case["hex"].as_str().unwrap());
        let error = Message::decode(&frame[4..]).unwrap_err();
        assert_eq!(reason_of(error), text(case, "reason"), "{name}");
    }
}
