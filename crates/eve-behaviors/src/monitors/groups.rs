//! `behaviors/monitors/groups.py`.

use crate::events::Subject;
use crate::events::message::ON_ENTITY_GROUP_MEMBERSHIP_CHANGED;
use crate::groups::mixin::{entity_group_not_found, has_entity_group};
use crate::tree::{Task, TaskCtx};
use crate::value::Value;

/// Starts the tree over when the members of my group change.
#[derive(Debug, Default)]
pub struct GroupMembershipMonitor;

impl Task for GroupMembershipMonitor {
    fn type_name(&self) -> &'static str {
        "GroupMembershipMonitor"
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        if !has_entity_group(t) {
            entity_group_not_found(t);
            return;
        }
        if let Some(group) = t.ctx().my_entity_group_id {
            t.subscribe(Subject::Group(group), ON_ENTITY_GROUP_MEMBERSHIP_CHANGED, 0);
        }
        t.set_status_to_success();
    }

    fn clean_up(&mut self, t: &mut TaskCtx<'_, '_>) {
        if !t.is_invalid() {
            // a group that cannot be found is not an error here
            if has_entity_group(t)
                && let Some(group) = t.ctx().my_entity_group_id
            {
                t.unsubscribe(Subject::Group(group), ON_ENTITY_GROUP_MEMBERSHIP_CHANGED, 0);
            }
            t.set_status_to_invalid();
        }
    }

    fn on_event(&mut self, t: &mut TaskCtx<'_, '_>, _handler: u16, _args: &[Value]) {
        if t.is_invalid() {
            return;
        }
        t.request_reset();
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::env::testing::TestGroup;
    use crate::tree::tests::Rig;
    use crate::tree::{Attrs, BehaviorError, Status};

    #[test]
    fn a_change_of_my_groups_members_starts_the_tree_over() {
        let mut rig = Rig::new();
        rig.world.groups.insert(10, TestGroup::default());
        assert_eq!(rig.run(Box::new(GroupMembershipMonitor), Attrs::new()), Status::Success);
        let before = rig.ctx.statistics.reset_requests;
        rig.tree.core.record_messages = false;
        let mut cx = crate::tree::Cx {
            core: &mut rig.tree.core,
            ctx: &mut rig.ctx,
            shared: &mut rig.shared,
            world: &mut rig.world,
        };
        cx.send_message_about(Subject::Group(10), ON_ENTITY_GROUP_MEMBERSHIP_CHANGED, &[Value::None]);
        assert_eq!(rig.ctx.statistics.reset_requests, before + 1);
    }

    #[test]
    fn a_monitor_of_a_group_that_is_not_there_raises_and_one_cleaned_up_after_the_group_went_does_not() {
        let mut rig = Rig::new();
        rig.run(Box::new(GroupMembershipMonitor), Attrs::new());
        assert!(matches!(rig.last_error, Some(BehaviorError::Task(ref m)) if m.contains("was not found")));
        let mut rig = Rig::new();
        rig.world.groups.insert(10, TestGroup::default());
        rig.run(Box::new(GroupMembershipMonitor), Attrs::new());
        rig.world.groups.clear();
        rig.tree.release(&mut rig.ctx, &mut rig.shared, &mut rig.world);
        assert_eq!(rig.last_error, None);
    }
}
