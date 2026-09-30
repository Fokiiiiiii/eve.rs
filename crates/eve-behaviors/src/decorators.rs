//! Tasks that run one task and change how it ends or when it runs (`behaviors/decorators/*`): the base `Decorator` (the helpers `decorator::*`),
//! the modifiers (`Not`, `ForceFailure`, `ForceSuccess`, `Uninterruptible`, `Invulnerable`, `Cloaked`, `OneShot`), the repeater
//! (`RepeatWhileFailing`) and the timers (`CooldownTimer`, `RandomCooldownTimer`).

use crate::blackboards::{MessageData, TaskId};
use crate::composites::composite;
use crate::tree::{BehaviorError, Status, Task, TaskCtx};
use crate::value::Value;

/// `Decorator`.
pub mod decorator {
    use super::*;

    /// `Decorator.OnEnter`: raises when there is nothing to decorate or more than one task.
    pub fn on_enter(t: &mut TaskCtx<'_, '_>) {
        match t.sub_task_count() {
            0 => {
                t.raise(BehaviorError::NoSubTasks);
                return;
            }
            1 => {}
            _ => {
                t.raise(BehaviorError::TooManySubTasks);
                return;
            }
        }
        t.set_status_to_suspended();
        let decorated = t.sub_tasks()[0];
        t.start_task(decorated);
    }

    /// `Decorator.DoSubTaskCompleted`: it ends as its task did.
    pub fn do_sub_task_completed(t: &mut TaskCtx<'_, '_>, sub: TaskId) {
        let status = t.sub_task_status(sub);
        t.set_status(status);
    }
}

macro_rules! decorator_base {
    () => {
        fn clean_up(&mut self, t: &mut TaskCtx<'_, '_>) {
            composite::clean_up(t);
        }
    };
}

/// Succeeds when its task fails, fails when it succeeds.
#[derive(Debug, Default)]
pub struct Not;

impl Task for Not {
    fn type_name(&self) -> &'static str {
        "Not"
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        decorator::on_enter(t);
    }

    decorator_base!();

    fn do_sub_task_completed(&mut self, t: &mut TaskCtx<'_, '_>, sub: TaskId) {
        match t.sub_task_status(sub) {
            Status::Success => t.set_status(Status::Failure),
            Status::Failure => t.set_status(Status::Success),
            _ => {}
        }
    }
}

/// Fails whatever its task does.
#[derive(Debug, Default)]
pub struct ForceFailure;

impl Task for ForceFailure {
    fn type_name(&self) -> &'static str {
        "ForceFailure"
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        decorator::on_enter(t);
    }

    decorator_base!();

    fn do_sub_task_completed(&mut self, t: &mut TaskCtx<'_, '_>, _sub: TaskId) {
        t.set_status(Status::Failure);
    }
}

/// Succeeds whatever its task does.
#[derive(Debug, Default)]
pub struct ForceSuccess;

impl Task for ForceSuccess {
    fn type_name(&self) -> &'static str {
        "ForceSuccess"
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        decorator::on_enter(t);
    }

    decorator_base!();

    fn do_sub_task_completed(&mut self, t: &mut TaskCtx<'_, '_>, _sub: TaskId) {
        t.set_status(Status::Success);
    }
}

/// Holds the tree off a reset while its task runs.
#[derive(Debug, Default)]
pub struct Uninterruptible;

impl Task for Uninterruptible {
    fn type_name(&self) -> &'static str {
        "Uninterruptible"
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        decorator::on_enter(t);
        if t.faulted() {
            return;
        }
        t.set_status(Status::Blocking);
        t.block_reset();
    }

    fn on_exit(&mut self, t: &mut TaskCtx<'_, '_>) {
        t.unblock_reset();
    }

    decorator_base!();

    fn do_sub_task_completed(&mut self, t: &mut TaskCtx<'_, '_>, sub: TaskId) {
        decorator::do_sub_task_completed(t, sub);
    }
}

/// Makes the ship invulnerable (for `reason`) while its task runs.
#[derive(Debug, Default)]
pub struct Invulnerable;

