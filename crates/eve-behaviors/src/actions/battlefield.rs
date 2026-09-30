//! `behaviors/actions/battlefield.py`: how strong the NPCs of a structure's battle should be against the players' fleets.

use crate::consts::CATEGORY_STRUCTURE;
use crate::env::PlayerFleetReport;
use crate::evemath::weighted_choice;
use crate::tree::{BehaviorError, Task, TaskCtx};
use crate::utility::ballparks::get_my_ball;
use crate::utility::battlefield::{generate_player_fleet_report, get_match_result, get_total_fleet_points};
use crate::utility::context::get_extra_context;
use crate::utility::owner::get_owner_id;
use crate::utility::spawning::{add_spawn_points, get_current_spawn_points};
use crate::utility::threat::evaluate_target;
use crate::value::Value;

const THREAT_WINDOW_IN_SECONDS: f64 = 10.0;
const FULL_HEALTH: f64 = 1.0;

/// Puts a multiplier for the spawn points on the blackboard, by the health of the structure that is my entity; fails for what is no structure.
#[derive(Debug, Default)]
pub struct CalculateStructureSpawnPointMultiplier;

impl CalculateStructureSpawnPointMultiplier {
    /// `_get_structure_health`: the health of the layer that can be damaged now.
    fn structure_health(t: &mut TaskCtx<'_, '_>) -> Option<f64> {
        let ball = get_my_ball(t)?;
        let Some(state) = t.world().structure_state(ball.id) else {
            t.raise(BehaviorError::Task("AttributeError: the ball is no structure".to_string()));
            return None;
        };
        if !state.is_damaged {
            return Some(FULL_HEALTH);
        }
        if state.is_shield_vulnerable {
            return Some(state.shield);
        }
        if state.is_armor_vulnerable {
            return Some(state.armor);
        }
        if state.is_hull_vulnerable {
            return Some(state.hull);
        }
        Some(FULL_HEALTH)
    }

    /// `_get_multiplier_at`.
    fn multiplier_at(t: &mut TaskCtx<'_, '_>, health: f64) -> Option<f64> {
        let min_val = t.required_float("minSpawnPointMultiplier")?;
        let max_val = t.required_float("maxSpawnPointMultiplier")?;
        let peak_health_fraction = t.required_float("peakHealthFraction")?;
        let divisor = (FULL_HEALTH - peak_health_fraction).powf(2.0);
        if divisor == 0.0 {
            t.raise(BehaviorError::Task("ZeroDivisionError: float division by zero".to_string()));
            return None;
        }
        let multiplier = min_val + (max_val - min_val) * (FULL_HEALTH - health).powf(2.0) / divisor;
        Some(multiplier.min(max_val).max(0.0))
    }
}

impl Task for CalculateStructureSpawnPointMultiplier {
    fn type_name(&self) -> &'static str {
        "CalculateStructureSpawnPointMultiplier"
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        let Some(slim) = t.ctx().my_slim_item.clone() else {
            t.raise(BehaviorError::Task(
                "AttributeError: 'NoneType' object has no attribute 'categoryID'".to_string(),
            ));
            return;
        };
        if slim.category_id != CATEGORY_STRUCTURE {
            t.set_status_to_failed();
            return;
        }
        let Some(health) = Self::structure_health(t) else { return };
        let Some(multiplier) = Self::multiplier_at(t, health) else { return };
        let Some(address) = t.required_address("spawnPointMultiplierAddress") else { return };
        t.send_blackboard_value(&address, Value::Float(multiplier));
        t.set_status_to_success();
    }
}

