//! `behaviors/actions/entityspawning.py`: the tasks that spawn NPCs, keep spawn point pools and respawn what is lost. The classes of the client
//! that only choose another getter and spawner are one task here with the two of them (`SpawnNewEntities::at_structure()` and the others).

use crate::constants::blackboardchannels::GUARD_OBJECT_ITEM_ID_MESSAGE_CHANNEL;
use crate::env::{ReactiveSpawnConfig, WorldError};
use crate::tree::{BehaviorError, Task, TaskCtx};
use crate::utility::awareness::{get_awareness_config, get_spawn_pool_id as get_spawn_pool_id_of_blackboard};
use crate::utility::ballparks::get_my_ball;
use crate::utility::context::get_extra_context;
use crate::utility::groups::get_entity_group_member_count;
use crate::utility::owner::get_owner_id;
use crate::utility::spawning::{EntityGetter, EntitySpawner};
use crate::value::Value;

/// `get_behavior_override_id`: `behaviorTreeName` if the task has it.
pub fn get_behavior_override_id(t: &mut TaskCtx<'_, '_>) -> Option<Value> {
    let name = t.attribute("behaviorTreeName");
    if name.is_none() { None } else { Some(name) }
}

/// `get_required_npc_tags`: `npcTagList`, else no tags.
pub fn get_required_npc_tags(t: &mut TaskCtx<'_, '_>) -> Value {
    if t.has_attribute("npcTagList") { t.attribute("npcTagList") } else { Value::set(std::iter::empty()) }
}

/// `get_bounty_pay_out_multiplier`: `bountyPayOutMultiplier` if the task has it.
pub fn get_bounty_pay_out_multiplier(t: &mut TaskCtx<'_, '_>) -> Option<Value> {
    let multiplier = t.attribute("bountyPayOutMultiplier");
    if multiplier.is_none() { None } else { Some(multiplier) }
}

/// `SpawnNewEntities.get_spawn_pool_id`: the pool on the channel of `spawnPoolIdAddress`, when the task has one.
pub fn get_spawn_pool_id(t: &mut TaskCtx<'_, '_>) -> Option<Value> {
    if t.has_attribute("spawnPoolIdAddress") { t.value_at("spawnPoolIdAddress") } else { None }
}

/// `SpawnNewEntities.get_spawn_table_id`: the table on the channel of `spawnTableIdAddress`, when the task has one.
pub fn get_spawn_table_id_of_task(t: &mut TaskCtx<'_, '_>) -> Option<Value> {
    if t.has_attribute("spawnTableIdAddress") { t.value_at("spawnTableIdAddress") } else { None }
}

/// `SpawnNewEntities.get_spawn_position` (and `SpawnDrones.get_spawn_position` for `drones`): where the new entities are, the task's or a deep
/// space location, and for drones where the controller is.
pub fn get_spawn_position(t: &mut TaskCtx<'_, '_>, drones: bool) -> Option<[f64; 3]> {
    if drones {
        return get_my_ball(t).map(|ball| ball.position);
    }
    if t.attribute("spawnPositionAddress").truthy() {
        let address = t.required_address("spawnPositionAddress")?;
        return t.get_last_blackboard_value(&address).as_vec3();
    }
    let system = t.ctx().solar_system_id;
    Some(t.world().random_deep_space_location(system, None))
}

/// Registers a new spawn point pool and puts its id on the blackboard.
#[derive(Debug, Default)]
pub struct RegisterSpawnPool;

impl Task for RegisterSpawnPool {
    fn type_name(&self) -> &'static str {
        "RegisterSpawnPool"
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        let Some(max_spawn_points) = t.required("maxSpawnPoints") else { return };
        if !max_spawn_points.truthy() {
            t.set_status_to_failed();
            return;
        }
        let initial_spawn_points = t.attribute("initialSpawnPoints");
        let spawn_point_pool_id =
            t.world().register_new_spawn_point_pool(&max_spawn_points, &initial_spawn_points);
        let Some(address) = t.required_address("spawnPoolIdAddress") else { return };
        t.send_blackboard_value(&address, Value::Int(spawn_point_pool_id));
        t.set_status_to_success();
    }
}

/// `SpawnNewEntities` and the tasks of the client that are it with another getter or spawner.
#[derive(Debug, Clone, Copy)]
pub struct SpawnNewEntities {
    name: &'static str,
    getter: EntityGetter,
    spawner: EntitySpawner,
}

impl SpawnNewEntities {
    /// `SpawnNewEntities`: the task's `entities` at a position.
    pub fn new() -> Self {
        Self { name: "SpawnNewEntities", getter: EntityGetter::Plain, spawner: EntitySpawner::Plain }
    }

