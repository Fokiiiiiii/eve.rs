//! `behaviors/actions/entities.py`: what an entity does to itself: its loot, its cargo, its slim item, its life.

use crate::constants::combat::FW_ATTACK_NONE;
use crate::tree::{BehaviorError, Task, TaskCtx};
use crate::utility::ballparks::{get_ball, get_my_ball};
use crate::utility::inventory::{
    add_type_to_cargo, get_cargo_capacity_max, get_cargo_used, get_cargo_used_fraction, remove_all_items,
};
use crate::value::Value;

const SELF_UNSPAWN_SLEEP_DELAY_SEC: f64 = 0.2;

fn attribute_error(t: &mut TaskCtx<'_, '_>, what: &str) {
    t.raise(BehaviorError::Task(format!("AttributeError: 'NoneType' object has no attribute '{what}'")));
}

/// `HasCargoBayComponent(mySlimItem.typeID)`.
fn has_cargo_bay(t: &mut TaskCtx<'_, '_>) -> Option<bool> {
    let Some(slim) = t.ctx().my_slim_item.clone() else {
        attribute_error(t, "typeID");
        return None;
    };
    Some(t.world().has_cargo_bay_component(slim.type_id))
}

enter_task!(
    /// Adds an amount of a type to the loot my entity will drop.
    AddTypeAndAmountToAdditionalLoot,
    |t| {
        let Some(address) = t.required_address("typeIdAddress") else { return };
        let type_id = t.get_last_blackboard_value(&address);
        if let Some(type_id) = type_id.as_int() {
            let Some(ball) = get_my_ball(t) else { return };
            let (Some(amount), Some(allow_overload)) = (t.required("amount"), t.required("allowOverload")) else { return };
            t.world().add_type_and_amount_to_additional_loot(ball.id, type_id, Some(&amount), Some(&allow_overload));
        }
        t.set_status_to_success();
    }
);

enter_task!(
    /// Adds the amount of the blackboard of a type of it to the loot my entity will drop.
    AddBlackboardTypeAndBlackboardAmountToAdditionalLoot,
    |t| {
        let (Some(type_address), Some(amount_address)) = (t.required_address("typeIdAddress"), t.required_address("amountAddress"))
        else {
            return;
        };
        let type_id = t.get_last_blackboard_value(&type_address);
        let amount = t.get_last_blackboard_value(&amount_address);
        if let (Some(type_id), false) = (type_id.as_int(), amount.is_none()) {
            let Some(ball) = get_my_ball(t) else { return };
            let Some(allow_overload) = t.required("allowOverload") else { return };
            t.world().add_type_and_amount_to_additional_loot(ball.id, type_id, Some(&amount), Some(&allow_overload));
        }
        t.set_status_to_success();
    }
);

enter_task!(
    /// Takes the types off the loot my entity will drop.
    RemoveTypesFromAdditionalLoot,
    |t| {
        let Some(type_ids) = t.required("typeIds") else { return };
        for type_id in type_ids.items().unwrap_or(&[]) {
            let Some(ball) = get_my_ball(t) else { return };
            if let Some(type_id) = type_id.as_int() {
                t.world().remove_type_from_additional_loot(ball.id, type_id);
            }
        }
        t.set_status_to_success();
    }
);

enter_task!(
    /// Takes the loot another entity would drop, and gives it to mine.
    MoveAdditionalLootBetweenEntities,
    |t| {
        t.set_status_to_failed();
        let Some(address) = t.required_address("entityIdAddress") else { return };
        let entity_id = t.get_last_blackboard_value(&address);
        if !entity_id.truthy() {
            return;
        }
        let Some(entity_id) = entity_id.as_int() else { return };
        if !t.world().has_ball(entity_id) {
            return;
        }
        let Some(target_entity_ball) = get_ball(t, entity_id) else { return };
        let additional_loot = t.world().remove_additional_loot(target_entity_ball.id);
        let Some(my_ball) = get_my_ball(t) else { return };
        for (type_id, amount) in additional_loot {
            t.world().add_type_and_amount_to_additional_loot(my_ball.id, type_id, Some(&amount), None);
        }
        t.set_status_to_success();
    }
);

