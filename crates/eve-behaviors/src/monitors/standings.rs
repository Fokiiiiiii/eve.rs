//! `behaviors/monitors/standings.py`.

use std::collections::BTreeSet;

use crate::blackboards::Address;
use crate::monitors::ballparks::{MonitorProximitySensors, SensorKind};
use crate::tree::{Attrs, BehaviorError, TaskCtx};
use crate::utility::ballparks::{get_ball_position, get_item_owner};
use crate::utility::blackboards::get_response_thresholds;
use crate::utility::standings::{
    STANDINGS_FRIENDLY, STANDINGS_HOSTILE, STANDINGS_NEUTRAL, classify_standings, get_standings_between,
};
use crate::value::Value;

/// `StandingsAddresses`: where the items of a standing class, a flag for them and the last place one was seen go.
#[derive(Debug, Clone, Default, PartialEq)]
pub struct StandingsAddresses {
    /// `address_by_class`, in the order the classes were mapped.
    pub by_class: Vec<(i64, ClassAddresses)>,
}

#[derive(Debug, Clone, PartialEq)]
pub struct ClassAddresses {
    pub item_ids: Address,
    pub flag: Option<Address>,
    pub last_position: Option<Address>,
}

impl StandingsAddresses {
    fn map_address_to_class(
        &mut self,
        attributes: &Attrs,
        standings_class: i64,
        item_ids: &str,
        flag: &str,
        last_position: &str,
    ) {
        let item_ids_address = attributes.address(item_ids);
        if let Some(item_ids) = item_ids_address {
            self.by_class.push((
                standings_class,
                ClassAddresses {
                    item_ids,
                    flag: attributes.address(flag),
                    last_position: attributes.address(last_position),
                },
            ));
        }
    }

    fn for_class(&self, standings_class: i64) -> Option<&ClassAddresses> {
        self.by_class.iter().find(|(class, _)| *class == standings_class).map(|(_, addresses)| addresses)
    }
}

/// `MonitorProximitySensorForGroupStanding(attributes)`: the proximity sensors of my group's members, and what they see sorted by how the NPC stands
pub fn monitor_proximity_sensor_for_group_standing(attributes: &Attrs) -> MonitorProximitySensors {
    let mut addresses = StandingsAddresses::default();
    addresses.map_address_to_class(
        attributes,
        STANDINGS_HOSTILE,
        "itemIdsWithHostileStandingsAddress",
        "flagHostileStandingsAddress",
        "lastPositionForHostileStandingsAddress",
    );
    addresses.map_address_to_class(
        attributes,
        STANDINGS_NEUTRAL,
        "itemIdsWithNeutralStandingsAddress",
        "flagNeutralStandingsAddress",
        "lastPositionForNeutralStandingsAddress",
    );
    addresses.map_address_to_class(
        attributes,
        STANDINGS_FRIENDLY,
        "itemIdsWithFriendlyStandingsAddress",
        "flagFriendlyStandingsAddress",
        "lastPositionForFriendlyStandingsAddress",
    );
    MonitorProximitySensors::new(SensorKind::GroupStanding(Box::new(addresses)))
}

/// `ClassifyItems` of `MonitorProximitySensorForGroupStanding`.
pub fn classify_group_standings(
    t: &mut TaskCtx<'_, '_>,
    addresses: &StandingsAddresses,
    item_ids: &BTreeSet<i64>,
) {
    let mut by_class: Vec<(i64, BTreeSet<i64>)> = Vec::new();
    for &item_id in item_ids {
        let owner_id = get_item_owner(t, item_id);
        let Some(standings) = get_standings_between(t, owner_id.unwrap_or(0)) else { return };
        let Some(thresholds) = get_response_thresholds(t) else {
            if !t.faulted() {
                t.raise(BehaviorError::Task(
                    "AttributeError: 'NoneType' object has no attribute 'hostile_response_threshold'"
                        .to_string(),
                ));
            }
            return;
        };
        let class = classify_standings(
            thresholds.hostile_response_threshold,
            thresholds.friendly_response_threshold,
            standings,
        );
        if addresses.for_class(class).is_some() {
            match by_class.iter_mut().find(|(c, _)| *c == class) {
                Some((_, set)) => {
                    set.insert(item_id);
                }
                None => by_class.push((class, BTreeSet::from([item_id]))),
            }
        }
    }
    // `iteritems()` of a Python 2 dict of these keys: neutral, friendly, hostile
    for class in [STANDINGS_NEUTRAL, STANDINGS_FRIENDLY, STANDINGS_HOSTILE] {
        if let Some((_, detected)) = by_class.iter().find(|(c, _)| *c == class)
            && let Some(class_addresses) = addresses.for_class(class)
        {
            update_blackboards_for_standing(t, detected, class_addresses);
        }
    }
}

