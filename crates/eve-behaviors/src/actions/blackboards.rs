//! `behaviors/actions/blackboards.py`: putting values, counters, sets and dictionaries on the blackboards.
//!
//! A value that is a collection is one object in the client, held by the channel that has it and by whoever asked for it; a task that changes it and
//! sends it again changes what the channel holds (`update_stored_value`). `copy` is a copy of the collection, not of what is in it (here it is a
//! copy of all of it).

use crate::blackboards::{Address, Scope};
use crate::tree::{BehaviorError, Task, TaskCtx};
use crate::utility::blackboards::update_stored_value;
use crate::value::Value;

fn type_error(t: &mut TaskCtx<'_, '_>, message: &str) {
    t.raise(BehaviorError::Task(format!("TypeError: {message}")));
}

/// `x or 0`.
fn or_zero(value: Value) -> Value {
    if value.truthy() { value } else { Value::Int(0) }
}

/// `set(x or [])`: the members of a collection (the keys of a dictionary), as a set.
fn to_set(value: &Value) -> Value {
    let members: Vec<Value> = match value {
        Value::Dict(entries) => entries.iter().map(|(key, _)| key.clone()).collect(),
        other => other.items().unwrap_or(&[]).to_vec(),
    };
    Value::set(members)
}

/// `not (entry is None or entry == '')`.
fn is_entry_valid(entry: &Value) -> bool {
    !(entry.is_none() || entry.py_eq(&Value::Str(String::new())))
}

/// An address of an attribute's list of addresses.
fn address_of(t: &mut TaskCtx<'_, '_>, value: &Value) -> Option<Address> {
    let address = Address::from_value(value);
    if address.is_none() {
        type_error(t, "not an address");
    }
    address
}

/// `BlackboardSendMyItemIdMessageAction` and `BlackboardSendMyEntityGroupIdMessageAction`.
#[derive(Debug, Clone, Copy)]
pub struct SendContextIdMessage {
    name: &'static str,
    group: bool,
}

impl SendContextIdMessage {
    pub fn my_item_id() -> Self {
        Self { name: "BlackboardSendMyItemIdMessageAction", group: false }
    }

    pub fn my_entity_group_id() -> Self {
        Self { name: "BlackboardSendMyEntityGroupIdMessageAction", group: true }
    }
}

impl Task for SendContextIdMessage {
    fn type_name(&self) -> &'static str {
        self.name
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        let Some(address) = t.required_address("messageAddress") else { return };
        let id = if self.group { t.ctx().my_entity_group_id } else { t.ctx().my_item_id };
        t.send_blackboard_value(&address, id.map_or(Value::None, Value::Int));
        t.set_status_to_success();
    }
}

/// `BlackboardCopyMessageAction` and `BlackboardCopyValueBySelfTypeFromBlackboardDictionary`.
#[derive(Debug, Clone, Copy)]
pub struct CopyMessage {
    name: &'static str,
    by_self_type: bool,
}

impl CopyMessage {
    pub fn copy() -> Self {
        Self { name: "BlackboardCopyMessageAction", by_self_type: false }
    }

    /// The value of the dictionary at the type of my entity.
    pub fn by_self_type_from_dictionary() -> Self {
        Self { name: "BlackboardCopyValueBySelfTypeFromBlackboardDictionary", by_self_type: true }
    }
}

impl Task for CopyMessage {
    fn type_name(&self) -> &'static str {
        self.name
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        // `_get_source_message`
        let Some(source_address) = t.required_address("sourceMessageAddress") else { return };
        let mut source = t.get_last_blackboard_value(&source_address);
        if self.by_self_type {
            source = if source.is_none() {
                Value::None
            } else {
                let Some(slim) = t.ctx().my_slim_item.clone() else {
                    t.raise(BehaviorError::Task(
                        "AttributeError: 'NoneType' object has no attribute 'typeID'".to_string(),
                    ));
                    return;
                };
                source.dict_get(&Value::Int(slim.type_id)).cloned().unwrap_or(Value::None)
            };
        }
        let Some(target) = t.required_address("targetMessageAddress") else { return };
        t.send_blackboard_value(&target, source);
        t.set_status_to_success();
    }
}

/// `BlackboardSetMessageAsNoneAction` and the ones that set the value of the task: a boolean, an integer, a float, a string.
#[derive(Debug, Clone, Copy)]
pub struct SetMessageValue {
    name: &'static str,
    from_attribute: bool,
}

