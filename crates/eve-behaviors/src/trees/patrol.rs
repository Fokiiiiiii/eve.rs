//! `behaviors/trees/patrol.py`: an entity that looks at what is about it, and goes to the observation point of the group when there is nothing.

use crate::blackboards::{Address, ScopeType};
use crate::consts::*;
use crate::value::Value;

use super::builder::{Node, int_set, selector, sequence};

/// `WARP_TO_LOCATION_GROUP`.
pub fn warp_to_location_group() -> Address {
    Address::new(ScopeType::EntityGroup, "WARP_TO_LOCATION_GROUP")
}

/// `PATROL_OBJECT_TIMER`.
pub fn patrol_object_timer() -> Address {
    Address::new(ScopeType::EntityGroup, "PATROL_OBJECT_TIMER")
}

/// `PATROL_OBJECT_ID`.
pub fn patrol_object_id() -> Address {
    Address::new(ScopeType::EntityGroup, "PATROL_OBJECT_ID")
}

/// `PATROL_CONSIDER_LIST`.
pub fn patrol_consider_list() -> Address {
    Address::new(ScopeType::EntityGroup, "PATROL_CONSIDER_LIST")
}

/// `IGNORED_CATEGORIES`.
pub fn ignored_categories() -> Value {
    int_set(&[CATEGORY_DRONE, CATEGORY_SHIP])
}

/// `IGNORED_GROUPS`.
pub fn ignored_groups() -> Value {
    int_set(&[
        GROUP_DEADSPACE_SLEEPER_AWAKENED_DEFENDER,
        GROUP_DEADSPACE_SLEEPER_SLEEPLESS_PATROLLER,
        GROUP_DEADSPACE_SLEEPER_SLEEPLESS_SENTINEL,
        GROUP_DEADSPACE_SLEEPER_SLEEPLESS_DEFENDER,
        GROUP_DEADSPACE_SLEEPER_AWAKENED_PATROLLER,
        GROUP_DEADSPACE_SLEEPER_AWAKENED_SENTINEL,
        GROUP_DEADSPACE_SLEEPER_AWAKENED_DEFENDER,
        GROUP_DEADSPACE_SLEEPER_EMERGENT_PATROLLER,
        GROUP_DEADSPACE_SLEEPER_EMERGENT_SENTINEL,
        GROUP_DEADSPACE_SLEEPER_EMERGENT_DEFENDER,
        GROUP_ROAMING_SLEEPERS_CRUISER,
        GROUP_DRIFTER_BATTLESHIP,
        GROUP_STATION,
        GROUP_CLOUD,
        GROUP_HARVESTABLE_CLOUD,
        GROUP_PLANETARY_CLOUD,
        GROUP_TEMPORARY_CLOUD,
        GROUP_WARP_DISRUPTION_PROBE,
        GROUP_BEACON,
        GROUP_INVISIBLE_BEACON,
    ])
}

/// `TYPE_UNIDENTIFIED_WH`.
pub const TYPE_UNIDENTIFIED_WH: i64 = 34494;

/// `IGNORED_TYPES`.
pub fn ignored_types() -> Value {
    int_set(&[TYPE_UNIDENTIFIED_WH])
}

/// `IDLE_ANCHOR`.
pub fn idle_anchor() -> Address {
    Address::new(ScopeType::EntityGroup, "IDLE_ANCHOR")
}

/// `IDLE_GOTO_POINT`.
pub fn idle_goto_point() -> Address {
    Address::new(ScopeType::EntityGroup, "IDLE_GOTO_POINT")
}

/// `CreateIdlePatrolBehavior`.
pub fn create_idle_patrol_behavior() -> Node {
    selector()
        .named("Idle")
        .add(create_patrol_behavior(100_000.0, 10_000.0, 90.0))
        .add(create_idle_fallback())
}

/// `HaveAValidTarget(targetAddress, maxDistance)`.
pub fn have_a_valid_target(target_address: &Address, max_distance: f64) -> Node {
    sequence()
        .named("Has Valid Target?")
        .add(Node::class("BlackboardMessageMonitorBlocking").with("messageAddress", target_address))
        .add(Node::class("IsBallPresentInMyBubbleCondition").with("ballIdAddress", target_address))
        .add(
            Node::class("IsBallWithInDistanceCondition")
                .with("ballIdAddress", target_address)
                .with("maxDistance", max_distance),
        )
}

