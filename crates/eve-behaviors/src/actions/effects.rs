//! `behaviors/actions/effects.py`: graphic effects, dogma attributes and effects that are started and stopped on the entity.

use crate::consts::ATTRIBUTE_RATE_OF_FIRE;
use crate::env::WorldError;
use crate::events::message;
use crate::tree::{BehaviorError, Task, TaskCtx};
use crate::utility::ballparks::{get_my_ball, get_ship_balls_in_bubble, get_slim_item};
use crate::utility::dogmatic::{get_default_effect_id_for_type, try_stop_effect, type_has_effect};
use crate::value::Value;

const TARGET_ATTACK_GRAPHIC_EFFECT: &str = "effects.Laser";
const NEBULA_PATH: &str = "res:/dx9/scene/abyssal/ad01_a_cube.dds";

fn attribute_error_on_item(t: &mut TaskCtx<'_, '_>, what: &str) {
    t.raise(BehaviorError::Task(format!("AttributeError: 'NoneType' object has no attribute '{what}'")));
}

/// `context.ballpark.inventory2.GetItem(myItemId)`.
fn get_self_as_item(t: &mut TaskCtx<'_, '_>) -> Option<crate::env::InventoryItem> {
    let me = t.my_item_id()?;
    t.world().inventory_item(me)
}

/// `getattr(self.attributes, 'useEntityAsModule', False)`: the entity is the module, when it is asked to be.
fn use_entity_as_module(t: &mut TaskCtx<'_, '_>) -> bool {
    t.attribute("useEntityAsModule").truthy()
}

/// The id a value of a blackboard is (`None` for none).
fn id_of(value: &Value) -> Option<i64> {
    value.as_int()
}

/// Plays a stretch effect once from a source to a target.
#[derive(Debug, Default)]
pub struct PlayOneShotStretchEffectAction;

impl PlayOneShotStretchEffectAction {
    /// `_GetEffectSourceId`.
    fn effect_source_id(t: &mut TaskCtx<'_, '_>) -> Option<Value> {
        if t.has_attribute("effectSourceAddress") {
            let address = t.required_address("effectSourceAddress")?;
            return Some(t.get_last_blackboard_value(&address));
        }
        Some(t.ctx().my_item_id.map_or(Value::None, Value::Int))
    }

    /// `_GetEffectDuration`.
    fn effect_duration(t: &mut TaskCtx<'_, '_>) -> Option<Value> {
        if t.has_attribute("effectDurationAddress") {
            let address = t.required_address("effectDurationAddress")?;
            return Some(t.get_last_blackboard_value(&address));
        }
        t.required("effectDuration")
    }
}

impl Task for PlayOneShotStretchEffectAction {
    fn type_name(&self) -> &'static str {
        "PlayOneShotStretchEffectAction"
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        let Some(source) = Self::effect_source_id(t) else { return };
        let Some(target_address) = t.required_address("effectTargetAddress") else { return };
        let target = t.get_last_blackboard_value(&target_address);
        let (Some(source_id), Some(target_id)) = (id_of(&source), id_of(&target)) else {
            t.set_status_to_failed();
            return;
        };
        let module_id = if use_entity_as_module(t) { Some(source_id) } else { None };
        let (Some(name), Some(duration)) = (t.required("effectName"), Self::effect_duration(t)) else {
            return;
        };
        t.world().start_stretch_effect(
            source_id,
            target_id,
            name.as_str().unwrap_or(""),
            &duration,
            &Value::Int(1),
            module_id,
            None,
        );
        t.set_status_to_success();
    }
}

/// Plays a ship effect once on the target.
#[derive(Debug, Default)]
pub struct PlayOneShotTargetedShipEffectAction;

impl Task for PlayOneShotTargetedShipEffectAction {
    fn type_name(&self) -> &'static str {
        "PlayOneShotTargetedShipEffectAction"
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        let Some(target_address) = t.required_address("effectTargetAddress") else { return };
        let target = t.get_last_blackboard_value(&target_address);
        let Some(target_id) = id_of(&target) else {
            t.set_status_to_failed();
            return;
        };
        let (Some(name), Some(duration)) = (t.required("effectName"), t.required("effectDuration")) else {
            return;
        };
        t.world().start_ship_effect(target_id, name.as_str().unwrap_or(""), &duration, 1, None);
        t.set_status_to_success();
    }
}

