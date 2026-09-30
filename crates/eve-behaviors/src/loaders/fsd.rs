//! `behaviors/loaders/fsd_behavior_loader.py`: behavior trees from the authored data. A tree is a dictionary (`name`, `scopeType`, `root`,
//! `parameters`), a task in it a dictionary (`taskClass`, `attributes`, `subTasks`, `uuid`); the class is one of the registry's, the attributes are
//! converted by what the class says of them, and a `ReferenceSubtree` is replaced by the tree it refers to, with the parameters the reference gives.

use std::collections::HashMap;

use crate::blackboards::{ScopeType, TaskId};
use crate::registry::{TaskRegistry, get_task_registry};
use crate::tree::{Attrs, BehaviorTree};
use crate::value::Value;

use super::LoaderError;

/// `BehaviortreeStorage`: the authored trees by their ids.
pub trait BehaviorTreeStorage {
    /// `storage.get(behavior_id)`.
    fn get(&self, behavior_id: &Value) -> Option<&Value>;

    /// `iter(storage)`.
    fn ids(&self) -> Vec<Value>;
}

/// Trees in a list, for a world that has its data in memory.
#[derive(Debug, Default, Clone)]
pub struct MemoryBehaviorTreeStorage {
    trees: Vec<(Value, Value)>,
}

impl MemoryBehaviorTreeStorage {
    pub fn new() -> Self {
        Self::default()
    }

    /// One tree more.
    pub fn insert(&mut self, behavior_id: Value, data: Value) {
        self.trees.retain(|(id, _)| !id.py_eq(&behavior_id));
        self.trees.push((behavior_id, data));
    }
}

impl BehaviorTreeStorage for MemoryBehaviorTreeStorage {
    fn get(&self, behavior_id: &Value) -> Option<&Value> {
        self.trees.iter().find(|(id, _)| id.py_eq(behavior_id)).map(|(_, data)| data)
    }

    fn ids(&self) -> Vec<Value> {
        self.trees.iter().map(|(id, _)| id.clone()).collect()
    }
}

/// The attribute overrides of the tasks of a subtree, by the uuid of the task: what a `ReferenceSubtree` sets in the tasks of the tree it refers to.
type OverridesByUuid = HashMap<String, Vec<(String, Value)>>;

/// The scope type a tree's data names.
fn scope_type_of(data: &Value) -> Option<ScopeType> {
    data.field("scopeType").and_then(Value::as_str).and_then(ScopeType::from_name)
}

/// `get_behavior_tree_data(behavior_id, scope_type)`.
fn get_behavior_tree_data<'a>(
    storage: &'a dyn BehaviorTreeStorage,
    behavior_id: &Value,
    scope_type: Option<ScopeType>,
) -> Result<&'a Value, LoaderError> {
    let data = storage.get(behavior_id).ok_or(LoaderError::BehaviorDoesNotExist)?;
    if let Some(scope_type) = scope_type
        && scope_type_of(data) != Some(scope_type)
    {
        return Err(LoaderError::IncompatibleScopeType);
    }
    Ok(data)
}

/// `get_id_and_names_by_scope(scope_type)`.
pub fn get_id_and_names_by_scope(
    storage: &dyn BehaviorTreeStorage,
    scope_type: ScopeType,
) -> Vec<(String, String)> {
    let mut ids_and_names = Vec::new();
    for behavior_id in storage.ids() {
        let Some(data) = storage.get(&behavior_id) else { continue };
        if scope_type_of(data) != Some(scope_type) {
            continue;
        }
        let name = data.field("name").and_then(Value::as_str).unwrap_or("<Unnamed FSD Behavior>");
        ids_and_names.push((behavior_id.to_string(), format!("[FSD] {name} ({behavior_id})")));
    }
    ids_and_names
}

/// `get_parameter_name(attribute_name)`: `'<task>.<parameter>'` names a parameter.
fn get_parameter_name(attribute_name: &str) -> Option<&str> {
    attribute_name.split('.').nth(1)
}