impl SetMessageValue {
    pub fn as_none() -> Self {
        Self { name: "BlackboardSetMessageAsNoneAction", from_attribute: false }
    }

    pub fn to_boolean_value() -> Self {
        Self { name: "BlackboardSetMessageToBooleanValueAction", from_attribute: true }
    }

    pub fn to_integer_value() -> Self {
        Self { name: "BlackboardSetMessageToIntegerValueAction", from_attribute: true }
    }

    pub fn to_float_value() -> Self {
        Self { name: "BlackboardSetMessageToFloatValueAction", from_attribute: true }
    }

    pub fn to_string_value() -> Self {
        Self { name: "BlackboardSetMessageToStringValueAction", from_attribute: true }
    }
}

impl Task for SetMessageValue {
    fn type_name(&self) -> &'static str {
        self.name
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        let Some(address) = t.required_address("messageAddress") else { return };
        let value = if self.from_attribute {
            let Some(value) = t.required("value") else { return };
            value
        } else {
            Value::None
        };
        t.send_blackboard_value(&address, value);
        t.set_status_to_success();
    }
}

enter_task!(
    /// Puts the bubble of an item on the blackboard, and fails for an item that has no ball.
    BlackboardSendItemBubbleIdMessage,
    |t| {
        let Some(source) = t.required_address("itemIdSourceAddress") else { return };
        let item_id = t.get_last_blackboard_value(&source);
        let ball = item_id.as_int().and_then(|id| t.world().ball(id));
        match ball {
            None => t.set_status_to_failed(),
            Some(ball) => {
                let Some(target) = t.required_address("bubbleIdTargetAddress") else { return };
                t.send_blackboard_value(&target, Value::Int(ball.new_bubble_id));
                t.set_status_to_success();
            }
        }
    }
);

/// `BlackboardIncrementCounter` and `BlackboardDecrementCounter`.
#[derive(Debug, Clone, Copy)]
pub struct Counter {
    name: &'static str,
    delta: i64,
}

impl Counter {
    pub fn increment() -> Self {
        Self { name: "BlackboardIncrementCounter", delta: 1 }
    }

    pub fn decrement() -> Self {
        Self { name: "BlackboardDecrementCounter", delta: -1 }
    }
}

impl Task for Counter {
    fn type_name(&self) -> &'static str {
        self.name
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        let Some(address) = t.required_address("counterAddress") else { return };
        let counter = or_zero(t.get_last_blackboard_value(&address));
        let result =
            if self.delta > 0 { counter.py_add(&Value::Int(1)) } else { counter.py_sub(&Value::Int(1)) };
        let Some(result) = result else {
            type_error(t, "a counter that is not a number");
            return;
        };
        t.send_blackboard_value(&address, result);
        t.set_status_to_success();
    }
}

enter_task!(
    /// Adds two numbers of the blackboard (0 for one that is not there) and puts the sum on it.
    BlackboardAddNumericValues,
    |t| {
        let (Some(first), Some(second), Some(result)) = (
            t.required_address("firstValueAddress"),
            t.required_address("secondValueAddress"),
            t.required_address("resultValueAddress"),
        ) else {
            return;
        };
        let first_value = or_zero(t.get_last_blackboard_value(&first));
        let second_value = or_zero(t.get_last_blackboard_value(&second));
        let Some(sum) = first_value.py_add(&second_value) else {
            type_error(t, "values that are not numbers");
            return;
        };
        t.send_blackboard_value(&result, sum);
        t.set_status_to_success();
    }
);

enter_task!(
    /// Puts the union of two collections on the blackboard as a set.
    BlackboardMergeCollectionsIntoSet,
    |t| {
        t.set_status_to_success();
        let (Some(first), Some(second)) =
            (t.required_address("firstCollectionAddress"), t.required_address("secondCollectionAddress"))
        else {
            return;
        };
        let mut result_set = to_set(&t.get_last_blackboard_value(&first));
        for member in to_set(&t.get_last_blackboard_value(&second)).items().unwrap_or(&[]) {
            result_set.set_add(member.clone());
        }
        let Some(result) = t.required_address("resultSetAddress") else { return };
        t.send_blackboard_value(&result, result_set);
    }
);