    pub fn by_spawnlist_and_owner_id() -> Self {
        Self {
            name: "SpawnNewEntitiesBySpawnlistAndOwnerId",
            getter: EntityGetter::BySpawnlistAndOwner,
            ..Self::new()
        }
    }

    /// `SpawnEntitiesFromSpawnTable`.
    pub fn from_spawn_table() -> Self {
        Self {
            name: "SpawnEntitiesFromSpawnTable",
            getter: EntityGetter::FromSpawnPointPool,
            spawner: EntitySpawner::SpawnTable,
        }
    }

    /// `SpawnNewEntitiesAtStructure`.
    pub fn at_structure() -> Self {
        Self { name: "SpawnNewEntitiesAtStructure", spawner: EntitySpawner::WithUndock, ..Self::new() }
    }

    pub fn at_structure_by_spawnlist_and_owner_id() -> Self {
        Self {
            name: "SpawnNewEntitiesAtStructureBySpawnlistAndOwnerId",
            getter: EntityGetter::BySpawnlistAndOwner,
            spawner: EntitySpawner::WithUndock,
        }
    }

    /// `SpawnNewEntitiesAtStructureFromSpawnTable`.
    pub fn at_structure_from_spawn_table() -> Self {
        Self {
            name: "SpawnNewEntitiesAtStructureFromSpawnTable",
            getter: EntityGetter::FromSpawnPointPool,
            spawner: EntitySpawner::WithUndock,
        }
    }

    /// `SpawnDrones` (`actions/drones.py`).
    pub fn drones() -> Self {
        Self { name: "SpawnDrones", getter: EntityGetter::Drone, spawner: EntitySpawner::Drone }
    }

    fn is_drones(&self) -> bool {
        self.spawner == EntitySpawner::Drone
    }

    /// `_get_max_npc_count_for_group`: what `maxNpcCount` leaves after the members the group has.
    fn max_npc_count_for_group(t: &mut TaskCtx<'_, '_>, group_id: Option<i64>) -> Option<Option<i64>> {
        if !t.has_attribute("maxNpcCount") {
            return Some(None);
        }
        let mut max_npc_count = t.attribute("maxNpcCount").as_int()?;
        if let Some(group_id) = group_id {
            let group_count = get_entity_group_member_count(t, group_id) as i64;
            if t.faulted() {
                return None;
            }
            max_npc_count -= group_count;
            if max_npc_count < 0 {
                max_npc_count = 0;
            }
        }
        Some(Some(max_npc_count))
    }

    /// `_update_group`.
    fn update_group(
        &self,
        t: &mut TaskCtx<'_, '_>,
        entity_group_id: i64,
        entity_count: usize,
        existing_group_id: Option<i64>,
    ) {
        self.update_reinforcements_for_group(t, entity_count as i64, existing_group_id);
        self.register_entity_group_with_blackboard(t, entity_group_id);
        self.update_group_blackboard_channels(t, entity_group_id);
    }

    /// `_update_reinforcements_for_group`: the group I spawn into counts more units at full strength (nothing for drones).
    fn update_reinforcements_for_group(
        &self,
        t: &mut TaskCtx<'_, '_>,
        entity_count: i64,
        existing_group_id: Option<i64>,
    ) {
        if self.is_drones() {
            return;
        }
        let Some(existing) = existing_group_id else { return };
        let Some(old_count) = t.world().reinforced_group_full_strength_count(existing) else { return };
        t.world().set_reinforced_group_entity_unit_count(existing, old_count + entity_count);
    }

    /// `_register_entity_group_with_blackboard`: a new group is added to the set of groups; the drones' group is remembered.
    fn register_entity_group_with_blackboard(&self, t: &mut TaskCtx<'_, '_>, entity_group_id: i64) {
        if self.is_drones() {
            let Some(existing) = t.value_at("droneGroupIdAddress") else { return };
            if existing.is_none()
                && let Some(address) = t.required_address("droneGroupIdAddress")
            {
                t.send_blackboard_value(&address, Value::Int(entity_group_id));
            }
            return;
        }
        if !t.has_attribute("entityGroupSetAddress") {
            return;
        }
        let Some(spawn_new_group) = t.required("spawnNewGroup") else { return };
        if !spawn_new_group.truthy() {
            return;
        }
        let Some(address) = t.required_address("entityGroupSetAddress") else { return };
        // `GetLastBlackboardValue(...) or set()`, then `add` and `Send`: the set that is there is changed in place
        let last = t.get_last_blackboard_value(&address);
        let stored = last.truthy();
        let mut set = if stored { last } else { Value::set(std::iter::empty()) };
        set.set_add(Value::Int(entity_group_id));
        if stored {
            let in_place = set.clone();
            t.with_channel(&address, |channel| {
                if let Some(last) = channel.last_value_mut() {
                    *last = in_place;
                }
            });
        }
        t.send_blackboard_value(&address, set);
    }

