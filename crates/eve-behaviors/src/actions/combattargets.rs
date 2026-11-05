//! `behaviors/actions/combattargets.py`: picking targets, locking them, letting them go.

use std::collections::BTreeSet;

use crate::constants::behaviorroles::{COMBAT_DISTRIBUTE_EFFECTS_BY_ROLE, COMBAT_IGNORE_PRIMARY_BY_ROLE};
use crate::constants::combat::{
    COMBAT_MAX_EFFECTIVE_DISTANCE_FACTOR, COMBAT_MAX_EFFECTIVE_FALLOFF_DISTANCE_FACTOR,
};
use crate::consts::*;
use crate::env::{Ball, WorldError};
use crate::evemath::weighted_choice;
use crate::events::message::TARGET_ADDED;
use crate::groups::mixin::{get_group_owner_id, get_member_id_list};
use crate::target_evaluation::TargetEvaluator;
use crate::tree::{BehaviorError, Task, TaskCtx};
use crate::utility::ballparks::{
    get_ball_type_id, get_distance_between, get_slim_item, get_specific_globals, is_ball_cloaked,
    is_ball_in_park, is_ball_in_range, is_invulnerable, is_target_valid,
};
use crate::utility::dogmatic::{
    get_default_effect_id_for_type, get_entity_max_missile_range, get_my_attribute_value,
    get_my_locked_targets, my_type_id, remove_my_target, try_stop_effect, type_has_effect,
};
use crate::utility::inventory::get_inventory_item;
use crate::utility::math::get_normalized_dict;
use crate::utility::standings::is_hostile_towards;
use crate::value::Value;

/// `ROLE_ADDRESS[1]`.
const ROLE_CHANNEL: &str = "ROLE";
/// `MY_COMBAT_TARGET[1]`.
const MY_COMBAT_TARGET_CHANNEL: &str = "MY_COMBAT_TARGET";

/// A filter `FindTarget` applies to a ball (`_GetFilterFunctions`).
enum Filter {
    /// `_GetFilterBubbleFunction`.
    Bubble { bubble_id: Value, only_check_local_bubble: bool },
    /// `_FilterTargetCategory`.
    Category,
    /// `_FilterCloaked`.
    Cloaked,
    /// `_FilterProtected`.
    Protected,
    /// `_FilterMaxRangeFromGuardObject`.
    MaxRangeFromGuardObject,
    /// `_get_module_range_filter_function`.
    ModuleRange { minimum_range: f64 },
}

/// Which of the classes built on `FindTarget` a task is.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum FindKind {
    #[default]
    FindTarget,
    /// `FindTargetForModule`.
    ForModule,
    /// `ResourceWarsFindTarget` (`actions/resourcewars.py`).
    ResourceWars,
    /// `FindTargetWithPolarizedPreference`.
    PolarizedPreference,
}

/// Picks the target to fight (`FindTarget`).
#[derive(Debug, Default)]
pub struct FindTarget {
    pub kind: FindKind,
    evaluator: Option<TargetEvaluator>,
}

impl FindTarget {
    pub fn new(kind: FindKind) -> Self {
        Self { kind, evaluator: None }
    }

    /// `SetTargetEvaluator`: the evaluator is made once.
    fn set_target_evaluator(&mut self, t: &mut TaskCtx<'_, '_>) -> bool {
        if self.evaluator.is_some() {
            return true;
        }
        let evaluator = match self.kind {
            FindKind::ResourceWars | FindKind::PolarizedPreference => Some(TargetEvaluator::threat(t)),
            FindKind::FindTarget | FindKind::ForModule => {
                let Some(name) = t.required("targetEvaluationFunction") else { return false };
                TargetEvaluator::create(t, &name)
            }
        };
        self.evaluator = evaluator;
        self.evaluator.is_some()
    }

    pub(crate) fn evaluate(&self, t: &mut TaskCtx<'_, '_>, target_id: i64) -> Option<f64> {
        let evaluator = self.evaluator.clone()?;
        evaluator.evaluate(t, target_id)
    }

    /// `_GetPotentialTargets`.
    fn potential_targets(&self, t: &mut TaskCtx<'_, '_>) -> Option<Value> {
        if self.kind == FindKind::ForModule && t.has_attribute("potentialTargetDictionaryAddress") {
            let by_entity = t.value_at("potentialTargetDictionaryAddress")?;
            if by_entity.truthy() {
                return Some(Value::List(
                    by_entity
                        .dict_entries()
                        .iter()
                        .map(|(_, v)| v.clone())
                        .filter(|v| !v.is_none())
                        .collect(),
                ));
            }
        }
        t.value_at("potentialTargetListAddress")
    }

    /// `_GetBubbleIdAndBubbleCheckRestriction`.
    fn bubble_and_restriction(t: &mut TaskCtx<'_, '_>) -> Option<(Value, bool)> {
        if !t.attribute("bubbleIdAddress").is_none() {
            let bubble_id = t.value_at("bubbleIdAddress")?;
            return Some((bubble_id, true));
        }
        let only_local = t.required("onlyCheckLocalBubble")?.truthy();
        let bubble_id = if only_local {
            Value::Int(crate::utility::ballparks::get_my_ball(t)?.new_bubble_id)
        } else {
            Value::None
        };
        Some((bubble_id, only_local))
    }

    /// `_GetFilterFunctions`.
    fn filters(&self, t: &mut TaskCtx<'_, '_>) -> Option<Vec<Filter>> {
        let (bubble_id, only_check_local_bubble) = Self::bubble_and_restriction(t)?;
        let bubble = Filter::Bubble { bubble_id, only_check_local_bubble };
        if self.kind == FindKind::ForModule {
            // `_get_module_range_filter_function`
            let module = t.value_at("moduleAddress")?;
            let module_item_id = t.expect_id(&module)?;
            let Some(item) = get_inventory_item(t, module_item_id) else {
                t.raise(BehaviorError::Task(
                    "AttributeError: 'NoneType' object has no attribute 'typeID'".to_string(),
                ));
                return None;
            };
            let Some(effect_id) = get_default_effect_id_for_type(t, item.type_id) else {
                t.raise(WorldError::Key(format!("no default effect for {}", item.type_id)).into());
                return None;
            };
            let minimum_range = t.world().effect_range_with_falloff_multiplier(
                effect_id,
                module_item_id,
                COMBAT_MAX_EFFECTIVE_FALLOFF_DISTANCE_FACTOR,
            );
            return Some(vec![
                bubble,
                Filter::Category,
                Filter::Cloaked,
                Filter::MaxRangeFromGuardObject,
                Filter::ModuleRange { minimum_range },
            ]);
        }
        Some(vec![
            bubble,
            Filter::Category,
            Filter::Cloaked,
            Filter::Protected,
            Filter::MaxRangeFromGuardObject,
        ])
    }

    /// `all(filterfunction(ballpark, ball) for filterfunction in self.filterFunctions)`.
    fn passes(t: &mut TaskCtx<'_, '_>, filters: &[Filter], ball: &Ball) -> bool {
        for filter in filters {
            let ok = match filter {
                Filter::Bubble { bubble_id, only_check_local_bubble } => {
                    !*only_check_local_bubble || Value::Int(ball.new_bubble_id).py_eq(bubble_id)
                }
                Filter::Category => {
                    let included = t.attribute("includedCategories");
                    if included.truthy() {
                        match t.world().slim_item(ball.id) {
                            None => false,
                            Some(slim) => included.contains(&Value::Int(slim.category_id)),
                        }
                    } else {
                        true
                    }
                }
                Filter::Cloaked => !ball.is_cloaked && !t.world().has_pending_cloak(ball.id),
                Filter::Protected => !t.world().is_ball_protected(ball.id),
                Filter::MaxRangeFromGuardObject => {
                    if !t.has_attribute("guardObjectIdAddress") {
                        true
                    } else {
                        let max_range = t.attribute("maxDistanceFromGuardObject");
                        if max_range.is_none() {
                            true
                        } else {
                            let Some(guard) = t.value_at("guardObjectIdAddress") else { return false };
                            let Some(guard_id) = t.expect_id(&guard) else { return false };
                            let distance = get_distance_between(t, ball.id, guard_id);
                            !distance.is_some_and(|d| Value::Float(d).py_ge(&max_range))
                        }
                    }
                }
                Filter::ModuleRange { minimum_range } => {
                    let Some(me) = t.my_item_id() else { return false };
                    is_ball_in_range(t, me, ball.id, *minimum_range)
                }
            };
            if !ok {
                return false;
            }
            if t.faulted() {
                return false;
            }
        }
        true
    }

    /// `_GetValidTargets`.
    fn valid_targets(&self, t: &mut TaskCtx<'_, '_>, filters: &[Filter], target_list: &Value) -> Vec<i64> {
        let mut valid = Vec::new();
        for target in target_list.items().unwrap_or(&[]).to_vec() {
            let Some(target_id) = target.as_int() else { continue };
            let Some(ball) = t.world().ball(target_id) else { continue };
            if Self::passes(t, filters, &ball) {
                valid.push(target_id);
            }
            if t.faulted() {
                return valid;
            }
        }
        valid
    }

