//! Slim items: what the client is told a ball is (its name, type, owner, and what its kind shows).

use eve_marshal::{Descriptor, PackedColumn, PackedRow, Value, dbtype};

use crate::security;
use crate::ship::ShipSpec;
use crate::wire::{id, int, list, real, text, tuple, word};
use crate::world::{Body, BodyKind, Detail, SolarSystem};

type Entries = Vec<(&'static str, Value)>;

/// The orientations the reference hand-authors for two stations (observed in the live game); every other station has none.
const STATION_ROTATIONS: [(i64, [f64; 3]); 2] =
    [(60003760, [206.8031, 0.0, 0.0]), (60015249, [305.0, 0.0, 0.0])];

/// A rotation as the client reads it: whole angles are ints.
fn number(value: f64) -> Value {
    if value.fract() == 0.0 && value.abs() < 1e9 { int(value as i64) } else { real(value) }
}

fn rotation(angles: [f64; 3]) -> Value {
    tuple(angles.map(number))
}

/// What every slim item starts with.
fn identity(body: &Body) -> Entries {
    vec![
        ("itemID", id(body.id)),
        ("typeID", int(i64::from(body.type_id))),
        ("ownerID", id(body.owner_id)),
        ("name", text(body.name.clone())),
        ("groupID", int(i64::from(body.group_id))),
        ("categoryID", int(i64::from(body.category_id))),
    ]
}

fn stargate_fields(system: &SolarSystem, body: &Body, entries: &mut Entries) {
    let Detail::Stargate {
        destination_system,
        destination_gate,
        destination_security,
        destination_faction,
        hazardous,
        facing,
        ..
    } = &body.detail
    else {
        return;
    };
    let stage = || list([int(0), int(1)]);
    let owner = |faction: Option<i64>| faction.map_or(Value::None, id);
    if let Some(graphic) = body.graphic_id {
        entries.push(("graphicID", id(graphic)));
    }
    entries.push(("nameID", Value::None));
    entries.push(("activationState", int(2)));
    entries.push(("poseID", int(0)));
    entries.push(("localCorruptionStageAndMaximum", stage()));
    entries.push(("destinationCorruptionStageAndMaximum", stage()));
    entries.push(("localSuppressionStageAndMaximum", stage()));
    entries.push(("destinationSuppressionStageAndMaximum", stage()));
    entries.push(("hasVolumetricDrifterCloud", int(0)));
    entries.push(("originSystemOwnerID", owner(system.faction_id)));
    entries.push(("destinationSystemOwnerID", owner(*destination_faction)));
    entries.push(("destinationSystemStatusIcons", list([text(security::icon_key(*destination_security))])));
    let warning = if *hazardous { text("stargate_travelwarning3.dds") } else { Value::None };
    entries.push(("destinationSystemWarning", warning.clone()));
    entries.push(("destinationSystemWarningIcon", warning));
    if let Some(angles) = facing {
        entries.push(("dunRotation", rotation(*angles)));
    }
    entries.push((
        "jumps",
        rowset(
            &["toCelestialID", "locationID"],
            [tuple([id(*destination_gate), id(i64::from(*destination_system))])],
        ),
    ));
}

/// An `eve.common.script.sys.rowset.Rowset` of plain lines.
pub fn rowset(columns: &[&'static str], lines: impl IntoIterator<Item = Value>) -> Value {
    let names = || list(columns.iter().map(|c| word(c)));
    Value::Object {
        name: Box::new(word("eve.common.script.sys.rowset.Rowset")),
        args: Box::new(crate::wire::dict([
            ("header", names()),
            ("columns", names()),
            ("RowClass", Value::Token("util.Row".into())),
            ("lines", list(lines)),
        ])),
    }
}

/// The entries of a body's slim item, in the order the reference gives them.
pub fn body_entries(system: &SolarSystem, body: &Body) -> Entries {
    let mut entries = identity(body);
    match body.kind {
        BodyKind::Station => {
            let corporation = match body.detail {
                Detail::Station { corporation_id } => corporation_id,
                _ => body.owner_id,
            };
            entries.push(("corpID", id(corporation)));
            entries.push(("allianceID", int(0)));
            entries.push(("warFactionID", int(0)));
            entries.push(("online", int(1)));
            entries.push(("incapacitated", int(0)));
            entries.push(("activityLevel", Value::None));
            entries.push(("skinMaterialSetID", Value::None));
            if let Some((_, angles)) = STATION_ROTATIONS.iter().find(|(station, _)| *station == body.id) {
                entries.push(("dunRotation", rotation(*angles)));
            }
        }
        BodyKind::Stargate => stargate_fields(system, body, &mut entries),
        BodyKind::Planet | BodyKind::Moon | BodyKind::Belt | BodyKind::Star => {}
    }
    entries
}

/// The entries of a ship's slim item.
pub fn ship_entries(spec: &ShipSpec) -> Entries {
    let modules = spec
        .modules
        .iter()
        .map(|m| tuple([id(m.item_id), int(i64::from(m.type_id)), int(i64::from(m.flag))]));
    vec![
        ("itemID", id(spec.ship_id)),
        ("typeID", int(i64::from(spec.type_id))),
        ("ownerID", id(spec.owner_id)),
        ("name", text(spec.name.clone())),
        ("groupID", int(i64::from(spec.group_id))),
        ("categoryID", int(i64::from(spec.category_id))),
        ("corpID", id(i64::from(spec.corporation_id))),
        ("allianceID", id(i64::from(spec.alliance_id))),
        ("warFactionID", id(i64::from(spec.war_faction_id))),
        ("charID", id(spec.character_id)),
        ("kills", int(0)),
        ("skinMaterialSetID", Value::None),
        ("modules", list(modules)),
        ("securityStatus", number(spec.security_status)),
        ("bounty", int(0)),
    ]
}

/// The system's own item, a row of the client's item table (`solItem` of a `SetState`).
pub fn system_row(system: &SolarSystem) -> Value {
    const COLUMNS: [(&str, i64); 11] = [
        ("itemID", dbtype::I8),
        ("typeID", dbtype::I4),
        ("ownerID", dbtype::I4),
        ("locationID", dbtype::I8),
        ("flagID", dbtype::I2),
        ("contraband", dbtype::BOOL),
        ("singleton", dbtype::I2),
        ("quantity", dbtype::I4),
        ("groupID", dbtype::I4),
        ("categoryID", dbtype::I4),
        ("customInfo", dbtype::STR),
    ];
    let header = Value::ObjectEx {
        kind: eve_marshal::ObjectExKind::Ex1,
        header: Box::new(tuple([
            Value::Token("blue.DBRowDescriptor".into()),
            tuple([tuple(COLUMNS.iter().map(|(name, code)| tuple([word(name), int(*code)])))]),
        ])),
        list: Vec::new(),
        dict: Vec::new(),
    };
    let columns = COLUMNS
        .iter()
        .map(|(name, code)| PackedColumn { name: (*name).to_string(), db_type: *code })
        .collect();
    Value::PackedRow(Box::new(PackedRow {
        descriptor: Descriptor::new(header, columns),
        values: vec![
            Value::Long(i64::from(system.id)),
            int(5),
            int(1),
            Value::Long(i64::from(system.constellation_id)),
            int(0),
            Value::Bool(false),
            int(1),
            int(-1),
            int(5),
            int(2),
            text(""),
        ],
        rle: None,
    }))
}