enter_task!(
    /// Takes the members of a collection out of a set of the blackboard (a new set is put on it).
    BlackboardRemoveCollectionFromSet,
    |t| {
        t.set_status_to_success();
        let Some(collection_address) = t.required_address("collectionAddress") else { return };
        let collection = to_set(&t.get_last_blackboard_value(&collection_address));
        let Some(target_address) = t.required_address("targetSetAddress") else { return };
        let last = t.get_last_blackboard_value(&target_address);
        let target_set = if last.truthy() { last } else { Value::set(std::iter::empty()) };
        let mut result_set = Value::set(std::iter::empty());
        for member in target_set.items().unwrap_or(&[]) {
            if !collection.contains(member) {
                result_set.set_add(member.clone());
            }
        }
        t.send_blackboard_value(&target_address, result_set);
    }
);

/// `BlackboardSetBooleanFlags` and `BlackboardClearBooleanFlags`.
#[derive(Debug, Clone, Copy)]
pub struct SetBooleanFlags {
    name: &'static str,
    value: bool,
}

impl SetBooleanFlags {
    pub fn set() -> Self {
        Self { name: "BlackboardSetBooleanFlags", value: true }
    }

    pub fn clear() -> Self {
        Self { name: "BlackboardClearBooleanFlags", value: false }
    }
}

impl Task for SetBooleanFlags {
    fn type_name(&self) -> &'static str {
        self.name
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        let Some(addresses) = t.required("flagAddressList") else { return };
        for address in addresses.items().unwrap_or(&[]) {
            let Some(address) = address_of(t, address) else { return };
            t.send_blackboard_value(&address, Value::Bool(self.value));
        }
        t.set_status_to_success();
    }
}

enter_task!(
    /// Puts a value of the blackboard in a collection of it, unless it is not a value (`None` or nothing) or is in it already.
    BlackboardAddBlackboardValueIntoBlackboardCollection,
    |t| {
        t.set_status_to_success();
        let Some(value_address) = t.required_address("valueAddress") else { return };
        let blackboard_value = t.get_last_blackboard_value(&value_address);
        if !is_entry_valid(&blackboard_value) {
            return;
        }
        let Some(collection_address) = t.required_address("collectionAddress") else { return };
        let mut collection = to_set(&t.get_last_blackboard_value(&collection_address));
        if collection.contains(&blackboard_value) {
            return;
        }
        collection.set_add(blackboard_value);
        t.send_blackboard_value(&collection_address, collection);
    }
);

/// The tasks that put an entry into a dictionary of the blackboard by a key.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum DictionaryKind {
    /// `BlackboardAddValueByKeyIntoBlackboardDictionary`: the value joins the set at the key.
    AddValueByKey,
    /// `BlackboardSetValueByKeyInBlackboardDictionary`: the value is the entry at the key.
    SetValueByKey,
    /// `BlackboardSetValueBySelfTypeInBlackboardDictionary`: the key is the type of my entity.
    SetValueBySelfType,
    /// `BlackboardAddSelfByKeyIntoBlackboardDictionary`: the value is my item.
    AddSelfByKey,
    /// `BlackboardAddValueBySelfIntoBlackboardDictionary`: the key is my item.
    AddValueBySelf,
    /// `BlackboardRemoveValueByKeyFromBlackboardDictionary`: the value leaves the set at the key.
    RemoveValueByKey,
    /// `BlackboardSetDictionaryValueByMyGroupId`: the key is my group.
    SetValueByMyGroupId,
}

/// `BlackboardAddValueByKeyIntoBlackboardDictionary` and its subclasses.
#[derive(Debug, Clone, Copy)]
pub struct DictionaryEntry {
    name: &'static str,
    kind: DictionaryKind,
}

impl DictionaryEntry {
    pub fn add_value_by_key() -> Self {
        Self { name: "BlackboardAddValueByKeyIntoBlackboardDictionary", kind: DictionaryKind::AddValueByKey }
    }

    pub fn set_value_by_key() -> Self {
        Self { name: "BlackboardSetValueByKeyInBlackboardDictionary", kind: DictionaryKind::SetValueByKey }
    }

    pub fn set_value_by_self_type() -> Self {
        Self {
            name: "BlackboardSetValueBySelfTypeInBlackboardDictionary",
            kind: DictionaryKind::SetValueBySelfType,
        }
    }

