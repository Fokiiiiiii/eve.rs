//! The tasks of `behaviors/registry/decorators.py`: the classes the behavior trees of the FSD data name, with the attributes they take.

#![allow(clippy::default_constructed_unit_structs)]

use super::{AttributeKind, AttributeSpec, TaskEntry, TaskKind};

pub const DECORATORS: &[TaskEntry] = &[
    TaskEntry {
        class_name: "behaviors.decorators.modifiers.ForceFailure",
        kind: TaskKind::Decorator,
        short_description: None,
        detailed_description: None,
        attributes: &[],
        factory: |_| Box::new(crate::decorators::ForceFailure::default()),
    },
    TaskEntry {
        class_name: "behaviors.decorators.modifiers.ForceSuccess",
        kind: TaskKind::Decorator,
        short_description: None,
        detailed_description: None,
        attributes: &[],
        factory: |_| Box::new(crate::decorators::ForceSuccess::default()),
    },
    TaskEntry {
        class_name: "behaviors.decorators.modifiers.Not",
        kind: TaskKind::Decorator,
        short_description: None,
        detailed_description: None,
        attributes: &[],
        factory: |_| Box::new(crate::decorators::Not::default()),
    },
    TaskEntry {
        class_name: "behaviors.decorators.modifiers.Uninterruptible",
        kind: TaskKind::Decorator,
        short_description: None,
        detailed_description: None,
        attributes: &[],
        factory: |_| Box::new(crate::decorators::Uninterruptible::default()),
    },
    TaskEntry {
        class_name: "behaviors.decorators.modifiers.Invulnerable",
        kind: TaskKind::Decorator,
        short_description: None,
        detailed_description: None,
        attributes: &[AttributeSpec::new("reason", "StringAttribute", AttributeKind::Plain)],
        factory: |_| Box::new(crate::decorators::Invulnerable::default()),
    },
    TaskEntry {
        class_name: "behaviors.decorators.repeaters.RepeatWhileFailing",
        kind: TaskKind::Decorator,
        short_description: None,
        detailed_description: None,
        attributes: &[AttributeSpec::new("maxRepetitions", "IntegerAttribute", AttributeKind::Plain)],
        factory: |_| Box::new(crate::decorators::RepeatWhileFailing::default()),
    },
    TaskEntry {
        class_name: "behaviors.decorators.timers.CooldownTimer",
        kind: TaskKind::Decorator,
        short_description: Some(
            "Prevents access to sub-behavior again util after a cooldown time has passed.",
        ),
        detailed_description: Some(
            "If the timer is in active the task will evaluate the child task and activate the timer, timing out after [timeoutSeconds]. The task will return what ever state the child task returns. If the timer is active the task will fail and not evaluate the child task. The timer can be reset by setting the [timerAddress] to None, the original timer will still reset the tree when it times out.  The cool-down can also be blocked if [timerAddress] is set to a 'bad' value while the timer is inactive, otherwise the original timer will reset the cool-down again.",
        ),
        attributes: &[
            AttributeSpec::new(
                "timerAddress",
                "BlackboardAddressAttribute",
                AttributeKind::BlackboardAddress,
            ),
            AttributeSpec::new("timeoutSeconds", "IntegerAttribute", AttributeKind::Plain),
        ],
        factory: |_| Box::new(crate::decorators::CooldownTimer::default()),
    },
    TaskEntry {
        class_name: "behaviors.decorators.timers.RandomCooldownTimer",
        kind: TaskKind::Decorator,
        short_description: Some(
            "Prevents access to sub-behavior again util after a randomized cooldown time has passed.",
        ),
        detailed_description: Some(
            "If the timer is in active the task will evaluate the child task and activate the timer, timing out after a random time between [minTimeoutSeconds] and [maxTimeoutSeconds]. The task will return what ever state the child task returns. If the timer is active the task will fail and not evaluate the child task. The timer can be reset by setting the [timerAddress] to None, the original timer will still reset the tree when it times out.  The cool-down can also be blocked if [timerAddress] is set to a 'bad' value while the timer is inactive, otherwise the original timer will reset the cool-down again.",
        ),
        attributes: &[
            AttributeSpec::new(
                "timerAddress",
                "BlackboardAddressAttribute",
                AttributeKind::BlackboardAddress,
            )
            .index(0.0),
            AttributeSpec::new("minTimeoutSeconds", "IntegerAttribute", AttributeKind::Plain).index(1.0),
            AttributeSpec::new("maxTimeoutSeconds", "IntegerAttribute", AttributeKind::Plain).index(2.0),
        ],
        factory: |_| Box::new(crate::decorators::RandomCooldownTimer::default()),
    },
    TaskEntry {
        class_name: "behaviors.decorators.modifiers.Cloaked",
        kind: TaskKind::Decorator,
        short_description: None,
        detailed_description: None,
        attributes: &[],
        factory: |_| Box::new(crate::decorators::Cloaked::default()),
    },
    TaskEntry {
        class_name: "behaviors.decorators.modifiers.OneShot",
        kind: TaskKind::Decorator,
        short_description: Some("Makes sure the decorated sub-tree is only run once."),
        detailed_description: Some(
            "Make a sub-behavior single shot by marking that it has already executed on the blackboard. This is very useful to for initialization that should only take place once for the lifetime of the entity.",
        ),
        attributes: &[AttributeSpec::new(
            "oneShotTriggeredFlagAddress",
            "BlackboardAddressAttribute",
            AttributeKind::BlackboardAddress,
        )],
        factory: |_| Box::new(crate::decorators::OneShot::default()),
    },
];
