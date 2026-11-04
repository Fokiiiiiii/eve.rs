//! `behaviors/trees/corpseharvesting.py`: an entity that picks up the corpses in its bubble.

use crate::blackboards::{Address, ScopeType};
use crate::consts::{TYPE_CORPSE, TYPE_CORPSE_FEMALE};

use super::builder::{Node, int_list, selector, sequence};

/// `CORPSE_TYPES`.
pub const CORPSE_TYPES: [i64; 2] = [TYPE_CORPSE, TYPE_CORPSE_FEMALE];

/// `CORPSE_ITEM_ADDRESS`.
pub fn corpse_item_address() -> Address {
    Address::new(ScopeType::Item, "CORPSE_ITEM")
}

/// `CORPSE_SCAN_COOLDOWN`.
pub fn corpse_scan_cooldown() -> Address {
    Address::new(ScopeType::EntityGroup, "CORPSE_SCAN_COOLDOWN")
}

/// `CORPSE_SCAN_COOLDOWN_SEC`.
pub const CORPSE_SCAN_COOLDOWN_SEC: i64 = 300;

fn find_corpse_in_bubble() -> Node {
    Node::class("SelectItemByTypes")
        .named("Find Corpse In Bubble")
        .with("typeIds", int_list(&CORPSE_TYPES))
        .with("selectedItemAddress", corpse_item_address())
}

/// `CreateCorpseHarvestingBehavior`.
pub fn create_corpse_harvesting_behavior() -> Node {
    selector()
        .named("Corpse Harvesting")
        .add(
            sequence()
                .named("Harvest Corpse If Valid Target")
                .add(
                    Node::class("IsBlackboardValueNotNone")
                        .named("Has Corpse Target")
                        .with("valueAddress", corpse_item_address()),
                )
                .add(
                    Node::class("TargetLeavingParkMonitor")
                        .with("targetAddress", corpse_item_address())
                        .with("clearValue", true),
                )
                .add(
                    Node::class("ApproachObject")
                        .named("Approach Corpse")
                        .with("itemIdAddress", corpse_item_address())
                        .with("notifyRange", 500)
                        .with("approachRange", 0)
                        .with("blocking", true),
                )
                .add(
                    Node::class("EntityLootItem")
                        .named("Pick Up Corpse For Loot")
                        .with("itemIdAddress", corpse_item_address()),
                )
                .add(
                    Node::class("BlackboardSetMessageAsNoneAction")
                        .named("Reset Corpse Target")
                        .with("messageAddress", corpse_item_address()),
                )
                .add(
                    Node::class("SucceedAfterTimeoutAction")
                        .named("Waiting On Object To Leave Park")
                        .with("timeoutSeconds", 5),
                )
                .add(find_corpse_in_bubble()),
        )
        .add(
            Node::class("CooldownTimer")
                .named("Corpse Harvest Cooldown Timer")
                .with("timeoutSeconds", CORPSE_SCAN_COOLDOWN_SEC)
                .with("timerAddress", corpse_scan_cooldown())
                .add(find_corpse_in_bubble()),
        )
}
