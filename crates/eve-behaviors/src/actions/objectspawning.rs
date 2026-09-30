//! `behaviors/actions/objectspawning.py`.

use crate::consts::FLAG_NONE;
use crate::env::{DungeonObject, FakeLocation};
use crate::tree::{Task, TaskCtx};
use crate::utility::ballparks::{get_my_ball, get_my_position};
use crate::utility::owner::get_owner_id;

/// What `_generate_replaced_objects` keeps of an object that is to be replaced.
struct Replacement {
    type_id: i64,
    count: i64,
    max_count: i64,
    type_list_id: i64,
}

/// Spawns the space objects of a dungeon's first room, some of them replaced by others of a type list.
#[derive(Debug, Default)]
pub struct SpawnSpaceObjectsFromDungeon;

impl SpawnSpaceObjectsFromDungeon {
    /// `_generate_replaced_objects`: three types, each replaced up to a random number of times. A type named twice keeps the last.
    fn generate_replaced_objects(t: &mut TaskCtx<'_, '_>) -> Option<Vec<Replacement>> {
        let mut replaced: Vec<Replacement> = Vec::new();
        for n in 1..=3 {
            let type_id = t.required_int(&format!("object{n}TypeIdForReplacing"))?;
            let min = t.required_int(&format!("object{n}MinCount"))?;
            let max = t.required_int(&format!("object{n}MaxCount"))?;
            let max_count = t.world().random_int(min, max);
            let type_list_id = t.required_int(&format!("eveTypeList{n}"))?;
            let entry = Replacement { type_id, count: 0, max_count, type_list_id };
            match replaced.iter_mut().find(|r| r.type_id == type_id) {
                Some(existing) => *existing = entry,
                None => replaced.push(entry),
            }
        }
        Some(replaced)
    }

    /// `_get_type_id_for_object_spawning`.
    fn type_id_for_object_spawning(
        t: &mut TaskCtx<'_, '_>,
        object_type_id: i64,
        replaced: &mut [Replacement],
    ) -> Option<i64> {
        let Some(replacement) = replaced.iter_mut().find(|r| r.type_id == object_type_id) else {
            return Some(object_type_id);
        };
        if replacement.count == replacement.max_count {
            return None;
        }
        let type_ids = t.world().type_ids_in_list(replacement.type_list_id);
        if type_ids.is_empty() {
            t.raise(
                crate::env::WorldError::Key("IndexError: cannot choose from an empty sequence".to_string())
                    .into(),
            );
            return None;
        }
        let at = t.world().random_index(type_ids.len());
        replacement.count += 1;
        Some(type_ids[at])
    }

    /// `_get_object_custom_info`.
    fn custom_info(object: &DungeonObject) -> crate::env::CustomInfo {
        let mut info = crate::env::CustomInfo::default();
        if let (Some(yaw), Some(pitch), Some(roll)) = (object.yaw, object.pitch, object.roll)
            && (yaw != 0.0 || pitch != 0.0 || roll != 0.0)
        {
            info.dun_rotation = Some([yaw, pitch, roll]);
        }
        if let Some(object_id) = object.object_id {
            info.dun_object_id = Some(object_id);
            info.dun_object_name_id = object.object_name_id;
        }
        info
    }
}

impl Task for SpawnSpaceObjectsFromDungeon {
    fn type_name(&self) -> &'static str {
        "SpawnSpaceObjectsFromDungeon"
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        t.set_status_to_success();
        let owner_id = get_owner_id(t);
        let Some(mut replaced) = Self::generate_replaced_objects(t) else { return };
        let Some(dungeon_id) = t.required_int("dungeonId") else { return };
        let assets = t.world().dungeon_room_objects(dungeon_id);
        let Some(mine) = get_my_ball(t) else { return };
        let Some(solar_system_id) = t.world().solar_system_id() else {
            t.raise(crate::env::WorldError::Runtime("no solar system".to_string()).into());
            return;
        };
        for object in assets {
            let custom_info = Self::custom_info(&object);
            let Some(type_id) = Self::type_id_for_object_spawning(t, object.type_id, &mut replaced) else {
                if t.faulted() {
                    return;
                }
                continue;
            };
            let position =
                [mine.position[0] + object.x, mine.position[1] + object.y, mine.position[2] + object.z];
            t.world().add_fake_location(FakeLocation {
                type_id,
                owner_id,
                solar_system_id,
                flag: FLAG_NONE,
                position,
                object_name_id: object.object_name_id,
                custom_info: Some(custom_info),
            });
        }
    }
}

/// `_get_spawn_coordinates`.
fn spawn_coordinates(t: &mut TaskCtx<'_, '_>) -> Option<Option<[f64; 3]>> {
    if t.has_attribute("coordinateAddress") {
        let value = t.value_at("coordinateAddress")?;
        if value.is_none() {
            return Some(None);
        }
        let position = value.as_vec3();
        if position.is_none() {
            t.raise(crate::tree::BehaviorError::Task(format!("TypeError: {value} is not a position")));
            return None;
        }
        return Some(position);
    }
    get_my_position(t).map(Some)
}

