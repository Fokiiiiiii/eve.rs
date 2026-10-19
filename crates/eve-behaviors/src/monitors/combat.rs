//! `behaviors/monitors/combat.py`.

use std::collections::BTreeSet;

use crate::consts::{CATEGORY_DRONE, CATEGORY_FIGHTER, is_player_owner};
use crate::env::WorldError;
use crate::events::message::{
    CRIMEWATCH_EVENT_STARTED, INDIRECT_OFFENSE_EVENT, MAX_LOCKED_TARGETS_CHANGED, ON_CLOAK_BALL,
    SEC_STATUS_LOST_EVENT,
};
use crate::groups::mixin::get_member_bubble_set;
use crate::monitors::ballparks::item_subject;
use crate::tree::{BehaviorError, Task, TaskCtx};
use crate::utility::ballparks::{get_ball_position, get_my_ball};
use crate::utility::blackboards::{SetAdd, add_to_set_on_channel};
use crate::utility::owner::get_owner_id;
use crate::value::Value;

const HANDLER_CRIMEWATCH: u16 = 0;
const HANDLER_INDIRECT_OFFENSE: u16 = 1;

/// `GetAggressorId` of `crimewatch.util`: a drone or a fighter is its pilot's ship.
fn get_aggressor_id(
    t: &mut TaskCtx<'_, '_>,
    category_id: Option<i64>,
    source_owner_id: i64,
    aggressor_id: i64,
) -> i64 {
    if category_id.is_some_and(|c| c == CATEGORY_DRONE || c == CATEGORY_FIGHTER)
        && let Some(ship_id) = t.world().ship_id_for_char_id(source_owner_id).filter(|s| *s != 0)
    {
        return ship_id;
    }
    aggressor_id
}

/// What a crimewatch event tells (`effectKey`, `effectInfo`).
struct Crimewatch {
    ship_id: i64,
    is_assistance: bool,
    is_offensive: bool,
    module_type_id: Option<i64>,
    source_owner_id: i64,
    target_type_id: Option<i64>,
}

fn read_crimewatch(t: &mut TaskCtx<'_, '_>, args: &[Value]) -> Option<Crimewatch> {
    let (Some(key), Some(info)) = (args.first(), args.get(1)) else {
        t.raise(BehaviorError::Task(
            "TypeError: a crimewatch event takes an effect key and an effect info".to_string(),
        ));
        return None;
    };
    let field = |record: &Value, name: &str| record.field(name).cloned().unwrap_or(Value::None);
    Some(Crimewatch {
        ship_id: field(key, "shipID").as_int().unwrap_or(0),
        is_assistance: field(info, "isAssistance").truthy(),
        is_offensive: field(info, "isOffensive").truthy(),
        module_type_id: field(info, "moduleTypeID").as_int(),
        source_owner_id: field(info, "sourceOwnerID").as_int().unwrap_or(0),
        target_type_id: field(info, "targetTypeID").as_int(),
    })
}

/// Which of the monitors built on `WaitForAggressiveAct` a task is.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum Aggression {
    /// `WaitForAggressiveAct`: an aggressor is put on the combat targets and the tree starts over.
    #[default]
    Wait,
    /// `SetFlagAndResetWhenAggressed`.
    SetFlagAndReset,
    /// `FlagAggressionAndReportMyPosition`.
    FlagAndReportPosition,
    /// `MonitorAssistanceToTargets`: those that help the targets are the aggressors.
    AssistanceToTargets,
    /// `MonitorAssistanceToGroupTargets`.
    AssistanceToGroupTargets,
    /// `MonitorAggressionTowardsTargets`.
    AggressionTowardsTargets,
    /// `MonitorAggressionTowardsGroupTargets`.
    AggressionTowardsGroupTargets,
}

/// The monitors that wait for someone to act aggressively and put them on the combat targets (`WaitForAggressiveAct` and the classes built on it).
#[derive(Debug, Default)]
pub struct WaitForAggressiveAct {
    pub kind: Aggression,
}

impl WaitForAggressiveAct {
    pub fn new(kind: Aggression) -> Self {
        Self { kind }
    }

    fn watches_targets(&self) -> bool {
        !matches!(
            self.kind,
            Aggression::Wait | Aggression::SetFlagAndReset | Aggression::FlagAndReportPosition
        )
    }

    fn is_group(&self) -> bool {
        matches!(self.kind, Aggression::AssistanceToGroupTargets | Aggression::AggressionTowardsGroupTargets)
    }

