//! `behaviors/utility/dogmatic.py`: what the tasks ask of the dogma engine.
//!
//! The client's `try_activate_effect` and `stop_effect` run in tasklets that sleep; here a sleep is a call the task asks for at a later time
//! (`TaskCtx::schedule_call`), and the task that used them forwards `on_call` to [`on_call`].

use crate::consts::*;
use crate::env::WorldError;
use crate::tree::TaskCtx;
use crate::utility::ballparks::is_ball_in_range;
use crate::utility::inventory::get_type_id_by_item_id;
use crate::utility::owner::get_owner_id;
use crate::value::Value;

/// The handler numbers of the calls [`try_activate_effect`] asks for. A task that uses it forwards these to [`on_call`].
pub const CALL_STOP_EFFECT: u16 = 900;
pub const CALL_ACTIVATE_EFFECT: u16 = 901;

/// `get_my_attribute_value`.
pub fn get_my_attribute_value(t: &mut TaskCtx<'_, '_>, attribute_id: i64) -> Option<f64> {
    let me = t.my_item_id()?;
    get_attribute_value(t, me, attribute_id)
}

/// `get_attribute_value`.
pub fn get_attribute_value(t: &mut TaskCtx<'_, '_>, item_id: i64, attribute_id: i64) -> Option<f64> {
    let value = t.world().attribute_value(item_id, attribute_id);
    t.ok(value)
}

/// `context.mySlimItem.typeID`.
pub fn my_type_id(t: &mut TaskCtx<'_, '_>) -> Option<i64> {
    let slim = t.ctx().my_slim_item.clone();
    if slim.is_none() {
        t.raise(WorldError::Reference.into());
    }
    slim.map(|s| s.type_id)
}

/// `get_my_type_attribute_value`.
pub fn get_my_type_attribute_value(t: &mut TaskCtx<'_, '_>, attribute_id: i64) -> Option<f64> {
    let type_id = my_type_id(t)?;
    Some(get_type_attribute_value(t, type_id, attribute_id))
}

/// `get_type_attribute_value`.
pub fn get_type_attribute_value(t: &mut TaskCtx<'_, '_>, type_id: i64, attribute_id: i64) -> f64 {
    t.world().type_attribute(type_id, attribute_id)
}

/// `type_has_effect`.
pub fn type_has_effect(t: &mut TaskCtx<'_, '_>, type_id: i64, effect_id: i64) -> bool {
    t.world().type_has_effect(type_id, effect_id)
}

/// `self_has_effect`.
pub fn self_has_effect(t: &mut TaskCtx<'_, '_>, effect_id: i64) -> bool {
    match my_type_id(t) {
        Some(type_id) => type_has_effect(t, type_id, effect_id),
        None => false,
    }
}

/// `is_effect_active`.
pub fn is_effect_active(t: &mut TaskCtx<'_, '_>, item_id: i64, effect_id: i64) -> bool {
    t.world().is_effect_active(item_id, effect_id)
}

/// `is_my_effect_active`.
pub fn is_my_effect_active(t: &mut TaskCtx<'_, '_>, effect_id: i64) -> bool {
    match t.my_item_id() {
        Some(me) => is_effect_active(t, me, effect_id),
        None => false,
    }
}

/// `is_my_effect_active_on_target`.
pub fn is_my_effect_active_on_target(
    t: &mut TaskCtx<'_, '_>,
    effect_id: i64,
    target_id: Option<i64>,
) -> bool {
    let Some(me) = t.my_item_id() else { return false };
    match t.world().active_effect_target_id(me, effect_id) {
        None => false,
        Some(target) => target == target_id,
    }
}

/// `get_my_locked_targets`.
pub fn get_my_locked_targets(t: &mut TaskCtx<'_, '_>) -> Vec<i64> {
    match t.my_item_id() {
        Some(me) => t.world().targets(me),
        None => Vec::new(),
    }
}

/// What `try_activate_effect` was asked, kept while it waits for its time.
fn activation_args(
    owner_id: i64,
    module_id: i64,
    effect_id: i64,
    target_id: Option<i64>,
    repeats: i64,
    context: &Value,
) -> Vec<Value> {
    vec![
        Value::Int(owner_id),
        Value::Int(module_id),
        Value::Int(effect_id),
        target_id.map_or(Value::None, Value::Int),
        Value::Int(repeats),
        context.clone(),
    ]
}

