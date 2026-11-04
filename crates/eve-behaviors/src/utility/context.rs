//! `behaviors/utility/context.py`: the context a spawned entity's behavior is given.

use std::collections::HashMap;

use crate::tree::BehaviorContext;
use crate::value::Value;

pub const CONTEXT_MY_DUNGEON_SPAWN_ID: &str = "myDungeonSpawnId";
pub const CONTEXT_MY_DUNGEON_ID: &str = "myDungeonId";
pub const CONTEXT_MY_DUNGEON_SPAWN_SCENARIO_ID: &str = "myDungeonSpawnScenarioId";
pub const CONTEXT_MY_TALE_ID: &str = "myTaleId";
pub const CONTEXT_MY_SPAWN_POINT_POOL_ID: &str = "mySpawnPointPoolId";
pub const CONTEXT_MY_SPAWN_TABLE_ID: &str = "mySpawnTableId";
pub const CONTEXT_MY_ENTITY_GROUP_ID: &str = "myEntityGroupId";
pub const CONTEXT_MY_NODE_GRAPH_INSTANCE_ID: &str = "myNodeGraphInstanceId";

/// `CONTEXT_TO_COPY_TO_ITEM`.
pub const CONTEXT_TO_COPY_TO_ITEM: [&str; 6] = [
    CONTEXT_MY_DUNGEON_SPAWN_ID,
    CONTEXT_MY_DUNGEON_ID,
    CONTEXT_MY_TALE_ID,
    CONTEXT_MY_SPAWN_POINT_POOL_ID,
    CONTEXT_MY_SPAWN_TABLE_ID,
    CONTEXT_MY_NODE_GRAPH_INSTANCE_ID,
];

/// The extra context of a spawned entity: names and values.
pub type ExtraContext = HashMap<String, Value>;

/// `get_extra_context(task)`, given what the task's `get_spawn_pool_id()` and `get_spawn_table_id()` answer (`None` for a task that has no such
/// method).
pub fn get_extra_context(
    context: &BehaviorContext,
    spawn_pool_id: Option<i64>,
    spawn_table_id: Option<i64>,
) -> ExtraContext {
    let mut extra = ExtraContext::new();
    extra.insert(CONTEXT_MY_TALE_ID.to_string(), Value::None);
    copy_context_to_context(&mut extra, context);
    extra.insert(CONTEXT_MY_SPAWN_POINT_POOL_ID.to_string(), spawn_pool_id.map_or(Value::None, Value::Int));
    extra.insert(CONTEXT_MY_SPAWN_TABLE_ID.to_string(), spawn_table_id.map_or(Value::None, Value::Int));
    extra
}

/// A dungeon's spawn: what `get_dungeon_context_from_spawn` reads of it.
#[derive(Debug, Clone, Default)]
pub struct DungeonSpawn {
    pub spawn_id: i64,
    pub dungeon_id: i64,
    pub extra_context: ExtraContext,
}

/// `get_dungeon_context_from_spawn(spawn, scenario_id)`.
pub fn get_dungeon_context_from_spawn(spawn: &DungeonSpawn, scenario_id: Option<i64>) -> ExtraContext {
    let mut extra = ExtraContext::new();
    extra.insert(CONTEXT_MY_DUNGEON_SPAWN_ID.to_string(), Value::Int(spawn.spawn_id));
    extra.insert(CONTEXT_MY_DUNGEON_ID.to_string(), Value::Int(spawn.dungeon_id));
    extra.insert(
        CONTEXT_MY_DUNGEON_SPAWN_SCENARIO_ID.to_string(),
        scenario_id.map_or(Value::None, Value::Int),
    );
    for (name, value) in &spawn.extra_context {
        extra.insert(name.clone(), value.clone());
    }
    extra
}

/// `get_spawn_point_pool_context(spawn_point_pool_id, spawn_table_id)`.
pub fn get_spawn_point_pool_context(
    spawn_point_pool_id: Option<i64>,
    spawn_table_id: Option<i64>,
) -> ExtraContext {
    let mut extra = ExtraContext::new();
    extra.insert(
        CONTEXT_MY_SPAWN_POINT_POOL_ID.to_string(),
        spawn_point_pool_id.map_or(Value::None, Value::Int),
    );
    extra.insert(CONTEXT_MY_SPAWN_TABLE_ID.to_string(), spawn_table_id.map_or(Value::None, Value::Int));
    extra
}

/// `copy_context_to_context(to_context, from_context)`: the names of `CONTEXT_TO_COPY_TO_ITEM` the source has.
pub fn copy_context_to_context(to_context: &mut ExtraContext, from_context: &BehaviorContext) {
    for name in CONTEXT_TO_COPY_TO_ITEM {
        if let Some(value) = from_context.get(name) {
            to_context.insert(name.to_string(), value);
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_extra_context_takes_what_is_copied_and_the_pool_and_the_table_and_no_tale() {
        let context = BehaviorContext {
            my_dungeon_spawn_id: Some(7),
            my_tale_id: Some(9),
            my_item_id: Some(1),
            ..Default::default()
        };
        let extra = get_extra_context(&context, Some(3), None);
        assert_eq!(extra[CONTEXT_MY_DUNGEON_SPAWN_ID], Value::Int(7));
        assert_eq!(
            extra[CONTEXT_MY_TALE_ID],
            Value::Int(9),
            "the copy comes after the tale is set to none, as in the client"
        );
        assert_eq!(extra[CONTEXT_MY_SPAWN_POINT_POOL_ID], Value::Int(3));
        assert_eq!(extra[CONTEXT_MY_SPAWN_TABLE_ID], Value::None);
        assert!(!extra.contains_key("myItemId") && !extra.contains_key(CONTEXT_MY_DUNGEON_ID));
    }

    #[test]
    fn a_dungeon_spawns_context_has_its_ids_and_its_own_extras() {
        let spawn = DungeonSpawn {
            spawn_id: 5,
            dungeon_id: 6,
            extra_context: [("x".to_string(), Value::Int(1))].into(),
        };
        let extra = get_dungeon_context_from_spawn(&spawn, Some(8));
        assert_eq!(extra[CONTEXT_MY_DUNGEON_ID], Value::Int(6));
        assert_eq!(extra[CONTEXT_MY_DUNGEON_SPAWN_SCENARIO_ID], Value::Int(8));
        assert_eq!(extra["x"], Value::Int(1));
    }
}
