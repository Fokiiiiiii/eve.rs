//! `behaviors/const/behaviorroles.py`: what an NPC does in a fight, and which effects go with each role.

use crate::consts::*;

pub const ROLE_ANTI_DPS: &str = "anti-dps";
pub const ROLE_ANTI_DPS_WITH_PRIMARY: &str = "anti-dps (with primary)";
pub const ROLE_ANTI_LOGISTIC: &str = "anti-logistic";
pub const ROLE_COMMANDER: &str = "commander";
pub const ROLE_DPS: &str = "dps";
pub const ROLE_EW: &str = "ew";
pub const ROLE_LOGISTIC: &str = "logistic";
pub const ROLE_TACKLER: &str = "tackler";
pub const ROLE_DREADNOUGHT: &str = "dreadnought";
pub const ROLE_GENERIC_EWAR: &str = "ewar";

pub const COMBAT_ROLES: [&str; 10] = [
    ROLE_ANTI_DPS,
    ROLE_ANTI_LOGISTIC,
    ROLE_COMMANDER,
    ROLE_DPS,
    ROLE_EW,
    ROLE_LOGISTIC,
    ROLE_TACKLER,
    ROLE_DREADNOUGHT,
    ROLE_ANTI_DPS_WITH_PRIMARY,
    ROLE_GENERIC_EWAR,
];

/// `COMBAT_EFFECTS_BY_ROLES[role]`: the effects a role uses (none for a role that has no entry).
pub fn combat_effects_by_role(role: &str) -> Option<&'static [i64]> {
    const LOGISTIC: [i64; 3] = [
        EFFECT_NPC_BEHAVIOR_REMOTE_ARMOR_REPAIRER,
        EFFECT_NPC_BEHAVIOR_REMOTE_CAPACITOR_TRANSMITTER,
        EFFECT_NPC_BEHAVIOR_REMOTE_SHIELD_BOOSTER,
    ];
    const TACKLER: [i64; 4] = [
        EFFECT_BEHAVIOR_WARP_DISRUPT,
        EFFECT_NPC_BEHAVIOR_WEBIFIER,
        EFFECT_BEHAVIOR_WARP_SCRAMBLE,
        EFFECT_BEHAVIOR_TARGET_PAINTER,
    ];
    const ANTI_DPS: [i64; 7] = [
        EFFECT_NPC_BEHAVIOR_GUIDANCE_DISRUPTOR,
        EFFECT_NPC_BEHAVIOR_TRACKING_DISRUPTOR,
        EFFECT_BEHAVIOR_TARGET_PAINTER,
        EFFECT_NPC_BEHAVIOR_ENERGY_NEUTRALIZER,
        EFFECT_NPC_BEHAVIOR_ENERGY_NOSFERATU,
        EFFECT_BEHAVIOR_SENSOR_DAMPENER,
        EFFECT_BEHAVIOR_ECM,
    ];
    const ANTI_LOGISTIC: [i64; 4] = [
        EFFECT_NPC_BEHAVIOR_ENERGY_NEUTRALIZER,
        EFFECT_BEHAVIOR_SENSOR_DAMPENER,
        EFFECT_BEHAVIOR_ECM,
        EFFECT_BEHAVIOR_TARGET_PAINTER,
    ];
    const DPS: [i64; 3] = [
        EFFECT_NPC_BEHAVIOR_MICRO_JUMP_ATTACK,
        EFFECT_NPC_BEHAVIOR_SMART_BOMB,
        EFFECT_ENTITY_CHAIN_LIGHTNING,
    ];
    const GENERIC_EWAR: [i64; 10] = [
        EFFECT_BEHAVIOR_WARP_DISRUPT,
        EFFECT_NPC_BEHAVIOR_WEBIFIER,
        EFFECT_BEHAVIOR_WARP_SCRAMBLE,
        EFFECT_NPC_BEHAVIOR_GUIDANCE_DISRUPTOR,
        EFFECT_NPC_BEHAVIOR_TRACKING_DISRUPTOR,
        EFFECT_BEHAVIOR_TARGET_PAINTER,
        EFFECT_NPC_BEHAVIOR_ENERGY_NEUTRALIZER,
        EFFECT_NPC_BEHAVIOR_ENERGY_NOSFERATU,
        EFFECT_BEHAVIOR_SENSOR_DAMPENER,
        EFFECT_BEHAVIOR_ECM,
    ];
    match role {
        ROLE_LOGISTIC => Some(&LOGISTIC),
        ROLE_TACKLER => Some(&TACKLER),
        ROLE_ANTI_DPS | ROLE_ANTI_DPS_WITH_PRIMARY => Some(&ANTI_DPS),
        ROLE_ANTI_LOGISTIC => Some(&ANTI_LOGISTIC),
        ROLE_DREADNOUGHT => Some(&[]),
        ROLE_DPS => Some(&DPS),
        ROLE_GENERIC_EWAR => Some(&GENERIC_EWAR),
        _ => None,
    }
}

/// `COMBAT_DISTRIBUTE_EFFECTS_BY_ROLE`.
pub const COMBAT_DISTRIBUTE_EFFECTS_BY_ROLE: [&str; 3] = [ROLE_TACKLER, ROLE_ANTI_DPS, ROLE_GENERIC_EWAR];

/// `COMBAT_IGNORE_PRIMARY_BY_ROLE`.
pub const COMBAT_IGNORE_PRIMARY_BY_ROLE: [&str; 1] = [ROLE_ANTI_DPS];

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_role_has_its_effects_and_a_role_with_none_listed_has_no_entry() {
        assert_eq!(combat_effects_by_role(ROLE_TACKLER).unwrap().len(), 4);
        assert_eq!(combat_effects_by_role(ROLE_ANTI_DPS), combat_effects_by_role(ROLE_ANTI_DPS_WITH_PRIMARY));
        assert_eq!(combat_effects_by_role(ROLE_DREADNOUGHT), Some(&[][..]));
        assert_eq!(
            combat_effects_by_role(ROLE_COMMANDER),
            None,
            "the client's table has no entry for the commander"
        );
        assert_eq!(combat_effects_by_role(ROLE_GENERIC_EWAR).unwrap().len(), 10);
    }
}
