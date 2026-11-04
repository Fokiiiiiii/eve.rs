//! `behaviors/trees/miners/basicdpstree.py`.

use crate::constants::behaviorroles::ROLE_DPS;
use crate::tree::BehaviorTree;

use crate::trees::builder::{Node, selector};
use crate::trees::combat::{
    EntityCombat, TargetSwitching, create_entity_combat_behavior, create_target_switching_behavior,
};
use crate::trees::regroup::create_re_group_behavior;
use crate::trees::reinforcements::create_reinforce_location_behavior;
use crate::trees::role::create_register_entity_role;
use crate::trees::unspawn::create_unspawn_behavior;

/// `CreateBasicDpsBehaviorTree`.
pub fn create_basic_dps_behavior_tree() -> BehaviorTree {
    selector()
        .named("Basic Dps Tree")
        .add(create_register_entity_role(ROLE_DPS))
        .add(create_target_switching_behavior(TargetSwitching::default()))
        .add(create_entity_combat_behavior(EntityCombat::default()))
        .add(create_unspawn_behavior())
        .add(create_reinforce_location_behavior())
        .add(create_re_group_behavior())
        .add(Node::class("WaitAction").named("Do nothing"))
        .into_tree()
}
