//! `behaviors/actions/guard.py`.

use crate::groups::mixin::get_member_id_list;
use crate::tree::{Task, TaskCtx};
use crate::utility::components::create_proximity_sensor;

/// Places a proximity sensor on an item, or moves the one it has when it is too far from the item (`PlaceProximitySensor`), for every member of my group
/// (`PlaceProximitySensorForGroup`).
#[derive(Debug, Default)]
pub struct PlaceProximitySensor {
    pub group: bool,
}

impl PlaceProximitySensor {
    pub fn for_group() -> Self {
        Self { group: true }
    }

    /// `_UpdateSensor`: an item that has no sensor (`KeyError`) gets one installed.
    fn update_sensor(t: &mut TaskCtx<'_, '_>, item_id: i64) {
        match t.world().proximity_sensor_distance_to(item_id) {
            Some(distance) => {
                let Some(replacement_range) = t.required_float("sensorReplacementRange") else { return };
                if distance > replacement_range {
                    t.world().replace_proximity_sensor(item_id);
                }
            }
            None => Self::install_sensor(t, item_id),
        }
    }

    /// `_InstallSensor`.
    fn install_sensor(t: &mut TaskCtx<'_, '_>, item_id: i64) {
        let Some(range) = t.value_at("rangeAddress") else { return };
        let Some(range) = range.as_float() else {
            t.raise(crate::tree::BehaviorError::Task(
                "TypeError: the aggression range is not a number".to_string(),
            ));
            return;
        };
        if create_proximity_sensor(t, item_id, range).is_none() {
            return;
        }
        // `SetTags`
        t.world().add_behavior_context_tags(item_id);
        let tags: Vec<String> = t
            .attribute("tags")
            .items()
            .unwrap_or(&[])
            .iter()
            .filter_map(|v| v.as_str().map(str::to_string))
            .collect();
        for tag in tags {
            t.world().add_proximity_sensor_tag(item_id, &tag);
        }
        t.world().register_proximity_sensor(item_id);
    }
}

impl Task for PlaceProximitySensor {
    fn type_name(&self) -> &'static str {
        if self.group { "PlaceProximitySensorForGroup" } else { "PlaceProximitySensor" }
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        if self.group {
            for item_id in get_member_id_list(t) {
                Self::update_sensor(t, item_id);
            }
        } else {
            let Some(item) = t.value_at("itemIdAddress") else { return };
            let Some(item_id) = t.expect_id(&item) else { return };
            Self::update_sensor(t, item_id);
        }
        if !t.faulted() {
            t.set_status_to_success();
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::blackboards::{Address, ScopeType};
    use crate::tree::tests::Rig;
    use crate::tree::{Attrs, Status};
    use crate::value::Value;

    fn attrs() -> Attrs {
        Attrs::new()
            .with("itemIdAddress", Address::new(ScopeType::Item, "Guard").to_value())
            .with("rangeAddress", Address::new(ScopeType::Item, "Range").to_value())
            .with("sensorReplacementRange", 1000.0)
            .with("tags", Value::List(vec![Value::Str("guard".into())]))
    }

    #[test]
    fn an_item_with_no_sensor_gets_one_with_its_tags() {
        let mut rig = Rig::new();
        rig.world.add_ball(7, [0.0; 3], 900);
        rig.post(ScopeType::Item, "Guard", Value::Int(7));
        rig.post(ScopeType::Item, "Range", Value::Int(20_000));
        assert_eq!(rig.run(Box::new(PlaceProximitySensor::default()), attrs()), Status::Success);
        assert_eq!(rig.last_error, None);
        assert_eq!(
            rig.world.take_calls(),
            [
                "add_proximity_sensor(7, 900, 20000)",
                "add_behavior_context_tags(7)",
                "add_proximity_sensor_tag(7, guard)",
                "register_proximity_sensor(7)"
            ]
        );
    }

    #[test]
    fn a_sensor_that_is_too_far_from_its_item_is_replaced_and_one_that_is_near_is_left() {
        for (distance, replaced) in [(1500.0, true), (500.0, false)] {
            let mut rig = Rig::new();
            rig.world.sensor_distances.insert(7, distance);
            rig.post(ScopeType::Item, "Guard", Value::Int(7));
            assert_eq!(rig.run(Box::new(PlaceProximitySensor::default()), attrs()), Status::Success);
            assert_eq!(rig.world.take_calls().contains(&"replace_proximity_sensor(7)".to_string()), replaced);
        }
    }
}
