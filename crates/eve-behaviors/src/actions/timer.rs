//! `behaviors/actions/timer.py`: waiting for a time to pass, on the clock of the simulation and on the timers of the blackboards.

use crate::blackboards::MessageData;
use crate::tree::{BehaviorError, Task, TaskCtx};
use crate::value::Value;

/// What a timeout task does when its time has come.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum TimeoutKind {
    /// `TimeoutAction`: nothing (the client's `_OnTimeout` raises `NotImplementedError` in the tasklet, which is only logged).
    Base,
    /// `SucceedAfterTimeoutAction`.
    SucceedAfter,
    /// `ClearValueAndResetAfterTimeout`.
    ClearValueAndReset,
}

const HANDLER_TIMEOUT_THREAD: u16 = 0;
const HANDLER_ON_TIMEOUT: u16 = 1;
const CONTEXT_TIMEOUT_THREAD: &str = "timeoutThread";

/// `TimeoutAction` and the tasks of the client that are it with another `_OnTimeout`.
#[derive(Debug, Clone, Copy)]
pub struct TimeoutAction {
    name: &'static str,
    kind: TimeoutKind,
}

impl TimeoutAction {
    /// `TimeoutAction`.
    pub fn new() -> Self {
        Self { name: "TimeoutAction", kind: TimeoutKind::Base }
    }

    /// `SucceedAfterTimeoutAction`: suspended until the time has passed, then a success.
    pub fn succeed_after_timeout() -> Self {
        Self { name: "SucceedAfterTimeoutAction", kind: TimeoutKind::SucceedAfter }
    }

    /// `ClearValueAndResetAfterTimeout`: after the time the value is cleared and the tree is reset.
    pub fn clear_value_and_reset_after_timeout() -> Self {
        Self { name: "ClearValueAndResetAfterTimeout", kind: TimeoutKind::ClearValueAndReset }
    }

    /// `_GetTimeoutSeconds`: the milliseconds on the blackboard divided by 1000 (whole seconds, for whole milliseconds: Python 2 divides integers
    /// so), else the task's `timeoutSeconds`.
    fn get_timeout_seconds(t: &mut TaskCtx<'_, '_>) -> Option<f64> {
        if t.attribute("timeoutMilliSecondsAddress").truthy() {
            let address = t.required_address("timeoutMilliSecondsAddress")?;
            return match t.get_last_blackboard_value(&address) {
                Value::Int(milliseconds) => Some(milliseconds.div_euclid(1000) as f64),
                Value::Float(milliseconds) => Some(milliseconds / 1000.0),
                _ => {
                    t.raise(BehaviorError::Task("TypeError: unsupported operand type(s) for /".to_string()));
                    None
                }
            };
        }
        t.required_float("timeoutSeconds")
    }
}

impl Default for TimeoutAction {
    fn default() -> Self {
        Self::new()
    }
}

impl Task for TimeoutAction {
    fn type_name(&self) -> &'static str {
        self.name
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        t.set_status_to_success();
        // `uthread2.StartTasklet(self._TimeoutThread)`
        let tasklet = t.new_tasklet();
        t.schedule_tasklet_call(tasklet, 0.0, HANDLER_TIMEOUT_THREAD, vec![Value::Int(tasklet)]);
        t.set_context_value(CONTEXT_TIMEOUT_THREAD, Value::Int(tasklet));
        if self.kind == TimeoutKind::SucceedAfter {
            t.set_status_to_suspended();
        }
    }

    fn clean_up(&mut self, t: &mut TaskCtx<'_, '_>) {
        t.set_status_to_invalid();
        let timeout_thread = t.get_context_value(CONTEXT_TIMEOUT_THREAD);
        if timeout_thread.truthy()
            && let Some(tasklet) = timeout_thread.as_int()
        {
            t.kill_tasklet(tasklet);
        }
    }

    fn on_call(&mut self, t: &mut TaskCtx<'_, '_>, handler: u16, args: &[Value]) {
        // what a tasklet raises is not the task's
        match handler {
            HANDLER_TIMEOUT_THREAD => {
                let Some(tasklet) = args.first().and_then(Value::as_int) else { return };
                t.eat_exceptions(|t| {
                    if let Some(seconds) = Self::get_timeout_seconds(t) {
                        t.schedule_tasklet_call(tasklet, seconds, HANDLER_ON_TIMEOUT, Vec::new());
                    }
                });
            }
            HANDLER_ON_TIMEOUT => {
                let kind = self.kind;
                t.eat_exceptions(|t| match kind {
                    TimeoutKind::Base => {}
                    TimeoutKind::SucceedAfter => {
                        if !t.is_invalid() {
                            t.set_status_to_success();
                            t.on_status_updated();
                            t.start_self_next_tick();
                        }
                    }
                    TimeoutKind::ClearValueAndReset => {
                        if let Some(address) = t.required_address("targetAddress") {
                            t.send_blackboard_value(&address, Value::None);
                            t.request_reset();
                        }
                    }
                });
            }
            _ => {}
        }
    }
}