    pub fn add_self_by_key() -> Self {
        Self { name: "BlackboardAddSelfByKeyIntoBlackboardDictionary", kind: DictionaryKind::AddSelfByKey }
    }

    pub fn add_value_by_self() -> Self {
        Self {
            name: "BlackboardAddValueBySelfIntoBlackboardDictionary",
            kind: DictionaryKind::AddValueBySelf,
        }
    }

    pub fn remove_value_by_key() -> Self {
        Self {
            name: "BlackboardRemoveValueByKeyFromBlackboardDictionary",
            kind: DictionaryKind::RemoveValueByKey,
        }
    }

    pub fn set_value_by_my_group_id() -> Self {
        Self { name: "BlackboardSetDictionaryValueByMyGroupId", kind: DictionaryKind::SetValueByMyGroupId }
    }

    /// `_get_blackboard_key`.
    fn get_key(&self, t: &mut TaskCtx<'_, '_>) -> Option<Value> {
        match self.kind {
            DictionaryKind::SetValueBySelfType => {
                let Some(slim) = t.ctx().my_slim_item.clone() else {
                    t.raise(BehaviorError::Task(
                        "AttributeError: 'NoneType' object has no attribute 'typeID'".to_string(),
                    ));
                    return None;
                };
                Some(Value::Int(slim.type_id))
            }
            DictionaryKind::AddValueBySelf => Some(t.ctx().my_item_id.map_or(Value::None, Value::Int)),
            DictionaryKind::SetValueByMyGroupId => {
                Some(t.ctx().my_entity_group_id.map_or(Value::None, Value::Int))
            }
            _ => {
                let address = t.required_address("keyAddress")?;
                Some(t.get_last_blackboard_value(&address))
            }
        }
    }

    /// `_get_blackboard_value`.
    fn get_value(&self, t: &mut TaskCtx<'_, '_>) -> Option<Value> {
        if self.kind == DictionaryKind::AddSelfByKey {
            return Some(t.ctx().my_item_id.map_or(Value::None, Value::Int));
        }
        let address = t.required_address("valueAddress")?;
        Some(t.get_last_blackboard_value(&address))
    }

    /// `_update_blackboard_dictionary(key, value)`.
    fn update_dictionary(&self, t: &mut TaskCtx<'_, '_>, key: Value, value: Value) {
        let Some(address) = t.required_address("dictionaryAddress") else { return };
        let last = t.get_last_blackboard_value(&address);
        let stored = last.truthy();
        let mut dictionary = if stored { last } else { Value::Dict(Vec::new()) };
        match self.kind {
            DictionaryKind::SetValueByKey
            | DictionaryKind::SetValueBySelfType
            | DictionaryKind::SetValueByMyGroupId => {
                if dictionary.dict_get(&key).is_some_and(|entry| entry.py_eq(&value)) {
                    return;
                }
                dictionary.dict_insert(key, value);
            }
            DictionaryKind::RemoveValueByKey => {
                if !dictionary.truthy() {
                    return;
                }
                // a dictionary that makes the sets it is asked for
                let Some(entry) = dictionary.dict_get(&key).cloned() else {
                    dictionary.dict_insert(key, Value::set(std::iter::empty()));
                    update_stored_value(t, &address, &dictionary);
                    return;
                };
                if !entry.contains(&value) {
                    return;
                }
                let mut entry = entry;
                entry.set_discard(&value);
                dictionary.dict_insert(key, entry);
            }
            _ => {
                let mut entry =
                    dictionary.dict_get(&key).cloned().unwrap_or_else(|| Value::set(std::iter::empty()));
                if entry.contains(&value) {
                    return;
                }
                entry.set_add(value);
                dictionary.dict_insert(key, entry);
            }
        }
        if stored {
            update_stored_value(t, &address, &dictionary);
        }
        t.send_blackboard_value(&address, dictionary);
    }
}

impl Task for DictionaryEntry {
    fn type_name(&self) -> &'static str {
        self.name
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        t.set_status_to_success();
        let Some(key) = self.get_key(t) else { return };
        if !is_entry_valid(&key) {
            return;
        }
        let Some(value) = self.get_value(t) else { return };
        if !is_entry_valid(&value) {
            return;
        }
        self.update_dictionary(t, key, value);
    }
}

