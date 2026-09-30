//! `behaviors/actions/tanking.py`: repairing the armor and boosting the shield of my entity when it is hurt.

use crate::consts::{
    EFFECT_ENTITY_ARMOR_REPAIRING_LARGE, EFFECT_ENTITY_ARMOR_REPAIRING_MEDIUM,
    EFFECT_ENTITY_ARMOR_REPAIRING_SMALL, EFFECT_ENTITY_SHIELD_BOOSTING_LARGE,
    EFFECT_ENTITY_SHIELD_BOOSTING_MEDIUM, EFFECT_ENTITY_SHIELD_BOOSTING_SMALL,
    EFFECT_NPC_BEHAVIOR_ARMOR_REPAIRER, EFFECT_NPC_BEHAVIOR_SHIELD_BOOSTER,
};
use crate::env::WorldError;
use crate::events::{EventKey, message};
use crate::tree::{Task, TaskCtx};
use crate::utility::dogmatic::type_has_effect;
use crate::value::Value;

/// `ARMOR_TANK_EFFECT_IDS` and `SHIELD_TANK_EFFECT_IDS` (the ids of a set have no order in the client; the lowest one the type has is used).
const ARMOR_TANK_EFFECT_IDS: [i64; 4] = [
    EFFECT_ENTITY_ARMOR_REPAIRING_SMALL,
    EFFECT_ENTITY_ARMOR_REPAIRING_MEDIUM,
    EFFECT_ENTITY_ARMOR_REPAIRING_LARGE,
    EFFECT_NPC_BEHAVIOR_ARMOR_REPAIRER,
];
const SHIELD_TANK_EFFECT_IDS: [i64; 4] = [
    EFFECT_ENTITY_SHIELD_BOOSTING_SMALL,
    EFFECT_ENTITY_SHIELD_BOOSTING_MEDIUM,
    EFFECT_ENTITY_SHIELD_BOOSTING_LARGE,
    EFFECT_NPC_BEHAVIOR_SHIELD_BOOSTER,
];

/// How many times the tank effect repeats.
const TANK_EFFECT_REPEAT: i64 = 100;

const HANDLER_ON_DAMAGE_RECEIVED: u16 = 0;
const HANDLER_SHIELD_TANK_REPAIRED: u16 = 1;
const HANDLER_ARMOR_TANK_REPAIRED: u16 = 2;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum TankKind {
    Armor,
    Shield,
}

/// `EntityArmorTank` and `EntityShieldTank`.
#[derive(Debug)]
struct EntityTank {
    kind: TankKind,
    is_tanking: bool,
    effect_id: Option<i64>,
    item_id: Option<i64>,
    type_id: Option<i64>,
    owner_id: Option<i64>,
}

impl EntityTank {
    fn new(kind: TankKind) -> Self {
        Self { kind, is_tanking: false, effect_id: None, item_id: None, type_id: None, owner_id: None }
    }

    fn repaired_handler(&self) -> u16 {
        match self.kind {
            TankKind::Armor => HANDLER_ARMOR_TANK_REPAIRED,
            TankKind::Shield => HANDLER_SHIELD_TANK_REPAIRED,
        }
    }

    /// `IsInitialized(itemId)`.
    fn is_initialized(&self, item_id: i64) -> bool {
        self.item_id == Some(item_id)
    }

    /// `Initialized(...)`, with `_DetermineValidTankEffect`.
    fn initialize(&mut self, t: &mut TaskCtx<'_, '_>, item_id: i64, type_id: i64, owner_id: Option<i64>) {
        self.item_id = Some(item_id);
        self.type_id = Some(type_id);
        self.owner_id = owner_id;
        self.effect_id = None;
        let valid = match self.kind {
            TankKind::Armor => ARMOR_TANK_EFFECT_IDS,
            TankKind::Shield => SHIELD_TANK_EFFECT_IDS,
        };
        for effect_id in valid {
            if type_has_effect(t, type_id, effect_id) {
                self.effect_id = Some(effect_id);
                break;
            }
        }
    }

