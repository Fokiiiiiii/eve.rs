//! `behaviors/actions/dungeon.py`.

use crate::tree::{Task, TaskCtx};
use crate::value::Value;

enter_task!(
    /// Selects the members of a team's groups that are within a distance of an item.
    SelectTeamMembersInRange,
    |t| {
        t.set_status_to_failed();
        let Some(max_distance) = t.value_at("maxDistanceAddress") else { return };
        let Some(reference) = t.value_at("referenceItemIdAddress") else { return };
        let Some(groups_by_team) = t.value_at("entityGroupsByTeamNameAddress") else { return };
        if !groups_by_team.truthy() {
            return;
        }
        let Some(team_name) = t.value_at("teamNameAddress") else { return };
        let group_ids = groups_by_team.dict_get(&team_name).cloned().unwrap_or(Value::None);
        if !group_ids.truthy() {
            return;
        }
        let Some(reference_id) = t.expect_id(&reference) else { return };
        let mut selected = Vec::new();
        for group in group_ids.items().unwrap_or(&[]) {
            let Some(group_id) = group.as_int() else { continue };
            let Some(members) = t.world().group_members(group_id) else { continue };
            for member_id in members {
                let distance = t.world().surface_dist(reference_id, member_id);
                let Some(distance) = t.ok(distance) else { return };
                if Value::Float(distance).py_le(&max_distance) {
                    selected.push(Value::Int(member_id));
                }
            }
        }
        let Some(address) = t.required_address("selectedItemsAddress") else { return };
        let none_selected = selected.is_empty();
        t.send_blackboard_value(&address, Value::List(selected));
        if none_selected {
            return;
        }
        t.set_status_to_success();
    }
);

/// Registers my group under a team name (`RegisterEntityGroupsByTeamName`). The dictionary is one object that everyone who reads it shares, and is
/// changed where it is.
#[derive(Debug, Default)]
pub struct RegisterEntityGroupsByTeamName;

impl Task for RegisterEntityGroupsByTeamName {
    fn type_name(&self) -> &'static str {
        "RegisterEntityGroupsByTeamName"
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        t.set_status_to_success();
        let Some(team_name) = t.value_at("teamNameAddress") else { return };
        let Some(address) = t.required_address("entityGroupsByTeamNameAddress") else { return };
        let last = t.get_last_blackboard_value(&address);
        let stored = last.truthy();
        let mut entities_by_group = if stored { last } else { Value::Dict(Vec::new()) };
        let mut group_ids_in_team =
            entities_by_group.dict_get(&team_name).cloned().unwrap_or_else(|| Value::set(std::iter::empty()));
        let Some(group_id) = t.ctx().my_entity_group_id else { return };
        if !group_ids_in_team.contains(&Value::Int(group_id)) {
            group_ids_in_team.set_add(Value::Int(group_id));
            entities_by_group.dict_insert(team_name, group_ids_in_team);
            if stored {
                t.with_channel(&address, |channel| {
                    if let Some(value) = channel.last_value_mut() {
                        *value = entities_by_group.clone();
                    }
                });
            }
            t.send_blackboard_value(&address, entities_by_group);
        }
    }
}

enter_task!(
    /// The place where a dungeon spawn is entered, put on a channel.
    GetDungeonSpawnEntryPoint,
    |t| {
        t.set_status_to_failed();
        let Some(spawn) = t.value_at("dungeonSpawnIdAddress") else { return };
        if !spawn.truthy() {
            return;
        }
        let Some(spawn_id) = t.expect_id(&spawn) else { return };
        if !t.world().does_spawn_exist(spawn_id) {
            return;
        }
        let entry = t.world().dungeon_spawn_entry_pos(spawn_id);
        let Some(entry) = entry else {
            t.raise(crate::env::WorldError::Key(spawn_id.to_string()).into());
            return;
        };
        let Some(address) = t.required_address("dungeonEntryCoordinatesAddress") else { return };
        t.send_blackboard_value(&address, Value::from_vec3(entry));
        t.set_status_to_success();
    }
);

enter_task!(
    /// Makes the dungeon spawn on a channel my dungeon spawn.
    SetContextDungeonSpawnId,
    |t| {
        let Some(spawn) = t.value_at("dungeonSpawnIdAddress") else { return };
        t.ctx().my_dungeon_spawn_id = spawn.as_int();
        // `ResetBlackboardAddressCache()`: the addresses here are looked up afresh every time, so there is no cache to reset
        t.set_status_to_success();
    }
);

enter_task!(
    /// The item that stands for an object of my dungeon spawn, put on a channel.
    GetItemIdForDungeonObject,
    |t| {
        let Some(object) = t.value_at("objectIdAddress") else { return };
        if !object.truthy() {
            t.set_status_to_failed();
            return;
        }
        let Some(spawn_id) = t.ctx().my_dungeon_spawn_id else {
            t.raise(crate::env::WorldError::Key("myDungeonSpawnId".to_string()).into());
            return;
        };
        let item_id = object.as_int().and_then(|object_id| t.world().dungeon_item_id_for_object(spawn_id, object_id)).filter(|i| *i != 0);
        let Some(item_id) = item_id else {
            t.set_status_to_failed();
            return;
        };
        let Some(address) = t.required_address("itemIdAddress") else { return };
        t.send_blackboard_value(&address, Value::Int(item_id));
        t.set_status_to_success();
    }
);

