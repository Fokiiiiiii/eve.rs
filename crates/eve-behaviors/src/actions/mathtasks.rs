//! `behaviors/actions/mathtasks.py`.

use crate::tree::{BehaviorError, TaskCtx};
use crate::value::Value;

fn vector_at(t: &mut TaskCtx<'_, '_>, attribute: &str) -> Option<[f64; 3]> {
    let value = t.value_at(attribute)?;
    let vector = value.as_vec3();
    if vector.is_none() {
        t.raise(BehaviorError::Task(format!("TypeError: {value} is not a vector")));
    }
    vector
}

fn constant_vector(t: &mut TaskCtx<'_, '_>) -> Option<[f64; 3]> {
    Some([t.required_float("x")?, t.required_float("y")?, t.required_float("z")?])
}

fn send_vector(t: &mut TaskCtx<'_, '_>, vector: [f64; 3]) {
    let Some(address) = t.required_address("outputVectorAddress") else { return };
    t.send_blackboard_value(&address, Value::from_vec3(vector));
    t.set_status_to_success();
}

enter_task!(
    /// A vector written to a channel.
    WriteVectorToBlackboard,
    |t| {
        let Some(v) = constant_vector(t) else { return };
        send_vector(t, v);
    }
);

enter_task!(
    /// A vector on a channel plus a constant one.
    AddToVectorInBlackboard,
    |t| {
        let Some(c) = constant_vector(t) else { return };
        let Some(input) = vector_at(t, "inputVectorAddress") else { return };
        send_vector(t, [input[0] + c[0], input[1] + c[1], input[2] + c[2]]);
    }
);

enter_task!(
    /// The sum of two vectors on channels.
    AddVectorsInBlackboard,
    |t| {
        let (Some(a), Some(b)) = (vector_at(t, "firstVectorAddress"), vector_at(t, "secondVectorAddress")) else { return };
        send_vector(t, [a[0] + b[0], a[1] + b[1], a[2] + b[2]]);
    }
);

enter_task!(
    /// The difference of two vectors on channels.
    SubtractVectorsInBlackboard,
    |t| {
        let (Some(a), Some(b)) = (vector_at(t, "firstVectorAddress"), vector_at(t, "secondVectorAddress")) else { return };
        send_vector(t, [a[0] - b[0], a[1] - b[1], a[2] - b[2]]);
    }
);

#[cfg(test)]
mod tests {
    use super::*;
    use crate::blackboards::{Address, ScopeType};
    use crate::tree::tests::Rig;
    use crate::tree::{Attrs, Status};

    fn item(name: &str) -> Value {
        Address::new(ScopeType::Item, name).to_value()
    }

    #[test]
    fn vectors_are_written_added_and_subtracted() {
        let out = || Attrs::new().with("outputVectorAddress", item("Out"));
        let mut rig = Rig::new();
        assert_eq!(
            rig.run(Box::new(WriteVectorToBlackboard), out().with("x", 1.0).with("y", 2.0).with("z", 3.0)),
            Status::Success
        );
        assert_eq!(rig.last(ScopeType::Item, "Out"), Value::from_vec3([1.0, 2.0, 3.0]));
        let mut rig = Rig::new();
        rig.post(ScopeType::Item, "In", Value::from_vec3([1.0, 1.0, 1.0]));
        rig.run(
            Box::new(AddToVectorInBlackboard),
            out().with("x", 1.0).with("y", 2.0).with("z", 3.0).with("inputVectorAddress", item("In")),
        );
        assert_eq!(rig.last(ScopeType::Item, "Out"), Value::from_vec3([2.0, 3.0, 4.0]));
        let mut rig = Rig::new();
        rig.post(ScopeType::Item, "A", Value::from_vec3([5.0, 5.0, 5.0]));
        rig.post(ScopeType::Item, "B", Value::from_vec3([1.0, 2.0, 3.0]));
        let both = out().with("firstVectorAddress", item("A")).with("secondVectorAddress", item("B"));
        rig.run(Box::new(AddVectorsInBlackboard), both.clone());
        assert_eq!(rig.last(ScopeType::Item, "Out"), Value::from_vec3([6.0, 7.0, 8.0]));
        rig.run(Box::new(SubtractVectorsInBlackboard), both);
        assert_eq!(rig.last(ScopeType::Item, "Out"), Value::from_vec3([4.0, 3.0, 2.0]));
    }
}