    /// `CanActiveTank`: it has an effect, and the entity is not exploding.
    fn can_active_tank(&self, t: &mut TaskCtx<'_, '_>) -> Result<bool, WorldError> {
        let (Some(_), Some(item_id)) = (self.effect_id, self.item_id) else { return Ok(false) };
        Ok(!t.world().is_exploding(item_id)?)
    }

    /// `GetTankStatus`: the ratio of the layer that is left.
    fn tank_status(&self, t: &mut TaskCtx<'_, '_>) -> f64 {
        let item_id = self.item_id.unwrap_or_default();
        match self.kind {
            TankKind::Armor => t.world().current_armor_ratio(item_id),
            TankKind::Shield => t.world().current_shield_ratio(item_id),
        }
    }

    /// `IsAtFullHealth`.
    fn is_at_full_health(&self, t: &mut TaskCtx<'_, '_>) -> bool {
        self.tank_status(t) >= 1.0
    }

    /// `ActivateTankIfDamaged`.
    fn activate_tank_if_damaged(&mut self, t: &mut TaskCtx<'_, '_>) -> Result<(), WorldError> {
        if !self.can_active_tank(t)? {
            return Ok(());
        }
        if !self.is_at_full_health(t) {
            self.start_tanking(t);
        }
        Ok(())
    }

    /// `_StartTanking`: the effect starts (what goes wrong in that is eaten) and the tank listens for it to cycle.
    fn start_tanking(&mut self, t: &mut TaskCtx<'_, '_>) {
        let (Some(item_id), Some(effect_id)) = (self.item_id, self.effect_id) else { return };
        self.is_tanking = true;
        t.subscribe_item(
            item_id,
            EventKey::with(message::ON_EFFECT_STARTED, effect_id),
            self.repaired_handler(),
        );
        // `_StartEffects`
        let owner_id = self.owner_id;
        t.eat_exceptions(|t| {
            if t.world().active_effect_target_id(item_id, effect_id).is_some() {
                return;
            }
            let Some(owner_id) = owner_id else { return };
            let result = t.world().activate_with_context(
                owner_id,
                item_id,
                Some(effect_id),
                None,
                TANK_EFFECT_REPEAT,
                &Value::None,
            );
            if let Err(error) = result {
                t.raise(error.into());
            }
        });
    }

    /// `_OnTankRepaired`.
    fn on_tank_repaired(&mut self, t: &mut TaskCtx<'_, '_>) {
        if !self.is_tanking {
            return;
        }
        if self.is_at_full_health(t) {
            self.try_stop_tanking(t);
        }
    }

    /// `TryStopTanking`: an effect that is still cycling stays, and so does anything else that goes wrong.
    fn try_stop_tanking(&mut self, t: &mut TaskCtx<'_, '_>) {
        let (Some(item_id), Some(effect_id)) = (self.item_id, self.effect_id) else { return };
        // `_StopEffects`
        if t.world().stop_effect(effect_id, item_id, false).is_err() {
            return;
        }
        self.is_tanking = false;
        t.unsubscribe_item(
            item_id,
            EventKey::with(message::ON_EFFECT_STARTED, effect_id),
            self.repaired_handler(),
        );
    }
}

/// Keeps the shield and the armor of my entity repaired for as long as it is hurt; it is never done (a failure) and resets the tree when it is
/// damaged with nothing repairing.
#[derive(Debug)]
pub struct ActiveTank {
    armor_tank: EntityTank,
    shield_tank: EntityTank,
}

impl Default for ActiveTank {
    fn default() -> Self {
        Self { armor_tank: EntityTank::new(TankKind::Armor), shield_tank: EntityTank::new(TankKind::Shield) }
    }
}

impl ActiveTank {
    /// `_IterTanks`: the shield first.
    fn tanks(&mut self) -> [&mut EntityTank; 2] {
        [&mut self.shield_tank, &mut self.armor_tank]
    }

