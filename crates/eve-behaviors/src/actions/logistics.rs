//! `behaviors/actions/logistics.py`: what remote repairers do: where the logistics stand, which member to help, and which ships to keep locked to repair.
//!
//! The order of a Python set is not kept: the ships to unlock and to lock are done in ascending order of their ids. When two repair effects are
//! registered equally often the first registered one is the most common (Python 2's `Counter.most_common(1)` goes by the order of the dict).
//! `AddTargetEx(tasklet=True)` waits for the lock in the client; here the world starts the lock and answers, so the thread that locks is done in one go.

use crate::consts::*;
use crate::env::WorldError;
use crate::evemath::center_point;
use crate::groups::mixin::get_member_id_list;
use crate::tree::{BehaviorError, Task, TaskCtx};
use crate::utility::ballparks::{get_ball_position, get_distance_between, is_ball_in_park};
use crate::utility::blackboards::update_stored_value;
use crate::utility::dogmatic::{
    get_attribute_value, get_my_attribute_value, get_range_falloff_multiplier_at_distance,
    is_assistance_disallowed, type_has_effect,
};
use crate::utility::logistics::{
    get_damage_state_function_by_effect_id, get_logistics_priority, get_remote_repair_effect_id,
};
use crate::utility::math::{compute_forward_fleet_position, compute_rear_fleet_position};
use crate::value::Value;

/// `LOGISTICS_CATEGORIES_TO_CONSIDER`.
const LOGISTICS_CATEGORIES_TO_CONSIDER: [i64; 3] = [CATEGORY_ENTITY, CATEGORY_SHIP, CATEGORY_DRONE];

/// `LOGISTICS_GROUPS_TO_IGNORE`.
const LOGISTICS_GROUPS_TO_IGNORE: [i64; 14] = [
    GROUP_SPAWN_CONTAINER,
    GROUP_SPEW_CONTAINER,
    GROUP_BILLBOARD,
    GROUP_AGENTS_IN_SPACE,
    GROUP_CAPTURE_POINT_TOWER,
    GROUP_CONTROL_BUNKER,
    GROUP_LARGE_COLLIDABLE_SHIP,
    GROUP_LARGE_COLLIDABLE_OBJECT,
    GROUP_LARGE_COLLIDABLE_STRUCTURE,
    GROUP_TEMPORARY_COLLIDABLE_STRUCTURE,
    GROUP_MISSION_CONTAINER,
    GROUP_TEMPORARY_CLOUD,
    GROUP_SENTRY_GUN,
    GROUP_INDUSTRIAL_SUPPORT_FACILITY,
];

/// `LOGISTICS_EFFECTS`.
const LOGISTICS_EFFECTS: [i64; 1] = [EFFECT_BEHAVIOR_NPC_REMOTE_ARMOR_REPAIR];

/// The positions of the friends, and of the enemies.
type FriendlyAndHostile = (Vec<[f64; 3]>, Vec<[f64; 3]>);

const HANDLER_UNLOCK_AND_LOCK_TARGETS: u16 = 1;

fn key_error(t: &mut TaskCtx<'_, '_>, key: impl ToString) {
    t.raise(WorldError::Key(key.to_string()).into());
}

fn type_error(t: &mut TaskCtx<'_, '_>, what: &str) {
    t.raise(BehaviorError::Task(format!("TypeError: {what}")));
}

fn owner_in(owners: &Value, owner_id: Option<i64>) -> bool {
    owners.contains(&owner_id.map_or(Value::None, Value::Int))
}

/// A position that a blackboard holds, as the vector maths takes it.
fn vec3_of(t: &mut TaskCtx<'_, '_>, value: &Value) -> Option<[f64; 3]> {
    let position = value.as_vec3();
    if position.is_none() {
        type_error(t, "unsupported operand type(s) for -: 'NoneType' and 'float'");
    }
    position
}

/// Where the logistics of a group stand: at the centre of their friends, or, with enemies about, behind their friends by what they can repair.
#[derive(Debug, Default)]
pub struct SelectDesiredAnchorPosition;

impl SelectDesiredAnchorPosition {
    /// `GetBallIdsToConsider`: the balls of the bubble I am in (the balls of the ones that are not real are left out).
    fn get_ball_ids_to_consider(t: &mut TaskCtx<'_, '_>) -> Option<Vec<i64>> {
        let me = t.my_item_id()?;
        let Some(ball) = t.world().ball(me) else {
            key_error(t, me);
            return None;
        };
        let Some(ball_ids) = t.world().ball_ids_in_bubble(ball.new_bubble_id) else {
            key_error(t, ball.new_bubble_id);
            return None;
        };
        Some(ball_ids.into_iter().filter(|&ball_id| ball_id > 0).collect())
    }

    /// `CollectFriendlyAndHostilePositions` with `ClassifyItemIdAndCollect`.
    fn collect_friendly_and_hostile_positions(t: &mut TaskCtx<'_, '_>) -> Option<FriendlyAndHostile> {
        let ball_ids = Self::get_ball_ids_to_consider(t)?;
        let mut friendly = Vec::new();
        let mut hostile = Vec::new();
        for item_id in ball_ids {
            let Some(slim) = t.world().slim_item(item_id) else { continue };
            if !LOGISTICS_CATEGORIES_TO_CONSIDER.contains(&slim.category_id)
                || LOGISTICS_GROUPS_TO_IGNORE.contains(&slim.group_id)
            {
                continue;
            }
            let friendly_owners = t.required("friendlyOwners")?;
            if owner_in(&friendly_owners, slim.owner_id) {
                friendly.push(get_ball_position(t, item_id)?);
            }
            let hostile_owners = t.required("hostileOwners")?;
            if owner_in(&hostile_owners, slim.owner_id) {
                hostile.push(get_ball_position(t, item_id)?);
            }
        }
        Some((friendly, hostile))
    }

    /// `GetGroupLogisticsRanges`: what the members of my group can repair from.
    fn get_group_logistics_ranges(t: &mut TaskCtx<'_, '_>) -> Option<Vec<f64>> {
        let mut ranges = Vec::new();
        for member_id in get_member_id_list(t) {
            let repair_range = get_attribute_value(t, member_id, ATTRIBUTE_NPC_ASSISTANCE_RANGE)?;
            if repair_range > 0.0 {
                ranges.push(repair_range);
            }
        }
        if t.faulted() { None } else { Some(ranges) }
    }

    /// `ComputeDesiredPosition`: `None` is the client's `ValueError` (no centre, no range).
    fn compute_desired_position(
        t: &mut TaskCtx<'_, '_>,
        friendly: &[[f64; 3]],
        hostile: &[[f64; 3]],
    ) -> Option<Option<[f64; 3]>> {
        let (Some(friendly_centroid), Some(hostile_centroid)) =
            (center_point(friendly), center_point(hostile))
        else {
            return Some(None);
        };
        let ranges = Self::get_group_logistics_ranges(t)?;
        let Some(logistics_range) = ranges.into_iter().reduce(f64::min) else { return Some(None) };
        let proximity_fraction = t.required("proximityFraction")?;
        let Some(fraction) = proximity_fraction.as_float() else {
            type_error(t, "unsupported operand type(s) for *: 'float' and 'NoneType'");
            return None;
        };
        Some(Some(compute_rear_fleet_position(
            friendly_centroid,
            hostile_centroid,
            logistics_range * fraction,
        )))
    }
}

