//! `behaviors/trees/*`: the behavior trees the client's package builds in code (the scripted trees), out of the tasks of the other modules. A tree is
//! written as the client writes it: a task, its attributes, and what it holds (`Sequence(Bundle(name=...)).AddSubTask(...)`); [`builder::Node`] is
//! the task before it is put in a tree, and a task is named by its class as the registry knows it.

pub mod builder;
pub mod combat;
pub mod corpsedisposal;
pub mod corpseharvesting;
pub mod driftercommander;
pub mod drifters;
pub mod dungeonboss;
pub mod explore;
pub mod fighters;
pub mod groups;
pub mod guardobject;
pub mod harvesting;
pub mod incursions;
pub mod miners;
pub mod patrol;
pub mod pirate;
pub mod pursuit;
pub mod regroup;
pub mod reinforcements;
pub mod role;
pub mod scoutexplore;
pub mod scoutsidle;
pub mod sleep;
pub mod sleeperscouts;
pub mod tanking;
pub mod travel;
pub mod unspawn;

/// `npcs.server.tags.DRIFTER_TAG`: the tag the proximity sensors of the drifters are registered under. The server's module is not part of the
/// client's files; a tag is only a name that the world and the trees agree on, and here they are the same crate.
pub const DRIFTER_TAG: &str = "drifter";

#[cfg(test)]
mod tests {
    use super::*;
    use crate::tree::BehaviorTree;

    type Script = fn() -> BehaviorTree;

    /// A tree that names a task the registry does not have panics when it is built; each tree is built once here.
    #[test]
    fn every_tree_of_the_client_is_built_of_registered_tasks() {
        let trees: [(&str, Script); 24] = [
            ("sleep", sleep::create_sleep_behavior_tree),
            ("reinforcement", reinforcements::create_reinforcement_tree),
            ("analyze", scoutsidle::create_analyze_tree),
            ("harvest", harvesting::create_harvest_outpost_behavior_tree),
            ("sleeper scout", sleeperscouts::create_sleeper_scout_behavior_tree),
            ("hive sleeper scout", sleeperscouts::create_hive_system_sleeper_scout_behavior_tree),
            ("drifter", drifters::driftertree::create_drifter_behavior_tree),
            ("hive drifter", drifters::driftertree::create_hive_system_drifter_behavior_tree),
            ("drifter commander", driftercommander::create_drifter_commander_behavior_tree),
            ("dungeon boss", dungeonboss::create_dungeon_boss_behavior_tree),
            ("dungeon drifter", incursions::dungeondriftertree::create_dungeon_drifter_behavior_tree),
            ("dungeon seeker", incursions::dungeonseekertree::create_dungeon_seeker_behavior_tree),
            ("roaming drifter", incursions::roamingdriftertree::create_roaming_drifter_behavior_tree),
            ("fighter", fighters::create_fighter_behavior_tree),
            (
                "shadow of the serpent",
                pirate::shadow_of_the_serpent_trees::create_roaming_shadow_of_the_serpent_tree,
            ),
            ("dps", miners::basicdpstree::create_basic_dps_behavior_tree),
            ("tackler", miners::tackler::create_tackler_behavior_tree),
            ("anti logistics", miners::antilogistics::create_anti_logistics_behavior_tree),
            ("anti dps", miners::antidps::create_anti_dps_behavior_tree),
            ("drifter group", groups::incursions::create_drifter_group_behavior_tree),
            (
                "serpentis group",
                pirate::shadow_of_the_serpent_trees::create_shadow_of_the_serpentis_group_tree,
            ),
            ("group", || {
                groups::incursions::create_group_behavior_tree(
                    crate::value::Value::None,
                    crate::value::Value::None,
                    false,
                )
            }),
            ("idle", || scoutsidle::create_idle_behavior().into_tree()),
            ("patrol", || patrol::create_idle_patrol_behavior().into_tree()),
        ];
        for (name, create) in trees {
            let tree = create();
            let root = tree.root_task().unwrap_or_else(|| panic!("{name}: no root"));
            assert!(!tree.sub_tasks_of(root).is_empty() || name == "sleep" || name == "fighter", "{name}");
        }
    }

    #[test]
    fn the_role_a_tree_registers_and_the_addresses_it_shares_are_the_clients() {
        assert_eq!(role::role_address().name, "ROLE");
        assert_eq!(combat::combat_targets_set().name, "COMBAT_TARGETS_SET");
        assert_eq!(travel::warp_to_location_group().name, "WARP_TO_LOCATION_GROUP");
        assert_eq!(fighters::addresses_by_slot_id(1).0.name, "ABILITY_TARGET_SLOT1_ADDRESS");
        assert_eq!(
            fighters::activation_failure_reason_address(2).name,
            "ABILITY_FAILURE_REASON_SLOT2_ADDRESS"
        );
    }
}