enter_task!(
    /// Puts the member of my group that has the lowest item id on the blackboard as its leader.
    SelectGroupLeaderAction,
    |t| {
        let members = t.ctx().my_entity_group_id.and_then(|id| t.world().group_members(id));
        let Some(members) = members else {
            attribute_error(t, "GetGroupMembers");
            return;
        };
        let Some(group_leader_id) = members.iter().min().copied() else {
            t.raise(BehaviorError::Unrecoverable("Unable to get own group members: min() arg is an empty sequence".to_string()));
            return;
        };
        let Some(address) = t.required_address("groupLeaderIdAddress") else { return };
        t.send_blackboard_value(&address, Value::Int(group_leader_id));
        t.set_status_to_success();
    }
);

const HANDLER_UNSPAWN_SELF: u16 = 0;
const HANDLER_DESTROY_SELF: u16 = 0;

/// Takes my entity out of space (after a moment), and holds the reset of the tree until then.
#[derive(Debug, Default)]
pub struct UnspawnSelf;

impl Task for UnspawnSelf {
    fn type_name(&self) -> &'static str {
        "UnspawnSelf"
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        t.set_status_to_suspended();
        t.block_reset();
        t.schedule_call(SELF_UNSPAWN_SLEEP_DELAY_SEC, HANDLER_UNSPAWN_SELF, Vec::new());
    }

    fn on_call(&mut self, t: &mut TaskCtx<'_, '_>, handler: u16, _args: &[Value]) {
        if handler != HANDLER_UNSPAWN_SELF {
            return;
        }
        // `_UnspawnSelf`, a tasklet: what it raises is not the task's
        t.eat_exceptions(|t| {
            let Some(me) = t.my_item_id() else { return };
            // `clear_inventory`
            if has_cargo_bay(t) == Some(true) {
                remove_all_items(t, me);
            }
            t.world().unspawn_entity_ball(me);
        });
    }
}

enter_task!(
    /// Puts the tale of a persisted object on the context of my entity; fails when it has none.
    UpdateTaleContextForPersistedObject,
    |t| {
        t.set_status_to_success();
        if t.ctx().my_tale_id.is_some() {
            return;
        }
        let Some(me) = t.my_item_id() else { return };
        let tale_id = t.world().tale_id_for_persisted_object(me);
        if tale_id.is_none_or(|id| id == 0) {
            t.set_status_to_failed();
        }
        t.ctx().my_tale_id = tale_id;
    }
);

/// Explodes my entity, suspended until it is gone.
#[derive(Debug, Default)]
pub struct DestroySelf;

impl Task for DestroySelf {
    fn type_name(&self) -> &'static str {
        "DestroySelf"
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        t.set_status_to_suspended();
        t.schedule_call(0.0, HANDLER_DESTROY_SELF, Vec::new());
    }

    fn on_call(&mut self, t: &mut TaskCtx<'_, '_>, handler: u16, _args: &[Value]) {
        if handler == HANDLER_DESTROY_SELF
            && let Some(me) = t.my_item_id()
        {
            t.world().explode_ex(me);
        }
    }
}

enter_task!(
    /// Sets the standings my entity is hostile and friendly at, on its slim item.
    UpdateStandingThresholdsForEntity,
    |t| {
        let Some(me) = t.my_item_id() else { return };
        let (Some(hostile), Some(friendly)) = (t.required("hostileResponseThreshold"), t.required("friendlyResponseThreshold"))
        else {
            return;
        };
        t.world().update_slim_item_field_multi(
            me,
            &[("hostile_response_threshold", hostile), ("friendly_response_threshold", friendly)],
        );
        t.set_status_to_success();
    }
);

