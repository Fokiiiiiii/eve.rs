//! `behaviors/registry/*`: which task classes the behavior trees of the FSD data can name, of what kind they are, what attributes they take, and how
//! to make one. The client registers Python classes by their full names (`behaviors.actions.ballparks.WarpToNewLocation`); here a class is made by a
//! function. The tables (`actions`, `conditions`, `composites`, `decorators`, `monitors`) follow the client's registration one to one.

use std::collections::HashMap;
use std::sync::OnceLock;

use crate::tree::{Attrs, Task};
use crate::value::Value;

pub mod actions;
pub mod composites;
pub mod conditions;
pub mod decorators;
pub mod monitors;

/// `COMPOSITE`, `ACTION`, `CONDITION`, `DECORATOR`, `MONITOR`.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum TaskKind {
    Composite,
    Action,
    Condition,
    Decorator,
    Monitor,
}

impl TaskKind {
    /// The name the client gives the kind of a task.
    pub fn name(self) -> &'static str {
        match self {
            TaskKind::Composite => "composite",
            TaskKind::Action => "action",
            TaskKind::Condition => "condition",
            TaskKind::Decorator => "decorator",
            TaskKind::Monitor => "monitor",
        }
    }
}

/// What the value of an attribute is in the FSD data, and what it is in a task (`fsd.attributes`): most are what they are, an address is a dictionary
/// in the data and a `(scope type, message name)` in the task.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum AttributeKind {
    Plain,
    /// `BlackboardAddressAttribute`.
    BlackboardAddress,
    /// `BlackboardAddressListAttribute`.
    BlackboardAddressList,
    /// `BlackboardAddressByFleetTypeAttribute` (and `ItemIdSetByFleetTypeAttribute`).
    BlackboardAddressByFleetType,
    /// `FleetTypeAttribute`.
    FleetType,

    OwnerIdToSpawnTableIdDictionary,

    IntKeyDictionary,
}

/// What an attribute is when the data does not say (`NO_DEFAULT` for none).
#[derive(Debug, Clone, Copy, PartialEq)]
pub enum DefaultValue {
    NoDefault,
    None,
    Bool(bool),
    Int(i64),
    Float(f64),
    Str(&'static str),
    EmptyList,
    EmptyDict,
}

impl DefaultValue {
    /// The value, for an attribute that has a default.
    pub fn value(self) -> Option<Value> {
        match self {
            DefaultValue::NoDefault => None,
            DefaultValue::None => Some(Value::None),
            DefaultValue::Bool(b) => Some(Value::Bool(b)),
            DefaultValue::Int(i) => Some(Value::Int(i)),
            DefaultValue::Float(f) => Some(Value::Float(f)),
            DefaultValue::Str(s) => Some(Value::Str(s.to_string())),
            DefaultValue::EmptyList => Some(Value::List(Vec::new())),
            DefaultValue::EmptyDict => Some(Value::Dict(Vec::new())),
        }
    }
}

/// What the data could not be converted from.
#[derive(Debug, Clone, PartialEq)]
pub enum ConvertError {
    /// A value that should be a whole number (a key, a fleet type) is not.
    NotAnInteger(String),
    /// A blackboard address that is not a `{scopeType, messageName}` dictionary.
    NotAnAddress(String),
    /// A value that should be a dictionary or a list is not.
    NotACollection(String),
}

/// `DataAttribute`: an attribute a task class takes.
#[derive(Debug, Clone, Copy)]
pub struct AttributeSpec {
    pub name: &'static str,
    /// The client's class of the attribute (`BlackboardAddressAttribute`).
    pub type_name: &'static str,
    pub kind: AttributeKind,
    pub is_optional: bool,
    /// Where the attribute is in the editor (999999 when it does not say; the client's data has one at 4.1).
    pub index: f64,
    pub default: DefaultValue,
    pub description: Option<&'static str>,
}

impl AttributeSpec {
    pub const fn new(name: &'static str, type_name: &'static str, kind: AttributeKind) -> Self {
        AttributeSpec {
            name,
            type_name,
            kind,
            is_optional: false,
            index: 999_999.0,
            default: DefaultValue::NoDefault,
            description: None,
        }
    }

    pub const fn index(mut self, index: f64) -> Self {
        self.index = index;
        self
    }

    pub const fn optional(mut self) -> Self {
        self.is_optional = true;
        self
    }

    pub const fn default(mut self, default: DefaultValue) -> Self {
        self.default = default;
        self
    }

    pub const fn described(mut self, description: &'static str) -> Self {
        self.description = Some(description);
        self
    }

