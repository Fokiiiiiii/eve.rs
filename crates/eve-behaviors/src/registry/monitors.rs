//! The tasks of `behaviors/registry/monitors.py`: the classes the behavior trees of the FSD data name, with the attributes they take.

#![allow(clippy::default_constructed_unit_structs)]

use super::{AttributeKind, AttributeSpec, DefaultValue, TaskEntry, TaskKind};

pub const MONITORS: &[TaskEntry] = &[
    TaskEntry {
        class_name: "behaviors.monitors.abyssal_deadspace.ResetOnCloseToAbyssalDangerZone",
        kind: TaskKind::Monitor,
        short_description: None,
        detailed_description: None,
        attributes: &[],
        factory: |_| Box::new(crate::monitors::abyssal_deadspace::ResetOnCloseToAbyssalDangerZone::default()),
    },
    TaskEntry {
        class_name: "behaviors.monitors.ballparks.BubbleChangedMonitor",
        kind: TaskKind::Monitor,
        short_description: None,
        detailed_description: None,
        attributes: &[],
        factory: |_| Box::new(crate::monitors::ballparks::BubbleChangedMonitor::default()),
    },
    TaskEntry {
        class_name: "behaviors.monitors.ballparks.TargetChangingBubbleMonitor",
        kind: TaskKind::Monitor,
        short_description: None,
        detailed_description: None,
        attributes: &[
            AttributeSpec::new(
                "targetAddress",
                "BlackboardAddressAttribute",
                AttributeKind::BlackboardAddress,
            ),
            AttributeSpec::new(
                "bubbleIdAddress",
                "BlackboardAddressAttribute",
                AttributeKind::BlackboardAddress,
            )
            .optional(),
            AttributeSpec::new("resetOnEnter", "BooleanAttribute", AttributeKind::Plain),
        ],
        factory: |_| Box::new(crate::monitors::ballparks::BubbleChangedMonitor::target_changing()),
    },
    TaskEntry {
        class_name: "behaviors.monitors.ballparks.BallEnteredOrLeftBubbleGroupMonitor",
        kind: TaskKind::Monitor,
        short_description: Some(
            "Monitors the bubbles of all group members for balls entering/leaving their bubble.",
        ),
        detailed_description: Some(
            "Monitors bubble enter/leave events in the bubbles of all group members and wakesup the tree when one of those occurs.",
        ),
        attributes: &[],
        factory: |_| Box::new(crate::monitors::ballparks::BallEnteredOrLeftBubbleGroupMonitor::default()),
    },
    TaskEntry {
        class_name: "behaviors.monitors.ballparks.TargetWithOwnerEnteredBubbleMonitor",
        kind: TaskKind::Monitor,
        short_description: None,
        detailed_description: None,
        attributes: &[
            AttributeSpec::new(
                "targetSetAddress",
                "BlackboardAddressAttribute",
                AttributeKind::BlackboardAddress,
            ),
            AttributeSpec::new("ownerIds", "OwnerListAttribute", AttributeKind::Plain),
        ],
        factory: |_| Box::new(crate::monitors::ballparks::TargetWithOwnerEnteredBubbleMonitor::default()),
    },
    TaskEntry {
        class_name: "behaviors.monitors.ballparks.TargetsWithOwnerEnteredBubbleMonitor",
        kind: TaskKind::Monitor,
        short_description: None,
        detailed_description: None,
        attributes: &[
            AttributeSpec::new(
                "targetSetAddress",
                "BlackboardAddressAttribute",
                AttributeKind::BlackboardAddress,
            ),
            AttributeSpec::new("ownerIds", "OwnerListAttribute", AttributeKind::Plain),
        ],
        factory: |_| Box::new(crate::monitors::ballparks::TargetWithOwnerEnteredBubbleMonitor::for_group()),
    },
    TaskEntry {
        class_name: "behaviors.monitors.ballparks.TargetLeavingParkMonitor",
        kind: TaskKind::Monitor,
        short_description: None,
        detailed_description: None,
        attributes: &[
            AttributeSpec::new(
                "targetAddress",
                "BlackboardAddressAttribute",
                AttributeKind::BlackboardAddress,
            ),
            AttributeSpec::new("clearValue", "BooleanAttribute", AttributeKind::Plain),
        ],
        factory: |_| Box::new(crate::monitors::ballparks::TargetLeavingParkMonitor::default()),
    },
    TaskEntry {
        class_name: "behaviors.monitors.ballparks.MonitorProximitySensors",
        kind: TaskKind::Monitor,
        short_description: None,
        detailed_description: None,
        attributes: &[
            AttributeSpec::new(
                "objectListAddress",
                "BlackboardAddressAttribute",
                AttributeKind::BlackboardAddress,
            ),
            AttributeSpec::new("includedCategories", "InventoryCategorySetAttribute", AttributeKind::Plain),
            AttributeSpec::new("excludedGroups", "InventoryGroupSetAttribute", AttributeKind::Plain),
            AttributeSpec::new("validOwnerIds", "OwnerListAttribute", AttributeKind::Plain),
            AttributeSpec::new("invalidOwnerIds", "OwnerListAttribute", AttributeKind::Plain),
            AttributeSpec::new("tags", "StringSetAttribute", AttributeKind::Plain),
        ],
        factory: |_| Box::new(crate::monitors::ballparks::MonitorProximitySensors::default()),
    },
    TaskEntry {
        class_name: "behaviors.monitors.ballparks.MonitorProximitySensorsForGroupMembers",
        kind: TaskKind::Monitor,
        short_description: None,
        detailed_description: None,
        attributes: &[
            AttributeSpec::new(
                "objectListAddress",
                "BlackboardAddressAttribute",
                AttributeKind::BlackboardAddress,
            ),
            AttributeSpec::new("includedCategories", "InventoryCategorySetAttribute", AttributeKind::Plain),
            AttributeSpec::new("excludedGroups", "InventoryGroupSetAttribute", AttributeKind::Plain),
            AttributeSpec::new("validOwnerIds", "OwnerListAttribute", AttributeKind::Plain),
            AttributeSpec::new("invalidOwnerIds", "OwnerListAttribute", AttributeKind::Plain),
        ],
        factory: |_| {
            Box::new(crate::monitors::ballparks::MonitorProximitySensors::new(
                crate::monitors::ballparks::SensorKind::Group,
            ))
        },
    },
    TaskEntry {
        class_name: "behaviors.monitors.ballparks.MonitorSpecificProximitySensor",
        kind: TaskKind::Monitor,
        short_description: None,
        detailed_description: None,
        attributes: &[
            AttributeSpec::new(
                "objectListAddress",
                "BlackboardAddressAttribute",
                AttributeKind::BlackboardAddress,
            ),
            AttributeSpec::new("includedCategories", "InventoryCategorySetAttribute", AttributeKind::Plain),
            AttributeSpec::new("excludedGroups", "InventoryGroupSetAttribute", AttributeKind::Plain),
            AttributeSpec::new("validOwnerIds", "OwnerListAttribute", AttributeKind::Plain),
            AttributeSpec::new("invalidOwnerIds", "OwnerListAttribute", AttributeKind::Plain),
            AttributeSpec::new(
                "proximitySensorAddress",
                "BlackboardAddressAttribute",
                AttributeKind::BlackboardAddress,
            ),
        ],
        factory: |_| {
            Box::new(crate::monitors::ballparks::MonitorProximitySensors::new(
                crate::monitors::ballparks::SensorKind::Specific,
            ))
        },
    },
    TaskEntry {
        class_name: "behaviors.monitors.ballparks.MonitorSpecificProximitySensorForGroupMembers",
        kind: TaskKind::Monitor,
        short_description: None,
        detailed_description: None,
        attributes: &[
            AttributeSpec::new(
                "objectListAddress",
                "BlackboardAddressAttribute",
                AttributeKind::BlackboardAddress,
            ),
            AttributeSpec::new("includedCategories", "InventoryCategorySetAttribute", AttributeKind::Plain),
            AttributeSpec::new("excludedGroups", "InventoryGroupSetAttribute", AttributeKind::Plain),
            AttributeSpec::new("validOwnerIds", "OwnerListAttribute", AttributeKind::Plain),
            AttributeSpec::new("invalidOwnerIds", "OwnerListAttribute", AttributeKind::Plain),
            AttributeSpec::new(
                "proximitySensorAddress",
                "BlackboardAddressAttribute",
                AttributeKind::BlackboardAddress,
            ),
        ],
        factory: |_| {
            Box::new(crate::monitors::ballparks::MonitorProximitySensors::new(
                crate::monitors::ballparks::SensorKind::SpecificGroup,
            ))
        },
    },
    TaskEntry {
        class_name: "behaviors.monitors.ballparks.MonitorInvulnerabilityCanceledInBubble",
        kind: TaskKind::Monitor,
        short_description: None,
        detailed_description: None,
        attributes: &[AttributeSpec::new(
            "targetIdListAddress",
            "BlackboardAddressAttribute",
            AttributeKind::BlackboardAddress,
        )
        .optional()],
        factory: |_| Box::new(crate::monitors::ballparks::MonitorInvulnerabilityCanceledInBubble::default()),
    },
    TaskEntry {
        class_name: "behaviors.monitors.ballparks.MonitorInvulnerabilityCanceledInGroupBubbles",
        kind: TaskKind::Monitor,
        short_description: None,
        detailed_description: None,
        attributes: &[AttributeSpec::new(
            "targetIdListAddress",
            "BlackboardAddressAttribute",
            AttributeKind::BlackboardAddress,
        )
        .optional()],
        factory: |_| Box::new(crate::monitors::ballparks::MonitorInvulnerabilityCanceledInBubble::for_group()),
    },
    TaskEntry {
        class_name: "behaviors.monitors.ballparks.ClearBlackboardChannelOnItemExplosion",
        kind: TaskKind::Monitor,
        short_description: None,
        detailed_description: None,
        attributes: &[
            AttributeSpec::new(
                "itemIdAddress",
                "BlackboardAddressAttribute",
                AttributeKind::BlackboardAddress,
            )
            .index(0.0),
            AttributeSpec::new(
                "blackBoardChannelAddress",
                "BlackboardAddressAttribute",
                AttributeKind::BlackboardAddress,
            )
            .index(1.0),
        ],
        factory: |_| Box::new(crate::monitors::ballparks::ClearBlackboardChannelOnItemExplosion::default()),
    },
    TaskEntry {
        class_name: "behaviors.monitors.blackboards.BlackboardMessageMonitor",
        kind: TaskKind::Monitor,
        short_description: None,
        detailed_description: None,
        attributes: &[AttributeSpec::new(
            "messageAddress",
            "BlackboardAddressAttribute",
            AttributeKind::BlackboardAddress,
        )],
        factory: |_| Box::new(crate::monitors::blackboards::BlackboardMessageMonitor::default()),
    },
    TaskEntry {
        class_name: "behaviors.monitors.blackboards.BlackboardMessageMonitorBlocking",
        kind: TaskKind::Monitor,
        short_description: None,
        detailed_description: None,
        attributes: &[AttributeSpec::new(
            "messageAddress",
            "BlackboardAddressAttribute",
            AttributeKind::BlackboardAddress,
        )],
        factory: |_| Box::new(crate::monitors::blackboards::BlackboardMessageMonitorBlocking::default()),
    },
    TaskEntry {
        class_name: "behaviors.monitors.blackboards.BlackboardFlagOnMessageUpdate",
        kind: TaskKind::Monitor,
        short_description: None,
        detailed_description: None,
        attributes: &[
            AttributeSpec::new(
                "messageAddress",
                "BlackboardAddressAttribute",
                AttributeKind::BlackboardAddress,
            ),
            AttributeSpec::new("flagAddress", "BlackboardAddressAttribute", AttributeKind::BlackboardAddress),
        ],
        factory: |_| Box::new(crate::monitors::blackboards::BlackboardFlagOnMessageUpdate::default()),
    },
    TaskEntry {
        class_name: "behaviors.monitors.combat.WaitForAggressiveAct",
        kind: TaskKind::Monitor,
        short_description: None,
        detailed_description: None,
        attributes: &[AttributeSpec::new(
            "combatTargetsAddress",
            "BlackboardAddressAttribute",
            AttributeKind::BlackboardAddress,
        )],
        factory: |_| Box::new(crate::monitors::combat::WaitForAggressiveAct::default()),
    },
    TaskEntry {
        class_name: "behaviors.monitors.combat.SetFlagAndResetWhenAggressed",
        kind: TaskKind::Monitor,
        short_description: None,
        detailed_description: None,
        attributes: &[
            AttributeSpec::new("flagAddress", "BlackboardAddressAttribute", AttributeKind::BlackboardAddress),
            AttributeSpec::new("onlyPlayerAggressors", "BooleanAttribute", AttributeKind::Plain)
                .index(3.0)
                .optional()
                .default(DefaultValue::Bool(false)),
        ],
        factory: |_| {
            Box::new(crate::monitors::combat::WaitForAggressiveAct::new(
                crate::monitors::combat::Aggression::SetFlagAndReset,
            ))
        },
    },
    TaskEntry {
        class_name: "behaviors.monitors.combat.FlagAggressionAndReportMyPosition",
        kind: TaskKind::Monitor,
        short_description: None,
        detailed_description: None,
        attributes: &[
            AttributeSpec::new(
                "combatTargetsAddress",
                "BlackboardAddressAttribute",
                AttributeKind::BlackboardAddress,
            )
            .index(0.0),
            AttributeSpec::new("flagAddress", "BlackboardAddressAttribute", AttributeKind::BlackboardAddress)
                .index(1.0),
            AttributeSpec::new(
                "myPositionAddress",
                "BlackboardAddressAttribute",
                AttributeKind::BlackboardAddress,
            )
            .index(2.0),
            AttributeSpec::new("onlyPlayerAggressors", "BooleanAttribute", AttributeKind::Plain)
                .index(3.0)
                .optional()
                .default(DefaultValue::Bool(false)),
        ],
        factory: |_| {
            Box::new(crate::monitors::combat::WaitForAggressiveAct::new(
                crate::monitors::combat::Aggression::FlagAndReportPosition,
            ))
        },
    },
    TaskEntry {
        class_name: "behaviors.monitors.combat.ClearValueAndResetIfTargetCloaks",
        kind: TaskKind::Monitor,
        short_description: None,
        detailed_description: None,
        attributes: &[
            AttributeSpec::new(
                "targetAddress",
                "BlackboardAddressAttribute",
                AttributeKind::BlackboardAddress,
            ),
            AttributeSpec::new(
                "valueAddress",
                "BlackboardAddressAttribute",
                AttributeKind::BlackboardAddress,
            ),
        ],
        factory: |_| Box::new(crate::monitors::combat::ClearValueAndResetIfTargetCloaks::default()),
    },
    TaskEntry {
        class_name: "behaviors.monitors.combat.MaxLockedTargetsChangedMonitor",
        kind: TaskKind::Monitor,
        short_description: None,
        detailed_description: None,
        attributes: &[],
        factory: |_| Box::new(crate::monitors::combat::MaxLockedTargetsChangedMonitor::default()),
    },
    TaskEntry {
        class_name: "behaviors.monitors.combat.MonitorAssistanceToTargets",
        kind: TaskKind::Monitor,
        short_description: None,
        detailed_description: None,
        attributes: &[AttributeSpec::new(
            "combatTargetsAddress",
            "BlackboardAddressAttribute",
            AttributeKind::BlackboardAddress,
        )],
        factory: |_| {
            Box::new(crate::monitors::combat::WaitForAggressiveAct::new(
                crate::monitors::combat::Aggression::AssistanceToTargets,
            ))
        },
    },
    TaskEntry {
        class_name: "behaviors.monitors.combat.MonitorAssistanceToGroupTargets",
        kind: TaskKind::Monitor,
        short_description: None,
        detailed_description: None,
        attributes: &[AttributeSpec::new(
            "combatTargetsAddress",
            "BlackboardAddressAttribute",
            AttributeKind::BlackboardAddress,
        )],
        factory: |_| {
            Box::new(crate::monitors::combat::WaitForAggressiveAct::new(
                crate::monitors::combat::Aggression::AssistanceToGroupTargets,
            ))
        },
    },
    TaskEntry {
        class_name: "behaviors.monitors.combat.MonitorAggressionTowardsTargets",
        kind: TaskKind::Monitor,
        short_description: Some("Monitors the bubble for aggression towards itemIDs read from blackboard."),
        detailed_description: Some(
            "Monitors aggression in the items bubble towards itemIDs specified in a blackboard address \"monitoredTargetsAddress\" and adding any aggressors to a collection in a different blackboard address \"combatTargetsAddress.\"",
        ),
        attributes: &[
            AttributeSpec::new(
                "monitoredTargetsAddress",
                "BlackboardAddressAttribute",
                AttributeKind::BlackboardAddress,
            ),
            AttributeSpec::new(
                "combatTargetsAddress",
                "BlackboardAddressAttribute",
                AttributeKind::BlackboardAddress,
            ),
        ],
        factory: |_| {
            Box::new(crate::monitors::combat::WaitForAggressiveAct::new(
                crate::monitors::combat::Aggression::AggressionTowardsTargets,
            ))
        },
    },
    TaskEntry {
        class_name: "behaviors.monitors.combat.MonitorAggressionTowardsGroupTargets",
        kind: TaskKind::Monitor,
        short_description: Some(
            "Monitors the bubbles of all group members for aggression towards itemIDs read from blackboard.",
        ),
        detailed_description: Some(
            "Monitors aggression in the bubbles of all group members towards itemIDs specified in a blackboard address \"monitoredTargetsAddress\" and adding any aggressors to a collection in a different blackboard address \"combatTargetsAddress.\"",
        ),
        attributes: &[
            AttributeSpec::new(
                "monitoredTargetsAddress",
                "BlackboardAddressAttribute",
                AttributeKind::BlackboardAddress,
            ),
            AttributeSpec::new(
                "combatTargetsAddress",
                "BlackboardAddressAttribute",
                AttributeKind::BlackboardAddress,
            ),
        ],
        factory: |_| {
            Box::new(crate::monitors::combat::WaitForAggressiveAct::new(
                crate::monitors::combat::Aggression::AggressionTowardsGroupTargets,
            ))
        },
    },
    TaskEntry {
        class_name: "behaviors.monitors.combat.MonitorSecurityStatusLoss",
        kind: TaskKind::Monitor,
        short_description: Some(
            "Monitors when a character loses sec status and adds them to the combat target",
        ),
        detailed_description: None,
        attributes: &[
            AttributeSpec::new(
                "combatTargetsAddress",
                "BlackboardAddressAttribute",
                AttributeKind::BlackboardAddress,
            )
            .index(0.0),
            AttributeSpec::new("maxDistance", "IntegerAttribute", AttributeKind::Plain)
                .index(1.0)
                .optional()
                .default(DefaultValue::None)
                .described("in m - if no maxDistance is set, it applies to the whole bubble"),
        ],
        factory: |_| Box::new(crate::monitors::combat::MonitorSecurityStatusLoss::default()),
    },
    TaskEntry {
        class_name: "behaviors.monitors.combattargets.CombatRangeMonitor",
        kind: TaskKind::Monitor,
        short_description: None,
        detailed_description: None,
        attributes: &[AttributeSpec::new(
            "combatRangeRecalculateAddress",
            "BlackboardAddressAttribute",
            AttributeKind::BlackboardAddress,
        )],
        factory: |_| Box::new(crate::monitors::combattargets::CombatRangeMonitor::default()),
    },
    TaskEntry {
        class_name: "behaviors.monitors.combattargets.MonitorTargetAdded",
        kind: TaskKind::Monitor,
        short_description: None,
        detailed_description: None,
        attributes: &[AttributeSpec::new(
            "targetAddress",
            "BlackboardAddressAttribute",
            AttributeKind::BlackboardAddress,
        )],
        factory: |_| Box::new(crate::monitors::combattargets::MonitorTargetAdded::default()),
    },
    TaskEntry {
        class_name: "behaviors.monitors.damage.ArmorDamageThresholdMonitor",
        kind: TaskKind::Monitor,
        short_description: None,
        detailed_description: None,
        attributes: &[
            AttributeSpec::new("armorRatioThreshold", "FloatAttribute", AttributeKind::Plain),
            AttributeSpec::new(
                "thresholdReachedAddress",
                "BlackboardAddressAttribute",
                AttributeKind::BlackboardAddress,
            ),
        ],
        factory: |_| Box::new(crate::monitors::damage::ArmorDamageThresholdMonitor::default()),
    },
    TaskEntry {
        class_name: "behaviors.monitors.dungeons.MonitorDungeonTimerSetBooleanAndReset",
        kind: TaskKind::Monitor,
        short_description: None,
        detailed_description: None,
        attributes: &[
            AttributeSpec::new("timerId", "IntegerAttribute", AttributeKind::Plain).index(1.0),
            AttributeSpec::new(
                "messageAddress",
                "BlackboardAddressAttribute",
                AttributeKind::BlackboardAddress,
            )
            .index(2.0),
        ],
        factory: |_| {
            Box::new(crate::monitors::MonitorTask(
                crate::monitors::dungeons::MonitorDungeonTimerSetBooleanAndReset::default(),
            ))
        },
    },
    TaskEntry {
        class_name: "behaviors.monitors.dungeons.DungeonCancelInvulnerableOnTrigger",
        kind: TaskKind::Monitor,
        short_description: None,
        detailed_description: None,
        attributes: &[
            AttributeSpec::new("triggerTypeId", "DungeonTriggerSelectAttribute", AttributeKind::Plain)
                .index(1.0),
            AttributeSpec::new("checkRoomScenario", "BooleanAttribute", AttributeKind::Plain).index(2.0),
            AttributeSpec::new("reason", "StringAttribute", AttributeKind::Plain).index(3.0),
        ],
        factory: |_| {
            Box::new(crate::monitors::MonitorTask(
                crate::monitors::dungeons::DungeonCancelInvulnerableOnTrigger::default(),
            ))
        },
    },
    TaskEntry {
        class_name: "behaviors.monitors.dungeons.DungeonSetBlackboardBooleanOnTriggerEvent",
        kind: TaskKind::Monitor,
        short_description: None,
        detailed_description: None,
        attributes: &[
            AttributeSpec::new(
                "triggerEventTypeId",
                "DungeonTriggerEventSelectAttribute",
                AttributeKind::Plain,
            )
            .index(1.0),
            AttributeSpec::new(
                "blackboardAddress",
                "BlackboardAddressAttribute",
                AttributeKind::BlackboardAddress,
            )
            .index(2.0),
            AttributeSpec::new("trueOrFalse", "BooleanAttribute", AttributeKind::Plain).index(3.0),
        ],
        factory: |_| {
            Box::new(crate::monitors::MonitorTask(
                crate::monitors::dungeons::DungeonSetBlackboardBooleanOnTriggerEvent::default(),
            ))
        },
    },
    TaskEntry {
        class_name: "behaviors.monitors.effects.EffectUsageMonitor",
        kind: TaskKind::Monitor,
        short_description: None,
        detailed_description: None,
        attributes: &[
            AttributeSpec::new("effectId", "DogmaEffectIdAttribute", AttributeKind::Plain),
            AttributeSpec::new(
                "shipIdSetAddress",
                "BlackboardAddressAttribute",
                AttributeKind::BlackboardAddress,
            ),
        ],
        factory: |_| Box::new(crate::monitors::effects::EffectUsageMonitor::default()),
    },
    TaskEntry {
        class_name: "behaviors.monitors.entities.AdditionalInventoryRemovalMonitor",
        kind: TaskKind::Monitor,
        short_description: None,
        detailed_description: None,
        attributes: &[],
        factory: |_| {
            Box::new(crate::monitors::MonitorTask(
                crate::monitors::entities::AdditionalInventoryRemovalMonitor::default(),
            ))
        },
    },
    TaskEntry {
        class_name: "behaviors.monitors.entities.InventoryChangeMonitor",
        kind: TaskKind::Monitor,
        short_description: None,
        detailed_description: None,
        attributes: &[],
        factory: |_| {
            Box::new(crate::monitors::MonitorTask(
                crate::monitors::entities::InventoryChangeMonitor::default(),
            ))
        },
    },
    TaskEntry {
        class_name: "behaviors.monitors.groups.GroupMembershipMonitor",
        kind: TaskKind::Monitor,
        short_description: None,
        detailed_description: None,
        attributes: &[],
        factory: |_| Box::new(crate::monitors::groups::GroupMembershipMonitor::default()),
    },
    TaskEntry {
        class_name: "behaviors.monitors.logistics.ManageRemoteRepairEffect",
        kind: TaskKind::Monitor,
        short_description: None,
        detailed_description: None,
        attributes: &[
            AttributeSpec::new(
                "repairEffectIdAddress",
                "BlackboardAddressAttribute",
                AttributeKind::BlackboardAddress,
            ),
            AttributeSpec::new(
                "prioritizedWatchlistAddress",
                "BlackboardAddressAttribute",
                AttributeKind::BlackboardAddress,
            ),
        ],
        factory: |_| Box::new(crate::monitors::logistics::ManageRemoteRepairEffect::default()),
    },
    TaskEntry {
        class_name: "behaviors.monitors.messages.SendMessageOnMessageMonitor",
        kind: TaskKind::Monitor,
        short_description: None,
        detailed_description: None,
        attributes: &[AttributeSpec::new(
            "monitoredMessageId",
            "MessageSelectAttribute",
            AttributeKind::Plain,
        )],
        factory: |_| {
            Box::new(crate::monitors::MonitorTask(
                crate::monitors::messages::SendMessageOnMessageMonitor::default(),
            ))
        },
    },
    TaskEntry {
        class_name: "behaviors.monitors.messages.SendMessageOnMessageWhenCargoIsFullMonitor",
        kind: TaskKind::Monitor,
        short_description: None,
        detailed_description: None,
        attributes: &[
            AttributeSpec::new("monitoredMessageId", "MessageSelectAttribute", AttributeKind::Plain)
                .index(1.0),
            AttributeSpec::new("notifiedMessageId", "MessageSelectAttribute", AttributeKind::Plain)
                .index(2.0),
        ],
        factory: |_| {
            Box::new(crate::monitors::MonitorTask(
                crate::monitors::messages::SendMessageOnMessageMonitor::when_cargo_is_full(),
            ))
        },
    },
    TaskEntry {
        class_name: "behaviors.monitors.navigation.GroupMemberWarpModeChangedMonitor",
        kind: TaskKind::Monitor,
        short_description: None,
        detailed_description: None,
        attributes: &[],
        factory: |_| Box::new(crate::monitors::navigation::GroupMemberWarpModeChangedMonitor::default()),
    },
    TaskEntry {
        class_name: "behaviors.monitors.reinforcements.AreReinforcementsAuthorizedMonitor",
        kind: TaskKind::Monitor,
        short_description: None,
        detailed_description: None,
        attributes: &[],
        factory: |_| Box::new(crate::monitors::reinforcements::AreReinforcementsAuthorizedMonitor::default()),
    },
    TaskEntry {
        class_name: "behaviors.monitors.reinforcements.AreReinforcementAvailableMonitor",
        kind: TaskKind::Monitor,
        short_description: None,
        detailed_description: None,
        attributes: &[],
        factory: |_| Box::new(crate::monitors::reinforcements::AreReinforcementAvailableMonitor::default()),
    },
    TaskEntry {
        class_name: "behaviors.monitors.standings.MonitorProximitySensorForGroupStanding",
        kind: TaskKind::Monitor,
        short_description: None,
        detailed_description: None,
        attributes: &[
            AttributeSpec::new("includedCategories", "InventoryCategorySetAttribute", AttributeKind::Plain)
                .index(0.0),
            AttributeSpec::new("excludedGroups", "InventoryGroupSetAttribute", AttributeKind::Plain)
                .index(1.0),
            AttributeSpec::new("validOwnerIds", "OwnerListAttribute", AttributeKind::Plain).index(2.0),
            AttributeSpec::new("invalidOwnerIds", "OwnerListAttribute", AttributeKind::Plain).index(3.0),
            AttributeSpec::new(
                "itemIdsWithHostileStandingsAddress",
                "BlackboardAddressAttribute",
                AttributeKind::BlackboardAddress,
            )
            .index(4.0)
            .optional(),
            AttributeSpec::new(
                "itemIdsWithNeutralStandingsAddress",
                "BlackboardAddressAttribute",
                AttributeKind::BlackboardAddress,
            )
            .index(5.0)
            .optional(),
            AttributeSpec::new(
                "itemIdsWithFriendlyStandingsAddress",
                "BlackboardAddressAttribute",
                AttributeKind::BlackboardAddress,
            )
            .index(6.0)
            .optional(),
            AttributeSpec::new(
                "flagHostileStandingsAddress",
                "BlackboardAddressAttribute",
                AttributeKind::BlackboardAddress,
            )
            .index(4.1)
            .optional(),
            AttributeSpec::new(
                "flagNeutralStandingsAddress",
                "BlackboardAddressAttribute",
                AttributeKind::BlackboardAddress,
            )
            .index(5.1)
            .optional(),
            AttributeSpec::new(
                "flagFriendlyStandingsAddress",
                "BlackboardAddressAttribute",
                AttributeKind::BlackboardAddress,
            )
            .index(6.1)
            .optional(),
            AttributeSpec::new(
                "lastPositionForHostileStandingsAddress",
                "BlackboardAddressAttribute",
                AttributeKind::BlackboardAddress,
            )
            .index(4.2)
            .optional(),
            AttributeSpec::new(
                "lastPositionForNeutralStandingsAddress",
                "BlackboardAddressAttribute",
                AttributeKind::BlackboardAddress,
            )
            .index(5.2)
            .optional(),
            AttributeSpec::new(
                "lastPositionForFriendlyStandingsAddress",
                "BlackboardAddressAttribute",
                AttributeKind::BlackboardAddress,
            )
            .index(6.2)
            .optional(),
        ],
        factory: |attributes| {
            Box::new(crate::monitors::standings::monitor_proximity_sensor_for_group_standing(attributes))
        },
    },
    TaskEntry {
        class_name: "behaviors.monitors.superweapon.WaitForTurboShieldState",
        kind: TaskKind::Monitor,
        short_description: None,
        detailed_description: None,
        attributes: &[
            AttributeSpec::new("turboShieldState", "IntegerAttribute", AttributeKind::Plain),
            AttributeSpec::new(
                "messageAddress",
                "BlackboardAddressAttribute",
                AttributeKind::BlackboardAddress,
            )
            .optional(),
        ],
        factory: |_| Box::new(crate::monitors::superweapon::WaitForTurboShieldState::default()),
    },
    TaskEntry {
        class_name: "behaviors.monitors.targeting.TargetLostMonitor",
        kind: TaskKind::Monitor,
        short_description: None,
        detailed_description: None,
        attributes: &[AttributeSpec::new(
            "targetAddress",
            "BlackboardAddressAttribute",
            AttributeKind::BlackboardAddress,
        )],
        factory: |_| Box::new(crate::monitors::targeting::TargetLostMonitor::default()),
    },
    TaskEntry {
        class_name: "behaviors.monitors.timers.ResetAfterTimeout",
        kind: TaskKind::Monitor,
        short_description: None,
        detailed_description: None,
        attributes: &[
            AttributeSpec::new(
                "timerAddress",
                "BlackboardAddressAttribute",
                AttributeKind::BlackboardAddress,
            ),
            AttributeSpec::new("timeoutSeconds", "IntegerAttribute", AttributeKind::Plain),
        ],
        factory: |_| Box::new(crate::monitors::timers::ResetAfterTimeout::default()),
    },
    TaskEntry {
        class_name: "behaviors.monitors.timers.ResetAfterTimeoutBlocks",
        kind: TaskKind::Monitor,
        short_description: None,
        detailed_description: None,
        attributes: &[
            AttributeSpec::new(
                "timerAddress",
                "BlackboardAddressAttribute",
                AttributeKind::BlackboardAddress,
            ),
            AttributeSpec::new("timeoutSeconds", "IntegerAttribute", AttributeKind::Plain),
        ],
        factory: |_| Box::new(crate::monitors::timers::ResetAfterTimeout::blocks()),
    },
    TaskEntry {
        class_name: "behaviors.monitors.timers.StartTimer",
        kind: TaskKind::Monitor,
        short_description: None,
        detailed_description: None,
        attributes: &[
            AttributeSpec::new(
                "timerAddress",
                "BlackboardAddressAttribute",
                AttributeKind::BlackboardAddress,
            ),
            AttributeSpec::new("timeoutSeconds", "IntegerAttribute", AttributeKind::Plain),
        ],
        factory: |_| Box::new(crate::monitors::timers::StartTimer::default()),
    },
    TaskEntry {
        class_name: "behaviors.monitors.timers.StartTimerBlocks",
        kind: TaskKind::Monitor,
        short_description: None,
        detailed_description: None,
        attributes: &[
            AttributeSpec::new(
                "timerAddress",
                "BlackboardAddressAttribute",
                AttributeKind::BlackboardAddress,
            ),
            AttributeSpec::new("timeoutSeconds", "IntegerAttribute", AttributeKind::Plain),
        ],
        factory: |_| Box::new(crate::monitors::timers::StartTimer::blocks()),
    },
    TaskEntry {
        class_name: "behaviors.monitors.timers.SetBooleanValueAndResetAfterTimeout",
        kind: TaskKind::Monitor,
        short_description: None,
        detailed_description: None,
        attributes: &[
            AttributeSpec::new("timeoutSeconds", "IntegerAttribute", AttributeKind::Plain).index(0.0),
            AttributeSpec::new(
                "timerAddress",
                "BlackboardAddressAttribute",
                AttributeKind::BlackboardAddress,
            )
            .index(1.0),
            AttributeSpec::new(
                "channelAddress",
                "BlackboardAddressAttribute",
                AttributeKind::BlackboardAddress,
            )
            .index(2.0),
            AttributeSpec::new("trueOrFalse", "BooleanAttribute", AttributeKind::Plain).index(3.0),
        ],
        factory: |_| Box::new(crate::monitors::timers::SetBooleanValueAndResetAfterTimeout::default()),
    },
];
