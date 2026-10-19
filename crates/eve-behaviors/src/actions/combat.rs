//! `behaviors/actions/combat.py`: firing at the target, kamikaze fighters, super weapons, podding, and the effects a role fights with.

use crate::constants::behaviorroles::combat_effects_by_role;
use crate::consts::{
    ATTRIBUTE_EM_DAMAGE, ATTRIBUTE_EXPLOSIVE_DAMAGE, ATTRIBUTE_FIGHTER_ABILITY_KAMIKAZE_DAMAGE_EM,
    ATTRIBUTE_FIGHTER_ABILITY_KAMIKAZE_DAMAGE_EXP, ATTRIBUTE_FIGHTER_ABILITY_KAMIKAZE_DAMAGE_KIN,
    ATTRIBUTE_FIGHTER_ABILITY_KAMIKAZE_RANGE, ATTRIBUTE_FIGHTER_ABILITY_KAMIKAZE_SIGNATURE_RADIUS,
    ATTRIBUTE_FIGHTER_SQUADRON_SIZE, ATTRIBUTE_KINETIC_DAMAGE, ATTRIBUTE_THERMAL_DAMAGE,
    DESTINY_MODE_MISSILE, EFFECT_ENTITY_SUPER_WEAPON, EFFECT_ENTITY_SUPER_WEAPON_LANCE_ALL_RACES,
};
use crate::env::{InventoryItem, WorldError};
use crate::events::message;
use crate::tree::{BehaviorError, Task, TaskCtx};
use crate::utility::ballparks::get_my_ball;
use crate::utility::dogmatic::{
    can_activate_effect_on_target, get_attribute_value, is_my_effect_active_on_target, try_activate_effect,
    try_stop_effect, type_has_effect,
};
use crate::value::Value;

fn attribute_error_on_item(t: &mut TaskCtx<'_, '_>, what: &str) {
    t.raise(BehaviorError::Task(format!("AttributeError: 'NoneType' object has no attribute '{what}'")));
}

/// `self.context.ballpark.inventory2.GetItem(self.context.myItemId)`.
fn get_self_as_item(t: &mut TaskCtx<'_, '_>) -> Option<InventoryItem> {
    let me = t.my_item_id()?;
    t.world().inventory_item(me)
}

const HANDLER_SUPER_WEAPON_STOPPED: u16 = 0;

/// Fires the super weapon of the entity at the selected target and waits for its cycle to stop.
#[derive(Debug, Default)]
pub struct ActivateSuperWeaponOnTarget;

impl ActivateSuperWeaponOnTarget {
    /// `TryActivateEffect`: what the activation raises of `UserError` and `RuntimeError` is only logged.
    fn try_activate_effect(t: &mut TaskCtx<'_, '_>, effect_id: i64, owner_id: i64, target_id: &Value) {
        let Some(me) = t.my_item_id() else { return };
        t.subscribe_item(me, message::ENTITY_SUPER_WEAPON_STOPPED, HANDLER_SUPER_WEAPON_STOPPED);
        let result = t.world().activate_with_context(
            owner_id,
            me,
            Some(effect_id),
            target_id.as_int(),
            0,
            &Value::None,
        );
        match result {
            Ok(()) | Err(WorldError::User(_) | WorldError::Runtime(_)) => {}
            Err(other) => t.raise(other.into()),
        }
    }
}

impl Task for ActivateSuperWeaponOnTarget {
    fn type_name(&self) -> &'static str {
        "ActivateSuperWeaponOnTarget"
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        let Some(address) = t.required_address("selectedTargetAddress") else { return };
        let selected_target = t.get_last_blackboard_value(&address);
        t.set_status_to_failed();
        let Some(item) = get_self_as_item(t) else {
            attribute_error_on_item(t, "typeID");
            return;
        };
        if !selected_target.truthy() {
            return;
        }
        for effect_id in [EFFECT_ENTITY_SUPER_WEAPON, EFFECT_ENTITY_SUPER_WEAPON_LANCE_ALL_RACES] {
            if type_has_effect(t, item.type_id, effect_id) {
                t.set_status_to_suspended();
                Self::try_activate_effect(t, effect_id, item.owner_id, &selected_target);
            }
        }
    }

    fn on_event(&mut self, t: &mut TaskCtx<'_, '_>, _handler: u16, _args: &[Value]) {
        // OnSuperWeaponStopped
        if !t.is_invalid() {
            t.set_status_to_success();
            t.on_status_updated();
        }
    }

    fn clean_up(&mut self, t: &mut TaskCtx<'_, '_>) {
        if !t.is_invalid() {
            if let Some(me) = t.my_item_id() {
                t.unsubscribe_item(me, message::ENTITY_SUPER_WEAPON_STOPPED, HANDLER_SUPER_WEAPON_STOPPED);
            }
            t.set_status_to_invalid();
        }
    }
}