enter_task!(
    /// Sets how my entity attacks in faction warfare, on its slim item.
    UpdateFwAttackMethodForEntity,
    |t| {
        let Some(me) = t.my_item_id() else { return };
        let Some(mut fw_attack_method) = t.required("fwAttackMethod") else { return };
        if fw_attack_method.py_eq(&Value::Str(FW_ATTACK_NONE.to_string())) {
            fw_attack_method = Value::None;
        }
        t.world().update_slim_item_field(me, "fwAttackMethod", fw_attack_method);
        let Some(group_standings_disabled) = t.required("groupStandingsDisabled") else { return };
        if group_standings_disabled.truthy() {
            t.world().update_slim_item_field(me, "group_standings_disabled", group_standings_disabled);
        }
        t.set_status_to_success();
    }
);

enter_task!(
    /// Counts the entities of some types that have a behavior in the system.
    CountBehaviorEntitiesOfTypeInSystem,
    |t| {
        let Some(types_to_count) = t.required("typeIDsToCount") else { return };
        let mut count = 0;
        for entry_key in t.world().behavior_entry_keys() {
            if t.world().slim_item(entry_key).is_some_and(|slim| types_to_count.contains(&Value::Int(slim.type_id))) {
                count += 1;
            }
        }
        let Some(address) = t.required_address("resultAddress") else { return };
        t.send_blackboard_value(&address, Value::Int(count));
        t.set_status_to_success();
    }
);

enter_task!(
    /// Puts the fraction of its cargo bay my entity has used on the blackboard.
    SendCargoUsedFractionToBlackboard,
    |t| {
        let Some(has_cargo) = has_cargo_bay(t) else { return };
        if has_cargo {
            let Some(me) = t.my_item_id() else { return };
            let Some(fraction) = get_cargo_used_fraction(t, me) else { return };
            let Some(address) = t.required_address("blackboardAddress") else { return };
            t.send_blackboard_value(&address, Value::Float(fraction));
        }
        t.set_status_to_success();
    }
);

enter_task!(
    /// Puts how much of its cargo bay my entity has used on the blackboard.
    SendCargoUsedToBlackboard,
    |t| {
        let Some(has_cargo) = has_cargo_bay(t) else { return };
        if has_cargo {
            let Some(me) = t.my_item_id() else { return };
            let Some(used) = get_cargo_used(t, me) else { return };
            let Some(address) = t.required_address("blackboardAddress") else { return };
            t.send_blackboard_value(&address, Value::Float(used));
        }
        t.set_status_to_success();
    }
);

enter_task!(
    /// Puts how much cargo my entity can carry on the blackboard.
    SendCargoCapacityToBlackboard,
    |t| {
        let Some(has_cargo) = has_cargo_bay(t) else { return };
        if has_cargo {
            let Some(me) = t.my_item_id() else { return };
            let Some(capacity) = get_cargo_capacity_max(t, me) else { return };
            let Some(address) = t.required_address("blackboardAddress") else { return };
            t.send_blackboard_value(&address, Value::Float(capacity));
        }
        t.set_status_to_success();
    }
);

enter_task!(
    /// Empties the cargo bay of my entity.
    RemoveAllCargo,
    |t| {
        let Some(has_cargo) = has_cargo_bay(t) else { return };
        if has_cargo && let Some(me) = t.my_item_id() {
            remove_all_items(t, me);
        }
        t.set_status_to_success();
    }
);

enter_task!(
    /// Puts an amount of a type in the cargo bay of my entity.
    AddTypeAndAmountToCargo,
    |t| {
        let Some(has_cargo) = has_cargo_bay(t) else { return };
        if has_cargo {
            let (Some(type_id), Some(amount)) = (t.required("typeId"), t.required("amount")) else { return };
            if let (Some(type_id), Some(amount), Some(me)) = (type_id.as_int(), amount.as_int(), t.my_item_id()) {
                add_type_to_cargo(t, me, type_id, amount);
            }
        }
        t.set_status_to_success();
    }
);

