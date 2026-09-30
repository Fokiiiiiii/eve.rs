//! `behaviors/utility/entity_groups.py`.

use crate::tree::{BehaviorError, TaskCtx};

/// `get_entity_group_member_ids`: the members, none for a group that is not there.
pub fn get_entity_group_member_ids(t: &mut TaskCtx<'_, '_>, entity_group_id: i64) -> Vec<i64> {
    t.world().group_members(entity_group_id).unwrap_or_default()
}

/// `entity_group_exists`.
pub fn entity_group_exists(t: &mut TaskCtx<'_, '_>, entity_group_id: i64) -> bool {
    t.world().group_exists(entity_group_id)
}

/// `is_entity_group_initial_spawning_complete`: raises for a group that is not there.
pub fn is_entity_group_initial_spawning_complete(t: &mut TaskCtx<'_, '_>, entity_group_id: i64) -> bool {
    match t.world().group_initial_spawning_complete(entity_group_id) {
        Some(complete) => complete,
        None => {
            t.raise(BehaviorError::Task(
                "AttributeError: 'NoneType' object has no attribute 'IsInitialSpawningComplete'".to_string(),
            ));
            false
        }
    }
}
