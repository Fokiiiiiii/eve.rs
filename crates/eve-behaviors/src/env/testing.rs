//! A world to test tasks against: a ballpark, a dogma engine and groups held in maps that a test fills in, dice that answer from a list, and a
//! record of what the tasks asked it to do.

use std::collections::{HashMap, HashSet};

use crate::value::Value;

use super::{
    Ball, Ballpark, CargoCapacity, DamageTotals, Deployed, Deployment, Dogma, DungeonObject, EffectCycle,
    EffectInfo, Entities, EntitySpawnData, FakeLocation, Formation, FormationSettings, Inventory,
    InventoryItem, Logging, PlayerFleetReport, ProximitySensor, Random, ReactiveSpawnConfig,
    ReinforcementWave, ResponseThresholds, SlimItem, SpawnPick, Spawning, StructureState, Universe,
    WorldError, WorldResult,
};

/// What `GetClosestDisruptorAndIntersectionInWarpPath` answers.
pub type WarpPath = WorldResult<Option<(i64, [f64; 3])>>;

/// A group of entities, as the tests give it.
#[derive(Debug, Clone, Default)]
pub struct TestGroup {
    pub members: Vec<i64>,
    pub owner_id: Option<i64>,
    pub initial_spawning_complete: bool,
}

/// A number as the calls of the test world write it.
fn plain(value: &Value) -> String {
    match value {
        Value::Int(i) => i.to_string(),
        Value::Float(f) => f.to_string(),
        other => format!("{other:?}"),
    }
}

/// A spawn point of an operation site: two ids and three coordinates.
pub type SpawnPointRow = (i64, i64, f64, f64, f64);

/// An in-memory world. Everything is public: a test sets what it needs and reads `calls` for what the tasks did.
#[derive(Debug, Default)]
pub struct TestWorld {
    pub calls: Vec<String>,
    numbers: Vec<f64>,
    next: usize,
    pub balls: HashMap<i64, Ball>,
    pub slims: HashMap<i64, SlimItem>,
    pub bubbles: HashMap<i64, Vec<i64>>,
    pub globals: Vec<i64>,
    /// Distances between two balls (either order).
    pub surface_dists: HashMap<(i64, i64), f64>,
    pub cloaked: HashSet<i64>,
    pub invulnerable: HashSet<i64>,
    pub aggressed: HashSet<i64>,
    pub warping: HashMap<i64, i64>,
    pub items: HashMap<i64, InventoryItem>,
    pub cargo: HashMap<i64, CargoCapacity>,
    pub cargo_contents: HashMap<i64, Vec<i64>>,
    pub type_ids_by_list: HashMap<i64, Vec<i64>>,
    pub type_attributes_by_name: HashMap<(i64, String), f64>,
    pub security: HashMap<i64, f64>,
    pub factions: HashMap<i64, i64>,
    pub standings: HashMap<(i64, i64), f64>,
    pub prices: HashMap<i64, f64>,
    pub active_effect_targets: HashMap<(i64, i64), Option<i64>>,
    pub effects: HashMap<i64, EffectInfo>,
    pub default_effects: HashMap<i64, i64>,
    pub activation_error: Option<WorldError>,
    pub attributes: HashMap<(i64, i64), f64>,
    /// What reading an attribute of an item raises (`WrongInventoryLocation` and the like).
    pub attribute_errors: HashMap<(i64, i64), WorldError>,
    /// `get_effect_range_falloff_modifier_at_distance`, by the distance in whole metres (1 where it is not said).
    pub falloff_at_distance: HashMap<i64, f64>,
    pub type_attributes: HashMap<(i64, i64), f64>,
    pub type_effects: HashSet<(i64, i64)>,
    pub active_effects: HashSet<(i64, i64)>,
    pub targets: HashMap<i64, Vec<i64>>,
    pub pending_targets: HashSet<(i64, i64)>,
    pub targeting: HashSet<(i64, i64)>,
    pub active_items: HashSet<i64>,
    pub fitted_group_ids: HashMap<i64, Vec<i64>>,
    pub fitted_modules: HashMap<i64, Vec<(i64, i64, i64)>>,
    pub abandoned: HashSet<i64>,
    pub groups: HashMap<i64, TestGroup>,
    pub item_groups: HashMap<i64, i64>,
    pub spawn_points: HashMap<i64, f64>,
    pub reinforced: HashMap<i64, Vec<i64>>,
    pub dungeon_spawns: HashSet<i64>,
    pub bubble_of_position: HashMap<[i64; 3], i64>,
    pub vulnerable_structures: HashSet<i64>,
    pub warp_paths: HashMap<[i64; 3], WarpPath>,
    pub warp_bubbles: (Vec<i64>, Vec<i64>),
    pub solar_system: Option<i64>,
    /// Proximity sensors with their tags.
    pub sensors: Vec<(ProximitySensor, Vec<String>)>,
    pub turbo_shield_states: HashMap<i64, i64>,
    pub reinforcement_authorized: HashMap<i64, WorldResult<bool>>,
    pub reinforcements_available: HashSet<i64>,
    pub group_behavior_ids: HashMap<i64, crate::value::Value>,
    pub type_categories: HashMap<i64, i64>,
    pub char_ships: HashMap<i64, i64>,
    pub thresholds: Option<ResponseThresholds>,
    pub fleet_members: HashMap<i64, Vec<i64>>,
    pub npc_fleet_costs: HashMap<i64, f64>,
    pub player_fleet_costs: HashMap<i64, f64>,
    pub additional_loot: HashMap<i64, Vec<i64>>,
    pub additional_capacity: HashMap<i64, f64>,
    pub damage: HashMap<i64, DamageTotals>,
    pub can_dock: HashSet<(i64, i64)>,
    pub sensor_distances: HashMap<i64, f64>,
    pub system_factions: HashMap<i64, i64>,
    pub persisted_tales: HashMap<i64, i64>,
    pub operation_spawnpoints: HashMap<(i64, i64), SpawnPointRow>,
    pub dungeon_entries: HashMap<i64, [f64; 3]>,
    pub dungeon_objects: HashMap<(i64, i64), i64>,
    pub dungeon_rooms: HashMap<i64, Vec<DungeonObject>>,
    pub reinforced_groups: HashSet<i64>,
    pub effect_ranges: HashMap<(i64, i64), (Option<f64>, Option<f64>)>,
    pub piloted: HashSet<i64>,
    pub pending_cloak: HashSet<i64>,
    pub protected: HashSet<i64>,
    pub ball_sources: HashMap<i64, i64>,
    /// `spawn_data_for_entities` answers one spawn data per unit of each type it is asked for.
    pub next_spawn_pool_id: i64,
    pub spawn_lists: HashMap<i64, Vec<(i64, f64)>>,
    pub groupings: HashMap<i64, Vec<(i64, i64)>>,
    pub spawn_pick: Option<SpawnPick>,
    pub spawn_table_owners: HashMap<i64, i64>,
    pub deployments: Vec<Deployment>,
    pub next_group_id: i64,
    pub next_deployed_item_id: i64,
    pub undock_vectors: HashMap<(i64, i64), ([f64; 3], [f64; 3])>,
    pub reinforced_full_strength: HashMap<i64, i64>,
    pub reactive_spawners: Vec<ReactiveSpawnConfig>,
    pub respawns: Vec<(Option<i64>, Option<i64>, Vec<EntitySpawnData>)>,
    pub deep_space_location: [f64; 3],
    pub ball_callbacks:
        HashMap<i64, (Option<crate::blackboards::Observer>, Option<crate::blackboards::Observer>)>,
    pub fighter_controllers: HashMap<i64, i64>,
    pub cloud_factors: HashMap<(i64, u64), f64>,
    pub fitted_keys: HashMap<i64, Vec<Value>>,
    pub same_bubble_ships: HashMap<i64, Vec<i64>>,
    pub busy_ships: HashSet<i64>,
    pub npc_warpers: HashSet<i64>,
    pub npc_warp_error: Option<WorldError>,
    pub failing_balls: HashSet<i64>,
    pub formation: Option<Formation>,
    pub group_removal_handlers: Vec<(i64, crate::tree::ChannelRef, Value)>,
    pub pre_inv64_characters: HashSet<i64>,
    pub behavior_trees: HashSet<i64>,
    pub removed_additional_loot: HashMap<i64, Vec<(i64, Value)>>,
    pub behavior_entries: Vec<i64>,
    pub registered_awareness: Vec<(Value, crate::utility::awareness::GroupAwarenessConfig)>,
    pub fleet_awareness: Vec<(Value, crate::utility::awareness::GroupAwarenessConfig, Vec<i64>, bool)>,
    pub threat_window: f64,
    pub cargo_bay_types: HashSet<i64>,
    pub player_fleet_report: Option<PlayerFleetReport>,
    pub total_npc_points: f64,
    pub structure_states: HashMap<i64, StructureState>,
    pub exploding: HashSet<i64>,
    pub exploding_error: Option<WorldError>,
    pub effect_cycles: HashMap<(i64, i64), EffectCycle>,
    pub max_effective_ranges: HashMap<i64, f64>,
    pub ability_events: Vec<String>,
    pub ability_contexts: HashMap<(i64, i64), Value>,
    pub ability_error: Option<WorldError>,
    /// What `stop_effect` fails with.
    pub stop_effect_error: Option<WorldError>,
}

