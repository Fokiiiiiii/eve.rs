//! The tasks of `behaviors/registry/conditions.py`: the classes the behavior trees of the FSD data name, with the attributes they take.

#![allow(clippy::default_constructed_unit_structs)]

use super::{AttributeKind, AttributeSpec, DefaultValue, TaskEntry, TaskKind};

pub const CONDITIONS: &[TaskEntry] = &[
    TaskEntry {
        class_name: "behaviors.conditions.awareness.AreClusterTargetsUsable",
        kind: TaskKind::Condition,
        short_description: None,
        detailed_description: None,
        attributes: &[
            AttributeSpec::new(
                "clusterTargetSetAddress",
                "BlackboardAddressAttribute",
                AttributeKind::BlackboardAddress,
            )
            .index(0.0),
            AttributeSpec::new(
                "guardObjectIdAddress",
                "BlackboardAddressAttribute",
                AttributeKind::BlackboardAddress,
            )
            .index(1.0),
        ],
        factory: |_| Box::new(crate::conditions::awareness::AreClusterTargetsUsable::default()),
    },
    TaskEntry {
        class_name: "behaviors.conditions.ballparks.IsCoordinateInSameBubbleCondition",
        kind: TaskKind::Condition,
        short_description: None,
        detailed_description: None,
        attributes: &[AttributeSpec::new(
            "coordinateAddress",
            "BlackboardAddressAttribute",
            AttributeKind::BlackboardAddress,
        )],
        factory: |_| Box::new(crate::conditions::ballparks::IsCoordinateInSameBubbleCondition::default()),
    },
    TaskEntry {
        class_name: "behaviors.conditions.ballparks.IsCoordinateWithinDistance",
        kind: TaskKind::Condition,
        short_description: None,
        detailed_description: None,
        attributes: &[
            AttributeSpec::new(
                "coordinateAddress",
                "BlackboardAddressAttribute",
                AttributeKind::BlackboardAddress,
            ),
            AttributeSpec::new("maxDistance", "FloatAttribute", AttributeKind::Plain),
        ],
        factory: |_| Box::new(crate::conditions::ballparks::IsCoordinateWithinDistance::default()),
    },
    TaskEntry {
        class_name: "behaviors.conditions.ballparks.IsCoordinateWithinDistanceByAddress",
        kind: TaskKind::Condition,
        short_description: None,
        detailed_description: None,
        attributes: &[
            AttributeSpec::new(
                "coordinateAddress",
                "BlackboardAddressAttribute",
                AttributeKind::BlackboardAddress,
            ),
            AttributeSpec::new(
                "maxDistanceAddress",
                "BlackboardAddressAttribute",
                AttributeKind::BlackboardAddress,
            ),
            AttributeSpec::new(
                "fudgeDistanceAddress",
                "BlackboardAddressAttribute",
                AttributeKind::BlackboardAddress,
            )
            .optional(),
        ],
        factory: |_| Box::new(crate::conditions::ballparks::IsCoordinateWithinDistanceByAddress::default()),
    },
    TaskEntry {
        class_name: "behaviors.conditions.ballparks.IsBallPresentInMyBubbleCondition",
        kind: TaskKind::Condition,
        short_description: None,
        detailed_description: None,
        attributes: &[AttributeSpec::new(
            "ballIdAddress",
            "BlackboardAddressAttribute",
            AttributeKind::BlackboardAddress,
        )],
        factory: |_| Box::new(crate::conditions::ballparks::IsBallPresentInMyBubbleCondition::default()),
    },
    TaskEntry {
        class_name: "behaviors.conditions.ballparks.IsBallPresentInBubbleByAddress",
        kind: TaskKind::Condition,
        short_description: None,
        detailed_description: None,
        attributes: &[
            AttributeSpec::new(
                "ballIdAddress",
                "BlackboardAddressAttribute",
                AttributeKind::BlackboardAddress,
            ),
            AttributeSpec::new(
                "bubbleIdAddress",
                "BlackboardAddressAttribute",
                AttributeKind::BlackboardAddress,
            ),
        ],
        factory: |_| Box::new(crate::conditions::ballparks::IsBallPresentInBubbleByAddress::default()),
    },
    TaskEntry {
        class_name: "behaviors.conditions.ballparks.IsBallWithInDistanceCondition",
        kind: TaskKind::Condition,
        short_description: None,
        detailed_description: None,
        attributes: &[
            AttributeSpec::new(
                "ballIdAddress",
                "BlackboardAddressAttribute",
                AttributeKind::BlackboardAddress,
            ),
            AttributeSpec::new("maxDistance", "FloatAttribute", AttributeKind::Plain),
        ],
        factory: |_| Box::new(crate::conditions::ballparks::IsBallWithInDistanceCondition::default()),
    },
    TaskEntry {
        class_name: "behaviors.conditions.ballparks.IsBallWithinDistanceOfBallCondition",
        kind: TaskKind::Condition,
        short_description: None,
        detailed_description: None,
        attributes: &[
            AttributeSpec::new(
                "ballIdAddress",
                "BlackboardAddressAttribute",
                AttributeKind::BlackboardAddress,
            ),
            AttributeSpec::new(
                "sourceBallIdAddress",
                "BlackboardAddressAttribute",
                AttributeKind::BlackboardAddress,
            ),
            AttributeSpec::new("maxDistance", "FloatAttribute", AttributeKind::Plain),
        ],
        factory: |_| Box::new(crate::conditions::ballparks::IsBallWithinDistanceOfBallCondition::default()),
    },
    TaskEntry {
        class_name: "behaviors.conditions.ballparks.IsOrbitingTargetCondition",
        kind: TaskKind::Condition,
        short_description: None,
        detailed_description: None,
        attributes: &[AttributeSpec::new(
            "targetAddress",
            "BlackboardAddressAttribute",
            AttributeKind::BlackboardAddress,
        )],
        factory: |_| Box::new(crate::conditions::ballparks::IsOrbitingTargetCondition::default()),
    },
    TaskEntry {
        class_name: "behaviors.conditions.ballparks.IsBallCloakedCondition",
        kind: TaskKind::Condition,
        short_description: None,
        detailed_description: None,
        attributes: &[AttributeSpec::new(
            "ballIdAddress",
            "BlackboardAddressAttribute",
            AttributeKind::BlackboardAddress,
        )],
        factory: |_| Box::new(crate::conditions::ballparks::IsBallCloakedCondition::default()),
    },
    TaskEntry {
        class_name: "behaviors.conditions.ballparks.IsBallInWarpCondition",
        kind: TaskKind::Condition,
        short_description: None,
        detailed_description: None,
        attributes: &[AttributeSpec::new(
            "ballIdAddress",
            "BlackboardAddressAttribute",
            AttributeKind::BlackboardAddress,
        )],
        factory: |_| Box::new(crate::conditions::ballparks::IsBallInWarpCondition::default()),
    },
    TaskEntry {
        class_name: "behaviors.conditions.ballparks.IsLocationWithinWarpDistance",
        kind: TaskKind::Condition,
        short_description: None,
        detailed_description: None,
        attributes: &[AttributeSpec::new(
            "coordinateAddress",
            "BlackboardAddressAttribute",
            AttributeKind::BlackboardAddress,
        )],
        factory: |_| Box::new(crate::conditions::ballparks::IsLocationWithinWarpDistance::default()),
    },
    TaskEntry {
        class_name: "behaviors.conditions.ballparks.IsBallWithinWarpDistance",
        kind: TaskKind::Condition,
        short_description: None,
        detailed_description: None,
        attributes: &[AttributeSpec::new(
            "ballIdAddress",
            "BlackboardAddressAttribute",
            AttributeKind::BlackboardAddress,
        )],
        factory: |_| Box::new(crate::conditions::ballparks::IsBallWithinWarpDistance::default()),
    },
    TaskEntry {
        class_name: "behaviors.conditions.ballparks.HasValidBallTarget",
        kind: TaskKind::Condition,
        short_description: None,
        detailed_description: None,
        attributes: &[AttributeSpec::new(
            "ballIdAddress",
            "BlackboardAddressAttribute",
            AttributeKind::BlackboardAddress,
        )],
        factory: |_| Box::new(crate::conditions::ballparks::HasValidBallTarget::default()),
    },
    TaskEntry {
        class_name: "behaviors.conditions.ballparks.AreItemsWithInDistanceCondition",
        kind: TaskKind::Condition,
        short_description: None,
        detailed_description: None,
        attributes: &[
            AttributeSpec::new(
                "itemIdAddress",
                "BlackboardAddressAttribute",
                AttributeKind::BlackboardAddress,
            ),
            AttributeSpec::new(
                "otherItemIdAddress",
                "BlackboardAddressAttribute",
                AttributeKind::BlackboardAddress,
            )
            .optional(),
            AttributeSpec::new(
                "maxDistanceAddress",
                "BlackboardAddressAttribute",
                AttributeKind::BlackboardAddress,
            ),
            AttributeSpec::new("maxDistanceMultiplier", "FloatAttribute", AttributeKind::Plain).optional(),
        ],
        factory: |_| Box::new(crate::conditions::ballparks::AreItemsWithInDistanceCondition::default()),
    },
    TaskEntry {
        class_name: "behaviors.conditions.ballparks.IsPathToCoordinatesClearOfBubbles",
        kind: TaskKind::Condition,
        short_description: None,
        detailed_description: None,
        attributes: &[AttributeSpec::new(
            "coordinateAddress",
            "BlackboardAddressAttribute",
            AttributeKind::BlackboardAddress,
        )],
        factory: |_| Box::new(crate::conditions::ballparks::IsPathToCoordinatesClearOfBubbles::default()),
    },
    TaskEntry {
        class_name: "behaviors.conditions.battlefield.IsFleetTypeEqualTo",
        kind: TaskKind::Condition,
        short_description: None,
        detailed_description: None,
        attributes: &[
            AttributeSpec::new("fleetType", "FleetTypeAttribute", AttributeKind::FleetType).index(0.0),
            AttributeSpec::new(
                "fleetTypeToCounterAddress",
                "BlackboardAddressAttribute",
                AttributeKind::BlackboardAddress,
            )
            .index(1.0),
        ],
        factory: |_| Box::new(crate::conditions::battlefield::IsFleetTypeEqualTo::default()),
    },
    TaskEntry {
        class_name: "behaviors.conditions.battlefield.IsAnyFleetMemberMemberAggressed",
        kind: TaskKind::Condition,
        short_description: None,
        detailed_description: None,
        attributes: &[AttributeSpec::new(
            "spawnPoolIdAddress",
            "BlackboardAddressAttribute",
            AttributeKind::BlackboardAddress,
        )
        .index(0.0)],
        factory: |_| Box::new(crate::conditions::battlefield::IsAnyFleetMemberMemberAggressed::default()),
    },
    TaskEntry {
        class_name: "behaviors.conditions.battlefield.IsPlayerFleetRelativeStrengthToHigh",
        kind: TaskKind::Condition,
        short_description: None,
        detailed_description: None,
        attributes: &[
            AttributeSpec::new(
                "playerItemIdsAddress",
                "BlackboardAddressAttribute",
                AttributeKind::BlackboardAddress,
            )
            .index(0.0),
            AttributeSpec::new("relativeStrengthThreshold", "FloatAttribute", AttributeKind::Plain)
                .index(1.0)
                .default(DefaultValue::Float(1.0)),
        ],
        factory: |_| Box::new(crate::conditions::battlefield::IsPlayerFleetRelativeStrengthToHigh::default()),
    },
    TaskEntry {
        class_name: "behaviors.conditions.blackboards.IsBlackboardValueNone",
        kind: TaskKind::Condition,
        short_description: None,
        detailed_description: None,
        attributes: &[AttributeSpec::new(
            "valueAddress",
            "BlackboardAddressAttribute",
            AttributeKind::BlackboardAddress,
        )],
        factory: |_| Box::new(crate::conditions::blackboards::IsBlackboardValueNone::default()),
    },
    TaskEntry {
        class_name: "behaviors.conditions.blackboards.IsBlackboardValueNotNone",
        kind: TaskKind::Condition,
        short_description: None,
        detailed_description: None,
        attributes: &[AttributeSpec::new(
            "valueAddress",
            "BlackboardAddressAttribute",
            AttributeKind::BlackboardAddress,
        )],
        factory: |_| Box::new(crate::conditions::blackboards::IsBlackboardValueNotNone::default()),
    },
    TaskEntry {
        class_name: "behaviors.conditions.blackboards.IsBlackboardValueTrue",
        kind: TaskKind::Condition,
        short_description: None,
        detailed_description: None,
        attributes: &[AttributeSpec::new(
            "valueAddress",
            "BlackboardAddressAttribute",
            AttributeKind::BlackboardAddress,
        )],
        factory: |_| Box::new(crate::conditions::blackboards::IsBlackboardValueTrue::default()),
    },
    TaskEntry {
        class_name: "behaviors.conditions.blackboards.IsBlackboardStringEqualToValue",
        kind: TaskKind::Condition,
        short_description: Some("Succeed if a blackboard value equals expected string"),
        detailed_description: Some(
            "Compares a value from blackboard identified by valueAddress to the string in expectedValue. Condition succeeds if they are the same or fails otherwise.",
        ),
        attributes: &[
            AttributeSpec::new(
                "valueAddress",
                "BlackboardAddressAttribute",
                AttributeKind::BlackboardAddress,
            )
            .index(0.0),
            AttributeSpec::new("expectedValue", "StringAttribute", AttributeKind::Plain).index(1.0),
        ],
        factory: |_| Box::new(crate::conditions::blackboards::IsBlackboardStringEqualToValue::default()),
    },
    TaskEntry {
        class_name: "behaviors.conditions.blackboards.IsBlackboardValueFalse",
        kind: TaskKind::Condition,
        short_description: None,
        detailed_description: None,
        attributes: &[AttributeSpec::new(
            "valueAddress",
            "BlackboardAddressAttribute",
            AttributeKind::BlackboardAddress,
        )],
        factory: |_| Box::new(crate::conditions::blackboards::IsBlackboardValueFalse::default()),
    },
    TaskEntry {
        class_name: "behaviors.conditions.blackboards.BlackboardMessagesEqualCondition",
        kind: TaskKind::Condition,
        short_description: None,
        detailed_description: None,
        attributes: &[
            AttributeSpec::new(
                "firstMessageAddress",
                "BlackboardAddressAttribute",
                AttributeKind::BlackboardAddress,
            ),
            AttributeSpec::new(
                "secondMessageAddress",
                "BlackboardAddressAttribute",
                AttributeKind::BlackboardAddress,
            ),
        ],
        factory: |_| Box::new(crate::conditions::blackboards::BlackboardMessagesEqualCondition::default()),
    },
    TaskEntry {
        class_name: "behaviors.conditions.blackboards.IsBlackboardCollectionEmpty",
        kind: TaskKind::Condition,
        short_description: None,
        detailed_description: None,
        attributes: &[AttributeSpec::new(
            "collectionAddress",
            "BlackboardAddressAttribute",
            AttributeKind::BlackboardAddress,
        )],
        factory: |_| Box::new(crate::conditions::blackboards::IsBlackboardCollectionEmpty::default()),
    },
    TaskEntry {
        class_name: "behaviors.conditions.blackboards.BlackboardValueUpdatedRecentlyCondition",
        kind: TaskKind::Condition,
        short_description: None,
        detailed_description: None,
        attributes: &[
            AttributeSpec::new("address", "BlackboardAddressAttribute", AttributeKind::BlackboardAddress),
            AttributeSpec::new("timeInSeconds", "IntegerAttribute", AttributeKind::Plain),
        ],
        factory: |_| {
            Box::new(crate::conditions::blackboards::BlackboardValueUpdatedRecentlyCondition::default())
        },
    },
    TaskEntry {
        class_name: "behaviors.conditions.blackboards.IsBlackboardValueInCollectionCondition",
        kind: TaskKind::Condition,
        short_description: None,
        detailed_description: None,
        attributes: &[
            AttributeSpec::new(
                "valueAddress",
                "BlackboardAddressAttribute",
                AttributeKind::BlackboardAddress,
            ),
            AttributeSpec::new(
                "collectionAddress",
                "BlackboardAddressAttribute",
                AttributeKind::BlackboardAddress,
            ),
        ],
        factory: |_| {
            Box::new(crate::conditions::blackboards::IsBlackboardValueInCollectionCondition::default())
        },
    },
    TaskEntry {
        class_name: "behaviors.conditions.blackboards.BlackboardMessageAndMyItemIdEqualCondition",
        kind: TaskKind::Condition,
        short_description: None,
        detailed_description: None,
        attributes: &[
            AttributeSpec::new(
                "messageAddress",
                "BlackboardAddressAttribute",
                AttributeKind::BlackboardAddress,
            ),
            AttributeSpec::new("isInverted", "BooleanAttribute", AttributeKind::Plain).optional(),
        ],
        factory: |_| {
            Box::new(crate::conditions::blackboards::BlackboardMessageAndMyItemIdEqualCondition::default())
        },
    },
    TaskEntry {
        class_name: "behaviors.conditions.blackboards.BlackboardMessageAndMyEntityGroupIdEqualCondition",
        kind: TaskKind::Condition,
        short_description: None,
        detailed_description: None,
        attributes: &[
            AttributeSpec::new(
                "messageAddress",
                "BlackboardAddressAttribute",
                AttributeKind::BlackboardAddress,
            ),
            AttributeSpec::new("isInverted", "BooleanAttribute", AttributeKind::Plain).optional(),
        ],
        factory: |_| {
            Box::new(
                crate::conditions::blackboards::BlackboardMessageAndMyEntityGroupIdEqualCondition::default(),
            )
        },
    },
    TaskEntry {
        class_name: "behaviors.conditions.blackboards.IsBlackboardValue3dVector",
        kind: TaskKind::Condition,
        short_description: None,
        detailed_description: None,
        attributes: &[AttributeSpec::new(
            "valueAddress",
            "BlackboardAddressAttribute",
            AttributeKind::BlackboardAddress,
        )],
        factory: |_| Box::new(crate::conditions::blackboards::IsBlackboardValue3dVector::default()),
    },
    TaskEntry {
        class_name: "behaviors.conditions.blackboards.IsBlackboardIntegerEqualToValue",
        kind: TaskKind::Condition,
        short_description: None,
        detailed_description: None,
        attributes: &[
            AttributeSpec::new(
                "valueAddress",
                "BlackboardAddressAttribute",
                AttributeKind::BlackboardAddress,
            ),
            AttributeSpec::new("value", "IntegerAttribute", AttributeKind::Plain),
        ],
        factory: |_| Box::new(crate::conditions::blackboards::IsBlackboardIntegerEqualToValue::default()),
    },
    TaskEntry {
        class_name: "behaviors.conditions.blackboards.IsBlackboardValueRatioLessThan",
        kind: TaskKind::Condition,
        short_description: None,
        detailed_description: None,
        attributes: &[
            AttributeSpec::new(
                "firstValueAddress",
                "BlackboardAddressAttribute",
                AttributeKind::BlackboardAddress,
            ),
            AttributeSpec::new(
                "secondValueAddress",
                "BlackboardAddressAttribute",
                AttributeKind::BlackboardAddress,
            ),
            AttributeSpec::new(
                "ratioAddress",
                "BlackboardAddressAttribute",
                AttributeKind::BlackboardAddress,
            ),
        ],
        factory: |_| Box::new(crate::conditions::blackboards::IsBlackboardValueRatioLessThan::default()),
    },
    TaskEntry {
        class_name: "behaviors.conditions.blackboards.BlackboardValueEqualsBlackboardValueInDictionaryCondition",
        kind: TaskKind::Condition,
        short_description: None,
        detailed_description: None,
        attributes: &[
            AttributeSpec::new(
                "valueAddress",
                "BlackboardAddressAttribute",
                AttributeKind::BlackboardAddress,
            ),
            AttributeSpec::new("keyAddress", "BlackboardAddressAttribute", AttributeKind::BlackboardAddress),
            AttributeSpec::new(
                "dictionaryAddress",
                "BlackboardAddressAttribute",
                AttributeKind::BlackboardAddress,
            ),
        ],
        factory: |_| {
            Box::new(crate::conditions::blackboards::BlackboardValueEqualsBlackboardValueInDictionaryCondition::default())
        },
    },
    TaskEntry {
        class_name: "behaviors.conditions.blackboards.IsBlackboardValueLessThenBlackboardValue",
        kind: TaskKind::Condition,
        short_description: None,
        detailed_description: None,
        attributes: &[
            AttributeSpec::new(
                "valueToCheckAddress",
                "BlackboardAddressAttribute",
                AttributeKind::BlackboardAddress,
            )
            .index(0.0),
            AttributeSpec::new(
                "valueToCompareAddress",
                "BlackboardAddressAttribute",
                AttributeKind::BlackboardAddress,
            )
            .index(1.0),
        ],
        factory: |_| {
            Box::new(crate::conditions::blackboards::IsBlackboardValueLessThenBlackboardValue::default())
        },
    },
    TaskEntry {
        class_name: "behaviors.conditions.blackboards.IsBlackboardDeleted",
        kind: TaskKind::Condition,
        short_description: Some("Check if the blackboard has been deleted"),
        detailed_description: Some(
            "This will check if the blackboard matching the scope type and the behavior context has been deleted.  This is relevant for instance if an entity can out live the scope. For example if an NPC can survive the deletion of a dungeon it's part of.",
        ),
        attributes: &[AttributeSpec::new("scopeType", "BlackboardScopeTypeAttribute", AttributeKind::Plain)
            .index(0.0)
            .described("The blackboard scope type to query")],
        factory: |_| Box::new(crate::conditions::blackboards::IsBlackboardDeleted::default()),
    },
    TaskEntry {
        class_name: "behaviors.conditions.combat.HasValidTarget",
        kind: TaskKind::Condition,
        short_description: None,
        detailed_description: None,
        attributes: &[AttributeSpec::new(
            "selectedTargetAddress",
            "BlackboardAddressAttribute",
            AttributeKind::BlackboardAddress,
        )],
        factory: |_| Box::new(crate::conditions::combat::HasValidTarget::default()),
    },
    TaskEntry {
        class_name: "behaviors.conditions.combat.HasAvailableTargetCapacity",
        kind: TaskKind::Condition,
        short_description: None,
        detailed_description: None,
        attributes: &[],
        factory: |_| Box::new(crate::conditions::combat::HasAvailableTargetCapacity::default()),
    },
    TaskEntry {
        class_name: "behaviors.conditions.combat.HasItemsFromCollectionWithinRange",
        kind: TaskKind::Condition,
        short_description: None,
        detailed_description: None,
        attributes: &[
            AttributeSpec::new(
                "itemIdCollectionAddress",
                "BlackboardAddressAttribute",
                AttributeKind::BlackboardAddress,
            ),
            AttributeSpec::new(
                "detectionRangeAddress",
                "BlackboardAddressAttribute",
                AttributeKind::BlackboardAddress,
            ),
            AttributeSpec::new("includeCloakedItems", "BooleanAttribute", AttributeKind::Plain),
        ],
        factory: |_| Box::new(crate::conditions::combat::HasItemsFromCollectionWithinRange::default()),
    },
    TaskEntry {
        class_name: "behaviors.conditions.combatmodules.IsModuleActive",
        kind: TaskKind::Condition,
        short_description: None,
        detailed_description: None,
        attributes: &[AttributeSpec::new(
            "moduleAddress",
            "BlackboardAddressAttribute",
            AttributeKind::BlackboardAddress,
        )],
        factory: |_| Box::new(crate::conditions::combatmodules::IsModuleActive::default()),
    },
    TaskEntry {
        class_name: "behaviors.conditions.combattargets.HasPendingTarget",
        kind: TaskKind::Condition,
        short_description: None,
        detailed_description: None,
        attributes: &[AttributeSpec::new(
            "targetAddress",
            "BlackboardAddressAttribute",
            AttributeKind::BlackboardAddress,
        )],
        factory: |_| Box::new(crate::conditions::combattargets::HasPendingTarget::default()),
    },
    TaskEntry {
        class_name: "behaviors.conditions.combattargets.AmIWithinMyEffectiveCombatRangeOfMyTarget",
        kind: TaskKind::Condition,
        short_description: None,
        detailed_description: None,
        attributes: &[
            AttributeSpec::new(
                "targetAddress",
                "BlackboardAddressAttribute",
                AttributeKind::BlackboardAddress,
            )
            .index(0.0),
            AttributeSpec::new(
                "orbitTargetAddress",
                "BlackboardAddressAttribute",
                AttributeKind::BlackboardAddress,
            )
            .index(1.0),
            AttributeSpec::new(
                "maxEffectiveCombatRangeAddress",
                "BlackboardAddressAttribute",
                AttributeKind::BlackboardAddress,
            )
            .index(2.0),
            AttributeSpec::new(
                "commanderAddress",
                "BlackboardAddressAttribute",
                AttributeKind::BlackboardAddress,
            )
            .index(3.0),
        ],
        factory: |_| {
            Box::new(crate::conditions::combattargets::AmIWithinMyEffectiveCombatRangeOfMyTarget::default())
        },
    },
    TaskEntry {
        class_name: "behaviors.conditions.combattargets.IsCoordinateWithinMyEffectiveCombatRange",
        kind: TaskKind::Condition,
        short_description: None,
        detailed_description: None,
        attributes: &[
            AttributeSpec::new(
                "coordinateAddress",
                "BlackboardAddressAttribute",
                AttributeKind::BlackboardAddress,
            )
            .index(0.0),
            AttributeSpec::new(
                "maxEffectiveCombatRangeAddress",
                "BlackboardAddressAttribute",
                AttributeKind::BlackboardAddress,
            )
            .index(1.0),
        ],
        factory: |_| {
            Box::new(crate::conditions::combattargets::IsCoordinateWithinMyEffectiveCombatRange::default())
        },
    },
    TaskEntry {
        class_name: "behaviors.conditions.combattargets.IsTargeting",
        kind: TaskKind::Condition,
        short_description: None,
        detailed_description: None,
        attributes: &[AttributeSpec::new(
            "targetAddress",
            "BlackboardAddressAttribute",
            AttributeKind::BlackboardAddress,
        )],
        factory: |_| Box::new(crate::conditions::combattargets::IsTargeting::default()),
    },
    TaskEntry {
        class_name: "behaviors.conditions.drones.CanControlDrones",
        kind: TaskKind::Condition,
        short_description: None,
        detailed_description: None,
        attributes: &[],
        factory: |_| Box::new(crate::conditions::drones::CanControlDrones::default()),
    },
    TaskEntry {
        class_name: "behaviors.conditions.drones.HasDroneBandwidth",
        kind: TaskKind::Condition,
        short_description: None,
        detailed_description: None,
        attributes: &[
            AttributeSpec::new(
                "droneGroupIdAddress",
                "BlackboardAddressAttribute",
                AttributeKind::BlackboardAddress,
            )
            .index(1.0),
            AttributeSpec::new(
                "maxDroneActiveCountAddress",
                "BlackboardAddressAttribute",
                AttributeKind::BlackboardAddress,
            )
            .index(2.0),
        ],
        factory: |_| Box::new(crate::conditions::drones::HasDroneBandwidth::default()),
    },
    TaskEntry {
        class_name: "behaviors.conditions.dungeons.DungeonIsMyDungeonSpawnAlive",
        kind: TaskKind::Condition,
        short_description: None,
        detailed_description: None,
        attributes: &[],
        factory: |_| Box::new(crate::conditions::dungeons::DungeonIsMyDungeonSpawnAlive::default()),
    },
    TaskEntry {
        class_name: "behaviors.conditions.effects.IsEffectActive",
        kind: TaskKind::Condition,
        short_description: None,
        detailed_description: None,
        attributes: &[AttributeSpec::new("effectId", "DogmaEffectIdAttribute", AttributeKind::Plain)],
        factory: |_| Box::new(crate::conditions::effects::IsEffectActive::default()),
    },
    TaskEntry {
        class_name: "behaviors.conditions.effects.IsEffectByAddressActive",
        kind: TaskKind::Condition,
        short_description: None,
        detailed_description: None,
        attributes: &[AttributeSpec::new(
            "effectIDAddress",
            "BlackboardAddressAttribute",
            AttributeKind::BlackboardAddress,
        )],
        factory: |_| Box::new(crate::conditions::effects::IsEffectByAddressActive::default()),
    },
    TaskEntry {
        class_name: "behaviors.conditions.effects.IsAnyEffectActive",
        kind: TaskKind::Condition,
        short_description: None,
        detailed_description: None,
        attributes: &[AttributeSpec::new("effectIds", "DogmaEffectIdSetAttribute", AttributeKind::Plain)],
        factory: |_| Box::new(crate::conditions::effects::IsAnyEffectActive::default()),
    },
    TaskEntry {
        class_name: "behaviors.conditions.effects.HasAnyEffectCondition",
        kind: TaskKind::Condition,
        short_description: None,
        detailed_description: None,
        attributes: &[AttributeSpec::new("effectIds", "DogmaEffectIdSetAttribute", AttributeKind::Plain)],
        factory: |_| Box::new(crate::conditions::effects::HasAnyEffectCondition::default()),
    },
    TaskEntry {
        class_name: "behaviors.conditions.entities.HasAnyOfTypesInEntityAdditionalLoot",
        kind: TaskKind::Condition,
        short_description: None,
        detailed_description: None,
        attributes: &[AttributeSpec::new("typeIds", "InventoryTypeSetAttribute", AttributeKind::Plain)],
        factory: |_| Box::new(crate::conditions::entities::HasAnyOfTypesInEntityAdditionalLoot::default()),
    },
    TaskEntry {
        class_name: "behaviors.conditions.entities.HasEnoughCargoForType",
        kind: TaskKind::Condition,
        short_description: None,
        detailed_description: None,
        attributes: &[AttributeSpec::new(
            "typeIdAddress",
            "BlackboardAddressAttribute",
            AttributeKind::BlackboardAddress,
        )],
        factory: |_| Box::new(crate::conditions::entities::HasEnoughCargoForType::default()),
    },
    TaskEntry {
        class_name: "behaviors.conditions.entities.IsCargoAboveCapacityThreshold",
        kind: TaskKind::Condition,
        short_description: None,
        detailed_description: None,
        attributes: &[AttributeSpec::new("cargoCapacityThreshold", "FloatAttribute", AttributeKind::Plain)],
        factory: |_| Box::new(crate::conditions::entities::IsCargoAboveCapacityThreshold::default()),
    },
    TaskEntry {
        class_name: "behaviors.conditions.entities.OwnerIsNpcCondition",
        kind: TaskKind::Condition,
        short_description: None,
        detailed_description: None,
        attributes: &[AttributeSpec::new(
            "ownerIdAddress",
            "BlackboardAddressAttribute",
            AttributeKind::BlackboardAddress,
        )],
        factory: |_| Box::new(crate::conditions::entities::OwnerIsNpcCondition::default()),
    },
    TaskEntry {
        class_name: "behaviors.conditions.entities.AmITheCommander",
        kind: TaskKind::Condition,
        short_description: None,
        detailed_description: None,
        attributes: &[],
        factory: |_| Box::new(crate::conditions::entities::AmITheCommander::default()),
    },
    TaskEntry {
        class_name: "behaviors.conditions.entities.IsMyCargoFull",
        kind: TaskKind::Condition,
        short_description: None,
        detailed_description: None,
        attributes: &[],
        factory: |_| Box::new(crate::conditions::entities::IsMyCargoFull::default()),
    },
    TaskEntry {
        class_name: "behaviors.conditions.entities.IsMyCargoEmpty",
        kind: TaskKind::Condition,
        short_description: None,
        detailed_description: None,
        attributes: &[],
        factory: |_| Box::new(crate::conditions::entities::IsMyCargoEmpty::default()),
    },
    TaskEntry {
        class_name: "behaviors.conditions.entityspawning.HasSpawnPointsGreaterThan",
        kind: TaskKind::Condition,
        short_description: None,
        detailed_description: None,
        attributes: &[
            AttributeSpec::new(
                "spawnPoolIdAddress",
                "BlackboardAddressAttribute",
                AttributeKind::BlackboardAddress,
            )
            .index(0.0),
            AttributeSpec::new("minSpawnPointAmount", "IntegerAttribute", AttributeKind::Plain).index(1.0),
        ],
        factory: |_| Box::new(crate::conditions::entityspawning::HasSpawnPointsGreaterThan::default()),
    },
    TaskEntry {
        class_name: "behaviors.conditions.group.IsMemberInEntityGroup",
        kind: TaskKind::Condition,
        short_description: None,
        detailed_description: None,
        attributes: &[AttributeSpec::new(
            "memberIdAddress",
            "BlackboardAddressAttribute",
            AttributeKind::BlackboardAddress,
        )],
        factory: |_| Box::new(crate::conditions::group::IsMemberInEntityGroup::default()),
    },
    TaskEntry {
        class_name: "behaviors.conditions.group.IsMemberTypeIdPartOfGroup",
        kind: TaskKind::Condition,
        short_description: None,
        detailed_description: None,
        attributes: &[AttributeSpec::new("typeId", "InventoryTypeAttribute", AttributeKind::Plain)],
        factory: |_| Box::new(crate::conditions::group::IsMemberTypeIdPartOfGroup::default()),
    },
    TaskEntry {
        class_name: "behaviors.conditions.group.IsOtherEntityGroupBlackboardValueTrue",
        kind: TaskKind::Condition,
        short_description: None,
        detailed_description: None,
        attributes: &[
            AttributeSpec::new(
                "memberIdAddress",
                "BlackboardAddressAttribute",
                AttributeKind::BlackboardAddress,
            ),
            AttributeSpec::new("messageName", "StringAttribute", AttributeKind::Plain),
        ],
        factory: |_| Box::new(crate::conditions::group::IsOtherEntityGroupBlackboardValueTrue::default()),
    },
    TaskEntry {
        class_name: "behaviors.conditions.group.IsAnyGroupMemberAggressed",
        kind: TaskKind::Condition,
        short_description: None,
        detailed_description: None,
        attributes: &[],
        factory: |_| Box::new(crate::conditions::group::IsAnyGroupMemberAggressed::default()),
    },
    TaskEntry {
        class_name: "behaviors.conditions.group.IsGroupInitialSpawningComplete",
        kind: TaskKind::Condition,
        short_description: None,
        detailed_description: None,
        attributes: &[],
        factory: |_| Box::new(crate::conditions::group::IsGroupInitialSpawningComplete::default()),
    },
    TaskEntry {
        class_name: "behaviors.conditions.randomized.RandomChanceCondition",
        kind: TaskKind::Condition,
        short_description: None,
        detailed_description: None,
        attributes: &[AttributeSpec::new("chance", "FloatAttribute", AttributeKind::Plain)],
        factory: |_| Box::new(crate::conditions::randomized::RandomChanceCondition::default()),
    },
    TaskEntry {
        class_name: "behaviors.conditions.randomized.CachedRandomChanceCondition",
        kind: TaskKind::Condition,
        short_description: None,
        detailed_description: None,
        attributes: &[
            AttributeSpec::new("chance", "FloatAttribute", AttributeKind::Plain),
            AttributeSpec::new(
                "randomValueAddress",
                "BlackboardAddressAttribute",
                AttributeKind::BlackboardAddress,
            ),
            AttributeSpec::new("cacheTimeInSeconds", "IntegerAttribute", AttributeKind::Plain),
        ],
        factory: |_| Box::new(crate::conditions::randomized::CachedRandomChanceCondition::default()),
    },
    TaskEntry {
        class_name: "behaviors.conditions.reinforcements.AreAnyReinforcementMembersAlive",
        kind: TaskKind::Condition,
        short_description: None,
        detailed_description: None,
        attributes: &[],
        factory: |_| Box::new(crate::conditions::reinforcements::AreAnyReinforcementMembersAlive::default()),
    },
    TaskEntry {
        class_name: "behaviors.conditions.structures.IsStructureVulnerable",
        kind: TaskKind::Condition,
        short_description: None,
        detailed_description: None,
        attributes: &[AttributeSpec::new(
            "structureIdAddress",
            "BlackboardAddressAttribute",
            AttributeKind::BlackboardAddress,
        )],
        factory: |_| Box::new(crate::conditions::structures::IsStructureVulnerable::default()),
    },
];