fn str_key(value: &Value) -> Option<&str> {
    value.as_str()
}

/// `get_attribute_overrides_by_uuid(parameter_overrides, parameters_values)`.
fn get_attribute_overrides_by_uuid(
    parameter_overrides: &Value,
    parameters_values: &[(String, Value)],
) -> Result<OverridesByUuid, LoaderError> {
    let mut overrides_by_uuid: OverridesByUuid = HashMap::new();
    for parameter in parameter_overrides.items().unwrap_or(&[]) {
        let list = parameter
            .field("attributeOverrideList")
            .ok_or_else(|| LoaderError::Malformed("KeyError: 'attributeOverrideList'".to_string()))?;
        for over in list.items().unwrap_or(&[]) {
            let task_uuid = over
                .field("taskUuid")
                .and_then(Value::as_str)
                .ok_or_else(|| LoaderError::Malformed("KeyError: 'taskUuid'".to_string()))?
                .to_string();
            let entry = overrides_by_uuid.entry(task_uuid).or_default();
            let attribute_name = over
                .field("attributeName")
                .and_then(Value::as_str)
                .ok_or_else(|| LoaderError::Malformed("KeyError: 'attributeName'".to_string()))?
                .to_string();
            let parameter_name = parameter
                .field("parameterName")
                .and_then(Value::as_str)
                .ok_or_else(|| LoaderError::Malformed("KeyError: 'parameterName'".to_string()))?;
            if let Some((_, value)) = parameters_values.iter().find(|(name, _)| name == parameter_name) {
                match entry.iter_mut().find(|(name, _)| *name == attribute_name) {
                    Some((_, existing)) => *existing = value.clone(),
                    None => entry.push((attribute_name, value.clone())),
                }
            }
        }
    }
    Ok(overrides_by_uuid)
}

/// `get_parameter_values_with_overrides(parameters, overrides)`.
fn get_parameter_values_with_overrides(
    parameters: Vec<(String, Value)>,
    overrides: Option<&Vec<(String, Value)>>,
) -> Vec<(String, Value)> {
    let Some(overrides) = overrides.filter(|overrides| !overrides.is_empty()) else { return parameters };
    let mut updated = parameters;
    for (parameter_name, parameter_value) in overrides {
        // a name without a `.` is not one of a parameter
        let Some(name) = get_parameter_name(parameter_name).filter(|_| parameter_name.contains('.')) else {
            continue;
        };
        match updated.iter_mut().find(|(existing, _)| existing == name) {
            Some((_, value)) => *value = parameter_value.clone(),
            None => updated.push((name.to_string(), parameter_value.clone())),
        }
    }
    updated
}

/// The pairs of a dictionary whose keys are text.
fn text_entries(value: &Value) -> Vec<(String, Value)> {
    match value {
        Value::Dict(entries) => entries
            .iter()
            .filter_map(|(key, entry)| str_key(key).map(|key| (key.to_string(), entry.clone())))
            .collect(),
        _ => Vec::new(),
    }
}

/// `FsdBehaviorLoader`.
pub struct FsdBehaviorLoader<'a> {
    registry: &'static TaskRegistry,
    storage: &'a dyn BehaviorTreeStorage,
    subtree_stack: Vec<Value>,
}

impl<'a> FsdBehaviorLoader<'a> {
    pub fn new(storage: &'a dyn BehaviorTreeStorage) -> Self {
        FsdBehaviorLoader { registry: get_task_registry(), storage, subtree_stack: Vec::new() }
    }

    /// `load(behavior_id, scope_type)`.
    pub fn load(
        &mut self,
        behavior_id: &Value,
        scope_type: Option<ScopeType>,
    ) -> Result<BehaviorTree, LoaderError> {
        let data = get_behavior_tree_data(self.storage, behavior_id, scope_type)?;
        let root_node =
            data.field("root").ok_or_else(|| LoaderError::Malformed("KeyError: 'root'".to_string()))?;
        let mut tree = BehaviorTree::new(0, Some(behavior_id.clone()), scope_type);
        self.subtree_stack.clear();
        let root_task = self.instantiate_tree(&mut tree, root_node, &OverridesByUuid::new())?;
        tree.start_root_task(root_task);
        Ok(tree)
    }

