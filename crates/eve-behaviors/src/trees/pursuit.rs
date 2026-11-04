//! `behaviors/trees/pursuit.py`: an entity that follows the enemy the group has fought, half of the time, by warping to where it is.

use crate::blackboards::{Address, ScopeType};
use crate::constants::blackboardchannels::{group_primary_target, warp_scrambled_address};
use crate::value::Value;

use super::builder::{Node, selector, sequence};
use super::combat::combat_targets_set;
use super::regroup::create_get_ball_position_after_warp_behavior;

/// `PURSUIT_DELAY_TIMER_ADDRESS`.
pub fn pursuit_delay_timer_address() -> Address {
    Address::new(ScopeType::Item, "PURSUIT_DELAY_TIMER")
}

/// `PURSUIT_TARGET_ADDRESS`.
pub fn pursuit_target_address() -> Address {
    Address::new(ScopeType::Item, "PURSUIT_TARGET")
}

/// `PURSUIT_LOCATION_ADDRESS`.
pub fn pursuit_location_address() -> Address {
    Address::new(ScopeType::EntityGroup, "PURSUIT_LOCATION")
}

/// `PURSUIT_CHANCE_ADDRESS`.
pub fn pursuit_chance_address() -> Address {
    Address::new(ScopeType::EntityGroup, "PURSUIT_CHANCE")
}

/// `CreatePursuitBehavior`.
pub fn create_pursuit_behavior() -> Node {
    sequence()
        .named("Enemy Pursuit root")
        .add(
            selector()
                .named("do we have a usable location?")
                .add(
                    sequence()
                        .add(
                            Node::class("BlackboardValueUpdatedRecentlyCondition")
                                .with("address", pursuit_location_address())
                                .with("timeInSeconds", 10),
                        )
                        .add(
                            Node::class("IsBlackboardValueNotNone")
                                .with("valueAddress", pursuit_location_address()),
                        ),
                )
                .add(
                    sequence()
                        .named("pick a location if appropriate?")
                        .add(
                            Node::class("BlackboardValueUpdatedRecentlyCondition")
                                .with("address", group_primary_target())
                                .with("timeInSeconds", 10),
                        )
                        .add(
                            Node::class("CachedRandomChanceCondition")
                                .with("chance", 0.5)
                                .with("randomValueAddress", pursuit_chance_address())
                                .with("cacheTimeInSeconds", 10),
                        )
                        .add(
                            Node::class("BlackboardSetMessageAsNoneAction")
                                .with("messageAddress", pursuit_target_address()),
                        )
                        .add(
                            Node::class("FindTarget")
                                .named("Find A Pursuit Target")
                                .with("selectedTargetAddress", pursuit_target_address())
                                .with("potentialTargetListAddress", combat_targets_set())
                                .with("includedCategories", Value::List(Vec::new()))
                                .with("targetEvaluationFunction", "threat")
                                .with("onlyCheckLocalBubble", false)
                                .with("primaryTargetIdAddress", combat_targets_set()),
                        )
                        .add(create_get_ball_position_after_warp_behavior(
                            &pursuit_target_address(),
                            &pursuit_location_address(),
                        )),
                ),
        )
        .add(
            Node::class("BlockWhileTimeout")
                .with("timerAddress", pursuit_delay_timer_address())
                .with("timeoutSeconds", 4.0),
        )
        .add(
            Node::class("WarpToNewLocation")
                .with("locationMessage", pursuit_location_address())
                .with("warpScrambledAddress", warp_scrambled_address()),
        )
        .add(
            Node::class("BlockWhileTimeout")
                .with("timerAddress", pursuit_delay_timer_address())
                .with("timeoutSeconds", 15.0),
        )
}
