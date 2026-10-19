use crate::consts::{
    EFFECT_ENTITY_ARMOR_REPAIRING_LARGE, EFFECT_ENTITY_ARMOR_REPAIRING_MEDIUM,
    EFFECT_ENTITY_ARMOR_REPAIRING_SMALL, EFFECT_ENTITY_SHIELD_BOOSTING_LARGE,
    EFFECT_ENTITY_SHIELD_BOOSTING_MEDIUM, EFFECT_ENTITY_SHIELD_BOOSTING_SMALL,
    EFFECT_NPC_BEHAVIOR_ARMOR_REPAIRER, EFFECT_NPC_BEHAVIOR_SHIELD_BOOSTER,
};
use crate::env::WorldError;
use crate::env::dogma::health;
use crate::events::{EventKey, message};
use crate::tree::{Task, TaskCtx};
use crate::utility::dogmatic::type_has_effect;
use crate::value::Value;

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

const TANK_EFFECT_REPEAT: i64 = 100;

const HANDLER_ON_DAMAGE_RECEIVED: u16 = 0;
const HANDLER_SHIELD_TANK_REPAIRED: u16 = 1;
const HANDLER_ARMOR_TANK_REPAIRED: u16 = 2;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum TankKind {
    Armor,
    Shield,
}

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

    fn is_initialized(&self, item_id: i64) -> bool {
        self.item_id == Some(item_id)
    }

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

    fn can_active_tank(&self, t: &mut TaskCtx<'_, '_>) -> Result<bool, WorldError> {
        let (Some(_), Some(item_id)) = (self.effect_id, self.item_id) else { return Ok(false) };
        Ok(!t.world().is_exploding(item_id)?)
    }

    fn tank_status(&self, t: &mut TaskCtx<'_, '_>) -> Result<f64, WorldError> {
        let item_id = self.item_id.unwrap_or_default();
        match self.kind {
            TankKind::Armor => health::current_armor_ratio(&*t.world(), item_id),
            TankKind::Shield => health::current_shield_ratio(&*t.world(), item_id),
        }
    }

    fn is_at_full_health(&self, t: &mut TaskCtx<'_, '_>) -> Result<bool, WorldError> {
        Ok(self.tank_status(t)? >= 1.0)
    }

    fn activate_tank_if_damaged(&mut self, t: &mut TaskCtx<'_, '_>) -> Result<(), WorldError> {
        if !self.can_active_tank(t)? {
            return Ok(());
        }
        if !self.is_at_full_health(t)? {
            self.start_tanking(t);
        }
        Ok(())
    }

    fn start_tanking(&mut self, t: &mut TaskCtx<'_, '_>) {
        let (Some(item_id), Some(effect_id)) = (self.item_id, self.effect_id) else { return };
        self.is_tanking = true;
        t.subscribe_item(
            item_id,
            EventKey::with(message::ON_EFFECT_STARTED, effect_id),
            self.repaired_handler(),
        );
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

    fn on_tank_repaired(&mut self, t: &mut TaskCtx<'_, '_>) {
        if !self.is_tanking {
            return;
        }
        match self.is_at_full_health(t) {
            Ok(true) => self.try_stop_tanking(t),
            Ok(false) => {}
            Err(error) => t.raise(error.into()),
        }
    }

    fn try_stop_tanking(&mut self, t: &mut TaskCtx<'_, '_>) {
        let (Some(item_id), Some(effect_id)) = (self.item_id, self.effect_id) else { return };
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
                    let hurt = match tank.can_active_tank(t) {
                        Ok(true) if !tank.is_tanking => tank.is_at_full_health(t).map(|whole| !whole),
                        Ok(_) => Ok(false),
                        Err(error) => Err(error),
                    };
                    match hurt {
                        Ok(true) => t.request_reset(),
                        Ok(false) => {}
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

    fn set_health(rig: &mut Rig, shield: f64, armor: f64) {
        use crate::consts::{
            ATTRIBUTE_ARMOR_DAMAGE, ATTRIBUTE_ARMOR_HP, ATTRIBUTE_SHIELD_CAPACITY, ATTRIBUTE_SHIELD_CHARGE,
        };
        rig.world.attributes.insert((1, ATTRIBUTE_SHIELD_CAPACITY), 1000.0);
        rig.world.attributes.insert((1, ATTRIBUTE_SHIELD_CHARGE), 1000.0 * shield);
        rig.world.attributes.insert((1, ATTRIBUTE_ARMOR_HP), 1000.0);
        rig.world.attributes.insert((1, ATTRIBUTE_ARMOR_DAMAGE), 1000.0 * (1.0 - armor));
    }

    fn rig() -> Rig {
        let mut rig = Rig::new();
        rig.ctx.my_slim_item =
            Some(SlimItem { item_id: 1, type_id: 700, owner_id: Some(7), ..Default::default() });
        rig.world.type_effects.insert((700, EFFECT_ENTITY_SHIELD_BOOSTING_MEDIUM));
        rig.world.type_effects.insert((700, EFFECT_ENTITY_ARMOR_REPAIRING_LARGE));
        set_health(&mut rig, 1.0, 1.0);
        rig
    }

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
        set_health(&mut rig, 0.5, 1.0);
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
        set_health(&mut rig, 0.5, 1.0);
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
        set_health(&mut rig, 1.0, 0.9);
        rig.send_event(1, message::ON_DAMAGE_CHANGED, &[]);
        assert!(rig.tree.has_reset_request());
    }

    #[test]
    fn an_entity_that_is_exploding_is_not_repaired() {
        let mut rig = rig();
        set_health(&mut rig, 0.5, 1.0);
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
        set_health(&mut rig, 0.1, 1.0);
        start(&mut rig);
        assert!(rig.world.take_calls().is_empty());
    }

    #[test]
    fn the_effect_is_stopped_when_the_layer_is_whole_again_unless_it_cannot_be() {
        let mut rig = rig();
        set_health(&mut rig, 0.5, 1.0);
        start(&mut rig);
        rig.world.take_calls();
        let key = EventKey::with(message::ON_EFFECT_STARTED, EFFECT_ENTITY_SHIELD_BOOSTING_MEDIUM);
        rig.send_event(1, key, &[]);
        assert!(rig.world.take_calls().is_empty(), "the shield is still hurt");
        set_health(&mut rig, 1.0, 1.0);
        rig.send_event(1, key, &[]);
        assert_eq!(
            rig.world.take_calls(),
            [format!("stop_effect({EFFECT_ENTITY_SHIELD_BOOSTING_MEDIUM}, 1, false)")]
        );
        rig.send_event(1, key, &[]);
        assert!(rig.world.take_calls().is_empty());
    }

    #[test]
    fn a_task_that_is_cleaned_up_stops_listening_for_damage() {
        let mut rig = rig();
        start(&mut rig);
        rig.tree.release(&mut rig.ctx, &mut rig.shared, &mut rig.world);
        rig.send_event(1, message::ON_DAMAGE_CHANGED, &[]);
        set_health(&mut rig, 1.0, 0.5);
        rig.send_event(1, message::ON_DAMAGE_CHANGED, &[]);
        assert!(!rig.tree.has_reset_request());
    }
}