/// Plays a ship effect once on my entity, or on the one on the blackboard.
#[derive(Debug, Default)]
pub struct PlayOneShotShipEffectAction;

impl Task for PlayOneShotShipEffectAction {
    fn type_name(&self) -> &'static str {
        "PlayOneShotShipEffectAction"
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        let mut ball_id = t.ctx().my_item_id.map_or(Value::None, Value::Int);
        if t.attribute("effectTargetAddress").truthy() {
            let Some(address) = t.required_address("effectTargetAddress") else { return };
            ball_id = t.get_last_blackboard_value(&address);
        }
        let (Some(name), Some(duration)) = (t.required("effectName"), t.required("effectDuration")) else {
            return;
        };
        let graphic_info = t.attribute("graphicInfo");
        let Some(ball_id) = id_of(&ball_id) else {
            t.raise(BehaviorError::Task("TypeError: no ball to play the effect on".to_string()));
            return;
        };
        let graphic_info = if graphic_info.is_none() { None } else { Some(graphic_info) };
        t.world().start_ship_effect(
            ball_id,
            name.as_str().unwrap_or(""),
            &duration,
            1,
            graphic_info.as_ref(),
        );
        t.set_status_to_success();
    }
}

/// Plays a stretch effect from my entity to the target, for some repeats.
#[derive(Debug, Default)]
pub struct PlayStretchEffectAction;

impl PlayStretchEffectAction {
    /// `get_effect_duration`: the duration, a little shorter or longer by chance when the task says so.
    fn get_effect_duration(t: &mut TaskCtx<'_, '_>) -> Option<Value> {
        let duration = t.required("effectDuration")?;
        if t.has_attribute("effectDurationRandomVariance") {
            let variance = t.attribute("effectDurationRandomVariance");
            if variance.py_gt(&Value::Int(0)) {
                let variance = variance.as_float().unwrap_or(0.0);
                let difference = t.world().random() * variance - variance * 0.5;
                return duration.py_add(&Value::Float(difference));
            }
        }
        Some(duration)
    }
}

impl Task for PlayStretchEffectAction {
    fn type_name(&self) -> &'static str {
        "PlayStretchEffectAction"
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        t.set_status_to_failed();
        let Some(target_address) = t.required_address("effectTargetAddress") else { return };
        let target = t.get_last_blackboard_value(&target_address);
        let Some(graphic_info_address) = t.required("graphicInfoAddress") else { return };
        let graphic_info = if graphic_info_address.truthy() {
            let Some(address) = crate::blackboards::Address::from_value(&graphic_info_address) else {
                return;
            };
            Some(t.get_last_blackboard_value(&address))
        } else {
            None
        };
        let Some(repeats) = t.required("effectRepeats") else { return };
        let Some(target_id) = id_of(&target) else { return };
        let Some(me) = t.my_item_id() else { return };
        let module_id = if use_entity_as_module(t) { Some(me) } else { None };
        let Some(name) = t.required("effectName") else { return };
        let Some(duration) = Self::get_effect_duration(t) else { return };
        t.world().start_stretch_effect(
            me,
            target_id,
            name.as_str().unwrap_or(""),
            &duration,
            &repeats,
            module_id,
            graphic_info.as_ref(),
        );
        t.set_status_to_success();
    }
}

/// Stops the stretch effect from my entity to the target.
#[derive(Debug, Default)]
pub struct StopStretchEffectAction;

impl Task for StopStretchEffectAction {
    fn type_name(&self) -> &'static str {
        "StopStretchEffectAction"
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        t.set_status_to_failed();
        let Some(target_address) = t.required_address("effectTargetAddress") else { return };
        let target = t.get_last_blackboard_value(&target_address);
        let Some(target_id) = id_of(&target) else { return };
        let Some(me) = t.my_item_id() else { return };
        let module_id = if use_entity_as_module(t) { Some(me) } else { None };
        let Some(name) = t.required("effectName") else { return };
        t.world().stop_stretch_effect(me, target_id, name.as_str().unwrap_or(""), module_id);
        t.set_status_to_success();
    }
}

