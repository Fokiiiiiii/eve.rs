//! `behaviors/actions/group.py`: what a group of NPCs counts, chooses and does as a group.

use crate::constants::behaviorroles::COMBAT_ROLES;
use crate::constants::blackboardchannels::{
    COMBAT_ORBIT_RANGE_CHANNEL_NAME, COMBAT_WARP_AT_DISTANCE_CHANNEL_NAME, ROLE_MESSAGE_CHANNEL,
};
use crate::constants::combat::{
    COMBAT_MAX_EFFECTIVE_FALLOFF_DISTANCE_FACTOR, COMBAT_MIN_EFFECTIVE_COMBAT_RANGE_FACTOR,
};
use crate::constants::loot::extra_loot_tables_for_owner;
use crate::consts::{EFFECT_MISSILE_LAUNCHING_FOR_ENTITY, EFFECT_TARGET_ATTACK};
use crate::groups::mixin::{get_group_owner_id, get_member_bubble_set, get_member_id_list};
use crate::tree::{BehaviorError, Task, TaskCtx};
use crate::utility::ballparks::{
    get_ball, get_distance_between, get_ship_balls_in_bubble, get_slim_item, is_ball_cloaked, is_target_valid,
};
use crate::utility::blackboards::update_stored_value;
use crate::utility::dogmatic::{get_entity_max_missile_range, type_has_effect};
use crate::utility::groups::get_entity_group_member_count;
use crate::utility::inventory::get_type_id_by_item_id;
use crate::value::Value;

const UNSPAWN_GROUP_SLEEP_DELAY_SEC: f64 = 0.2;

/// `self.GetEntityGroup().Count()`.
fn entity_group_count(t: &mut TaskCtx<'_, '_>) -> Option<i64> {
    let Some(group_id) = t.ctx().my_entity_group_id else {
        t.raise(BehaviorError::Task(
            "AttributeError: 'NoneType' object has no attribute 'Count'".to_string(),
        ));
        return None;
    };
    let count = get_entity_group_member_count(t, group_id);
    if t.faulted() { None } else { Some(count as i64) }
}

enter_task!(
    /// Keeps the most members my group has had on the blackboard.
    UpdateMaxMemberCount,
    |t| {
        let Some(current_count) = entity_group_count(t) else { return };
        let Some(address) = t.required_address("maxMemberCountAddress") else { return };
        let last_count = t.get_last_blackboard_value(&address);
        if last_count.is_none() || Value::Int(current_count).py_gt(&last_count) {
            t.send_blackboard_value(&address, Value::Int(current_count));
        }
        t.set_status_to_success();
    }
);

enter_task!(
    /// Puts the number of members of my group on the blackboard when it has changed.
    UpdateCurrentMemberCount,
    |t| {
        let Some(current_count) = entity_group_count(t) else { return };
        let Some(address) = t.required_address("currentMemberCountAddress") else { return };
        let last_count = t.get_last_blackboard_value(&address);
        if !Value::Int(current_count).py_eq(&last_count) {
            t.send_blackboard_value(&address, Value::Int(current_count));
        }
        t.set_status_to_success();
    }
);

/// Chooses the commander of my group among the members that have a combat role.
#[derive(Debug, Default)]
pub struct ChooseCommander;

/// The members of my group by their combat role (in the order the roles were first met: a client dictionary has no order to keep).
type MembersByRole = Vec<(String, Vec<i64>)>;

impl ChooseCommander {
    /// `_GetRoleByGroupMemberId`: `Err` for a member whose blackboard is gone.
    fn role_by_group_member_id(
        t: &mut TaskCtx<'_, '_>,
        member_id: i64,
    ) -> Result<Value, crate::tree::BlackboardDeleted> {
        let channel = t.channel_for_item(member_id, ROLE_MESSAGE_CHANNEL);
        t.get_channel_last_value_unless_deleted(&channel)
    }

    /// `_GetMembersWithCombatRoles`.
    fn members_with_combat_roles(t: &mut TaskCtx<'_, '_>) -> Option<MembersByRole> {
        let mut members: MembersByRole = Vec::new();
        let member_ids = get_member_id_list(t);
        if t.faulted() {
            return None;
        }
        for member_id in member_ids {
            let Ok(role) = Self::role_by_group_member_id(t, member_id) else { continue };
            let Some(role) = role.as_str().filter(|role| COMBAT_ROLES.contains(role)) else { continue };
            match members.iter_mut().find(|(name, _)| name == role) {
                Some((_, ids)) => ids.push(member_id),
                None => members.push((role.to_string(), vec![member_id])),
            }
        }
        Some(members)
    }