    /// `_update_group_blackboard_channels`.
    fn update_group_blackboard_channels(&self, t: &mut TaskCtx<'_, '_>, entity_group_id: i64) {
        if self.is_drones() {
            let controller = t.channel_for_group(
                entity_group_id,
                crate::constants::blackboardchannels::DRONE_CONTROLLER_CHANNEL_NAME,
            );
            if !t.channel_has_message(&controller) {
                let me = t.ctx().my_item_id.map_or(Value::None, Value::Int);
                t.channel_send(&controller, me);
            }
            let self_destruct = t.channel_for_group(
                entity_group_id,
                crate::constants::blackboardchannels::SELF_DESTRUCT_DRONE_ON_CONTROLLER_DEATH_CHANNEL_NAME,
            );
            let Some(should_self_destruct) = t.required("shouldSelfDestructOnControllerDeath") else {
                return;
            };
            t.channel_send(&self_destruct, should_self_destruct);
            return;
        }
        if t.has_attribute("guardObjectAddress") {
            let Some(guard_object_item_id) = t.value_at("guardObjectAddress") else { return };
            let channel = t.channel_for_group(entity_group_id, GUARD_OBJECT_ITEM_ID_MESSAGE_CHANNEL);
            t.channel_send(&channel, guard_object_item_id);
        }
    }
}

impl Default for SpawnNewEntities {
    fn default() -> Self {
        Self::new()
    }
}

impl Task for SpawnNewEntities {
    fn type_name(&self) -> &'static str {
        self.name
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        t.set_status_to_failed();
        let (getter, spawner) = (self.getter, self.spawner);
        let existing_entity_group_id = spawner.get_group_id(t);
        if t.faulted() {
            return;
        }
        let Some(max_npc_count) = Self::max_npc_count_for_group(t, existing_entity_group_id) else { return };
        let spawn_data = getter.get_spawn_data_for_entities(t, max_npc_count);
        if t.faulted() {
            return;
        }
        if spawn_data.is_empty() {
            return;
        }
        let Some(deployed) = spawner.spawn_new_entities(t, spawn_data) else { return };
        self.update_group(t, deployed.group_id, deployed.entity_item_ids.len(), existing_entity_group_id);
        if t.faulted() {
            return;
        }
        getter.on_spawn_entities(t, deployed.group_id);
        t.set_status_to_success();
    }
}

enter_task!(
    /// Puts the spawn table of the task on a blackboard.
    SendSpawnTableIdToBlackboard,
    |t| {
        let Some(address) = t.required_address("npcSpawnTableIdAddress") else { return };
        let Some(table) = t.required("npcSpawnTableId") else { return };
        t.send_blackboard_value(&address, table);
        t.set_status_to_success();
    }
);

enter_task!(
    /// Sets what a spawn point pool has.
    SetSpawnPoolAmount,
    |t| {
        let Some(spawn_pool_id) = get_spawn_pool_id_of_blackboard(t) else { return };
        let Some(amount) = t.required("spawnPointAmount") else { return };
        t.world().set_spawn_points(spawn_pool_id.as_int(), &amount);
        t.set_status_to_success();
    }
);

enter_task!(
    /// Has the entity respawn, itself, in its group when it is lost.
    SetSelfEntityRespawn,
    |t| {
        let Some(slim) = t.ctx().my_slim_item.clone() else {
            t.raise(BehaviorError::Task("AttributeError: 'NoneType' object has no attribute 'typeID'".to_string()));
            return;
        };
        let Some(ball) = get_my_ball(t) else { return };
        let Some(range) = t.required_float("deploymentRangeMeters") else { return };
        let behavior_id = t.behavior_id();
        let data = t.world().spawn_data_for_entities(&[(slim.type_id, 1)], Some(ball.position), range, Some(&behavior_id));
        let (Some(min), Some(max)) = (t.required("minRespawnDelaySeconds"), t.required("maxRespawnDelaySeconds")) else {
            return;
        };
        let group = t.ctx().my_entity_group_id;
        t.world().set_real_item_respawn(group, slim.owner_id, data, &min, &max);
        t.set_status_to_success();
    }
);

enter_task!(
    /// Takes my group off the groups that respawn.
    DisableRespawnGroup,
    |t| {
        let group = t.ctx().my_entity_group_id;
        t.world().remove_respawn_group(group);
        t.set_status_to_success();
    }
);

