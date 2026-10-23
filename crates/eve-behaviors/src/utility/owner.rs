use crate::tree::TaskCtx;

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

/// `idCheckers.IsEvePlayerCharacter`: the ids of the characters of the newer generations are, and for the ids in between the server knows.
pub fn is_eve_player_character(t: &mut TaskCtx<'_, '_>, owner_id: Option<i64>) -> bool {
    let Some(owner_id) = owner_id else { return false };
    if (2_100_000_000..=2_129_999_999).contains(&owner_id) || (90_000_000..=97_999_999).contains(&owner_id) {
        return true;
    }
    if (100_000_000..=2_099_999_999).contains(&owner_id) {
        return t.world().is_pre_inv64_player_character(owner_id);
    }
    false
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
