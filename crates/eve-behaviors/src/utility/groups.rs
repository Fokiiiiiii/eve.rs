//! `behaviors/utility/groups.py`.

use crate::tree::{BehaviorError, TaskCtx};

fn missing(t: &mut TaskCtx<'_, '_>, what: &str) {
    t.raise(BehaviorError::Task(format!("AttributeError: 'NoneType' object has no attribute '{what}'")));
}

/// `get_my_entity_group`: whether it is there.
pub fn my_entity_group_exists(t: &mut TaskCtx<'_, '_>) -> bool {
    match t.ctx().my_entity_group_id {
        Some(id) => t.world().group_exists(id),
        None => false,
    }
}

/// `get_entity_group_member_id_list`: raises for a group that is not there.
pub fn get_entity_group_member_id_list(t: &mut TaskCtx<'_, '_>, group_id: i64) -> Vec<i64> {
    match t.world().group_members(group_id) {
        Some(members) => members,
        None => {
            missing(t, "GetGroupMembers");
            Vec::new()
        }
    }
}

/// `get_entity_group_member_count`: raises for a group that is not there.
pub fn get_entity_group_member_count(t: &mut TaskCtx<'_, '_>, group_id: i64) -> usize {
    match t.world().group_count(group_id) {
        Some(count) => count,
        None => {
            missing(t, "Count");
            0
        }
    }
}

pub fn get_my_entity_group_owner_id(t: &mut TaskCtx<'_, '_>) -> Option<i64> {
    if !my_entity_group_exists(t) {
        missing(t, "GetGroupOwnerId");
        return None;
    }
    let id = t.ctx().my_entity_group_id?;
    t.world().group_owner_id(id)
}
