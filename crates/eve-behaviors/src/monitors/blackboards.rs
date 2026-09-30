//! `behaviors/monitors/blackboards.py`.

use crate::blackboards::MessageData;
use crate::tree::{Task, TaskCtx};
use crate::value::Value;

const HANDLER_UPDATE: u16 = 0;

/// Starts the tree over whenever a message comes on a channel.
#[derive(Debug, Default)]
pub struct BlackboardMessageMonitor;

impl Task for BlackboardMessageMonitor {
    fn type_name(&self) -> &'static str {
        "BlackboardMessageMonitor"
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        let Some(address) = t.required_address("messageAddress") else { return };
        t.subscribe_to_blackboard(&address, HANDLER_UPDATE);
        t.set_status_to_success();
    }

    fn clean_up(&mut self, t: &mut TaskCtx<'_, '_>) {
        if !t.is_invalid() {
            if let Some(address) = t.required_address("messageAddress") {
                t.unsubscribe_to_blackboard(&address, HANDLER_UPDATE);
            }
            t.set_status_to_invalid();
        }
    }

    fn on_blackboard_message(
        &mut self,
        t: &mut TaskCtx<'_, '_>,
        _handler: u16,
        _channel: &str,
        _message: Option<&MessageData>,
    ) {
        if !t.is_invalid() {
            t.request_reset();
        }
    }
}

/// `BlackboardMessageMonitor` that fails while the channel has no message.
#[derive(Debug, Default)]
pub struct BlackboardMessageMonitorBlocking;

impl Task for BlackboardMessageMonitorBlocking {
    fn type_name(&self) -> &'static str {
        "BlackboardMessageMonitorBlocking"
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        let Some(address) = t.required_address("messageAddress") else { return };
        t.subscribe_to_blackboard(&address, HANDLER_UPDATE);
        let message = t.get_last_blackboard_value(&address);
        t.set_status_to_success_if_true_else_to_failed(!message.is_none());
    }

    fn clean_up(&mut self, t: &mut TaskCtx<'_, '_>) {
        BlackboardMessageMonitor.clean_up(t);
    }

    fn on_blackboard_message(
        &mut self,
        t: &mut TaskCtx<'_, '_>,
        handler: u16,
        channel: &str,
        message: Option<&MessageData>,
    ) {
        BlackboardMessageMonitor.on_blackboard_message(t, handler, channel, message);
    }
}

/// Puts a flag up whenever a message comes on a channel.
#[derive(Debug, Default)]
pub struct BlackboardFlagOnMessageUpdate;

impl Task for BlackboardFlagOnMessageUpdate {
    fn type_name(&self) -> &'static str {
        "BlackboardFlagOnMessageUpdate"
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        let Some(address) = t.required_address("messageAddress") else { return };
        t.subscribe_to_blackboard(&address, HANDLER_UPDATE);
        t.set_status_to_success();
    }

    fn clean_up(&mut self, t: &mut TaskCtx<'_, '_>) {
        if t.is_invalid() {
            return;
        }
        if let Some(address) = t.required_address("messageAddress") {
            t.unsubscribe_to_blackboard(&address, HANDLER_UPDATE);
        }
        t.set_status_to_invalid();
    }

    fn on_blackboard_message(
        &mut self,
        t: &mut TaskCtx<'_, '_>,
        _handler: u16,
        _channel: &str,
        _message: Option<&MessageData>,
    ) {
        if t.is_invalid() {
            return;
        }
        let Some(flag) = t.required_address("flagAddress") else { return };
        t.send_blackboard_value(&flag, Value::Bool(true));
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::blackboards::{Address, ScopeType};
    use crate::tree::tests::Rig;
    use crate::tree::{Attrs, Status};

    fn attrs() -> Attrs {
        Attrs::new()
            .with("messageAddress", Address::new(ScopeType::Item, "M").to_value())
            .with("flagAddress", Address::new(ScopeType::Item, "F").to_value())
    }

    #[test]
    fn a_message_on_the_channel_starts_the_tree_over() {
        let mut rig = Rig::new();
        assert_eq!(rig.run(Box::new(BlackboardMessageMonitor), attrs()), Status::Success);
        let before = rig.ctx.statistics.reset_requests;
        rig.post(ScopeType::Item, "M", Value::Int(1));
        assert_eq!(rig.ctx.statistics.reset_requests, before + 1);
    }

    #[test]
    fn the_blocking_monitor_fails_until_the_channel_has_a_message_and_a_cleaned_up_monitor_stops_listening() {
        let mut rig = Rig::new();
        assert_eq!(rig.run(Box::new(BlackboardMessageMonitorBlocking), attrs()), Status::Failure);
        let mut rig = Rig::new();
        rig.post(ScopeType::Item, "M", Value::Int(1));
        assert_eq!(rig.run(Box::new(BlackboardMessageMonitorBlocking), attrs()), Status::Success);
        rig.tree.release(&mut rig.ctx, &mut rig.shared, &mut rig.world);
        let channel = rig.shared.blackboards.message_channel(crate::blackboards::item_scope(1), "M").unwrap();
        assert!(channel.observers().is_empty());
    }

    #[test]
    fn a_message_puts_the_flag_up() {
        let mut rig = Rig::new();
        rig.run(Box::new(BlackboardFlagOnMessageUpdate), attrs());
        rig.post(ScopeType::Item, "M", Value::Int(1));
        let flag = rig
            .shared
            .blackboards
            .message_channel(crate::blackboards::item_scope(1), "F")
            .unwrap()
            .last_message_value();
        assert_eq!(flag, Value::Bool(true));
    }
}
