//! `behaviors/conditions/blackboards.py`.

use crate::blackboards::ScopeType;
use crate::consts::SEC;
use crate::tree::{BehaviorError, TaskCtx};
use crate::value::Value;

enter_task!(IsBlackboardValueNone, |t| {
    let Some(value) = t.value_at("valueAddress") else { return };
    t.set_status_to_success_if_true_else_to_failed(value.is_none());
});

enter_task!(IsBlackboardValueNotNone, |t| {
    let Some(value) = t.value_at("valueAddress") else { return };
    t.set_status_to_success_if_true_else_to_failed(!value.is_none());
});

enter_task!(IsBlackboardValue3dVector, |t| {
    let Some(value) = t.value_at("valueAddress") else { return };
    let is_vector = matches!(&value, Value::List(v) | Value::Tuple(v) if v.len() == 3);
    t.set_status_to_success_if_true_else_to_failed(is_vector);
});

enter_task!(IsBlackboardValueTrue, |t| {
    let Some(value) = t.value_at("valueAddress") else { return };
    t.set_status_to_success_if_true_else_to_failed(value == Value::Bool(true));
});

enter_task!(IsBlackboardValueFalse, |t| {
    let Some(value) = t.value_at("valueAddress") else { return };
    t.set_status_to_success_if_true_else_to_failed(value == Value::Bool(false));
});

enter_task!(IsBlackboardStringEqualToValue, |t| {
    let Some(value) = t.value_at("valueAddress") else { return };
    let expected = t.attribute("expectedValue");
    t.set_status_to_success_if_true_else_to_failed(value.py_eq(&expected));
});

enter_task!(BlackboardMessagesEqualCondition, |t| {
    let (Some(first), Some(second)) = (t.value_at("firstMessageAddress"), t.value_at("secondMessageAddress"))
    else {
        return;
    };
    t.set_status_to_success_if_true_else_to_failed(first.py_eq(&second));
});

enter_task!(BlackboardValueUpdatedRecentlyCondition, |t| {
    let Some(address) = t.required_address("address") else { return };
    let Some(seconds) = t.required_float("timeInSeconds") else { return };
    let value = t.get_last_blackboard_value_with_max_age(&address, (seconds * SEC as f64) as i64);
    t.set_status_to_success_if_true_else_to_failed(value.truthy());
});

enter_task!(IsBlackboardValueInCollectionCondition, |t| {
    let (Some(value), Some(collection)) = (t.value_at("valueAddress"), t.value_at("collectionAddress"))
    else {
        return;
    };
    t.set_status_to_success_if_true_else_to_failed(collection.truthy() && collection.contains(&value));
});

/// `BlackboardMessageAndMyItemIdEqualCondition` and its subclass: whether a value on a channel is my item's id (or my group's).
fn message_equals_my_id(t: &mut TaskCtx<'_, '_>, mine: impl FnOnce(&mut TaskCtx<'_, '_>) -> Option<i64>) {
    let Some(value) = t.value_at("messageAddress") else { return };
    let mine = mine(t).map_or(Value::None, Value::Int);
    let mut result = value.py_eq(&mine);
    if t.attribute("isInverted").truthy() {
        result = !result;
    }
    t.set_status_to_success_if_true_else_to_failed(result);
}

enter_task!(BlackboardMessageAndMyItemIdEqualCondition, |t| {
    message_equals_my_id(t, |t| t.ctx().my_item_id);
});

enter_task!(BlackboardMessageAndMyEntityGroupIdEqualCondition, |t| {
    message_equals_my_id(t, |t| t.ctx().my_entity_group_id);
});

enter_task!(IsBlackboardIntegerEqualToValue, |t| {
    let Some(value) = t.value_at("valueAddress") else { return };
    let expected = t.attribute("value");
    t.set_status_to_success_if_true_else_to_failed(value.py_eq(&expected));
});

