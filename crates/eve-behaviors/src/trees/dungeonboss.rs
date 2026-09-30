//! `behaviors/trees/dungeonboss/dungeonbosstree.py`: the boss of a dungeon guards where it is, fights the biggest, and escapes to explore when it is
//! hurt.

use crate::actions::combatnavigation::{DEFAULT_COMBAT_ATTACK_RANGE, DEFAULT_COMBAT_ORBIT_RANGE};
use crate::blackboards::{Address, ScopeType};
use crate::constants::combat::SIZE_TARGET_EVALUATOR;
use crate::consts::{ARCHETYPE_HIDDEN_WORMHOLE_DUNGEON, GROUP_PLANET};
use crate::tree::BehaviorTree;
use crate::value::Value;

use super::builder::{Node, int_list, selector, sequence};
use super::combat::{Combat, TargetSwitching, create_combat_behavior, create_target_switching_behavior};
use super::explore::{Exploration, create_exploration_behavior};
use super::guardobject::{create_guard_current_location_behavior, create_guard_object_behavior_default};
use super::travel::{create_warp_to_new_location_behavior, warp_to_location_group};

/// `ESCAPE_TIMER`.
pub fn escape_timer() -> Address {
    Address::new(ScopeType::Item, "ESCAPE_TIMER")
}

/// `ESCAPE_TRIGGER_ADDRESS`.
pub fn escape_trigger_address() -> Address {
    Address::new(ScopeType::Item, "ESCAPE_TRIGGER")
}

pub const COMBAT_ORBIT_VELOCITY: i64 = 1500;
/// `BOSS_EXPLORATION_SITE_GROUP_IDS`.
pub const BOSS_EXPLORATION_SITE_GROUP_IDS: [i64; 1] = [GROUP_PLANET];
pub const BOSS_EXPLORATION_TIME: i64 = 3600;
pub const BOSS_GUARD_RANGE: i64 = 500_000;
pub const BOSS_GUARD_REPLACEMENT_RANGE: i64 = 100_000;
pub const BOSS_ESCAPE_INTERVAL_SECONDS: i64 = 1200;
pub const BOSS_ESCAPE_ARMOR_RATIO: f64 = 0.2;
pub const BOSS_ESCAPE_EMERGENCY_ARMOR_RATIO_BUFFER: f64 = 0.05;

/// `CreateFindNewEscapeLocation`.
pub fn create_find_new_escape_location() -> Node {
    Node::class("FindPointToExplore")
        .named("Find Point To Escape-warp to")
        .with("locationMessage", warp_to_location_group())
        .with("groupsOfInterest", int_list(&BOSS_EXPLORATION_SITE_GROUP_IDS))
        .with("dungeonArchetypesOfInterest", int_list(&[ARCHETYPE_HIDDEN_WORMHOLE_DUNGEON]))
        .with("dungeonsOfInterest", Value::List(Vec::new()))
        .with("pickSpecificChance", 0.0)
        .with("shouldConsiderUnspawnedDungeons", false)
}

/// `CreateEscapeBehavior(escapeIntervalSeconds, escapeArmorRatio)`.
pub fn create_escape_behavior(escape_interval_seconds: i64, escape_armor_ratio: f64) -> Node {
    sequence()
        .named("Escape")
        .add(
            Node::class("ArmorDamageThresholdMonitor")
                .with("armorRatioThreshold", escape_armor_ratio)
                .with("thresholdReachedAddress", escape_trigger_address()),
        )
        .add(Node::class("IsBlackboardValueTrue").with("valueAddress", escape_trigger_address()))
        .add(
            Node::class("CooldownTimer")
                .with("timerAddress", escape_timer())
                .with("timeoutSeconds", escape_interval_seconds)
                .add(
                    sequence()
                        .add(create_find_new_escape_location())
                        .add(create_warp_to_new_location_behavior(&warp_to_location_group()))
                        .add(
                            Node::class("SetArmorRatio")
                                .named("Assign emergency armor value to prevent re-triggering")
                                .with(
                                    "armorRatio",
                                    escape_armor_ratio + BOSS_ESCAPE_EMERGENCY_ARMOR_RATIO_BUFFER,
                                ),
                        )
                        .add(
                            Node::class("BlackboardSetMessageToBooleanValueAction")
                                .named("Reset trigger")
                                .with("messageAddress", escape_trigger_address())
                                .with("value", false),
                        ),
                ),
        )
}

/// `CreateDungeonBossBehaviorTree`.
pub fn create_dungeon_boss_behavior_tree() -> BehaviorTree {
    selector()
        .named("Dungeon Boss Root")
        .add(create_guard_current_location_behavior(BOSS_GUARD_RANGE, BOSS_GUARD_REPLACEMENT_RANGE))
        .add(create_escape_behavior(BOSS_ESCAPE_INTERVAL_SECONDS, BOSS_ESCAPE_ARMOR_RATIO))
        .add(create_target_switching_behavior(TargetSwitching {
            target_evaluation_function: Value::Str(SIZE_TARGET_EVALUATOR.to_string()),
            ..TargetSwitching::default()
        }))
        .add(create_combat_behavior(Combat {
            orbit_velocity: Value::Int(COMBAT_ORBIT_VELOCITY),
            orbit_range: Value::Float(DEFAULT_COMBAT_ORBIT_RANGE),
            attack_range: Value::Float(DEFAULT_COMBAT_ATTACK_RANGE),
            ..Combat::default()
        }))
        .add(create_exploration_behavior(Exploration {
            groups_of_interest: int_list(&BOSS_EXPLORATION_SITE_GROUP_IDS),
            dungeon_archetypes_of_interest: int_list(&[ARCHETYPE_HIDDEN_WORMHOLE_DUNGEON]),
            exploration_interval_seconds: BOSS_EXPLORATION_TIME,
            ..Exploration::default()
        }))
        .add(create_guard_object_behavior_default())
        .add(Node::class("WaitAction").named("Do nothing"))
        .into_tree()
}
