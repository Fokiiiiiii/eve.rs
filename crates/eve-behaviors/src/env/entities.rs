//! The server's `entityLocation` as the tasks see it: the groups of NPCs (and what they remember), their spawning and reinforcement, and the
//! dungeon keeper.

use super::{WorldError, WorldResult};
use crate::value::Value;

/// The id `FindGroupIDForItemID` answers for an item that is in no group (`ballpark.entities.entitygroup.INVALID_ID`).
pub const INVALID_GROUP_ID: i64 = -1;

/// The standing thresholds an NPC answers to (`EntityResponseThresholds`): at or below the hostile one it is hostile, at or above the friendly one it
/// is friendly.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct ResponseThresholds {
    pub hostile_response_threshold: f64,
    pub friendly_response_threshold: f64,
}

/// A wave of reinforcements (`ReinforcementWave`).
#[derive(Debug, Clone, PartialEq)]
pub struct ReinforcementWave {
    pub wave_id: crate::value::Value,
    pub cooldown_seconds: crate::value::Value,
    pub threshold: crate::value::Value,
    pub behavior_override_id: Option<crate::value::Value>,
}

/// An object of a dungeon's room (`dungeon_data.roomObjects`).
#[derive(Debug, Clone, Default, PartialEq)]
pub struct DungeonObject {
    pub type_id: i64,
    pub x: f64,
    pub y: f64,
    pub z: f64,
    pub object_name_id: Option<i64>,
    pub object_id: Option<i64>,
    pub yaw: Option<f64>,
    pub pitch: Option<f64>,
    pub roll: Option<f64>,
}

/// What the player fleet reporter says of one type of fleet (`fleet_by_type[fleet_type]`).
#[derive(Debug, Clone, Default, PartialEq)]
pub struct FleetReport {
    pub fleet_cost: f64,
    pub item_ids: Vec<i64>,
}

/// What `GetPlayerFleetReporter().generate(spawnPoolId)` gives: the players' fleets by type, what they cost, and the report as a dictionary
/// (`convert_report_to_dict`).
#[derive(Debug, Clone, Default, PartialEq)]
pub struct PlayerFleetReport {
    pub fleet_by_type: Vec<(Value, FleetReport)>,
    pub total_fleet_cost: f64,
    pub as_dict: Value,
}

/// What a structure ball says of its health.
#[derive(Debug, Clone, Copy, Default, PartialEq)]
pub struct StructureState {
    pub is_damaged: bool,
    pub is_shield_vulnerable: bool,
    pub is_armor_vulnerable: bool,
    pub is_hull_vulnerable: bool,
    pub shield: f64,
    pub armor: f64,
    pub hull: f64,
}

/// The groups of entities and their managers.
pub trait Entities {
    /// `EntityResponseThresholds(None, None, task)`: the server works them out for the entity from its group's and its type's blackboards
    /// (`npcs.server.components.responsethresholdoverrides`); `None` when it has none.
    fn response_thresholds(
        &self,
        _item_id: i64,
        _entity_group_id: Option<i64>,
    ) -> Option<ResponseThresholds> {
        None
    }

    /// `groupManager.HasGroup(groupId)`.
    fn group_exists(&self, _group_id: i64) -> bool {
        false
    }

    /// `groupManager.GetGroup(groupId).GetGroupMembers()`: `None` when there is no such group (`GetGroup` answers `None`).
    fn group_members(&self, _group_id: i64) -> Option<Vec<i64>> {
        None
    }

    /// `groupManager.GetGroup(groupId).Count()`.
    fn group_count(&self, _group_id: i64) -> Option<usize> {
        None
    }

    /// `groupManager.GetGroup(groupId).GetGroupOwnerId()`.
    fn group_owner_id(&self, _group_id: i64) -> Option<i64> {
        None
    }

    /// `group.HasEntity(itemId)`.
    fn group_has_entity(&self, _group_id: i64, _item_id: i64) -> Option<bool> {
        None
    }

    /// `group.IsInitialSpawningComplete()`.
    fn group_initial_spawning_complete(&self, _group_id: i64) -> Option<bool> {
        None
    }

    /// `context.groupManager.FindGroupIDForItemID(itemId)`.
    fn find_group_id_for_item(&self, _item_id: i64) -> i64 {
        INVALID_GROUP_ID
    }

