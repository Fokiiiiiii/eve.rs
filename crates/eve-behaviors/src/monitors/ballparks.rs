//! `behaviors/monitors/ballparks.py`.

use std::collections::BTreeSet;

use crate::env::ProximitySensor;
use crate::events::message::{
    INVULNERABILITY_CANCELED, ON_BALLPARK_ITEM_DESTROY, ON_BUBBLE_PROXIMITY_ENTERED,
    ON_ITEM_REMOVED_FROM_PARK, ON_PROXIMITY,
};
use crate::events::{EventKey, Subject};
use crate::groups::mixin::get_member_bubble_set;
use crate::monitors::standings::{StandingsAddresses, classify_group_standings};
use crate::tree::{BehaviorError, Status, Task, TaskCtx};
use crate::utility::ballparks::{get_my_ball, is_ball_cloaked};
use crate::utility::blackboards::{add_to_set_on_channel, update_blackboard_set_if_changed};
use crate::utility::components::{
    get_proximity_sensor, get_proximity_sensors, get_proximity_sensors_for_group_members,
};
use crate::value::Value;

/// `PROXIMITY_EVENT`.
pub const PROXIMITY_EVENT: EventKey = EventKey::of(crate::events::message::DO_BUBBLE_PROXIMITY);

/// The subject of an id a task may not have (`SubscribeItem(None, ...)` subscribes to a message about nothing).
pub(crate) fn item_subject(value: &Value) -> Subject {
    Subject::Item(value.as_int().unwrap_or(i64::MIN))
}

/// The whole numbers of an authored list attribute (none for one that is not there).
pub(crate) fn int_list(t: &mut TaskCtx<'_, '_>, name: &str) -> Vec<i64> {
    t.attribute(name).items().unwrap_or(&[]).iter().filter_map(Value::as_int).collect()
}

const HANDLER_PROXIMITY: u16 = 0;

/// Starts the tree over when a ball enters or leaves my bubble (`BubbleChangedMonitor`); `TargetChangingBubbleMonitor` when it is the target that does,
/// entering or leaving as `resetOnEnter` says.
#[derive(Debug, Default)]
pub struct BubbleChangedMonitor {
    pub target_changing: bool,
    bubble_id: Option<Value>,
}

impl BubbleChangedMonitor {
    pub fn target_changing() -> Self {
        Self { target_changing: true, bubble_id: None }
    }

    fn get_bubble_id(t: &mut TaskCtx<'_, '_>) -> Option<Value> {
        if t.attribute("bubbleIdAddress").is_none() {
            get_my_ball(t).map(|ball| Value::Int(ball.new_bubble_id))
        } else {
            t.value_at("bubbleIdAddress")
        }
    }
}

impl Task for BubbleChangedMonitor {
    fn type_name(&self) -> &'static str {
        if self.target_changing { "TargetChangingBubbleMonitor" } else { "BubbleChangedMonitor" }
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        let Some(bubble_id) = Self::get_bubble_id(t) else { return };
        t.subscribe(Subject::Notify, PROXIMITY_EVENT, HANDLER_PROXIMITY);
        if let Some(id) = bubble_id.as_int() {
            t.world().set_bubble_events(id, true);
        }
        self.bubble_id = Some(bubble_id);
        t.set_status_to_success();
    }

    fn clean_up(&mut self, t: &mut TaskCtx<'_, '_>) {
        if !t.is_invalid() {
            t.unsubscribe(Subject::Notify, PROXIMITY_EVENT, HANDLER_PROXIMITY);
            if let Some(id) = self.bubble_id.take().and_then(|b| b.as_int()) {
                t.world().set_bubble_events(id, false);
            }
        }
        t.set_status_to_invalid();
    }

    fn on_event(&mut self, t: &mut TaskCtx<'_, '_>, _handler: u16, args: &[Value]) {
        // (solar system id, bubble id, ball id, is entering)
        if t.is_invalid() {
            return;
        }
        let (Some(bubble_id), Some(ball_id), Some(is_entering)) = (args.get(1), args.get(2), args.get(3))
        else {
            return;
        };
        let mine = self.bubble_id.clone().unwrap_or(Value::None);
        if self.target_changing {
            if !bubble_id.py_eq(&mine) {
                return;
            }
            let Some(reset_on_enter) = t.required("resetOnEnter") else { return };
            if !is_entering.py_eq(&reset_on_enter) {
                return;
            }
            let Some(target) = t.value_at("targetAddress") else { return };
            if !ball_id.py_eq(&target) {
                return;
            }
            t.request_reset();
        } else if bubble_id.py_eq(&mine) {
            t.request_reset();
        }
    }
}