const HANDLER_TIMER_CHANNEL: u16 = 0;

/// Fails until the timer on the blackboard has run out, and then succeeds once and starts it again.
#[derive(Debug, Default)]
pub struct FailUntilTimeoutAction;

impl Task for FailUntilTimeoutAction {
    fn type_name(&self) -> &'static str {
        "FailUntilTimeoutAction"
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        let Some(trigger_address) = t.required_address("triggerAddress") else { return };
        let reset_value = t.get_last_blackboard_value(&trigger_address);
        if reset_value.truthy() {
            t.set_status_to_success();
            t.send_blackboard_value(&trigger_address, Value::Bool(false));
        } else {
            t.set_status_to_failed();
        }
        let Some(timer_address) = t.required_address("timerAddress") else { return };
        let timer_value = t.get_last_blackboard_value(&timer_address);
        if timer_value.is_none() {
            t.set_status_to_failed();
            let Some(timeout_seconds) = t.required_float("timeoutSeconds") else { return };
            t.start_timer(timeout_seconds, &timer_address);
        }
        t.subscribe_to_blackboard(&timer_address, HANDLER_TIMER_CHANNEL);
    }

    fn on_blackboard_message(
        &mut self,
        t: &mut TaskCtx<'_, '_>,
        _handler: u16,
        _channel: &str,
        message: Option<&MessageData>,
    ) {
        // `_OnTimeout(name, message)`
        let Some(message) = message else {
            t.raise(BehaviorError::Task(
                "AttributeError: 'NoneType' object has no attribute 'value'".to_string(),
            ));
            return;
        };
        if message.value.is_none() && !t.is_invalid() {
            let Some(trigger_address) = t.required_address("triggerAddress") else { return };
            t.send_blackboard_value(&trigger_address, Value::Bool(true));
            let Some(timer_address) = t.required_address("timerAddress") else { return };
            let Some(timeout_seconds) = t.required_float("timeoutSeconds") else { return };
            t.start_timer(timeout_seconds, &timer_address);
            t.request_reset();
        }
    }

    fn clean_up(&mut self, t: &mut TaskCtx<'_, '_>) {
        if !t.is_invalid()
            && let Some(timer_address) = t.required_address("timerAddress")
        {
            t.unsubscribe_to_blackboard(&timer_address, HANDLER_TIMER_CHANNEL);
        }
        t.set_status_to_invalid();
    }
}

/// Suspends until the timer on the blackboard has run out (starting it when there is none).
#[derive(Debug, Default)]
pub struct BlockWhileTimeout;

impl Task for BlockWhileTimeout {
    fn type_name(&self) -> &'static str {
        "BlockWhileTimeout"
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        let Some(timer_address) = t.required_address("timerAddress") else { return };
        let timer_value = t.get_last_blackboard_value(&timer_address);
        let Some(max_random) = t.required("maxRandomTimeoutSeconds") else { return };
        let Some(timeout_seconds) = t.required("timeoutSeconds") else { return };
        let seconds_until_reset = if max_random.truthy() {
            // `random.randint(timeoutSeconds, maxRandomTimeoutSeconds)`
            let (Some(low), Some(high)) = (timeout_seconds.as_int(), max_random.as_int()) else {
                t.raise(BehaviorError::Task("TypeError: randint needs whole numbers".to_string()));
                return;
            };
            t.world().random_int(low, high) as f64
        } else {
            timeout_seconds.as_float().unwrap_or(0.0)
        };
        if timer_value.is_none() {
            t.start_timer(seconds_until_reset, &timer_address);
        }
        t.set_status_to_suspended();
        t.subscribe_to_blackboard(&timer_address, HANDLER_TIMER_CHANNEL);
    }

    fn on_exit(&mut self, t: &mut TaskCtx<'_, '_>) {
        if let Some(timer_address) = t.required_address("timerAddress") {
            t.unsubscribe_to_blackboard(&timer_address, HANDLER_TIMER_CHANNEL);
        }
    }

    fn on_blackboard_message(
        &mut self,
        t: &mut TaskCtx<'_, '_>,
        _handler: u16,
        _channel: &str,
        message: Option<&MessageData>,
    ) {
        if t.is_invalid() {
            return;
        }
        let Some(message) = message else {
            t.raise(BehaviorError::Task(
                "AttributeError: 'NoneType' object has no attribute 'value'".to_string(),
            ));
            return;
        };
        if message.value.is_none() {
            t.set_status_to_success();
            t.start_self_next_tick();
        }
    }

    fn clean_up(&mut self, t: &mut TaskCtx<'_, '_>) {
        if !t.is_invalid()
            && let Some(timer_address) = t.required_address("timerAddress")
        {
            t.unsubscribe_to_blackboard(&timer_address, HANDLER_TIMER_CHANNEL);
        }
        t.set_status_to_invalid();
    }
}

