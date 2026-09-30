//! `behaviors/actions/combatmodules.py`: picking the modules of an entity and putting them to work on a target.

use std::collections::BTreeMap;

use crate::tree::{BehaviorError, Task, TaskCtx};
use crate::utility::ballparks::{is_ball_in_park, is_ship};
use crate::utility::dogmatic::{
    can_activate_effect_on_target, get_default_effect_id_for_type, try_activate_effect,
};
use crate::utility::inventory::{get_inventory_item, get_type_id_by_item_id};
use crate::utility::item_modules::has_fitted_from_type_list;
use crate::value::Value;

const HANDLER_START_MODULE_ON_TARGET: u16 = 0;

/// Starts the default effect of a module on my target, for some cycles.
#[derive(Debug, Default)]
pub struct ActivateModuleTypeOnTarget;

impl ActivateModuleTypeOnTarget {
    /// `_get_cycles`: a whole number of cycles from `min_cycles` up to the one before `max_cycles`; nothing when there is no such number (the
    /// tasklet the client starts dies of a `ValueError`).
    fn get_cycles(t: &mut TaskCtx<'_, '_>) -> Option<i64> {
        let min_cycles = if t.has_attribute("min_cycles") { t.attribute("min_cycles").as_int()? } else { 0 };
        let max_cycles = if t.has_attribute("max_cycles") { t.attribute("max_cycles").as_int()? } else { 0 };
        if max_cycles <= min_cycles {
            return None;
        }
        Some(t.world().random_int(min_cycles, max_cycles - 1))
    }
}

impl Task for ActivateModuleTypeOnTarget {
    fn type_name(&self) -> &'static str {
        "ActivateModuleTypeOnTarget"
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        t.set_status_to_failed();
        let Some(module_address) = t.required_address("moduleAddress") else { return };
        let assigned_module_id = t.get_last_blackboard_value(&module_address);
        let Some(target_address) = t.required_address("targetAddress") else { return };
        let my_target_id = t.get_last_blackboard_value(&target_address);
        let Some(target_id) = my_target_id.as_int() else { return };
        let Some(module_item) = assigned_module_id.as_int().and_then(|id| get_inventory_item(t, id)) else {
            t.raise(BehaviorError::Task(
                "AttributeError: 'NoneType' object has no attribute 'typeID'".to_string(),
            ));
            return;
        };
        let module_type_id = module_item.type_id;
        let Some(effect_id) = get_default_effect_id_for_type(t, module_type_id) else {
            t.raise(crate::env::WorldError::Key("None".to_string()).into());
            return;
        };
        if can_activate_effect_on_target(t, module_type_id, effect_id, target_id) {
            t.set_status_to_success();
            t.schedule_call(0.0, HANDLER_START_MODULE_ON_TARGET, vec![assigned_module_id, my_target_id]);
        }
    }

    fn on_call(&mut self, t: &mut TaskCtx<'_, '_>, handler: u16, args: &[Value]) {
        if handler == HANDLER_START_MODULE_ON_TARGET {
            // `_start_module_on_target(module_id, my_target)`: a tasklet, whose errors are not the task's
            let (Some(module_id), Some(target_id)) =
                (args.first().and_then(Value::as_int), args.get(1).and_then(Value::as_int))
            else {
                return;
            };
            let Some(cycles) = Self::get_cycles(t) else { return };
            let Some(repeats) = t.required("repeats") else { return };
            let repeats = repeats.as_int().unwrap_or(0);
            t.eat_exceptions(|t| {
                try_activate_effect(
                    t,
                    None,
                    repeats,
                    Some(target_id),
                    &Value::None,
                    Some(module_id),
                    cycles,
                    0.0,
                );
            });
        } else {
            crate::utility::dogmatic::on_call(t, handler, args);
        }
    }
}

/// Picks a module of the type that is fitted and not registered yet, and registers it.
#[derive(Debug, Default)]
pub struct FindAndRegisterModule;

impl FindAndRegisterModule {
    /// `_get_fitted_modules`: what is fitted, by its type. (A set of item ids has no order in the client; the ids go up here.)
    fn fitted_modules(t: &mut TaskCtx<'_, '_>) -> Option<BTreeMap<i64, Vec<i64>>> {
        let me = t.my_item_id()?;
        let Some(keys) = t.world().fitted_item_keys(me) else {
            t.raise(BehaviorError::Task(
                "AttributeError: 'NoneType' object has no attribute 'GetFittedItems'".to_string(),
            ));
            return None;
        };
        let mut fitted: BTreeMap<i64, Vec<i64>> = BTreeMap::new();
        for key in keys {
            let module_item_id = match &key {
                Value::Tuple(parts) => parts.first().and_then(Value::as_int),
                other => other.as_int(),
            }?;
            let Some(type_id) = get_type_id_by_item_id(t, module_item_id) else {
                t.raise(BehaviorError::Task(
                    "AttributeError: 'NoneType' object has no attribute 'typeID'".to_string(),
                ));
                return None;
            };
            let modules = fitted.entry(type_id).or_default();
            if !modules.contains(&module_item_id) {
                modules.push(module_item_id);
            }
        }
        for modules in fitted.values_mut() {
            modules.sort_unstable();
        }
        Some(fitted)
    }

