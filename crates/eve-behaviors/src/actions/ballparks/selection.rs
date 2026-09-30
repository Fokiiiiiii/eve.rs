//! `behaviors/actions/ballparks.py`, the part that looks about a ball: choosing an item of the bubble by its type, group or distance, counting them,
//! and telling where things are.

use super::util::{in_collection, type_error};
use crate::consts::CATEGORY_CHARGE;
use crate::env::WorldError;
use crate::evemath::{vec_add, vec_length, vec_scale, vec_sub};
use crate::tree::{BehaviorError, Task, TaskCtx};
use crate::utility::ballparks::{
    get_ball, get_ball_warp_to_position, get_balls_in_bubble, get_id_and_types_in_bubble_by_typelist,
    get_my_ball, get_slim_item, is_ball_in_park, is_ball_in_range,
};
use crate::utility::blackboards::update_stored_value;
use crate::utility::inventory::get_inventory_item;
use crate::value::Value;

// ---- SelectTargetToAnalyzeAction ----

/// A random ball of my bubble that is close enough and not one of what is to be ignored.
#[derive(Debug, Default)]
pub struct SelectTargetToAnalyzeAction;

impl SelectTargetToAnalyzeAction {
    /// `FindSuitableTarget`.
    fn find_suitable_target(
        t: &mut TaskCtx<'_, '_>,
        mut target_candidates: Vec<i64>,
        me: i64,
    ) -> Option<Option<i64>> {
        let max_distance = t.required_float("maxDistance")?;
        let ignored_categories = t.required("ignoredCategories")?;
        let ignored_groups = t.required("ignoredGroups")?;
        let ignored_types = t.required("ignoredTypes")?;
        let should_ignore_mobile_targets = t.required("shouldIgnoreMobileTargets")?;
        while !target_candidates.is_empty() {
            let index = t.world().random_int(0, target_candidates.len() as i64 - 1) as usize;
            let ball_id = target_candidates.remove(index);
            let Some(slim) = t.world().slim_item(ball_id) else { continue };
            if in_collection(t, slim.category_id, &ignored_categories)?
                || in_collection(t, slim.group_id, &ignored_groups)?
                || in_collection(t, slim.type_id, &ignored_types)?
            {
                continue;
            }
            let ball = get_ball(t, ball_id)?;
            if ball.is_cloaked || (should_ignore_mobile_targets.truthy() && ball.is_free) {
                continue;
            }
            let distance = t.world().surface_dist(me, ball_id);
            let distance = t.ok(distance)?;
            if distance != 0.0 && distance <= max_distance {
                return Some(Some(ball_id));
            }
        }
        Some(None)
    }
}

impl Task for SelectTargetToAnalyzeAction {
    fn type_name(&self) -> &'static str {
        "SelectTargetToAnalyzeAction"
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        let Some(my_ball) = get_my_ball(t) else { return };
        let balls_in_bubble = t.world().ball_ids_in_bubble(my_ball.new_bubble_id);
        t.set_status_to_failed();
        let Some(balls_in_bubble) = balls_in_bubble.filter(|balls| !balls.is_empty()) else { return };
        let candidates =
            balls_in_bubble.into_iter().filter(|&ball_id| ball_id > 0 && ball_id != my_ball.id).collect();
        let Some(target_id) = Self::find_suitable_target(t, candidates, my_ball.id) else { return };
        if let Some(target_id) = target_id {
            let Some(address) = t.required_address("selectedTargetAddress") else { return };
            t.send_blackboard_value(&address, Value::Int(target_id));
            t.set_status_to_success();
        }
    }
}

// ---- GetBallWarpToLocation, GetObjectSpaceLocation, GetBallLocation ----

enter_task!(
    /// Puts where a ball is warping to on the blackboard.
    GetBallWarpToLocation,
    |t| {
        t.set_status_to_failed();
        let Some(ball) = t.value_at("ballIdAddress") else { return };
        if ball.is_none() {
            return;
        }
        let Some(me) = t.my_item_id() else { return };
        match t.world().check_ball(me) {
            Ok(()) => {}
            Err(WorldError::User(_) | WorldError::Runtime(_)) => {
                t.raise(BehaviorError::Unrecoverable("Entity no longer in ballpark. Probably dead.".to_string()));
                return;
            }
            Err(other) => {
                t.raise(other.into());
                return;
            }
        }
        let Some(ball_id) = t.expect_id(&ball) else { return };
        let position = get_ball_warp_to_position(t, ball_id);
        let Some(address) = t.required_address("locationAddress") else { return };
        t.send_blackboard_value(&address, position.map_or(Value::None, Value::from_vec3));
        t.set_status_to_success();
    }
);

enter_task!(
    /// Puts where an item is on the blackboard.
    GetObjectSpaceLocation,
    |t| {
        let Some(item) = t.value_at("itemIdAddress") else { return };
        if !item.truthy() {
            t.set_status_to_failed();
            return;
        }
        let Some(item_id) = t.expect_id(&item) else { return };
        let Some(ball) = t.world().ball(item_id) else {
            t.raise(WorldError::Key(item_id.to_string()).into());
            return;
        };
        let Some(address) = t.required_address("locationAddress") else { return };
        t.send_blackboard_value(&address, Value::from_vec3(ball.position));
        t.set_status_to_success();
    }
);

enter_task!(
    /// Puts where a ball that is in the park is on the blackboard.
    GetBallLocation,
    |t| {
        t.set_status_to_failed();
        let Some(ball) = t.value_at("ballIdAddress") else { return };
        if !ball.truthy() {
            return;
        }
        let Some(ball_id) = t.expect_id(&ball) else { return };
        if !is_ball_in_park(t, ball_id) {
            return;
        }
        let Some(ball) = get_ball(t, ball_id) else { return };
        let Some(address) = t.required_address("coordinateAddress") else { return };
        t.send_blackboard_value(&address, Value::from_vec3(ball.position));
        t.set_status_to_success();
    }
);

// ---- GetItemIdByTypeId, GetItemIdByTypeIdsFromBlackboard ----

/// A random item of my bubble that is of one of the types (which the task has, or the blackboard).
#[derive(Debug, Default)]
pub struct GetItemIdByTypeId {
    from_blackboard: bool,
}

impl GetItemIdByTypeId {
    pub fn by_type_ids() -> Self {
        GetItemIdByTypeId { from_blackboard: false }
    }

    pub fn by_type_ids_from_blackboard() -> Self {
        GetItemIdByTypeId { from_blackboard: true }
    }

    /// `GetItemIdFromBallpark`.
    fn get_item_id_from_ballpark(t: &mut TaskCtx<'_, '_>, type_ids: &Value) -> Option<Option<i64>> {
        let my_ball = get_my_ball(t)?;
        let mut items_in_bubble = t.world().items_in_bubble_of(my_ball.id);
        while !items_in_bubble.is_empty() {
            let index = t.world().random_int(0, items_in_bubble.len() as i64 - 1) as usize;
            let ball_id = items_in_bubble.remove(index);
            let Some(slim) = t.world().slim_item(ball_id) else { continue };
            if !in_collection(t, slim.type_id, type_ids)? {
                continue;
            }
            return Some(Some(ball_id));
        }
        Some(None)
    }
}

