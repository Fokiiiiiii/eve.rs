//! `behaviors/monitors/timers.py`.

use crate::blackboards::MessageData;
use crate::tree::{Status, Task, TaskCtx};
use crate::value::Value;

const HANDLER_TIMEOUT: u16 = 0;

/// `ResetAfterTimeout` and `ResetAfterTimeoutBlocks`: starts a timer on a channel when there is none, and starts the tree over when it runs out (the
/// second fails while a timer runs).
#[derive(Debug, Default)]
pub struct ResetAfterTimeout {
    pub blocks: bool,
}

impl ResetAfterTimeout {
    pub fn blocks() -> Self {
        Self { blocks: true }
    }

    /// `_StartTimer(secondsUntilReset)`: the tasklet that sleeps, and the number of seconds on the channel.
    fn start_timer(t: &mut TaskCtx<'_, '_>, seconds: &Value) {
        let Some(address) = t.required_address("timerAddress") else { return };
        t.schedule_call(seconds.as_float().unwrap_or(0.0), HANDLER_TIMEOUT, Vec::new());
        t.send_blackboard_value(&address, seconds.clone());
    }
}

impl Task for ResetAfterTimeout {
    fn type_name(&self) -> &'static str {
        if self.blocks { "ResetAfterTimeoutBlocks" } else { "ResetAfterTimeout" }
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        t.set_status(if self.blocks { Status::Failure } else { Status::Success });
        let Some(timer_value) = t.value_at("timerAddress") else { return };
        let Some(seconds) = t.required("timeoutSeconds") else { return };
        if timer_value.is_none() {
            if self.blocks {
                t.set_status(Status::Success);
            }
            Self::start_timer(t, &seconds);
        }
    }

    fn clean_up(&mut self, t: &mut TaskCtx<'_, '_>) {
        t.set_status(Status::Invalid);
    }

    fn on_call(&mut self, t: &mut TaskCtx<'_, '_>, _handler: u16, _args: &[Value]) {
        // TimeoutThread, after the sleep
        if t.status() == Status::Invalid {
            return;
        }
        let Some(address) = t.required_address("timerAddress") else { return };
        t.send_blackboard_value(&address, Value::None);
        t.request_reset();
    }
}

/// `StartTimer` and `StartTimerBlocks`: starts a timer on a channel when there is none (the second fails while a timer runs).
#[derive(Debug, Default)]
pub struct StartTimer {
    pub blocks: bool,
}

impl StartTimer {
    pub fn blocks() -> Self {
        Self { blocks: true }
    }
}

impl Task for StartTimer {
    fn type_name(&self) -> &'static str {
        if self.blocks { "StartTimerBlocks" } else { "StartTimer" }
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        t.set_status(if self.blocks { Status::Failure } else { Status::Success });
        let Some(timer_value) = t.value_at("timerAddress") else { return };
        let Some(seconds) = t.required_float("timeoutSeconds") else { return };
        if timer_value.is_none() {
            if self.blocks {
                t.set_status(Status::Success);
            }
            let Some(address) = t.required_address("timerAddress") else { return };
            t.start_timer(seconds, &address);
        }
    }

    fn clean_up(&mut self, t: &mut TaskCtx<'_, '_>) {
        t.set_status(Status::Invalid);
    }
}

/// Starts a timer when there is none, and sets a value and starts the tree over whenever the timer's channel gets a message. The observer is never taken
/// off, as in the client.
#[derive(Debug, Default)]
pub struct SetBooleanValueAndResetAfterTimeout;