enter_task!(SetSpawnTableOnBlackboardByOwner, |t| {
    let owner_id = get_owner_id(t);
    let Some(mapping) = t.required("ownerToSpawnTableMapping") else { return };
    let spawn_table_id = owner_id.and_then(|owner| mapping.dict_get(&Value::Int(owner)).cloned());
    let Some(spawn_table_id) = spawn_table_id.filter(|table| !table.is_none()) else {
        let owner = owner_id.map_or("None".to_string(), |id| id.to_string());
        t.raise(BehaviorError::Task(format!(
            "ValueError: Unable to find a spawn table id for owner {owner}"
        )));
        return;
    };
    let Some(address) = t.required_address("spawnTableIdAddress") else { return };
    t.send_blackboard_value(&address, spawn_table_id);
    t.set_status_to_success();
});

/// Starts a spawner that reacts to what threatens it (`ReactiveSpawning`).
#[derive(Debug, Default)]
pub struct ReactiveSpawning;

impl ReactiveSpawning {
    /// `get_spawn_table_id`: the table on the channel of `spawnTableIdAddress`.
    fn get_spawn_table_id(t: &mut TaskCtx<'_, '_>) -> Option<Value> {
        t.value_at("spawnTableIdAddress")
    }

    /// `_get_spawn_config`.
    fn get_spawn_config(t: &mut TaskCtx<'_, '_>, owner_id: Option<i64>) -> Option<ReactiveSpawnConfig> {
        let spawn_table_id = Self::get_spawn_table_id(t)?;
        let npc_fleet_counter_table_id = t.required("npcFleetCounterTableId")?;
        // this task has a spawn table id and no spawn pool id, and its members are its group
        let table = spawn_table_id.as_int();
        let member_collection_id = t.ctx().my_entity_group_id.map_or(Value::None, Value::Int);
        let awareness_config = get_awareness_config(t, member_collection_id, None, table);
        if t.faulted() {
            return None;
        }
        let initial_point_amount = t.required("initialPointAmount")?;
        let min_point_amount = t.required("minPointAmount")?;
        let max_point_amount = t.required("maxPointAmount")?;
        let extra_context = get_extra_context(t.ctx(), None, table);
        let capped_spawn_points = t.required("cappedSpawnPoints")?;
        let should_spawn_without_threats = t.required("spawnWithoutThreats")?;
        let should_reset_without_threats = t.required("resetWithoutThreats")?;
        let spawn_point_multiplier = t.required("spawnPointMultiplier")?;
        let initial_spawn_npc_fleet_type_id = t.attribute("initialSpawnNpcFleetTypeID");
        let min_spawn_interval_seconds = t.required("minSpawnIntervalSeconds")?;
        let max_spawn_interval_seconds = t.required("maxSpawnIntervalSeconds")?;
        let Some(slim) = t.ctx().my_slim_item.clone() else {
            t.raise(WorldError::Reference.into());
            return None;
        };
        let bounty_payout_multiplier = t.required("bountyPayoutMultiplier")?;
        let should_match_player_points = t.required("shouldMatchPlayerPoints")?;
        let spawn_point_threshold = t.required("spawnPointThreshold")?;
        Some(ReactiveSpawnConfig {
            owner_id,
            spawn_table_id,
            npc_fleet_counter_table_id,
            awareness_config,
            initial_point_amount,
            min_point_amount,
            max_point_amount,
            extra_context,
            capped_spawn_points,
            should_spawn_without_threats,
            should_reset_without_threats,
            spawn_point_multiplier,
            initial_spawn_npc_fleet_type_id,
            min_spawn_interval_seconds,
            max_spawn_interval_seconds,
            spawner_item_id: Some(slim.item_id),
            bounty_payout_multiplier,
            should_match_player_points,
            spawn_point_threshold,
        })
    }
}

impl Task for ReactiveSpawning {
    fn type_name(&self) -> &'static str {
        "ReactiveSpawning"
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        t.set_status_to_success();
        let owner_id = get_owner_id(t);
        if t.faulted() {
            return;
        }
        let Some(spawn_config) = Self::get_spawn_config(t, owner_id) else { return };
        // `start_spawner` and `deregister_when_spawner_is_destroyed` are the server's: it answers the pool the spawner spends
        let spawn_point_pool_id = t.world().start_reactive_spawner(spawn_config);
        let Some(address) = t.required_address("spawnPoolIdAddress") else { return };
        t.send_blackboard_value(&address, Value::Int(spawn_point_pool_id));
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::blackboards::{Address, ScopeType};
    use crate::env::testing::TestGroup;
    use crate::env::{SlimItem, SpawnPick};
    use crate::tree::tests::Rig;
    use crate::tree::{Attrs, Status};