impl Task for GetItemIdByTypeId {
    fn type_name(&self) -> &'static str {
        if self.from_blackboard { "GetItemIdByTypeIdsFromBlackboard" } else { "GetItemIdByTypeId" }
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        t.set_status_to_failed();
        let type_ids = if self.from_blackboard {
            let Some(address) = t.required("typeIdsAddress") else { return };
            if address.is_none() {
                return;
            }
            let Some(type_ids) = t.value_at("typeIdsAddress") else { return };
            type_ids
        } else {
            let Some(type_ids) = t.required("typeIds") else { return };
            type_ids
        };
        if !type_ids.truthy() {
            return;
        }
        let Some(item_id) = Self::get_item_id_from_ballpark(t, &type_ids) else { return };
        if let Some(item_id) = item_id {
            let Some(address) = t.required_address("itemIdAddress") else { return };
            t.send_blackboard_value(&address, Value::Int(item_id));
            t.set_status_to_success();
        }
    }
}

// ---- SelectItemByTypes, SelectItemByGroups ----

/// The first item of my bubble that is of one of the types (or of the groups) of the task.
#[derive(Debug, Default)]
pub struct SelectItemBy {
    groups: bool,
}

impl SelectItemBy {
    pub fn types() -> Self {
        SelectItemBy { groups: false }
    }

    pub fn groups() -> Self {
        SelectItemBy { groups: true }
    }
}

impl Task for SelectItemBy {
    fn type_name(&self) -> &'static str {
        if self.groups { "SelectItemByGroups" } else { "SelectItemByTypes" }
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        let Some(my_ball) = get_my_ball(t) else { return };
        let ball_ids = get_balls_in_bubble(t, my_ball.new_bubble_id);
        t.set_status_to_failed();
        let Some(ball_ids) = ball_ids.filter(|ids| !ids.is_empty()) else { return };
        for ball_id in ball_ids {
            let Some(slim) = t.world().slim_item(ball_id) else { continue };
            let (attribute, value) =
                if self.groups { ("groupIds", slim.group_id) } else { ("typeIds", slim.type_id) };
            let Some(wanted) = t.required(attribute) else { return };
            let Some(found) = in_collection(t, value, &wanted) else { return };
            if found {
                let Some(address) = t.required_address("selectedItemAddress") else { return };
                t.send_blackboard_value(&address, Value::Int(ball_id));
                t.set_status_to_success();
                return;
            }
        }
    }
}

enter_task!(
    /// Puts the sun of the system on the blackboard.
    StoreSunToBlackboardValue,
    |t| {
        t.set_status_to_failed();
        if let Some(sun_id) = t.world().sun_id().filter(|&id| id != 0) {
            let Some(address) = t.required_address("targetAddress") else { return };
            t.send_blackboard_value(&address, Value::Int(sun_id));
            t.set_status_to_success();
        }
    }
);

// ---- SelectClosestPointOnObject ----

/// The point of an object that is closest to me (of its mini balls, the closest).
#[derive(Debug, Default)]
pub struct SelectClosestPointOnObject;

impl Task for SelectClosestPointOnObject {
    fn type_name(&self) -> &'static str {
        "SelectClosestPointOnObject"
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        let Some(object) = t.value_at("objectIdAddress") else { return };
        // `balls[objectId]` raises a `KeyError` for what is not there, and that is a failure
        let Some(ball) = object.as_int().and_then(|object_id| t.world().ball(object_id)) else {
            t.set_status_to_failed();
            return;
        };
        let Some(my_ball) = get_my_ball(t) else { return };
        let my_position = my_ball.position;
        // `GetClosestSphere`
        let (center, radius) = match ball.mini_balls.iter().min_by(|a, b| {
            let distance = |mini: &crate::env::MiniBall| {
                vec_length(vec_sub(my_position, vec_add(ball.position, mini.position)))
            };
            distance(a).partial_cmp(&distance(b)).unwrap_or(std::cmp::Ordering::Equal)
        }) {
            None => (ball.position, ball.radius),
            Some(mini) => (vec_add(ball.position, mini.position), mini.radius),
        };
        // `GetClosestSurfacePoint`
        let vec_to_other = vec_sub(center, my_position);
        let distance_to_other = vec_length(vec_to_other);
        if distance_to_other == 0.0 {
            t.raise(BehaviorError::Task("ZeroDivisionError: float division by zero".to_string()));
            return;
        }
        let closest_point = vec_add(my_position, vec_scale(vec_to_other, 1.0 - radius / distance_to_other));
        let Some(address) = t.required_address("closestPointAddress") else { return };
        t.send_blackboard_value(&address, Value::from_vec3(closest_point));
        t.set_status_to_success();
    }
}

// ---- GetBallsInBubbleByOwnerIds ----

/// Adds the ships of my bubble that belong to the owners to the set of objects on the blackboard.
#[derive(Debug, Default)]
pub struct GetBallsInBubbleByOwnerIds;

impl GetBallsInBubbleByOwnerIds {
    /// `GetBallsInBubbleForOwnerIds`: the new ones in ascending order (a Python set has none).
    fn get_balls_in_bubble_for_owner_ids(t: &mut TaskCtx<'_, '_>) -> Option<Vec<i64>> {
        let me = t.my_item_id()?;
        let ship_balls_in_bubble = t.world().ships_in_same_bubble(me);
        let owner_ids = t.value_at("ownerListAddress")?;
        let mut ball_ids = Vec::new();
        for ship_ball_id in ship_balls_in_bubble {
            let Some(slim) = t.world().slim_item(ship_ball_id) else {
                t.raise(BehaviorError::Task(
                    "AttributeError: 'NoneType' object has no attribute 'ownerID'".to_string(),
                ));
                return None;
            };
            let owner = slim.owner_id.map_or(Value::None, Value::Int);
            if owner_ids.is_none() {
                type_error(t, "argument of type 'NoneType' is not iterable");
                return None;
            }
            if owner_ids.contains(&owner) {
                ball_ids.push(ship_ball_id);
            }
        }
        ball_ids.sort_unstable();
        ball_ids.dedup();
        Some(ball_ids)
    }

    /// `UpdateObjectList`: what is not on the blackboard yet is added to what is, which is one set (the client changes it where it is).
    fn update_object_list(t: &mut TaskCtx<'_, '_>, ball_ids: &[i64]) -> Option<()> {
        if ball_ids.is_empty() {
            return Some(());
        }
        let address = t.required_address("objectListAddress")?;
        let existing = t.get_last_blackboard_value(&address);
        let stored = existing.truthy();
        let mut existing = if stored { existing } else { Value::Set(Vec::new()) };
        if !matches!(existing, Value::Set(_)) {
            t.raise(BehaviorError::Task("AttributeError: object has no attribute 'update'".to_string()));
            return None;
        }
        if ball_ids.iter().all(|&ball_id| existing.contains(&Value::Int(ball_id))) {
            return Some(());
        }
        for &ball_id in ball_ids {
            existing.set_add(Value::Int(ball_id));
        }
        if stored {
            update_stored_value(t, &address, &existing);
        }
        t.send_blackboard_value(&address, existing);
        Some(())
    }
}

impl Task for GetBallsInBubbleByOwnerIds {
    fn type_name(&self) -> &'static str {
        "GetBallsInBubbleByOwnerIds"
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        let Some(ball_ids) = Self::get_balls_in_bubble_for_owner_ids(t) else { return };
        if Self::update_object_list(t, &ball_ids).is_none() {
            return;
        }
        t.set_status_to_success();
    }
}

