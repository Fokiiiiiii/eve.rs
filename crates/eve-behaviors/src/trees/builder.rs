//! A task that is not in a tree yet, with its attributes and what it holds: how the client writes its trees (`Task(Bundle(...)).AddSubTask(...)`).

use crate::blackboards::{Address, TaskId};
use crate::registry::get_task_registry;
use crate::tree::{Attrs, BehaviorTree, Task};
use crate::value::Value;

/// The class of a task: a task that is made, or a class of the registry that is made when the tree is (with the attributes it has by then).
enum Class {
    Made(Box<dyn Task>),
    Registered(String),
}

/// A task with its attributes and its sub tasks.
pub struct Node {
    class: Class,
    attrs: Attrs,
    sub_tasks: Vec<Node>,
}

impl Node {
    /// A task that is made already.
    pub fn new(task: impl Task + 'static) -> Self {
        Node { class: Class::Made(Box::new(task)), attrs: Attrs::new(), sub_tasks: Vec::new() }
    }

    /// A task by the name of its class in the registry (`Sequence`, `behaviors.actions.WaitAction`), as the trees of the client's data name them.
    pub fn class(name: &str) -> Self {
        Node { class: Class::Registered(name.to_string()), attrs: Attrs::new(), sub_tasks: Vec::new() }
    }

    /// `Bundle(name=...)`.
    pub fn named(self, name: &str) -> Self {
        self.with("name", name)
    }

    /// One attribute of the `Bundle`.
    pub fn with(mut self, name: &str, value: impl Into<Value>) -> Self {
        self.attrs.set(name, value);
        self
    }

    /// `AddSubTask`.
    #[allow(clippy::should_implement_trait)]
    pub fn add(mut self, sub_task: Node) -> Self {
        self.sub_tasks.push(sub_task);
        self
    }

    /// The task and what it holds, made in the tree.
    pub fn build(self, tree: &mut BehaviorTree) -> TaskId {
        let task = match self.class {
            Class::Made(task) => task,
            Class::Registered(name) => {
                get_task_registry().create(&name, &self.attrs).unwrap_or_else(|error| {
                    panic!("a tree names a task that the registry does not have: {error:?}")
                })
            }
        };
        let id = tree.add_task(task, self.attrs);
        for sub_task in self.sub_tasks {
            let sub_task = sub_task.build(tree);
            tree.add_sub_task(id, sub_task);
        }
        id
    }

    /// `behaviorTree = BehaviorTree(); behaviorTree.StartRootTask(root)`: the tree has no id and no behavior id; who loads it says.
    pub fn into_tree(self) -> BehaviorTree {
        let mut tree = BehaviorTree::new(0, None, None);
        let root = self.build(&mut tree);
        tree.start_root_task(root);
        tree
    }
}

/// `composites.Sequence()`.
pub fn sequence() -> Node {
    Node::class("Sequence")
}

/// `composites.PrioritySelector()`.
pub fn selector() -> Node {
    Node::class("PrioritySelector")
}

/// A list of whole numbers.
pub fn int_list(ids: &[i64]) -> Value {
    Value::List(ids.iter().map(|&id| Value::Int(id)).collect())
}

/// A tuple of whole numbers.
pub fn int_tuple(ids: &[i64]) -> Value {
    Value::Tuple(ids.iter().map(|&id| Value::Int(id)).collect())
}

/// A set of whole numbers (as the client writes `{a, b}`; in the order they are written).
pub fn int_set(ids: &[i64]) -> Value {
    Value::set(ids.iter().map(|&id| Value::Int(id)))
}

/// A blackboard address as an attribute.
pub fn address_value(address: &Address) -> Value {
    address.to_value()
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::tree::Status;
    use crate::tree::tests::Rig;

    #[test]
    fn a_node_is_built_with_its_attributes_and_sub_tasks_in_the_order_they_were_added() {
        let node = sequence()
            .named("root")
            .with("x", 5)
            .add(Node::class("WaitAction").named("first"))
            .add(selector().add(Node::class("WaitAction").named("nested")));
        let tree = node.into_tree();
        let root = tree.root_task().unwrap();
        assert_eq!(tree.attributes_of(root).value("name"), Value::Str("root".into()));
        assert_eq!(tree.attributes_of(root).value("x"), Value::Int(5));
        let subs = tree.sub_tasks_of(root).to_vec();
        assert_eq!(subs.len(), 2);
        assert_eq!(tree.attributes_of(subs[0]).value("name"), Value::Str("first".into()));
        assert_eq!(tree.sub_tasks_of(subs[1]).len(), 1);
        assert_eq!(tree.status_of(root), Status::Invalid);
        let _ = Rig::new();
    }

    #[test]
    fn values_are_written_as_lists_tuples_and_sets() {
        assert_eq!(int_list(&[1, 2]), Value::List(vec![Value::Int(1), Value::Int(2)]));
        assert_eq!(int_tuple(&[1]), Value::Tuple(vec![Value::Int(1)]));
        assert_eq!(int_set(&[1, 1, 2]), Value::Set(vec![Value::Int(1), Value::Int(2)]));
    }
}