#[cfg(test)]
mod tests {
    use super::*;
    use crate::blackboards::{Address, ScopeType};
    use crate::env::testing::TestGroup;
    use crate::tree::tests::Rig;
    use crate::tree::{Attrs, Status};

    fn item(name: &str) -> Value {
        Address::new(ScopeType::Item, name).to_value()
    }

    #[test]
    fn the_members_of_a_teams_groups_within_the_distance_are_selected() {
        let attrs = Attrs::new()
            .with("maxDistanceAddress", item("Max"))
            .with("referenceItemIdAddress", item("Ref"))
            .with("entityGroupsByTeamNameAddress", item("Teams"))
            .with("teamNameAddress", item("Team"))
            .with("selectedItemsAddress", item("Selected"));
        let mut rig = Rig::new();
        rig.world.groups.insert(10, TestGroup { members: vec![5, 6], ..Default::default() });
        rig.world.set_dist(1, 5, 100.0);
        rig.world.set_dist(1, 6, 900.0);
        rig.post(ScopeType::Item, "Max", Value::Int(500));
        rig.post(ScopeType::Item, "Ref", Value::Int(1));
        rig.post(ScopeType::Item, "Team", Value::Str("red".into()));
        rig.post(
            ScopeType::Item,
            "Teams",
            Value::Dict(vec![(Value::Str("red".into()), Value::set([Value::Int(10), Value::Int(11)]))]),
        );
        assert_eq!(rig.run(Box::new(SelectTeamMembersInRange), attrs.clone()), Status::Success);
        assert_eq!(rig.last(ScopeType::Item, "Selected"), Value::List(vec![Value::Int(5)]));
        rig.post(ScopeType::Item, "Max", Value::Int(50));
        assert_eq!(rig.run(Box::new(SelectTeamMembersInRange), attrs), Status::Failure);
        assert_eq!(
            rig.last(ScopeType::Item, "Selected"),
            Value::List(vec![]),
            "an empty selection is still put on the channel"
        );
    }

    #[test]
    fn my_group_is_registered_under_its_team_once() {
        let attrs = Attrs::new()
            .with("teamNameAddress", item("Team"))
            .with("entityGroupsByTeamNameAddress", item("Teams"));
        let mut rig = Rig::new();
        rig.post(ScopeType::Item, "Team", Value::Str("red".into()));
        rig.run(Box::new(RegisterEntityGroupsByTeamName), attrs.clone());
        assert_eq!(
            rig.last(ScopeType::Item, "Teams"),
            Value::Dict(vec![(Value::Str("red".into()), Value::Set(vec![Value::Int(10)]))])
        );
        let sent = rig
            .shared
            .blackboards
            .message_channel(crate::blackboards::item_scope(1), "Teams")
            .unwrap()
            .messages()
            .len();
        rig.run(Box::new(RegisterEntityGroupsByTeamName), attrs);
        assert_eq!(
            rig.shared
                .blackboards
                .message_channel(crate::blackboards::item_scope(1), "Teams")
                .unwrap()
                .messages()
                .len(),
            sent
        );
    }

    #[test]
    fn the_entry_point_the_context_spawn_and_the_item_of_an_object() {
        let mut rig = Rig::new();
        rig.world.dungeon_spawns.insert(77);
        rig.world.dungeon_entries.insert(77, [1.0, 2.0, 3.0]);
        rig.post(ScopeType::Item, "Spawn", Value::Int(77));
        let attrs = Attrs::new()
            .with("dungeonSpawnIdAddress", item("Spawn"))
            .with("dungeonEntryCoordinatesAddress", item("Entry"));
        assert_eq!(rig.run(Box::new(GetDungeonSpawnEntryPoint), attrs.clone()), Status::Success);
        assert_eq!(rig.last(ScopeType::Item, "Entry"), Value::from_vec3([1.0, 2.0, 3.0]));
        assert_eq!(rig.run(Box::new(SetContextDungeonSpawnId), attrs), Status::Success);
        assert_eq!(rig.ctx.my_dungeon_spawn_id, Some(77));
        rig.world.dungeon_objects.insert((77, 9), 555);
        rig.post(ScopeType::Item, "Obj", Value::Int(9));
        let object = Attrs::new().with("objectIdAddress", item("Obj")).with("itemIdAddress", item("ItemId"));
        assert_eq!(rig.run(Box::new(GetItemIdForDungeonObject), object.clone()), Status::Success);
        assert_eq!(rig.last(ScopeType::Item, "ItemId"), Value::Int(555));
        rig.post(ScopeType::Item, "Obj", Value::Int(10));
        assert_eq!(rig.run(Box::new(GetItemIdForDungeonObject), object), Status::Failure);
    }
}
