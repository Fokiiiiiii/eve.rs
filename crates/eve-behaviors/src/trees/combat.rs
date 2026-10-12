//! `behaviors/trees/combat.py`: fighting: choosing a target, going where to fight it from, and attacking it.

use crate::blackboards::{Address, ScopeType};
use crate::constants::blackboardchannels::{
    combat_min_falloff_address, combat_min_optimal_address, combat_targets_set_address, commander_address,
    group_primary_target, my_combat_target,
};
use crate::constants::combat::THREAT_TARGET_EVALUATOR;
use crate::consts::*;
use crate::value::Value;

use super::DRIFTER_TAG;
use super::builder::{Node, int_list, int_tuple, selector, sequence};
use super::role::role_address;

/// `COMBAT_TARGETS_SET`.
pub fn combat_targets_set() -> Address {
    combat_targets_set_address()
}

/// `TARGETS_MONITORED_FOR_ASSISTANCE_SET`.
pub fn targets_monitored_for_assistance_set() -> Address {
    Address::new(ScopeType::EntityGroup, "TARGETS_MONITORED_FOR_ASSISTANCE_SET")
}

/// `COMBAT_WARP_TO_LOCATION`.
pub fn combat_warp_to_location() -> Address {
    Address::new(ScopeType::EntityGroup, "COMBAT_WARP_TO_LOCATION")
}

/// `POTENTIAL_TARGET`.
pub fn potential_target() -> Address {
    Address::new(ScopeType::Item, "POTENTIAL_TARGET")
}

/// `GROUP_TARGET_SWITCH_TIMER`.
pub fn group_target_switch_timer() -> Address {
    Address::new(ScopeType::EntityGroup, "GROUP_TARGET_SWITCH_TIMER")
}

/// `TARGET_SWITCH_TIMER`.
pub fn target_switch_timer() -> Address {
    Address::new(ScopeType::Item, "TARGET_SWITCH_TIMER")
}

/// `COMBAT_ORBIT_RANGE`.
pub fn combat_orbit_range() -> Address {
    Address::new(ScopeType::Item, "COMBAT_ORBIT_RANGE")
}

/// `COMBAT_ATTACK_RANGE`.
pub fn combat_attack_range() -> Address {
    Address::new(ScopeType::Item, "COMBAT_ATTACK_RANGE")
}

/// `COMBAT_ORBIT_TARGET`.
pub fn combat_orbit_target() -> Address {
    Address::new(ScopeType::Item, "COMBAT_ORBIT_TARGET")
}

/// `COMBAT_WARP_AT_DISTANCE`.
pub fn combat_warp_at_distance() -> Address {
    Address::new(ScopeType::Item, "COMBAT_WARP_AT_DISTANCE")
}

/// `COMBAT_EFFECTS_ADDRESS`.
pub fn combat_effects_address() -> Address {
    Address::new(ScopeType::Item, "COMBAT_EFFECTS")
}

/// `COMBAT_EFFECTS`: the effects a combat entity fights with.
pub const COMBAT_EFFECTS: [i64; 20] = [
    EFFECT_ENTITY_CAPACITOR_DRAIN,
    EFFECT_ENTITY_TARGET_PAINT,
    EFFECT_MISSILE_LAUNCHING_FOR_ENTITY,
    EFFECT_MODIFY_TARGET_SPEED2,
    EFFECT_TARGET_ATTACK,
    EFFECT_WARP_SCRAMBLE_FOR_ENTITY,
    EFFECT_WARP_SCRAMBLE_TARGET_MWDBLOCK_ACTIVATION_FOR_ENTITY,
    EFFECT_BEHAVIOR_ECM,
    EFFECT_NPC_BEHAVIOR_ENERGY_NEUTRALIZER,
    EFFECT_NPC_BEHAVIOR_ENERGY_NOSFERATU,
    EFFECT_NPC_BEHAVIOR_GUIDANCE_DISRUPTOR,
    EFFECT_BEHAVIOR_SENSOR_DAMPENER,
    EFFECT_BEHAVIOR_TARGET_PAINTER,
    EFFECT_NPC_BEHAVIOR_TRACKING_DISRUPTOR,
    EFFECT_BEHAVIOR_WARP_DISRUPT,
    EFFECT_BEHAVIOR_WARP_SCRAMBLE,
    EFFECT_NPC_BEHAVIOR_WEBIFIER,
    EFFECT_NPC_BEHAVIOR_MICRO_JUMP_ATTACK,
    EFFECT_NPC_BEHAVIOR_SMART_BOMB,
    EFFECT_ENTITY_CHAIN_LIGHTNING,
];

