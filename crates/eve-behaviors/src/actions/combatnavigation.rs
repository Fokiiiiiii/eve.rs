//! `behaviors/actions/combatnavigation.py`: how far to orbit and attack from, whom to orbit, and when to warp in.

use crate::constants::behaviorroles::ROLE_COMMANDER;
use crate::constants::behaviorroles::{ROLE_DPS, ROLE_TACKLER};
use crate::consts::ATTRIBUTE_BEHAVIOR_COMBAT_ORBIT_RANGE;
use crate::env::WorldError;
use crate::tree::{BehaviorError, Task, TaskCtx};
use crate::utility::dogmatic::get_my_attribute_value;
use crate::value::Value;

pub const DEFAULT_COMBAT_ORBIT_RANGE: f64 = 10_000.0;
pub const DEFAULT_COMBAT_ATTACK_RANGE: f64 = 30_000.0;
const COMBAT_ATTACK_RANGE_FACTOR: f64 = 3.0;
const DEFAULT_MAXIMUM_ORBIT_RANGE: f64 = 100_000.0;
const FALLOFF_ORBIT_DISTANCE_FACTOR: f64 = 0.25;
const OPTIMAL_ORBIT_DISTANCE_FACTOR: f64 = 0.75;
const COMBAT_WARP_AT_DISTANCE_FACTOR: f64 = 50_000.0;
const DEFAULT_ANCHOR_ORBIT_RANGE: (f64, f64) = (1000.0, 4000.0);

/// `FLEET_NAVIGATION_BY_ROLE[role]['orbitTarget']`: whom a role orbits (`None` for a role that is not in the table).
fn fleet_navigation_orbit_target(role: &str) -> Option<&'static str> {
    match role {
        ROLE_DPS => Some(ROLE_COMMANDER),
        ROLE_TACKLER => Some("target"),
        _ => None,
    }
}

fn type_error(t: &mut TaskCtx<'_, '_>, message: &str) {
    t.raise(BehaviorError::Task(format!("TypeError: {message}")));
}

/// The numbers a `BehaviorAuthoringException` names: the behavior and the type of my entity.
fn authoring_error(t: &mut TaskCtx<'_, '_>, what: &str) {
    let behavior = t.behavior_id();
    let type_id = t.ctx().my_slim_item.as_ref().map(|slim| slim.type_id);
    t.raise(BehaviorError::Authoring(format!("Behavior={behavior:?} for entity_type={type_id:?} {what}")));
}

/// Works out how far to orbit the target and how far to attack from, unless it is on the blackboard already.
#[derive(Debug, Default)]
pub struct GetCombatOrbitAndAttackRange;

impl GetCombatOrbitAndAttackRange {
    /// `_get_min_optimal_and_falloff_from_addresses`: what the blackboard has (none where it has none).
    fn min_optimal_and_falloff_from_addresses(t: &mut TaskCtx<'_, '_>) -> Option<(Value, Value)> {
        let mut min_optimal = Value::Float(0.0);
        let mut min_falloff = Value::Float(0.0);
        if t.has_attribute("minOptimalAddress") {
            min_optimal = t.value_at("minOptimalAddress")?;
        }
        if t.has_attribute("minFalloffAddress") {
            min_falloff = t.value_at("minFalloffAddress")?;
        }
        Some((min_optimal, min_falloff))
    }

    /// `_calculate_orbit_and_attack_range`.
    fn calculate_orbit_and_attack_range(t: &mut TaskCtx<'_, '_>) -> Option<(f64, f64)> {
        let (min_optimal, min_falloff) = Self::min_optimal_and_falloff_from_addresses(t)?;
        let Some(optimal) = min_optimal.as_float() else {
            type_error(t, "unsupported operand type(s) for *: 'NoneType' and 'float'");
            return None;
        };
        let (orbit_range, attack_range) = if !min_falloff.truthy() {
            (optimal * OPTIMAL_ORBIT_DISTANCE_FACTOR, optimal)
        } else {
            let falloff = min_falloff.as_float().unwrap_or(0.0);
            (
                optimal + falloff * FALLOFF_ORBIT_DISTANCE_FACTOR,
                optimal + falloff * COMBAT_ATTACK_RANGE_FACTOR,
            )
        };
        Some((
            if orbit_range != 0.0 { orbit_range } else { DEFAULT_COMBAT_ORBIT_RANGE },
            if attack_range != 0.0 { attack_range } else { DEFAULT_COMBAT_ATTACK_RANGE },
        ))
    }

