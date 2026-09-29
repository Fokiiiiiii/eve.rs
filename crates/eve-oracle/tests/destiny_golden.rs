//! Replays tools/oracle/fixtures/destiny against the Rust destiny kernels.
//! Outputs are compared as EJSON, i.e. down to the exact IEEE-754 bits.

use eve_oracle::destiny_adapter;
use eve_oracle::fixtures;
use eve_oracle::js::{from_ejson, to_ejson};
use serde_json::Value as Json;

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
