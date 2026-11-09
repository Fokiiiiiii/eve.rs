//! Replays tools/oracle/fixtures/destiny against the Rust destiny kernels.

use eve_destiny::ball::{BallEntity, IdRef, Kind, MiniBall, MiniBox, MiniCapsule, encode_entity_ball};
use eve_oracle::destiny_adapter;
use eve_oracle::fixtures;
use eve_oracle::js::{Js, from_ejson, to_ejson};
use serde_json::Value as Json;
use std::sync::OnceLock;

static DESTINY_CASES: OnceLock<Vec<Json>> = OnceLock::new();

fn fixture_input(id: &str) -> Js {
    let cases = DESTINY_CASES.get_or_init(|| fixtures::load("destiny"));
    let case = cases.iter().find(|case| case["id"] == id).unwrap_or_else(|| panic!("missing case {id}"));
    from_ejson(&case["input"]).unwrap_or_else(|error| panic!("{id}: bad input: {error}"))
}

fn encoded_ball(input: &Js) -> Vec<u8> {
    match destiny_adapter::run_ball(input).expect("ball should encode") {
        Js::Buf(bytes) => bytes,
        other => panic!("ball encoder returned {other:?}, expected bytes"),
    }
}

fn obj(fields: impl IntoIterator<Item = (&'static str, Js)>) -> Js {
    Js::obj(fields)
}

fn number(bytes: &[u8], at: usize) -> f32 {
    f32::from_le_bytes(bytes[at..at + 4].try_into().expect("four bytes"))
}

fn int32(bytes: &[u8], at: usize) -> i32 {
    i32::from_le_bytes(bytes[at..at + 4].try_into().expect("four bytes"))
}

fn clip(text: &str) -> String {
    if text.len() > 400 { format!("{}…", &text[..400]) } else { text.to_owned() }
}

#[test]
fn destiny_golden() {
    let cases = fixtures::load("destiny");
    assert!(cases.len() > 1000, "fixture set unexpectedly small: {}", cases.len());

    let mut failures: Vec<String> = Vec::new();
    let mut checked = 0usize;

    for case in &cases {
        let id = case["id"].as_str().expect("case id");
        let group = case["group"].as_str().expect("case group");
        let input = from_ejson(&case["input"]).unwrap_or_else(|error| panic!("{id}: bad input: {error}"));

        let actual: Result<Json, String> = match group {
            "carbon.integrate" => Ok(to_ejson(&destiny_adapter::run_integrate(&input))),
            "carbon.acceleration" => Ok(to_ejson(&destiny_adapter::run_acceleration(&input))),
            "math.exp" => Ok(to_ejson(&destiny_adapter::run_exp(&input))),
            "ball.encode" => match destiny_adapter::run_ball(&input) {
                Ok(ball) => Ok(to_ejson(&ball)),
                Err(error) => Err(error.kind().to_owned()),
            },
            _ => continue, // other groups are replayed by their own tests
        };
        checked += 1;

        match (actual, case.get("output"), case.get("error")) {
            (Err(kind), None, Some(expected)) => {
                if expected.as_str() != Some(kind.as_str()) {
                    failures.push(format!(
                        "{id}: failed as {kind}, expected {expected}\n  input {}",
                        clip(&case["input"].to_string())
                    ));
                }
            }
            (Ok(actual), Some(expected), None) => {
                if &actual != expected {
                    failures.push(format!(
                        "{id}: output differs\n  input    {}\n  expected {}\n  actual   {}",
                        clip(&case["input"].to_string()),
                        clip(&expected.to_string()),
                        clip(&actual.to_string())
                    ));
                }
            }
            (actual, expected, error) => {
                failures.push(format!("{id}: unexpected shape: {actual:?} vs {expected:?} / {error:?}"));
            }
        }
    }

    assert!(checked > 1000, "only {checked} destiny cases were replayed");
    assert!(
        failures.is_empty(),
        "{} of {} cases differ:\n{}",
        failures.len(),
        checked,
        failures.iter().take(8).cloned().collect::<Vec<_>>().join("\n")
    );
}

#[test]
fn owned_fighter_ball_is_noninteractive_while_drones_and_explicit_overrides_remain() {
    let fighter = encoded_ball(&fixture_input("ball:rand:free:39"));
    assert_eq!(fighter[37] & 0x08, 0, "owned fighter must not carry IS_INTERACTIVE");

    let drone = obj([
        ("itemID", Js::Num(42.0)),
        ("kind", Js::Str("drone".into())),
        ("mode", Js::Str("STOP".into())),
        ("ownerID", Js::Num(9001.0)),
    ]);
    assert_ne!(encoded_ball(&drone)[37] & 0x08, 0, "owned drone remains interactive");

    let forced_fighter = obj([
        ("itemID", Js::Num(43.0)),
        ("kind", Js::Str("fighter".into())),
        ("mode", Js::Str("STOP".into())),
        ("ownerID", Js::Num(9001.0)),
        ("destinyForceInteractive", Js::Bool(true)),
    ]);
    assert_ne!(encoded_ball(&forced_fighter)[37] & 0x08, 0, "explicit interactive override is preserved");

    let configured_fighter = obj([
        ("itemID", Js::Num(44.0)),
        ("kind", Js::Str("fighter".into())),
        ("mode", Js::Str("STOP".into())),
        ("ownerID", Js::Num(9001.0)),
        ("destinyBallFlags", Js::Num(8.0)),
    ]);
    assert_ne!(encoded_ball(&configured_fighter)[37] & 0x08, 0, "configured flag override is preserved");
}

#[test]
fn orbit_ball_uses_orbit_distance_when_follow_range_is_also_present() {
    let input = obj([
        ("itemID", Js::Num(142917555.0)),
        ("kind", Js::Str("warpDisruptionProbe".into())),
        ("mode", Js::Str("ORBIT".into())),
        ("followID", Js::Num(143603203.0)),
        ("followRange", Js::Num(27104.054)),
        ("orbitDistance", Js::Num(1790.032)),
    ]);
    let bytes = encoded_ball(&input);
    assert_eq!(bytes[8], 4, "ORBIT mode");
    assert_eq!(number(&bytes, 108), 1790.032_f64 as f32);
}

#[test]
fn warp_ball_uses_the_client_visible_root_fields() {
    let target = obj([("x", Js::Num(5000.0)), ("y", Js::Num(0.0)), ("z", Js::Num(0.0))]);
    let input = obj([
        ("itemID", Js::Num(140000001.0)),
        ("kind", Js::Str("ship".into())),
        ("mode", Js::Str("WARP".into())),
        ("targetPoint", target),
        ("effectStamp", Js::Num(123456789.0)),
        ("warpDistance", Js::Num(450000000000.0)),
        ("warpMinRange", Js::Num(2500.0)),
        ("warpFactor", Js::Num(4500.0)),
    ]);
    let bytes = encoded_ball(&input);
    assert_eq!(int32(&bytes, 124), 123456789, "root effectStamp");
    assert_eq!(f64::from_le_bytes(bytes[128..136].try_into().expect("eight bytes")), 450000000000.0);
    assert_eq!(f64::from_le_bytes(bytes[136..144].try_into().expect("eight bytes")), 2500.0);
    assert_eq!(i64::from_le_bytes(bytes[144..152].try_into().expect("eight bytes")), 4500);

    let zero_factor = obj([
        ("itemID", Js::Num(140000002.0)),
        ("kind", Js::Str("ship".into())),
        ("mode", Js::Str("WARP".into())),
        ("warpFactor", Js::Num(0.0)),
        ("warpSpeedAU", Js::Num(2.5)),
    ]);
    let bytes = encoded_ball(&zero_factor);
    assert_eq!(i64::from_le_bytes(bytes[144..152].try_into().expect("eight bytes")), 2500);
}

#[test]
fn mini_geometry_count_and_payload_are_truncated_at_the_wire_limit() {
    for count in [usize::from(u16::MAX), usize::from(u16::MAX) + 1] {
        let ball = BallEntity {
            item_id: IdRef::Valid(1),
            kind: Kind::Other,
            is_free: Some(false),
            mini_balls: vec![MiniBall::default(); count],
            mini_capsules: vec![MiniCapsule::default(); count],
            mini_boxes: vec![MiniBox::default(); count],
            ..BallEntity::default()
        };
        let bytes = encode_entity_ball(&ball).expect("static ball should encode");
        let expected_count = u16::MAX;
        let count_bytes = expected_count.to_le_bytes();
        let mini_ball_end = 41 + usize::from(expected_count) * 28;
        let mini_capsule_end = mini_ball_end + 2 + usize::from(expected_count) * 52;
        let mini_box_end = mini_capsule_end + 2 + usize::from(expected_count) * 96;
        assert_eq!(&bytes[39..41], &count_bytes, "mini-ball count for input {count}");
        assert_eq!(
            &bytes[mini_ball_end..mini_ball_end + 2],
            &count_bytes,
            "mini-capsule count for input {count}"
        );
        assert_eq!(
            &bytes[mini_capsule_end..mini_capsule_end + 2],
            &count_bytes,
            "mini-box count for input {count}"
        );
        assert_eq!(bytes.len(), mini_box_end, "mini-geometry payload for input {count}");
    }
}