    /// `entityLocation.GetBehaviorEntityGroup(groupId)`: the owner of the group when it is a behavior group.
    fn behavior_group_owner_id(&self, _group_id: i64) -> Option<i64> {
        None
    }

    /// `spawnPoolManager.get_current_spawn_points(spawnPoolId)`.
    fn current_spawn_points(&self, _spawn_pool_id: Option<i64>) -> f64 {
        0.0
    }

    /// `reinforcementManager.GetActiveReinforcedEntityIds(groupId)`.
    fn active_reinforced_entity_ids(&self, _group_id: i64) -> Vec<i64> {
        Vec::new()
    }

    /// `keeper.DoesSpawnExist(dungeonSpawnId)`.
    fn does_spawn_exist(&self, _dungeon_spawn_id: i64) -> bool {
        false
    }

    /// `awarenessManager.get_spawn_pool_fleet_awareness(spawnPoolId).get_member_ids()`.
    fn fleet_awareness_member_ids(&self, _spawn_pool_id: Option<i64>) -> Vec<i64> {
        Vec::new()
    }

    /// `npcFleetReporter.get_fleet_cost_for_item_ids(spawnTableId, itemIds)`.
    fn npc_fleet_cost_for_item_ids(&self, _spawn_table_id: Option<i64>, _item_ids: &[i64]) -> f64 {
        0.0
    }

    /// `sum(playerFleetReporter.get_fleet_cost_by_item_ids(itemIds).values())`.
    fn player_fleet_cost_for_item_ids(&self, _item_ids: &[i64]) -> f64 {
        0.0
    }

    // ---- what an NPC entity carries that is not in its cargo bay (`BehaviorEntity.additionalLoot`) ----

    /// `ball.HasTypeInAdditionalLoot(typeId)` of an entity.
    fn has_type_in_additional_loot(&self, _item_id: i64, _type_id: i64) -> bool {
        false
    }

    /// `ball.HasEnoughAdditionalCargoCapacityForType(typeId, quantity)`.
    fn has_enough_additional_cargo_capacity_for_type(
        &self,
        _item_id: i64,
        _type_id: i64,
        _quantity: i64,
    ) -> bool {
        false
    }

    /// `ball.GetAvailableAdditionalCargoCapacity()`.
    fn available_additional_cargo_capacity(&self, _item_id: i64) -> f64 {
        0.0
    }

    /// `reinforcementManager.IsReinforcementAuthorized(groupId)`: raises for a group that has no reinforcements
    /// (`ReinforcedEntityGroupNotFoundError`).
    fn is_reinforcement_authorized(&self, _group_id: i64) -> WorldResult<bool> {
        Err(WorldError::ReinforcedGroupNotFound)
    }

    /// `reinforcementManager.AreReinforcementsAvailable(groupId)`.
    fn are_reinforcements_available(&self, _group_id: i64) -> bool {
        false
    }

    /// `get_behavior_sensor_tags(context, tags)` (`npcs.server.tags`).
    fn behavior_sensor_tags(&self, tags: &[String]) -> Vec<String> {
        tags.to_vec()
    }

    /// `entityLocation.GetBehaviorEntityGroup(groupId).GetBehaviorId()`.
    fn behavior_group_behavior_id(&self, _group_id: i64) -> Option<crate::value::Value> {
        None
    }

    /// `keeper.GetOperationSpawnpointPosition(characterId, siteId)`: `(solar system id, spawn id, x, y, z)`.
    fn operation_spawnpoint_position(
        &self,
        _character_id: Option<i64>,
        _site_id: Option<i64>,
    ) -> WorldResult<(i64, i64, f64, f64, f64)> {
        Err(WorldError::Runtime("no such operation site".into()))
    }

    /// `RewardsPayment(...).register_payment_on_explosion()` and `.register_wreck_loot_rights_on_explosion()` for an item in a bubble.
    fn register_payment_on_explosion(
        &mut self,
        _item_id: i64,
        _bubble_id: i64,
        _reward_id: Option<i64>,
        _tale_id: i64,
        _dungeon_id: i64,
    ) {
    }

