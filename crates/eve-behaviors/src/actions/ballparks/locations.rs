//! `behaviors/actions/ballparks.py`, the part that finds a place to go: a place in deep space, near a planet, or the place of something worth
//! exploring.

use crate::blackboards::{Address, ScopeType};
use crate::consts::{AU, GROUP_STARGATE, GROUP_STATION};
use crate::tree::{Task, TaskCtx};
use crate::utility::ballparks::get_ball;
use crate::utility::ballparks::get_my_ball;
use crate::utility::inventory::get_inventory_item;
use crate::utility::owner::is_eve_player_character;
use crate::utility::security::{SECURITY_CLASS_ZERO_SEC, get_local_security_level};
use crate::value::Value;

use super::util::in_collection;

/// `UNSAFE_EXPLORATION_GROUPS`.
const UNSAFE_EXPLORATION_GROUPS: [i64; 2] = [GROUP_STARGATE, GROUP_STATION];

/// The deep space placements are not nearer to anything than this (`minPlacementDistance=500`).
const MIN_PLACEMENT_DISTANCE: f64 = 500.0;

/// `ADDITIONAL_EXPLORATION_GROUPS`.
fn additional_exploration_groups() -> Address {
    Address::new(ScopeType::EntityGroup, "ADDITIONAL_EXPLORATION_GROUPS")
}

/// `ADDITIONAL_EXPLORATION_ACTION`.
fn additional_exploration_action() -> Address {
    Address::new(ScopeType::EntityGroup, "ADDITIONAL_EXPLORATION_ACTION")
}

enter_task!(
    /// A place in deep space.
    GetRandomDeepSpacePosition,
    |t| {
        t.set_status_to_failed();
        let solar_system_id = t.world().solar_system_id();
        let location = t.world().random_deep_space_location(solar_system_id, Some(MIN_PLACEMENT_DISTANCE));
        let Some(address) = t.required_address("locationAddress") else { return };
        t.send_blackboard_value(&address, Value::from_vec3(location));
        t.set_status_to_success();
    }
);

enter_task!(
    /// A place near a random planet, between two distances (in AU).
    GetRandomPositionNearRandomPlanet,
    |t| {
        t.set_status_to_failed();
        let (Some(min_distance_au), Some(max_distance_au)) =
            (t.required_float("minDistanceAU"), t.required_float("maxDistanceAU"))
        else {
            return;
        };
        let solar_system_id = t.world().solar_system_id();
        let location =
            t.world().random_placement_near_random_planet(solar_system_id, min_distance_au * AU, max_distance_au * AU);
        if let Some(location) = location {
            let Some(address) = t.required_address("locationAddress") else { return };
            t.send_blackboard_value(&address, Value::from_vec3(location));
            t.set_status_to_success();
        }
    }
);

enter_task!(
    /// A place near a planet, between 1000 and 10000 m, to flee to.
    FindPointToEscapeWarpTo,
    |t| {
        let solar_system_id = t.world().solar_system_id();
        let location = t.world().random_placement_near_random_planet(solar_system_id, 1000.0, 10000.0);
        match location {
            Some(location) => {
                let Some(address) = t.required_address("locationMessage") else { return };
                t.send_blackboard_value(&address, Value::from_vec3(location));
                t.set_status_to_success();
            }
            None => t.set_status_to_failed(),
        }
    }
);

/// The place of something worth exploring: the group's balls of interest, the warp beacons and the entrances of dungeons of interest, and among them
/// one that is not in my bubble. The balls are ids; the client makes a set of the ones of the lists, which has no order, and here they are in the
/// order they were found.
#[derive(Debug, Default)]
pub struct FindPointToExplore;

impl FindPointToExplore {
    /// `getattr`-like reading of an optional list attribute (`[]` where there is none).
    fn optional(t: &mut TaskCtx<'_, '_>, name: &str) -> Value {
        if t.has_attribute(name) { t.attribute(name) } else { Value::List(Vec::new()) }
    }

