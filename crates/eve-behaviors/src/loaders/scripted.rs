//! `behaviors/loaders/scripted_behavior_loader.py`: the trees that the code builds, by the ids the behaviors of the entities and groups use.

use crate::blackboards::ScopeType;
use crate::blackboards::TaskId;
use crate::registry::get_task_registry;
use crate::tree::BehaviorTree;
use crate::trees::driftercommander::create_drifter_commander_behavior_tree;
use crate::trees::drifters::driftertree::{
    create_drifter_behavior_tree, create_hive_system_drifter_behavior_tree,
};
use crate::trees::dungeonboss::create_dungeon_boss_behavior_tree;
use crate::trees::fighters::create_fighter_behavior_tree;
use crate::trees::groups::incursions::{create_drifter_group_behavior_tree, create_group_behavior_tree};
use crate::trees::harvesting::create_harvest_outpost_behavior_tree;
use crate::trees::incursions::dungeondriftertree::create_dungeon_drifter_behavior_tree;
use crate::trees::incursions::dungeonseekertree::create_dungeon_seeker_behavior_tree;
use crate::trees::incursions::roamingdriftertree::create_roaming_drifter_behavior_tree;
use crate::trees::miners::antidps::create_anti_dps_behavior_tree;
use crate::trees::miners::antilogistics::create_anti_logistics_behavior_tree;
use crate::trees::miners::basicdpstree::create_basic_dps_behavior_tree;
use crate::trees::miners::tackler::create_tackler_behavior_tree;
use crate::trees::pirate::shadow_of_the_serpent_trees::{
    create_roaming_shadow_of_the_serpent_tree, create_shadow_of_the_serpentis_group_tree,
};
use crate::trees::reinforcements::create_reinforcement_tree;
use crate::trees::scoutsidle::create_analyze_tree;
use crate::trees::sleep::create_sleep_behavior_tree;
use crate::trees::sleeperscouts::{
    create_hive_system_sleeper_scout_behavior_tree, create_sleeper_scout_behavior_tree,
};
use crate::value::Value;

use super::LoaderError;

/// The code that builds a tree.
pub type TreeScript = fn() -> BehaviorTree;

/// `CreateGroupBehaviorTree()` of the client with its arguments left out.
fn create_default_group_behavior_tree() -> BehaviorTree {
    create_group_behavior_tree(Value::None, Value::None, false)
}