impl Task for SelectDesiredAnchorPosition {
    fn type_name(&self) -> &'static str {
        "SelectDesiredAnchorPosition"
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        let Some((friendly, hostile)) = Self::collect_friendly_and_hostile_positions(t) else { return };
        let desired = if hostile.is_empty() {
            Some(center_point(&friendly))
        } else {
            Self::compute_desired_position(t, &friendly, &hostile)
        };
        let Some(desired) = desired else { return };
        let Some(address) = t.required_address("desiredPositionAddress") else { return };
        match desired {
            Some(position) => {
                t.send_blackboard_value(&address, Value::from_vec3(position));
                t.set_status_to_success();
            }
            None => {
                t.send_blackboard_value(&address, Value::None);
                t.set_status_to_failed();
            }
        }
    }
}

/// The member of my group with the lowest value of an attribute among those that can repair.
#[derive(Debug, Default)]
pub struct SelectGroupMemberBasedOnLowestAttributeValue;

impl SelectGroupMemberBasedOnLowestAttributeValue {
    /// `HasLogisticsEffects`: a member the ballpark has no slim item of has none.
    fn has_logistics_effects(t: &mut TaskCtx<'_, '_>, member_id: i64) -> bool {
        let Some(slim) = t.world().slim_item(member_id) else { return false };
        LOGISTICS_EFFECTS.iter().any(|&effect_id| type_has_effect(t, slim.type_id, effect_id))
    }
}

impl Task for SelectGroupMemberBasedOnLowestAttributeValue {
    fn type_name(&self) -> &'static str {
        "SelectGroupMemberBasedOnLowestAttributeValue"
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        let mut members = Vec::new();
        for member_id in get_member_id_list(t) {
            if Self::has_logistics_effects(t, member_id) {
                members.push(member_id);
            }
        }
        if t.faulted() {
            return;
        }
        if members.is_empty() {
            t.set_status_to_failed();
            return;
        }
        // `SortMemberByAttributeValue`: a stable sort, lowest first
        let Some(attribute) = t.required("attributeId") else { return };
        let Some(attribute_id) = attribute.as_int() else {
            key_error(t, attribute);
            return;
        };
        let mut keyed = Vec::new();
        for &member_id in &members {
            let Some(value) = get_attribute_value(t, member_id, attribute_id) else { return };
            keyed.push((value, member_id));
        }
        keyed.sort_by(|a, b| a.0.partial_cmp(&b.0).unwrap_or(std::cmp::Ordering::Equal));
        let Some(address) = t.required_address("selectedIdAddress") else { return };
        t.send_blackboard_value(&address, Value::Int(keyed[0].1));
        t.set_status_to_success();
    }
}

/// `SetRearFleetPosition` and `SetForwardFleetPosition`: where to stand, `distance` from the ship the fleet is anchored to, along the line from the
/// hostile to it (the rear) or from it to the hostile (the forward).
#[derive(Debug, Default)]
pub struct SetFleetPosition {
    forward: bool,
}

impl SetFleetPosition {
    pub fn rear() -> Self {
        SetFleetPosition { forward: false }
    }

    pub fn forward() -> Self {
        SetFleetPosition { forward: true }
    }
}

impl Task for SetFleetPosition {
    fn type_name(&self) -> &'static str {
        if self.forward { "SetForwardFleetPosition" } else { "SetRearFleetPosition" }
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        t.set_status_to_failed();
        let Some(hostile_position) = t.value_at("hostilePositionAddress") else { return };
        if !hostile_position.truthy() {
            return;
        }
        let Some(anchor) = t.value_at("anchorBallIdAddress") else { return };
        let Some(anchor_ball_id) = anchor.as_int().filter(|&id| id != 0) else { return };
        if !is_ball_in_park(t, anchor_ball_id) {
            return;
        }
        let Some(friendly_position) = get_ball_position(t, anchor_ball_id) else { return };
        let Some(distance) = t.value_at("distanceAddress") else { return };
        let Some(hostile) = vec3_of(t, &hostile_position) else { return };
        let Some(distance) = distance.as_float() else {
            type_error(t, "a float is required");
            return;
        };
        let position = if self.forward {
            compute_forward_fleet_position(friendly_position, hostile, distance)
        } else {
            compute_rear_fleet_position(friendly_position, hostile, distance)
        };
        let Some(address) = t.required_address("targetPositionAddress") else { return };
        t.send_blackboard_value(&address, Value::from_vec3(position));
        t.set_status_to_success();
    }
}

/// The repair effect the members of my group have most of: the one `remoteRepairEffectRegistryAddress` holds for most types (`None` for no registry,
/// and for the type that has none registered as many times).
fn get_repair_effect_id(t: &mut TaskCtx<'_, '_>) -> Option<Value> {
    let registry = t.value_at("remoteRepairEffectRegistryAddress")?;
    let Value::Dict(entries) = &registry else { return Some(Value::None) };
    let mut counted: Vec<(&Value, usize)> = Vec::new();
    for (_, effect_id) in entries {
        match counted.iter_mut().find(|(seen, _)| seen.py_eq(effect_id)) {
            Some((_, count)) => *count += 1,
            None => counted.push((effect_id, 1)),
        }
    }
    // `max` keeps the first of those that are equal
    let mut best: Option<(&Value, usize)> = None;
    for (effect_id, count) in counted {
        if best.is_none_or(|(_, most)| count > most) {
            best = Some((effect_id, count));
        }
    }
    Some(best.map_or(Value::None, |(effect_id, _)| effect_id.clone()))
}

/// Puts the ships of the watchlist that can be repaired in order: the most in need, and the most important, first.
#[derive(Debug, Default)]
pub struct PrioritizeLogisticsWatchlist;

impl PrioritizeLogisticsWatchlist {
    /// `get_damage_state_function`: whether there is a function of what a ship has left for the effect the group repairs with. The client keeps the
    /// function; here it is the effect that has one.
    fn get_damage_state_function(t: &mut TaskCtx<'_, '_>) -> Option<Value> {
        if !t.has_context_value("get_damage_state") {
            let effect_id = get_repair_effect_id(t)?;
            if !effect_id.is_none() {
                let function = effect_id.as_int().and_then(get_damage_state_function_by_effect_id);
                t.set_context_value("get_damage_state", function.map_or(Value::None, |_| effect_id));
            }
        }
        Some(t.get_context_value("get_damage_state"))
    }

    /// `try_add_item_with_priority`: a ship that cannot be measured is left out.
    fn try_add_item_with_priority(
        t: &mut TaskCtx<'_, '_>,
        item_id: i64,
        prioritized: &mut Vec<(f64, i64)>,
        damage_state: Option<crate::utility::logistics::DamageState>,
    ) {
        if !is_ball_in_park(t, item_id) || is_assistance_disallowed(t, item_id) || t.faulted() {
            return;
        }
        let mut priority = None;
        let caught = t.catch_exceptions(|t| priority = get_logistics_priority(t, item_id, damage_state));
        match caught {
            None => {
                if let Some(priority) = priority {
                    prioritized.push((priority, item_id));
                }
            }
            Some(
                BehaviorError::World(
                    WorldError::WrongInventoryLocation
                    | WorldError::FakeItemNotHere
                    | WorldError::ZeroDivision,
                )
                | BehaviorError::BlackboardDeleted,
            ) => {}
            Some(other) => t.raise(other),
        }
    }

    /// `_GetPrioritizedWatchlist`.
    fn get_prioritized_watchlist(t: &mut TaskCtx<'_, '_>, watchlist: &Value) -> Option<Vec<(f64, i64)>> {
        let repair_effect_id = get_repair_effect_id(t)?;
        if repair_effect_id.is_none() {
            return Some(Vec::new());
        }
        let damage_state = repair_effect_id.as_int().and_then(get_damage_state_function_by_effect_id);
        let mut prioritized = Vec::new();
        for item in watchlist.items().unwrap_or(&[]) {
            let Some(item_id) = item.as_int() else { continue };
            Self::try_add_item_with_priority(t, item_id, &mut prioritized, damage_state);
            if t.faulted() {
                return None;
            }
        }
        prioritized.sort_by(|a, b| b.partial_cmp(a).unwrap_or(std::cmp::Ordering::Equal));
        Some(prioritized)
    }
}

