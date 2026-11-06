//! `behaviors/utility/components.py`: the proximity sensors of the ballpark's space components.

use crate::env::ProximitySensor;
use crate::groups::mixin::get_member_id_list;
use crate::tree::TaskCtx;

/// `get_proximity_sensor`.
pub fn get_proximity_sensor(t: &mut TaskCtx<'_, '_>, proximity_sensor_id: i64) -> Option<ProximitySensor> {
    t.world().proximity_sensor(proximity_sensor_id)
}

/// `get_proximity_sensors`: the sensors that have every one of the tags.
pub fn get_proximity_sensors(t: &mut TaskCtx<'_, '_>, tags: &[String]) -> Vec<ProximitySensor> {
    t.world().proximity_sensors(tags)
}

/// `get_proximity_sensors_for_group_members`.
pub fn get_proximity_sensors_for_group_members(t: &mut TaskCtx<'_, '_>) -> Vec<ProximitySensor> {
    let member_ids = get_member_id_list(t);
    let sensors = get_proximity_sensors(t, &[]);
    sensors.into_iter().filter(|sensor| member_ids.contains(&sensor.item_id)).collect()
}

/// `create_proximity_sensor`: a sensor for an item, as far as it sees.
pub fn create_proximity_sensor(
    t: &mut TaskCtx<'_, '_>,
    item_id: i64,
    aggression_range: f64,
) -> Option<ProximitySensor> {
    let type_id = t.world().inventory_item(item_id).map(|item| item.type_id);
    let Some(type_id) = type_id else {
        t.raise(crate::env::WorldError::Runtime(format!("no item {item_id}")).into());
        return None;
    };
    t.world().add_proximity_sensor(item_id, type_id, aggression_range)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::env::testing::TestGroup;
    use crate::tree::tests::with_task;

    #[test]
    fn sensors_are_found_by_tags_and_by_the_members_of_my_group() {
        let (_, answer) = with_task(
            |rig| {
                rig.ctx.my_entity_group_id = Some(10);
                rig.world.groups.insert(10, TestGroup { members: vec![5], ..Default::default() });
                rig.world
                    .sensors
                    .push((ProximitySensor { item_id: 5, ..Default::default() }, vec!["a".into()]));
                rig.world.sensors.push((
                    ProximitySensor { item_id: 6, ..Default::default() },
                    vec!["a".into(), "b".into()],
                ));
                rig.world.add_ball(7, [0.0; 3], 900);
            },
            |t| {
                (
                    get_proximity_sensors(t, &["a".to_string()]).len(),
                    get_proximity_sensors(t, &["b".to_string()]).len(),
                    get_proximity_sensors_for_group_members(t).iter().map(|s| s.item_id).collect::<Vec<_>>(),
                    get_proximity_sensor(t, 6).map(|s| s.item_id),
                    create_proximity_sensor(t, 7, 5000.0).map(|s| s.item_id),
                )
            },
        );
        assert_eq!(answer, Some((2, 1, vec![5], Some(6), Some(7))));
    }
}