impl TestWorld {
    pub fn with_numbers(numbers: &[f64]) -> Self {
        Self { numbers: numbers.to_vec(), ..Self::default() }
    }

    /// The numbers the dice answer from, from the start.
    pub fn set_numbers(&mut self, numbers: &[f64]) {
        self.numbers = numbers.to_vec();
        self.next = 0;
    }

    pub fn take_calls(&mut self) -> Vec<String> {
        std::mem::take(&mut self.calls)
    }

    /// A ball at a place, with a slim item of a type.
    pub fn add_ball(&mut self, id: i64, position: [f64; 3], type_id: i64) -> &mut Ball {
        self.slims.insert(id, SlimItem { item_id: id, type_id, ..SlimItem::default() });
        self.items.insert(id, InventoryItem { item_id: id, type_id, ..InventoryItem::default() });
        self.balls.entry(id).or_insert(Ball { id, position, ..Ball::default() })
    }

    pub fn set_dist(&mut self, a: i64, b: i64, distance: f64) {
        self.surface_dists.insert((a, b), distance);
        self.surface_dists.insert((b, a), distance);
    }
}

impl Random for TestWorld {
    fn random(&mut self) -> f64 {
        let value = self.numbers.get(self.next).or(self.numbers.last()).copied().unwrap_or(0.0);
        self.next += 1;
        value
    }
}

impl Ballpark for TestWorld {
    fn update_slim_item_field(&mut self, item_id: i64, name: &str, value: Value) {
        self.calls.push(format!("update_slim_item_field({item_id}, {name}, {value:?})"));
    }

    fn update_slim_item_field_multi(&mut self, item_id: i64, fields: &[(&str, Value)]) {
        self.calls.push(format!("update_slim_item_field_multi({item_id}, {fields:?})"));
    }

    fn start_stretch_effect(
        &mut self,
        source_id: i64,
        target_id: i64,
        effect_name: &str,
        duration: &Value,
        repeat: &Value,
        module_id: Option<i64>,
        graphic_info: Option<&Value>,
    ) {
        let graphic = graphic_info.map_or(String::new(), |info| format!(", {info:?}"));
        self.calls.push(format!(
            "start_stretch_effect({source_id}, {target_id}, {effect_name}, {}, {}, {module_id:?}{graphic})",
            plain(duration),
            plain(repeat)
        ));
    }

    fn stop_stretch_effect(
        &mut self,
        source_id: i64,
        target_id: i64,
        effect_name: &str,
        module_id: Option<i64>,
    ) {
        self.calls
            .push(format!("stop_stretch_effect({source_id}, {target_id}, {effect_name}, {module_id:?})"));
    }

    fn cancel_if_doing_stuff(&mut self, ship_id: i64) -> bool {
        self.calls.push(format!("cancel_if_doing_stuff({ship_id})"));
        self.busy_ships.contains(&ship_id)
    }

    fn warp_to(
        &mut self,
        ship_id: i64,
        destination: [f64; 3],
        min_range: f64,
        warp_speed: f64,
        jitter_radius: f64,
    ) {
        self.calls
            .push(format!("warp_to({ship_id}, {destination:?}, {min_range}, {warp_speed}, {jitter_radius})"));
    }

    fn npc_warp_to(
        &mut self,
        ball_id: i64,
        destination: [f64; 3],
        jitter_radius: f64,
        warp_at_range: f64,
        warp_speed: f64,
    ) -> Option<WorldResult<()>> {
        if !self.npc_warpers.contains(&ball_id) {
            return None;
        }
        self.calls.push(format!(
            "npc_warp_to({ball_id}, {destination:?}, {jitter_radius}, {warp_at_range}, {warp_speed})"
        ));
        Some(self.npc_warp_error.clone().map_or(Ok(()), Err))
    }

    fn goto_point(&mut self, ball_id: i64, position: [f64; 3]) -> WorldResult<()> {
        self.calls.push(format!("goto_point({ball_id}, {position:?})"));
        if self.failing_balls.contains(&ball_id) {
            return Err(WorldError::Runtime("no".into()));
        }
        Ok(())
    }

    fn set_speed_fraction(&mut self, ball_id: i64, fraction: f64) -> WorldResult<()> {
        self.calls.push(format!("set_speed_fraction({ball_id}, {fraction})"));
        Ok(())
    }

    fn pick_formation(
        &mut self,
        candidates: &[(i64, Option<i64>)],
        destination_coordinates: &Value,
        settings: &FormationSettings,
    ) -> Formation {
        self.calls.push(format!("pick_formation({candidates:?}, {destination_coordinates:?}, {settings:?})"));
        self.formation.clone().unwrap_or_default()
    }

    fn ships_in_same_bubble(&self, item_id: i64) -> Vec<i64> {
        self.same_bubble_ships.get(&item_id).cloned().unwrap_or_default()
    }

