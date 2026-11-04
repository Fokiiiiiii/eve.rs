//! `behaviors/trees/drifters/driftertree.py`: the drifter battleships.

use crate::actions::combatnavigation::{DEFAULT_COMBAT_ATTACK_RANGE, DEFAULT_COMBAT_ORBIT_RANGE};
use crate::tree::BehaviorTree;
use crate::value::Value;

use super::disappear::create_disappear_behavior;
use super::drifter_aggression::create_drifter_entosis_aggression_behavior;
use super::idle::create_idle_behavior;
use super::superweapon::create_super_weapon_behavior;
use crate::trees::builder::{int_list, selector};
use crate::trees::combat::{
    Combat, TargetSwitching, create_combat_behavior, create_friendly_target_filter_behavior,
    create_target_switching_behavior,
};
use crate::trees::corpsedisposal::create_corpse_disposal_behavior;
use crate::trees::corpseharvesting::create_corpse_harvesting_behavior;
use crate::trees::explore::{Exploration, create_exploration_behavior};
use crate::trees::harvesting::create_harvest_outpost_behavior;
use crate::trees::incursions::drifter_owners;
use crate::trees::pursuit::create_pursuit_behavior;
use crate::trees::regroup::create_re_group_behavior;
use crate::trees::reinforcements::create_request_reinforcements;

pub const LANDMARK_ARCHETYPE: i64 = 2;
pub const DUNGEON_WORMHOLE: i64 = 5643;
pub const UNIDENTIFIED_STRUCTURE: i64 = 5610;
pub const COMBAT_ORBIT_VELOCITY: i64 = 1500;

fn combat() -> Combat {
    Combat {
        orbit_velocity: Value::Int(COMBAT_ORBIT_VELOCITY),
        orbit_range: Value::Float(DEFAULT_COMBAT_ORBIT_RANGE),
        attack_range: Value::Float(DEFAULT_COMBAT_ATTACK_RANGE),
        ..Combat::default()
    }
}

fn exploration() -> Exploration {
    Exploration {
        dungeon_archetypes_of_interest: int_list(&[LANDMARK_ARCHETYPE]),
        dungeons_of_interest: int_list(&[DUNGEON_WORMHOLE, UNIDENTIFIED_STRUCTURE]),
        ..Exploration::default()
    }
}

/// `CreateDrifterBehaviorTree`.
pub fn create_drifter_behavior_tree() -> BehaviorTree {
    selector()
        .named("Drifter Root")
        .add(create_request_reinforcements())
        .add(create_super_weapon_behavior())
        .add(create_target_switching_behavior(TargetSwitching::default()))
        .add(create_drifter_entosis_aggression_behavior())
        .add(create_friendly_target_filter_behavior(drifter_owners()))
        .add(create_combat_behavior(combat()))
        .add(create_re_group_behavior())
        .add(create_pursuit_behavior())
        .add(create_corpse_harvesting_behavior())
        .add(create_corpse_disposal_behavior())
        .add(create_exploration_behavior(exploration()))
        .add(create_disappear_behavior())
        .add(create_harvest_outpost_behavior())
        .add(create_idle_behavior())
        .into_tree()
}

/// `CreateHiveSystemDrifterBehaviorTree`.
pub fn create_hive_system_drifter_behavior_tree() -> BehaviorTree {
    selector()
        .named("Drifter Root")
        .add(create_request_reinforcements())
        .add(create_super_weapon_behavior())
        .add(create_target_switching_behavior(TargetSwitching::default()))
        .add(create_friendly_target_filter_behavior(drifter_owners()))
        .add(create_combat_behavior(combat()))
        .add(create_re_group_behavior())
        .add(create_corpse_harvesting_behavior())
        .add(create_exploration_behavior(exploration()))
        .add(create_idle_behavior())
        .into_tree()
}
