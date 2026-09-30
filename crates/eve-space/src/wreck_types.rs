//! Which wreck a ship leaves: a port of `server/src/space/wreckUtils.js` of EVE.js (`resolveShipWreckType` and what it calls), with the item registry
//! of `server/src/services/inventory/itemTypeRegistry.js` (`resolveItemByName`, `resolveItemByTypeID`) that it looks the wreck types up in (ADR 0009).
//! The rules are the reference's: the wreck is named after the ship's race and hull class (`Caldari Frigate Wreck`), with its special cases (the
//! Drifters, the titans and dreadnoughts of the pirate factions, the capsule), and the name is looked up among the item types; the registry here is
//! built from the static data, in the order of the type ids, as the reference builds its own from its tables.

use std::collections::{BTreeMap, HashMap, HashSet};

use crate::sde::{Sde, SdeError, english, i};
use crate::wreck::WreckKind;

/// The race ids of the races whose wrecks are named after them (`RACE_WRECK_PREFIX_BY_ID`).
const RACE_WRECK_PREFIX_BY_ID: [(i64, &str); 9] = [
    (1, "Caldari"),
    (2, "Minmatar"),
    (4, "Amarr"),
    (8, "Gallente"),
    (32, "Jove"),
    (64, "CONCORD"),
    (128, "ORE"),
    (256, "Triglavian"),
    (512, "EDENCOM"),
];

/// `SPECIAL_WRECK_CANDIDATE_PREFIX_BY_RACE_AND_HULL`: the prefix a race's hull class has instead of the race's name.
const SPECIAL_WRECK_CANDIDATE_PREFIX_BY_RACE_AND_HULL: [(&str, &str); 11] = [
    ("135:Dreadnought", "Triglavian"),
    ("135:Battleship", "Triglavian"),
    ("135:Battlecruiser", "Triglavian"),
    ("135:Cruiser", "Triglavian"),
    ("135:Destroyer", "Triglavian"),
    ("135:Frigate", "Triglavian"),
    ("168:Battleship", "EDENCOM"),
    ("168:Cruiser", "EDENCOM"),
    ("168:Frigate", "EDENCOM"),
    ("168:Freighter", "Upwell"),
    ("168:Hauler", "Upwell"),
];

/// `SPECIAL_WRECK_NAME_BY_SHIP_NAME`.
const SPECIAL_WRECK_NAME_BY_SHIP_NAME: [(&str, &str); 9] = [
    ("Sarathiel", "Angel Dreadnought Wreck"),
    ("Azariel", "Angel Titan Wreck"),
    ("Molok", "Blood Titan Wreck"),
    ("Chemosh", "Blood Dreadnought Wreck"),
    ("Caiman", "Guristas Dreadnought Wreck"),
    ("Vanquisher", "Serpentis Titan Wreck"),
    ("Komodo", "Guristas Titan Wreck"),
    ("Vehement", "Serpentis Dreadnought Wreck"),
    ("Vendetta", "Serpentis Supercarrier Wreck"),
];

/// `SPECIAL_WRECK_TYPE_ID_BY_SHIP_TYPE_ID`.
const SPECIAL_WRECK_TYPE_ID_BY_SHIP_TYPE_ID: [(i64, i64); 19] = [
    (34495, 34768),
    (47153, 47560),
    (47722, 47560),
    (47724, 34768),
    (37473, 37531),
    (86498, 37531),
    (47958, 47560),
    (47959, 47560),
    (47960, 47560),
    (56217, 34768),
    (56219, 34768),
    (56220, 47560),
    (56221, 47560),
    (56222, 47560),
    (87612, 34768),
    (88153, 88275),
    (88154, 88156),
    (88559, 34768),
    (88613, 88156),
];

/// `SPECIAL_WRECK_TYPE_ID_BY_SHIP_NAME`.
const SPECIAL_WRECK_TYPE_ID_BY_SHIP_NAME: [(&str, i64); 17] = [
    ("drifter battleship", 34768),
    ("drifter cruiser", 47560),
    ("drifter strike cruiser", 47560),
    ("drifter response battleship", 37531),
    ("drifter recon battleship", 37531),
    ("drifter entanglement cruiser", 47560),
    ("drifter nullwarp cruiser", 47560),
    ("drifter nullcharge cruiser", 47560),
    ("drifter strike commander", 34768),
    ("drifter polemarkos battleship", 34768),
    ("drifter raider battleship", 34768),
    ("drifter navarkos cruiser", 47560),
    ("drifter assault cruiser", 47560),
    ("drifter scout cruiser", 47560),
    ("ladon tyrannos", 34768),
    ("drifter hopilite", 88275),
    ("strategos dreadnought", 88156),
];