    fn start_ship_effect(
        &mut self,
        ship_id: i64,
        effect_name: &str,
        duration: &Value,
        repeat: i64,
        graphic_info: Option<&Value>,
    ) {
        let graphic = graphic_info.map_or(String::new(), |info| format!(", {info:?}"));
        self.calls.push(format!(
            "start_ship_effect({ship_id}, {effect_name}, {}, {repeat}{graphic})",
            plain(duration)
        ));
    }

    fn approach_object(&mut self, ball_id: i64, target_id: i64, approach_range: f64, cruise: bool) {
        self.calls.push(format!("approach_object({ball_id}, {target_id}, {approach_range}, {cruise})"));
    }

    fn set_target_tracking(&mut self, item_id: i64, target_id: i64, notify_range: f64) {
        self.calls.push(format!("set_target_tracking({item_id}, {target_id}, {notify_range})"));
    }

    fn set_ball_callbacks(
        &mut self,
        ball_id: i64,
        do_target_tracking: Option<crate::blackboards::Observer>,
        do_collision: Option<crate::blackboards::Observer>,
    ) {
        self.calls.push(format!(
            "set_ball_callbacks({ball_id}, {}, {})",
            do_target_tracking.is_some(),
            do_collision.is_some()
        ));
        self.ball_callbacks.insert(ball_id, (do_target_tracking, do_collision));
    }

    fn stop_ex(&mut self, item_id: i64) {
        self.calls.push(format!("stop_ex({item_id})"));
    }

    fn explode_ex(&mut self, ball_id: i64) {
        self.calls.push(format!("explode_ex({ball_id})"));
    }

    fn missile_follow(&mut self, fighter_id: i64, target_id: i64, controller_id: i64) {
        self.calls.push(format!("missile_follow({fighter_id}, {target_id}, {controller_id})"));
    }

    fn set_ball_massive(&mut self, ball_id: i64, massive: bool) {
        self.calls.push(format!("set_ball_massive({ball_id}, {massive})"));
    }

    fn controller_for_fighter(&self, fighter_id: i64) -> Option<i64> {
        self.fighter_controllers.get(&fighter_id).copied()
    }

    fn has_pending_cloak(&self, ball_id: i64) -> bool {
        self.pending_cloak.contains(&ball_id)
    }

    fn is_ball_protected(&self, ball_id: i64) -> bool {
        self.protected.contains(&ball_id)
    }

    fn ball_ids_and_dist_in_range(&self, ball_id: i64, range: f64) -> Vec<(f64, i64)> {
        let Some(center) = self.balls.get(&ball_id) else { return Vec::new() };
        let mut found: Vec<(f64, i64)> = self
            .balls
            .values()
            .filter(|b| b.id != ball_id)
            .map(|b| {
                let d = (b.position[0] - center.position[0]).powi(2)
                    + (b.position[1] - center.position[1]).powi(2)
                    + (b.position[2] - center.position[2]).powi(2);
                (d, b.id)
            })
            .filter(|(d, _)| *d <= range * range)
            .collect();
        found.sort_by(|a, b| a.partial_cmp(b).unwrap_or(std::cmp::Ordering::Equal));
        found
    }

    fn ball_source_id(&self, ball_id: i64) -> Option<i64> {
        self.ball_sources.get(&ball_id).copied()
    }

    fn slim_items(&self) -> Vec<SlimItem> {
        let mut items: Vec<SlimItem> = self.slims.values().cloned().collect();
        items.sort_by_key(|s| s.item_id);
        items
    }

    fn proximity_sensor_distance_to(&self, item_id: i64) -> Option<f64> {
        self.sensor_distances.get(&item_id).copied()
    }

    fn replace_proximity_sensor(&mut self, item_id: i64) {
        self.calls.push(format!("replace_proximity_sensor({item_id})"));
    }

    fn add_behavior_context_tags(&mut self, item_id: i64) {
        self.calls.push(format!("add_behavior_context_tags({item_id})"));
    }

    fn add_proximity_sensor_tag(&mut self, item_id: i64, tag: &str) {
        self.calls.push(format!("add_proximity_sensor_tag({item_id}, {tag})"));
    }

    fn register_proximity_sensor(&mut self, item_id: i64) -> Option<i64> {
        self.calls.push(format!("register_proximity_sensor({item_id})"));
        Some(item_id)
    }

    fn has_ball(&self, ball_id: i64) -> bool {
        self.balls.contains_key(&ball_id)
    }

    fn ball(&self, ball_id: i64) -> Option<Ball> {
        self.balls.get(&ball_id).cloned()
    }

    fn slim_item(&self, item_id: i64) -> Option<SlimItem> {
        self.slims.get(&item_id).cloned()
    }

    fn ball_ids_in_bubble(&self, bubble_id: i64) -> Option<Vec<i64>> {
        self.bubbles.get(&bubble_id).cloned()
    }

    fn ship_ids_in_bubble(&self, bubble_id: i64) -> Vec<i64> {
        self.bubbles
            .get(&bubble_id)
            .map(|ids| {
                ids.iter()
                    .copied()
                    .filter(|id| self.slims.get(id).is_some_and(|s| s.category_id == 6))
                    .collect()
            })
            .unwrap_or_default()
    }

    fn global_ball_ids(&self) -> Vec<i64> {
        self.globals.clone()
    }

    fn surface_dist(&self, a: i64, b: i64) -> WorldResult<f64> {
        self.surface_dists
            .get(&(a, b))
            .copied()
            .ok_or_else(|| WorldError::Runtime(format!("no distance between {a} and {b}")))
    }

    fn is_cloaked(&self, ball_id: i64) -> WorldResult<bool> {
        Ok(self.cloaked.contains(&ball_id))
    }

    fn is_invulnerable(&self, item_id: i64) -> bool {
        self.invulnerable.contains(&item_id)
    }

    fn is_warping(&self, ball_id: i64) -> WorldResult<i64> {
        self.warping.get(&ball_id).copied().ok_or_else(|| WorldError::User("no such ship".into()))
    }

    fn bubble_at_coordinates(&self, position: [f64; 3]) -> i64 {
        self.bubble_of_position.get(&position.map(|c| c as i64)).copied().unwrap_or(-1)
    }

    fn closest_disruptor_and_intersection_in_warp_path(
        &self,
        _from: [f64; 3],
        to: [f64; 3],
    ) -> WorldResult<Option<(i64, [f64; 3])>> {
        self.warp_paths.get(&to.map(|c| c as i64)).cloned().unwrap_or(Ok(None))
    }

    fn warp_bubble_item_ids_within_range_of_coordinate(&self, _position: [f64; 3]) -> (Vec<i64>, Vec<i64>) {
        self.warp_bubbles.clone()
    }

    fn is_structure_vulnerable(&self, ball_id: i64) -> WorldResult<bool> {
        if self.balls.contains_key(&ball_id) {
            Ok(self.vulnerable_structures.contains(&ball_id))
        } else {
            Err(WorldError::Runtime("no such structure".into()))
        }
    }

    fn is_aggressed(&self, item_id: i64) -> bool {
        self.aggressed.contains(&item_id)
    }

    fn distance_between(&self, a: i64, b: i64) -> WorldResult<f64> {
        self.surface_dist(a, b)
    }