impl Task for PrioritizeLogisticsWatchlist {
    fn type_name(&self) -> &'static str {
        "PrioritizeLogisticsWatchlist"
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        t.set_status_to_success();
        let Some(damage_state_function) = Self::get_damage_state_function(t) else { return };
        if damage_state_function.is_none() {
            return;
        }
        let Some(watchlist) = t.value_at("watchlistAddress") else { return };
        let prioritized = if watchlist.truthy() {
            let Some(prioritized) = Self::get_prioritized_watchlist(t, &watchlist) else { return };
            prioritized
        } else {
            Vec::new()
        };
        let Some(address) = t.required_address("prioritizedWatchlistAddress") else { return };
        let entries = prioritized
            .into_iter()
            .map(|(priority, item_id)| Value::List(vec![Value::Float(priority), Value::Int(item_id)]))
            .collect();
        t.send_blackboard_value(&address, Value::List(entries));
    }
}

/// Keeps the ships at the top of the prioritized watchlist locked, as many as there are locks to spare and as far as the repairer reaches.
#[derive(Debug, Default)]
pub struct LockPrioritizedWatchlistMembers {
    is_updating_locks: bool,
}

impl LockPrioritizedWatchlistMembers {
    /// `_initialize`.
    fn initialize(t: &mut TaskCtx<'_, '_>) -> Option<()> {
        if !t.has_context_value("remote_repair_effect_id") {
            let remote_repair_effect_id = if t.has_attribute("remoteRepairEffectIdAddress") {
                t.value_at("remoteRepairEffectIdAddress")?
            } else {
                get_remote_repair_effect_id(t).map_or(Value::None, Value::Int)
            };
            t.set_context_value("remote_repair_effect_id", remote_repair_effect_id);
        }
        let reserved = i64::from(t.has_attribute("combatTargetAddress"));
        t.set_context_value("reserved_combat_target_count", Value::Int(reserved));
        Some(())
    }

    /// The repair effect of the context. An effect that is not there is one the server's lookup does not know.
    fn remote_repair_effect_id(t: &mut TaskCtx<'_, '_>) -> Option<i64> {
        let effect = t.get_context_value("remote_repair_effect_id");
        let effect_id = effect.as_int();
        if effect_id.is_none() {
            key_error(t, effect);
        }
        effect_id
    }

    /// `get_max_targets`: the locks I have, less the one for the combat target.
    fn get_max_targets(t: &mut TaskCtx<'_, '_>) -> Option<usize> {
        let max_total_targets = get_my_attribute_value(t, ATTRIBUTE_MAX_LOCKED_TARGETS)? as i64;
        let reserved = t.get_context_value("reserved_combat_target_count").as_int().unwrap_or(0);
        Some((max_total_targets - reserved).max(0) as usize)
    }

    /// `_FilterItemsInTargetingRange`: those within reach, best first, no more than there are locks for.
    fn filter_items_in_targeting_range(t: &mut TaskCtx<'_, '_>, prioritized: &Value) -> Option<Vec<i64>> {
        let me = t.my_item_id()?;
        let target_range = get_my_attribute_value(t, ATTRIBUTE_MAX_TARGET_RANGE)?;
        let effect_id = Self::remote_repair_effect_id(t)?;
        let effective_range = t.world().effect_max_effective_range_with_falloff(effect_id, me);
        let useful_range = target_range.min(effective_range);
        let mut in_range: Vec<(f64, i64)> = Vec::new();
        for entry in prioritized.items().unwrap_or(&[]) {
            let (Some(priority), Some(item_id)) = (
                entry.items().and_then(|pair| pair.first()).and_then(Value::as_float),
                entry.items().and_then(|pair| pair.get(1)).and_then(Value::as_int),
            ) else {
                continue;
            };
            if item_id == me {
                continue;
            }
            let distance = get_distance_between(t, me, item_id)?;
            if distance < useful_range {
                let falloff_multiplier = get_range_falloff_multiplier_at_distance(t, effect_id, distance);
                in_range.push((priority * falloff_multiplier, item_id));
            }
        }
        in_range.sort_by(|a, b| b.partial_cmp(a).unwrap_or(std::cmp::Ordering::Equal));
        let max_targets = Self::get_max_targets(t)?;
        Some(in_range.into_iter().take(max_targets).map(|(_, item_id)| item_id).collect())
    }

    /// `_UpdateLockedTargets`: what is locked and is not wanted is to be unlocked, what is wanted and is not locked is to be locked; the combat target
    /// is not the logistics' to unlock.
    fn update_locked_targets(t: &mut TaskCtx<'_, '_>, items_to_target: &[i64]) -> Option<()> {
        let me = t.my_item_id()?;
        let mut locked: Vec<i64> = t.world().locked_targets(me);
        if t.has_attribute("combatTargetAddress") {
            let combat_target = t.value_at("combatTargetAddress")?;
            if combat_target.truthy()
                && let Some(combat_target) = combat_target.as_int()
            {
                locked.retain(|&id| id != combat_target);
            }
        }
        let mut to_unlock: Vec<i64> =
            locked.iter().copied().filter(|id| !items_to_target.contains(id)).collect();
        let mut to_lock: Vec<i64> =
            items_to_target.iter().copied().filter(|id| !locked.contains(id)).collect();
        to_unlock.sort_unstable();
        to_unlock.dedup();
        to_lock.sort_unstable();
        to_lock.dedup();
        let ids = |ids: Vec<i64>| Value::Set(ids.into_iter().map(Value::Int).collect());
        t.schedule_call(0.0, HANDLER_UNLOCK_AND_LOCK_TARGETS, vec![ids(to_unlock), ids(to_lock)]);
        Some(())
    }

    /// `_UnlockAndLockTargets_Thread`: one thread at a time. `_TryLockTarget` lets nothing out.
    fn unlock_and_lock_targets(&mut self, t: &mut TaskCtx<'_, '_>, to_unlock: &Value, to_lock: &Value) {
        if self.is_updating_locks {
            return;
        }
        self.is_updating_locks = true;
        if let Some(me) = t.my_item_id() {
            for item_id in to_unlock.items().unwrap_or(&[]).iter().filter_map(Value::as_int) {
                t.world().remove_target(me, item_id);
            }
            for item_id in to_lock.items().unwrap_or(&[]).iter().filter_map(Value::as_int) {
                if !t.world().has_pending_target(me, Some(item_id)) {
                    let _ = t.world().add_target_ex(me, item_id);
                }
            }
        }
        self.is_updating_locks = false;
    }
}

impl Task for LockPrioritizedWatchlistMembers {
    fn type_name(&self) -> &'static str {
        "LockPrioritizedWatchlistMembers"
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        if Self::initialize(t).is_none() {
            return;
        }
        t.set_status_to_success();
        let Some(prioritized) = t.value_at("prioritizedWatchlistAddress") else { return };
        if !prioritized.truthy() {
            return;
        }
        let Some(best_candidates) = Self::filter_items_in_targeting_range(t, &prioritized) else { return };
        Self::update_locked_targets(t, &best_candidates);
    }

    fn on_call(&mut self, t: &mut TaskCtx<'_, '_>, handler: u16, args: &[Value]) {
        if handler == HANDLER_UNLOCK_AND_LOCK_TARGETS
            && let [to_unlock, to_lock] = args
        {
            self.unlock_and_lock_targets(t, to_unlock, to_lock);
        }
    }
}