/// One item type, as much of it as the wreck rules read (`itemTypeRegistry` entries).
#[derive(Debug, Clone, PartialEq)]
struct ItemType {
    type_id: i64,
    name: String,
    group_name: String,
    race_id: i64,
    graphic_id: i64,
}

impl ItemType {
    fn is_wreck(&self) -> bool {
        self.group_name.trim().to_lowercase() == "wreck"
    }
}

/// `normalizeItemName` / `normalizeText`: lower case, every run of anything that is not a letter or a digit one space, trimmed.
fn normalize_item_name(value: &str) -> String {
    let mut out = String::new();
    let mut gap = false;
    for c in value.to_lowercase().chars() {
        if c.is_ascii_lowercase() || c.is_ascii_digit() {
            if gap && !out.is_empty() {
                out.push(' ');
            }
            gap = false;
            out.push(c);
        } else {
            gap = true;
        }
    }
    out
}

/// `normalizeShipNameKey`: what precedes the first letter or digit is dropped, and the ends are trimmed.
fn normalize_ship_name_key(value: &str) -> String {
    value.trim_start_matches(|c: char| !c.is_ascii_alphanumeric()).trim().to_string()
}

/// The item types the wreck rules look names and types up in.
#[derive(Debug, Default)]
pub struct WreckCatalog {
    by_type: BTreeMap<i64, ItemType>,
    by_name: HashMap<String, Vec<i64>>,
}

impl WreckCatalog {
    /// The catalog of a build of the static data: every type with a name, in the order of the ids, with its group's name, its race and its graphic.
    pub fn from_sde(sde: &Sde) -> Result<Self, SdeError> {
        let groups: HashMap<i64, String> =
            sde.table("groups")?.into_iter().map(|(key, group)| (key, english(&group["name"]))).collect();
        let mut catalog = Self::default();
        let mut types: Vec<(i64, serde_json::Value)> = sde.table("types")?.into_iter().collect();
        types.sort_by_key(|(key, _)| *key);
        for (key, record) in types {
            let name = english(&record["name"]).trim().to_string();
            if key <= 0 || name.is_empty() {
                continue;
            }
            let group_name =
                i(&record, "groupID").and_then(|group| groups.get(&group).cloned()).unwrap_or_default();
            catalog.add(ItemType {
                type_id: key,
                name,
                group_name,
                race_id: i(&record, "raceID").unwrap_or(0),
                graphic_id: i(&record, "graphicID").unwrap_or(0),
            });
        }
        Ok(catalog)
    }

    fn add(&mut self, item: ItemType) {
        let normalized = normalize_item_name(&item.name);
        if !normalized.is_empty() {
            let entries = self.by_name.entry(normalized).or_default();
            if !entries.contains(&item.type_id) {
                entries.push(item.type_id);
            }
        }
        self.by_type.insert(item.type_id, item);
    }

    /// A catalog of these types, for tests: `(type id, name, group name, race id, graphic id)`.
    pub fn of(items: impl IntoIterator<Item = (i64, &'static str, &'static str, i64, i64)>) -> Self {
        let mut catalog = Self::default();
        for (type_id, name, group_name, race_id, graphic_id) in items {
            catalog.add(ItemType {
                type_id,
                name: name.to_string(),
                group_name: group_name.to_string(),
                race_id,
                graphic_id,
            });
        }
        catalog
    }

