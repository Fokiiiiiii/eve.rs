//! `behaviors/actions/ballparks.py`, the part that works with items and their slim items: looting an item, and storing a slim item's field.

use crate::env::{SlimItem, WorldError};
use crate::tree::TaskCtx;
use crate::value::Value;

/// `getattr(slimItem, name, None)` of a field the slim item has as a named member; the fields a behavior stores are the world's.
fn slim_item_field(t: &mut TaskCtx<'_, '_>, slim: &SlimItem, name: &str) -> Value {
    match name {
        "itemID" => Value::Int(slim.item_id),
        "typeID" => Value::Int(slim.type_id),
        "groupID" => Value::Int(slim.group_id),
        "categoryID" => Value::Int(slim.category_id),
        "ownerID" => slim.owner_id.map_or(Value::None, Value::Int),
        "charID" => slim.char_id.map_or(Value::None, Value::Int),
        other => t.world().slim_item_attribute(slim.item_id, other),
    }
}

enter_task!(
    /// Takes an item out of the park and into the loot of my ship.
    EntityLootItem,
    |t| {
        let Some(item) = t.value_at("itemIdAddress") else { return };
        if !item.truthy() {
            t.set_status_to_failed();
            return;
        }
        let Some(item_id) = t.expect_id(&item) else { return };
        let Some(item) = t.world().inventory_item(item_id) else {
            t.raise(WorldError::Runtime(format!("no item {item_id}")).into());
            return;
        };
        t.world().remove_item_from_park(item_id);
        let Some(my_ball) = crate::utility::ballparks::get_my_ball(t) else { return };
        t.world().add_type_and_amount_to_additional_loot(my_ball.id, item.type_id, None, None);
        t.set_status_to_success();
    }
);

enter_task!(
    /// Stores a field of the slim item of a target in its item settings, when its type has the component for it and the field is one it keeps.
    StoreSlimItemFieldInItemSettingsAction,
    |t| {
        let Some(target) = t.value_at("targetAddress") else { return };
        let Some(target_id) = target.as_int() else {
            t.set_status_to_failed();
            return;
        };
        let Some(slim) = t.world().slim_item(target_id) else {
            t.set_status_to_failed();
            return;
        };
        if !t.world().has_store_slim_item_field_component(slim.type_id) {
            t.set_status_to_failed();
            return;
        }
        let Some(field_name) = t.value_at("slimItemFieldName") else { return };
        let Some(field) = field_name.as_str().map(str::to_string) else {
            t.set_status_to_failed();
            return;
        };
        if !t.world().persistent_slim_item_attributes_for_type(slim.type_id).contains(&field) {
            t.set_status_to_failed();
            return;
        }
        let Some(field_value) = t.value_at("slimItemFieldValue") else { return };
        if field_value.is_none() {
            t.set_status_to_failed();
            return;
        }
        t.world().save_slim_item_setting(target_id, &field, &field_value);
        t.set_status_to_success();
    }
);

enter_task!(
    /// Puts what the blackboard holds in a field of a slim item.
    StoreBlackboardToSlimItemAttribute,
    |t| {
        let Some(target) = t.value_at("targetAddress") else { return };
        let Some(message) = t.value_at("messageAddress") else { return };
        let Some(attribute_name) = t.required_string("attributeName") else { return };
        let Some(target_id) = t.expect_id(&target) else { return };
        t.world().update_slim_item_field(target_id, &attribute_name, message);
        t.set_status_to_success();
    }
);

enter_task!(
    /// Puts a field of a slim item on the blackboard (nothing where there is none).
    StoreSlimItemAttributeToBlackboard,
    |t| {
        let Some(target) = t.value_at("targetAddress") else { return };
        let Some(attribute_name) = t.required_string("attributeName") else { return };
        let slim = target.as_int().and_then(|target_id| t.world().slim_item(target_id));
        let value = match slim {
            Some(slim) => slim_item_field(t, &slim, &attribute_name),
            None => Value::None,
        };
        let Some(address) = t.required_address("messageAddress") else { return };
        t.send_blackboard_value(&address, value);
        t.set_status_to_success();
    }
);

#[cfg(test)]
mod tests {
    use super::*;
    use crate::blackboards::{Address, ScopeType};
    use crate::tree::tests::Rig;
    use crate::tree::{Attrs, BehaviorError, Status};

    fn item(name: &str) -> Value {
        Address::new(ScopeType::Item, name).to_value()
    }

    fn rig() -> Rig {
        let mut rig = Rig::new();
        rig.world.add_ball(1, [0.0; 3], 500);
        rig.world.add_ball(2, [10.0, 0.0, 0.0], 501);
        rig.world.slims.get_mut(&2).unwrap().owner_id = Some(7);
        rig.world.slims.get_mut(&2).unwrap().group_id = 25;
        rig.world.slims.get_mut(&2).unwrap().category_id = 6;
        rig
    }

    // ---- EntityLootItem ----

    #[test]
    fn an_item_is_taken_out_of_the_park_and_its_type_goes_into_my_loot() {
        let mut rig = rig();
        rig.post(ScopeType::Item, "Item", Value::Int(2));
        let attrs = Attrs::new().with("itemIdAddress", item("Item"));
        assert_eq!(rig.run(Box::new(EntityLootItem), attrs.clone()), Status::Success);
        assert_eq!(
            rig.world.take_calls(),
            ["remove_item_from_park(2)", "add_additional_loot(1, 501, -, None)"]
        );
        rig.post(ScopeType::Item, "Item", Value::None);
        assert_eq!(rig.run(Box::new(EntityLootItem), attrs.clone()), Status::Failure);
        rig.post(ScopeType::Item, "Item", Value::Int(99));
        rig.run(Box::new(EntityLootItem), attrs);
        assert!(matches!(rig.last_error, Some(BehaviorError::World(WorldError::Runtime(_)))));
        assert!(rig.world.take_calls().is_empty());
    }