/// Starts the tree over when a ball enters or leaves the bubble of any member of my group.
#[derive(Debug, Default)]
pub struct BallEnteredOrLeftBubbleGroupMonitor {
    bubble_id_set: BTreeSet<i64>,
}

impl Task for BallEnteredOrLeftBubbleGroupMonitor {
    fn type_name(&self) -> &'static str {
        "BallEnteredOrLeftBubbleGroupMonitor"
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        self.bubble_id_set = get_member_bubble_set(t);
        t.subscribe(Subject::Notify, PROXIMITY_EVENT, HANDLER_PROXIMITY);
        for bubble_id in &self.bubble_id_set {
            t.world().set_bubble_events(*bubble_id, true);
        }
        t.set_status_to_success();
    }

    fn clean_up(&mut self, t: &mut TaskCtx<'_, '_>) {
        if !t.is_invalid() {
            t.unsubscribe(Subject::Notify, PROXIMITY_EVENT, HANDLER_PROXIMITY);
            for bubble_id in &self.bubble_id_set {
                t.world().set_bubble_events(*bubble_id, false);
            }
        }
        t.set_status_to_invalid();
    }

    fn on_event(&mut self, t: &mut TaskCtx<'_, '_>, _handler: u16, args: &[Value]) {
        if t.is_invalid() {
            return;
        }
        if args.get(1).and_then(Value::as_int).is_some_and(|bubble| self.bubble_id_set.contains(&bubble)) {
            t.request_reset();
        }
    }
}

#[derive(Debug, Default)]
pub struct TargetWithOwnerEnteredBubbleMonitor {
    pub group: bool,
    bubble_id_set: Option<BTreeSet<i64>>,
}

impl TargetWithOwnerEnteredBubbleMonitor {
    pub fn for_group() -> Self {
        Self { group: true, bubble_id_set: None }
    }

    fn get_bubble_id_set(&self, t: &mut TaskCtx<'_, '_>) -> BTreeSet<i64> {
        if self.group {
            get_member_bubble_set(t)
        } else {
            get_my_ball(t).map(|ball| BTreeSet::from([ball.new_bubble_id])).unwrap_or_default()
        }
    }

    fn unsubscribe_from_bubble_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        if let Some(bubbles) = self.bubble_id_set.take() {
            for bubble_id in bubbles {
                t.unsubscribe(Subject::Bubble(bubble_id), ON_BUBBLE_PROXIMITY_ENTERED, HANDLER_PROXIMITY);
                t.world().set_bubble_events(bubble_id, false);
            }
        }
    }
}

impl Task for TargetWithOwnerEnteredBubbleMonitor {
    fn type_name(&self) -> &'static str {
        if self.group {
            "TargetsWithOwnerEnteredBubbleMonitor"
        } else {
            "TargetWithOwnerEnteredBubbleMonitor"
        }
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        self.bubble_id_set = None;
        let Some(owner_ids) = t.required("ownerIds") else { return };
        if owner_ids.truthy() {
            let mut bubbles = self.get_bubble_id_set(t);
            bubbles.extend(self.get_bubble_id_set(t));
            for bubble_id in &bubbles {
                t.subscribe(Subject::Bubble(*bubble_id), ON_BUBBLE_PROXIMITY_ENTERED, HANDLER_PROXIMITY);
                t.world().set_bubble_events(*bubble_id, true);
            }
            self.bubble_id_set = Some(bubbles);
        }
        t.set_status_to_success();
    }

    fn clean_up(&mut self, t: &mut TaskCtx<'_, '_>) {
        if !t.is_invalid() && t.attribute("ownerIds").truthy() {
            self.unsubscribe_from_bubble_enter(t);
        }
        t.set_status_to_invalid();
    }

    fn on_event(&mut self, t: &mut TaskCtx<'_, '_>, _handler: u16, args: &[Value]) {
        if t.is_invalid() {
            return;
        }
        let Some(ball_id) = args.first().and_then(Value::as_int) else { return };
        let slim = t.world().slim_item(ball_id);
        let owner_ids = t.attribute("ownerIds");
        if let Some(slim) = &slim
            && !slim.owner_id.is_some_and(|owner| owner_ids.contains(&Value::Int(owner)))
        {
            return;
        }

        if slim.is_none() {
            t.raise(BehaviorError::Task(
                "AttributeError: 'NoneType' object has no attribute 'ownerID'".to_string(),
            ));
            return;
        }
        if t.world().ball(ball_id).is_none() {
            t.raise(crate::env::WorldError::Key(ball_id.to_string()).into());
            return;
        }
        let Some(address) = t.required_address("targetSetAddress") else { return };
        add_to_set_on_channel(t, &address, Value::Int(ball_id), false);
    }
}