    /// `resolveItemByName`, as far as the wreck rules use it (`resolveWreckLookupByCandidate`): the one type a name is, or, when several are (an exact name
    /// or a part of one), the first of the five the reference suggests that is a wreck of exactly that name.
    fn resolve_wreck_lookup_by_candidate(&self, candidate: &str) -> Option<&ItemType> {
        let query = normalize_item_name(candidate);
        if query.is_empty() {
            return None;
        }
        let single_or_suggested = |ids: Vec<i64>| -> Option<&ItemType> {
            match ids.as_slice() {
                [only] => self.by_type.get(only).filter(|item| item.is_wreck()),
                _ => ids.iter().take(5).filter_map(|id| self.by_type.get(id)).find(|item| {
                    item.is_wreck() && item.name.trim().to_lowercase() == candidate.trim().to_lowercase()
                }),
            }
        };
        if let Some(exact) = self.by_name.get(&query).filter(|ids| !ids.is_empty()) {
            return single_or_suggested(exact.clone());
        }
        // no exact name: the types whose name holds the query
        let partial: Vec<i64> = self
            .by_type
            .values()
            .filter(|item| normalize_item_name(&item.name).contains(&query))
            .map(|item| item.type_id)
            .collect();
        if partial.is_empty() { None } else { single_or_suggested(partial) }
    }

    /// `resolveShipWreckType`: the wreck a ship of this type leaves.
    pub fn resolve_ship_wreck_type(&self, ship_type_id: i64) -> Option<WreckKind> {
        let ship = self.by_type.get(&ship_type_id);
        let wreck = ship.and_then(|ship| {
            self.resolve_special_ship_wreck_type(ship)
                .or_else(|| self.resolve_exact_named_ship_wreck_type(ship))
                .or_else(|| {
                    build_ship_wreck_candidate_names(ship)
                        .iter()
                        .find_map(|candidate| self.resolve_wreck_lookup_by_candidate(candidate))
                })
        })?;
        Some(WreckKind {
            type_id: i32::try_from(wreck.type_id).ok()?,
            name: wreck.name.clone(),
            graphic_id: i32::try_from(wreck.graphic_id).ok().filter(|graphic| *graphic != 0),
        })
    }

    /// `resolveSpecialShipWreckType`: the wrecks named for a ship's type or name outright.
    fn resolve_special_ship_wreck_type(&self, ship: &ItemType) -> Option<&ItemType> {
        let direct = |type_id: i64| self.by_type.get(&type_id).filter(|item| item.is_wreck());
        if let Some(found) = SPECIAL_WRECK_TYPE_ID_BY_SHIP_TYPE_ID
            .iter()
            .find(|(ship_type, _)| *ship_type == ship.type_id)
            .and_then(|(_, wreck)| direct(*wreck))
        {
            return Some(found);
        }
        let name = normalize_ship_name_key(&ship.name);
        if !name.is_empty() {
            let lowered = name.to_lowercase();
            if let Some(found) = SPECIAL_WRECK_TYPE_ID_BY_SHIP_NAME
                .iter()
                .find(|(ship_name, _)| *ship_name == lowered)
                .and_then(|(_, wreck)| direct(*wreck))
            {
                return Some(found);
            }
            if let Some(wreck_name) = SPECIAL_WRECK_NAME_BY_SHIP_NAME
                .iter()
                .find(|(ship_name, _)| *ship_name == name)
                .map(|(_, wreck)| *wreck)
                && let Some(found) = self.resolve_wreck_lookup_by_candidate(wreck_name)
            {
                return Some(found);
            }
        }
        None
    }

    /// `resolveExactNamedShipWreckType`: a wreck named after the ship (`<ship> Wreck`).
    fn resolve_exact_named_ship_wreck_type(&self, ship: &ItemType) -> Option<&ItemType> {
        let name = normalize_ship_name_key(&ship.name);
        if name.is_empty() { None } else { self.resolve_wreck_lookup_by_candidate(&format!("{name} Wreck")) }
    }
}

/// `resolveShipHullClassName`: the class of hull a group is of, by its name.
fn hull_class_name(group_name: &str) -> Option<&'static str> {
    let group = group_name.trim().to_lowercase();
    if group.is_empty() {
        return None;
    }
    let has = |part: &str| group.contains(part);
    Some(if has("titan") {
        "Titan"
    } else if has("supercarrier") {
        "Supercarrier"
    } else if has("carrier") {
        "Carrier"
    } else if has("dread") {
        "Dreadnought"
    } else if has("jump freighter") || has("freighter") {
        "Freighter"
    } else if has("mining barge") || has("barge") || has("exhumer") {
        "Mining Barge"
    } else if has("industrial") || has("hauler") || has("transport ship") {
        "Hauler"
    } else if has("battleship") || has("marauder") || has("black ops") {
        "Battleship"
    } else if has("battlecruiser") || has("command ship") {
        "Battlecruiser"
    } else if has("cruiser") || has("heavy interdictor") || has("strategic cruiser") {
        "Cruiser"
    } else if has("destroyer") || has("interdictor") {
        "Destroyer"
    } else if has("shuttle") {
        "Shuttle"
    } else if has("frigate") || has("corvette") {
        "Frigate"
    } else {
        return None;
    })
}

