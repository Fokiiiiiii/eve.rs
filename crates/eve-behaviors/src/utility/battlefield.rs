//! `behaviors/utility/battlefield.py`: what the two sides of a battle are worth.

use crate::env::PlayerFleetReport;
use crate::tree::TaskCtx;
use crate::utility::entity_groups::get_entity_group_member_ids;
use crate::value::Value;

/// `get_npc_cost_by_group_id`: nothing for no group.
pub fn get_npc_cost_by_group_id(
    t: &mut TaskCtx<'_, '_>,
    entity_group_id: Option<i64>,
    spawn_table_id: Option<i64>,
) -> f64 {
    let Some(entity_group_id) = entity_group_id.filter(|g| *g != 0) else { return 0.0 };
    let member_ids = get_entity_group_member_ids(t, entity_group_id);
    t.world().npc_fleet_cost_for_item_ids(spawn_table_id, &member_ids)
}

/// `get_player_cost_for_item_ids`.
pub fn get_player_cost_for_item_ids(t: &mut TaskCtx<'_, '_>, item_ids: &[i64]) -> f64 {
    t.world().player_fleet_cost_for_item_ids(item_ids)
}

/// `generate_player_fleet_report(task, spawn_pool_id)`.
pub fn generate_player_fleet_report(
    t: &mut TaskCtx<'_, '_>,
    spawn_pool_id: &Value,
) -> Option<PlayerFleetReport> {
    let report = t.world().generate_player_fleet_report(spawn_pool_id);
    t.ok(report)
}

/// `get_total_fleet_points(task, spawn_pool_id, spawn_table_id)`: what the NPCs of the pool are worth.
pub fn get_total_fleet_points(
    t: &mut TaskCtx<'_, '_>,
    spawn_pool_id: &Value,
    spawn_table_id: &Value,
) -> Option<f64> {
    let points = t.world().total_npc_fleet_points(spawn_pool_id, spawn_table_id);
    t.ok(points)
}

/// What `get_match_result` builds: how the players' fleet of a type and the NPCs that answer it compare.
#[derive(Debug, Clone, PartialEq)]
pub struct MatchResult {
    pub fleet_type: Value,
    pub player_fleet_points: i64,
    pub npc_fleet_points: f64,
    pub item_ids: Vec<i64>,
}

impl MatchResult {
    /// The dictionary the client puts on the blackboard.
    pub fn to_value(&self) -> Value {
        Value::Dict(vec![
            (Value::Str("fleet_type".to_string()), self.fleet_type.clone()),
            (Value::Str("player_fleet_points".to_string()), Value::Int(self.player_fleet_points)),
            (Value::Str("npc_fleet_points".to_string()), Value::Float(self.npc_fleet_points)),
            (
                Value::Str("item_ids".to_string()),
                Value::List(self.item_ids.iter().map(|id| Value::Int(*id)).collect()),
            ),
        ])
    }
}

/// `get_match_result`: `entity_group_id_by_fleet_type` is a dictionary of the blackboard.
#[allow(clippy::too_many_arguments)]
pub fn get_match_result(
    t: &mut TaskCtx<'_, '_>,
    spawn_table_id: &Value,
    fleet_type: &Value,
    player_report: &PlayerFleetReport,
    spawn_point_multiplier: f64,
    support_points: f64,
    supported_fleet_points: f64,
    entity_group_id_by_fleet_type: &Value,
) -> Option<MatchResult> {
    let Some((_, fleet_report)) = player_report.fleet_by_type.iter().find(|(kind, _)| kind.py_eq(fleet_type))
    else {
        t.raise(crate::env::WorldError::Key(format!("{fleet_type:?}")).into());
        return None;
    };
    let player_fleet_cost = fleet_report.fleet_cost;
    let supported = t.world().supported_fleet_types().iter().any(|kind| kind.py_eq(fleet_type));
    let support_fleet_contribution = if supported_fleet_points > 0.0 && supported {
        (player_fleet_cost / supported_fleet_points * support_points) as i64
    } else {
        0
    };
    let player_fleet_points =
        ((fleet_report.fleet_cost + support_fleet_contribution as f64) * spawn_point_multiplier) as i64;
    let entity_group_id = entity_group_id_by_fleet_type.dict_get(fleet_type).and_then(Value::as_int);
    let npc_fleet_points = match entity_group_id {
        Some(group) if group != 0 => get_npc_cost_by_group_id(t, Some(group), spawn_table_id.as_int()),
        _ => 0.0,
    };
    if t.faulted() {
        return None;
    }
    Some(MatchResult {
        fleet_type: fleet_type.clone(),
        player_fleet_points,
        npc_fleet_points,
        item_ids: fleet_report.item_ids.clone(),
    })
}
