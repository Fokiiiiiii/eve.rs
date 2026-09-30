//! `behaviors/conditions/ballparks.py`.

use crate::consts::{DESTINY_MODE_ORBIT, MIN_WARP_DISTANCE, SHIP_NOT_WARPING};
use crate::env::WorldError;
use crate::tree::{Task, TaskCtx};
use crate::utility::ballparks::{dist3, get_my_ball, get_my_position, is_ball_cloaked, is_ball_in_park};
use crate::value::Value;

/// A position held by a blackboard value.
fn position_of(t: &mut TaskCtx<'_, '_>, value: &Value) -> Option<[f64; 3]> {
    let position = value.as_vec3();
    if position.is_none() {
        t.raise(crate::tree::BehaviorError::Task(format!("TypeError: {value} is not a position")));
    }
    position
}

enter_task!(IsCoordinateInSameBubbleCondition, |t| {
    let Some(coordinates) = t.value_at("coordinateAddress") else { return };
    let mut status = crate::tree::Status::Failure;
    if !coordinates.is_none() {
        let Some(position) = position_of(t, &coordinates) else { return };
        let bubble_id = t.world().bubble_at_coordinates(position);
        let Some(mine) = get_my_ball(t) else { return };
        if bubble_id == mine.new_bubble_id {
            status = crate::tree::Status::Success;
        }
    }
    t.set_status(status);
});

/// `IsCoordinateWithinDistance` and `IsCoordinateWithinDistanceByAddress`: the maximum distance is what `GetMaxDistance` says.
fn is_coordinate_within_distance(
    t: &mut TaskCtx<'_, '_>,
    max_distance: impl FnOnce(&mut TaskCtx<'_, '_>) -> Option<Value>,
) {
    let Some(coordinates) = t.value_at("coordinateAddress") else { return };
    t.set_status_to_failed();
    if coordinates.is_none() {
        return;
    }
    let Some(target) = position_of(t, &coordinates) else { return };
    let Some(mine) = get_my_ball(t) else { return };
    let distance = dist3(target, mine.position);
    let Some(max) = max_distance(t) else { return };
    t.set_status_to_success_if_true_else_to_failed(Value::Float(distance).py_le(&max));
}

enter_task!(IsCoordinateWithinDistance, |t| {
    is_coordinate_within_distance(t, |t| t.required("maxDistance"));
});

enter_task!(IsCoordinateWithinDistanceByAddress, |t| {
    is_coordinate_within_distance(t, |t| {
        let mut max = t.value_at("maxDistanceAddress")?;
        if t.has_attribute("fudgeDistanceAddress") {
            let fudge = t.value_at("fudgeDistanceAddress")?;
            if !fudge.is_none() {
                let again = t.value_at("fudgeDistanceAddress")?;
                let (Some(a), Some(b)) = (max.as_float(), again.as_float()) else {
                    t.raise(crate::tree::BehaviorError::Task(
                        "TypeError: unsupported operand type(s) for +=".to_string(),
                    ));
                    return None;
                };
                max = match (&max, &again) {
                    (Value::Int(x), Value::Int(y)) => Value::Int(x + y),
                    _ => Value::Float(a + b),
                };
            }
        }
        Some(max)
    });
});

enter_task!(IsBallPresentInMyBubbleCondition, |t| {
    let Some(ball_id) = t.value_at("ballIdAddress") else { return };
    let ball = ball_id.as_int().and_then(|id| t.world().ball(id));
    let Some(mine) = get_my_ball(t) else { return };
    t.set_status_to_success_if_true_else_to_failed(
        ball.is_some_and(|b| b.new_bubble_id == mine.new_bubble_id),
    );
});

enter_task!(IsBallPresentInBubbleByAddress, |t| {
    let (Some(bubble_id), Some(ball_id)) = (t.value_at("bubbleIdAddress"), t.value_at("ballIdAddress"))
    else {
        return;
    };
    let ball = ball_id.as_int().and_then(|id| t.world().ball(id));
    t.set_status_to_success_if_true_else_to_failed(
        ball.is_some_and(|b| Value::Int(b.new_bubble_id).py_eq(&bubble_id)),
    );
});

/// `IsBallWithInDistanceCondition`: the distance between the ball on the channel and a source ball is at most `maxDistance`.
fn is_ball_within_distance(
    t: &mut TaskCtx<'_, '_>,
    source: impl FnOnce(&mut TaskCtx<'_, '_>) -> Option<Value>,
) {
    t.set_status_to_failed();
    let Some(ball_id) = t.value_at("ballIdAddress") else { return };
    let Some(source) = source(t) else { return };
    let (Some(source_id), Some(ball_id)) = (t.expect_id(&source), t.expect_id(&ball_id)) else { return };
    let distance = t.world().surface_dist(source_id, ball_id);
    let Some(distance) = t.ok(distance) else { return };
    let Some(max) = t.required("maxDistance") else { return };
    if Value::Float(distance).py_le(&max) {
        t.set_status_to_success();
    }
}

