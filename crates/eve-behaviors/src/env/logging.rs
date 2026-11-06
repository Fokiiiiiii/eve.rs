//! `context.eventLogger`: what the tasks write down for whoever studies the NPCs.

/// The event logger.
pub trait Logging {
    /// `logger.error(...)` of a task that finds something authored wrong.
    fn log_error(&mut self, _message: &str) {}

    fn log_behavior_aggressed(
        &mut self,
        _owner_id: Option<i64>,
        _behavior_id: &crate::value::Value,
        _group_behavior_id: &crate::value::Value,
        _target_type_id: Option<i64>,
        _aggressor_id: i64,
    ) {
    }

    /// `eventLogger.log_monitored_target_aggressed(...)`.
    fn log_monitored_target_aggressed(
        &mut self,
        _owner_id: Option<i64>,
        _behavior_id: &crate::value::Value,
        _group_behavior_id: &crate::value::Value,
        _target_type_id: Option<i64>,
        _aggressor_id: i64,
    ) {
    }

    fn log_behavior_task(
        &mut self,
        _event: &crate::value::Value,
        _owner_id: Option<i64>,
        _behavior_id: &crate::value::Value,
        _type_id: i64,
    ) {
    }

    fn log_behavior_group_destroyed(
        &mut self,
        _owner_id: Option<i64>,
        _behavior_id: &crate::value::Value,
        _combat_targets: &crate::value::Value,
    ) {
    }

    fn log_killed_and_fled_players(
        &mut self,
        _owner_id: Option<i64>,
        _behavior_id: &crate::value::Value,
        _target_set: &crate::value::Value,
        _members: &[i64],
    ) {
    }

    fn log_adding_spawn_points(
        &mut self,
        _owner_id: Option<i64>,
        _spawn_pool_id: &crate::value::Value,
        _requested: f64,
        _min: &crate::value::Value,
        _max: &crate::value::Value,
        _actual: f64,
    ) {
    }
}