    /// `load_reference_subtree(behavior_id, scope_type, parameters_values)`: the tasks of the subtree are made in `tree`; the root is answered.
    pub fn load_reference_subtree(
        &mut self,
        tree: &mut BehaviorTree,
        behavior_id: &Value,
        scope_type: Option<ScopeType>,
        parameters_values: &[(String, Value)],
    ) -> Result<TaskId, LoaderError> {
        if self.subtree_stack.iter().any(|id| id.py_eq(behavior_id)) {
            return Err(LoaderError::CircularReference(format!(
                "Detected circular references while loading subtree {behavior_id}. Stack was {:?}",
                self.subtree_stack.iter().map(Value::to_string).collect::<Vec<_>>()
            )));
        }
        self.subtree_stack.push(behavior_id.clone());
        let data = get_behavior_tree_data(self.storage, behavior_id, scope_type)?;
        let root_node =
            data.field("root").ok_or_else(|| LoaderError::Malformed("KeyError: 'root'".to_string()))?;
        let parameter_overrides = data.field("parameters").cloned().unwrap_or(Value::List(Vec::new()));
        let overrides_by_uuid = get_attribute_overrides_by_uuid(&parameter_overrides, parameters_values)?;
        let root_task = self.instantiate_tree(tree, root_node, &overrides_by_uuid)?;
        self.subtree_stack.pop();
        Ok(root_task)
    }

    /// `_instantiate_tree(node, overrides_by_uuid)`.
    fn instantiate_tree(
        &mut self,
        tree: &mut BehaviorTree,
        node: &Value,
        overrides_by_uuid: &OverridesByUuid,
    ) -> Result<TaskId, LoaderError> {
        let task_name = node
            .field("taskClass")
            .and_then(Value::as_str)
            .ok_or_else(|| LoaderError::Malformed("KeyError: 'taskClass'".to_string()))?;
        let entry = self.registry.get(task_name)?;
        let attributes = self.get_task_attributes(node, entry.class_name, overrides_by_uuid)?;
        if task_name.ends_with("ReferenceSubtree") {
            let subtree_id = attributes.value("behaviorTreeId");
            let uuid = node
                .field("uuid")
                .and_then(Value::as_str)
                .ok_or_else(|| LoaderError::Malformed("KeyError: 'uuid'".to_string()))?;
            let parameter_values = get_parameter_values_with_overrides(
                text_entries(&attributes.value("parameters")),
                overrides_by_uuid.get(uuid),
            );
            return self.load_reference_subtree(tree, &subtree_id, None, &parameter_values);
        }
        let task = tree.add_task((entry.factory)(&attributes), attributes);
        for child_node in node.field("subTasks").and_then(Value::items).unwrap_or(&[]) {
            let child_task = self.instantiate_tree(tree, child_node, overrides_by_uuid)?;
            tree.add_sub_task(task, child_task);
        }
        Ok(task)
    }

    /// `_get_task_attributes(node, task_name, overrides_by_uuid)`: what the class registers, converted, and the name.
    fn get_task_attributes(
        &self,
        node: &Value,
        task_name: &str,
        overrides_by_uuid: &OverridesByUuid,
    ) -> Result<Attrs, LoaderError> {
        let entry = self.registry.get(task_name)?;
        // `get_attributes_with_overrides`
        let mut node_attributes = text_entries(node.field("attributes").unwrap_or(&Value::None));
        if let Some(overrides) =
            node.field("uuid").and_then(Value::as_str).and_then(|uuid| overrides_by_uuid.get(uuid))
        {
            for (name, value) in overrides {
                match node_attributes.iter_mut().find(|(existing, _)| existing == name) {
                    Some((_, existing)) => *existing = value.clone(),
                    None => node_attributes.push((name.clone(), value.clone())),
                }
            }
        }
        let find = |name: &str| {
            node_attributes.iter().find(|(existing, _)| existing == name).map(|(_, value)| value)
        };
        // `_get_converted_attributes`: `name`, then the attributes of the class, in the order of its registration
        let mut attributes = Attrs::new();
        attributes.set("name", find("name").cloned().unwrap_or(Value::Str(String::new())));
        for spec in entry.attributes {
            let value = match find(spec.name) {
                Some(value) => {
                    spec.convert_fsd_to_attribute(value).map_err(|error| LoaderError::Convert {
                        task: entry.class_name.to_string(),
                        attribute: spec.name.to_string(),
                        error,
                    })?
                }
                None if spec.is_optional => Value::None,
                None => {
                    return Err(LoaderError::TaskAttributeMissing {
                        task: entry.class_name.to_string(),
                        attribute: spec.name.to_string(),
                    });
                }
            };
            attributes.set(spec.name, value);
        }
        Ok(attributes)
    }

