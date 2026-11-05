//! `behaviors/monitors/combattargets.py`.

use crate::consts::{ATTRIBUTE_MAX_RANGE, ATTRIBUTE_MISSILE_ENTITY_VELOCITY_MULTIPLIER};
use crate::events::message::{ON_DOGMA_ATTRIBUTE_VALUE_CHANGED, TARGET_ADDED};
use crate::tree::{Task, TaskCtx};
use crate::value::Value;

/// `BEHAVIOR_RANGE_ATTRIBUTES_FOR_RECALCULATION`.
pub const BEHAVIOR_RANGE_ATTRIBUTES_FOR_RECALCULATION: [i64; 2] =
    [ATTRIBUTE_MAX_RANGE, ATTRIBUTE_MISSILE_ENTITY_VELOCITY_MULTIPLIER];

/// Starts the tree over when the target it waits for is locked.
#[derive(Debug, Default)]
pub struct MonitorTargetAdded;

impl Task for MonitorTargetAdded {
    fn type_name(&self) -> &'static str {
        "MonitorTargetAdded"
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        let Some(me) = t.my_item_id() else { return };
        t.subscribe_item(me, TARGET_ADDED, 0);
        t.set_status_to_success();
    }

    fn clean_up(&mut self, t: &mut TaskCtx<'_, '_>) {
        if !t.is_invalid()
            && let Some(me) = t.my_item_id()
        {
            t.unsubscribe_item(me, TARGET_ADDED, 0);
        }
        t.set_status_to_invalid();
    }

    fn on_event(&mut self, t: &mut TaskCtx<'_, '_>, _handler: u16, args: &[Value]) {
        if t.is_invalid() {
            return;
        }
        let Some(target_id) = args.first() else { return };
        let Some(pending) = t.value_at("targetAddress") else { return };
        if pending.py_eq(target_id) {
            t.request_reset();
        }
    }
}

/// Clears the combat range and starts the tree over when an attribute that the range depends on changes.
#[derive(Debug, Default)]
pub struct CombatRangeMonitor;

impl Task for CombatRangeMonitor {
    fn type_name(&self) -> &'static str {
        "CombatRangeMonitor"
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        t.set_status_to_success();
        let Some(me) = t.my_item_id() else { return };
        t.subscribe_item(me, ON_DOGMA_ATTRIBUTE_VALUE_CHANGED, 0);
    }

    fn clean_up(&mut self, t: &mut TaskCtx<'_, '_>) {
        if !t.is_invalid()
            && let Some(me) = t.my_item_id()
        {
            t.unsubscribe_item(me, ON_DOGMA_ATTRIBUTE_VALUE_CHANGED, 0);
        }
        t.set_status_to_invalid();
    }

    fn on_event(&mut self, t: &mut TaskCtx<'_, '_>, _handler: u16, args: &[Value]) {
        let Some(attribute_id) = args.first().and_then(Value::as_int) else { return };
        if BEHAVIOR_RANGE_ATTRIBUTES_FOR_RECALCULATION.contains(&attribute_id) {
            let Some(address) = t.required_address("combatRangeRecalculateAddress") else { return };
            t.send_blackboard_value(&address, Value::None);
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

    fn item(name: &str) -> Value {
        Address::new(ScopeType::Item, name).to_value()
    }

    fn tell(rig: &mut Rig, message: crate::events::MessageId, args: &[Value]) {
        let mut cx = crate::tree::Cx {
            core: &mut rig.tree.core,
            ctx: &mut rig.ctx,
            shared: &mut rig.shared,
            world: &mut rig.world,
        };
        cx.send_event(1, message, args);
    }

    #[test]
    fn a_target_added_that_is_the_one_i_wait_for_starts_the_tree_over() {
        let mut rig = Rig::new();
        rig.post(ScopeType::Item, "T", Value::Int(20));
        assert_eq!(
            rig.run(Box::new(MonitorTargetAdded), Attrs::new().with("targetAddress", item("T"))),
            Status::Success
        );
        let before = rig.ctx.statistics.reset_requests;
        tell(&mut rig, TARGET_ADDED, &[Value::Int(21)]);
        assert_eq!(rig.ctx.statistics.reset_requests, before);
        tell(&mut rig, TARGET_ADDED, &[Value::Int(20)]);
        assert_eq!(rig.ctx.statistics.reset_requests, before + 1);
    }

    #[test]
    fn a_change_of_an_attribute_the_range_depends_on_clears_the_range_and_starts_the_tree_over() {
        let mut rig = Rig::new();
        rig.post(ScopeType::Item, "R", Value::Float(5.0));
        rig.run(Box::new(CombatRangeMonitor), Attrs::new().with("combatRangeRecalculateAddress", item("R")));
        let before = rig.ctx.statistics.reset_requests;
        tell(&mut rig, ON_DOGMA_ATTRIBUTE_VALUE_CHANGED, &[Value::Int(9)]);
        assert_eq!(rig.ctx.statistics.reset_requests, before);
        tell(&mut rig, ON_DOGMA_ATTRIBUTE_VALUE_CHANGED, &[Value::Int(ATTRIBUTE_MAX_RANGE)]);
        assert_eq!(rig.ctx.statistics.reset_requests, before + 1);
        assert_eq!(
            rig.shared
                .blackboards
                .message_channel(crate::blackboards::item_scope(1), "R")
                .unwrap()
                .last_message_value(),
            Value::None
        );
    }
}
