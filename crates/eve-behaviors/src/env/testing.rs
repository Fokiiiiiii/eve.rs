//! A world to test tasks against: a ballpark, a dogma engine and groups held in maps that a test fills in, dice that answer from a list, and a
//! record of what the tasks asked it to do.

use std::collections::{HashMap, HashSet};

use super::{
    Ball, Ballpark, CargoCapacity, DamageTotals, Dogma, EffectInfo, Entities, Inventory, InventoryItem,
    Random, ResponseThresholds, SlimItem, Universe, WorldError, WorldResult,
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
    pub thresholds: Option<ResponseThresholds>,
    pub fleet_members: HashMap<i64, Vec<i64>>,
    pub npc_fleet_costs: HashMap<i64, f64>,
    pub player_fleet_costs: HashMap<i64, f64>,
    pub additional_loot: HashMap<i64, Vec<i64>>,
    pub additional_capacity: HashMap<i64, f64>,
    pub damage: HashMap<i64, DamageTotals>,
    pub can_dock: HashSet<(i64, i64)>,
}

impl TestWorld {
    pub fn with_numbers(numbers: &[f64]) -> Self {
        Self { numbers: numbers.to_vec(), ..Self::default() }
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

impl Entities for TestWorld {
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