    /// `convert_fsd_to_attribute`.
    pub fn convert_fsd_to_attribute(&self, value: &Value) -> Result<Value, ConvertError> {
        self.kind.convert_fsd_to_attribute(value)
    }

    /// `convert_attribute_to_fsd`.
    pub fn convert_attribute_to_fsd(&self, value: &Value) -> Value {
        self.kind.convert_attribute_to_fsd(value)
    }
}

fn to_int(value: &Value) -> Result<i64, ConvertError> {
    match value {
        Value::Int(i) => Ok(*i),
        Value::Bool(b) => Ok(i64::from(*b)),
        Value::Float(f) => Ok(*f as i64),
        Value::Str(s) => s.trim().parse().map_err(|_| ConvertError::NotAnInteger(s.clone())),
        other => Err(ConvertError::NotAnInteger(other.to_string())),
    }
}

/// `(value['scopeType'], value['messageName'])`.
fn address_of(value: &Value) -> Result<Value, ConvertError> {
    match (value.field("scopeType"), value.field("messageName")) {
        (Some(scope), Some(name)) => Ok(Value::Tuple(vec![scope.clone(), name.clone()])),
        _ => Err(ConvertError::NotAnAddress(value.to_string())),
    }
}

fn entries_of(value: &Value) -> Result<&[(Value, Value)], ConvertError> {
    match value {
        Value::Dict(entries) => Ok(entries),
        other => Err(ConvertError::NotACollection(other.to_string())),
    }
}

impl AttributeKind {
    pub fn convert_fsd_to_attribute(self, value: &Value) -> Result<Value, ConvertError> {
        match self {
            AttributeKind::Plain => Ok(value.clone()),
            AttributeKind::BlackboardAddress => address_of(value),
            AttributeKind::BlackboardAddressList => {
                let items = value.items().ok_or_else(|| ConvertError::NotACollection(value.to_string()))?;
                Ok(Value::List(items.iter().map(address_of).collect::<Result<_, _>>()?))
            }
            AttributeKind::BlackboardAddressByFleetType => {
                let mut converted = Vec::new();
                for (key, address) in entries_of(value)? {
                    converted.push((Value::Int(to_int(key)?), address_of(address)?));
                }
                Ok(Value::Dict(converted))
            }
            AttributeKind::FleetType => Ok(Value::Int(to_int(value)?)),
            AttributeKind::OwnerIdToSpawnTableIdDictionary => {
                let mut converted = Vec::new();
                for (key, table) in entries_of(value)? {
                    converted.push((Value::Int(to_int(key)?), Value::Int(to_int(table)?)));
                }
                Ok(Value::Dict(converted))
            }
            AttributeKind::IntKeyDictionary => {
                let mut converted = Vec::new();
                for (key, entry) in entries_of(value)? {
                    converted.push((Value::Int(to_int(key)?), entry.clone()));
                }
                Ok(Value::Dict(converted))
            }
        }
    }

    /// `convert_attribute_to_fsd`: only an address is not what it is in the data.
    pub fn convert_attribute_to_fsd(self, value: &Value) -> Value {
        match (self, value.items()) {
            (AttributeKind::BlackboardAddress, Some([scope, name])) => Value::Dict(vec![
                (Value::Str("scopeType".to_string()), scope.clone()),
                (Value::Str("messageName".to_string()), name.clone()),
            ]),
            _ => value.clone(),
        }
    }
}

/// One registered class: `register_task(task_type, {...})`.
pub struct TaskEntry {
    /// The client's full name of the class (`behaviors.actions.WaitAction`).
    pub class_name: &'static str,
    pub kind: TaskKind,
    pub short_description: Option<&'static str>,
    pub detailed_description: Option<&'static str>,
    pub attributes: &'static [AttributeSpec],
    /// `task_class(attributes)`.
    pub factory: fn(&Attrs) -> Box<dyn Task>,
}

impl TaskEntry {
    /// The name of the class without its module.
    pub fn short_name(&self) -> &'static str {
        self.class_name.rsplit('.').next().unwrap_or(self.class_name)
    }

    pub fn attribute(&self, name: &str) -> Option<&'static AttributeSpec> {
        self.attributes.iter().find(|attribute| attribute.name == name)
    }
}

/// `TaskRegistry`.
pub struct TaskRegistry {
    by_class_name: HashMap<&'static str, &'static TaskEntry>,
    by_short_name: HashMap<&'static str, Vec<&'static TaskEntry>>,
}