enter_task!(
    /// Sets a dogma attribute of my entity to a value of the blackboard or of the task.
    SetDogmaAttributeValue,
    |t| {
        // `_GetValue`
        let Some(value_address) = t.required("valueAddress") else { return };
        let value = if value_address.truthy() {
            let Some(address) = crate::blackboards::Address::from_value(&value_address) else { return };
            t.get_last_blackboard_value(&address)
        } else {
            let Some(value) = t.required("value") else { return };
            if value.is_none() {
                let behavior = t.behavior_id();
                t.raise(BehaviorError::Authoring(format!(
                    "BehaviorID: {behavior:?} has no value defined for task: SetDogmaAttributeValue"
                )));
                return;
            }
            value
        };
        let (Some(me), Some(attribute_id)) = (t.my_item_id(), t.required_int("attributeId")) else { return };
        t.world().set_attribute_value(me, attribute_id, &value);
        t.set_status_to_success();
    }
);

enter_task!(
    /// Puts a dogma attribute of my entity, times a scalar, on the blackboard.
    SendDogmaAttributeValueAsMessage,
    |t| {
        let (Some(me), Some(attribute_id)) = (t.my_item_id(), t.required_int("attributeId")) else { return };
        let attribute_value = t.world().attribute_value(me, attribute_id);
        let Some(attribute_value) = t.ok(attribute_value) else { return };
        let Some(scalar_address) = t.required("scalarValueAddress") else { return };
        let scalar = if !scalar_address.is_none() {
            let Some(address) = crate::blackboards::Address::from_value(&scalar_address) else { return };
            t.get_last_blackboard_value(&address)
        } else {
            // `getattr(self.attributes, 'scalar', None) or 1.0`
            let scalar = t.attribute("scalar");
            if scalar.truthy() { scalar } else { Value::Float(1.0) }
        };
        let Some(scalar) = scalar.as_float() else {
            t.raise(BehaviorError::Task("TypeError: unsupported operand type(s) for *: 'float' and 'NoneType'".to_string()));
            return;
        };
        let Some(value_address) = t.required_address("valueAddress") else { return };
        t.send_blackboard_value(&value_address, Value::Float(attribute_value * scalar));
        t.set_status_to_success();
    }
);

enter_task!(
    /// Sets a dogma attribute of my entity back to what its type has.
    ResetDogmaAttributeValue,
    |t| {
        let Some(slim) = t.ctx().my_slim_item.clone() else {
            attribute_error_on_item(t, "typeID");
            return;
        };
        let (Some(me), Some(attribute_id)) = (t.my_item_id(), t.required_int("attributeId")) else { return };
        let type_value = t.world().type_attribute(slim.type_id, attribute_id);
        t.world().set_attribute_value(me, attribute_id, &Value::Float(type_value));
        t.set_status_to_success();
    }
);

const HANDLER_ON_STOP_EFFECT: u16 = 0;

/// Starts an effect of my entity on the selected target, and can reset the tree when the effect stops.
#[derive(Debug, Default)]
pub struct ActivateTargetedEffect;

impl ActivateTargetedEffect {
    /// `StartMonitoringEffectStoppedIfRequired`.
    fn start_monitoring_effect_stopped_if_required(t: &mut TaskCtx<'_, '_>) {
        let Some(required) = t.required("shouldResetBehaviorOnEffectStopped") else { return };
        if required.truthy()
            && let Some(me) = t.my_item_id()
        {
            t.subscribe_item(me, message::ON_EFFECT_STOPPED, HANDLER_ON_STOP_EFFECT);
        }
    }

    /// `TryActivateEffect(ownerID, targetID)`: a `UserError` is only logged (and an effect that is active already is monitored as if it had started).
    fn try_activate_effect(t: &mut TaskCtx<'_, '_>, owner_id: i64, target_id: &Value) {
        let (Some(me), Some(effect_id), Some(repeats)) =
            (t.my_item_id(), t.required_int("effectId"), t.required_int("repeats"))
        else {
            return;
        };
        let result = t.world().activate_with_context(
            owner_id,
            me,
            Some(effect_id),
            target_id.as_int(),
            repeats,
            &Value::None,
        );
        match result {
            Ok(()) => Self::start_monitoring_effect_stopped_if_required(t),
            Err(WorldError::User(msg)) => {
                if msg == "EffectAlreadyActive2" {
                    Self::start_monitoring_effect_stopped_if_required(t);
                }
            }
            Err(other) => t.raise(other.into()),
        }
    }
}

