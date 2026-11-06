//! `behaviors/actions/hauling.py`: which ball a hauler picks up next. The client keeps the balls in a deque that it changes in place.

use crate::blackboards::Address;
use crate::tree::TaskCtx;
use crate::value::Value;

/// `_GetAvailableBallIds`: the list on the channel (`None` gives a new empty one).
fn available_ball_ids(t: &mut TaskCtx<'_, '_>) -> Option<(Address, Value, bool)> {
    let address = t.required_address("availableBallsAddress")?;
    let last = t.get_last_blackboard_value(&address);
    Some(match last {
        Value::None => (address, Value::List(Vec::new()), false),
        other => (address, other, true),
    })
}

/// The stored list is what every holder of it sees: it is changed where it is.
fn store_in_place(t: &mut TaskCtx<'_, '_>, address: &Address, value: &Value) {
    t.with_channel(address, |channel| {
        if let Some(last) = channel.last_value_mut() {
            *last = value.clone();
        }
    });
}

enter_task!(
    /// Takes a ball off the end of the balls waiting for a pick-up and puts it on a channel.
    GetHaulerPickUpDestination,
    |t| {
        t.set_status_to_failed();
        let Some((address, mut balls, stored)) = available_ball_ids(t) else { return };
        let chosen = match &mut balls {
            Value::List(items) => items.pop(),
            _ => None,
        };
        if stored {
            store_in_place(t, &address, &balls);
        }
        if let Some(chosen) = chosen {
            let Some(chosen_address) = t.required_address("chosenBallAddress") else { return };
            t.send_blackboard_value(&chosen_address, chosen);
            t.set_status_to_success();
        }
        t.send_blackboard_value(&address, balls);
    }
);

enter_task!(
    /// Puts my item at the front of the balls waiting for a pick-up.
    AddMeToHaulerPickUpDestination,
    |t| {
        t.set_status_to_success();
        let Some((address, mut balls, stored)) = available_ball_ids(t) else { return };
        let Some(me) = t.my_item_id() else { return };
        if balls.contains(&Value::Int(me)) {
            return;
        }
        if let Value::List(items) = &mut balls {
            items.insert(0, Value::Int(me));
        }
        if stored {
            store_in_place(t, &address, &balls);
        }
        t.send_blackboard_value(&address, balls);
    }
);

#[cfg(test)]
mod tests {
    use super::*;
    use crate::blackboards::ScopeType;
    use crate::tree::tests::Rig;
    use crate::tree::{Attrs, Status};

    fn attrs() -> Attrs {
        Attrs::new()
            .with("availableBallsAddress", Address::new(ScopeType::Item, "Balls").to_value())
            .with("chosenBallAddress", Address::new(ScopeType::Item, "Chosen").to_value())
    }

    #[test]
    fn a_hauler_takes_the_last_ball_and_puts_back_the_rest() {
        let mut rig = Rig::new();
        rig.post(ScopeType::Item, "Balls", Value::List(vec![Value::Int(4), Value::Int(5), Value::Int(6)]));
        assert_eq!(rig.run(Box::new(GetHaulerPickUpDestination), attrs()), Status::Success);
        assert_eq!(rig.last(ScopeType::Item, "Chosen"), Value::Int(6));
        assert_eq!(rig.last(ScopeType::Item, "Balls"), Value::List(vec![Value::Int(4), Value::Int(5)]));
        // no balls: it fails and says none are left
        let mut rig = Rig::new();
        assert_eq!(rig.run(Box::new(GetHaulerPickUpDestination), attrs()), Status::Failure);
        assert_eq!(rig.last(ScopeType::Item, "Balls"), Value::List(vec![]));
    }

    #[test]
    fn i_put_myself_at_the_front_once() {
        let mut rig = Rig::new();
        rig.post(ScopeType::Item, "Balls", Value::List(vec![Value::Int(4)]));
        rig.run(Box::new(AddMeToHaulerPickUpDestination), attrs());
        assert_eq!(rig.last(ScopeType::Item, "Balls"), Value::List(vec![Value::Int(1), Value::Int(4)]));
        let count = rig
            .shared
            .blackboards
            .message_channel(crate::blackboards::item_scope(1), "Balls")
            .unwrap()
            .messages()
            .len();
        rig.run(Box::new(AddMeToHaulerPickUpDestination), attrs());
        let after = rig
            .shared
            .blackboards
            .message_channel(crate::blackboards::item_scope(1), "Balls")
            .unwrap()
            .messages()
            .len();
        assert_eq!(count, after, "already there: nothing is sent");
    }
}
