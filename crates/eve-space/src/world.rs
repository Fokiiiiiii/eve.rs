//! A solar system's stations and celestials, built from the static data: what a ship undocking finds around it.

use std::collections::HashMap;

use eve_destiny::Vec3;
use serde_json::Value;

use crate::names;
use crate::sde::{Sde, SdeError, english, f, i};
use crate::security;

/// What kind of body a ball stands for. The kind decides its flags and how it is named.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum BodyKind {
    Station,
    Planet,
    Moon,
    Belt,
    Star,
    Stargate,
    /// An Upwell structure the host put in space (protocol 59): a body of the system, shown with the state the host gives it.
    Structure,
    /// A wormhole end the host puts in space (protocol 63): a body of its system whose far end is in another system.
    Wormhole,
}

/// The category of a structure's item (`categoryID` 65 in the client).
pub const STRUCTURE_CATEGORY: i32 = 65;
/// The category of an orbital (a customs office of a planet, categoryOrbital 46 in the client's constants).
pub const ORBITAL_CATEGORY: i32 = 46;

/// The slim fields of a structure, as the host has them: the sim shows them and decides nothing about them (ADR 0012, rule 3). A timer is
/// `(start, end, paused)` in filetime; `repairing` is `None` when the client should show no repair at all.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct StructureStatus {
    pub state: i32,
    pub upkeep_state: i32,
    pub timer: Option<(i64, i64, Option<i64>)>,
    pub unanchoring: Option<i64>,
    pub repairing: Option<bool>,
}

/// What only some bodies have.
#[derive(Debug, Clone, PartialEq)]
pub enum Detail {
    None,
    Station {
        corporation_id: i64,
    },
    Structure {
        corporation_id: i64,
        alliance_id: i64,
        status: StructureStatus,
        /// The planet of a customs office (0 for an Upwell structure): see `StructurePlacement::planet_id`.
        planet_id: i64,
    },
    Stargate {
        destination_system: i32,
        destination_gate: i64,
        destination_name: String,
        destination_security: f64,
        destination_faction: Option<i64>,
        /// Whether the jump leads to less safe space (the client warns before it).
        hazardous: bool,
        /// The gate's rotation as the slim item carries it (yaw, pitch, roll).
        facing: Option<[f64; 3]>,
        /// The way the gate faces: the unit vector from its system to the one it leads to (what ships that jump appear in front of).
        heading: Option<Vec3>,
    },
    /// A wormhole end: the far end it leads to (its system, body ID, the class of that system, its position and radius) and the largest ship it takes.
    Wormhole {
        destination_system: i32,
        destination_end: i64,
        destination_class: i32,
        destination_position: Vec3,
        destination_radius: f64,
        max_jump_mass: f64,
    },
}

/// A body of a solar system: a rigid ball with the identity the client shows for it.
#[derive(Debug, Clone, PartialEq)]
pub struct Body {
    pub id: i64,
    pub type_id: i32,
    pub group_id: i32,
    pub category_id: i32,
    pub owner_id: i64,
    pub name: String,
    pub kind: BodyKind,
    pub position: Vec3,
    pub radius: f64,
    pub graphic_id: Option<i64>,
    pub detail: Detail,
}

/// A solar system with everything in it that does not move.
#[derive(Debug, Clone)]
pub struct SolarSystem {
    pub id: i32,
    pub name: String,
    pub constellation_id: i32,
    pub region_id: i32,
    pub faction_id: Option<i64>,
    pub security: f64,
    /// The bodies in the order the client is sent them: stations, then the celestials (planets, moons, belts and the star) by id, then the stargates.
    pub bodies: Vec<Body>,
}

/// The unit vector from one system's position to another's (plain double arithmetic, as the reference's `normalizeVector` does it).
fn heading(from: Vec3, to: Vec3) -> Option<Vec3> {
    let (x, y, z) = (to.x - from.x, to.y - from.y, to.z - from.z);
    let length = (x * x + y * y + z * z).sqrt();
    (length.is_finite() && length > 0.0).then(|| Vec3::new(x / length, y / length, z / length))
}

impl SolarSystem {
    pub fn body(&self, id: i64) -> Option<&Body> {
        self.bodies.iter().find(|b| b.id == id)
    }

    pub fn stations(&self) -> impl Iterator<Item = &Body> {
        self.bodies.iter().filter(|b| b.kind == BodyKind::Station)
    }
}

#[derive(Debug, Clone)]
struct TypeInfo {
    group_id: i32,
    radius: f64,
    graphic_id: Option<i64>,
}

#[derive(Debug, Clone)]
struct SystemInfo {
    name: String,
    position: Vec3,
    constellation_id: i32,
    region_id: i32,
    faction_id: Option<i64>,
    security: f64,
}

