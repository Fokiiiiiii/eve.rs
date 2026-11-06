//! `behaviors/conditions/combat.py`.

use crate::consts::ATTRIBUTE_MAX_LOCKED_TARGETS;
use crate::tree::Status;
use crate::utility::ballparks::{is_ball_cloaked, is_ball_in_range, is_target_valid};

enter_task!(HasValidTarget, |t| {
    t.set_status_to_failed();
    let Some(selected) = t.value_at("selectedTargetAddress") else { return };
    let selected = selected.as_int();
    if is_target_valid(t, selected, None, &[], &[], &[]) {
        t.set_status_to_success();
    }
});

enter_task!(HasAvailableTargetCapacity, |t| {
    let Some(me) = t.my_item_id() else { return };
    let count = t.world().targets_ex(me).len() as f64;
    let max = t.world().attribute_value(me, ATTRIBUTE_MAX_LOCKED_TARGETS);
    let Some(max) = t.ok(max) else { return };
    t.set_status(if count >= max { Status::Failure } else { Status::Success });
});

enter_task!(HasItemsFromCollectionWithinRange, |t| {
    t.set_status_to_failed();
    let Some(collection) = t.value_at("itemIdCollectionAddress") else { return };
    if !collection.truthy() {
        return;
    }
    let Some(detection_range) = t.value_at("detectionRangeAddress") else { return };
    let Some(reference_item_id) = t.my_item_id() else { return };
    let Some(range) = detection_range.as_float() else {
        t.raise(crate::tree::BehaviorError::Task(
            "TypeError: the detection range is not a number".to_string(),
        ));
        return;
    };
    let include_cloaked = t.attribute("includeCloakedItems").truthy();
    for item in collection.items().unwrap_or(&[]).to_vec() {
        let Some(item_id) = t.expect_id(&item) else { return };
        if !is_ball_in_range(t, reference_item_id, item_id, range) {
            if t.faulted() {
                return;
            }
            continue;
        }
        if !include_cloaked && is_ball_cloaked(t, item_id) {
            continue;
        }
        t.set_status_to_success();
        return;
    }
});

#[cfg(test)]
mod tests {
    use super::*;
    use crate::blackboards::{Address, ScopeType};
    use crate::tree::Attrs;
    use crate::tree::tests::Rig;
    use crate::value::Value;

    fn item(name: &str) -> Value {
        Address::new(ScopeType::Item, name).to_value()
    }

    #[test]
    fn a_valid_target_is_one_that_is_there_and_not_cloaked() {
        let attrs = Attrs::new().with("selectedTargetAddress", item("T"));
        let mut rig = Rig::new();
        rig.world.add_ball(20, [0.0; 3], 1);
        rig.post(ScopeType::Item, "T", Value::Int(20));
        assert_eq!(rig.run(Box::new(HasValidTarget), attrs.clone()), Status::Success);
        let mut rig = Rig::new();
        rig.world.add_ball(20, [0.0; 3], 1);
        rig.world.cloaked.insert(20);
        rig.post(ScopeType::Item, "T", Value::Int(20));
        assert_eq!(rig.run(Box::new(HasValidTarget), attrs.clone()), Status::Failure);
        assert_eq!(Rig::new().run(Box::new(HasValidTarget), attrs), Status::Failure);
    }

    #[test]
    fn there_is_target_capacity_while_the_targets_are_fewer_than_the_maximum() {
        let mut rig = Rig::new();
        rig.world.attributes.insert((1, ATTRIBUTE_MAX_LOCKED_TARGETS), 2.0);
        rig.world.targets.insert(1, vec![5]);
        assert_eq!(rig.run(Box::new(HasAvailableTargetCapacity), Attrs::new()), Status::Success);
        let mut rig = Rig::new();
        rig.world.attributes.insert((1, ATTRIBUTE_MAX_LOCKED_TARGETS), 2.0);
        rig.world.targets.insert(1, vec![5, 6]);
        assert_eq!(rig.run(Box::new(HasAvailableTargetCapacity), Attrs::new()), Status::Failure);
    }

    #[test]
    fn items_from_a_collection_within_range_skip_the_cloaked_unless_they_are_included() {
        let attrs = Attrs::new()
            .with("itemIdCollectionAddress", item("C"))
            .with("detectionRangeAddress", item("R"))
            .with("includeCloakedItems", false);
        let setup = |rig: &mut Rig| {
            rig.world.add_ball(20, [0.0; 3], 1);
            rig.world.set_dist(1, 20, 50.0);
            rig.world.cloaked.insert(20);
            rig.post(ScopeType::Item, "C", Value::set([Value::Int(20)]));
            rig.post(ScopeType::Item, "R", Value::Int(100));
        };
        let mut rig = Rig::new();
        setup(&mut rig);
        assert_eq!(rig.run(Box::new(HasItemsFromCollectionWithinRange), attrs.clone()), Status::Failure);
        let mut rig = Rig::new();
        setup(&mut rig);
        assert_eq!(
            rig.run(Box::new(HasItemsFromCollectionWithinRange), attrs.with("includeCloakedItems", true)),
            Status::Success
        );
    }
}
