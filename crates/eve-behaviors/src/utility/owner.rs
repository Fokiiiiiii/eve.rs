//! `behaviors/utility/owner.py`.

use crate::tree::TaskCtx;

/// `get_owner_id`: the owner of my behavior group, else of my slim item.
pub fn get_owner_id(t: &mut TaskCtx<'_, '_>) -> Option<i64> {
    let group_id = t.ctx().my_entity_group_id;
    if let Some(owner) = group_id.and_then(|id| t.world().behavior_group_owner_id(id)) {
        return Some(owner);
    }
    let slim = t.ctx().my_slim_item.clone();
    if slim.is_none() {
        t.raise(crate::env::WorldError::Reference.into());
    }
    slim.and_then(|s| s.owner_id)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::env::SlimItem;
    use crate::env::testing::TestGroup;
    use crate::tree::tests::with_task;

    #[test]
    fn the_owner_is_the_groups_and_when_there_is_no_group_the_slim_items() {
        let (_, answer) = with_task(
            |rig| {
                rig.ctx.my_entity_group_id = Some(10);
                rig.ctx.my_slim_item = Some(SlimItem { owner_id: Some(7), ..Default::default() });
                rig.world.groups.insert(10, TestGroup { owner_id: Some(1000006), ..Default::default() });
            },
            get_owner_id,
        );
        assert_eq!(answer, Some(Some(1000006)));
        let (_, answer) = with_task(
            |rig| {
                rig.ctx.my_entity_group_id = Some(11);
                rig.ctx.my_slim_item = Some(SlimItem { owner_id: Some(7), ..Default::default() });
            },
            get_owner_id,
        );
        assert_eq!(answer, Some(Some(7)));
    }
}
