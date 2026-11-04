//! `behaviors/utility/awareness.py`: what a group of NPCs is aware of, and the configuration of that awareness.

use crate::tree::TaskCtx;
use crate::utility::blackboards::{ResponseThresholds, get_response_thresholds};
use crate::utility::context::{ExtraContext, get_extra_context};
use crate::utility::owner::get_owner_id;
use crate::value::Value;

/// `get_spawn_pool_id`: the pool on the channel of the task's `spawnPoolIdAddress`.
pub fn get_spawn_pool_id(t: &mut TaskCtx<'_, '_>) -> Option<Value> {
    t.value_at("spawnPoolIdAddress")
}

/// `get_fleet_awareness_members_ids`.
pub fn get_fleet_awareness_members_ids(t: &mut TaskCtx<'_, '_>, spawn_pool_id: &Value) -> Vec<i64> {
    let pool = spawn_pool_id.as_int();
    t.world().fleet_awareness_member_ids(pool)
}

/// What `GroupAwarenessConfig` holds: how a group of NPCs watches for threats.
#[derive(Debug, Clone, Default, PartialEq)]
pub struct GroupAwarenessConfig {
    pub member_collection_id: Value,
    pub threat_range: Value,
    pub threat_timeout_seconds: Value,
    pub included_categories: Option<Value>,
    pub excluded_groups: Option<Value>,
    pub max_sensor_drift: Value,
    pub update_time_seconds: Value,
    pub standing_thresholds: Option<ResponseThresholds>,
    pub combat_targets_set_address: Value,
    pub owner_id: Option<i64>,
    pub context: ExtraContext,
    pub tracked_type_list_id: Option<Value>,
    /// What the tasks that register the awareness set on it.
    pub standings_enabled: Value,
    pub react_to_disapproval: Value,
    pub fw_attack_method: Value,
}

/// `get_awareness_config(task)`, given what the task's `get_member_collection_id()` answers and what its `get_spawn_pool_id()` and
/// `get_spawn_table_id()` do.
pub fn get_awareness_config(
    t: &mut TaskCtx<'_, '_>,
    member_collection_id: Value,
    spawn_pool_id: Option<i64>,
    spawn_table_id: Option<i64>,
) -> Option<GroupAwarenessConfig> {
    let mut config = GroupAwarenessConfig { member_collection_id, ..Default::default() };
    config.threat_range = t.required("threatRange")?;
    config.threat_timeout_seconds = t.required("threatTimeoutSeconds")?;
    if t.has_attribute("includedCategories") {
        config.included_categories = Some(t.attribute("includedCategories"));
    }
    if t.has_attribute("excludedGroups") {
        config.excluded_groups = Some(t.attribute("excludedGroups"));
    }
    config.max_sensor_drift = t.required("maxSensorDrift")?;
    config.update_time_seconds = t.required("updateTimeSeconds")?;
    config.standing_thresholds = get_response_thresholds(t);
    config.combat_targets_set_address = t.required("combatTargetsSetAddress")?;
    config.owner_id = get_owner_id(t);
    config.context = get_extra_context(t.ctx(), spawn_pool_id, spawn_table_id);
    if t.has_attribute("trackedTypeListID") {
        config.tracked_type_list_id = Some(t.attribute("trackedTypeListID"));
    }
    if t.faulted() { None } else { Some(config) }
}
