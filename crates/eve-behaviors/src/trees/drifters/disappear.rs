//! `behaviors/trees/drifters/disappear.py`: a drifter that goes into a wormhole to hide, and comes back.

use crate::blackboards::{Address, ScopeType};

use crate::trees::builder::{Node, int_list, sequence};

/// `WORMHOLE_TYPE_IDS`.
pub const WORMHOLE_TYPE_IDS: [i64; 5] = [9, 10, 11, 12, 13];

/// `WORMHOLE`.
pub fn wormhole() -> Address {
    Address::new(ScopeType::EntityGroup, "WORMHOLE")
}

/// `WORMHOLE_LOCATION`.
pub fn wormhole_location() -> Address {
    Address::new(ScopeType::EntityGroup, "WORMHOLE_LOCATION")
}

/// `DISAPPEAR_WORMHOLE`.
pub fn disappear_wormhole() -> Address {
    Address::new(ScopeType::EntityGroup, "DISAPPEAR_WORMHOLE")
}

pub const DISAPPEAR_DURATION_SEC: i64 = 1800;
pub const CLOAK_EFFECT_DELAY_SEC: i64 = 2;
pub const INVULNERABILITY_REASON: &str = "Hideout";
pub const WORMHOLE_EFFECT: &str = "effects.WormholeActivity";
pub const WORMHOLE_DURATION_MSEC: i64 = 2000;

/// `CreateDisappearBehavior`.
pub fn create_disappear_behavior() -> Node {
    let approach_wormhole = sequence()
        .named("Approach Wormhole")
        .add(
            Node::class("GetItemIdByTypeId")
                .with("typeIds", int_list(&WORMHOLE_TYPE_IDS))
                .with("itemIdAddress", wormhole()),
        )
        .add(
            Node::class("ApproachObject")
                .with("itemIdAddress", wormhole())
                .with("notifyRange", 2500)
                .with("approachRange", 500)
                .with("blocking", true),
        );
    let hideout_and_then_return = Node::class("Uninterruptible").add(
        Node::class("Invulnerable").with("reason", INVULNERABILITY_REASON).add(
            Node::class("Cloaked").add(
                sequence()
                    .named("Go Hideout, then Return")
                    .add(
                        Node::class("GetRandomDeepSpacePosition")
                            .with("locationAddress", disappear_wormhole()),
                    )
                    .add(
                        Node::class("PlayOneShotShipEffectAction")
                            .with("effectDuration", WORMHOLE_DURATION_MSEC)
                            .with("effectName", WORMHOLE_EFFECT)
                            .with("effectTargetAddress", wormhole()),
                    )
                    .add(
                        Node::class("SucceedAfterTimeoutAction")
                            .with("timeoutSeconds", CLOAK_EFFECT_DELAY_SEC),
                    )
                    .add(Node::class("TransferToLocation").with("locationAddress", disappear_wormhole()))
                    .add(
                        Node::class("SucceedAfterTimeoutAction")
                            .with("timeoutSeconds", DISAPPEAR_DURATION_SEC),
                    )
                    .add(
                        Node::class("GetObjectSpaceLocation")
                            .with("itemIdAddress", wormhole())
                            .with("locationAddress", wormhole_location()),
                    )
                    .add(Node::class("TransferToLocation").with("locationAddress", wormhole_location()))
                    .add(
                        Node::class("SucceedAfterTimeoutAction")
                            .with("timeoutSeconds", CLOAK_EFFECT_DELAY_SEC),
                    ),
            ),
        ),
    );
    sequence().named("Disappear").add(approach_wormhole).add(hideout_and_then_return)
}