impl Task for ActivateTargetedEffect {
    fn type_name(&self) -> &'static str {
        "ActivateTargetedEffect"
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        let Some(address) = t.required_address("selectedTargetAddress") else { return };
        let selected_target = t.get_last_blackboard_value(&address);
        t.set_status_to_success();
        let Some(item) = get_self_as_item(t) else {
            attribute_error_on_item(t, "typeID");
            return;
        };
        if selected_target.truthy() {
            let Some(effect_id) = t.required_int("effectId") else { return };
            if type_has_effect(t, item.type_id, effect_id) {
                Self::try_activate_effect(t, item.owner_id, &selected_target);
            }
        }
    }

    fn on_event(&mut self, t: &mut TaskCtx<'_, '_>, _handler: u16, args: &[Value]) {
        // `OnStopEffect(effectId)`
        if t.is_invalid() {
            return;
        }
        let Some(effect_id) = t.required("effectId") else { return };
        if args.first().is_some_and(|stopped| stopped.py_eq(&effect_id)) {
            t.request_reset();
        }
    }

    fn clean_up(&mut self, t: &mut TaskCtx<'_, '_>) {
        if t.is_invalid() {
            return;
        }
        if let Some(me) = t.my_item_id() {
            t.unsubscribe_item(me, message::ON_EFFECT_STOPPED, HANDLER_ON_STOP_EFFECT);
        }
        t.set_status_to_invalid();
    }
}

/// `ActivateEffect` and `ActivateDefaultEffect`.
#[derive(Debug, Clone, Copy)]
pub struct ActivateEffect {
    name: &'static str,
    default_effect: bool,
}

impl ActivateEffect {
    pub fn new() -> Self {
        Self { name: "ActivateEffect", default_effect: false }
    }

    /// `ActivateDefaultEffect`: the default effect of my type.
    pub fn default_effect() -> Self {
        Self { name: "ActivateDefaultEffect", default_effect: true }
    }
}

impl Default for ActivateEffect {
    fn default() -> Self {
        Self::new()
    }
}

impl Task for ActivateEffect {
    fn type_name(&self) -> &'static str {
        self.name
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        t.set_status_to_success();
        let Some(item) = get_self_as_item(t) else {
            attribute_error_on_item(t, "ownerID");
            return;
        };
        // `GetEffectId`
        let effect_id = if self.default_effect {
            let Some(slim) = t.ctx().my_slim_item.clone() else {
                attribute_error_on_item(t, "typeID");
                return;
            };
            get_default_effect_id_for_type(t, slim.type_id)
        } else {
            let Some(effect_id) = t.required_int("effectId") else { return };
            Some(effect_id)
        };
        let Some(me) = t.my_item_id() else { return };
        let Some(repeats) = t.required_int("repeats") else { return };
        let context = t.attribute("contextDict");
        let result = t.world().activate_with_context(item.owner_id, me, effect_id, None, repeats, &context);
        match result {
            Ok(()) | Err(WorldError::User(_)) => {}
            Err(other) => t.raise(other.into()),
        }
    }
}

enter_task!(
    /// Shows my entity attacking the target with a laser, at its rate of fire.
    MockTargetAttack,
    |t| {
        t.set_status_to_failed();
        let Some(address) = t.required_address("targetIdAddress") else { return };
        let target = t.get_last_blackboard_value(&address);
        let Some(target_id) = id_of(&target) else { return };
        let Some(me) = t.my_item_id() else { return };
        let duration = t.world().attribute_value(me, ATTRIBUTE_RATE_OF_FIRE);
        let Some(duration) = t.ok(duration) else { return };
        t.world().start_stretch_effect(
            me,
            target_id,
            TARGET_ATTACK_GRAPHIC_EFFECT,
            &Value::Float(duration),
            &Value::Int(99),
            Some(me),
            None,
        );
        t.set_status_to_success();
    }
);

