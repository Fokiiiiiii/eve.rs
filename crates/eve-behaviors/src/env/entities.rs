//! The server's `entityLocation` as the tasks see it: the groups of NPCs (and what they remember), their spawning and reinforcement, and the
//! dungeon keeper.

/// The id `FindGroupIDForItemID` answers for an item that is in no group (`ballpark.entities.entitygroup.INVALID_ID`).
pub const INVALID_GROUP_ID: i64 = -1;

/// The standing thresholds an NPC answers to (`EntityResponseThresholds`): at or below the hostile one it is hostile, at or above the friendly one it
/// is friendly.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct ResponseThresholds {
    pub hostile_response_threshold: f64,
    pub friendly_response_threshold: f64,
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
}
