//! The tasks of `behaviors/registry/composites.py`: the classes the behavior trees of the FSD data name, with the attributes they take.

#![allow(clippy::default_constructed_unit_structs)]

use super::{AttributeKind, AttributeSpec, TaskEntry, TaskKind};

pub const COMPOSITES: &[TaskEntry] = &[
    TaskEntry {
        class_name: "behaviors.composites.Sequence",
        kind: TaskKind::Composite,
        short_description: Some("Execute each child task in a sequence."),
        detailed_description: Some(
            "Execute each child task in a sequence. If the first child task succeeds it proceeds to the next child task while they keep succeeding.  If all child task succeed the Sequence succeeds. If any child task fails the Sequence will fail and iteration stops.",
        ),
        attributes: &[],
        factory: |_| Box::new(crate::composites::Sequence::default()),
    },
    TaskEntry {
        class_name: "behaviors.composites.PrioritySelector",
        kind: TaskKind::Composite,
        short_description: Some("Execute child task until the one succeeds."),
        detailed_description: Some(
            "Execute child task from the first to last until one succeeds.  If a task succeeds we stop iterating and return success. If all child tasks fail the PrioritySelector fails. The order of the child tasks controls the priority of child tasks.",
        ),
        attributes: &[],
        factory: |_| Box::new(crate::composites::PrioritySelector::default()),
    },
    TaskEntry {
        class_name: "behaviors.composites.weighted.WeightedSelector",
        kind: TaskKind::Composite,
        short_description: Some("Return a weighted choice from the child tasks"),
        detailed_description: Some(
            "Will return a single randomly chosen child task based on weight parameters. The WeightedSelector succeeds or fails based on the chosen child task.",
        ),
        attributes: &[
            AttributeSpec::new("subTaskWeights", "FloatListAttribute", AttributeKind::Plain).index(0.0)
        ],
        factory: |_| Box::new(crate::composites::WeightedSelector::default()),
    },
];