impl Invulnerable {
    fn cancel(t: &mut TaskCtx<'_, '_>) {
        let (Some(ball), Some(reason)) = (t.my_item_id(), t.required_string("reason")) else { return };
        t.world().cancel_current_invulnerability(ball, &reason);
    }
}

impl Task for Invulnerable {
    fn type_name(&self) -> &'static str {
        "Invulnerable"
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        decorator::on_enter(t);
        if t.faulted() {
            return;
        }
        let (Some(ball), Some(reason)) = (t.my_item_id(), t.required_string("reason")) else { return };
        t.world().make_invulnerable_permanently(ball, &reason);
    }

    fn on_exit(&mut self, t: &mut TaskCtx<'_, '_>) {
        Self::cancel(t);
    }

    fn clean_up(&mut self, t: &mut TaskCtx<'_, '_>) {
        if !t.is_invalid() {
            Self::cancel(t);
        }
        composite::clean_up(t);
    }

    fn do_sub_task_completed(&mut self, t: &mut TaskCtx<'_, '_>, sub: TaskId) {
        decorator::do_sub_task_completed(t, sub);
    }
}

/// Cloaks the ship while its task runs.
#[derive(Debug, Default)]
pub struct Cloaked;

impl Task for Cloaked {
    fn type_name(&self) -> &'static str {
        "Cloaked"
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        decorator::on_enter(t);
        if t.faulted() {
            return;
        }
        if let Some(item) = t.my_item_id() {
            t.world().cloak_ship(item);
        }
    }

    fn on_exit(&mut self, t: &mut TaskCtx<'_, '_>) {
        if let Some(item) = t.my_item_id() {
            t.world().uncloak_ship(item);
        }
    }

    fn clean_up(&mut self, t: &mut TaskCtx<'_, '_>) {
        if !t.is_invalid()
            && let Some(item) = t.my_item_id()
        {
            t.world().uncloak_ship(item);
        }
        composite::clean_up(t);
    }

    fn do_sub_task_completed(&mut self, t: &mut TaskCtx<'_, '_>, sub: TaskId) {
        decorator::do_sub_task_completed(t, sub);
    }
}

/// Runs its task once: the flag at `oneShotTriggeredFlagAddress` says it has, and the next time it succeeds at once.
#[derive(Debug, Default)]
pub struct OneShot;

impl Task for OneShot {
    fn type_name(&self) -> &'static str {
        "OneShot"
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        let Some(flag) = t.required_address("oneShotTriggeredFlagAddress") else { return };
        if t.get_last_blackboard_value(&flag).truthy() {
            t.set_status_to_success();
        } else {
            // `set_one_shot_triggered`
            if !t.get_last_blackboard_value(&flag).truthy() {
                t.send_blackboard_value(&flag, Value::Bool(true));
            }
            decorator::on_enter(t);
        }
    }

    decorator_base!();

    fn do_sub_task_completed(&mut self, t: &mut TaskCtx<'_, '_>, sub: TaskId) {
        decorator::do_sub_task_completed(t, sub);
    }
}

/// Runs its task again, up to `maxRepetitions` times, while it fails.
#[derive(Debug, Default)]
pub struct RepeatWhileFailing {
    repetitions: i64,
}

impl Task for RepeatWhileFailing {
    fn type_name(&self) -> &'static str {
        "RepeatWhileFailing"
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        decorator::on_enter(t);
        if t.faulted() {
            return;
        }
        if let Some(max) = t.required_int("maxRepetitions") {
            self.repetitions = max;
        }
    }

    decorator_base!();

    fn do_sub_task_completed(&mut self, t: &mut TaskCtx<'_, '_>, sub: TaskId) {
        if t.sub_task_status(sub) == Status::Success {
            t.set_status_to_success();
        } else {
            self.repetitions -= 1;
            if self.repetitions > 0 {
                t.clean_up_task(sub);
                t.start_task_next_tick(sub);
            } else {
                t.set_status_to_failed();
            }
        }
    }
}