    /// `taleMgr.GetTaleForPersistedObject(itemId)[0].taleID`.
    fn tale_id_for_persisted_object(&self, _item_id: i64) -> Option<i64> {
        None
    }

    /// `keeper.GetSpawn(spawnId).entryPos`.
    fn dungeon_spawn_entry_pos(&self, _spawn_id: i64) -> Option<[f64; 3]> {
        None
    }

    /// `keeper.GetItemIdForObject(keeper.GetSpawn(spawnId), objectId)`.
    fn dungeon_item_id_for_object(&self, _spawn_id: i64, _object_id: i64) -> Option<i64> {
        None
    }

    /// `keeper.GetDungeonData(dungeonId).roomObjects[first room]`.
    fn dungeon_room_objects(&self, _dungeon_id: i64) -> Vec<DungeonObject> {
        Vec::new()
    }

    /// `reinforcementManager.CallInReinforcements(groupId, thresholds)`.
    fn call_in_reinforcements(&mut self, _group_id: i64, _thresholds: Option<ResponseThresholds>) {}

    /// `reinforcementManager.GetReinforcedGroup(groupId) is not None`.
    fn reinforced_group_exists(&self, _group_id: i64) -> bool {
        false
    }

    /// `reinforcementManager.AddEntityGroupReinforcement(CreateReinforcedGroup(entityCount, ownerId, wave, None).SetEntityGroupID(groupId))`.
    fn add_entity_group_reinforcement(
        &mut self,
        _group_id: i64,
        _entity_count: usize,
        _owner_id: Option<i64>,
        _wave: ReinforcementWave,
    ) {
    }

    /// `reinforcementManager.RemoveActiveReinforcedEntityIds(groupId)`.
    fn remove_active_reinforced_entity_ids(&mut self, _group_id: i64) {}
    /// `groupManager.AddGroupRemovalHandler(groupId, channel.SendMessage, (value,))`: when the group is removed the channel is told `value`.
    fn add_group_removal_handler(
        &mut self,
        _group_id: Option<i64>,
        _channel: crate::tree::ChannelRef,
        _value: crate::value::Value,
    ) {
    }
    /// `AssignAdditionalLootToGroupEntities(entityLocation, groupId)`.
    fn assign_additional_loot_to_group_entities(&mut self, _group_id: Option<i64>) {}

    /// `entityLocation.UnspawnBehaviorEntityGroupWithRespawnEnabled(groupId)`.
    fn unspawn_behavior_entity_group_with_respawn_enabled(&mut self, _group_id: Option<i64>) {}

    /// `entityLocation.GetBehaviorTreeManager().GetEntryByKey(itemId).ResetTree()`: false for an item that has no tree.
    fn reset_behavior_tree(&mut self, _item_id: i64) -> bool {
        false
    }
    /// `myBall.AddTypeAndAmountToAdditionalLoot(typeId, amount, allowOverload)`.
    fn add_type_and_amount_to_additional_loot(
        &mut self,
        _ball_id: i64,
        _type_id: i64,
        _amount: &Value,
        _allow_overload: Option<&Value>,
    ) {
    }

    /// `myBall.RemoveTypeFromAdditionalLoot(typeId)`.
    fn remove_type_from_additional_loot(&mut self, _ball_id: i64, _type_id: i64) {}

    /// `ball.RemoveAdditionalLoot()`: the types and amounts that were taken off it.
    fn remove_additional_loot(&mut self, _ball_id: i64) -> Vec<(i64, Value)> {
        Vec::new()
    }

    /// `behaviorTreeManager.GetAllBehaviorEntriesByKey()`: the item ids of the trees that run in the system.
    fn behavior_entry_keys(&self) -> Vec<i64> {
        Vec::new()
    }

    /// `ballpark.UnspawnEntityBall(itemId)`.
    fn unspawn_entity_ball(&mut self, _item_id: i64) {}

    /// `GetGroupAwarenessManager().is_registered(memberCollectionId)`.
    fn is_awareness_registered(&self, _member_collection_id: &Value) -> bool {
        false
    }

    /// `GetGroupAwarenessManager().register_group_awareness(memberCollectionId, config)`.
    fn register_group_awareness(
        &mut self,
        _member_collection_id: &Value,
        _config: crate::utility::awareness::GroupAwarenessConfig,
    ) {
    }