// ---- SelectClosestItemAndTypeByEveTypeList, SelectClosestItemByEveTypeList ----

/// The closest ball of my bubble whose type is in a type list; the one that is selected already stays when it is still in my bubble.
#[derive(Debug, Default)]
pub struct SelectClosestItemByEveTypeList {
    with_type: bool,
}

impl SelectClosestItemByEveTypeList {
    pub fn and_type() -> Self {
        SelectClosestItemByEveTypeList { with_type: true }
    }

    pub fn item_only() -> Self {
        SelectClosestItemByEveTypeList { with_type: false }
    }

    /// `HasValidTarget`.
    fn has_valid_target(t: &mut TaskCtx<'_, '_>) -> Option<bool> {
        let target = t.value_at("selectedTargetAddress")?;
        if !target.truthy() {
            return Some(false);
        }
        let target_id = t.expect_id(&target)?;
        if !t.world().has_ball(target_id) {
            return Some(false);
        }
        let target_ball = get_ball(t, target_id)?;
        let my_ball = get_my_ball(t)?;
        Some(target_ball.new_bubble_id == my_ball.new_bubble_id)
    }

    /// `GetClosestItemAndTypeOfInterest`.
    fn get_closest_item_and_type_of_interest(t: &mut TaskCtx<'_, '_>) -> Option<Option<(i64, i64)>> {
        let my_ball = get_my_ball(t)?;
        let type_list_id = t.required_int("eveTypeListId")?;
        let balls_of_interest =
            get_id_and_types_in_bubble_by_typelist(t, my_ball.new_bubble_id, type_list_id);
        let mut closest: Option<(i64, i64)> = None;
        let mut closest_distance = 0.0;
        for (ball_id, type_id) in balls_of_interest {
            let distance = t.world().distance_between(my_ball.id, ball_id);
            let distance_to_ball = t.ok(distance)?;
            if closest.is_none_or(|(item_id, _)| item_id == 0) || distance_to_ball < closest_distance {
                closest = Some((ball_id, type_id));
                closest_distance = distance_to_ball;
            }
        }
        Some(closest)
    }
}

impl Task for SelectClosestItemByEveTypeList {
    fn type_name(&self) -> &'static str {
        if self.with_type {
            "SelectClosestItemAndTypeByEveTypeList"
        } else {
            "SelectClosestItemByEveTypeList"
        }
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        t.set_status_to_failed();
        let Some(valid) = Self::has_valid_target(t) else { return };
        if valid {
            t.set_status_to_success();
            return;
        }
        let Some(closest) = Self::get_closest_item_and_type_of_interest(t) else { return };
        if let Some((item_id, type_id)) = closest {
            let Some(target_address) = t.required_address("selectedTargetAddress") else { return };
            t.send_blackboard_value(&target_address, Value::Int(item_id));
            if self.with_type {
                let Some(type_address) = t.required_address("selectedTypeAddress") else { return };
                t.send_blackboard_value(&type_address, Value::Int(type_id));
            }
            t.set_status_to_success();
        }
    }
}

// ---- GetItemIdsInBubbleWithinRange ----

/// The balls of a bubble (the one of a centre ball, or mine) that are of the categories or groups of interest and within a distance of the centre.
#[derive(Debug, Default)]
pub struct GetItemIdsInBubbleWithinRange;

impl GetItemIdsInBubbleWithinRange {
    /// `_get_center_ball_id`.
    fn get_center_ball_id(t: &mut TaskCtx<'_, '_>) -> Option<Value> {
        if t.has_attribute("centerBallAddress") {
            return t.value_at("centerBallAddress");
        }
        Some(t.my_item_id().map_or(Value::None, Value::Int))
    }

    /// `getattr(self.attributes, name, [])` and `slim_item.x in it`.
    fn is_of_interest(t: &mut TaskCtx<'_, '_>, attribute: &str, id: i64) -> Option<bool> {
        if !t.has_attribute(attribute) {
            return Some(false);
        }
        let wanted = t.attribute(attribute);
        in_collection(t, id, &wanted)
    }

    /// `_is_ball_within_range`.
    fn is_ball_within_range(
        t: &mut TaskCtx<'_, '_>,
        center_ball_id: i64,
        target_ball_id: i64,
    ) -> Option<bool> {
        let max_distance = if t.has_attribute("maxDistanceAddress") {
            t.value_at("maxDistanceAddress")?
        } else if t.has_attribute("maxDistance") {
            t.attribute("maxDistance")
        } else {
            t.raise(BehaviorError::Authoring(
                "Behavior Task: GetItemIdsInBubbleWithinRange is missing maxDistance".to_string(),
            ));
            return None;
        };
        let Some(max_distance) = max_distance.as_float() else {
            type_error(t, "a number is required");
            return None;
        };
        Some(is_ball_in_range(t, center_ball_id, target_ball_id, max_distance))
    }
}

impl Task for GetItemIdsInBubbleWithinRange {
    fn type_name(&self) -> &'static str {
        "GetItemIdsInBubbleWithinRange"
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        t.set_status_to_success();
        let Some(center) = Self::get_center_ball_id(t) else { return };
        if center.is_none() {
            t.set_status_to_failed();
            return;
        }
        let Some(center_ball_id) = t.expect_id(&center) else { return };
        let Some(center_ball) = get_ball(t, center_ball_id) else { return };
        let Some(balls_in_bubble) = get_balls_in_bubble(t, center_ball.new_bubble_id) else {
            t.raise(BehaviorError::Task(
                "AttributeError: 'NoneType' object has no attribute 'keys'".to_string(),
            ));
            return;
        };
        let mut balls_of_interest = Vec::new();
        for ball_id in balls_in_bubble {
            let Some(slim) = get_slim_item(t, ball_id) else { continue };
            let Some(category) = Self::is_of_interest(t, "categoriesOfInterest", slim.category_id) else {
                return;
            };
            let interesting = if category {
                true
            } else {
                let Some(group) = Self::is_of_interest(t, "groupsOfInterest", slim.group_id) else { return };
                group
            };
            if interesting {
                let Some(within) = Self::is_ball_within_range(t, center_ball_id, ball_id) else { return };
                if within {
                    balls_of_interest.push(Value::Int(ball_id));
                }
            }
        }
        let Some(address) = t.required_address("ballsWithinRangeAddress") else { return };
        t.send_blackboard_value(&address, Value::List(balls_of_interest));
    }
}

// ---- GetWarpBubbleItemIdsBlockingMeFromWarp ----

enter_task!(
    /// The ships that hold me from warping: those that have a bubble that reaches me, and the ships that launched the probes that do.
    GetWarpBubbleItemIdsBlockingMeFromWarp,
    |t| {
        t.set_status_to_success();
        let Some(me) = t.my_item_id() else { return };
        let (warp_disrupt_probe_ids, mut warp_disrupt_ship_ids) =
            t.world().warp_bubble_item_ids_within_range_of_ball(me);
        for probe_id in warp_disrupt_probe_ids {
            // `_get_targetable_source_for_probe`
            let Some(probe) = get_inventory_item(t, probe_id) else {
                t.raise(BehaviorError::Task("AttributeError: 'NoneType' object has no attribute 'categoryID'".to_string()));
                return;
            };
            let probe_category_id = t.world().type_category_id(probe.type_id);
            let source = if probe_category_id != Some(CATEGORY_CHARGE) {
                Value::Int(probe_id)
            } else {
                if get_slim_item(t, probe_id).is_none() {
                    t.raise(BehaviorError::Task(
                        "AttributeError: 'NoneType' object has no attribute 'sourceShipID'".to_string(),
                    ));
                    return;
                }
                t.world().slim_item_attribute(probe_id, "sourceShipID")
            };
            if let Some(source_id) = source.as_int() {
                warp_disrupt_ship_ids.push(source_id);
            }
        }
        let Some(address) = t.required_address("itemIdSetAddress") else { return };
        t.send_blackboard_value(&address, Value::List(warp_disrupt_ship_ids.into_iter().map(Value::Int).collect()));
    }
);