/// The arguments of `CreateTargetSwitchingBehavior`.
#[derive(Debug, Clone)]
pub struct TargetSwitching {
    pub target_evaluation_function: Value,
    pub preferred_module_groups: Value,
    pub target_address: Address,
    pub group_primary_address: Address,
    pub target_switch_timer_address: Address,
}

impl Default for TargetSwitching {
    fn default() -> Self {
        TargetSwitching {
            target_evaluation_function: Value::Str(THREAT_TARGET_EVALUATOR.to_string()),
            preferred_module_groups: Value::List(Vec::new()),
            target_address: group_primary_target(),
            group_primary_address: group_primary_target(),
            target_switch_timer_address: group_target_switch_timer(),
        }
    }
}

/// `CreateTargetSwitchingBehavior`.
pub fn create_target_switching_behavior(args: TargetSwitching) -> Node {
    Node::class("ForceFailure").named("Target Switching").add(
        Node::class("CooldownTimer")
            .with("timerAddress", &args.target_switch_timer_address)
            .with("timeoutSeconds", 60)
            .add(
                sequence()
                    .add(
                        Node::class("FindTarget")
                            .named("Get A Target")
                            .with("selectedTargetAddress", potential_target())
                            .with("potentialTargetListAddress", combat_targets_set())
                            .with("includedCategories", Value::List(Vec::new()))
                            .with("onlyCheckLocalBubble", true)
                            .with("targetEvaluationFunction", args.target_evaluation_function.clone())
                            .with("roleAddress", role_address())
                            .with("preferredModuleGroups", args.preferred_module_groups.clone())
                            .with("primaryTargetIdAddress", &args.group_primary_address),
                    )
                    .add(
                        Node::class("Not").add(
                            Node::class("BlackboardMessagesEqualCondition")
                                .with("firstMessageAddress", &args.target_address)
                                .with("secondMessageAddress", potential_target()),
                        ),
                    )
                    .add(
                        Node::class("BlackboardCopyMessageAction")
                            .with("sourceMessageAddress", potential_target())
                            .with("targetMessageAddress", &args.target_address),
                    )
                    .add(
                        Node::class("StopCombatEffectsOnTarget")
                            .named("Stop All Combat Effects")
                            .with("effectIds", int_tuple(&COMBAT_EFFECTS)),
                    ),
            ),
    )
}

/// The arguments of `CreateCombatBehavior`.
#[derive(Debug, Clone)]
pub struct Combat {
    pub valid_owner_ids: Value,
    pub orbit_velocity: Value,
    pub orbit_range: Value,
    pub attack_range: Value,
    pub target_address: Address,
    pub group_primary_address: Address,
    pub target_evaluation_function: Value,
    pub preferred_module_groups: Value,
    pub proximity_sensor_tags: Value,
    /// The categories the proximity sensors report (`includedCategories`): ships, drones and fighters by default, the entities too for a fleet
    /// that fights NPCs of another faction (`create_pochven_npc_behavior_tree`).
    pub included_categories: Value,
}

impl Default for Combat {
    fn default() -> Self {
        Combat {
            valid_owner_ids: Value::List(Vec::new()),
            orbit_velocity: Value::None,
            orbit_range: Value::None,
            attack_range: Value::None,
            target_address: group_primary_target(),
            group_primary_address: group_primary_target(),
            target_evaluation_function: Value::Str(THREAT_TARGET_EVALUATOR.to_string()),
            preferred_module_groups: Value::List(Vec::new()),
            proximity_sensor_tags: Value::Tuple(vec![Value::Str(DRIFTER_TAG.to_string())]),
            included_categories: int_list(&[CATEGORY_SHIP, CATEGORY_DRONE, CATEGORY_FIGHTER]),
        }
    }
}

/// `CreateCombatBehavior`.
pub fn create_combat_behavior(args: Combat) -> Node {
    sequence()
        .named("Combat")
        .add(
            Node::class("WaitForAggressiveAct")
                .named("Aggression Monitor")
                .with("combatTargetsAddress", combat_targets_set()),
        )
        .add(
            Node::class("MonitorAssistanceToTargets")
                .named("Monitor Enemy Assistance")
                .with("combatTargetsAddress", combat_targets_set()),
        )
        .add(
            Node::class("MonitorProximitySensors")
                .with("objectListAddress", combat_targets_set())
                .with("includedCategories", args.included_categories.clone())
                .with("excludedGroups", int_list(&[GROUP_CAPSULE]))
                .with("validOwnerIds", args.valid_owner_ids.clone())
                .with("invalidOwnerIds", Value::None)
                .with("tags", args.proximity_sensor_tags.clone()),
        )
        .add(
            Node::class("MonitorInvulnerabilityCanceledInBubble")
                .with("targetIdListAddress", combat_targets_set()),
        )
        .add(Node::class("MaxLockedTargetsChangedMonitor"))
        .add(Node::class("BlackboardMessageMonitor").with("messageAddress", combat_targets_set()))
        .add(create_select_target_behavior(
            &args.target_address,
            &args.target_evaluation_function,
            &args.preferred_module_groups,
            &args.group_primary_address,
        ))
        .add(create_get_combat_navigation_behavior(
            &args.orbit_range,
            &args.attack_range,
            &args.target_address,
        ))
        .add(create_attack_target_behavior(&args.orbit_velocity, &args.target_address))
}