/// Registers the repair effect of my type, for the others of the group to count.
#[derive(Debug, Default)]
pub struct RegisterRepairEffectForType;

impl Task for RegisterRepairEffectForType {
    fn type_name(&self) -> &'static str {
        "RegisterRepairEffectForType"
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        t.set_status_to_success();
        let Some(address) = t.required_address("remoteRepairEffectRegistryAddress") else { return };
        let last = t.get_last_blackboard_value(&address);
        let stored = last.truthy();
        let mut registry = if stored { last } else { Value::Dict(Vec::new()) };
        let Some(slim) = t.ctx().my_slim_item.clone() else {
            t.raise(BehaviorError::Task(
                "AttributeError: 'NoneType' object has no attribute 'typeID'".to_string(),
            ));
            return;
        };
        let type_id = Value::Int(slim.type_id);
        if registry.dict_get(&type_id).is_some() {
            return;
        }
        let remote_repair_effect_id = get_remote_repair_effect_id(t).map_or(Value::None, Value::Int);
        registry.dict_insert(type_id, remote_repair_effect_id);
        // the registry that is there is changed, and sent again
        if stored {
            update_stored_value(t, &address, &registry);
        }
        t.send_blackboard_value(&address, registry);
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::blackboards::{Address, ScopeType};
    use crate::env::testing::TestGroup;
    use crate::tree::tests::{Rig, with_task};
    use crate::tree::{Attrs, Status};

    fn item(name: &str) -> Value {
        Address::new(ScopeType::Item, name).to_value()
    }

    fn ints(ids: &[i64]) -> Value {
        Value::List(ids.iter().map(|&id| Value::Int(id)).collect())
    }

    /// A ship of a player at a place.
    fn ship(rig: &mut Rig, id: i64, position: [f64; 3], owner: i64) {
        rig.world.add_ball(id, position, 500);
        let slim = rig.world.slims.get_mut(&id).unwrap();
        slim.category_id = CATEGORY_SHIP;
        slim.group_id = 25;
        slim.owner_id = Some(owner);
    }

    // ---- SelectDesiredAnchorPosition ----

    fn anchor_attrs() -> Attrs {
        Attrs::new()
            .with("desiredPositionAddress", item("Desired"))
            .with("friendlyOwners", ints(&[7]))
            .with("hostileOwners", ints(&[8]))
            .with("proximityFraction", 0.5)
    }

    /// Me and a friend at 0 and 10, an enemy at 100, all in the bubble 5; the logistics of my group reach 20000 and 30000.
    fn anchor_rig() -> Rig {
        let mut rig = Rig::new();
        ship(&mut rig, 1, [0.0; 3], 7);
        ship(&mut rig, 2, [10.0, 0.0, 0.0], 7);
        ship(&mut rig, 3, [100.0, 0.0, 0.0], 8);
        rig.world.balls.get_mut(&1).unwrap().new_bubble_id = 5;
        rig.world.bubbles.insert(5, vec![1, 2, 3]);
        rig.world.groups.insert(10, TestGroup { members: vec![1, 2], ..Default::default() });
        rig.world.attributes.insert((1, ATTRIBUTE_NPC_ASSISTANCE_RANGE), 20000.0);
        rig.world.attributes.insert((2, ATTRIBUTE_NPC_ASSISTANCE_RANGE), 30000.0);
        rig
    }

    #[test]
    fn with_enemies_about_the_logistics_stand_behind_their_friends_by_a_fraction_of_the_shortest_reach() {
        let mut rig = anchor_rig();
        assert_eq!(rig.run(Box::new(SelectDesiredAnchorPosition), anchor_attrs()), Status::Success);
        assert_eq!(rig.last_error, None);
        // the friends' centre is 5, the enemy is at 100: 20000 * 0.5 further away from the enemy
        assert_eq!(rig.last(ScopeType::Item, "Desired"), Value::from_vec3([-9995.0, 0.0, 0.0]));
    }

    #[test]
    fn a_member_that_cannot_repair_does_not_count_for_the_reach() {
        let mut rig = anchor_rig();
        rig.world.attributes.insert((1, ATTRIBUTE_NPC_ASSISTANCE_RANGE), 0.0);
        assert_eq!(rig.run(Box::new(SelectDesiredAnchorPosition), anchor_attrs()), Status::Success);
        assert_eq!(rig.last(ScopeType::Item, "Desired"), Value::from_vec3([-14995.0, 0.0, 0.0]));
    }

    #[test]
    fn without_enemies_the_logistics_stand_at_the_centre_of_their_friends() {
        let mut rig = anchor_rig();
        rig.world.bubbles.insert(5, vec![1, 2]);
        assert_eq!(rig.run(Box::new(SelectDesiredAnchorPosition), anchor_attrs()), Status::Success);
        assert_eq!(rig.last(ScopeType::Item, "Desired"), Value::from_vec3([5.0, 0.0, 0.0]));
    }

    #[test]
    fn only_what_can_be_friend_or_foe_in_my_bubble_is_looked_at() {
        let mut rig = anchor_rig();
        // a ship of a group that is ignored, a drone of a friend, a charge of a friend, one of nobody I know, a friend that is not in my bubble,
        // a ball that has no slim item, and balls that are not real
        ship(&mut rig, 4, [1000.0, 0.0, 0.0], 7);
        rig.world.slims.get_mut(&4).unwrap().group_id = GROUP_SENTRY_GUN;
        ship(&mut rig, 5, [1000.0, 0.0, 0.0], 9);
        ship(&mut rig, 6, [1000.0, 0.0, 0.0], 7);
        rig.world.slims.get_mut(&6).unwrap().category_id = 8;
        ship(&mut rig, 7, [1000.0, 0.0, 0.0], 7);
        rig.world.slims.get_mut(&7).unwrap().category_id = CATEGORY_DRONE;
        ship(&mut rig, 8, [1000.0, 0.0, 0.0], 7);
        rig.world
            .balls
            .insert(9, crate::env::Ball { id: 9, position: [1000.0, 0.0, 0.0], ..Default::default() });
        ship(&mut rig, 0, [1000.0, 0.0, 0.0], 7);
        ship(&mut rig, -3, [1000.0, 0.0, 0.0], 7);
        rig.world.bubbles.insert(5, vec![1, 2, 4, 5, 6, 9, -3, 0]);
        let mut attrs = anchor_attrs();
        attrs.set("hostileOwners", ints(&[]));
        assert_eq!(rig.run(Box::new(SelectDesiredAnchorPosition), attrs.clone()), Status::Success);
        assert_eq!(rig.last(ScopeType::Item, "Desired"), Value::from_vec3([5.0, 0.0, 0.0]));
        // the drone is a friend to count
        rig.world.bubbles.insert(5, vec![1, 2, 7]);
        let mut rig2 = rig;
        assert_eq!(rig2.run(Box::new(SelectDesiredAnchorPosition), attrs), Status::Success);
        let centre = rig2.last(ScopeType::Item, "Desired").as_vec3().unwrap();
        assert!(
            (centre[0] - 1010.0 / 3.0).abs() < 1e-9 && centre[1] == 0.0 && centre[2] == 0.0,
            "{centre:?}"
        );
    }

