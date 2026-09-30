//! `behaviors/groups/mixin.py`: `GroupTaskMixin`, what a task that works on its entity group asks of it. The methods are functions of the task's
//! view of everything.

use std::collections::BTreeSet;

use crate::env::WorldError;
use crate::tree::{BehaviorError, TaskCtx};

/// What `GetGroupMessenger` raises for a group that is not there (`EntityGroupNotFound`).
pub fn entity_group_not_found(t: &mut TaskCtx<'_, '_>) {
    let id = t.ctx().my_entity_group_id;
    t.raise(BehaviorError::Task(format!(
        "Entity group {} was not found",
        id.map_or("None".to_string(), |i| i.to_string())
    )));
}

fn attribute_error(t: &mut TaskCtx<'_, '_>, what: &str) {
    t.raise(BehaviorError::Task(format!("AttributeError: 'NoneType' object has no attribute '{what}'")));
}

/// `GetEntityGroup`: whether the group of my entity is there.
pub fn has_entity_group(t: &mut TaskCtx<'_, '_>) -> bool {
    match t.ctx().my_entity_group_id {
        Some(id) => t.world().group_exists(id),
        None => false,
    }
}

/// `GetMemberIdList`: raises when there is no group.
pub fn get_member_id_list(t: &mut TaskCtx<'_, '_>) -> Vec<i64> {
    let members = t.ctx().my_entity_group_id.and_then(|id| t.world().group_members(id));
    match members {
        Some(members) => members,
        None => {
            attribute_error(t, "GetGroupMembers");
            Vec::new()
        }
    }
}

/// `GetMemberBubbleSet`: the bubbles the members' balls are in (a member without a ball is left out).
pub fn get_member_bubble_set(t: &mut TaskCtx<'_, '_>) -> BTreeSet<i64> {
    let mut bubbles = BTreeSet::new();
    for member_id in get_member_id_list(t) {
        if let Some(ball) = t.world().ball(member_id) {
            bubbles.insert(ball.new_bubble_id);
        }
    }
    bubbles
}

/// `IsMember`: raises when there is no group.
pub fn is_member(t: &mut TaskCtx<'_, '_>, item_id: i64) -> bool {
    let answer = t.ctx().my_entity_group_id.and_then(|id| t.world().group_has_entity(id, item_id));
    match answer {
        Some(is_member) => is_member,
        None => {
            attribute_error(t, "HasEntity");
            false
        }
    }
}

/// `GetGroupOwnerId`: raises when there is no group.
pub fn get_group_owner_id(t: &mut TaskCtx<'_, '_>) -> Option<i64> {
    let has_group = has_entity_group(t);
    if !has_group {
        attribute_error(t, "GetGroupOwnerId");
        return None;
    }
    let id = t.ctx().my_entity_group_id?;
    t.world().group_owner_id(id)
}

/// `GetGroupFactionId`.
pub fn get_group_faction_id(t: &mut TaskCtx<'_, '_>) -> Option<i64> {
    let owner = get_group_owner_id(t)?;
    t.world().corporation_faction_id(owner)
}

/// What a lookup that is not found raises where the client's does (`KeyError`).
pub fn key_error(t: &mut TaskCtx<'_, '_>, what: impl ToString) {
    t.raise(WorldError::Key(what.to_string()).into());
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::env::testing::TestGroup;
    use crate::tree::tests::with_task;

    fn setup(rig: &mut crate::tree::tests::Rig) {
        rig.world.groups.insert(
            10,
            TestGroup { members: vec![1, 2], owner_id: Some(1000006), initial_spawning_complete: false },
        );
        rig.world.factions.insert(1000006, 500001);
        rig.world.add_ball(1, [0.0; 3], 1).new_bubble_id = 4;
        rig.world.add_ball(2, [0.0; 3], 1).new_bubble_id = 5;
    }

    #[test]
    fn a_group_task_reads_its_members_owner_and_faction() {
        let (rig, answer) = with_task(setup, |t| {
            (
                get_member_id_list(t),
                is_member(t, 2),
                is_member(t, 3),
                get_group_owner_id(t),
                get_group_faction_id(t),
                get_member_bubble_set(t),
            )
        });
        assert_eq!(rig.last_error, None);
        assert_eq!(
            answer,
            Some((vec![1, 2], true, false, Some(1000006), Some(500001), BTreeSet::from([4, 5])))
        );
    }

    #[test]
    fn a_group_task_without_a_group_raises_where_the_client_does() {
        let (rig, _) = with_task(|rig| rig.ctx.my_entity_group_id = Some(99), get_member_id_list);
        assert!(matches!(rig.last_error, Some(BehaviorError::Task(ref m)) if m.contains("GetGroupMembers")));
    }
}