    /// `_get_desired_orbit_and_attack_range`.
    fn desired_orbit_and_attack_range(t: &mut TaskCtx<'_, '_>) -> Option<(Value, Value)> {
        let behavior_orbit_range = t.attribute("orbitRange");
        let attack_range = t.attribute("attackRange");
        let behavior_orbit_range =
            if behavior_orbit_range.is_none() { Value::Int(0) } else { behavior_orbit_range };
        let attack_range = if attack_range.is_none() { Value::Int(0) } else { attack_range };
        if behavior_orbit_range.truthy() && attack_range.truthy() {
            return Some((behavior_orbit_range, attack_range));
        }
        let (calculated_orbit_range, calculated_attack_range) = Self::calculate_orbit_and_attack_range(t)?;
        if calculated_orbit_range == 0.0 && !behavior_orbit_range.truthy() {
            authoring_error(
                t,
                "missing orbitRange or optimalRange from address, might be missing task=GetMyMinOptimalAndFalloff",
            );
            return None;
        }
        let mut orbit_range = if behavior_orbit_range.truthy() {
            behavior_orbit_range
        } else {
            Value::Float(calculated_orbit_range.min(DEFAULT_MAXIMUM_ORBIT_RANGE))
        };
        let dogma_orbit_range = get_my_attribute_value(t, ATTRIBUTE_BEHAVIOR_COMBAT_ORBIT_RANGE)?;
        if dogma_orbit_range > 0.0 {
            // `min(orbit_range, dogma_orbit_range)`: the first of two that are equal
            let dogma = Value::Float(dogma_orbit_range);
            if dogma.py_lt(&orbit_range) {
                orbit_range = dogma;
            }
        }
        let attack_range =
            if attack_range.truthy() { attack_range } else { Value::Float(calculated_attack_range) };
        Some((orbit_range, attack_range))
    }
}

impl Task for GetCombatOrbitAndAttackRange {
    fn type_name(&self) -> &'static str {
        "GetCombatOrbitAndAttackRange"
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        t.set_status_to_success();
        let Some(orbit_address) = t.required_address("orbitRangeAddress") else { return };
        if !t.get_last_blackboard_value(&orbit_address).is_none() {
            return;
        }
        let Some((orbit_range, attack_range)) = Self::desired_orbit_and_attack_range(t) else { return };
        if orbit_range.py_gt(&attack_range) {
            authoring_error(t, "has less attack range than orbit range");
            return;
        }
        let Some(attack_address) = t.required_address("attackRangeAddress") else { return };
        t.send_blackboard_value(&orbit_address, orbit_range);
        t.send_blackboard_value(&attack_address, attack_range);
    }
}

enter_task!(
    /// Puts the commander to orbit on the blackboard for a role that orbits it, and the combat target for the others.
    GetTargetToOrbit,
    |t| {
        t.set_status_to_success();
        let Some(commander_address) = t.required_address("commanderAddress") else { return };
        let commander_id = t.get_last_blackboard_value(&commander_address);
        let Some(orbit_target_address) = t.required_address("orbitTargetAddress") else { return };
        if should_orbit_commander(t, &commander_id) {
            t.send_blackboard_value(&orbit_target_address, commander_id);
        } else {
            let Some(target_address) = t.required_address("combatTargetAddress") else { return };
            let combat_target_id = t.get_last_blackboard_value(&target_address);
            t.send_blackboard_value(&orbit_target_address, combat_target_id);
        }
    }
);