enter_task!(
    /// Spawns a space object of a type where a channel says (or where I am).
    SpawnSpaceObject,
    |t| {
        let owner_id = get_owner_id(t);
        let Some(coordinates) = spawn_coordinates(t) else { return };
        let Some(position) = coordinates else {
            t.set_status_to_failed();
            return;
        };
        let (Some(type_id), Some(solar_system_id)) = (t.required_int("typeId"), t.world().solar_system_id()) else { return };
        t.world().add_fake_location(FakeLocation { type_id, owner_id, solar_system_id, flag: FLAG_NONE, position, object_name_id: None, custom_info: None });
        t.set_status_to_success();
    }
);

enter_task!(
    /// Spawns a space object that is kept in the database.
    SpawnPersistentSpaceObject,
    |t| {
        let owner_id = get_owner_id(t);
        let Some(coordinates) = spawn_coordinates(t) else { return };
        let Some(position) = coordinates else {
            t.set_status_to_failed();
            return;
        };
        let (Some(type_id), Some(solar_system_id)) = (t.required_int("typeId"), t.world().solar_system_id()) else { return };
        t.world().insert_location(FakeLocation { type_id, owner_id, solar_system_id, flag: FLAG_NONE, position, object_name_id: None, custom_info: None });
        t.set_status_to_success();
    }
);

#[cfg(test)]
mod tests {
    use super::*;
    use crate::blackboards::{Address, ScopeType};
    use crate::env::{SlimItem, TestWorld};
    use crate::tree::tests::Rig;
    use crate::tree::{Attrs, Status};
    use crate::value::Value;

    fn rig() -> Rig {
        let mut rig = Rig::new();
        rig.ctx.my_slim_item = Some(SlimItem { owner_id: Some(1000006), ..Default::default() });
        rig.world.solar_system = Some(30000142);
        rig.world.add_ball(1, [100.0, 200.0, 300.0], 2);
        rig
    }

    #[test]
    fn a_space_object_is_spawned_where_i_am_or_where_a_channel_says_and_none_without_a_place_fails() {
        let mut rig = rig();
        assert_eq!(rig.run(Box::new(SpawnSpaceObject), Attrs::new().with("typeId", 600i64)), Status::Success);
        assert_eq!(
            rig.world.take_calls(),
            ["add_fake_location(600, Some(1000006), 30000142, [100.0, 200.0, 300.0], None)"]
        );
        let mut rig = self::rig();
        rig.post(ScopeType::Item, "At", Value::from_vec3([1.0, 2.0, 3.0]));
        let attrs = Attrs::new()
            .with("typeId", 600i64)
            .with("coordinateAddress", Address::new(ScopeType::Item, "At").to_value());
        rig.run(Box::new(SpawnPersistentSpaceObject), attrs.clone());
        assert_eq!(
            rig.world.take_calls(),
            ["insert_location(600, Some(1000006), 30000142, [1.0, 2.0, 3.0])"]
        );
        let mut rig = self::rig();
        assert_eq!(rig.run(Box::new(SpawnSpaceObject), attrs), Status::Failure);
    }

    #[test]
    fn a_dungeons_objects_are_spawned_around_me_and_some_are_replaced_up_to_their_counts() {
        let mut rig = rig();
        rig.world = TestWorld::with_numbers(&[0.999_999, 0.0, 0.0, 0.0, 0.0, 0.0]);
        rig.world.solar_system = Some(30000142);
        rig.world.add_ball(1, [100.0, 200.0, 300.0], 2);
        rig.world.type_ids_by_list.insert(9, vec![700, 701]);
        let object = |type_id: i64, x: f64| DungeonObject {
            type_id,
            x,
            y: 0.0,
            z: 0.0,
            object_name_id: Some(3),
            object_id: Some(88),
            yaw: Some(1.0),
            pitch: Some(0.0),
            roll: Some(0.0),
        };
        rig.world.dungeon_rooms.insert(5, vec![object(500, 10.0), object(501, 20.0), object(501, 30.0)]);
        let attrs = Attrs::new()
            .with("dungeonId", 5i64)
            .with("object1TypeIdForReplacing", 501i64)
            .with("object1MinCount", 0i64)
            .with("object1MaxCount", 1i64)
            .with("eveTypeList1", 9i64)
            .with("object2TypeIdForReplacing", 999i64)
            .with("object2MinCount", 0i64)
            .with("object2MaxCount", 0i64)
            .with("eveTypeList2", 9i64)
            .with("object3TypeIdForReplacing", 998i64)
            .with("object3MinCount", 0i64)
            .with("object3MaxCount", 0i64)
            .with("eveTypeList3", 9i64);
        assert_eq!(rig.run(Box::new(SpawnSpaceObjectsFromDungeon), attrs), Status::Success);
        assert_eq!(rig.last_error, None);
        let calls = rig.world.take_calls();
        assert_eq!(
            calls.len(),
            2,
            "the second object of a replaced type is not spawned once the count is reached: {calls:?}"
        );
        assert!(
            calls[0].starts_with("add_fake_location(500, Some(1000006), 30000142, [110.0, 200.0, 300.0]"),
            "{}",
            calls[0]
        );
        assert!(calls[1].starts_with("add_fake_location(700, "), "{}", calls[1]);
    }
}