    /// `GetGroupAwarenessManager().register_spawn_pool_fleet_awareness(spawnPoolId, ownerId, blackboardId, behaviorId, config)`.
    fn register_spawn_pool_fleet_awareness(
        &mut self,
        _spawn_pool_id: &Value,
        _owner_id: Option<i64>,
        _blackboard_id: Option<i64>,
        _behavior_id: &Value,
        _config: crate::utility::awareness::GroupAwarenessConfig,
    ) {
    }

    /// `get_spawn_pool_fleet_awareness(spawnPoolId).get_member_collection().add_item_id(itemId)`: false when the pool has no fleet awareness.
    fn add_item_to_spawn_pool_fleet_awareness(&mut self, _spawn_pool_id: &Value, _item_id: i64) -> bool {
        false
    }

    /// `get_fleet_awareness(spawnPoolId).get_enemy_cluster_generator() is None`: nothing when the pool has no fleet awareness.
    fn fleet_awareness_lacks_enemy_cluster_generator(&self, _spawn_pool_id: &Value) -> Option<bool> {
        None
    }

    /// `get_fleet_awareness(spawnPoolId).enable_enemy_cluster_generator(dogmaLM, damageTracker, updateIntervalSeconds, channel)`.
    fn enable_enemy_cluster_generator(
        &mut self,
        _spawn_pool_id: &Value,
        _update_interval_seconds: &Value,
        _channel: crate::tree::ChannelRef,
    ) {
    }

    /// `THREAT_TIME_WINDOW_SECONDS` of the server's enemy cluster generator (not in the client's files).
    fn threat_time_window_seconds(&self) -> f64 {
        0.0
    }
    /// `GetPlayerFleetReporter().generate(spawnPoolId)`.
    fn generate_player_fleet_report(&mut self, _spawn_pool_id: &Value) -> WorldResult<PlayerFleetReport> {
        Err(WorldError::Runtime("no player fleet report".to_string()))
    }

    /// `GetNpcFleetReporter().get_total_fleet_points(awareness, spawnTableId, spawnPoolId)`.
    fn total_npc_fleet_points(
        &mut self,
        _spawn_pool_id: &Value,
        _spawn_table_id: &Value,
    ) -> WorldResult<f64> {
        Ok(0.0)
    }

    /// `ballpark.fleets.fleet_types.FLEET_TYPE_SUPPORT` (not in the client's files).
    fn fleet_type_support(&self) -> Value {
        Value::None
    }

    /// `SUPPORTED_FLEET_TYPES`.
    fn supported_fleet_types(&self) -> Vec<Value> {
        Vec::new()
    }

    /// `FLEET_TYPES_TO_COUNTER`.
    fn fleet_types_to_counter(&self) -> Vec<Value> {
        Vec::new()
    }

    /// `myBall`, a structure: what `IsDamaged()`, the `IsState...Vulnerable()` and `GetShield()`, `GetArmor()` and `GetHull()` answer.
    fn structure_state(&self, _ball_id: i64) -> Option<StructureState> {
        None
    }
    /// `GetAbilityRegistryForSolarsystem(system).OnAbilityActivationSucceeded(itemId, slot, startTime, durationMs)`.
    fn ability_activation_succeeded(
        &mut self,
        _item_id: i64,
        _slot: &Value,
        _start_time: i64,
        _duration_ms: f64,
    ) {
    }

    /// `.GetActivationContext(itemId, slot)`.
    fn ability_activation_context(&self, _item_id: i64, _slot: &Value) -> Option<Value> {
        None
    }

    /// `StartFighterAbilityDogmaEffect(effectId, activationContext, targetId)`.
    fn start_fighter_ability_dogma_effect(
        &mut self,
        _effect_id: i64,
        _context: &Value,
        _target_id: Option<i64>,
    ) -> WorldResult<()> {
        Ok(())
    }

    /// `.OnAbilityDeactivated(itemId, slot, failureReason)`.
    fn ability_deactivated(&mut self, _item_id: i64, _slot: &Value, _failure_reason: &Value) {}

    /// `.OnAbilityActivationFailed(itemId, slot)`.
    fn ability_activation_failed(&mut self, _item_id: i64, _slot: &Value) {}
}
