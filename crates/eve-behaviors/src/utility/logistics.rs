//! `behaviors/utility/logistics.py`: whom a logistics ship repairs first.

use crate::constants::behaviorroles::ROLE_LOGISTIC;
use crate::consts::*;
use crate::env::dogma::health;
use crate::groups::mixin;
use crate::tree::TaskCtx;
use crate::utility::dogmatic::{get_remote_repair_impedance, self_has_effect};
use crate::utility::roles::{get_group_commander, get_role_for_member};
use crate::value::Value;

pub const LOGISTICS_PRIORITY_COMMANDER: f64 = 0.5;
pub const LOGISTICS_PRIORITY_LOGISTIC: f64 = 0.4;
pub const LOGISTICS_PRIORITY_MEMBER_WITH_ROLE: f64 = 0.2;
pub const LOGISTICS_PRIORITY_MEMBER_WITHOUT_ROLE: f64 = 0.1;
pub const LOGISTICS_PRIORITY_NON_MEMBER: f64 = 0.0;

/// A function of what a ship has left: `health.GetCurrentArmorRatio` or `health.GetCurrentShieldRatio`.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum DamageState {
    Armor,
    Shield,
}

/// `get_base_logistics_priority`.
pub fn get_base_logistics_priority(t: &mut TaskCtx<'_, '_>, item_id: i64) -> f64 {
    if mixin::is_member(t, item_id) {
        let commander_id = get_group_commander(t);
        if commander_id.py_eq(&Value::Int(item_id)) {
            return LOGISTICS_PRIORITY_COMMANDER;
        }
        let role = get_role_for_member(t, item_id);
        if role.as_str() == Some(ROLE_LOGISTIC) {
            return LOGISTICS_PRIORITY_LOGISTIC;
        }
        if !role.is_none() {
            return LOGISTICS_PRIORITY_MEMBER_WITH_ROLE;
        }
        return LOGISTICS_PRIORITY_MEMBER_WITHOUT_ROLE;
    }
    LOGISTICS_PRIORITY_NON_MEMBER
}

/// `get_damage_state_function`: which ratio matters to me, by the remote repair effect I have.
pub fn get_damage_state_function(t: &mut TaskCtx<'_, '_>) -> Option<DamageState> {
    if self_has_effect(t, EFFECT_NPC_BEHAVIOR_REMOTE_ARMOR_REPAIRER) {
        return Some(DamageState::Armor);
    }
    if self_has_effect(t, EFFECT_NPC_BEHAVIOR_REMOTE_SHIELD_BOOSTER) {
        return Some(DamageState::Shield);
    }
    None
}

/// `get_remote_repair_effect_id`.
pub fn get_remote_repair_effect_id(t: &mut TaskCtx<'_, '_>) -> Option<i64> {
    if self_has_effect(t, EFFECT_NPC_BEHAVIOR_REMOTE_ARMOR_REPAIRER) {
        return Some(EFFECT_NPC_BEHAVIOR_REMOTE_ARMOR_REPAIRER);
    }
    if self_has_effect(t, EFFECT_NPC_BEHAVIOR_REMOTE_SHIELD_BOOSTER) {
        return Some(EFFECT_NPC_BEHAVIOR_REMOTE_SHIELD_BOOSTER);
    }
    None
}

/// `get_damage_state_function_by_effect_id`.
pub fn get_damage_state_function_by_effect_id(effect_id: i64) -> Option<DamageState> {
    match effect_id {
        EFFECT_NPC_BEHAVIOR_REMOTE_ARMOR_REPAIRER => Some(DamageState::Armor),
        EFFECT_NPC_BEHAVIOR_REMOTE_SHIELD_BOOSTER => Some(DamageState::Shield),
        _ => None,
    }
}

/// `get_damage_state_modifier`: `1 - ratio` of what the ship has left.
pub fn get_damage_state_modifier(
    t: &mut TaskCtx<'_, '_>,
    item_id: i64,
    damage_state: DamageState,
) -> Option<f64> {
    let ratio = match damage_state {
        DamageState::Armor => health::current_armor_ratio(&*t.world(), item_id),
        DamageState::Shield => health::current_shield_ratio(&*t.world(), item_id),
    };
    Some(1.0 - t.ok(ratio)?)
}

/// `get_logistics_priority`.
pub fn get_logistics_priority(
    t: &mut TaskCtx<'_, '_>,
    item_id: i64,
    damage_state: DamageState,
) -> Option<f64> {
    let base_priority = get_base_logistics_priority(t, item_id);
    let damage_modifier = get_damage_state_modifier(t, item_id, damage_state)?;
    let remote_repair_impedance = get_remote_repair_impedance(t, item_id)?;
    Some((base_priority + damage_modifier) * remote_repair_impedance)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::blackboards::{Address, ScopeType};
    use crate::constants::blackboardchannels::{ROLE_MESSAGE_CHANNEL, commander_address};
    use crate::env::testing::TestGroup;
    use crate::tree::tests::with_task;

    #[test]
    fn a_ships_priority_goes_by_its_place_in_my_group() {
        let (_, answer) = with_task(
            |rig| {
                rig.ctx.my_entity_group_id = Some(10);
                rig.world.groups.insert(10, TestGroup { members: vec![1, 2, 3, 4, 5], ..Default::default() });
            },
            |t| {
                t.send_blackboard_value(&commander_address(), Value::Int(2));
                for (item, role) in [(3, "logistic"), (4, "dps")] {
                    let channel = t.channel_for_item(item, ROLE_MESSAGE_CHANNEL);
                    t.channel_send(&channel, Value::Str(role.into()));
                }
                [2, 3, 4, 5, 99].map(|item| get_base_logistics_priority(t, item))
            },
        );
        assert_eq!(answer, Some([0.5, 0.4, 0.2, 0.1, 0.0]));
        let _ = Address::new(ScopeType::Item, "");
    }

    #[test]
    fn the_damage_state_function_goes_by_the_repair_effect_i_have_and_the_priority_adds_the_damage_and_multiplies_by_the_impedance()
     {
        let (_, answer) = with_task(
            |rig| {
                rig.ctx.my_slim_item = Some(crate::env::SlimItem { type_id: 500, ..Default::default() });
                rig.world.type_effects.insert((500, EFFECT_NPC_BEHAVIOR_REMOTE_SHIELD_BOOSTER));
                rig.ctx.my_entity_group_id = Some(10);
                rig.world.groups.insert(10, TestGroup { members: vec![7], ..Default::default() });
                rig.world.attributes.insert((7, ATTRIBUTE_SHIELD_CHARGE), 25.0);
                rig.world.attributes.insert((7, ATTRIBUTE_SHIELD_CAPACITY), 100.0);
                rig.world.attributes.insert((7, ATTRIBUTE_REMOTE_REPAIR_IMPEDANCE), 0.5);
            },
            |t| {
                let state = get_damage_state_function(t);
                (state, get_remote_repair_effect_id(t), state.and_then(|s| get_logistics_priority(t, 7, s)))
            },
        );
        assert_eq!(
            answer,
            Some((
                Some(DamageState::Shield),
                Some(EFFECT_NPC_BEHAVIOR_REMOTE_SHIELD_BOOSTER),
                Some((0.1 + 0.75) * 0.5)
            ))
        );
        assert_eq!(
            get_damage_state_function_by_effect_id(EFFECT_NPC_BEHAVIOR_REMOTE_ARMOR_REPAIRER),
            Some(DamageState::Armor)
        );
        assert_eq!(get_damage_state_function_by_effect_id(1), None);
    }
}
