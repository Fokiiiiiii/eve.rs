//! The behavior of the NPCs this server places itself (the rats of a belt): a tree of the client's package, put together the way its own trees are
//! (`trees/miners/basicdpstree.py` is the pattern) out of the same parts. The client's package has the parts and no tree for a rat that waits until
//! somebody comes near and then fights: the trees it does have are the drifters', the sleepers', the miners'. This is the one server-authored tree.

use eve_behaviors::actions::combatnavigation::{DEFAULT_COMBAT_ATTACK_RANGE, DEFAULT_COMBAT_ORBIT_RANGE};
use eve_behaviors::constants::behaviorroles::ROLE_DPS;
use eve_behaviors::consts::{CATEGORY_DRONE, CATEGORY_ENTITY, CATEGORY_FIGHTER, CATEGORY_SHIP};
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
    fighter(
        "Belt Rat",
        Combat {
            orbit_velocity: Value::Int(ORBIT_VELOCITY),
            orbit_range: Value::Float(DEFAULT_COMBAT_ORBIT_RANGE),
            attack_range: Value::Float(DEFAULT_COMBAT_ATTACK_RANGE),
            proximity_sensor_tags: Value::Tuple(vec![Value::Str(RAT_TAG.to_string())]),
            ..Combat::default()
        },
    )
}

/// The name the tree of a Pochven NPC is given by (`Scene::spawn_npc_with_behavior`): a fighter of one of the four Pochven factions, which attacks the
/// NPCs of the others (`pochven.rs`) and the entities among them too.
pub const POCHVEN_NPC: &str = "pochven npc";

/// The tag of the proximity sensor of a Pochven NPC.
pub const POCHVEN_NPC_TAG: &str = "pochven_npc";

/// The tree of a Pochven NPC: the rat's tree, with entities in its sensors' reports (the fleets of the other factions are entities, category 11), and the
/// owners it attacks only the ones given (`pochven::hostile_corporations`).
pub fn create_pochven_npc_behavior_tree(valid_owner_ids: Vec<i64>) -> BehaviorTree {
    fighter(
        "Pochven NPC",
        Combat {
            valid_owner_ids: Value::List(valid_owner_ids.into_iter().map(Value::Int).collect()),
            orbit_velocity: Value::Int(ORBIT_VELOCITY),
            orbit_range: Value::Float(DEFAULT_COMBAT_ORBIT_RANGE),
            attack_range: Value::Float(DEFAULT_COMBAT_ATTACK_RANGE),
            proximity_sensor_tags: Value::Tuple(vec![Value::Str(POCHVEN_NPC_TAG.to_string())]),
            included_categories: Value::List(vec![
                Value::Int(CATEGORY_SHIP),
                Value::Int(CATEGORY_DRONE),
                Value::Int(CATEGORY_ENTITY),
                Value::Int(CATEGORY_FIGHTER),
            ]),
            ..Combat::default()
        },
    )
}

/// The shape every fighting NPC shares: say what its role is, pick the target that threatens it most, fight what came into its sensor's range or shot at
/// it, otherwise wait.
fn fighter(name: &str, combat: Combat) -> BehaviorTree {
    selector()
        .named(name)
        .add(create_register_entity_role(ROLE_DPS))
        .add(create_target_switching_behavior(TargetSwitching::default()))
        .add(create_combat_behavior(combat))
        .add(Node::class("WaitAction").named("Do nothing"))
        .into_tree()
}