    fn is_aggression(&self) -> bool {
        matches!(self.kind, Aggression::AggressionTowardsTargets | Aggression::AggressionTowardsGroupTargets)
    }

    /// `AddAggressorAndResetTree`.
    fn add_aggressor_and_reset_tree(
        &mut self,
        t: &mut TaskCtx<'_, '_>,
        aggressor_id: i64,
        target_type_id: Option<i64>,
    ) {
        let Some(address) = t.required_address("combatTargetsAddress") else { return };
        match add_to_set_on_channel(t, &address, Value::Int(aggressor_id), true) {
            SetAdd::Added => {
                t.request_reset();
                self.log_aggression(t, aggressor_id, target_type_id);
                if self.kind == Aggression::FlagAndReportPosition {
                    let Some(me) = t.my_item_id() else { return };
                    let Some(position) = get_ball_position(t, me) else { return };
                    let (Some(flag), Some(my_position)) =
                        (t.required_address("flagAddress"), t.required_address("myPositionAddress"))
                    else {
                        return;
                    };
                    t.send_blackboard_value(&flag, Value::Bool(true));
                    t.send_blackboard_value(&my_position, Value::from_vec3(position));
                }
            }
            SetAdd::AlreadyThere | SetAdd::Deleted => {}
        }
    }

    /// `AddAggressorsAndResetTree`.
    fn add_aggressors_and_reset_tree(
        &mut self,
        t: &mut TaskCtx<'_, '_>,
        category_id: Option<i64>,
        source_owner_id: i64,
        aggressor_id: i64,
        target_type_id: Option<i64>,
    ) {
        if self.kind == Aggression::SetFlagAndReset {
            let Some(flag) = t.required_address("flagAddress") else { return };
            t.send_blackboard_value(&flag, Value::Bool(true));
            t.request_reset();
            return;
        }
        let aggressor_id = get_aggressor_id(t, category_id, source_owner_id, aggressor_id);
        self.add_aggressor_and_reset_tree(t, aggressor_id, target_type_id);
    }

    /// `_LogAggression`, inside an `ExceptionEater`.
    fn log_aggression(&self, t: &mut TaskCtx<'_, '_>, aggressor_id: i64, target_type_id: Option<i64>) {
        let towards = self.is_aggression();
        t.eat_exceptions(|t| {
            let group = t.ctx().my_entity_group_id;
            let group_behavior = group.and_then(|g| t.world().behavior_group_behavior_id(g));
            let owner = get_owner_id(t);
            let behavior_id = t.behavior_id();
            let group_behavior_id = group_behavior.unwrap_or_else(|| behavior_id.clone());
            if towards {
                t.world().log_monitored_target_aggressed(
                    owner,
                    &behavior_id,
                    &group_behavior_id,
                    target_type_id,
                    aggressor_id,
                );
            } else {
                t.world().log_behavior_aggressed(
                    owner,
                    &behavior_id,
                    &group_behavior_id,
                    target_type_id,
                    aggressor_id,
                );
            }
        });
    }

    /// `IsValidAggressor`.
    fn is_valid_aggressor(&self, t: &mut TaskCtx<'_, '_>, aggressor_id: i64) -> bool {
        if aggressor_id == 0 {
            return false;
        }
        let Some(ball) = t.world().ball(aggressor_id) else { return false };
        // `_PlayerAggressorFilter`
        if t.attribute("onlyPlayerAggressors").truthy() {
            let Some(slim) = t.world().slim_item(ball.id) else { return false };
            let Some(owner) = slim.owner_id else { return false };
            if !is_player_owner(owner) {
                return false;
            }
        }
        true
    }

    fn on_crimewatch_event_started(&mut self, t: &mut TaskCtx<'_, '_>, args: &[Value]) {
        if t.is_invalid() {
            return;
        }
        let Some(event) = read_crimewatch(t, args) else { return };
        match self.kind {
            Aggression::AssistanceToTargets | Aggression::AssistanceToGroupTargets => {
                if !event.is_assistance {
                    return;
                }
            }
            Aggression::AggressionTowardsTargets | Aggression::AggressionTowardsGroupTargets => {
                if !event.is_offensive {
                    return;
                }
            }
            _ => {
                if event.is_assistance {
                    return;
                }
            }
        }
        let category_id = event.module_type_id.and_then(|m| t.world().type_category_id(m));
        let valid = if self.watches_targets() { true } else { self.is_valid_aggressor(t, event.ship_id) };
        if valid {
            self.add_aggressors_and_reset_tree(
                t,
                category_id,
                event.source_owner_id,
                event.ship_id,
                event.target_type_id,
            );
        }
    }