/// Starts the tree over when its target leaves the park.
#[derive(Debug, Default)]
pub struct TargetLeavingParkMonitor {
    target_id: Value,
}

impl Task for TargetLeavingParkMonitor {
    fn type_name(&self) -> &'static str {
        "TargetLeavingParkMonitor"
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        let Some(target) = t.value_at("targetAddress") else { return };
        self.target_id = target;
        t.subscribe(item_subject(&self.target_id), ON_ITEM_REMOVED_FROM_PARK, HANDLER_PROXIMITY);
        t.set_status(Status::Success);
    }

    fn clean_up(&mut self, t: &mut TaskCtx<'_, '_>) {
        if !t.is_invalid() {
            t.set_status_to_invalid();
            t.unsubscribe(item_subject(&self.target_id), ON_ITEM_REMOVED_FROM_PARK, HANDLER_PROXIMITY);
        }
    }

    fn on_event(&mut self, t: &mut TaskCtx<'_, '_>, _handler: u16, args: &[Value]) {
        let Some(target) = t.value_at("targetAddress") else { return };
        let Some(item_id) = args.first() else { return };
        if !item_id.py_eq(&target) {
            return;
        }
        if t.attribute("clearValue").truthy() {
            let Some(address) = t.required_address("targetAddress") else { return };
            t.send_blackboard_value(&address, Value::None);
        }
        t.request_reset();
    }
}

/// What kind of `MonitorProximitySensors` this is.
#[derive(Debug, Default)]
pub enum SensorKind {
    /// The sensors in my bubble.
    #[default]
    Base,
    /// The sensors of the members of my group, in the bubbles of the members.
    Group,
    /// The one sensor whose item is on a channel.
    Specific,
    /// The one sensor on a channel, in the bubbles of the members.
    SpecificGroup,
    /// The sensors of the members of my group, sorted by standing on blackboards (`MonitorProximitySensorForGroupStanding`).
    GroupStanding(Box<StandingsAddresses>),
}

const HANDLER_DETECTED_LATER: u16 = 1;

/// Tells what the proximity sensors see (`MonitorProximitySensors` and the classes built on it).
#[derive(Debug, Default)]
pub struct MonitorProximitySensors {
    pub kind: SensorKind,
    proximity_sensor_ids: BTreeSet<i64>,
    bubble_id_set: BTreeSet<i64>,
    tags: Vec<String>,
}

impl MonitorProximitySensors {
    pub fn new(kind: SensorKind) -> Self {
        Self { kind, ..Self::default() }
    }

    fn uses_group_bubbles(&self) -> bool {
        !matches!(self.kind, SensorKind::Base | SensorKind::Specific)
    }

    fn get_bubble_id_set(&self, t: &mut TaskCtx<'_, '_>) -> BTreeSet<i64> {
        if self.uses_group_bubbles() {
            get_member_bubble_set(t)
        } else {
            get_my_ball(t).map(|ball| BTreeSet::from([ball.new_bubble_id])).unwrap_or_default()
        }
    }

    fn get_proximity_sensors(&self, t: &mut TaskCtx<'_, '_>) -> Vec<ProximitySensor> {
        match self.kind {
            SensorKind::Base | SensorKind::Group => get_proximity_sensors(t, &self.tags),
            SensorKind::Specific | SensorKind::SpecificGroup => {
                let Some(id) = t.value_at("proximitySensorAddress") else { return Vec::new() };
                let has_ball = id.as_int().is_some_and(|id| t.world().has_ball(id));
                if id.truthy() && has_ball {
                    match id.as_int().and_then(|id| get_proximity_sensor(t, id)) {
                        Some(sensor) => vec![sensor],
                        None => Vec::new(),
                    }
                } else {
                    Vec::new()
                }
            }
            SensorKind::GroupStanding(_) => get_proximity_sensors_for_group_members(t),
        }
    }

