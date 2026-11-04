//! `behaviors/trees/pirate/shadow_of_the_serpent_trees.py`.

use crate::actions::combatnavigation::{DEFAULT_COMBAT_ATTACK_RANGE, DEFAULT_COMBAT_ORBIT_RANGE};
use crate::consts::{GROUP_ASTEROID_BELT, GROUP_STARGATE};
use crate::tree::BehaviorTree;
use crate::value::Value;

use crate::trees::builder::{Node, int_list, selector, sequence};
use crate::trees::combat::{
    EntityCombat, TargetSwitching, combat_targets_set, create_entity_combat_behavior,
    create_target_switching_behavior,
};
use crate::trees::explore::{Exploration, create_exploration_behavior};
use crate::trees::groups::combat::create_manage_target_combat_set_behavior;
use crate::trees::groups::threat_management::create_threat_management_behavior;
use crate::trees::patrol::{create_idle_fallback, create_patrol_behavior};
use crate::trees::regroup::create_re_group_behavior;
use crate::trees::reinforcements::create_request_reinforcements;
use crate::trees::tanking::create_tanking_behavior;

pub const PRUNE_TIMEOUT_SECONDS: i64 = 120;
pub const COMBAT_ORBIT_VELOCITY: i64 = 300;

/// `CreateRoamingShadowOfTheSerpentTree`.
pub fn create_roaming_shadow_of_the_serpent_tree() -> BehaviorTree {
    selector()
        .named("Shadow of the Serpent behavior")
        .add(create_target_switching_behavior(TargetSwitching::default()))
        .add(create_tanking_behavior())
        .add(create_entity_combat_behavior(EntityCombat {
            orbit_velocity: Value::Int(COMBAT_ORBIT_VELOCITY),
            orbit_range: Value::Float(DEFAULT_COMBAT_ORBIT_RANGE),
            attack_range: Value::Float(DEFAULT_COMBAT_ATTACK_RANGE),
            ..EntityCombat::default()
        }))
        .add(create_re_group_behavior())
        .add(create_exploration_behavior(Exploration {
            groups_of_interest: int_list(&[GROUP_STARGATE, GROUP_ASTEROID_BELT]),
            exploration_interval_seconds: 810,
            ..Exploration::default()
        }))
        .add(create_patrol_behavior(100_000.0, 10_000.0, 90.0))
        .add(create_idle_fallback())
        .add(Node::class("WaitAction").named("Do nothing"))
        .into_tree()
}

/// `CreateShadowOfTheSerpentGroupBehavior`.
pub fn create_shadow_of_the_serpent_group_behavior() -> Node {
    sequence()
        .named("Shadow of the Serpent group behavior")
        .add(Node::class("GroupMembershipMonitor").named("Monitor changes in membership"))
        .add(
            Node::class("GroupMemberWarpModeChangedMonitor")
                .named("Tracking movement that can result in bubbles changing."),
        )
        .add(Node::class("BlackboardMessageMonitor").with("messageAddress", combat_targets_set()))
        .add(Node::class("ForceSuccess").add(create_request_reinforcements()))
        .add(create_threat_management_behavior(Value::None, Value::None, true))
        .add(create_manage_target_combat_set_behavior(PRUNE_TIMEOUT_SECONDS))
        .add(Node::class("WaitAction").named("Wait for some action"))
}

/// `CreateShadowOfTheSerpentisGroupTree`.
pub fn create_shadow_of_the_serpentis_group_tree() -> BehaviorTree {
    create_shadow_of_the_serpent_group_behavior().into_tree()
}
