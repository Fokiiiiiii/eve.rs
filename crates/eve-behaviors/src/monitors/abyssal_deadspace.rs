//! `behaviors/monitors/abyssal_deadspace.py`.

use crate::events::message::ON_CLOSE_TO_ABYSSAL_DANGER_ZONE;
use crate::tree::{Task, TaskCtx};
use crate::value::Value;

/// Starts the tree over when my ship gets close to the danger zone of an abyssal deadspace.
#[derive(Debug, Default)]
pub struct ResetOnCloseToAbyssalDangerZone;

impl Task for ResetOnCloseToAbyssalDangerZone {
    fn type_name(&self) -> &'static str {
        "ResetOnCloseToAbyssalDangerZone"
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        let Some(me) = t.my_item_id() else { return };
        t.subscribe_item(me, ON_CLOSE_TO_ABYSSAL_DANGER_ZONE, 0);
        t.set_status_to_success();
    }

    fn clean_up(&mut self, t: &mut TaskCtx<'_, '_>) {
        if !t.is_invalid() {
            t.set_status_to_invalid();
            if let Some(me) = t.my_item_id() {
                t.unsubscribe_item(me, ON_CLOSE_TO_ABYSSAL_DANGER_ZONE, 0);
            }
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
    use crate::tree::tests::Rig;
    use crate::tree::{Attrs, Status};

    #[test]
    fn the_tree_starts_over_when_the_danger_zone_is_close() {
        let mut rig = Rig::new();
        assert_eq!(rig.run(Box::new(ResetOnCloseToAbyssalDangerZone), Attrs::new()), Status::Success);
        let before = rig.ctx.statistics.reset_requests;
        {
            let mut cx = crate::tree::Cx {
                core: &mut rig.tree.core,
                ctx: &mut rig.ctx,
                shared: &mut rig.shared,
                world: &mut rig.world,
            };
            cx.send_event(1, ON_CLOSE_TO_ABYSSAL_DANGER_ZONE, &[]);
        }
        assert_eq!(rig.ctx.statistics.reset_requests, before + 1);
        rig.tree.release(&mut rig.ctx, &mut rig.shared, &mut rig.world);
        assert_eq!(rig.shared.events.subscription_count(), 0);
    }
}
