//! `behaviors/actions/operations.py`.

use crate::value::Value;

enter_task!(
    /// Where the operation site of a character is: its solar system, its spawn and its coordinates, put on channels.
    GetOperationSiteSpawn,
    |t| {
        let Some(character) = t.value_at("characterIdAddress") else { return };
        let Some(site_id) = t.required("operationSiteId") else { return };
        let position = t.world().operation_spawnpoint_position(character.as_int(), site_id.as_int());
        let Some((solar_system_id, spawn_id, x, y, z)) = t.ok(position) else { return };
        let (Some(system), Some(spawn), Some(coordinates)) =
            (t.required_address("siteSolarSystemIdAddress"), t.required_address("siteSpawnIdAddress"), t.required_address("siteCoordinatesAddress"))
        else {
            return;
        };
        t.send_blackboard_value(&system, Value::Int(solar_system_id));
        t.send_blackboard_value(&spawn, Value::Int(spawn_id));
        t.send_blackboard_value(&coordinates, Value::from_vec3([x, y, z]));
        t.set_status_to_success();
    }
);
