//! `context.eventLogger`: what the tasks write down for whoever studies the NPCs.

/// The event logger.
pub trait Logging {
    /// `logger.error(...)` of a task that finds something authored wrong.
    fn log_error(&mut self, _message: &str) {}

    /// `eventLogger.log_behavior_aggressed(ownerId, behaviorId, groupBehaviorId, targetTypeId, aggressorId)`.
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
}