/// `_ShouldOrbitCommander`.
fn should_orbit_commander(t: &mut TaskCtx<'_, '_>, commander_id: &Value) -> bool {
    if commander_id.is_none() {
        return false;
    }
    if t.ctx().my_item_id.is_some_and(|me| commander_id.py_eq(&Value::Int(me))) {
        return false;
    }
    let Some(role_address) = t.required_address("roleAddress") else { return false };
    let role = t.get_last_blackboard_value(&role_address);
    if !role.truthy() {
        return false;
    }
    role.as_str().and_then(fleet_navigation_orbit_target) == Some(ROLE_COMMANDER)
}

enter_task!(
    /// Puts the distance a fleet member orbits its commander at on the blackboard, by its role.
    GetFleetCombatOrbitRangeByRole,
    |t| {
        t.set_status_to_success();
        let Some(role_address) = t.required_address("roleAddress") else { return };
        let role = t.get_last_blackboard_value(&role_address);
        let (min_orbit_range, max_orbit_range) = match role.as_str() {
            Some(ROLE_DPS) | None => DEFAULT_ANCHOR_ORBIT_RANGE,
            Some(ROLE_TACKLER) => {
                // the table has no orbit range for it
                t.raise(WorldError::Key("orbitRange".to_string()).into());
                return;
            }
            Some(_) => DEFAULT_ANCHOR_ORBIT_RANGE,
        };
        // `random.randrange(min, max)`: a whole number from the first up to the last but one
        let orbit_range = t.world().random_int(min_orbit_range as i64, max_orbit_range as i64 - 1);
        let Some(address) = t.required_address("orbitRangeAddress") else { return };
        t.send_blackboard_value(&address, Value::Int(orbit_range));
    }
);

enter_task!(
    /// Works out the distance to warp in at from the combat orbit range, unless it is on the blackboard already.
    GetCombatWarpAtDistance,
    |t| {
        t.set_status_to_success();
        let Some(address) = t.required_address("combatWarpAtDistanceAddress") else { return };
        if !t.get_last_blackboard_value(&address).is_none() {
            return;
        }
        let Some(orbit_address) = t.required_address("combatOrbitRangeAddress") else { return };
        let combat_orbit_range = t.get_last_blackboard_value(&orbit_address);
        if !combat_orbit_range.truthy() {
            let behavior = t.behavior_id();
            let me = t.ctx().my_item_id;
            t.raise(BehaviorError::Authoring(format!(
                "Behavior={behavior:?} for entity={me:?} has no combat_orbit_range for GetCombatWarpAtDistance"
            )));
            return;
        }
        let Some(range) = combat_orbit_range.as_float() else {
            type_error(t, "unsupported operand type(s) for /: 'str' and 'float'");
            return;
        };
        let factor = 1.0 - 1.0 / ((range / COMBAT_WARP_AT_DISTANCE_FACTOR).powf(2.0)).exp();
        t.send_blackboard_value(&address, Value::Float(factor * range));
    }
);

#[cfg(test)]
mod tests {
    use super::*;
    use crate::blackboards::{Address, ScopeType};
    use crate::env::SlimItem;
    use crate::tree::tests::Rig;
    use crate::tree::{Attrs, Status};

    fn item(name: &str) -> Value {
        Address::new(ScopeType::Item, name).to_value()
    }

    fn range_attrs() -> Attrs {
        Attrs::new().with("orbitRangeAddress", item("Orbit")).with("attackRangeAddress", item("Attack"))
    }

    fn rig() -> Rig {
        let mut rig = Rig::new();
        rig.ctx.my_slim_item = Some(SlimItem { item_id: 1, type_id: 700, ..Default::default() });
        rig
    }

