//! Where a ship comes out of a station.
//!
//! The SDE says where a station is, not where its ships appear. The reference's own table (`data/undock_points.json`, written by
//! `tools/dev/extract-undock-points.py` from the reference's `stationGraphicLocators`, which were made from the client's graphics) holds, for each
//! type of station, its undock points in the station model's own frame. The ship leaves at the first point of the category that suits its size,
//! facing the point's direction, both turned by the station's orientation (the reference authors one for two stations and leaves the others as
//! they are in the model).

use std::collections::HashMap;
use std::sync::OnceLock;

use eve_destiny::Vec3;

const TABLE: &str = include_str!("../data/undock_points.json");

const SMALL: &str = "undockPointSmall";
const LARGE: &str = "undockPointLarge";
const CAPITAL: &str = "undockPointCapital";
const SUPERCAPITAL: &str = "undockPointSupercapital";
/// The category of a point that suits every ship.
const GENERIC: &str = "undockPoint";

#[derive(Debug)]
struct Point {
    category: String,
    position: Vec3,
    direction: Vec3,
}

/// A place a ship comes out at: the point in the station's frame turned by the station's orientation, and the way the ship faces.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct UndockPoint {
    /// Metres from the station's centre.
    pub offset: Vec3,
    pub direction: Vec3,
}

fn vector(value: &serde_json::Value) -> Vec3 {
    let n = |i: usize| value[i].as_f64().expect("the undock table holds numbers");
    Vec3::new(n(0), n(1), n(2))
}

fn table() -> &'static HashMap<i32, Vec<Point>> {
    static POINTS: OnceLock<HashMap<i32, Vec<Point>>> = OnceLock::new();
    POINTS.get_or_init(|| {
        let root: serde_json::Value = serde_json::from_str(TABLE).expect("the undock table is JSON");
        root["stations"]
            .as_object()
            .expect("the undock table lists stations")
            .iter()
            .map(|(station_type, points)| {
                let points = points
                    .as_array()
                    .expect("a station's points are a list")
                    .iter()
                    .map(|p| Point {
                        category: p[0].as_str().expect("a category").to_string(),
                        position: vector(&p[1]),
                        direction: vector(&p[2]),
                    })
                    .collect();
                (station_type.parse().expect("a station type is a number"), points)
            })
            .collect()
    })
}

/// The kind of undock point a ship of this group needs: capitals and the big hulls come out of bigger doors. `GROUP_MAPPING` of
/// `ballparkCommon/docking.py` of the client (V24.01, `Fokiiiiiii/eve-logic`), ported as it is.
pub fn category_of(ship_group: i32) -> &'static str {
    match ship_group {
        // freighter, industrial, transport ship, blockade runner, jump freighter, battleship, elite battleship, black ops, marauder, industrial command ship
        513 | 28 | 380 | 1202 | 902 | 27 | 381 | 898 | 900 | 941 => LARGE,
        // carrier, command carrier, force auxiliary, dreadnought, lancer dreadnought, capital industrial ship
        547 | 5120 | 1538 | 485 | 4594 | 883 => CAPITAL,
        // titan, supercarrier
        30 | 659 => SUPERCAPITAL,
        _ => SMALL,
    }
}

/// The structures that only let the biggest ships out (they cannot dock there): Raitaru, Azbel, Sotiyo (`ONE_WAY_UNDOCKS_BY_STRUCTURE_TYPE`).
const ONE_WAY_UNDOCKS: [(i32, &[&str]); 3] =
    [(35825, &[CAPITAL, SUPERCAPITAL]), (35826, &[CAPITAL, SUPERCAPITAL]), (35827, &[SUPERCAPITAL])];

/// The ship groups a structure does not let dock (`EXCLUDED_DOCKS_BY_STRUCTURE_TYPE`): the Tatara takes no carriers, dreadnoughts or force auxiliaries,
/// the Insurgency FOBs no freighters.
const EXCLUDED_DOCKS: [(i32, &[i32]); 3] =
    [(35836, &[547, 5120, 1538, 485, 4594]), (78260, &[513, 902]), (79172, &[513, 902])];

/// Whether a ship of `ship_group` may dock at a station of `station_type` (`CanDock` of `ballparkCommon/docking.py`): the type needs a door for the ship's size,
/// does not exclude the group, and is not one that only lets that size out. None when the table does not know the type (the game would then
/// say no; this table holds only the types the reference knows, and saying no to the rest would shut ships out of stations that have doors).
pub fn can_dock(station_type: i32, ship_group: i32) -> Option<bool> {
    let points = table().get(&station_type)?;
    let wanted = category_of(ship_group);
    if !points.iter().any(|p| p.category == wanted) {
        return Some(false);
    }
    if EXCLUDED_DOCKS.iter().any(|(t, groups)| *t == station_type && groups.contains(&ship_group)) {
        return Some(false);
    }
    Some(!ONE_WAY_UNDOCKS.iter().any(|(t, categories)| *t == station_type && categories.contains(&wanted)))
}

