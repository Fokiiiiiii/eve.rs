//! The behavior of the NPCs this server places itself (the rats of a belt): a tree of the client's package, put together the way its own trees are
//! (`trees/miners/basicdpstree.py` is the pattern) out of the same parts. The client's package has the parts and no tree for a rat that waits until
//! somebody comes near and then fights: the trees it does have are the drifters', the sleepers', the miners'. This is the one server-authored tree.

use eve_behaviors::actions::combatnavigation::{DEFAULT_COMBAT_ATTACK_RANGE, DEFAULT_COMBAT_ORBIT_RANGE};
use eve_behaviors::constants::behaviorroles::ROLE_DPS;
use eve_behaviors::tree::BehaviorTree;
use eve_behaviors::trees::builder::{Node, selector};
use eve_behaviors::trees::combat::{
    Combat, TargetSwitching, create_combat_behavior, create_target_switching_behavior,
};
use eve_behaviors::trees::role::create_register_entity_role;
use eve_behaviors::value::Value;

/// The name the tree is given an NPC by (`Scene::spawn_npc_with_behavior`).
pub const BELT_RAT: &str = "belt rat";

/// The tag of the proximity sensor of a rat: what the rat's combat behavior listens to.
pub const RAT_TAG: &str = "belt_rat";

/// The speed a rat orbits its target at, m/s.
const ORBIT_VELOCITY: i64 = 300;

/// A rat: say what its role is, pick the target that threatens it most, fight what came into its sensor's range or shot at it, otherwise wait.
pub fn create_belt_rat_behavior_tree() -> BehaviorTree {
    selector()
        .named("Belt Rat")
        .add(create_register_entity_role(ROLE_DPS))
        .add(create_target_switching_behavior(TargetSwitching::default()))
        .add(create_combat_behavior(Combat {
            orbit_velocity: Value::Int(ORBIT_VELOCITY),
            orbit_range: Value::Float(DEFAULT_COMBAT_ORBIT_RANGE),
            attack_range: Value::Float(DEFAULT_COMBAT_ATTACK_RANGE),
            proximity_sensor_tags: Value::Tuple(vec![Value::Str(RAT_TAG.to_string())]),
            ..Combat::default()
        }))
        .add(Node::class("WaitAction").named("Do nothing"))
        .into_tree()
}