    fn is_valid_target(&self, t: &mut TaskCtx<'_, '_>, item_id: i64) -> bool {
        let Some(slim) = t.world().slim_item(item_id) else { return false };
        let included = int_list(t, "includedCategories");
        if !included.is_empty() && !included.contains(&slim.category_id) {
            return false;
        }
        let excluded = int_list(t, "excludedGroups");
        if !excluded.is_empty() && excluded.contains(&slim.group_id) {
            return false;
        }
        let valid_owners = int_list(t, "validOwnerIds");
        if !valid_owners.is_empty() && !slim.owner_id.is_some_and(|o| valid_owners.contains(&o)) {
            return false;
        }
        let invalid_owners = int_list(t, "invalidOwnerIds");
        if !invalid_owners.is_empty() && slim.owner_id.is_some_and(|o| invalid_owners.contains(&o)) {
            return false;
        }
        !is_ball_cloaked(t, item_id)
    }

    fn classify_items(&mut self, t: &mut TaskCtx<'_, '_>, item_ids: &BTreeSet<i64>) {
        match &self.kind {
            SensorKind::GroupStanding(addresses) => classify_group_standings(t, addresses, item_ids),
            _ => {
                let Some(address) = t.required_address("objectListAddress") else { return };
                let ids: Vec<Value> = item_ids.iter().map(|id| Value::Int(*id)).collect();
                update_blackboard_set_if_changed(t, &ids, &address);
            }
        }
    }
}

impl Task for MonitorProximitySensors {
    fn type_name(&self) -> &'static str {
        match self.kind {
            SensorKind::Base => "MonitorProximitySensors",
            SensorKind::Group => "MonitorProximitySensorsForGroupMembers",
            SensorKind::Specific => "MonitorSpecificProximitySensor",
            SensorKind::SpecificGroup => "MonitorSpecificProximitySensorForGroupMembers",
            SensorKind::GroupStanding(_) => "MonitorProximitySensorForGroupStanding",
        }
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        let tags: Vec<String> = t
            .attribute("tags")
            .items()
            .unwrap_or(&[])
            .iter()
            .filter_map(|v| v.as_str().map(str::to_string))
            .collect();
        self.tags = t.world().behavior_sensor_tags(&tags);
        self.proximity_sensor_ids.clear();
        t.set_status_to_running();
        self.bubble_id_set = self.get_bubble_id_set(t);
        let sensors: Vec<ProximitySensor> = self
            .get_proximity_sensors(t)
            .into_iter()
            .filter(|s| self.bubble_id_set.contains(&s.bubble_id))
            .collect();
        let mut item_ids = BTreeSet::new();
        for sensor in &sensors {
            for item_id in &sensor.ball_ids_in_range {
                if self.is_valid_target(t, *item_id) {
                    item_ids.insert(*item_id);
                }
            }
        }
        self.classify_items(t, &item_ids);
        for sensor in &sensors {
            self.proximity_sensor_ids.insert(sensor.item_id);
            t.subscribe_item(sensor.item_id, ON_PROXIMITY, HANDLER_PROXIMITY);
        }
        t.set_status_to_success();
    }

    fn clean_up(&mut self, t: &mut TaskCtx<'_, '_>) {
        if !t.is_invalid() {
            for item_id in std::mem::take(&mut self.proximity_sensor_ids) {
                t.unsubscribe_item(item_id, ON_PROXIMITY, HANDLER_PROXIMITY);
            }
        }
        t.set_status_to_invalid();
    }

    fn on_event(&mut self, t: &mut TaskCtx<'_, '_>, _handler: u16, args: &[Value]) {
        // OnProximityDetected(ballId, isEntering): the client starts a tasklet for it
        t.schedule_call(0.0, HANDLER_DETECTED_LATER, args.to_vec());
    }

    fn on_call(&mut self, t: &mut TaskCtx<'_, '_>, handler: u16, args: &[Value]) {
        if handler != HANDLER_DETECTED_LATER || t.is_invalid() {
            return;
        }
        let (Some(ball_id), Some(is_entering)) = (args.first().and_then(Value::as_int), args.get(1)) else {
            return;
        };
        if is_entering.truthy() && self.is_valid_target(t, ball_id) {
            self.classify_items(t, &BTreeSet::from([ball_id]));
        }
    }
}