enter_task!(
    /// Stops showing my entity attacking the target with a laser.
    StopMockTargetAttack,
    |t| {
        t.set_status_to_failed();
        let Some(address) = t.required_address("targetIdAddress") else { return };
        let target = t.get_last_blackboard_value(&address);
        let Some(target_id) = id_of(&target) else { return };
        let Some(me) = t.my_item_id() else { return };
        t.world().stop_stretch_effect(me, target_id, TARGET_ATTACK_GRAPHIC_EFFECT, Some(me));
        t.set_status_to_success();
    }
);

enter_task!(
    /// Puts the effect of the task on the blackboard.
    SendDogmaEffectIdAsMessage,
    |t| {
        let Some(address) = t.required_address("effectIdAddress") else { return };
        let Some(effect_id) = t.required("effectId") else { return };
        t.send_blackboard_value(&address, effect_id);
        t.set_status_to_success();
    }
);

enter_task!(
    /// Stops an effect of my entity, if its type has it.
    StopEffect,
    |t| {
        t.set_status_to_failed();
        let Some(effect_id) = t.required("effectId") else { return };
        if !effect_id.truthy() {
            return;
        }
        let Some(slim) = t.ctx().my_slim_item.clone() else {
            attribute_error_on_item(t, "typeID");
            return;
        };
        let Some(effect_id) = effect_id.as_int() else { return };
        if !type_has_effect(t, slim.type_id, effect_id) {
            return;
        }
        t.set_status_to_success();
        let Some(forced) = t.required("forcedStop") else { return };
        try_stop_effect(t, effect_id, forced.truthy());
    }
);

enter_task!(
    /// Has the players in my bubble show another nebula.
    ReplaceNebulaForPlayersInBubble,
    |t| {
        t.set_status_to_success();
        let Some(my_ball) = get_my_ball(t) else { return };
        for ball_id in get_ship_balls_in_bubble(t, my_ball.new_bubble_id) {
            let Some(slim) = get_slim_item(t, ball_id) else {
                attribute_error_on_item(t, "charID");
                return;
            };
            t.world().singlecast_by_char_id(slim.char_id, "OnReplaceNebula", &[Value::Str(NEBULA_PATH.to_string())]);
        }
    }
);

#[cfg(test)]
mod tests {
    use super::*;
    use crate::blackboards::{Address, ScopeType};
    use crate::env::{InventoryItem, SlimItem};
    use crate::tree::tests::Rig;
    use crate::tree::{Attrs, Status};

    fn item(name: &str) -> Value {
        Address::new(ScopeType::Item, name).to_value()
    }

    fn rig() -> Rig {
        let mut rig = Rig::new();
        rig.ctx.my_slim_item =
            Some(SlimItem { item_id: 1, type_id: 700, owner_id: Some(7), ..Default::default() });
        rig.world.items.insert(1, InventoryItem { item_id: 1, type_id: 700, owner_id: 7, location_id: 1 });
        rig
    }

    #[test]
    fn a_one_shot_stretch_effect_needs_a_source_and_a_target() {
        let attrs = Attrs::new()
            .with("effectTargetAddress", item("Target"))
            .with("effectName", "effects.Laser")
            .with("effectDuration", 2000i64)
            .with("useEntityAsModule", true);
        let mut rig = rig();
        assert_eq!(rig.run(Box::new(PlayOneShotStretchEffectAction), attrs.clone()), Status::Failure);
        rig.post(ScopeType::Item, "Target", Value::Int(20));
        assert_eq!(rig.run(Box::new(PlayOneShotStretchEffectAction), attrs.clone()), Status::Success);
        assert_eq!(rig.world.take_calls(), ["start_stretch_effect(1, 20, effects.Laser, 2000, 1, Some(1))"]);
        // a source of the blackboard, and a duration of it
        rig.post(ScopeType::Item, "Source", Value::Int(5));
        rig.post(ScopeType::Item, "Duration", Value::Int(300));
        let attrs =
            attrs.with("effectSourceAddress", item("Source")).with("effectDurationAddress", item("Duration"));
        rig.run(Box::new(PlayOneShotStretchEffectAction), attrs.clone());
        assert_eq!(rig.world.take_calls(), ["start_stretch_effect(5, 20, effects.Laser, 300, 1, Some(5))"]);
        rig.post(ScopeType::Item, "Source", Value::None);
        assert_eq!(rig.run(Box::new(PlayOneShotStretchEffectAction), attrs), Status::Failure);
    }

