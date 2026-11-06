//! `behaviors/monitors/logistics.py`.

use crate::evemath::weighted_choice;
use crate::events::message::ON_EFFECT_STOPPED;
use crate::tree::{Task, TaskCtx};
use crate::utility::dogmatic::{get_my_locked_targets, is_my_effect_active, try_activate_effect};
use crate::utility::logistics::{
    DamageState, get_base_logistics_priority, get_damage_state_function_by_effect_id,
    get_damage_state_modifier,
};
use crate::value::Value;

const HANDLER_EFFECT_STOPPED: u16 = 0;

/// Keeps my remote repair effect on the ship that needs it most (`ManageRemoteRepairEffect`).
#[derive(Debug, Default)]
pub struct ManageRemoteRepairEffect {
    repair_effect_id: Value,
    damage_state: Option<DamageState>,
}

impl ManageRemoteRepairEffect {
    /// `_apply_effect_on_best_target`.
    fn apply_effect_on_best_target(&mut self, t: &mut TaskCtx<'_, '_>) {
        let Some(watchlist) = t.value_at("prioritizedWatchlistAddress") else { return };
        if !watchlist.truthy() {
            return;
        }
        let locked_target_ids = get_my_locked_targets(t);
        if locked_target_ids.is_empty() {
            return;
        }
        let candidates = self.generate_candidates(t, &locked_target_ids, &watchlist);
        if candidates.is_empty() {
            return;
        }
        let item_id = weighted_choice(&candidates, t.world());
        let Some(effect_id) = self.repair_effect_id.as_int() else { return };
        try_activate_effect(t, Some(effect_id), 0, item_id, &Value::None, None, 0, 0.0);
    }

    /// `_generate_candidates`.
    fn generate_candidates(
        &mut self,
        t: &mut TaskCtx<'_, '_>,
        locked_target_ids: &[i64],
        watchlist: &Value,
    ) -> Vec<(i64, f64)> {
        let mut candidates = Vec::new();
        for entry in watchlist.items().unwrap_or(&[]) {
            let Some([_priority, item]) = entry.items() else { continue };
            let Some(item_id) = item.as_int() else { continue };
            if locked_target_ids.contains(&item_id) {
                // the client calls what `get_damage_state_function_by_effect_id` gave, which is nothing for an effect that is no repair effect
                let Some(damage_state) = self.damage_state else {
                    t.raise(crate::tree::BehaviorError::Task(
                        "TypeError: 'NoneType' object is not callable".to_string(),
                    ));
                    return candidates;
                };
                let Some(mut weight) = get_damage_state_modifier(t, item_id, damage_state) else {
                    return candidates;
                };
                if weight > 0.0 {
                    weight += get_base_logistics_priority(t, item_id);
                    candidates.push((item_id, weight));
                }
            }
        }
        candidates
    }
}