    fn item(name: &str) -> Value {
        Address::new(ScopeType::Item, name).to_value()
    }

    fn group(name: &str) -> Value {
        Address::new(ScopeType::EntityGroup, name).to_value()
    }

    fn entities(list: &[(i64, i64)]) -> Value {
        Value::Dict(list.iter().map(|(t, n)| (Value::Int(*t), Value::Int(*n))).collect())
    }

    fn spawn_attrs() -> Attrs {
        Attrs::new()
            .with("entities", entities(&[(100, 2), (200, 1)]))
            .with("spawnNewGroup", true)
            .with("spawnPositionAddress", item("Where"))
    }

    fn spawn_rig() -> Rig {
        let mut rig = Rig::new();
        rig.ctx.my_slim_item =
            Some(SlimItem { item_id: 1, type_id: 700, owner_id: Some(7), ..Default::default() });
        rig.world
            .groups
            .insert(10, TestGroup { members: vec![1, 2], owner_id: Some(7), ..Default::default() });
        rig.post(ScopeType::Item, "Where", Value::from_vec3([1.0, 2.0, 3.0]));
        rig
    }

    #[test]
    fn new_entities_are_deployed_at_the_position_in_a_new_group_that_joins_the_set_of_groups() {
        let mut rig = spawn_rig();
        rig.post(ScopeType::Item, "Groups", Value::set([Value::Int(3)]));
        let attrs = spawn_attrs()
            .with("entityGroupSetAddress", item("Groups"))
            .with("guardObjectAddress", item("Guard"))
            .with("bountyPayOutMultiplier", 0.5)
            .with("behaviorTreeName", "guards")
            .with("deploymentRange", 5000.0);
        rig.post(ScopeType::Item, "Guard", Value::Int(55));
        assert_eq!(rig.run(Box::new(SpawnNewEntities::new()), attrs), Status::Success);
        assert_eq!(rig.last_error, None);
        let deployment = &rig.world.deployments[0];
        assert_eq!(deployment.owner_id, Some(7));
        assert_eq!(deployment.group_id, None, "a new group");
        assert_eq!(
            deployment.spawn_data.iter().map(|d| d.entity_type_id).collect::<Vec<_>>(),
            [100, 100, 200]
        );
        assert!(deployment.spawn_data.iter().all(|d| d.spawn_position == Some([1.0, 2.0, 3.0])));
        assert!(
            rig.world.calls.iter().any(|c| c.contains("5000") && c.contains("guards")),
            "range and behavior go to the spawn data"
        );
        // the new group (9001) is in the set, has the guard object and its entities carry the bounty multiplier
        let guard = rig.last_in(crate::blackboards::entity_group_scope(9001), "GUARD_OBJECT_ITEM_ID");
        assert_eq!(guard, Value::Int(55));
        assert_eq!(rig.last(ScopeType::Item, "Groups"), Value::set([Value::Int(3), Value::Int(9001)]));
        assert!(rig.world.calls.iter().any(|c| c.starts_with("register_bounty_pay_out_multiplier")));
    }

    #[test]
    fn a_task_with_nothing_to_spawn_fails_and_deploys_nothing() {
        let mut rig = spawn_rig();
        let attrs = spawn_attrs().with("entities", entities(&[]));
        assert_eq!(rig.run(Box::new(SpawnNewEntities::new()), attrs), Status::Failure);
        assert!(rig.world.deployments.is_empty());
    }

    #[test]
    fn spawning_into_the_group_i_am_in_counts_what_it_has_and_reinforces_its_full_strength() {
        let mut rig = spawn_rig();
        rig.world.reinforced_full_strength.insert(10, 4);
        let attrs = spawn_attrs().with("spawnNewGroup", false).with("maxNpcCount", 5i64);
        assert_eq!(rig.run(Box::new(SpawnNewEntities::new()), attrs), Status::Success);
        assert_eq!(rig.world.deployments[0].group_id, Some(10));
        assert_eq!(rig.world.reinforced_full_strength[&10], 7, "4 and the 3 that came");
    }

    #[test]
    fn a_group_that_is_at_its_maximum_gets_no_more_and_the_maximum_is_never_below_0() {
        let mut rig = spawn_rig();
        rig.world.groups.insert(10, TestGroup { members: vec![1, 2, 3, 4], ..Default::default() });
        rig.post(ScopeType::Item, "Table", Value::Int(5));
        rig.world.spawn_table_owners.insert(5, 1);
        let attrs = spawn_attrs()
            .with("spawnNewGroup", false)
            .with("maxNpcCount", 3i64)
            .with("spawnTableIdAddress", item("Table"))
            .with("spawnPoints", 6.0);
        assert_eq!(rig.run(Box::new(SpawnNewEntities::from_spawn_table()), attrs), Status::Failure);
        assert!(
            rig.world.calls.iter().any(|c| c == "pick_npcs(Some(5), 6, Set([]), Some(0))"),
            "{:?}",
            rig.world.calls
        );
    }

