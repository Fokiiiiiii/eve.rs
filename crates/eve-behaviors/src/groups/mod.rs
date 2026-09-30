//! `behaviors/groups/*`: the key a group's behavior is stored under and what the tasks of a group share.

pub mod mixin;

/// `GroupBehaviorKey(groupId)`.
pub fn group_behavior_key(group_id: i64) -> (&'static str, i64) {
    ("group", group_id)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_groups_behavior_key_is_group_and_its_id() {
        assert_eq!(group_behavior_key(7), ("group", 7));
    }
}
