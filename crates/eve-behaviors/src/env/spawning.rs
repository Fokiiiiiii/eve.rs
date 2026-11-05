//! What the spawning tasks ask of the server: the spawn data of NPCs, the spawn point pools and tables, the deployment of a group, and the
//! managers that respawn and react (`entityLocation.spawnPointPoolManager`, `.spawnTablePicker`, `.spawnManager`, `.reactiveSpawnManager`, ...;
//! `ballpark.entities.spawndataforentities`, `npcs.server.npcspawning`, `spawnlists`). None of it is part of the client.

use std::collections::HashMap;

use super::{ResponseThresholds, WorldResult};
use crate::utility::awareness::GroupAwarenessConfig;
use crate::value::Value;

/// `EntitySpawnData`: what one NPC needs to be deployed.
#[derive(Debug, Clone, Default, PartialEq)]
pub struct EntitySpawnData {
    pub entity_type_id: i64,
    pub spawn_position: Option<[f64; 3]>,
    pub direction: Option<[f64; 3]>,
    pub behavior_override_id: Option<Value>,
}

impl EntitySpawnData {
    /// `update_spawn_position`.
    pub fn update_spawn_position(&mut self, position: Option<[f64; 3]>) {
        self.spawn_position = position;
    }
}

/// What the spawn table picker chose: NPC types and their counts, and the points that were not spent.
#[derive(Debug, Clone, Default, PartialEq)]
pub struct SpawnPick {
    pub npcs: Vec<(i64, i64)>,
    pub remaining_points: f64,
}

/// What `DeployBehaviorEntities` answers: the group the entities are in and their item ids.
#[derive(Debug, Clone, Default, PartialEq)]
pub struct Deployed {
    pub group_id: i64,
    pub entity_item_ids: Vec<i64>,
}

/// What a deployment was asked for (`DeployBehaviorEntities`' arguments).
#[derive(Debug, Clone, Default, PartialEq)]
pub struct Deployment {
    pub owner_id: Option<i64>,
    pub spawn_data: Vec<EntitySpawnData>,
    pub group_id: Option<i64>,
    pub group_behavior_tree_name: Option<Value>,
    pub extra_context: HashMap<String, Value>,
    pub spawn_tags: Value,
    pub standing_thresholds: Option<ResponseThresholds>,
    pub objective_target_group: Option<Value>,
}

/// `ReactiveSpawnConfig`.
#[derive(Debug, Clone, Default, PartialEq)]
pub struct ReactiveSpawnConfig {
    pub owner_id: Option<i64>,
    pub spawn_table_id: Value,
    pub npc_fleet_counter_table_id: Value,
    pub awareness_config: Option<GroupAwarenessConfig>,
    pub initial_point_amount: Value,
    pub min_point_amount: Value,
    pub max_point_amount: Value,
    pub extra_context: HashMap<String, Value>,
    pub capped_spawn_points: Value,
    pub should_spawn_without_threats: Value,
    pub should_reset_without_threats: Value,
    pub spawn_point_multiplier: Value,
    pub initial_spawn_npc_fleet_type_id: Value,
    pub min_spawn_interval_seconds: Value,
    pub max_spawn_interval_seconds: Value,
    pub spawner_item_id: Option<i64>,
    pub bounty_payout_multiplier: Value,
    pub should_match_player_points: Value,
    pub spawn_point_threshold: Value,
}

/// The spawning machinery of the server.
pub trait Spawning {
    /// `spawn_const.DEFAULT_DEPLOYMENT_OFFSET_METERS`: how far from its spawn position an NPC is deployed unless the task says (a server
    /// constant that the client's files do not hold, so a world says it).
    fn default_deployment_offset_meters(&self) -> f64 {
        0.0
    }

    /// `get_spawn_data_for_entities(types_and_counts, position, deployment_range, behavior_override_id)`.
    fn spawn_data_for_entities(
        &mut self,
        _types_and_counts: &[(i64, i64)],
        _position: Option<[f64; 3]>,
        _deployment_range: f64,
        _behavior_override_id: Option<&Value>,
    ) -> Vec<EntitySpawnData> {
        Vec::new()
    }

    /// `get_spawn_data_for_entities_with_custom_behavior_overrides(types_and_counts, position, deployment_range)`.
    fn spawn_data_for_entities_with_custom_behavior_overrides(
        &mut self,
        _types_and_counts: &[(i64, i64)],
        _position: Option<[f64; 3]>,
        _deployment_range: f64,
    ) -> Vec<EntitySpawnData> {
        Vec::new()
    }

