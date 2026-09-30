//! `behaviors/utility/battlefield.py`: what the two sides of a battle are worth.

use crate::tree::TaskCtx;
use crate::utility::entity_groups::get_entity_group_member_ids;

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