// ---- FindSpecificItemLocation ----

enter_task!(
    /// The place to warp to of a random item of a type list: it and all the items of the type list are put on the blackboard.
    FindSpecificItemLocation,
    |t| {
        // `_get_slims_by_typelist`: in the order of their ids
        let specific_interest_typelist = t.required("specificInterestTypelist");
        let Some(specific_interest_typelist) = specific_interest_typelist else { return };
        let mut slims_list = Vec::new();
        if !specific_interest_typelist.is_none() {
            let Some(type_list_id) = specific_interest_typelist.as_int() else {
                type_error(t, "a type list is a whole number");
                return;
            };
            let type_ids = t.world().type_ids_in_list(type_list_id);
            for slim in t.world().slim_items() {
                if type_ids.contains(&slim.type_id) {
                    slims_list.push(slim.item_id);
                }
            }
        }
        let Some(items_address) = t.required_address("itemsOfInterestAddress") else { return };
        t.send_blackboard_value(&items_address, Value::List(slims_list.iter().copied().map(Value::Int).collect()));
        // `select_location`
        let mut location = None;
        if !slims_list.is_empty() {
            let chosen = slims_list[t.world().random_index(slims_list.len())];
            let Some(chosen_address) = t.required_address("chosenItemOfInterestAddress") else { return };
            t.send_blackboard_value(&chosen_address, Value::Int(chosen));
            let Some(me) = t.my_item_id() else { return };
            location = Some(t.world().warp_destination_point(me, chosen));
        }
        match location {
            Some(location) => {
                let Some(address) = t.required_address("locationMessageAddress") else { return };
                t.send_blackboard_value(&address, Value::from_vec3(location));
                t.set_status_to_success();
            }
            None => t.set_status_to_failed(),
        }
    }
);

// ---- SelectAllItemIdsInBubbleByTypeList, CountItemsInBubbleByTypeList ----

enter_task!(
    /// The ids of the balls of my bubble whose type is in a type list.
    SelectAllItemIdsInBubbleByTypeList,
    |t| {
        let Some(my_ball) = get_my_ball(t) else { return };
        let Some(type_list_id) = t.required_int("typeListId") else { return };
        let balls_of_interest = get_id_and_types_in_bubble_by_typelist(t, my_ball.new_bubble_id, type_list_id);
        let Some(address) = t.required_address("selectedTargetsAddress") else { return };
        t.send_blackboard_value(&address, Value::List(balls_of_interest.into_iter().map(|(id, _)| Value::Int(id)).collect()));
        t.set_status_to_success();
    }
);

enter_task!(
    /// How many balls of my bubble have a type that is in a type list.
    CountItemsInBubbleByTypeList,
    |t| {
        let Some(my_ball) = get_my_ball(t) else { return };
        let Some(type_list_id) = t.required_int("typeListId") else { return };
        let balls_of_interest = get_id_and_types_in_bubble_by_typelist(t, my_ball.new_bubble_id, type_list_id);
        let Some(address) = t.required_address("itemCountAddress") else { return };
        t.send_blackboard_value(&address, Value::Int(balls_of_interest.len() as i64));
        t.set_status_to_success();
    }
);

#[cfg(test)]
mod tests {
    use super::*;
    use crate::blackboards::{Address, ScopeType};
    use crate::env::{Ball, MiniBall, SlimItem};
    use crate::tree::tests::Rig;
    use crate::tree::{Attrs, Status};

    fn item(name: &str) -> Value {
        Address::new(ScopeType::Item, name).to_value()
    }

    fn ints(ids: &[i64]) -> Value {
        Value::List(ids.iter().map(|&id| Value::Int(id)).collect())
    }

    /// I (1) and the balls 2 to 5 are in the bubble 7; 2 is a type 501 of the group 25 of the category 6 (a ship), 3 a 502 of the group 26 of the
    /// category 11, 4 a 503 that is cloaked, 5 a 504 that is free.
    fn bubble_rig() -> Rig {
        let mut rig = Rig::new();
        rig.world.add_ball(1, [0.0; 3], 500);
        for (id, type_id, group, category) in
            [(2, 501, 25, 6), (3, 502, 26, 11), (4, 503, 25, 6), (5, 504, 25, 6)]
        {
            rig.world.add_ball(id, [100.0 * id as f64, 0.0, 0.0], type_id);
            let slim = rig.world.slims.get_mut(&id).unwrap();
            slim.group_id = group;
            slim.category_id = category;
            slim.owner_id = Some(7);
        }
        for id in 1..=5 {
            rig.world.balls.get_mut(&id).unwrap().new_bubble_id = 7;
        }
        rig.world.balls.get_mut(&4).unwrap().is_cloaked = true;
        rig.world.balls.get_mut(&5).unwrap().is_free = true;
        rig.world.bubbles.insert(7, vec![1, 2, 3, 4, 5, -1, 0]);
        for id in 2..=5 {
            rig.world.set_dist(1, id, 100.0 * id as f64);
        }
        rig
    }

    // ---- SelectTargetToAnalyzeAction ----

    fn analyze_attrs() -> Attrs {
        Attrs::new()
            .with("selectedTargetAddress", item("Target"))
            .with("maxDistance", 1000.0)
            .with("ignoredCategories", ints(&[]))
            .with("ignoredGroups", ints(&[]))
            .with("ignoredTypes", ints(&[]))
            .with("shouldIgnoreMobileTargets", false)
    }

    #[test]
    fn a_random_ball_that_is_close_enough_is_selected_and_the_rest_are_skipped() {
        // the dice pick the last candidate each time: 5 (cloaked 4 is before it)
        let mut rig = bubble_rig();
        rig.world.set_numbers(&[0.99]);
        rig.world.set_numbers(&[0.0]);
        assert_eq!(rig.run(Box::new(SelectTargetToAnalyzeAction), analyze_attrs()), Status::Success);
        // the first candidate is 2
        assert_eq!(rig.last(ScopeType::Item, "Target"), Value::Int(2));
        let mut rig = bubble_rig();
        rig.world.set_numbers(&[0.99, 0.0, 0.0, 0.0]);
        rig.run(Box::new(SelectTargetToAnalyzeAction), analyze_attrs());
        // 5 first (a free ball is fine), and it is at 500
        assert_eq!(rig.last(ScopeType::Item, "Target"), Value::Int(5));
    }