enter_task!(
    /// Selects the type of fleet to counter among the ones that match the players', by how threatening they are.
    SelectFleetToCounter,
    |t| {
        let Some(results_address) = t.required_address("fleetMatchResultsAddress") else { return };
        let match_results = t.get_last_blackboard_value(&results_address);
        let Some(type_address) = t.required_address("fleetTypeToCounterAddress") else { return };
        let results = match_results.items().unwrap_or(&[]).to_vec();
        if match_results.truthy() && !results.is_empty() {
            let chosen = if results.len() > 1 {
                let mut weighted = Vec::new();
                for (at, result) in results.iter().enumerate() {
                    // `_get_match_result_weight`: at least 1
                    let mut threat = 0.0;
                    for item_id in result.dict_get(&Value::Str("item_ids".to_string())).map_or(Vec::new(), |ids| ids.int_items()) {
                        threat += evaluate_target(t, item_id, THREAT_WINDOW_IN_SECONDS);
                    }
                    weighted.push((at, threat.max(1.0)));
                }
                weighted_choice(&weighted, t.world()).map(|at| results[at].clone())
            } else {
                results.first().cloned()
            };
            let fleet_type = chosen.and_then(|result| result.dict_get(&Value::Str("fleet_type".to_string())).cloned());
            let Some(fleet_type) = fleet_type else {
                t.raise(crate::env::WorldError::Key("fleet_type".to_string()).into());
                return;
            };
            t.send_blackboard_value(&type_address, fleet_type);
        } else {
            t.send_blackboard_value(&type_address, Value::None);
        }
        t.set_status_to_success();
    }
);

const HANDLER_ANALYZE: u16 = 0;

/// Starts an analysis of the players' fleets against the NPCs of a spawn pool, to add spawn points and to say what to counter.
#[derive(Debug, Default)]
pub struct AnalyzeBattlefieldFleetMatching {
    is_analysing: bool,
}

impl AnalyzeBattlefieldFleetMatching {
    /// `get_entity_group_id_by_fleet_type_dict`: the group of each type of fleet that has one.
    fn entity_group_id_by_fleet_type(t: &mut TaskCtx<'_, '_>) -> Option<Value> {
        let addresses = t.required("entityGroupIdAddressByFleetType")?;
        let mut by_type = Value::Dict(Vec::new());
        for (fleet_type, address) in addresses.dict_entries() {
            if address.is_none() {
                continue;
            }
            let Some(address) = crate::blackboards::Address::from_value(address) else { continue };
            let entity_group_id = t.get_last_blackboard_value(&address);
            if entity_group_id.is_none() {
                continue;
            }
            by_type.dict_insert(fleet_type.clone(), entity_group_id);
        }
        Some(by_type)
    }

    /// `calculate_spawn_points_difference`.
    fn calculate_spawn_points_difference(
        t: &mut TaskCtx<'_, '_>,
        player_report: &PlayerFleetReport,
        spawn_point_multiplier: f64,
        spawn_pool_id: &Value,
        spawn_table_id: &Value,
    ) -> Option<f64> {
        let total_npc_fleet_cost = get_total_fleet_points(t, spawn_pool_id, spawn_table_id)?;
        let fleet_cost_difference =
            player_report.total_fleet_cost * spawn_point_multiplier - total_npc_fleet_cost;
        let current_spawn_points = get_current_spawn_points(t, spawn_pool_id.as_int());
        Some(fleet_cost_difference - current_spawn_points)
    }

    /// `assign_spawn_points`: what is asked for, within the limits.
    fn assign_spawn_points(
        t: &mut TaskCtx<'_, '_>,
        spawn_pool_id: &Value,
        requested: f64,
        min_spawn_points: &Value,
        max_spawn_points: &Value,
    ) {
        let (Some(min), Some(max)) = (min_spawn_points.as_float(), max_spawn_points.as_float()) else {
            t.raise(BehaviorError::Task("TypeError: the spawn point limits are no numbers".to_string()));
            return;
        };
        // `mathext.clamp`
        let actual = requested.max(min).min(max);
        add_spawn_points(t, spawn_pool_id.as_int(), actual);
        let owner_id = get_owner_id(t);
        let _ = get_extra_context(t.ctx(), None, None);
        t.world().log_adding_spawn_points(
            owner_id,
            spawn_pool_id,
            requested,
            min_spawn_points,
            max_spawn_points,
            actual,
        );
    }

