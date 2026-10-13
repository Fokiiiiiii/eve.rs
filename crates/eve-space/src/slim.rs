//! Slim items: what the client is told a ball is (its name, type, owner, and what its kind shows).

use eve_marshal::{Descriptor, PackedColumn, PackedRow, Value, dbtype};

use crate::security;
use crate::ship::{DroneLink, NpcIdentity, Ship, ShipSpec};
use crate::wire::{filetime, id, int, list, real, text, tuple, word};
use crate::world::{Body, BodyKind, Detail, SolarSystem};

type Entries = Vec<(&'static str, Value)>;

/// The orientations the reference hand-authors for two stations (observed in the live game); every other station has none.
const STATION_ROTATIONS: [(i64, [f64; 3]); 2] =
    [(60003760, [206.8031, 0.0, 0.0]), (60015249, [305.0, 0.0, 0.0])];

/// The orientation the reference authors for a station, if it has one.
pub fn station_rotation(station_id: i64) -> Option<[f64; 3]> {
    STATION_ROTATIONS.iter().find(|(id, _)| *id == station_id).map(|(_, angles)| *angles)
}

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
            if let Some(angles) = station_rotation(body.id) {
                entries.push(("dunRotation", rotation(angles)));
            }
        }
        BodyKind::Stargate => stargate_fields(system, body, &mut entries),
        BodyKind::Structure => structure_fields(body, &mut entries),
        BodyKind::Wormhole => wormhole_fields(body, &mut entries),
        BodyKind::Planet | BodyKind::Moon | BodyKind::Belt | BodyKind::Star => {}
    }
    entries
}

/// A wormhole end's slim fields: the class of the system it leads to (the client's info window reads `otherSolarSystemClass`).
fn wormhole_fields(body: &Body, entries: &mut Entries) {
    let Detail::Wormhole { destination_class, .. } = &body.detail else {
        return;
    };
    entries.push(("otherSolarSystemClass", int(i64::from(*destination_class))));
}

/// A structure's slim fields (the client's structure bracket and `structure.py` read them): its owner, its state and upkeep, its timer
/// `(start, end, paused)`, its unanchoring time and whether it is repaired.
fn structure_fields(body: &Body, entries: &mut Entries) {
    let Detail::Structure { corporation_id, alliance_id, status, planet_id } = &body.detail else {
        return;
    };
    if *planet_id > 0 {
        // a customs office: an orbital of its planet, at the level the client's office window reads (the spaceport model)
        entries.push(("planetID", id(*planet_id)));
        entries.push(("level", int(1)));
    }
    entries.push(("corpID", id(*corporation_id)));
    entries.push(("allianceID", int(*alliance_id)));
    entries.push(("state", int(i64::from(status.state))));
    entries.push(("upkeepState", int(i64::from(status.upkeep_state))));
    let timer = match status.timer {
        Some((start, end, paused)) => {
            tuple([filetime(start), filetime(end), paused.map_or(Value::None, filetime)])
        }
        None => Value::None,
    };
    entries.push(("timer", timer));
    entries.push(("unanchoring", status.unanchoring.map_or(Value::None, filetime)));
    entries.push(("repairing", status.repairing.map_or(Value::None, Value::Bool)));
}

/// The entries of a ship's slim item: a player's, or an NPC's (which has no name and ends with the limits of its owner's reactions).
pub fn ship_entries(ship: &Ship) -> Entries {
    match &ship.npc {
        Some(NpcIdentity { drone: Some(link), .. }) => drone_entries(&ship.spec, link),
        Some(npc) if !npc.hull => npc_ship_entries(&ship.spec, npc),
        _ => pilot_ship_entries(&ship.spec),
    }
}