    #[test]
    fn the_ranges_the_task_gives_are_used_as_they_are() {
        let mut rig = rig();
        let attrs = range_attrs().with("orbitRange", 5000i64).with("attackRange", 20000i64);
        assert_eq!(rig.run(Box::new(GetCombatOrbitAndAttackRange), attrs), Status::Success);
        assert_eq!(rig.last(ScopeType::Item, "Orbit"), Value::Int(5000));
        assert_eq!(rig.last(ScopeType::Item, "Attack"), Value::Int(20000));
    }

    #[test]
    fn the_ranges_are_worked_out_from_the_optimal_and_falloff_ranges_and_the_dogma_limit() {
        let attrs = range_attrs()
            .with("minOptimalAddress", item("Optimal"))
            .with("minFalloffAddress", item("Falloff"));
        // with a falloff
        let mut rig = rig();
        rig.post(ScopeType::Item, "Optimal", Value::Float(10_000.0));
        rig.post(ScopeType::Item, "Falloff", Value::Float(8000.0));
        rig.run(Box::new(GetCombatOrbitAndAttackRange), attrs.clone());
        assert_eq!(rig.last(ScopeType::Item, "Orbit"), Value::Float(12_000.0));
        assert_eq!(rig.last(ScopeType::Item, "Attack"), Value::Float(34_000.0));
        // without one, and the entity may not orbit as far
        let mut rig = self::rig();
        rig.post(ScopeType::Item, "Optimal", Value::Float(10_000.0));
        rig.post(ScopeType::Item, "Falloff", Value::Float(0.0));
        rig.world.attributes.insert((1, ATTRIBUTE_BEHAVIOR_COMBAT_ORBIT_RANGE), 6000.0);
        rig.run(Box::new(GetCombatOrbitAndAttackRange), attrs.clone());
        assert_eq!(rig.last(ScopeType::Item, "Orbit"), Value::Float(6000.0));
        assert_eq!(rig.last(ScopeType::Item, "Attack"), Value::Float(10_000.0));
        // a far away optimal range is not orbited beyond the maximum
        let mut rig = self::rig();
        rig.post(ScopeType::Item, "Optimal", Value::Float(300_000.0));
        rig.run(Box::new(GetCombatOrbitAndAttackRange), attrs.clone());
        assert_eq!(rig.last(ScopeType::Item, "Orbit"), Value::Float(100_000.0));
        assert_eq!(rig.last(ScopeType::Item, "Attack"), Value::Float(300_000.0));
        // what is on the blackboard already stays
        let mut rig = self::rig();
        rig.post(ScopeType::Item, "Orbit", Value::Float(1.0));
        rig.run(Box::new(GetCombatOrbitAndAttackRange), attrs);
        assert_eq!(rig.last(ScopeType::Item, "Orbit"), Value::Float(1.0));
        assert_eq!(rig.last(ScopeType::Item, "Attack"), Value::None);
    }

    #[test]
    fn no_optimal_range_where_one_is_asked_for_and_an_orbit_beyond_the_attack_range_are_errors() {
        let mut rig = rig();
        rig.run(
            Box::new(GetCombatOrbitAndAttackRange),
            range_attrs().with("minOptimalAddress", item("Optimal")),
        );
        assert!(
            matches!(&rig.last_error, Some(BehaviorError::Task(m)) if m.starts_with("TypeError")),
            "an optimal range that is not there"
        );
        let mut rig = self::rig();
        rig.post(ScopeType::Item, "Optimal", Value::Float(0.0));
        rig.run(
            Box::new(GetCombatOrbitAndAttackRange),
            range_attrs().with("minOptimalAddress", item("Optimal")),
        );
        // an optimal of 0 gives the defaults
        assert_eq!(rig.last(ScopeType::Item, "Orbit"), Value::Float(10_000.0));
        let mut rig = self::rig();
        let attrs = range_attrs().with("orbitRange", 5000i64).with("attackRange", 4000i64);
        rig.run(Box::new(GetCombatOrbitAndAttackRange), attrs);
        assert!(
            matches!(&rig.last_error, Some(BehaviorError::Authoring(m)) if m.contains("less attack range than orbit range"))
        );
    }

