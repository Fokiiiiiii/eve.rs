//! `behaviors/actions/awareness.py`: what a group of NPCs is aware of: where the balls are, which clusters of enemies there are, and the awareness
//! the server keeps for a group and for a spawn pool.

use crate::constants::combat::{FW_ATTACK_NONE, THREAT_MAX_DISTANCE_FROM_GUARD_OBJECT};
use crate::evemath::{center_point, farthest_distance_from_point, weighted_choice};
use crate::tree::{BehaviorError, Task, TaskCtx};
use crate::utility::awareness::{GroupAwarenessConfig, get_awareness_config};
use crate::utility::ballparks::{
    get_ball_position, get_ball_positions, is_ball_cloaked, is_ball_in_park, is_coordinate_in_range_of_ball,
};
use crate::utility::owner::get_owner_id;
use crate::utility::threat::evaluate_target;
use crate::value::Value;

const MIN_CLUSTER_QUALITY: f64 = 0.1;

/// The ids of a collection of the blackboard.
fn ids_of(value: &Value) -> Vec<i64> {
    value.items().unwrap_or(&[]).iter().filter_map(Value::as_int).collect()
}

enter_task!(
    /// Puts the centroid of the balls on the blackboard (none when there are none).
    SendCentroidOfBallIds,
    |t| {
        let Some(list_address) = t.required_address("ballIdListAddress") else { return };
        let ball_ids = t.get_last_blackboard_value(&list_address);
        let mut centroid = None;
        if ball_ids.truthy() {
            let position_list = get_ball_positions(t, &ids_of(&ball_ids));
            centroid = center_point(&position_list);
        }
        let Some(address) = t.required_address("centroidPositionAddress") else { return };
        t.send_blackboard_value(&address, centroid.map_or(Value::None, Value::from_vec3));
        t.set_status_to_success();
    }
);

enter_task!(
    /// Puts the centroid of the items, and the distance from it to the farthest, on the blackboard.
    ComputeCentroidAndRadiusOfItemIds,
    |t| {
        let Some(ids_address) = t.required_address("itemIdsAddress") else { return };
        let ball_ids = t.get_last_blackboard_value(&ids_address);
        let mut centroid = None;
        let mut radius = None;
        if ball_ids.truthy() {
            let position_list = get_ball_positions(t, &ids_of(&ball_ids));
            if let Some(center) = center_point(&position_list) {
                radius = farthest_distance_from_point(center, &position_list);
                centroid = Some(center);
            }
        }
        let (Some(centroid_address), Some(radius_address)) =
            (t.required_address("centroidPositionAddress"), t.required_address("radiusPositionAddress"))
        else {
            return;
        };
        t.send_blackboard_value(&centroid_address, centroid.map_or(Value::None, Value::from_vec3));
        t.send_blackboard_value(&radius_address, radius.map_or(Value::None, Value::Float));
        t.set_status_to_success();
    }
);

/// `fwAttackMethod == FW_ATTACK_NONE` is none.
fn fw_attack_method(t: &mut TaskCtx<'_, '_>) -> Option<Value> {
    let method = t.required("fwAttackMethod")?;
    Some(if method.py_eq(&Value::Str(FW_ATTACK_NONE.to_string())) { Value::None } else { method })
}

/// Has the server watch for threats for my group.
#[derive(Debug, Default)]
pub struct InitializeGroupAwareness;

impl InitializeGroupAwareness {
    /// `get_awareness_config` of this task.
    fn awareness_config(
        t: &mut TaskCtx<'_, '_>,
        member_collection_id: Value,
    ) -> Option<GroupAwarenessConfig> {
        let mut config = get_awareness_config(t, member_collection_id, None, None)?;
        config.standings_enabled = t.required("standingsEnabled")?;
        let react = t.required("reactToDisapproval")?;
        config.react_to_disapproval = if react.truthy() { react } else { Value::Bool(false) };
        config.fw_attack_method = fw_attack_method(t)?;
        Some(config)
    }
}

