//! `behaviors/targetEvaluation.py`: how much a target is worth, by the function an authored task names.

use crate::constants::combat::{
    BASIC_TARGET_EVALUATOR, COST_TARGET_EVALUATOR, FITTED_MODULE_GROUP_TARGET_EVALUATOR,
    RANDOM_TARGET_EVALUATOR, RANGE_TARGET_EVALUATOR, SIGNATURE_RADIUS_AND_VELOCITY_EVALUATOR,
    SIZE_TARGET_EVALUATOR, THREAT_TARGET_EVALUATOR, TOWING_TOW_OBJECTIVE_EVALUATOR,
};
use crate::consts::{ATTRIBUTE_MAX_VELOCITY, ATTRIBUTE_SIGNATURE_RADIUS};
use crate::env::WorldError;
use crate::tree::{BehaviorError, TaskCtx};
use crate::utility::ballparks::get_my_ball;
use crate::utility::threat::evaluate_target as evaluate_threat;
use crate::value::Value;

/// The target evaluators of `targetEvaluationRegistry`.
#[derive(Debug, Clone, PartialEq)]
pub enum TargetEvaluator {
    /// `TargetEvaluator`: everything is worth nothing.
    Zero,
    Random,
    Size,
    Threat {
        threat_sample_interval_seconds: f64,
    },
    Cost,
    FittedModuleGroup {
        preferred_module_groups: Vec<i64>,
    },
    Range {
        my_ball_id: i64,
    },
    SignatureRadiusAndVelocity,
    TowingTowObjective,
}

impl TargetEvaluator {
    /// `targetEvaluationRegistry[name](context, attributes)` with the attributes of the task that asks. An unknown name raises (`KeyError`).
    pub fn create(t: &mut TaskCtx<'_, '_>, name: &Value) -> Option<TargetEvaluator> {
        let Some(name) = name.as_str() else {
            t.raise(WorldError::Key(name.to_string()).into());
            return None;
        };
        Some(match name {
            BASIC_TARGET_EVALUATOR => TargetEvaluator::Zero,
            RANDOM_TARGET_EVALUATOR => TargetEvaluator::Random,
            SIZE_TARGET_EVALUATOR => TargetEvaluator::Size,
            THREAT_TARGET_EVALUATOR => TargetEvaluator::threat(t),
            COST_TARGET_EVALUATOR => TargetEvaluator::Cost,
            FITTED_MODULE_GROUP_TARGET_EVALUATOR => {
                let preferred = t.attribute("preferredModuleGroups");
                TargetEvaluator::FittedModuleGroup {
                    preferred_module_groups: preferred
                        .items()
                        .unwrap_or(&[])
                        .iter()
                        .filter_map(Value::as_int)
                        .collect(),
                }
            }
            RANGE_TARGET_EVALUATOR => {
                let my_ball = get_my_ball(t)?;
                TargetEvaluator::Range { my_ball_id: my_ball.id }
            }
            SIGNATURE_RADIUS_AND_VELOCITY_EVALUATOR => TargetEvaluator::SignatureRadiusAndVelocity,
            TOWING_TOW_OBJECTIVE_EVALUATOR => TargetEvaluator::TowingTowObjective,
            other => {
                t.raise(WorldError::Key(other.to_string()).into());
                return None;
            }
        })
    }

    /// `ThreatTargetEvaluator(context, attributes)`: the interval is `threatSampleIntervalSeconds` of the attributes, ten seconds when they have none.
    pub fn threat(t: &mut TaskCtx<'_, '_>) -> TargetEvaluator {
        let interval = t.attribute("threatSampleIntervalSeconds");
        TargetEvaluator::Threat {
            threat_sample_interval_seconds: if interval.is_none() {
                10.0
            } else {
                interval.as_float().unwrap_or(10.0)
            },
        }
    }