    #[test]
    fn a_role_that_follows_the_commander_orbits_it_and_the_others_the_combat_target() {
        let attrs = Attrs::new()
            .with("commanderAddress", item("Commander"))
            .with("roleAddress", item("Role"))
            .with("combatTargetAddress", item("Combat"))
            .with("orbitTargetAddress", item("OrbitTarget"));
        for (role, commander, wanted) in [
            (Some("dps"), Some(5), 5),
            (Some("tackler"), Some(5), 20),
            (Some("dps"), Some(1), 20),
            (Some("dps"), None, 20),
            (Some("unknown"), Some(5), 20),
            (None, Some(5), 20),
        ] {
            let mut rig = rig();
            rig.post(ScopeType::Item, "Combat", Value::Int(20));
            if let Some(commander) = commander {
                rig.post(ScopeType::Item, "Commander", Value::Int(commander));
            }
            if let Some(role) = role {
                rig.post(ScopeType::Item, "Role", Value::Str(role.into()));
            }
            assert_eq!(rig.run(Box::new(GetTargetToOrbit), attrs.clone()), Status::Success);
            assert_eq!(
                rig.last(ScopeType::Item, "OrbitTarget"),
                Value::Int(wanted),
                "{role:?} {commander:?}"
            );
        }
    }

    #[test]
    fn a_fleet_member_orbits_at_a_whole_number_of_meters_by_its_role() {
        let attrs = Attrs::new().with("roleAddress", item("Role")).with("orbitRangeAddress", item("Orbit"));
        let mut rig = rig();
        rig.world.set_numbers(&[0.5]);
        rig.post(ScopeType::Item, "Role", Value::Str("dps".into()));
        assert_eq!(rig.run(Box::new(GetFleetCombatOrbitRangeByRole), attrs.clone()), Status::Success);
        assert_eq!(rig.last(ScopeType::Item, "Orbit"), Value::Int(2500));
        let mut rig = self::rig();
        rig.world.set_numbers(&[0.9999]);
        rig.run(Box::new(GetFleetCombatOrbitRangeByRole), attrs.clone());
        assert_eq!(
            rig.last(ScopeType::Item, "Orbit"),
            Value::Int(3999),
            "the default range for a role that has none"
        );
        let mut rig = self::rig();
        rig.post(ScopeType::Item, "Role", Value::Str("tackler".into()));
        rig.run(Box::new(GetFleetCombatOrbitRangeByRole), attrs);
        assert_eq!(rig.last_error, Some(BehaviorError::World(WorldError::Key("orbitRange".into()))));
    }

    #[test]
    fn the_distance_to_warp_in_at_is_worked_out_from_the_orbit_range_once() {
        let attrs = Attrs::new()
            .with("combatWarpAtDistanceAddress", item("Warp"))
            .with("combatOrbitRangeAddress", item("Orbit"));
        let mut rig = rig();
        rig.post(ScopeType::Item, "Orbit", Value::Float(25_000.0));
        assert_eq!(rig.run(Box::new(GetCombatWarpAtDistance), attrs.clone()), Status::Success);
        let Value::Float(distance) = rig.last(ScopeType::Item, "Warp") else { panic!("no distance") };
        assert!((distance - 25_000.0 * (1.0 - 1.0 / 0.25f64.exp())).abs() < 1e-6, "{distance}");
        rig.post(ScopeType::Item, "Orbit", Value::Float(100.0));
        rig.run(Box::new(GetCombatWarpAtDistance), attrs.clone());
        assert_eq!(rig.last(ScopeType::Item, "Warp"), Value::Float(distance), "it was there already");
        let mut rig = self::rig();
        rig.run(Box::new(GetCombatWarpAtDistance), attrs);
        assert!(
            matches!(&rig.last_error, Some(BehaviorError::Authoring(m)) if m.contains("has no combat_orbit_range"))
        );
    }
}