    /// `_IsCurrentCommanderRoleValid`.
    fn is_current_commander_role_valid(
        t: &mut TaskCtx<'_, '_>,
        required_role: &Value,
        existing_commander: i64,
        members: &MembersByRole,
    ) -> bool {
        let required = required_role.as_str().unwrap_or("");
        if !required_role.truthy() || !members.iter().any(|(name, _)| name == required) {
            return true;
        }
        match Self::role_by_group_member_id(t, existing_commander) {
            Ok(role) => role.py_eq(required_role),
            Err(_) => false,
        }
    }

    /// `_ChooseCommanderFromGroup`.
    fn choose_commander_from_group(
        t: &mut TaskCtx<'_, '_>,
        required_role: &Value,
        existing: &Value,
    ) -> Option<Value> {
        let mut members = Self::members_with_combat_roles(t)?;
        let invalid_roles = t.required("invalidCommanderRoles")?;
        for invalid_role in invalid_roles.items().unwrap_or(&[]) {
            if let Some(name) = invalid_role.as_str() {
                members.retain(|(role, _)| role != name);
            }
        }
        if members.is_empty() {
            return Some(Value::None);
        }
        // `_IsCurrentCommanderValid`
        let existing_id = existing.as_int().filter(|id| *id != 0);
        if let Some(existing_id) = existing_id
            && t.world().has_ball(existing_id)
            && Self::is_current_commander_role_valid(t, required_role, existing_id, &members)
        {
            return Some(existing.clone());
        }
        if required_role.truthy()
            && let Some((_, ids)) =
                required_role.as_str().and_then(|required| members.iter().find(|(role, _)| role == required))
        {
            let pick = t.world().random_index(ids.len());
            return Some(Value::Int(ids[pick]));
        }
        let role_pick = t.world().random_index(members.len());
        let ids = &members[role_pick].1;
        let ids = ids.clone();
        let pick = t.world().random_index(ids.len());
        Some(Value::Int(ids[pick]))
    }

    /// `_ResetCommanderNavigationRangesAndBehaviorTree`.
    fn reset_commander_navigation_ranges_and_behavior_tree(t: &mut TaskCtx<'_, '_>, chosen: i64) {
        for name in [COMBAT_ORBIT_RANGE_CHANNEL_NAME, COMBAT_WARP_AT_DISTANCE_CHANNEL_NAME] {
            let channel = t.channel_for_item(chosen, name);
            t.channel_send(&channel, Value::None);
        }
        if !t.world().reset_behavior_tree(chosen) {
            t.raise(BehaviorError::Task(
                "AttributeError: 'NoneType' object has no attribute 'ResetTree'".to_string(),
            ));
        }
    }
}

impl Task for ChooseCommander {
    fn type_name(&self) -> &'static str {
        "ChooseCommander"
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        t.set_status_to_success();
        let required_role = t.attribute("requiredCommanderRole");
        let Some(commander_address) = t.required_address("commanderAddress") else { return };
        let existing = t.get_last_blackboard_value(&commander_address);
        let Some(chosen) = Self::choose_commander_from_group(t, &required_role, &existing) else { return };
        if chosen.py_eq(&existing) {
            return;
        }
        t.send_blackboard_value(&commander_address, chosen.clone());
        if chosen.truthy()
            && let Some(chosen_id) = chosen.as_int()
        {
            Self::reset_commander_navigation_ranges_and_behavior_tree(t, chosen_id);
        }
    }
}

enter_task!(
    /// Puts the members of my group on the blackboard.
    GetMemberList,
    |t| {
        let member_id_list = get_member_id_list(t);
        if t.faulted() {
            return;
        }
        let Some(address) = t.required_address("memberListAddress") else { return };
        t.send_blackboard_value(&address, Value::List(member_id_list.into_iter().map(Value::Int).collect()));
        t.set_status_to_success();
    }
);

enter_task!(AssignExtraLootTableByOwnerIdAndEntityGroupId, |t| {
    t.set_status_to_success();
    let Some(owner_id) = get_group_owner_id(t) else { return };
    let Some(new_extra_loot) = extra_loot_tables_for_owner(owner_id) else { return };
    if new_extra_loot.is_empty() {
        return;
    }
    let Some(address) = t.required_address("additionalLootTablesAddress") else { return };
    let last = t.get_last_blackboard_value(&address);
    let stored = last.truthy();
    let mut extra_loot = if stored { last } else { Value::Dict(Vec::new()) };
    for (entity_group_id, loot_table_ids) in new_extra_loot {
        let key = Value::Int(entity_group_id);
        let mut tables = extra_loot.dict_get(&key).cloned().unwrap_or_else(|| Value::set(std::iter::empty()));
        for table in loot_table_ids {
            tables.set_add(Value::Int(table));
        }
        extra_loot.dict_insert(key, tables);
    }
    if stored {
        update_stored_value(t, &address, &extra_loot);
    }
    t.send_blackboard_value(&address, extra_loot);
    let group = t.ctx().my_entity_group_id;
    t.world().assign_additional_loot_to_group_entities(group);
});