    #[test]
    fn ship_effects_are_played_on_the_target_or_on_my_entity_or_the_one_of_the_blackboard() {
        let mut rig = rig();
        let targeted = Attrs::new()
            .with("effectTargetAddress", item("Target"))
            .with("effectName", "effects.X")
            .with("effectDuration", 100i64);
        assert_eq!(rig.run(Box::new(PlayOneShotTargetedShipEffectAction), targeted.clone()), Status::Failure);
        rig.post(ScopeType::Item, "Target", Value::Int(20));
        assert_eq!(rig.run(Box::new(PlayOneShotTargetedShipEffectAction), targeted), Status::Success);
        assert_eq!(rig.world.take_calls(), ["start_ship_effect(20, effects.X, 100, 1)"]);
        let mine = Attrs::new()
            .with("effectName", "effects.Y")
            .with("effectDuration", 50i64)
            .with("graphicInfo", Value::Int(9));
        assert_eq!(rig.run(Box::new(PlayOneShotShipEffectAction), mine.clone()), Status::Success);
        assert_eq!(rig.world.take_calls(), ["start_ship_effect(1, effects.Y, 50, 1, Int(9))"]);
        rig.run(Box::new(PlayOneShotShipEffectAction), mine.with("effectTargetAddress", item("Target")));
        assert_eq!(rig.world.take_calls(), ["start_ship_effect(20, effects.Y, 50, 1, Int(9))"]);
    }

    #[test]
    fn a_stretch_effect_repeats_for_as_long_as_it_is_told_and_its_duration_varies_by_chance() {
        let attrs = Attrs::new()
            .with("effectTargetAddress", item("Target"))
            .with("graphicInfoAddress", Value::None)
            .with("effectRepeats", 4i64)
            .with("effectName", "effects.Z")
            .with("effectDuration", 1000i64)
            .with("effectDurationRandomVariance", 200i64);
        let mut rig = rig();
        rig.world.set_numbers(&[0.75]);
        rig.post(ScopeType::Item, "Target", Value::Int(20));
        assert_eq!(rig.run(Box::new(PlayStretchEffectAction), attrs.clone()), Status::Success);
        assert_eq!(
            rig.world.take_calls(),
            ["start_stretch_effect(1, 20, effects.Z, 1050, 4, None)"],
            "1000 + 0.75 * 200 - 100"
        );
        let mut rig2 = self::rig();
        assert_eq!(rig2.run(Box::new(PlayStretchEffectAction), attrs), Status::Failure, "no target");
    }

    #[test]
    fn a_stretch_effect_is_stopped_for_a_target() {
        let attrs = Attrs::new().with("effectTargetAddress", item("Target")).with("effectName", "effects.Z");
        let mut rig = rig();
        assert_eq!(rig.run(Box::new(StopStretchEffectAction), attrs.clone()), Status::Failure);
        rig.post(ScopeType::Item, "Target", Value::Int(20));
        assert_eq!(rig.run(Box::new(StopStretchEffectAction), attrs), Status::Success);
        assert_eq!(rig.world.take_calls(), ["stop_stretch_effect(1, 20, effects.Z, None)"]);
    }

    #[test]
    fn a_dogma_attribute_is_set_from_the_blackboard_or_the_task_and_no_value_is_an_authoring_error() {
        let mut rig = rig();
        rig.post(ScopeType::Item, "V", Value::Float(2.5));
        let from_board = Attrs::new()
            .with("valueAddress", item("V"))
            .with("attributeId", 77i64)
            .with("value", Value::None);
        assert_eq!(rig.run(Box::new(SetDogmaAttributeValue), from_board), Status::Success);
        let from_task =
            Attrs::new().with("valueAddress", Value::None).with("attributeId", 77i64).with("value", 3i64);
        rig.run(Box::new(SetDogmaAttributeValue), from_task);
        assert_eq!(
            rig.world.take_calls(),
            ["set_attribute_value(1, 77, 2.5)", "set_attribute_value(1, 77, 3)"]
        );
        let nothing = Attrs::new()
            .with("valueAddress", Value::None)
            .with("attributeId", 77i64)
            .with("value", Value::None);
        rig.run(Box::new(SetDogmaAttributeValue), nothing);
        assert!(matches!(&rig.last_error, Some(BehaviorError::Authoring(_))));
    }

