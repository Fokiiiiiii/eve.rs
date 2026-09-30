//! `behaviors/monitors/damage.py`.

use crate::env::dogma::health;
use crate::events::message::ON_DAMAGE_CHANGED;
use crate::tree::{Task, TaskCtx};
use crate::value::Value;

/// Puts a flag up and starts the tree over when my armor is below a ratio.
#[derive(Debug, Default)]
pub struct ArmorDamageThresholdMonitor;

impl Task for ArmorDamageThresholdMonitor {
    fn type_name(&self) -> &'static str {
        "ArmorDamageThresholdMonitor"
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        let Some(me) = t.my_item_id() else { return };
        t.subscribe_item(me, ON_DAMAGE_CHANGED, 0);
        t.set_status_to_success();
    }

    fn clean_up(&mut self, t: &mut TaskCtx<'_, '_>) {
        if !t.is_invalid()
            && let Some(me) = t.my_item_id()
        {
            t.unsubscribe_item(me, ON_DAMAGE_CHANGED, 0);
        }
        t.set_status_to_invalid();
    }

    fn on_event(&mut self, t: &mut TaskCtx<'_, '_>, _handler: u16, _args: &[Value]) {
        if t.is_invalid() {
            return;
        }
        let Some(me) = t.my_item_id() else { return };
        let ratio = health::current_armor_ratio(&*t.world(), me);
        let Some(armor_ratio) = t.ok(ratio) else { return };
        let Some(threshold) = t.required_float("armorRatioThreshold") else { return };
        if armor_ratio < threshold {
            let Some(address) = t.required_address("thresholdReachedAddress") else { return };
            t.send_blackboard_value(&address, Value::Bool(true));
            t.request_reset();
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::blackboards::{Address, ScopeType};
    use crate::consts::{ATTRIBUTE_ARMOR_DAMAGE, ATTRIBUTE_ARMOR_HP};
    use crate::tree::Attrs;
    use crate::tree::tests::Rig;

    #[test]
    fn a_hit_that_leaves_the_armor_below_the_ratio_puts_the_flag_up_and_starts_the_tree_over() {
        let attrs = Attrs::new()
            .with("armorRatioThreshold", 0.5)
            .with("thresholdReachedAddress", Address::new(ScopeType::Item, "T").to_value());
        for (damage, reached) in [(40.0, false), (60.0, true)] {
            let mut rig = Rig::new();
            rig.world.attributes.insert((1, ATTRIBUTE_ARMOR_HP), 100.0);
            rig.world.attributes.insert((1, ATTRIBUTE_ARMOR_DAMAGE), damage);
            rig.run(Box::new(ArmorDamageThresholdMonitor), attrs.clone());
            let before = rig.ctx.statistics.reset_requests;
            {
                let mut cx = crate::tree::Cx {
                    core: &mut rig.tree.core,
                    ctx: &mut rig.ctx,
                    shared: &mut rig.shared,
                    world: &mut rig.world,
                };
                cx.send_event(1, ON_DAMAGE_CHANGED, &[]);
            }
            assert_eq!(rig.ctx.statistics.reset_requests, before + u64::from(reached));
            let flag = rig
                .shared
                .blackboards
                .message_channel(crate::blackboards::item_scope(1), "T")
                .unwrap()
                .last_message_value();
            assert_eq!(flag, if reached { Value::Bool(true) } else { Value::None });
        }
    }
}