    #[test]
    fn nowhere_to_stand_is_a_failure_that_says_so_on_the_blackboard() {
        // no friends
        let mut rig = anchor_rig();
        rig.world.bubbles.insert(5, vec![3]);
        rig.post(ScopeType::Item, "Desired", Value::from_vec3([9.0, 9.0, 9.0]));
        assert_eq!(rig.run(Box::new(SelectDesiredAnchorPosition), anchor_attrs()), Status::Failure);
        assert_eq!(rig.last_error, None);
        assert_eq!(rig.last(ScopeType::Item, "Desired"), Value::None);
        // no reach
        let mut rig = anchor_rig();
        rig.world.attributes.clear();
        rig.post(ScopeType::Item, "Desired", Value::from_vec3([9.0, 9.0, 9.0]));
        assert_eq!(rig.run(Box::new(SelectDesiredAnchorPosition), anchor_attrs()), Status::Failure);
        assert_eq!(rig.last(ScopeType::Item, "Desired"), Value::None);
        // nobody at all, no enemies
        let mut rig = anchor_rig();
        rig.world.bubbles.insert(5, vec![]);
        assert_eq!(rig.run(Box::new(SelectDesiredAnchorPosition), anchor_attrs()), Status::Failure);
    }

    #[test]
    fn a_ball_or_a_bubble_or_a_group_that_is_not_there_is_a_fault() {
        let mut rig = anchor_rig();
        rig.world.balls.remove(&1);
        rig.run(Box::new(SelectDesiredAnchorPosition), anchor_attrs());
        assert!(matches!(rig.last_error, Some(BehaviorError::World(WorldError::Key(_)))));
        let mut rig = anchor_rig();
        rig.world.bubbles.clear();
        rig.run(Box::new(SelectDesiredAnchorPosition), anchor_attrs());
        assert!(matches!(rig.last_error, Some(BehaviorError::World(WorldError::Key(_)))));
        // a friend that is in the bubble and whose ball is gone
        let mut rig = anchor_rig();
        rig.world.balls.remove(&2);
        rig.run(Box::new(SelectDesiredAnchorPosition), anchor_attrs());
        assert!(matches!(rig.last_error, Some(BehaviorError::World(WorldError::Key(_)))));
        // no group to ask about the reach
        let mut rig = anchor_rig();
        rig.ctx.my_entity_group_id = None;
        rig.run(Box::new(SelectDesiredAnchorPosition), anchor_attrs());
        assert!(matches!(rig.last_error, Some(BehaviorError::Task(_))));
    }

    // ---- SelectGroupMemberBasedOnLowestAttributeValue ----

    fn member_attrs() -> Attrs {
        Attrs::new().with("attributeId", 9i64).with("selectedIdAddress", item("Selected"))
    }

    fn member_rig() -> Rig {
        let mut rig = Rig::new();
        for (id, type_id) in [(1, 500), (2, 500), (3, 501), (5, 500)] {
            rig.world.add_ball(id, [0.0; 3], type_id);
        }
        rig.world.type_effects.insert((500, EFFECT_BEHAVIOR_NPC_REMOTE_ARMOR_REPAIR));
        // 4 has no slim item
        rig.world.groups.insert(10, TestGroup { members: vec![1, 2, 3, 4, 5], ..Default::default() });
        rig.world.attributes.insert((1, 9), 5.0);
        rig.world.attributes.insert((2, 9), 3.0);
        rig.world.attributes.insert((3, 9), 1.0);
        rig.world.attributes.insert((5, 9), 3.0);
        rig
    }

    #[test]
    fn the_member_with_the_lowest_value_of_the_attribute_among_those_that_repair_is_selected() {
        let mut rig = member_rig();
        assert_eq!(
            rig.run(Box::new(SelectGroupMemberBasedOnLowestAttributeValue), member_attrs()),
            Status::Success
        );
        // 3 has the lowest and cannot repair, 4 is not there, 2 and 5 are the same and the first is the one
        assert_eq!(rig.last(ScopeType::Item, "Selected"), Value::Int(2));
    }

    #[test]
    fn no_member_that_repairs_is_a_failure_and_a_group_that_is_not_there_is_a_fault() {
        let mut rig = member_rig();
        rig.world.type_effects.clear();
        assert_eq!(
            rig.run(Box::new(SelectGroupMemberBasedOnLowestAttributeValue), member_attrs()),
            Status::Failure
        );
        assert_eq!(rig.last(ScopeType::Item, "Selected"), Value::None);
        let mut rig = member_rig();
        rig.ctx.my_entity_group_id = None;
        rig.run(Box::new(SelectGroupMemberBasedOnLowestAttributeValue), member_attrs());
        assert!(rig.last_error.is_some());
        // the attribute of a member that cannot be read
        let mut rig = member_rig();
        rig.world.attribute_errors.insert((2, 9), WorldError::WrongInventoryLocation);
        rig.run(Box::new(SelectGroupMemberBasedOnLowestAttributeValue), member_attrs());
        assert_eq!(rig.last_error, Some(BehaviorError::World(WorldError::WrongInventoryLocation)));
    }

    // ---- SetRearFleetPosition, SetForwardFleetPosition ----

    fn position_attrs() -> Attrs {
        Attrs::new()
            .with("hostilePositionAddress", item("Hostile"))
            .with("anchorBallIdAddress", item("Anchor"))
            .with("distanceAddress", item("Distance"))
            .with("targetPositionAddress", item("Target"))
    }

    fn position_rig() -> Rig {
        let mut rig = Rig::new();
        rig.world.add_ball(2, [10.0, 0.0, 0.0], 500);
        rig.post(ScopeType::Item, "Hostile", Value::from_vec3([0.0; 3]));
        rig.post(ScopeType::Item, "Anchor", Value::Int(2));
        rig.post(ScopeType::Item, "Distance", Value::Float(4.0));
        rig
    }

    #[test]
    fn the_rear_is_further_from_the_enemy_than_the_anchor_and_the_forward_is_that_far_from_the_enemy() {
        let mut rig = position_rig();
        assert_eq!(rig.run(Box::new(SetFleetPosition::rear()), position_attrs()), Status::Success);
        assert_eq!(rig.last(ScopeType::Item, "Target"), Value::from_vec3([14.0, 0.0, 0.0]));
        let mut rig = position_rig();
        assert_eq!(rig.run(Box::new(SetFleetPosition::forward()), position_attrs()), Status::Success);
        assert_eq!(rig.last(ScopeType::Item, "Target"), Value::from_vec3([4.0, 0.0, 0.0]));
        assert_eq!(SetFleetPosition::rear().type_name(), "SetRearFleetPosition");
        assert_eq!(SetFleetPosition::forward().type_name(), "SetForwardFleetPosition");
    }

    #[test]
    fn no_enemy_no_anchor_or_an_anchor_that_is_gone_is_a_failure() {
        for (what, spoil) in [("no enemy", Value::None), ("an enemy that is nowhere", Value::Tuple(vec![]))] {
            let mut rig = position_rig();
            rig.post(ScopeType::Item, "Hostile", spoil);
            assert_eq!(
                rig.run(Box::new(SetFleetPosition::rear()), position_attrs()),
                Status::Failure,
                "{what}"
            );
            assert_eq!(rig.last_error, None, "{what}");
        }
        let mut rig = position_rig();
        rig.post(ScopeType::Item, "Anchor", Value::None);
        assert_eq!(rig.run(Box::new(SetFleetPosition::rear()), position_attrs()), Status::Failure);
        let mut rig = position_rig();
        rig.world.add_ball(0, [10.0, 0.0, 0.0], 500);
        rig.post(ScopeType::Item, "Anchor", Value::Int(0));
        assert_eq!(rig.run(Box::new(SetFleetPosition::rear()), position_attrs()), Status::Failure);
        let mut rig = position_rig();
        rig.post(ScopeType::Item, "Anchor", Value::Int(77));
        assert_eq!(rig.run(Box::new(SetFleetPosition::rear()), position_attrs()), Status::Failure);
        assert_eq!(rig.last_error, None);
        assert_eq!(rig.last(ScopeType::Item, "Target"), Value::None);
    }

