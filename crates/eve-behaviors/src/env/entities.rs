//! The server's `entityLocation` as the tasks see it: the groups of NPCs (and what they remember), their spawning and reinforcement, and the
//! dungeon keeper.

use super::{WorldError, WorldResult};

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
}