    /// `_is_safe_to_visit`: a station or a gate is only for a player, or in null security space, when the task asks to avoid the unsafe ones.
    fn is_safe_to_visit(t: &mut TaskCtx<'_, '_>, group_id: i64, owner_id: i64) -> Option<bool> {
        if !t.has_attribute("avoidUnsafeLocationsInEmpireSpace")
            || !t.attribute("avoidUnsafeLocationsInEmpireSpace").truthy()
        {
            return Some(true);
        }
        if !UNSAFE_EXPLORATION_GROUPS.contains(&group_id) {
            return Some(true);
        }
        if is_eve_player_character(t, Some(owner_id)) {
            return Some(true);
        }
        Some(get_local_security_level(t)? == SECURITY_CLASS_ZERO_SEC)
    }

    /// `_GetBallsOfInterestFromGlobals`.
    fn get_balls_of_interest_from_globals(t: &mut TaskCtx<'_, '_>, groups: &Value) -> Option<Vec<i64>> {
        let groups = if groups.truthy() { groups.clone() } else { Value::List(Vec::new()) };
        let categories = Self::optional(t, "categoriesOfInterest");
        if !groups.truthy() && !categories.truthy() {
            return Some(Vec::new());
        }
        let mut safe_balls_of_interest = Vec::new();
        for ball_id in t.world().global_ball_ids() {
            let Some(item) = get_inventory_item(t, ball_id) else {
                t.raise(crate::tree::BehaviorError::Task(
                    "AttributeError: 'NoneType' object has no attribute 'groupID'".to_string(),
                ));
                return None;
            };
            let group_id = Self::type_group(t, item.type_id)?;
            let of_interest = if in_collection(t, group_id, &groups)? {
                true
            } else {
                let category_id = Self::type_category(t, item.type_id)?;
                in_collection(t, category_id, &categories)?
            };
            if of_interest && Self::is_safe_to_visit(t, group_id, item.owner_id)? {
                safe_balls_of_interest.push(ball_id);
            }
        }
        Some(safe_balls_of_interest)
    }

    fn type_group(t: &mut TaskCtx<'_, '_>, type_id: i64) -> Option<i64> {
        let group = t.world().type_group_id(type_id);
        if group.is_none() {
            t.raise(crate::env::WorldError::Key(type_id.to_string()).into());
        }
        group
    }

    fn type_category(t: &mut TaskCtx<'_, '_>, type_id: i64) -> Option<i64> {
        let category = t.world().type_category_id(type_id);
        if category.is_none() {
            t.raise(crate::env::WorldError::Key(type_id.to_string()).into());
        }
        category
    }

    /// `_GetExplorationBallsFromDistribution`.
    fn get_exploration_balls_from_distribution(t: &mut TaskCtx<'_, '_>) -> Option<Vec<i64>> {
        let groups = t.get_last_blackboard_value(&additional_exploration_groups());
        Self::get_balls_of_interest_from_globals(t, &groups)
    }

    /// `_GetWarpBeaconsInSystem`.
    fn get_warp_beacons_in_system(t: &mut TaskCtx<'_, '_>) -> Option<Vec<i64>> {
        let mut balls = Vec::new();
        for beacon_id in t.world().npc_warp_beacon_item_ids() {
            balls.push(get_ball(t, beacon_id)?.id);
        }
        Some(balls)
    }

    /// `_GetDungeonArchetypeEntryPointsOfInterest`.
    fn get_dungeon_archetype_entry_points_of_interest(t: &mut TaskCtx<'_, '_>) -> Option<Vec<i64>> {
        let archetypes = Self::optional(t, "dungeonArchetypesOfInterest");
        if !archetypes.truthy() {
            return Some(Vec::new());
        }
        let installed = t.required("shouldConsiderUnspawnedDungeons")?.truthy();
        let solar_system_id = t.ctx().solar_system_id;
        Some(t.world().dungeon_entry_objects_of_archetype(solar_system_id, &archetypes, installed))
    }