    fn on_indirect_offense_event(&mut self, t: &mut TaskCtx<'_, '_>, args: &[Value]) {
        if t.is_invalid() {
            return;
        }
        let (Some(source_owner_id), Some(target_item)) = (args.first().and_then(Value::as_int), args.get(1))
        else {
            return;
        };
        let Some(offending_owner) = t.world().inventory_item(source_owner_id) else {
            t.raise(BehaviorError::Task(
                "UnboundLocalError: local variable 'offendingOwner' referenced before assignment".to_string(),
            ));
            return;
        };
        let offending_ship_id = offending_owner.location_id;
        let Some(offending_item) = t.world().inventory_item(offending_ship_id) else {
            t.raise(WorldError::Runtime(format!("no item {offending_ship_id}")).into());
            return;
        };
        let checks_aggressor = matches!(
            self.kind,
            Aggression::Wait
                | Aggression::SetFlagAndReset
                | Aggression::FlagAndReportPosition
                | Aggression::AssistanceToTargets
                | Aggression::AssistanceToGroupTargets
        );
        if !self.is_aggression() && checks_aggressor && !self.is_valid_aggressor(t, offending_ship_id) {
            return;
        }
        let category_id = t.world().type_category_id(offending_item.type_id);
        let target_type_id = target_item.field("typeID").and_then(Value::as_int);
        self.add_aggressors_and_reset_tree(
            t,
            category_id,
            source_owner_id,
            offending_ship_id,
            target_type_id,
        );
    }

    /// `SubscribeToTargetsInBubble(targetIds, bubbleId)`.
    fn subscribe_to_targets_in_bubble(
        &mut self,
        t: &mut TaskCtx<'_, '_>,
        target_ids: &Value,
        bubble_id: i64,
    ) {
        if !target_ids.truthy() {
            return;
        }
        let monitored = get_monitored_targets(t);
        let mut monitored = monitored.unwrap_or_default();
        for target in target_ids.items().unwrap_or(&[]).to_vec() {
            let Some(target_id) = target.as_int() else { continue };
            if let Some(ball) = t.world().ball(target_id)
                && ball.new_bubble_id == bubble_id
            {
                monitored.insert(target_id);
                t.subscribe_item(target_id, CRIMEWATCH_EVENT_STARTED, HANDLER_CRIMEWATCH);
                if self.is_aggression() {
                    t.subscribe_item(target_id, INDIRECT_OFFENSE_EVENT, HANDLER_INDIRECT_OFFENSE);
                }
            }
        }
        // the client keeps this set in a local: what it gathers is not stored (so the monitors never unsubscribe)
        let _ = monitored;
    }

    /// `UnsubscribeFromMonitoredTargets`.
    fn unsubscribe_from_monitored_targets(&mut self, t: &mut TaskCtx<'_, '_>) {
        let Some(monitored) = get_monitored_targets(t) else { return };
        if monitored.is_empty() {
            return;
        }
        for target_id in monitored {
            t.unsubscribe_item(target_id, CRIMEWATCH_EVENT_STARTED, HANDLER_CRIMEWATCH);
            if self.is_aggression()
                && let Some(me) = t.my_item_id()
            {
                t.unsubscribe_item(me, INDIRECT_OFFENSE_EVENT, HANDLER_INDIRECT_OFFENSE);
            }
        }
        t.set_context_value("monitoredTargetSet", Value::None);
    }
}

/// `GetMonitoredTargets`: a fresh empty set when the task has stored none; what was stored (`None` after it was cleared) otherwise.
fn get_monitored_targets(t: &mut TaskCtx<'_, '_>) -> Option<BTreeSet<i64>> {
    if !t.has_context_value("monitoredTargetSet") {
        return Some(BTreeSet::new());
    }
    t.get_context_value("monitoredTargetSet")
        .items()
        .map(|items| items.iter().filter_map(Value::as_int).collect())
}