enter_task!(
    /// Stops the timer on the blackboard.
    ClearTimer,
    |t| {
        let Some(address) = t.required_address("timerAddress") else { return };
        let timer_id = t.get_last_blackboard_value(&address);
        if let Some(id) = timer_id.as_int() {
            t.clear_timer(id);
        }
        t.set_status_to_success();
    }
);

#[cfg(test)]
mod tests {
    use super::*;
    use crate::blackboards::{Address, ScopeType};
    use crate::tree::tests::Rig;
    use crate::tree::{Attrs, Status};

    fn item(name: &str) -> Value {
        Address::new(ScopeType::Item, name).to_value()
    }

    fn fire(rig: &mut Rig) {
        rig.shared.fire_due_calls();
        rig.shared.fire_due_timers();
        for delivery in std::mem::take(&mut rig.shared.pending) {
            if let Err(error) = rig.tree.deliver(&mut rig.ctx, &mut rig.shared, &mut rig.world, &delivery) {
                rig.last_error.get_or_insert(error);
            }
        }
    }

    #[test]
    fn a_timeout_task_succeeds_at_once_and_its_tasklet_reads_the_time_and_sleeps_it() {
        let mut rig = Rig::new();
        let attrs = Attrs::new().with("timeoutSeconds", 30i64).with("targetAddress", item("Value"));
        rig.post(ScopeType::Item, "Value", Value::Int(5));
        let task = rig.tree.add_task(Box::new(TimeoutAction::clear_value_and_reset_after_timeout()), attrs);
        rig.tree.start_root_task(task);
        rig.tick().unwrap();
        assert_eq!(rig.tree.status_of(task), Status::Success);
        assert_eq!(rig.shared.scheduled_call_count(), 1, "the tasklet has not started");
        fire(&mut rig);
        assert_eq!(rig.shared.scheduled_call_count(), 1, "it sleeps");
        assert_eq!(rig.shared.next_call_due(), Some(rig.shared.now + crate::env::seconds_to_ticks(30.0)));
        rig.advance_seconds(30.0);
        fire(&mut rig);
        assert_eq!(rig.last(ScopeType::Item, "Value"), Value::None);
        assert!(rig.tree.has_reset_request());
    }

    #[test]
    fn the_milliseconds_on_the_blackboard_are_divided_by_1000_as_python_2_divides_whole_numbers() {
        for (milliseconds, seconds) in [(Value::Int(2500), 2.0), (Value::Float(2500.0), 2.5)] {
            let mut rig = Rig::new();
            rig.post(ScopeType::Item, "Ms", milliseconds);
            let attrs = Attrs::new()
                .with("timeoutMilliSecondsAddress", item("Ms"))
                .with("targetAddress", item("Value"));
            let task =
                rig.tree.add_task(Box::new(TimeoutAction::clear_value_and_reset_after_timeout()), attrs);
            rig.tree.start_root_task(task);
            rig.tick().unwrap();
            let now = rig.shared.now;
            fire(&mut rig);
            assert_eq!(rig.shared.next_call_due(), Some(now + crate::env::seconds_to_ticks(seconds)));
        }
    }

    #[test]
    fn a_timeout_task_that_is_cleaned_up_kills_its_tasklet() {
        let mut rig = Rig::new();
        let attrs = Attrs::new().with("timeoutSeconds", 30i64).with("targetAddress", item("Value"));
        let task = rig.tree.add_task(Box::new(TimeoutAction::succeed_after_timeout()), attrs);
        rig.tree.start_root_task(task);
        rig.tick().unwrap();
        assert_eq!(rig.tree.status_of(task), Status::Suspended);
        fire(&mut rig);
        rig.tree.release(&mut rig.ctx, &mut rig.shared, &mut rig.world);
        assert_eq!(rig.shared.scheduled_call_count(), 0);
    }