impl Task for InitializeGroupAwareness {
    fn type_name(&self) -> &'static str {
        "InitializeGroupAwareness"
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        let member_collection_id = t.ctx().my_entity_group_id.map_or(Value::None, Value::Int);
        if t.world().is_awareness_registered(&member_collection_id) {
            t.set_status_to_failed();
            return;
        }
        let Some(config) = Self::awareness_config(t, member_collection_id.clone()) else { return };
        t.world().register_group_awareness(&member_collection_id, config);
        t.set_status_to_success();
    }
}

/// Has the server watch for threats for a spawn pool: its members are the items that are added to it.
#[derive(Debug, Default)]
pub struct InitializeSpawnPoolFleetAwareness {
    spawn_pool_id: Value,
}

impl Task for InitializeSpawnPoolFleetAwareness {
    fn type_name(&self) -> &'static str {
        "InitializeSpawnPoolFleetAwareness"
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        let Some(pool_address) = t.required_address("spawnPoolIdAddress") else { return };
        self.spawn_pool_id = t.get_last_blackboard_value(&pool_address);
        // the first `get_owner_id` is only a check that there is one
        let _ = get_owner_id(t);
        if t.faulted() {
            return;
        }
        let Some(targets_address) = t.required_address("combatTargetsSetAddress") else { return };
        let blackboard_id = t.ctx().id_for_scope(targets_address.scope_type);
        let owner_id = get_owner_id(t);
        let behavior_id = t.behavior_id();
        let Some(mut config) = get_awareness_config(t, self.spawn_pool_id.clone(), None, None) else {
            return;
        };
        config.standings_enabled = Value::Bool(false);
        config.fw_attack_method = Value::None;
        t.world().register_spawn_pool_fleet_awareness(
            &self.spawn_pool_id,
            owner_id,
            blackboard_id,
            &behavior_id,
            config,
        );
        t.set_status_to_success();
    }
}

enter_task!(
    /// Adds my item to the members of the fleet awareness of a spawn pool; fails for a pool that has none.
    AddMyItemIdToSpawnPoolFleetAwareness,
    |t| {
        let Some(address) = t.required_address("spawnPoolIdAddress") else { return };
        let spawn_pool_id = t.get_last_blackboard_value(&address);
        let Some(me) = t.my_item_id() else { return };
        if t.world().add_item_to_spawn_pool_fleet_awareness(&spawn_pool_id, me) {
            t.set_status_to_success();
        } else {
            t.set_status_to_failed();
        }
    }
);

enter_task!(
    /// Has the fleet awareness of a spawn pool cluster its enemies, once.
    InitializeEnemyClusterGeneration,
    |t| {
        let Some(address) = t.required_address("spawnPoolIdAddress") else { return };
        let spawn_pool_id = t.get_last_blackboard_value(&address);
        match t.world().fleet_awareness_lacks_enemy_cluster_generator(&spawn_pool_id) {
            None => {
                t.raise(BehaviorError::Task(
                    "AttributeError: 'NoneType' object has no attribute 'get_enemy_cluster_generator'".to_string(),
                ));
                return;
            }
            Some(true) => {
                let (Some(interval), Some(clusters_address)) =
                    (t.required("updateIntervalSeconds"), t.required_address("enemyClustersAddress"))
                else {
                    return;
                };
                let Some(channel) = t.channel_from_address(&clusters_address) else { return };
                t.world().enable_enemy_cluster_generator(&spawn_pool_id, &interval, channel);
            }
            Some(false) => {}
        }
        t.set_status_to_success();
    }
);

/// `ClusterData`: a cluster of enemies as it is now.
struct ClusterData {
    item_ids: Vec<i64>,
    centroid: [f64; 3],
    radius: f64,
    quality: f64,
}

impl ClusterData {
    fn new(item_ids: Vec<i64>, centroid: [f64; 3], radius: f64, threat: f64) -> Self {
        let cluster_radius_over_max_distance = radius / THREAT_MAX_DISTANCE_FROM_GUARD_OBJECT;
        let cluster_proportion = cluster_radius_over_max_distance.min(1.0) / item_ids.len() as f64;
        let quality = threat * (1.0 - cluster_proportion).max(MIN_CLUSTER_QUALITY);
        Self { item_ids, centroid, radius, quality }
    }
}