    fn solar_system_id(&self) -> Option<i64> {
        self.solar_system
    }

    fn set_bubble_events(&mut self, bubble_id: i64, on: bool) {
        self.calls.push(format!("set_bubble_events({bubble_id}, {on})"));
    }

    fn proximity_sensor(&self, item_id: i64) -> Option<ProximitySensor> {
        self.sensors.iter().find(|(sensor, _)| sensor.item_id == item_id).map(|(sensor, _)| sensor.clone())
    }

    fn proximity_sensors(&self, tags: &[String]) -> Vec<ProximitySensor> {
        self.sensors
            .iter()
            .filter(|(_, own)| tags.iter().all(|tag| own.contains(tag)))
            .map(|(sensor, _)| sensor.clone())
            .collect()
    }

    fn add_proximity_sensor(
        &mut self,
        item_id: i64,
        type_id: i64,
        detection_range: f64,
    ) -> Option<ProximitySensor> {
        self.calls.push(format!("add_proximity_sensor({item_id}, {type_id}, {detection_range})"));
        let sensor = ProximitySensor { item_id, ..Default::default() };
        self.sensors.push((sensor.clone(), Vec::new()));
        Some(sensor)
    }

    fn turbo_shield_is_state(&self, item_id: i64, state: i64) -> Option<bool> {
        self.turbo_shield_states.get(&item_id).map(|current| *current == state)
    }

    fn make_invulnerable_permanently(&mut self, ball_id: i64, reason: &str) {
        self.calls.push(format!("make_invulnerable_permanently({ball_id}, {reason})"));
    }

    fn cancel_current_invulnerability(&mut self, ball_id: i64, reason: &str) {
        self.calls.push(format!("cancel_current_invulnerability({ball_id}, {reason})"));
    }

    fn cloak_ship(&mut self, item_id: i64) {
        self.calls.push(format!("cloak_ship({item_id})"));
    }

    fn uncloak_ship(&mut self, item_id: i64) {
        self.calls.push(format!("uncloak_ship({item_id})"));
    }
}

impl Dogma for TestWorld {
    fn active_effect_data(&self, item_id: i64, effect_id: i64) -> Option<EffectCycle> {
        self.effect_cycles.get(&(item_id, effect_id)).copied()
    }

    fn effect_max_effective_range_with_falloff(&self, effect_id: i64, _item_id: i64) -> f64 {
        self.max_effective_ranges.get(&effect_id).copied().unwrap_or(0.0)
    }

    fn range_falloff_modifier_at_distance(&self, _effect_id: i64, _source_id: i64, distance: f64) -> f64 {
        self.falloff_at_distance.get(&(distance as i64)).copied().unwrap_or(1.0)
    }

    fn locked_targets(&self, item_id: i64) -> Vec<i64> {
        self.targets(item_id)
    }

    fn is_exploding(&self, item_id: i64) -> WorldResult<bool> {
        if self.exploding_error.is_some() {
            return Err(self.exploding_error.clone().unwrap_or(WorldError::Runtime(String::new())));
        }
        Ok(self.exploding.contains(&item_id))
    }

    fn set_attribute_value(&mut self, item_id: i64, attribute_id: i64, value: &Value) {
        self.calls.push(format!("set_attribute_value({item_id}, {attribute_id}, {})", plain(value)));
    }

    fn apply_damage_with_multiplier(
        &mut self,
        source_item_id: i64,
        target_id: i64,
        damage_multiplier: f64,
        weapon_damages: &[(i64, f64)],
    ) {
        self.calls.push(format!(
            "apply_damage_with_multiplier({source_item_id}, {target_id}, {damage_multiplier:.4}, {weapon_damages:?})"
        ));
    }

    fn fitted_item_keys(&self, item_id: i64) -> Option<Vec<Value>> {
        self.fitted_keys.get(&item_id).cloned()
    }

    fn cloud_factor(&self, target_id: i64, cloud_size: f64) -> f64 {
        self.cloud_factors.get(&(target_id, cloud_size.to_bits())).copied().unwrap_or(1.0)
    }

    fn apply_damage(
        &mut self,
        source_item_id: i64,
        target_id: i64,
        damage_amount: f64,
        source_id: i64,
        weapon_damages: &[(i64, f64)],
    ) {
        self.calls.push(format!(
            "apply_damage({source_item_id}, {target_id}, {damage_amount}, {source_id}, {weapon_damages:?})"
        ));
    }

    fn effect_range_with_falloff_multiplier(&self, effect_id: i64, item_id: i64, factor: f64) -> f64 {
        self.effect_ranges
            .get(&(item_id, effect_id))
            .map_or(0.0, |(optimal, falloff)| optimal.unwrap_or(0.0) + falloff.unwrap_or(0.0) * factor)
    }

    fn effect_range_and_falloff_for_item(&self, item_id: i64, effect_id: i64) -> (Option<f64>, Option<f64>) {
        self.effect_ranges.get(&(item_id, effect_id)).copied().unwrap_or((None, None))
    }

    fn add_target_ex(&mut self, item_id: i64, target_id: i64) -> WorldResult<()> {
        if let Some(error) = self.activation_error.clone() {
            return Err(error);
        }
        self.calls.push(format!("add_target_ex({item_id}, {target_id})"));
        Ok(())
    }

    fn is_ship_piloted(&self, ship_id: i64) -> bool {
        self.piloted.contains(&ship_id)
    }

    fn attribute_value(&self, item_id: i64, attribute_id: i64) -> WorldResult<f64> {
        if let Some(error) = self.attribute_errors.get(&(item_id, attribute_id)) {
            return Err(error.clone());
        }
        Ok(self.attributes.get(&(item_id, attribute_id)).copied().unwrap_or(0.0))
    }

    fn type_attribute(&self, type_id: i64, attribute_id: i64) -> f64 {
        self.type_attributes.get(&(type_id, attribute_id)).copied().unwrap_or(0.0)
    }

    fn type_has_effect(&self, type_id: i64, effect_id: i64) -> bool {
        self.type_effects.contains(&(type_id, effect_id))
    }

    fn is_effect_active(&self, item_id: i64, effect_id: i64) -> bool {
        self.active_effects.contains(&(item_id, effect_id))
    }

    fn targets(&self, item_id: i64) -> Vec<i64> {
        self.targets.get(&item_id).cloned().unwrap_or_default()
    }

    fn targets_ex(&self, item_id: i64) -> Vec<i64> {
        self.targets(item_id)
    }

    fn has_pending_target(&self, item_id: i64, target_id: Option<i64>) -> bool {
        target_id.is_some_and(|t| self.pending_targets.contains(&(item_id, t)))
    }

    fn is_targeting(&self, item_id: i64, target_id: Option<i64>) -> bool {
        target_id.is_some_and(|t| self.targeting.contains(&(item_id, t)))
    }

    fn is_item_active(&self, item_id: i64) -> bool {
        self.active_items.contains(&item_id)
    }

    fn fitted_module_group_ids(&self, ship_id: i64) -> Vec<i64> {
        self.fitted_group_ids.get(&ship_id).cloned().unwrap_or_default()
    }