/// Starts the tree over when an invulnerability is canceled in my bubble (`MonitorInvulnerabilityCanceledInBubble`) or in a bubble of my group
/// (`MonitorInvulnerabilityCanceledInGroupBubbles`).
#[derive(Debug, Default)]
pub struct MonitorInvulnerabilityCanceledInBubble {
    pub group: bool,
    bubble_id_set: Option<BTreeSet<i64>>,
}

impl MonitorInvulnerabilityCanceledInBubble {
    pub fn for_group() -> Self {
        Self { group: true, bubble_id_set: None }
    }

    fn is_valid_target(t: &mut TaskCtx<'_, '_>, item_id: i64) -> bool {
        if t.attribute("targetIdListAddress").is_none() {
            return true;
        }
        let Some(list) = t.value_at("targetIdListAddress") else { return false };
        list.truthy() && list.contains(&Value::Int(item_id))
    }
}

impl Task for MonitorInvulnerabilityCanceledInBubble {
    fn type_name(&self) -> &'static str {
        if self.group {
            "MonitorInvulnerabilityCanceledInGroupBubbles"
        } else {
            "MonitorInvulnerabilityCanceledInBubble"
        }
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        let bubbles = if self.group {
            get_member_bubble_set(t)
        } else {
            get_my_ball(t).map(|ball| BTreeSet::from([ball.new_bubble_id])).unwrap_or_default()
        };
        for bubble_id in &bubbles {
            t.subscribe(Subject::Bubble(*bubble_id), INVULNERABILITY_CANCELED, HANDLER_PROXIMITY);
        }
        self.bubble_id_set = Some(bubbles);
        t.set_status_to_success();
    }

    fn clean_up(&mut self, t: &mut TaskCtx<'_, '_>) {
        if !t.is_invalid()
            && let Some(bubbles) = self.bubble_id_set.take()
        {
            for bubble_id in bubbles {
                t.unsubscribe(Subject::Bubble(bubble_id), INVULNERABILITY_CANCELED, HANDLER_PROXIMITY);
            }
        }
        t.set_status_to_invalid();
    }

    fn on_event(&mut self, t: &mut TaskCtx<'_, '_>, _handler: u16, args: &[Value]) {
        if t.is_invalid() {
            return;
        }
        let Some(item_id) = args.first().and_then(Value::as_int) else { return };
        if !Self::is_valid_target(t, item_id) {
            return;
        }
        t.request_reset();
    }
}

/// Clears a channel and starts the tree over when the item on another channel explodes.
#[derive(Debug, Default)]
pub struct ClearBlackboardChannelOnItemExplosion {
    item_id: Option<i64>,
}