enter_task!(IsBallWithInDistanceCondition, |t| {
    is_ball_within_distance(t, |t| t.my_item_id().map(Value::Int));
});

enter_task!(IsBallWithinDistanceOfBallCondition, |t| {
    is_ball_within_distance(t, |t| t.value_at("sourceBallIdAddress"));
});

enter_task!(AreItemsWithInDistanceCondition, |t| {
    t.set_status_to_failed();
    let Some(item_id) = t.value_at("itemIdAddress") else { return };
    let other = if t.attribute("otherItemIdAddress").is_none() {
        let Some(me) = t.my_item_id() else { return };
        Value::Int(me)
    } else {
        let Some(other) = t.value_at("otherItemIdAddress") else { return };
        other
    };
    let (Some(item_id), Some(other_id)) = (t.expect_id(&item_id), t.expect_id(&other)) else { return };
    let distance = t.world().surface_dist(item_id, other_id);
    let Some(distance) = t.ok(distance) else { return };
    let Some(max_distance) = t.value_at("maxDistanceAddress") else { return };
    let multiplier = match t.attribute("maxDistanceMultiplier") {
        m if m.truthy() => m.as_float().unwrap_or(1.0),
        _ => 1.0,
    };
    let Some(max_distance) = max_distance.as_float() else {
        t.raise(crate::tree::BehaviorError::Task(
            "TypeError: unsupported operand type(s) for *=".to_string(),
        ));
        return;
    };
    if distance <= max_distance * multiplier {
        t.set_status_to_success();
    }
});

enter_task!(IsOrbitingTargetCondition, |t| {
    let Some(target) = t.value_at("targetAddress") else { return };
    let orbiting = if target.is_none() {
        false
    } else {
        let Some(mine) = get_my_ball(t) else { return };
        mine.mode == DESTINY_MODE_ORBIT && Value::Int(mine.follow_id).py_eq(&target)
    };
    t.set_status_to_success_if_true_else_to_failed(orbiting);
});

enter_task!(IsBallCloakedCondition, |t| {
    let Some(ball_id) = t.value_at("ballIdAddress") else { return };
    let Some(ball_id) = t.expect_id(&ball_id) else { return };
    let cloaked = is_ball_cloaked(t, ball_id);
    t.set_status_to_success_if_true_else_to_failed(cloaked);
});

enter_task!(IsBallInWarpCondition, |t| {
    t.set_status_to_failed();
    let Some(ball_id) = t.value_at("ballIdAddress") else { return };
    let Some(ball_id) = t.expect_id(&ball_id) else { return };
    match t.world().is_warping(ball_id) {
        Ok(state) => {
            if state != SHIP_NOT_WARPING {
                t.set_status_to_success();
            }
        }
        Err(WorldError::User(_)) => {}
        Err(other) => t.raise(other.into()),
    }
});

enter_task!(IsLocationWithinWarpDistance, |t| {
    t.set_status_to_failed();
    let Some(target) = t.value_at("coordinateAddress") else { return };
    if !target.truthy() {
        return;
    }
    let Some(target) = position_of(t, &target) else { return };
    let Some(mine) = get_my_ball(t) else { return };
    if dist3(target, mine.position) > MIN_WARP_DISTANCE as f64 {
        t.set_status_to_success();
    }
});

enter_task!(IsBallWithinWarpDistance, |t| {
    t.set_status_to_failed();
    let Some(target) = t.value_at("ballIdAddress") else { return };
    let Some(target_id) = t.expect_id(&target) else { return };
    if !is_ball_in_park(t, target_id) {
        return;
    }
    let Some(me) = t.my_item_id() else { return };
    let distance = t.world().surface_dist(target_id, me);
    let Some(distance) = t.ok(distance) else { return };
    if distance > MIN_WARP_DISTANCE as f64 {
        t.set_status_to_success();
    }
});

enter_task!(HasValidBallTarget, |t| {
    t.set_status_to_failed();
    let Some(ball_id) = t.value_at("ballIdAddress") else { return };
    let has_ball = ball_id.as_int().is_some_and(|id| t.world().has_ball(id));
    if has_ball {
        t.set_status_to_success();
    }
});