enter_task!(
    /// Puts the character on the list of those to pod.
    AddCharacterForPodding,
    |t| {
        let Some(character_address) = t.required_address("characterAddress") else { return };
        let character_id = t.get_last_blackboard_value(&character_address);
        if character_id.truthy() {
            // `UpdatePoddingList`
            let Some(address) = t.required_address("poddingTargetListAddress") else { return };
            let last = t.get_last_blackboard_value(&address);
            let stored = !last.is_none();
            let mut podding_list = if stored { last } else { Value::set(std::iter::empty()) };
            if !podding_list.contains(&character_id) {
                podding_list.set_add(character_id);
                if stored {
                    let in_place = podding_list.clone();
                    t.with_channel(&address, |channel| {
                        if let Some(last) = channel.last_value_mut() {
                            *last = in_place;
                        }
                    });
                }
                t.send_blackboard_value(&address, podding_list);
            }
        }
        t.set_status_to_success();
    }
);

const EXPLOSION_DELAY_SECONDS: f64 = 1.0;
const HANDLER_EXPLODE_SQUADRON: u16 = 0;
const HANDLER_DO_TARGET_TRACKING: u16 = 1;
const HANDLER_DO_TARGET_TRACKING_THREADED: u16 = 2;
const HANDLER_DO_COLLISION: u16 = 3;

/// A squadron of fighters that flies into its target and explodes.
#[derive(Debug, Default)]
pub struct KamikazeAttack {
    explosion_scheduled: bool,
}

impl KamikazeAttack {
    /// `_ScheduleExplosion`: the explosion comes after a moment, once.
    fn schedule_explosion(&mut self, t: &mut TaskCtx<'_, '_>, target_id: &Value) {
        if !self.explosion_scheduled {
            self.explosion_scheduled = true;
            t.schedule_call(EXPLOSION_DELAY_SECONDS, HANDLER_EXPLODE_SQUADRON, vec![target_id.clone()]);
        }
    }

    /// `DoTargetTracking`: a tasklet asks the ballpark to make the fighter follow its target as a missile.
    fn do_target_tracking(t: &mut TaskCtx<'_, '_>, args: Vec<Value>) {
        t.schedule_call(0.0, HANDLER_DO_TARGET_TRACKING_THREADED, args);
    }

    /// `_GetTargetBall`: the ball of the selected target, none when it is gone.
    fn get_target_ball(t: &mut TaskCtx<'_, '_>) -> Option<crate::env::Ball> {
        let address = t.required_address("selectedTargetAddress")?;
        let target_id = t.get_last_blackboard_value(&address);
        target_id.as_int().and_then(|id| t.world().ball(id))
    }

    /// `EngageTarget`.
    fn engage_target(&mut self, t: &mut TaskCtx<'_, '_>, target_id: i64) {
        let Some(my_ball) = get_my_ball(t) else { return };
        let Some(me) = t.my_item_id() else { return };
        let Some(notify_range) = get_attribute_value(t, me, ATTRIBUTE_FIGHTER_ABILITY_KAMIKAZE_RANGE) else {
            return;
        };
        let dist_between_balls = match t.world().surface_dist(my_ball.id, target_id) {
            Ok(dist) => dist,
            Err(error) => {
                t.raise(error.into());
                return;
            }
        };
        if dist_between_balls <= 0.0 {
            self.schedule_explosion(t, &Value::Int(target_id));
        }
        // the ball is looked at again: the explosion above may have changed nothing, but the client reads `myBall.mode` afresh
        let Some(my_ball_now) = get_my_ball(t) else { return };
        if my_ball_now.mode == DESTINY_MODE_MISSILE {
            t.set_status_to_running();
            return;
        }
        if dist_between_balls <= notify_range {
            Self::do_target_tracking(t, Vec::new());
            t.set_status_to_running();
            return;
        }
        t.world().approach_object(my_ball.id, target_id, -999_999.0, false);
        let tracking = t.observer(HANDLER_DO_TARGET_TRACKING);
        let collision = t.observer(HANDLER_DO_COLLISION);
        t.world().set_ball_callbacks(my_ball_now.id, Some(tracking), Some(collision));
        t.world().set_target_tracking(me, target_id, notify_range);
        t.set_status_to_running();
    }

