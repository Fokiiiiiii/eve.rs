//! `behaviors/actions/drones.py`: launching drones and telling them what to attack. `SpawnDrones` is `SpawnNewEntities::drones()`; its getter and its
//! spawner are the parts of `EntityGetter` and `EntitySpawner` that call here.

use crate::constants::blackboardchannels::GROUP_PRIMARY_TARGET_CHANNEL_NAME;
use crate::env::EntitySpawnData;
use crate::tree::{BehaviorError, Task, TaskCtx};
use crate::utility::entity_groups::entity_group_exists;
use crate::utility::spawning::EntityGetter;
use crate::value::Value;

use super::entityspawning::get_behavior_override_id;

fn type_error(t: &mut TaskCtx<'_, '_>, what: &str) {
    t.raise(BehaviorError::Task(format!("TypeError: {what}")));
}

/// `DroneGetter._get_active_drone_count`: the drones of my drone group; a group that is gone is forgotten.
fn get_active_drone_count(t: &mut TaskCtx<'_, '_>) -> Option<i64> {
    let drone_group = t.value_at("droneGroupIdAddress")?;
    let Some(drone_group_id) = drone_group.as_int() else { return Some(0) };
    if !entity_group_exists(t, drone_group_id) {
        let address = t.required_address("droneGroupIdAddress")?;
        t.send_blackboard_value(&address, Value::None);
        return Some(0);
    }
    Some(t.world().group_count(drone_group_id).unwrap_or(0) as i64)
}

/// `DroneGetter._get_max_drones`: as many as the controller may launch, and no more than it may have out at once.
fn get_max_drones(t: &mut TaskCtx<'_, '_>, drones_spawned_total: i64) -> Option<i64> {
    let max_drone_count = t.value_at("maxDroneCountAddress")?;
    let max_drone_active_count = t.value_at("maxDroneActiveCountAddress")?;
    let (Some(count), Some(active)) = (max_drone_count.as_int(), max_drone_active_count.as_int()) else {
        type_error(t, "unsupported operand type(s) for -: 'NoneType' and 'int'");
        return None;
    };
    Some((count - drones_spawned_total).min(active))
}

/// `DroneGetter.get_spawn_data_for_entities`: the drones that are missing from those the controller may have out.
pub fn get_spawn_data_for_drones(t: &mut TaskCtx<'_, '_>) -> Vec<EntitySpawnData> {
    let Some(drone_type_id) = t.required("droneTypeId") else { return Vec::new() };
    let Some(spawned) = t.value_at("dronesSpawnedCountAddress") else { return Vec::new() };
    let Some(drones_spawned_total) = spawned.as_int() else {
        type_error(t, "unsupported operand type(s) for -: 'int' and 'NoneType'");
        return Vec::new();
    };
    let Some(max_drones) = get_max_drones(t, drones_spawned_total) else { return Vec::new() };
    let Some(active_drone_count) = get_active_drone_count(t) else { return Vec::new() };
    let drone_count_for_spawning = max_drones - active_drone_count;
    let Some(drone_type_id) = drone_type_id.as_int() else {
        type_error(t, "int() argument must be a string or a number");
        return Vec::new();
    };
    // `_reduce_drone_capacity`
    let Some(address) = t.required_address("dronesSpawnedCountAddress") else { return Vec::new() };
    t.send_blackboard_value(&address, Value::Int(drones_spawned_total + drone_count_for_spawning));
    let behavior_override_id = get_behavior_override_id(t);
    EntityGetter::spawn_data(t, &[(drone_type_id, drone_count_for_spawning)], behavior_override_id.as_ref())
}

/// `DroneSpawner.get_group_id`: my drone group, when it is still there.
pub fn get_drone_spawn_group_id(t: &mut TaskCtx<'_, '_>) -> Option<i64> {
    let drone_group = t.value_at("droneGroupIdAddress")?;
    let drone_group_id = drone_group.as_int()?;
    if entity_group_exists(t, drone_group_id) { Some(drone_group_id) } else { None }
}

/// Tells my drone group what to attack.
#[derive(Debug, Default)]
pub struct AssignTargetToDroneGroup;

impl Task for AssignTargetToDroneGroup {
    fn type_name(&self) -> &'static str {
        "AssignTargetToDroneGroup"
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        t.set_status_to_failed();
        let Some(drone_group) = t.value_at("droneGroupIdAddress") else { return };
        let Some(drone_group_id) = drone_group.as_int() else { return };
        if !entity_group_exists(t, drone_group_id) {
            return;
        }
        let Some(target_id) = t.value_at("targetIdAddress") else { return };
        let channel = t.channel_for_group(drone_group_id, GROUP_PRIMARY_TARGET_CHANNEL_NAME);
        t.channel_send(&channel, target_id);
        t.set_status_to_success();
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::actions::entityspawning::SpawnNewEntities;
    use crate::blackboards::{Address, ScopeType, entity_group_scope};
    use crate::env::testing::TestGroup;
    use crate::tree::tests::Rig;
    use crate::tree::{Attrs, Status};

    fn item(name: &str) -> Value {
        Address::new(ScopeType::Item, name).to_value()
    }

