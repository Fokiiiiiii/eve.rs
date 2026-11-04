use eve_space::link::Message;
use serde_json::Value;
use std::collections::BTreeSet;

fn fixtures() -> Value {
    let path =
        concat!(env!("CARGO_MANIFEST_DIR"), "/../../oracle/fixtures/simlink/tournament-metrics-v89.json");
    serde_json::from_str(&std::fs::read_to_string(path).unwrap()).unwrap()
}
fn bytes(s: &str) -> Vec<u8> {
    (0..s.len() / 2).map(|i| u8::from_str_radix(&s[2 * i..2 * i + 2], 16).unwrap()).collect()
}
#[test]
fn metrics_v89_raw_goldens_roundtrip_and_cover_all_six_kinds() {
    let fixture = fixtures();
    let mut kinds = BTreeSet::new();
    for case in fixture["valid"].as_array().unwrap() {
        let frame = bytes(case["hex"].as_str().unwrap());
        assert_eq!(u32::from_le_bytes(frame[..4].try_into().unwrap()) as usize, frame.len() - 4);
        let message = Message::decode(&frame[4..]).unwrap_or_else(|e| panic!("{}: {e}", case["name"]));
        assert_eq!(message.encode(), frame, "{}", case["name"]);
        kinds.insert(case["kind"].as_u64().unwrap());
    }
    assert_eq!(kinds, (148..=153).collect());
}
#[test]
fn metrics_v89_raw_invalid_goldens_are_rejected() {
    let fixture = fixtures();
    for case in fixture["invalid"].as_array().unwrap() {
        let frame = bytes(case["hex"].as_str().unwrap());
        assert!(Message::decode(&frame[4..]).is_err(), "{}", case["name"]);
    }
}