    #[test]
    fn a_distance_that_is_not_there_is_a_fault() {
        let mut rig = position_rig();
        rig.post(ScopeType::Item, "Distance", Value::None);
        rig.run(Box::new(SetFleetPosition::rear()), position_attrs());
        assert!(matches!(rig.last_error, Some(BehaviorError::Task(ref m)) if m.starts_with("TypeError")));
    }

    // ---- PrioritizeLogisticsWatchlist ----

    fn prioritize_attrs() -> Attrs {
        Attrs::new()
            .with("remoteRepairEffectRegistryAddress", item("Registry"))
            .with("watchlistAddress", item("Watchlist"))
            .with("prioritizedWatchlistAddress", item("Prioritized"))
    }

    fn registry(entries: &[(i64, Value)]) -> Value {
        Value::Dict(entries.iter().map(|(type_id, effect)| (Value::Int(*type_id), effect.clone())).collect())
    }

    fn armor() -> Value {
        Value::Int(EFFECT_NPC_BEHAVIOR_REMOTE_ARMOR_REPAIRER)
    }

    fn shield() -> Value {
        Value::Int(EFFECT_NPC_BEHAVIOR_REMOTE_SHIELD_BOOSTER)
    }

    /// The ships 2 (a member, half its armor), 3 (not a member, a tenth of its armor gone), 4 (not in the park), 5 (does not want help),
    /// 6 (no armor at all), 7 (that is not where the server looked for it) and 8 (a fake item that is not here).
    fn prioritize_rig() -> Rig {
        let mut rig = Rig::new();
        rig.world.groups.insert(10, TestGroup { members: vec![1, 2], ..Default::default() });
        for id in [2, 3, 5, 6, 7, 8] {
            rig.world.add_ball(id, [0.0; 3], 500);
            rig.world.attributes.insert((id, ATTRIBUTE_REMOTE_REPAIR_IMPEDANCE), 1.0);
            rig.world.attributes.insert((id, ATTRIBUTE_ARMOR_HP), 100.0);
        }
        rig.world.attributes.insert((2, ATTRIBUTE_ARMOR_DAMAGE), 50.0);
        rig.world.attributes.insert((3, ATTRIBUTE_ARMOR_DAMAGE), 10.0);
        rig.world.attributes.insert((5, ATTRIBUTE_DISALLOW_ASSISTANCE), 1.0);
        rig.world.attributes.insert((6, ATTRIBUTE_ARMOR_HP), 0.0);
        rig.world.attribute_errors.insert((7, ATTRIBUTE_ARMOR_HP), WorldError::WrongInventoryLocation);
        rig.world.attribute_errors.insert((8, ATTRIBUTE_ARMOR_DAMAGE), WorldError::FakeItemNotHere);
        rig.post(ScopeType::Item, "Watchlist", ints(&[3, 4, 5, 6, 7, 8, 2]));
        rig
    }

    #[test]
    fn the_watchlist_is_put_in_order_of_need_and_what_cannot_be_measured_or_helped_is_left_out() {
        let mut rig = prioritize_rig();
        // two types repair armor and one shields: armor it is
        rig.post(ScopeType::Item, "Registry", registry(&[(700, shield()), (701, armor()), (702, armor())]));
        assert_eq!(rig.run(Box::new(PrioritizeLogisticsWatchlist), prioritize_attrs()), Status::Success);
        assert_eq!(rig.last_error, None);
        let member = (0.1 + (1.0 - (100.0 - 50.0) / 100.0)) * 1.0;
        let outsider = (0.0 + (1.0 - (100.0 - 10.0) / 100.0)) * 1.0;
        assert_eq!(
            rig.last(ScopeType::Item, "Prioritized"),
            Value::List(vec![
                Value::List(vec![Value::Float(member), Value::Int(2)]),
                Value::List(vec![Value::Float(outsider), Value::Int(3)]),
            ])
        );
    }

    #[test]
    fn the_repair_effect_of_the_most_types_and_of_the_first_of_a_tie_is_the_one_that_counts() {
        let mut rig = prioritize_rig();
        rig.world.attributes.insert((2, ATTRIBUTE_SHIELD_CAPACITY), 400.0);
        rig.world.attributes.insert((2, ATTRIBUTE_SHIELD_CHARGE), 100.0);
        rig.post(ScopeType::Item, "Registry", registry(&[(700, shield()), (701, armor())]));
        rig.post(ScopeType::Item, "Watchlist", ints(&[2]));
        rig.run(Box::new(PrioritizeLogisticsWatchlist), prioritize_attrs());
        assert_eq!(
            rig.last(ScopeType::Item, "Prioritized"),
            Value::List(vec![Value::List(vec![Value::Float(0.1 + (1.0 - 100.0 / 400.0)), Value::Int(2)])]),
            "a tie: the first"
        );
        let mut rig = prioritize_rig();
        rig.world.attributes.insert((2, ATTRIBUTE_SHIELD_CAPACITY), 400.0);
        rig.world.attributes.insert((2, ATTRIBUTE_SHIELD_CHARGE), 100.0);
        rig.post(ScopeType::Item, "Registry", registry(&[(700, armor()), (701, shield()), (702, shield())]));
        rig.post(ScopeType::Item, "Watchlist", ints(&[2]));
        rig.run(Box::new(PrioritizeLogisticsWatchlist), prioritize_attrs());
        assert_eq!(
            rig.last(ScopeType::Item, "Prioritized"),
            Value::List(vec![Value::List(vec![Value::Float(0.1 + (1.0 - 100.0 / 400.0)), Value::Int(2)])]),
        );
    }

    #[test]
    fn an_empty_watchlist_is_an_empty_prioritized_one_and_no_repair_effect_is_no_answer() {
        let mut rig = prioritize_rig();
        rig.post(ScopeType::Item, "Registry", registry(&[(700, armor())]));
        rig.post(ScopeType::Item, "Watchlist", ints(&[]));
        rig.post(ScopeType::Item, "Prioritized", ints(&[1]));
        assert_eq!(rig.run(Box::new(PrioritizeLogisticsWatchlist), prioritize_attrs()), Status::Success);
        assert_eq!(rig.last(ScopeType::Item, "Prioritized"), Value::List(vec![]));
        // no registry
        let mut rig = prioritize_rig();
        rig.post(ScopeType::Item, "Prioritized", ints(&[1]));
        assert_eq!(rig.run(Box::new(PrioritizeLogisticsWatchlist), prioritize_attrs()), Status::Success);
        assert_eq!(rig.last(ScopeType::Item, "Prioritized"), ints(&[1]), "nothing was said");
        // an empty registry
        let mut rig = prioritize_rig();
        rig.post(ScopeType::Item, "Registry", registry(&[]));
        rig.post(ScopeType::Item, "Prioritized", ints(&[1]));
        rig.run(Box::new(PrioritizeLogisticsWatchlist), prioritize_attrs());
        assert_eq!(rig.last(ScopeType::Item, "Prioritized"), ints(&[1]));
        // an effect that repairs nothing that has a function of what is left
        let mut rig = prioritize_rig();
        rig.post(ScopeType::Item, "Registry", registry(&[(700, Value::Int(42))]));
        rig.post(ScopeType::Item, "Prioritized", ints(&[1]));
        rig.run(Box::new(PrioritizeLogisticsWatchlist), prioritize_attrs());
        assert_eq!(rig.last(ScopeType::Item, "Prioritized"), ints(&[1]));
        // a type that has no effect registered
        let mut rig = prioritize_rig();
        rig.post(ScopeType::Item, "Registry", registry(&[(700, Value::None)]));
        rig.post(ScopeType::Item, "Prioritized", ints(&[1]));
        rig.run(Box::new(PrioritizeLogisticsWatchlist), prioritize_attrs());
        assert_eq!(rig.last(ScopeType::Item, "Prioritized"), ints(&[1]));
    }

