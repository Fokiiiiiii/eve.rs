//! `behaviors/trees/driftercommander/driftercommandertree.py`.

use crate::actions::combatnavigation::{DEFAULT_COMBAT_ATTACK_RANGE, DEFAULT_COMBAT_ORBIT_RANGE};
use crate::tree::BehaviorTree;
use crate::value::Value;

use super::builder::selector;
use super::combat::{Combat, TargetSwitching, create_combat_behavior, create_target_switching_behavior};
use super::drifters::superweapon::create_super_weapon_behavior;
use super::guardobject::create_guard_object_behavior_default;

pub const COMBAT_ORBIT_VELOCITY: i64 = 1500;

/// `CreateDrifterCommanderBehaviorTree`.
pub fn create_drifter_commander_behavior_tree() -> BehaviorTree {
    selector()
        .named("Drifter Root")
        .add(create_super_weapon_behavior())
        .add(create_target_switching_behavior(TargetSwitching::default()))
        .add(create_combat_behavior(Combat {
            orbit_velocity: Value::Int(COMBAT_ORBIT_VELOCITY),
            orbit_range: Value::Float(DEFAULT_COMBAT_ORBIT_RANGE),
            attack_range: Value::Float(DEFAULT_COMBAT_ATTACK_RANGE),
            ..Combat::default()
        }))
        .add(create_guard_object_behavior_default())
        .into_tree()
}