impl Task for WaitForAggressiveAct {
    fn type_name(&self) -> &'static str {
        match self.kind {
            Aggression::Wait => "WaitForAggressiveAct",
            Aggression::SetFlagAndReset => "SetFlagAndResetWhenAggressed",
            Aggression::FlagAndReportPosition => "FlagAggressionAndReportMyPosition",
            Aggression::AssistanceToTargets => "MonitorAssistanceToTargets",
            Aggression::AssistanceToGroupTargets => "MonitorAssistanceToGroupTargets",
            Aggression::AggressionTowardsTargets => "MonitorAggressionTowardsTargets",
            Aggression::AggressionTowardsGroupTargets => "MonitorAggressionTowardsGroupTargets",
        }
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        if self.watches_targets() {
            let address =
                if self.is_aggression() { "monitoredTargetsAddress" } else { "combatTargetsAddress" };
            let Some(target_ids) = t.value_at(address) else { return };
            if self.is_group() {
                for bubble_id in get_member_bubble_set(t) {
                    self.subscribe_to_targets_in_bubble(t, &target_ids, bubble_id);
                }
            } else {
                let Some(mine) = get_my_ball(t) else { return };
                self.subscribe_to_targets_in_bubble(t, &target_ids, mine.new_bubble_id);
            }
            t.set_status_to_success();
            return;
        }
        let Some(me) = t.my_item_id() else { return };
        t.subscribe_item(me, CRIMEWATCH_EVENT_STARTED, HANDLER_CRIMEWATCH);
        t.subscribe_item(me, INDIRECT_OFFENSE_EVENT, HANDLER_INDIRECT_OFFENSE);
        t.set_status_to_success();
    }

    fn clean_up(&mut self, t: &mut TaskCtx<'_, '_>) {
        if self.watches_targets() {
            if !t.is_invalid() {
                self.unsubscribe_from_monitored_targets(t);
                t.set_status_to_invalid();
            }
            return;
        }
        t.set_status_to_invalid();
        if let Some(me) = t.my_item_id() {
            t.unsubscribe_item(me, CRIMEWATCH_EVENT_STARTED, HANDLER_CRIMEWATCH);
            t.unsubscribe_item(me, INDIRECT_OFFENSE_EVENT, HANDLER_INDIRECT_OFFENSE);
        }
    }

    fn on_event(&mut self, t: &mut TaskCtx<'_, '_>, handler: u16, args: &[Value]) {
        match handler {
            HANDLER_CRIMEWATCH => self.on_crimewatch_event_started(t, args),
            HANDLER_INDIRECT_OFFENSE => self.on_indirect_offense_event(t, args),
            _ => {}
        }
    }
}

/// Clears a value and starts the tree over when the target cloaks.
#[derive(Debug, Default)]
pub struct ClearValueAndResetIfTargetCloaks {
    target_id: Value,
}

impl Task for ClearValueAndResetIfTargetCloaks {
    fn type_name(&self) -> &'static str {
        "ClearValueAndResetIfTargetCloaks"
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        let Some(target) = t.value_at("targetAddress") else { return };
        self.target_id = target;
        if self.target_id.is_none() {
            t.set_status_to_failed();
        } else {
            t.subscribe(item_subject(&self.target_id), ON_CLOAK_BALL, 0);
            t.set_status_to_success();
        }
    }

    fn clean_up(&mut self, t: &mut TaskCtx<'_, '_>) {
        if !t.is_invalid() {
            t.unsubscribe(item_subject(&self.target_id), ON_CLOAK_BALL, 0);
            self.target_id = Value::None;
            t.set_status_to_invalid();
        }
    }

    fn on_event(&mut self, t: &mut TaskCtx<'_, '_>, _handler: u16, _args: &[Value]) {
        let Some(address) = t.required_address("valueAddress") else { return };
        t.send_blackboard_value(&address, Value::None);
        t.request_reset();
    }
}

/// Starts the tree over when the number of targets I can lock changes.
#[derive(Debug, Default)]
pub struct MaxLockedTargetsChangedMonitor;

impl Task for MaxLockedTargetsChangedMonitor {
    fn type_name(&self) -> &'static str {
        "MaxLockedTargetsChangedMonitor"
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        let Some(me) = t.my_item_id() else { return };
        t.subscribe_item(me, MAX_LOCKED_TARGETS_CHANGED, 0);
        t.set_status_to_success();
    }

    fn clean_up(&mut self, t: &mut TaskCtx<'_, '_>) {
        if !t.is_invalid()
            && let Some(me) = t.my_item_id()
        {
            t.unsubscribe_item(me, MAX_LOCKED_TARGETS_CHANGED, 0);
        }
        t.set_status_to_invalid();
    }

    fn on_event(&mut self, t: &mut TaskCtx<'_, '_>, _handler: u16, _args: &[Value]) {
        if !t.is_invalid() {
            t.request_reset();
        }
    }
}