    /// `FilterValidTargets`.
    fn filter_valid_targets(
        &self,
        t: &mut TaskCtx<'_, '_>,
        filters: &[Filter],
        target_list: &Value,
    ) -> Vec<i64> {
        if self.kind == FindKind::ForModule {
            if !target_list.truthy() {
                return Vec::new();
            }
            let valid = self.valid_targets(t, filters, target_list);
            let valid = self.filter_valid_targets_for_behavior_role(t, valid);
            return self.filter_valid_targets_for_module_distribution(t, valid);
        }
        if target_list.is_none() {
            return Vec::new();
        }
        let valid = self.valid_targets(t, filters, target_list);
        let valid = Self::filter_valid_targets_for_fleet_spread(t, valid);
        self.filter_valid_targets_for_behavior_role(t, valid)
    }

    /// `_filter_valid_targets_for_fleet_spread`.
    fn filter_valid_targets_for_fleet_spread(t: &mut TaskCtx<'_, '_>, valid_targets: Vec<i64>) -> Vec<i64> {
        let should_spread = t.has_attribute("shouldSpreadTargetsInFleet")
            && t.attribute("shouldSpreadTargetsInFleet") == Value::Bool(true)
            && t.has_attribute("fleetTargetsByGroupAddress");
        if !should_spread {
            return valid_targets;
        }
        let mut available = valid_targets.clone();
        let Some(by_group) = t.value_at("fleetTargetsByGroupAddress") else { return valid_targets };
        let mine = t.ctx().my_entity_group_id;
        for (group_id, fleet_target) in by_group.dict_entries() {
            if group_id.as_int() == mine {
                continue;
            }
            if let Some(target) = fleet_target.as_int()
                && let Some(at) = available.iter().position(|a| *a == target)
            {
                available.remove(at);
            }
        }
        if available.is_empty() { valid_targets } else { available }
    }

    /// `_FilterValidTargetsForBehaviorRole`.
    fn filter_valid_targets_for_behavior_role(
        &self,
        t: &mut TaskCtx<'_, '_>,
        valid_targets: Vec<i64>,
    ) -> Vec<i64> {
        if !t.attribute("roleAddress").truthy() {
            return valid_targets;
        }
        let Some(role) = t.value_at("roleAddress") else { return valid_targets };
        if !role.truthy() {
            return valid_targets;
        }
        let valid_targets = Self::filter_primary(t, &role, valid_targets);
        Self::filter_valid_targets_for_effect_distribution(t, &role, valid_targets)
    }

    /// `_FilterPrimary`.
    fn filter_primary(t: &mut TaskCtx<'_, '_>, role: &Value, mut valid_targets: Vec<i64>) -> Vec<i64> {
        if valid_targets.len() <= 1 {
            return valid_targets;
        }
        if role.as_str().is_some_and(|r| COMBAT_IGNORE_PRIMARY_BY_ROLE.contains(&r))
            && t.attribute("primaryTargetIdAddress").truthy()
        {
            let Some(primary) = t.value_at("primaryTargetIdAddress") else { return valid_targets };
            if let Some(primary) = primary.as_int().filter(|p| *p != 0)
                && let Some(at) = valid_targets.iter().position(|v| *v == primary)
            {
                valid_targets.remove(at);
            }
        }
        valid_targets
    }

    /// `_FilterValidTargetsForEffectDistribution`.
    fn filter_valid_targets_for_effect_distribution(
        t: &mut TaskCtx<'_, '_>,
        role: &Value,
        valid_targets: Vec<i64>,
    ) -> Vec<i64> {
        if !role.as_str().is_some_and(|r| COMBAT_DISTRIBUTE_EFFECTS_BY_ROLE.contains(&r)) {
            return valid_targets;
        }
        let already_effected = Self::all_targets_for_group_members_with_role(t, role);
        let available: Vec<i64> =
            valid_targets.iter().copied().filter(|v| !already_effected.contains(v)).collect();
        if available.is_empty() { valid_targets } else { available }
    }

    /// `_GetAllTargetsForGroupMembersWithRole`.
    fn all_targets_for_group_members_with_role(t: &mut TaskCtx<'_, '_>, role: &Value) -> BTreeSet<i64> {
        let mut already_effected = BTreeSet::new();
        let members = get_member_id_list(t);
        let me = t.ctx().my_item_id;
        for member_id in members {
            if Some(member_id) == me {
                continue;
            }
            // a member whose blackboard is gone has no target
            let member_role = t.channel_for_item(member_id, ROLE_CHANNEL);
            let Ok(member_role) = t.get_channel_last_value_unless_deleted(&member_role) else { continue };
            if member_role.py_eq(role) {
                let channel = t.channel_for_item(member_id, MY_COMBAT_TARGET_CHANNEL);
                let Ok(target) = t.get_channel_last_value_unless_deleted(&channel) else { continue };
                if target.truthy()
                    && let Some(id) = target.as_int()
                {
                    already_effected.insert(id);
                }
            }
        }
        already_effected
    }

    /// `_filter_valid_targets_for_module_distribution`.
    fn filter_valid_targets_for_module_distribution(
        &self,
        t: &mut TaskCtx<'_, '_>,
        valid_targets: Vec<i64>,
    ) -> Vec<i64> {
        let Some(already_affected) = Self::my_already_affected_targets(t, &valid_targets) else {
            return valid_targets;
        };
        if already_affected.is_empty() {
            return valid_targets;
        }
        let not_affected: Vec<i64> = valid_targets
            .iter()
            .copied()
            .filter(|v| !already_affected.iter().any(|(id, _)| id == v))
            .collect();
        if !not_affected.is_empty() {
            return not_affected;
        }
        // the least affected of them
        let mut least = already_affected[0];
        for entry in &already_affected {
            if entry.1 < least.1 {
                least = *entry;
            }
        }
        let choices = [least.0];
        let at = t.world().random_index(choices.len());
        vec![choices[at]]
    }

    /// `_get_my_already_affected_targets`: how many of my modules that do not stack already work on each target. `None` when the task could not be read.
    fn my_already_affected_targets(
        t: &mut TaskCtx<'_, '_>,
        valid_targets: &[i64],
    ) -> Option<Vec<(i64, i64)>> {
        let mut already: Vec<(i64, i64)> = Vec::new();
        let list_id = t.required("nonStackableEveTypeListId")?;
        if !list_id.truthy() {
            return Some(already);
        }
        let non_stackable = t.world().type_ids_in_list(list_id.as_int()?);
        if non_stackable.is_empty() {
            return Some(already);
        }
        let by_modules = t.value_at("targetsByModulesAddress")?;
        for (module_id, target_id) in by_modules.dict_entries() {
            let Some(target) = target_id.as_int() else { continue };
            if !valid_targets.contains(&target) {
                continue;
            }
            let module = module_id.as_int()?;
            let Some(item) = get_inventory_item(t, module) else {
                t.raise(BehaviorError::Task(
                    "AttributeError: 'NoneType' object has no attribute 'typeID'".to_string(),
                ));
                return None;
            };
            if non_stackable.contains(&item.type_id) {
                match already.iter_mut().find(|(id, _)| *id == target) {
                    Some((_, count)) => *count += 1,
                    None => already.push((target, 1)),
                }
            }
        }
        Some(already)
    }

    /// `ShouldPickNewTarget`.
    fn should_pick_new_target(selected: &Value, target_list: &[i64]) -> bool {
        if target_list.is_empty() {
            false
        } else {
            selected.is_none() || !selected.as_int().is_some_and(|s| target_list.contains(&s))
        }
    }

    /// `PickTarget`.
    fn pick_target(&self, t: &mut TaskCtx<'_, '_>, target_list: &[i64]) -> Option<i64> {
        match self.kind {
            FindKind::FindTarget | FindKind::ForModule => {
                // `sorted(targetList, key=evaluateTarget)[-1]`: the last of the best
                let mut best: Option<(i64, f64)> = None;
                for &target in target_list {
                    let weight = self.evaluate(t, target)?;
                    if best.is_none_or(|(_, w)| weight >= w) {
                        best = Some((target, weight));
                    }
                }
                best.map(|(id, _)| id)
            }
            FindKind::ResourceWars => self.pick_target_resource_wars(t, target_list),
            FindKind::PolarizedPreference => self.pick_target_polarized(t, target_list),
        }
    }

    fn pick_target_resource_wars(&self, t: &mut TaskCtx<'_, '_>, target_list: &[i64]) -> Option<i64> {
        crate::actions::resourcewars::pick_target(self, t, target_list)
    }

    /// `FindTargetWithPolarizedPreference.PickTarget`.
    fn pick_target_polarized(&self, t: &mut TaskCtx<'_, '_>, target_list: &[i64]) -> Option<i64> {
        let mut weights: Vec<(i64, f64)> = Vec::new();
        for &item_id in target_list {
            let weight = self.evaluate(t, item_id)?;
            weights.push((item_id, weight));
        }
        let enabled = t.value_at("isPreferenceEnabledAddress")?;
        if enabled.truthy() {
            weights =
                get_normalized_dict(&weights.iter().copied().collect::<std::collections::BTreeMap<_, _>>())
                    .map(|m| weights.iter().map(|(id, _)| (*id, m[id])).collect())?;
            let bias = t.value_at("preferredTypeBiasAmountAddress")?;
            let Some(bias) = bias.as_float() else {
                t.raise(BehaviorError::Task("TypeError: the bias amount is not a number".to_string()));
                return None;
            };
            let preferring = self.is_preferring_type(t)?;
            let bias = if preferring { bias } else { -bias };
            let list_id = t.required_int("preferredTypeListID")?;
            let preferred = t.world().type_ids_in_list(list_id);
            for (item_id, weight) in weights.iter_mut() {
                if get_ball_type_id(t, *item_id).is_some_and(|ty| preferred.contains(&ty)) {
                    *weight += bias;
                }
            }
        }
        sorted_last(&weights)
    }