#[cfg(test)]
mod tests {
    use super::*;
    use crate::blackboards::{Address, ScopeType};
    use crate::env::SlimItem;
    use crate::tree::tests::Rig;
    use crate::tree::{Attrs, Status};

    fn item(name: &str) -> Value {
        Address::new(ScopeType::Item, name).to_value()
    }

    fn rig() -> Rig {
        let mut rig = Rig::new();
        rig.ctx.my_slim_item = Some(SlimItem { item_id: 1, type_id: 700, ..Default::default() });
        rig.world.add_ball(1, [0.0; 3], 700);
        rig
    }

    #[test]
    fn a_type_and_an_amount_are_added_to_the_loot_and_a_type_that_is_not_there_adds_nothing() {
        let attrs =
            Attrs::new().with("typeIdAddress", item("Type")).with("amount", 5i64).with("allowOverload", true);
        let mut rig = rig();
        assert_eq!(rig.run(Box::new(AddTypeAndAmountToAdditionalLoot), attrs.clone()), Status::Success);
        assert!(rig.world.take_calls().is_empty());
        rig.post(ScopeType::Item, "Type", Value::Int(34));
        rig.run(Box::new(AddTypeAndAmountToAdditionalLoot), attrs);
        assert_eq!(rig.world.take_calls(), ["add_additional_loot(1, 34, Int(5), Some(Bool(true)))"]);
        let both = Attrs::new()
            .with("typeIdAddress", item("Type"))
            .with("amountAddress", item("Amount"))
            .with("allowOverload", false);
        rig.run(Box::new(AddBlackboardTypeAndBlackboardAmountToAdditionalLoot), both.clone());
        assert!(rig.world.take_calls().is_empty(), "no amount");
        rig.post(ScopeType::Item, "Amount", Value::Int(0));
        rig.run(Box::new(AddBlackboardTypeAndBlackboardAmountToAdditionalLoot), both);
        assert_eq!(
            rig.world.take_calls(),
            ["add_additional_loot(1, 34, Int(0), Some(Bool(false)))"],
            "0 is an amount"
        );
    }

    #[test]
    fn types_are_removed_from_the_loot() {
        let mut rig = rig();
        let attrs = Attrs::new().with("typeIds", Value::List(vec![Value::Int(34), Value::Int(35)]));
        assert_eq!(rig.run(Box::new(RemoveTypesFromAdditionalLoot), attrs), Status::Success);
        assert_eq!(
            rig.world.take_calls(),
            ["remove_additional_loot_type(1, 34)", "remove_additional_loot_type(1, 35)"]
        );
    }

    #[test]
    fn the_loot_of_another_entity_is_moved_to_mine() {
        let attrs = Attrs::new().with("entityIdAddress", item("Other"));
        let mut rig = rig();
        assert_eq!(rig.run(Box::new(MoveAdditionalLootBetweenEntities), attrs.clone()), Status::Failure);
        rig.post(ScopeType::Item, "Other", Value::Int(20));
        assert_eq!(
            rig.run(Box::new(MoveAdditionalLootBetweenEntities), attrs.clone()),
            Status::Failure,
            "no ball"
        );
        rig.world.removed_additional_loot.insert(20, vec![(34, Value::Int(3))]);
        assert_eq!(
            rig.run(Box::new(MoveAdditionalLootBetweenEntities), attrs.clone()),
            Status::Failure,
            "no ball"
        );
        assert_eq!(rig.last_error, None, "an entity that is not in the park is no error");
        assert!(rig.world.take_calls().is_empty(), "nothing is taken off an entity that is not there");
        rig.world.add_ball(20, [0.0; 3], 600);
        rig.world.removed_additional_loot.insert(20, vec![(34, Value::Int(3)), (35, Value::Int(4))]);
        assert_eq!(rig.run(Box::new(MoveAdditionalLootBetweenEntities), attrs), Status::Success);
        assert_eq!(
            rig.world.take_calls(),
            [
                "remove_additional_loot(20)",
                "add_additional_loot(1, 34, Int(3), None)",
                "add_additional_loot(1, 35, Int(4), None)"
            ]
        );
    }