/// `try_activate_effect`: activates an effect (of the module, of me when none is given) and, when `cycles` is more than 0, stops it after that many
/// cycles and asks the tree to reset. What the activation raises (`UserError`, `ReferenceError`, `RuntimeError`) is not an error of the task.
/// `delay` seconds pass before the activation.
#[allow(clippy::too_many_arguments)]
pub fn try_activate_effect(
    t: &mut TaskCtx<'_, '_>,
    effect_id: Option<i64>,
    repeats: i64,
    target_id: Option<i64>,
    context: &Value,
    module_id: Option<i64>,
    cycles: i64,
    delay: f64,
) {
    let module_id = match module_id.filter(|m| *m != 0) {
        Some(m) => m,
        None => match t.my_item_id() {
            Some(me) => me,
            None => return,
        },
    };
    let Some(owner_id) = get_owner_id(t) else { return };
    if cycles > 0 {
        start_stop_effect(t, effect_id, module_id, cycles);
        if t.faulted() {
            return;
        }
    }
    if delay > 0.0 {
        // the reference's tasklet sleeps here, and activates when it wakes
        let Some(effect_id) = effect_id else { return };
        t.schedule_call(
            delay,
            CALL_ACTIVATE_EFFECT,
            activation_args(owner_id, module_id, effect_id, target_id, repeats, context),
        );
        return;
    }
    let Some(effect_id) = effect_id else { return };
    activate_now(t, owner_id, module_id, effect_id, target_id, repeats, context);
}

fn activate_now(
    t: &mut TaskCtx<'_, '_>,
    owner_id: i64,
    module_id: i64,
    effect_id: i64,
    target_id: Option<i64>,
    repeats: i64,
    context: &Value,
) {
    match t.world().activate_with_context(owner_id, module_id, effect_id, target_id, repeats, context) {
        Ok(()) | Err(WorldError::User(_) | WorldError::Reference | WorldError::Runtime(_)) => {}
        Err(other) => t.raise(other.into()),
    }
}

/// `stop_effect`, the part that runs at once: how long the module runs for `cycles` cycles, and the call to stop it then.
fn start_stop_effect(t: &mut TaskCtx<'_, '_>, effect_id: Option<i64>, module_id: i64, cycles: i64) {
    let effect_id = match effect_id {
        Some(effect) => effect,
        None => {
            let Some(module_type_id) = get_type_id_by_item_id(t, module_id) else { return };
            match t.world().default_effect_id(module_type_id) {
                Some(effect) => effect,
                None => {
                    t.raise(WorldError::Key(format!("no default effect for type {module_type_id}")).into());
                    return;
                }
            }
        }
    };
    let Some(effect) = t.world().effect_info(effect_id) else {
        t.raise(WorldError::Key(format!("no effect {effect_id}")).into());
        return;
    };
    let Some(duration_attribute_id) = effect.duration_attribute_id else {
        t.raise(WorldError::Runtime(format!("effect {effect_id} has no duration attribute")).into());
        return;
    };
    let Some(single_cycle_duration) = get_attribute_value(t, module_id, duration_attribute_id) else {
        return;
    };
    let seconds_until_stop = single_cycle_duration * cycles as f64 / 1000.0;
    t.schedule_call(seconds_until_stop, CALL_STOP_EFFECT, vec![Value::Int(effect_id), Value::Int(module_id)]);
}

/// What a task does when a call it asked for through `try_activate_effect` comes; false for a call that is not one of these.
pub fn on_call(t: &mut TaskCtx<'_, '_>, handler: u16, args: &[Value]) -> bool {
    match handler {
        CALL_STOP_EFFECT => {
            let (Some(effect_id), Some(module_id)) =
                (args.first().and_then(Value::as_int), args.get(1).and_then(Value::as_int))
            else {
                return true;
            };
            match t.world().stop_effect_force_repeating(effect_id, module_id) {
                Ok(()) | Err(WorldError::User(_)) => {}
                Err(other) => {
                    t.raise(other.into());
                    return true;
                }
            }
            t.request_reset();
            true
        }
        CALL_ACTIVATE_EFFECT => {
            let ints: Vec<Option<i64>> = args.iter().map(Value::as_int).collect();
            if let (Some(owner_id), Some(module_id), Some(effect_id), Some(repeats), Some(context)) = (
                ints.first().copied().flatten(),
                ints.get(1).copied().flatten(),
                ints.get(2).copied().flatten(),
                ints.get(4).copied().flatten(),
                args.get(5),
            ) {
                let target = ints.get(3).copied().flatten();
                activate_now(t, owner_id, module_id, effect_id, target, repeats, context);
            }
            true
        }
        _ => false,
    }
}