    /// `_IsPreferringType`.
    fn is_preferring_type(&self, t: &mut TaskCtx<'_, '_>) -> Option<bool> {
        let preferring = t.value_at("isPreferringTypeAddress")?;
        if !preferring.is_none() {
            return Some(preferring.truthy());
        }
        let chance = t.value_at("chanceOfPreferringTypesAddress")?;
        let dice = t.world().random() * 100.0;
        let Some(chance) = chance.as_float() else {
            t.raise(BehaviorError::Task("TypeError: the chance is not a number".to_string()));
            return None;
        };
        let preferring = chance >= dice;
        let address = t.required_address("isPreferringTypeAddress")?;
        t.send_blackboard_value(&address, Value::Bool(preferring));
        Some(preferring)
    }

    /// `_update_picked_target`.
    fn update_picked_target(t: &mut TaskCtx<'_, '_>, picked: i64) {
        let Some(selected) = t.required_address("selectedTargetAddress") else { return };
        t.send_blackboard_value(&selected, Value::Int(picked));
        if t.has_attribute("fleetTargetsByGroupAddress") {
            let Some(address) = t.required_address("fleetTargetsByGroupAddress") else { return };
            let last = t.get_last_blackboard_value(&address);
            let stored = last.truthy();
            let mut by_group = if stored { last } else { Value::Dict(Vec::new()) };
            if let Some(group) = t.ctx().my_entity_group_id {
                by_group.dict_insert(Value::Int(group), Value::Int(picked));
            }
            if stored {
                t.with_channel(&address, |channel| {
                    if let Some(value) = channel.last_value_mut() {
                        *value = by_group.clone();
                    }
                });
            }
            t.send_blackboard_value(&address, by_group);
        }
    }
}

/// `sorted(weight_by_item_id.keys(), key=lambda i: weight_by_item_id[i])[-1]`: the last of the heaviest.
pub(crate) fn sorted_last(weights: &[(i64, f64)]) -> Option<i64> {
    let mut best: Option<(i64, f64)> = None;
    for &(id, weight) in weights {
        if best.is_none_or(|(_, w)| weight >= w) {
            best = Some((id, weight));
        }
    }
    best.map(|(id, _)| id)
}

impl Task for FindTarget {
    fn type_name(&self) -> &'static str {
        match self.kind {
            FindKind::FindTarget => "FindTarget",
            FindKind::ForModule => "FindTargetForModule",
            FindKind::ResourceWars => "ResourceWarsFindTarget",
            FindKind::PolarizedPreference => "FindTargetWithPolarizedPreference",
        }
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        if !self.set_target_evaluator(t) {
            return;
        }
        let Some(target_list) = self.potential_targets(t) else { return };
        let Some(selected) = t.value_at("selectedTargetAddress") else { return };
        let Some(filters) = self.filters(t) else { return };
        let target_list = self.filter_valid_targets(t, &filters, &target_list);
        if t.faulted() {
            return;
        }
        if Self::should_pick_new_target(&selected, &target_list) {
            let Some(picked) = self.pick_target(t, &target_list) else { return };
            Self::update_picked_target(t, picked);
            let Some(address) = t.required_address("selectedTargetAddress") else { return };
            t.send_blackboard_value(&address, Value::Int(picked));
            // the client's listing goes on to `SetStatusToFailed()` here; the trees that use this task need it to succeed, and an early return was
            // lost in the decompiled listing
            t.set_status_to_success();
            return;
        } else if !selected.is_none() {
            let Some(address) = t.required_address("selectedTargetAddress") else { return };
            t.send_blackboard_value(&address, Value::None);
        }
        t.set_status_to_failed();
    }
}

const HANDLER_TARGET_ADDED: u16 = 0;
const HANDLER_TRY_TARGET_LOCK: u16 = 1;

/// Tries to lock the selected target (`AcquireTargetLock`; `AcquireTargetLockNonBlocking` does not wait for the lock).
#[derive(Debug, Default)]
pub struct AcquireTargetLock {
    pub non_blocking: bool,
    targeting_thread: bool,
}

impl AcquireTargetLock {
    pub fn non_blocking() -> Self {
        Self { non_blocking: true, targeting_thread: false }
    }

    /// `_IsTargetValid`.
    fn is_target_valid(t: &mut TaskCtx<'_, '_>, selected: &Value) -> bool {
        let Some(target) = selected.as_int() else { return false };
        if !is_ball_in_park(t, target) {
            return false;
        }
        !is_ball_cloaked(t, target)
    }

    fn set_status_while_waiting_for_lock(&self, t: &mut TaskCtx<'_, '_>) {
        if self.non_blocking {
            t.set_status_to_success();
            return;
        }
        if let Some(me) = t.my_item_id() {
            t.subscribe_item(me, TARGET_ADDED, HANDLER_TARGET_ADDED);
        }
        t.set_status_to_suspended();
    }

    fn set_status_while_lock_pending(&self, t: &mut TaskCtx<'_, '_>) {
        if self.non_blocking {
            t.set_status_to_success();
            return;
        }
        if let Some(me) = t.my_item_id() {
            t.subscribe_item(me, TARGET_ADDED, HANDLER_TARGET_ADDED);
        }
        t.set_status_to_running();
    }

    /// `_AttemptToAcquireTargetLock`.
    fn attempt_to_acquire_target_lock(&mut self, t: &mut TaskCtx<'_, '_>) {
        let Some(selected) = t.value_at("selectedTargetAddress") else { return };
        let Some(me) = t.my_item_id() else { return };
        if !Self::is_target_valid(t, &selected) {
            t.set_status_to_failed();
        } else if is_invulnerable(t, me) {
            self.set_status_while_lock_pending(t);
        } else if t.world().is_targeting(me, selected.as_int()) {
            t.set_status_to_success();
        } else if t.world().has_pending_target(me, selected.as_int()) {
            if !self.targeting_thread {
                self.set_status_while_lock_pending(t);
            } else {
                self.set_status_while_waiting_for_lock(t);
            }
        } else {
            self.set_status_while_waiting_for_lock(t);
            self.targeting_thread = true;
            t.schedule_call(0.0, HANDLER_TRY_TARGET_LOCK, vec![selected]);
        }
    }
}

impl Task for AcquireTargetLock {
    fn type_name(&self) -> &'static str {
        if self.non_blocking { "AcquireTargetLockNonBlocking" } else { "AcquireTargetLock" }
    }

    fn on_enter(&mut self, _t: &mut TaskCtx<'_, '_>) {}

    fn update(&mut self, t: &mut TaskCtx<'_, '_>) {
        self.attempt_to_acquire_target_lock(t);
    }

    fn on_event(&mut self, t: &mut TaskCtx<'_, '_>, _handler: u16, _args: &[Value]) {
        t.set_status_to_success();
        t.on_status_updated();
    }

    fn on_call(&mut self, t: &mut TaskCtx<'_, '_>, handler: u16, args: &[Value]) {
        if handler != HANDLER_TRY_TARGET_LOCK {
            return;
        }
        // `_TryTargetLock(selectedTarget)`
        if let (Some(me), Some(target)) = (t.my_item_id(), args.first().and_then(Value::as_int)) {
            match t.world().add_target_ex(me, target) {
                Ok(()) | Err(WorldError::User(_) | WorldError::Runtime(_)) => {}
                Err(other) => t.raise(other.into()),
            }
        }
        if t.is_suspended() {
            t.set_status_to_running();
            t.on_status_updated();
        }
        self.targeting_thread = false;
    }

    fn clean_up(&mut self, t: &mut TaskCtx<'_, '_>) {
        if !t.is_invalid()
            && let Some(me) = t.my_item_id()
        {
            t.unsubscribe_item(me, TARGET_ADDED, HANDLER_TARGET_ADDED);
        }
        t.set_status_to_invalid();
    }
}

enter_task!(
    /// Lets go of the first target I have locked.
    UnlockTarget,
    |t| {
        let Some(me) = t.my_item_id() else { return };
        let targets = t.world().targets_ex(me);
        if let Some(first) = targets.first() {
            t.world().remove_target(me, *first);
        }
        t.set_status_to_success();
    }
);

/// `UnlockModuleTarget._get_target_id`: the target of my module, when no other module has it as well.
fn module_target_id(t: &mut TaskCtx<'_, '_>) -> Option<Option<i64>> {
    let module = t.value_at("moduleAddress")?;
    let by_modules = t.value_at("targetsByModulesAddress")?;
    if by_modules.is_none() {
        return Some(None);
    }
    let Some(target) = by_modules.dict_get(&module).cloned() else { return Some(None) };
    if target.is_none() {
        return Some(None);
    }
    for (other_module, other_target) in by_modules.dict_entries() {
        if other_module.py_eq(&module) {
            continue;
        }
        if other_target.py_eq(&target) {
            return Some(None);
        }
    }
    Some(target.as_int())
}

enter_task!(
    /// Lets go of the target my module works on, when it is a target of no other module.
    UnlockModuleTarget,
    |t| {
        let Some(me) = t.my_item_id() else { return };
        let targets = t.world().targets_ex(me);
        let Some(target) = module_target_id(t) else { return };
        if let Some(target_id) = target.filter(|id| *id != 0)
            && targets.contains(&target_id)
        {
            t.world().remove_target(me, target_id);
        }
        t.set_status_to_success();
    }
);