/// A vector turned by yaw, pitch and roll in degrees (the orientation the client's `dunRotation` gives).
pub fn rotate(vector: Vec3, angles: [f64; 3]) -> Vec3 {
    let [yaw, pitch, roll] = angles.map(|a| a.to_radians() / 2.0);
    let (cy, sy) = (yaw.cos(), yaw.sin());
    let (cp, sp) = (pitch.cos(), pitch.sin());
    let (cr, sr) = (roll.cos(), roll.sin());
    let w = cy * cp * cr + sy * sp * sr;
    let (x, y, z) = (cy * sp * cr + sy * cp * sr, sy * cp * cr - cy * sp * sr, cy * cp * sr - sy * sp * cr);
    let t = Vec3::new(
        2.0 * (y * vector.z - z * vector.y),
        2.0 * (z * vector.x - x * vector.z),
        2.0 * (x * vector.y - y * vector.x),
    );
    Vec3::new(
        vector.x + w * t.x + (y * t.z - z * t.y),
        vector.y + w * t.y + (z * t.x - x * t.z),
        vector.z + w * t.z + (x * t.y - y * t.x),
    )
}

/// The first point of the wanted category; failing that the first generic one; failing that the first of all.
fn suiting<'a>(points: &'a [Point], wanted: &str) -> Option<&'a Point> {
    let of = |category: &str| points.iter().find(|p| p.category == category);
    of(wanted).or_else(|| of(GENERIC)).or_else(|| points.first())
}

