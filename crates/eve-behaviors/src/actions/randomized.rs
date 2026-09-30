//! `behaviors/actions/randomized.py`.

use crate::tree::BehaviorError;
use crate::value::Value;

enter_task!(
    /// A whole number between `minValue` and `maxValue` put on a channel.
    PostRandomIntegerToBlackboard,
    |t| {
        t.set_status_to_success();
        let (Some(min), Some(max)) = (t.required_int("minValue"), t.required_int("maxValue")) else { return };
        let value = t.world().random_int(min, max);
        let Some(address) = t.required_address("blackboardAddress") else { return };
        t.send_blackboard_value(&address, Value::Int(value));
    }
);

enter_task!(
    /// The value on a channel multiplied by a number between `minValue` and `maxValue`.
    ScaleValueRandomized,
    |t| {
        t.set_status_to_success();
        let Some(value) = t.value_at("valueAddress") else { return };
        let (Some(min), Some(max)) = (t.required_float("minValue"), t.required_float("maxValue")) else { return };
        let scalar = t.world().random_uniform(min, max);
        let Some(number) = value.as_float() else {
            t.raise(BehaviorError::Task(format!("TypeError: can't multiply {value} by a float")));
            return;
        };
        let Some(address) = t.required_address("valueAddress") else { return };
        t.send_blackboard_value(&address, Value::Float(number * scalar));
    }
);

#[cfg(test)]
mod tests {
    use super::*;
    use crate::blackboards::{Address, ScopeType};
    use crate::env::TestWorld;
    use crate::tree::Attrs;
    use crate::tree::tests::Rig;

    #[test]
    fn a_random_integer_and_a_scaled_value() {
        let mut rig = Rig::new();
        rig.world = TestWorld::with_numbers(&[0.999_999]);
        rig.run(
            Box::new(PostRandomIntegerToBlackboard),
            Attrs::new()
                .with("minValue", 3i64)
                .with("maxValue", 5i64)
                .with("blackboardAddress", Address::new(ScopeType::Item, "R").to_value()),
        );
        assert_eq!(rig.last(ScopeType::Item, "R"), Value::Int(5));
        let mut rig = Rig::new();
        rig.world = TestWorld::with_numbers(&[0.5]);
        rig.post(ScopeType::Item, "V", Value::Int(10));
        rig.run(
            Box::new(ScaleValueRandomized),
            Attrs::new()
                .with("minValue", 1.0)
                .with("maxValue", 3.0)
                .with("valueAddress", Address::new(ScopeType::Item, "V").to_value()),
        );
        assert_eq!(rig.last(ScopeType::Item, "V"), Value::Float(20.0));
    }
}
