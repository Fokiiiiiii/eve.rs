//! `behaviors/actions/eventlogger.py`.

use crate::groups::mixin::get_group_owner_id;

enter_task!(
    /// Writes down that the behavior did something (`behaviorEvent`).
    LogTaskEvent,
    |t| {
        t.set_status_to_success();
        let Some(event) = t.required("behaviorEvent") else { return };
        let Some(slim) = t.ctx().my_slim_item.clone() else {
            t.raise(crate::env::WorldError::Reference.into());
            return;
        };
        let behavior_id = t.behavior_id();
        t.world().log_behavior_task(&event, slim.owner_id, &behavior_id, slim.type_id);
    }
);

enter_task!(
    /// Writes down that my group was destroyed.
    LogGroupDestruction,
    |t| {
        t.set_status_to_success();
        let owner = get_group_owner_id(t);
        if t.faulted() {
            return;
        }
        let behavior_id = t.behavior_id();
        let Some(targets) = t.value_at("combatTargetsAddress") else { return };
        t.world().log_behavior_group_destroyed(owner, &behavior_id, &targets);
    }
);

#[cfg(test)]
mod tests {
    use super::*;
    use crate::env::SlimItem;
    use crate::env::testing::TestGroup;
    use crate::tree::Attrs;
    use crate::tree::tests::Rig;
    use crate::value::Value;

    #[test]
    fn a_task_event_is_logged_with_my_owner_and_type() {
        let mut rig = Rig::new();
        rig.ctx.my_slim_item = Some(SlimItem { owner_id: Some(1000006), type_id: 500, ..Default::default() });
        rig.run(Box::new(LogTaskEvent), Attrs::new().with("behaviorEvent", "arrived"));
        assert_eq!(rig.world.take_calls(), ["log_behavior_task('arrived', Some(1000006), None, 500)"]);
    }

    #[test]
    fn a_groups_destruction_is_logged_with_its_owner_and_the_combat_targets() {
        let mut rig = Rig::new();
        rig.world.groups.insert(10, TestGroup { owner_id: Some(1000006), ..Default::default() });
        rig.post(crate::blackboards::ScopeType::Item, "Targets", Value::set([Value::Int(4)]));
        let attrs = Attrs::new().with(
            "combatTargetsAddress",
            crate::blackboards::Address::new(crate::blackboards::ScopeType::Item, "Targets").to_value(),
        );
        rig.run(Box::new(LogGroupDestruction), attrs);
        assert_eq!(rig.world.take_calls(), ["log_behavior_group_destroyed(Some(1000006), None, {4})"]);
    }
}
