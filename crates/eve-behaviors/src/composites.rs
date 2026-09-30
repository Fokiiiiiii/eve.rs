//! Tasks that run other tasks (`behaviors/composites/__init__.py`, `weighted.py`): the base `Composite` (as the helpers `composite::*` that the nodes
//! built on it call where the client calls `super()`), `Sequence`, `PrioritySelector` and `WeightedSelector`.

use crate::blackboards::TaskId;
use crate::evemath::weighted_choice;
use crate::tree::{BehaviorError, Status, Task, TaskCtx};

/// `Composite`.
pub mod composite {
    use super::*;

    /// `Composite.OnEnter`, with the task to start first (`GetInitialTask`): raises when there is nothing to run.
    pub fn on_enter_with(
        t: &mut TaskCtx<'_, '_>,
        initial: impl FnOnce(&mut TaskCtx<'_, '_>) -> Option<TaskId>,
    ) {
        if t.sub_task_count() == 0 {
            t.raise(BehaviorError::NoSubTasks);
            return;
        }
        t.set_status_to_suspended();
        if let Some(first) = initial(t) {
            t.start_task(first);
        }
    }

    /// `Composite.OnEnter`: the first sub task starts.
    pub fn on_enter(t: &mut TaskCtx<'_, '_>) {
        on_enter_with(t, |t| t.sub_tasks().first().copied());
    }

    /// `Composite.CleanUp`: the sub tasks that were started are cleaned up, then the task itself.
    pub fn clean_up(t: &mut TaskCtx<'_, '_>) {
        for sub in t.sub_tasks() {
            if t.sub_task_status(sub) != Status::Invalid {
                t.clean_up_task(sub);
            }
        }
        t.set_status_to_invalid();
    }

    /// `IsLastSubTask`.
    pub fn is_last_sub_task(t: &TaskCtx<'_, '_>, sub: TaskId) -> bool {
        t.sub_tasks().last() == Some(&sub)
    }

    /// `GetNextSubTask`: the one after `sub` (none when `sub` is the last; the client's index error).
    pub fn next_sub_task(t: &TaskCtx<'_, '_>, sub: TaskId) -> Option<TaskId> {
        let subs = t.sub_tasks();
        let at = subs.iter().position(|s| *s == sub)?;
        subs.get(at + 1).copied()
    }

    /// `self.behaviorTree.StartTask(self.GetNextSubTask(task))`.
    pub fn start_next(t: &mut TaskCtx<'_, '_>, sub: TaskId) {
        match next_sub_task(t, sub) {
            Some(next) => t.start_task(next),
            None => t.raise(BehaviorError::Task("a composite has no sub task after its last".to_string())),
        }
    }
}

/// Runs its tasks in turn; fails at the first that fails, succeeds when the last does.
#[derive(Debug, Default)]
pub struct Sequence;

impl Task for Sequence {
    fn type_name(&self) -> &'static str {
        "Sequence"
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        composite::on_enter(t);
    }

    fn clean_up(&mut self, t: &mut TaskCtx<'_, '_>) {
        composite::clean_up(t);
    }

    fn do_sub_task_completed(&mut self, t: &mut TaskCtx<'_, '_>, sub: TaskId) {
        if t.sub_task_status(sub) == Status::Failure {
            t.set_status_to_failed();
        } else if composite::is_last_sub_task(t, sub) {
            t.set_status_to_success();
        } else {
            composite::start_next(t, sub);
        }
    }
}

/// Runs its tasks in turn; succeeds at the first that succeeds, fails when the last does not.
#[derive(Debug, Default)]
pub struct PrioritySelector;

impl Task for PrioritySelector {
    fn type_name(&self) -> &'static str {
        "PrioritySelector"
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        composite::on_enter(t);
    }

    fn clean_up(&mut self, t: &mut TaskCtx<'_, '_>) {
        composite::clean_up(t);
    }

    fn do_sub_task_completed(&mut self, t: &mut TaskCtx<'_, '_>, sub: TaskId) {
        if t.sub_task_status(sub) == Status::Success {
            t.set_status_to_success();
        } else if composite::is_last_sub_task(t, sub) {
            t.set_status_to_failed();
        } else {
            composite::start_next(t, sub);
        }
    }
}

/// Runs one of its tasks, chosen by the weights in `subTaskWeights`, and ends as it does.
#[derive(Debug, Default)]
pub struct WeightedSelector;