    #[test]
    fn the_function_that_was_found_is_kept_and_a_ship_measured_with_a_function_that_is_gone_is_a_fault() {
        let (rig, _) = with_task(
            |rig| {
                *rig = prioritize_rig();
                // the registry has changed to an effect that repairs nothing since the function was found
                rig.post(ScopeType::Item, "Registry", registry(&[(700, Value::Int(42))]));
                rig.post(ScopeType::Item, "Watchlist", ints(&[2]));
            },
            |t| {
                for (name, address) in [
                    ("remoteRepairEffectRegistryAddress", item("Registry")),
                    ("watchlistAddress", item("Watchlist")),
                    ("prioritizedWatchlistAddress", item("Prioritized")),
                ] {
                    t.set_attribute(name, address);
                }
                t.set_context_value("get_damage_state", armor());
                PrioritizeLogisticsWatchlist.on_enter(t);
            },
        );
        assert!(matches!(rig.last_error, Some(BehaviorError::Task(ref m)) if m.contains("not callable")));
    }

    #[test]
    fn a_ship_that_the_blackboard_of_the_group_cannot_place_is_left_out() {
        let mut rig = prioritize_rig();
        rig.post(ScopeType::Item, "Registry", registry(&[(700, armor())]));
        // the blackboard of the group is gone: who the commander is cannot be asked, of a member
        rig.shared.blackboards.get_blackboard(crate::blackboards::entity_group_scope(10)).unwrap();
        rig.shared.blackboards.remove_blackboard(crate::blackboards::entity_group_scope(10));
        assert_eq!(rig.run(Box::new(PrioritizeLogisticsWatchlist), prioritize_attrs()), Status::Success);
        assert_eq!(rig.last_error, None);
        let outsider = (0.0 + (1.0 - (100.0 - 10.0) / 100.0)) * 1.0;
        assert_eq!(
            rig.last(ScopeType::Item, "Prioritized"),
            Value::List(vec![Value::List(vec![Value::Float(outsider), Value::Int(3)])])
        );
    }

    #[test]
    fn a_fault_that_is_not_one_of_the_measuring_is_not_eaten() {
        let mut rig = prioritize_rig();
        rig.post(ScopeType::Item, "Registry", registry(&[(700, armor())]));
        // the group is gone: whether a ship is a member cannot be told
        rig.ctx.my_entity_group_id = None;
        rig.run(Box::new(PrioritizeLogisticsWatchlist), prioritize_attrs());
        assert!(matches!(rig.last_error, Some(BehaviorError::Task(_))));
        // what reading whether help is wanted raises is not caught either
        let mut rig = prioritize_rig();
        rig.post(ScopeType::Item, "Registry", registry(&[(700, armor())]));
        rig.world.attribute_errors.insert((3, ATTRIBUTE_DISALLOW_ASSISTANCE), WorldError::FakeItemNotHere);
        rig.run(Box::new(PrioritizeLogisticsWatchlist), prioritize_attrs());
        assert_eq!(rig.last_error, Some(BehaviorError::World(WorldError::FakeItemNotHere)));
    }

    // ---- LockPrioritizedWatchlistMembers ----

    fn lock_attrs() -> Attrs {
        Attrs::new()
            .with("prioritizedWatchlistAddress", item("Prioritized"))
            .with("remoteRepairEffectIdAddress", item("Effect"))
    }

    /// I (1) can lock 3 ships and see 50000 m; the effect reaches 30000 m. In the watchlist are 1 (me), and 2, 3, 4, 5 and 6 at 1000, 40000, 2000,
    /// 30000 and 3000 m. I have 9 and 6 locked.
    fn lock_rig() -> Rig {
        let mut rig = Rig::new();
        for (id, distance) in [(2, 1000.0), (3, 40000.0), (4, 2000.0), (5, 30000.0), (6, 3000.0)] {
            rig.world.add_ball(id, [0.0; 3], 500);
            rig.world.set_dist(1, id, distance);
        }
        rig.world.attributes.insert((1, ATTRIBUTE_MAX_TARGET_RANGE), 50000.0);
        rig.world.attributes.insert((1, ATTRIBUTE_MAX_LOCKED_TARGETS), 3.0);
        rig.world.max_effective_ranges.insert(EFFECT_NPC_BEHAVIOR_REMOTE_ARMOR_REPAIRER, 30000.0);
        rig.world.targets.insert(1, vec![9, 6]);
        rig.post(ScopeType::Item, "Effect", armor());
        let entries = [(1, 0.95), (2, 0.9), (3, 0.8), (4, 0.7), (5, 0.6), (6, 0.5)];
        rig.post(
            ScopeType::Item,
            "Prioritized",
            Value::List(
                entries
                    .iter()
                    .map(|&(id, priority)| Value::List(vec![Value::Float(priority), Value::Int(id)]))
                    .collect(),
            ),
        );
        rig
    }

    #[test]
    fn the_best_of_those_in_reach_are_locked_and_what_is_locked_and_not_wanted_is_let_go() {
        let mut rig = lock_rig();
        assert_eq!(
            rig.run(Box::new(LockPrioritizedWatchlistMembers::default()), lock_attrs()),
            Status::Success
        );
        assert_eq!(rig.last_error, None);
        assert!(rig.world.take_calls().is_empty(), "the locking is a thread of its own");
        rig.deliver_scheduled();
        assert_eq!(rig.last_error, None);
        // me, 3 (too far), and 5 (as far as the effect reaches) are not candidates; of 2, 4 and 6, all are wanted: 6 is locked already, 9 is not wanted
        assert_eq!(
            rig.world.take_calls(),
            ["remove_target(1, 9)", "add_target_ex(1, 2)", "add_target_ex(1, 4)"]
        );
    }

    #[test]
    fn the_target_of_the_combat_is_not_the_logistics_to_let_go_and_takes_a_lock() {
        let mut rig = lock_rig();
        rig.post(ScopeType::Item, "Combat", Value::Int(9));
        let attrs = lock_attrs().with("combatTargetAddress", item("Combat"));
        assert_eq!(rig.run(Box::new(LockPrioritizedWatchlistMembers::default()), attrs), Status::Success);
        rig.deliver_scheduled();
        // two locks for them: 2 and 4; 6 is locked and not wanted, 9 is locked and is not to be let go
        assert_eq!(
            rig.world.take_calls(),
            ["remove_target(1, 6)", "add_target_ex(1, 2)", "add_target_ex(1, 4)"]
        );
    }

    #[test]
    fn a_lock_that_is_pending_is_not_asked_for_again() {
        let mut rig = lock_rig();
        rig.world.pending_targets.insert((1, 4));
        rig.run(Box::new(LockPrioritizedWatchlistMembers::default()), lock_attrs());
        rig.deliver_scheduled();
        assert_eq!(rig.world.take_calls(), ["remove_target(1, 9)", "add_target_ex(1, 2)"]);
    }

    #[test]
    fn a_lock_that_fails_does_not_keep_the_next_from_being_tried() {
        let mut rig = lock_rig();
        rig.world.activation_error = Some(WorldError::User("Nope".into()));
        rig.run(Box::new(LockPrioritizedWatchlistMembers::default()), lock_attrs());
        rig.deliver_scheduled();
        assert_eq!(rig.last_error, None);
        assert_eq!(rig.world.take_calls(), ["remove_target(1, 9)"]);
    }