enter_task!(
    /// Takes the targets that are gone from the park (and are not real items) out of a target set.
    PruneNpcsFromTargetSet,
    |t| {
        t.set_status_to_success();
        let Some(target_set) = t.value_at("targetSetAddress") else { return };
        if !target_set.truthy() {
            return;
        }
        let mut removed = Vec::new();
        for target in target_set.items().unwrap_or(&[]) {
            if let Some(id) = target.as_int()
                && is_fake_item(id)
                && !t.world().has_ball(id)
            {
                removed.push(target.clone());
            }
        }
        if !removed.is_empty() {
            let Some(address) = t.required_address("targetSetAddress") else { return };
            let mut set = target_set.clone();
            for id in &removed {
                set.set_discard(id);
            }
            t.with_channel(&address, |channel| {
                if let Some(last) = channel.last_value_mut() {
                    *last = set.clone();
                }
            });
            t.send_blackboard_value(&address, set);
        }
    }
);

const HANDLER_LOG_KILLED_AND_FLED: u16 = 0;

/// Empties a target set and logs the players that were killed or fled (`ClearTargetSet`).
#[derive(Debug, Default)]
pub struct ClearTargetSet;

impl Task for ClearTargetSet {
    fn type_name(&self) -> &'static str {
        "ClearTargetSet"
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        t.set_status_to_success();
        let Some(target_set) = t.value_at("targetSetAddress") else { return };
        if !target_set.truthy() {
            return;
        }
        let Some(address) = t.required_address("targetSetAddress") else { return };
        t.send_blackboard_value(&address, Value::set(std::iter::empty()));
        let members = get_member_id_list(t);
        // the client logs this in a tasklet
        t.schedule_call(
            0.0,
            HANDLER_LOG_KILLED_AND_FLED,
            vec![target_set, Value::List(members.into_iter().map(Value::Int).collect())],
        );
    }

    fn on_call(&mut self, t: &mut TaskCtx<'_, '_>, _handler: u16, args: &[Value]) {
        let owner = get_group_owner_id(t);
        if t.faulted() {
            return;
        }
        let behavior_id = t.behavior_id();
        let (Some(target_set), Some(members)) = (args.first(), args.get(1)) else { return };
        t.world().log_killed_and_fled_players(owner, &behavior_id, target_set, &members.int_items());
    }
}

enter_task!(
    /// The ships that are flown, of a set of items.
    SelectPilotedShips,
    |t| {
        t.set_status_to_success();
        let Some(source) = t.value_at("sourceSetAddress") else { return };
        if !source.truthy() {
            return;
        }
        let mut piloted = Vec::new();
        for target in source.items().unwrap_or(&[]).to_vec() {
            let Some(id) = target.as_int() else { continue };
            if !is_ball_in_park(t, id) {
                continue;
            }
            let Some(slim) = get_slim_item(t, id) else { continue };
            if slim.category_id != CATEGORY_SHIP || !t.world().is_ship_piloted(id) {
                continue;
            }
            piloted.push(target);
        }
        let Some(address) = t.required_address("targetSetAddress") else { return };
        t.send_blackboard_value(&address, Value::set(piloted));
    }
);

/// `GetMyMinOptimalAndFalloff`.
mod min_range {
    use super::*;

    /// `_get_effect_optimal_and_falloff`.
    fn effect_optimal_and_falloff(t: &mut TaskCtx<'_, '_>, effect_id: i64) -> Option<(f64, f64)> {
        let me = t.my_item_id()?;
        let (optimal, falloff) = t.world().effect_range_and_falloff_for_item(me, effect_id);
        Some((optimal.filter(|v| *v != 0.0).unwrap_or(0.0), falloff.filter(|v| *v != 0.0).unwrap_or(0.0)))
    }

    /// `_get_my_min_effect_range`.
    pub fn my_min_effect_range(t: &mut TaskCtx<'_, '_>) -> Option<(f64, f64)> {
        let mut minimum_optimal = 0.0;
        let mut minimum_falloff = 0.0;
        let effects = t.value_at("combatEffectsAddress")?;
        let effects = if effects.truthy() { effects.int_items() } else { Vec::new() };
        let type_id = my_type_id(t)?;
        for effect_id in effects {
            if type_has_effect(t, type_id, effect_id) {
                let (optimal, falloff) = effect_optimal_and_falloff(t, effect_id)?;
                if optimal == 0.0 {
                    // the client logs that the effect is badly authored
                    t.world().log_error(
                        "Badly authored effect for behavior NPC range picks - no range assigned to it",
                    );
                    continue;
                }
                if optimal < minimum_optimal || minimum_optimal == 0.0 {
                    minimum_optimal = optimal;
                    minimum_falloff = falloff;
                }
            }
        }
        Some((minimum_optimal, minimum_falloff))
    }

    /// `_get_turret_range`.
    fn turret_range(t: &mut TaskCtx<'_, '_>) -> Option<(f64, f64)> {
        let optimal = get_my_attribute_value(t, ATTRIBUTE_MAX_RANGE)?;
        let mut falloff = get_my_attribute_value(t, ATTRIBUTE_FALLOFF)?;
        if falloff == 1.0 {
            falloff = 0.0;
        }
        Some((optimal, falloff))
    }

    /// `_get_my_min_weapon_range`.
    pub fn my_min_weapon_range(t: &mut TaskCtx<'_, '_>) -> Option<(f64, f64)> {
        let (turret_optimal, turret_falloff) = turret_range(t)?;
        let (mut weapon_optimal, mut weapon_falloff) = (turret_optimal, turret_falloff);
        let type_id = my_type_id(t)?;
        if type_has_effect(t, type_id, EFFECT_MISSILE_LAUNCHING_FOR_ENTITY) {
            let missile_range = get_entity_max_missile_range(t, type_id);
            if turret_optimal == 0.0 || missile_range < turret_optimal {
                weapon_optimal = missile_range;
                weapon_falloff = 0.0;
            }
        }
        Some((
            if weapon_optimal != 0.0 { weapon_optimal } else { 0.0 },
            if weapon_falloff != 0.0 { weapon_falloff } else { 0.0 },
        ))
    }
}

enter_task!(
    /// The shortest optimal range and falloff of my effects and weapons, put on channels.
    GetMyMinOptimalAndFalloff,
    |t| {
        t.set_status_to_success();
        let Some((effect_optimal, effect_falloff)) = min_range::my_min_effect_range(t) else { return };
        let Some((weapon_optimal, weapon_falloff)) = min_range::my_min_weapon_range(t) else { return };
        let (min_optimal, min_falloff) =
            if weapon_optimal == 0.0 || (effect_optimal != 0.0 && effect_optimal < weapon_optimal) { (effect_optimal, effect_falloff) } else { (weapon_optimal, weapon_falloff) };
        let (Some(optimal_address), Some(falloff_address)) = (t.required_address("minOptimalAddress"), t.required_address("minFalloffAddress")) else { return };
        t.send_blackboard_value(&optimal_address, Value::Float(min_optimal));
        t.send_blackboard_value(&falloff_address, Value::Float(min_falloff));
    }
);

enter_task!(
    /// The farthest I am effective, from the optimal range and the falloff, put on a channel.
    GetMaxEffectiveRange,
    |t| {
        t.set_status_to_success();
        let Some(optimal) = t.value_at("minOptimalAddress") else { return };
        let Some(falloff) = t.value_at("minFalloffAddress") else { return };
        let falloff = if falloff.truthy() { falloff } else { Value::Int(0) };
        let (Some(optimal_range), Some(falloff_range)) = (optimal.as_float(), falloff.as_float()) else {
            t.raise(BehaviorError::Task("TypeError: unsupported operand type(s) for the effective range".to_string()));
            return;
        };
        let value = if falloff_range != 0.0 {
            (optimal_range + falloff_range * COMBAT_MAX_EFFECTIVE_FALLOFF_DISTANCE_FACTOR) * COMBAT_MAX_EFFECTIVE_DISTANCE_FACTOR
        } else {
            optimal_range
        };
        let Some(address) = t.required_address("maxEffectiveRangeAddress") else { return };
        t.send_blackboard_value(&address, Value::Float(value));
    }
);

enter_task!(
    /// The ships in reach of each kind of entity of my group, by the range of the kind, around the commander.
    GenerateTargetSetsByCombatRangesInGroup,
    |t| {
        t.set_status_to_success();
        let Some(range_by_entity_type) = t.value_at("rangeByEntityTypeAddress") else { return };
        let mut balls_in_range: Vec<(f64, i64)> = Vec::new();
        if !range_by_entity_type.is_none() {
            let Some(commander) = t.value_at("commanderAddress") else { return };
            let commander_id = commander.as_int();
            if commander_id.is_none_or(|c| !is_ball_in_park(t, c)) {
                t.world().log_error("behavior task is missing its commander");
            } else if let Some(commander_id) = commander_id {
                let max_range = range_by_entity_type.dict_entries().iter().filter_map(|(_, r)| r.as_float()).fold(f64::NEG_INFINITY, f64::max);
                balls_in_range = t.world().ball_ids_and_dist_in_range(commander_id, max_range);
            }
        }
        let threat_set = if t.has_attribute("threatTargetSetAddres") {
            let Some(set) = t.value_at("threatTargetSetAddress") else { return };
            Some(set)
        } else {
            None
        };
        let mut targets_by_type: Value = Value::Dict(Vec::new());
        for (distance_squared, ball_id) in balls_in_range {
            let list = threat_set.as_ref().map(|s| s.int_items());
            if !is_target_valid(t, Some(ball_id), list.as_deref(), &[], &[], &[CATEGORY_SHIP]) {
                continue;
            }
            for (entity_type, max_range) in range_by_entity_type.dict_entries() {
                let Some(max_range) = max_range.as_float() else { continue };
                if max_range * max_range > distance_squared {
                    let mut set = targets_by_type.dict_get(entity_type).cloned().unwrap_or_else(|| Value::set(std::iter::empty()));
                    set.set_add(Value::Int(ball_id));
                    targets_by_type.dict_insert(entity_type.clone(), set);
                }
            }
        }
        let Some(address) = t.required_address("targetsByEntityTypeAddress") else { return };
        t.send_blackboard_value(&address, targets_by_type);
    }
);