/// Picks one of the clusters of enemies, by how threatening it is, and puts it on the blackboard.
#[derive(Debug, Default)]
pub struct SelectClusterWeightedByThreat;

impl SelectClusterWeightedByThreat {
    /// `_get_guard_object_id`.
    fn guard_object_id(t: &mut TaskCtx<'_, '_>) -> Option<Option<i64>> {
        if !t.has_attribute("guardObjectIdAddress") {
            return Some(None);
        }
        let address = t.required_address("guardObjectIdAddress")?;
        let guard = t.get_last_blackboard_value(&address);
        let Some(guard_id) = guard.as_int() else { return Some(None) };
        Some(if is_ball_in_park(t, guard_id) { Some(guard_id) } else { None })
    }

    /// `_is_in_range(cluster_coordinates)`.
    fn is_in_range(t: &mut TaskCtx<'_, '_>, coordinates: [f64; 3]) -> Option<bool> {
        match Self::guard_object_id(t)? {
            None => Some(true),
            Some(guard_id) => Some(is_coordinate_in_range_of_ball(
                t,
                guard_id,
                coordinates,
                THREAT_MAX_DISTANCE_FROM_GUARD_OBJECT,
            )),
        }
    }

    /// `get_updated_enemy_clusters`.
    fn updated_enemy_clusters(t: &mut TaskCtx<'_, '_>, enemy_clusters: &Value) -> Option<Vec<ClusterData>> {
        let Some(clusters) = enemy_clusters.items() else {
            t.raise(BehaviorError::Task("TypeError: 'NoneType' object is not iterable".to_string()));
            return None;
        };
        let mut updated = Vec::new();
        for cluster in clusters {
            let Some(item_ids) = cluster.dict_get(&Value::Str("item_ids".to_string())) else {
                t.raise(crate::env::WorldError::Key("item_ids".to_string()).into());
                return None;
            };
            let mut ids: Vec<i64> = Vec::new();
            for item_id in ids_of(item_ids) {
                if is_ball_in_park(t, item_id) && !is_ball_cloaked(t, item_id) && !ids.contains(&item_id) {
                    ids.push(item_id);
                }
            }
            if ids.is_empty() {
                continue;
            }
            ids.sort_unstable();
            let mut points = Vec::new();
            for ball_id in &ids {
                points.push(get_ball_position(t, *ball_id)?);
            }
            let centroid = center_point(&points)?;
            if !Self::is_in_range(t, centroid)? {
                continue;
            }
            let radius = farthest_distance_from_point(centroid, &points)?;
            // `_get_cluster_threat`
            let window = t.world().threat_time_window_seconds();
            let mut threat = 0.0;
            for item_id in &ids {
                threat += evaluate_target(t, *item_id, window);
            }
            updated.push(ClusterData::new(ids, centroid, radius, threat));
        }
        Some(updated)
    }
}