    #[test]
    fn the_ignored_categories_groups_and_types_and_the_cloaked_and_the_mobile_and_the_far_are_skipped() {
        let cases: [(&str, Attrs, i64); 6] = [
            ("categories", analyze_attrs().with("ignoredCategories", ints(&[6])), 3),
            ("groups", analyze_attrs().with("ignoredGroups", ints(&[25])), 3),
            ("types", analyze_attrs().with("ignoredTypes", ints(&[501, 503])), 3),
            (
                "mobile",
                analyze_attrs().with("ignoredTypes", ints(&[501])).with("shouldIgnoreMobileTargets", true),
                3,
            ),
            ("far", analyze_attrs().with("maxDistance", 250.0), 2),
            ("cloaked", analyze_attrs().with("ignoredTypes", ints(&[501, 502, 504])), 0),
        ];
        for (what, attrs, expected) in cases {
            let mut rig = bubble_rig();
            // always the first candidate that is left
            rig.world.set_numbers(&[0.0]);
            let status = rig.run(Box::new(SelectTargetToAnalyzeAction), attrs);
            if expected == 0 {
                assert_eq!(status, Status::Failure, "{what}");
            } else {
                assert_eq!(rig.last(ScopeType::Item, "Target"), Value::Int(expected), "{what}");
            }
        }
    }

    #[test]
    fn a_ball_exactly_as_far_as_the_maximum_is_close_enough() {
        let mut rig = bubble_rig();
        rig.world.bubbles.insert(7, vec![1, 2]);
        assert_eq!(
            rig.run(Box::new(SelectTargetToAnalyzeAction), analyze_attrs().with("maxDistance", 200.0)),
            Status::Success
        );
        let mut rig = bubble_rig();
        rig.world.bubbles.insert(7, vec![1, 2]);
        assert_eq!(
            rig.run(Box::new(SelectTargetToAnalyzeAction), analyze_attrs().with("maxDistance", 199.0)),
            Status::Failure
        );
    }

    #[test]
    fn nothing_to_pick_from_or_nothing_close_is_a_failure() {
        let mut rig = bubble_rig();
        rig.world.bubbles.insert(7, vec![]);
        assert_eq!(rig.run(Box::new(SelectTargetToAnalyzeAction), analyze_attrs()), Status::Failure);
        let mut rig = bubble_rig();
        rig.world.bubbles.insert(7, vec![1]);
        assert_eq!(rig.run(Box::new(SelectTargetToAnalyzeAction), analyze_attrs()), Status::Failure);
        let mut rig = bubble_rig();
        rig.world.bubbles.clear();
        assert_eq!(rig.run(Box::new(SelectTargetToAnalyzeAction), analyze_attrs()), Status::Failure);
        assert_eq!(rig.last_error, None);
        // a distance of 0 is no distance
        let mut rig = bubble_rig();
        rig.world.set_dist(1, 2, 0.0);
        rig.world.bubbles.insert(7, vec![1, 2]);
        assert_eq!(rig.run(Box::new(SelectTargetToAnalyzeAction), analyze_attrs()), Status::Failure);
        // a ball with no slim item is skipped
        let mut rig = bubble_rig();
        rig.world.slims.remove(&2);
        rig.world.bubbles.insert(7, vec![1, 2]);
        assert_eq!(rig.run(Box::new(SelectTargetToAnalyzeAction), analyze_attrs()), Status::Failure);
    }

    // ---- GetBallWarpToLocation, GetObjectSpaceLocation, GetBallLocation ----

    #[test]
    fn where_a_ball_is_going_or_is_is_put_on_the_blackboard() {
        let mut rig = bubble_rig();
        rig.world.balls.get_mut(&2).unwrap().goto = [7.0, 8.0, 9.0];
        rig.post(ScopeType::Item, "Ball", Value::Int(2));
        let attrs = Attrs::new().with("ballIdAddress", item("Ball")).with("locationAddress", item("Where"));
        assert_eq!(rig.run(Box::new(GetBallWarpToLocation), attrs.clone()), Status::Success);
        assert_eq!(rig.last(ScopeType::Item, "Where"), Value::from_vec3([7.0, 8.0, 9.0]));
        // a ball that is not there has no place
        rig.post(ScopeType::Item, "Ball", Value::Int(99));
        assert_eq!(rig.run(Box::new(GetBallWarpToLocation), attrs.clone()), Status::Success);
        assert_eq!(rig.last(ScopeType::Item, "Where"), Value::None);
        // no ball is a failure; my ball being gone is the end of me
        rig.post(ScopeType::Item, "Ball", Value::None);
        assert_eq!(rig.run(Box::new(GetBallWarpToLocation), attrs.clone()), Status::Failure);
        let mut rig = bubble_rig();
        rig.world.balls.remove(&1);
        rig.post(ScopeType::Item, "Ball", Value::Int(2));
        rig.run(Box::new(GetBallWarpToLocation), attrs);
        assert!(matches!(rig.last_error, Some(BehaviorError::Unrecoverable(_))));

        let mut rig = bubble_rig();
        rig.post(ScopeType::Item, "Item", Value::Int(3));
        let attrs = Attrs::new().with("itemIdAddress", item("Item")).with("locationAddress", item("Where"));
        assert_eq!(rig.run(Box::new(GetObjectSpaceLocation), attrs.clone()), Status::Success);
        assert_eq!(rig.last(ScopeType::Item, "Where"), Value::from_vec3([300.0, 0.0, 0.0]));
        rig.post(ScopeType::Item, "Item", Value::None);
        assert_eq!(rig.run(Box::new(GetObjectSpaceLocation), attrs.clone()), Status::Failure);
        rig.post(ScopeType::Item, "Item", Value::Int(99));
        rig.run(Box::new(GetObjectSpaceLocation), attrs);
        assert!(matches!(rig.last_error, Some(BehaviorError::World(WorldError::Key(_)))));

        let mut rig = bubble_rig();
        rig.post(ScopeType::Item, "Ball", Value::Int(3));
        let attrs = Attrs::new().with("ballIdAddress", item("Ball")).with("coordinateAddress", item("Where"));
        assert_eq!(rig.run(Box::new(GetBallLocation), attrs.clone()), Status::Success);
        assert_eq!(rig.last(ScopeType::Item, "Where"), Value::from_vec3([300.0, 0.0, 0.0]));
        for ball in [Value::None, Value::Int(99), Value::Int(0)] {
            rig.post(ScopeType::Item, "Ball", ball);
            assert_eq!(rig.run(Box::new(GetBallLocation), attrs.clone()), Status::Failure);
            assert_eq!(rig.last_error, None);
        }
    }

    // ---- GetItemIdByTypeId ----

    #[test]
    fn a_random_item_of_my_bubble_with_one_of_the_types_is_found() {
        let attrs = Attrs::new().with("typeIds", ints(&[502, 504])).with("itemIdAddress", item("Found"));
        // the dice pick the first of the bubble that is left each time; 3 is the first with a type asked for
        let mut rig = bubble_rig();
        rig.world.set_numbers(&[0.0]);
        assert_eq!(rig.run(Box::new(GetItemIdByTypeId::by_type_ids()), attrs.clone()), Status::Success);
        assert_eq!(rig.last(ScopeType::Item, "Found"), Value::Int(3));
        // the last first: 5
        let mut rig = bubble_rig();
        rig.world.set_numbers(&[0.99]);
        rig.run(Box::new(GetItemIdByTypeId::by_type_ids()), attrs);
        assert_eq!(rig.last(ScopeType::Item, "Found"), Value::Int(5));
    }