const HANDLER_UNSPAWN_GROUP: u16 = 0;

/// Unspawns my group, a moment later.
#[derive(Debug, Default)]
pub struct UnspawnGroup;

impl Task for UnspawnGroup {
    fn type_name(&self) -> &'static str {
        "UnspawnGroup"
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        t.set_status_to_success();
        // a tasklet that sleeps a moment and then unspawns the group
        t.schedule_call(UNSPAWN_GROUP_SLEEP_DELAY_SEC, HANDLER_UNSPAWN_GROUP, Vec::new());
    }

    fn on_call(&mut self, t: &mut TaskCtx<'_, '_>, handler: u16, _args: &[Value]) {
        if handler == HANDLER_UNSPAWN_GROUP {
            let group = t.ctx().my_entity_group_id;
            t.world().unspawn_behavior_entity_group_with_respawn_enabled(group);
        }
    }
}

/// Puts the ranges my group can fight at on the blackboard.
#[derive(Debug, Default)]
pub struct PostWeaponRangesForGroup;

impl PostWeaponRangesForGroup {
    /// `_get_entities_to_consider`: the members, or the entities of the roles the task lists.
    fn entities_to_consider(t: &mut TaskCtx<'_, '_>) -> Option<Vec<i64>> {
        let roles_to_consider = t.required("rolesToConsider")?;
        if !roles_to_consider.truthy() {
            let members = get_member_id_list(t);
            return if t.faulted() { None } else { Some(members) };
        }
        let address = t.required_address("entitiesByRole")?;
        let entities_by_role = t.get_last_blackboard_value(&address);
        let mut entities = Vec::new();
        if entities_by_role.is_none() {
            return Some(entities);
        }
        for role in roles_to_consider.items().unwrap_or(&[]) {
            if let Some(ids) = entities_by_role.dict_get(role) {
                entities.extend(ids.items().unwrap_or(&[]).iter().filter_map(Value::as_int));
            }
        }
        Some(entities)
    }

    /// `_get_max_effective_weapon_range_for_entity`.
    fn max_effective_weapon_range_for_entity(
        t: &mut TaskCtx<'_, '_>,
        entity_id: i64,
        entity_type_id: i64,
    ) -> f64 {
        let turret_range = if type_has_effect(t, entity_type_id, EFFECT_TARGET_ATTACK) {
            t.world().effect_range_with_falloff_multiplier(
                EFFECT_TARGET_ATTACK,
                entity_id,
                COMBAT_MAX_EFFECTIVE_FALLOFF_DISTANCE_FACTOR,
            )
        } else {
            0.0
        };
        let missile_range = if type_has_effect(t, entity_type_id, EFFECT_MISSILE_LAUNCHING_FOR_ENTITY) {
            get_entity_max_missile_range(t, entity_type_id)
        } else {
            0.0
        };
        if turret_range == 0.0 && missile_range == 0.0 {
            return 0.0;
        }
        if missile_range == 0.0 || turret_range < missile_range { turret_range } else { missile_range }
    }

    /// `_get_max_effective_weapon_range`: the middle one of the ranges of the entities that have one.
    fn max_effective_weapon_range(t: &mut TaskCtx<'_, '_>) -> Option<Value> {
        let entities = Self::entities_to_consider(t)?;
        if entities.is_empty() {
            return Some(Value::Int(0));
        }
        let mut ranges = Vec::new();
        for entity_id in entities {
            let Some(entity_type_id) = get_type_id_by_item_id(t, entity_id) else { continue };
            let range = Self::max_effective_weapon_range_for_entity(t, entity_id, entity_type_id);
            if t.faulted() {
                return None;
            }
            if range == 0.0 {
                continue;
            }
            ranges.push(range);
        }
        if ranges.is_empty() {
            return Some(Value::Int(0));
        }
        ranges.sort_by(f64::total_cmp);
        Some(Value::Float(ranges[ranges.len() / 2]))
    }
}

impl Task for PostWeaponRangesForGroup {
    fn type_name(&self) -> &'static str {
        "PostWeaponRangesForGroup"
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        t.set_status_to_success();
        let Some(max_range) = Self::max_effective_weapon_range(t) else { return };
        let Some(max_address) = t.required_address("maxEffectiveWeaponRangeAddress") else { return };
        t.send_blackboard_value(&max_address, max_range.clone());
        let min_range = max_range.as_float().unwrap_or(0.0) * COMBAT_MIN_EFFECTIVE_COMBAT_RANGE_FACTOR;
        let Some(min_address) = t.required_address("minEffectiveWeaponRangeAddress") else { return };
        t.send_blackboard_value(&min_address, Value::Float(min_range));
    }
}

