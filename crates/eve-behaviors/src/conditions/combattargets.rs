//! `behaviors/conditions/combattargets.py`.

use crate::constants::combat::COMBAT_MIN_EFFECTIVE_RANGE;
use crate::tree::{Task, TaskCtx};
use crate::utility::ballparks::{is_ball_in_range, is_coordinate_in_range};
use crate::utility::roles::is_logistic_role;

pub const MAX_DISTANCE_FROM_COMMANDER: f64 = 50000.0;

enter_task!(HasPendingTarget, |t| {
    let Some(target) = t.value_at("targetAddress") else { return };
    let Some(me) = t.my_item_id() else { return };
    let pending = t.world().has_pending_target(me, target.as_int());
    t.set_status_to_success_if_true_else_to_failed(pending);
});

enter_task!(IsTargeting, |t| {
    let Some(target) = t.value_at("targetAddress") else { return };
    let Some(me) = t.my_item_id() else { return };
    let targeting = t.world().is_targeting(me, target.as_int());
    t.set_status_to_success_if_true_else_to_failed(targeting);
});

/// `AmIWithinMyEffectiveCombatRangeOfMyTarget.get_my_max_effective_combat_range`.
fn my_max_effective_combat_range(t: &mut TaskCtx<'_, '_>) -> Option<f64> {
    let range = t.value_at("maxEffectiveCombatRangeAddress")?;
    let Some(range) = range.as_float() else {
        t.raise(crate::tree::BehaviorError::Task(
            "TypeError: the maximum effective combat range is not a number".to_string(),
        ));
        return None;
    };
    Some(range.max(COMBAT_MIN_EFFECTIVE_RANGE))
}

/// `AmIWithinMyEffectiveCombatRangeOfMyTarget._is_within_range`.
fn is_within_range_of_target(t: &mut TaskCtx<'_, '_>, max_effective_combat_range: f64) -> Option<bool> {
    let target = t.value_at("targetAddress")?;
    let orbit_target = t.value_at("orbitTargetAddress")?;
    let commander = t.value_at("commanderAddress")?;
    let me = t.my_item_id()?;
    if !orbit_target.py_eq(&commander) {
        let target = t.expect_id(&target)?;
        Some(is_ball_in_range(t, me, target, max_effective_combat_range))
    } else {
        let orbit_target_id = t.expect_id(&orbit_target)?;
        if is_logistic_role(t, me) {
            return Some(is_ball_in_range(t, me, orbit_target_id, max_effective_combat_range));
        }
        Some(is_ball_in_range(t, me, orbit_target_id, MAX_DISTANCE_FROM_COMMANDER))
    }
}

enter_task!(AmIWithinMyEffectiveCombatRangeOfMyTarget, |t| {
    t.set_status_to_failed();
    let Some(range) = my_max_effective_combat_range(t) else { return };
    if is_within_range_of_target(t, range) == Some(true) {
        t.set_status_to_success();
    }
});

/// `IsCoordinateWithinMyEffectiveCombatRange`: the same, of a coordinate.
#[derive(Debug, Default)]
pub struct IsCoordinateWithinMyEffectiveCombatRange;