/// `buildShipWreckCandidateNames`: the names of the wrecks a ship may leave, the likeliest first.
fn build_ship_wreck_candidate_names(ship: &ItemType) -> Vec<String> {
    let hull_class = hull_class_name(&ship.group_name);
    let race_prefix =
        RACE_WRECK_PREFIX_BY_ID.iter().find(|(race, _)| *race == ship.race_id).map(|(_, prefix)| *prefix);
    let group = ship.group_name.trim().to_lowercase();
    let mut candidates: Vec<String> = Vec::new();
    if group.contains("capsule") {
        candidates.push("Mysterious Capsule Wreck".to_string());
    }
    if let Some((_, explicit)) =
        SPECIAL_WRECK_NAME_BY_SHIP_NAME.iter().find(|(name, _)| *name == ship.name.trim())
    {
        candidates.push((*explicit).to_string());
    }
    if let Some(class) = hull_class {
        let key = format!("{}:{class}", ship.race_id);
        if let Some((_, prefix)) =
            SPECIAL_WRECK_CANDIDATE_PREFIX_BY_RACE_AND_HULL.iter().find(|(k, _)| *k == key)
        {
            candidates.push(format!("{prefix} {class} Wreck"));
        }
        if let Some(prefix) = race_prefix {
            candidates.push(format!("{prefix} {class} Wreck"));
        }
        candidates.push(format!("{class} Wreck"));
    }
    candidates.push("Wreck".to_string());
    let mut seen = HashSet::new();
    candidates.retain(|candidate| seen.insert(candidate.clone()));
    candidates
}

#[cfg(test)]
mod tests {
    use super::*;

    /// A small registry: the wrecks the rules meet and a few ships.
    fn catalog() -> WreckCatalog {
        WreckCatalog::of([
            (601, "Ibis", "Frigate", 1, 312),
            (596, "Impairor", "Frigate", 4, 312),
            (11567, "Avatar", "Titan", 4, 100),
            (23913, "Nyx", "Supercarrier", 1, 100),
            (670, "Capsule", "Capsule", 0, 100),
            (34495, "Drifter Battleship", "Battleship", 0, 100),
            (999_001, "Molok", "Titan", 128, 100),
            (999_002, "Sample Cruiser", "Heavy Assault Cruiser", 1, 100),
            (999_003, "Mystery", "Rogue Drone Thing", 0, 100),
            (26_506, "Caldari Frigate Wreck", "Wreck", 0, 3117),
            (26_484, "Amarr Frigate Wreck", "Wreck", 0, 3117),
            (26_490, "Amarr Titan Wreck", "Wreck", 0, 3100),
            (26_504, "Caldari Cruiser Wreck", "Wreck", 0, 3110),
            (26_468, "Wreck", "Wreck", 0, 3117),
            (26_469, "Wreck", "Wreck", 0, 3118),
            (26_470, "Cruiser Wreck", "Wreck", 0, 3111),
            (26_500, "Blood Titan Wreck", "Wreck", 0, 3101),
            (34_768, "Drifter Battleship Wreck", "Wreck", 0, 3200),
            (26_510, "Mysterious Capsule Wreck", "Wreck", 0, 3300),
        ])
    }

    fn kind(catalog: &WreckCatalog, ship: i64) -> Option<(i32, String, Option<i32>)> {
        catalog.resolve_ship_wreck_type(ship).map(|wreck| (wreck.type_id, wreck.name, wreck.graphic_id))
    }