    /// `_get_my_module`.
    fn my_module(t: &mut TaskCtx<'_, '_>, registered_modules: &Value) -> Option<Option<i64>> {
        let fitted = Self::fitted_modules(t)?;
        let module_type_id = t.required("moduleTypeId")?.as_int();
        let Some(modules) = module_type_id.and_then(|type_id| fitted.get(&type_id)) else {
            return Some(None);
        };
        for module_id in modules {
            // `_is_module_available`
            if !registered_modules.truthy() || !registered_modules.contains(&Value::Int(*module_id)) {
                return Some(Some(*module_id));
            }
        }
        Some(None)
    }
}

impl Task for FindAndRegisterModule {
    fn type_name(&self) -> &'static str {
        "FindAndRegisterModule"
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        t.set_status_to_success();
        let Some(registered_address) = t.required_address("registeredModulesAddress") else { return };
        let last = t.get_last_blackboard_value(&registered_address);
        let stored = last.truthy();
        let mut registered_modules = if stored { last } else { Value::set(std::iter::empty()) };
        let Some(assigned_module_id) = Self::my_module(t, &registered_modules) else { return };
        let Some(assigned_module_id) = assigned_module_id else {
            t.set_status_to_failed();
            return;
        };
        let Some(module_address) = t.required_address("moduleAddress") else { return };
        t.send_blackboard_value(&module_address, Value::Int(assigned_module_id));
        registered_modules.set_add(Value::Int(assigned_module_id));
        if stored {
            // the set that is there is the one that grows
            let in_place = registered_modules.clone();
            t.with_channel(&registered_address, |channel| {
                if let Some(last) = channel.last_value_mut() {
                    *last = in_place;
                }
            });
        }
        t.send_blackboard_value(&registered_address, registered_modules);
    }
}

enter_task!(
    /// Puts on a blackboard the items of a set that are ships in the park with a module of a type list fitted.
    FilterItemsWithModules,
    |t| {
        t.set_status_to_success();
        let Some(source_address) = t.required_address("sourceItemSetAddress") else { return };
        let source_item_set = t.get_last_blackboard_value(&source_address);
        let mut target_item_set = Value::set(std::iter::empty());
        if source_item_set.truthy() {
            for item in source_item_set.items().unwrap_or(&[]) {
                let Some(item_id) = item.as_int() else { continue };
                if is_ball_in_park(t, item_id) && is_ship(t, item_id) {
                    let Some(type_list_id) = t.required("typeListId") else { return };
                    let Some(type_list_id) = type_list_id.as_int() else { return };
                    if has_fitted_from_type_list(t, item_id, type_list_id) {
                        target_item_set.set_add(item.clone());
                    }
                }
            }
        }
        let Some(address) = t.required_address("targetItemSetAddress") else { return };
        t.send_blackboard_value(&address, target_item_set);
    }
);

#[cfg(test)]
mod tests {
    use super::*;
    use crate::blackboards::{Address, ScopeType};
    use crate::env::{EffectInfo, InventoryItem, SlimItem};
    use crate::tree::tests::Rig;
    use crate::tree::{Attrs, Status};

    fn item(name: &str) -> Value {
        Address::new(ScopeType::Item, name).to_value()
    }

    fn fitted_rig() -> Rig {
        let mut rig = Rig::new();
        for (id, type_id) in [(50, 3001), (51, 3001), (52, 3002)] {
            rig.world.items.insert(id, InventoryItem { item_id: id, type_id, owner_id: 7, location_id: 1 });
        }
        rig.world
            .fitted_keys
            .insert(1, vec![Value::Int(52), Value::tuple([Value::Int(51), Value::Int(5)]), Value::Int(50)]);
        rig
    }

    fn register_attrs() -> Attrs {
        Attrs::new()
            .with("moduleTypeId", 3001i64)
            .with("moduleAddress", item("Module"))
            .with("registeredModulesAddress", item("Registered"))
    }

    #[test]
    fn modules_of_the_type_are_registered_one_by_one_until_there_are_no_more() {
        let mut rig = fitted_rig();
        assert_eq!(rig.run(Box::new(FindAndRegisterModule), register_attrs()), Status::Success);
        assert_eq!(rig.last(ScopeType::Item, "Module"), Value::Int(50));
        assert_eq!(rig.last(ScopeType::Item, "Registered"), Value::set([Value::Int(50)]));
        assert_eq!(rig.run(Box::new(FindAndRegisterModule), register_attrs()), Status::Success);
        assert_eq!(
            rig.last(ScopeType::Item, "Module"),
            Value::Int(51),
            "a fitted item of the tuple form counts"
        );
        assert_eq!(rig.last(ScopeType::Item, "Registered"), Value::set([Value::Int(50), Value::Int(51)]));
        assert_eq!(rig.run(Box::new(FindAndRegisterModule), register_attrs()), Status::Failure);
        assert_eq!(rig.last(ScopeType::Item, "Module"), Value::Int(51));
    }