    /// `get_id_and_names_by_scope(scope_type)`.
    pub fn get_id_and_names_by_scope(&self, scope_type: ScopeType) -> Vec<(String, String)> {
        get_id_and_names_by_scope(self.storage, scope_type)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::tree::Status;
    use crate::tree::tests::Rig;

    fn dict(entries: Vec<(&str, Value)>) -> Value {
        Value::Dict(entries.into_iter().map(|(key, value)| (Value::Str(key.to_string()), value)).collect())
    }

    fn text(value: &str) -> Value {
        Value::Str(value.to_string())
    }

    fn address(scope: &str, name: &str) -> Value {
        dict(vec![("scopeType", text(scope)), ("messageName", text(name))])
    }

    fn node(class: &str, attributes: Vec<(&str, Value)>, sub_tasks: Vec<Value>, uuid: Option<&str>) -> Value {
        let mut entries = vec![
            ("taskClass", text(class)),
            ("attributes", dict(attributes)),
            ("subTasks", Value::List(sub_tasks)),
        ];
        if let Some(uuid) = uuid {
            entries.push(("uuid", text(uuid)));
        }
        dict(entries)
    }

    fn tree_data(scope: &str, root: Value) -> Value {
        dict(vec![("name", text("A tree")), ("scopeType", text(scope)), ("root", root)])
    }

    fn storage(trees: Vec<(i64, Value)>) -> MemoryBehaviorTreeStorage {
        let mut storage = MemoryBehaviorTreeStorage::new();
        for (id, data) in trees {
            storage.insert(Value::Int(id), data);
        }
        storage
    }

    #[test]
    fn a_tree_is_made_of_the_registered_classes_with_the_attributes_converted() {
        let root = node(
            "behaviors.composites.Sequence",
            vec![("name", text("Root"))],
            vec![
                node(
                    "behaviors.conditions.blackboards.IsBlackboardValueNone",
                    vec![("valueAddress", address("item", "X"))],
                    vec![],
                    None,
                ),
                node("behaviors.actions.WaitAction", vec![], vec![], None),
            ],
            None,
        );
        let storage = storage(vec![(7, tree_data("item", root))]);
        let mut loader = FsdBehaviorLoader::new(&storage);
        let tree = loader.load(&Value::Int(7), Some(ScopeType::Item)).unwrap();
        assert_eq!(tree.core.behavior_id, Some(Value::Int(7)));
        assert_eq!(tree.core.scope_type, Some(ScopeType::Item));
        let root = tree.root_task().unwrap();
        assert_eq!(tree.type_name_of(root), "Sequence");
        assert_eq!(tree.attributes_of(root).value("name"), text("Root"));
        let subs = tree.sub_tasks_of(root).to_vec();
        assert_eq!(subs.len(), 2);
        assert_eq!(tree.type_name_of(subs[0]), "IsBlackboardValueNone");
        // an address is a `(scope type, message name)`; a task with no name has the name of nothing
        assert_eq!(
            tree.attributes_of(subs[0]).value("valueAddress"),
            Value::Tuple(vec![text("item"), text("X")])
        );
        assert_eq!(tree.attributes_of(subs[1]).value("name"), text(""));
    }

    #[test]
    fn a_tree_that_is_made_runs() {
        let root = node("behaviors.actions.WaitAction", vec![], vec![], None);
        let storage = storage(vec![(7, tree_data("item", root))]);
        let mut loader = FsdBehaviorLoader::new(&storage);
        let mut rig = Rig::new();
        rig.tree = loader.load(&Value::Int(7), None).unwrap();
        rig.tick().unwrap();
        assert_eq!(rig.tree.status_of(rig.tree.root_task().unwrap()), Status::Suspended);
    }

    #[test]
    fn an_attribute_that_is_missing_is_an_error_unless_it_is_optional_and_then_it_is_nothing() {
        let condition = |attributes| {
            node("behaviors.conditions.ballparks.IsCoordinateWithinDistance", attributes, vec![], None)
        };
        let storage = storage(vec![
            (1, tree_data("item", condition(vec![("coordinateAddress", address("item", "C"))]))),
            (
                2,
                tree_data(
                    "item",
                    node(
                        "behaviors.conditions.ballparks.IsCoordinateWithinDistanceByAddress",
                        vec![
                            ("coordinateAddress", address("item", "C")),
                            ("maxDistanceAddress", address("item", "D")),
                        ],
                        vec![],
                        None,
                    ),
                ),
            ),
        ]);
        let mut loader = FsdBehaviorLoader::new(&storage);
        assert_eq!(
            loader.load(&Value::Int(1), None).err(),
            Some(LoaderError::TaskAttributeMissing {
                task: "behaviors.conditions.ballparks.IsCoordinateWithinDistance".to_string(),
                attribute: "maxDistance".to_string()
            })
        );
        let tree = loader.load(&Value::Int(2), None).unwrap();
        let root = tree.root_task().unwrap();
        assert_eq!(tree.attributes_of(root).value("fudgeDistanceAddress"), Value::None);
    }

    #[test]
    fn a_tree_that_is_not_there_or_is_of_another_scope_or_names_a_class_that_is_not_registered_is_an_error() {
        let storage = storage(vec![
            (1, tree_data("entity_group", node("behaviors.actions.WaitAction", vec![], vec![], None))),
            (2, tree_data("item", node("behaviors.actions.NoSuchAction", vec![], vec![], None))),
        ]);
        let mut loader = FsdBehaviorLoader::new(&storage);
        assert_eq!(loader.load(&Value::Int(9), None).err(), Some(LoaderError::BehaviorDoesNotExist));
        assert_eq!(
            loader.load(&Value::Int(1), Some(ScopeType::Item)).err(),
            Some(LoaderError::IncompatibleScopeType)
        );
        assert!(loader.load(&Value::Int(1), Some(ScopeType::EntityGroup)).is_ok());
        assert!(matches!(
            loader.load(&Value::Int(2), None).err(),
            Some(LoaderError::Registry(RegistryError::UnknownTask(_)))
        ));
    }

    use crate::registry::RegistryError;

    #[test]
    fn the_ids_and_names_of_the_trees_of_a_scope_are_listed() {
        let storage = storage(vec![
            (1, tree_data("entity_group", node("behaviors.actions.WaitAction", vec![], vec![], None))),
            (2, dict(vec![("scopeType", text("item")), ("root", Value::None)])),
            (3, tree_data("item", Value::None)),
        ]);
        let listed = get_id_and_names_by_scope(&storage, ScopeType::Item);
        assert_eq!(
            listed,
            [
                ("2".to_string(), "[FSD] <Unnamed FSD Behavior> (2)".to_string()),
                ("3".to_string(), "[FSD] A tree (3)".to_string())
            ]
        );
    }

    fn subtree() -> Value {
        // a tree with one parameter that sets the `name` of the task with the uuid `wait`
        dict(vec![
            ("name", text("Subtree")),
            ("scopeType", text("item")),
            (
                "root",
                node("behaviors.actions.WaitAction", vec![("name", text("Default"))], vec![], Some("wait")),
            ),
            (
                "parameters",
                Value::List(vec![dict(vec![
                    ("parameterName", text("what")),
                    (
                        "attributeOverrideList",
                        Value::List(vec![dict(vec![
                            ("taskUuid", text("wait")),
                            ("attributeName", text("name")),
                        ])]),
                    ),
                ])]),
            ),
        ])
    }

    fn reference(subtree_id: i64, parameters: Value, uuid: &str) -> Value {
        node(
            "behaviors.actions.reference_subtree.ReferenceSubtree",
            vec![("behaviorTreeId", Value::Int(subtree_id)), ("parameters", parameters)],
            vec![],
            Some(uuid),
        )
    }

    #[test]
    fn a_reference_to_a_subtree_is_the_subtree_with_the_parameters_the_reference_gives() {
        let root = node(
            "behaviors.composites.Sequence",
            vec![],
            vec![
                reference(5, dict(vec![("what", text("Given"))]), "ref1"),
                reference(5, dict(vec![]), "ref2"),
            ],
            None,
        );
        let storage = storage(vec![(1, tree_data("item", root)), (5, subtree())]);
        let mut loader = FsdBehaviorLoader::new(&storage);
        let tree = loader.load(&Value::Int(1), None).unwrap();
        let subs = tree.sub_tasks_of(tree.root_task().unwrap()).to_vec();
        assert_eq!(tree.type_name_of(subs[0]), "WaitAction", "the reference is replaced");
        assert_eq!(tree.attributes_of(subs[0]).value("name"), text("Given"));
        assert_eq!(tree.attributes_of(subs[1]).value("name"), text("Default"), "no parameter, no override");
    }

    #[test]
    fn a_parameter_of_the_reference_can_be_overridden_by_a_task_of_the_tree_that_refers() {
        // the outer tree is itself a subtree, whose task with the uuid `ref` has the parameter `what` overridden by `outer.what`
        let outer = dict(vec![
            ("name", text("Outer")),
            ("scopeType", text("item")),
            ("root", reference(5, dict(vec![("what", text("Inner"))]), "ref")),
            (
                "parameters",
                Value::List(vec![dict(vec![
                    ("parameterName", text("outerWhat")),
                    (
                        "attributeOverrideList",
                        Value::List(vec![dict(vec![
                            ("taskUuid", text("ref")),
                            ("attributeName", text("x.what")),
                        ])]),
                    ),
                ])]),
            ),
        ]);
        let root = reference(6, dict(vec![("outerWhat", text("FromTop"))]), "top");
        let storage = storage(vec![(1, tree_data("item", root)), (5, subtree()), (6, outer)]);
        let mut loader = FsdBehaviorLoader::new(&storage);
        let tree = loader.load(&Value::Int(1), None).unwrap();
        assert_eq!(tree.attributes_of(tree.root_task().unwrap()).value("name"), text("FromTop"));
    }

    #[test]
    fn a_tree_that_refers_to_itself_is_an_error() {
        let looping = dict(vec![
            ("name", text("Loop")),
            ("scopeType", text("item")),
            ("root", reference(5, dict(vec![]), "r")),
        ]);
        let storage = storage(vec![(1, tree_data("item", reference(5, dict(vec![]), "top"))), (5, looping)]);
        let mut loader = FsdBehaviorLoader::new(&storage);
        assert!(matches!(loader.load(&Value::Int(1), None).err(), Some(LoaderError::CircularReference(_))));
    }

    #[test]
    fn the_subtree_that_is_a_reference_twice_but_not_in_itself_is_no_loop() {
        let root = node(
            "behaviors.composites.Sequence",
            vec![],
            vec![reference(5, dict(vec![]), "a"), reference(5, dict(vec![]), "b")],
            None,
        );
        let storage = storage(vec![(1, tree_data("item", root)), (5, subtree())]);
        let mut loader = FsdBehaviorLoader::new(&storage);
        assert!(loader.load(&Value::Int(1), None).is_ok());
    }
}