/// `ScriptedBehaviorLoader`.
pub struct ScriptedBehaviorLoader {
    /// `behaviors_by_scope_by_id`.
    behaviors_by_scope_by_id: Vec<(ScopeType, Vec<(&'static str, TreeScript)>)>,
}

impl ScriptedBehaviorLoader {
    pub fn new() -> Self {
        ScriptedBehaviorLoader {
            behaviors_by_scope_by_id: vec![
                (
                    ScopeType::Item,
                    vec![
                        ("sleep", create_sleep_behavior_tree as TreeScript),
                        ("scan", create_analyze_tree),
                        ("reinforce", create_reinforcement_tree),
                        ("harvest", create_harvest_outpost_behavior_tree),
                        ("Roaming Sleeper Scout", create_sleeper_scout_behavior_tree),
                        (
                            "Roaming Sleeper Scout In Hive System",
                            create_hive_system_sleeper_scout_behavior_tree,
                        ),
                        ("Drifter Battleship", create_drifter_behavior_tree),
                        ("Drifter Battleship In Hive System", create_hive_system_drifter_behavior_tree),
                        ("Drifter Commander", create_drifter_commander_behavior_tree),
                        ("Dungeon Boss", create_dungeon_boss_behavior_tree),
                        ("Incursion Dungeon Drifter", create_dungeon_drifter_behavior_tree),
                        ("Incursion Dungeon Seeker", create_dungeon_seeker_behavior_tree),
                        ("Incursion Roaming Drifter", create_roaming_drifter_behavior_tree),
                        ("fighter", create_fighter_behavior_tree),
                        ("Shadow Of The Serpent", create_roaming_shadow_of_the_serpent_tree),
                        ("dps", create_basic_dps_behavior_tree),
                        ("tackler", create_tackler_behavior_tree),
                        ("antilogistics", create_anti_logistics_behavior_tree),
                        ("antidps", create_anti_dps_behavior_tree),
                    ],
                ),
                (
                    ScopeType::EntityGroup,
                    vec![
                        ("sleep", create_sleep_behavior_tree as TreeScript),
                        ("Incursion Group", create_default_group_behavior_tree),
                        ("Incursion Drifter Group", create_drifter_group_behavior_tree),
                        ("Shadow Of The Serpent Group", create_shadow_of_the_serpentis_group_tree),
                    ],
                ),
            ],
        }
    }

    /// `load(behavior_id, scope_type)`. Without a scope type the client looks for the id in the scope types themselves, which are numbers, and raises
    /// a `TypeError`; so does this.
    pub fn load(
        &self,
        behavior_id: &str,
        scope_type: Option<ScopeType>,
    ) -> Result<BehaviorTree, LoaderError> {
        let script = match scope_type {
            Some(scope_type) => self
                .behaviors_by_scope_by_id
                .iter()
                .find(|(scope, _)| *scope == scope_type)
                .and_then(|(_, behaviors)| behaviors.iter().find(|(id, _)| *id == behavior_id))
                .map(|(_, script)| *script),
            None => {
                return Err(LoaderError::Malformed(
                    "TypeError: argument of type 'int' is not iterable".to_string(),
                ));
            }
        };
        let script = script.ok_or(LoaderError::BehaviorDoesNotExist)?;
        let mut behavior_tree = script();
        behavior_tree.core.behavior_id = Some(Value::Str(behavior_id.to_string()));
        behavior_tree.core.scope_type = scope_type;
        Ok(behavior_tree)
    }

    /// `get_id_and_names_by_scope(scope_type)`.
    pub fn get_id_and_names_by_scope(&self, scope_type: ScopeType) -> Vec<(String, String)> {
        self.behaviors_by_scope_by_id
            .iter()
            .find(|(scope, _)| *scope == scope_type)
            .map(|(_, behaviors)| {
                behaviors.iter().map(|(id, _)| (id.to_string(), format!("[Python] {id}"))).collect()
            })
            .unwrap_or_default()
    }

    /// `convert_script_to_fsd(behavior_id, scope_type)`: the tree the code builds, as the authored data would hold it.
    pub fn convert_script_to_fsd(
        &self,
        behavior_id: &str,
        scope_type: ScopeType,
    ) -> Result<Value, LoaderError> {
        let behavior_tree = self.load(behavior_id, Some(scope_type))?;
        let root_task = behavior_tree
            .root_task()
            .ok_or_else(|| LoaderError::Malformed("a tree without a root task".to_string()))?;
        let root = self.convert_task_to_dict(&behavior_tree, root_task)?;
        Ok(Value::Dict(vec![
            (Value::Str("name".to_string()), Value::Str(behavior_id.to_string())),
            (Value::Str("description".to_string()), Value::Str(behavior_id.to_string())),
            (Value::Str("scopeType".to_string()), Value::Str(scope_type.name().to_string())),
            (Value::Str("root".to_string()), root),
        ]))
    }

    /// `convert_task_to_dict(task)`.
    pub fn convert_task_to_dict(&self, tree: &BehaviorTree, task: TaskId) -> Result<Value, LoaderError> {
        let registry = get_task_registry();
        let type_name = tree.type_name_of(task);
        let entry = registry.get(type_name)?;
        let mut sub_tasks = Vec::new();
        for &sub_task in tree.sub_tasks_of(task) {
            sub_tasks.push(self.convert_task_to_dict(tree, sub_task)?);
        }
        let mut attributes: Vec<(String, Value)> =
            tree.attributes_of(task).iter().map(|(name, value)| (name.clone(), value.clone())).collect();
        attributes.sort_by(|a, b| a.0.cmp(&b.0));
        let mut converted = Vec::new();
        for (name, value) in attributes {
            let value = if name == "name" {
                value
            } else {
                // `task_attributes[name]`: an attribute the class does not register is a `KeyError`
                let spec = entry.attribute(&name).ok_or_else(|| LoaderError::TaskAttributeMissing {
                    task: entry.class_name.to_string(),
                    attribute: name.clone(),
                })?;
                spec.convert_attribute_to_fsd(&value)
            };
            converted.push((Value::Str(name), value));
        }
        Ok(Value::Dict(vec![
            (Value::Str("taskClass".to_string()), Value::Str(entry.class_name.to_string())),
            (Value::Str("attributes".to_string()), Value::Dict(converted)),
            (Value::Str("subTasks".to_string()), Value::List(sub_tasks)),
        ]))
    }
}

impl Default for ScriptedBehaviorLoader {
    fn default() -> Self {
        Self::new()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn every_script_builds_a_tree_and_every_task_of_it_is_registered() {
        let loader = ScriptedBehaviorLoader::new();
        for scope in [ScopeType::Item, ScopeType::EntityGroup] {
            let ids = loader.get_id_and_names_by_scope(scope);
            assert!(!ids.is_empty());
            for (id, name) in ids {
                assert_eq!(name, format!("[Python] {id}"));
                let tree = loader.load(&id, Some(scope)).unwrap_or_else(|e| panic!("{id}: {e:?}"));
                assert_eq!(tree.core.behavior_id, Some(Value::Str(id.clone())));
                assert_eq!(tree.core.scope_type, Some(scope));
                assert!(tree.root_task().is_some(), "{id}");
                // the tree is the data it can be saved as, and back
                let data = loader.convert_script_to_fsd(&id, scope).unwrap_or_else(|e| panic!("{id}: {e:?}"));
                assert_eq!(data.field("name"), Some(&Value::Str(id)));
            }
        }
    }

    #[test]
    fn the_ids_are_the_ones_of_the_client_and_a_scope_has_only_its_own() {
        let loader = ScriptedBehaviorLoader::new();
        let ids =
            |scope| loader.get_id_and_names_by_scope(scope).into_iter().map(|(id, _)| id).collect::<Vec<_>>();
        assert_eq!(ids(ScopeType::Item).len(), 19);
        assert_eq!(
            ids(ScopeType::EntityGroup),
            ["sleep", "Incursion Group", "Incursion Drifter Group", "Shadow Of The Serpent Group"]
        );
        assert!(ids(ScopeType::Dungeon).is_empty());
        assert_eq!(
            loader.load("Incursion Group", Some(ScopeType::Item)).err(),
            Some(LoaderError::BehaviorDoesNotExist)
        );
        assert_eq!(
            loader.load("nothing", Some(ScopeType::Item)).err(),
            Some(LoaderError::BehaviorDoesNotExist)
        );
    }

    #[test]
    fn without_a_scope_type_the_client_raises_a_type_error() {
        let loader = ScriptedBehaviorLoader::new();
        assert!(
            matches!(loader.load("sleep", None), Err(LoaderError::Malformed(ref m)) if m.starts_with("TypeError"))
        );
    }

    #[test]
    fn a_task_is_converted_to_the_dictionary_the_data_holds_with_its_addresses_as_dictionaries() {
        let loader = ScriptedBehaviorLoader::new();
        let data = loader.convert_script_to_fsd("sleep", ScopeType::Item).unwrap();
        let root = data.field("root").unwrap();
        assert_eq!(root.field("taskClass"), Some(&Value::Str("behaviors.actions.WaitAction".to_string())));
        assert_eq!(
            root.field("attributes").unwrap().field("name"),
            Some(&Value::Str("Sleep forever my pretty".to_string()))
        );
        let data = loader.convert_script_to_fsd("dps", ScopeType::Item).unwrap();
        let text = format!("{data:?}");
        assert!(text.contains("scopeType") && text.contains("messageName"), "an address is a dictionary");
    }
}