impl Task for SetBooleanValueAndResetAfterTimeout {
    fn type_name(&self) -> &'static str {
        "SetBooleanValueAndResetAfterTimeout"
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        t.set_status_to_success();
        let Some(address) = t.required_address("timerAddress") else { return };
        let timer_value = t.get_last_blackboard_value(&address);
        if timer_value.is_none() {
            let Some(seconds) = t.required_float("timeoutSeconds") else { return };
            t.start_timer(seconds, &address);
        }
        t.subscribe_to_blackboard(&address, HANDLER_TIMEOUT);
    }

    fn on_blackboard_message(
        &mut self,
        t: &mut TaskCtx<'_, '_>,
        _handler: u16,
        _channel: &str,
        message: Option<&MessageData>,
    ) {
        let (Some(channel), Some(value)) =
            (t.required_address("channelAddress"), Some(t.attribute("trueOrFalse")))
        else {
            return;
        };
        t.send_blackboard_value(&channel, value);
        if message.is_some_and(|m| m.value.is_none()) && !t.is_invalid() {
            t.request_reset();
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::blackboards::{Address, ScopeType};
    use crate::env::SECOND;
    use crate::tree::Attrs;
    use crate::tree::tests::Rig;

    fn item(name: &str) -> Value {
        Address::new(ScopeType::Item, name).to_value()
    }

    fn deliver_due(rig: &mut Rig, seconds: i64) {
        rig.shared.now += seconds * SECOND;
        rig.shared.fire_due_calls();
        rig.shared.fire_due_timers();
        for delivery in std::mem::take(&mut rig.shared.pending) {
            rig.tree.deliver(&mut rig.ctx, &mut rig.shared, &mut rig.world, &delivery).unwrap();
        }
    }

    #[test]
    fn a_reset_after_timeout_puts_the_seconds_on_the_channel_and_at_the_end_clears_it_and_starts_the_tree_over()
     {
        let attrs = Attrs::new().with("timerAddress", item("T")).with("timeoutSeconds", 20i64);
        let mut rig = Rig::new();
        assert_eq!(rig.run(Box::new(ResetAfterTimeout::default()), attrs.clone()), Status::Success);
        assert_eq!(
            rig.shared
                .blackboards
                .message_channel(crate::blackboards::item_scope(1), "T")
                .unwrap()
                .last_message_value(),
            Value::Int(20)
        );
        let before = rig.ctx.statistics.reset_requests;
        deliver_due(&mut rig, 19);
        assert_eq!(rig.ctx.statistics.reset_requests, before);
        // the tree resets by itself (the root exited): what matters is the channel and the request
        deliver_due(&mut rig, 1);
        assert_eq!(
            rig.shared
                .blackboards
                .message_channel(crate::blackboards::item_scope(1), "T")
                .unwrap()
                .last_message_value(),
            Value::None
        );
        assert_eq!(rig.ctx.statistics.reset_requests, before + 1);
        // it does not start a second timer while one runs
        let mut rig = Rig::new();
        rig.post(ScopeType::Item, "T", Value::Int(5));
        rig.run(Box::new(ResetAfterTimeout::default()), attrs);
        assert_eq!(rig.shared.scheduled_call_count(), 0);
    }

    #[test]
    fn the_blocking_versions_fail_while_a_timer_runs() {
        let attrs = Attrs::new().with("timerAddress", item("T")).with("timeoutSeconds", 20i64);
        let mut rig = Rig::new();
        rig.post(ScopeType::Item, "T", Value::Int(5));
        assert_eq!(rig.run(Box::new(ResetAfterTimeout::blocks()), attrs.clone()), Status::Failure);
        let mut rig = Rig::new();
        assert_eq!(rig.run(Box::new(ResetAfterTimeout::blocks()), attrs.clone()), Status::Success);
        let mut rig = Rig::new();
        rig.post(ScopeType::Item, "T", Value::Int(5));
        assert_eq!(rig.run(Box::new(StartTimer::blocks()), attrs.clone()), Status::Failure);
        let mut rig = Rig::new();
        assert_eq!(rig.run(Box::new(StartTimer::blocks()), attrs.clone()), Status::Success);
        assert_eq!(rig.shared.timers.active_count(), 1);
        let mut rig = Rig::new();
        assert_eq!(rig.run(Box::new(StartTimer::default()), attrs), Status::Success);
        assert_eq!(rig.shared.timers.active_count(), 1);
    }

    #[test]
    fn a_boolean_is_set_and_the_tree_starts_over_when_the_timer_runs_out() {
        let attrs = Attrs::new()
            .with("timerAddress", item("T"))
            .with("timeoutSeconds", 10i64)
            .with("channelAddress", item("C"))
            .with("trueOrFalse", true);
        let mut rig = Rig::new();
        rig.run(Box::new(SetBooleanValueAndResetAfterTimeout), attrs);
        deliver_due(&mut rig, 10);
        assert_eq!(
            rig.shared
                .blackboards
                .message_channel(crate::blackboards::item_scope(1), "C")
                .unwrap()
                .last_message_value(),
            Value::Bool(true)
        );
    }
}