    #[test]
    fn a_task_that_succeeds_after_a_timeout_waits_suspended_and_then_succeeds() {
        let mut rig = Rig::new();
        let attrs = Attrs::new().with("timeoutSeconds", 5i64);
        let task = rig.tree.add_task(Box::new(TimeoutAction::succeed_after_timeout()), attrs);
        rig.tree.start_root_task(task);
        rig.tick().unwrap();
        fire(&mut rig);
        rig.tick().unwrap();
        assert_eq!(rig.tree.status_of(task), Status::Suspended);
        rig.advance_seconds(5.0);
        fire(&mut rig);
        assert_eq!(rig.tree.status_of(task), Status::Success);
    }

    #[test]
    fn a_task_that_fails_until_the_timer_has_run_out_starts_the_timer_and_then_lets_one_success_through() {
        let attrs = Attrs::new()
            .with("triggerAddress", item("Trigger"))
            .with("timerAddress", item("Timer"))
            .with("timeoutSeconds", 10i64);
        let mut rig = Rig::new();
        assert_eq!(rig.run(Box::new(FailUntilTimeoutAction), attrs.clone()), Status::Failure);
        assert!(matches!(rig.last(ScopeType::Item, "Timer"), Value::Int(_)), "the timer is started");
        rig.advance_seconds(10.0);
        fire(&mut rig);
        assert_eq!(rig.last(ScopeType::Item, "Trigger"), Value::Bool(true));
        assert!(rig.tree.has_reset_request());
        // after the reset the trigger is on: a success, and the trigger off
        let mut rig2 = Rig::new();
        rig2.post(ScopeType::Item, "Trigger", Value::Bool(true));
        rig2.post(ScopeType::Item, "Timer", Value::Int(7));
        assert_eq!(rig2.run(Box::new(FailUntilTimeoutAction), attrs), Status::Success);
        assert_eq!(rig2.last(ScopeType::Item, "Trigger"), Value::Bool(false));
    }

    #[test]
    fn a_task_that_blocks_while_the_timer_runs_succeeds_when_it_has_run_out() {
        let attrs = Attrs::new()
            .with("timerAddress", item("Timer"))
            .with("timeoutSeconds", 4i64)
            .with("maxRandomTimeoutSeconds", 0i64);
        let mut rig = Rig::new();
        let task = rig.tree.add_task(Box::new(BlockWhileTimeout), attrs);
        rig.tree.start_root_task(task);
        rig.tick().unwrap();
        assert_eq!(rig.tree.status_of(task), Status::Suspended);
        rig.advance_seconds(4.0);
        fire(&mut rig);
        assert_eq!(rig.tree.status_of(task), Status::Success);
    }

    #[test]
    fn a_random_timeout_is_a_whole_number_of_seconds_between_the_two() {
        let attrs = Attrs::new()
            .with("timerAddress", item("Timer"))
            .with("timeoutSeconds", 4i64)
            .with("maxRandomTimeoutSeconds", 10i64);
        let mut rig = Rig::new();
        rig.world.set_numbers(&[0.999]);
        let task = rig.tree.add_task(Box::new(BlockWhileTimeout), attrs);
        rig.tree.start_root_task(task);
        rig.tick().unwrap();
        let now = rig.shared.now;
        assert_eq!(rig.shared.timers.next_due(), Some(now + crate::env::seconds_to_ticks(10.0)));
    }

    #[test]
    fn a_timer_is_stopped_and_the_channel_told() {
        let mut rig = Rig::new();
        let attrs = Attrs::new().with("timerAddress", item("Timer"));
        assert_eq!(rig.run(Box::new(ClearTimer), attrs.clone()), Status::Success, "no timer");
        let a = Address::new(ScopeType::Item, "Timer");
        {
            let mut cx = crate::tree::Cx {
                core: &mut rig.tree.core,
                ctx: &mut rig.ctx,
                shared: &mut rig.shared,
                world: &mut rig.world,
            };
            cx.start_timer(60.0, &a);
        }
        assert!(matches!(rig.last(ScopeType::Item, "Timer"), Value::Int(_)));
        assert_eq!(rig.run(Box::new(ClearTimer), attrs), Status::Success);
        assert_eq!(rig.last(ScopeType::Item, "Timer"), Value::None);
    }
}