impl Task for ClearBlackboardChannelOnItemExplosion {
    fn type_name(&self) -> &'static str {
        "ClearBlackboardChannelOnItemExplosion"
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        t.set_status_to_success();
        let Some(item) = t.value_at("itemIdAddress") else { return };
        match item.as_int().filter(|id| t.world().has_ball(*id)) {
            Some(item_id) => {
                self.item_id = Some(item_id);
                t.subscribe_item(item_id, ON_BALLPARK_ITEM_DESTROY, HANDLER_PROXIMITY);
            }
            None => self.item_id = None,
        }
    }

    fn on_event(&mut self, t: &mut TaskCtx<'_, '_>, _handler: u16, args: &[Value]) {
        if args.first().and_then(Value::as_int) == self.item_id && self.item_id.is_some() {
            let Some(address) = t.required_address("blackBoardChannelAddress") else { return };
            t.send_blackboard_value(&address, Value::None);
            t.request_reset();
        }
    }

    fn clean_up(&mut self, t: &mut TaskCtx<'_, '_>) {
        if !t.is_invalid()
            && let Some(item_id) = self.item_id
        {
            t.unsubscribe_item(item_id, ON_BALLPARK_ITEM_DESTROY, HANDLER_PROXIMITY);
        }
        t.set_status_to_invalid();
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::blackboards::{Address, ScopeType};
    use crate::env::testing::TestGroup;
    use crate::events::message::DO_BUBBLE_PROXIMITY;
    use crate::tree::Attrs;
    use crate::tree::tests::Rig;

    fn item(name: &str) -> Value {
        Address::new(ScopeType::Item, name).to_value()
    }

    fn notify(rig: &mut Rig, args: &[Value]) {
        let mut cx = crate::tree::Cx {
            core: &mut rig.tree.core,
            ctx: &mut rig.ctx,
            shared: &mut rig.shared,
            world: &mut rig.world,
        };
        cx.send_message_about(Subject::Notify, DO_BUBBLE_PROXIMITY, args);
    }

    fn tell(rig: &mut Rig, subject: Subject, message: crate::events::MessageId, args: &[Value]) {
        let mut cx = crate::tree::Cx {
            core: &mut rig.tree.core,
            ctx: &mut rig.ctx,
            shared: &mut rig.shared,
            world: &mut rig.world,
        };
        cx.send_message_about(subject, message, args);
    }

    fn proximity(bubble: i64, ball: i64, entering: bool) -> Vec<Value> {
        vec![Value::Int(30000142), Value::Int(bubble), Value::Int(ball), Value::Bool(entering)]
    }

    fn rig_in_bubble(bubble: i64) -> Rig {
        let mut rig = Rig::new();
        rig.world.add_ball(1, [0.0; 3], 2).new_bubble_id = bubble;
        rig
    }

    #[test]
    fn a_bubble_monitor_turns_the_bubbles_events_on_and_off_and_a_ball_in_the_bubble_starts_the_tree_over() {
        let mut rig = rig_in_bubble(3);
        rig.run(Box::new(BubbleChangedMonitor::default()), Attrs::new());
        assert_eq!(rig.world.take_calls(), ["set_bubble_events(3, true)"]);
        let before = rig.ctx.statistics.reset_requests;
        notify(&mut rig, &proximity(4, 50, true));
        assert_eq!(rig.ctx.statistics.reset_requests, before);
        notify(&mut rig, &proximity(3, 50, true));
        assert_eq!(rig.ctx.statistics.reset_requests, before + 1);
        rig.tree.release(&mut rig.ctx, &mut rig.shared, &mut rig.world);
        assert_eq!(rig.world.take_calls(), ["set_bubble_events(3, false)"]);
    }

    #[test]
    fn a_bubble_monitor_of_a_target_only_hears_the_target_entering_or_leaving_as_asked() {
        let attrs = Attrs::new().with("resetOnEnter", true).with("targetAddress", item("T"));
        let mut rig = rig_in_bubble(3);
        rig.post(ScopeType::Item, "T", Value::Int(50));
        rig.run(Box::new(BubbleChangedMonitor::target_changing()), attrs);
        let before = rig.ctx.statistics.reset_requests;
        notify(&mut rig, &proximity(3, 51, true));
        notify(&mut rig, &proximity(3, 50, false));
        assert_eq!(rig.ctx.statistics.reset_requests, before);
        notify(&mut rig, &proximity(3, 50, true));
        assert_eq!(rig.ctx.statistics.reset_requests, before + 1);
    }

    #[test]
    fn the_group_bubble_monitor_watches_the_bubbles_of_the_members() {
        let mut rig = rig_in_bubble(3);
        rig.ctx.my_entity_group_id = Some(10);
        rig.world.groups.insert(10, TestGroup { members: vec![1, 2], ..Default::default() });
        rig.world.add_ball(2, [0.0; 3], 2).new_bubble_id = 5;
        rig.run(Box::new(BallEnteredOrLeftBubbleGroupMonitor::default()), Attrs::new());
        assert_eq!(rig.world.take_calls(), ["set_bubble_events(3, true)", "set_bubble_events(5, true)"]);
        let before = rig.ctx.statistics.reset_requests;
        notify(&mut rig, &proximity(9, 50, true));
        notify(&mut rig, &proximity(5, 50, true));
        assert_eq!(rig.ctx.statistics.reset_requests, before + 1);
    }

    #[test]
    fn a_ball_of_a_wanted_owner_that_enters_the_bubble_is_put_on_the_target_set() {
        let attrs = Attrs::new()
            .with("ownerIds", Value::List(vec![Value::Int(98_000_001)]))
            .with("targetSetAddress", item("Targets"));
        let mut rig = rig_in_bubble(3);
        rig.world.add_ball(50, [0.0; 3], 1).new_bubble_id = 3;
        rig.world.slims.get_mut(&50).unwrap().owner_id = Some(98_000_001);
        rig.world.add_ball(51, [0.0; 3], 1).new_bubble_id = 3;
        rig.world.slims.get_mut(&51).unwrap().owner_id = Some(98_000_002);
        rig.run(Box::new(TargetWithOwnerEnteredBubbleMonitor::default()), attrs);
        tell(&mut rig, Subject::Bubble(3), ON_BUBBLE_PROXIMITY_ENTERED, &[Value::Int(51)]);
        tell(&mut rig, Subject::Bubble(3), ON_BUBBLE_PROXIMITY_ENTERED, &[Value::Int(50)]);
        tell(&mut rig, Subject::Bubble(3), ON_BUBBLE_PROXIMITY_ENTERED, &[Value::Int(50)]);
        let targets =
            rig.shared.blackboards.message_channel(crate::blackboards::item_scope(1), "Targets").unwrap();
        assert_eq!(targets.last_message_value(), Value::Set(vec![Value::Int(50)]));
        assert_eq!(targets.messages().len(), 1);
        rig.tree.release(&mut rig.ctx, &mut rig.shared, &mut rig.world);
        assert!(rig.world.take_calls().contains(&"set_bubble_events(3, false)".to_string()));
    }

    #[test]
    fn a_target_that_leaves_the_park_clears_the_value_when_asked_and_starts_the_tree_over() {
        let attrs = Attrs::new().with("targetAddress", item("T")).with("clearValue", true);
        let mut rig = Rig::new();
        rig.post(ScopeType::Item, "T", Value::Int(50));
        rig.run(Box::new(TargetLeavingParkMonitor::default()), attrs);
        let before = rig.ctx.statistics.reset_requests;
        tell(&mut rig, Subject::Item(50), ON_ITEM_REMOVED_FROM_PARK, &[Value::Int(51)]);
        assert_eq!(rig.ctx.statistics.reset_requests, before);
        tell(&mut rig, Subject::Item(50), ON_ITEM_REMOVED_FROM_PARK, &[Value::Int(50)]);
        assert_eq!(rig.ctx.statistics.reset_requests, before + 1);
        assert_eq!(rig.last(ScopeType::Item, "T"), Value::None);
    }

    #[test]
    fn the_proximity_sensors_in_my_bubble_tell_what_is_in_range_and_what_comes_in_later() {
        let attrs = Attrs::new().with("objectListAddress", item("Objects"));
        let mut rig = rig_in_bubble(3);
        rig.world.add_ball(30, [0.0; 3], 1);
        rig.world.add_ball(31, [0.0; 3], 1);
        rig.world.add_ball(32, [0.0; 3], 1);
        rig.world.cloaked.insert(31);
        rig.world
            .sensors
            .push((ProximitySensor { item_id: 5, bubble_id: 3, ball_ids_in_range: vec![30, 31] }, vec![]));
        rig.world
            .sensors
            .push((ProximitySensor { item_id: 6, bubble_id: 4, ball_ids_in_range: vec![32] }, vec![]));
        rig.run(Box::new(MonitorProximitySensors::default()), attrs);
        assert_eq!(rig.last_error, None);
        assert_eq!(rig.last(ScopeType::Item, "Objects"), Value::Set(vec![Value::Int(30)]));
        assert_eq!(rig.shared.events.subscribers(5, ON_PROXIMITY).len(), 1);
        assert!(rig.shared.events.subscribers(6, ON_PROXIMITY).is_empty());
        // a ball comes into the sensor's range: the client starts a tasklet, which runs at the next call time
        tell(&mut rig, Subject::Item(5), ON_PROXIMITY, &[Value::Int(32), Value::Bool(true)]);
        rig.shared.fire_due_calls();
        for delivery in std::mem::take(&mut rig.shared.pending) {
            rig.tree.deliver(&mut rig.ctx, &mut rig.shared, &mut rig.world, &delivery).unwrap();
        }
        assert_eq!(rig.last(ScopeType::Item, "Objects"), Value::Set(vec![Value::Int(30), Value::Int(32)]));
        // one that leaves is not looked at
        tell(&mut rig, Subject::Item(5), ON_PROXIMITY, &[Value::Int(33), Value::Bool(false)]);
        rig.shared.fire_due_calls();
        assert_eq!(rig.shared.pending.len(), 1);
    }

    #[test]
    fn a_target_must_be_of_the_authored_kinds() {
        let attrs = Attrs::new()
            .with("objectListAddress", item("Objects"))
            .with("includedCategories", Value::List(vec![Value::Int(6)]))
            .with("excludedGroups", Value::List(vec![Value::Int(25)]))
            .with("invalidOwnerIds", Value::List(vec![Value::Int(7)]));
        let mut rig = rig_in_bubble(3);
        for (id, category, group, owner) in [(30, 6, 1, 8), (31, 5, 1, 8), (32, 6, 25, 8), (33, 6, 1, 7)] {
            rig.world.add_ball(id, [0.0; 3], 1);
            let slim = rig.world.slims.get_mut(&id).unwrap();
            slim.category_id = category;
            slim.group_id = group;
            slim.owner_id = Some(owner);
        }
        rig.world.sensors.push((
            ProximitySensor { item_id: 5, bubble_id: 3, ball_ids_in_range: vec![30, 31, 32, 33] },
            vec![],
        ));
        rig.run(Box::new(MonitorProximitySensors::default()), attrs);
        assert_eq!(rig.last(ScopeType::Item, "Objects"), Value::Set(vec![Value::Int(30)]));
    }

    #[test]
    fn the_specific_sensor_is_the_one_whose_item_is_on_a_channel() {
        let attrs =
            Attrs::new().with("objectListAddress", item("Objects")).with("proximitySensorAddress", item("S"));
        let mut rig = rig_in_bubble(3);
        rig.world.add_ball(30, [0.0; 3], 1);
        rig.world.add_ball(5, [0.0; 3], 1);
        rig.world
            .sensors
            .push((ProximitySensor { item_id: 5, bubble_id: 3, ball_ids_in_range: vec![30] }, vec![]));
        rig.post(ScopeType::Item, "S", Value::Int(5));
        rig.run(Box::new(MonitorProximitySensors::new(SensorKind::Specific)), attrs.clone());
        assert_eq!(rig.last(ScopeType::Item, "Objects"), Value::Set(vec![Value::Int(30)]));
        let mut rig = rig_in_bubble(3);
        rig.post(ScopeType::Item, "S", Value::Int(5));
        rig.run(Box::new(MonitorProximitySensors::new(SensorKind::Specific)), attrs);
        assert_eq!(rig.last(ScopeType::Item, "Objects"), Value::None, "the sensor's ball is not in the park");
    }

    #[test]
    fn a_canceled_invulnerability_of_a_listed_ship_in_my_bubble_starts_the_tree_over() {
        let attrs = Attrs::new().with("targetIdListAddress", item("List"));
        let mut rig = rig_in_bubble(3);
        rig.post(ScopeType::Item, "List", Value::set([Value::Int(50)]));
        rig.run(Box::new(MonitorInvulnerabilityCanceledInBubble::default()), attrs);
        let before = rig.ctx.statistics.reset_requests;
        tell(
            &mut rig,
            Subject::Bubble(3),
            INVULNERABILITY_CANCELED,
            &[Value::Int(51), Value::Bool(true), Value::None],
        );
        assert_eq!(rig.ctx.statistics.reset_requests, before);
        tell(
            &mut rig,
            Subject::Bubble(3),
            INVULNERABILITY_CANCELED,
            &[Value::Int(50), Value::Bool(true), Value::None],
        );
        assert_eq!(rig.ctx.statistics.reset_requests, before + 1);
        // with no list every ship counts
        let mut rig = rig_in_bubble(3);
        rig.run(Box::new(MonitorInvulnerabilityCanceledInBubble::default()), Attrs::new());
        let before = rig.ctx.statistics.reset_requests;
        tell(
            &mut rig,
            Subject::Bubble(3),
            INVULNERABILITY_CANCELED,
            &[Value::Int(51), Value::Bool(true), Value::None],
        );
        assert_eq!(rig.ctx.statistics.reset_requests, before + 1);
    }

    #[test]
    fn a_channel_is_cleared_when_the_item_on_another_explodes() {
        let attrs =
            Attrs::new().with("itemIdAddress", item("Item")).with("blackBoardChannelAddress", item("Chan"));
        let mut rig = Rig::new();
        rig.world.add_ball(50, [0.0; 3], 1);
        rig.post(ScopeType::Item, "Item", Value::Int(50));
        rig.post(ScopeType::Item, "Chan", Value::Int(9));
        rig.run(Box::new(ClearBlackboardChannelOnItemExplosion::default()), attrs);
        let before = rig.ctx.statistics.reset_requests;
        tell(&mut rig, Subject::Item(50), ON_BALLPARK_ITEM_DESTROY, &[Value::Int(51)]);
        assert_eq!(rig.last(ScopeType::Item, "Chan"), Value::Int(9));
        tell(&mut rig, Subject::Item(50), ON_BALLPARK_ITEM_DESTROY, &[Value::Int(50)]);
        assert_eq!(rig.last(ScopeType::Item, "Chan"), Value::None);
        assert_eq!(rig.ctx.statistics.reset_requests, before + 1);
    }
}