enter_task!(
    /// Has the channels told a value when my group is removed, once.
    BlackboardSetBooleanFlagsOnGroupRemoval,
    |t| {
        t.set_status_to_success();
        let Some(one_shot) = t.required_address("oneShotAddress") else { return };
        if t.get_last_blackboard_value(&one_shot).as_bool() == Some(true) {
            return;
        }
        let Some(addresses) = t.required("messageAddressList") else { return };
        for address in addresses.items().unwrap_or(&[]) {
            let Some(address) = address_of(t, address) else { return };
            let Some(channel) = t.channel_from_address(&address) else { return };
            let Some(value) = t.required("value") else { return };
            let group = t.ctx().my_entity_group_id;
            t.world().add_group_removal_handler(group, channel, value);
        }
        t.send_blackboard_value(&one_shot, Value::Bool(true));
    }
);

enter_task!(
    /// Puts a value on the channel of each of the blackboards of some objects (the ones that do not have a blackboard yet, as the client's
    /// condition reads).
    BroadcastBlackboardValueToBlackboards,
    |t| {
        t.set_status_to_success();
        let Some(ids_address) = t.required_address("objectIdsAddress") else { return };
        let object_ids = t.get_last_blackboard_value(&ids_address);
        if !object_ids.truthy() {
            return;
        }
        let Some(value_address) = t.required_address("valueAddress") else { return };
        let value = t.get_last_blackboard_value(&value_address);
        let Some(target) = t.required_address("targetAddress") else { return };
        for object_id in object_ids.items().unwrap_or(&[]) {
            let Some(object_id) = object_id.as_int() else { continue };
            let scope = Scope { scope_type: target.scope_type, id: object_id };
            if !t.has_blackboard(scope) {
                t.send_to_blackboard(scope, &target.name, value.clone());
            }
        }
    }
);

#[cfg(test)]
mod tests {
    use super::*;
    use crate::blackboards::ScopeType;
    use crate::env::SlimItem;
    use crate::tree::tests::Rig;
    use crate::tree::{Attrs, Status};

    fn item(name: &str) -> Value {
        Address::new(ScopeType::Item, name).to_value()
    }

    fn ints(list: &[i64]) -> Value {
        Value::set(list.iter().map(|i| Value::Int(*i)))
    }

    #[test]
    fn the_context_ids_are_sent() {
        let mut rig = Rig::new();
        let attrs = Attrs::new().with("messageAddress", item("Out"));
        assert_eq!(rig.run(Box::new(SendContextIdMessage::my_item_id()), attrs.clone()), Status::Success);
        assert_eq!(rig.last(ScopeType::Item, "Out"), Value::Int(1));
        rig.run(Box::new(SendContextIdMessage::my_entity_group_id()), attrs);
        assert_eq!(rig.last(ScopeType::Item, "Out"), Value::Int(10));
    }

    #[test]
    fn a_message_is_copied_or_set_to_none_or_to_the_value_of_the_task() {
        let mut rig = Rig::new();
        rig.post(ScopeType::Item, "Src", ints(&[1, 2]));
        let copy =
            Attrs::new().with("sourceMessageAddress", item("Src")).with("targetMessageAddress", item("Dst"));
        assert_eq!(rig.run(Box::new(CopyMessage::copy()), copy), Status::Success);
        assert_eq!(rig.last(ScopeType::Item, "Dst"), ints(&[1, 2]));
        let set = Attrs::new().with("messageAddress", item("Dst")).with("value", 4.5);
        rig.run(Box::new(SetMessageValue::to_float_value()), set);
        assert_eq!(rig.last(ScopeType::Item, "Dst"), Value::Float(4.5));
        rig.run(Box::new(SetMessageValue::as_none()), Attrs::new().with("messageAddress", item("Dst")));
        assert_eq!(rig.last(ScopeType::Item, "Dst"), Value::None);
    }

