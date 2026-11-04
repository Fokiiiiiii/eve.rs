//! `behaviors/utility/ballparks.py`: what the tasks ask of the ballpark about balls, distances and targets.

use crate::consts::CATEGORY_SHIP;
use crate::env::{Ball, SlimItem, WorldError};
use crate::tree::TaskCtx;

pub fn dist3(a: [f64; 3], b: [f64; 3]) -> f64 {
    let (x, y, z) = (a[0] - b[0], a[1] - b[1], a[2] - b[2]);
    (x * x + y * y + z * z).sqrt()
}

/// `context.myBall`: the ball of the entity the behavior is of. A ball that is gone raises what a dead reference does.
pub fn get_my_ball(t: &mut TaskCtx<'_, '_>) -> Option<Ball> {
    let id = t.my_item_id()?;
    let ball = t.world().ball(id);
    if ball.is_none() {
        t.raise(WorldError::Reference.into());
    }
    ball
}

/// `get_ball_position`: `ballpark.balls[ball_id]` raises for a ball that is not there.
pub fn get_ball_position(t: &mut TaskCtx<'_, '_>, ball_id: i64) -> Option<[f64; 3]> {
    match t.world().ball(ball_id) {
        Some(ball) => Some(ball.position),
        None => {
            t.raise(WorldError::Key(ball_id.to_string()).into());
            None
        }
    }
}

/// `get_ball_positions`: the positions of the balls that are in the park and not cloaked.
pub fn get_ball_positions(t: &mut TaskCtx<'_, '_>, ball_ids: &[i64]) -> Vec<[f64; 3]> {
    let mut positions = Vec::new();
    for &ball_id in ball_ids {
        if is_ball_in_park(t, ball_id)
            && !is_ball_cloaked(t, ball_id)
            && let Some(position) = get_ball_position(t, ball_id)
        {
            positions.push(position);
        }
    }
    positions
}

/// `get_ball_warp_to_position`: where the ball is going, or `None` for one that is not there (`UserError`, `RuntimeError`).
pub fn get_ball_warp_to_position(t: &mut TaskCtx<'_, '_>, ball_id: i64) -> Option<[f64; 3]> {
    t.world().ball(ball_id).map(|ball| ball.goto)
}

pub fn is_ball_in_park(t: &mut TaskCtx<'_, '_>, ball_id: i64) -> bool {
    t.world().has_ball(ball_id)
}

/// `is_ball_in_range`.
pub fn is_ball_in_range(t: &mut TaskCtx<'_, '_>, ball1_id: i64, ball2_id: i64, required_range: f64) -> bool {
    match get_distance_between(t, ball1_id, ball2_id) {
        Some(distance) => distance <= required_range,
        None => false,
    }
}

/// `get_distance_between`: `ballpark.GetSurfaceDist`.
pub fn get_distance_between(t: &mut TaskCtx<'_, '_>, ball1_id: i64, ball2_id: i64) -> Option<f64> {
    let distance = t.world().surface_dist(ball1_id, ball2_id);
    t.ok(distance)
}

/// `is_coordinate_in_range`: from my ball.
pub fn is_coordinate_in_range(t: &mut TaskCtx<'_, '_>, coordinate: [f64; 3], required_range: f64) -> bool {
    let Some(me) = t.my_item_id() else { return false };
    let Some(mine) = get_ball_position(t, me) else { return false };
    dist3(coordinate, mine) <= required_range
}

/// `is_coordinate_in_range_of_ball`.
pub fn is_coordinate_in_range_of_ball(
    t: &mut TaskCtx<'_, '_>,
    ball_id: i64,
    coordinates: [f64; 3],
    required_range: f64,
) -> bool {
    let Some(ball) = get_ball_position(t, ball_id) else { return false };
    dist3(ball, coordinates) <= required_range
}

pub fn get_item_owner(t: &mut TaskCtx<'_, '_>, item_id: i64) -> Option<i64> {
    t.world().slim_item(item_id)?.owner_id
}

/// `is_ball_cloaked`: a ball the ballpark cannot tell about is not cloaked.
pub fn is_ball_cloaked(t: &mut TaskCtx<'_, '_>, ball_id: i64) -> bool {
    match t.world().is_cloaked(ball_id) {
        Ok(cloaked) => cloaked,
        Err(WorldError::User(_) | WorldError::Runtime(_)) => false,
        Err(other) => {
            t.raise(other.into());
            false
        }
    }
}