enter_task!(IsBlackboardValueRatioLessThan, |t| {
    let (Some(first), Some(second)) = (t.value_at("firstValueAddress"), t.value_at("secondValueAddress"))
    else {
        return;
    };
    if first.is_none() || second.is_none() {
        t.set_status_to_failed();
        return;
    }
    let Some(ratio_to_compare) = t.value_at("ratioAddress") else { return };
    let (Some(numerator), Some(denominator)) = (first.as_float(), second.as_float()) else {
        t.raise(BehaviorError::Task("TypeError: unsupported operand for the ratio".to_string()));
        return;
    };
    if denominator == 0.0 {
        t.raise(BehaviorError::Task("ZeroDivisionError: float division by zero".to_string()));
        return;
    }
    let ratio = Value::Float(numerator / denominator);
    t.set_status_to_success_if_true_else_to_failed(ratio.py_lt(&ratio_to_compare));
});

enter_task!(BlackboardValueEqualsBlackboardValueInDictionaryCondition, |t| {
    t.set_status_to_failed();
    let (Some(expected), Some(key)) = (t.value_at("valueAddress"), t.value_at("keyAddress")) else { return };
    if expected.is_none() || key.is_none() {
        return;
    }
    let Some(dictionary) = t.value_at("dictionaryAddress") else { return };
    if dictionary.is_none() {
        return;
    }
    let Some(found) = dictionary.dict_get(&key) else { return };
    if !found.py_eq(&expected) {
        return;
    }
    t.set_status_to_success();
});

enter_task!(IsBlackboardCollectionEmpty, |t| {
    let Some(collection) = t.value_at("collectionAddress") else { return };
    t.set_status_to_success_if_true_else_to_failed(collection.is_none() || collection.is_empty());
});

enter_task!(IsBlackboardValueLessThenBlackboardValue, |t| {
    let (Some(to_check), Some(to_compare)) =
        (t.value_at("valueToCheckAddress"), t.value_at("valueToCompareAddress"))
    else {
        return;
    };
    t.set_status_to_success_if_true_else_to_failed(to_check.py_lt(&to_compare));
});

enter_task!(IsBlackboardDeleted, |t| {
    t.set_status_to_failed();
    let Some(scope_name) = t.required_string("scopeType") else { return };
    let Some(scope_type) = ScopeType::from_name(&scope_name) else {
        t.raise(BehaviorError::Authoring(format!("{scope_name} is no scope type")));
        return;
    };
    let Some(id) = t.ctx().id_for_scope(scope_type) else {
        t.raise(BehaviorError::BlackboardContextIdNotInBehaviorContext(scope_type));
        return;
    };
    let scope = crate::blackboards::Scope { scope_type, id };
    if t.shared().blackboards.get_blackboard(scope).is_err() {
        t.set_status_to_success();
    }
});

#[cfg(test)]
mod tests {
    use super::*;
    use crate::blackboards::Address;
    use crate::tree::tests::Rig;
    use crate::tree::{Attrs, Status, Task};

    fn addr(name: &str) -> Value {
        Address::new(ScopeType::Item, name).to_value()
    }

    /// The status a condition ends in with these attributes, after the blackboard was given these values (item scope).
    fn run(node: Box<dyn Task>, attributes: Attrs, values: &[(&str, Value)]) -> Status {
        let mut rig = Rig::new();
        for (channel, value) in values {
            rig.post(ScopeType::Item, channel, value.clone());
        }
        let status = rig.run(node, attributes);
        assert_eq!(rig.last_error, None);
        status
    }

