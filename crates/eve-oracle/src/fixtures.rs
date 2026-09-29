//! Loading the fixture files under `oracle/fixtures`.

use std::path::PathBuf;

use serde_json::Value as Json;

pub fn fixture_dir() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../../oracle/fixtures")
}

/// Load `<group>/cases.json`: an array of records.
pub fn load(group: &str) -> Vec<Json> {
    let path = fixture_dir().join(group).join("cases.json");
    let text = std::fs::read_to_string(&path)
        .unwrap_or_else(|error| panic!("cannot read {}: {error}", path.display()));
    match serde_json::from_str(&text)
        .unwrap_or_else(|error| panic!("bad JSON in {}: {error}", path.display()))
    {
        Json::Array(records) => records,
        _ => panic!("{} is not a JSON array", path.display()),
    }
}