/// `GetPrimaryAndNonPrimaryTargetsByEntityTypes`.
#[derive(Debug, Default)]
pub struct GetPrimaryAndNonPrimaryTargetsByEntityTypes;

impl GetPrimaryAndNonPrimaryTargetsByEntityTypes {
    /// `_filter_targets_set_by_type_list`.
    fn filter_targets_set_by_type_list(
        t: &mut TaskCtx<'_, '_>,
        type_list_id: &Value,
        targets: &[Value],
    ) -> Option<Vec<Value>> {
        let Some(list_id) = type_list_id.as_int() else { return Some(targets.to_vec()) };
        let types_of_interest = t.world().type_ids_in_list(list_id);
        let mut filtered = Vec::new();
        for target in targets {
            let id = target.as_int()?;
            let Some(slim) = get_slim_item(t, id) else {
                t.raise(BehaviorError::Task(
                    "AttributeError: 'NoneType' object has no attribute 'typeID'".to_string(),
                ));
                return None;
            };
            if types_of_interest.contains(&slim.type_id) {
                filtered.push(target.clone());
            }
        }
        Some(filtered)
    }
}

impl Task for GetPrimaryAndNonPrimaryTargetsByEntityTypes {
    fn type_name(&self) -> &'static str {
        "GetPrimaryAndNonPrimaryTargetsByEntityTypes"
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        t.set_status_to_success();
        let Some(all_targets) = t.value_at("targetsAddress") else { return };
        let (primary_targets, non_primary_targets) = if !all_targets.truthy() {
            (Value::None, Value::None)
        } else {
            let Some(primary_name) = t.required("primaryTargetEvaluationFunction") else { return };
            let Some(primary_evaluator) = TargetEvaluator::create(t, &primary_name) else { return };
            let Some(non_primary_name) = t.required("nonPrimaryTargetEvaluationFunction") else { return };
            let Some(non_primary_evaluator) = TargetEvaluator::create(t, &non_primary_name) else { return };
            let mut primary_targets = Value::Dict(Vec::new());
            let mut non_primary_targets = Value::Dict(Vec::new());
            for (entity_type, targets_set) in all_targets.dict_entries().to_vec() {
                let targets: Vec<Value> = targets_set.items().unwrap_or(&[]).to_vec();
                // `_get_primary_target`
                let primary_list_id = t.attribute("primaryTypeListId");
                let Some(filtered) = Self::filter_targets_set_by_type_list(t, &primary_list_id, &targets)
                else {
                    return;
                };
                let primary = if filtered.is_empty() {
                    Value::None
                } else {
                    let mut best: Option<(Value, f64)> = None;
                    for target in &filtered {
                        let Some(id) = target.as_int() else { return };
                        let Some(weight) = primary_evaluator.evaluate(t, id) else { return };
                        if best.as_ref().is_none_or(|(_, w)| weight >= *w) {
                            best = Some((target.clone(), weight));
                        }
                    }
                    best.map_or(Value::None, |(v, _)| v)
                };
                primary_targets.dict_insert(entity_type.clone(), primary.clone());
                // `_get_non_primary_targets`
                let non_primary_list_id = t.attribute("nonPrimaryTypeListId");
                let Some(mut filtered) =
                    Self::filter_targets_set_by_type_list(t, &non_primary_list_id, &targets)
                else {
                    return;
                };
                if primary.truthy()
                    && let Some(at) = filtered.iter().position(|v| v.py_eq(&primary))
                {
                    filtered.remove(at);
                }
                let mut weighted = Vec::new();
                for target in &filtered {
                    let Some(id) = target.as_int() else { return };
                    let Some(weight) = non_primary_evaluator.evaluate(t, id) else { return };
                    weighted.push(Value::tuple([target.clone(), Value::Float(weight)]));
                }
                non_primary_targets.dict_insert(entity_type, Value::set(weighted));
            }
            (primary_targets, non_primary_targets)
        };
        let (Some(primary_address), Some(non_primary_address)) = (
            t.required_address("primaryTargetsByEntityTypesAddress"),
            t.required_address("nonPrimaryTargetsByEntityTypesAddress"),
        ) else {
            return;
        };
        t.send_blackboard_value(&primary_address, primary_targets);
        t.send_blackboard_value(&non_primary_address, non_primary_targets);
    }
}

enter_task!(
    /// A target chosen by weight from a set of `(target, weight)` pairs.
    GetWeightedTargetFromTargetsSet,
    |t| {
        let Some(weight_set) = t.value_at("targetWeightSetAddress") else { return };
        let choices: Vec<(i64, f64)> = if weight_set.truthy() {
            weight_set
                .items()
                .unwrap_or(&[])
                .iter()
                .filter_map(|entry| {
                    let [target, weight] = entry.items()? else { return None };
                    Some((target.as_int()?, weight.as_float()?))
                })
                .collect()
        } else {
            Vec::new()
        };
        let target = if choices.is_empty() { None } else { weighted_choice(&choices, t.world()) };
        match target {
            None => t.set_status_to_failed(),
            Some(target_id) => {
                t.set_status_to_success();
                let Some(address) = t.required_address("targetIdAddress") else { return };
                t.send_blackboard_value(&address, Value::Int(target_id));
            }
        }
    }
);

enter_task!(
    /// Lets go of every locked target that is not my target, and stops the combat effects on it.
    DisengageObsoleteTargets,
    |t| {
        t.set_status_to_success();
        let Some(my_target) = t.value_at("myTargetAddress") else { return };
        let locked = get_my_locked_targets(t);
        let mut effects = vec![EFFECT_TARGET_ATTACK, EFFECT_MISSILE_LAUNCHING_FOR_ENTITY];
        let Some(from_address) = t.value_at("myCombatEffectsAddress") else { return };
        if from_address.truthy() {
            effects.extend(from_address.int_items());
        }
        for target_id in locked {
            if !Value::Int(target_id).py_eq(&my_target) {
                for effect_id in &effects {
                    try_stop_effect(t, *effect_id, false);
                }
                remove_my_target(t, target_id);
            }
        }
    }
);

enter_task!(
    /// The hostile structures in range are put on the targets.
    FindHostileStructureInRange,
    |t| {
        t.set_status_to_failed();
        let mut hostile = Vec::new();
        for (global_ball_id, _ball) in get_specific_globals(t, None, None, Some(CATEGORY_STRUCTURE)) {
            let vulnerable = t.world().is_structure_vulnerable(global_ball_id);
            let Some(vulnerable) = t.ok(vulnerable) else { return };
            if !vulnerable {
                continue;
            }
            let owner = t.world().slim_item(global_ball_id).and_then(|s| s.owner_id);
            let Some(owner) = owner else {
                t.raise(BehaviorError::Task("AttributeError: the ball has no owner".to_string()));
                return;
            };
            if is_hostile_towards(t, owner) != Some(true) {
                continue;
            }
            let Some(me) = t.my_item_id() else { return };
            let Some(min_distance) = t.required_float("minDistance") else { return };
            if is_ball_in_range(t, me, global_ball_id, min_distance) {
                hostile.push(Value::Int(global_ball_id));
            }
            if t.faulted() {
                return;
            }
        }
        if !hostile.is_empty() {
            let Some(current) = t.value_at("targetsAddress") else { return };
            let mut union = if current.truthy() { current } else { Value::set(std::iter::empty()) };
            for id in hostile {
                union.set_add(id);
            }
            let Some(address) = t.required_address("targetsAddress") else { return };
            t.send_blackboard_value(&address, union);
            t.set_status_to_success();
        }
    }
);

#[cfg(test)]
mod tests {
    use super::*;
    use crate::blackboards::{Address, ScopeType};
    use crate::env::testing::TestGroup;
    use crate::env::{EffectInfo, ResponseThresholds, SlimItem, TestWorld};
    use crate::tree::tests::Rig;
    use crate::tree::{Attrs, Status};

    fn item(name: &str) -> Value {
        Address::new(ScopeType::Item, name).to_value()
    }

    fn ids(list: &[i64]) -> Value {
        Value::set(list.iter().map(|i| Value::Int(*i)))
    }

    fn find_attrs() -> Attrs {
        Attrs::new()
            .with("selectedTargetAddress", item("Selected"))
            .with("potentialTargetListAddress", item("Potential"))
            .with("includedCategories", Value::List(vec![]))
            .with("onlyCheckLocalBubble", true)
            .with("targetEvaluationFunction", "size")
    }

