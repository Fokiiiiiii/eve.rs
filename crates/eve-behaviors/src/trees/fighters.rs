//! `behaviors/trees/fighters/__init__.py`: what a fighter (of a carrier) does: the abilities of its slots, at the points it is told, and the kamikaze.

use crate::blackboards::{Address, ScopeType};
use crate::consts::{ABILITY_SLOT_0, ABILITY_SLOT_1, ABILITY_SLOT_2};
use crate::tree::BehaviorTree;

use super::builder::{Node, selector, sequence};

/// `KAMIKAZE_ADDRESS`.
pub fn kamikaze_address() -> Address {
    Address::new(ScopeType::Item, "KAMIKAZE_ADDRESS")
}

/// `KAMIKAZE_ABORT_ADDRESS`.
pub fn kamikaze_abort_address() -> Address {
    Address::new(ScopeType::Item, "KAMIKAZE_ABORT_ADDRESS")
}

fn item(name: String) -> Address {
    Address::new(ScopeType::Item, &name)
}

/// `ADDRESSES_BY_SLOT_ID`: where the target point, and the effects to start and to stop, of the ability of a slot are.
pub fn addresses_by_slot_id(slot: i64) -> (Address, Address, Address) {
    (
        item(format!("ABILITY_TARGET_SLOT{slot}_ADDRESS")),
        item(format!("ABILITY_START_EFFECTID_SLOT{slot}_ADDRESS")),
        item(format!("ABILITY_STOP_EFFECTID_SLOT{slot}_ADDRESS")),
    )
}

/// `ACTIVATION_FAILURE_REASON_ADDRESSES`.
pub fn activation_failure_reason_address(slot: i64) -> Address {
    item(format!("ABILITY_FAILURE_REASON_SLOT{slot}_ADDRESS"))
}

/// `CreateFighterBehavior`.
pub fn create_fighter_behavior() -> Node {
    sequence()
        .add(create_ability_slot_behavior(ABILITY_SLOT_0))
        .add(create_ability_slot_behavior(ABILITY_SLOT_1))
        .add(create_ability_slot_behavior(ABILITY_SLOT_2))
        .add(
            sequence()
                .named("Kamikaze")
                .add(Node::class("BlackboardMessageMonitor").with("messageAddress", kamikaze_address()))
                .add(
                    Node::class("ForceSuccess").add(
                        selector().add(
                            sequence()
                                .named("Kamikaze")
                                .add(
                                    Node::class("IsBlackboardValueNotNone")
                                        .with("valueAddress", kamikaze_address()),
                                )
                                .add(
                                    Node::class("KamikazeAttack")
                                        .named("Kamikaze")
                                        .with("selectedTargetAddress", kamikaze_address())
                                        .with("shouldAbortAddress", kamikaze_abort_address()),
                                )
                                .add(
                                    Node::class("BlackboardSetMessageAsNoneAction")
                                        .with("messageAddress", kamikaze_address()),
                                ),
                        ),
                    ),
                ),
        )
        .add(Node::class("WaitAction"))
}

/// `_CreateAbilitySlotBehavior(abilitySlotID)`.
pub fn create_ability_slot_behavior(ability_slot_id: i64) -> Node {
    let description = format!("Slot {ability_slot_id}");
    let (target_address, start_effect_id_address, stop_effect_id_address) =
        addresses_by_slot_id(ability_slot_id);
    let failure_reason_address = activation_failure_reason_address(ability_slot_id);
    sequence()
        .named(&format!("Ability effect [{description}]"))
        .add(Node::class("BlackboardMessageMonitor").with("messageAddress", &start_effect_id_address))
        .add(Node::class("BlackboardMessageMonitor").with("messageAddress", &stop_effect_id_address))
        .add(
            Node::class("ForceSuccess").add(
                selector()
                    .add(
                        sequence()
                            .named("Start point-targeted-effect ability")
                            .add(
                                Node::class("IsBlackboardValueNotNone")
                                    .with("valueAddress", &start_effect_id_address),
                            )
                            .add(
                                Node::class("IsBlackboardValue3dVector")
                                    .with("valueAddress", &target_address),
                            )
                            .add(
                                selector()
                                    .add(
                                        Node::class("Uninterruptible").add(
                                            sequence()
                                                .add(
                                                    Node::class("AlignToCoordinatesAction")
                                                        .with("coordinateAddress", &target_address)
                                                        .with("timeoutSeconds", 10)
                                                        .with("acceptableAngleDeviationDegrees", 1),
                                                )
                                                .add(
                                                    selector()
                                                        .add(Node::class("IsEffectByAddressActive").with(
                                                            "effectIDAddress",
                                                            &start_effect_id_address,
                                                        ))
                                                        .add(
                                                            Node::class("ActivateAbilityEffectOnPoint")
                                                                .with(
                                                                    "effectIDAddress",
                                                                    &start_effect_id_address,
                                                                )
                                                                .with("targetPoint", &target_address)
                                                                .with("abilitySlotID", ability_slot_id)
                                                                .with(
                                                                    "failureReasonAddress",
                                                                    &failure_reason_address,
                                                                ),
                                                        ),
                                                ),
                                        ),
                                    )
                                    .add(
                                        Node::class("NotifyAbilityActivationFailure")
                                            .with("abilitySlotID", ability_slot_id),
                                    ),
                            )
                            .add(
                                Node::class("BlackboardSetMessageAsNoneAction")
                                    .with("messageAddress", &start_effect_id_address),
                            ),
                    )
                    .add(
                        sequence()
                            .named("Stop effect ability")
                            .add(
                                Node::class("IsBlackboardValueNotNone")
                                    .with("valueAddress", &stop_effect_id_address),
                            )
                            .add(
                                Node::class("StopAbilityEffectOnTarget")
                                    .with("effectIDAddress", &stop_effect_id_address)
                                    .with("abilitySlotID", ability_slot_id)
                                    .with("failureReasonAddress", &failure_reason_address),
                            )
                            .add(
                                Node::class("BlackboardSetMessageAsNoneAction")
                                    .with("messageAddress", &stop_effect_id_address),
                            ),
                    ),
            ),
        )
}

/// `CreateFighterBehaviorTree`.
pub fn create_fighter_behavior_tree() -> BehaviorTree {
    create_fighter_behavior().into_tree()
}
