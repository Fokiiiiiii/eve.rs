//! `behaviors/utility/roles.py`.

use crate::constants::behaviorroles::{COMBAT_ROLES, ROLE_LOGISTIC};
use crate::constants::blackboardchannels::{ROLE_MESSAGE_CHANNEL, commander_address};
use crate::tree::TaskCtx;
use crate::value::Value;

/// `get_role_for_member`: the role on the member's `ROLE` channel.
pub fn get_role_for_member(t: &mut TaskCtx<'_, '_>, item_id: i64) -> Value {
    let channel = t.channel_for_item(item_id, ROLE_MESSAGE_CHANNEL);
    t.channel_last_value(&channel)
}

/// `get_group_commander`.
pub fn get_group_commander(t: &mut TaskCtx<'_, '_>) -> Value {
    t.get_last_blackboard_value(&commander_address())
}

/// `is_commander`.
pub fn is_commander(t: &mut TaskCtx<'_, '_>, item_id: i64) -> bool {
    get_group_commander(t).py_eq(&Value::Int(item_id))
}

/// `has_combat_role`.
pub fn has_combat_role(t: &mut TaskCtx<'_, '_>, item_id: i64) -> bool {
    let role = get_role_for_member(t, item_id);
    role.as_str().is_some_and(|r| COMBAT_ROLES.contains(&r))
}

/// `is_logistic_role`.
pub fn is_logistic_role(t: &mut TaskCtx<'_, '_>, item_id: i64) -> bool {
    get_role_for_member(t, item_id).as_str() == Some(ROLE_LOGISTIC)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::tree::tests::with_task;

    #[test]
    fn a_member_has_the_role_on_its_channel_and_the_commander_is_on_the_groups() {
        let (_, answer) = with_task(
            |_| {},
            |t| {
                let channel = t.channel_for_item(5, ROLE_MESSAGE_CHANNEL);
                t.channel_send(&channel, Value::Str("logistic".into()));
                let other = t.channel_for_item(6, ROLE_MESSAGE_CHANNEL);
                t.channel_send(&other, Value::Str("miner".into()));
                t.send_blackboard_value(&commander_address(), Value::Int(6));
                (
                    is_logistic_role(t, 5),
                    has_combat_role(t, 5),
                    has_combat_role(t, 6),
                    has_combat_role(t, 7),
                    is_commander(t, 6),
                    is_commander(t, 5),
                )
            },
        );
        assert_eq!(answer, Some((true, true, false, false, true, false)));
    }
}