    #[test]
    fn the_value_at_the_type_of_my_entity_is_copied_from_a_dictionary_and_none_when_there_is_none() {
        let attrs =
            Attrs::new().with("sourceMessageAddress", item("Src")).with("targetMessageAddress", item("Dst"));
        let mut rig = Rig::new();
        rig.ctx.my_slim_item = Some(SlimItem { item_id: 1, type_id: 700, ..Default::default() });
        rig.post(
            ScopeType::Item,
            "Src",
            Value::Dict(vec![(Value::Int(700), Value::Str("mine".into())), (Value::Int(9), Value::Int(9))]),
        );
        rig.run(Box::new(CopyMessage::by_self_type_from_dictionary()), attrs.clone());
        assert_eq!(rig.last(ScopeType::Item, "Dst"), Value::Str("mine".into()));
        rig.post(ScopeType::Item, "Src", Value::Dict(vec![(Value::Int(9), Value::Int(9))]));
        rig.run(Box::new(CopyMessage::by_self_type_from_dictionary()), attrs.clone());
        assert_eq!(rig.last(ScopeType::Item, "Dst"), Value::None);
        rig.post(ScopeType::Item, "Src", Value::None);
        rig.run(Box::new(CopyMessage::by_self_type_from_dictionary()), attrs);
        assert_eq!(rig.last(ScopeType::Item, "Dst"), Value::None);
    }

    #[test]
    fn the_bubble_of_an_item_is_sent_and_an_item_that_has_no_ball_fails() {
        let attrs = Attrs::new()
            .with("itemIdSourceAddress", item("Item"))
            .with("bubbleIdTargetAddress", item("Bubble"));
        let mut rig = Rig::new();
        rig.world.add_ball(20, [0.0; 3], 1).new_bubble_id = 8;
        rig.post(ScopeType::Item, "Item", Value::Int(20));
        assert_eq!(rig.run(Box::new(BlackboardSendItemBubbleIdMessage), attrs.clone()), Status::Success);
        assert_eq!(rig.last(ScopeType::Item, "Bubble"), Value::Int(8));
        rig.post(ScopeType::Item, "Item", Value::Int(21));
        assert_eq!(rig.run(Box::new(BlackboardSendItemBubbleIdMessage), attrs), Status::Failure);
    }

    #[test]
    fn counters_count_from_0_and_sums_take_0_for_what_is_not_there() {
        let mut rig = Rig::new();
        let attrs = Attrs::new().with("counterAddress", item("Count"));
        rig.run(Box::new(Counter::increment()), attrs.clone());
        rig.run(Box::new(Counter::increment()), attrs.clone());
        assert_eq!(rig.last(ScopeType::Item, "Count"), Value::Int(2));
        rig.run(Box::new(Counter::decrement()), attrs.clone());
        rig.run(Box::new(Counter::decrement()), attrs.clone());
        rig.run(Box::new(Counter::decrement()), attrs);
        assert_eq!(rig.last(ScopeType::Item, "Count"), Value::Int(-1));
        let sum = Attrs::new()
            .with("firstValueAddress", item("A"))
            .with("secondValueAddress", item("B"))
            .with("resultValueAddress", item("Sum"));
        rig.post(ScopeType::Item, "A", Value::Float(1.5));
        rig.run(Box::new(BlackboardAddNumericValues), sum.clone());
        assert_eq!(rig.last(ScopeType::Item, "Sum"), Value::Float(1.5));
        rig.post(ScopeType::Item, "B", Value::Int(2));
        rig.run(Box::new(BlackboardAddNumericValues), sum.clone());
        assert_eq!(rig.last(ScopeType::Item, "Sum"), Value::Float(3.5));
        rig.post(ScopeType::Item, "A", Value::None);
        rig.run(Box::new(BlackboardAddNumericValues), sum);
        assert_eq!(rig.last(ScopeType::Item, "Sum"), Value::Int(2));
    }

    #[test]
    fn a_counter_that_is_not_a_number_raises() {
        let mut rig = Rig::new();
        rig.post(ScopeType::Item, "Count", Value::Str("x".into()));
        rig.run(Box::new(Counter::increment()), Attrs::new().with("counterAddress", item("Count")));
        assert!(matches!(&rig.last_error, Some(BehaviorError::Task(m)) if m.starts_with("TypeError")));
    }

    #[test]
    fn collections_are_merged_into_a_set_and_taken_out_of_one() {
        let mut rig = Rig::new();
        rig.post(ScopeType::Item, "A", Value::List(vec![Value::Int(1), Value::Int(2), Value::Int(2)]));
        rig.post(ScopeType::Item, "B", ints(&[2, 3]));
        let merge = Attrs::new()
            .with("firstCollectionAddress", item("A"))
            .with("secondCollectionAddress", item("B"))
            .with("resultSetAddress", item("Merged"));
        assert_eq!(rig.run(Box::new(BlackboardMergeCollectionsIntoSet), merge), Status::Success);
        assert_eq!(rig.last(ScopeType::Item, "Merged"), ints(&[1, 2, 3]));
        let remove =
            Attrs::new().with("collectionAddress", item("B")).with("targetSetAddress", item("Merged"));
        rig.run(Box::new(BlackboardRemoveCollectionFromSet), remove);
        assert_eq!(rig.last(ScopeType::Item, "Merged"), ints(&[1]));
    }