    fn online_fitted_modules(&self, item_id: i64) -> Vec<(i64, i64, i64)> {
        self.fitted_modules.get(&item_id).cloned().unwrap_or_default()
    }

    fn is_ship_abandoned(&self, ship_id: i64) -> bool {
        self.abandoned.contains(&ship_id)
    }

    fn active_effect_target_id(&self, item_id: i64, effect_id: i64) -> Option<Option<i64>> {
        self.active_effect_targets.get(&(item_id, effect_id)).copied()
    }

    fn effect_info(&self, effect_id: i64) -> Option<EffectInfo> {
        self.effects.get(&effect_id).cloned()
    }

    fn default_effect_id(&self, type_id: i64) -> Option<i64> {
        self.default_effects.get(&type_id).copied()
    }

    fn activate_with_context(
        &mut self,
        owner_id: i64,
        module_id: i64,
        effect_id: Option<i64>,
        target_id: Option<i64>,
        repeat: i64,
        _context: &crate::value::Value,
    ) -> WorldResult<()> {
        if let Some(error) = self.activation_error.clone() {
            return Err(error);
        }
        let effect = effect_id.map_or("None".to_string(), |effect| effect.to_string());
        self.calls.push(format!("activate({owner_id}, {module_id}, {effect}, {target_id:?}, {repeat})"));
        Ok(())
    }

    fn stop_effect_force_repeating(&mut self, effect_id: i64, module_id: i64) -> WorldResult<()> {
        self.calls.push(format!("stop_effect_force_repeating({effect_id}, {module_id})"));
        Ok(())
    }

    fn stop_effect(&mut self, effect_id: i64, item_id: i64, forced: bool) -> WorldResult<()> {
        self.calls.push(format!("stop_effect({effect_id}, {item_id}, {forced})"));
        self.stop_effect_error.clone().map_or(Ok(()), Err)
    }

    fn remove_target(&mut self, item_id: i64, target_id: i64) {
        self.calls.push(format!("remove_target({item_id}, {target_id})"));
    }
}

impl Logging for TestWorld {
    fn log_adding_spawn_points(
        &mut self,
        owner_id: Option<i64>,
        spawn_pool_id: &Value,
        requested: f64,
        min: &Value,
        max: &Value,
        actual: f64,
    ) {
        self.calls.push(format!("log_adding_spawn_points({owner_id:?}, {spawn_pool_id:?}, {requested}, {min:?}, {max:?}, {actual})"));
    }

    fn log_killed_and_fled_players(
        &mut self,
        owner_id: Option<i64>,
        behavior_id: &crate::value::Value,
        target_set: &crate::value::Value,
        members: &[i64],
    ) {
        self.calls.push(format!(
            "log_killed_and_fled_players({owner_id:?}, {behavior_id}, {target_set}, {members:?})"
        ));
    }

    fn log_behavior_task(
        &mut self,
        event: &crate::value::Value,
        owner_id: Option<i64>,
        behavior_id: &crate::value::Value,
        type_id: i64,
    ) {
        self.calls.push(format!("log_behavior_task({event}, {owner_id:?}, {behavior_id}, {type_id})"));
    }

    fn log_behavior_group_destroyed(
        &mut self,
        owner_id: Option<i64>,
        behavior_id: &crate::value::Value,
        combat_targets: &crate::value::Value,
    ) {
        self.calls
            .push(format!("log_behavior_group_destroyed({owner_id:?}, {behavior_id}, {combat_targets})"));
    }

    fn log_error(&mut self, message: &str) {
        self.calls.push(format!("log_error({message})"));
    }

    fn log_behavior_aggressed(
        &mut self,
        owner_id: Option<i64>,
        behavior_id: &crate::value::Value,
        group_behavior_id: &crate::value::Value,
        target_type_id: Option<i64>,
        aggressor_id: i64,
    ) {
        self.calls.push(format!("log_behavior_aggressed({owner_id:?}, {behavior_id}, {group_behavior_id}, {target_type_id:?}, {aggressor_id})"));
    }

    fn log_monitored_target_aggressed(
        &mut self,
        owner_id: Option<i64>,
        behavior_id: &crate::value::Value,
        group_behavior_id: &crate::value::Value,
        target_type_id: Option<i64>,
        aggressor_id: i64,
    ) {
        self.calls.push(format!("log_monitored_target_aggressed({owner_id:?}, {behavior_id}, {group_behavior_id}, {target_type_id:?}, {aggressor_id})"));
    }
}

impl Entities for TestWorld {
    fn ability_activation_succeeded(
        &mut self,
        item_id: i64,
        slot: &Value,
        start_time: i64,
        duration_ms: f64,
    ) {
        self.ability_events.push(format!("succeeded({item_id}, {slot:?}, {start_time}, {duration_ms})"));
    }

    fn ability_activation_context(&self, item_id: i64, slot: &Value) -> Option<Value> {
        self.ability_contexts.get(&(item_id, slot.as_int().unwrap_or(-1))).cloned()
    }

    fn start_fighter_ability_dogma_effect(
        &mut self,
        effect_id: i64,
        context: &Value,
        target_id: Option<i64>,
    ) -> WorldResult<()> {
        if let Some(error) = self.ability_error.clone() {
            return Err(error);
        }
        self.ability_events.push(format!("started({effect_id}, {context:?}, {target_id:?})"));
        Ok(())
    }

    fn ability_deactivated(&mut self, item_id: i64, slot: &Value, failure_reason: &Value) {
        self.ability_events.push(format!("deactivated({item_id}, {slot:?}, {failure_reason:?})"));
    }

    fn ability_activation_failed(&mut self, item_id: i64, slot: &Value) {
        self.ability_events.push(format!("failed({item_id}, {slot:?})"));
    }

    fn generate_player_fleet_report(&mut self, _spawn_pool_id: &Value) -> WorldResult<PlayerFleetReport> {
        self.player_fleet_report
            .clone()
            .ok_or_else(|| WorldError::Runtime("no player fleet report".to_string()))
    }

    fn total_npc_fleet_points(
        &mut self,
        _spawn_pool_id: &Value,
        _spawn_table_id: &Value,
    ) -> WorldResult<f64> {
        Ok(self.total_npc_points)
    }

    fn fleet_type_support(&self) -> Value {
        Value::Str("support".into())
    }

    fn supported_fleet_types(&self) -> Vec<Value> {
        vec![Value::Str("dps".into()), Value::Str("tackle".into())]
    }

    fn fleet_types_to_counter(&self) -> Vec<Value> {
        vec![Value::Str("dps".into()), Value::Str("tackle".into()), Value::Str("support".into())]
    }

    fn structure_state(&self, ball_id: i64) -> Option<StructureState> {
        self.structure_states.get(&ball_id).copied()
    }

    fn add_type_and_amount_to_additional_loot(
        &mut self,
        ball_id: i64,
        type_id: i64,
        amount: &Value,
        allow_overload: Option<&Value>,
    ) {
        self.calls.push(format!("add_additional_loot({ball_id}, {type_id}, {amount:?}, {allow_overload:?})"));
    }

    fn remove_type_from_additional_loot(&mut self, ball_id: i64, type_id: i64) {
        self.calls.push(format!("remove_additional_loot_type({ball_id}, {type_id})"));
    }