    /// `_GetSpecificDungeonEntryPointsOfInterest`.
    fn get_specific_dungeon_entry_points_of_interest(t: &mut TaskCtx<'_, '_>) -> Option<Vec<i64>> {
        let dungeons = Self::optional(t, "dungeonsOfInterest");
        if !dungeons.truthy() {
            return Some(Vec::new());
        }
        let installed = t.required("shouldConsiderUnspawnedDungeons")?.truthy();
        let solar_system_id = t.ctx().solar_system_id;
        Some(t.world().dungeon_entry_objects_of_dungeon_id(solar_system_id, &dungeons, installed))
    }

    /// `GetGeneralList`.
    fn get_general_list(t: &mut TaskCtx<'_, '_>) -> Option<Vec<i64>> {
        let distribution_overwrite = t.get_last_blackboard_value(&additional_exploration_action());
        if distribution_overwrite.truthy() {
            return Self::get_exploration_balls_from_distribution(t);
        }
        let groups_of_interest = t.required("groupsOfInterest")?;
        let mut list = Self::get_balls_of_interest_from_globals(t, &groups_of_interest)?;
        list.extend(Self::get_exploration_balls_from_distribution(t)?);
        list.extend(Self::get_warp_beacons_in_system(t)?);
        list.extend(Self::get_dungeon_archetype_entry_points_of_interest(t)?);
        let mut seen = Vec::new();
        for ball_id in list {
            if !seen.contains(&ball_id) {
                seen.push(ball_id);
            }
        }
        Some(seen)
    }

    /// `_GetPointToExplore`: one of the objects that is not in my bubble, or after 10 tries the last one there was.
    fn get_point_to_explore(t: &mut TaskCtx<'_, '_>, warp_object_list: &[i64]) -> Option<Option<[f64; 3]>> {
        if warp_object_list.is_empty() {
            return Some(None);
        }
        let my_ball = get_my_ball(t)?;
        let mut object_to_warp_to = None;
        let mut has_no_valid_location = true;
        let mut count = 0;
        while has_no_valid_location && count < 10 {
            let object = warp_object_list[t.world().random_index(warp_object_list.len())];
            has_no_valid_location = t.world().in_same_bubble(object, my_ball.id);
            object_to_warp_to = Some(object);
            count += 1;
        }
        Some(object_to_warp_to.map(|object| t.world().warp_destination_point(my_ball.id, object)))
    }

    /// `SelectLocation`.
    fn select_location(t: &mut TaskCtx<'_, '_>) -> Option<Option<[f64; 3]>> {
        let general_list = Self::get_general_list(t)?;
        let specific_list = Self::get_specific_dungeon_entry_points_of_interest(t)?;
        let pick_specific = if specific_list.is_empty() {
            false
        } else if general_list.is_empty() {
            true
        } else {
            let chance = t.required_float("pickSpecificChance")?;
            t.world().random() < chance
        };
        Self::get_point_to_explore(t, if pick_specific { &specific_list } else { &general_list })
    }
}

