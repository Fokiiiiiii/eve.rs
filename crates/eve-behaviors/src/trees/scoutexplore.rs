//! `behaviors/trees/scoutexplore.py`: a scout that explores the stargates, stations, customs offices and belts of a system, and the dungeons that
//! are worth it.

use crate::blackboards::{Address, ScopeType};
use crate::constants::blackboardchannels::warp_scrambled_address;
use crate::consts::*;
use crate::value::Value;

use super::builder::{Node, int_list, selector, sequence};

/// `WARP_TO_LOCATION_GROUP`.
pub fn warp_to_location_group() -> Address {
    Address::new(ScopeType::EntityGroup, "WARP_TO_LOCATION_GROUP")
}

/// `RESET_TIMER`.
pub fn reset_timer() -> Address {
    Address::new(ScopeType::EntityGroup, "EXPLORATION_RESET_TIMER")
}

pub const LANDMARK_ARCHETYPE: i64 = 2;
pub const AJS1: i64 = 5460;
pub const UNIDENTIFIED_STRUCTURE: i64 = 5610;
pub const DUNGEON_WORMHOLE: i64 = 5643;

/// `CreateExplorationBehavior`.
pub fn create_exploration_behavior() -> Node {
    let find_new_exploration_point = Node::class("FindPointToExplore")
        .named("Find Point To Explore")
        .with("locationMessage", warp_to_location_group())
        .with(
            "groupsOfInterest",
            int_list(&[GROUP_STARGATE, GROUP_STATION, GROUP_PLANETARY_CUSTOMS_OFFICES, GROUP_ASTEROID_BELT]),
        )
        .with("dungeonsOfInterest", int_list(&[AJS1, UNIDENTIFIED_STRUCTURE, DUNGEON_WORMHOLE]))
        .with("dungeonArchetypesOfInterest", Value::List(Vec::new()))
        .with("pickSpecificChance", 0.3)
        .with("shouldConsiderUnspawnedDungeons", false);
    let should_explore = sequence()
        .named("Should Change Location")
        .add(
            Node::class("BlackboardMessageMonitor")
                .named("Reset On Timer Change")
                .with("messageAddress", reset_timer()),
        )
        .add(
            Node::class("StartTimerBlocks")
                .named("Exploration Reset Timer")
                .with("timeoutSeconds", 360)
                .with("timerAddress", reset_timer()),
        )
        .add(find_new_exploration_point)
        .add(
            Node::class("IsCoordinateInSameBubbleCondition")
                .named("Force Fail So We Warp")
                .with("coordinateAddress", warp_to_location_group()),
        );
    let warp_to_new_location = sequence()
        .named("Warp To New Location")
        .add(
            Node::class("BlackboardMessageMonitor")
                .named("Reset On Warp Location Message")
                .with("messageAddress", warp_to_location_group()),
        )
        .add(
            Node::class("Not").add(
                Node::class("IsCoordinateInSameBubbleCondition")
                    .with("coordinateAddress", warp_to_location_group()),
            ),
        )
        .add(
            Node::class("WarpToNewLocation")
                .named("Do Warp")
                .with("locationMessage", warp_to_location_group())
                .with("warpScrambledAddress", warp_scrambled_address()),
        );
    selector().named("Exploration").add(should_explore).add(warp_to_new_location)
}
