//! `behaviors/monitors/dungeons.py`.

use crate::events::message::{ON_DUNGEON_TIMER_EXPIRED, ON_DUNGEON_TRIGGER, ON_DUNGEON_TRIGGER_EVENT};
use crate::events::{EventKey, Subject};
use crate::monitors::Monitor;
use crate::tree::TaskCtx;
use crate::value::Value;

/// `BaseDungeonMonitorClass._get_item_id`: my dungeon spawn, when I have one.
fn dungeon_subject(t: &mut TaskCtx<'_, '_>) -> Option<Subject> {
    t.ctx().my_dungeon_spawn_id.map(Subject::DungeonSpawn)
}

/// Sets a flag and starts the tree over when the dungeon's timer expires.
#[derive(Debug, Default)]
pub struct MonitorDungeonTimerSetBooleanAndReset;

impl Monitor for MonitorDungeonTimerSetBooleanAndReset {
    fn type_name(&self) -> &'static str {
        "MonitorDungeonTimerSetBooleanAndReset"
    }

    fn subject(&self, t: &mut TaskCtx<'_, '_>) -> Option<Subject> {
        dungeon_subject(t)
    }

    fn message(&self, _t: &mut TaskCtx<'_, '_>) -> Option<EventKey> {
        Some(ON_DUNGEON_TIMER_EXPIRED.into())
    }

    fn can_process_message(&mut self, t: &mut TaskCtx<'_, '_>, args: &[Value]) -> bool {
        let Some(timer_id) = t.required("timerId") else { return false };
        args.first().is_some_and(|expired| timer_id.py_eq(expired))
    }

    fn process_message(&mut self, t: &mut TaskCtx<'_, '_>, args: &[Value]) {
        if self.can_process_message(t, args) {
            let Some(address) = t.required_address("messageAddress") else { return };
            t.send_blackboard_value(&address, Value::Bool(true));
            t.request_reset();
        }
    }
}

/// `BaseDungeonTriggerMonitorClass._can_process_message` (for triggers) and `BaseDungeonTriggerEventMonitorClass` (for trigger events).
fn can_process_trigger(t: &mut TaskCtx<'_, '_>, args: &[Value], events: bool) -> bool {
    let [trigger, spawn_id, scenario_id, _] = args else {
        t.raise(crate::tree::BehaviorError::Task(
            "ValueError: a dungeon trigger message has four arguments".to_string(),
        ));
        return false;
    };
    let (wanted, field) =
        if events { ("triggerEventTypeId", "eventTypeID") } else { ("triggerTypeId", "triggerTypeID") };
    let Some(wanted) = t.required(wanted) else { return false };
    if !trigger.field(field).is_some_and(|f| wanted.py_eq(f)) {
        return false;
    }
    if t.ctx().my_dungeon_spawn_id.map(Value::Int).is_none_or(|mine| !mine.py_eq(spawn_id)) {
        return false;
    }
    if t.has_attribute("checkRoomScenario") && t.attribute("checkRoomScenario").truthy() {
        return t
            .ctx()
            .my_dungeon_spawn_scenario_id
            .map(Value::Int)
            .is_some_and(|mine| mine.py_eq(scenario_id));
    }
    true
}

/// Takes the ship's invulnerability back when the dungeon's trigger is pulled.
#[derive(Debug, Default)]
pub struct DungeonCancelInvulnerableOnTrigger;

impl Monitor for DungeonCancelInvulnerableOnTrigger {
    fn type_name(&self) -> &'static str {
        "DungeonCancelInvulnerableOnTrigger"
    }

    fn subject(&self, t: &mut TaskCtx<'_, '_>) -> Option<Subject> {
        dungeon_subject(t)
    }

    fn message(&self, _t: &mut TaskCtx<'_, '_>) -> Option<EventKey> {
        Some(ON_DUNGEON_TRIGGER.into())
    }

    fn process_message(&mut self, t: &mut TaskCtx<'_, '_>, args: &[Value]) {
        if can_process_trigger(t, args, false) {
            let (Some(me), Some(reason)) = (t.my_item_id(), t.required_string("reason")) else { return };
            t.world().cancel_current_invulnerability(me, &reason);
        }
    }
}

/// Sets a flag when a trigger event of the dungeon comes.
#[derive(Debug, Default)]
pub struct DungeonSetBlackboardBooleanOnTriggerEvent;