/// `get_ball`: `ballpark.GetBall` raises for a ball that is not there.
pub fn get_ball(t: &mut TaskCtx<'_, '_>, ball_id: i64) -> Option<Ball> {
    let ball = t.world().ball(ball_id);
    if ball.is_none() {
        t.raise(WorldError::Runtime(format!("no ball {ball_id}")).into());
    }
    ball
}

/// `get_balls_in_bubble`.
pub fn get_balls_in_bubble(t: &mut TaskCtx<'_, '_>, bubble_id: i64) -> Option<Vec<i64>> {
    t.world().ball_ids_in_bubble(bubble_id)
}

/// `get_ship_balls_in_bubble`.
pub fn get_ship_balls_in_bubble(t: &mut TaskCtx<'_, '_>, bubble_id: i64) -> Vec<i64> {
    t.world().ship_ids_in_bubble(bubble_id)
}

/// `get_slim_item`.
pub fn get_slim_item(t: &mut TaskCtx<'_, '_>, ball_id: i64) -> Option<SlimItem> {
    t.world().slim_item(ball_id)
}

/// `get_ball_type_id`.
pub fn get_ball_type_id(t: &mut TaskCtx<'_, '_>, ball_id: i64) -> Option<i64> {
    get_slim_item(t, ball_id).map(|slim| slim.type_id)
}

/// `get_my_position`.
pub fn get_my_position(t: &mut TaskCtx<'_, '_>) -> Option<[f64; 3]> {
    get_my_ball(t).map(|ball| ball.position)
}

/// `is_invulnerable`.
pub fn is_invulnerable(t: &mut TaskCtx<'_, '_>, item_id: i64) -> bool {
    t.world().is_invulnerable(item_id)
}

/// `is_target_valid`: the target is there, is of the kinds asked for and is not cloaked. An empty list of kinds asks for none.
pub fn is_target_valid(
    t: &mut TaskCtx<'_, '_>,
    target_id: Option<i64>,
    target_list: Option<&[i64]>,
    required_types: &[i64],
    required_groups: &[i64],
    required_categories: &[i64],
) -> bool {
    let Some(target_id) = target_id else { return false };
    let Some(slim) = get_slim_item(t, target_id) else { return false };
    if target_list.is_some_and(|list| !list.contains(&target_id)) {
        return false;
    }
    if !required_types.is_empty() && !required_types.contains(&slim.type_id) {
        return false;
    }
    if !required_groups.is_empty() && !required_groups.contains(&slim.group_id) {
        return false;
    }
    if !required_categories.is_empty() && !required_categories.contains(&slim.category_id) {
        return false;
    }
    !is_ball_cloaked(t, target_id)
}

/// `is_ship`.
pub fn is_ship(t: &mut TaskCtx<'_, '_>, item_id: i64) -> bool {
    get_slim_item(t, item_id).is_some_and(|slim| slim.category_id == CATEGORY_SHIP)
}

/// `get_specific_globals`: the global balls of a type, of a group or of a category (given none, none).
pub fn get_specific_globals(
    t: &mut TaskCtx<'_, '_>,
    type_id: Option<i64>,
    group_id: Option<i64>,
    category_id: Option<i64>,
) -> Vec<(i64, Ball)> {
    let mut specific = Vec::new();
    if type_id.is_none() && group_id.is_none() && category_id.is_none() {
        return specific;
    }
    for global_ball_id in t.world().global_ball_ids() {
        let Some(slim) = get_slim_item(t, global_ball_id) else {
            t.raise(WorldError::Runtime(format!("no slim item for the global ball {global_ball_id}")).into());
            return specific;
        };
        if (Some(slim.type_id) == type_id
            || Some(slim.group_id) == group_id
            || Some(slim.category_id) == category_id)
            && let Some(ball) = t.world().ball(global_ball_id)
        {
            specific.push((global_ball_id, ball));
        }
    }
    specific
}

