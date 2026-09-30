//! `behaviors/monitors/navigation.py`.

use crate::consts::DESTINY_MODE_WARP;
use crate::events::message::ON_BALL_MODE_CHANGED;
use crate::groups::mixin::get_member_id_list;
use crate::tree::{Task, TaskCtx};
use crate::value::Value;

/// Starts the tree over when a member of my group starts or ends a warp.
#[derive(Debug, Default)]
pub struct GroupMemberWarpModeChangedMonitor {
    tracked_item_ids: Vec<i64>,
}

impl Task for GroupMemberWarpModeChangedMonitor {
    fn type_name(&self) -> &'static str {
        "GroupMemberWarpModeChangedMonitor"
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        t.set_status_to_success();
        self.tracked_item_ids = get_member_id_list(t);
        for item_id in self.tracked_item_ids.clone() {
            t.subscribe_item(item_id, ON_BALL_MODE_CHANGED, 0);
        }
    }

    fn clean_up(&mut self, t: &mut TaskCtx<'_, '_>) {
        if t.is_invalid() {
            return;
        }
        for item_id in std::mem::take(&mut self.tracked_item_ids) {
            t.unsubscribe_item(item_id, ON_BALL_MODE_CHANGED, 0);
        }
        t.set_status_to_invalid();
    }

    fn on_event(&mut self, t: &mut TaskCtx<'_, '_>, _handler: u16, args: &[Value]) {
        if t.is_invalid() {
            return;
        }
        let modes: Vec<Option<i64>> = args.iter().take(2).map(Value::as_int).collect();
        if modes.contains(&Some(DESTINY_MODE_WARP)) {
            t.request_reset();
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::env::testing::TestGroup;
    use crate::tree::Attrs;
    use crate::tree::tests::Rig;

    #[test]
    fn a_members_warp_starting_or_ending_starts_the_tree_over() {
        let mut rig = Rig::new();
        rig.world.groups.insert(10, TestGroup { members: vec![1, 2], ..Default::default() });
        rig.run(Box::new(GroupMemberWarpModeChangedMonitor::default()), Attrs::new());
        let before = rig.ctx.statistics.reset_requests;
        for (item, old, new, count) in [
            (2, 0, 1, 0),
            (2, 0, DESTINY_MODE_WARP, 1),
            (1, DESTINY_MODE_WARP, 0, 1),
            (3, 0, DESTINY_MODE_WARP, 0),
        ] {
            let mut cx = crate::tree::Cx {
                core: &mut rig.tree.core,
                ctx: &mut rig.ctx,
                shared: &mut rig.shared,
                world: &mut rig.world,
            };
            cx.send_event(item, ON_BALL_MODE_CHANGED, &[Value::Int(old), Value::Int(new)]);
            let _ = count;
        }
        assert_eq!(rig.ctx.statistics.reset_requests, before + 2, "an unknown item is not tracked");
        rig.tree.release(&mut rig.ctx, &mut rig.shared, &mut rig.world);
        assert_eq!(rig.shared.events.subscription_count(), 0);
    }
}
