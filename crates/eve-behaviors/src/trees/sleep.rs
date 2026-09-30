//! `behaviors/trees/sleep.py`.

use crate::tree::BehaviorTree;

use super::builder::Node;

/// `CreateSleepBehaviorTree`: does nothing for ever.
pub fn create_sleep_behavior_tree() -> BehaviorTree {
    Node::class("WaitAction").named("Sleep forever my pretty").into_tree()
}
