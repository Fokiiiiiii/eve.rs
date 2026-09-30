//! `behaviors/trees/miners/antilogistics.py`.

use crate::constants::behaviorroles::ROLE_ANTI_LOGISTIC;
use crate::constants::blackboardchannels::my_combat_target;
use crate::constants::combat::FITTED_MODULE_GROUP_TARGET_EVALUATOR;
use crate::consts::*;
use crate::tree::BehaviorTree;
use crate::value::Value;

use crate::trees::builder::{Node, int_list, selector};
use crate::trees::combat::{
    EntityCombat, TargetSwitching, create_entity_combat_behavior, create_target_switching_behavior,
    target_switch_timer,
};
use crate::trees::regroup::create_re_group_behavior;
use crate::trees::reinforcements::create_reinforce_location_behavior;
use crate::trees::role::create_register_entity_role;
use crate::trees::unspawn::create_unspawn_behavior;

/// `ANTI_LOGISTICS_PREFERRED_MODULE_GROUPS`.
pub const ANTI_LOGISTICS_PREFERRED_MODULE_GROUPS: [i64; 6] = [
    GROUP_SHIELD_TRANSPORTER,
    GROUP_ARMOR_REPAIR_PROJECTOR,
    GROUP_FUELED_REMOTE_ARMOR_REPAIRER,
    GROUP_FUELED_REMOTE_SHIELD_BOOSTER,
    GROUP_REMOTE_HULL_REPAIRER,
    GROUP_ENERGY_TRANSFER_ARRAY,
];

/// `CreateAntiLogisticsBehaviorTree`.
pub fn create_anti_logistics_behavior_tree() -> BehaviorTree {
    let evaluator = Value::Str(FITTED_MODULE_GROUP_TARGET_EVALUATOR.to_string());
    selector()
        .named("Anti-Logistics Tree")
        .add(create_register_entity_role(ROLE_ANTI_LOGISTIC))
        .add(create_target_switching_behavior(TargetSwitching {
            target_address: my_combat_target(),
            target_switch_timer_address: target_switch_timer(),
            target_evaluation_function: evaluator.clone(),
            preferred_module_groups: int_list(&ANTI_LOGISTICS_PREFERRED_MODULE_GROUPS),
            ..TargetSwitching::default()
        }))
        .add(create_entity_combat_behavior(EntityCombat {
            target_address: my_combat_target(),
            target_evaluation_function: evaluator,
            preferred_module_groups: int_list(&ANTI_LOGISTICS_PREFERRED_MODULE_GROUPS),
            ..EntityCombat::default()
        }))
        .add(create_unspawn_behavior())
        .add(create_reinforce_location_behavior())
        .add(create_re_group_behavior())
        .add(Node::class("WaitAction").named("Do nothing"))
        .into_tree()
}
