//! `behaviors/utility/spawning.py`: how the tasks that spawn NPCs get the spawn data (`EntityGetter` and its kinds) and deploy them (`EntitySpawner`
//! and its kinds). A getter or a spawner is a value of its kind here; what the client's subclasses override is a `match` on it.

use crate::actions::drones;
use crate::actions::entityspawning::{
    get_behavior_override_id, get_bounty_pay_out_multiplier, get_required_npc_tags, get_spawn_pool_id,
    get_spawn_position,
};
use crate::constants::blackboardchannels::UNDOCK_DIRECTION_CHANNEL_NAME;
use crate::env::{Deployed, Deployment, EntitySpawnData};
use crate::tree::{BehaviorError, TaskCtx};
use crate::utility::ballparks::get_slim_item;
use crate::utility::blackboards::get_response_thresholds;
use crate::utility::context::get_extra_context;
use crate::utility::entity_groups::entity_group_exists;
use crate::utility::owner::get_owner_id;
use crate::utility::security::get_spawnlist_by_group_owner_and_security_level;
use crate::value::Value;

/// `get_deployment_range`: the task's `deploymentRange` if it has one (that is not 0), else the server's default.
pub fn get_deployment_range(t: &mut TaskCtx<'_, '_>) -> f64 {
    let range = t.attribute("deploymentRange");
    if range.truthy() {
        range.as_float().unwrap_or(0.0)
    } else {
        t.world().default_deployment_offset_meters()
    }
}

/// `get_spawn_table_id`: the spawn table on the channel of the task's `spawnTableIdAddress`.
pub fn get_spawn_table_id(t: &mut TaskCtx<'_, '_>) -> Option<Value> {
    t.value_at("spawnTableIdAddress")
}

/// The types and counts of an `entities` dict (`entities.iteritems()`); what is not a whole number is left out.
pub fn types_and_counts(entities: &Value) -> Vec<(i64, i64)> {
    entities
        .dict_entries()
        .iter()
        .filter_map(|(type_id, count)| Some((type_id.as_int()?, count.as_int()?)))
        .collect()
}

/// `EntityGetter` and its subclasses: where the spawn data of a spawning task come from.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum EntityGetter {
    /// `EntityGetter`: the task's `entities`.
    Plain,
    /// `EntityGetterBySpawnlistAndOwner`: a group picked from the spawn list of my group's owner.
    BySpawnlistAndOwner,
    /// `EntityGetterFromSpawnPointPool`: NPCs picked from a spawn table for the points of a pool.
    FromSpawnPointPool,
    /// `DroneGetter` (`actions/drones.py`).
    Drone,
}

impl EntityGetter {
    /// `get_spawn_data_for_entities(max_npc_count)`: nothing to spawn is an empty list (the client's `None` or `[]`).
    pub fn get_spawn_data_for_entities(
        self,
        t: &mut TaskCtx<'_, '_>,
        max_npc_count: Option<i64>,
    ) -> Vec<EntitySpawnData> {
        match self {
            Self::Plain => {
                let entities = t.attribute("entities");
                let behavior_override_id = get_behavior_override_id(t);
                Self::spawn_data(t, &types_and_counts(&entities), behavior_override_id.as_ref())
            }
            Self::BySpawnlistAndOwner => {
                let spawn_list_id = get_spawnlist_by_group_owner_and_security_level(t);
                if t.faulted() || !spawn_list_id.truthy() {
                    return Vec::new();
                }
                let Some(list_id) = spawn_list_id.as_int() else { return Vec::new() };
                let entity_group_list = t.world().select_probability_items(list_id);
                let Some((entity_group_id, _)) = entity_group_list.first().copied() else {
                    return Vec::new();
                };
                let entities = t.world().generate_grouping(entity_group_id);
                let behavior_override_id = get_behavior_override_id(t);
                Self::spawn_data(t, &entities, behavior_override_id.as_ref())
            }
            Self::FromSpawnPointPool => Self::from_spawn_point_pool(t, max_npc_count),
            Self::Drone => drones::get_spawn_data_for_drones(t),
        }
    }

