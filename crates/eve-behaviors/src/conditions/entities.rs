//! `behaviors/conditions/entities.py`.

use crate::consts::{OWNER_UNKNOWN, is_npc};
use crate::utility::dogmatic::my_type_id;
use crate::utility::inventory::{is_my_cargo_empty, is_my_cargo_full};
use crate::utility::roles::is_commander;
enter_task!(HasAnyOfTypesInEntityAdditionalLoot, |t| {
    let Some(type_ids) = t.required("typeIds") else { return };
    let Some(me) = t.my_item_id() else { return };
    for type_id in type_ids.items().unwrap_or(&[]) {
        if let Some(id) = type_id.as_int()
            && t.world().has_type_in_additional_loot(me, id)
        {
            t.set_status_to_success();
            return;
        }
    }
    t.set_status_to_failed();
});

enter_task!(OwnerIsNpcCondition, |t| {
    let Some(owner) = t.value_at("ownerIdAddress") else { return };
    let is_npc_owner = owner.truthy() && owner.as_int().is_some_and(|id| is_npc(id) || id == OWNER_UNKNOWN);
    t.set_status_to_success_if_true_else_to_failed(is_npc_owner);
});

enter_task!(HasEnoughCargoForType, |t| {
    let Some(type_id) = t.value_at("typeIdAddress") else { return };
    let Some(me) = t.my_item_id() else { return };
    let enough = type_id.truthy()
        && type_id
            .as_int()
            .is_some_and(|id| t.world().has_enough_additional_cargo_capacity_for_type(me, id, 1));
    t.set_status_to_success_if_true_else_to_failed(enough);
});

enter_task!(IsCargoAboveCapacityThreshold, |t| {
    t.set_status_to_failed();
    let Some(type_id) = my_type_id(t) else { return };
    let total_capacity = t.world().type_attribute_by_name(type_id, "capacity");
    if total_capacity == 0.0 {
        // the client logs an error and gives up
        return;
    }
    let Some(me) = t.my_item_id() else { return };
    let used_capacity = total_capacity - t.world().available_additional_cargo_capacity(me);
    let Some(threshold) = t.required_float("cargoCapacityThreshold") else { return };
    if used_capacity / total_capacity >= threshold {
        t.set_status_to_success();
    }
});

enter_task!(AmITheCommander, |t| {
    t.set_status_to_failed();
    let Some(me) = t.my_item_id() else { return };
    if is_commander(t, me) {
        t.set_status_to_success();
    }
});

enter_task!(IsMyCargoFull, |t| {
    t.set_status_to_failed();
    if is_my_cargo_full(t) {
        t.set_status_to_success();
    }
});

enter_task!(IsMyCargoEmpty, |t| {
    t.set_status_to_failed();
    if is_my_cargo_empty(t) {
        t.set_status_to_success();
    }
});

#[cfg(test)]
mod tests {
    use super::*;
    use crate::blackboards::{Address, ScopeType};
    use crate::constants::blackboardchannels::commander_address;
    use crate::env::{CargoCapacity, SlimItem};
    use crate::tree::tests::Rig;
    use crate::tree::{Attrs, Status};
    use crate::value::Value;

    fn ids(ids: &[i64]) -> Value {
        Value::List(ids.iter().map(|i| Value::Int(*i)).collect())
    }

    #[test]
    fn an_entity_has_a_type_in_its_additional_loot() {
        let mut rig = Rig::new();
        rig.world.additional_loot.insert(1, vec![34]);
        assert_eq!(
            rig.run(
                Box::new(HasAnyOfTypesInEntityAdditionalLoot),
                Attrs::new().with("typeIds", ids(&[33, 34]))
            ),
            Status::Success
        );
        assert_eq!(
            rig.run(Box::new(HasAnyOfTypesInEntityAdditionalLoot), Attrs::new().with("typeIds", ids(&[35]))),
            Status::Failure
        );
    }

    #[test]
    fn an_owner_is_an_npc_when_it_is_between_the_system_and_the_players_or_unknown() {
        let attrs = Attrs::new().with("ownerIdAddress", Address::new(ScopeType::Item, "O").to_value());
        for (owner, wanted) in [
            (1000006, Status::Success),
            (3006, Status::Success),
            (5, Status::Failure),
            (98_000_000, Status::Failure),
            (0, Status::Failure),
        ] {
            let mut rig = Rig::new();
            rig.post(ScopeType::Item, "O", Value::Int(owner));
            assert_eq!(rig.run(Box::new(OwnerIsNpcCondition), attrs.clone()), wanted, "owner {owner}");
        }
    }

    #[test]
    fn cargo_for_a_type_and_above_a_capacity_threshold() {
        let mut rig = Rig::new();
        rig.world.additional_capacity.insert(1, 10.0);
        rig.world.type_attributes_by_name.insert((34, "volume".into()), 4.0);
        rig.post(ScopeType::Item, "T", Value::Int(34));
        assert_eq!(
            rig.run(
                Box::new(HasEnoughCargoForType),
                Attrs::new().with("typeIdAddress", Address::new(ScopeType::Item, "T").to_value())
            ),
            Status::Success
        );

        let attrs = Attrs::new().with("cargoCapacityThreshold", 0.5);
        let mut rig = Rig::new();
        rig.ctx.my_slim_item = Some(SlimItem { type_id: 500, ..Default::default() });
        rig.world.type_attributes_by_name.insert((500, "capacity".into()), 100.0);
        rig.world.additional_capacity.insert(1, 40.0);
        assert_eq!(
            rig.run(Box::new(IsCargoAboveCapacityThreshold), attrs.clone()),
            Status::Success,
            "60 of 100 used"
        );
        rig.world.additional_capacity.insert(1, 60.0);
        assert_eq!(rig.run(Box::new(IsCargoAboveCapacityThreshold), attrs.clone()), Status::Failure);
        let mut rig = Rig::new();
        rig.ctx.my_slim_item = Some(SlimItem { type_id: 501, ..Default::default() });
        assert_eq!(
            rig.run(Box::new(IsCargoAboveCapacityThreshold), attrs),
            Status::Failure,
            "no cargo at all"
        );
    }

    #[test]
    fn the_commander_and_a_full_or_empty_cargo() {
        let mut rig = Rig::new();
        {
            let mut cx = crate::tree::Cx {
                core: &mut rig.tree.core,
                ctx: &mut rig.ctx,
                shared: &mut rig.shared,
                world: &mut rig.world,
            };
            cx.send_blackboard_value(&commander_address(), Value::Int(1));
        }
        assert_eq!(rig.run(Box::new(AmITheCommander), Attrs::new()), Status::Success);
        let mut rig = Rig::new();
        rig.world.cargo.insert(1, CargoCapacity { used: 100.0, capacity: 100.0 });
        assert_eq!(rig.run(Box::new(IsMyCargoFull), Attrs::new()), Status::Success);
        assert_eq!(rig.run(Box::new(IsMyCargoEmpty), Attrs::new()), Status::Failure);
        let mut rig = Rig::new();
        rig.world.cargo.insert(1, CargoCapacity { used: 0.0, capacity: 100.0 });
        assert_eq!(rig.run(Box::new(IsMyCargoEmpty), Attrs::new()), Status::Success);
    }
}