    fn rig_with_targets() -> Rig {
        let mut rig = Rig::new();
        rig.world.add_ball(1, [0.0; 3], 2).new_bubble_id = 3;
        for (id, radius) in [(20, 100.0), (21, 300.0), (22, 200.0)] {
            rig.world.add_ball(id, [0.0; 3], 600 + id).new_bubble_id = 3;
            rig.world.type_attributes.insert((600 + id, ATTRIBUTE_SIGNATURE_RADIUS), radius);
        }
        rig.post(ScopeType::Item, "Potential", ids(&[20, 21, 22, 23]));
        rig
    }

    #[test]
    fn find_target_picks_the_best_of_the_valid_targets_and_says_so() {
        let mut rig = rig_with_targets();
        assert_eq!(rig.run(Box::new(FindTarget::default()), find_attrs()), Status::Success);
        assert_eq!(rig.last_error, None);
        assert_eq!(
            rig.last(ScopeType::Item, "Selected"),
            Value::Int(21),
            "the largest signature radius; 23 is no ball"
        );
    }

    #[test]
    fn find_target_fails_and_clears_the_selected_target_when_nothing_new_is_to_be_picked() {
        let mut rig = rig_with_targets();
        rig.post(ScopeType::Item, "Selected", Value::Int(20));
        assert_eq!(rig.run(Box::new(FindTarget::default()), find_attrs()), Status::Failure);
        assert_eq!(
            rig.last(ScopeType::Item, "Selected"),
            Value::None,
            "the client takes a target that is still valid off too: nothing new is picked and it is not None"
        );
        let mut rig = rig_with_targets();
        rig.post(ScopeType::Item, "Selected", Value::Int(99));
        rig.world.cloaked.insert(20);
        rig.world.balls.get_mut(&20).unwrap().is_cloaked = true;
        rig.world.balls.get_mut(&21).unwrap().new_bubble_id = 9;
        rig.world.protected.insert(22);
        assert_eq!(
            rig.run(Box::new(FindTarget::default()), find_attrs()),
            Status::Failure,
            "a cloaked one, one in another bubble and a protected one"
        );
        assert_eq!(rig.last(ScopeType::Item, "Selected"), Value::None);
    }

    #[test]
    fn find_target_picks_a_new_target_when_the_selected_one_is_no_longer_among_the_valid_ones() {
        let mut rig = rig_with_targets();
        rig.post(ScopeType::Item, "Selected", Value::Int(99));
        assert_eq!(rig.run(Box::new(FindTarget::default()), find_attrs()), Status::Success);
        assert_eq!(rig.last(ScopeType::Item, "Selected"), Value::Int(21));
    }

    #[test]
    fn a_role_that_ignores_the_primary_target_keeps_it_when_it_is_the_only_valid_target() {
        let mut rig = rig_with_targets();
        rig.world.balls.get_mut(&20).unwrap().new_bubble_id = 9;
        rig.world.balls.get_mut(&22).unwrap().new_bubble_id = 9;
        rig.ctx.my_entity_group_id = Some(10);
        rig.world.groups.insert(10, TestGroup { members: vec![1], ..Default::default() });
        rig.post(ScopeType::Item, "Role", Value::Str("anti-dps".into()));
        rig.post(ScopeType::Item, "Primary", Value::Int(21));
        let attrs =
            find_attrs().with("roleAddress", item("Role")).with("primaryTargetIdAddress", item("Primary"));
        assert_eq!(rig.run(Box::new(FindTarget::default()), attrs), Status::Success);
        assert_eq!(rig.last(ScopeType::Item, "Selected"), Value::Int(21));
    }

    #[test]
    fn find_target_filters_by_category_and_by_distance_from_the_guard_object() {
        let mut rig = rig_with_targets();
        rig.world.slims.get_mut(&20).unwrap().category_id = 6;
        rig.world.slims.get_mut(&21).unwrap().category_id = 6;
        rig.world.slims.get_mut(&22).unwrap().category_id = 7;
        rig.world.add_ball(50, [0.0; 3], 1);
        rig.world.set_dist(21, 50, 5000.0);
        rig.world.set_dist(20, 50, 100.0);
        rig.post(ScopeType::Item, "Guard", Value::Int(50));
        let attrs = find_attrs()
            .with("includedCategories", Value::List(vec![Value::Int(6)]))
            .with("guardObjectIdAddress", item("Guard"))
            .with("maxDistanceFromGuardObject", 1000.0);
        assert_eq!(rig.run(Box::new(FindTarget::default()), attrs), Status::Success);
        assert_eq!(rig.last(ScopeType::Item, "Selected"), Value::Int(20));
    }

    #[test]
    fn a_role_that_ignores_the_primary_target_leaves_it_out_and_one_that_spreads_effects_leaves_out_what_the_others_have()
     {
        let mut rig = rig_with_targets();
        rig.ctx.my_entity_group_id = Some(10);
        rig.world.groups.insert(10, TestGroup { members: vec![1], ..Default::default() });
        rig.post(ScopeType::Item, "Role", Value::Str("anti-dps".into()));
        rig.post(ScopeType::Item, "Primary", Value::Int(21));
        let attrs =
            find_attrs().with("roleAddress", item("Role")).with("primaryTargetIdAddress", item("Primary"));
        let status = rig.run(Box::new(FindTarget::default()), attrs);
        assert_eq!((status, rig.last_error.clone()), (Status::Success, None));
        assert_eq!(
            rig.last(ScopeType::Item, "Selected"),
            Value::Int(22),
            "not the primary, which is the largest"
        );
        // a tackler: what another tackler of the group has locked is left to it
        let mut rig = rig_with_targets();
        rig.ctx.my_entity_group_id = Some(10);
        rig.world.groups.insert(10, TestGroup { members: vec![1, 2], ..Default::default() });
        rig.post(ScopeType::Item, "Role", Value::Str("tackler".into()));
        {
            let mut cx = crate::tree::Cx {
                core: &mut rig.tree.core,
                ctx: &mut rig.ctx,
                shared: &mut rig.shared,
                world: &mut rig.world,
            };
            cx.send_to_channel(crate::blackboards::item_scope(2), "ROLE", Value::Str("tackler".into()));
            cx.send_to_channel(crate::blackboards::item_scope(2), "MY_COMBAT_TARGET", Value::Int(21));
        }
        rig.run(Box::new(FindTarget::default()), find_attrs().with("roleAddress", item("Role")));
        assert_eq!(rig.last(ScopeType::Item, "Selected"), Value::Int(22));
    }

    #[test]
    fn find_target_spreads_a_fleet_over_its_targets_and_records_my_groups() {
        let mut rig = rig_with_targets();
        rig.ctx.my_entity_group_id = Some(10);
        rig.post(ScopeType::Item, "Fleet", Value::Dict(vec![(Value::Int(11), Value::Int(21))]));
        let attrs = find_attrs()
            .with("shouldSpreadTargetsInFleet", true)
            .with("fleetTargetsByGroupAddress", item("Fleet"));
        rig.run(Box::new(FindTarget::default()), attrs);
        assert_eq!(rig.last(ScopeType::Item, "Selected"), Value::Int(22), "21 is another group's");
        assert_eq!(
            rig.last(ScopeType::Item, "Fleet"),
            Value::Dict(vec![(Value::Int(11), Value::Int(21)), (Value::Int(10), Value::Int(22))])
        );
    }

    #[test]
    fn find_target_for_a_module_stays_within_the_modules_range_and_spreads_over_modules() {
        let mut rig = rig_with_targets();
        rig.world.add_ball(30, [0.0; 3], 900);
        rig.world
            .items
            .insert(30, crate::env::InventoryItem { item_id: 30, type_id: 900, ..Default::default() });
        rig.world.default_effects.insert(900, 77);
        rig.world.effect_ranges.insert((30, 77), (Some(1000.0), Some(0.0)));
        rig.world.set_dist(1, 20, 900.0);
        rig.world.set_dist(1, 21, 2000.0);
        rig.world.set_dist(1, 22, 500.0);
        rig.post(ScopeType::Item, "Module", Value::Int(30));
        rig.post(ScopeType::Item, "ByModules", Value::Dict(vec![(Value::Int(31), Value::Int(22))]));
        rig.world
            .items
            .insert(31, crate::env::InventoryItem { item_id: 31, type_id: 901, ..Default::default() });
        rig.world.type_ids_by_list.insert(5, vec![901]);
        let attrs = find_attrs()
            .with("moduleAddress", item("Module"))
            .with("nonStackableEveTypeListId", 5i64)
            .with("targetsByModulesAddress", item("ByModules"));
        assert_eq!(rig.run(Box::new(FindTarget::new(FindKind::ForModule)), attrs), Status::Success);
        assert_eq!(rig.last_error, None);
        assert_eq!(
            rig.last(ScopeType::Item, "Selected"),
            Value::Int(20),
            "21 is out of range; 22 already has a module of the kind on it"
        );
    }

