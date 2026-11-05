//! `behaviors/monitors/effects.py`.

use crate::events::Subject;
use crate::events::message::EFFECT_CALLBACK;
use crate::tree::{Task, TaskCtx};
use crate::utility::ballparks::get_my_ball;
use crate::utility::blackboards::{SetAdd, add_to_set_on_channel};
use crate::value::Value;

/// Collects the ships in my bubble that use an effect, on a channel (`EffectUsageMonitor`).
#[derive(Debug, Default)]
pub struct EffectUsageMonitor;

impl Task for EffectUsageMonitor {
    fn type_name(&self) -> &'static str {
        "EffectUsageMonitor"
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        // `RegisterEffectCallback(('OnEffect', self))`
        t.subscribe(Subject::Notify, EFFECT_CALLBACK, 0);
        t.set_status_to_failed();
    }

    fn clean_up(&mut self, t: &mut TaskCtx<'_, '_>) {
        t.unsubscribe(Subject::Notify, EFFECT_CALLBACK, 0);
        t.set_status_to_invalid();
    }

    fn on_event(&mut self, t: &mut TaskCtx<'_, '_>, _handler: u16, args: &[Value]) {
        // OnEffect(effect, start, active, env, otherTypeID, duration, repeat, error): the effect's id and the ship of its dogma environment
        if t.is_invalid() {
            return;
        }
        let Some(effect_id) = args.first() else { return };
        let Some(wanted) = t.required("effectId") else { return };
        if !effect_id.py_eq(&wanted) {
            return;
        }
        let ship = args.get(3).cloned().unwrap_or(Value::None);
        if ship.is_none() {
            return;
        }
        let Some(ship_id) = t.expect_id(&ship) else { return };
        let Some(ship_ball) = t.world().ball(ship_id) else {
            t.raise(crate::env::WorldError::Runtime(format!("no ball {ship_id}")).into());
            return;
        };
        let Some(mine) = get_my_ball(t) else { return };
        if ship_ball.new_bubble_id != mine.new_bubble_id {
            return;
        }
        let Some(address) = t.required_address("shipIdSetAddress") else { return };
        let _ = add_to_set_on_channel(t, &address, Value::Int(ship_id), false) == SetAdd::Added;
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::blackboards::{Address, ScopeType};
    use crate::tree::tests::Rig;
    use crate::tree::{Attrs, Status};

    fn effect_args(effect: i64, ship: Option<i64>) -> Vec<Value> {
        vec![
            Value::Int(effect),
            Value::Bool(true),
            Value::Bool(true),
            ship.map_or(Value::None, Value::Int),
            Value::None,
            Value::None,
            Value::None,
            Value::None,
        ]
    }

    fn tell(rig: &mut Rig, args: &[Value]) {
        let mut cx = crate::tree::Cx {
            core: &mut rig.tree.core,
            ctx: &mut rig.ctx,
            shared: &mut rig.shared,
            world: &mut rig.world,
        };
        cx.send_message_about(Subject::Notify, EFFECT_CALLBACK, args);
    }

    #[test]
    fn a_ship_in_my_bubble_that_uses_the_effect_is_collected() {
        let attrs = Attrs::new()
            .with("effectId", 55i64)
            .with("shipIdSetAddress", Address::new(ScopeType::Item, "Ships").to_value());
        let mut rig = Rig::new();
        rig.world.add_ball(1, [0.0; 3], 1).new_bubble_id = 3;
        rig.world.add_ball(40, [0.0; 3], 1).new_bubble_id = 3;
        rig.world.add_ball(41, [0.0; 3], 1).new_bubble_id = 4;
        assert_eq!(
            rig.run(Box::new(EffectUsageMonitor), attrs),
            Status::Failure,
            "it always fails: it only collects"
        );
        tell(&mut rig, &effect_args(56, Some(40)));
        tell(&mut rig, &effect_args(55, None));
        tell(&mut rig, &effect_args(55, Some(41)));
        tell(&mut rig, &effect_args(55, Some(40)));
        tell(&mut rig, &effect_args(55, Some(40)));
        let ships =
            rig.shared.blackboards.message_channel(crate::blackboards::item_scope(1), "Ships").unwrap();
        assert_eq!(ships.last_message_value(), Value::Set(vec![Value::Int(40)]));
        assert_eq!(ships.messages().len(), 1);
    }
}
