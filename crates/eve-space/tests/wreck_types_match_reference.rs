//! The wreck a ship of some type leaves is the one the reference's `resolveShipWreckType` gave for the same type (`oracle/fixtures/wrecks/ship_wrecks.json`,
//! made by `tools/dev/gen-ship-wrecks-fixture.js` for every ship type of the static data).

use std::path::PathBuf;

use eve_space::sde::Sde;
use eve_space::wreck_types::WreckCatalog;
use serde_json::Value;

#[test]
fn every_ship_type_leaves_the_wreck_the_reference_gave_it() {
    let Some(sde) = Sde::locate() else {
        eprintln!("no static data found (EVE_SDE_DIR or .cache/sde): skipped");
        return;
    };
    let catalog = WreckCatalog::from_sde(&sde).expect("the static data loads");
    let path =
        PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../../oracle/fixtures/wrecks/ship_wrecks.json");
    let fixture: Value =
        serde_json::from_str(&std::fs::read_to_string(path).expect("the fixture is in the repository"))
            .expect("the fixture is JSON");
    let cases = fixture["cases"].as_array().unwrap();
    assert!(cases.len() > 400, "{} cases", cases.len());

    let mut wrong = Vec::new();
    for case in cases {
        let ship = case["shipTypeID"].as_i64().unwrap();
        let mine = catalog.resolve_ship_wreck_type(ship);
        let theirs = case["wreck"].as_object().map(|wreck| {
            (
                wreck["typeID"].as_i64().unwrap() as i32,
                wreck["name"].as_str().unwrap().to_string(),
                wreck["graphicID"].as_i64().map(|graphic| graphic as i32),
            )
        });
        let mine = mine.map(|kind| (kind.type_id, kind.name, kind.graphic_id));
        if mine != theirs {
            wrong.push(format!("ship {ship}: mine {mine:?}, the reference's {theirs:?}"));
        }
    }
    assert!(wrong.is_empty(), "{} of {} differ:\n{}", wrong.len(), cases.len(), wrong.join("\n"));
}