/// Puts the player ship that is the nearest to the commander of my group on the blackboard.
#[derive(Debug, Default)]
pub struct GetClosestPlayerBallToGroupLeader;

impl GetClosestPlayerBallToGroupLeader {
    /// `_select_group_member`: the commander, if the task has one and there is one, else the first member.
    fn select_group_member(t: &mut TaskCtx<'_, '_>) -> Option<i64> {
        let members = get_member_id_list(t);
        if t.faulted() {
            return None;
        }
        let first = |t: &mut TaskCtx<'_, '_>| {
            let first = members.first().copied();
            if first.is_none() {
                t.raise(BehaviorError::Task("IndexError: list index out of range".to_string()));
            }
            first
        };
        if !t.has_attribute("commanderAddress") {
            return first(t);
        }
        let address = t.required_address("commanderAddress")?;
        let commander_id = t.get_last_blackboard_value(&address);
        if commander_id.truthy() { commander_id.as_int() } else { first(t) }
    }
}

impl Task for GetClosestPlayerBallToGroupLeader {
    fn type_name(&self) -> &'static str {
        "GetClosestPlayerBallToGroupLeader"
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        t.set_status_to_success();
        let Some(selected_group_member_id) = Self::select_group_member(t) else { return };
        let Some(ball) = get_ball(t, selected_group_member_id) else { return };
        let mut closest_player_ball_id: Option<i64> = None;
        let mut shortest_distance: Option<f64> = None;
        for player_ship_ball_id in get_ship_balls_in_bubble(t, ball.new_bubble_id) {
            if !is_target_valid(t, Some(player_ship_ball_id), None, &[], &[], &[]) {
                continue;
            }
            let Some(distance) = get_distance_between(t, selected_group_member_id, player_ship_ball_id)
            else {
                return;
            };
            if shortest_distance.is_none_or(|shortest| distance < shortest) {
                closest_player_ball_id = Some(player_ship_ball_id);
                shortest_distance = Some(distance);
            }
        }
        let Some(address) = t.required_address("ballIdAddress") else { return };
        t.send_blackboard_value(&address, closest_player_ball_id.map_or(Value::None, Value::Int));
    }
}

/// Puts on the blackboard whether a player ship is in a bubble with a member of my group.
#[derive(Debug, Default)]
pub struct IsAnyPlayerShipOnGridWithGroupMember;

impl IsAnyPlayerShipOnGridWithGroupMember {
    /// `_is_target_valid`.
    fn is_target_valid(t: &mut TaskCtx<'_, '_>, target_id: i64) -> Option<bool> {
        if get_slim_item(t, target_id).is_none() {
            return Some(false);
        }
        let include_cloaked = t.required("includeCloaked")?;
        if !include_cloaked.truthy() && is_ball_cloaked(t, target_id) {
            return Some(false);
        }
        Some(true)
    }

    /// `_bubble_contains_player`.
    fn bubble_contains_player(t: &mut TaskCtx<'_, '_>, bubble_id: i64) -> Option<bool> {
        for player_ship_ball_id in get_ship_balls_in_bubble(t, bubble_id) {
            if Self::is_target_valid(t, player_ship_ball_id)? {
                return Some(true);
            }
        }
        Some(false)
    }
}

impl Task for IsAnyPlayerShipOnGridWithGroupMember {
    fn type_name(&self) -> &'static str {
        "IsAnyPlayerShipOnGridWithGroupMember"
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        t.set_status_to_success();
        let bubbles = get_member_bubble_set(t);
        if t.faulted() {
            return;
        }
        let Some(address) = t.required_address("resultAddress") else { return };
        for bubble_id in bubbles {
            let Some(contains_player) = Self::bubble_contains_player(t, bubble_id) else { return };
            if contains_player {
                t.send_blackboard_value(&address, Value::Bool(true));
                return;
            }
        }
        t.send_blackboard_value(&address, Value::Bool(false));
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::blackboards::{Address, ScopeType, entity_group_scope};
    use crate::env::testing::TestGroup;
    use crate::tree::tests::Rig;
    use crate::tree::{Attrs, Status};

    fn item(name: &str) -> Value {
        Address::new(ScopeType::Item, name).to_value()
    }