    #[test]
    fn a_ship_leaves_the_wreck_of_its_race_and_hull_class() {
        let catalog = catalog();
        // recorded from the reference: an Ibis leaves a Caldari Frigate Wreck (26506, graphic 3117), an Impairor an Amarr one
        assert_eq!(kind(&catalog, 601), Some((26_506, "Caldari Frigate Wreck".to_string(), Some(3117))));
        assert_eq!(kind(&catalog, 596), Some((26_484, "Amarr Frigate Wreck".to_string(), Some(3117))));
        assert_eq!(kind(&catalog, 11_567), Some((26_490, "Amarr Titan Wreck".to_string(), Some(3100))));
    }

    #[test]
    fn a_hull_class_without_a_wreck_of_the_race_falls_back_to_the_class_and_then_to_the_plain_wreck() {
        let catalog = catalog();
        // Caldari Supercarrier Wreck is not in this registry, nor is Supercarrier Wreck: the plain "Wreck" (two types of that name: the first)
        assert_eq!(kind(&catalog, 23_913), Some((26_468, "Wreck".to_string(), Some(3117))));
        // a hull class the rules do not know is a plain wreck as well
        assert_eq!(kind(&catalog, 999_003), Some((26_468, "Wreck".to_string(), Some(3117))));
        // Caldari Cruiser Wreck exists
        assert_eq!(kind(&catalog, 999_002), Some((26_504, "Caldari Cruiser Wreck".to_string(), Some(3110))));
    }

    #[test]
    fn a_capsule_leaves_the_mysterious_capsule_wreck() {
        assert_eq!(
            kind(&catalog(), 670).map(|(_, name, _)| name),
            Some("Mysterious Capsule Wreck".to_string())
        );
    }

    #[test]
    fn the_ships_the_rules_name_leave_the_wreck_they_name() {
        let catalog = catalog();
        // by type: a Drifter Battleship -> Drifter Battleship Wreck; by name: Molok -> Blood Titan Wreck
        assert_eq!(kind(&catalog, 34_495).map(|(id, _, _)| id), Some(34_768));
        assert_eq!(kind(&catalog, 999_001).map(|(id, _, _)| id), Some(26_500));
    }

    #[test]
    fn a_type_that_is_not_there_leaves_none() {
        assert_eq!(kind(&catalog(), 12), None);
        assert_eq!(WreckCatalog::default().resolve_ship_wreck_type(601), None);
    }

    #[test]
    fn names_are_normalized_as_the_reference_does() {
        assert_eq!(normalize_item_name("  Caldari-Frigate  Wreck! "), "caldari frigate wreck");
        assert_eq!(normalize_item_name("---"), "");
        assert_eq!(normalize_ship_name_key("  #Ibis "), "Ibis");
    }

    #[test]
    fn the_hull_classes_are_told_apart_by_the_name_of_the_group() {
        for (group, class) in [
            ("Titan", "Titan"),
            ("Supercarrier", "Supercarrier"),
            ("Carrier", "Carrier"),
            ("Dreadnought", "Dreadnought"),
            ("Jump Freighter", "Freighter"),
            ("Exhumer", "Mining Barge"),
            ("Transport Ship", "Hauler"),
            ("Black Ops", "Battleship"),
            ("Command Ship", "Battlecruiser"),
            ("Heavy Interdictor", "Cruiser"),
            ("Interdictor", "Destroyer"),
            ("Shuttle", "Shuttle"),
            ("Corvette", "Frigate"),
        ] {
            assert_eq!(hull_class_name(group), Some(class), "{group}");
        }
        assert_eq!(hull_class_name(""), None);
        assert_eq!(hull_class_name("Rogue Drone Thing"), None);
    }

    #[test]
    fn the_candidates_come_in_the_order_the_reference_tries_them_without_repeats() {
        let ship = ItemType {
            type_id: 1,
            name: "Molok".into(),
            group_name: "Titan".into(),
            race_id: 4,
            graphic_id: 0,
        };
        assert_eq!(
            build_ship_wreck_candidate_names(&ship),
            ["Blood Titan Wreck", "Amarr Titan Wreck", "Titan Wreck", "Wreck"]
        );
        let triglavian = ItemType {
            type_id: 2,
            name: "X".into(),
            group_name: "Cruiser".into(),
            race_id: 135,
            graphic_id: 0,
        };
        assert_eq!(
            build_ship_wreck_candidate_names(&triglavian),
            ["Triglavian Cruiser Wreck", "Cruiser Wreck", "Wreck"]
        );
    }
}
