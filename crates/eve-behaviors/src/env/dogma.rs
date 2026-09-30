//! The server's `dogmaLM` (and its `dogmaStaticMgr`) as the tasks see it: the numbers of items and of types, effects that run, targets.

use super::WorldResult;
use crate::value::Value;

/// A dogma effect's static data: the attributes that give its duration, range and falloff (`dogmaStaticMgr.GetEffect(effectId)`).
#[derive(Debug, Clone, Default, PartialEq)]
pub struct EffectInfo {
    pub duration_attribute_id: Option<i64>,
    pub range_attribute_id: Option<i64>,
    pub falloff_attribute_id: Option<i64>,
}

/// The dogma engine's answers and what it is asked to do.
pub trait Dogma {
    /// `dogmaLM.GetAttributeValue(itemId, attributeId)`.
    fn attribute_value(&self, _item_id: i64, _attribute_id: i64) -> WorldResult<f64> {
        Ok(0.0)
    }

    /// `dogmaLM.dogmaStaticMgr.GetTypeAttribute2(typeId, attributeId)`.
    fn type_attribute(&self, _type_id: i64, _attribute_id: i64) -> f64 {
        0.0
    }

    /// `dogmaLM.dogmaStaticMgr.TypeHasEffect(typeId, effectId)`.
    fn type_has_effect(&self, _type_id: i64, _effect_id: i64) -> bool {
        false
    }

    /// `dogmaLM.GetActiveEffectData(itemId, effectId) is not None`.
    fn is_effect_active(&self, _item_id: i64, _effect_id: i64) -> bool {
        false
    }

    /// `dogmaLM.GetTargets(itemId)`.
    fn targets(&self, _item_id: i64) -> Vec<i64> {
        Vec::new()
    }

    /// `dogmaLM.GetTargetsEx(itemId)`: the targets, and those being locked.
    fn targets_ex(&self, _item_id: i64) -> Vec<i64> {
        Vec::new()
    }

    /// `dogmaLM.HasPendingTarget(itemId, targetId)`.
    fn has_pending_target(&self, _item_id: i64, _target_id: Option<i64>) -> bool {
        false
    }

    /// `dogmaLM.IsTargeting(itemId, targetId)`.
    fn is_targeting(&self, _item_id: i64, _target_id: Option<i64>) -> bool {
        false
    }

    /// `dogmaLM.IsItemActive(itemId)`.
    fn is_item_active(&self, _item_id: i64) -> bool {
        false
    }

    /// `dogmaLM.GetFittedModulesGroupIdsForShip(shipId)`.
    fn fitted_module_group_ids(&self, _ship_id: i64) -> Vec<i64> {
        Vec::new()
    }

    /// `dogmaLM.GetItemIdTypeIdAndFlagIdForOnlineFittedModules(itemId)`: `(module id, type id, flag id)`.
    fn online_fitted_modules(&self, _item_id: i64) -> Vec<(i64, i64, i64)> {
        Vec::new()
    }

    /// `dogmaLM.IsShipAbandoned(shipId)`.
    fn is_ship_abandoned(&self, _ship_id: i64) -> bool {
        false
    }

    /// `dogmaLM.GetActiveEffectEnvironment(itemId, effectId).targetID`: `None` when the effect is not active, else its target (which may be none).
    fn active_effect_target_id(&self, _item_id: i64, _effect_id: i64) -> Option<Option<i64>> {
        None
    }

    /// `dogmaStaticMgr.GetEffect(effectId)` and `effectId in dogmaStaticMgr.effects`.
    fn effect_info(&self, _effect_id: i64) -> Option<EffectInfo> {
        None
    }

    /// `dogmaStaticMgr.GetDefaultEffect(typeId)`: the effect id of the type's default effect.
    fn default_effect_id(&self, _type_id: i64) -> Option<i64> {
        None
    }

    /// `get_effect_range_falloff_modifier_at_distance(dogmaLM, effectId, sourceId, distance)`.
    fn range_falloff_modifier_at_distance(&self, _effect_id: i64, _source_id: i64, _distance: f64) -> f64 {
        1.0
    }

    /// `dogmaLM.ActivateWithContext(ownerId, moduleId, effectId, targetid=, repeat=, context=)`.
    fn activate_with_context(
        &mut self,
        _owner_id: i64,
        _module_id: i64,
        _effect_id: i64,
        _target_id: Option<i64>,
        _repeat: i64,
        _context: &Value,
    ) -> WorldResult<()> {
        Ok(())
    }

    /// `dogmaLM.StopEffect(effectId, moduleId, forceStopRepeating=True)`.
    fn stop_effect_force_repeating(&mut self, _effect_id: i64, _module_id: i64) -> WorldResult<()> {
        Ok(())
    }

    /// `dogmaLM.StopEffect(effectId, itemId, forced=)`.
    fn stop_effect(&mut self, _effect_id: i64, _item_id: i64, _forced: bool) -> WorldResult<()> {
        Ok(())
    }

    /// `dogmaLM.RemoveTarget(itemId, targetId)`.
    fn remove_target(&mut self, _item_id: i64, _target_id: i64) {}
}

/// `dogma.attributes.health`: the ratios of what a ship has left, from the dogma engine's numbers.
pub mod health {
    use super::{Dogma, WorldResult};
    use crate::consts::*;

    /// `GetCurrentArmorRatio`.
    pub fn current_armor_ratio(dogma: &dyn Dogma, item_id: i64) -> WorldResult<f64> {
        let max_armor_hp = dogma.attribute_value(item_id, ATTRIBUTE_ARMOR_HP)?;
        let current_armor_damage = dogma.attribute_value(item_id, ATTRIBUTE_ARMOR_DAMAGE)?;
        Ok((max_armor_hp - current_armor_damage) / max_armor_hp)
    }

    /// `GetCurrentShieldRatio`.
    pub fn current_shield_ratio(dogma: &dyn Dogma, item_id: i64) -> WorldResult<f64> {
        Ok(dogma.attribute_value(item_id, ATTRIBUTE_SHIELD_CHARGE)?
            / dogma.attribute_value(item_id, ATTRIBUTE_SHIELD_CAPACITY)?)
    }

    /// `GetCurrentStructureDamage`.
    pub fn current_structure_damage(dogma: &dyn Dogma, item_id: i64) -> WorldResult<f64> {
        dogma.attribute_value(item_id, ATTRIBUTE_DAMAGE)
    }

    /// `GetMaxStructureAmount`.
    pub fn max_structure_amount(dogma: &dyn Dogma, item_id: i64) -> WorldResult<f64> {
        dogma.attribute_value(item_id, ATTRIBUTE_HP)
    }
}