    /// `select_probability_items(spawn_list_id)`: the entity groups of a spawn list with their weights (a picked one first).
    fn select_probability_items(&mut self, _spawn_list_id: i64) -> Vec<(i64, f64)> {
        Vec::new()
    }

    /// `generate_grouping(entity_group_id)`: the NPC types and counts of a group of the spawn list.
    fn generate_grouping(&mut self, _entity_group_id: i64) -> Vec<(i64, i64)> {
        Vec::new()
    }

    /// `spawnPointPoolManager.register_new_spawn_point_pool(max, initial)`.
    fn register_new_spawn_point_pool(
        &mut self,
        _max_spawn_points: &Value,
        _initial_spawn_points: &Value,
    ) -> i64 {
        0
    }

    /// `spawnPointPoolManager.consume_spawn_points(pool, amount)`.
    fn consume_spawn_points(&mut self, _spawn_pool_id: i64, _amount: f64) {}

    /// `spawnPointPoolManager.set_spawn_points(pool, amount)`.
    fn set_spawn_points(&mut self, _spawn_pool_id: Option<i64>, _amount: &Value) {}

    /// `spawnPointPoolManager.add_spawn_points(pool, amount)`.
    fn add_spawn_points(&mut self, _spawn_pool_id: Option<i64>, _amount: f64) {}

    /// `spawnTablePicker.pick_npcs_from_table_by_points_and_tags(table, points, tags, max_npc_count=...)`: nothing when nothing fits.
    fn pick_npcs_from_table_by_points_and_tags(
        &mut self,
        _spawn_table_id: Option<i64>,
        _spawn_points: f64,
        _required_npc_tags: &Value,
        _max_npc_count: Option<i64>,
    ) -> Option<SpawnPick> {
        None
    }

    /// `GetGroupAwarenessManager().on_entity_group_spawn(spawn_pool_id, entity_group_id)`.
    fn on_entity_group_spawn(&mut self, _spawn_pool_id: Option<i64>, _entity_group_id: i64) {}

    fn spawn_table_owner(&self, _spawn_table_id: Option<i64>) -> WorldResult<i64> {
        Err(super::WorldError::Key("spawn table".to_string()))
    }

    /// `DeployBehaviorEntities(...)`.
    fn deploy_behavior_entities(&mut self, _deployment: Deployment) -> WorldResult<Deployed> {
        Err(super::WorldError::Runtime("no entities can be deployed".to_string()))
    }

    /// `RegisterBountyPayOutMultiplierForEntities(entity_item_ids, multiplier)`.
    fn register_bounty_pay_out_multiplier(&mut self, _entity_item_ids: &[i64], _multiplier: &Value) {}

    /// `GetRandomDeepSpaceLocation(solarsystem_id, minPlacementDistance=)`; what is not given is the generator's default.
    fn random_deep_space_location(
        &mut self,
        _solar_system_id: Option<i64>,
        _min_placement_distance: Option<f64>,
    ) -> [f64; 3] {
        [0.0, 0.0, 0.0]
    }

    /// `GetUndockVector(ballpark, structure_id, type_id)`: where an entity of the type comes out of the structure, and which way it looks.
    fn undock_vector(&mut self, _structure_id: i64, _type_id: i64) -> ([f64; 3], [f64; 3]) {
        ([0.0, 0.0, 0.0], [0.0, 0.0, 1.0])
    }

    /// `reinforcementManager.GetReinforcedGroup(group_id).GetFullStrengthCount()`: nothing for a group that is not reinforced.
    fn reinforced_group_full_strength_count(&self, _group_id: i64) -> Option<i64> {
        None
    }

    /// `reinforcementManager.GetReinforcedGroup(group_id).SetEntityUnitCount(count)`.
    fn set_reinforced_group_entity_unit_count(&mut self, _group_id: i64, _count: i64) {}

    fn set_real_item_respawn(
        &mut self,
        _group_id: Option<i64>,
        _owner_id: Option<i64>,
        _spawn_data: Vec<EntitySpawnData>,
        _min_delay_seconds: &Value,
        _max_delay_seconds: &Value,
    ) {
    }

    /// `behaviorRespawnManager.remove_respawn_group(group_id)`.
    fn remove_respawn_group(&mut self, _group_id: Option<i64>) {}

    /// `reactiveSpawnManager.start_spawner(config)`, and `deregister_when_spawner_is_destroyed(config, ...)`: the pool the spawner spends
    /// (`config.spawn_point_pool_id`).
    fn start_reactive_spawner(&mut self, _config: ReactiveSpawnConfig) -> i64 {
        0
    }
}