    fn remove_additional_loot(&mut self, ball_id: i64) -> Vec<(i64, Value)> {
        self.calls.push(format!("remove_additional_loot({ball_id})"));
        self.removed_additional_loot.get(&ball_id).cloned().unwrap_or_default()
    }

    fn behavior_entry_keys(&self) -> Vec<i64> {
        self.behavior_entries.clone()
    }

    fn unspawn_entity_ball(&mut self, item_id: i64) {
        self.calls.push(format!("unspawn_entity_ball({item_id})"));
    }

    fn is_awareness_registered(&self, member_collection_id: &Value) -> bool {
        self.registered_awareness.iter().any(|(id, _)| id.py_eq(member_collection_id))
    }

    fn register_group_awareness(
        &mut self,
        member_collection_id: &Value,
        config: crate::utility::awareness::GroupAwarenessConfig,
    ) {
        self.registered_awareness.push((member_collection_id.clone(), config));
    }

    fn register_spawn_pool_fleet_awareness(
        &mut self,
        spawn_pool_id: &Value,
        owner_id: Option<i64>,
        blackboard_id: Option<i64>,
        behavior_id: &Value,
        config: crate::utility::awareness::GroupAwarenessConfig,
    ) {
        self.calls.push(format!("register_spawn_pool_fleet_awareness({spawn_pool_id:?}, {owner_id:?}, {blackboard_id:?}, {behavior_id:?})"));
        self.fleet_awareness.push((spawn_pool_id.clone(), config, Vec::new(), false));
    }

    fn add_item_to_spawn_pool_fleet_awareness(&mut self, spawn_pool_id: &Value, item_id: i64) -> bool {
        match self.fleet_awareness.iter_mut().find(|(id, _, _, _)| id.py_eq(spawn_pool_id)) {
            Some((_, _, members, _)) => {
                members.push(item_id);
                true
            }
            None => false,
        }
    }

    fn fleet_awareness_lacks_enemy_cluster_generator(&self, spawn_pool_id: &Value) -> Option<bool> {
        self.fleet_awareness
            .iter()
            .find(|(id, _, _, _)| id.py_eq(spawn_pool_id))
            .map(|(_, _, _, enabled)| !enabled)
    }

    fn enable_enemy_cluster_generator(
        &mut self,
        spawn_pool_id: &Value,
        update_interval_seconds: &Value,
        channel: crate::tree::ChannelRef,
    ) {
        if let Some(entry) = self.fleet_awareness.iter_mut().find(|(id, _, _, _)| id.py_eq(spawn_pool_id)) {
            entry.3 = true;
        }
        self.calls.push(format!(
            "enable_enemy_cluster_generator({spawn_pool_id:?}, {update_interval_seconds:?}, {})",
            channel.name
        ));
    }

    fn threat_time_window_seconds(&self) -> f64 {
        self.threat_window
    }

    fn assign_additional_loot_to_group_entities(&mut self, group_id: Option<i64>) {
        self.calls.push(format!("assign_additional_loot({group_id:?})"));
    }

    fn unspawn_behavior_entity_group_with_respawn_enabled(&mut self, group_id: Option<i64>) {
        self.calls.push(format!("unspawn_group({group_id:?})"));
    }

    fn reset_behavior_tree(&mut self, item_id: i64) -> bool {
        self.calls.push(format!("reset_behavior_tree({item_id})"));
        self.behavior_trees.contains(&item_id)
    }

    fn add_group_removal_handler(
        &mut self,
        group_id: Option<i64>,
        channel: crate::tree::ChannelRef,
        value: Value,
    ) {
        if let Some(group_id) = group_id {
            self.group_removal_handlers.push((group_id, channel, value));
        }
    }

    fn dungeon_spawn_entry_pos(&self, spawn_id: i64) -> Option<[f64; 3]> {
        self.dungeon_entries.get(&spawn_id).copied()
    }

    fn dungeon_item_id_for_object(&self, spawn_id: i64, object_id: i64) -> Option<i64> {
        self.dungeon_objects.get(&(spawn_id, object_id)).copied()
    }

    fn dungeon_room_objects(&self, dungeon_id: i64) -> Vec<DungeonObject> {
        self.dungeon_rooms.get(&dungeon_id).cloned().unwrap_or_default()
    }

    fn call_in_reinforcements(&mut self, group_id: i64, thresholds: Option<ResponseThresholds>) {
        self.calls.push(format!("call_in_reinforcements({group_id}, {})", thresholds.is_some()));
    }

    fn reinforced_group_exists(&self, group_id: i64) -> bool {
        self.reinforced_groups.contains(&group_id)
    }

    fn add_entity_group_reinforcement(
        &mut self,
        group_id: i64,
        entity_count: usize,
        owner_id: Option<i64>,
        wave: ReinforcementWave,
    ) {
        let override_id = wave.behavior_override_id.map(|v| v.to_string());
        self.calls.push(format!(
            "add_entity_group_reinforcement({group_id}, {entity_count}, {owner_id:?}, {}, {}, {}, {override_id:?})",
            wave.wave_id, wave.cooldown_seconds, wave.threshold
        ));
    }

    fn remove_active_reinforced_entity_ids(&mut self, group_id: i64) {
        self.calls.push(format!("remove_active_reinforced_entity_ids({group_id})"));
    }

    fn operation_spawnpoint_position(
        &self,
        character_id: Option<i64>,
        site_id: Option<i64>,
    ) -> WorldResult<(i64, i64, f64, f64, f64)> {
        let key = (character_id.unwrap_or(0), site_id.unwrap_or(0));
        self.operation_spawnpoints
            .get(&key)
            .copied()
            .ok_or_else(|| WorldError::Runtime("no such operation site".into()))
    }

    fn register_payment_on_explosion(
        &mut self,
        item_id: i64,
        bubble_id: i64,
        reward_id: Option<i64>,
        tale_id: i64,
        dungeon_id: i64,
    ) {
        self.calls.push(format!(
            "register_payment_on_explosion({item_id}, {bubble_id}, {reward_id:?}, {tale_id}, {dungeon_id})"
        ));
    }

    fn tale_id_for_persisted_object(&self, item_id: i64) -> Option<i64> {
        self.persisted_tales.get(&item_id).copied()
    }

    fn is_reinforcement_authorized(&self, group_id: i64) -> WorldResult<bool> {
        self.reinforcement_authorized
            .get(&group_id)
            .cloned()
            .unwrap_or(Err(WorldError::ReinforcedGroupNotFound))
    }

    fn are_reinforcements_available(&self, group_id: i64) -> bool {
        self.reinforcements_available.contains(&group_id)
    }

    fn behavior_group_behavior_id(&self, group_id: i64) -> Option<crate::value::Value> {
        self.group_behavior_ids.get(&group_id).cloned()
    }

    fn fleet_awareness_member_ids(&self, spawn_pool_id: Option<i64>) -> Vec<i64> {
        spawn_pool_id.and_then(|p| self.fleet_members.get(&p).cloned()).unwrap_or_default()
    }