    /// `match_fleets`.
    fn match_fleets(
        t: &mut TaskCtx<'_, '_>,
        entity_group_id_by_fleet_type: &Value,
        player_report: &PlayerFleetReport,
        spawn_point_multiplier: f64,
        spawn_table_id: &Value,
    ) -> Option<Vec<Value>> {
        let support = t.world().fleet_type_support();
        let Some((_, support_report)) =
            player_report.fleet_by_type.iter().find(|(kind, _)| kind.py_eq(&support))
        else {
            t.raise(crate::env::WorldError::Key(format!("{support:?}")).into());
            return None;
        };
        let support_points = support_report.fleet_cost;
        let supported_types = t.world().supported_fleet_types();
        let supported_fleet_points: f64 = player_report
            .fleet_by_type
            .iter()
            .filter(|(kind, _)| supported_types.iter().any(|supported| supported.py_eq(kind)))
            .map(|(_, report)| report.fleet_cost)
            .sum();
        let mut match_results = Vec::new();
        for fleet_type in t.world().fleet_types_to_counter() {
            let result = get_match_result(
                t,
                spawn_table_id,
                &fleet_type,
                player_report,
                spawn_point_multiplier,
                support_points,
                supported_fleet_points,
                entity_group_id_by_fleet_type,
            )?;
            if result.player_fleet_points > 0 {
                match_results.push(result.to_value());
            }
        }
        Some(match_results)
    }

    /// `_analyze_battlefield_fleet_matching`.
    fn analyze(
        t: &mut TaskCtx<'_, '_>,
        spawn_pool_id: &Value,
        spawn_table_id: &Value,
        spawn_point_multiplier: f64,
        entity_group_id_by_fleet_type: &Value,
        min_spawn_points: &Value,
        max_spawn_points: &Value,
    ) {
        let Some(player_report) = generate_player_fleet_report(t, spawn_pool_id) else { return };
        let Some(spawn_point_difference) = Self::calculate_spawn_points_difference(
            t,
            &player_report,
            spawn_point_multiplier,
            spawn_pool_id,
            spawn_table_id,
        ) else {
            return;
        };
        Self::assign_spawn_points(
            t,
            spawn_pool_id,
            spawn_point_difference,
            min_spawn_points,
            max_spawn_points,
        );
        if t.faulted() {
            return;
        }
        let Some(match_results) = Self::match_fleets(
            t,
            entity_group_id_by_fleet_type,
            &player_report,
            spawn_point_multiplier,
            spawn_table_id,
        ) else {
            return;
        };
        // `post_results`
        let (Some(report_address), Some(results_address)) =
            (t.required_address("playerFleetReportAddress"), t.required_address("fleetMatchResultsAddress"))
        else {
            return;
        };
        t.send_blackboard_value(&report_address, player_report.as_dict.clone());
        t.send_blackboard_value(&results_address, Value::List(match_results));
    }
}

