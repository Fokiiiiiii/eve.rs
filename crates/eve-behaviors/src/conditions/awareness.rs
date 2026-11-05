//! `behaviors/conditions/awareness.py`.

use crate::constants::combat::THREAT_MAX_DISTANCE_FROM_GUARD_OBJECT;
use crate::tree::TaskCtx;
use crate::utility::ballparks::{is_ball_cloaked, is_ball_in_park, is_ball_in_range};
use crate::value::Value;

/// Whether some target of the cluster is in the park, near the guard object and not cloaked.
fn is_target_valid(t: &mut TaskCtx<'_, '_>, item_id: i64, guard_object_id: i64) -> bool {
    if !is_ball_in_park(t, item_id) {
        return false;
    }
    if !is_ball_in_range(t, item_id, guard_object_id, THREAT_MAX_DISTANCE_FROM_GUARD_OBJECT) {
        return false;
    }
    !is_ball_cloaked(t, item_id)
}

enter_task!(AreClusterTargetsUsable, |t| {
    t.set_status_to_failed();
    let Some(guard_object) = t.value_at("guardObjectIdAddress") else { return };
    if guard_object.is_none() {
        return;
    }
    let Some(guard_object_id) = t.expect_id(&guard_object) else { return };
    let targets = match t.value_at("clusterTargetSetAddress") {
        Some(value) if value.truthy() => value,
        Some(_) => Value::List(Vec::new()),
        None => return,
    };
    for item in targets.items().unwrap_or(&[]) {
        let Some(item_id) = t.expect_id(item) else { return };
        if is_target_valid(t, item_id, guard_object_id) {
            t.set_status_to_success();
            break;
        }
        if t.faulted() {
            return;
        }
    }
});

#[cfg(test)]
mod tests {
    use super::*;
    use crate::blackboards::{Address, ScopeType};
    use crate::tree::tests::Rig;
    use crate::tree::{Attrs, Status};

    fn run(setup: impl FnOnce(&mut Rig), cluster: Option<Value>, guard: Option<i64>) -> Status {
        let mut rig = Rig::new();
        setup(&mut rig);
        if let Some(cluster) = cluster {
            rig.post(ScopeType::Item, "C", cluster);
        }
        if let Some(guard) = guard {
            rig.post(ScopeType::Item, "G", Value::Int(guard));
        }
        let attributes = Attrs::new()
            .with("clusterTargetSetAddress", Address::new(ScopeType::Item, "C").to_value())
            .with("guardObjectIdAddress", Address::new(ScopeType::Item, "G").to_value());
        let status = rig.run(Box::new(AreClusterTargetsUsable), attributes);
        assert_eq!(rig.last_error, None);
        status
    }

    #[test]
    fn cluster_targets_are_usable_when_one_is_in_the_park_near_the_guard_object_and_not_cloaked() {
        let setup = |rig: &mut Rig| {
            rig.world.add_ball(100, [0.0; 3], 1);
            rig.world.add_ball(101, [0.0; 3], 1);
            rig.world.add_ball(102, [0.0; 3], 1);
            rig.world.set_dist(101, 100, 10.0);
            rig.world.set_dist(102, 100, 2_000_000.0);
            rig.world.cloaked.insert(101);
        };
        let set = |ids: &[i64]| Value::set(ids.iter().map(|i| Value::Int(*i)));
        assert_eq!(
            run(setup, Some(set(&[101, 102, 103])), Some(100)),
            Status::Failure,
            "one is cloaked, one is far, one is not there"
        );
        let setup2 = |rig: &mut Rig| {
            rig.world.add_ball(100, [0.0; 3], 1);
            rig.world.add_ball(101, [0.0; 3], 1);
            rig.world.set_dist(101, 100, 10.0);
        };
        assert_eq!(run(setup2, Some(set(&[101])), Some(100)), Status::Success);
        assert_eq!(run(setup2, None, Some(100)), Status::Failure, "no cluster");
        assert_eq!(run(setup2, Some(set(&[101])), None), Status::Failure, "no guard object");
    }
}