/// `CreateOwnerAggressionBehavior`.
pub fn create_owner_aggression_behavior(owner_ids: Value) -> Node {
    Node::class("ForceFailure").named("Owner Aggression").add(
        Node::class("TargetWithOwnerEnteredBubbleMonitor")
            .with("targetSetAddress", combat_targets_set())
            .with("ownerIds", owner_ids),
    )
}

/// `CreateFriendlyTargetFilterBehavior`.
pub fn create_friendly_target_filter_behavior(friendly_owner_ids: Value) -> Node {
    Node::class("ForceFailure").named("Friendly Target Filter").add(
        Node::class("OwnerListFilterAction")
            .with("ownerIdSet", friendly_owner_ids)
            .with("itemIdSetAddress", combat_targets_set()),
    )
}

/// The arguments of `CreateEntityCombatBehavior`.
#[derive(Debug, Clone)]
pub struct EntityCombat {
    pub orbit_velocity: Value,
    pub orbit_range: Value,
    pub attack_range: Value,
    pub target_address: Address,
    pub group_primary_address: Address,
    pub target_evaluation_function: Value,
    pub preferred_module_groups: Value,
}

impl Default for EntityCombat {
    fn default() -> Self {
        EntityCombat {
            orbit_velocity: Value::None,
            orbit_range: Value::None,
            attack_range: Value::None,
            target_address: group_primary_target(),
            group_primary_address: group_primary_target(),
            target_evaluation_function: Value::Str(THREAT_TARGET_EVALUATOR.to_string()),
            preferred_module_groups: Value::List(Vec::new()),
        }
    }
}

/// `CreateEntityCombatBehavior`.
pub fn create_entity_combat_behavior(args: EntityCombat) -> Node {
    sequence()
        .named("Combat")
        .add(
            Node::class("WaitForAggressiveAct")
                .named("Aggression Monitor")
                .with("combatTargetsAddress", combat_targets_set()),
        )
        .add(
            Node::class("HasAnyEffectCondition")
                .named("Can I fight?")
                .with("effectIds", int_tuple(&COMBAT_EFFECTS)),
        )
        .add(Node::class("MaxLockedTargetsChangedMonitor"))
        .add(Node::class("BlackboardMessageMonitor").with("messageAddress", combat_targets_set()))
        .add(create_select_target_behavior(
            &args.target_address,
            &args.target_evaluation_function,
            &args.preferred_module_groups,
            &args.group_primary_address,
        ))
        .add(create_get_combat_navigation_behavior(
            &args.orbit_range,
            &args.attack_range,
            &args.target_address,
        ))
        .add(create_attack_target_behavior(&args.orbit_velocity, &args.target_address))
}

/// `CreateSelectTargetBehavior`.
pub fn create_select_target_behavior(
    selected_target_address: &Address,
    target_evaluation_function: &Value,
    preferred_module_groups: &Value,
    group_primary_address: &Address,
) -> Node {
    selector()
        .named("Select Target")
        .add(
            sequence()
                .add(Node::class("IsBlackboardValueNotNone").with("valueAddress", selected_target_address))
                .add(
                    Node::class("IsBlackboardValueInCollectionCondition")
                        .with("valueAddress", selected_target_address)
                        .with("collectionAddress", combat_targets_set()),
                )
                .add(
                    Node::class("IsBallPresentInMyBubbleCondition")
                        .with("ballIdAddress", selected_target_address),
                ),
        )
        .add(
            Node::class("FindTarget")
                .named("Get A Target")
                .with("selectedTargetAddress", selected_target_address)
                .with("potentialTargetListAddress", combat_targets_set())
                .with("includedCategories", Value::List(Vec::new()))
                .with("onlyCheckLocalBubble", true)
                .with("targetEvaluationFunction", target_evaluation_function.clone())
                .with("roleAddress", role_address())
                .with("preferredModuleGroups", preferred_module_groups.clone())
                .with("primaryTargetIdAddress", group_primary_address),
        )
}