/// The tables every system is built from, read once.
pub struct World {
    sde: Sde,
    types: HashMap<i64, TypeInfo>,
    category_of_group: HashMap<i64, i32>,
    systems: HashMap<i64, SystemInfo>,
    corporations: HashMap<i64, String>,
    operations: HashMap<i64, String>,
    wrecks: std::sync::Arc<crate::wreck_types::WreckCatalog>,
}

impl World {
    pub fn load(sde: Sde) -> Result<Self, SdeError> {
        let mut types = HashMap::new();
        sde.scan("types", None, |key, record| {
            if let Some(group_id) = i(record, "groupID") {
                types.insert(
                    key,
                    TypeInfo {
                        group_id: group_id as i32,
                        radius: f(record, "radius").unwrap_or(0.0),
                        graphic_id: i(record, "graphicID"),
                    },
                );
            }
        })?;
        let mut category_of_group = HashMap::new();
        sde.scan("groups", None, |key, record| {
            if let Some(category) = i(record, "categoryID") {
                category_of_group.insert(key, category as i32);
            }
        })?;
        // a system without a faction of its own belongs to its constellation's, and that to its region's
        let mut constellation_factions = HashMap::new();
        sde.scan("mapConstellations", None, |key, record| {
            if let Some(faction) = i(record, "factionID") {
                constellation_factions.insert(key, faction);
            }
        })?;
        let mut region_factions = HashMap::new();
        sde.scan("mapRegions", None, |key, record| {
            if let Some(faction) = i(record, "factionID") {
                region_factions.insert(key, faction);
            }
        })?;
        let mut systems = HashMap::new();
        sde.scan("mapSolarSystems", None, |key, record| {
            let constellation = i(record, "constellationID").unwrap_or(0);
            let region = i(record, "regionID").unwrap_or(0);
            let faction = i(record, "factionID")
                .or_else(|| constellation_factions.get(&constellation).copied())
                .or_else(|| region_factions.get(&region).copied())
                .filter(|faction| *faction > 0);
            systems.insert(
                key,
                SystemInfo {
                    name: record.get("name").map(english).unwrap_or_default(),
                    position: Self::position(record),
                    constellation_id: i(record, "constellationID").unwrap_or(0) as i32,
                    region_id: i(record, "regionID").unwrap_or(0) as i32,
                    faction_id: faction,
                    security: f(record, "securityStatus").unwrap_or(0.0),
                },
            );
        })?;
        let mut corporations = HashMap::new();
        sde.scan("npcCorporations", None, |key, record| {
            corporations.insert(key, record.get("name").map(english).unwrap_or_default());
        })?;
        let mut operations = HashMap::new();
        sde.scan("stationOperations", None, |key, record| {
            operations.insert(key, record.get("operationName").map(english).unwrap_or_default());
        })?;
        let wrecks = std::sync::Arc::new(crate::wreck_types::WreckCatalog::from_sde(&sde)?);
        Ok(Self { sde, types, category_of_group, systems, corporations, operations, wrecks })
    }

    /// The item types the wreck rules look up (`crate::wreck_types`).
    pub fn wreck_catalog(&self) -> std::sync::Arc<crate::wreck_types::WreckCatalog> {
        std::sync::Arc::clone(&self.wrecks)
    }

    fn type_info(&self, type_id: i64) -> Option<&TypeInfo> {
        self.types.get(&type_id)
    }

    fn category(&self, group_id: i32) -> i32 {
        self.category_of_group.get(&i64::from(group_id)).copied().unwrap_or(0)
    }

    pub fn system_name(&self, system_id: i64) -> Option<&str> {
        self.systems.get(&system_id).map(|s| s.name.as_str())
    }

    fn position(record: &Value) -> Vec3 {
        let p = record.get("position");
        let axis = |name: &str| p.and_then(|p| p.get(name)).and_then(Value::as_f64).unwrap_or(0.0);
        Vec3::new(axis("x"), axis("y"), axis("z"))
    }