impl Task for ManageRemoteRepairEffect {
    fn type_name(&self) -> &'static str {
        "ManageRemoteRepairEffect"
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        let Some(effect) = t.value_at("repairEffectIdAddress") else { return };
        self.repair_effect_id = effect;
        self.damage_state = self.repair_effect_id.as_int().and_then(get_damage_state_function_by_effect_id);
        let active = self.repair_effect_id.as_int().is_some_and(|id| is_my_effect_active(t, id));
        if !active {
            self.apply_effect_on_best_target(t);
        }
        if t.faulted() {
            return;
        }
        if let Some(me) = t.my_item_id() {
            t.subscribe_item(me, ON_EFFECT_STOPPED, HANDLER_EFFECT_STOPPED);
        }
        t.set_status_to_success();
    }

    fn clean_up(&mut self, t: &mut TaskCtx<'_, '_>) {
        if let Some(me) = t.my_item_id() {
            t.unsubscribe_item(me, ON_EFFECT_STOPPED, HANDLER_EFFECT_STOPPED);
        }
        t.set_status_to_invalid();
    }

    fn on_event(&mut self, t: &mut TaskCtx<'_, '_>, _handler: u16, args: &[Value]) {
        if args.first().is_some_and(|effect| effect.py_eq(&self.repair_effect_id)) {
            self.apply_effect_on_best_target(t);
        }
    }

    fn on_call(&mut self, t: &mut TaskCtx<'_, '_>, handler: u16, args: &[Value]) {
        crate::utility::dogmatic::on_call(t, handler, args);
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::blackboards::{Address, ScopeType};
    use crate::consts::*;
    use crate::env::TestWorld;
    use crate::env::testing::TestGroup;
    use crate::tree::tests::Rig;
    use crate::tree::{Attrs, Status};

    fn item(name: &str) -> Value {
        Address::new(ScopeType::Item, name).to_value()
    }

    #[test]
    fn the_repair_effect_goes_to_the_locked_ship_on_the_watchlist_that_is_hurt() {
        let attrs = Attrs::new()
            .with("repairEffectIdAddress", item("E"))
            .with("prioritizedWatchlistAddress", item("W"));
        let mut rig = Rig::new();
        rig.ctx.my_entity_group_id = Some(10);
        rig.ctx.my_slim_item = Some(crate::env::SlimItem { owner_id: Some(5), ..Default::default() });
        rig.world = TestWorld::with_numbers(&[0.5]);
        rig.world.groups.insert(10, TestGroup { members: vec![7, 8], ..Default::default() });
        rig.world.targets.insert(1, vec![7]);
        for ship in [7, 8] {
            rig.world.attributes.insert((ship, ATTRIBUTE_SHIELD_CAPACITY), 100.0);
            rig.world.attributes.insert((ship, ATTRIBUTE_SHIELD_CHARGE), 50.0);
        }
        rig.post(ScopeType::Item, "E", Value::Int(EFFECT_NPC_BEHAVIOR_REMOTE_SHIELD_BOOSTER));
        rig.post(
            ScopeType::Item,
            "W",
            Value::List(vec![
                Value::tuple([Value::Float(0.9), Value::Int(7)]),
                Value::tuple([Value::Float(0.8), Value::Int(8)]),
            ]),
        );
        assert_eq!(rig.run(Box::new(ManageRemoteRepairEffect::default()), attrs), Status::Success);
        assert_eq!(rig.last_error, None);
        assert_eq!(
            rig.world.take_calls(),
            [format!("activate(5, 1, {EFFECT_NPC_BEHAVIOR_REMOTE_SHIELD_BOOSTER}, Some(7), 0)")]
        );
    }

    #[test]
    fn nothing_is_done_with_no_watchlist_no_locked_target_or_the_effect_already_running() {
        let attrs = Attrs::new()
            .with("repairEffectIdAddress", item("E"))
            .with("prioritizedWatchlistAddress", item("W"));
        let mut rig = Rig::new();
        rig.post(ScopeType::Item, "E", Value::Int(EFFECT_NPC_BEHAVIOR_REMOTE_SHIELD_BOOSTER));
        assert_eq!(rig.run(Box::new(ManageRemoteRepairEffect::default()), attrs.clone()), Status::Success);
        assert!(rig.world.take_calls().is_empty());
        let mut rig = Rig::new();
        rig.post(ScopeType::Item, "E", Value::Int(EFFECT_NPC_BEHAVIOR_REMOTE_SHIELD_BOOSTER));
        rig.post(ScopeType::Item, "W", Value::List(vec![Value::tuple([Value::Float(0.9), Value::Int(7)])]));
        rig.world.active_effects.insert((1, EFFECT_NPC_BEHAVIOR_REMOTE_SHIELD_BOOSTER));
        assert_eq!(rig.run(Box::new(ManageRemoteRepairEffect::default()), attrs), Status::Success);
        assert!(rig.world.take_calls().is_empty());
    }
}
