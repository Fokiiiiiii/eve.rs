//! `behaviors/actions/resourcewars.py`: `ResourceWarsFindTarget`, a `FindTarget` that prefers haulers that are nearly full, and miners.

use crate::actions::combattargets::{FindTarget, sorted_last};
use crate::consts::{GROUP_MINING_DRONE, GROUP_NPC_INDUSTRIAL_COMMAND};
use crate::tree::{BehaviorError, TaskCtx};
use crate::utility::ballparks::{get_slim_item, is_ship};
use crate::utility::inventory::get_cargo_used_fraction;
use crate::utility::item_modules::has_fitted_from_type_list;
use crate::utility::math::get_normalized_dict;
use crate::value::Value;

/// `_is_hauler`.
fn is_hauler(t: &mut TaskCtx<'_, '_>, item_id: i64) -> Option<bool> {
    let Some(slim) = get_slim_item(t, item_id) else {
        t.raise(BehaviorError::Task(
            "AttributeError: 'NoneType' object has no attribute 'groupID'".to_string(),
        ));
        return None;
    };
    Some(slim.group_id == GROUP_NPC_INDUSTRIAL_COMMAND)
}

/// `_is_miner`.
fn is_miner(t: &mut TaskCtx<'_, '_>, item_id: i64) -> Option<bool> {
    if !is_ship(t, item_id) {
        return Some(false);
    }
    let list_id = t.required_int("miningModuleTypeListID")?;
    Some(has_fitted_from_type_list(t, item_id, list_id))
}

/// `_get_mining_drone_operators`: who sent the mining drones among the targets.
fn mining_drone_operators(t: &mut TaskCtx<'_, '_>, weights: &[(i64, f64)]) -> Option<Vec<i64>> {
    let mut operators = Vec::new();
    for (item_id, _) in weights {
        let Some(slim) = get_slim_item(t, *item_id) else {
            t.raise(BehaviorError::Task(
                "AttributeError: 'NoneType' object has no attribute 'groupID'".to_string(),
            ));
            return None;
        };
        if slim.group_id != GROUP_MINING_DRONE {
            continue;
        }
        let Some(source_id) = t.world().ball_source_id(*item_id) else {
            t.raise(crate::env::WorldError::Runtime(format!("no ball {item_id}")).into());
            return None;
        };
        if !operators.contains(&source_id) {
            operators.push(source_id);
        }
    }
    Some(operators)
}

/// `ResourceWarsFindTarget.PickTarget`.
pub fn pick_target(find: &FindTarget, t: &mut TaskCtx<'_, '_>, target_list: &[i64]) -> Option<i64> {
    let mut weights: Vec<(i64, f64)> = Vec::new();
    for &item_id in target_list {
        weights.push((item_id, find.evaluate(t, item_id)?));
    }
    let normalized =
        get_normalized_dict(&weights.iter().copied().collect::<std::collections::BTreeMap<_, _>>())?;
    weights = weights.iter().map(|(id, _)| (*id, normalized[id])).collect();
    let mut hauler_cargo: Vec<(i64, f64)> = Vec::new();
    for &item_id in target_list {
        if is_hauler(t, item_id)? {
            hauler_cargo.push((item_id, get_cargo_used_fraction(t, item_id)?));
        }
    }
    if should_prioritize_haulers(t, &hauler_cargo)? {
        for (item_id, cargo_fraction) in &hauler_cargo {
            if let Some(entry) = weights.iter_mut().find(|(id, _)| id == item_id) {
                entry.1 += 1.0 + cargo_fraction;
            }
        }
    } else if should_prioritize_miners(t)? {
        let operators = mining_drone_operators(t, &weights)?;
        for entry in weights.iter_mut() {
            if is_miner(t, entry.0)? || operators.contains(&entry.0) {
                entry.1 += 2.0;
            }
        }
    }
    sorted_last(&weights)
}

/// `_should_prioritize_haulers`.
fn should_prioritize_haulers(t: &mut TaskCtx<'_, '_>, hauler_cargo: &[(i64, f64)]) -> Option<bool> {
    let chance = t.value_at("chanceOfPreferringFillingHaulersAddress")?;
    let dice = t.world().random();
    if Value::Float(dice).py_gt(&chance) {
        return Some(false);
    }
    let threshold = t.value_at("cargoFullFractionThresholdAddress")?;
    Some(hauler_cargo.iter().any(|(_, fraction)| Value::Float(*fraction).py_ge(&threshold)))
}

/// `_should_prioritize_miners`.
fn should_prioritize_miners(t: &mut TaskCtx<'_, '_>) -> Option<bool> {
    let dice = t.world().random();
    let chance = t.value_at("chanceOfPreferringMinersAddress")?;
    Some(Value::Float(dice).py_le(&chance))
}
