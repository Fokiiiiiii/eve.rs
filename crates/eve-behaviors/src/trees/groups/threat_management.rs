//! `behaviors/trees/groups/threatManagement.py`.

use crate::consts::{CATEGORY_DRONE, CATEGORY_FIGHTER, CATEGORY_SHIP, GROUP_CAPSULE};
use crate::value::Value;

use crate::trees::builder::{Node, int_list, sequence};
use crate::trees::combat::combat_targets_set;

/// `CreateThreatManagementBehavior(friendlyOwnerIds, hostileOwnerIds, isAutomaticallyAggressive)`.
pub fn create_threat_management_behavior(
    friendly_owner_ids: Value,
    hostile_owner_ids: Value,
    is_automatically_aggressive: bool,
) -> Node {
    let mut root = sequence().named("threat management");
    if friendly_owner_ids.truthy() {
        root = root.add(
            Node::class("OwnerListFilterAction")
                .named("Clear out friendly targets")
                .with("ownerIdSet", friendly_owner_ids.clone())
                .with("itemIdSetAddress", combat_targets_set()),
        );
    }
    if hostile_owner_ids.truthy() {
        root = root.add(
            Node::class("TargetsWithOwnerEnteredBubbleMonitor")
                .named("Watch out for enemy owners arriving")
                .with("targetSetAddress", combat_targets_set())
                .with("ownerIds", hostile_owner_ids.clone()),
        );
    }
    root.add(
        Node::class("MonitorAssistanceToGroupTargets")
            .named("Watch out for neutrals assisting known enemies")
            .with("combatTargetsAddress", combat_targets_set()),
    )
    .add(
        Node::class("MonitorProximitySensorsForGroupMembers")
            .named("Watch out for hostiles entering protected airspace")
            .with("objectListAddress", combat_targets_set())
            .with("includedCategories", int_list(&[CATEGORY_SHIP, CATEGORY_DRONE, CATEGORY_FIGHTER]))
            .with("excludedGroups", int_list(&[GROUP_CAPSULE]))
            .with("validOwnerIds", if is_automatically_aggressive { Value::None } else { hostile_owner_ids })
            .with(
                "invalidOwnerIds",
                if is_automatically_aggressive { friendly_owner_ids } else { Value::None },
            ),
    )
    .add(
        Node::class("MonitorInvulnerabilityCanceledInGroupBubbles")
            .with("targetIdListAddress", combat_targets_set()),
    )
}
