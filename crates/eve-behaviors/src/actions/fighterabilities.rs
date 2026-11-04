//! `behaviors/actions/fighterabilities.py`: the abilities of fighters: starting one on a point, stopping it, and telling the ability registry how it went.

use crate::env::WorldError;
use crate::tree::{BehaviorError, Task, TaskCtx};
use crate::utility::dogmatic::{get_attribute_value, type_has_effect};
use crate::value::Value;

fn attribute_error(t: &mut TaskCtx<'_, '_>, what: &str) {
    t.raise(BehaviorError::Task(format!("AttributeError: 'NoneType' object has no attribute '{what}'")));
}

/// `context.ballpark.inventory2.GetItem(itemId)`.
fn get_item(t: &mut TaskCtx<'_, '_>, item_id: i64) -> Option<crate::env::InventoryItem> {
    t.world().inventory_item(item_id)
}

/// `ActivateAbilityEffectOnPoint` (the client's `ActivateAbilityEffectBaseTask` with its `ActivateAbility`): starts the ability effect of the fighter
/// at a point, and tells the registry when it did.
#[derive(Debug, Default)]
pub struct ActivateAbilityEffectOnPoint;

impl ActivateAbilityEffectOnPoint {
    /// `GetAbilityEffectID`: the effect on the blackboard, if the type of the fighter has it.
    fn get_ability_effect_id(t: &mut TaskCtx<'_, '_>) -> Option<Option<i64>> {
        let me = t.my_item_id()?;
        let Some(item) = get_item(t, me) else {
            attribute_error(t, "typeID");
            return None;
        };
        let address = t.required_address("effectIDAddress")?;
        let effect = t.get_last_blackboard_value(&address);
        let Some(effect_id) = effect.as_int() else { return Some(None) };
        Some(if type_has_effect(t, item.type_id, effect_id) { Some(effect_id) } else { None })
    }

    /// `IsTargetWithinEffectRange`.
    fn is_target_within_effect_range(
        t: &mut TaskCtx<'_, '_>,
        effect_id: i64,
        target_id: i64,
    ) -> Option<bool> {
        let Some(effect) = t.world().effect_info(effect_id) else {
            t.raise(WorldError::Key(effect_id.to_string()).into());
            return None;
        };
        if effect.falloff_attribute_id.is_none_or(|a| a == 0)
            && let Some(range_attribute) = effect.range_attribute_id
        {
            let me = t.my_item_id()?;
            let distance = t.world().surface_dist(me, target_id);
            let distance = t.ok(distance)?;
            let type_effect_range = get_attribute_value(t, me, range_attribute)?;
            if distance > type_effect_range {
                return Some(false);
            }
        }
        Some(true)
    }

    /// `TryActivateEffect(targetID)`.
    fn try_activate_effect(t: &mut TaskCtx<'_, '_>, target_id: Option<i64>) -> Option<bool> {
        let me = t.my_item_id()?;
        if get_item(t, me).is_none() {
            return Some(false);
        }
        let Some(effect_id) = Self::get_ability_effect_id(t)? else { return Some(false) };
        if let Some(target_id) = target_id {
            if get_item(t, target_id).is_none() {
                return Some(false);
            }
            if !Self::is_target_within_effect_range(t, effect_id, target_id)? {
                return Some(false);
            }
        }
        let slot = t.required("abilitySlotID")?;
        let Some(activation_context) = t.world().ability_activation_context(me, &slot) else {
            return Some(false);
        };
        match t.world().start_fighter_ability_dogma_effect(effect_id, &activation_context, target_id) {
            Ok(()) => Some(true),
            Err(WorldError::User(message)) => {
                // the reason is told on the blackboard
                let address = t.required_address("failureReasonAddress")?;
                t.send_blackboard_value(&address, Value::Str(message));
                Some(false)
            }
            Err(other) => {
                t.raise(other.into());
                None
            }
        }
    }

    /// `ActivateAbility` of `ActivateAbilityEffectOnPoint`: only for a point.
    fn activate_ability(t: &mut TaskCtx<'_, '_>) -> Option<bool> {
        let address = t.required_address("targetPoint")?;
        let target_point = t.get_last_blackboard_value(&address);
        if target_point.is_none() {
            return Some(false);
        }
        Self::try_activate_effect(t, None)
    }

    /// `_GetEffectCycleTiming`.
    fn effect_cycle_timing(t: &mut TaskCtx<'_, '_>) -> Option<(i64, f64)> {
        let address = t.required_address("effectIDAddress")?;
        let effect_id = t.get_last_blackboard_value(&address).as_int();
        let me = t.my_item_id()?;
        let cycle = effect_id.and_then(|effect| t.world().active_effect_data(me, effect));
        let Some(cycle) = cycle else {
            t.raise(BehaviorError::Task(
                "TypeError: 'NoneType' object has no attribute '__getitem__'".to_string(),
            ));
            return None;
        };
        Some((cycle.start_time, cycle.duration_ms))
    }
}

