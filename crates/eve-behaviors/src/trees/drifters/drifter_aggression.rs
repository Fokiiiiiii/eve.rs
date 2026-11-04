//! `behaviors/trees/drifters/drifter_aggression.py`.

use crate::consts::EFFECT_ENTOSIS_LINK;

use crate::trees::builder::Node;
use crate::trees::combat::combat_targets_set;

/// `CreateDrifterEntosisAggressionBehavior`: a ship that uses an entosis link is an aggressor.
pub fn create_drifter_entosis_aggression_behavior() -> Node {
    Node::class("EffectUsageMonitor")
        .with("shipIdSetAddress", combat_targets_set())
        .with("effectId", EFFECT_ENTOSIS_LINK)
}