impl Task for WeightedSelector {
    fn type_name(&self) -> &'static str {
        "WeightedSelector"
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        composite::on_enter_with(t, |t| {
            let weights: Vec<f64> =
                t.required("subTaskWeights")?.items()?.iter().map(|w| w.as_float().unwrap_or(0.0)).collect();
            let subs = t.sub_tasks();
            if weights.len() < subs.len() {
                t.raise(BehaviorError::WeightsMissing);
                return None;
            }
            if subs.len() < weights.len() {
                t.raise(BehaviorError::TooManyWeights);
                return None;
            }
            let choices: Vec<(TaskId, f64)> = subs.iter().copied().zip(weights).collect();
            weighted_choice(&choices, t.world())
        });
    }

    fn clean_up(&mut self, t: &mut TaskCtx<'_, '_>) {
        composite::clean_up(t);
    }

    fn do_sub_task_completed(&mut self, t: &mut TaskCtx<'_, '_>, sub: TaskId) {
        let status = t.sub_task_status(sub);
        t.set_status(status);
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::env::RecordingWorld;
    use crate::tree::tests::Rig;
    use crate::tree::{Attrs, TaskCtx};
    use crate::value::Value;

    #[test]
    fn a_sequence_whose_last_task_is_still_running_waits_and_then_succeeds() {
        let mut rig = Rig::new();
        let (a, b) = (rig.leaf("a", Status::Success, 0), rig.leaf("b", Status::Success, 2));
        let root = rig.sequence(&[a, b]);
        rig.tree.start_root_task(root);
        rig.tick().unwrap();
        assert_eq!(rig.tree.status_of(root), Status::Suspended);
        rig.tick().unwrap();
        rig.tick().unwrap();
        assert_eq!(rig.tree.status_of(root), Status::Success);
    }

    #[test]
    fn a_selector_that_ends_cleans_up_the_tasks_it_started_when_it_is_reset() {
        let mut rig = Rig::new();
        let (a, b, c) = (
            rig.leaf("a", Status::Failure, 0),
            rig.leaf("b", Status::Success, 0),
            rig.leaf("c", Status::Success, 0),
        );
        let root = rig.selector(&[a, b, c]);
        rig.tree.start_root_task(root);
        rig.tick().unwrap();
        rig.take_log();
        rig.tick().unwrap();
        // the reset cleans up a and b (c never started), then the selector runs again
        assert_eq!(&rig.take_log()[..2], ["a:cleanup", "b:cleanup"]);
    }

    #[test]
    fn a_weighted_selector_runs_the_task_its_weights_and_the_dice_choose_and_ends_as_it_does() {
        let mut rig = Rig::new();
        rig.world = RecordingWorld::with_numbers(&[0.6]);
        let (a, b) = (rig.leaf("a", Status::Success, 0), rig.leaf("b", Status::Failure, 0));
        let root = rig.tree.add_task(
            Box::new(WeightedSelector),
            Attrs::new().with("subTaskWeights", Value::List(vec![Value::Int(1), Value::Int(1)])),
        );
        rig.tree.add_sub_task(root, a);
        rig.tree.add_sub_task(root, b);
        rig.tree.start_root_task(root);
        rig.tick().unwrap();
        assert_eq!(rig.take_log(), ["b:enter", "b:update", "b:exit"]);
        assert_eq!(rig.tree.status_of(root), Status::Failure);
    }

    #[test]
    fn a_weighted_selector_with_too_few_or_too_many_weights_raises() {
        for (weights, wanted) in [
            (vec![Value::Int(1)], BehaviorError::WeightsMissing),
            (vec![Value::Int(1); 3], BehaviorError::TooManyWeights),
        ] {
            let mut rig = Rig::new();
            let (a, b) = (rig.leaf("a", Status::Success, 0), rig.leaf("b", Status::Success, 0));
            let root = rig.tree.add_task(
                Box::new(WeightedSelector),
                Attrs::new().with("subTaskWeights", Value::List(weights)),
            );
            rig.tree.add_sub_task(root, a);
            rig.tree.add_sub_task(root, b);
            rig.tree.start_root_task(root);
            assert_eq!(rig.tick(), Err(wanted));
        }
    }

    #[test]
    fn a_task_that_asks_for_a_next_task_after_its_last_raises() {
        struct Bad;
        impl Task for Bad {
            fn type_name(&self) -> &'static str {
                "Bad"
            }
            fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
                composite::on_enter(t);
            }
            fn do_sub_task_completed(&mut self, t: &mut TaskCtx<'_, '_>, sub: TaskId) {
                composite::start_next(t, sub);
            }
        }
        let mut rig = Rig::new();
        let a = rig.leaf("a", Status::Success, 0);
        let root = rig.tree.add_task(Box::new(Bad), Attrs::new());
        rig.tree.add_sub_task(root, a);
        rig.tree.start_root_task(root);
        assert!(matches!(rig.tick(), Err(BehaviorError::Task(_))));
    }
}
