//! `behaviors/trees/groups/combat.py`.

use crate::blackboards::{Address, ScopeType};

use crate::trees::builder::Node;
use crate::trees::combat::combat_targets_set;

/// `PRUNE_TARGET_TIMER_ADDRESS`.
pub fn prune_target_timer_address() -> Address {
    Address::new(ScopeType::EntityGroup, "PRUNE_TARGET_TIMER")
}

/// `CreateManageTargetCombatSetBehavior(cooldownTimerSeconds)`.
pub fn create_manage_target_combat_set_behavior(cooldown_timer_seconds: i64) -> Node {
    Node::class("ForceSuccess").add(
        Node::class("CooldownTimer")
            .with("timerAddress", prune_target_timer_address())
            .with("timeoutSeconds", cooldown_timer_seconds)
            .add(Node::class("PruneNpcsFromTargetSet").with("targetSetAddress", combat_targets_set())),
    )
}