    /// `_get_spawn_data_for_entities(entities, behavior_override_id)`.
    pub fn spawn_data(
        t: &mut TaskCtx<'_, '_>,
        entities: &[(i64, i64)],
        behavior_override_id: Option<&Value>,
    ) -> Vec<EntitySpawnData> {
        let range = get_deployment_range(t);
        t.world().spawn_data_for_entities(entities, None, range, behavior_override_id)
    }

    /// `EntityGetterFromSpawnPointPool.get_spawn_data_for_entities`.
    fn from_spawn_point_pool(t: &mut TaskCtx<'_, '_>, max_npc_count: Option<i64>) -> Vec<EntitySpawnData> {
        let spawn_pool_id = get_spawn_pool_id(t);
        let spawn_point_amount = Self::spawn_point_amount(t, spawn_pool_id.as_ref());
        if t.faulted() {
            return Vec::new();
        }
        if spawn_point_amount <= 0.0 {
            let type_id = t.ctx().my_slim_item.as_ref().map(|slim| slim.type_id);
            t.world()
                .log_error(&format!("Bad authoring for behavior type {type_id:?} - missing spawn points"));
            return Vec::new();
        }
        let picked = if spawn_pool_id.as_ref().is_some_and(|pool| !pool.is_none()) {
            Self::entities_using_spawn_point_pool(
                t,
                spawn_point_amount,
                spawn_pool_id.as_ref(),
                max_npc_count,
            )
        } else {
            Self::pick_entities_from_spawn_table(t, spawn_point_amount, max_npc_count)
        };
        let Some(picked) = picked else { return Vec::new() };
        let range = get_deployment_range(t);
        t.world().spawn_data_for_entities_with_custom_behavior_overrides(&picked.npcs, None, range)
    }

    /// `_get_spawn_point_amount`: the task's `spawnPoints`, else what the pool has.
    fn spawn_point_amount(t: &mut TaskCtx<'_, '_>, spawn_pool_id: Option<&Value>) -> f64 {
        if t.has_attribute("spawnPoints") {
            return t.attribute("spawnPoints").as_float().unwrap_or(0.0);
        }
        match spawn_pool_id {
            Some(pool) if pool.truthy() => t.world().current_spawn_points(pool.as_int()),
            _ => 0.0,
        }
    }

    /// `_get_entities_using_spawn_point_pool`: what is picked is paid for from the pool, which must have enough.
    fn entities_using_spawn_point_pool(
        t: &mut TaskCtx<'_, '_>,
        spawn_point_amount: f64,
        spawn_pool_id: Option<&Value>,
        max_npc_count: Option<i64>,
    ) -> Option<crate::env::SpawnPick> {
        let pool = spawn_pool_id.filter(|pool| pool.truthy()).and_then(Value::as_int);
        if let Some(pool) = pool {
            let available = t.world().current_spawn_points(Some(pool));
            if spawn_point_amount > available {
                return None;
            }
        }
        let picked = Self::pick_entities_from_spawn_table(t, spawn_point_amount, max_npc_count)?;
        if let Some(pool) = pool {
            t.world().consume_spawn_points(pool, spawn_point_amount - picked.remaining_points);
        }
        Some(picked)
    }

    /// `_pick_entities_from_spawn_table`.
    fn pick_entities_from_spawn_table(
        t: &mut TaskCtx<'_, '_>,
        spawn_point_amount: f64,
        max_npc_count: Option<i64>,
    ) -> Option<crate::env::SpawnPick> {
        let tags = get_required_npc_tags(t);
        let spawn_table_id = get_spawn_table_id(t)?;
        t.world().pick_npcs_from_table_by_points_and_tags(
            spawn_table_id.as_int(),
            spawn_point_amount,
            &tags,
            max_npc_count,
        )
    }

    /// `on_spawn_entities(entity_group_id)`.
    pub fn on_spawn_entities(self, t: &mut TaskCtx<'_, '_>, entity_group_id: i64) {
        if self == Self::FromSpawnPointPool {
            let pool = get_spawn_pool_id(t).and_then(|pool| pool.as_int());
            t.world().on_entity_group_spawn(pool, entity_group_id);
        }
    }
}