    #[test]
    fn a_polarized_preference_adds_or_takes_off_the_bias_for_the_preferred_types() {
        let mut rig = rig_with_targets();
        rig.world.type_ids_by_list.insert(8, vec![620]);
        rig.post(ScopeType::Item, "Enabled", Value::Bool(true));
        rig.post(ScopeType::Item, "Preferring", Value::Bool(true));
        rig.post(ScopeType::Item, "Bias", Value::Float(5.0));
        rig.world.damage.insert(20, crate::env::DamageTotals { damage_to_npcs: 1.0, healing_done: 0.0 });
        rig.world.damage.insert(21, crate::env::DamageTotals { damage_to_npcs: 10.0, healing_done: 0.0 });
        rig.world.damage.insert(22, crate::env::DamageTotals { damage_to_npcs: 2.0, healing_done: 0.0 });
        let attrs = Attrs::new()
            .with("selectedTargetAddress", item("Selected"))
            .with("potentialTargetListAddress", item("Potential"))
            .with("includedCategories", Value::List(vec![]))
            .with("onlyCheckLocalBubble", true)
            .with("isPreferenceEnabledAddress", item("Enabled"))
            .with("isPreferringTypeAddress", item("Preferring"))
            .with("preferredTypeBiasAmountAddress", item("Bias"))
            .with("preferredTypeListID", 8i64)
            .with("chanceOfPreferringTypesAddress", item("Chance"));
        assert_eq!(
            rig.run(Box::new(FindTarget::new(FindKind::PolarizedPreference)), attrs.clone()),
            Status::Success
        );
        assert_eq!(
            rig.last(ScopeType::Item, "Selected"),
            Value::Int(20),
            "type 620 is 20's and gets the bias over the 21 that has the most damage"
        );
        let mut rig2 = rig_with_targets();
        rig2.world.type_ids_by_list.insert(8, vec![620]);
        rig2.post(ScopeType::Item, "Enabled", Value::Bool(true));
        rig2.post(ScopeType::Item, "Preferring", Value::Bool(false));
        rig2.post(ScopeType::Item, "Bias", Value::Float(5.0));
        rig2.world.damage.insert(21, crate::env::DamageTotals { damage_to_npcs: 10.0, healing_done: 0.0 });
        rig2.run(Box::new(FindTarget::new(FindKind::PolarizedPreference)), attrs);
        assert_eq!(
            rig2.last(ScopeType::Item, "Selected"),
            Value::Int(21),
            "not preferring: the bias is taken off 20"
        );
    }

    fn lock_attrs() -> Attrs {
        Attrs::new().with("selectedTargetAddress", item("Selected"))
    }

    #[test]
    fn a_lock_is_acquired_by_a_tasklet_that_asks_the_dogma_engine_and_the_task_waits_suspended() {
        let mut rig = Rig::new();
        rig.world.add_ball(20, [0.0; 3], 1);
        rig.post(ScopeType::Item, "Selected", Value::Int(20));
        let lock = rig.tree.add_task(Box::new(AcquireTargetLock::default()), lock_attrs());
        rig.tree.start_root_task(lock);
        rig.tick().unwrap();
        assert_eq!(rig.tree.status_of(lock), Status::Suspended);
        assert_eq!(rig.shared.scheduled_call_count(), 1);
        // the tasklet runs: the target is asked for, the task goes on running
        rig.shared.fire_due_calls();
        for delivery in std::mem::take(&mut rig.shared.pending) {
            rig.tree.deliver(&mut rig.ctx, &mut rig.shared, &mut rig.world, &delivery).unwrap();
        }
        assert_eq!(rig.world.take_calls(), ["add_target_ex(1, 20)"]);
        assert_eq!(rig.tree.status_of(lock), Status::Running);
        // the target was added: the task succeeds
        rig.world.targeting.insert((1, 20));
        rig.tick().unwrap();
        assert_eq!(rig.tree.status_of(lock), Status::Success);
    }

    #[test]
    fn a_lock_on_a_target_that_is_gone_or_cloaked_fails_and_the_non_blocking_task_succeeds_while_it_waits() {
        let mut rig = Rig::new();
        rig.post(ScopeType::Item, "Selected", Value::Int(20));
        assert_eq!(rig.run(Box::new(AcquireTargetLock::default()), lock_attrs()), Status::Failure);
        let mut rig = Rig::new();
        rig.world.add_ball(20, [0.0; 3], 1);
        rig.post(ScopeType::Item, "Selected", Value::Int(20));
        assert_eq!(rig.run(Box::new(AcquireTargetLock::non_blocking()), lock_attrs()), Status::Success);
        assert_eq!(rig.shared.scheduled_call_count(), 1, "it still asks for the lock");
    }

    #[test]
    fn a_target_added_message_ends_a_waiting_lock_and_a_user_error_from_the_engine_is_not_an_error() {
        let mut rig = Rig::new();
        rig.world.add_ball(20, [0.0; 3], 1);
        rig.post(ScopeType::Item, "Selected", Value::Int(20));
        let lock = rig.tree.add_task(Box::new(AcquireTargetLock::default()), lock_attrs());
        rig.tree.start_root_task(lock);
        rig.tick().unwrap();
        {
            let mut cx = crate::tree::Cx {
                core: &mut rig.tree.core,
                ctx: &mut rig.ctx,
                shared: &mut rig.shared,
                world: &mut rig.world,
            };
            cx.send_event(1, TARGET_ADDED, &[Value::Int(20)]);
        }
        assert_eq!(rig.tree.status_of(lock), Status::Success);
        assert_eq!(rig.last_error, None);
        let mut rig = Rig::new();
        rig.world.add_ball(20, [0.0; 3], 1);
        rig.world.activation_error = Some(WorldError::User("no".into()));
        rig.post(ScopeType::Item, "Selected", Value::Int(20));
        rig.run(Box::new(AcquireTargetLock::default()), lock_attrs());
        rig.shared.fire_due_calls();
        for delivery in std::mem::take(&mut rig.shared.pending) {
            rig.tree.deliver(&mut rig.ctx, &mut rig.shared, &mut rig.world, &delivery).unwrap();
        }
        assert_eq!(rig.tree.take_fault(), None);
    }

    #[test]
    fn unlocking_takes_the_first_target_off_and_a_modules_target_only_when_no_other_module_has_it() {
        let mut rig = Rig::new();
        rig.world.targets.insert(1, vec![20, 21]);
        assert_eq!(rig.run(Box::new(UnlockTarget), Attrs::new()), Status::Success);
        assert_eq!(rig.world.take_calls(), ["remove_target(1, 20)"]);
        let attrs = Attrs::new()
            .with("moduleAddress", item("Module"))
            .with("targetsByModulesAddress", item("ByModules"));
        // module 30 works on 20: it is let go only when module 31 works on something else
        for (other_target, removed) in [(20, false), (22, true)] {
            let mut rig = Rig::new();
            rig.world.targets.insert(1, vec![20]);
            rig.post(ScopeType::Item, "Module", Value::Int(30));
            let dict = Value::Dict(vec![
                (Value::Int(30), Value::Int(20)),
                (Value::Int(31), Value::Int(other_target)),
            ]);
            rig.post(ScopeType::Item, "ByModules", dict);
            rig.run(Box::new(UnlockModuleTarget), attrs.clone());
            assert_eq!(rig.world.take_calls().len(), usize::from(removed));
        }
    }

    #[test]
    fn a_target_set_is_pruned_of_the_fake_items_that_left_the_park_and_cleared_with_a_log() {
        let attrs = Attrs::new().with("targetSetAddress", item("Set"));
        let mut rig = Rig::new();
        rig.world.add_ball(9_000_000_000_000_000_005, [0.0; 3], 1);
        rig.post(
            ScopeType::Item,
            "Set",
            Value::set([
                Value::Int(500),
                Value::Int(9_000_000_000_000_000_005),
                Value::Int(9_000_000_000_000_000_007),
            ]),
        );
        rig.run(Box::new(PruneNpcsFromTargetSet), attrs.clone());
        assert_eq!(
            rig.last(ScopeType::Item, "Set"),
            Value::Set(vec![Value::Int(500), Value::Int(9_000_000_000_000_000_005)])
        );
        // cleared
        let mut rig = Rig::new();
        rig.ctx.my_entity_group_id = Some(10);
        rig.world
            .groups
            .insert(10, TestGroup { members: vec![1], owner_id: Some(1000006), ..Default::default() });
        rig.post(ScopeType::Item, "Set", ids(&[500]));
        rig.run(Box::new(ClearTargetSet), attrs);
        assert_eq!(rig.last(ScopeType::Item, "Set"), Value::Set(vec![]));
        rig.shared.fire_due_calls();
        for delivery in std::mem::take(&mut rig.shared.pending) {
            rig.tree.deliver(&mut rig.ctx, &mut rig.shared, &mut rig.world, &delivery).unwrap();
        }
        assert_eq!(rig.world.take_calls(), ["log_killed_and_fled_players(Some(1000006), None, {500}, [1])"]);
    }

    #[test]
    fn only_flown_ships_in_the_park_are_selected() {
        let mut rig = Rig::new();
        for id in [20, 21, 22] {
            rig.world.add_ball(id, [0.0; 3], 1).new_bubble_id = 1;
            rig.world.slims.get_mut(&id).unwrap().category_id = CATEGORY_SHIP;
        }
        rig.world.slims.get_mut(&22).unwrap().category_id = 7;
        rig.world.piloted.insert(20);
        rig.world.piloted.insert(22);
        rig.post(ScopeType::Item, "Source", ids(&[20, 21, 22, 23]));
        rig.run(
            Box::new(SelectPilotedShips),
            Attrs::new().with("sourceSetAddress", item("Source")).with("targetSetAddress", item("Out")),
        );
        assert_eq!(rig.last(ScopeType::Item, "Out"), Value::Set(vec![Value::Int(20)]));
    }