    #[test]
    fn the_leader_of_a_group_is_its_lowest_member_and_a_group_without_members_is_unrecoverable() {
        let attrs = Attrs::new().with("groupLeaderIdAddress", item("Leader"));
        let mut rig = rig();
        rig.world
            .groups
            .insert(10, crate::env::testing::TestGroup { members: vec![7, 3, 9], ..Default::default() });
        assert_eq!(rig.run(Box::new(SelectGroupLeaderAction), attrs.clone()), Status::Success);
        assert_eq!(rig.last(ScopeType::Item, "Leader"), Value::Int(3));
        rig.world.groups.insert(10, crate::env::testing::TestGroup::default());
        rig.run(Box::new(SelectGroupLeaderAction), attrs);
        assert!(matches!(&rig.last_error, Some(BehaviorError::Unrecoverable(_))));
    }

    #[test]
    fn an_entity_that_unspawns_itself_holds_the_reset_and_empties_its_cargo_after_a_moment() {
        let mut rig = rig();
        rig.world.cargo_bay_types.insert(700);
        let task = rig.tree.add_task(Box::new(UnspawnSelf), Attrs::new());
        rig.tree.start_root_task(task);
        rig.tick().unwrap();
        assert_eq!(rig.tree.status_of(task), Status::Suspended);
        assert!(rig.tree.is_reset_blocked());
        assert!(rig.world.take_calls().is_empty());
        rig.advance_seconds(0.2);
        rig.deliver_scheduled();
        assert_eq!(rig.world.take_calls().last().map(String::as_str), Some("unspawn_entity_ball(1)"));
        // an entity with a cargo bay empties it first
        let mut rig = self::rig();
        rig.world.cargo_bay_types.insert(700);
        rig.world.cargo_contents.insert(1, vec![50]);
        let task = rig.tree.add_task(Box::new(UnspawnSelf), Attrs::new());
        rig.tree.start_root_task(task);
        rig.tick().unwrap();
        rig.advance_seconds(0.2);
        rig.deliver_scheduled();
        assert_eq!(rig.world.take_calls(), ["destroy_item(50)", "unspawn_entity_ball(1)"]);
        // one without does not
        let mut rig = self::rig();
        rig.world.cargo_contents.insert(1, vec![50]);
        let task = rig.tree.add_task(Box::new(UnspawnSelf), Attrs::new());
        rig.tree.start_root_task(task);
        rig.tick().unwrap();
        rig.advance_seconds(0.2);
        rig.deliver_scheduled();
        assert_eq!(rig.world.take_calls(), ["unspawn_entity_ball(1)"]);
    }

    #[test]
    fn an_entity_that_is_destroyed_explodes_at_once_but_suspended() {
        let mut rig = rig();
        let task = rig.tree.add_task(Box::new(DestroySelf), Attrs::new());
        rig.tree.start_root_task(task);
        rig.tick().unwrap();
        assert_eq!(rig.tree.status_of(task), Status::Suspended);
        rig.deliver_scheduled();
        assert_eq!(rig.world.take_calls(), ["explode_ex(1)"]);
    }

    #[test]
    fn the_tale_of_a_persisted_object_goes_on_the_context_unless_there_is_one() {
        let mut rig = rig();
        rig.world.persisted_tales.insert(1, 55);
        assert_eq!(rig.run(Box::new(UpdateTaleContextForPersistedObject), Attrs::new()), Status::Success);
        assert_eq!(rig.ctx.my_tale_id, Some(55));
        rig.world.persisted_tales.insert(1, 66);
        rig.run(Box::new(UpdateTaleContextForPersistedObject), Attrs::new());
        assert_eq!(rig.ctx.my_tale_id, Some(55), "one is there");
        let mut rig = self::rig();
        assert_eq!(rig.run(Box::new(UpdateTaleContextForPersistedObject), Attrs::new()), Status::Failure);
        assert_eq!(rig.ctx.my_tale_id, None);
    }