impl Task for AnalyzeBattlefieldFleetMatching {
    fn type_name(&self) -> &'static str {
        "AnalyzeBattlefieldFleetMatching"
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        t.set_status_to_failed();
        let Some(by_type) = Self::entity_group_id_by_fleet_type(t) else { return };
        let (Some(pool_address), Some(table_address)) =
            (t.required_address("spawnPoolIdAddress"), t.required_address("spawnTableIdAddress"))
        else {
            return;
        };
        let spawn_pool_id = t.get_last_blackboard_value(&pool_address);
        let spawn_table_id = t.get_last_blackboard_value(&table_address);
        let (Some(min_spawn_points), Some(max_spawn_points)) =
            (t.required("minimumSpawnPoints"), t.required("maximumSpawnPoints"))
        else {
            return;
        };
        let Some(multiplier_address) = t.required_address("spawnPointMultiplierAddress") else { return };
        let spawn_point_multiplier = t.get_last_blackboard_value(&multiplier_address);
        if spawn_pool_id.truthy() && spawn_point_multiplier.truthy() {
            // `start_analysis`: a tasklet
            t.schedule_call(
                0.0,
                HANDLER_ANALYZE,
                vec![
                    spawn_pool_id,
                    spawn_table_id,
                    spawn_point_multiplier,
                    by_type,
                    min_spawn_points,
                    max_spawn_points,
                ],
            );
            t.set_status_to_success();
        }
    }

    fn on_call(&mut self, t: &mut TaskCtx<'_, '_>, handler: u16, args: &[Value]) {
        if handler != HANDLER_ANALYZE || args.len() < 6 {
            return;
        }
        // `analyze_battlefield_fleet_matching`: one analysis at a time, and what goes wrong in it is only logged
        if self.is_analysing {
            return;
        }
        self.is_analysing = true;
        let multiplier = args[2].as_float().unwrap_or(0.0);
        t.eat_exceptions(|t| Self::analyze(t, &args[0], &args[1], multiplier, &args[3], &args[4], &args[5]));
        self.is_analysing = false;
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::blackboards::{Address, ScopeType};
    use crate::env::testing::TestGroup;
    use crate::env::{FleetReport, SlimItem, StructureState};
    use crate::tree::tests::Rig;
    use crate::tree::{Attrs, Status};

    fn item(name: &str) -> Value {
        Address::new(ScopeType::Item, name).to_value()
    }

    fn structure_rig(state: StructureState) -> Rig {
        let mut rig = Rig::new();
        rig.ctx.my_slim_item =
            Some(SlimItem { item_id: 1, category_id: CATEGORY_STRUCTURE, ..Default::default() });
        rig.world.add_ball(1, [0.0; 3], 700);
        rig.world.structure_states.insert(1, state);
        rig
    }

    fn multiplier_attrs() -> Attrs {
        Attrs::new()
            .with("spawnPointMultiplierAddress", item("Multiplier"))
            .with("minSpawnPointMultiplier", 0.5)
            .with("maxSpawnPointMultiplier", 2.0)
            .with("peakHealthFraction", 0.2)
    }

    #[test]
    fn the_multiplier_grows_as_the_layer_that_can_be_hurt_is_hurt() {
        for (state, wanted) in [
            (StructureState::default(), 0.5),
            (
                StructureState {
                    is_damaged: true,
                    is_shield_vulnerable: true,
                    shield: 0.6,
                    armor: 0.1,
                    hull: 0.1,
                    ..Default::default()
                },
                0.5 + 1.5 * 0.16 / 0.64,
            ),
            (
                StructureState {
                    is_damaged: true,
                    is_armor_vulnerable: true,
                    shield: 0.0,
                    armor: 0.2,
                    ..Default::default()
                },
                2.0,
            ),
            (
                StructureState {
                    is_damaged: true,
                    is_hull_vulnerable: true,
                    hull: 0.0,
                    ..Default::default()
                },
                2.0,
            ),
            (StructureState { is_damaged: true, ..Default::default() }, 0.5),
        ] {
            let mut rig = structure_rig(state);
            assert_eq!(
                rig.run(Box::new(CalculateStructureSpawnPointMultiplier), multiplier_attrs()),
                Status::Success
            );
            let Value::Float(multiplier) = rig.last(ScopeType::Item, "Multiplier") else {
                panic!("no multiplier")
            };
            assert!((multiplier - wanted).abs() < 1e-9, "{state:?}: {multiplier} != {wanted}");
        }
    }

    #[test]
    fn the_multiplier_never_goes_below_0_and_what_is_no_structure_fails() {
        let mut rig = structure_rig(StructureState::default());
        rig.run(
            Box::new(CalculateStructureSpawnPointMultiplier),
            multiplier_attrs().with("minSpawnPointMultiplier", -3.0),
        );
        assert_eq!(rig.last(ScopeType::Item, "Multiplier"), Value::Float(0.0));
        let mut rig = structure_rig(StructureState::default());
        rig.ctx.my_slim_item = Some(SlimItem { item_id: 1, category_id: 6, ..Default::default() });
        assert_eq!(
            rig.run(Box::new(CalculateStructureSpawnPointMultiplier), multiplier_attrs()),
            Status::Failure
        );
    }

    fn result(fleet_type: &str, ids: &[i64]) -> Value {
        Value::Dict(vec![
            (Value::Str("fleet_type".into()), Value::Str(fleet_type.into())),
            (Value::Str("item_ids".into()), Value::List(ids.iter().map(|i| Value::Int(*i)).collect())),
        ])
    }

    #[test]
    fn a_fleet_to_counter_is_selected_by_threat_or_is_the_only_one_or_none() {
        let attrs = Attrs::new()
            .with("fleetMatchResultsAddress", item("Results"))
            .with("fleetTypeToCounterAddress", item("Counter"));
        let mut rig = Rig::new();
        assert_eq!(rig.run(Box::new(SelectFleetToCounter), attrs.clone()), Status::Success);
        assert_eq!(rig.last(ScopeType::Item, "Counter"), Value::None);
        rig.post(ScopeType::Item, "Results", Value::List(vec![result("tackle", &[5])]));
        rig.run(Box::new(SelectFleetToCounter), attrs.clone());
        assert_eq!(rig.last(ScopeType::Item, "Counter"), Value::Str("tackle".into()));
        // two: the one that did more damage is much more likely; the dice say 0.99
        rig.world.damage.insert(6, crate::env::DamageTotals { damage_to_npcs: 500.0, healing_done: 0.0 });
        rig.post(ScopeType::Item, "Results", Value::List(vec![result("tackle", &[5]), result("dps", &[6])]));
        rig.world.set_numbers(&[0.5]);
        rig.run(Box::new(SelectFleetToCounter), attrs);
        assert_eq!(rig.last(ScopeType::Item, "Counter"), Value::Str("dps".into()), "weights 1 and 500");
    }

    #[test]
    fn fleets_that_did_no_damage_weigh_1_each() {
        let attrs = Attrs::new()
            .with("fleetMatchResultsAddress", item("Results"))
            .with("fleetTypeToCounterAddress", item("Counter"));
        let mut rig = Rig::new();
        rig.post(ScopeType::Item, "Results", Value::List(vec![result("tackle", &[5]), result("dps", &[6])]));
        rig.world.set_numbers(&[0.9, 0.0]);
        rig.run(Box::new(SelectFleetToCounter), attrs);
        assert_eq!(rig.last(ScopeType::Item, "Counter"), Value::Str("dps".into()));
    }

    #[test]
    fn an_analysis_that_is_going_on_is_not_started_again() {
        let mut rig = analysis_rig();
        rig.run(Box::new(AnalyzeBattlefieldFleetMatching { is_analysing: true }), analysis_attrs());
        rig.deliver_scheduled();
        assert_eq!(rig.last(ScopeType::Item, "Report"), Value::None);
        assert_eq!(rig.world.spawn_points[&77], 10.0);
    }

    fn analysis_attrs() -> Attrs {
        Attrs::new()
            .with(
                "entityGroupIdAddressByFleetType",
                Value::Dict(vec![
                    (Value::Str("dps".into()), item("DpsGroup")),
                    (Value::Str("tackle".into()), Value::None),
                    (Value::Str("support".into()), item("MissingGroup")),
                ]),
            )
            .with("spawnPoolIdAddress", item("Pool"))
            .with("spawnTableIdAddress", item("Table"))
            .with("minimumSpawnPoints", -50i64)
            .with("maximumSpawnPoints", 200i64)
            .with("spawnPointMultiplierAddress", item("Multiplier"))
            .with("playerFleetReportAddress", item("Report"))
            .with("fleetMatchResultsAddress", item("Results"))
    }

    fn analysis_rig() -> Rig {
        let mut rig = Rig::new();
        rig.ctx.my_slim_item = Some(SlimItem { item_id: 1, owner_id: Some(7), ..Default::default() });
        rig.world.groups.insert(10, TestGroup { members: vec![1], owner_id: Some(7), ..Default::default() });
        rig.post(ScopeType::Item, "Pool", Value::Int(77));
        rig.post(ScopeType::Item, "Table", Value::Int(5));
        rig.post(ScopeType::Item, "Multiplier", Value::Float(1.5));
        rig.post(ScopeType::Item, "DpsGroup", Value::Int(31));
        rig.world.groups.insert(31, TestGroup { members: vec![40, 41], ..Default::default() });
        rig.world.spawn_points.insert(77, 10.0);
        rig.world.total_npc_points = 20.0;
        rig.world.player_fleet_report = Some(PlayerFleetReport {
            fleet_by_type: vec![
                (Value::Str("dps".into()), FleetReport { fleet_cost: 100.0, item_ids: vec![90, 91] }),
                (Value::Str("tackle".into()), FleetReport { fleet_cost: 0.0, item_ids: vec![] }),
                (Value::Str("support".into()), FleetReport { fleet_cost: 40.0, item_ids: vec![92] }),
            ],
            total_fleet_cost: 140.0,
            as_dict: Value::Str("the report".into()),
        });
        rig
    }

    #[test]
    fn an_analysis_starts_in_a_tasklet_adds_spawn_points_within_the_limits_and_posts_what_it_found() {
        let mut rig = analysis_rig();
        let mut task = Some(Box::new(AnalyzeBattlefieldFleetMatching::default()));
        assert_eq!(rig.run(task.take().unwrap(), analysis_attrs()), Status::Success);
        assert_eq!(rig.last(ScopeType::Item, "Report"), Value::None, "the tasklet has not run");
        rig.deliver_scheduled();
        assert_eq!(rig.last_error, None);
        // 140 * 1.5 - 20 = 190 less the 10 the pool has: 180, within -50 and 200
        assert_eq!(rig.world.spawn_points[&77], 190.0);
        assert!(
            rig.world
                .take_calls()
                .iter()
                .any(|c| c == "log_adding_spawn_points(Some(7), Int(77), 180, Int(-50), Int(200), 180)")
        );
        assert_eq!(rig.last(ScopeType::Item, "Report"), Value::Str("the report".into()));
        // dps: the supported ones are worth 100 and the support 40: (100 + int(100 / 100 * 40)) * 1.5 = 210; support: (40 + 0) * 1.5 = 60; tackle: 0 is left out
        let Value::List(results) = rig.last(ScopeType::Item, "Results") else { panic!("no results") };
        assert_eq!(results.len(), 2);
        assert_eq!(results[0].dict_get(&Value::Str("player_fleet_points".into())), Some(&Value::Int(210)));
        assert_eq!(
            results[0].dict_get(&Value::Str("item_ids".into())),
            Some(&Value::List(vec![Value::Int(90), Value::Int(91)]))
        );
        assert_eq!(results[1].dict_get(&Value::Str("player_fleet_points".into())), Some(&Value::Int(60)));
    }

    #[test]
    fn the_points_that_are_added_are_kept_within_the_limits() {
        let mut rig = analysis_rig();
        rig.world.total_npc_points = 1000.0;
        rig.run(Box::new(AnalyzeBattlefieldFleetMatching::default()), analysis_attrs());
        rig.deliver_scheduled();
        assert_eq!(rig.world.spawn_points[&77], -40.0, "-50 is the least that is added");
        let mut rig = analysis_rig();
        rig.world.total_npc_points = -1000.0;
        rig.run(Box::new(AnalyzeBattlefieldFleetMatching::default()), analysis_attrs());
        rig.deliver_scheduled();
        assert_eq!(rig.world.spawn_points[&77], 210.0, "200 is the most");
    }

    #[test]
    fn without_a_pool_or_a_multiplier_the_task_fails_and_an_analysis_that_goes_wrong_is_no_error() {
        let mut rig = analysis_rig();
        rig.post(ScopeType::Item, "Multiplier", Value::Float(0.0));
        assert_eq!(
            rig.run(Box::new(AnalyzeBattlefieldFleetMatching::default()), analysis_attrs()),
            Status::Failure
        );
        assert_eq!(rig.shared.scheduled_call_count(), 0);
        let mut rig = analysis_rig();
        rig.world.player_fleet_report = None;
        assert_eq!(
            rig.run(Box::new(AnalyzeBattlefieldFleetMatching::default()), analysis_attrs()),
            Status::Success
        );
        rig.deliver_scheduled();
        assert_eq!(rig.last_error, None);
        assert_eq!(rig.last(ScopeType::Item, "Report"), Value::None);
    }
}
