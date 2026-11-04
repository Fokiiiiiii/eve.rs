//! What the tasks of `behaviors/actions/ballparks.py` share: the faults Python raises where the client's code uses what is not there, and the
//! reading of positions, numbers and collections from what the blackboards and the task attributes hold.

use crate::tree::{BehaviorError, TaskCtx};
use crate::value::Value;

pub fn type_error(t: &mut TaskCtx<'_, '_>, what: &str) {
    t.raise(BehaviorError::Task(format!("TypeError: {what}")));
}

pub fn attribute_error(t: &mut TaskCtx<'_, '_>, what: &str) {
    t.raise(BehaviorError::Task(format!("AttributeError: 'NoneType' object has no attribute '{what}'")));
}

/// A position that a blackboard holds, as `*position` and the vector maths take it.
pub fn position_of(t: &mut TaskCtx<'_, '_>, value: &Value) -> Option<[f64; 3]> {
    let position = value.as_vec3();
    if position.is_none() {
        type_error(t, "a position of three numbers is required");
    }
    position
}

/// A number that a task attribute holds (a float that is not there raises where the client uses it).
pub fn number_of(t: &mut TaskCtx<'_, '_>, value: &Value) -> Option<f64> {
    let number = value.as_float();
    if number.is_none() {
        type_error(t, "a number is required");
    }
    number
}

/// `getattr(self.attributes, name, default)` of a number.
pub fn attribute_or(t: &mut TaskCtx<'_, '_>, name: &str, default: f64) -> Option<f64> {
    if t.has_attribute(name) {
        let value = t.attribute(name);
        number_of(t, &value)
    } else {
        Some(default)
    }
}

/// `id in collection`: what is not a collection cannot be asked.
pub fn in_collection(t: &mut TaskCtx<'_, '_>, id: i64, collection: &Value) -> Option<bool> {
    if collection.is_none() {
        type_error(t, "argument of type 'NoneType' is not iterable");
        return None;
    }
    Some(collection.contains(&Value::Int(id)))
}