    // ---- StoreSlimItemFieldInItemSettingsAction ----

    type Spoil = fn(&mut Rig);

    fn settings_attrs() -> Attrs {
        Attrs::new()
            .with("targetAddress", item("Target"))
            .with("slimItemFieldName", item("Field"))
            .with("slimItemFieldValue", item("Value"))
    }

    fn settings_rig() -> Rig {
        let mut rig = rig();
        rig.world.persistent_slim_attributes.insert(501, vec!["poseID".to_string(), "other".to_string()]);
        rig.post(ScopeType::Item, "Target", Value::Int(2));
        rig.post(ScopeType::Item, "Field", Value::Str("poseID".into()));
        rig.post(ScopeType::Item, "Value", Value::Int(3));
        rig
    }

    #[test]
    fn a_field_that_the_type_keeps_is_stored_in_the_item_settings() {
        let mut rig = settings_rig();
        assert_eq!(
            rig.run(Box::new(StoreSlimItemFieldInItemSettingsAction), settings_attrs()),
            Status::Success
        );
        assert_eq!(rig.world.take_calls(), ["save_slim_item_setting(2, poseID, Int(3))"]);
        // a value of nothing is nothing to store, but a value of 0 is a value
        let mut rig = settings_rig();
        rig.post(ScopeType::Item, "Value", Value::Int(0));
        assert_eq!(
            rig.run(Box::new(StoreSlimItemFieldInItemSettingsAction), settings_attrs()),
            Status::Success
        );
    }

    #[test]
    fn nothing_is_stored_without_a_target_a_component_a_field_that_is_kept_or_a_value() {
        let spoilers: [(&str, Spoil); 7] = [
            ("no target", |rig| rig.post(ScopeType::Item, "Target", Value::None)),
            ("no slim item", |rig| rig.post(ScopeType::Item, "Target", Value::Int(99))),
            ("no component", |rig| {
                rig.world.persistent_slim_attributes.clear();
            }),
            ("no field name", |rig| rig.post(ScopeType::Item, "Field", Value::None)),
            ("a field name that is no text", |rig| rig.post(ScopeType::Item, "Field", Value::Int(4))),
            ("a field that is not kept", |rig| {
                rig.post(ScopeType::Item, "Field", Value::Str("secret".into()))
            }),
            ("no value", |rig| rig.post(ScopeType::Item, "Value", Value::None)),
        ];
        for (what, spoil) in spoilers {
            let mut rig = settings_rig();
            spoil(&mut rig);
            assert_eq!(
                rig.run(Box::new(StoreSlimItemFieldInItemSettingsAction), settings_attrs()),
                Status::Failure,
                "{what}"
            );
            assert_eq!(rig.last_error, None, "{what}");
            assert!(rig.world.take_calls().is_empty(), "{what}");
        }
    }

    // ---- StoreBlackboardToSlimItemAttribute, StoreSlimItemAttributeToBlackboard ----

    #[test]
    fn a_blackboard_value_is_put_in_a_field_of_a_slim_item() {
        let mut rig = rig();
        rig.post(ScopeType::Item, "Target", Value::Int(2));
        rig.post(ScopeType::Item, "Message", Value::Str("hello".into()));
        let attrs = Attrs::new()
            .with("targetAddress", item("Target"))
            .with("messageAddress", item("Message"))
            .with("attributeName", "greeting");
        assert_eq!(rig.run(Box::new(StoreBlackboardToSlimItemAttribute), attrs), Status::Success);
        assert_eq!(rig.world.take_calls(), ["update_slim_item_field(2, greeting, Str(\"hello\"))"]);
    }

    #[test]
    fn a_field_of_a_slim_item_is_put_on_the_blackboard_and_a_slim_item_that_is_not_there_has_none() {
        let read = |target: Value, name: &str| {
            let mut rig = rig();
            rig.post(ScopeType::Item, "Target", target);
            rig.world.slim_attributes.insert((2, "greeting".to_string()), Value::Str("hello".into()));
            rig.post(ScopeType::Item, "Out", Value::Int(1));
            let attrs = Attrs::new()
                .with("targetAddress", item("Target"))
                .with("messageAddress", item("Out"))
                .with("attributeName", name);
            let status = rig.run(Box::new(StoreSlimItemAttributeToBlackboard), attrs);
            (status, rig.last(ScopeType::Item, "Out"))
        };
        assert_eq!(read(Value::Int(2), "typeID"), (Status::Success, Value::Int(501)));
        assert_eq!(read(Value::Int(2), "groupID"), (Status::Success, Value::Int(25)));
        assert_eq!(read(Value::Int(2), "categoryID"), (Status::Success, Value::Int(6)));
        assert_eq!(read(Value::Int(2), "ownerID"), (Status::Success, Value::Int(7)));
        assert_eq!(read(Value::Int(2), "itemID"), (Status::Success, Value::Int(2)));
        assert_eq!(read(Value::Int(2), "charID"), (Status::Success, Value::None));
        assert_eq!(read(Value::Int(2), "greeting"), (Status::Success, Value::Str("hello".into())));
        assert_eq!(read(Value::Int(2), "nothing"), (Status::Success, Value::None));
        assert_eq!(read(Value::Int(99), "typeID"), (Status::Success, Value::None));
        assert_eq!(read(Value::None, "typeID"), (Status::Success, Value::None));
    }
}