    fn rig() -> Rig {
        let mut rig = Rig::new();
        rig.world
            .groups
            .insert(10, TestGroup { members: vec![1, 2, 3], owner_id: Some(1000006), ..Default::default() });
        rig
    }

    #[test]
    fn the_most_members_and_the_current_count_are_kept() {
        let mut rig = rig();
        let max = Attrs::new().with("maxMemberCountAddress", item("Max"));
        let current = Attrs::new().with("currentMemberCountAddress", item("Now"));
        rig.run(Box::new(UpdateMaxMemberCount), max.clone());
        rig.run(Box::new(UpdateCurrentMemberCount), current.clone());
        assert_eq!(
            (rig.last(ScopeType::Item, "Max"), rig.last(ScopeType::Item, "Now")),
            (Value::Int(3), Value::Int(3))
        );
        rig.world.groups.insert(10, TestGroup { members: vec![1], ..Default::default() });
        rig.run(Box::new(UpdateMaxMemberCount), max.clone());
        rig.run(Box::new(UpdateCurrentMemberCount), current);
        assert_eq!(
            (rig.last(ScopeType::Item, "Max"), rig.last(ScopeType::Item, "Now")),
            (Value::Int(3), Value::Int(1))
        );
        assert_eq!(rig.messages_on("Max"), 1);
        assert_eq!(rig.messages_on("Now"), 2);
        rig.run(
            Box::new(UpdateCurrentMemberCount),
            Attrs::new().with("currentMemberCountAddress", item("Now")),
        );
        assert_eq!(rig.messages_on("Now"), 2, "1 is the count still");
        rig.world.groups.insert(10, TestGroup { members: vec![1, 2, 3, 4], ..Default::default() });
        rig.run(Box::new(UpdateMaxMemberCount), max);
        assert_eq!(rig.last(ScopeType::Item, "Max"), Value::Int(4));
    }

    #[test]
    fn a_group_that_is_not_there_raises_when_it_is_counted() {
        let mut rig = Rig::new();
        rig.run(Box::new(UpdateMaxMemberCount), Attrs::new().with("maxMemberCountAddress", item("Max")));
        assert!(rig.last_error.is_some());
        assert_eq!(rig.last(ScopeType::Item, "Max"), Value::None);
    }

    fn commander_attrs() -> Attrs {
        Attrs::new()
            .with("commanderAddress", item("Commander"))
            .with("invalidCommanderRoles", Value::List(vec![Value::Str("logistic".into())]))
    }

    fn post_role(rig: &mut Rig, member: i64, role: &str) {
        let scope = crate::blackboards::item_scope(member);
        let mut cx = crate::tree::Cx {
            core: &mut rig.tree.core,
            ctx: &mut rig.ctx,
            shared: &mut rig.shared,
            world: &mut rig.world,
        };
        cx.send_to_channel(scope, "ROLE", Value::Str(role.into()));
    }

    #[test]
    fn a_commander_is_chosen_among_the_members_with_a_combat_role_and_its_ranges_and_tree_are_reset() {
        let mut rig = rig();
        post_role(&mut rig, 1, "dps");
        post_role(&mut rig, 2, "logistic");
        post_role(&mut rig, 3, "commander");
        rig.world.behavior_trees.insert(1);
        rig.world.behavior_trees.insert(3);
        rig.world.set_numbers(&[0.99, 0.0]);
        assert_eq!(rig.run(Box::new(ChooseCommander), commander_attrs()), Status::Success);
        assert_eq!(rig.last_error, None);
        // the roles are dps [1], commander [3] (logistic is invalid): 0.99 picks the second role, its only member
        assert_eq!(rig.last(ScopeType::Item, "Commander"), Value::Int(3));
        assert_eq!(rig.world.take_calls(), ["reset_behavior_tree(3)"]);
        assert_eq!(rig.last_in(crate::blackboards::item_scope(3), "COMBAT_ORBIT_RANGE"), Value::None);
    }

    #[test]
    fn the_required_role_is_chosen_from_and_a_commander_that_is_valid_stays() {
        let mut rig = rig();
        post_role(&mut rig, 1, "dps");
        post_role(&mut rig, 2, "dps");
        post_role(&mut rig, 3, "commander");
        rig.world.add_ball(2, [0.0; 3], 1);
        rig.world.behavior_trees.extend([1, 2, 3]);
        rig.world.set_numbers(&[0.9]);
        let attrs = commander_attrs().with("requiredCommanderRole", "dps");
        rig.run(Box::new(ChooseCommander), attrs.clone());
        assert_eq!(rig.last(ScopeType::Item, "Commander"), Value::Int(2), "0.9 of two dps members");
        // 2 is a dps with a ball: it stays, nothing is sent
        rig.world.take_calls();
        rig.run(Box::new(ChooseCommander), attrs.clone());
        assert_eq!(rig.messages_on("Commander"), 1);
        assert!(rig.world.take_calls().is_empty());
        // a commander that has no ball any more is replaced
        rig.world.balls.clear();
        rig.world.set_numbers(&[0.0]);
        rig.run(Box::new(ChooseCommander), attrs);
        assert_eq!(rig.last(ScopeType::Item, "Commander"), Value::Int(1));
    }