    #[test]
    fn the_reach_is_the_smaller_of_what_i_can_see_and_what_the_effect_reaches_and_the_falloff_weighs_the_priority()
     {
        let mut rig = lock_rig();
        rig.world.attributes.insert((1, ATTRIBUTE_MAX_TARGET_RANGE), 1500.0);
        rig.world.targets.clear();
        rig.run(Box::new(LockPrioritizedWatchlistMembers::default()), lock_attrs());
        rig.deliver_scheduled();
        assert_eq!(rig.world.take_calls(), ["add_target_ex(1, 2)"], "only 2 is within 1500");
        // 2 is far in the falloff of the effect: what 4 and 6 have is more, and there are locks for two
        let mut rig = lock_rig();
        rig.world.attributes.insert((1, ATTRIBUTE_MAX_LOCKED_TARGETS), 2.0);
        rig.world.falloff_at_distance.insert(1000, 0.1);
        rig.world.targets.clear();
        rig.run(Box::new(LockPrioritizedWatchlistMembers::default()), lock_attrs());
        rig.deliver_scheduled();
        assert_eq!(rig.world.take_calls(), ["add_target_ex(1, 4)", "add_target_ex(1, 6)"]);
    }

    #[test]
    fn there_are_never_less_than_no_locks_to_spare() {
        let mut rig = lock_rig();
        rig.world.attributes.insert((1, ATTRIBUTE_MAX_LOCKED_TARGETS), 0.0);
        rig.post(ScopeType::Item, "Combat", Value::Int(9));
        rig.world.targets.clear();
        let attrs = lock_attrs().with("combatTargetAddress", item("Combat"));
        assert_eq!(rig.run(Box::new(LockPrioritizedWatchlistMembers::default()), attrs), Status::Success);
        rig.deliver_scheduled();
        assert!(rig.world.take_calls().is_empty());
    }

    #[test]
    fn nothing_prioritized_is_nothing_done_and_the_repair_effect_is_mine_unless_the_blackboard_says() {
        let mut rig = lock_rig();
        rig.post(ScopeType::Item, "Prioritized", Value::List(vec![]));
        assert_eq!(
            rig.run(Box::new(LockPrioritizedWatchlistMembers::default()), lock_attrs()),
            Status::Success
        );
        rig.deliver_scheduled();
        assert!(rig.world.take_calls().is_empty());
        // the effect of my own type
        let mut rig = lock_rig();
        rig.world.max_effective_ranges.clear();
        rig.world.max_effective_ranges.insert(EFFECT_NPC_BEHAVIOR_REMOTE_SHIELD_BOOSTER, 1500.0);
        rig.ctx.my_slim_item = Some(crate::env::SlimItem { item_id: 1, type_id: 600, ..Default::default() });
        rig.world.type_effects.insert((600, EFFECT_NPC_BEHAVIOR_REMOTE_SHIELD_BOOSTER));
        rig.world.targets.clear();
        let attrs = Attrs::new().with("prioritizedWatchlistAddress", item("Prioritized"));
        assert_eq!(rig.run(Box::new(LockPrioritizedWatchlistMembers::default()), attrs), Status::Success);
        rig.deliver_scheduled();
        assert_eq!(rig.world.take_calls(), ["add_target_ex(1, 2)"]);
        // no effect at all: the server does not know it
        let mut rig = lock_rig();
        rig.post(ScopeType::Item, "Effect", Value::None);
        rig.run(Box::new(LockPrioritizedWatchlistMembers::default()), lock_attrs());
        assert!(matches!(rig.last_error, Some(BehaviorError::World(WorldError::Key(_)))));
    }

    #[test]
    fn only_one_thread_at_a_time_locks_and_unlocks() {
        let (rig, answer) = with_task(
            |_| {},
            |t| {
                let args = [Value::Set(vec![Value::Int(9)]), Value::Set(vec![Value::Int(2)])];
                let mut busy = LockPrioritizedWatchlistMembers { is_updating_locks: true };
                busy.on_call(t, HANDLER_UNLOCK_AND_LOCK_TARGETS, &args);
                let mut free = LockPrioritizedWatchlistMembers::default();
                free.on_call(t, HANDLER_UNLOCK_AND_LOCK_TARGETS + 1, &args);
                (busy.is_updating_locks, free.is_updating_locks)
            },
        );
        assert_eq!(answer, Some((true, false)));
        assert!(rig.world.calls.is_empty());
        let (mut rig, answer) = with_task(
            |_| {},
            |t| {
                let args = [Value::Set(vec![Value::Int(9)]), Value::Set(vec![Value::Int(2)])];
                let mut free = LockPrioritizedWatchlistMembers::default();
                free.on_call(t, HANDLER_UNLOCK_AND_LOCK_TARGETS, &args);
                free.is_updating_locks
            },
        );
        assert_eq!(answer, Some(false), "the thread is done");
        assert_eq!(rig.world.take_calls(), ["remove_target(1, 9)", "add_target_ex(1, 2)"]);
    }

    // ---- RegisterRepairEffectForType ----

    fn register_attrs() -> Attrs {
        Attrs::new().with("remoteRepairEffectRegistryAddress", item("Registry"))
    }

    fn register_rig() -> Rig {
        let mut rig = Rig::new();
        rig.ctx.my_slim_item = Some(crate::env::SlimItem { item_id: 1, type_id: 501, ..Default::default() });
        rig.world.type_effects.insert((501, EFFECT_NPC_BEHAVIOR_REMOTE_SHIELD_BOOSTER));
        rig
    }

    #[test]
    fn my_type_is_registered_with_its_repair_effect() {
        let mut rig = register_rig();
        assert_eq!(rig.run(Box::new(RegisterRepairEffectForType), register_attrs()), Status::Success);
        assert_eq!(rig.last(ScopeType::Item, "Registry"), registry(&[(501, shield())]));
        // a type that repairs nothing is registered as that
        let mut rig = register_rig();
        rig.world.type_effects.clear();
        rig.run(Box::new(RegisterRepairEffectForType), register_attrs());
        assert_eq!(rig.last(ScopeType::Item, "Registry"), registry(&[(501, Value::None)]));
    }

    #[test]
    fn the_registry_that_is_there_is_changed_in_place_and_sent_again_unless_the_type_is_in_it() {
        let mut rig = register_rig();
        rig.post(ScopeType::Item, "Registry", registry(&[(500, armor())]));
        assert_eq!(rig.run(Box::new(RegisterRepairEffectForType), register_attrs()), Status::Success);
        let both = registry(&[(500, armor()), (501, shield())]);
        assert_eq!(rig.history("Registry"), [both.clone(), both], "what was sent before is what it is now");
        // the type is in it
        let mut rig = register_rig();
        rig.post(ScopeType::Item, "Registry", registry(&[(501, armor())]));
        assert_eq!(rig.run(Box::new(RegisterRepairEffectForType), register_attrs()), Status::Success);
        assert_eq!(rig.history("Registry"), [registry(&[(501, armor())])]);
        // an empty registry is not the one that is kept
        let mut rig = register_rig();
        rig.post(ScopeType::Item, "Registry", registry(&[]));
        rig.run(Box::new(RegisterRepairEffectForType), register_attrs());
        assert_eq!(rig.history("Registry"), [registry(&[(501, shield())]), registry(&[])], "newest first");
    }

    #[test]
    fn a_slim_item_that_is_not_there_is_a_fault() {
        let mut rig = register_rig();
        rig.ctx.my_slim_item = None;
        rig.run(Box::new(RegisterRepairEffectForType), register_attrs());
        assert!(matches!(rig.last_error, Some(BehaviorError::Task(ref m)) if m.contains("typeID")));
    }
}