    #[test]
    fn no_type_no_such_item_or_no_types_on_the_blackboard_is_a_failure() {
        let found = || Attrs::new().with("itemIdAddress", item("Found"));
        let mut rig = bubble_rig();
        assert_eq!(
            rig.run(Box::new(GetItemIdByTypeId::by_type_ids()), found().with("typeIds", ints(&[]))),
            Status::Failure
        );
        let mut rig = bubble_rig();
        assert_eq!(
            rig.run(Box::new(GetItemIdByTypeId::by_type_ids()), found().with("typeIds", ints(&[999]))),
            Status::Failure
        );
        assert_eq!(rig.last_error, None);
        // from the blackboard
        let attrs = found().with("typeIdsAddress", item("Types"));
        let mut rig = bubble_rig();
        rig.world.set_numbers(&[0.0]);
        rig.post(ScopeType::Item, "Types", ints(&[503]));
        assert_eq!(
            rig.run(Box::new(GetItemIdByTypeId::by_type_ids_from_blackboard()), attrs.clone()),
            Status::Success
        );
        assert_eq!(rig.last(ScopeType::Item, "Found"), Value::Int(4));
        rig.post(ScopeType::Item, "Types", Value::None);
        assert_eq!(
            rig.run(Box::new(GetItemIdByTypeId::by_type_ids_from_blackboard()), attrs.clone()),
            Status::Failure
        );
        assert_eq!(
            rig.run(
                Box::new(GetItemIdByTypeId::by_type_ids_from_blackboard()),
                found().with("typeIdsAddress", Value::None)
            ),
            Status::Failure
        );
        assert_eq!(
            GetItemIdByTypeId::by_type_ids_from_blackboard().type_name(),
            "GetItemIdByTypeIdsFromBlackboard"
        );
    }

    // ---- SelectItemBy..., StoreSun ----

    #[test]
    fn the_first_item_of_my_bubble_of_a_type_or_a_group_is_selected() {
        let attrs = Attrs::new().with("selectedItemAddress", item("Selected"));
        let mut rig = bubble_rig();
        assert_eq!(
            rig.run(Box::new(SelectItemBy::types()), attrs.clone().with("typeIds", ints(&[503, 502]))),
            Status::Success
        );
        assert_eq!(rig.last(ScopeType::Item, "Selected"), Value::Int(3));
        let mut rig = bubble_rig();
        assert_eq!(
            rig.run(Box::new(SelectItemBy::groups()), attrs.clone().with("groupIds", ints(&[26, 25]))),
            Status::Success
        );
        assert_eq!(rig.last(ScopeType::Item, "Selected"), Value::Int(2));
        let mut rig = bubble_rig();
        assert_eq!(
            rig.run(Box::new(SelectItemBy::groups()), attrs.clone().with("groupIds", ints(&[1]))),
            Status::Failure
        );
        let mut rig = bubble_rig();
        rig.world.bubbles.clear();
        assert_eq!(
            rig.run(Box::new(SelectItemBy::types()), attrs.clone().with("typeIds", ints(&[502]))),
            Status::Failure
        );
        // the types of a task that has none are asked for only when there is an item
        let mut rig = bubble_rig();
        rig.world.bubbles.insert(7, vec![]);
        assert_eq!(rig.run(Box::new(SelectItemBy::types()), attrs.clone()), Status::Failure);
        assert_eq!(rig.last_error, None);
        let mut rig = bubble_rig();
        rig.run(Box::new(SelectItemBy::types()), attrs);
        assert!(rig.last_error.is_some());
        assert_eq!(SelectItemBy::groups().type_name(), "SelectItemByGroups");
    }

    #[test]
    fn the_sun_is_put_on_the_blackboard() {
        let attrs = Attrs::new().with("targetAddress", item("Sun"));
        let mut rig = bubble_rig();
        rig.world.sun = Some(40000001);
        assert_eq!(rig.run(Box::new(StoreSunToBlackboardValue), attrs.clone()), Status::Success);
        assert_eq!(rig.last(ScopeType::Item, "Sun"), Value::Int(40000001));
        let mut rig = bubble_rig();
        assert_eq!(rig.run(Box::new(StoreSunToBlackboardValue), attrs.clone()), Status::Failure);
        let mut rig = bubble_rig();
        rig.world.sun = Some(0);
        assert_eq!(rig.run(Box::new(StoreSunToBlackboardValue), attrs.clone()), Status::Failure);
    }

    // ---- SelectClosestPointOnObject ----

    fn closest_attrs() -> Attrs {
        Attrs::new().with("objectIdAddress", item("Object")).with("closestPointAddress", item("Point"))
    }

    #[test]
    fn the_closest_point_of_an_object_is_on_its_surface_towards_me() {
        let mut rig = bubble_rig();
        rig.world.balls.get_mut(&2).unwrap().radius = 50.0;
        rig.post(ScopeType::Item, "Object", Value::Int(2));
        assert_eq!(rig.run(Box::new(SelectClosestPointOnObject), closest_attrs()), Status::Success);
        // I am at 0, it is at 200, and the surface is 150 from me
        assert_eq!(rig.last(ScopeType::Item, "Point"), Value::from_vec3([150.0, 0.0, 0.0]));
    }

    #[test]
    fn of_the_mini_balls_of_an_object_the_closest_is_the_sphere() {
        let mut rig = bubble_rig();
        rig.world.balls.get_mut(&2).unwrap().mini_balls = vec![
            MiniBall { position: [100.0, 0.0, 0.0], radius: 10.0 },
            MiniBall { position: [-50.0, 0.0, 0.0], radius: 20.0 },
            MiniBall { position: [0.0, 500.0, 0.0], radius: 30.0 },
        ];
        rig.post(ScopeType::Item, "Object", Value::Int(2));
        rig.run(Box::new(SelectClosestPointOnObject), closest_attrs());
        // the closest mini ball is at 150 and 20 wide: 130 from me
        assert_eq!(rig.last(ScopeType::Item, "Point"), Value::from_vec3([130.0, 0.0, 0.0]));
    }

    #[test]
    fn an_object_that_is_not_there_is_a_failure_and_one_where_i_am_is_a_division_by_zero() {
        for object in [Value::None, Value::Int(99)] {
            let mut rig = bubble_rig();
            rig.post(ScopeType::Item, "Object", object);
            assert_eq!(rig.run(Box::new(SelectClosestPointOnObject), closest_attrs()), Status::Failure);
            assert_eq!(rig.last_error, None);
        }
        let mut rig = bubble_rig();
        rig.world.balls.insert(6, Ball { id: 6, position: [0.0; 3], ..Default::default() });
        rig.post(ScopeType::Item, "Object", Value::Int(6));
        rig.run(Box::new(SelectClosestPointOnObject), closest_attrs());
        assert!(matches!(rig.last_error, Some(BehaviorError::Task(ref m)) if m.starts_with("ZeroDivision")));
    }

    // ---- GetBallsInBubbleByOwnerIds ----

    fn owners_attrs() -> Attrs {
        Attrs::new().with("ownerListAddress", item("Owners")).with("objectListAddress", item("Objects"))
    }

    fn owners_rig() -> Rig {
        let mut rig = bubble_rig();
        rig.world.same_bubble_ships.insert(1, vec![1, 2, 3, 5]);
        rig.world.slims.get_mut(&1).unwrap().owner_id = Some(7);
        rig.world.slims.get_mut(&3).unwrap().owner_id = Some(8);
        rig.world.slims.get_mut(&5).unwrap().owner_id = Some(9);
        rig.post(ScopeType::Item, "Owners", ints(&[7, 8]));
        rig
    }