    #[test]
    fn the_roles_that_cannot_command_are_left_out_of_the_choice() {
        let mut rig = rig();
        post_role(&mut rig, 1, "dps");
        post_role(&mut rig, 2, "logistic");
        post_role(&mut rig, 3, "commander");
        rig.world.behavior_trees.extend([1, 3]);
        rig.world.set_numbers(&[0.5, 0.0]);
        rig.run(Box::new(ChooseCommander), commander_attrs());
        assert_eq!(rig.last(ScopeType::Item, "Commander"), Value::Int(3), "the second of dps and commander");
    }

    #[test]
    fn a_commander_that_has_not_the_required_role_is_replaced_and_its_navigation_ranges_are_cleared() {
        let mut rig = rig();
        post_role(&mut rig, 1, "dps");
        post_role(&mut rig, 3, "commander");
        rig.world.add_ball(3, [0.0; 3], 1);
        rig.world.behavior_trees.extend([1, 3]);
        rig.post(ScopeType::Item, "Commander", Value::Int(3));
        {
            let scope = crate::blackboards::item_scope(1);
            let mut cx = crate::tree::Cx {
                core: &mut rig.tree.core,
                ctx: &mut rig.ctx,
                shared: &mut rig.shared,
                world: &mut rig.world,
            };
            cx.send_to_channel(scope, "COMBAT_WARP_AT_DISTANCE", Value::Float(5.0));
            cx.send_to_channel(scope, "COMBAT_ORBIT_RANGE", Value::Float(6.0));
        }
        rig.world.set_numbers(&[0.0]);
        rig.run(Box::new(ChooseCommander), commander_attrs().with("requiredCommanderRole", "dps"));
        assert_eq!(rig.last(ScopeType::Item, "Commander"), Value::Int(1));
        assert_eq!(rig.last_in(crate::blackboards::item_scope(1), "COMBAT_WARP_AT_DISTANCE"), Value::None);
        assert_eq!(rig.last_in(crate::blackboards::item_scope(1), "COMBAT_ORBIT_RANGE"), Value::None);
    }

    #[test]
    fn with_no_members_with_a_combat_role_the_commander_is_none() {
        let mut rig = rig();
        rig.post(ScopeType::Item, "Commander", Value::Int(2));
        rig.run(Box::new(ChooseCommander), commander_attrs());
        assert_eq!(rig.last(ScopeType::Item, "Commander"), Value::None);
        assert!(rig.world.take_calls().is_empty(), "no commander: no tree to reset");
    }

    #[test]
    fn the_members_are_put_on_the_blackboard() {
        let mut rig = rig();
        rig.run(Box::new(GetMemberList), Attrs::new().with("memberListAddress", item("Members")));
        assert_eq!(
            rig.last(ScopeType::Item, "Members"),
            Value::List(vec![Value::Int(1), Value::Int(2), Value::Int(3)])
        );
    }

    #[test]
    fn the_extra_loot_tables_of_the_owner_are_added_and_assigned() {
        let attrs = Attrs::new().with("additionalLootTablesAddress", item("Loot"));
        let mut rig = rig();
        assert_eq!(
            rig.run(Box::new(AssignExtraLootTableByOwnerIdAndEntityGroupId), attrs.clone()),
            Status::Success
        );
        assert_eq!(
            rig.last_in(entity_group_scope(10), "Loot"),
            Value::None,
            "the channel is the task's: of the item"
        );
        let loot = rig.last(ScopeType::Item, "Loot");
        assert_eq!(loot, Value::Dict(vec![(Value::Int(1767), Value::set([Value::Int(10198)]))]));
        assert_eq!(rig.world.take_calls(), ["assign_additional_loot(Some(10))"]);

        rig.world.groups.insert(10, TestGroup { members: vec![1], owner_id: Some(5), ..Default::default() });
        rig.run(Box::new(AssignExtraLootTableByOwnerIdAndEntityGroupId), attrs);
        assert!(rig.world.take_calls().is_empty());
    }