    #[test]
    fn a_dogma_attribute_is_sent_times_a_scalar_and_reset_to_the_type_s() {
        let mut rig = rig();
        rig.world.attributes.insert((1, 77), 10.0);
        rig.world.type_attributes.insert((700, 77), 4.0);
        let attrs = Attrs::new()
            .with("attributeId", 77i64)
            .with("scalarValueAddress", Value::None)
            .with("valueAddress", item("Out"));
        assert_eq!(
            rig.run(Box::new(SendDogmaAttributeValueAsMessage), attrs.clone().with("scalar", 0.5)),
            Status::Success
        );
        assert_eq!(rig.last(ScopeType::Item, "Out"), Value::Float(5.0));
        rig.run(Box::new(SendDogmaAttributeValueAsMessage), attrs);
        assert_eq!(rig.last(ScopeType::Item, "Out"), Value::Float(10.0), "no scalar is 1");
        rig.post(ScopeType::Item, "Scalar", Value::Float(3.0));
        rig.run(
            Box::new(SendDogmaAttributeValueAsMessage),
            Attrs::new()
                .with("attributeId", 77i64)
                .with("scalarValueAddress", item("Scalar"))
                .with("valueAddress", item("Out")),
        );
        assert_eq!(rig.last(ScopeType::Item, "Out"), Value::Float(30.0));
        rig.run(Box::new(ResetDogmaAttributeValue), Attrs::new().with("attributeId", 77i64));
        assert_eq!(rig.world.take_calls(), ["set_attribute_value(1, 77, 4)"]);
    }

    fn targeted_attrs() -> Attrs {
        Attrs::new()
            .with("selectedTargetAddress", item("Target"))
            .with("effectId", 101i64)
            .with("repeats", 0i64)
            .with("shouldResetBehaviorOnEffectStopped", true)
    }

    #[test]
    fn a_targeted_effect_is_started_and_the_tree_is_reset_when_it_stops() {
        let mut rig = rig();
        rig.world.type_effects.insert((700, 101));
        rig.post(ScopeType::Item, "Target", Value::Int(20));
        let task = rig.tree.add_task(Box::new(ActivateTargetedEffect), targeted_attrs());
        let wait = rig.leaf("wait", Status::Success, 50);
        let root = rig.sequence(&[task, wait]);
        rig.tree.start_root_task(root);
        rig.tick().unwrap();
        assert_eq!(rig.world.take_calls(), ["activate(7, 1, 101, Some(20), 0)"]);
        rig.send_event(1, message::ON_EFFECT_STOPPED, &[Value::Int(999)]);
        assert!(!rig.tree.has_reset_request());
        rig.send_event(1, message::ON_EFFECT_STOPPED, &[Value::Int(101)]);
        assert!(rig.tree.has_reset_request());
    }

    #[test]
    fn an_effect_that_is_active_already_is_monitored_and_another_user_error_is_not() {
        for (msg, monitored) in [("EffectAlreadyActive2", true), ("Other", false)] {
            let mut rig = rig();
            rig.world.type_effects.insert((700, 101));
            rig.world.activation_error = Some(WorldError::User(msg.into()));
            rig.post(ScopeType::Item, "Target", Value::Int(20));
            let task = rig.tree.add_task(Box::new(ActivateTargetedEffect), targeted_attrs());
            let wait = rig.leaf("wait", Status::Success, 50);
            let root = rig.sequence(&[task, wait]);
            rig.tree.start_root_task(root);
            rig.tick().unwrap();
            assert_eq!(rig.last_error, None);
            rig.send_event(1, message::ON_EFFECT_STOPPED, &[Value::Int(101)]);
            assert_eq!(rig.tree.has_reset_request(), monitored, "{msg}");
        }
    }

    #[test]
    fn a_targeted_effect_that_the_type_lacks_or_without_a_target_is_not_started_and_the_task_succeeds() {
        let mut rig = rig();
        rig.post(ScopeType::Item, "Target", Value::Int(20));
        assert_eq!(rig.run(Box::new(ActivateTargetedEffect), targeted_attrs()), Status::Success);
        assert!(rig.world.take_calls().is_empty());
    }