fn update_blackboards_for_standing(
    t: &mut TaskCtx<'_, '_>,
    detected: &BTreeSet<i64>,
    addresses: &ClassAddresses,
) {
    let stored = t.get_last_blackboard_value(&addresses.item_ids);
    let stored = if stored.truthy() { stored } else { Value::set(std::iter::empty()) };
    let new_item_ids: Vec<i64> =
        detected.iter().copied().filter(|id| !stored.contains(&Value::Int(*id))).collect();
    if new_item_ids.is_empty() {
        return;
    }
    let mut union = stored.clone();
    for id in &new_item_ids {
        union.set_add(Value::Int(*id));
    }
    t.send_blackboard_value(&addresses.item_ids, union);
    if let Some(flag) = &addresses.flag {
        t.send_blackboard_value(flag, Value::Bool(true));
    }
    if let Some(last_position) = &addresses.last_position {
        let at = t.world().random_index(new_item_ids.len());
        let item_id = new_item_ids[at];
        let Some(position) = get_ball_position(t, item_id) else { return };
        t.send_blackboard_value(last_position, Value::from_vec3(position));
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::blackboards::ScopeType;
    use crate::env::testing::TestGroup;
    use crate::env::{ResponseThresholds, SlimItem};
    use crate::tree::Status;
    use crate::tree::tests::Rig;

    fn item(name: &str) -> Value {
        Address::new(ScopeType::Item, name).to_value()
    }

    #[test]
    fn what_the_group_sensors_see_is_sorted_by_standing_onto_the_blackboards() {
        let attrs = Attrs::new()
            .with("objectListAddress", item("Objects"))
            .with("itemIdsWithHostileStandingsAddress", item("Hostile"))
            .with("flagHostileStandingsAddress", item("HostileFlag"))
            .with("lastPositionForHostileStandingsAddress", item("HostilePos"))
            .with("itemIdsWithFriendlyStandingsAddress", item("Friendly"));
        let mut rig = Rig::new();
        rig.ctx.my_entity_group_id = Some(10);
        rig.ctx.my_slim_item = Some(SlimItem { owner_id: Some(1000006), ..Default::default() });
        rig.world
            .groups
            .insert(10, TestGroup { members: vec![5], owner_id: Some(1000006), ..Default::default() });
        rig.world.thresholds =
            Some(ResponseThresholds { hostile_response_threshold: -1.0, friendly_response_threshold: 1.0 });
        rig.world.add_ball(5, [0.0; 3], 100).new_bubble_id = 3;
        rig.world.add_ball(30, [7.0, 8.0, 9.0], 200);
        rig.world.add_ball(31, [1.0, 1.0, 1.0], 200);
        rig.world.slims.get_mut(&30).unwrap().owner_id = Some(98_000_001);
        rig.world.slims.get_mut(&31).unwrap().owner_id = Some(98_000_002);
        rig.world.standings.insert((1000006, 98_000_001), -5.0);
        rig.world.standings.insert((1000006, 98_000_002), 5.0);
        rig.world.sensors.push((
            crate::env::ProximitySensor { item_id: 5, bubble_id: 3, ball_ids_in_range: vec![30, 31] },
            vec![],
        ));
        let status = rig.run(Box::new(monitor_proximity_sensor_for_group_standing(&attrs)), attrs);
        assert_eq!(rig.last_error, None);
        assert_eq!(status, Status::Success);
        let hostile = rig
            .shared
            .blackboards
            .message_channel(crate::blackboards::item_scope(1), "Hostile")
            .unwrap()
            .last_message_value();
        assert_eq!(hostile, Value::Set(vec![Value::Int(30)]));
        assert_eq!(
            rig.shared
                .blackboards
                .message_channel(crate::blackboards::item_scope(1), "HostileFlag")
                .unwrap()
                .last_message_value(),
            Value::Bool(true)
        );
        assert_eq!(
            rig.shared
                .blackboards
                .message_channel(crate::blackboards::item_scope(1), "HostilePos")
                .unwrap()
                .last_message_value(),
            Value::from_vec3([7.0, 8.0, 9.0])
        );
        let friendly = rig
            .shared
            .blackboards
            .message_channel(crate::blackboards::item_scope(1), "Friendly")
            .unwrap()
            .last_message_value();
        assert_eq!(friendly, Value::Set(vec![Value::Int(31)]));
    }
}
