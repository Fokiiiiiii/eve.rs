//! `behaviors/conditions/drones.py`.

use crate::consts::ATTRIBUTE_NPC_DRONE_BANDWIDTH;
use crate::utility::dogmatic::get_my_attribute_value;
use crate::value::Value;

enter_task!(CanControlDrones, |t| {
    let Some(bandwidth) = get_my_attribute_value(t, ATTRIBUTE_NPC_DRONE_BANDWIDTH) else { return };
    t.set_status_to_success_if_true_else_to_failed(bandwidth > 0.0);
});

enter_task!(HasDroneBandwidth, |t| {
    let Some(drone_group_id) = t.value_at("droneGroupIdAddress") else { return };
    let group_id = drone_group_id.as_int();
    let count = group_id.and_then(|id| t.world().group_count(id));
    let Some(drone_count) = count else {
        // no group id, or no group: nothing limits the drones
        t.set_status_to_success();
        return;
    };
    let Some(max_group_size) = t.value_at("maxDroneActiveCountAddress") else { return };
    t.set_status_to_success_if_true_else_to_failed(max_group_size.py_gt(&Value::Int(drone_count as i64)));
});

#[cfg(test)]
mod tests {
    use super::*;
    use crate::blackboards::{Address, ScopeType};
    use crate::env::testing::TestGroup;
    use crate::tree::tests::Rig;
    use crate::tree::{Attrs, Status};

    #[test]
    fn a_ship_controls_drones_when_it_has_drone_bandwidth() {
        let mut rig = Rig::new();
        rig.world.attributes.insert((1, ATTRIBUTE_NPC_DRONE_BANDWIDTH), 25.0);
        assert_eq!(rig.run(Box::new(CanControlDrones), Attrs::new()), Status::Success);
        assert_eq!(Rig::new().run(Box::new(CanControlDrones), Attrs::new()), Status::Failure);
    }

    #[test]
    fn there_is_drone_bandwidth_when_there_is_no_group_or_the_group_is_smaller_than_the_maximum() {
        let attrs = Attrs::new()
            .with("droneGroupIdAddress", Address::new(ScopeType::Item, "G").to_value())
            .with("maxDroneActiveCountAddress", Address::new(ScopeType::Item, "M").to_value());
        assert_eq!(
            Rig::new().run(Box::new(HasDroneBandwidth), attrs.clone()),
            Status::Success,
            "no group id"
        );
        for (max, wanted) in [(3, Status::Success), (2, Status::Failure)] {
            let mut rig = Rig::new();
            rig.world.groups.insert(7, TestGroup { members: vec![1, 2], ..Default::default() });
            rig.post(ScopeType::Item, "G", Value::Int(7));
            rig.post(ScopeType::Item, "M", Value::Int(max));
            assert_eq!(rig.run(Box::new(HasDroneBandwidth), attrs.clone()), wanted);
        }
        let mut rig = Rig::new();
        rig.post(ScopeType::Item, "G", Value::Int(8));
        assert_eq!(rig.run(Box::new(HasDroneBandwidth), attrs), Status::Success, "a group that is gone");
    }
}