    #[test]
    fn an_effect_is_started_on_my_entity_with_its_context_or_its_types_default_one() {
        let attrs =
            Attrs::new().with("effectId", 101i64).with("repeats", 3i64).with("contextDict", Value::Int(4));
        let mut rig = rig();
        assert_eq!(rig.run(Box::new(ActivateEffect::new()), attrs.clone()), Status::Success);
        assert_eq!(rig.world.take_calls(), ["activate(7, 1, 101, None, 3)"]);
        rig.world.default_effects.insert(700, 202);
        rig.run(Box::new(ActivateEffect::default_effect()), attrs);
        assert_eq!(rig.world.take_calls(), ["activate(7, 1, 202, None, 3)"]);
    }

    #[test]
    fn a_mock_attack_shows_a_laser_at_the_rate_of_fire_and_ends_it() {
        let mut rig = rig();
        rig.world.attributes.insert((1, ATTRIBUTE_RATE_OF_FIRE), 3000.0);
        let attrs = Attrs::new().with("targetIdAddress", item("Target"));
        assert_eq!(rig.run(Box::new(MockTargetAttack), attrs.clone()), Status::Failure);
        rig.post(ScopeType::Item, "Target", Value::Int(20));
        assert_eq!(rig.run(Box::new(MockTargetAttack), attrs.clone()), Status::Success);
        assert_eq!(rig.run(Box::new(StopMockTargetAttack), attrs), Status::Success);
        assert_eq!(
            rig.world.take_calls(),
            [
                "start_stretch_effect(1, 20, effects.Laser, 3000, 99, Some(1))",
                "stop_stretch_effect(1, 20, effects.Laser, Some(1))"
            ]
        );
    }

    #[test]
    fn an_effect_id_is_sent_and_an_effect_is_stopped_only_when_the_type_has_it() {
        let mut rig = rig();
        let send = Attrs::new().with("effectIdAddress", item("Effect")).with("effectId", 101i64);
        rig.run(Box::new(SendDogmaEffectIdAsMessage), send);
        assert_eq!(rig.last(ScopeType::Item, "Effect"), Value::Int(101));
        let stop = Attrs::new().with("effectId", 101i64).with("forcedStop", true);
        assert_eq!(
            rig.run(Box::new(StopEffect), stop.clone()),
            Status::Failure,
            "the type has not the effect"
        );
        rig.world.type_effects.insert((700, 101));
        assert_eq!(rig.run(Box::new(StopEffect), stop), Status::Success);
        assert_eq!(rig.world.take_calls(), ["stop_effect(101, 1, true)"]);
        rig.world.type_effects.insert((700, 0));
        assert_eq!(
            rig.run(Box::new(StopEffect), Attrs::new().with("effectId", 0i64).with("forcedStop", true)),
            Status::Failure,
            "no effect authored"
        );
    }

    #[test]
    fn the_players_in_my_bubble_are_told_to_replace_the_nebula() {
        let mut rig = rig();
        rig.world.add_ball(1, [0.0; 3], 700).new_bubble_id = 3;
        rig.world.bubbles.insert(3, vec![1, 20, 21]);
        for (id, char_id) in [(20, 90000001), (21, 90000002)] {
            rig.world.add_ball(id, [0.0; 3], 600).new_bubble_id = 3;
            rig.world.slims.get_mut(&id).unwrap().char_id = Some(char_id);
            rig.world.slims.get_mut(&id).unwrap().category_id = 6;
        }
        rig.world.slims.get_mut(&1).unwrap().category_id = 6;
        rig.world.slims.get_mut(&1).unwrap().char_id = None;
        assert_eq!(rig.run(Box::new(ReplaceNebulaForPlayersInBubble), Attrs::new()), Status::Success);
        assert_eq!(rig.last_error, None);
        let calls = rig.world.take_calls();
        assert!(calls.contains(&"singlecast_by_char_id(Some(90000001), OnReplaceNebula, [Str(\"res:/dx9/scene/abyssal/ad01_a_cube.dds\")])".to_string()), "{calls:?}");
        assert_eq!(calls.len(), 3, "the entity itself is a ship in the bubble too");
    }
}
