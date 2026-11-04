//! `behaviors/trees/unspawn.py`.

use crate::constants::blackboardchannels::unspawn_entity;

use super::builder::{Node, sequence};
use super::travel::{create_warp_and_unspawn_behavior, warp_to_location_group};

/// `CreateUnspawnBehavior`: when the entity is told to unspawn it warps away and does.
pub fn create_unspawn_behavior() -> Node {
    sequence()
        .named("Unspawn Self")
        .add(Node::class("BlackboardMessageMonitor").with("messageAddress", unspawn_entity()))
        .add(Node::class("IsBlackboardValueTrue").with("valueAddress", unspawn_entity()))
        .add(create_warp_and_unspawn_behavior(&warp_to_location_group()))
}