impl Task for IsCoordinateWithinMyEffectiveCombatRange {
    fn type_name(&self) -> &'static str {
        "IsCoordinateWithinMyEffectiveCombatRange"
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        t.set_status_to_failed();
        let Some(range) = my_max_effective_combat_range(t) else { return };
        let Some(coordinate) = t.value_at("coordinateAddress") else { return };
        let Some(position) = coordinate.as_vec3() else {
            t.raise(crate::tree::BehaviorError::Task(format!("TypeError: {coordinate} is not a position")));
            return;
        };
        if is_coordinate_in_range(t, position, range) {
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
    use crate::value::Value;

    fn item(name: &str) -> Value {
        Address::new(ScopeType::Item, name).to_value()
    }

    #[test]
    fn pending_and_targeting_ask_the_dogma_engine_about_my_target() {
        let attrs = Attrs::new().with("targetAddress", item("T"));
        let mut rig = Rig::new();
        rig.world.pending_targets.insert((1, 20));
        rig.post(ScopeType::Item, "T", Value::Int(20));
        assert_eq!(rig.run(Box::new(HasPendingTarget), attrs.clone()), Status::Success);
        assert_eq!(rig.run(Box::new(IsTargeting), attrs.clone()), Status::Failure);
        let mut rig = Rig::new();
        rig.world.targeting.insert((1, 20));
        rig.post(ScopeType::Item, "T", Value::Int(20));
        assert_eq!(rig.run(Box::new(IsTargeting), attrs), Status::Success);
    }

    fn range_attrs() -> Attrs {
        Attrs::new()
            .with("maxEffectiveCombatRangeAddress", item("R"))
            .with("targetAddress", item("T"))
            .with("orbitTargetAddress", item("O"))
            .with("commanderAddress", item("C"))
    }

    #[test]
    fn within_my_effective_range_of_my_target_or_of_the_commander_i_orbit() {
        let mut rig = Rig::new();
        rig.world.add_ball(20, [0.0; 3], 1);
        rig.world.set_dist(1, 20, 25_000.0);
        rig.post(ScopeType::Item, "R", Value::Int(10_000));
        rig.post(ScopeType::Item, "T", Value::Int(20));
        rig.post(ScopeType::Item, "O", Value::Int(20));
        rig.post(ScopeType::Item, "C", Value::Int(99));
        assert_eq!(
            rig.run(Box::new(AmIWithinMyEffectiveCombatRangeOfMyTarget), range_attrs()),
            Status::Success,
            "the range is at least 30 km"
        );
        // orbiting the commander: within 50 km of it, unless logistic
        let mut rig = Rig::new();
        rig.world.add_ball(99, [0.0; 3], 1);
        rig.world.set_dist(1, 99, 40_000.0);
        rig.post(ScopeType::Item, "R", Value::Int(10_000));
        rig.post(ScopeType::Item, "T", Value::Int(20));
        rig.post(ScopeType::Item, "O", Value::Int(99));
        rig.post(ScopeType::Item, "C", Value::Int(99));
        assert_eq!(
            rig.run(Box::new(AmIWithinMyEffectiveCombatRangeOfMyTarget), range_attrs()),
            Status::Success
        );
        let mut rig = Rig::new();
        rig.world.add_ball(99, [0.0; 3], 1);
        rig.world.set_dist(1, 99, 40_000.0);
        rig.post(ScopeType::Item, "R", Value::Int(10_000));
        rig.post(ScopeType::Item, "T", Value::Int(20));
        rig.post(ScopeType::Item, "O", Value::Int(99));
        rig.post(ScopeType::Item, "C", Value::Int(99));
        rig.post(ScopeType::Item, "ROLE", Value::Str("logistic".into()));
        assert_eq!(
            rig.run(Box::new(AmIWithinMyEffectiveCombatRangeOfMyTarget), range_attrs()),
            Status::Failure,
            "a logistic ship stays within its own range of the commander"
        );
    }

    #[test]
    fn a_coordinate_within_my_effective_range() {
        let attrs = Attrs::new()
            .with("maxEffectiveCombatRangeAddress", item("R"))
            .with("coordinateAddress", item("K"));
        let mut rig = Rig::new();
        rig.world.add_ball(1, [0.0; 3], 1);
        rig.post(ScopeType::Item, "R", Value::Int(1));
        rig.post(ScopeType::Item, "K", Value::from_vec3([29_999.0, 0.0, 0.0]));
        assert_eq!(
            rig.run(Box::new(IsCoordinateWithinMyEffectiveCombatRange), attrs.clone()),
            Status::Success
        );
        let mut rig = Rig::new();
        rig.world.add_ball(1, [0.0; 3], 1);
        rig.post(ScopeType::Item, "R", Value::Int(1));
        rig.post(ScopeType::Item, "K", Value::from_vec3([30_001.0, 0.0, 0.0]));
        assert_eq!(rig.run(Box::new(IsCoordinateWithinMyEffectiveCombatRange), attrs), Status::Failure);
    }
}