/// Puts a ship that lost security status in my bubble on the combat targets (`MonitorSecurityStatusLoss`).
#[derive(Debug, Default)]
pub struct MonitorSecurityStatusLoss {
    solar_system_id: Option<i64>,
}

impl Task for MonitorSecurityStatusLoss {
    fn type_name(&self) -> &'static str {
        "MonitorSecurityStatusLoss"
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        if let Some(solar_system_id) = t.world().solar_system_id() {
            self.solar_system_id = Some(solar_system_id);
            t.subscribe_item(solar_system_id, SEC_STATUS_LOST_EVENT, 0);
        }
        t.set_status_to_success();
    }

    fn clean_up(&mut self, t: &mut TaskCtx<'_, '_>) {
        t.set_status_to_invalid();
        // the client unsubscribes from the crimewatch message here, not from the one it subscribed to: the subscription stays
        if let Some(solar_system_id) = self.solar_system_id.filter(|s| *s != 0) {
            t.unsubscribe_item(solar_system_id, CRIMEWATCH_EVENT_STARTED, 0);
        }
    }

    fn on_event(&mut self, t: &mut TaskCtx<'_, '_>, _handler: u16, args: &[Value]) {
        let Some(ship) = args.get(1) else { return };
        if t.is_invalid() || ship.is_none() {
            return;
        }
        let Some(ship_id) = ship.as_int() else { return };
        let Some(ball) = t.world().ball(ship_id) else { return };
        let Some(mine) = get_my_ball(t) else { return };
        if ball.new_bubble_id != mine.new_bubble_id {
            return;
        }
        let max_distance = t.attribute("maxDistance");
        if !max_distance.is_none()
            && let Some(me) = t.my_item_id()
            && let Ok(distance) = t.world().distance_between(me, ship_id)
            && Value::Float(distance).py_gt(&max_distance)
        {
            return;
        }
        let Some(address) = t.required_address("combatTargetsAddress") else { return };
        if add_to_set_on_channel(t, &address, Value::Int(ship_id), true) == SetAdd::Added {
            t.request_reset();
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::blackboards::{Address, ScopeType};
    use crate::env::InventoryItem;
    use crate::tree::tests::Rig;
    use crate::tree::{Attrs, Status};

    fn item(name: &str) -> Value {
        Address::new(ScopeType::Item, name).to_value()
    }

    fn tell(rig: &mut Rig, subject_item: i64, message: crate::events::MessageId, args: &[Value]) {
        let mut cx = crate::tree::Cx {
            core: &mut rig.tree.core,
            ctx: &mut rig.ctx,
            shared: &mut rig.shared,
            world: &mut rig.world,
        };
        cx.send_event(subject_item, message, args);
    }

    fn crimewatch(ship: i64, assistance: bool, offensive: bool) -> Vec<Value> {
        vec![
            Value::record(&[("shipID", Value::Int(ship))]),
            Value::record(&[
                ("isAssistance", Value::Bool(assistance)),
                ("isOffensive", Value::Bool(offensive)),
                ("moduleTypeID", Value::Int(3001)),
                ("sourceOwnerID", Value::Int(555)),
                ("targetTypeID", Value::Int(600)),
            ]),
        ]
    }

    fn combat_set(rig: &mut Rig) -> Value {
        rig.shared
            .blackboards
            .message_channel(crate::blackboards::item_scope(1), "Combat")
            .unwrap()
            .last_message_value()
    }

    #[test]
    fn an_aggressor_is_put_on_the_combat_targets_once_and_the_tree_starts_over() {
        let attrs = Attrs::new().with("combatTargetsAddress", item("Combat"));
        let mut rig = Rig::new();
        rig.world.add_ball(40, [0.0; 3], 1);
        rig.world.type_categories.insert(3001, 7);
        rig.run(Box::new(WaitForAggressiveAct::default()), attrs);
        let before = rig.ctx.statistics.reset_requests;
        tell(&mut rig, 1, CRIMEWATCH_EVENT_STARTED, &crimewatch(40, false, false));
        assert_eq!(combat_set(&mut rig), Value::Set(vec![Value::Int(40)]));
        assert_eq!(rig.ctx.statistics.reset_requests, before + 1);
        tell(&mut rig, 1, CRIMEWATCH_EVENT_STARTED, &crimewatch(40, false, false));
        assert_eq!(rig.ctx.statistics.reset_requests, before + 1, "already on the set");
        tell(&mut rig, 1, CRIMEWATCH_EVENT_STARTED, &crimewatch(41, true, false));
        assert_eq!(
            combat_set(&mut rig),
            Value::Set(vec![Value::Int(40)]),
            "help is not aggression, and 41 is no ball"
        );
        let calls = rig.world.take_calls();
        assert_eq!(calls.len(), 1);
        assert!(calls[0].starts_with("log_behavior_aggressed("), "{calls:?}");
    }

    #[test]
    fn a_drone_is_its_pilots_ship_and_only_player_aggressors_can_be_asked_for() {
        let attrs =
            Attrs::new().with("combatTargetsAddress", item("Combat")).with("onlyPlayerAggressors", true);
        let mut rig = Rig::new();
        rig.world.add_ball(40, [0.0; 3], 1);
        rig.world.slims.get_mut(&40).unwrap().owner_id = Some(98_000_001);
        rig.world.add_ball(50, [0.0; 3], 1);
        rig.world.slims.get_mut(&50).unwrap().owner_id = Some(1000006);
        rig.world.type_categories.insert(3001, CATEGORY_DRONE);
        rig.world.char_ships.insert(555, 77);
        rig.run(Box::new(WaitForAggressiveAct::default()), attrs);
        tell(&mut rig, 1, CRIMEWATCH_EVENT_STARTED, &crimewatch(50, false, false));
        assert_eq!(combat_set(&mut rig), Value::None, "an NPC aggressor is refused");
        tell(&mut rig, 1, CRIMEWATCH_EVENT_STARTED, &crimewatch(40, false, false));
        assert_eq!(combat_set(&mut rig), Value::Set(vec![Value::Int(77)]), "the drone's pilot's ship");
    }

    #[test]
    fn set_flag_and_reset_puts_the_flag_up_and_the_position_variant_reports_where_i_am() {
        let attrs = Attrs::new()
            .with("combatTargetsAddress", item("Combat"))
            .with("flagAddress", item("Flag"))
            .with("myPositionAddress", item("Pos"));
        let mut rig = Rig::new();
        rig.world.add_ball(40, [0.0; 3], 1);
        rig.world.add_ball(1, [5.0, 6.0, 7.0], 2);
        rig.run(Box::new(WaitForAggressiveAct::new(Aggression::SetFlagAndReset)), attrs.clone());
        tell(&mut rig, 1, CRIMEWATCH_EVENT_STARTED, &crimewatch(40, false, false));
        assert_eq!(
            rig.shared
                .blackboards
                .message_channel(crate::blackboards::item_scope(1), "Flag")
                .unwrap()
                .last_message_value(),
            Value::Bool(true)
        );
        assert_eq!(combat_set(&mut rig), Value::None);
        let mut rig = Rig::new();
        rig.world.add_ball(40, [0.0; 3], 1);
        rig.world.add_ball(1, [5.0, 6.0, 7.0], 2);
        rig.run(Box::new(WaitForAggressiveAct::new(Aggression::FlagAndReportPosition)), attrs);
        tell(&mut rig, 1, CRIMEWATCH_EVENT_STARTED, &crimewatch(40, false, false));
        assert_eq!(
            rig.shared
                .blackboards
                .message_channel(crate::blackboards::item_scope(1), "Pos")
                .unwrap()
                .last_message_value(),
            Value::from_vec3([5.0, 6.0, 7.0])
        );
    }

    #[test]
    fn an_indirect_offense_is_traced_to_the_offending_ship() {
        let attrs = Attrs::new().with("combatTargetsAddress", item("Combat"));
        let mut rig = Rig::new();
        rig.world.add_ball(40, [0.0; 3], 1);
        rig.world
            .items
            .insert(900, InventoryItem { item_id: 900, type_id: 1373, owner_id: 0, location_id: 40 });
        rig.world
            .items
            .insert(40, InventoryItem { item_id: 40, type_id: 601, owner_id: 900, location_id: 5 });
        rig.world.type_categories.insert(601, 6);
        rig.run(Box::new(WaitForAggressiveAct::default()), attrs.clone());
        tell(
            &mut rig,
            1,
            INDIRECT_OFFENSE_EVENT,
            &[Value::Int(900), Value::record(&[("typeID", Value::Int(600))])],
        );
        assert_eq!(combat_set(&mut rig), Value::Set(vec![Value::Int(40)]));

        tell(
            &mut rig,
            1,
            INDIRECT_OFFENSE_EVENT,
            &[Value::Int(901), Value::record(&[("typeID", Value::Int(600))])],
        );
        assert!(
            matches!(rig.tree.take_fault(), Some(BehaviorError::Task(ref m)) if m.contains("UnboundLocalError"))
        );
    }

    #[test]
    fn the_target_monitors_subscribe_to_targets_in_the_bubble_and_never_let_go_as_the_client_does() {
        let attrs = Attrs::new().with("combatTargetsAddress", item("Combat"));
        let mut rig = Rig::new();
        rig.world.add_ball(1, [0.0; 3], 2).new_bubble_id = 3;
        rig.world.add_ball(40, [0.0; 3], 1).new_bubble_id = 3;
        rig.world.add_ball(41, [0.0; 3], 1).new_bubble_id = 4;
        rig.post(ScopeType::Item, "Combat", Value::set([Value::Int(40), Value::Int(41)]));
        assert_eq!(
            rig.run(Box::new(WaitForAggressiveAct::new(Aggression::AssistanceToTargets)), attrs),
            Status::Success
        );
        assert_eq!(rig.shared.events.subscribers(40, CRIMEWATCH_EVENT_STARTED).len(), 1);
        assert!(rig.shared.events.subscribers(41, CRIMEWATCH_EVENT_STARTED).is_empty());
        // the tree starts over: the task is cleaned up and entered again, and the subscription of the first time is still there
        rig.tree.request_reset(&mut rig.ctx, &mut rig.shared, &mut rig.world);
        rig.tick().unwrap();
        assert_eq!(rig.shared.events.subscribers(40, CRIMEWATCH_EVENT_STARTED).len(), 2);
    }

    #[test]
    fn a_target_that_cloaks_clears_the_value_and_a_ship_that_lost_security_status_in_my_bubble_is_a_target() {
        let mut rig = Rig::new();
        rig.post(ScopeType::Item, "T", Value::Int(40));
        rig.post(ScopeType::Item, "V", Value::Int(9));
        assert_eq!(
            rig.run(
                Box::new(ClearValueAndResetIfTargetCloaks::default()),
                Attrs::new().with("targetAddress", item("T")).with("valueAddress", item("V"))
            ),
            Status::Success
        );
        tell(&mut rig, 40, ON_CLOAK_BALL, &[Value::Int(40)]);
        assert_eq!(
            rig.shared
                .blackboards
                .message_channel(crate::blackboards::item_scope(1), "V")
                .unwrap()
                .last_message_value(),
            Value::None
        );

        let attrs = Attrs::new().with("combatTargetsAddress", item("Combat")).with("maxDistance", 1000.0);
        let mut rig = Rig::new();
        rig.world.solar_system = Some(30000142);
        rig.world.add_ball(1, [0.0; 3], 2).new_bubble_id = 3;
        rig.world.add_ball(40, [0.0; 3], 1).new_bubble_id = 3;
        rig.world.add_ball(41, [0.0; 3], 1).new_bubble_id = 3;
        rig.world.set_dist(1, 40, 500.0);
        rig.world.set_dist(1, 41, 5000.0);
        rig.run(Box::new(MonitorSecurityStatusLoss::default()), attrs);
        tell(&mut rig, 30000142, SEC_STATUS_LOST_EVENT, &[Value::Int(1), Value::Int(41)]);
        assert_eq!(combat_set(&mut rig), Value::None, "too far");
        tell(&mut rig, 30000142, SEC_STATUS_LOST_EVENT, &[Value::Int(1), Value::Int(40)]);
        assert_eq!(combat_set(&mut rig), Value::Set(vec![Value::Int(40)]));
    }

    #[test]
    fn a_change_of_the_locked_targets_starts_the_tree_over() {
        let mut rig = Rig::new();
        rig.run(Box::new(MaxLockedTargetsChangedMonitor), Attrs::new());
        let before = rig.ctx.statistics.reset_requests;
        tell(&mut rig, 1, MAX_LOCKED_TARGETS_CHANGED, &[]);
        assert_eq!(rig.ctx.statistics.reset_requests, before + 1);
    }
}
