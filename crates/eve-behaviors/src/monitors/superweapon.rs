//! `behaviors/monitors/superweapon.py`.

use crate::events::message::TURBO_SHIELD_STATE_CHANGED;
use crate::tree::{Task, TaskCtx};
use crate::value::Value;

/// Waits for a turbo shield (of my item) to be in a state.
#[derive(Debug, Default)]
pub struct WaitForTurboShieldState {
    has_component: bool,
}

impl WaitForTurboShieldState {
    /// `SendMessageStateChanged`.
    fn send_message_state_changed(t: &mut TaskCtx<'_, '_>) {
        if t.attribute("messageAddress").is_none() {
            return;
        }
        let Some(state) = t.value_at("messageAddress") else { return };
        if !state.truthy() {
            let Some(address) = t.required_address("messageAddress") else { return };
            t.send_blackboard_value(&address, Value::Bool(true));
        }
    }
}

impl Task for WaitForTurboShieldState {
    fn type_name(&self) -> &'static str {
        "WaitForTurboShieldState"
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        self.has_component = false;
        let Some(me) = t.my_item_id() else { return };
        let Some(wanted) = t.required_int("turboShieldState") else { return };
        // a `KeyError` from the component registry: there is no such component
        let Some(is_state) = t.world().turbo_shield_is_state(me, wanted) else {
            t.set_status_to_failed();
            return;
        };
        self.has_component = true;
        if is_state {
            t.set_status_to_success();
            Self::send_message_state_changed(t);
        } else {
            t.set_status_to_failed();
            t.subscribe_item(me, TURBO_SHIELD_STATE_CHANGED, 0);
        }
    }

    fn clean_up(&mut self, t: &mut TaskCtx<'_, '_>) {
        if t.is_invalid() {
            return;
        }
        if self.has_component
            && let Some(me) = t.my_item_id()
        {
            t.unsubscribe_item(me, TURBO_SHIELD_STATE_CHANGED, 0);
            self.has_component = false;
        }
        t.set_status_to_invalid();
    }

    fn on_event(&mut self, t: &mut TaskCtx<'_, '_>, _handler: u16, args: &[Value]) {
        if t.is_invalid() {
            return;
        }
        let Some(wanted) = t.required("turboShieldState") else { return };
        if args.first().is_some_and(|state| state.py_eq(&wanted)) {
            Self::send_message_state_changed(t);
            t.request_reset();
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::blackboards::{Address, ScopeType};
    use crate::tree::tests::Rig;
    use crate::tree::{Attrs, Status};

    fn attrs() -> Attrs {
        Attrs::new()
            .with("turboShieldState", 2i64)
            .with("messageAddress", Address::new(ScopeType::Item, "M").to_value())
    }

    #[test]
    fn a_shield_that_is_in_the_state_succeeds_and_says_so() {
        let mut rig = Rig::new();
        rig.world.turbo_shield_states.insert(1, 2);
        assert_eq!(rig.run(Box::new(WaitForTurboShieldState::default()), attrs()), Status::Success);
        assert_eq!(
            rig.shared
                .blackboards
                .message_channel(crate::blackboards::item_scope(1), "M")
                .unwrap()
                .last_message_value(),
            Value::Bool(true)
        );
    }

    #[test]
    fn a_shield_in_another_state_fails_and_starts_the_tree_over_when_it_gets_to_the_wanted_one_and_no_shield_fails()
     {
        let mut rig = Rig::new();
        rig.world.turbo_shield_states.insert(1, 1);
        assert_eq!(rig.run(Box::new(WaitForTurboShieldState::default()), attrs()), Status::Failure);
        let before = rig.ctx.statistics.reset_requests;
        {
            let mut cx = crate::tree::Cx {
                core: &mut rig.tree.core,
                ctx: &mut rig.ctx,
                shared: &mut rig.shared,
                world: &mut rig.world,
            };
            cx.send_event(1, TURBO_SHIELD_STATE_CHANGED, &[Value::Int(3)]);
            cx.send_event(1, TURBO_SHIELD_STATE_CHANGED, &[Value::Int(2)]);
        }
        assert_eq!(rig.ctx.statistics.reset_requests, before + 1);
        assert_eq!(Rig::new().run(Box::new(WaitForTurboShieldState::default()), attrs()), Status::Failure);
    }
}
