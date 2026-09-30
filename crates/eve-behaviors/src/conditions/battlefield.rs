//! `behaviors/conditions/battlefield.py`.

use crate::tree::{BehaviorError, TaskCtx};
use crate::utility::awareness::{get_fleet_awareness_members_ids, get_spawn_pool_id};
use crate::utility::battlefield::{get_npc_cost_by_group_id, get_player_cost_for_item_ids};
use crate::value::Value;

enter_task!(IsFleetTypeEqualTo, |t| {
    let Some(fleet_type) = t.value_at("fleetTypeToCounterAddress") else { return };
    let expected = t.attribute("fleetType");
    t.set_status_to_success_if_true_else_to_failed(fleet_type.py_eq(&expected));
});

enter_task!(IsAnyFleetMemberMemberAggressed, |t| {
    let Some(pool) = get_spawn_pool_id(t) else { return };
    let member_ids = get_fleet_awareness_members_ids(t, &pool);
    for member_id in member_ids {
        if t.world().is_aggressed(member_id) {
            t.set_status_to_success();
            return;
        }
    }
    t.set_status_to_failed();
});

/// `IsPlayerFleetRelativeStrengthToHigh._get_relative_strength`.
fn relative_strength(t: &mut TaskCtx<'_, '_>) -> Option<f64> {
    let spawn_table_id = t.ctx().my_spawn_table_id;
    let Some(spawn_table_id) = spawn_table_id else {
        t.raise(BehaviorError::Authoring("the behavior is not spawned from a spawn table and should not have IsPlayerFleetRelativeStrengthToHigh assigned".to_string()));
        return None;
    };
    let group = t.ctx().my_entity_group_id;
    let npc_fleet_cost = get_npc_cost_by_group_id(t, group, Some(spawn_table_id));
    let item_ids = t.value_at("playerItemIdsAddress")?;
    let item_ids: Vec<i64> = item_ids.items().unwrap_or(&[]).iter().filter_map(Value::as_int).collect();
    let player_fleet_cost = get_player_cost_for_item_ids(t, &item_ids);
    if npc_fleet_cost == 0.0 {
        t.raise(BehaviorError::Task("ZeroDivisionError: float division by zero".to_string()));
        return None;
    }
    Some(player_fleet_cost / npc_fleet_cost)
}

enter_task!(IsPlayerFleetRelativeStrengthToHigh, |t| {
    t.set_status_to_failed();
    let Some(strength) = relative_strength(t) else { return };
    let Some(threshold) = t.required_float("relativeStrengthThreshold") else { return };
    if strength > threshold {
        t.set_status_to_success();
    }
});

#[cfg(test)]
mod tests {
    use super::*;
    use crate::blackboards::{Address, ScopeType};
    use crate::tree::tests::Rig;
    use crate::tree::{Attrs, Status};

    fn item(name: &str) -> Value {
        Address::new(ScopeType::Item, name).to_value()
    }

    #[test]
    fn a_fleet_type_equal_to_the_wanted_one() {
        let attrs = Attrs::new().with("fleetTypeToCounterAddress", item("F")).with("fleetType", 2i64);
        let mut rig = Rig::new();
        rig.post(ScopeType::Item, "F", Value::Int(2));
        assert_eq!(rig.run(Box::new(IsFleetTypeEqualTo), attrs.clone()), Status::Success);
        let mut rig = Rig::new();
        rig.post(ScopeType::Item, "F", Value::Int(3));
        assert_eq!(rig.run(Box::new(IsFleetTypeEqualTo), attrs), Status::Failure);
    }

    #[test]
    fn any_fleet_member_aggressed_looks_at_the_pools_awareness() {
        let attrs = Attrs::new().with("spawnPoolIdAddress", item("P"));
        let mut rig = Rig::new();
        rig.post(ScopeType::Item, "P", Value::Int(4));
        rig.world.fleet_members.insert(4, vec![10, 11]);
        rig.world.aggressed.insert(11);
        assert_eq!(rig.run(Box::new(IsAnyFleetMemberMemberAggressed), attrs.clone()), Status::Success);
        let mut rig = Rig::new();
        rig.post(ScopeType::Item, "P", Value::Int(4));
        rig.world.fleet_members.insert(4, vec![10, 11]);
        assert_eq!(rig.run(Box::new(IsAnyFleetMemberMemberAggressed), attrs), Status::Failure);
    }

    #[test]
    fn the_players_fleet_is_too_strong_when_its_cost_over_the_npcs_is_above_the_threshold() {
        let attrs =
            Attrs::new().with("playerItemIdsAddress", item("Players")).with("relativeStrengthThreshold", 1.5);
        let setup = |rig: &mut Rig, npc: f64| {
            rig.ctx.my_spawn_table_id = Some(3);
            rig.world
                .groups
                .insert(10, crate::env::testing::TestGroup { members: vec![1], ..Default::default() });
            rig.world.npc_fleet_costs.insert(3, npc);
            rig.world.player_fleet_costs.insert(50, 200.0);
            rig.post(ScopeType::Item, "Players", Value::List(vec![Value::Int(50)]));
        };
        let mut rig = Rig::new();
        setup(&mut rig, 100.0);
        assert_eq!(rig.run(Box::new(IsPlayerFleetRelativeStrengthToHigh), attrs.clone()), Status::Success);
        let mut rig = Rig::new();
        setup(&mut rig, 200.0);
        assert_eq!(rig.run(Box::new(IsPlayerFleetRelativeStrengthToHigh), attrs.clone()), Status::Failure);
        let mut rig = Rig::new();
        setup(&mut rig, 0.0);
        rig.run(Box::new(IsPlayerFleetRelativeStrengthToHigh), attrs.clone());
        assert!(matches!(rig.last_error, Some(BehaviorError::Task(ref m)) if m.contains("ZeroDivision")));
        let mut rig = Rig::new();
        rig.run(Box::new(IsPlayerFleetRelativeStrengthToHigh), attrs);
        assert!(
            matches!(rig.last_error, Some(BehaviorError::Authoring(_))),
            "a behavior not spawned from a table is authored wrong"
        );
    }
}
