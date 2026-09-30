//! `behaviors/conditions/group.py`.

use crate::env::INVALID_GROUP_ID;
use crate::groups::mixin;
use crate::utility::entity_groups::is_entity_group_initial_spawning_complete;
use crate::value::Value;

enter_task!(IsMemberInEntityGroup, |t| {
    let Some(member) = t.value_at("memberIdAddress") else { return };
    let is_member = if member.is_none() {
        false
    } else {
        let Some(member_id) = t.expect_id(&member) else { return };
        mixin::is_member(t, member_id)
    };
    if t.faulted() {
        return;
    }
    t.set_status_to_success_if_true_else_to_failed(is_member);
});

enter_task!(IsMemberTypeIdPartOfGroup, |t| {
    t.set_status_to_failed();
    let type_id = t.attribute("typeId");
    if type_id.is_none() {
        return;
    }
    let members = mixin::get_member_id_list(t);
    if t.faulted() {
        return;
    }
    for item_id in members {
        let Some(item) = t.world().inventory_item(item_id) else {
            t.raise(crate::env::WorldError::Runtime(format!("no item {item_id}")).into());
            return;
        };
        if Value::Int(item.type_id).py_eq(&type_id) {
            t.set_status_to_success();
            return;
        }
    }
});

enter_task!(IsOtherEntityGroupBlackboardValueTrue, |t| {
    t.set_status_to_failed();
    let Some(member) = t.value_at("memberIdAddress") else { return };
    let Some(member_id) = t.expect_id(&member) else { return };
    let group_id = t.world().find_group_id_for_item(member_id);
    if group_id == INVALID_GROUP_ID {
        return;
    }
    let Some(message_name) = t.required_string("messageName") else { return };
    let channel = t.channel_for_group(group_id, &message_name);
    let value = t.channel_last_value(&channel);
    if t.faulted() {
        return;
    }
    t.set_status_to_success_if_true_else_to_failed(value.truthy());
});

enter_task!(IsAnyGroupMemberAggressed, |t| {
    let members = mixin::get_member_id_list(t);
    if t.faulted() {
        return;
    }
    for member_id in members {
        if t.world().is_aggressed(member_id) {
            t.set_status_to_success();
            return;
        }
    }
    t.set_status_to_failed();
});

enter_task!(IsGroupInitialSpawningComplete, |t| {
    let Some(group_id) = t.ctx().my_entity_group_id else {
        t.raise(crate::env::WorldError::Key("myEntityGroupId".to_string()).into());
        return;
    };
    if is_entity_group_initial_spawning_complete(t, group_id) {
        t.set_status_to_success();
        return;
    }
    if t.faulted() {
        return;
    }
    t.set_status_to_failed();
});

#[cfg(test)]
mod tests {
    use super::*;
    use crate::blackboards::{Address, ScopeType};
    use crate::env::testing::TestGroup;
    use crate::tree::tests::Rig;
    use crate::tree::{Attrs, Status};

    fn rig_with_group() -> Rig {
        let mut rig = Rig::new();
        rig.world.groups.insert(
            10,
            TestGroup { members: vec![1, 2], initial_spawning_complete: true, ..Default::default() },
        );
        rig.world.add_ball(1, [0.0; 3], 500);
        rig.world.add_ball(2, [0.0; 3], 501);
        rig
    }

    #[test]
    fn a_member_is_in_my_group_by_the_id_on_a_channel() {
        let attrs = Attrs::new().with("memberIdAddress", Address::new(ScopeType::Item, "M").to_value());
        let mut rig = rig_with_group();
        rig.post(ScopeType::Item, "M", Value::Int(2));
        assert_eq!(rig.run(Box::new(IsMemberInEntityGroup), attrs.clone()), Status::Success);
        let mut rig = rig_with_group();
        rig.post(ScopeType::Item, "M", Value::Int(3));
        assert_eq!(rig.run(Box::new(IsMemberInEntityGroup), attrs.clone()), Status::Failure);
        assert_eq!(rig_with_group().run(Box::new(IsMemberInEntityGroup), attrs), Status::Failure);
    }

    #[test]
    fn a_member_of_a_type_is_part_of_the_group() {
        let mut rig = rig_with_group();
        assert_eq!(
            rig.run(Box::new(IsMemberTypeIdPartOfGroup), Attrs::new().with("typeId", 501i64)),
            Status::Success
        );
        let mut rig = rig_with_group();
        assert_eq!(
            rig.run(Box::new(IsMemberTypeIdPartOfGroup), Attrs::new().with("typeId", 999i64)),
            Status::Failure
        );
        let mut rig = rig_with_group();
        assert_eq!(rig.run(Box::new(IsMemberTypeIdPartOfGroup), Attrs::new()), Status::Failure);
    }

    #[test]
    fn another_groups_blackboard_value_is_true_when_the_members_group_says_so() {
        let attrs = Attrs::new()
            .with("memberIdAddress", Address::new(ScopeType::Item, "M").to_value())
            .with("messageName", "FLAG");
        let mut rig = rig_with_group();
        rig.world.item_groups.insert(7, 55);
        rig.post(ScopeType::Item, "M", Value::Int(7));
        {
            let mut cx = crate::tree::Cx {
                core: &mut rig.tree.core,
                ctx: &mut rig.ctx,
                shared: &mut rig.shared,
                world: &mut rig.world,
            };
            cx.send_to_channel(crate::blackboards::entity_group_scope(55), "FLAG", Value::Bool(true));
        }
        assert_eq!(rig.run(Box::new(IsOtherEntityGroupBlackboardValueTrue), attrs.clone()), Status::Success);
        let mut rig = rig_with_group();
        rig.post(ScopeType::Item, "M", Value::Int(8));
        assert_eq!(
            rig.run(Box::new(IsOtherEntityGroupBlackboardValueTrue), attrs),
            Status::Failure,
            "in no group"
        );
    }

    #[test]
    fn any_group_member_aggressed_and_the_initial_spawning_of_my_group() {
        let mut rig = rig_with_group();
        rig.world.aggressed.insert(2);
        assert_eq!(rig.run(Box::new(IsAnyGroupMemberAggressed), Attrs::new()), Status::Success);
        let mut rig = rig_with_group();
        assert_eq!(rig.run(Box::new(IsAnyGroupMemberAggressed), Attrs::new()), Status::Failure);
        let mut rig = rig_with_group();
        assert_eq!(rig.run(Box::new(IsGroupInitialSpawningComplete), Attrs::new()), Status::Success);
        let mut rig = rig_with_group();
        rig.world.groups.get_mut(&10).unwrap().initial_spawning_complete = false;
        assert_eq!(rig.run(Box::new(IsGroupInitialSpawningComplete), Attrs::new()), Status::Failure);
    }
}