/// What asking for a class that is not registered raises.
#[derive(Debug, Clone, PartialEq)]
pub enum RegistryError {
    /// `KeyError`.
    UnknownTask(String),
    /// The name is a short one and several classes have it.
    AmbiguousTask(String),
}

impl TaskRegistry {
    /// Every table of the client's registration.
    pub fn new() -> Self {
        let mut registry = TaskRegistry { by_class_name: HashMap::new(), by_short_name: HashMap::new() };
        for table in [
            actions::ACTIONS,
            conditions::CONDITIONS,
            composites::COMPOSITES,
            decorators::DECORATORS,
            monitors::MONITORS,
        ] {
            for entry in table {
                registry.register_task(entry);
            }
        }
        registry
    }

    /// `register_task`.
    pub fn register_task(&mut self, entry: &'static TaskEntry) {
        self.by_class_name.insert(entry.class_name, entry);
        self.by_short_name.entry(entry.short_name()).or_default().push(entry);
    }

    /// A class by its full name, or by its name alone when only one class has it.
    pub fn get(&self, name: &str) -> Result<&'static TaskEntry, RegistryError> {
        if let Some(entry) = self.by_class_name.get(name) {
            return Ok(entry);
        }
        match self.by_short_name.get(name).map(Vec::as_slice) {
            Some([only]) => Ok(only),
            Some(_) => Err(RegistryError::AmbiguousTask(name.to_string())),
            None => Err(RegistryError::UnknownTask(name.to_string())),
        }
    }

    /// `task_class(attributes)` of a registered class.
    pub fn create(&self, name: &str, attributes: &Attrs) -> Result<Box<dyn Task>, RegistryError> {
        Ok((self.get(name)?.factory)(attributes))
    }

    /// `get_tasks_by_type`: the full names, in order.
    pub fn get_tasks_by_type(&self, kind: TaskKind) -> Vec<&'static str> {
        let mut names: Vec<&'static str> = self
            .by_class_name
            .values()
            .filter(|entry| entry.kind == kind)
            .map(|entry| entry.class_name)
            .collect();
        names.sort_unstable();
        names
    }

    pub fn is_action(&self, name: &str) -> bool {
        self.is_kind(name, TaskKind::Action)
    }

    pub fn is_condition(&self, name: &str) -> bool {
        self.is_kind(name, TaskKind::Condition)
    }

    pub fn is_composite(&self, name: &str) -> bool {
        self.is_kind(name, TaskKind::Composite)
    }

    pub fn is_decorator(&self, name: &str) -> bool {
        self.is_kind(name, TaskKind::Decorator)
    }

    pub fn is_monitor(&self, name: &str) -> bool {
        self.is_kind(name, TaskKind::Monitor)
    }

    fn is_kind(&self, name: &str, kind: TaskKind) -> bool {
        self.by_class_name.get(name).is_some_and(|entry| entry.kind == kind)
    }

    /// `get_task_type`.
    pub fn get_task_type(&self, name: &str) -> Result<TaskKind, RegistryError> {
        Ok(self.get(name)?.kind)
    }

    /// `get_class_attributes`.
    pub fn get_class_attributes(&self, name: &str) -> Result<&'static [AttributeSpec], RegistryError> {
        Ok(self.get(name)?.attributes)
    }

    pub fn len(&self) -> usize {
        self.by_class_name.len()
    }

    pub fn is_empty(&self) -> bool {
        self.by_class_name.is_empty()
    }
}

impl Default for TaskRegistry {
    fn default() -> Self {
        Self::new()
    }
}

/// `get_task_registry()`: the one registry.
pub fn get_task_registry() -> &'static TaskRegistry {
    static REGISTRY: OnceLock<TaskRegistry> = OnceLock::new();
    REGISTRY.get_or_init(TaskRegistry::new)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn every_registered_class_makes_the_task_of_that_class() {
        let registry = get_task_registry();
        assert_eq!(registry.len(), 367);
        let mut wrong = Vec::new();
        for kind in [
            TaskKind::Composite,
            TaskKind::Action,
            TaskKind::Condition,
            TaskKind::Decorator,
            TaskKind::Monitor,
        ] {
            for name in registry.get_tasks_by_type(kind) {
                let entry = registry.get(name).unwrap();
                let task = (entry.factory)(&Attrs::new());
                if task.type_name() != entry.short_name() {
                    wrong.push(format!("{} makes {}", entry.class_name, task.type_name()));
                }
            }
        }
        assert!(wrong.is_empty(), "{wrong:#?}");
    }
}