    #[test]
    fn none_and_not_none_and_true_and_false_look_at_the_value_by_identity() {
        let a = Attrs::new().with("valueAddress", addr("V"));
        assert_eq!(run(Box::new(IsBlackboardValueNone), a.clone(), &[]), Status::Success);
        assert_eq!(run(Box::new(IsBlackboardValueNone), a.clone(), &[("V", Value::Int(1))]), Status::Failure);
        assert_eq!(
            run(Box::new(IsBlackboardValueNotNone), a.clone(), &[("V", Value::Int(0))]),
            Status::Success
        );
        assert_eq!(
            run(Box::new(IsBlackboardValueTrue), a.clone(), &[("V", Value::Bool(true))]),
            Status::Success
        );
        assert_eq!(
            run(Box::new(IsBlackboardValueTrue), a.clone(), &[("V", Value::Int(1))]),
            Status::Failure,
            "`is True`: one is not True"
        );
        assert_eq!(
            run(Box::new(IsBlackboardValueFalse), a.clone(), &[("V", Value::Bool(false))]),
            Status::Success
        );
        assert_eq!(run(Box::new(IsBlackboardValueFalse), a.clone(), &[("V", Value::None)]), Status::Failure);
        assert_eq!(
            run(Box::new(IsBlackboardValue3dVector), a.clone(), &[("V", Value::from_vec3([1.0, 2.0, 3.0]))]),
            Status::Success
        );
        assert_eq!(
            run(Box::new(IsBlackboardValue3dVector), a, &[("V", Value::List(vec![Value::Int(1)]))]),
            Status::Failure
        );
    }

    #[test]
    fn values_are_compared_as_python_does() {
        let a = Attrs::new().with("firstMessageAddress", addr("A")).with("secondMessageAddress", addr("B"));
        assert_eq!(
            run(
                Box::new(BlackboardMessagesEqualCondition),
                a.clone(),
                &[("A", Value::Int(1)), ("B", Value::Float(1.0))]
            ),
            Status::Success
        );
        assert_eq!(
            run(Box::new(BlackboardMessagesEqualCondition), a, &[("A", Value::Int(1)), ("B", Value::Int(2))]),
            Status::Failure
        );
        let a = Attrs::new().with("valueToCheckAddress", addr("A")).with("valueToCompareAddress", addr("B"));
        assert_eq!(
            run(Box::new(IsBlackboardValueLessThenBlackboardValue), a.clone(), &[("B", Value::Int(1))]),
            Status::Success,
            "None is below one"
        );
        assert_eq!(
            run(
                Box::new(IsBlackboardValueLessThenBlackboardValue),
                a,
                &[("A", Value::Int(2)), ("B", Value::Int(1))]
            ),
            Status::Failure
        );
    }

    #[test]
    fn a_value_in_a_collection_needs_a_collection_that_has_it_and_an_empty_collection_is_empty() {
        let a = Attrs::new().with("valueAddress", addr("V")).with("collectionAddress", addr("C"));
        let set = Value::set([Value::Int(1), Value::Int(2)]);
        assert_eq!(
            run(
                Box::new(IsBlackboardValueInCollectionCondition),
                a.clone(),
                &[("V", Value::Int(2)), ("C", set.clone())]
            ),
            Status::Success
        );
        assert_eq!(
            run(Box::new(IsBlackboardValueInCollectionCondition), a, &[("V", Value::Int(3)), ("C", set)]),
            Status::Failure
        );
        let a = Attrs::new().with("collectionAddress", addr("C"));
        assert_eq!(run(Box::new(IsBlackboardCollectionEmpty), a.clone(), &[]), Status::Success);
        assert_eq!(
            run(Box::new(IsBlackboardCollectionEmpty), a.clone(), &[("C", Value::set([]))]),
            Status::Success
        );
        assert_eq!(
            run(Box::new(IsBlackboardCollectionEmpty), a, &[("C", Value::set([Value::Int(1)]))]),
            Status::Failure
        );
    }

    #[test]
    fn a_message_equal_to_my_item_or_my_group_and_the_inverted_one() {
        let a = Attrs::new().with("messageAddress", addr("M"));
        assert_eq!(
            run(Box::new(BlackboardMessageAndMyItemIdEqualCondition), a.clone(), &[("M", Value::Int(1))]),
            Status::Success,
            "the rig's item is 1"
        );
        assert_eq!(
            run(
                Box::new(BlackboardMessageAndMyItemIdEqualCondition),
                a.clone().with("isInverted", true),
                &[("M", Value::Int(1))]
            ),
            Status::Failure
        );
        assert_eq!(
            run(
                Box::new(BlackboardMessageAndMyEntityGroupIdEqualCondition),
                a.clone(),
                &[("M", Value::Int(10))]
            ),
            Status::Success
        );
        assert_eq!(
            run(Box::new(BlackboardMessageAndMyEntityGroupIdEqualCondition), a, &[("M", Value::Int(1))]),
            Status::Failure
        );
    }