/// The base of the timers: fails at once while the timer at `timerAddress` runs, else runs its task and starts the timer.
mod cooldown {
    use super::*;

    /// The handler number of `OnTimeout`.
    pub const ON_TIMEOUT: u16 = 0;

    pub fn on_enter(t: &mut TaskCtx<'_, '_>, timeout_seconds: fn(&mut TaskCtx<'_, '_>) -> Option<f64>) {
        t.set_status_to_failed();
        let Some(address) = t.required_address("timerAddress") else { return };
        let timer_value = t.get_last_blackboard_value(&address);
        if t.faulted() {
            return;
        }
        if timer_value.is_none() {
            decorator::on_enter(t);
            if t.faulted() {
                return;
            }
            let Some(seconds) = timeout_seconds(t) else { return };
            t.start_timer(seconds, &address);
        }
        t.subscribe_to_blackboard(&address, ON_TIMEOUT);
    }

    /// `OnTimeout`: the timer ran out and the task is asked to start over.
    pub fn on_blackboard_message(t: &mut TaskCtx<'_, '_>, handler: u16, message: Option<&MessageData>) {
        if handler == ON_TIMEOUT && message.is_some_and(|m| m.value.is_none()) && !t.is_invalid() {
            t.request_reset();
        }
    }

    pub fn clean_up(t: &mut TaskCtx<'_, '_>) {
        if !t.is_invalid()
            && let Some(address) = t.required_address("timerAddress")
        {
            t.unsubscribe_to_blackboard(&address, ON_TIMEOUT);
        }
        composite::clean_up(t);
    }
}

/// A task that can be run again once a timer has run out.
#[derive(Debug, Default)]
pub struct CooldownTimer;

impl Task for CooldownTimer {
    fn type_name(&self) -> &'static str {
        "CooldownTimer"
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        cooldown::on_enter(t, |t| t.required_float("timeoutSeconds"));
    }

    fn clean_up(&mut self, t: &mut TaskCtx<'_, '_>) {
        cooldown::clean_up(t);
    }

    fn do_sub_task_completed(&mut self, t: &mut TaskCtx<'_, '_>, sub: TaskId) {
        decorator::do_sub_task_completed(t, sub);
    }

    fn on_blackboard_message(
        &mut self,
        t: &mut TaskCtx<'_, '_>,
        handler: u16,
        _channel: &str,
        message: Option<&MessageData>,
    ) {
        cooldown::on_blackboard_message(t, handler, message);
    }
}

/// `CooldownTimer` whose time is a whole number of seconds between `minTimeoutSeconds` and `maxTimeoutSeconds`.
#[derive(Debug, Default)]
pub struct RandomCooldownTimer;

