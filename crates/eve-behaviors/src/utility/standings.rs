//! `behaviors/utility/standings.py`, and `classify_standings` of `npcs.common.standings`.

use crate::tree::TaskCtx;
use crate::utility::blackboards::get_response_thresholds;
use crate::utility::owner::get_owner_id;

pub const STANDINGS_HOSTILE: i64 = -1;
pub const STANDINGS_NEUTRAL: i64 = 0;
pub const STANDINGS_FRIENDLY: i64 = 1;

/// `classify_standings`.
pub fn classify_standings(hostile_threshold: f64, friendly_threshold: f64, standings: f64) -> i64 {
    if standings <= hostile_threshold {
        STANDINGS_HOSTILE
    } else if standings >= friendly_threshold {
        STANDINGS_FRIENDLY
    } else {
        STANDINGS_NEUTRAL
    }
}

pub fn get_standings_between(t: &mut TaskCtx<'_, '_>, to_id: i64) -> Option<f64> {
    let owner = get_owner_id(t)?;
    Some(t.world().standings_from_npc_to_item(owner, to_id))
}

/// `classify_standings_between`: the thresholds are missing for an entity that has none (raises as the client does).
pub fn classify_standings_between(t: &mut TaskCtx<'_, '_>, to_id: i64) -> Option<i64> {
    let thresholds = thresholds_or_raise(t)?;
    let standings = get_standings_between(t, to_id)?;
    Some(classify_standings(
        thresholds.hostile_response_threshold,
        thresholds.friendly_response_threshold,
        standings,
    ))
}

/// `is_hostile_towards`.
pub fn is_hostile_towards(t: &mut TaskCtx<'_, '_>, to_id: i64) -> Option<bool> {
    let thresholds = thresholds_or_raise(t)?;
    let standings = get_standings_between(t, to_id)?;
    Some(standings < thresholds.hostile_response_threshold)
}

fn thresholds_or_raise(t: &mut TaskCtx<'_, '_>) -> Option<crate::env::ResponseThresholds> {
    let thresholds = get_response_thresholds(t);
    if thresholds.is_none() && !t.faulted() {
        t.raise(crate::tree::BehaviorError::Task(
            "AttributeError: 'NoneType' object has no attribute 'hostile_response_threshold'".to_string(),
        ));
    }
    thresholds
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::env::{ResponseThresholds, SlimItem};
    use crate::tree::tests::with_task;

    #[test]
    fn standings_are_hostile_at_or_below_the_hostile_threshold_and_friendly_at_or_above_the_friendly_one() {
        let classes: Vec<i64> =
            [-5.0, -1.0, 0.0, 1.0, 5.0].into_iter().map(|s| classify_standings(-1.0, 1.0, s)).collect();
        assert_eq!(classes, [-1, -1, 0, 1, 1]);
    }

    #[test]
    fn my_owners_standing_to_an_item_is_classified_by_my_thresholds_and_hostile_is_strictly_below() {
        let (_, answer) = with_task(
            |rig| {
                rig.ctx.my_slim_item = Some(SlimItem { owner_id: Some(1000006), ..Default::default() });
                rig.world.thresholds = Some(ResponseThresholds {
                    hostile_response_threshold: -1.0,
                    friendly_response_threshold: 1.0,
                });
                rig.world.standings.insert((1000006, 20), -1.0);
                rig.world.standings.insert((1000006, 21), -2.0);
            },
            |t| (classify_standings_between(t, 20), is_hostile_towards(t, 20), is_hostile_towards(t, 21)),
        );
        assert_eq!(answer, Some((Some(STANDINGS_HOSTILE), Some(false), Some(true))));
    }

    #[test]
    fn an_entity_with_no_thresholds_raises() {
        let (rig, _) = with_task(
            |rig| rig.ctx.my_slim_item = Some(SlimItem { owner_id: Some(1), ..Default::default() }),
            |t| is_hostile_towards(t, 1),
        );
        assert!(rig.last_error.is_some());
    }
}
