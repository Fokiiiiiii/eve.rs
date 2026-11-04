//! `behaviors/trees/regroup.py`: an entity that is not with the leader of its group goes to it.

use crate::blackboards::{Address, ScopeType};
use crate::constants::blackboardchannels::warp_scrambled_address;

use super::builder::{Node, selector, sequence};

/// `GROUP_LEADER_ADDRESS`.
pub fn group_leader_address() -> Address {
    Address::new(ScopeType::EntityGroup, "GROUP_LEADER")
}

/// `REGROUP_LOCATION_ADDRESS`.
pub fn regroup_location_address() -> Address {
    Address::new(ScopeType::EntityGroup, "REGROUP_LOCATION")
}

/// `CreateReGroupBehavior`.
pub fn create_re_group_behavior() -> Node {
    sequence()
        .named("ReGroup root")
        .add(Node::class("SelectGroupLeaderAction").with("groupLeaderIdAddress", group_leader_address()))
        .add(Node::class("Not").add(
            Node::class("IsBallPresentInMyBubbleCondition").with("ballIdAddress", group_leader_address()),
        ))
        .add(create_get_ball_position_after_warp_behavior(
            &group_leader_address(),
            &regroup_location_address(),
        ))
        .add(
            Node::class("WarpToNewLocation")
                .with("locationMessage", regroup_location_address())
                .with("warpScrambledAddress", warp_scrambled_address()),
        )
}

/// `CreateGetBallPositionAfterWarpBehavior(ballIdAddress, locationAddress)`.
pub fn create_get_ball_position_after_warp_behavior(
    ball_id_address: &Address,
    location_address: &Address,
) -> Node {
    selector()
        .named("Get Position After Warp")
        .add(
            sequence().add(Node::class("IsBallInWarpCondition").with("ballIdAddress", ball_id_address)).add(
                Node::class("GetBallGotoPointAction")
                    .with("ballIdAddress", ball_id_address)
                    .with("gotoPointAddress", location_address),
            ),
        )
        .add(
            Node::class("GetBallWarpToLocation")
                .with("ballIdAddress", ball_id_address)
                .with("locationAddress", location_address),
        )
}
