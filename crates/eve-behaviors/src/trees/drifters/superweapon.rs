//! `behaviors/trees/drifters/superweapon.py`: a drifter that fires its super weapon at a ship, and puts the pilot on the list to pod.

use crate::blackboards::{Address, ScopeType};
use crate::constants::combat::THREAT_TARGET_EVALUATOR;
use crate::consts::*;

use crate::trees::builder::{Node, int_list, int_tuple, selector, sequence};
use crate::trees::combat::{COMBAT_EFFECTS, combat_targets_set};

/// `SUPER_WEAPON_TARGET`.
pub fn super_weapon_target() -> Address {
    Address::new(ScopeType::Item, "SUPER_WEAPON_TARGET")
}

/// `SUPER_WEAPON_READY`.
pub fn super_weapon_ready() -> Address {
    Address::new(ScopeType::Item, "SUPER_WEAPON_READY")
}

pub fn super_weapon_target_owner() -> Address {
    Address::new(ScopeType::Item, "SUPER_WEAPON_TARGET_OWNER")
}

/// `PODDING_TARGET_LIST`.
pub fn podding_target_list() -> Address {
    Address::new(ScopeType::EntityGroup, "PODDING_TARGET_LIST")
}

/// `OVERRIDE_SUPER_WEAPON_RESET`.
pub fn override_super_weapon_reset() -> Address {
    Address::new(ScopeType::Item, "OVERRIDE_SUPER_WEAPON_RESET")
}

/// `CreateSuperWeaponBehavior`.
pub fn create_super_weapon_behavior() -> Node {
    let pick_target_and_lock_quickly = sequence()
        .named("Pick target and lock quickly")
        .add(Node::class("BlackboardSetMessageAsNoneAction").with("messageAddress", super_weapon_target()))
        .add(
            Node::class("FindTarget")
                .with("potentialTargetListAddress", combat_targets_set())
                .with("selectedTargetAddress", super_weapon_target())
                .with("includedCategories", int_list(&[CATEGORY_SHIP, CATEGORY_ENTITY]))
                .with("onlyCheckLocalBubble", true)
                .with("targetEvaluationFunction", THREAT_TARGET_EVALUATOR)
                .with("primaryTargetIdAddress", combat_targets_set()),
        )
        .add(
            Node::class("SetDogmaAttributeValue")
                .with("attributeId", ATTRIBUTE_SCAN_RESOLUTION)
                .with("value", 10000.0),
        )
        .add(Node::class("AcquireTargetLock").with("selectedTargetAddress", super_weapon_target()))
        .add(Node::class("ResetDogmaAttributeValue").with("attributeId", ATTRIBUTE_SCAN_RESOLUTION));
    let add_owner_to_pod_list = sequence()
        .named("Add owner to pod list")
        .add(
            Node::class("AddCharacterForPodding")
                .with("characterAddress", super_weapon_target_owner())
                .with("poddingTargetListAddress", podding_target_list()),
        )
        .add(
            Node::class("GetBallsInBubbleByOwnerIds")
                .with("objectListAddress", combat_targets_set())
                .with("ownerListAddress", podding_target_list()),
        );
    let destroy_someone = sequence()
        .named("Destroy someone")
        .add(Node::class("RepeatWhileFailing").with("maxRepetitions", 5).add(pick_target_and_lock_quickly))
        .add(
            Node::class("ActivateTargetedEffect")
                .with("selectedTargetAddress", super_weapon_target())
                .with("effectId", EFFECT_WARP_SCRAMBLE_FOR_ENTITY)
                .with("repeats", crate::value::Value::None)
                .with("shouldResetBehaviorOnEffectStopped", false),
        )
        .add(Node::class("StopCombatEffectsOnTarget").with("effectIds", int_tuple(&COMBAT_EFFECTS)))
        .add(Node::class("FullStopAction"))
        .add(
            Node::class("StoreSlimItemAttributeToBlackboard")
                .with("targetAddress", super_weapon_target())
                .with("messageAddress", super_weapon_target_owner())
                .with("attributeName", "ownerID"),
        )
        .add(Node::class("ActivateSuperWeaponOnTarget").with("selectedTargetAddress", super_weapon_target()))
        .add(
            selector()
                .add(
                    Node::class("OwnerIsNpcCondition")
                        .named("Skipping NPC owners")
                        .with("ownerIdAddress", super_weapon_target_owner()),
                )
                .add(add_owner_to_pod_list),
        )
        .add(
            Node::class("BlackboardSetMessageToBooleanValueAction")
                .with("messageAddress", super_weapon_ready())
                .with("value", false),
        );
    selector()
        .named("Super Weapon")
        .add(
            Node::class("ForceFailure").add(
                selector()
                    .add(
                        Node::class("IsBlackboardValueTrue")
                            .named("Should override Weapon Reset?")
                            .with("valueAddress", override_super_weapon_reset()),
                    )
                    .add(
                        sequence()
                            .named("Reset Weapon")
                            .add(
                                Node::class("WaitForTurboShieldState")
                                    .with("turboShieldState", TURBO_SHIELD_STATE_ACTIVE),
                            )
                            .add(
                                Node::class("BlackboardSetMessageToBooleanValueAction")
                                    .with("messageAddress", super_weapon_ready())
                                    .with("value", false),
                            ),
                    ),
            ),
        )
        .add(
            sequence()
                .named("Enable Weapon")
                .add(Node::class("IsBlackboardValueTrue").with("valueAddress", super_weapon_ready()))
                .add(Node::class("Uninterruptible").add(destroy_someone)),
        )
        .add(
            Node::class("WaitForTurboShieldState")
                .with("turboShieldState", TURBO_SHIELD_STATE_RESISTIVE)
                .with("messageAddress", super_weapon_ready()),
        )
}
