//! Replays tools/oracle/fixtures/marshal against the Rust codec.
//!
//! Encoding must reproduce the recorded bytes exactly; decoding must produce
//! the recorded value (compared in EJSON, where every number keeps its exact

use eve_marshal::{decode, decode_exact, encode};
use eve_oracle::fixtures;
use eve_oracle::js::{from_ejson, hex_encode, hex_to_bytes, to_ejson};
use eve_oracle::marshal_adapter::{js_to_value, value_to_js};
use serde_json::Value as Json;

fn clip(text: &str) -> String {
    if text.len() > 300 { format!("{}…", &text[..300]) } else { text.to_owned() }
}

fn check_decode(
    id: &str,
    label: &str,
    result: Result<eve_marshal::Value, eve_marshal::MarshalError>,
    expected: Option<&Json>,
    expected_error: Option<&str>,
    failures: &mut Vec<String>,
) {
    match (result, expected, expected_error) {
        (Ok(value), Some(expected), None) => {
            let actual = to_ejson(&value_to_js(&value));
            if &actual != expected {
                failures.push(format!(
                    "{id}: {label} value differs\n  expected {}\n  actual   {}",
                    clip(&expected.to_string()),
                    clip(&actual.to_string())
                ));
            }
        }
        (Err(error), None, Some(kind)) => {
            if error.kind() != kind {
                failures.push(format!("{id}: {label} failed as {} ({error}), expected {kind}", error.kind()));
            }
        }
        (Ok(_), None, Some(kind)) => failures.push(format!("{id}: {label} succeeded, expected {kind}")),
        (Err(error), Some(_), None) => failures.push(format!("{id}: {label} failed: {error}")),
        _ => failures.push(format!("{id}: malformed fixture record for {label}")),
    }
}

#[test]
fn marshal_golden() {
    let cases = fixtures::load("marshal");
    assert!(cases.len() > 500, "fixture set unexpectedly small: {}", cases.len());

    let mut failures: Vec<String> = Vec::new();
    let (mut encoded_cases, mut decoded_cases) = (0, 0);

    for case in &cases {
        let id = case["id"].as_str().expect("case id");

        if let Some(input) = case.get("input") {
            encoded_cases += 1;
            let js = from_ejson(input).unwrap_or_else(|error| panic!("{id}: bad input EJSON: {error}"));
            let result = js_to_value(&js).and_then(|value| encode(&value));
            match (
                result,
                case.get("encoded").and_then(Json::as_str),
                case.get("encodeError").and_then(Json::as_str),
            ) {
                (Ok(bytes), Some(expected), None) => {
                    if hex_encode(&bytes) != expected {
                        failures.push(format!(
                            "{id}: encoded bytes differ\n  expected {}\n  actual   {}",
                            clip(expected),
                            clip(&hex_encode(&bytes))
                        ));
                    }
                }
                (Err(error), None, Some(kind)) => {
                    if error.kind() != kind {
                        failures.push(format!(
                            "{id}: encode failed as {} ({error}), expected {kind}",
                            error.kind()
                        ));
                    }
                }
                (Ok(_), None, Some(kind)) => {
                    failures.push(format!("{id}: encode succeeded, expected {kind}"))
                }
                (Err(error), Some(_), None) => failures.push(format!("{id}: encode failed: {error}")),
                _ => failures.push(format!("{id}: malformed fixture record for encode")),
            }
        }

        if let Some(hex) = case.get("encoded").and_then(Json::as_str) {
            decoded_cases += 1;
            let bytes = hex_to_bytes(hex).unwrap_or_else(|error| panic!("{id}: {error}"));
            check_decode(
                id,
                "decode",
                decode(&bytes),
                case.get("decoded"),
                case.get("decodeError").and_then(Json::as_str),
                &mut failures,
            );
            check_decode(
                id,
                "decode_exact",
                decode_exact(&bytes),
                case.get("decodedExact"),
                case.get("decodeExactError").and_then(Json::as_str),
                &mut failures,
            );
        }
    }

    assert!(
        failures.is_empty(),
        "{} problem(s) across {} encode / {} decode cases:\n{}",
        failures.len(),
        encoded_cases,
        decoded_cases,
        failures.iter().take(25).cloned().collect::<Vec<_>>().join("\n")
    );
}