    #[test]
    fn an_entity_the_dogma_engine_does_not_have_raises() {
        let mut rig = Rig::new();
        rig.run(Box::new(FindAndRegisterModule), register_attrs());
        assert!(matches!(&rig.last_error, Some(BehaviorError::Task(m)) if m.contains("GetFittedItems")));
    }

    fn activate_attrs() -> Attrs {
        Attrs::new()
            .with("moduleAddress", item("Module"))
            .with("targetAddress", item("Target"))
            .with("repeats", 0i64)
    }

    fn activate_rig() -> Rig {
        let mut rig = fitted_rig();
        rig.world.default_effects.insert(3001, 101);
        rig.world.effects.insert(101, EffectInfo::default());
        rig.world.type_effects.insert((3001, 101));
        rig.ctx.my_slim_item =
            Some(SlimItem { item_id: 1, type_id: 700, owner_id: Some(7), ..Default::default() });
        rig.post(ScopeType::Item, "Module", Value::Int(50));
        rig.post(ScopeType::Item, "Target", Value::Int(20));
        rig
    }

    #[test]
    fn a_module_that_can_work_on_the_target_is_started_by_a_tasklet_for_a_number_of_cycles() {
        let mut rig = activate_rig();
        rig.world.set_numbers(&[0.9]);
        let attrs = activate_attrs().with("min_cycles", 2i64).with("max_cycles", 6i64);
        rig.world.effects.insert(101, EffectInfo { duration_attribute_id: Some(73), ..Default::default() });
        rig.world.attributes.insert((50, 73), 4000.0);
        assert_eq!(rig.run(Box::new(ActivateModuleTypeOnTarget), attrs), Status::Success);
        assert_eq!(rig.shared.scheduled_call_count(), 1, "the tasklet");
        assert!(rig.world.take_calls().is_empty());
        rig.deliver_scheduled();
        // 2 + int(0.9 * 4) = 5 cycles of 4 seconds are asked for: the module is stopped after 20 seconds
        assert_eq!(
            rig.world.take_calls(),
            ["activate(7, 50, None, Some(20), 0)"],
            "the engine is given no effect: the default one"
        );
        assert_eq!(rig.shared.scheduled_call_count(), 1);
        assert_eq!(rig.shared.next_call_due(), Some(rig.shared.now + crate::env::seconds_to_ticks(20.0)));
    }

    #[test]
    fn a_module_that_cannot_work_on_the_target_or_no_target_fails() {
        let mut rig = activate_rig();
        rig.world.type_effects.clear();
        assert_eq!(rig.run(Box::new(ActivateModuleTypeOnTarget), activate_attrs()), Status::Failure);
        assert_eq!(rig.shared.scheduled_call_count(), 0);
        let mut rig = fitted_rig();
        rig.post(ScopeType::Item, "Module", Value::Int(50));
        assert_eq!(
            rig.run(Box::new(ActivateModuleTypeOnTarget), activate_attrs()),
            Status::Failure,
            "no target"
        );
    }

    #[test]
    fn with_no_range_of_cycles_the_tasklet_dies_and_nothing_is_started() {
        let mut rig = activate_rig();
        assert_eq!(rig.run(Box::new(ActivateModuleTypeOnTarget), activate_attrs()), Status::Success);
        rig.deliver_scheduled();
        assert!(rig.world.take_calls().is_empty());
        assert_eq!(rig.last_error, None);
    }

    #[test]
    fn the_ships_with_a_module_of_the_type_list_are_put_on_the_blackboard() {
        let mut rig = Rig::new();
        rig.world.add_ball(60, [0.0; 3], 500);
        rig.world.add_ball(61, [0.0; 3], 500);
        rig.world.add_ball(62, [0.0; 3], 500);
        for id in [60, 61, 62] {
            rig.world.slims.get_mut(&id).unwrap().category_id = if id == 62 { 65 } else { 6 };
        }
        rig.world.fitted_modules.insert(60, vec![(500, 3001, 19)]);
        rig.world.fitted_modules.insert(61, vec![(501, 3009, 19)]);
        rig.world.fitted_modules.insert(62, vec![(502, 3001, 19)]);
        rig.world.type_ids_by_list.insert(9, vec![3001]);
        rig.post(
            ScopeType::Item,
            "Source",
            Value::set([Value::Int(60), Value::Int(61), Value::Int(62), Value::Int(63)]),
        );
        let attrs = Attrs::new()
            .with("sourceItemSetAddress", item("Source"))
            .with("targetItemSetAddress", item("Found"))
            .with("typeListId", 9i64);
        assert_eq!(rig.run(Box::new(FilterItemsWithModules), attrs), Status::Success);
        assert_eq!(rig.last(ScopeType::Item, "Found"), Value::set([Value::Int(60)]));
    }
}
