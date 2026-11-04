//! `behaviors/trees/guardobject.py`: an entity that guards an object.

use crate::constants::blackboardchannels::{
    guard_object_item_id, guard_object_orbit_range, guard_object_position, warp_scrambled_address,
};
use crate::consts::*;
use crate::value::Value;

use super::DRIFTER_TAG;
use super::builder::{Node, int_list, selector, sequence};
use super::combat::combat_targets_set;

/// `GUARD_OBJECT_DEFAULT_CATEGORIES`.
pub const GUARD_OBJECT_DEFAULT_CATEGORIES: [i64; 3] = [CATEGORY_SHIP, CATEGORY_DRONE, CATEGORY_FIGHTER];

pub fn create_guard_object_behavior(
    valid_owner_ids: Value,
    included_categories: Value,
    invalid_owner_ids: Value,
) -> Node {
    selector()
        .named("Guard Object")
        .add(
            sequence()
                .named("Orbit Guard Object")
                .add(
                    Node::class("IsBallPresentInMyBubbleCondition")
                        .with("ballIdAddress", guard_object_item_id()),
                )
                .add(
                    Node::class("OrbitAtDistanceAction")
                        .with("orbitTargetAddress", guard_object_item_id())
                        .with("orbitRange", 0)
                        .with("blocking", false)
                        .with("blockUntilRange", Value::None)
                        .with("orbitRangeAddress", guard_object_orbit_range()),
                )
                .add(
                    Node::class("MonitorSpecificProximitySensor")
                        .with("objectListAddress", combat_targets_set())
                        .with("includedCategories", included_categories)
                        .with("excludedGroups", int_list(&[GROUP_CAPSULE]))
                        .with("proximitySensorAddress", guard_object_item_id())
                        .with("validOwnerIds", valid_owner_ids)
                        .with("invalidOwnerIds", invalid_owner_ids),
                )
                .add(Node::class("WaitAction").named("Wait until disturbed")),
        )
        .add(
            Node::class("WarpToNewLocation")
                .named("Warp to Guard Object")
                .with("locationMessage", guard_object_position())
                .with("warpScrambledAddress", warp_scrambled_address()),
        )
}

/// `CreateGuardObjectBehavior()` with what the client's arguments default to.
pub fn create_guard_object_behavior_default() -> Node {
    create_guard_object_behavior(
        Value::Tuple(Vec::new()),
        int_list(&GUARD_OBJECT_DEFAULT_CATEGORIES),
        Value::None,
    )
}

/// `CreateGuardObjectEntityBehavior`.
pub fn create_guard_object_entity_behavior() -> Node {
    selector()
        .named("Guard Object")
        .add(
            sequence()
                .named("Orbit Guard Object")
                .add(
                    Node::class("IsBallPresentInMyBubbleCondition")
                        .with("ballIdAddress", guard_object_item_id()),
                )
                .add(
                    Node::class("OrbitAtDistanceAction")
                        .with("orbitTargetAddress", guard_object_item_id())
                        .with("orbitRange", 0)
                        .with("blocking", false)
                        .with("blockUntilRange", Value::None)
                        .with("orbitRangeAddress", guard_object_orbit_range()),
                )
                .add(Node::class("WaitAction").named("Wait until disturbed")),
        )
        .add(
            Node::class("WarpToNewLocation")
                .named("Warp to Guard Object")
                .with("locationMessage", guard_object_position())
                .with("warpScrambledAddress", warp_scrambled_address()),
        )
}

/// `CreateGuardCurrentLocationBehavior(guardRange, guardReplacementRange)`.
pub fn create_guard_current_location_behavior(guard_range: i64, guard_replacement_range: i64) -> Node {
    Node::class("ForceFailure").named("Guard the current location").add(
        sequence()
            .add(
                Node::class("BlackboardSendMyItemIdMessageAction")
                    .with("messageAddress", guard_object_item_id()),
            )
            .add(
                Node::class("BlackboardSetMessageToIntegerValueAction")
                    .with("messageAddress", guard_object_orbit_range())
                    .with("value", guard_range),
            )
            .add(
                Node::class("PlaceProximitySensor")
                    .with("itemIdAddress", guard_object_item_id())
                    .with("rangeAddress", guard_object_orbit_range())
                    .with("sensorReplacementRange", guard_replacement_range)
                    .with("tags", Value::set([Value::Str(DRIFTER_TAG.to_string())])),
            ),
    )
}

/// `CreateGuardCurrentLocationBehavior()` with what the client's arguments default to.
pub fn create_guard_current_location_behavior_default() -> Node {
    create_guard_current_location_behavior(1_000_000, 10_000)
}