    /// The part of `OnEnter` that a `RuntimeError` cuts short.
    fn activate_and_subscribe(&mut self, t: &mut TaskCtx<'_, '_>) -> Result<(), WorldError> {
        // `ActivateTanks`
        for tank in self.tanks() {
            tank.activate_tank_if_damaged(t)?;
        }
        // `_SubscribeDamageChangesIfNotTanking`
        if self.tanks().iter().all(|tank| !tank.is_tanking)
            && let Some(me) = t.my_item_id()
        {
            t.subscribe_item(me, message::ON_DAMAGE_CHANGED, HANDLER_ON_DAMAGE_RECEIVED);
        }
        Ok(())
    }
}

impl Task for ActiveTank {
    fn type_name(&self) -> &'static str {
        "ActiveTank"
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        // `_InitTanks`
        let Some(me) = t.my_item_id() else { return };
        let Some(slim) = t.ctx().my_slim_item.clone() else {
            t.raise(crate::tree::BehaviorError::Task(
                "AttributeError: 'NoneType' object has no attribute 'typeID'".to_string(),
            ));
            return;
        };
        for tank in self.tanks() {
            if !tank.is_initialized(me) {
                tank.initialize(t, me, slim.type_id, slim.owner_id);
            }
        }
        match self.activate_and_subscribe(t) {
            Ok(()) | Err(WorldError::Runtime(_)) => {}
            Err(other) => t.raise(other.into()),
        }
        t.set_status_to_failed();
    }

    fn on_event(&mut self, t: &mut TaskCtx<'_, '_>, handler: u16, _args: &[Value]) {
        match handler {
            HANDLER_ON_DAMAGE_RECEIVED => {
                // `_OnDamageReceived`
                for tank in self.tanks() {
                    match tank.can_active_tank(t) {
                        Ok(true) if !tank.is_tanking && !tank.is_at_full_health(t) => t.request_reset(),
                        Ok(_) => {}
                        Err(error) => {
                            t.raise(error.into());
                            return;
                        }
                    }
                }
            }
            HANDLER_SHIELD_TANK_REPAIRED => self.shield_tank.on_tank_repaired(t),
            HANDLER_ARMOR_TANK_REPAIRED => self.armor_tank.on_tank_repaired(t),
            _ => {}
        }
    }

    fn clean_up(&mut self, t: &mut TaskCtx<'_, '_>) {
        if !t.is_invalid()
            && let Some(me) = t.my_item_id()
        {
            t.unsubscribe_item(me, message::ON_DAMAGE_CHANGED, HANDLER_ON_DAMAGE_RECEIVED);
        }
        t.set_status_to_invalid();
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::env::SlimItem;
    use crate::tree::tests::Rig;
    use crate::tree::{Attrs, Status};

    fn rig() -> Rig {
        let mut rig = Rig::new();
        rig.ctx.my_slim_item =
            Some(SlimItem { item_id: 1, type_id: 700, owner_id: Some(7), ..Default::default() });
        rig.world.type_effects.insert((700, EFFECT_ENTITY_SHIELD_BOOSTING_MEDIUM));
        rig.world.type_effects.insert((700, EFFECT_ENTITY_ARMOR_REPAIRING_LARGE));
        rig
    }

    /// A tank task under a sequence that goes on, so that its reset requests are only its own.
    fn start(rig: &mut Rig) -> crate::blackboards::TaskId {
        let task = rig.tree.add_task(Box::new(ActiveTank::default()), Attrs::new());
        let wait = rig.leaf("wait", Status::Success, 50);
        let root = rig.selector(&[task, wait]);
        rig.tree.start_root_task(root);
        rig.tick().unwrap();
        task
    }

    #[test]
    fn a_hurt_entity_starts_the_repairs_of_the_layers_that_are_hurt_and_the_task_fails_all_the_same() {
        let mut rig = rig();
        rig.world.shield_ratios.insert(1, 0.5);
        let task = start(&mut rig);
        assert_eq!(rig.last_error, None);
        assert_eq!(rig.tree.status_of(task), Status::Failure);
        assert_eq!(
            rig.world.take_calls(),
            [format!("activate(7, 1, {EFFECT_ENTITY_SHIELD_BOOSTING_MEDIUM}, None, 100)")],
            "only the shield is hurt"
        );
    }

    #[test]
    fn an_effect_that_is_running_is_not_started_again() {
        let mut rig = rig();
        rig.world.shield_ratios.insert(1, 0.5);
        rig.world.active_effect_targets.insert((1, EFFECT_ENTITY_SHIELD_BOOSTING_MEDIUM), None);
        start(&mut rig);
        assert!(rig.world.take_calls().is_empty());
    }

    #[test]
    fn a_whole_entity_waits_to_be_hurt_and_then_resets_the_tree_once_for_each_layer_that_is_not_repaired() {
        let mut rig = rig();
        start(&mut rig);
        assert!(rig.world.take_calls().is_empty());
        assert!(!rig.tree.has_reset_request());
        rig.send_event(1, message::ON_DAMAGE_CHANGED, &[]);
        assert!(!rig.tree.has_reset_request(), "nothing is hurt");
        rig.world.armor_ratios.insert(1, 0.9);
        rig.send_event(1, message::ON_DAMAGE_CHANGED, &[]);
        assert!(rig.tree.has_reset_request());
    }

    #[test]
    fn an_entity_that_is_exploding_is_not_repaired() {
        let mut rig = rig();
        rig.world.shield_ratios.insert(1, 0.5);
        rig.world.exploding.insert(1);
        start(&mut rig);
        assert!(rig.world.take_calls().is_empty());
        rig.send_event(1, message::ON_DAMAGE_CHANGED, &[]);
        assert!(!rig.tree.has_reset_request());
    }

    #[test]
    fn a_runtime_error_of_the_world_cuts_the_start_short_and_is_no_error() {
        let mut rig = rig();
        rig.world.exploding_error = Some(WorldError::Runtime("gone".into()));
        let task = start(&mut rig);
        assert_eq!(rig.last_error, None);
        assert_eq!(rig.tree.status_of(task), Status::Failure);
    }

    #[test]
    fn a_type_without_a_tank_effect_has_nothing_to_repair() {
        let mut rig = Rig::new();
        rig.ctx.my_slim_item =
            Some(SlimItem { item_id: 1, type_id: 700, owner_id: Some(7), ..Default::default() });
        rig.world.shield_ratios.insert(1, 0.1);
        start(&mut rig);
        assert!(rig.world.take_calls().is_empty());
    }

    #[test]
    fn the_effect_is_stopped_when_the_layer_is_whole_again_unless_it_cannot_be() {
        let mut rig = rig();
        rig.world.shield_ratios.insert(1, 0.5);
        start(&mut rig);
        rig.world.take_calls();
        let key = EventKey::with(message::ON_EFFECT_STARTED, EFFECT_ENTITY_SHIELD_BOOSTING_MEDIUM);
        rig.send_event(1, key, &[]);
        assert!(rig.world.take_calls().is_empty(), "the shield is still hurt");
        rig.world.shield_ratios.insert(1, 1.0);
        rig.send_event(1, key, &[]);
        assert_eq!(
            rig.world.take_calls(),
            [format!("stop_effect({EFFECT_ENTITY_SHIELD_BOOSTING_MEDIUM}, 1, false)")]
        );
        // it is not tanking any more: the next cycle changes nothing
        rig.send_event(1, key, &[]);
        assert!(rig.world.take_calls().is_empty());
    }

    #[test]
    fn a_task_that_is_cleaned_up_stops_listening_for_damage() {
        let mut rig = rig();
        start(&mut rig);
        rig.tree.release(&mut rig.ctx, &mut rig.shared, &mut rig.world);
        rig.send_event(1, message::ON_DAMAGE_CHANGED, &[]);
        rig.world.armor_ratios.insert(1, 0.5);
        rig.send_event(1, message::ON_DAMAGE_CHANGED, &[]);
        assert!(!rig.tree.has_reset_request());
    }
}