/// `CreatePatrolBehavior(patrolDistance, orbitRange, patrolInterval)`.
pub fn create_patrol_behavior(patrol_distance: f64, orbit_range: f64, patrol_interval: f64) -> Node {
    let select_target_action = |scan_distance: f64| {
        Node::class("SelectTargetToAnalyzeAction")
            .with("selectedTargetAddress", patrol_object_id())
            .with("maxDistance", scan_distance)
            .with("ignoredCategories", ignored_categories())
            .with("ignoredGroups", ignored_groups())
            .with("ignoredTypes", ignored_types())
            .with("shouldIgnoreMobileTargets", true)
    };
    let select_target_to_analyze = |scan_distance: f64| {
        sequence()
            .named("Select Patrol Target")
            .add(
                Node::class("StartTimerBlocks")
                    .with("timeoutSeconds", patrol_interval)
                    .with("timerAddress", patrol_object_timer()),
            )
            .add(select_target_action(scan_distance))
    };
    let pick_object_to_patrol = selector()
        .named("Pick Object to Patrol")
        .add(select_target_to_analyze(patrol_distance))
        .add(have_a_valid_target(&patrol_object_id(), patrol_distance))
        .add(select_target_action(patrol_distance));
    sequence()
        .named("Patrol Area")
        .add(Node::class("BubbleChangedMonitor"))
        .add(pick_object_to_patrol)
        .add(Node::class("BlackboardMessageMonitor").with("messageAddress", patrol_object_timer()))
        .add(Node::class("BlackboardMessageMonitor").with("messageAddress", patrol_object_id()))
        .add(
            Node::class("OrbitAtDistanceAction")
                .with("orbitTargetAddress", patrol_object_id())
                .with("orbitRange", orbit_range)
                .with("blocking", true)
                .with("blockUntilRange", orbit_range * 1.5),
        )
        .add(Node::class("WaitAction").named("Patrolling object"))
}

/// `CreateIdleFallback`.
pub fn create_idle_fallback() -> Node {
    let valid_point = sequence()
        .add(Node::class("BlackboardMessageMonitorBlocking").with("messageAddress", idle_goto_point()))
        .add(Node::class("IsCoordinateInSameBubbleCondition").with("coordinateAddress", idle_goto_point()));
    let select_point = selector().named("Select Observation Point").add(valid_point).add(
        Node::class("SelectCoordinateNearCoordinateAction")
            .with("sourceCoordinateAddress", warp_to_location_group())
            .with("destinationCoordinateAddress", idle_goto_point())
            .with("minDistance", 10000.0)
            .with("maxDistance", 20000.0),
    );
    let pick_anchor = selector()
        .named("Pick Anchor")
        .add(have_a_valid_target(&idle_anchor(), 40000.0))
        .add(Node::class("BlackboardSendMyItemIdMessageAction").with("messageAddress", idle_anchor()));
    let anchor = sequence()
        .named("Anchor")
        .add(
            Node::class("BlackboardMessageAndMyItemIdEqualCondition")
                .named("Am I Anchor?")
                .with("messageAddress", idle_anchor()),
        )
        .add(Node::class("FullStopAction"))
        .add(Node::class("WaitAction"));
    let orbit_by_default = sequence()
        .named("Orbit By Default")
        .add(
            Node::class("OrbitAtDistanceAction")
                .with("orbitTargetAddress", idle_anchor())
                .with("orbitRange", 3000.0)
                .with("blocking", false)
                .with("blockUntilRange", Value::None),
        )
        .add(Node::class("WaitAction"));
    let anchor_or_orbit = selector().named("Anchor or Orbit").add(anchor).add(orbit_by_default);
    sequence()
        .named("Idle Fallback")
        .add(select_point)
        .add(
            Node::class("ApproachCoordinatesAction")
                .with("coordinateAddress", idle_goto_point())
                .with("notifyRange", 1000.0)
                .with("blocking", true),
        )
        .add(pick_anchor)
        .add(anchor_or_orbit)
}
