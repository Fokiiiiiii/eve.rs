//! `behaviors/trees/explore.py`: exploring, for the groups of interest, the dungeons and the archetypes an entity is told.

use crate::blackboards::{Address, ScopeType};
use crate::value::Value;

use super::builder::{Node, selector, sequence};
use super::travel::{create_warp_to_new_location_behavior, warp_to_location_group};

/// `RESET_TIMER`.
pub fn reset_timer() -> Address {
    Address::new(ScopeType::EntityGroup, "EXPLORATION_RESET_TIMER")
}

/// The arguments of `CreateExplorationBehavior`: what is not given is nothing of interest, and 1800 seconds.
#[derive(Debug, Clone)]
pub struct Exploration {
    pub groups_of_interest: Value,
    pub dungeons_of_interest: Value,
    pub dungeon_archetypes_of_interest: Value,
    pub exploration_interval_seconds: i64,
}

impl Default for Exploration {
    fn default() -> Self {
        Exploration {
            groups_of_interest: Value::List(Vec::new()),
            dungeons_of_interest: Value::List(Vec::new()),
            dungeon_archetypes_of_interest: Value::List(Vec::new()),
            exploration_interval_seconds: 1800,
        }
    }
}

/// `CreateExplorationBehavior`.
pub fn create_exploration_behavior(args: Exploration) -> Node {
    // `groupsOfInterest = groupsOfInterest or []`
    let or_empty = |value: &Value| if value.truthy() { value.clone() } else { Value::List(Vec::new()) };
    let find_new_exploration_point = Node::class("FindPointToExplore")
        .named("Find Point To Explore")
        .with("locationMessage", warp_to_location_group())
        .with("groupsOfInterest", or_empty(&args.groups_of_interest))
        .with("dungeonsOfInterest", or_empty(&args.dungeons_of_interest))
        .with("dungeonArchetypesOfInterest", or_empty(&args.dungeon_archetypes_of_interest))
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
                .with("timeoutSeconds", args.exploration_interval_seconds)
                .with("timerAddress", reset_timer()),
        )
        .add(find_new_exploration_point)
        .add(
            Node::class("IsCoordinateInSameBubbleCondition")
                .named("Force Fail So We Warp")
                .with("coordinateAddress", warp_to_location_group()),
        );
    selector()
        .named("Exploration")
        .add(should_explore)
        .add(create_warp_to_new_location_behavior(&warp_to_location_group()))
}