    fn npc_fleet_cost_for_item_ids(&self, spawn_table_id: Option<i64>, _item_ids: &[i64]) -> f64 {
        spawn_table_id.and_then(|t| self.npc_fleet_costs.get(&t).copied()).unwrap_or(0.0)
    }

    fn player_fleet_cost_for_item_ids(&self, item_ids: &[i64]) -> f64 {
        item_ids.iter().filter_map(|id| self.player_fleet_costs.get(id)).sum()
    }

    fn has_type_in_additional_loot(&self, item_id: i64, type_id: i64) -> bool {
        self.additional_loot.get(&item_id).is_some_and(|types| types.contains(&type_id))
    }

    fn has_enough_additional_cargo_capacity_for_type(
        &self,
        item_id: i64,
        type_id: i64,
        quantity: i64,
    ) -> bool {
        let volume =
            self.type_attributes_by_name.get(&(type_id, "volume".to_string())).copied().unwrap_or(0.0);
        self.additional_capacity.get(&item_id).copied().unwrap_or(0.0) - volume * quantity as f64 >= 0.0
    }

    fn available_additional_cargo_capacity(&self, item_id: i64) -> f64 {
        self.additional_capacity.get(&item_id).copied().unwrap_or(0.0)
    }

    fn response_thresholds(
        &self,
        _item_id: i64,
        _entity_group_id: Option<i64>,
    ) -> Option<ResponseThresholds> {
        self.thresholds
    }

    fn group_exists(&self, group_id: i64) -> bool {
        self.groups.contains_key(&group_id)
    }

    fn group_members(&self, group_id: i64) -> Option<Vec<i64>> {
        self.groups.get(&group_id).map(|g| g.members.clone())
    }

    fn group_count(&self, group_id: i64) -> Option<usize> {
        self.groups.get(&group_id).map(|g| g.members.len())
    }

    fn group_owner_id(&self, group_id: i64) -> Option<i64> {
        self.groups.get(&group_id).and_then(|g| g.owner_id)
    }

    fn group_has_entity(&self, group_id: i64, item_id: i64) -> Option<bool> {
        self.groups.get(&group_id).map(|g| g.members.contains(&item_id))
    }

    fn group_initial_spawning_complete(&self, group_id: i64) -> Option<bool> {
        self.groups.get(&group_id).map(|g| g.initial_spawning_complete)
    }

    fn find_group_id_for_item(&self, item_id: i64) -> i64 {
        self.item_groups.get(&item_id).copied().unwrap_or(super::INVALID_GROUP_ID)
    }

    fn behavior_group_owner_id(&self, group_id: i64) -> Option<i64> {
        self.groups.get(&group_id).and_then(|g| g.owner_id)
    }

    fn current_spawn_points(&self, spawn_pool_id: Option<i64>) -> f64 {
        spawn_pool_id.and_then(|p| self.spawn_points.get(&p).copied()).unwrap_or(0.0)
    }

    fn active_reinforced_entity_ids(&self, group_id: i64) -> Vec<i64> {
        self.reinforced.get(&group_id).cloned().unwrap_or_default()
    }

    fn does_spawn_exist(&self, dungeon_spawn_id: i64) -> bool {
        self.dungeon_spawns.contains(&dungeon_spawn_id)
    }
}

impl Spawning for TestWorld {
    fn spawn_data_for_entities(
        &mut self,
        types_and_counts: &[(i64, i64)],
        position: Option<[f64; 3]>,
        deployment_range: f64,
        behavior_override_id: Option<&Value>,
    ) -> Vec<EntitySpawnData> {
        self.calls.push(format!(
            "spawn_data_for_entities({types_and_counts:?}, {position:?}, {deployment_range}, {behavior_override_id:?})"
        ));
        let mut data = Vec::new();
        for (type_id, count) in types_and_counts {
            for _ in 0..*count {
                data.push(EntitySpawnData {
                    entity_type_id: *type_id,
                    spawn_position: position,
                    direction: None,
                    behavior_override_id: behavior_override_id.cloned(),
                });
            }
        }
        data
    }

    fn spawn_data_for_entities_with_custom_behavior_overrides(
        &mut self,
        types_and_counts: &[(i64, i64)],
        position: Option<[f64; 3]>,
        deployment_range: f64,
    ) -> Vec<EntitySpawnData> {
        self.calls.push(format!(
            "spawn_data_with_custom_overrides({types_and_counts:?}, {position:?}, {deployment_range})"
        ));
        types_and_counts
            .iter()
            .flat_map(|(type_id, count)| {
                (0..*count).map(move |_| EntitySpawnData {
                    entity_type_id: *type_id,
                    spawn_position: position,
                    ..Default::default()
                })
            })
            .collect()
    }

    fn select_probability_items(&mut self, spawn_list_id: i64) -> Vec<(i64, f64)> {
        self.spawn_lists.get(&spawn_list_id).cloned().unwrap_or_default()
    }

    fn generate_grouping(&mut self, entity_group_id: i64) -> Vec<(i64, i64)> {
        self.groupings.get(&entity_group_id).cloned().unwrap_or_default()
    }

    fn register_new_spawn_point_pool(
        &mut self,
        max_spawn_points: &Value,
        initial_spawn_points: &Value,
    ) -> i64 {
        self.next_spawn_pool_id += 1;
        let id = self.next_spawn_pool_id;
        let initial = initial_spawn_points.as_float().or_else(|| max_spawn_points.as_float()).unwrap_or(0.0);
        self.spawn_points.insert(id, initial);
        self.calls.push(format!(
            "register_new_spawn_point_pool({max_spawn_points:?}, {initial_spawn_points:?}) = {id}"
        ));
        id
    }

    fn consume_spawn_points(&mut self, spawn_pool_id: i64, amount: f64) {
        *self.spawn_points.entry(spawn_pool_id).or_insert(0.0) -= amount;
        self.calls.push(format!("consume_spawn_points({spawn_pool_id}, {amount})"));
    }

    fn set_spawn_points(&mut self, spawn_pool_id: Option<i64>, amount: &Value) {
        if let (Some(pool), Some(amount)) = (spawn_pool_id, amount.as_float()) {
            self.spawn_points.insert(pool, amount);
        }
        self.calls.push(format!("set_spawn_points({spawn_pool_id:?}, {amount:?})"));
    }

    fn add_spawn_points(&mut self, spawn_pool_id: Option<i64>, amount: f64) {
        if let Some(pool) = spawn_pool_id {
            *self.spawn_points.entry(pool).or_insert(0.0) += amount;
        }
        self.calls.push(format!("add_spawn_points({spawn_pool_id:?}, {amount})"));
    }

    fn pick_npcs_from_table_by_points_and_tags(
        &mut self,
        spawn_table_id: Option<i64>,
        spawn_points: f64,
        required_npc_tags: &Value,
        max_npc_count: Option<i64>,
    ) -> Option<SpawnPick> {
        self.calls.push(format!(
            "pick_npcs({spawn_table_id:?}, {spawn_points}, {required_npc_tags:?}, {max_npc_count:?})"
        ));
        self.spawn_pick.clone()
    }