    #[test]
    fn the_ships_of_the_owners_are_added_to_the_set_of_objects_that_is_there() {
        let mut rig = owners_rig();
        assert_eq!(rig.run(Box::new(GetBallsInBubbleByOwnerIds), owners_attrs()), Status::Success);
        assert_eq!(
            rig.last(ScopeType::Item, "Objects"),
            Value::set([Value::Int(1), Value::Int(2), Value::Int(3)])
        );
        // the set that is there is changed where it is, and sent again
        let mut rig = owners_rig();
        rig.post(ScopeType::Item, "Objects", Value::set([Value::Int(3), Value::Int(50)]));
        rig.run(Box::new(GetBallsInBubbleByOwnerIds), owners_attrs());
        let all = Value::set([Value::Int(3), Value::Int(50), Value::Int(1), Value::Int(2)]);
        assert_eq!(rig.history("Objects"), [all.clone(), all]);
    }

    #[test]
    fn the_ships_are_added_in_the_order_of_their_ids_and_once() {
        let mut rig = owners_rig();
        rig.world.same_bubble_ships.insert(1, vec![3, 2, 3, 1]);
        rig.run(Box::new(GetBallsInBubbleByOwnerIds), owners_attrs());
        assert_eq!(
            rig.last(ScopeType::Item, "Objects"),
            Value::Set(vec![Value::Int(1), Value::Int(2), Value::Int(3)])
        );
    }

    #[test]
    fn nothing_is_sent_for_no_ships_of_the_owners_or_for_ships_that_are_there_already() {
        let mut rig = owners_rig();
        rig.post(ScopeType::Item, "Owners", ints(&[99]));
        assert_eq!(rig.run(Box::new(GetBallsInBubbleByOwnerIds), owners_attrs()), Status::Success);
        assert_eq!(rig.messages_on("Objects"), 0);
        let mut rig = owners_rig();
        rig.post(
            ScopeType::Item,
            "Objects",
            Value::set([Value::Int(1), Value::Int(2), Value::Int(3), Value::Int(4)]),
        );
        rig.run(Box::new(GetBallsInBubbleByOwnerIds), owners_attrs());
        assert_eq!(rig.messages_on("Objects"), 1);
    }

    #[test]
    fn a_ship_with_no_slim_item_or_owners_that_are_not_there_are_faults() {
        let mut rig = owners_rig();
        rig.world.slims.remove(&2);
        rig.run(Box::new(GetBallsInBubbleByOwnerIds), owners_attrs());
        assert!(matches!(rig.last_error, Some(BehaviorError::Task(ref m)) if m.contains("ownerID")));
        let mut rig = owners_rig();
        rig.post(ScopeType::Item, "Owners", Value::None);
        rig.run(Box::new(GetBallsInBubbleByOwnerIds), owners_attrs());
        assert!(matches!(rig.last_error, Some(BehaviorError::Task(ref m)) if m.starts_with("TypeError")));
    }

    // ---- SelectClosestItem... ----

    fn closest_item_attrs() -> Attrs {
        Attrs::new()
            .with("eveTypeListId", 55)
            .with("selectedTargetAddress", item("Target"))
            .with("selectedTypeAddress", item("Type"))
    }

    fn closest_item_rig() -> Rig {
        let mut rig = bubble_rig();
        rig.world.type_ids_by_list.insert(55, vec![502, 503, 504]);
        rig
    }

    #[test]
    fn the_closest_ball_of_the_type_list_is_selected_with_or_without_its_type() {
        let mut rig = closest_item_rig();
        assert_eq!(
            rig.run(Box::new(SelectClosestItemByEveTypeList::and_type()), closest_item_attrs()),
            Status::Success
        );
        assert_eq!(rig.last(ScopeType::Item, "Target"), Value::Int(3));
        assert_eq!(rig.last(ScopeType::Item, "Type"), Value::Int(502));
        let mut rig = closest_item_rig();
        assert_eq!(
            rig.run(Box::new(SelectClosestItemByEveTypeList::item_only()), closest_item_attrs()),
            Status::Success
        );
        assert_eq!(rig.last(ScopeType::Item, "Target"), Value::Int(3));
        assert_eq!(rig.messages_on("Type"), 0);
        // the one that is closest, whichever comes first
        let mut rig = closest_item_rig();
        rig.world.bubbles.insert(7, vec![5, 4, 3]);
        rig.run(Box::new(SelectClosestItemByEveTypeList::and_type()), closest_item_attrs());
        assert_eq!(rig.last(ScopeType::Item, "Target"), Value::Int(3));
        assert_eq!(SelectClosestItemByEveTypeList::item_only().type_name(), "SelectClosestItemByEveTypeList");
    }

    #[test]
    fn a_target_that_is_in_my_bubble_stays_and_one_that_is_not_is_replaced() {
        let mut rig = closest_item_rig();
        rig.post(ScopeType::Item, "Target", Value::Int(5));
        assert_eq!(
            rig.run(Box::new(SelectClosestItemByEveTypeList::and_type()), closest_item_attrs()),
            Status::Success
        );
        assert_eq!(rig.messages_on("Target"), 1, "nothing was sent");
        let mut rig = closest_item_rig();
        rig.post(ScopeType::Item, "Target", Value::Int(5));
        rig.world.balls.get_mut(&5).unwrap().new_bubble_id = 8;
        rig.run(Box::new(SelectClosestItemByEveTypeList::and_type()), closest_item_attrs());
        assert_eq!(rig.last(ScopeType::Item, "Target"), Value::Int(3));
        let mut rig = closest_item_rig();
        rig.post(ScopeType::Item, "Target", Value::Int(99));
        rig.run(Box::new(SelectClosestItemByEveTypeList::and_type()), closest_item_attrs());
        assert_eq!(rig.last(ScopeType::Item, "Target"), Value::Int(3));
    }

    #[test]
    fn no_ball_of_the_type_list_is_a_failure() {
        let mut rig = closest_item_rig();
        rig.world.type_ids_by_list.insert(55, vec![999]);
        assert_eq!(
            rig.run(Box::new(SelectClosestItemByEveTypeList::and_type()), closest_item_attrs()),
            Status::Failure
        );
        assert_eq!(rig.last_error, None);
    }

    // ---- GetItemIdsInBubbleWithinRange ----

    fn range_attrs() -> Attrs {
        Attrs::new().with("ballsWithinRangeAddress", item("Within")).with("maxDistance", 350.0)
    }

    #[test]
    fn the_balls_of_the_categories_or_groups_of_interest_within_range_of_me_are_listed() {
        // categories 11 (3), groups 25 (2, 4, 5): 2 at 200, 3 at 300, 4 at 400 (too far)
        let attrs =
            range_attrs().with("categoriesOfInterest", ints(&[11])).with("groupsOfInterest", ints(&[25]));
        let mut rig = bubble_rig();
        assert_eq!(rig.run(Box::new(GetItemIdsInBubbleWithinRange), attrs.clone()), Status::Success);
        assert_eq!(rig.last(ScopeType::Item, "Within"), ints(&[2, 3]));
        // only categories, only groups, neither
        let mut rig = bubble_rig();
        rig.run(
            Box::new(GetItemIdsInBubbleWithinRange),
            range_attrs().with("categoriesOfInterest", ints(&[11])),
        );
        assert_eq!(rig.last(ScopeType::Item, "Within"), ints(&[3]));
        let mut rig = bubble_rig();
        rig.run(Box::new(GetItemIdsInBubbleWithinRange), range_attrs().with("groupsOfInterest", ints(&[25])));
        assert_eq!(rig.last(ScopeType::Item, "Within"), ints(&[2]));
        let mut rig = bubble_rig();
        rig.run(Box::new(GetItemIdsInBubbleWithinRange), range_attrs());
        assert_eq!(rig.last(ScopeType::Item, "Within"), ints(&[]));
        // exactly the range is in
        let mut rig = bubble_rig();
        rig.run(Box::new(GetItemIdsInBubbleWithinRange), attrs.with("maxDistance", 300.0));
        assert_eq!(rig.last(ScopeType::Item, "Within"), ints(&[2, 3]));
    }

