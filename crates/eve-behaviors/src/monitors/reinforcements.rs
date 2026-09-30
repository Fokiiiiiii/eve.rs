//! `behaviors/monitors/reinforcements.py`.

use crate::blackboards::MessageData;
use crate::constants::blackboardchannels::{
    reinforcement_cooldown_address, reinforcements_authorized_address,
};
use crate::env::WorldError;
use crate::tree::{BehaviorError, Status, Task, TaskCtx};

const HANDLER_CHANGED: u16 = 0;

/// Succeeds while reinforcements are authorized; when they are not, fails and starts the tree over when they become so.
#[derive(Debug, Default)]
pub struct AreReinforcementsAuthorizedMonitor;

impl Task for AreReinforcementsAuthorizedMonitor {
    fn type_name(&self) -> &'static str {
        "AreReinforcementsAuthorizedMonitor"
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        let Some(group_id) = t.ctx().my_entity_group_id else {
            t.raise(WorldError::Key("myEntityGroupId".to_string()).into());
            return;
        };
        let unrecoverable = || {
            BehaviorError::Unrecoverable(
                "Unable to find reinforced entity group.  All the members are likely dead.".to_string(),
            )
        };
        match t.world().is_reinforcement_authorized(group_id) {
            Ok(true) => t.set_status(Status::Success),
            Ok(false) => {
                if t.subscribe_to_blackboard_unless_deleted(
                    &reinforcements_authorized_address(),
                    HANDLER_CHANGED,
                )
                .is_err()
                {
                    t.raise(unrecoverable());
                    return;
                }
                t.set_status(Status::Failure);
            }
            Err(WorldError::ReinforcedGroupNotFound) => t.raise(unrecoverable()),
            Err(other) => t.raise(other.into()),
        }
    }

    fn on_blackboard_message(
        &mut self,
        t: &mut TaskCtx<'_, '_>,
        _handler: u16,
        _channel: &str,
        message: Option<&MessageData>,
    ) {
        if t.is_invalid() {
            return;
        }
        if message.is_some_and(|m| m.value.truthy()) {
            t.request_reset();
        }
    }

    fn clean_up(&mut self, t: &mut TaskCtx<'_, '_>) {
        if !t.is_invalid() {
            t.unsubscribe_to_blackboard(&reinforcements_authorized_address(), HANDLER_CHANGED);
            t.set_status_to_invalid();
        }
    }
}

/// Succeeds while reinforcements are available; when they are not, fails and starts the tree over when the cooldown is done.
#[derive(Debug, Default)]
pub struct AreReinforcementAvailableMonitor;

impl Task for AreReinforcementAvailableMonitor {
    fn type_name(&self) -> &'static str {
        "AreReinforcementAvailableMonitor"
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        let Some(group_id) = t.ctx().my_entity_group_id else {
            t.raise(WorldError::Key("myEntityGroupId".to_string()).into());
            return;
        };
        if t.world().are_reinforcements_available(group_id) {
            t.set_status_to_success();
        } else {
            t.subscribe_to_blackboard(&reinforcement_cooldown_address(), HANDLER_CHANGED);
            t.set_status_to_failed();
        }
    }

    fn on_blackboard_message(
        &mut self,
        t: &mut TaskCtx<'_, '_>,
        _handler: u16,
        _channel: &str,
        message: Option<&MessageData>,
    ) {
        if t.is_invalid() {
            return;
        }
        let is_cooling_down = message.is_some_and(|m| !m.value.is_none());
        if is_cooling_down {
            return;
        }
        t.request_reset();
    }

    fn clean_up(&mut self, t: &mut TaskCtx<'_, '_>) {
        t.set_status_to_invalid();
        t.unsubscribe_to_blackboard(&reinforcement_cooldown_address(), HANDLER_CHANGED);
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::blackboards::ScopeType;
    use crate::tree::Attrs;
    use crate::tree::tests::Rig;
    use crate::value::Value;

    #[test]
    fn authorized_reinforcements_succeed_and_unauthorized_ones_fail_and_wait_for_the_change() {
        let mut rig = Rig::new();
        rig.world.reinforcement_authorized.insert(10, Ok(true));
        assert_eq!(rig.run(Box::new(AreReinforcementsAuthorizedMonitor), Attrs::new()), Status::Success);
        let mut rig = Rig::new();
        rig.world.reinforcement_authorized.insert(10, Ok(false));
        assert_eq!(rig.run(Box::new(AreReinforcementsAuthorizedMonitor), Attrs::new()), Status::Failure);
        let before = rig.ctx.statistics.reset_requests;
        rig.post(ScopeType::EntityGroup, "REINFORCEMENTS_AUTHORIZED", Value::Bool(false));
        assert_eq!(rig.ctx.statistics.reset_requests, before);
        rig.post(ScopeType::EntityGroup, "REINFORCEMENTS_AUTHORIZED", Value::Bool(true));
        assert_eq!(rig.ctx.statistics.reset_requests, before + 1);
    }

    #[test]
    fn a_group_with_no_reinforcements_is_an_unrecoverable_behavior() {
        let mut rig = Rig::new();
        rig.run(Box::new(AreReinforcementsAuthorizedMonitor), Attrs::new());
        assert!(matches!(rig.last_error, Some(BehaviorError::Unrecoverable(_))));
    }

    #[test]
    fn reinforcements_available_succeed_and_otherwise_the_end_of_the_cooldown_starts_the_tree_over() {
        let mut rig = Rig::new();
        rig.world.reinforcements_available.insert(10);
        assert_eq!(rig.run(Box::new(AreReinforcementAvailableMonitor), Attrs::new()), Status::Success);
        let mut rig = Rig::new();
        assert_eq!(rig.run(Box::new(AreReinforcementAvailableMonitor), Attrs::new()), Status::Failure);
        let before = rig.ctx.statistics.reset_requests;
        rig.post(ScopeType::EntityGroup, "REINFORCEMENT_COOLDOWN", Value::Int(3));
        assert_eq!(rig.ctx.statistics.reset_requests, before);
        rig.post(ScopeType::EntityGroup, "REINFORCEMENT_COOLDOWN", Value::None);
        assert_eq!(rig.ctx.statistics.reset_requests, before + 1);
    }
}
