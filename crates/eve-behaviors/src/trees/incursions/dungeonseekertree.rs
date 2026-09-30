//! `behaviors/trees/incursions/dungeonseekertree.py`.

use crate::actions::combatnavigation::{DEFAULT_COMBAT_ATTACK_RANGE, DEFAULT_COMBAT_ORBIT_RANGE};
use crate::tree::BehaviorTree;
use crate::value::Value;

use crate::trees::builder::selector;
use crate::trees::combat::{
    EntityCombat, TargetSwitching, create_entity_combat_behavior, create_target_switching_behavior,
};
use crate::trees::guardobject::create_guard_object_behavior_default;

pub const COMBAT_ORBIT_VELOCITY: i64 = 600;

/// `CreateDungeonSeekerBehaviorTree`.
pub fn create_dungeon_seeker_behavior_tree() -> BehaviorTree {
    selector()
        .named("Dungeon Seeker Root")
        .add(create_target_switching_behavior(TargetSwitching::default()))
        .add(create_entity_combat_behavior(EntityCombat {
            orbit_velocity: Value::Int(COMBAT_ORBIT_VELOCITY),
            orbit_range: Value::Float(DEFAULT_COMBAT_ORBIT_RANGE),
            attack_range: Value::Float(DEFAULT_COMBAT_ATTACK_RANGE),
            ..EntityCombat::default()
        }))
        .add(create_guard_object_behavior_default())
        .add(crate::trees::builder::Node::class("WaitAction").named("Do nothing"))
        .into_tree()
}