    #[test]
    fn a_group_is_unspawned_after_a_moment() {
        let mut rig = rig();
        assert_eq!(rig.run(Box::new(UnspawnGroup), Attrs::new()), Status::Success);
        assert!(rig.world.take_calls().is_empty());
        assert_eq!(rig.shared.next_call_due(), Some(rig.shared.now + crate::env::seconds_to_ticks(0.2)));
        rig.advance_seconds(0.2);
        rig.deliver_scheduled();
        assert_eq!(rig.world.take_calls(), ["unspawn_group(Some(10))"]);
    }

    fn weapon_attrs() -> Attrs {
        Attrs::new()
            .with("rolesToConsider", Value::List(vec![]))
            .with("entitiesByRole", item("ByRole"))
            .with("maxEffectiveWeaponRangeAddress", item("Max"))
            .with("minEffectiveWeaponRangeAddress", item("Min"))
    }

    #[test]
    fn the_middle_range_of_the_weapons_of_my_group_is_posted_with_the_smaller_one() {
        let mut rig = rig();
        for (id, type_id, range) in [(1, 501, 10_000.0), (2, 502, 30_000.0), (3, 503, 20_000.0)] {
            rig.world
                .items
                .insert(id, crate::env::InventoryItem { item_id: id, type_id, owner_id: 7, location_id: 1 });
            rig.world.type_effects.insert((type_id, EFFECT_TARGET_ATTACK));
            rig.world.effect_ranges.insert((id, EFFECT_TARGET_ATTACK), (Some(range), None));
        }
        rig.run(Box::new(PostWeaponRangesForGroup), weapon_attrs());
        assert_eq!(rig.last_error, None);
        assert_eq!(rig.last(ScopeType::Item, "Max"), Value::Float(20_000.0));
        assert_eq!(rig.last(ScopeType::Item, "Min"), Value::Float(6_000.0));
    }

    #[test]
    fn with_no_weapons_the_range_is_0_and_the_roles_the_task_lists_are_the_entities_it_considers() {
        let mut rig = rig();
        rig.run(Box::new(PostWeaponRangesForGroup), weapon_attrs());
        assert_eq!(
            (rig.last(ScopeType::Item, "Max"), rig.last(ScopeType::Item, "Min")),
            (Value::Int(0), Value::Float(0.0))
        );
        let mut rig = self::rig();
        rig.world
            .items
            .insert(2, crate::env::InventoryItem { item_id: 2, type_id: 502, owner_id: 7, location_id: 1 });
        rig.world.type_effects.insert((502, EFFECT_TARGET_ATTACK));
        rig.world.effect_ranges.insert((2, EFFECT_TARGET_ATTACK), (Some(8000.0), None));
        let attrs = weapon_attrs().with(
            "rolesToConsider",
            Value::List(vec![Value::Str("dps".into()), Value::Str("tackler".into())]),
        );
        rig.run(Box::new(PostWeaponRangesForGroup), attrs.clone());
        assert_eq!(rig.last(ScopeType::Item, "Max"), Value::Int(0), "no entities by role posted yet");
        rig.post(
            ScopeType::Item,
            "ByRole",
            Value::Dict(vec![
                (Value::Str("dps".into()), Value::List(vec![Value::Int(2)])),
                (Value::Str("ew".into()), Value::List(vec![Value::Int(1)])),
            ]),
        );
        rig.run(Box::new(PostWeaponRangesForGroup), attrs);
        assert_eq!(rig.last(ScopeType::Item, "Max"), Value::Float(8000.0));
    }

    fn weapon_entity(rig: &mut Rig, id: i64, type_id: i64, turret: Option<f64>, missile: bool) {
        rig.world
            .items
            .insert(id, crate::env::InventoryItem { item_id: id, type_id, owner_id: 7, location_id: 1 });
        if let Some(range) = turret {
            rig.world.type_effects.insert((type_id, EFFECT_TARGET_ATTACK));
            rig.world.effect_ranges.insert((id, EFFECT_TARGET_ATTACK), (Some(range), None));
        }
        if missile {
            rig.world.type_effects.insert((type_id, EFFECT_MISSILE_LAUNCHING_FOR_ENTITY));
            rig.world
                .type_attributes
                .insert((type_id, crate::consts::ATTRIBUTE_ENTITY_MISSILE_TYPE_ID), 9000.0);
            rig.world.type_attributes.insert((9000, crate::consts::ATTRIBUTE_MAX_VELOCITY), 100.0);
            rig.world.type_attributes.insert((9000, crate::consts::ATTRIBUTE_EXPLOSION_DELAY), 10_000.0);
            rig.world
                .type_attributes
                .insert((type_id, crate::consts::ATTRIBUTE_MISSILE_ENTITY_VELOCITY_MULTIPLIER), 1.0);
            rig.world
                .type_attributes
                .insert((type_id, crate::consts::ATTRIBUTE_MISSILE_ENTITY_FLIGHT_TIME_MULTIPLIER), 1.0);
        }
    }