/// `EntitySpawner` and its subclasses: how a spawning task deploys what it got.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum EntitySpawner {
    /// `EntitySpawner`.
    Plain,
    /// `EntitySpawnTableSpawner`: the owner is the spawn table's.
    SpawnTable,
    /// `EntitySpawnerWithUndock`: the entities come out of a structure.
    WithUndock,
    /// `DroneSpawner` (`actions/drones.py`).
    Drone,
}

impl EntitySpawner {
    /// `get_owner_id`: nothing when the spawn table has no owner (the client's `KeyError` is raised).
    pub fn get_owner_id(self, t: &mut TaskCtx<'_, '_>) -> Option<i64> {
        if self == Self::SpawnTable {
            let table = get_spawn_table_id(t)?.as_int();
            let owner = t.world().spawn_table_owner(table);
            return t.ok(owner);
        }
        get_owner_id(t)
    }

    /// `get_group_id`: the group the new entities join, nothing for a new group.
    pub fn get_group_id(self, t: &mut TaskCtx<'_, '_>) -> Option<i64> {
        if self == Self::Drone {
            return drones::get_drone_spawn_group_id(t);
        }
        let spawn_new_group = t.required("spawnNewGroup")?;
        if spawn_new_group.truthy() {
            return None;
        }
        if t.has_attribute("entityGroupIdAddress") {
            let group = t.value_at("entityGroupIdAddress")?;
            let group_id = group.as_int()?;
            return if entity_group_exists(t, group_id) { Some(group_id) } else { None };
        }
        t.ctx().my_entity_group_id
    }

    /// `_update_spawn_position(spawn_data_for_entities)`.
    fn update_spawn_position(self, t: &mut TaskCtx<'_, '_>, spawn_data: &mut [EntitySpawnData]) {
        let position = get_spawn_position(t, self == Self::Drone);
        for data in spawn_data {
            data.update_spawn_position(position);
        }
    }

    fn behavior_group_tree_name(t: &mut TaskCtx<'_, '_>) -> Option<Value> {
        let name = t.attribute("behaviorTreeGroupName");
        if name.is_none() { None } else { Some(name) }
    }

    fn objective_target_group(t: &mut TaskCtx<'_, '_>) -> Option<Value> {
        if t.attribute("objectiveTargetGroupAddress").truthy() {
            return t.value_at("objectiveTargetGroupAddress");
        }
        None
    }

    /// `spawn_new_entities(spawn_data_for_entities)`: the group and the item ids of what was deployed.
    pub fn spawn_new_entities(
        self,
        t: &mut TaskCtx<'_, '_>,
        mut spawn_data: Vec<EntitySpawnData>,
    ) -> Option<Deployed> {
        if self == Self::WithUndock {
            return self.spawn_new_entities_with_undock(t, spawn_data);
        }
        let owner_id = self.get_owner_id(t);
        let group_id = self.get_group_id(t);
        if t.faulted() {
            return None;
        }
        self.update_spawn_position(t, &mut spawn_data);
        let deployed = self.deploy(t, owner_id, spawn_data, group_id)?;
        self.register_bounty_multiplier(t, &deployed.entity_item_ids);
        self.update_entity_group_id(t, deployed.group_id);
        Some(deployed)
    }

    /// `DeployBehaviorEntities` with everything the task says of the deployment.
    fn deploy(
        self,
        t: &mut TaskCtx<'_, '_>,
        owner_id: Option<i64>,
        spawn_data: Vec<EntitySpawnData>,
        group_id: Option<i64>,
    ) -> Option<Deployed> {
        let spool = get_spawn_pool_id(t).and_then(|v| v.as_int());
        let stable = crate::actions::entityspawning::get_spawn_table_id_of_task(t).and_then(|v| v.as_int());
        let extra_context = get_extra_context(t.ctx(), spool, stable);
        let deployment = Deployment {
            owner_id,
            spawn_data,
            group_id,
            group_behavior_tree_name: Self::behavior_group_tree_name(t),
            extra_context,
            spawn_tags: get_required_npc_tags(t),
            standing_thresholds: get_response_thresholds(t),
            objective_target_group: Self::objective_target_group(t),
        };
        let deployed = t.world().deploy_behavior_entities(deployment);
        t.ok(deployed)
    }

