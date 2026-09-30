//! `behaviors/trees/tanking.py`.

use super::builder::Node;

/// `CreateTankingBehavior`.
pub fn create_tanking_behavior() -> Node {
    Node::class("ActiveTank").named("Active Tanking")
}
