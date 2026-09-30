//! `behaviors/trees/reinforcements.py`.

use crate::constants::blackboardchannels::{
    last_reinforcement_request_coordinate_address, warp_scrambled_address,
};
use crate::tree::BehaviorTree;

use super::builder::{Node, sequence};

/// `CreateRequestReinforcements`.
pub fn create_request_reinforcements() -> Node {
    Node::class("ForceFailure").named("Reinforcements").add(
        sequence()
            .add(Node::class("AreReinforcementsAuthorizedMonitor"))
            .add(Node::class("AreReinforcementAvailableMonitor"))
            .add(Node::class("CallInReinforcementsAction")),
    )
}

/// `CreateReinforcementTree`.
pub fn create_reinforcement_tree() -> BehaviorTree {
    create_request_reinforcements().into_tree()
}

/// `CreateReinforceLocationBehavior`.
pub fn create_reinforce_location_behavior() -> Node {
    let location = last_reinforcement_request_coordinate_address();
    sequence()
        .named("Reinforce Group")
        .add(
            Node::class("IsBlackboardValueNotNone")
                .named("Do we have a location to reinforce?")
                .with("valueAddress", &location),
        )
        .add(
            Node::class("BlackboardValueUpdatedRecentlyCondition")
                .named("Posted in the last minute?")
                .with("address", &location)
                .with("timeInSeconds", 60),
        )
        .add(
            Node::class("IsLocationWithinWarpDistance")
                .named("Far enough to warp?")
                .with("coordinateAddress", &location),
        )
        .add(
            Node::class("WarpToNewLocation")
                .named("Warp drive active!")
                .with("locationMessage", &location)
                .with("warpScrambledAddress", warp_scrambled_address()),
        )
}
