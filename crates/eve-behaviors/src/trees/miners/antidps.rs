//! `behaviors/trees/miners/antidps.py`.

use crate::constants::behaviorroles::ROLE_ANTI_DPS;
use crate::constants::blackboardchannels::my_combat_target;
use crate::tree::BehaviorTree;

use crate::trees::builder::{Node, selector};
use crate::trees::combat::{
    EntityCombat, TargetSwitching, create_entity_combat_behavior, create_target_switching_behavior,
    target_switch_timer,
};
use crate::trees::regroup::create_re_group_behavior;
use crate::trees::reinforcements::create_reinforce_location_behavior;
use crate::trees::role::create_register_entity_role;
use crate::trees::unspawn::create_unspawn_behavior;

/// `CreateAntiDpsBehaviorTree`.
pub fn create_anti_dps_behavior_tree() -> BehaviorTree {
    selector()
        .named("Anti-Dps Tree")
        .add(create_register_entity_role(ROLE_ANTI_DPS))
        .add(create_target_switching_behavior(TargetSwitching {
            target_address: my_combat_target(),
            target_switch_timer_address: target_switch_timer(),
            ..TargetSwitching::default()
        }))
        .add(create_entity_combat_behavior(EntityCombat {
            target_address: my_combat_target(),
            ..EntityCombat::default()
        }))
        .add(create_unspawn_behavior())
        .add(create_reinforce_location_behavior())
        .add(create_re_group_behavior())
        .add(Node::class("WaitAction").named("Do nothing"))
        .into_tree()
}
