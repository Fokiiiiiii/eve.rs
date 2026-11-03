use eve_space::link::Message;
use serde_json::Value;

fn bytes(s: &str) -> Vec<u8> {
    (0..s.len() / 2).map(|i| u8::from_str_radix(&s[2 * i..2 * i + 2], 16).unwrap()).collect()
}
fn fixtures() -> Value {
    let path =
        concat!(env!("CARGO_MANIFEST_DIR"), "/../../oracle/fixtures/simlink/tournament-metrics-v90.json");
    serde_json::from_str(&std::fs::read_to_string(path).unwrap()).unwrap()
}
#[test]
fn metrics_v90_golden_members_roundtrip() {
    for case in fixtures()["valid"].as_array().unwrap() {
        let frame = bytes(case["hex"].as_str().unwrap());
        let message = Message::decode(&frame[4..]).unwrap_or_else(|e| panic!("{}: {e}", case["name"]));
        assert_eq!(message.encode(), frame, "{}", case["name"]);
    }
}
#[test]
fn metrics_v90_golden_invalid_members_rejected() {
    for case in fixtures()["invalid"].as_array().unwrap() {
        let frame = bytes(case["hex"].as_str().unwrap());
        assert!(Message::decode(&frame[4..]).is_err(), "{}", case["name"]);
    }
}