impl Monitor for DungeonSetBlackboardBooleanOnTriggerEvent {
    fn type_name(&self) -> &'static str {
        "DungeonSetBlackboardBooleanOnTriggerEvent"
    }

    fn subject(&self, t: &mut TaskCtx<'_, '_>) -> Option<Subject> {
        dungeon_subject(t)
    }

    fn message(&self, _t: &mut TaskCtx<'_, '_>) -> Option<EventKey> {
        Some(ON_DUNGEON_TRIGGER_EVENT.into())
    }

    fn process_message(&mut self, t: &mut TaskCtx<'_, '_>, args: &[Value]) {
        if can_process_trigger(t, args, true) {
            let Some(address) = t.required_address("blackboardAddress") else { return };
            let value = t.attribute("trueOrFalse");
            t.send_blackboard_value(&address, value);
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::blackboards::{Address, ScopeType};
    use crate::monitors::MonitorTask;
    use crate::tree::tests::Rig;
    use crate::tree::{Attrs, Status};

    fn item(name: &str) -> Value {
        Address::new(ScopeType::Item, name).to_value()
    }

    fn tell(rig: &mut Rig, message: crate::events::MessageId, args: &[Value]) {
        let mut cx = crate::tree::Cx {
            core: &mut rig.tree.core,
            ctx: &mut rig.ctx,
            shared: &mut rig.shared,
            world: &mut rig.world,
        };
        cx.send_message_about(Subject::DungeonSpawn(77), message, args);
    }

    #[test]
    fn a_monitor_without_a_dungeon_spawn_fails() {
        let mut rig = Rig::new();
        assert_eq!(
            rig.run(Box::new(MonitorTask(MonitorDungeonTimerSetBooleanAndReset)), Attrs::new()),
            Status::Failure
        );
    }

    #[test]
    fn the_dungeon_timer_that_is_mine_sets_the_flag_and_starts_the_tree_over() {
        let mut rig = Rig::new();
        rig.ctx.my_dungeon_spawn_id = Some(77);
        rig.run(
            Box::new(MonitorTask(MonitorDungeonTimerSetBooleanAndReset)),
            Attrs::new().with("timerId", 4i64).with("messageAddress", item("Flag")),
        );
        let before = rig.ctx.statistics.reset_requests;
        tell(&mut rig, ON_DUNGEON_TIMER_EXPIRED, &[Value::Int(5)]);
        assert_eq!(rig.ctx.statistics.reset_requests, before);
        tell(&mut rig, ON_DUNGEON_TIMER_EXPIRED, &[Value::Int(4)]);
        assert_eq!(rig.ctx.statistics.reset_requests, before + 1);
        assert_eq!(
            rig.shared
                .blackboards
                .message_channel(crate::blackboards::item_scope(1), "Flag")
                .unwrap()
                .last_message_value(),
            Value::Bool(true)
        );
    }

    #[test]
    fn a_trigger_of_my_type_in_my_spawn_and_room_cancels_the_invulnerability() {
        let mut rig = Rig::new();
        rig.ctx.my_dungeon_spawn_id = Some(77);
        rig.ctx.my_dungeon_spawn_scenario_id = Some(3);
        rig.run(
            Box::new(MonitorTask(DungeonCancelInvulnerableOnTrigger)),
            Attrs::new()
                .with("triggerTypeId", 9i64)
                .with("reason", "dungeon")
                .with("checkRoomScenario", true),
        );
        let trigger = |type_id: i64| Value::record(&[("triggerTypeID", Value::Int(type_id))]);
        tell(&mut rig, ON_DUNGEON_TRIGGER, &[trigger(8), Value::Int(77), Value::Int(3), Value::None]);
        tell(&mut rig, ON_DUNGEON_TRIGGER, &[trigger(9), Value::Int(78), Value::Int(3), Value::None]);
        tell(&mut rig, ON_DUNGEON_TRIGGER, &[trigger(9), Value::Int(77), Value::Int(4), Value::None]);
        assert!(rig.world.take_calls().is_empty());
        tell(&mut rig, ON_DUNGEON_TRIGGER, &[trigger(9), Value::Int(77), Value::Int(3), Value::None]);
        assert_eq!(rig.world.take_calls(), ["cancel_current_invulnerability(1, dungeon)"]);
    }

    #[test]
    fn a_trigger_event_sets_the_flag_to_the_authored_value() {
        let mut rig = Rig::new();
        rig.ctx.my_dungeon_spawn_id = Some(77);
        rig.run(
            Box::new(MonitorTask(DungeonSetBlackboardBooleanOnTriggerEvent)),
            Attrs::new()
                .with("triggerEventTypeId", 2i64)
                .with("blackboardAddress", item("B"))
                .with("trueOrFalse", false),
        );
        tell(
            &mut rig,
            ON_DUNGEON_TRIGGER_EVENT,
            &[Value::record(&[("eventTypeID", Value::Int(2))]), Value::Int(77), Value::Int(0), Value::None],
        );
        assert_eq!(
            rig.shared
                .blackboards
                .message_channel(crate::blackboards::item_scope(1), "B")
                .unwrap()
                .last_message_value(),
            Value::Bool(false)
        );
    }
}