    /// `EvaluateTarget(targetId)`.
    pub fn evaluate(&self, t: &mut TaskCtx<'_, '_>, target_id: i64) -> Option<f64> {
        match self {
            TargetEvaluator::Zero => Some(0.0),
            TargetEvaluator::Random => Some(t.world().random()),
            TargetEvaluator::Size => {
                let Some(item) = t.world().inventory_item(target_id) else {
                    t.raise(WorldError::Runtime(format!("no item {target_id}")).into());
                    return None;
                };
                Some(t.world().type_attribute(item.type_id, ATTRIBUTE_SIGNATURE_RADIUS))
            }
            TargetEvaluator::Threat { threat_sample_interval_seconds } => {
                Some(evaluate_threat(t, target_id, *threat_sample_interval_seconds))
            }
            TargetEvaluator::Cost => {
                let Some(slim) = t.world().slim_item(target_id) else {
                    t.raise(BehaviorError::Task(
                        "AttributeError: 'NoneType' object has no attribute 'typeID'".to_string(),
                    ));
                    return None;
                };
                Some(t.world().average_price(slim.type_id).filter(|p| *p != 0.0).unwrap_or(0.0))
            }
            TargetEvaluator::FittedModuleGroup { preferred_module_groups } => {
                let fitted = t.world().fitted_module_group_ids(target_id);
                Some(if preferred_module_groups.iter().any(|group| fitted.contains(group)) {
                    1.0
                } else {
                    0.0
                })
            }
            TargetEvaluator::Range { my_ball_id } => {
                let distance = t.world().distance_between(*my_ball_id, target_id);
                Some(1.0 - t.ok(distance)?)
            }
            TargetEvaluator::SignatureRadiusAndVelocity => {
                let signature_radius = t.world().attribute_value(target_id, ATTRIBUTE_SIGNATURE_RADIUS);
                let signature_radius = t.ok(signature_radius)?;
                let max_velocity = t.world().attribute_value(target_id, ATTRIBUTE_MAX_VELOCITY);
                let max_velocity = t.ok(max_velocity)?;
                Some(signature_radius / max_velocity.max(1.0))
            }
            TargetEvaluator::TowingTowObjective => {
                if t.world().slim_item(target_id).is_some() {
                    // the client calls `getattr(slimItem, SLIM_KEY_TOWING_ID, default=None)`, and `getattr` takes no keyword arguments
                    t.raise(BehaviorError::Task(
                        "TypeError: getattr() takes no keyword arguments".to_string(),
                    ));
                    return None;
                }
                Some(0.0)
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::env::DamageTotals;
    use crate::tree::tests::with_task;

    fn name(n: &str) -> Value {
        Value::Str(n.to_string())
    }

    #[test]
    fn the_evaluators_weigh_a_target_as_the_client_does() {
        let (rig, answer) = with_task(
            |rig| {
                rig.world.add_ball(1, [0.0; 3], 2);
                rig.world.add_ball(20, [0.0; 3], 600);
                rig.world.type_attributes.insert((600, ATTRIBUTE_SIGNATURE_RADIUS), 150.0);
                rig.world.prices.insert(600, 2500.0);
                rig.world.fitted_group_ids.insert(20, vec![55, 56]);
                rig.world.set_dist(1, 20, 0.25);
                rig.world.attributes.insert((20, ATTRIBUTE_SIGNATURE_RADIUS), 100.0);
                rig.world.attributes.insert((20, ATTRIBUTE_MAX_VELOCITY), 400.0);
                rig.world.damage.insert(20, DamageTotals { damage_to_npcs: 8.0, healing_done: 2.0 });
                rig.world.set_numbers(&[0.42]);
            },
            |t| {
                let mut out = Vec::new();
                for n in ["zero", "random", "size", "threat", "cost", "range", "sigRadius & velocity"] {
                    let evaluator = TargetEvaluator::create(t, &name(n)).unwrap();
                    out.push(evaluator.evaluate(t, 20).unwrap());
                }
                t.set_attribute("preferredModuleGroups", Value::List(vec![Value::Int(56)]));
                out.push(TargetEvaluator::create(t, &name("module")).unwrap().evaluate(t, 20).unwrap());
                out
            },
        );
        assert_eq!(rig.last_error, None);
        assert_eq!(answer, Some(vec![0.0, 0.42, 150.0, 10.0, 2500.0, 0.75, 0.25, 1.0]));
    }

    #[test]
    fn an_unknown_name_raises_and_the_towing_evaluator_raises_for_a_target_it_knows() {
        let (rig, _) = with_task(|_| {}, |t| TargetEvaluator::create(t, &name("nonsense")));
        assert!(matches!(rig.last_error, Some(BehaviorError::World(WorldError::Key(_)))));
        let (rig, _) = with_task(
            |rig| {
                rig.world.add_ball(20, [0.0; 3], 600);
            },
            |t| TargetEvaluator::create(t, &name("towing tow game objective")).unwrap().evaluate(t, 20),
        );
        assert!(matches!(rig.last_error, Some(BehaviorError::Task(ref m)) if m.contains("keyword")));
        let (_, answer) = with_task(
            |_| {},
            |t| TargetEvaluator::create(t, &name("towing tow game objective")).unwrap().evaluate(t, 21),
        );
        assert_eq!(answer, Some(Some(0.0)));
    }
}