impl Task for ActivateAbilityEffectOnPoint {
    fn type_name(&self) -> &'static str {
        "ActivateAbilityEffectOnPoint"
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        t.set_status_to_failed();
        let Some(reason_address) = t.required_address("failureReasonAddress") else { return };
        t.send_blackboard_value(&reason_address, Value::None);
        let Some(activated) = Self::activate_ability(t) else { return };
        if activated {
            t.set_status_to_success();
            let Some((start_time, duration_ms)) = Self::effect_cycle_timing(t) else { return };
            let (Some(me), Some(slot)) = (t.my_item_id(), t.required("abilitySlotID")) else { return };
            t.world().ability_activation_succeeded(me, &slot, start_time, duration_ms);
        }
    }
}

/// Stops the ability effect of the fighter, or tells the registry the ability is off when the effect is not running.
#[derive(Debug, Default)]
pub struct StopAbilityEffectOnTarget;

impl Task for StopAbilityEffectOnTarget {
    fn type_name(&self) -> &'static str {
        "StopAbilityEffectOnTarget"
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        t.set_status_to_success();
        let Some(fighter_id) = t.my_item_id() else { return };
        let Some(item) = get_item(t, fighter_id) else {
            attribute_error(t, "typeID");
            return;
        };
        let Some(effect_address) = t.required_address("effectIDAddress") else { return };
        let effect = t.get_last_blackboard_value(&effect_address);
        let Some(effect_id) = effect.as_int() else { return };
        if !type_has_effect(t, item.type_id, effect_id) {
            return;
        }
        // `TryStopEffect`: a `UserError` is nothing
        if t.world().active_effect_data(fighter_id, effect_id).is_none() {
            let (Some(reason_address), Some(slot)) =
                (t.required_address("failureReasonAddress"), t.required("abilitySlotID"))
            else {
                return;
            };
            let failure_reason = t.get_last_blackboard_value(&reason_address);
            t.world().ability_deactivated(fighter_id, &slot, &failure_reason);
        } else {
            match t.world().stop_effect(effect_id, fighter_id, false) {
                Ok(()) | Err(WorldError::User(_)) => {}
                Err(other) => t.raise(other.into()),
            }
        }
    }
}

enter_task!(
    /// Tells the ability registry that the fighter's ability could not be activated.
    NotifyAbilityActivationFailure,
    |t| {
        t.set_status_to_success();
        let (Some(me), Some(slot)) = (t.my_item_id(), t.required("abilitySlotID")) else { return };
        t.world().ability_activation_failed(me, &slot);
    }
);

#[cfg(test)]
mod tests {
    use super::*;
    use crate::blackboards::{Address, ScopeType};
    use crate::env::{EffectCycle, EffectInfo, InventoryItem};
    use crate::tree::tests::Rig;
    use crate::tree::{Attrs, Status};

    fn item(name: &str) -> Value {
        Address::new(ScopeType::Item, name).to_value()
    }

    fn rig() -> Rig {
        let mut rig = Rig::new();
        rig.world.items.insert(1, InventoryItem { item_id: 1, type_id: 700, owner_id: 7, location_id: 1 });
        rig.world.type_effects.insert((700, 101));
        rig.world.effects.insert(101, EffectInfo::default());
        rig.world.ability_contexts.insert((1, 2), Value::Str("context".into()));
        rig.world.effect_cycles.insert((1, 101), EffectCycle { start_time: 500, duration_ms: 3000.0 });
        rig.post(ScopeType::Item, "Effect", Value::Int(101));
        rig.post(ScopeType::Item, "Point", Value::from_vec3([1.0, 2.0, 3.0]));
        rig
    }

    type Spoil = fn(&mut Rig);

    fn activate_attrs() -> Attrs {
        Attrs::new()
            .with("failureReasonAddress", item("Reason"))
            .with("effectIDAddress", item("Effect"))
            .with("targetPoint", item("Point"))
            .with("abilitySlotID", 2i64)
    }

    #[test]
    fn an_ability_on_a_point_is_started_and_the_registry_is_told_when_the_cycle_began_and_how_long_it_is() {
        let mut rig = rig();
        rig.post(ScopeType::Item, "Reason", Value::Str("old".into()));
        assert_eq!(rig.run(Box::new(ActivateAbilityEffectOnPoint), activate_attrs()), Status::Success);
        assert_eq!(rig.last_error, None);
        assert_eq!(
            rig.world.ability_events,
            ["started(101, Str(\"context\"), None)", "succeeded(1, Int(2), 500, 3000)"]
        );
        assert_eq!(rig.last(ScopeType::Item, "Reason"), Value::None, "the reason of before is forgotten");
    }

