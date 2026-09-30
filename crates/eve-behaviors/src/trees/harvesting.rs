//! `behaviors/trees/harvesting.py`: an entity that takes an outpost apart.

use crate::blackboards::{Address, ScopeType};
use crate::tree::BehaviorTree;

use super::builder::{Node, int_list, selector, sequence};

/// `SELECTED_HARVEST_TARGET`.
pub fn selected_harvest_target() -> Address {
    Address::new(ScopeType::EntityGroup, "SELECTED_HARVEST_TARGET")
}

/// `SELECTED_HARVEST_LOCATION`.
pub fn selected_harvest_location() -> Address {
    Address::new(ScopeType::Item, "SELECTED_HARVEST_LOCATION")
}

/// `HARVESTABLE_STRUCTURE_SET`.
pub const HARVESTABLE_STRUCTURE_SET: [i64; 7] = [11, 12, 13, 14, 15, 16, 17];
pub const DECONSTRUCTION_EFFECT: &str = "effects.DeconstructionBeam";
pub const HARVEST_EFFECT: &str = "effects.HarvestingBeam";
pub const DECONSTRUCTION_DURATION_MS: f64 = 10000.0;
pub const HARVEST_DURATION_MS: f64 = 10000.0;
pub const EFFECT_DELAY_SEC: f64 = 5.0;

/// `CreateHarvestOutpostBehaviorTree`.
pub fn create_harvest_outpost_behavior_tree() -> BehaviorTree {
    create_harvest_outpost_behavior().into_tree()
}

/// `CreateHarvestOutpostBehavior`.
pub fn create_harvest_outpost_behavior() -> Node {
    sequence()
        .named("Harvest Mechanical Parts")
        .add(
            Node::class("SelectItemByTypes")
                .with("typeIds", int_list(&HARVESTABLE_STRUCTURE_SET))
                .with("selectedItemAddress", selected_harvest_target()),
        )
        .add(
            Node::class("SelectClosestPointOnObject")
                .with("objectIdAddress", selected_harvest_target())
                .with("closestPointAddress", selected_harvest_location()),
        )
        .add(
            selector()
                .add(
                    Node::class("IsCoordinateWithinDistance")
                        .with("coordinateAddress", selected_harvest_location())
                        .with("maxDistance", 8000.0),
                )
                .add(
                    Node::class("ApproachCoordinatesAction")
                        .with("coordinateAddress", selected_harvest_location())
                        .with("notifyRange", 5000.0)
                        .with("blocking", true),
                ),
        )
        .add(
            sequence()
                .add(Node::class("FullStopAction"))
                .add(
                    Node::class("PlayOneShotStretchEffectAction")
                        .with("effectDuration", DECONSTRUCTION_DURATION_MS)
                        .with("effectName", DECONSTRUCTION_EFFECT)
                        .with("effectTargetAddress", selected_harvest_target()),
                )
                .add(
                    Node::class("SucceedAfterTimeoutAction")
                        .with("timeoutSeconds", EFFECT_DELAY_SEC + DECONSTRUCTION_DURATION_MS / 1000.0),
                )
                .add(
                    Node::class("PlayOneShotStretchEffectAction")
                        .with("effectDuration", HARVEST_DURATION_MS)
                        .with("effectName", HARVEST_EFFECT)
                        .with("effectTargetAddress", selected_harvest_target()),
                )
                .add(
                    Node::class("SucceedAfterTimeoutAction")
                        .with("timeoutSeconds", EFFECT_DELAY_SEC + HARVEST_DURATION_MS / 1000.0),
                ),
        )
}