    #[test]
    fn a_spawn_table_spawner_pays_from_the_pool_for_what_it_picked_and_tells_the_awareness() {
        let mut rig = spawn_rig();
        rig.world.spawn_points.insert(77, 10.0);
        rig.world.spawn_table_owners.insert(5, 1000125);
        rig.world.spawn_pick = Some(SpawnPick { npcs: vec![(300, 2)], remaining_points: 4.0 });
        rig.post(ScopeType::Item, "Pool", Value::Int(77));
        rig.post(ScopeType::Item, "Table", Value::Int(5));
        let attrs = spawn_attrs()
            .with("spawnTableIdAddress", item("Table"))
            .with("spawnPoolIdAddress", item("Pool"))
            .with("spawnPoints", 6.0)
            .with("npcTagList", Value::set([Value::Str("pirate".into())]));
        assert_eq!(rig.run(Box::new(SpawnNewEntities::from_spawn_table()), attrs), Status::Success);
        assert_eq!(rig.last_error, None);
        assert_eq!(rig.world.deployments[0].owner_id, Some(1000125), "the owner of the spawn table");
        assert_eq!(rig.world.deployments[0].spawn_data.len(), 2);
        assert_eq!(rig.world.spawn_points[&77], 8.0, "10 less what was used: 6 - 4");
        assert!(rig.world.calls.iter().any(|c| c == "on_entity_group_spawn(Some(77), 9001)"));
    }

    #[test]
    fn a_pool_that_has_less_than_the_task_asks_for_spawns_nothing_and_nothing_is_paid() {
        let mut rig = spawn_rig();
        rig.world.spawn_points.insert(77, 3.0);
        rig.world.spawn_pick = Some(SpawnPick { npcs: vec![(300, 2)], remaining_points: 0.0 });
        rig.post(ScopeType::Item, "Pool", Value::Int(77));
        rig.post(ScopeType::Item, "Table", Value::Int(5));
        rig.world.spawn_table_owners.insert(5, 1);
        let attrs = spawn_attrs()
            .with("spawnTableIdAddress", item("Table"))
            .with("spawnPoolIdAddress", item("Pool"))
            .with("spawnPoints", 6.0);
        assert_eq!(rig.run(Box::new(SpawnNewEntities::from_spawn_table()), attrs), Status::Failure);
        assert!(rig.world.deployments.is_empty());
        assert_eq!(rig.world.spawn_points[&77], 3.0);
    }

    #[test]
    fn a_pool_that_has_exactly_what_the_task_asks_for_pays_it() {
        let mut rig = spawn_rig();
        rig.world.spawn_points.insert(77, 6.0);
        rig.world.spawn_table_owners.insert(5, 1);
        rig.world.spawn_pick = Some(SpawnPick { npcs: vec![(300, 1)], remaining_points: 0.0 });
        rig.post(ScopeType::Item, "Pool", Value::Int(77));
        rig.post(ScopeType::Item, "Table", Value::Int(5));
        let attrs = spawn_attrs()
            .with("spawnTableIdAddress", item("Table"))
            .with("spawnPoolIdAddress", item("Pool"))
            .with("spawnPoints", 6.0);
        assert_eq!(rig.run(Box::new(SpawnNewEntities::from_spawn_table()), attrs), Status::Success);
        assert_eq!(rig.world.spawn_points[&77], 0.0);
    }

    #[test]
    fn the_group_on_the_blackboard_is_joined_only_when_it_is_there() {
        for (group_on_blackboard, joined) in [(10, Some(10)), (99, None)] {
            let mut rig = spawn_rig();
            rig.post(ScopeType::Item, "G", Value::Int(group_on_blackboard));
            let attrs = spawn_attrs().with("spawnNewGroup", false).with("entityGroupIdAddress", item("G"));
            assert_eq!(rig.run(Box::new(SpawnNewEntities::new()), attrs), Status::Success);
            assert_eq!(rig.world.deployments[0].group_id, joined);
            // the group the entities ended up in is put on the blackboard
            assert_eq!(rig.last(ScopeType::Item, "G"), Value::Int(joined.unwrap_or(9001)));
        }
    }