/// `CreateAttackTargetBehavior`.
pub fn create_attack_target_behavior(orbit_velocity: &Value, target_address: &Address) -> Node {
    sequence()
        .add(
            Node::class("ForceSuccess").named("Disengage obsolete targets").add(
                sequence()
                    .add(
                        Node::class("Not").add(
                            Node::class("BlackboardMessagesEqualCondition")
                                .with("firstMessageAddress", my_combat_target())
                                .with("secondMessageAddress", target_address),
                        ),
                    )
                    .add(
                        Node::class("StopCombatEffectsOnTarget")
                            .named("Stop All Combat Effects")
                            .with("effectIds", int_tuple(&COMBAT_EFFECTS)),
                    ),
            ),
        )
        .add(
            Node::class("ClearValueAndResetIfTargetCloaks")
                .with("targetAddress", target_address)
                .with("valueAddress", target_address),
        )
        .add(
            Node::class("TargetLeavingParkMonitor")
                .with("targetAddress", target_address)
                .with("clearValue", true),
        )
        .add(
            Node::class("BlackboardCopyMessageAction")
                .with("sourceMessageAddress", target_address)
                .with("targetMessageAddress", my_combat_target()),
        )
        .add(Node::class("BlackboardMessageMonitor").with("messageAddress", target_address))
        .add(Node::class("TargetLostMonitor").with("targetAddress", target_address))
        .add(
            Node::class("TargetChangingBubbleMonitor")
                .with("targetAddress", target_address)
                .with("resetOnEnter", false),
        )
        .add(Node::class("ModifyOrbitVelocity").with("orbitVelocity", orbit_velocity.clone()))
        .add(
            Node::class("OrbitAtDistanceAction")
                .with("orbitTargetAddress", combat_orbit_target())
                .with("orbitRangeAddress", combat_orbit_range())
                .with("blocking", false)
                .with("blockUntilRange", combat_attack_range()),
        )
        .add(
            Node::class("ManageMicroWarpDriveToTarget")
                .with("targetAddress", target_address)
                .with("orbitRangeAddress", combat_orbit_range())
                .with("microWarpDriveDuration", 5000)
                .with("microWarpDriveChance", 1.0),
        )
        .add(
            selector()
                .named("Release target if required")
                .add(Node::class("HasAvailableTargetCapacity"))
                .add(Node::class("UnlockTarget")),
        )
        .add(
            Node::class("RepeatWhileFailing")
                .named("Retry")
                .with("maxRepetitions", 7)
                .add(Node::class("AcquireTargetLock").with("selectedTargetAddress", target_address)),
        )
        .add(
            Node::class("ActivateCombatEffectsOnTarget")
                .named("Start all Combat Effects on Target")
                .with("effectIds", int_tuple(&COMBAT_EFFECTS))
                .with("selectedTargetAddress", target_address)
                .with("repeat", 1000),
        )
        .add(Node::class("WaitAction"))
}

/// `CreateGetCombatNavigationBehavior`.
pub fn create_get_combat_navigation_behavior(
    orbit_range: &Value,
    attack_range: &Value,
    target_address: &Address,
) -> Node {
    sequence()
        .add(
            Node::class("GetTargetToOrbit")
                .named("Get Target To Orbit In Combat")
                .with("commanderAddress", commander_address())
                .with("combatTargetAddress", target_address)
                .with("orbitTargetAddress", combat_orbit_target())
                .with("roleAddress", role_address()),
        )
        .add(
            sequence()
                .add(
                    Node::class("ForceSuccess").add(
                        sequence()
                            .named("Get Combat Effects & Orbit Range")
                            .add(
                                Node::class("GetCombatEffectsForRole")
                                    .with("combatEffectsAddress", combat_effects_address())
                                    .with("roleAddress", role_address()),
                            )
                            .add(
                                Node::class("GetMyMinOptimalAndFalloff")
                                    .with("minOptimalAddress", combat_min_optimal_address())
                                    .with("minFalloffAddress", combat_min_falloff_address())
                                    .with("combatEffectsAddress", combat_effects_address()),
                            )
                            .add(
                                Node::class("GetCombatOrbitAndAttackRange")
                                    .with("orbitRangeAddress", combat_orbit_range())
                                    .with("attackRangeAddress", combat_attack_range())
                                    .with("orbitRange", orbit_range.clone())
                                    .with("attackRange", attack_range.clone())
                                    .with("minOptimalAddress", combat_min_optimal_address())
                                    .with("minFalloffAddress", combat_min_falloff_address()),
                            ),
                    ),
                )
                .add(
                    Node::class("ForceSuccess").add(
                        sequence()
                            .named("Overwriting Orbit Range if Orbiting Commander")
                            .add(
                                Node::class("BlackboardMessagesEqualCondition")
                                    .with("firstMessageAddress", combat_orbit_target())
                                    .with("secondMessageAddress", commander_address()),
                            )
                            .add(
                                Node::class("GetFleetCombatOrbitRangeByRole")
                                    .with("roleAddress", role_address())
                                    .with("orbitRangeAddress", combat_orbit_range()),
                            ),
                    ),
                ),
        )
}