    /// `Abort`.
    fn abort(&mut self, t: &mut TaskCtx<'_, '_>) {
        let Some(my_ball) = get_my_ball(t) else { return };
        let Some(should_abort_address) = t.required_address("shouldAbortAddress") else { return };
        if my_ball.mode == DESTINY_MODE_MISSILE {
            t.send_blackboard_value(&should_abort_address, Value::Bool(false));
            if Self::get_target_ball(t).is_none() {
                t.world().explode_ex(my_ball.id);
            }
            return;
        }
        // the functions that do nothing take the ball's places
        t.world().set_ball_callbacks(my_ball.id, None, None);
        if let Some(me) = t.my_item_id() {
            t.world().stop_ex(me);
        }
        t.send_blackboard_value(&should_abort_address, Value::Bool(false));
        t.set_status_to_success();
    }

    fn update_task(&mut self, t: &mut TaskCtx<'_, '_>) {
        let Some(should_abort_address) = t.required_address("shouldAbortAddress") else { return };
        let should_abort = t.get_last_blackboard_value(&should_abort_address);
        let target_ball = Self::get_target_ball(t);
        match target_ball {
            Some(ball) if !should_abort.truthy() => self.engage_target(t, ball.id),
            _ => self.abort(t),
        }
    }

    /// `_ApplyDamageToTarget(fighterID)`.
    fn apply_damage_to_target(t: &mut TaskCtx<'_, '_>, fighter_id: i64) {
        let Some(target_ball) = Self::get_target_ball(t) else { return };
        let Some(me) = t.my_item_id() else { return };
        let Some(squadron_size_multiplier) = get_attribute_value(t, me, ATTRIBUTE_FIGHTER_SQUADRON_SIZE)
        else {
            return;
        };
        let Some(cloud_size) =
            get_attribute_value(t, fighter_id, ATTRIBUTE_FIGHTER_ABILITY_KAMIKAZE_SIGNATURE_RADIUS)
        else {
            return;
        };
        let cloud_factor = t.world().cloud_factor(target_ball.id, cloud_size);
        let damage_amount = squadron_size_multiplier * cloud_factor;
        let mut damage_by_type = Vec::new();
        for (attribute, from) in [
            (ATTRIBUTE_EM_DAMAGE, ATTRIBUTE_FIGHTER_ABILITY_KAMIKAZE_DAMAGE_EM),
            (ATTRIBUTE_EXPLOSIVE_DAMAGE, ATTRIBUTE_FIGHTER_ABILITY_KAMIKAZE_DAMAGE_EXP),
            (ATTRIBUTE_KINETIC_DAMAGE, ATTRIBUTE_FIGHTER_ABILITY_KAMIKAZE_DAMAGE_KIN),
            // the client reads the thermal damage of the fighter itself
            (ATTRIBUTE_THERMAL_DAMAGE, ATTRIBUTE_THERMAL_DAMAGE),
        ] {
            let Some(amount) = get_attribute_value(t, fighter_id, from) else { return };
            damage_by_type.push((attribute, amount));
        }
        t.world().apply_damage(me, target_ball.id, damage_amount, fighter_id, &damage_by_type);
    }
}

impl Task for KamikazeAttack {
    fn type_name(&self) -> &'static str {
        "KamikazeAttack"
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        if get_self_as_item(t).is_none() {
            t.set_status_to_failed();
            return;
        }
        self.update_task(t);
    }

    fn update(&mut self, t: &mut TaskCtx<'_, '_>) {
        self.update_task(t);
    }

    fn on_call(&mut self, t: &mut TaskCtx<'_, '_>, handler: u16, args: &[Value]) {
        match handler {
            HANDLER_EXPLODE_SQUADRON => {
                // `_ExplodeSquadron(targetID)`
                let Some(fighter_id) = t.my_item_id() else { return };
                Self::apply_damage_to_target(t, fighter_id);
                if t.faulted() {
                    return;
                }
                if let Some(ball_id) = t.my_item_id() {
                    t.world().explode_ex(ball_id);
                }
                t.set_status_to_success();
            }
            HANDLER_DO_TARGET_TRACKING => Self::do_target_tracking(t, args.to_vec()),
            HANDLER_DO_TARGET_TRACKING_THREADED => {
                // `_DoTargetTracking_Threaded`
                let Some(address) = t.required_address("selectedTargetAddress") else { return };
                let target_id = t.get_last_blackboard_value(&address);
                let Some(target_id) = target_id.as_int() else { return };
                let Some(fighter_id) = t.my_item_id() else { return };
                let controller_id = t.world().controller_for_fighter(fighter_id).unwrap_or(0);
                t.world().missile_follow(fighter_id, target_id, controller_id);
                t.world().set_ball_massive(fighter_id, false);
            }
            HANDLER_DO_COLLISION => {
                // `DoCollision(otherID, otherX, otherY, otherZ)`
                let Some(address) = t.required_address("selectedTargetAddress") else { return };
                let target_id = t.get_last_blackboard_value(&address);
                if args.first().is_some_and(|other| other.py_eq(&target_id)) {
                    self.schedule_explosion(t, &target_id);
                }
            }
            _ => {}
        }
    }
}