    #[test]
    fn the_range_and_the_centre_can_come_from_the_blackboard_and_a_task_without_a_range_is_badly_made() {
        let mut rig = bubble_rig();
        rig.post(ScopeType::Item, "Center", Value::Int(3));
        rig.post(ScopeType::Item, "Max", Value::Float(150.0));
        rig.world.set_dist(3, 2, 100.0);
        rig.world.set_dist(3, 4, 100.0);
        rig.world.set_dist(3, 5, 200.0);
        rig.world.set_dist(3, 3, 0.0);
        let attrs = Attrs::new()
            .with("ballsWithinRangeAddress", item("Within"))
            .with("centerBallAddress", item("Center"))
            .with("maxDistanceAddress", item("Max"))
            .with("groupsOfInterest", ints(&[25, 26]));
        rig.run(Box::new(GetItemIdsInBubbleWithinRange), attrs.clone());
        assert_eq!(rig.last(ScopeType::Item, "Within"), ints(&[2, 3, 4]));
        // no centre
        rig.post(ScopeType::Item, "Center", Value::None);
        assert_eq!(rig.run(Box::new(GetItemIdsInBubbleWithinRange), attrs), Status::Failure);
        let mut rig = bubble_rig();
        rig.run(
            Box::new(GetItemIdsInBubbleWithinRange),
            Attrs::new()
                .with("ballsWithinRangeAddress", item("Within"))
                .with("groupsOfInterest", ints(&[25])),
        );
        assert!(matches!(rig.last_error, Some(BehaviorError::Authoring(_))));
    }

    // ---- GetWarpBubbleItemIdsBlockingMeFromWarp ----

    #[test]
    fn the_ships_that_hold_me_from_warp_are_the_ships_with_a_bubble_and_the_sources_of_the_probes() {
        let mut rig = bubble_rig();
        rig.world.warp_bubbles = (vec![10, 11], vec![2, 3]);
        rig.world
            .items
            .insert(10, crate::env::InventoryItem { item_id: 10, type_id: 900, ..Default::default() });
        rig.world
            .items
            .insert(11, crate::env::InventoryItem { item_id: 11, type_id: 901, ..Default::default() });
        // 10 is a probe that is a ship's own; 11 is a charge that a ship launched
        rig.world.type_categories.insert(900, 22);
        rig.world.type_categories.insert(901, CATEGORY_CHARGE);
        rig.world.slims.insert(11, SlimItem { item_id: 11, type_id: 901, ..Default::default() });
        rig.world.slim_attributes.insert((11, "sourceShipID".to_string()), Value::Int(4));
        let attrs = Attrs::new().with("itemIdSetAddress", item("Blockers"));
        assert_eq!(rig.run(Box::new(GetWarpBubbleItemIdsBlockingMeFromWarp), attrs.clone()), Status::Success);
        assert_eq!(rig.last(ScopeType::Item, "Blockers"), ints(&[2, 3, 10, 4]));
        // a charge whose source is not known adds nothing
        let mut rig = bubble_rig();
        rig.world.warp_bubbles = (vec![11], vec![]);
        rig.world
            .items
            .insert(11, crate::env::InventoryItem { item_id: 11, type_id: 901, ..Default::default() });
        rig.world.type_categories.insert(901, CATEGORY_CHARGE);
        rig.world.slims.insert(11, SlimItem { item_id: 11, type_id: 901, ..Default::default() });
        rig.run(Box::new(GetWarpBubbleItemIdsBlockingMeFromWarp), attrs.clone());
        assert_eq!(rig.last(ScopeType::Item, "Blockers"), ints(&[]));
        // a probe that is not in the inventory
        let mut rig = bubble_rig();
        rig.world.warp_bubbles = (vec![12], vec![]);
        rig.run(Box::new(GetWarpBubbleItemIdsBlockingMeFromWarp), attrs);
        assert!(matches!(rig.last_error, Some(BehaviorError::Task(ref m)) if m.contains("categoryID")));
    }

    // ---- FindSpecificItemLocation ----

    fn specific_attrs() -> Attrs {
        Attrs::new()
            .with("specificInterestTypelist", 55)
            .with("itemsOfInterestAddress", item("Items"))
            .with("chosenItemOfInterestAddress", item("Chosen"))
            .with("locationMessageAddress", item("Location"))
    }

    #[test]
    fn a_random_item_of_the_type_list_is_chosen_and_the_place_to_warp_to_is_told() {
        let mut rig = bubble_rig();
        rig.world.type_ids_by_list.insert(55, vec![502, 504]);
        rig.world.set_numbers(&[0.99]);
        rig.world.warp_destination_points.insert((1, 5), [1.0, 2.0, 3.0]);
        assert_eq!(rig.run(Box::new(FindSpecificItemLocation), specific_attrs()), Status::Success);
        assert_eq!(rig.last(ScopeType::Item, "Items"), ints(&[3, 5]));
        assert_eq!(rig.last(ScopeType::Item, "Chosen"), Value::Int(5));
        assert_eq!(rig.last(ScopeType::Item, "Location"), Value::from_vec3([1.0, 2.0, 3.0]));
        // none of the type list
        let mut rig = bubble_rig();
        rig.world.type_ids_by_list.insert(55, vec![999]);
        assert_eq!(rig.run(Box::new(FindSpecificItemLocation), specific_attrs()), Status::Failure);
        assert_eq!(rig.last(ScopeType::Item, "Items"), ints(&[]));
        assert_eq!(rig.messages_on("Chosen"), 0);
        // no type list: none of the items, and it is said
        let mut rig = bubble_rig();
        assert_eq!(
            rig.run(
                Box::new(FindSpecificItemLocation),
                specific_attrs().with("specificInterestTypelist", Value::None)
            ),
            Status::Failure
        );
        assert_eq!(rig.last(ScopeType::Item, "Items"), ints(&[]));
    }

    // ---- SelectAllItemIdsInBubbleByTypeList, CountItemsInBubbleByTypeList ----

    #[test]
    fn the_balls_of_a_type_list_are_listed_and_counted() {
        let mut rig = bubble_rig();
        rig.world.type_ids_by_list.insert(55, vec![502, 504, 999]);
        let attrs = Attrs::new()
            .with("typeListId", 55)
            .with("selectedTargetsAddress", item("Ids"))
            .with("itemCountAddress", item("Count"));
        assert_eq!(rig.run(Box::new(SelectAllItemIdsInBubbleByTypeList), attrs.clone()), Status::Success);
        assert_eq!(rig.last(ScopeType::Item, "Ids"), ints(&[3, 5]));
        assert_eq!(rig.run(Box::new(CountItemsInBubbleByTypeList), attrs), Status::Success);
        assert_eq!(rig.last(ScopeType::Item, "Count"), Value::Int(2));
    }
}