    #[test]
    fn flags_are_set_and_cleared() {
        let mut rig = Rig::new();
        let attrs = Attrs::new().with("flagAddressList", Value::List(vec![item("F1"), item("F2")]));
        assert_eq!(rig.run(Box::new(SetBooleanFlags::set()), attrs.clone()), Status::Success);
        assert_eq!(
            (rig.last(ScopeType::Item, "F1"), rig.last(ScopeType::Item, "F2")),
            (Value::Bool(true), Value::Bool(true))
        );
        rig.run(Box::new(SetBooleanFlags::clear()), attrs);
        assert_eq!(rig.last(ScopeType::Item, "F2"), Value::Bool(false));
    }

    #[test]
    fn a_value_is_added_to_a_collection_unless_it_is_none_or_empty_or_there() {
        let attrs = Attrs::new().with("valueAddress", item("V")).with("collectionAddress", item("C"));
        let mut rig = Rig::new();
        rig.run(Box::new(BlackboardAddBlackboardValueIntoBlackboardCollection), attrs.clone());
        assert_eq!(rig.last(ScopeType::Item, "C"), Value::None);
        rig.post(ScopeType::Item, "V", Value::Str(String::new()));
        rig.run(Box::new(BlackboardAddBlackboardValueIntoBlackboardCollection), attrs.clone());
        assert_eq!(rig.last(ScopeType::Item, "C"), Value::None);
        rig.post(ScopeType::Item, "V", Value::Int(0));
        rig.run(Box::new(BlackboardAddBlackboardValueIntoBlackboardCollection), attrs.clone());
        assert_eq!(rig.last(ScopeType::Item, "C"), ints(&[0]), "0 is a value");
        rig.run(Box::new(BlackboardAddBlackboardValueIntoBlackboardCollection), attrs);
        assert_eq!(rig.messages_on("C"), 1);
    }

    fn dict_attrs() -> Attrs {
        Attrs::new()
            .with("keyAddress", item("K"))
            .with("valueAddress", item("V"))
            .with("dictionaryAddress", item("D"))
    }

    #[test]
    fn values_join_the_set_at_a_key_of_a_dictionary_and_leave_it_and_the_dictionary_that_is_there_is_changed_in_place()
     {
        let mut rig = Rig::new();
        rig.post(ScopeType::Item, "K", Value::Int(5));
        rig.post(ScopeType::Item, "V", Value::Int(70));
        rig.run(Box::new(DictionaryEntry::add_value_by_key()), dict_attrs());
        rig.post(ScopeType::Item, "V", Value::Int(71));
        rig.run(Box::new(DictionaryEntry::add_value_by_key()), dict_attrs());
        rig.run(Box::new(DictionaryEntry::add_value_by_key()), dict_attrs());
        assert_eq!(rig.last(ScopeType::Item, "D"), Value::Dict(vec![(Value::Int(5), ints(&[70, 71]))]));
        assert_eq!(rig.messages_on("D"), 2, "the third was there already");
        // the dictionary of the first message is the one that grew: the client has one object
        assert_eq!(rig.history("D")[0], rig.history("D")[1]);
        rig.run(Box::new(DictionaryEntry::remove_value_by_key()), dict_attrs());
        assert_eq!(rig.last(ScopeType::Item, "D"), Value::Dict(vec![(Value::Int(5), ints(&[70]))]));
        // a value that is not at the key changes nothing
        rig.post(ScopeType::Item, "V", Value::Int(99));
        let messages = rig.messages_on("D");
        rig.run(Box::new(DictionaryEntry::remove_value_by_key()), dict_attrs());
        assert_eq!(rig.messages_on("D"), messages);
        // a key that is not there is made by the dictionary, empty, and nothing is sent
        rig.post(ScopeType::Item, "K", Value::Int(6));
        let messages = rig.messages_on("D");
        rig.run(Box::new(DictionaryEntry::remove_value_by_key()), dict_attrs());
        assert_eq!(rig.messages_on("D"), messages);
        assert_eq!(
            rig.last(ScopeType::Item, "D"),
            Value::Dict(vec![(Value::Int(5), ints(&[70])), (Value::Int(6), ints(&[]))])
        );
    }