/// The entries of the slim item of a drone, in the order the reference gives them (`oracle/fixtures/scenarios/drone_flight.json`): its type's name, the graphic and what it is doing.
fn drone_entries(spec: &ShipSpec, link: &DroneLink) -> Entries {
    vec![
        ("itemID", id(spec.ship_id)),
        ("typeID", int(i64::from(spec.type_id))),
        ("ownerID", int(spec.owner_id)),
        ("name", text(spec.name.clone())),
        ("groupID", int(i64::from(spec.group_id))),
        ("categoryID", int(i64::from(spec.category_id))),
        ("graphicID", int(i64::from(link.graphic_id))),
        ("corpID", int(i64::from(spec.corporation_id))),
        ("allianceID", int(i64::from(spec.alliance_id))),
        ("warFactionID", int(i64::from(spec.war_faction_id))),
        ("activityState", int(i64::from(link.activity))),
    ]
}

/// The entries of the slim item of an NPC's ship, in the order the reference gives them (`oracle/fixtures/scenarios/jump.json`).
fn npc_ship_entries(spec: &ShipSpec, npc: &NpcIdentity) -> Entries {
    let modules = spec
        .modules
        .iter()
        .map(|m| tuple([id(m.item_id), int(i64::from(m.type_id)), int(i64::from(m.flag))]));
    vec![
        ("itemID", id(spec.ship_id)),
        ("typeID", int(i64::from(spec.type_id))),
        ("ownerID", id(spec.owner_id)),
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
        ("hostile_response_threshold", int(npc.hostile_response_threshold)),
        ("friendly_response_threshold", int(npc.friendly_response_threshold)),
    ]
}

/// The entries of the slim item of a player's ship.
fn pilot_ship_entries(spec: &ShipSpec) -> Entries {
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
    .into_iter()
    .chain(spec.stance.map(|stance| {
        // the stance a ship has, as the client reads it: (old stance, when it switched as a FILETIME, new stance); a ship with none has no field
        (
            "shipStance",
            tuple([
                int(i64::from(stance.old_stance)),
                filetime(stance.switched_at),
                int(i64::from(stance.stance)),
            ]),
        )
    }))
    .collect()
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

#[cfg(test)]
mod tests {
    use super::*;
    use crate::scene::tests::spec;
    use eve_destiny::Vec3;

    fn ship_of(identity: NpcIdentity) -> Ship {
        let mut ship = spec();
        ship.ship_id = 4_200_000_000_000_000;
        ship.character_id = 0;
        ship.owner_id = 0;
        ship.corporation_id = 0;
        ship.name = "Ibis".to_string();
        ship.modules.clear();
        Ship::new(ship, Vec3::ZERO, Vec3::ZERO, Vec3::new(1.0, 0.0, 0.0), 0.0, crate::ship::Course::Stop, 0)
            .as_npc(identity)
    }

    /// The hull `/deathtest` spawned in `oracle/fixtures/scenarios/death.json`: a player's ship's slim item, with the ship's name and nobody's ids.
    #[test]
    fn a_hull_nobody_flies_has_the_slim_item_of_a_players_ship_as_the_reference_gave_it() {
        let entries = ship_entries(&ship_of(NpcIdentity { hull: true, ..NpcIdentity::default() }));
        let names: Vec<&str> = entries.iter().map(|(name, _)| *name).collect();
        assert_eq!(
            names,
            [
                "itemID",
                "typeID",
                "ownerID",
                "name",
                "groupID",
                "categoryID",
                "corpID",
                "allianceID",
                "warFactionID",
                "charID",
                "kills",
                "skinMaterialSetID",
                "modules",
                "securityStatus",
                "bounty"
            ]
        );
        let value = |key: &str| entries.iter().find(|(name, _)| *name == key).unwrap().1.clone();
        assert_eq!(value("itemID"), id(4_200_000_000_000_000));
        assert_eq!((value("ownerID"), value("corpID"), value("charID")), (id(0), id(0), id(0)));
        assert_eq!(value("name"), text("Ibis"));
        assert_eq!(value("modules"), list([]));
    }

    #[test]
    fn an_npc_that_is_not_a_hull_keeps_its_slim_item_without_a_name_and_with_the_limits_of_its_reactions() {
        let entries = ship_entries(&ship_of(NpcIdentity::default()));
        let names: Vec<&str> = entries.iter().map(|(name, _)| *name).collect();
        assert!(!names.contains(&"name"));
        assert_eq!(&names[names.len() - 2..], ["hostile_response_threshold", "friendly_response_threshold"]);
    }
}
