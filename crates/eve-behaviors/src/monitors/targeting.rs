//! `behaviors/monitors/targeting.py`.

use crate::events::message::TARGET_LOST;
use crate::tree::{Task, TaskCtx};
use crate::value::Value;

/// Starts the tree over when the target it watches is lost.
#[derive(Debug, Default)]
pub struct TargetLostMonitor {
    target_id: Value,
}

impl Task for TargetLostMonitor {
    fn type_name(&self) -> &'static str {
        "TargetLostMonitor"
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        let Some(target) = t.value_at("targetAddress") else { return };
        self.target_id = target;
        if self.target_id.truthy() {
            let Some(me) = t.my_item_id() else { return };
            t.subscribe_item(me, TARGET_LOST, 0);
            t.set_status_to_success();
        } else {
            t.set_status_to_failed();
        }
    }

    fn clean_up(&mut self, t: &mut TaskCtx<'_, '_>) {
        if !t.is_invalid() {
            if let Some(me) = t.my_item_id() {
                t.unsubscribe_item(me, TARGET_LOST, 0);
            }
            t.set_status_to_invalid();
        }
    }

    fn on_event(&mut self, t: &mut TaskCtx<'_, '_>, _handler: u16, args: &[Value]) {
        if t.is_invalid() {
            return;
        }
        if args.first().is_some_and(|target| self.target_id.py_eq(target)) {
            t.request_reset();
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::blackboards::{Address, ScopeType};
    use crate::tree::tests::Rig;
    use crate::tree::{Attrs, Status};

    #[test]
    fn losing_the_target_i_watch_starts_the_tree_over_and_with_no_target_the_monitor_fails() {
        let attrs = Attrs::new().with("targetAddress", Address::new(ScopeType::Item, "T").to_value());
        let mut rig = Rig::new();
        rig.post(ScopeType::Item, "T", Value::Int(20));
        assert_eq!(rig.run(Box::new(TargetLostMonitor::default()), attrs.clone()), Status::Success);
        let before = rig.ctx.statistics.reset_requests;
        let mut cx = crate::tree::Cx {
            core: &mut rig.tree.core,
            ctx: &mut rig.ctx,
            shared: &mut rig.shared,
            world: &mut rig.world,
        };
        cx.send_event(1, TARGET_LOST, &[Value::Int(21), Value::Str("x".into())]);
        cx.send_event(1, TARGET_LOST, &[Value::Int(20), Value::Str("x".into())]);
        assert_eq!(rig.ctx.statistics.reset_requests, before + 1);
        assert_eq!(Rig::new().run(Box::new(TargetLostMonitor::default()), attrs), Status::Failure);
    }
}
