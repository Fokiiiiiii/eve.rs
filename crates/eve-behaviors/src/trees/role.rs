//! `behaviors/trees/role.py`: an entity says what its role in the group is.

use crate::blackboards::{Address, ScopeType};

use super::builder::{Node, sequence};

/// `ROLE_ADDRESS`.
pub fn role_address() -> Address {
    Address::new(ScopeType::Item, "ROLE")
}

/// `GROUP_MEMBER_ROLE_ASSIGNED_ADDRESS`.
pub fn group_member_role_assigned_address() -> Address {
    Address::new(ScopeType::EntityGroup, "GROUP_MEMBER_ROLE_ASSIGNED")
}

/// `CreateRegisterEntityRole(role)`.
pub fn create_register_entity_role(role: &str) -> Node {
    sequence()
        .named("Registering Entity Role")
        .add(Node::class("IsBlackboardValueNone").with("valueAddress", role_address()))
        .add(
            Node::class("BlackboardSetMessageToStringValueAction")
                .named("Remember my role")
                .with("messageAddress", role_address())
                .with("value", role),
        )
        .add(
            Node::class("BlackboardSetMessageToBooleanValueAction")
                .named("Notify group on role")
                .with("messageAddress", group_member_role_assigned_address())
                .with("value", true),
        )
}
