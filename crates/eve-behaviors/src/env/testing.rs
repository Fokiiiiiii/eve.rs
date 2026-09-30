//! A world to test tasks against: a ballpark, a dogma engine and groups held in maps that a test fills in, dice that answer from a list, and a
//! record of what the tasks asked it to do.

use std::collections::{HashMap, HashSet};

use super::{
    Ball, Ballpark, CargoCapacity, DamageTotals, Dogma, DungeonObject, EffectInfo, Entities, FakeLocation,
    Inventory, InventoryItem, Logging, ProximitySensor, Random, ReinforcementWave, ResponseThresholds,
    SlimItem, Universe, WorldError, WorldResult,
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
        effect_id: i64,
        target_id: Option<i64>,
        repeat: i64,
        _context: &crate::value::Value,
    ) -> WorldResult<()> {
        if let Some(error) = self.activation_error.clone() {
            return Err(error);
        }
        self.calls.push(format!("activate({owner_id}, {module_id}, {effect_id}, {target_id:?}, {repeat})"));
        Ok(())
    }

    fn stop_effect_force_repeating(&mut self, effect_id: i64, module_id: i64) -> WorldResult<()> {
        self.calls.push(format!("stop_effect_force_repeating({effect_id}, {module_id})"));
        Ok(())
    }

    fn stop_effect(&mut self, effect_id: i64, item_id: i64, forced: bool) -> WorldResult<()> {
        self.calls.push(format!("stop_effect({effect_id}, {item_id}, {forced})"));
        Ok(())
    }

    fn remove_target(&mut self, item_id: i64, target_id: i64) {
        self.calls.push(format!("remove_target({item_id}, {target_id})"));
    }
}

impl Logging for TestWorld {
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

impl Inventory for TestWorld {
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