impl Task for FindPointToExplore {
    fn type_name(&self) -> &'static str {
        "FindPointToExplore"
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        let Some(location) = Self::select_location(t) else { return };
        match location {
            Some(location) => {
                let Some(address) = t.required_address("locationMessage") else { return };
                t.send_blackboard_value(&address, Value::from_vec3(location));
                t.set_status_to_success();
            }
            None => t.set_status_to_failed(),
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::env::InventoryItem;
    use crate::tree::tests::Rig;
    use crate::tree::{Attrs, BehaviorError, Status};

    fn item(name: &str) -> Value {
        Address::new(ScopeType::Item, name).to_value()
    }

    fn ints(ids: &[i64]) -> Value {
        Value::List(ids.iter().map(|&id| Value::Int(id)).collect())
    }

    #[test]
    fn a_place_in_deep_space_is_asked_of_the_solar_system_with_the_distance_from_things() {
        let mut rig = Rig::new();
        rig.world.solar_system = Some(30000142);
        rig.world.deep_space_location = [1.0, 2.0, 3.0];
        let attrs = Attrs::new().with("locationAddress", item("Where"));
        assert_eq!(rig.run(Box::new(GetRandomDeepSpacePosition), attrs), Status::Success);
        assert_eq!(rig.last(ScopeType::Item, "Where"), Value::from_vec3([1.0, 2.0, 3.0]));
        assert_eq!(rig.world.take_calls(), ["random_deep_space_location(Some(30000142), Some(500.0))"]);
    }

    #[test]
    fn a_place_near_a_planet_is_asked_for_in_metres_and_no_place_is_a_failure() {
        let attrs = || {
            Attrs::new()
                .with("locationAddress", item("Where"))
                .with("minDistanceAU", 0.5)
                .with("maxDistanceAU", 2.0)
        };
        let mut rig = Rig::new();
        rig.world.solar_system = Some(30000142);
        rig.world.random_planet_location = Some([4.0, 5.0, 6.0]);
        assert_eq!(rig.run(Box::new(GetRandomPositionNearRandomPlanet), attrs()), Status::Success);
        assert_eq!(rig.last(ScopeType::Item, "Where"), Value::from_vec3([4.0, 5.0, 6.0]));
        assert_eq!(
            rig.world.take_calls(),
            [format!("random_placement_near_random_planet(Some(30000142), {}, {})", 0.5 * AU, 2.0 * AU)]
        );
        let mut rig = Rig::new();
        assert_eq!(rig.run(Box::new(GetRandomPositionNearRandomPlanet), attrs()), Status::Failure);
        assert_eq!(rig.messages_on("Where"), 0);
    }

    #[test]
    fn a_place_to_escape_to_is_near_a_planet_between_a_thousand_and_ten_thousand_metres() {
        let attrs = || Attrs::new().with("locationMessage", item("Where"));
        let mut rig = Rig::new();
        rig.world.solar_system = Some(7);
        rig.world.random_planet_location = Some([4.0, 5.0, 6.0]);
        assert_eq!(rig.run(Box::new(FindPointToEscapeWarpTo), attrs()), Status::Success);
        assert_eq!(rig.last(ScopeType::Item, "Where"), Value::from_vec3([4.0, 5.0, 6.0]));
        assert_eq!(rig.world.take_calls(), ["random_placement_near_random_planet(Some(7), 1000, 10000)"]);
        let mut rig = Rig::new();
        assert_eq!(rig.run(Box::new(FindPointToEscapeWarpTo), attrs()), Status::Failure);
    }

    // ---- FindPointToExplore ----

    fn explore_attrs() -> Attrs {
        Attrs::new()
            .with("locationMessage", item("Where"))
            .with("groupsOfInterest", ints(&[25]))
            .with("pickSpecificChance", 0.5)
            .with("shouldConsiderUnspawnedDungeons", false)
    }

    /// I (1) am a ball of the bubble 0; 10 is a ship of the group 25 (of a player, 7 - not a global) in another bubble, 11 a station (group 15) of the
    /// category 3 and an NPC's, 12 an object of the group 26; they are all globals, and a ball each.
    fn explore_rig() -> Rig {
        let mut rig = Rig::new();
        rig.world.add_ball(1, [0.0; 3], 500);
        for (id, type_id, group, category, owner) in [
            (10, 600, 25, 6, 90_000_100),
            (11, 601, GROUP_STATION, 3, 1_000_125),
            (12, 602, 26, 3, 1_000_125),
        ] {
            rig.world.add_ball(id, [1000.0 * id as f64, 0.0, 0.0], type_id);
            rig.world.balls.get_mut(&id).unwrap().new_bubble_id = 5;
            rig.world
                .items
                .insert(id, InventoryItem { item_id: id, type_id, owner_id: owner, location_id: 0 });
            rig.world.type_groups.insert(type_id, group);
            rig.world.type_categories.insert(type_id, category);
            rig.world.warp_destination_points.insert((1, id), [id as f64, 0.0, 0.0]);
        }
        rig.world.globals = vec![10, 11, 12];
        rig
    }

    #[test]
    fn the_place_to_warp_to_is_next_to_a_ball_of_interest_that_is_not_in_my_bubble() {
        let mut rig = explore_rig();
        assert_eq!(rig.run(Box::new(FindPointToExplore), explore_attrs()), Status::Success);
        assert_eq!(rig.last(ScopeType::Item, "Where"), Value::from_vec3([10.0, 0.0, 0.0]));
        // the one that is picked is the one the dice say
        let mut rig = explore_rig();
        rig.world.set_numbers(&[0.0]);
        rig.run(Box::new(FindPointToExplore), explore_attrs().with("groupsOfInterest", ints(&[25, 26])));
        assert_eq!(rig.last(ScopeType::Item, "Where"), Value::from_vec3([10.0, 0.0, 0.0]));
        let mut rig = explore_rig();
        rig.world.set_numbers(&[0.99]);
        rig.run(Box::new(FindPointToExplore), explore_attrs().with("groupsOfInterest", ints(&[25, 26])));
        assert_eq!(rig.last(ScopeType::Item, "Where"), Value::from_vec3([12.0, 0.0, 0.0]));
    }

    #[test]
    fn a_ball_in_my_bubble_is_tried_again_up_to_ten_times_and_the_last_is_used() {
        let mut rig = explore_rig();
        for id in [10, 12] {
            rig.world.balls.get_mut(&id).unwrap().new_bubble_id = 0;
        }
        rig.world.balls.get_mut(&1).unwrap().new_bubble_id = 0;
        // every ball is in my bubble: ten draws (the dice go 0.0 / 0.99 alternating) and the last one is used
        rig.world.set_numbers(&[0.0, 0.99]);
        rig.run(Box::new(FindPointToExplore), explore_attrs().with("groupsOfInterest", ints(&[25, 26])));
        assert_eq!(rig.world.next, 10);
        assert_eq!(rig.last(ScopeType::Item, "Where"), Value::from_vec3([12.0, 0.0, 0.0]));
        // one that is not in my bubble ends it
        let mut rig = explore_rig();
        rig.world.balls.get_mut(&1).unwrap().new_bubble_id = 5;
        rig.world.balls.get_mut(&12).unwrap().new_bubble_id = 9;
        rig.world.set_numbers(&[0.0, 0.99]);
        rig.run(Box::new(FindPointToExplore), explore_attrs().with("groupsOfInterest", ints(&[25, 26])));
        assert_eq!(rig.world.next, 2);
        assert_eq!(rig.last(ScopeType::Item, "Where"), Value::from_vec3([12.0, 0.0, 0.0]));
    }

    #[test]
    fn categories_are_of_interest_too_and_a_list_of_the_distribution_can_take_the_place_of_the_groups() {
        let mut rig = explore_rig();
        rig.run(
            Box::new(FindPointToExplore),
            explore_attrs().with("groupsOfInterest", ints(&[])).with("categoriesOfInterest", ints(&[3])),
        );
        // 11 and 12 are in the category: the first the dice pick
        assert_eq!(rig.last(ScopeType::Item, "Where"), Value::from_vec3([11.0, 0.0, 0.0]));
        // nothing of interest
        let mut rig = explore_rig();
        assert_eq!(
            rig.run(Box::new(FindPointToExplore), explore_attrs().with("groupsOfInterest", ints(&[]))),
            Status::Failure
        );
        // the groups of the distribution (entity group blackboard), that are used with what the action says
        let mut rig = explore_rig();
        rig.post(ScopeType::EntityGroup, "ADDITIONAL_EXPLORATION_GROUPS", ints(&[26]));
        rig.run(Box::new(FindPointToExplore), explore_attrs().with("groupsOfInterest", ints(&[])));
        assert_eq!(rig.last(ScopeType::Item, "Where"), Value::from_vec3([12.0, 0.0, 0.0]));
        // with an overwrite, the groups of the task are not used
        let mut rig = explore_rig();
        rig.post(ScopeType::EntityGroup, "ADDITIONAL_EXPLORATION_GROUPS", ints(&[26]));
        rig.post(ScopeType::EntityGroup, "ADDITIONAL_EXPLORATION_ACTION", Value::Bool(true));
        rig.run(Box::new(FindPointToExplore), explore_attrs());
        assert_eq!(rig.last(ScopeType::Item, "Where"), Value::from_vec3([12.0, 0.0, 0.0]));
    }

    #[test]
    fn warp_beacons_and_dungeon_entrances_are_places_to_explore_too() {
        let mut rig = explore_rig();
        rig.world.warp_beacons = vec![12];
        rig.run(Box::new(FindPointToExplore), explore_attrs().with("groupsOfInterest", ints(&[])));
        assert_eq!(rig.last(ScopeType::Item, "Where"), Value::from_vec3([12.0, 0.0, 0.0]));
        // a beacon that has no ball
        let mut rig = explore_rig();
        rig.world.warp_beacons = vec![99];
        rig.run(Box::new(FindPointToExplore), explore_attrs());
        assert!(rig.last_error.is_some());
        // an archetype: the spawned, or with the task saying so, the installed
        let attrs = || {
            explore_attrs()
                .with("groupsOfInterest", ints(&[]))
                .with("dungeonArchetypesOfInterest", ints(&[3]))
        };
        let mut rig = explore_rig();
        rig.world.archetype_entries = [vec![11], vec![12]];
        rig.run(Box::new(FindPointToExplore), attrs());
        assert_eq!(rig.last(ScopeType::Item, "Where"), Value::from_vec3([11.0, 0.0, 0.0]));
        let mut rig = explore_rig();
        rig.world.archetype_entries = [vec![11], vec![12]];
        rig.run(Box::new(FindPointToExplore), attrs().with("shouldConsiderUnspawnedDungeons", true));
        assert_eq!(rig.last(ScopeType::Item, "Where"), Value::from_vec3([12.0, 0.0, 0.0]));
    }

    #[test]
    fn the_entrances_of_the_dungeons_of_interest_are_picked_by_chance_over_the_general_list_and_are_the_only_ones_when_that_is_empty()
     {
        let attrs = || explore_attrs().with("dungeonsOfInterest", ints(&[9]));
        // the general list is 10; the specific is 12: a die under the chance picks the specific one (the die is drawn after none for the list)
        let mut rig = explore_rig();
        rig.world.dungeon_id_entries = [vec![12], vec![]];
        rig.world.set_numbers(&[0.25, 0.0]);
        rig.run(Box::new(FindPointToExplore), attrs());
        assert_eq!(rig.last(ScopeType::Item, "Where"), Value::from_vec3([12.0, 0.0, 0.0]));
        let mut rig = explore_rig();
        rig.world.dungeon_id_entries = [vec![12], vec![]];
        rig.world.set_numbers(&[0.75, 0.0]);
        rig.run(Box::new(FindPointToExplore), attrs());
        assert_eq!(rig.last(ScopeType::Item, "Where"), Value::from_vec3([10.0, 0.0, 0.0]));
        // no general list: no die for the chance
        let mut rig = explore_rig();
        rig.world.dungeon_id_entries = [vec![12], vec![]];
        rig.world.set_numbers(&[0.0]);
        rig.run(Box::new(FindPointToExplore), attrs().with("groupsOfInterest", ints(&[])));
        assert_eq!(rig.last(ScopeType::Item, "Where"), Value::from_vec3([12.0, 0.0, 0.0]));
        assert_eq!(rig.world.next, 1, "only the choice");
        // installed ones when the task says so
        let mut rig = explore_rig();
        rig.world.dungeon_id_entries = [vec![], vec![11]];
        rig.world.set_numbers(&[0.0, 0.0]);
        rig.run(
            Box::new(FindPointToExplore),
            attrs().with("groupsOfInterest", ints(&[])).with("shouldConsiderUnspawnedDungeons", true),
        );
        assert_eq!(rig.last(ScopeType::Item, "Where"), Value::from_vec3([11.0, 0.0, 0.0]));
    }

    #[test]
    fn stations_and_gates_are_avoided_in_empire_space_unless_they_are_a_players_or_the_space_is_null() {
        let avoiding = || {
            explore_attrs()
                .with("groupsOfInterest", ints(&[GROUP_STATION]))
                .with("avoidUnsafeLocationsInEmpireSpace", true)
        };
        // the station of an NPC, in high security space (0.9): not safe
        let mut rig = explore_rig();
        rig.world.security.insert(30000142, 0.9);
        rig.ctx.solar_system_id = Some(30000142);
        assert_eq!(rig.run(Box::new(FindPointToExplore), avoiding()), Status::Failure);
        // in null security space it is
        let mut rig = explore_rig();
        rig.world.security.insert(30000142, -0.5);
        rig.ctx.solar_system_id = Some(30000142);
        assert_eq!(rig.run(Box::new(FindPointToExplore), avoiding()), Status::Success);
        // one that is a player's is safe wherever it is
        let mut rig = explore_rig();
        rig.world.security.insert(30000142, 0.9);
        rig.ctx.solar_system_id = Some(30000142);
        rig.world.items.get_mut(&11).unwrap().owner_id = 90_000_100;
        assert_eq!(rig.run(Box::new(FindPointToExplore), avoiding()), Status::Success);
        // when the task does not say to avoid them, no place is unsafe
        let mut rig = explore_rig();
        rig.world.security.insert(30000142, 0.9);
        rig.ctx.solar_system_id = Some(30000142);
        assert_eq!(
            rig.run(
                Box::new(FindPointToExplore),
                explore_attrs().with("groupsOfInterest", ints(&[GROUP_STATION]))
            ),
            Status::Success
        );
        // a task that says not to avoid them
        let mut rig = explore_rig();
        rig.world.security.insert(30000142, 0.9);
        rig.ctx.solar_system_id = Some(30000142);
        assert_eq!(
            rig.run(
                Box::new(FindPointToExplore),
                avoiding().with("avoidUnsafeLocationsInEmpireSpace", false)
            ),
            Status::Success
        );
        // what is no station or gate is safe
        let mut rig = explore_rig();
        rig.world.security.insert(30000142, 0.9);
        rig.ctx.solar_system_id = Some(30000142);
        assert_eq!(
            rig.run(Box::new(FindPointToExplore), avoiding().with("groupsOfInterest", ints(&[25]))),
            Status::Success
        );
    }

    #[test]
    fn a_global_ball_that_is_not_in_the_inventory_or_has_no_type_is_a_fault() {
        let mut rig = explore_rig();
        rig.world.items.remove(&11);
        rig.run(Box::new(FindPointToExplore), explore_attrs());
        assert!(matches!(rig.last_error, Some(BehaviorError::Task(ref m)) if m.contains("groupID")));
        let mut rig = explore_rig();
        rig.world.type_groups.clear();
        rig.run(Box::new(FindPointToExplore), explore_attrs());
        assert!(matches!(rig.last_error, Some(BehaviorError::World(crate::env::WorldError::Key(_)))));
    }
}