    fn on_entity_group_spawn(&mut self, spawn_pool_id: Option<i64>, entity_group_id: i64) {
        self.calls.push(format!("on_entity_group_spawn({spawn_pool_id:?}, {entity_group_id})"));
    }

    fn spawn_table_owner(&self, spawn_table_id: Option<i64>) -> WorldResult<i64> {
        spawn_table_id
            .and_then(|id| self.spawn_table_owners.get(&id).copied())
            .ok_or_else(|| WorldError::Key(format!("spawn table {spawn_table_id:?}")))
    }

    fn deploy_behavior_entities(&mut self, deployment: Deployment) -> WorldResult<Deployed> {
        let group_id = deployment.group_id.unwrap_or_else(|| {
            self.next_group_id += 1;
            9_000 + self.next_group_id
        });
        let mut ids = Vec::new();
        for _ in &deployment.spawn_data {
            self.next_deployed_item_id += 1;
            ids.push(9_000_000_000_000_000_000 + self.next_deployed_item_id);
        }
        self.deployments.push(deployment);
        Ok(Deployed { group_id, entity_item_ids: ids })
    }

    fn register_bounty_pay_out_multiplier(&mut self, entity_item_ids: &[i64], multiplier: &Value) {
        self.calls.push(format!("register_bounty_pay_out_multiplier({entity_item_ids:?}, {multiplier:?})"));
    }

    fn random_deep_space_location(&mut self, _solar_system_id: Option<i64>) -> [f64; 3] {
        self.deep_space_location
    }

    fn undock_vector(&mut self, structure_id: i64, type_id: i64) -> ([f64; 3], [f64; 3]) {
        self.undock_vectors
            .get(&(structure_id, type_id))
            .copied()
            .unwrap_or(([0.0, 0.0, 0.0], [0.0, 0.0, 1.0]))
    }

    fn reinforced_group_full_strength_count(&self, group_id: i64) -> Option<i64> {
        self.reinforced_full_strength.get(&group_id).copied()
    }

    fn set_reinforced_group_entity_unit_count(&mut self, group_id: i64, count: i64) {
        self.reinforced_full_strength.insert(group_id, count);
        self.calls.push(format!("set_entity_unit_count({group_id}, {count})"));
    }

    fn set_real_item_respawn(
        &mut self,
        group_id: Option<i64>,
        owner_id: Option<i64>,
        spawn_data: Vec<EntitySpawnData>,
        min_delay_seconds: &Value,
        max_delay_seconds: &Value,
    ) {
        self.calls.push(format!(
            "set_real_item_respawn({group_id:?}, {owner_id:?}, {min_delay_seconds:?}, {max_delay_seconds:?})"
        ));
        self.respawns.push((group_id, owner_id, spawn_data));
    }

    fn remove_respawn_group(&mut self, group_id: Option<i64>) {
        self.calls.push(format!("remove_respawn_group({group_id:?})"));
    }

    fn start_reactive_spawner(&mut self, config: ReactiveSpawnConfig) -> i64 {
        self.next_spawn_pool_id += 1;
        self.reactive_spawners.push(config);
        self.next_spawn_pool_id
    }
}

impl Inventory for TestWorld {
    fn has_cargo_bay_component(&self, type_id: i64) -> bool {
        self.cargo_bay_types.contains(&type_id)
    }

    fn add_fake_location(&mut self, location: FakeLocation) {
        self.calls.push(format!(
            "add_fake_location({}, {:?}, {}, {:?}, {:?})",
            location.type_id,
            location.owner_id,
            location.solar_system_id,
            location.position,
            location.object_name_id
        ));
    }

    fn insert_location(&mut self, location: FakeLocation) {
        self.calls.push(format!(
            "insert_location({}, {:?}, {}, {:?})",
            location.type_id, location.owner_id, location.solar_system_id, location.position
        ));
    }

    fn inventory_item(&self, item_id: i64) -> Option<InventoryItem> {
        self.items.get(&item_id).cloned()
    }

    fn cargo_capacity(&self, item_id: i64) -> WorldResult<CargoCapacity> {
        self.cargo
            .get(&item_id)
            .copied()
            .ok_or_else(|| WorldError::Runtime(format!("no cargo bay for {item_id}")))
    }

    fn select_items(&self, item_id: i64) -> Vec<i64> {
        self.cargo_contents.get(&item_id).cloned().unwrap_or_default()
    }

    fn destroy_item(&mut self, item_id: i64) {
        self.calls.push(format!("destroy_item({item_id})"));
    }

    fn manufacture_item_raw(&mut self, type_id: i64, portions: i64, location_id: i64, flag: i64) {
        self.calls.push(format!("manufacture_item_raw({type_id}, {portions}, {location_id}, {flag})"));
    }
}

impl Universe for TestWorld {
    fn singlecast_by_char_id(&mut self, char_id: Option<i64>, notification: &str, args: &[Value]) {
        self.calls.push(format!("singlecast_by_char_id({char_id:?}, {notification}, {args:?})"));
    }

    fn is_pre_inv64_player_character(&self, owner_id: i64) -> bool {
        self.pre_inv64_characters.contains(&owner_id)
    }

    fn singlecast_by_solar_system_id(
        &mut self,
        solar_system_id: Option<i64>,
        notification: &str,
        args: &[Value],
    ) {
        self.calls.push(format!("singlecast({solar_system_id:?}, {notification}, {args:?})"));
    }

    fn solar_system_faction_id(&self, solar_system_id: i64) -> Option<i64> {
        self.system_factions.get(&solar_system_id).copied()
    }

    fn type_category_id(&self, type_id: i64) -> Option<i64> {
        self.type_categories.get(&type_id).copied()
    }

    fn ship_id_for_char_id(&self, char_id: i64) -> Option<i64> {
        self.char_ships.get(&char_id).copied()
    }

    fn damage_tracker_totals(&self, ship_id: i64, _since: i64) -> DamageTotals {
        self.damage.get(&ship_id).copied().unwrap_or_default()
    }

    fn structure_housing_can_dock(
        &self,
        corporation_id: i64,
        structure_id: i64,
        _character_corporation_id: i64,
        _alliance_id: Option<i64>,
    ) -> bool {
        self.can_dock.contains(&(corporation_id, structure_id))
    }

    fn type_ids_in_list(&self, type_list_id: i64) -> Vec<i64> {
        self.type_ids_by_list.get(&type_list_id).cloned().unwrap_or_default()
    }

    fn type_attribute_by_name(&self, type_id: i64, name: &str) -> f64 {
        self.type_attributes_by_name.get(&(type_id, name.to_string())).copied().unwrap_or(0.0)
    }

    fn security_status(&self, solar_system_id: i64) -> f64 {
        self.security.get(&solar_system_id).copied().unwrap_or(0.0)
    }

    fn corporation_faction_id(&self, corporation_id: i64) -> Option<i64> {
        self.factions.get(&corporation_id).copied()
    }

    fn standings_from_npc_to_item(&self, npc_owner_id: i64, to_id: i64) -> f64 {
        self.standings.get(&(npc_owner_id, to_id)).copied().unwrap_or(0.0)
    }

    fn average_price(&self, type_id: i64) -> Option<f64> {
        self.prices.get(&type_id).copied()
    }
}
