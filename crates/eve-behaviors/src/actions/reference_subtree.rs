//! `behaviors/actions/reference_subtree.py`.

use crate::blackboards::TaskId;
use crate::composites::composite;
use crate::decorators::decorator;
use crate::tree::{Task, TaskCtx};

/// `ReferenceSubtree`: a decorator the loader replaces with the tree it refers to (a tree that is loaded from data never has one).
#[derive(Debug, Default)]
pub struct ReferenceSubtree;

impl Task for ReferenceSubtree {
    fn type_name(&self) -> &'static str {
        "ReferenceSubtree"
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        decorator::on_enter(t);
    }

    fn clean_up(&mut self, t: &mut TaskCtx<'_, '_>) {
        composite::clean_up(t);
    }

    fn do_sub_task_completed(&mut self, t: &mut TaskCtx<'_, '_>, sub: TaskId) {
        decorator::do_sub_task_completed(t, sub);
    }
}