/// `is_assistance_disallowed`.
pub fn is_assistance_disallowed(t: &mut TaskCtx<'_, '_>, item_id: i64) -> bool {
    get_attribute_value(t, item_id, ATTRIBUTE_DISALLOW_ASSISTANCE).is_some_and(|value| value > 0.0)
}

/// `get_remote_repair_impedance`.
pub fn get_remote_repair_impedance(t: &mut TaskCtx<'_, '_>, item_id: i64) -> Option<f64> {
    get_attribute_value(t, item_id, ATTRIBUTE_REMOTE_REPAIR_IMPEDANCE)
}

/// `get_range_falloff_multiplier_at_distance`.
pub fn get_range_falloff_multiplier_at_distance(
    t: &mut TaskCtx<'_, '_>,
    effect_id: i64,
    distance: f64,
) -> f64 {
    match t.my_item_id() {
        Some(source_id) => t.world().range_falloff_modifier_at_distance(effect_id, source_id, distance),
        None => 1.0,
    }
}

/// `get_default_effect_id_for_type`.
pub fn get_default_effect_id_for_type(t: &mut TaskCtx<'_, '_>, type_id: i64) -> Option<i64> {
    t.world().default_effect_id(type_id)
}

/// `can_activate_effect_on_target`: an effect that has a range and no falloff needs the target within it (the attack effect excepted).
pub fn can_activate_effect_on_target(
    t: &mut TaskCtx<'_, '_>,
    type_id: i64,
    effect_id: i64,
    target_id: i64,
) -> bool {
    let Some(effect) = t.world().effect_info(effect_id) else { return false };
    if !t.world().type_has_effect(type_id, effect_id) {
        return false;
    }
    if effect.falloff_attribute_id.is_none_or(|a| a == 0)
        && effect.range_attribute_id.is_some()
        && effect_id != EFFECT_TARGET_ABC_ATTACK
    {
        let Some(range_attribute) = effect.range_attribute_id else { return true };
        let Some(effect_range) = get_my_attribute_value(t, range_attribute) else { return false };
        let Some(me) = t.my_item_id() else { return false };
        return is_ball_in_range(t, me, target_id, effect_range);
    }
    true
}

/// `get_entity_max_missile_range`.
pub fn get_entity_max_missile_range(t: &mut TaskCtx<'_, '_>, type_id: i64) -> f64 {
    let missile_type_id = get_type_attribute_value(t, type_id, ATTRIBUTE_ENTITY_MISSILE_TYPE_ID) as i64;
    let missile_base_velocity = get_type_attribute_value(t, missile_type_id, ATTRIBUTE_MAX_VELOCITY);
    let missile_explosion_delay = get_type_attribute_value(t, missile_type_id, ATTRIBUTE_EXPLOSION_DELAY);
    let velocity_multiplier =
        get_type_attribute_value(t, type_id, ATTRIBUTE_MISSILE_ENTITY_VELOCITY_MULTIPLIER);
    let flight_time_multiplier =
        get_type_attribute_value(t, type_id, ATTRIBUTE_MISSILE_ENTITY_FLIGHT_TIME_MULTIPLIER);
    let missile_velocity = missile_base_velocity * flight_time_multiplier;
    let flight_time = missile_explosion_delay * velocity_multiplier * SECONDS_IN_MILLISECOND;
    missile_velocity * flight_time
}

/// `is_module_fitted_from_group_id_set`.
pub fn is_module_fitted_from_group_id_set(
    t: &mut TaskCtx<'_, '_>,
    item_id: i64,
    group_id_set: &[i64],
) -> bool {
    let fitted = t.world().fitted_module_group_ids(item_id);
    group_id_set.iter().any(|group| fitted.contains(group))
}

/// `try_stop_effect`.
pub fn try_stop_effect(t: &mut TaskCtx<'_, '_>, effect_id: i64, forced: bool) {
    let Some(me) = t.my_item_id() else { return };
    match t.world().stop_effect(effect_id, me, forced) {
        Ok(()) | Err(WorldError::User(_)) => {}
        Err(other) => t.raise(other.into()),
    }
}