/// Starts the combat effects on the selected target, after the reaction time.
#[derive(Debug, Default)]
pub struct ActivateCombatEffectsOnTarget;

impl ActivateCombatEffectsOnTarget {
    /// `_GetReactionDelay`: up to `reactionDelayMilliseconds`, else up to a second.
    fn get_reaction_delay(t: &mut TaskCtx<'_, '_>) -> Option<f64> {
        if t.has_attribute("reactionDelayMilliseconds") {
            let milliseconds = t.attribute("reactionDelayMilliseconds").as_float()?;
            return Some(t.world().random_uniform(0.0, milliseconds / 1000.0));
        }
        Some(t.world().random())
    }

    /// `_ActivateCombatEffectsOnTarget`.
    fn activate_combat_effects_on_target(t: &mut TaskCtx<'_, '_>, target_id: i64, self_item: &InventoryItem) {
        let Some(reaction_delay) = Self::get_reaction_delay(t) else { return };
        let Some(effect_ids) = t.required("effectIds") else { return };
        for effect_id in effect_ids.int_items() {
            if is_my_effect_active_on_target(t, effect_id, Some(target_id)) {
                t.set_status_to_success();
                continue;
            }
            if can_activate_effect_on_target(t, self_item.type_id, effect_id, target_id) {
                // `_TryActiveEffect`: a tasklet that activates it after the delay; what it raises is not this task's
                let Some(repeat) = t.required("repeat") else { return };
                let repeats = repeat.as_int().unwrap_or(0);
                t.eat_exceptions(|t| {
                    try_activate_effect(
                        t,
                        Some(effect_id),
                        repeats,
                        Some(target_id),
                        &Value::None,
                        None,
                        0,
                        reaction_delay,
                    );
                });
                t.set_status_to_success();
            }
        }
    }
}

impl Task for ActivateCombatEffectsOnTarget {
    fn type_name(&self) -> &'static str {
        "ActivateCombatEffectsOnTarget"
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        t.set_status_to_failed();
        let self_item = get_self_as_item(t);
        let Some(address) = t.required_address("selectedTargetAddress") else { return };
        let target = t.get_last_blackboard_value(&address);
        let Some(target_id) = target.as_int() else { return };
        let target_item = t.world().inventory_item(target_id);
        let (Some(self_item), Some(_)) = (self_item, target_item) else { return };
        Self::activate_combat_effects_on_target(t, target_id, &self_item);
    }

    fn on_call(&mut self, t: &mut TaskCtx<'_, '_>, handler: u16, args: &[Value]) {
        crate::utility::dogmatic::on_call(t, handler, args);
    }
}

/// Stops the combat effects of the entity.
#[derive(Debug, Default)]
pub struct StopCombatEffectsOnTarget;

impl Task for StopCombatEffectsOnTarget {
    fn type_name(&self) -> &'static str {
        "StopCombatEffectsOnTarget"
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        t.set_status_to_success();
        let Some(item) = get_self_as_item(t) else {
            attribute_error_on_item(t, "typeID");
            return;
        };
        let Some(effect_ids) = t.required("effectIds") else { return };
        for effect_id in effect_ids.int_items() {
            if type_has_effect(t, item.type_id, effect_id) {
                try_stop_effect(t, effect_id, true);
            }
        }
    }
}

#[derive(Debug, Default)]
pub struct OwnerListFilterAction;

impl Task for OwnerListFilterAction {
    fn type_name(&self) -> &'static str {
        "OwnerListFilterAction"
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        t.set_status_to_success();
        let Some(address) = t.required_address("itemIdSetAddress") else { return };
        let item_id_set = t.get_last_blackboard_value(&address);
        if !item_id_set.truthy() {
            return;
        }
        let Some(owner_id_set) = t.required("ownerIdSet") else { return };
        // `FilterSet`
        let mut filtered = item_id_set.clone();
        for item in item_id_set.items().unwrap_or(&[]) {
            let owned = item.as_int().and_then(|id| t.world().slim_item(id)).is_some_and(|slim| {
                slim.owner_id.is_some_and(|owner| owner_id_set.contains(&Value::Int(owner)))
            });
            if owned {
                filtered.set_discard(item);
            }
        }
        if !filtered.py_eq(&item_id_set) {
            t.send_blackboard_value(&address, filtered);
        }
    }
}

/// Puts on a blackboard the combat effects of the role that the type of the entity has.
#[derive(Debug, Default)]
pub struct GetCombatEffectsForRole;

