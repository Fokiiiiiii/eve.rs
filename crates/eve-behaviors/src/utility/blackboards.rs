//! `behaviors/utility/blackboards.py`.

use crate::blackboards::Address;
pub use crate::env::ResponseThresholds;
use crate::tree::TaskCtx;
use crate::value::Value;

/// `update_blackboard_set_if_changed`: adds ids to the set on a channel and tells the channel, unless they were all in it already. The set on the
/// channel is one object that every reader shares, so it is changed in place before it is sent again.
pub fn update_blackboard_set_if_changed(
    t: &mut TaskCtx<'_, '_>,
    detected_ids: &[Value],
    address: &Address,
) -> bool {
    let current = t.get_last_blackboard_value(address);
    let mut id_set = match current {
        Value::None => {
            if detected_ids.is_empty() {
                return false;
            }
            Value::set(detected_ids.iter().cloned())
        }
        Value::Set(ref existing) => {
            if detected_ids.iter().all(|id| existing.contains(id)) {
                return false;
            }
            let mut merged = current.clone();
            for id in detected_ids {
                merged.set_add(id.clone());
            }
            // what every holder of the set sees changes with it
            t.with_channel(address, |channel| {
                if let Some(last) = channel.last_value_mut() {
                    *last = merged.clone();
                }
            });
            merged
        }
        other => other,
    };
    if !matches!(id_set, Value::Set(_)) {
        id_set = Value::set(std::iter::empty());
    }
    t.send_blackboard_value(address, id_set);
    true
}

/// `get_response_thresholds(task)`: the server works them out for the entity from its blackboards (`npcs.server.components`), `None` when it has none.
pub fn get_response_thresholds(t: &mut TaskCtx<'_, '_>) -> Option<ResponseThresholds> {
    let item = t.my_item_id()?;
    let group = t.ctx().my_entity_group_id;
    t.world().response_thresholds(item, group)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::blackboards::ScopeType;
    use crate::tree::tests::with_task;

    fn addr() -> Address {
        Address::new(ScopeType::Item, "SEEN")
    }

    #[test]
    fn a_set_is_started_by_the_first_ids_and_told_only_when_something_new_comes() {
        let (_, answer) = with_task(
            |_| {},
            |t| {
                let first = update_blackboard_set_if_changed(t, &[], &addr());
                let second = update_blackboard_set_if_changed(t, &[Value::Int(1), Value::Int(2)], &addr());
                let third = update_blackboard_set_if_changed(t, &[Value::Int(2)], &addr());
                let fourth = update_blackboard_set_if_changed(t, &[Value::Int(2), Value::Int(3)], &addr());
                (first, second, third, fourth, t.get_last_blackboard_value(&addr()))
            },
        );
        let (first, second, third, fourth, set) = answer.unwrap();
        assert_eq!((first, second, third, fourth), (false, true, false, true));
        assert_eq!(set, Value::Set(vec![Value::Int(1), Value::Int(2), Value::Int(3)]));
    }

    #[test]
    fn the_set_that_was_sent_before_changes_with_it() {
        let (_, answer) = with_task(
            |_| {},
            |t| {
                update_blackboard_set_if_changed(t, &[Value::Int(1)], &addr());
                update_blackboard_set_if_changed(t, &[Value::Int(2)], &addr());
                t.with_channel(&addr(), |c| c.messages().iter().map(|m| m.value.clone()).collect::<Vec<_>>())
            },
        );
        let history = answer.unwrap().unwrap();
        assert_eq!(history.len(), 2);
        assert_eq!(history[1], Value::Set(vec![Value::Int(1), Value::Int(2)]));
        assert_eq!(history[0], history[1], "the first message's set is the one that grew");
    }
}
