//! `behaviors/trees/corpsedisposal.py`: an entity that carries corpses takes them to a wormhole.

use super::builder::{Node, int_list, sequence};
use super::corpseharvesting::CORPSE_TYPES;
use super::drifters::disappear::{WORMHOLE_TYPE_IDS, wormhole};

/// `CreateCorpseDisposalBehavior`.
pub fn create_corpse_disposal_behavior() -> Node {
    sequence()
        .named("Corpse Disposal")
        .add(
            Node::class("HasAnyOfTypesInEntityAdditionalLoot")
                .named("Check For Corpses In Loot")
                .with("typeIds", int_list(&CORPSE_TYPES)),
        )
        .add(
            Node::class("GetItemIdByTypeId")
                .named("Get Wormhole ID")
                .with("typeIds", int_list(&WORMHOLE_TYPE_IDS))
                .with("itemIdAddress", wormhole()),
        )
        .add(
            Node::class("ApproachObject")
                .named("Approach Wormhole")
                .with("itemIdAddress", wormhole())
                .with("notifyRange", 2500)
                .with("approachRange", 500)
                .with("blocking", true),
        )
        .add(
            Node::class("RemoveTypesFromAdditionalLoot")
                .named("Dispose Of Corpses")
                .with("typeIds", int_list(&CORPSE_TYPES)),
        )
}