/// `IsPathToCoordinatesClearOfBubbles`.
#[derive(Debug, Default)]
pub struct IsPathToCoordinatesClearOfBubbles;

impl IsPathToCoordinatesClearOfBubbles {
    fn is_path_to_coordinates_clear(t: &mut TaskCtx<'_, '_>, coordinates: [f64; 3]) -> bool {
        let Some(from) = get_my_position(t) else { return false };
        let closest = match t.world().closest_disruptor_and_intersection_in_warp_path(from, coordinates) {
            Ok(closest) => closest,
            // the warp path is blocked by a bubble that is too close for warping there
            Err(WorldError::User(_)) => return false,
            Err(other) => {
                t.raise(other.into());
                return false;
            }
        };
        match closest {
            None => true,
            Some((warp_disruptor_id, _)) => {
                let (probe_ids, ship_ids) =
                    t.world().warp_bubble_item_ids_within_range_of_coordinate(coordinates);
                probe_ids.contains(&warp_disruptor_id) || ship_ids.contains(&warp_disruptor_id)
            }
        }
    }
}

impl Task for IsPathToCoordinatesClearOfBubbles {
    fn type_name(&self) -> &'static str {
        "IsPathToCoordinatesClearOfBubbles"
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        t.set_status_to_failed();
        let Some(coordinates) = t.value_at("coordinateAddress") else { return };
        if coordinates.is_none() {
            return;
        }
        let Some(position) = position_of(t, &coordinates) else { return };
        if Self::is_path_to_coordinates_clear(t, position) {
            t.set_status_to_success();
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::blackboards::{Address, ScopeType};
    use crate::tree::tests::Rig;
    use crate::tree::{Attrs, Status};

    fn item(name: &str) -> Value {
        Address::new(ScopeType::Item, name).to_value()
    }

    fn rig_with_me_at(position: [f64; 3]) -> Rig {
        let mut rig = Rig::new();
        rig.world.add_ball(1, position, 100);
        rig
    }

    #[test]
    fn a_coordinate_is_in_my_bubble_when_the_ballpark_says_so() {
        let mut rig = rig_with_me_at([0.0; 3]);
        rig.world.balls.get_mut(&1).unwrap().new_bubble_id = 7;
        rig.world.bubble_of_position.insert([5, 5, 5], 7);
        rig.post(ScopeType::Item, "C", Value::from_vec3([5.0, 5.0, 5.0]));
        assert_eq!(
            rig.run(
                Box::new(IsCoordinateInSameBubbleCondition),
                Attrs::new().with("coordinateAddress", item("C"))
            ),
            Status::Success
        );
        let mut rig = rig_with_me_at([0.0; 3]);
        rig.post(ScopeType::Item, "C", Value::from_vec3([5.0, 5.0, 5.0]));
        assert_eq!(
            rig.run(
                Box::new(IsCoordinateInSameBubbleCondition),
                Attrs::new().with("coordinateAddress", item("C"))
            ),
            Status::Failure
        );
    }

    #[test]
    fn a_coordinate_within_a_distance_by_number_and_by_address_with_the_fudge() {
        let attrs = Attrs::new().with("coordinateAddress", item("C")).with("maxDistance", 5.0);
        let mut rig = rig_with_me_at([0.0; 3]);
        rig.post(ScopeType::Item, "C", Value::from_vec3([3.0, 4.0, 0.0]));
        assert_eq!(rig.run(Box::new(IsCoordinateWithinDistance), attrs.clone()), Status::Success);
        let mut rig = rig_with_me_at([0.0; 3]);
        rig.post(ScopeType::Item, "C", Value::from_vec3([3.0, 4.0, 1.0]));
        assert_eq!(rig.run(Box::new(IsCoordinateWithinDistance), attrs), Status::Failure);
        let by_address = Attrs::new()
            .with("coordinateAddress", item("C"))
            .with("maxDistanceAddress", item("M"))
            .with("fudgeDistanceAddress", item("F"));
        let mut rig = rig_with_me_at([0.0; 3]);
        rig.post(ScopeType::Item, "C", Value::from_vec3([3.0, 4.0, 0.0]));
        rig.post(ScopeType::Item, "M", Value::Int(4));
        assert_eq!(
            rig.run(Box::new(IsCoordinateWithinDistanceByAddress), by_address.clone()),
            Status::Failure,
            "with no fudge the limit is 4 and the distance 5"
        );
        let mut rig = rig_with_me_at([0.0; 3]);
        rig.post(ScopeType::Item, "C", Value::from_vec3([3.0, 4.0, 0.0]));
        rig.post(ScopeType::Item, "M", Value::Int(4));
        rig.post(ScopeType::Item, "F", Value::Int(1));
        assert_eq!(
            rig.run(Box::new(IsCoordinateWithinDistanceByAddress), by_address),
            Status::Success,
            "with a fudge of one the limit is 5"
        );
    }

    #[test]
    fn a_ball_present_in_a_bubble_and_within_a_distance() {
        let mut rig = rig_with_me_at([0.0; 3]);
        rig.world.balls.get_mut(&1).unwrap().new_bubble_id = 3;
        rig.world.add_ball(2, [0.0; 3], 1).new_bubble_id = 3;
        rig.world.add_ball(4, [0.0; 3], 1).new_bubble_id = 9;
        rig.post(ScopeType::Item, "B", Value::Int(2));
        assert_eq!(
            rig.run(
                Box::new(IsBallPresentInMyBubbleCondition),
                Attrs::new().with("ballIdAddress", item("B"))
            ),
            Status::Success
        );
        let mut rig2 = Rig::new();
        rig2.world.balls = rig.world.balls.clone();
        rig2.post(ScopeType::Item, "B", Value::Int(4));
        assert_eq!(
            rig2.run(
                Box::new(IsBallPresentInMyBubbleCondition),
                Attrs::new().with("ballIdAddress", item("B"))
            ),
            Status::Failure
        );
        let mut rig3 = Rig::new();
        rig3.world.balls = rig.world.balls.clone();
        rig3.post(ScopeType::Item, "B", Value::Int(4));
        rig3.post(ScopeType::Item, "Bub", Value::Int(9));
        assert_eq!(
            rig3.run(
                Box::new(IsBallPresentInBubbleByAddress),
                Attrs::new().with("bubbleIdAddress", item("Bub")).with("ballIdAddress", item("B"))
            ),
            Status::Success
        );

        let mut rig = rig_with_me_at([0.0; 3]);
        rig.world.add_ball(2, [0.0; 3], 1);
        rig.world.set_dist(1, 2, 500.0);
        rig.post(ScopeType::Item, "B", Value::Int(2));
        let attrs = Attrs::new().with("ballIdAddress", item("B")).with("maxDistance", 500.0);
        assert_eq!(rig.run(Box::new(IsBallWithInDistanceCondition), attrs.clone()), Status::Success);
        let mut rig = rig_with_me_at([0.0; 3]);
        rig.world.add_ball(2, [0.0; 3], 1);
        rig.world.set_dist(1, 2, 500.0);
        rig.post(ScopeType::Item, "B", Value::Int(2));
        assert_eq!(
            rig.run(Box::new(IsBallWithInDistanceCondition), attrs.with("maxDistance", 499.0)),
            Status::Failure
        );
    }

    #[test]
    fn items_within_a_distance_by_the_multiplied_maximum() {
        let mut rig = rig_with_me_at([0.0; 3]);
        rig.world.add_ball(2, [0.0; 3], 1);
        rig.world.set_dist(1, 2, 150.0);
        rig.post(ScopeType::Item, "I", Value::Int(2));
        rig.post(ScopeType::Item, "M", Value::Int(100));
        let attrs = Attrs::new().with("itemIdAddress", item("I")).with("maxDistanceAddress", item("M"));
        assert_eq!(rig.run(Box::new(AreItemsWithInDistanceCondition), attrs.clone()), Status::Failure);
        let mut rig = rig_with_me_at([0.0; 3]);
        rig.world.add_ball(2, [0.0; 3], 1);
        rig.world.set_dist(1, 2, 150.0);
        rig.post(ScopeType::Item, "I", Value::Int(2));
        rig.post(ScopeType::Item, "M", Value::Int(100));
        assert_eq!(
            rig.run(Box::new(AreItemsWithInDistanceCondition), attrs.with("maxDistanceMultiplier", 1.5)),
            Status::Success
        );
    }

    #[test]
    fn orbiting_a_target_needs_the_orbit_mode_and_the_target_to_follow() {
        let mut rig = rig_with_me_at([0.0; 3]);
        {
            let me = rig.world.balls.get_mut(&1).unwrap();
            me.mode = DESTINY_MODE_ORBIT;
            me.follow_id = 5;
        }
        rig.post(ScopeType::Item, "T", Value::Int(5));
        let attrs = Attrs::new().with("targetAddress", item("T"));
        assert_eq!(rig.run(Box::new(IsOrbitingTargetCondition), attrs.clone()), Status::Success);
        let mut rig = rig_with_me_at([0.0; 3]);
        rig.post(ScopeType::Item, "T", Value::Int(5));
        assert_eq!(rig.run(Box::new(IsOrbitingTargetCondition), attrs), Status::Failure);
    }

    #[test]
    fn a_cloaked_ball_and_a_ball_in_warp_and_a_ball_that_is_there() {
        let mut rig = rig_with_me_at([0.0; 3]);
        rig.world.add_ball(2, [0.0; 3], 1);
        rig.world.cloaked.insert(2);
        rig.world.warping.insert(2, 1);
        rig.post(ScopeType::Item, "B", Value::Int(2));
        let attrs = Attrs::new().with("ballIdAddress", item("B"));
        assert_eq!(rig.run(Box::new(IsBallCloakedCondition), attrs.clone()), Status::Success);
        let mut rig = rig_with_me_at([0.0; 3]);
        rig.world.warping.insert(2, 3);
        rig.post(ScopeType::Item, "B", Value::Int(2));
        assert_eq!(rig.run(Box::new(IsBallInWarpCondition), attrs.clone()), Status::Success);
        let mut rig = rig_with_me_at([0.0; 3]);
        rig.post(ScopeType::Item, "B", Value::Int(9));
        assert_eq!(
            rig.run(Box::new(IsBallInWarpCondition), attrs.clone()),
            Status::Failure,
            "a user error is not an error"
        );
        assert_eq!(rig.last_error, None);
        let mut rig = rig_with_me_at([0.0; 3]);
        rig.post(ScopeType::Item, "B", Value::Int(1));
        assert_eq!(rig.run(Box::new(HasValidBallTarget), attrs), Status::Success);
    }

    #[test]
    fn a_location_or_a_ball_beyond_the_minimum_warp_distance() {
        let mut rig = rig_with_me_at([0.0; 3]);
        rig.post(ScopeType::Item, "C", Value::from_vec3([150_001.0, 0.0, 0.0]));
        assert_eq!(
            rig.run(
                Box::new(IsLocationWithinWarpDistance),
                Attrs::new().with("coordinateAddress", item("C"))
            ),
            Status::Success
        );
        let mut rig = rig_with_me_at([0.0; 3]);
        rig.post(ScopeType::Item, "C", Value::from_vec3([150_000.0, 0.0, 0.0]));
        assert_eq!(
            rig.run(
                Box::new(IsLocationWithinWarpDistance),
                Attrs::new().with("coordinateAddress", item("C"))
            ),
            Status::Failure
        );
        let mut rig = rig_with_me_at([0.0; 3]);
        rig.world.add_ball(2, [0.0; 3], 1);
        rig.world.set_dist(2, 1, 200_000.0);
        rig.post(ScopeType::Item, "B", Value::Int(2));
        assert_eq!(
            rig.run(Box::new(IsBallWithinWarpDistance), Attrs::new().with("ballIdAddress", item("B"))),
            Status::Success
        );
    }

    #[test]
    fn a_path_is_clear_when_no_disruptor_is_in_it_or_the_disruptor_is_at_its_end() {
        let attrs = Attrs::new().with("coordinateAddress", item("C"));
        let mut rig = rig_with_me_at([0.0; 3]);
        rig.post(ScopeType::Item, "C", Value::from_vec3([9.0, 9.0, 9.0]));
        assert_eq!(rig.run(Box::new(IsPathToCoordinatesClearOfBubbles), attrs.clone()), Status::Success);
        let mut rig = rig_with_me_at([0.0; 3]);
        rig.world.warp_paths.insert([9, 9, 9], Ok(Some((77, [1.0; 3]))));
        rig.post(ScopeType::Item, "C", Value::from_vec3([9.0, 9.0, 9.0]));
        assert_eq!(rig.run(Box::new(IsPathToCoordinatesClearOfBubbles), attrs.clone()), Status::Failure);
        let mut rig = rig_with_me_at([0.0; 3]);
        rig.world.warp_paths.insert([9, 9, 9], Ok(Some((77, [1.0; 3]))));
        rig.world.warp_bubbles = (vec![77], vec![]);
        rig.post(ScopeType::Item, "C", Value::from_vec3([9.0, 9.0, 9.0]));
        assert_eq!(rig.run(Box::new(IsPathToCoordinatesClearOfBubbles), attrs.clone()), Status::Success);
        let mut rig = rig_with_me_at([0.0; 3]);
        rig.world.warp_paths.insert([9, 9, 9], Err(WorldError::User("too close".into())));
        rig.post(ScopeType::Item, "C", Value::from_vec3([9.0, 9.0, 9.0]));
        assert_eq!(rig.run(Box::new(IsPathToCoordinatesClearOfBubbles), attrs), Status::Failure);
        assert_eq!(rig.last_error, None);
    }
}