    /// Builds one system: its stations, celestials and stargates.
    pub fn system(&self, system_id: i32) -> Result<SolarSystem, SdeError> {
        let info = self.systems.get(&i64::from(system_id)).ok_or(SdeError::UnknownSystem(system_id))?;
        let needle = format!("\"solarSystemID\": {system_id}");
        let in_system = |record: &Value| i(record, "solarSystemID") == Some(i64::from(system_id));

        let mut stations = Vec::new();
        self.sde.scan("npcStations", Some(&needle), |key, record| {
            if !in_system(record) {
                return;
            }
            let type_id = i(record, "typeID").unwrap_or(0);
            let (group_id, radius, graphic_id) =
                self.type_info(type_id).map_or((0, 0.0, None), |t| (t.group_id, t.radius, t.graphic_id));
            let owner = i(record, "ownerID").unwrap_or(0);
            let operation =
                i(record, "operationID").and_then(|o| self.operations.get(&o)).map(String::as_str);
            let use_operation = record.get("useOperationName").and_then(Value::as_bool).unwrap_or(false);
            let name = names::station(
                &info.name,
                i(record, "celestialIndex").unwrap_or(0) as i32,
                i(record, "orbitIndex").map(|m| m as i32),
                self.corporations.get(&owner).map_or("", String::as_str),
                if use_operation { operation } else { None },
            );
            stations.push(Body {
                id: key,
                type_id: type_id as i32,
                group_id,
                category_id: self.category(group_id),
                owner_id: owner,
                name,
                kind: BodyKind::Station,
                position: Self::position(record),
                radius,
                graphic_id,
                detail: Detail::Station { corporation_id: owner },
            });
        })?;
        stations.sort_by_key(|b| b.id);

        // planets first: moons and belts are named for their planet's number
        let mut celestials = Vec::new();
        for (table, kind) in [
            ("mapPlanets", BodyKind::Planet),
            ("mapMoons", BodyKind::Moon),
            ("mapAsteroidBelts", BodyKind::Belt),
            ("mapStars", BodyKind::Star),
        ] {
            self.sde.scan(table, Some(&needle), |key, record| {
                if !in_system(record) {
                    return;
                }
                let type_id = i(record, "typeID").unwrap_or(0);
                let type_info = self.type_info(type_id);
                let group_id = type_info.map_or(0, |t| t.group_id);
                let celestial_index = i(record, "celestialIndex").unwrap_or(0) as i32;
                let orbit_index = i(record, "orbitIndex").unwrap_or(0) as i32;
                let name = match kind {
                    BodyKind::Planet => names::planet(&info.name, celestial_index),
                    BodyKind::Moon => names::moon(&info.name, celestial_index, orbit_index),
                    BodyKind::Belt => names::belt(&info.name, celestial_index, orbit_index),
                    _ => names::star(&info.name),
                };
                celestials.push(Body {
                    id: key,
                    type_id: type_id as i32,
                    group_id,
                    category_id: self.category(group_id),
                    owner_id: 1,
                    name,
                    kind,
                    position: Self::position(record),
                    radius: f(record, "radius").or(type_info.map(|t| t.radius)).unwrap_or(0.0),
                    graphic_id: type_info.and_then(|t| t.graphic_id),
                    detail: Detail::None,
                });
            })?;
        }
        celestials.sort_by_key(|b| b.id);

        let mut gates = Vec::new();
        self.sde.scan("mapStargates", Some(&needle), |key, record| {
            if !in_system(record) {
                return;
            }
            let type_id = i(record, "typeID").unwrap_or(0);
            let type_info = self.type_info(type_id);
            let group_id = type_info.map_or(0, |t| t.group_id);
            let destination = record.get("destination");
            let destination_system = destination.and_then(|d| i(d, "solarSystemID")).unwrap_or(0);
            let destination_info = self.systems.get(&destination_system);
            gates.push(Body {
                id: key,
                type_id: type_id as i32,
                group_id,
                category_id: self.category(group_id),
                owner_id: info.faction_id.unwrap_or(1),
                name: names::stargate(destination_info.map_or("", |d| d.name.as_str())),
                kind: BodyKind::Stargate,
                position: Self::position(record),
                radius: type_info.map_or(0.0, |t| t.radius),
                graphic_id: type_info.and_then(|t| t.graphic_id),
                detail: Detail::Stargate {
                    destination_system: destination_system as i32,
                    destination_gate: destination.and_then(|d| i(d, "stargateID")).unwrap_or(0),
                    destination_name: destination_info.map(|d| d.name.clone()).unwrap_or_default(),
                    destination_security: destination_info.map_or(0.0, |d| d.security),
                    destination_faction: destination_info.and_then(|d| d.faction_id),
                    hazardous: destination_info
                        .is_some_and(|d| security::hazardous_transition(info.security, d.security)),
                    facing: destination_info
                        .and_then(|d| crate::geo::stargate_facing(info.position, d.position)),
                    heading: destination_info.and_then(|d| heading(info.position, d.position)),
                },
            });
        })?;
        gates.sort_by_key(|b| b.id);

        let mut bodies = stations;
        bodies.extend(celestials);
        bodies.extend(gates);
        Ok(SolarSystem {
            id: system_id,
            name: info.name.clone(),
            constellation_id: info.constellation_id,
            region_id: info.region_id,
            faction_id: info.faction_id,
            security: info.security,
            bodies,
        })
    }
}
