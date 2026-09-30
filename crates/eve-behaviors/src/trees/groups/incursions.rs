//! `behaviors/trees/groups/incursions.py`: the behavior of the group of an incursion (and the drifters').

use crate::constants::blackboardchannels::guard_object_item_id;
use crate::consts::*;
use crate::tree::BehaviorTree;
use crate::value::Value;

use super::combat::create_manage_target_combat_set_behavior;
use super::threat_management::create_threat_management_behavior;
use crate::trees::builder::{Node, int_list, selector, sequence};
use crate::trees::combat::combat_targets_set;
use crate::trees::incursions::{amarr_navy_owners, drifter_owners};
use crate::trees::reinforcements::create_request_reinforcements;

pub const PRUNE_TIMEOUT_SECONDS: i64 = 120;

/// `CreateIncursionGroupBehavior`.
pub fn create_incursion_group_behavior(
    friendly_owner_ids: Value,
    hostile_owner_ids: Value,
    is_automatically_aggressive: bool,
) -> Node {
    sequence()
        .named("root")
        .add(Node::class("GroupMembershipMonitor").named("Monitor changes in membership"))
        .add(
            Node::class("GroupMemberWarpModeChangedMonitor")
                .named("Tracking movement that can result in bubbles changing."),
        )
        .add(Node::class("BlackboardMessageMonitor").with("messageAddress", combat_targets_set()))
        .add(Node::class("ForceSuccess").add(create_request_reinforcements()))
        .add(create_threat_management_behavior(
            friendly_owner_ids.clone(),
            hostile_owner_ids.clone(),
            is_automatically_aggressive,
        ))
        .add(create_manage_target_combat_set_behavior(PRUNE_TIMEOUT_SECONDS))
        .add(create_group_guard_behavior(friendly_owner_ids, hostile_owner_ids, is_automatically_aggressive))
        .add(Node::class("WaitAction").named("Wait for some action"))
}

/// `CreateGroupBehaviorTree(friendlyOwnerIds, hostileOwnerIds, isAutomaticallyAggressive)`.
pub fn create_group_behavior_tree(
    friendly_owner_ids: Value,
    hostile_owner_ids: Value,
    is_automatically_aggressive: bool,
) -> BehaviorTree {
    create_incursion_group_behavior(friendly_owner_ids, hostile_owner_ids, is_automatically_aggressive)
        .into_tree()
}

/// `CreateDrifterGroupBehaviorTree`.
pub fn create_drifter_group_behavior_tree() -> BehaviorTree {
    create_group_behavior_tree(drifter_owners(), amarr_navy_owners(), true)
}

/// `CreateGroupGuardBehavior`.
pub fn create_group_guard_behavior(
    friendly_owner_ids: Value,
    hostile_owner_ids: Value,
    is_automatically_aggressive: bool,
) -> Node {
    selector()
        .named("guard monitoring")
        .add(Node::class("IsBlackboardValueNone").with("valueAddress", guard_object_item_id()))
        .add(
            Node::class("MonitorSpecificProximitySensorForGroupMembers")
                .with("objectListAddress", combat_targets_set())
                .with("proximitySensorAddress", guard_object_item_id())
                .with(
                    "includedCategories",
                    int_list(&[CATEGORY_SHIP, CATEGORY_DRONE, CATEGORY_ENTITY, CATEGORY_FIGHTER]),
                )
                .with("excludedGroups", int_list(&[GROUP_CAPSULE]))
                .with(
                    "validOwnerIds",
                    if is_automatically_aggressive { Value::None } else { hostile_owner_ids },
                )
                .with(
                    "invalidOwnerIds",
                    if is_automatically_aggressive { friendly_owner_ids } else { Value::None },
                ),
        )
}
