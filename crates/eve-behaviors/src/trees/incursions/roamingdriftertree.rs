//! `behaviors/trees/incursions/roamingdriftertree.py`.

use crate::actions::combatnavigation::{DEFAULT_COMBAT_ATTACK_RANGE, DEFAULT_COMBAT_ORBIT_RANGE};
use crate::tree::BehaviorTree;
use crate::value::Value;

use super::const_::ROAMING_SITE_GROUP_IDS;
use crate::trees::builder::{Node, int_list, selector};
use crate::trees::combat::{
    EntityCombat, TargetSwitching, create_entity_combat_behavior, create_target_switching_behavior,
};
use crate::trees::drifters::superweapon::create_super_weapon_behavior;
use crate::trees::explore::{Exploration, create_exploration_behavior};
use crate::trees::regroup::create_re_group_behavior;

pub const COMBAT_ORBIT_VELOCITY: i64 = 1500;

/// `CreateRoamingDrifterBehaviorTree`.
pub fn create_roaming_drifter_behavior_tree() -> BehaviorTree {
    selector()
        .named("Roaming Drifter Root")
        .add(create_super_weapon_behavior())
        .add(create_target_switching_behavior(TargetSwitching::default()))
        .add(create_entity_combat_behavior(EntityCombat {
            orbit_velocity: Value::Int(COMBAT_ORBIT_VELOCITY),
            orbit_range: Value::Float(DEFAULT_COMBAT_ORBIT_RANGE),
            attack_range: Value::Float(DEFAULT_COMBAT_ATTACK_RANGE),
            ..EntityCombat::default()
        }))
        .add(create_re_group_behavior())
        .add(create_exploration_behavior(Exploration {
            groups_of_interest: int_list(&ROAMING_SITE_GROUP_IDS),
            exploration_interval_seconds: 570,
            ..Exploration::default()
        }))
        .add(Node::class("WaitAction").named("Do nothing"))
        .into_tree()
}