impl Task for SelectClusterWeightedByThreat {
    fn type_name(&self) -> &'static str {
        "SelectClusterWeightedByThreat"
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        let Some(clusters_address) = t.required_address("enemyClustersAddress") else { return };
        let enemy_clusters = t.get_last_blackboard_value(&clusters_address);
        let Some(clusters) = Self::updated_enemy_clusters(t, &enemy_clusters) else { return };
        let mut cluster = None;
        if !clusters.is_empty() {
            let choices: Vec<(usize, f64)> =
                clusters.iter().enumerate().map(|(at, c)| (at, c.quality)).collect();
            cluster = weighted_choice(&choices, t.world());
        }
        let Some(cluster) = cluster.map(|at| &clusters[at]) else {
            t.set_status_to_failed();
            return;
        };
        let (Some(centroid_address), Some(radius_address), Some(ids_address)) = (
            t.required_address("clusterCentroidAddress"),
            t.required_address("clusterRadiusAddress"),
            t.required_address("clusterItemIdsAddress"),
        ) else {
            return;
        };
        t.send_blackboard_value(&centroid_address, Value::from_vec3(cluster.centroid));
        t.send_blackboard_value(&radius_address, Value::Float(cluster.radius));
        t.send_blackboard_value(&ids_address, Value::set(cluster.item_ids.iter().map(|id| Value::Int(*id))));
        t.set_status_to_success();
    }
}

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

    fn ints(list: &[i64]) -> Value {
        Value::set(list.iter().map(|i| Value::Int(*i)))
    }

    fn balls(rig: &mut Rig) {
        rig.world.add_ball(20, [0.0, 0.0, 0.0], 1);
        rig.world.add_ball(21, [10.0, 0.0, 0.0], 1);
        rig.world.add_ball(22, [0.0, 30.0, 0.0], 1);
    }

    #[test]
    fn the_centroid_of_the_balls_that_are_there_is_sent_and_none_for_no_balls() {
        let attrs = Attrs::new()
            .with("ballIdListAddress", item("Balls"))
            .with("centroidPositionAddress", item("Centroid"));
        let mut rig = Rig::new();
        balls(&mut rig);
        rig.post(ScopeType::Item, "Balls", Value::List(vec![Value::Int(20), Value::Int(21), Value::Int(99)]));
        assert_eq!(rig.run(Box::new(SendCentroidOfBallIds), attrs.clone()), Status::Success);
        assert_eq!(rig.last(ScopeType::Item, "Centroid"), Value::from_vec3([5.0, 0.0, 0.0]));
        rig.post(ScopeType::Item, "Balls", Value::List(vec![Value::Int(99)]));
        rig.run(Box::new(SendCentroidOfBallIds), attrs);
        assert_eq!(rig.last(ScopeType::Item, "Centroid"), Value::None);
    }

    #[test]
    fn the_centroid_and_the_radius_of_the_items_are_sent() {
        let attrs = Attrs::new()
            .with("itemIdsAddress", item("Items"))
            .with("centroidPositionAddress", item("Centroid"))
            .with("radiusPositionAddress", item("Radius"));
        let mut rig = Rig::new();
        balls(&mut rig);
        rig.post(ScopeType::Item, "Items", ints(&[20, 21]));
        assert_eq!(rig.run(Box::new(ComputeCentroidAndRadiusOfItemIds), attrs.clone()), Status::Success);
        assert_eq!(rig.last(ScopeType::Item, "Radius"), Value::Float(5.0));
        rig.post(ScopeType::Item, "Items", ints(&[]));
        rig.run(Box::new(ComputeCentroidAndRadiusOfItemIds), attrs);
        assert_eq!(
            (rig.last(ScopeType::Item, "Centroid"), rig.last(ScopeType::Item, "Radius")),
            (Value::None, Value::None)
        );
    }

    fn awareness_attrs() -> Attrs {
        Attrs::new()
            .with("threatRange", 20_000.0)
            .with("threatTimeoutSeconds", 60i64)
            .with("maxSensorDrift", 1i64)
            .with("updateTimeSeconds", 5i64)
            .with(
                "combatTargetsSetAddress",
                Address::new(ScopeType::EntityGroup, "COMBAT_TARGETS_SET").to_value(),
            )
            .with("standingsEnabled", true)
            .with("reactToDisapproval", Value::None)
            .with("fwAttackMethod", "None")
            .with("spawnPoolIdAddress", item("Pool"))
    }

    fn awareness_rig() -> Rig {
        let mut rig = Rig::new();
        rig.ctx.my_slim_item =
            Some(crate::env::SlimItem { item_id: 1, owner_id: Some(7), ..Default::default() });
        rig.world.groups.insert(10, TestGroup { members: vec![1], owner_id: Some(7), ..Default::default() });
        rig
    }

    #[test]
    fn the_awareness_of_my_group_is_registered_once() {
        let mut rig = awareness_rig();
        assert_eq!(rig.run(Box::new(InitializeGroupAwareness), awareness_attrs()), Status::Success);
        assert_eq!(rig.last_error, None);
        let (id, config) = rig.world.registered_awareness[0].clone();
        assert_eq!(id, Value::Int(10));
        assert_eq!(config.standings_enabled, Value::Bool(true));
        assert_eq!(config.react_to_disapproval, Value::Bool(false), "none or false");
        assert_eq!(config.fw_attack_method, Value::None, "\"None\" is none");
        assert_eq!(config.owner_id, Some(7));
        assert_eq!(rig.run(Box::new(InitializeGroupAwareness), awareness_attrs()), Status::Failure);
        assert_eq!(rig.world.registered_awareness.len(), 1);
    }

    #[test]
    fn the_awareness_of_a_spawn_pool_is_registered_with_no_standings_and_items_are_added_to_it() {
        let mut rig = awareness_rig();
        rig.post(ScopeType::Item, "Pool", Value::Int(77));
        assert_eq!(
            rig.run(Box::new(InitializeSpawnPoolFleetAwareness::default()), awareness_attrs()),
            Status::Success
        );
        assert_eq!(rig.last_error, None);
        let (pool, config, _, _) = rig.world.fleet_awareness[0].clone();
        assert_eq!(pool, Value::Int(77));
        assert_eq!(
            (config.standings_enabled, config.fw_attack_method, config.member_collection_id),
            (Value::Bool(false), Value::None, Value::Int(77))
        );
        let calls = rig.world.take_calls();
        assert_eq!(calls, ["register_spawn_pool_fleet_awareness(Int(77), Some(7), Some(10), None)"]);
        let add = Attrs::new().with("spawnPoolIdAddress", item("Pool"));
        assert_eq!(rig.run(Box::new(AddMyItemIdToSpawnPoolFleetAwareness), add.clone()), Status::Success);
        assert_eq!(rig.world.fleet_awareness[0].2, [1]);
        rig.post(ScopeType::Item, "Pool", Value::Int(78));
        assert_eq!(rig.run(Box::new(AddMyItemIdToSpawnPoolFleetAwareness), add), Status::Failure);
    }

    #[test]
    fn enemy_cluster_generation_is_enabled_once_and_a_pool_without_awareness_raises() {
        let mut rig = awareness_rig();
        rig.post(ScopeType::Item, "Pool", Value::Int(77));
        let attrs = Attrs::new()
            .with("spawnPoolIdAddress", item("Pool"))
            .with("updateIntervalSeconds", 3i64)
            .with("enemyClustersAddress", item("Clusters"));
        rig.run(Box::new(InitializeEnemyClusterGeneration), attrs.clone());
        assert!(
            matches!(&rig.last_error, Some(BehaviorError::Task(m)) if m.contains("get_enemy_cluster_generator"))
        );
        rig.run(Box::new(InitializeSpawnPoolFleetAwareness::default()), awareness_attrs());
        rig.world.take_calls();
        assert_eq!(rig.run(Box::new(InitializeEnemyClusterGeneration), attrs.clone()), Status::Success);
        assert_eq!(rig.world.take_calls(), ["enable_enemy_cluster_generator(Int(77), Int(3), Clusters)"]);
        rig.run(Box::new(InitializeEnemyClusterGeneration), attrs);
        assert!(rig.world.take_calls().is_empty(), "it is enabled");
    }

    fn cluster(ids: &[i64]) -> Value {
        Value::Dict(vec![(Value::Str("item_ids".into()), ints(ids))])
    }

    fn select_attrs() -> Attrs {
        Attrs::new()
            .with("enemyClustersAddress", item("Clusters"))
            .with("clusterCentroidAddress", item("Centroid"))
            .with("clusterRadiusAddress", item("Radius"))
            .with("clusterItemIdsAddress", item("Ids"))
    }

    #[test]
    fn a_cluster_is_chosen_by_its_threat_and_only_the_balls_that_are_there_count() {
        let mut rig = Rig::new();
        balls(&mut rig);
        rig.world.add_ball(23, [1_000.0, 0.0, 0.0], 1);
        rig.world.threat_window = 60.0;
        rig.world.damage.insert(23, crate::env::DamageTotals { damage_to_npcs: 100.0, healing_done: 0.0 });
        rig.world.damage.insert(20, crate::env::DamageTotals { damage_to_npcs: 1.0, healing_done: 0.0 });
        rig.world.cloaked.insert(21);
        rig.post(
            ScopeType::Item,
            "Clusters",
            Value::List(vec![cluster(&[20, 21, 99]), cluster(&[23]), cluster(&[98])]),
        );
        rig.world.set_numbers(&[0.99]);
        assert_eq!(rig.run(Box::new(SelectClusterWeightedByThreat), select_attrs()), Status::Success);
        assert_eq!(rig.last_error, None);
        assert_eq!(
            rig.last(ScopeType::Item, "Ids"),
            ints(&[23]),
            "the threatening one; the cluster of 20 is by itself 20 with 21 cloaked"
        );
        assert_eq!(rig.last(ScopeType::Item, "Radius"), Value::Float(0.0));
    }

    #[test]
    fn a_cloaked_ball_is_not_in_the_cluster_that_is_selected() {
        let mut rig = Rig::new();
        balls(&mut rig);
        rig.world.cloaked.insert(21);
        rig.post(ScopeType::Item, "Clusters", Value::List(vec![cluster(&[20, 21])]));
        assert_eq!(rig.run(Box::new(SelectClusterWeightedByThreat), select_attrs()), Status::Success);
        assert_eq!(rig.last(ScopeType::Item, "Ids"), ints(&[20]));
    }

    #[test]
    fn no_clusters_that_are_left_fail_and_clusters_far_from_the_guard_object_are_left_out() {
        let mut rig = Rig::new();
        balls(&mut rig);
        rig.post(ScopeType::Item, "Clusters", Value::List(vec![cluster(&[98])]));
        assert_eq!(rig.run(Box::new(SelectClusterWeightedByThreat), select_attrs()), Status::Failure);
        // the guard object is far from the cluster
        rig.world.add_ball(50, [9_000_000.0, 0.0, 0.0], 1);
        rig.post(ScopeType::Item, "Clusters", Value::List(vec![cluster(&[20])]));
        rig.post(ScopeType::Item, "Guard", Value::Int(50));
        let attrs = select_attrs().with("guardObjectIdAddress", item("Guard"));
        assert_eq!(rig.run(Box::new(SelectClusterWeightedByThreat), attrs.clone()), Status::Failure);
        rig.post(ScopeType::Item, "Guard", Value::Int(51));
        assert_eq!(
            rig.run(Box::new(SelectClusterWeightedByThreat), attrs),
            Status::Success,
            "a guard object that is gone counts for none"
        );
    }

    #[test]
    fn no_list_of_clusters_at_all_raises() {
        let mut rig = Rig::new();
        rig.run(Box::new(SelectClusterWeightedByThreat), select_attrs());
        assert!(matches!(&rig.last_error, Some(BehaviorError::Task(m)) if m.starts_with("TypeError")));
    }

    #[test]
    fn the_quality_of_a_cluster_falls_with_its_radius_and_never_below_a_tenth_of_its_threat() {
        let small = ClusterData::new(vec![1, 2], [0.0; 3], 0.0, 10.0);
        assert_eq!(small.quality, 10.0);
        let wide = ClusterData::new(vec![1, 2], [0.0; 3], THREAT_MAX_DISTANCE_FROM_GUARD_OBJECT, 10.0);
        assert_eq!(wide.quality, 5.0, "half of it, for two");
        let single_wide =
            ClusterData::new(vec![1], [0.0; 3], THREAT_MAX_DISTANCE_FROM_GUARD_OBJECT * 10.0, 10.0);
        assert_eq!(single_wide.quality, 1.0, "a tenth");
    }
}