    #[test]
    fn a_spawn_table_without_points_is_bad_authoring_and_nothing_is_spawned() {
        let mut rig = spawn_rig();
        rig.world.spawn_pick = Some(SpawnPick { npcs: vec![(300, 1)], remaining_points: 0.0 });
        rig.world.spawn_table_owners.insert(5, 1);
        rig.post(ScopeType::Item, "Table", Value::Int(5));
        let attrs = spawn_attrs().with("spawnTableIdAddress", item("Table")).with("spawnPoints", 0.0);
        assert_eq!(rig.run(Box::new(SpawnNewEntities::from_spawn_table()), attrs), Status::Failure);
        assert!(rig.world.deployments.is_empty());
    }

    #[test]
    fn a_spawn_list_picks_the_group_of_the_owner_by_the_security_of_the_system() {
        let mut rig = spawn_rig();
        rig.ctx.solar_system_id = Some(30000142);
        rig.world.security.insert(30000142, 0.9);
        rig.world.spawn_lists.insert(4321, vec![(88, 1.0), (89, 1.0)]);
        rig.world.groupings.insert(88, vec![(400, 3)]);
        let attrs = spawn_attrs()
            .with("highSecOwnerToSpawnlistMapping", Value::Dict(vec![(Value::Int(7), Value::Int(4321))]));
        assert_eq!(
            rig.run(Box::new(SpawnNewEntities::by_spawnlist_and_owner_id()), attrs.clone()),
            Status::Success
        );
        assert_eq!(rig.world.deployments[0].spawn_data.len(), 3);

        let mut rig2 = rig_with_owner(8);
        rig2.ctx.solar_system_id = Some(30000142);
        rig2.world.security.insert(30000142, 0.9);
        assert_eq!(rig2.run(Box::new(SpawnNewEntities::by_spawnlist_and_owner_id()), attrs), Status::Failure);
    }

    fn rig_with_owner(owner: i64) -> Rig {
        let mut rig = spawn_rig();
        rig.world
            .groups
            .insert(10, TestGroup { members: vec![1, 2], owner_id: Some(owner), ..Default::default() });
        rig
    }

    #[test]
    fn entities_that_come_out_of_a_structure_are_placed_at_its_undock_point_and_told_which_way_to_look() {
        let mut rig = spawn_rig();
        rig.world.undock_vectors.insert((900, 100), ([10.0, 0.0, 0.0], [1.0, 0.0, 0.0]));
        rig.world.undock_vectors.insert((900, 200), ([0.0, 10.0, 0.0], [0.0, 1.0, 0.0]));
        rig.post(ScopeType::Item, "Structure", Value::Int(900));
        let attrs = spawn_attrs().with("structureIdAddress", item("Structure"));
        // the deployed items must have slim items with their types for the direction to be found
        for (offset, type_id) in [(1, 100), (2, 100), (3, 200)] {
            let id = 9_000_000_000_000_000_000 + offset;
            rig.world.slims.insert(id, SlimItem { item_id: id, type_id, ..Default::default() });
        }
        assert_eq!(rig.run(Box::new(SpawnNewEntities::at_structure()), attrs), Status::Success);
        assert_eq!(rig.last_error, None);
        let data = &rig.world.deployments[0].spawn_data;
        assert_eq!(data[0].spawn_position, Some([10.0, 0.0, 0.0]));
        assert_eq!(data[2].direction, Some([0.0, 1.0, 0.0]));
    }

    #[test]
    fn a_spawn_pool_is_registered_and_its_id_posted_and_its_amount_can_be_set() {
        let mut rig = spawn_rig();
        let attrs = Attrs::new()
            .with("maxSpawnPoints", 20.0)
            .with("initialSpawnPoints", 5.0)
            .with("spawnPoolIdAddress", item("Pool"));
        assert_eq!(rig.run(Box::new(RegisterSpawnPool), attrs), Status::Success);
        assert_eq!(rig.last(ScopeType::Item, "Pool"), Value::Int(1));
        assert_eq!(rig.world.spawn_points[&1], 5.0);
        let attrs = Attrs::new().with("spawnPoolIdAddress", item("Pool")).with("spawnPointAmount", 12.0);
        assert_eq!(rig.run(Box::new(SetSpawnPoolAmount), attrs), Status::Success);
        assert_eq!(rig.world.spawn_points[&1], 12.0);
        // a pool of no points is not registered
        let mut rig = Rig::new();
        let attrs = Attrs::new().with("maxSpawnPoints", 0i64).with("spawnPoolIdAddress", item("Pool"));
        assert_eq!(rig.run(Box::new(RegisterSpawnPool), attrs), Status::Failure);
    }