impl Task for RandomCooldownTimer {
    fn type_name(&self) -> &'static str {
        "RandomCooldownTimer"
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        cooldown::on_enter(t, |t| {
            let (min, max) = (t.required_int("minTimeoutSeconds")?, t.required_int("maxTimeoutSeconds")?);
            Some(t.world().random_int(min, max) as f64)
        });
    }

    fn clean_up(&mut self, t: &mut TaskCtx<'_, '_>) {
        cooldown::clean_up(t);
    }

    fn do_sub_task_completed(&mut self, t: &mut TaskCtx<'_, '_>, sub: TaskId) {
        decorator::do_sub_task_completed(t, sub);
    }

    fn on_blackboard_message(
        &mut self,
        t: &mut TaskCtx<'_, '_>,
        handler: u16,
        _channel: &str,
        message: Option<&MessageData>,
    ) {
        cooldown::on_blackboard_message(t, handler, message);
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::blackboards::{Address, ScopeType};
    use crate::env::{RecordingWorld, SECOND};
    use crate::tree::Attrs;
    use crate::tree::tests::Rig;

    /// A decorator of the type, with attributes, around one leaf.
    fn decorated(
        rig: &mut Rig,
        node: Box<dyn Task>,
        attributes: Attrs,
        result: Status,
        ticks: u32,
    ) -> (TaskId, TaskId) {
        let leaf = rig.leaf("a", result, ticks);
        let decorator = rig.tree.add_task(node, attributes);
        rig.tree.add_sub_task(decorator, leaf);
        rig.tree.start_root_task(decorator);
        (decorator, leaf)
    }

    #[test]
    fn a_decorator_with_no_task_or_with_two_raises() {
        let mut rig = Rig::new();
        let root = rig.tree.add_task(Box::new(Not), Attrs::new());
        rig.tree.start_root_task(root);
        assert_eq!(rig.tick(), Err(BehaviorError::NoSubTasks));

        let mut rig = Rig::new();
        let (a, b) = (rig.leaf("a", Status::Success, 0), rig.leaf("b", Status::Success, 0));
        let root = rig.tree.add_task(Box::new(ForceSuccess), Attrs::new());
        rig.tree.add_sub_task(root, a);
        rig.tree.add_sub_task(root, b);
        rig.tree.start_root_task(root);
        assert_eq!(rig.tick(), Err(BehaviorError::TooManySubTasks));
    }

    #[test]
    fn not_turns_success_into_failure_and_failure_into_success() {
        for (result, wanted) in [(Status::Success, Status::Failure), (Status::Failure, Status::Success)] {
            let mut rig = Rig::new();
            let (root, _) = decorated(&mut rig, Box::new(Not), Attrs::new(), result, 0);
            rig.tick().unwrap();
            assert_eq!(rig.tree.status_of(root), wanted);
        }
    }

    #[test]
    fn force_success_and_force_failure_end_as_they_say() {
        for (result, wanted, force_failure) in
            [(Status::Failure, Status::Success, false), (Status::Success, Status::Failure, true)]
        {
            let mut rig = Rig::new();
            let node: Box<dyn Task> =
                if force_failure { Box::new(ForceFailure) } else { Box::new(ForceSuccess) };
            let (root, _) = decorated(&mut rig, node, Attrs::new(), result, 0);
            rig.tick().unwrap();
            assert_eq!(rig.tree.status_of(root), wanted);
        }
    }

    #[test]
    fn an_uninterruptible_task_holds_a_reset_off_until_its_task_is_done() {
        let mut rig = Rig::new();
        let (root, _) = decorated(&mut rig, Box::new(Uninterruptible), Attrs::new(), Status::Success, 2);
        rig.tick().unwrap();
        assert_eq!(rig.tree.status_of(root), Status::Blocking);
        assert!(rig.tree.is_reset_blocked());
        rig.tree.request_reset(&mut rig.ctx, &mut rig.shared, &mut rig.world);
        rig.tick().unwrap();
        assert!(rig.tree.has_reset_request(), "the request waits");
        rig.tick().unwrap();
        assert_eq!(rig.tree.status_of(root), Status::Success);
        assert!(!rig.tree.is_reset_blocked(), "the exit lets go");
    }

    #[test]
    fn an_invulnerable_task_makes_the_ship_invulnerable_while_it_runs_and_takes_it_back_when_it_is_cleaned_up_early()
     {
        let mut rig = Rig::new();
        let (root, _) = decorated(
            &mut rig,
            Box::new(Invulnerable),
            Attrs::new().with("reason", "test"),
            Status::Success,
            1,
        );
        rig.tick().unwrap();
        assert_eq!(rig.world.take_calls(), ["make_invulnerable_permanently(1, test)"]);
        rig.tick().unwrap();
        assert_eq!(rig.world.take_calls(), ["cancel_current_invulnerability(1, test)"], "when it exits");
        assert_eq!(rig.tree.status_of(root), Status::Success);

        // a tree that is released while the task runs takes the invulnerability back once
        let mut rig = Rig::new();
        decorated(&mut rig, Box::new(Invulnerable), Attrs::new().with("reason", "test"), Status::Success, 5);
        rig.tick().unwrap();
        rig.world.take_calls();
        rig.tree.release(&mut rig.ctx, &mut rig.shared, &mut rig.world);
        assert_eq!(rig.world.take_calls(), ["cancel_current_invulnerability(1, test)"]);
    }

    #[test]
    fn a_cloaked_task_cloaks_the_ship_while_it_runs() {
        let mut rig = Rig::new();
        decorated(&mut rig, Box::new(Cloaked), Attrs::new(), Status::Success, 1);
        rig.tick().unwrap();
        assert_eq!(rig.world.take_calls(), ["cloak_ship(1)"]);
        rig.tick().unwrap();
        assert_eq!(rig.world.take_calls(), ["uncloak_ship(1)"]);

        let mut rig = Rig::new();
        decorated(&mut rig, Box::new(Cloaked), Attrs::new(), Status::Success, 5);
        rig.tick().unwrap();
        rig.world.take_calls();
        rig.tree.release(&mut rig.ctx, &mut rig.shared, &mut rig.world);
        assert_eq!(rig.world.take_calls(), ["uncloak_ship(1)"]);
    }

    fn flag() -> Address {
        Address::new(ScopeType::Item, "Done")
    }

    #[test]
    fn a_one_shot_task_runs_its_task_once_and_then_succeeds_at_once() {
        let mut rig = Rig::new();
        let (root, _) = decorated(
            &mut rig,
            Box::new(OneShot),
            Attrs::new().with("oneShotTriggeredFlagAddress", flag().to_value()),
            Status::Failure,
            0,
        );
        rig.tick().unwrap();
        assert_eq!(rig.take_log(), ["a:enter", "a:update", "a:exit"]);
        assert_eq!(rig.tree.status_of(root), Status::Failure);
        // the tree resets and the task starts again: the flag is up
        rig.tick().unwrap();
        assert_eq!(rig.take_log(), ["a:cleanup"]);
        assert_eq!(rig.tree.status_of(root), Status::Success);
        let mut cx = crate::tree::Cx {
            core: &mut rig.tree.core,
            ctx: &mut rig.ctx,
            shared: &mut rig.shared,
            world: &mut rig.world,
        };
        assert_eq!(cx.last_blackboard_value(&flag()), Value::Bool(true));
    }

    #[test]
    fn a_task_repeated_while_failing_runs_up_to_the_number_of_repetitions_and_stops_at_a_success() {
        let mut rig = Rig::new();
        let (root, _) = decorated(
            &mut rig,
            Box::new(RepeatWhileFailing::default()),
            Attrs::new().with("maxRepetitions", 3i64),
            Status::Failure,
            0,
        );
        rig.tick().unwrap();
        assert_eq!(rig.tree.status_of(root), Status::Suspended, "the second try waits for the next tick");
        rig.tick().unwrap();
        rig.tick().unwrap();
        assert_eq!(rig.tree.status_of(root), Status::Failure);
        assert_eq!(rig.take_log().iter().filter(|l| l.as_str() == "a:enter").count(), 3);

        let mut rig = Rig::new();
        let (root, _) = decorated(
            &mut rig,
            Box::new(RepeatWhileFailing::default()),
            Attrs::new().with("maxRepetitions", 3i64),
            Status::Success,
            0,
        );
        rig.tick().unwrap();
        assert_eq!(rig.tree.status_of(root), Status::Success);
    }

    fn timer() -> Address {
        Address::new(ScopeType::Item, "Cooldown")
    }

    /// The system's part of a timer running out: the timers due, and what they tell the trees.
    fn timers_run_out(rig: &mut Rig, seconds: i64) {
        rig.shared.now += seconds * SECOND;
        rig.shared.fire_due_timers();
        for delivery in std::mem::take(&mut rig.shared.pending) {
            rig.tree.deliver(&mut rig.ctx, &mut rig.shared, &mut rig.world, &delivery).unwrap();
        }
    }

    #[test]
    fn a_cooldown_timer_that_finds_its_timer_running_fails_at_once_without_running_its_task() {
        let mut rig = Rig::new();
        let attributes = Attrs::new().with("timerAddress", timer().to_value()).with("timeoutSeconds", 30.0);
        let (root, _) = decorated(&mut rig, Box::new(CooldownTimer), attributes, Status::Success, 0);
        {
            let mut cx = crate::tree::Cx {
                core: &mut rig.tree.core,
                ctx: &mut rig.ctx,
                shared: &mut rig.shared,
                world: &mut rig.world,
            };
            cx.send_blackboard_value(&timer(), Value::Int(99));
        }
        rig.tick().unwrap();
        assert_eq!(rig.take_log(), Vec::<String>::new(), "the task was not run");
        assert_eq!(rig.tree.status_of(root), Status::Failure);
        assert_eq!(rig.shared.timers.active_count(), 0, "and no timer was started");
    }

    #[test]
    fn a_cooldown_timer_runs_its_task_and_starts_the_timer_and_asks_for_a_reset_when_it_runs_out() {
        let mut rig = Rig::new();
        let attributes = Attrs::new().with("timerAddress", timer().to_value()).with("timeoutSeconds", 30.0);
        let cooldown = rig.tree.add_task(Box::new(CooldownTimer), attributes);
        let a = rig.leaf("a", Status::Success, 0);
        rig.tree.add_sub_task(cooldown, a);
        let long = rig.leaf("b", Status::Success, 1000);
        let root = rig.sequence(&[cooldown, long]);
        rig.tree.start_root_task(root);
        rig.tick().unwrap();
        assert_eq!(rig.take_log(), ["a:enter", "a:update", "a:exit", "b:enter", "b:update"]);
        assert_eq!(rig.shared.timers.active_count(), 1);
        assert!(!rig.tree.has_reset_request());
        // the timer runs out: the cooldown task, still listening, asks for a reset, and the next tick runs its task again
        timers_run_out(&mut rig, 30);
        assert_eq!(rig.shared.timers.active_count(), 0);
        assert!(rig.tree.has_reset_request());
        rig.tick().unwrap();
        assert_eq!(rig.take_log()[..3], ["a:cleanup", "b:cleanup", "a:enter"]);
        assert_eq!(rig.shared.timers.active_count(), 1, "and a new timer runs");
    }

    #[test]
    fn a_random_cooldown_timer_takes_its_time_from_the_dice() {
        let mut rig = Rig::new();
        rig.world = RecordingWorld::with_numbers(&[0.999_999]);
        let attributes = Attrs::new()
            .with("timerAddress", timer().to_value())
            .with("minTimeoutSeconds", 10i64)
            .with("maxTimeoutSeconds", 20i64);
        decorated(&mut rig, Box::new(RandomCooldownTimer), attributes, Status::Success, 0);
        rig.tick().unwrap();
        let now = rig.shared.now;
        assert_eq!(rig.shared.timers.take_due(now + 19 * SECOND), Vec::new());
        assert_eq!(
            rig.shared.timers.take_due(now + 20 * SECOND).len(),
            1,
            "twenty seconds: the top of the range"
        );
    }

    #[test]
    fn a_cooldown_timer_that_is_cleaned_up_stops_listening() {
        let mut rig = Rig::new();
        let attributes = Attrs::new().with("timerAddress", timer().to_value()).with("timeoutSeconds", 30.0);
        decorated(&mut rig, Box::new(CooldownTimer), attributes, Status::Success, 5);
        rig.tick().unwrap();
        rig.tree.release(&mut rig.ctx, &mut rig.shared, &mut rig.world);
        let cx = crate::tree::Cx {
            core: &mut rig.tree.core,
            ctx: &mut rig.ctx,
            shared: &mut rig.shared,
            world: &mut rig.world,
        };
        let channel =
            cx.shared.blackboards.message_channel(crate::blackboards::item_scope(1), "Cooldown").unwrap();
        assert!(channel.observers().is_empty());
    }

    #[test]
    fn an_attribute_that_is_missing_raises() {
        let mut rig = Rig::new();
        decorated(&mut rig, Box::new(CooldownTimer), Attrs::new(), Status::Success, 0);
        assert_eq!(rig.tick(), Err(BehaviorError::TaskAttributeMissing("timerAddress".to_string())));
    }
}