/// `remove_my_target`.
pub fn remove_my_target(t: &mut TaskCtx<'_, '_>, target_id: i64) {
    if let Some(me) = t.my_item_id() {
        t.world().remove_target(me, target_id);
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::env::{EffectInfo, SlimItem};
    use crate::tree::tests::with_task;

    #[test]
    fn my_type_and_effects_come_from_the_slim_item_the_behavior_started_with() {
        let (_, answer) = with_task(
            |rig| {
                rig.ctx.my_slim_item = Some(SlimItem { item_id: 1, type_id: 500, ..Default::default() });
                rig.world.type_effects.insert((500, 77));
                rig.world.active_effects.insert((1, 78));
                rig.world.type_attributes.insert((500, 9), 4.5);
            },
            |t| {
                (
                    self_has_effect(t, 77),
                    self_has_effect(t, 78),
                    is_my_effect_active(t, 78),
                    is_my_effect_active(t, 77),
                    get_my_type_attribute_value(t, 9),
                )
            },
        );
        assert_eq!(answer, Some((true, false, true, false, Some(4.5))));
    }

    #[test]
    fn an_effect_active_on_a_target_names_it_and_one_that_is_not_active_names_nothing() {
        let (_, answer) = with_task(
            |rig| {
                rig.world.active_effect_targets.insert((1, 5), Some(20));
                rig.world.active_effect_targets.insert((1, 6), None);
            },
            |t| {
                (
                    is_my_effect_active_on_target(t, 5, Some(20)),
                    is_my_effect_active_on_target(t, 5, Some(21)),
                    is_my_effect_active_on_target(t, 6, None),
                    is_my_effect_active_on_target(t, 7, None),
                )
            },
        );
        assert_eq!(answer, Some((true, false, true, false)));
    }

    #[test]
    fn an_effect_with_a_range_and_no_falloff_needs_the_target_in_range() {
        let (_, answer) = with_task(
            |rig| {
                rig.world
                    .effects
                    .insert(10, EffectInfo { range_attribute_id: Some(54), ..Default::default() });
                rig.world.effects.insert(
                    11,
                    EffectInfo {
                        range_attribute_id: Some(54),
                        falloff_attribute_id: Some(158),
                        ..Default::default()
                    },
                );
                rig.world.effects.insert(
                    EFFECT_TARGET_ABC_ATTACK,
                    EffectInfo { range_attribute_id: Some(54), ..Default::default() },
                );
                for effect in [10, 11, EFFECT_TARGET_ABC_ATTACK] {
                    rig.world.type_effects.insert((500, effect));
                }
                rig.world.attributes.insert((1, 54), 1000.0);
                rig.world.set_dist(1, 20, 1500.0);
            },
            |t| {
                (
                    can_activate_effect_on_target(t, 500, 10, 20),
                    can_activate_effect_on_target(t, 500, 11, 20),
                    can_activate_effect_on_target(t, 500, EFFECT_TARGET_ABC_ATTACK, 20),
                    can_activate_effect_on_target(t, 500, 12, 20),
                    can_activate_effect_on_target(t, 501, 10, 20),
                )
            },
        );
        assert_eq!(answer, Some((false, true, true, false, false)));
    }

    #[test]
    fn an_activation_that_raises_a_user_error_is_not_an_error_of_the_task_and_a_key_error_is() {
        let (rig, _) = with_task(
            |rig| {
                rig.world.activation_error = Some(WorldError::User("no".into()));
                rig.world.add_ball(1, [0.0; 3], 1);
                rig.ctx.my_entity_group_id = None;
                rig.ctx.my_slim_item = Some(SlimItem { owner_id: Some(5), ..Default::default() });
            },
            |t| try_activate_effect(t, Some(3), 1, Some(9), &Value::None, None, 0, 0.0),
        );
        assert_eq!(rig.last_error, None);
    }

    #[test]
    fn an_activation_is_asked_of_the_module_with_its_repeats_and_a_stop_is_asked_for_after_the_cycles() {
        let (mut rig, _) = with_task(
            |rig| {
                rig.ctx.my_slim_item = Some(SlimItem { owner_id: Some(5), ..Default::default() });
                rig.world
                    .effects
                    .insert(3, EffectInfo { duration_attribute_id: Some(73), ..Default::default() });
                rig.world.attributes.insert((30, 73), 2000.0);
            },
            |t| try_activate_effect(t, Some(3), 1, Some(9), &Value::None, Some(30), 3, 0.0),
        );
        assert_eq!(rig.world.take_calls(), ["activate(5, 30, 3, Some(9), 1)"]);
        assert_eq!(rig.shared.scheduled_call_count(), 1);
        rig.shared.now += 6 * SEC;
        rig.shared.fire_due_calls();
        let deliveries = std::mem::take(&mut rig.shared.pending);
        assert_eq!(deliveries.len(), 1, "3 cycles of 2 seconds");
    }
}