    #[test]
    fn the_slim_item_is_updated_with_the_standings_and_the_faction_warfare_attack_method() {
        let mut rig = rig();
        let standings =
            Attrs::new().with("hostileResponseThreshold", -5.0).with("friendlyResponseThreshold", 5.0);
        assert_eq!(rig.run(Box::new(UpdateStandingThresholdsForEntity), standings), Status::Success);
        assert_eq!(
            rig.world.take_calls(),
            [
                "update_slim_item_field_multi(1, [(\"hostile_response_threshold\", Float(-5.0)), (\"friendly_response_threshold\", Float(5.0))])"
            ]
        );
        let fw = |method: &str, disabled: bool| {
            Attrs::new().with("fwAttackMethod", method).with("groupStandingsDisabled", disabled)
        };
        rig.run(Box::new(UpdateFwAttackMethodForEntity), fw("None", false));
        assert_eq!(rig.world.take_calls(), ["update_slim_item_field(1, fwAttackMethod, None)"]);
        rig.run(Box::new(UpdateFwAttackMethodForEntity), fw("attackFwAllEnemies", true));
        assert_eq!(
            rig.world.take_calls(),
            [
                "update_slim_item_field(1, fwAttackMethod, Str(\"attackFwAllEnemies\"))",
                "update_slim_item_field(1, group_standings_disabled, Bool(true))"
            ]
        );
    }

    #[test]
    fn the_entities_of_the_types_with_a_behavior_are_counted() {
        let mut rig = rig();
        rig.world.behavior_entries = vec![20, 21, 22, 23];
        for (id, type_id) in [(20, 600), (21, 601), (22, 600)] {
            rig.world.slims.insert(id, SlimItem { item_id: id, type_id, ..Default::default() });
        }
        let attrs = Attrs::new()
            .with("typeIDsToCount", Value::set([Value::Int(600)]))
            .with("resultAddress", item("Count"));
        assert_eq!(rig.run(Box::new(CountBehaviorEntitiesOfTypeInSystem), attrs), Status::Success);
        assert_eq!(rig.last(ScopeType::Item, "Count"), Value::Int(2));
    }

    #[test]
    fn the_cargo_is_reported_emptied_and_filled_only_for_an_entity_that_has_a_cargo_bay() {
        let attrs = Attrs::new().with("blackboardAddress", item("Out"));
        let mut rig = rig();
        rig.world.cargo.insert(1, crate::env::CargoCapacity { used: 25.0, capacity: 100.0 });
        assert_eq!(rig.run(Box::new(SendCargoUsedFractionToBlackboard), attrs.clone()), Status::Success);
        assert_eq!(rig.last(ScopeType::Item, "Out"), Value::None, "no cargo bay");
        rig.world.cargo_bay_types.insert(700);
        rig.run(Box::new(SendCargoUsedFractionToBlackboard), attrs.clone());
        assert_eq!(rig.last(ScopeType::Item, "Out"), Value::Float(0.25));
        rig.run(Box::new(SendCargoUsedToBlackboard), attrs.clone());
        assert_eq!(rig.last(ScopeType::Item, "Out"), Value::Float(25.0));
        rig.run(Box::new(SendCargoCapacityToBlackboard), attrs);
        assert_eq!(rig.last(ScopeType::Item, "Out"), Value::Float(100.0));
        rig.world.take_calls();
        rig.world.cargo_contents.insert(1, vec![50]);
        rig.run(Box::new(RemoveAllCargo), Attrs::new());
        rig.run(Box::new(AddTypeAndAmountToCargo), Attrs::new().with("typeId", 34i64).with("amount", 10i64));
        let calls = rig.world.take_calls();
        assert_eq!(calls.len(), 2, "{calls:?}");
    }
}