    #[test]
    fn a_value_is_set_at_a_key_and_a_value_that_is_there_is_not_sent_again() {
        let mut rig = Rig::new();
        rig.post(ScopeType::Item, "K", Value::Int(5));
        rig.post(ScopeType::Item, "V", Value::Str("a".into()));
        rig.run(Box::new(DictionaryEntry::set_value_by_key()), dict_attrs());
        rig.run(Box::new(DictionaryEntry::set_value_by_key()), dict_attrs());
        assert_eq!(rig.messages_on("D"), 1);
        rig.post(ScopeType::Item, "V", Value::Str("b".into()));
        rig.run(Box::new(DictionaryEntry::set_value_by_key()), dict_attrs());
        assert_eq!(
            rig.last(ScopeType::Item, "D"),
            Value::Dict(vec![(Value::Int(5), Value::Str("b".into()))])
        );
        // an entry that is none or empty is not put
        rig.post(ScopeType::Item, "V", Value::None);
        rig.run(Box::new(DictionaryEntry::set_value_by_key()), dict_attrs());
        assert_eq!(rig.messages_on("D"), 2);
    }

    #[test]
    fn the_key_or_the_value_can_be_my_type_my_item_or_my_group() {
        let mut rig = Rig::new();
        rig.ctx.my_slim_item = Some(SlimItem { item_id: 1, type_id: 700, ..Default::default() });
        rig.post(ScopeType::Item, "V", Value::Int(3));
        rig.post(ScopeType::Item, "K", Value::Int(4));
        rig.run(Box::new(DictionaryEntry::set_value_by_self_type()), dict_attrs());
        rig.run(Box::new(DictionaryEntry::set_value_by_my_group_id()), dict_attrs());
        rig.run(Box::new(DictionaryEntry::add_value_by_self()), dict_attrs());
        rig.run(Box::new(DictionaryEntry::add_self_by_key()), dict_attrs());
        assert_eq!(
            rig.last(ScopeType::Item, "D"),
            Value::Dict(vec![
                (Value::Int(700), Value::Int(3)),
                (Value::Int(10), Value::Int(3)),
                (Value::Int(1), ints(&[3])),
                (Value::Int(4), ints(&[1])),
            ])
        );
    }

    #[test]
    fn the_channels_are_told_a_value_when_my_group_is_removed_and_only_once() {
        let mut rig = Rig::new();
        let attrs = Attrs::new()
            .with("oneShotAddress", item("Once"))
            .with("messageAddressList", Value::List(vec![item("A"), item("B")]))
            .with("value", true);
        assert_eq!(
            rig.run(Box::new(BlackboardSetBooleanFlagsOnGroupRemoval), attrs.clone()),
            Status::Success
        );
        assert_eq!(rig.world.group_removal_handlers.len(), 2);
        assert_eq!(rig.world.group_removal_handlers[1].0, 10);
        assert_eq!(rig.world.group_removal_handlers[1].1.name, "B");
        assert_eq!(rig.last(ScopeType::Item, "Once"), Value::Bool(true));
        rig.run(Box::new(BlackboardSetBooleanFlagsOnGroupRemoval), attrs);
        assert_eq!(rig.world.group_removal_handlers.len(), 2);
    }

    #[test]
    fn a_value_is_put_on_the_blackboards_that_are_not_there_yet() {
        let mut rig = Rig::new();
        rig.post(ScopeType::Item, "Ids", Value::set([Value::Int(1), Value::Int(50)]));
        rig.post(ScopeType::Item, "V", Value::Int(9));
        let attrs = Attrs::new()
            .with("objectIdsAddress", item("Ids"))
            .with("valueAddress", item("V"))
            .with("targetAddress", item("Out"));
        assert_eq!(rig.run(Box::new(BroadcastBlackboardValueToBlackboards), attrs), Status::Success);
        assert_eq!(rig.last_in(crate::blackboards::item_scope(50), "Out"), Value::Int(9));
        assert_eq!(rig.last(ScopeType::Item, "Out"), Value::None, "the blackboard of item 1 is there");
    }
}