    fn drone_attrs() -> Attrs {
        Attrs::new()
            .with("droneTypeId", 2454i64)
            .with("dronesSpawnedCountAddress", item("Spawned"))
            .with("maxDroneCountAddress", item("MaxCount"))
            .with("maxDroneActiveCountAddress", item("MaxActive"))
            .with("droneGroupIdAddress", item("DroneGroup"))
            .with("spawnNewGroup", false)
            .with("shouldSelfDestructOnControllerDeath", true)
    }

    fn drone_rig() -> Rig {
        let mut rig = Rig::new();
        rig.world.add_ball(1, [5.0, 6.0, 7.0], 700);
        rig.world.groups.insert(10, TestGroup { members: vec![1], owner_id: Some(7), ..Default::default() });
        rig.ctx.my_slim_item =
            Some(crate::env::SlimItem { item_id: 1, type_id: 700, owner_id: Some(7), ..Default::default() });
        rig.post(ScopeType::Item, "Spawned", Value::Int(2));
        rig.post(ScopeType::Item, "MaxCount", Value::Int(10));
        rig.post(ScopeType::Item, "MaxActive", Value::Int(3));
        rig
    }

    #[test]
    fn drones_are_launched_up_to_what_may_be_out_at_once_from_the_position_of_the_controller() {
        let mut rig = drone_rig();
        assert_eq!(rig.run(Box::new(SpawnNewEntities::drones()), drone_attrs()), Status::Success);
        assert_eq!(rig.last_error, None);
        let deployment = &rig.world.deployments[0];
        assert_eq!(deployment.spawn_data.len(), 3, "3 may be out and none are");
        assert!(
            deployment
                .spawn_data
                .iter()
                .all(|d| d.entity_type_id == 2454 && d.spawn_position == Some([5.0, 6.0, 7.0]))
        );
        assert_eq!(rig.last(ScopeType::Item, "Spawned"), Value::Int(5));
        // the new group is the drone group; its controller and its instruction to self destruct are on its blackboard
        assert_eq!(rig.last(ScopeType::Item, "DroneGroup"), Value::Int(9001));
        assert_eq!(rig.last_in(entity_group_scope(9001), "DRONE_CONTROLLER"), Value::Int(1));
        assert_eq!(
            rig.last_in(entity_group_scope(9001), "SELF_DESTRUCT_DRONE_ON_CONTROLLER_DEATH"),
            Value::Bool(true)
        );
    }

    #[test]
    fn the_drones_that_are_out_count_against_the_active_maximum_and_a_group_that_is_gone_is_forgotten() {
        let mut rig = drone_rig();
        rig.world.groups.insert(30, TestGroup { members: vec![40, 41], ..Default::default() });
        rig.post(ScopeType::Item, "DroneGroup", Value::Int(30));
        assert_eq!(rig.run(Box::new(SpawnNewEntities::drones()), drone_attrs()), Status::Success);
        let deployment = &rig.world.deployments[0];
        assert_eq!(deployment.group_id, Some(30), "the drones join the group that is there");
        assert_eq!(deployment.spawn_data.len(), 1, "3 may be out and 2 are");
        assert_eq!(rig.last(ScopeType::Item, "DroneGroup"), Value::Int(30));
        let mut rig = drone_rig();
        rig.post(ScopeType::Item, "DroneGroup", Value::Int(31));
        assert_eq!(rig.run(Box::new(SpawnNewEntities::drones()), drone_attrs()), Status::Success);
        assert_eq!(rig.world.deployments[0].group_id, None, "group 31 is gone: a new group");
        assert_eq!(rig.last(ScopeType::Item, "DroneGroup"), Value::Int(9001));
    }

    #[test]
    fn launching_drones_does_not_reinforce_the_group_they_join() {
        let mut rig = drone_rig();
        rig.world.groups.insert(30, TestGroup { members: vec![], ..Default::default() });
        rig.world.reinforced_full_strength.insert(30, 4);
        rig.post(ScopeType::Item, "DroneGroup", Value::Int(30));
        assert_eq!(rig.run(Box::new(SpawnNewEntities::drones()), drone_attrs()), Status::Success);
        assert_eq!(rig.world.reinforced_full_strength[&30], 4);
    }

    #[test]
    fn the_controller_of_the_drone_group_is_not_replaced_and_nothing_left_to_launch_fails() {
        let mut rig = drone_rig();
        rig.post(ScopeType::Item, "Spawned", Value::Int(10));
        assert_eq!(
            rig.run(Box::new(SpawnNewEntities::drones()), drone_attrs()),
            Status::Failure,
            "the maximum is launched"
        );
        assert!(rig.world.deployments.is_empty());
    }

    #[test]
    fn a_target_is_assigned_to_a_drone_group_that_is_there() {
        let mut rig = drone_rig();
        let attrs = Attrs::new()
            .with("droneGroupIdAddress", item("DroneGroup"))
            .with("targetIdAddress", item("Target"));
        rig.post(ScopeType::Item, "Target", Value::Int(77));
        assert_eq!(
            rig.run(Box::new(AssignTargetToDroneGroup), attrs.clone()),
            Status::Failure,
            "no drone group"
        );
        rig.world.groups.insert(30, TestGroup { members: vec![40], ..Default::default() });
        rig.post(ScopeType::Item, "DroneGroup", Value::Int(30));
        assert_eq!(rig.run(Box::new(AssignTargetToDroneGroup), attrs), Status::Success);
        assert_eq!(rig.last_in(entity_group_scope(30), "GROUP_PRIMARY_TARGET"), Value::Int(77));
    }
}