/// `get_id_and_types_in_bubble_by_typelist`: `(ball id, type id)` of the balls of a bubble whose type is in the list.
pub fn get_id_and_types_in_bubble_by_typelist(
    t: &mut TaskCtx<'_, '_>,
    bubble_id: i64,
    type_list_id: i64,
) -> Vec<(i64, i64)> {
    let mut balls_of_interest = Vec::new();
    let Some(balls_in_bubble) = t.world().ball_ids_in_bubble(bubble_id) else { return balls_of_interest };
    if balls_in_bubble.is_empty() {
        return balls_of_interest;
    }
    let types_of_interest = t.world().type_ids_in_list(type_list_id);
    for ball_id in balls_in_bubble {
        if !t.world().has_ball(ball_id) {
            continue;
        }
        let Some(slim) = t.world().slim_item(ball_id) else { continue };
        if types_of_interest.contains(&slim.type_id) {
            balls_of_interest.push((ball_id, slim.type_id));
        }
    }
    balls_of_interest
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::tree::tests::with_task;

    #[test]
    fn a_ball_that_is_not_there_raises_for_its_position_and_gives_no_warp_position() {
        let (rig, _) = with_task(|_| {}, |t| get_ball_position(t, 5));
        assert_eq!(rig.last_error, Some(crate::tree::BehaviorError::World(WorldError::Key("5".into()))));
        let (_, answer) = with_task(|_| {}, |t| get_ball_warp_to_position(t, 5));
        assert_eq!(answer, Some(None));
    }

    #[test]
    fn a_target_is_valid_when_it_is_there_of_the_right_kind_and_not_cloaked() {
        let (_, answer) = with_task(
            |rig| {
                rig.world.add_ball(10, [0.0; 3], 600);
                rig.world.slims.get_mut(&10).unwrap().group_id = 25;
                rig.world.add_ball(11, [0.0; 3], 601);
                rig.world.cloaked.insert(11);
            },
            |t| {
                (
                    is_target_valid(t, Some(10), None, &[], &[], &[]),
                    is_target_valid(t, Some(10), Some(&[9]), &[], &[], &[]),
                    is_target_valid(t, Some(10), None, &[600], &[25], &[]),
                    is_target_valid(t, Some(10), None, &[601], &[], &[]),
                    is_target_valid(t, Some(11), None, &[], &[], &[]),
                    is_target_valid(t, None, None, &[], &[], &[]),
                    is_target_valid(t, Some(99), None, &[], &[], &[]),
                )
            },
        );
        assert_eq!(answer, Some((true, false, true, false, false, false, false)));
    }

    #[test]
    fn distances_in_range_and_positions() {
        let (_, answer) = with_task(
            |rig| {
                rig.world.add_ball(1, [0.0, 0.0, 0.0], 1);
                rig.world.add_ball(2, [3.0, 4.0, 0.0], 1);
                rig.world.set_dist(1, 2, 100.0);
            },
            |t| {
                (
                    is_ball_in_range(t, 1, 2, 100.0),
                    is_ball_in_range(t, 1, 2, 99.0),
                    is_coordinate_in_range(t, [3.0, 4.0, 0.0], 5.0),
                    is_coordinate_in_range_of_ball(t, 2, [0.0, 0.0, 0.0], 4.9),
                    get_ball_positions(t, &[1, 2, 3]),
                )
            },
        );
        assert_eq!(answer, Some((true, false, true, false, vec![[0.0; 3], [3.0, 4.0, 0.0]])));
    }

    #[test]
    fn a_cloaked_ball_is_left_out_of_the_positions_and_a_ship_is_a_ship_by_its_category() {
        let (_, answer) = with_task(
            |rig| {
                rig.world.add_ball(1, [1.0; 3], 1);
                rig.world.add_ball(2, [2.0; 3], 1);
                rig.world.cloaked.insert(2);
                rig.world.slims.get_mut(&1).unwrap().category_id = 6;
            },
            |t| (get_ball_positions(t, &[1, 2]), is_ship(t, 1), is_ship(t, 2), is_ship(t, 3)),
        );
        assert_eq!(answer, Some((vec![[1.0; 3]], true, false, false)));
    }

    #[test]
    fn the_global_balls_of_a_kind_and_the_balls_of_a_bubble_of_a_type_list() {
        let (_, answer) = with_task(
            |rig| {
                rig.world.add_ball(1, [0.0; 3], 100);
                rig.world.add_ball(2, [0.0; 3], 200);
                rig.world.globals = vec![1, 2];
                rig.world.bubbles.insert(9, vec![1, 2, 3]);
                rig.world.type_ids_by_list.insert(7, vec![200]);
            },
            |t| {
                (
                    get_specific_globals(t, Some(100), None, None)
                        .iter()
                        .map(|(id, _)| *id)
                        .collect::<Vec<_>>(),
                    get_specific_globals(t, None, None, None).len(),
                    get_id_and_types_in_bubble_by_typelist(t, 9, 7),
                    get_id_and_types_in_bubble_by_typelist(t, 8, 7),
                )
            },
        );
        assert_eq!(answer, Some((vec![1], 0, vec![(2, 200)], vec![])));
    }
}