/// The place a ship of `ship_group` comes out of a station of `station_type` that is turned by `rotation`, or None when the table does not know
/// the type. The points of the ship's category come first; when there are none the generic points, and when there are none all of them.
pub fn undock_point(station_type: i32, ship_group: i32, rotation: [f64; 3]) -> Option<UndockPoint> {
    let point = suiting(table().get(&station_type)?, category_of(ship_group))?;
    Some(UndockPoint {
        offset: rotate(point.position, rotation),
        direction: rotate(point.direction, rotation),
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    fn near(a: Vec3, b: Vec3) -> bool {
        (a.x - b.x).abs() < 1e-6 && (a.y - b.y).abs() < 1e-6 && (a.z - b.z).abs() < 1e-6
    }

    #[test]
    fn every_type_of_the_table_has_a_point_and_the_table_knows_the_stations_types() {
        assert_eq!(table().len(), 44);
        assert!(table().values().all(|points| !points.is_empty()));
        assert!(table().contains_key(&92885), "the station of the recorded undock");
    }

    #[test]
    fn a_ship_group_needs_the_kind_of_point_its_size_does() {
        assert_eq!(category_of(237), SMALL, "a rookie ship");
        assert_eq!(category_of(25), SMALL, "a frigate");
        assert_eq!(category_of(27), LARGE, "a battleship");
        assert_eq!(category_of(485), CAPITAL, "a dreadnought");
        assert_eq!(category_of(659), SUPERCAPITAL, "a supercarrier");
    }

    #[test]
    fn every_group_the_client_maps_to_a_door_is_mapped_to_the_one_it_says() {
        // the client's `GROUP_MAPPING`, group by group (the ids are the client's `inventorycommon.const`)
        for (group, category) in [
            (513, LARGE),
            (28, LARGE),
            (380, LARGE),
            (1202, LARGE),
            (902, LARGE),
            (27, LARGE),
            (381, LARGE),
            (898, LARGE),
            (900, LARGE),
            (941, LARGE),
            (547, CAPITAL),
            (5120, CAPITAL),
            (1538, CAPITAL),
            (485, CAPITAL),
            (4594, CAPITAL),
            (883, CAPITAL),
            (30, SUPERCAPITAL),
            (659, SUPERCAPITAL),
            (25, SMALL),
            (237, SMALL),
        ] {
            assert_eq!(category_of(group), category, "group {group}");
        }
    }

    #[test]
    fn a_ship_docks_where_the_station_has_a_door_for_its_size_and_the_structure_lets_it() {
        // a station with a single generic point has no door of a size: the client's rule says none of its own kind is there
        assert_eq!(can_dock(54, 485), Some(false));
        // AIR Trade Center has doors of the two smaller sizes
        assert_eq!(can_dock(92885, 237), Some(true));
        assert_eq!(can_dock(92885, 27), Some(true));
        assert_eq!(can_dock(1, 237), None, "a type the table does not know");
    }

    #[test]
    fn the_structures_that_turn_ships_away_or_only_let_them_out_do_it_for_the_groups_the_client_names() {
        // not in the table, so exercised through the lists
        assert!(
            EXCLUDED_DOCKS.iter().any(|(t, g)| *t == 35836 && g.contains(&5120)),
            "a Tatara takes no command carriers"
        );
        assert!(
            ONE_WAY_UNDOCKS
                .iter()
                .any(|(t, c)| *t == 35827 && c.contains(&SUPERCAPITAL) && !c.contains(&CAPITAL))
        );
        assert!(ONE_WAY_UNDOCKS.iter().any(|(t, c)| *t == 35825 && c.contains(&CAPITAL)));
    }

    #[test]
    fn the_first_point_of_the_ships_category_is_the_one_used_and_the_others_give_way_to_it() {
        // AIR Trade Center: the first small point is the one the reference's recorded ship came out at
        let small = undock_point(92885, 237, [0.0; 3]).unwrap();
        assert!(near(small.offset, Vec3::new(-1220.84558, 2234.68237, 23089.24609)), "{:?}", small.offset);
        assert_eq!(small.direction, Vec3::new(0.0, 0.0, 1.0));
        let large = undock_point(92885, 27, [0.0; 3]).unwrap();
        assert!(near(large.offset, Vec3::new(-61.2897, 2849.74243, 22551.12305)), "{:?}", large.offset);
        // a station with a single generic point serves every size
        let generic = undock_point(54, 485, [0.0; 3]).unwrap();
        assert!(near(generic.offset, Vec3::new(36.0, 6854.26221, 7948.4668)));
        assert_eq!(undock_point(1, 237, [0.0; 3]), None, "a type the table does not know");
    }

    #[test]
    fn a_point_of_another_size_is_taken_only_when_there_is_none_of_the_size_or_a_generic_one() {
        let point = |category: &str, x: f64| Point {
            category: category.to_string(),
            position: Vec3::new(x, 0.0, 0.0),
            direction: Vec3::new(0.0, 0.0, 1.0),
        };
        let x_of = |points: &[Point], wanted: &str| suiting(points, wanted).map(|p| p.position.x);
        let all = [point(SMALL, 1.0), point(GENERIC, 2.0), point(LARGE, 3.0), point(LARGE, 4.0)];
        assert_eq!(x_of(&all, LARGE), Some(3.0), "the first of the size");
        assert_eq!(x_of(&all, SMALL), Some(1.0));
        assert_eq!(x_of(&all, CAPITAL), Some(2.0), "the generic one serves a size there is none of");
        let sized = [point(SMALL, 1.0), point(LARGE, 3.0)];
        assert_eq!(x_of(&sized, CAPITAL), Some(1.0), "with no generic one, the first of all");
        assert_eq!(x_of(&[], SMALL), None);
    }

    #[test]
    fn a_station_turned_about_the_vertical_axis_turns_where_ships_come_out() {
        // 305 degrees of yaw: the recorded station's orientation, and the direction the recorded ship faced
        let turned = undock_point(92885, 237, [305.0, 0.0, 0.0]).unwrap();
        assert!(
            near(turned.offset, Vec3::new(-19613.851392826436, 2234.68237, 12243.389337716348)),
            "{:?}",
            turned.offset
        );
        assert!((turned.direction.x + 0.8191520442889917).abs() < 1e-9 && turned.direction.y.abs() < 1e-12);
        assert!((turned.direction.z - 0.5735764363510462).abs() < 1e-9);
    }

    #[test]
    fn rotating_by_nothing_changes_nothing_and_a_quarter_turn_takes_forward_to_the_side() {
        let v = Vec3::new(3.0, 4.0, 5.0);
        assert!(near(rotate(v, [0.0; 3]), v));
        assert!(near(rotate(Vec3::new(0.0, 0.0, 1.0), [90.0, 0.0, 0.0]), Vec3::new(1.0, 0.0, 0.0)));
        // pitch turns about the side axis: forward goes down, roll about forward leaves it be
        assert!(near(rotate(Vec3::new(0.0, 0.0, 1.0), [0.0, 90.0, 0.0]), Vec3::new(0.0, -1.0, 0.0)));
        assert!(near(rotate(Vec3::new(0.0, 0.0, 1.0), [0.0, 0.0, 90.0]), Vec3::new(0.0, 0.0, 1.0)));
    }
}
