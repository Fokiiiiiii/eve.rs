//! `behaviors/monitors/messages.py`: what an entity hears it says to others.

use crate::events::{EventKey, MessageId};
use crate::monitors::Monitor;
use crate::tree::TaskCtx;
use crate::utility::inventory::is_my_cargo_full;
use crate::value::Value;

/// `SendMessageOnMessageMonitor` and `SendMessageOnMessageWhenCargoIsFullMonitor`: when the monitored message comes, another message is sent about my
/// item with the same arguments (the second only while my cargo is full).
#[derive(Debug, Default)]
pub struct SendMessageOnMessageMonitor {
    pub when_cargo_is_full: bool,
}

impl SendMessageOnMessageMonitor {
    pub fn when_cargo_is_full() -> Self {
        Self { when_cargo_is_full: true }
    }
}

/// A message id an authored attribute holds.
fn message_from_attribute(t: &mut TaskCtx<'_, '_>, name: &str) -> Option<MessageId> {
    let id = t.required_int(name)?;
    Some(MessageId::Number(id))
}

impl Monitor for SendMessageOnMessageMonitor {
    fn type_name(&self) -> &'static str {
        if self.when_cargo_is_full {
            "SendMessageOnMessageWhenCargoIsFullMonitor"
        } else {
            "SendMessageOnMessageMonitor"
        }
    }

    fn message(&self, t: &mut TaskCtx<'_, '_>) -> Option<EventKey> {
        message_from_attribute(t, "monitoredMessageId").map(EventKey::of)
    }

    fn process_message(&mut self, t: &mut TaskCtx<'_, '_>, args: &[Value]) {
        let can = if self.when_cargo_is_full { is_my_cargo_full(t) } else { true };
        if can {
            let Some(notified) = message_from_attribute(t, "notifiedMessageId") else { return };
            let Some(me) = t.my_item_id() else { return };
            t.send_event(me, EventKey::of(notified), args);
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::env::CargoCapacity;
    use crate::monitors::MonitorTask;
    use crate::tree::tests::Rig;
    use crate::tree::{Attrs, Status, Task, TaskCtx as Ctx};

    /// Hears the notified message and keeps its arguments.
    struct Ear(std::rc::Rc<std::cell::RefCell<Vec<Vec<Value>>>>);

    impl Task for Ear {
        fn type_name(&self) -> &'static str {
            "Ear"
        }
        fn on_enter(&mut self, t: &mut Ctx<'_, '_>) {
            t.subscribe_item(1, MessageId::Number(20), 0);
            t.set_status_to_suspended();
        }
        fn on_event(&mut self, _t: &mut Ctx<'_, '_>, _handler: u16, args: &[Value]) {
            self.0.borrow_mut().push(args.to_vec());
        }
    }

    fn setup(rig: &mut Rig, full: bool) -> std::rc::Rc<std::cell::RefCell<Vec<Vec<Value>>>> {
        let heard = std::rc::Rc::default();
        let ear = rig.tree.add_task(Box::new(Ear(std::rc::Rc::clone(&heard))), Attrs::new());
        let monitor = rig.tree.add_task(
            Box::new(MonitorTask(if full {
                SendMessageOnMessageMonitor::when_cargo_is_full()
            } else {
                SendMessageOnMessageMonitor::default()
            })),
            Attrs::new().with("monitoredMessageId", 10i64).with("notifiedMessageId", 20i64),
        );
        let root = rig.sequence(&[ear, monitor]);
        rig.tree.start_root_task(root);
        rig.tick().unwrap();
        // the ear suspends its sequence: start the monitor by hand
        assert_eq!(rig.tree.status_of(ear), Status::Suspended);
        {
            let mut cx = crate::tree::Cx {
                core: &mut rig.tree.core,
                ctx: &mut rig.ctx,
                shared: &mut rig.shared,
                world: &mut rig.world,
            };
            cx.start_task(monitor);
        }
        rig.tick().unwrap();
        heard
    }

    #[test]
    fn the_monitored_message_is_sent_on_as_the_notified_one_with_its_arguments() {
        let mut rig = Rig::new();
        let heard = setup(&mut rig, false);
        let mut cx = crate::tree::Cx {
            core: &mut rig.tree.core,
            ctx: &mut rig.ctx,
            shared: &mut rig.shared,
            world: &mut rig.world,
        };
        cx.send_event(1, MessageId::Number(10), &[Value::Int(5), Value::Int(6)]);
        assert_eq!(*heard.borrow(), [vec![Value::Int(5), Value::Int(6)]]);
    }

    #[test]
    fn the_cargo_variant_sends_only_while_the_cargo_is_full() {
        for (used, sent) in [(100.0, 1), (10.0, 0)] {
            let mut rig = Rig::new();
            rig.world.cargo.insert(1, CargoCapacity { used, capacity: 100.0 });
            let heard = setup(&mut rig, true);
            let mut cx = crate::tree::Cx {
                core: &mut rig.tree.core,
                ctx: &mut rig.ctx,
                shared: &mut rig.shared,
                world: &mut rig.world,
            };
            cx.send_event(1, MessageId::Number(10), &[]);
            assert_eq!(heard.borrow().len(), sent);
        }
    }
}