    #[test]
    fn the_spawn_table_of_the_owner_goes_to_the_blackboard_and_an_owner_without_one_raises() {
        let attrs = Attrs::new()
            .with("ownerToSpawnTableMapping", Value::Dict(vec![(Value::Int(7), Value::Int(55))]))
            .with("spawnTableIdAddress", item("Table"));
        let mut rig = spawn_rig();
        assert_eq!(rig.run(Box::new(SetSpawnTableOnBlackboardByOwner), attrs.clone()), Status::Success);
        assert_eq!(rig.last(ScopeType::Item, "Table"), Value::Int(55));
        let mut rig = rig_with_owner(8);
        rig.run(Box::new(SetSpawnTableOnBlackboardByOwner), attrs);
        assert!(
            matches!(&rig.last_error, Some(BehaviorError::Task(m)) if m.contains("Unable to find a spawn table id for owner 8"))
        );
    }

    #[test]
    fn a_spawn_table_id_of_the_task_goes_to_the_blackboard() {
        let mut rig = Rig::new();
        let attrs = Attrs::new().with("npcSpawnTableIdAddress", item("T")).with("npcSpawnTableId", 12i64);
        assert_eq!(rig.run(Box::new(SendSpawnTableIdToBlackboard), attrs), Status::Success);
        assert_eq!(rig.last(ScopeType::Item, "T"), Value::Int(12));
    }

    #[test]
    fn a_real_item_is_told_to_respawn_itself_in_its_group_and_a_group_can_stop_respawning() {
        let mut rig = spawn_rig();
        rig.world.add_ball(1, [5.0, 6.0, 7.0], 700);
        let attrs = Attrs::new()
            .with("deploymentRangeMeters", 1000.0)
            .with("minRespawnDelaySeconds", 10i64)
            .with("maxRespawnDelaySeconds", 20i64);
        assert_eq!(rig.run(Box::new(SetSelfEntityRespawn), attrs), Status::Success);
        let (group, owner, data) = &rig.world.respawns[0];
        assert_eq!((*group, *owner), (Some(10), Some(7)));
        assert_eq!(data[0].entity_type_id, 700);
        assert_eq!(data[0].spawn_position, Some([5.0, 6.0, 7.0]));
        assert_eq!(rig.run(Box::new(DisableRespawnGroup), Attrs::new()), Status::Success);
        assert!(rig.world.calls.iter().any(|c| c == "remove_respawn_group(Some(10))"));
    }

    #[test]
    fn a_reactive_spawner_is_started_with_what_the_task_says_and_its_pool_is_posted() {
        let mut rig = spawn_rig();
        rig.post(ScopeType::Item, "Table", Value::Int(5));
        let attrs = Attrs::new()
            .with("spawnTableIdAddress", item("Table"))
            .with("spawnPoolIdAddress", item("Pool"))
            .with("npcFleetCounterTableId", 3i64)
            .with("threatRange", 20000.0)
            .with("threatTimeoutSeconds", 60i64)
            .with("maxSensorDrift", 1i64)
            .with("updateTimeSeconds", 5i64)
            .with("combatTargetsSetAddress", group("Targets"))
            .with("initialPointAmount", 10i64)
            .with("minPointAmount", 1i64)
            .with("maxPointAmount", 50i64)
            .with("cappedSpawnPoints", true)
            .with("spawnWithoutThreats", false)
            .with("resetWithoutThreats", true)
            .with("spawnPointMultiplier", 1.5)
            .with("minSpawnIntervalSeconds", 30i64)
            .with("maxSpawnIntervalSeconds", 90i64)
            .with("bountyPayoutMultiplier", 0.8)
            .with("shouldMatchPlayerPoints", false)
            .with("spawnPointThreshold", 2i64);
        assert_eq!(rig.run(Box::new(ReactiveSpawning), attrs.clone()), Status::Success);
        assert_eq!(rig.last_error, None);
        let config = &rig.world.reactive_spawners[0];
        assert_eq!(
            (config.owner_id, config.spawn_table_id.clone(), config.spawner_item_id),
            (Some(7), Value::Int(5), Some(1))
        );
        assert_eq!(config.spawn_point_multiplier, Value::Float(1.5));
        assert_eq!(config.initial_spawn_npc_fleet_type_id, Value::None, "not given");
        assert_eq!(rig.last(ScopeType::Item, "Pool"), Value::Int(1));
        // a task without an attribute raises, and is a success already
        let mut rig = spawn_rig();
        rig.post(ScopeType::Item, "Table", Value::Int(5));
        assert_eq!(
            rig.run(Box::new(ReactiveSpawning), Attrs::new().with("spawnTableIdAddress", item("Table"))),
            Status::Success
        );
        assert!(rig.last_error.is_some());
        assert!(rig.world.reactive_spawners.is_empty());
    }
}
