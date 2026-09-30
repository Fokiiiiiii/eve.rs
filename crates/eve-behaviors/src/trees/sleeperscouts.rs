//! `behaviors/trees/sleeperscouts.py`: the roaming sleeper scouts.

use crate::actions::combatnavigation::{DEFAULT_COMBAT_ATTACK_RANGE, DEFAULT_COMBAT_ORBIT_RANGE};
use crate::tree::BehaviorTree;
use crate::value::Value;

use super::builder::selector;
use super::combat::{Combat, create_combat_behavior, create_friendly_target_filter_behavior};
use super::corpsedisposal::create_corpse_disposal_behavior;
use super::corpseharvesting::create_corpse_harvesting_behavior;
use super::harvesting::create_harvest_outpost_behavior;
use super::incursions::drifter_owners;
use super::pursuit::create_pursuit_behavior;
use super::regroup::create_re_group_behavior;
use super::reinforcements::create_request_reinforcements;
use super::scoutexplore::create_exploration_behavior;
use super::scoutsidle::create_idle_behavior;

pub const COMBAT_ORBIT_VELOCITY: i64 = 600;

fn combat() -> Combat {
    Combat {
        orbit_velocity: Value::Int(COMBAT_ORBIT_VELOCITY),
        orbit_range: Value::Float(DEFAULT_COMBAT_ORBIT_RANGE),
        attack_range: Value::Float(DEFAULT_COMBAT_ATTACK_RANGE),
        ..Combat::default()
    }
}

/// `CreateSleeperScoutBehaviorTree`.
pub fn create_sleeper_scout_behavior_tree() -> BehaviorTree {
    selector()
        .named("Sleeper Scout Root")
        .add(create_request_reinforcements())
        .add(create_friendly_target_filter_behavior(drifter_owners()))
        .add(create_combat_behavior(combat()))
        .add(create_re_group_behavior())
        .add(create_pursuit_behavior())
        .add(create_corpse_harvesting_behavior())
        .add(create_corpse_disposal_behavior())
        .add(create_exploration_behavior())
        .add(create_harvest_outpost_behavior())
        .add(create_idle_behavior())
        .into_tree()
}

/// `CreateHiveSystemSleeperScoutBehaviorTree`.
pub fn create_hive_system_sleeper_scout_behavior_tree() -> BehaviorTree {
    selector()
        .named("Sleeper Scout Root")
        .add(create_request_reinforcements())
        .add(create_friendly_target_filter_behavior(drifter_owners()))
        .add(create_combat_behavior(combat()))
        .add(create_re_group_behavior())
        .add(create_corpse_harvesting_behavior())
        .add(create_exploration_behavior())
        .add(create_idle_behavior())
        .into_tree()
}
