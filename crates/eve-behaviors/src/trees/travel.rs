//! `behaviors/trees/travel.py`: warping about.

use crate::blackboards::{Address, ScopeType};
use crate::constants::blackboardchannels::warp_scrambled_address;

use super::builder::{Node, sequence};

/// `WARP_TO_LOCATION_GROUP`.
pub fn warp_to_location_group() -> Address {
    Address::new(ScopeType::EntityGroup, "WARP_TO_LOCATION_GROUP")
}

/// `WARP_TO_LOCATION_ENTITY`.
pub fn warp_to_location_entity() -> Address {
    Address::new(ScopeType::Item, "WARP_TO_LOCATION_ENTITY")
}

/// `CreateWarpToNewLocationBehavior(warpToAddress)`.
pub fn create_warp_to_new_location_behavior(warp_to_address: &Address) -> Node {
    sequence()
        .named("Warp To New Location")
        .add(
            Node::class("BlackboardMessageMonitor")
                .named("Reset On Warp Location Message")
                .with("messageAddress", warp_to_address),
        )
        .add(Node::class("IsLocationWithinWarpDistance").with("coordinateAddress", warp_to_address))
        .add(
            Node::class("WarpToNewLocation")
                .named("Do Warp")
                .with("locationMessage", warp_to_address)
                .with("warpScrambledAddress", warp_scrambled_address()),
        )
}

/// `CreateWarpAndUnspawnBehavior(warpToAddress)`.
pub fn create_warp_and_unspawn_behavior(warp_to_address: &Address) -> Node {
    sequence()
        .named("Warp to random location and unspawn")
        .add(Node::class("GetRandomDeepSpacePosition").with("locationAddress", warp_to_address))
        .add(create_warp_to_new_location_behavior(warp_to_address))
        .add(Node::class("UnspawnSelf"))
}
