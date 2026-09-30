//! `behaviors/monitors/*`: tasks that watch for something and ask the tree to start over when it happens.

use crate::events::EventKey;
use crate::events::Subject;
use crate::tree::{Task, TaskCtx};
use crate::value::Value;

pub mod abyssal_deadspace;
pub mod ballparks;
pub mod blackboards;
pub mod combat;
pub mod combattargets;
pub mod damage;
pub mod dungeons;
pub mod effects;
pub mod entities;
pub mod groups;
pub mod logistics;
pub mod messages;
pub mod navigation;
pub mod reinforcements;
pub mod standings;
pub mod superweapon;
pub mod targeting;
pub mod timers;

/// The handler number of `MonitorTask._self_process_message`.
pub const HANDLER_MESSAGE: u16 = 0;

/// What a class built on the client's `MonitorTask` says: whose messages it listens to, which, and when one is to reset the tree.
pub trait Monitor: Send {
    /// The class name.
    fn type_name(&self) -> &'static str;

    /// `_get_item_id`: whom the messages are about; `None` fails the task. Mine unless it says otherwise.
    fn subject(&self, t: &mut TaskCtx<'_, '_>) -> Option<Subject> {
        t.ctx().my_item_id.map(Subject::Item)
    }

    /// `_get_message`: the message it listens to (`None` when it could not tell, and the tick was stopped).
    fn message(&self, t: &mut TaskCtx<'_, '_>) -> Option<EventKey>;

    /// `_can_process_message(*args)`.
    fn can_process_message(&mut self, _t: &mut TaskCtx<'_, '_>, _args: &[Value]) -> bool {
        true
    }

    /// `_self_process_message(*args)`: the tree starts over when the message can be processed.
    fn process_message(&mut self, t: &mut TaskCtx<'_, '_>, args: &[Value]) {
        if self.can_process_message(t, args) {
            t.request_reset();
        }
    }
}

/// `MonitorTask`: subscribes to the message when it is entered (and succeeds), hears it through `process_message`, and lets go when it is cleaned up.
#[derive(Debug, Default)]
pub struct MonitorTask<M>(pub M);

impl<M: Monitor> Task for MonitorTask<M> {
    fn type_name(&self) -> &'static str {
        self.0.type_name()
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        let Some(subject) = self.0.subject(t) else {
            t.set_status_to_failed();
            return;
        };
        let Some(key) = self.0.message(t) else { return };
        t.subscribe(subject, key, HANDLER_MESSAGE);
        t.set_status_to_success();
    }

    fn on_event(&mut self, t: &mut TaskCtx<'_, '_>, handler: u16, args: &[Value]) {
        if handler == HANDLER_MESSAGE {
            self.0.process_message(t, args);
        }
    }

    fn clean_up(&mut self, t: &mut TaskCtx<'_, '_>) {
        if !t.is_invalid() {
            if let Some(subject) = self.0.subject(t)
                && let Some(key) = self.0.message(t)
            {
                t.unsubscribe(subject, key, HANDLER_MESSAGE);
            }
            t.set_status_to_invalid();
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::events::message;
    use crate::tree::tests::Rig;
    use crate::tree::{Attrs, Status};

    struct OnDamage;

    impl Monitor for OnDamage {
        fn type_name(&self) -> &'static str {
            "OnDamage"
        }

        fn message(&self, _t: &mut TaskCtx<'_, '_>) -> Option<EventKey> {
            Some(message::ON_DAMAGE_CHANGED.into())
        }

        fn can_process_message(&mut self, _t: &mut TaskCtx<'_, '_>, args: &[Value]) -> bool {
            args.first().is_some_and(|v| v.truthy())
        }
    }

    #[test]
    fn a_monitor_subscribes_succeeds_and_asks_for_a_reset_when_it_hears_what_it_can_process() {
        let mut rig = Rig::new();
        assert_eq!(rig.run(Box::new(MonitorTask(OnDamage)), Attrs::new()), Status::Success);
        assert_eq!(rig.shared.events.subscription_count(), 1);
        rig.tree.core.record_messages = false;
        {
            let mut cx = crate::tree::Cx {
                core: &mut rig.tree.core,
                ctx: &mut rig.ctx,
                shared: &mut rig.shared,
                world: &mut rig.world,
            };
            cx.send_event(1, message::ON_DAMAGE_CHANGED, &[Value::Bool(false)]);
        }
        rig.tick().unwrap();
        // the root's own exit asked for a reset already; look at the statistics instead: who asked last
        let before = rig.ctx.statistics.reset_requests;
        {
            let mut cx = crate::tree::Cx {
                core: &mut rig.tree.core,
                ctx: &mut rig.ctx,
                shared: &mut rig.shared,
                world: &mut rig.world,
            };
            cx.send_event(1, message::ON_DAMAGE_CHANGED, &[Value::Bool(true)]);
        }
        assert_eq!(rig.ctx.statistics.reset_requests, before + 1);
        assert_eq!(rig.ctx.statistics.last_reset_requested_by.as_deref(), Some("OnDamage"));
    }

    #[test]
    fn a_monitor_with_no_one_to_listen_to_fails_and_a_cleaned_up_one_lets_go() {
        let mut rig = Rig::new();
        rig.ctx.my_item_id = None;
        assert_eq!(rig.run(Box::new(MonitorTask(OnDamage)), Attrs::new()), Status::Failure);
        let mut rig = Rig::new();
        rig.run(Box::new(MonitorTask(OnDamage)), Attrs::new());
        rig.tree.release(&mut rig.ctx, &mut rig.shared, &mut rig.world);
        assert_eq!(rig.shared.events.subscription_count(), 0);
    }
}