    #[test]
    fn nothing_is_started_without_a_point_an_effect_a_context_or_an_item() {
        let cases: [(&str, Spoil); 5] = [
            ("no point", |rig| rig.post(ScopeType::Item, "Point", Value::None)),
            ("an effect the type has not", |rig| rig.world.type_effects.clear()),
            ("no effect on the blackboard", |rig| rig.post(ScopeType::Item, "Effect", Value::None)),
            ("no activation context for the slot", |rig| rig.world.ability_contexts.clear()),
            ("no item", |rig| rig.world.items.clear()),
        ];
        for (what, spoil) in cases {
            let mut rig = self::rig();
            spoil(&mut rig);
            assert_eq!(
                rig.run(Box::new(ActivateAbilityEffectOnPoint), activate_attrs()),
                Status::Failure,
                "{what}"
            );
            assert!(rig.world.ability_events.is_empty(), "{what}");
        }
    }

    #[test]
    fn a_user_error_of_the_effect_is_the_failure_reason_on_the_blackboard() {
        let mut rig = rig();
        rig.world.ability_error = Some(WorldError::User("NoCapacitor".into()));
        assert_eq!(rig.run(Box::new(ActivateAbilityEffectOnPoint), activate_attrs()), Status::Failure);
        assert_eq!(rig.last(ScopeType::Item, "Reason"), Value::Str("NoCapacitor".into()));
        assert_eq!(rig.last_error, None);
    }

    #[test]
    fn an_effect_that_is_running_is_stopped_and_one_that_is_not_tells_the_registry_why() {
        let attrs = Attrs::new()
            .with("effectIDAddress", item("Effect"))
            .with("failureReasonAddress", item("Reason"))
            .with("abilitySlotID", 2i64);
        let mut rig = rig();
        assert_eq!(rig.run(Box::new(StopAbilityEffectOnTarget), attrs.clone()), Status::Success);
        assert_eq!(rig.world.take_calls(), ["stop_effect(101, 1, false)"]);
        // a `UserError` of the stop is nothing; any other error is a fault
        rig.world.stop_effect_error = Some(WorldError::User("NotRunning".into()));
        assert_eq!(rig.run(Box::new(StopAbilityEffectOnTarget), attrs.clone()), Status::Success);
        assert_eq!(rig.last_error, None);
        rig.world.stop_effect_error = Some(WorldError::Runtime("boom".into()));
        rig.run(Box::new(StopAbilityEffectOnTarget), attrs.clone());
        assert!(rig.last_error.is_some());
        rig.world.stop_effect_error = None;
        rig.world.effect_cycles.clear();
        rig.post(ScopeType::Item, "Reason", Value::Str("NoCapacitor".into()));
        rig.run(Box::new(StopAbilityEffectOnTarget), attrs.clone());
        assert_eq!(rig.world.ability_events, ["deactivated(1, Int(2), Str(\"NoCapacitor\"))"]);
        // an effect the type has not
        rig.world.type_effects.clear();
        rig.world.ability_events.clear();
        rig.run(Box::new(StopAbilityEffectOnTarget), attrs);
        assert!(rig.world.ability_events.is_empty());
    }

    #[test]
    fn the_registry_is_told_that_an_ability_could_not_be_activated() {
        let mut rig = rig();
        assert_eq!(
            rig.run(Box::new(NotifyAbilityActivationFailure), Attrs::new().with("abilitySlotID", 3i64)),
            Status::Success
        );
        assert_eq!(rig.world.ability_events, ["failed(1, Int(3))"]);
    }

    #[test]
    fn a_target_is_in_range_when_the_effect_has_no_falloff_and_the_target_is_within_its_range() {
        let mut rig = rig();
        rig.world.effects.insert(102, EffectInfo { range_attribute_id: Some(54), ..Default::default() });
        rig.world.attributes.insert((1, 54), 5000.0);
        rig.world.set_dist(1, 20, 4000.0);
        rig.world.set_dist(1, 21, 6000.0);
        let (mut rig, answer) = {
            let (r, a) = crate::tree::tests::with_task(|_| {}, |_| ());
            let _ = a;
            (rig, r.last_error)
        };
        assert_eq!(answer, None);
        let world = std::mem::take(&mut rig.world);
        let (_, answers) = crate::tree::tests::with_task(
            move |r| r.world = world,
            |t| {
                (
                    ActivateAbilityEffectOnPoint::is_target_within_effect_range(t, 102, 20),
                    ActivateAbilityEffectOnPoint::is_target_within_effect_range(t, 102, 21),
                    ActivateAbilityEffectOnPoint::is_target_within_effect_range(t, 101, 21),
                )
            },
        );
        assert_eq!(answers, Some((Some(true), Some(false), Some(true))));
    }
}