    #[test]
    fn a_ratio_is_compared_and_a_missing_side_fails_and_a_zero_denominator_raises() {
        let a = Attrs::new()
            .with("firstValueAddress", addr("A"))
            .with("secondValueAddress", addr("B"))
            .with("ratioAddress", addr("R"));
        assert_eq!(
            run(
                Box::new(IsBlackboardValueRatioLessThan),
                a.clone(),
                &[("A", Value::Int(1)), ("B", Value::Int(4)), ("R", Value::Float(0.5))]
            ),
            Status::Success
        );
        assert_eq!(
            run(
                Box::new(IsBlackboardValueRatioLessThan),
                a.clone(),
                &[("A", Value::Int(3)), ("B", Value::Int(4)), ("R", Value::Float(0.5))]
            ),
            Status::Failure
        );
        assert_eq!(
            run(Box::new(IsBlackboardValueRatioLessThan), a.clone(), &[("A", Value::Int(1))]),
            Status::Failure
        );
        let mut rig = Rig::new();
        rig.post(ScopeType::Item, "A", Value::Int(1));
        rig.post(ScopeType::Item, "B", Value::Int(0));
        rig.run(Box::new(IsBlackboardValueRatioLessThan), a);
        assert!(rig.last_error.is_some());
    }

    #[test]
    fn a_value_in_a_dictionary_by_a_key() {
        let a = Attrs::new()
            .with("valueAddress", addr("V"))
            .with("keyAddress", addr("K"))
            .with("dictionaryAddress", addr("D"));
        let dict = Value::Dict(vec![(Value::Int(5), Value::Str("x".into()))]);
        let ok = [("V", Value::Str("x".into())), ("K", Value::Int(5)), ("D", dict.clone())];
        assert_eq!(
            run(Box::new(BlackboardValueEqualsBlackboardValueInDictionaryCondition), a.clone(), &ok),
            Status::Success
        );
        let other = [("V", Value::Str("y".into())), ("K", Value::Int(5)), ("D", dict.clone())];
        assert_eq!(
            run(Box::new(BlackboardValueEqualsBlackboardValueInDictionaryCondition), a.clone(), &other),
            Status::Failure
        );
        let no_key = [("V", Value::Str("x".into())), ("K", Value::Int(6)), ("D", dict)];
        assert_eq!(
            run(Box::new(BlackboardValueEqualsBlackboardValueInDictionaryCondition), a, &no_key),
            Status::Failure
        );
    }

    #[test]
    fn a_value_updated_recently_needs_a_message_no_older_than_the_time() {
        let a = Attrs::new().with("address", addr("U")).with("timeInSeconds", 5.0);
        assert_eq!(
            run(Box::new(BlackboardValueUpdatedRecentlyCondition), a.clone(), &[("U", Value::Int(1))]),
            Status::Success
        );
        assert_eq!(
            run(Box::new(BlackboardValueUpdatedRecentlyCondition), a.clone(), &[("U", Value::Int(0))]),
            Status::Failure,
            "the value is falsy"
        );
        assert_eq!(run(Box::new(BlackboardValueUpdatedRecentlyCondition), a, &[]), Status::Failure);
    }

    #[test]
    fn a_blackboard_that_was_removed_is_deleted() {
        let a = Attrs::new().with("scopeType", "item");
        assert_eq!(run(Box::new(IsBlackboardDeleted), a.clone(), &[]), Status::Failure);
        let mut rig = Rig::new();
        rig.shared.blackboards.get_blackboard(crate::blackboards::item_scope(1)).unwrap();
        rig.shared.blackboards.remove_blackboard(crate::blackboards::item_scope(1));
        assert_eq!(rig.run(Box::new(IsBlackboardDeleted), a), Status::Success);
    }
}
