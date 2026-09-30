//! `behaviors/trees/scoutsidle.py`: what a scout does when there is nothing to fight: it scans what is about it, or waits at an anchor.

use crate::blackboards::{Address, ScopeType};
use crate::tree::BehaviorTree;
use crate::value::Value;

use super::builder::{Node, selector, sequence};
use super::patrol::have_a_valid_target;

pub use super::patrol::{ignored_groups, ignored_types};

/// `ANALYZE_OBJECT_TIMER`.
pub fn analyze_object_timer() -> Address {
    Address::new(ScopeType::Item, "ANALYZE_OBJECT_TIMER")
}

/// `ANALYZE_OBJECT_ID`.
pub fn analyze_object_id() -> Address {
    Address::new(ScopeType::Item, "ANALYZE_OBJECT_ID")
}

/// `ANALYZE_CONSIDER_LIST`.
pub fn analyze_consider_list() -> Address {
    Address::new(ScopeType::Item, "ANALYZE_CONSIDER_LIST")
}

pub const SCANNER_STRETCH_EFFECT: &str = "effects.SleeperScannerStretch";
pub const SCANNER_SHIP_EFFECT: &str = "effects.SleeperScannerShip";
pub const SCANNER_EFFECT_DURATION_MSEC: i64 = 10000;

/// `CreateIdleBehavior`.
pub fn create_idle_behavior() -> Node {
    selector().named("Idle").add(create_analyze_behavior()).add(create_idle_fallback())
}

/// `CreateAnalyzeBehavior`.
pub fn create_analyze_behavior() -> Node {
    let select_target_action = |scan_distance: f64| {
        Node::class("SelectTargetToAnalyzeAction")
            .with("selectedTargetAddress", analyze_object_id())
            .with("maxDistance", scan_distance)
            .with("ignoredCategories", Value::List(Vec::new()))
            .with("ignoredGroups", ignored_groups())
            .with("ignoredTypes", ignored_types())
            .with("shouldIgnoreMobileTargets", false)
    };
    let scan_distance = 75_000.0;
    let select_target_to_analyze = sequence()
        .named("Select Analyze Target")
        .add(
            Node::class("StartTimerBlocks")
                .with("timeoutSeconds", 30.0)
                .with("timerAddress", analyze_object_timer()),
        )
        .add(select_target_action(scan_distance));
    let pick_object_to_analyze = selector()
        .named("Pick Object")
        .add(select_target_to_analyze)
        .add(have_a_valid_target(&analyze_object_id(), scan_distance))
        .add(select_target_action(scan_distance));
    sequence()
        .named("Analyze Objects")
        .add(Node::class("BubbleChangedMonitor"))
        .add(pick_object_to_analyze)
        .add(
            Node::class("OrbitAtDistanceAction")
                .with("orbitTargetAddress", analyze_object_id())
                .with("orbitRange", 4000.0)
                .with("blocking", true)
                .with("blockUntilRange", 7000),
        )
        .add(
            Node::class("PlayOneShotStretchEffectAction")
                .with("effectDuration", SCANNER_EFFECT_DURATION_MSEC)
                .with("effectName", SCANNER_STRETCH_EFFECT)
                .with("effectTargetAddress", analyze_object_id()),
        )
        .add(
            Node::class("PlayOneShotTargetedShipEffectAction")
                .with("effectDuration", SCANNER_EFFECT_DURATION_MSEC)
                .with("effectName", SCANNER_SHIP_EFFECT)
                .with("effectTargetAddress", analyze_object_id()),
        )
        .add(
            Node::class("SucceedAfterTimeoutAction")
                .with("timeoutSeconds", SCANNER_EFFECT_DURATION_MSEC as f64 / 1000.0),
        )
}

/// `CreateIdleFallback`: the client writes it twice, in `patrol.py` and here, the same.
pub fn create_idle_fallback() -> Node {
    super::patrol::create_idle_fallback()
}

/// `CreateAnalyzeTree`.
pub fn create_analyze_tree() -> BehaviorTree {
    create_analyze_behavior().into_tree()
}