    #[test]
    fn the_shorter_of_the_turret_and_the_missile_range_counts_and_entities_without_a_range_do_not() {
        // a missile of 1000 meters
        for (turret, missile, wanted) in [
            (Some(30_000.0), true, 1000.0),
            (Some(500.0), true, 500.0),
            (None, true, 0.0),
            (Some(700.0), false, 700.0),
        ] {
            let mut rig = rig();
            rig.world.groups.insert(10, TestGroup { members: vec![1], ..Default::default() });
            weapon_entity(&mut rig, 1, 501, turret, missile);
            rig.run(Box::new(PostWeaponRangesForGroup), weapon_attrs());
            let expected = if wanted == 0.0 { Value::Int(0) } else { Value::Float(wanted) };
            assert_eq!(rig.last(ScopeType::Item, "Max"), expected, "{turret:?} {missile}");
        }
        // 0, 0, 10 and 20 kilometers: the middle one of the two that have a range
        let mut rig = rig();
        rig.world.groups.insert(10, TestGroup { members: vec![1, 2, 3, 4], ..Default::default() });
        weapon_entity(&mut rig, 1, 501, None, false);
        weapon_entity(&mut rig, 2, 502, None, false);
        weapon_entity(&mut rig, 3, 503, Some(10_000.0), false);
        weapon_entity(&mut rig, 4, 504, Some(20_000.0), false);
        rig.run(Box::new(PostWeaponRangesForGroup), weapon_attrs());
        assert_eq!(rig.last(ScopeType::Item, "Max"), Value::Float(20_000.0));
    }

    fn player_rig() -> Rig {
        let mut rig = rig();
        rig.world.add_ball(1, [0.0; 3], 700).new_bubble_id = 3;
        rig.world.add_ball(2, [0.0; 3], 700).new_bubble_id = 4;
        rig.world.add_ball(3, [0.0; 3], 700).new_bubble_id = 3;
        for (id, bubble) in [(40, 3), (41, 3), (42, 4)] {
            rig.world.add_ball(id, [0.0; 3], 600).new_bubble_id = bubble;
            rig.world.slims.get_mut(&id).unwrap().category_id = 6;
        }
        rig.world.bubbles.insert(3, vec![40, 41]);
        rig.world.bubbles.insert(4, vec![42]);
        rig
    }

    #[test]
    fn the_nearest_player_ship_to_the_first_member_or_the_commander_is_posted() {
        let mut rig = player_rig();
        rig.world.set_dist(1, 40, 4000.0);
        rig.world.set_dist(1, 41, 9000.0);
        rig.world.set_dist(2, 42, 100.0);
        let attrs = Attrs::new().with("ballIdAddress", item("Ball"));
        assert_eq!(rig.run(Box::new(GetClosestPlayerBallToGroupLeader), attrs.clone()), Status::Success);
        assert_eq!(rig.last(ScopeType::Item, "Ball"), Value::Int(40));
        rig.post(ScopeType::Item, "Commander", Value::Int(2));
        rig.run(
            Box::new(GetClosestPlayerBallToGroupLeader),
            attrs.clone().with("commanderAddress", item("Commander")),
        );
        assert_eq!(rig.last(ScopeType::Item, "Ball"), Value::Int(42));
        rig.world.slims.remove(&42);
        rig.run(
            Box::new(GetClosestPlayerBallToGroupLeader),
            attrs.with("commanderAddress", item("Commander")),
        );
        assert_eq!(rig.last(ScopeType::Item, "Ball"), Value::None);
    }

    #[test]
    fn a_player_ship_on_grid_with_a_member_counts_unless_it_is_cloaked_and_the_task_says_so() {
        let attrs = Attrs::new().with("resultAddress", item("Result")).with("includeCloaked", false);
        let mut rig = player_rig();
        rig.world.groups.insert(10, TestGroup { members: vec![2], ..Default::default() });
        assert_eq!(rig.run(Box::new(IsAnyPlayerShipOnGridWithGroupMember), attrs.clone()), Status::Success);
        assert_eq!(rig.last(ScopeType::Item, "Result"), Value::Bool(true));
        rig.world.cloaked.insert(42);
        rig.run(Box::new(IsAnyPlayerShipOnGridWithGroupMember), attrs.clone());
        assert_eq!(rig.last(ScopeType::Item, "Result"), Value::Bool(false));
        rig.run(Box::new(IsAnyPlayerShipOnGridWithGroupMember), attrs.with("includeCloaked", true));
        assert_eq!(rig.last(ScopeType::Item, "Result"), Value::Bool(true));
    }
}