    #[test]
    fn the_min_range_is_the_shorter_of_my_effects_and_my_weapons_and_the_max_effective_range_follows_from_it()
    {
        let mut rig = Rig::new();
        rig.ctx.my_slim_item = Some(SlimItem { type_id: 500, ..Default::default() });
        rig.world.type_effects.insert((500, 6754));
        rig.world.effect_ranges.insert((1, 6754), (Some(8000.0), Some(2000.0)));
        rig.world.attributes.insert((1, ATTRIBUTE_MAX_RANGE), 12_000.0);
        rig.world.attributes.insert((1, ATTRIBUTE_FALLOFF), 5000.0);
        rig.post(ScopeType::Item, "Effects", Value::List(vec![Value::Int(6754)]));
        let attrs = Attrs::new()
            .with("combatEffectsAddress", item("Effects"))
            .with("minOptimalAddress", item("Optimal"))
            .with("minFalloffAddress", item("Falloff"));
        rig.run(Box::new(GetMyMinOptimalAndFalloff), attrs.clone());
        assert_eq!(
            (rig.last(ScopeType::Item, "Optimal"), rig.last(ScopeType::Item, "Falloff")),
            (Value::Float(8000.0), Value::Float(2000.0)),
            "the effect's is shorter"
        );
        let mut rig = Rig::new();
        rig.ctx.my_slim_item = Some(SlimItem { type_id: 500, ..Default::default() });
        rig.world.attributes.insert((1, ATTRIBUTE_MAX_RANGE), 12_000.0);
        rig.world.attributes.insert((1, ATTRIBUTE_FALLOFF), 5000.0);
        rig.run(Box::new(GetMyMinOptimalAndFalloff), attrs);
        assert_eq!(
            (rig.last(ScopeType::Item, "Optimal"), rig.last(ScopeType::Item, "Falloff")),
            (Value::Float(12_000.0), Value::Float(5000.0))
        );
        let mut rig = Rig::new();
        rig.post(ScopeType::Item, "Optimal", Value::Float(8000.0));
        rig.post(ScopeType::Item, "Falloff", Value::Float(2000.0));
        rig.run(
            Box::new(GetMaxEffectiveRange),
            Attrs::new()
                .with("minOptimalAddress", item("Optimal"))
                .with("minFalloffAddress", item("Falloff"))
                .with("maxEffectiveRangeAddress", item("Max")),
        );
        assert_eq!(rig.last(ScopeType::Item, "Max"), Value::Float((8000.0 + 2000.0 * 0.75) * 1.5));
        let mut rig = Rig::new();
        rig.post(ScopeType::Item, "Optimal", Value::Float(8000.0));
        rig.run(
            Box::new(GetMaxEffectiveRange),
            Attrs::new()
                .with("minOptimalAddress", item("Optimal"))
                .with("minFalloffAddress", item("Falloff"))
                .with("maxEffectiveRangeAddress", item("Max")),
        );
        assert_eq!(rig.last(ScopeType::Item, "Max"), Value::Float(8000.0));
    }

    #[test]
    fn targets_by_combat_range_group_the_ships_in_reach_of_each_kind_of_entity() {
        let mut rig = Rig::new();
        rig.world.add_ball(5, [0.0; 3], 1);
        rig.world.add_ball(20, [100.0, 0.0, 0.0], 1).new_bubble_id = 0;
        rig.world.add_ball(21, [500.0, 0.0, 0.0], 1);
        rig.world.add_ball(22, [100.0, 0.0, 0.0], 1);
        for id in [20, 21] {
            rig.world.slims.get_mut(&id).unwrap().category_id = CATEGORY_SHIP;
        }
        rig.post(
            ScopeType::Item,
            "Ranges",
            Value::Dict(vec![(Value::Int(700), Value::Float(200.0)), (Value::Int(701), Value::Float(600.0))]),
        );
        rig.post(ScopeType::Item, "Commander", Value::Int(5));
        let attrs = Attrs::new()
            .with("rangeByEntityTypeAddress", item("Ranges"))
            .with("commanderAddress", item("Commander"))
            .with("targetsByEntityTypeAddress", item("Out"));
        rig.run(Box::new(GenerateTargetSetsByCombatRangesInGroup), attrs);
        assert_eq!(rig.last_error, None);
        assert_eq!(
            rig.last(ScopeType::Item, "Out"),
            Value::Dict(vec![
                (Value::Int(700), Value::Set(vec![Value::Int(20)])),
                (Value::Int(701), Value::Set(vec![Value::Int(20), Value::Int(21)]))
            ])
        );
    }

    #[test]
    fn the_primary_target_and_the_weighted_others_of_each_entity_type() {
        let mut rig = Rig::new();
        for (id, radius) in [(20, 100.0), (21, 300.0), (22, 200.0)] {
            rig.world.add_ball(id, [0.0; 3], 600 + id);
            rig.world.type_attributes.insert((600 + id, ATTRIBUTE_SIGNATURE_RADIUS), radius);
            rig.world.items.insert(
                id,
                crate::env::InventoryItem { item_id: id, type_id: 600 + id, ..Default::default() },
            );
        }
        rig.post(ScopeType::Item, "Targets", Value::Dict(vec![(Value::Int(700), ids(&[20, 21, 22]))]));
        let attrs = Attrs::new()
            .with("targetsAddress", item("Targets"))
            .with("primaryTargetEvaluationFunction", "size")
            .with("nonPrimaryTargetEvaluationFunction", "size")
            .with("primaryTargetsByEntityTypesAddress", item("Primary"))
            .with("nonPrimaryTargetsByEntityTypesAddress", item("NonPrimary"));
        rig.run(Box::new(GetPrimaryAndNonPrimaryTargetsByEntityTypes), attrs);
        assert_eq!(rig.last_error, None);
        assert_eq!(
            rig.last(ScopeType::Item, "Primary"),
            Value::Dict(vec![(Value::Int(700), Value::Int(21))])
        );
        assert_eq!(
            rig.last(ScopeType::Item, "NonPrimary"),
            Value::Dict(vec![(
                Value::Int(700),
                Value::Set(vec![
                    Value::tuple([Value::Int(20), Value::Float(100.0)]),
                    Value::tuple([Value::Int(22), Value::Float(200.0)])
                ])
            )])
        );
    }

    #[test]
    fn a_weighted_target_is_chosen_and_no_targets_fail() {
        let attrs = Attrs::new()
            .with("targetWeightSetAddress", item("Weights"))
            .with("targetIdAddress", item("Chosen"));
        let mut rig = Rig::new();
        rig.world = TestWorld::with_numbers(&[0.9]);
        rig.post(
            ScopeType::Item,
            "Weights",
            Value::set([
                Value::tuple([Value::Int(20), Value::Float(1.0)]),
                Value::tuple([Value::Int(21), Value::Float(1.0)]),
            ]),
        );
        assert_eq!(rig.run(Box::new(GetWeightedTargetFromTargetsSet), attrs.clone()), Status::Success);
        assert_eq!(rig.last(ScopeType::Item, "Chosen"), Value::Int(21));
        assert_eq!(Rig::new().run(Box::new(GetWeightedTargetFromTargetsSet), attrs), Status::Failure);
    }

    #[test]
    fn the_targets_that_are_not_mine_are_let_go_and_their_combat_effects_stopped() {
        let mut rig = Rig::new();
        rig.world.targets.insert(1, vec![20, 21]);
        rig.post(ScopeType::Item, "Mine", Value::Int(20));
        rig.post(ScopeType::Item, "Effects", Value::List(vec![Value::Int(99)]));
        rig.run(
            Box::new(DisengageObsoleteTargets),
            Attrs::new()
                .with("myTargetAddress", item("Mine"))
                .with("myCombatEffectsAddress", item("Effects")),
        );
        assert_eq!(
            rig.world.take_calls(),
            [
                "stop_effect(10, 1, false)",
                "stop_effect(569, 1, false)",
                "stop_effect(99, 1, false)",
                "remove_target(1, 21)",
            ]
        );
    }

    #[test]
    fn a_hostile_vulnerable_structure_in_range_is_put_on_the_targets() {
        let mut rig = Rig::new();
        rig.world.add_ball(1, [0.0; 3], 2);
        rig.world.add_ball(40, [0.0; 3], 1);
        rig.world.globals = vec![40];
        {
            let slim = rig.world.slims.get_mut(&40).unwrap();
            slim.category_id = CATEGORY_STRUCTURE;
            slim.owner_id = Some(98_000_001);
        }
        rig.world.vulnerable_structures.insert(40);
        rig.world.set_dist(1, 40, 100.0);
        rig.ctx.my_slim_item = Some(SlimItem { owner_id: Some(1000006), ..Default::default() });
        rig.world.thresholds =
            Some(ResponseThresholds { hostile_response_threshold: -1.0, friendly_response_threshold: 1.0 });
        rig.world.standings.insert((1000006, 98_000_001), -5.0);
        let attrs = Attrs::new().with("targetsAddress", item("Targets")).with("minDistance", 500.0);
        assert_eq!(rig.run(Box::new(FindHostileStructureInRange), attrs.clone()), Status::Success);
        assert_eq!(rig.last(ScopeType::Item, "Targets"), Value::Set(vec![Value::Int(40)]));
        rig.world.standings.insert((1000006, 98_000_001), 5.0);
        assert_eq!(rig.run(Box::new(FindHostileStructureInRange), attrs), Status::Failure);
        let _ = EffectInfo::default();
    }
}