    /// `_register_entity_bounty_multiplier(entity_item_ids)`.
    fn register_bounty_multiplier(self, t: &mut TaskCtx<'_, '_>, entity_item_ids: &[i64]) {
        let Some(multiplier) = get_bounty_pay_out_multiplier(t) else { return };
        t.world().register_bounty_pay_out_multiplier(entity_item_ids, &multiplier);
    }

    /// `update_entity_group_id(entity_group_id)`.
    fn update_entity_group_id(self, t: &mut TaskCtx<'_, '_>, entity_group_id: i64) {
        if t.has_attribute("entityGroupIdAddress")
            && let Some(address) = t.required_address("entityGroupIdAddress")
        {
            t.send_blackboard_value(&address, Value::Int(entity_group_id));
        }
    }

    /// `EntitySpawnerWithUndock.spawn_new_entities`.
    fn spawn_new_entities_with_undock(
        self,
        t: &mut TaskCtx<'_, '_>,
        mut spawn_data: Vec<EntitySpawnData>,
    ) -> Option<Deployed> {
        let owner_id = get_owner_id(t);
        let group_id = self.get_group_id(t);
        if t.faulted() {
            return None;
        }
        let direction_by_entity_type = Self::update_spawn_parameters_and_get_direction(t, &mut spawn_data)?;
        let deployed = self.deploy(t, owner_id, spawn_data, group_id)?;
        self.register_bounty_multiplier(t, &deployed.entity_item_ids);
        self.update_entity_group_id(t, deployed.group_id);
        Self::set_undock_direction(t, &deployed.entity_item_ids, &direction_by_entity_type);
        Some(deployed)
    }

    /// `_update_spawn_parameters_and_get_direction`: where each comes out of the structure, and which way it looks.
    fn update_spawn_parameters_and_get_direction(
        t: &mut TaskCtx<'_, '_>,
        spawn_data: &mut [EntitySpawnData],
    ) -> Option<Vec<(i64, [f64; 3])>> {
        let structure_id = t.value_at("structureIdAddress")?;
        let structure_id = t.expect_id(&structure_id)?;
        let mut direction_by_entity_type: Vec<(i64, [f64; 3])> = Vec::new();
        for data in spawn_data {
            let (position, direction) = t.world().undock_vector(structure_id, data.entity_type_id);
            data.spawn_position = Some(position);
            data.direction = Some(direction);
            match direction_by_entity_type.iter_mut().find(|(type_id, _)| *type_id == data.entity_type_id) {
                Some(entry) => entry.1 = direction,
                None => direction_by_entity_type.push((data.entity_type_id, direction)),
            }
        }
        Some(direction_by_entity_type)
    }

    /// `_set_undock_direction`: each entity is told the direction it looks.
    fn set_undock_direction(
        t: &mut TaskCtx<'_, '_>,
        entity_ids: &[i64],
        direction_by_entity_type: &[(i64, [f64; 3])],
    ) {
        for entity_id in entity_ids {
            let Some(slim) = get_slim_item(t, *entity_id) else {
                t.raise(BehaviorError::Task(
                    "AttributeError: 'NoneType' object has no attribute 'typeID'".to_string(),
                ));
                return;
            };
            let Some((_, direction)) =
                direction_by_entity_type.iter().find(|(type_id, _)| *type_id == slim.type_id)
            else {
                t.raise(crate::env::WorldError::Key(slim.type_id.to_string()).into());
                return;
            };
            let channel = t.channel_for_item(*entity_id, UNDOCK_DIRECTION_CHANNEL_NAME);
            t.channel_send(&channel, Value::from_vec3(*direction));
        }
    }
}

/// `get_spawn_point_pool_manager(task).get_current_spawn_points(pool)`: raises (after a log) what the manager raises.
pub fn get_current_spawn_points(t: &mut TaskCtx<'_, '_>, spawn_pool_id: Option<i64>) -> f64 {
    t.world().current_spawn_points(spawn_pool_id)
}

/// `add_spawn_points(task, spawn_pool_id, amount)`.
pub fn add_spawn_points(t: &mut TaskCtx<'_, '_>, spawn_pool_id: Option<i64>, amount: f64) {
    t.world().add_spawn_points(spawn_pool_id, amount);
}