impl Task for GetCombatEffectsForRole {
    fn type_name(&self) -> &'static str {
        "GetCombatEffectsForRole"
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        t.set_status_to_success();
        let Some(effects_address) = t.required_address("combatEffectsAddress") else { return };
        if !t.get_last_blackboard_value(&effects_address).is_none() {
            return;
        }
        let Some(role_address) = t.required_address("roleAddress") else { return };
        let role = t.get_last_blackboard_value(&role_address);
        // `_GetCombatEffectsOnTypeForRole`
        let by_role = role.as_str().and_then(combat_effects_by_role).unwrap_or(&[]);
        let Some(slim) = t.ctx().my_slim_item.clone() else {
            attribute_error_on_item(t, "typeID");
            return;
        };
        let mut actual = Vec::new();
        for effect_id in by_role {
            if type_has_effect(t, slim.type_id, *effect_id) {
                actual.push(Value::Int(*effect_id));
            }
        }
        t.send_blackboard_value(&effects_address, Value::List(actual));
    }
}

/// The address of a name, for the tests.
#[cfg(test)]
fn address_value(scope: crate::blackboards::ScopeType, name: &str) -> Value {
    crate::blackboards::Address::new(scope, name).to_value()
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::blackboards::ScopeType;
    use crate::env::{EffectInfo, SlimItem};
    use crate::tree::tests::Rig;
    use crate::tree::{Attrs, Status};

    fn item(name: &str) -> Value {
        address_value(ScopeType::Item, name)
    }

    fn rig() -> Rig {
        let mut rig = Rig::new();
        rig.ctx.my_slim_item =
            Some(SlimItem { item_id: 1, type_id: 700, owner_id: Some(7), ..Default::default() });
        rig.world
            .items
            .insert(1, InventoryItem { item_id: 1, type_id: 700, owner_id: 7, location_id: 30000142 });
        rig.world
            .items
            .insert(20, InventoryItem { item_id: 20, type_id: 600, owner_id: 8, location_id: 30000142 });
        rig
    }

    #[test]
    fn a_super_weapon_is_fired_at_the_target_and_the_task_ends_when_the_cycle_stops() {
        let mut rig = rig();
        rig.world.type_effects.insert((700, EFFECT_ENTITY_SUPER_WEAPON));
        rig.post(ScopeType::Item, "Target", Value::Int(20));
        let attrs = Attrs::new().with("selectedTargetAddress", item("Target"));
        assert_eq!(rig.run(Box::new(ActivateSuperWeaponOnTarget), attrs.clone()), Status::Suspended);
        assert_eq!(
            rig.world.take_calls(),
            [format!("activate(7, 1, {EFFECT_ENTITY_SUPER_WEAPON}, Some(20), 0)")]
        );
        rig.send_event(1, message::ENTITY_SUPER_WEAPON_STOPPED, &[]);
        assert_eq!(rig.tree.status_of(rig.tree.root_task().unwrap()), Status::Success);
    }

    #[test]
    fn a_super_weapon_that_is_not_there_or_a_target_that_is_not_selected_fails() {
        let mut rig = rig();
        rig.post(ScopeType::Item, "Target", Value::Int(20));
        let attrs = Attrs::new().with("selectedTargetAddress", item("Target"));
        assert_eq!(rig.run(Box::new(ActivateSuperWeaponOnTarget), attrs.clone()), Status::Failure);
        let mut rig = self::rig();
        rig.world.type_effects.insert((700, EFFECT_ENTITY_SUPER_WEAPON));
        assert_eq!(rig.run(Box::new(ActivateSuperWeaponOnTarget), attrs), Status::Failure, "no target");
    }

    #[test]
    fn a_character_is_put_on_the_podding_list_once() {
        let attrs =
            Attrs::new().with("characterAddress", item("Char")).with("poddingTargetListAddress", item("Pod"));
        let mut rig = rig();
        rig.post(ScopeType::Item, "Char", Value::Int(90000001));
        assert_eq!(rig.run(Box::new(AddCharacterForPodding), attrs.clone()), Status::Success);
        assert_eq!(rig.last(ScopeType::Item, "Pod"), Value::set([Value::Int(90000001)]));
        assert_eq!(rig.messages_on("Pod"), 1);
        assert_eq!(rig.run(Box::new(AddCharacterForPodding), attrs), Status::Success);
        assert_eq!(rig.last(ScopeType::Item, "Pod"), Value::set([Value::Int(90000001)]));
        assert_eq!(rig.messages_on("Pod"), 1, "it was there already");
        // no character: nothing is put, and the task succeeds
        let mut rig = self::rig();
        let attrs =
            Attrs::new().with("characterAddress", item("Char")).with("poddingTargetListAddress", item("Pod"));
        assert_eq!(rig.run(Box::new(AddCharacterForPodding), attrs), Status::Success);
        assert_eq!(rig.last(ScopeType::Item, "Pod"), Value::None);
    }

    fn effect(range_attribute: Option<i64>) -> EffectInfo {
        EffectInfo { range_attribute_id: range_attribute, ..Default::default() }
    }

    #[test]
    fn combat_effects_start_after_a_reaction_delay_and_the_task_succeeds_for_those_that_are_on_or_can_be() {
        let mut rig = rig();
        rig.world.type_effects.insert((700, 101));
        rig.world.effects.insert(101, effect(None));
        rig.world.effects.insert(102, effect(None));
        rig.world.active_effect_targets.insert((1, 102), Some(20));
        rig.world.type_effects.insert((700, 102));
        rig.post(ScopeType::Item, "Target", Value::Int(20));
        rig.world.set_numbers(&[0.5]);
        let attrs = Attrs::new()
            .with("selectedTargetAddress", item("Target"))
            .with("effectIds", Value::List(vec![Value::Int(101), Value::Int(102), Value::Int(103)]))
            .with("repeat", 0i64)
            .with("reactionDelayMilliseconds", 2000i64);
        assert_eq!(rig.run(Box::new(ActivateCombatEffectsOnTarget), attrs), Status::Success);
        assert_eq!(rig.last_error, None);
        // 101 is scheduled for a moment (0.5 of 2 seconds); 102 is on already; 103 has no effect
        assert_eq!(rig.shared.scheduled_call_count(), 1);
        assert_eq!(
            rig.shared.next_call_due(),
            Some(rig.shared.now + crate::env::seconds_to_ticks(1.0)),
            "0.5 of 2000 milliseconds"
        );
        assert!(rig.world.take_calls().is_empty(), "nothing is activated before the delay");
    }

    #[test]
    fn combat_effects_on_nothing_or_a_target_without_an_item_fail() {
        let attrs = Attrs::new()
            .with("selectedTargetAddress", item("Target"))
            .with("effectIds", Value::List(vec![Value::Int(101)]))
            .with("repeat", 0i64);
        let mut rig = rig();
        assert_eq!(rig.run(Box::new(ActivateCombatEffectsOnTarget), attrs.clone()), Status::Failure);
        let mut rig = self::rig();
        rig.post(ScopeType::Item, "Target", Value::Int(21));
        assert_eq!(rig.run(Box::new(ActivateCombatEffectsOnTarget), attrs), Status::Failure);
    }

    #[test]
    fn the_effects_the_entity_has_are_stopped_by_force() {
        let mut rig = rig();
        rig.world.type_effects.insert((700, 101));
        let attrs = Attrs::new().with("effectIds", Value::List(vec![Value::Int(101), Value::Int(102)]));
        assert_eq!(rig.run(Box::new(StopCombatEffectsOnTarget), attrs), Status::Success);
        assert_eq!(rig.world.take_calls(), ["stop_effect(101, 1, true)"]);
    }

    #[test]
    fn the_items_of_the_owners_are_taken_out_of_the_set_and_a_set_that_is_the_same_is_not_sent_again() {
        let attrs = Attrs::new()
            .with("itemIdSetAddress", item("Items"))
            .with("ownerIdSet", Value::set([Value::Int(8)]));
        let mut rig = rig();
        rig.world.slims.insert(20, SlimItem { item_id: 20, owner_id: Some(8), ..Default::default() });
        rig.world.slims.insert(21, SlimItem { item_id: 21, owner_id: Some(9), ..Default::default() });
        rig.post(ScopeType::Item, "Items", Value::set([Value::Int(20), Value::Int(21), Value::Int(22)]));
        assert_eq!(rig.run(Box::new(OwnerListFilterAction), attrs.clone()), Status::Success);
        assert_eq!(rig.last(ScopeType::Item, "Items"), Value::set([Value::Int(21), Value::Int(22)]));
        // nothing to take out: the channel keeps its one message
        let mut rig = self::rig();
        rig.post(ScopeType::Item, "Items", Value::set([Value::Int(21)]));
        rig.run(Box::new(OwnerListFilterAction), attrs);
        assert_eq!(rig.messages_on("Items"), 1);
    }

    #[test]
    fn a_role_gets_the_combat_effects_the_type_has_once() {
        let attrs =
            Attrs::new().with("roleAddress", item("Role")).with("combatEffectsAddress", item("Effects"));
        let mut rig = rig();
        let effects = crate::constants::behaviorroles::combat_effects_by_role("tackler").unwrap();
        rig.world.type_effects.insert((700, effects[0]));
        rig.post(ScopeType::Item, "Role", Value::Str("tackler".into()));
        assert_eq!(rig.run(Box::new(GetCombatEffectsForRole), attrs.clone()), Status::Success);
        assert_eq!(rig.last(ScopeType::Item, "Effects"), Value::List(vec![Value::Int(effects[0])]));
        // what is there is left alone
        rig.world.type_effects.insert((700, effects[1]));
        rig.run(Box::new(GetCombatEffectsForRole), attrs.clone());
        assert_eq!(rig.last(ScopeType::Item, "Effects"), Value::List(vec![Value::Int(effects[0])]));
        // a role without effects gets an empty list
        let mut rig = self::rig();
        rig.post(ScopeType::Item, "Role", Value::Str("nothing".into()));
        rig.run(Box::new(GetCombatEffectsForRole), attrs);
        assert_eq!(rig.last(ScopeType::Item, "Effects"), Value::List(vec![]));
    }

    fn kamikaze_attrs() -> Attrs {
        Attrs::new().with("selectedTargetAddress", item("Target")).with("shouldAbortAddress", item("Abort"))
    }

    fn kamikaze_rig() -> Rig {
        let mut rig = rig();
        rig.world.add_ball(1, [0.0; 3], 700);
        rig.world.add_ball(20, [50_000.0, 0.0, 0.0], 600);
        rig.world.attributes.insert((1, ATTRIBUTE_FIGHTER_ABILITY_KAMIKAZE_RANGE), 1000.0);
        rig.world.attributes.insert((1, ATTRIBUTE_FIGHTER_SQUADRON_SIZE), 9.0);
        rig.world.attributes.insert((1, ATTRIBUTE_FIGHTER_ABILITY_KAMIKAZE_SIGNATURE_RADIUS), 200.0);
        rig.world.attributes.insert((1, ATTRIBUTE_FIGHTER_ABILITY_KAMIKAZE_DAMAGE_EM), 10.0);
        rig.world.attributes.insert((1, ATTRIBUTE_FIGHTER_ABILITY_KAMIKAZE_DAMAGE_EXP), 20.0);
        rig.world.attributes.insert((1, ATTRIBUTE_FIGHTER_ABILITY_KAMIKAZE_DAMAGE_KIN), 30.0);
        rig.world.attributes.insert((1, ATTRIBUTE_THERMAL_DAMAGE), 40.0);
        rig.world.set_dist(1, 20, 50_000.0);
        rig.post(ScopeType::Item, "Target", Value::Int(20));
        rig
    }

    // A task is entered and updated in the tick it starts in, and `KamikazeAttack.OnEnter` updates as well: what it does is done twice.

    #[test]
    fn a_fighter_far_from_its_target_approaches_it_and_asks_to_be_told_when_it_is_near_or_collides() {
        let mut rig = kamikaze_rig();
        assert_eq!(rig.run(Box::new(KamikazeAttack::default()), kamikaze_attrs()), Status::Running);
        let once = [
            "approach_object(1, 20, -999999, false)",
            "set_ball_callbacks(1, true, true)",
            "set_target_tracking(1, 20, 1000)",
        ];
        assert_eq!(rig.world.take_calls(), [once, once].concat());
    }

    #[test]
    fn a_fighter_in_reach_of_its_target_starts_following_it_as_a_missile() {
        let mut rig = kamikaze_rig();
        rig.world.set_dist(1, 20, 1000.0);
        rig.world.fighter_controllers.insert(1, 90000001);
        assert_eq!(rig.run(Box::new(KamikazeAttack::default()), kamikaze_attrs()), Status::Running);
        assert!(rig.world.take_calls().is_empty(), "the tasklets have not run");
        rig.deliver_scheduled();
        let once = ["missile_follow(1, 20, 90000001)", "set_ball_massive(1, false)"];
        assert_eq!(rig.world.take_calls(), [once, once].concat());
        // a fighter without a controller has controller 0
        let mut rig = kamikaze_rig();
        rig.world.set_dist(1, 20, 900.0);
        rig.run(Box::new(KamikazeAttack::default()), kamikaze_attrs());
        rig.deliver_scheduled();
        assert!(rig.world.take_calls().contains(&"missile_follow(1, 20, 0)".to_string()));
    }

    #[test]
    fn a_fighter_that_touches_its_target_explodes_a_second_later_once_and_hurts_it() {
        let mut rig = kamikaze_rig();
        rig.world.set_dist(1, 20, 0.0);
        rig.world.cloud_factors.insert((20, 200.0f64.to_bits()), 0.5);
        rig.run(Box::new(KamikazeAttack::default()), kamikaze_attrs());
        assert_eq!(rig.shared.scheduled_call_count(), 3, "one explosion, and two tracking tasklets");
        rig.world.take_calls();
        rig.advance_seconds(1.0);
        rig.deliver_scheduled();
        let calls = rig.world.take_calls();
        assert_eq!(
            calls.iter().filter(|c| c.starts_with("apply_damage")).collect::<Vec<_>>(),
            ["apply_damage(1, 20, 4.5, 1, [(114, 10.0), (116, 20.0), (117, 30.0), (118, 40.0)])"]
        );
        assert_eq!(calls.iter().filter(|c| *c == "explode_ex(1)").count(), 1);
        assert_eq!(rig.tree.status_of(rig.tree.root_task().unwrap()), Status::Success);
    }

    #[test]
    fn a_collision_with_the_target_and_with_something_else() {
        let mut rig = kamikaze_rig();
        let task = rig.tree.add_task(Box::new(KamikazeAttack::default()), kamikaze_attrs());
        rig.tree.start_root_task(task);
        rig.tick().unwrap();
        let count = rig.shared.scheduled_call_count();
        let observer = crate::blackboards::Observer { tree: 1, task, handler: HANDLER_DO_COLLISION };
        let call = |rig: &mut Rig, other: i64| {
            rig.tree
                .deliver(
                    &mut rig.ctx,
                    &mut rig.shared,
                    &mut rig.world,
                    &crate::tree::Delivery::Call {
                        observer,
                        args: vec![
                            Value::Int(other),
                            Value::Float(0.0),
                            Value::Float(0.0),
                            Value::Float(0.0),
                        ],
                    },
                )
                .unwrap();
        };
        call(&mut rig, 99);
        assert_eq!(rig.shared.scheduled_call_count(), count, "somebody else");
        call(&mut rig, 20);
        call(&mut rig, 20);
        assert_eq!(rig.shared.scheduled_call_count(), count + 1, "the target: one explosion");
    }

    #[test]
    fn a_fighter_told_to_abort_stops_and_forgets_the_abort_and_is_at_it_again_in_the_same_tick() {
        let mut rig = kamikaze_rig();
        rig.post(ScopeType::Item, "Abort", Value::Bool(true));
        assert_eq!(rig.run(Box::new(KamikazeAttack::default()), kamikaze_attrs()), Status::Running);
        let calls = rig.world.take_calls();
        assert_eq!(&calls[..2], ["set_ball_callbacks(1, false, false)", "stop_ex(1)"]);
        assert_eq!(rig.last(ScopeType::Item, "Abort"), Value::Bool(false));
        assert!(
            calls.contains(&"approach_object(1, 20, -999999, false)".to_string()),
            "the abort is forgotten in the update that follows the enter"
        );
    }

    #[test]
    fn a_fighter_whose_target_is_gone_stops_and_succeeds() {
        let mut rig = kamikaze_rig();
        rig.world.balls.remove(&20);
        assert_eq!(rig.run(Box::new(KamikazeAttack::default()), kamikaze_attrs()), Status::Success);
        let once = ["set_ball_callbacks(1, false, false)", "stop_ex(1)"];
        assert_eq!(rig.world.take_calls(), [once, once].concat());
    }

    #[test]
    fn a_missile_fighter_that_lost_its_target_explodes_and_one_that_has_it_flies_on() {
        for (target_ball, exploded) in [(false, true), (true, false)] {
            let mut rig = kamikaze_rig();
            rig.world.balls.get_mut(&1).unwrap().mode = DESTINY_MODE_MISSILE;
            if !target_ball {
                rig.world.balls.remove(&20);
            }
            rig.post(ScopeType::Item, "Abort", Value::Bool(!target_ball));
            let status = rig.run(Box::new(KamikazeAttack::default()), kamikaze_attrs());
            assert_ne!(status, Status::Success);
            assert_eq!(rig.world.take_calls().contains(&"explode_ex(1)".to_string()), exploded);
        }
    }

    #[test]
    fn a_fighter_that_is_no_item_fails_unless_the_update_that_follows_says_otherwise() {
        let mut rig = kamikaze_rig();
        rig.world.items.remove(&1);
        assert_eq!(
            rig.run(Box::new(KamikazeAttack::default()), kamikaze_attrs()),
            Status::Running,
            "the update engages the target"
        );
        // a missile whose target is gone changes no status
        let mut rig = kamikaze_rig();
        rig.world.items.remove(&1);
        rig.world.balls.get_mut(&1).unwrap().mode = DESTINY_MODE_MISSILE;
        rig.world.balls.remove(&20);
        assert_eq!(rig.run(Box::new(KamikazeAttack::default()), kamikaze_attrs()), Status::Failure);
    }
}
