//! The POS battery sentry (protocol 106): the policy names pilots, the battery locks the nearest one it can and fires at it through the holder's
//! fire rule, and a holder's battery is never the sentry's.

use super::tests::{A, B, BATTERY, SECOND, battery_placement, beside_a, crowd, obo_of, ship_id};
use super::*;
use crate::flight::Flight;
use crate::visibility::CloakState;

const TOWER: i64 = 1_060_000_001;
const OWNER_CORPORATION: i64 = 98_000_001;
const RIVAL_CORPORATION: i64 = 98_000_002;
const OWNER_ALLIANCE: i64 = 99_000_001;
const OTHER_TOWER: i64 = 1_060_000_002;

fn policy(standing: Option<f64>, security_status: Option<f64>) -> SentryPolicy {
    SentryPolicy {
        tower_id: TOWER,
        online: true,
        standing,
        security_status,
        war: false,
        high_security: false,
        owner_corporation_id: OWNER_CORPORATION,
        owner_alliance_id: 0,
    }
}

fn pilot(ship: i64, standing: f64) -> SentryPilot {
    SentryPilot {
        ship_id: ship,
        corporation_id: RIVAL_CORPORATION,
        alliance_id: 0,
        security_status: Some(0.0),
        standing,
        at_war: false,
    }
}

/// Pilots A and B (and more when asked), a battery beside A with no holder, bound to the tower and online.
fn sentry_scene(ships: usize) -> (Scene, i64) {
    let (mut scene, now) = crowd(ships);
    let at = beside_a(&scene, 1_000.0);
    scene.place_battery(battery_placement(at, 0, true)).expect("the battery is placed");
    scene.bind_sentry_battery(BATTERY, TOWER);
    (scene, now)
}

/// Two passes of the scene at `now` and well after it: the first locks, the second has the lock settle and the fire start.
fn settle_twice(scene: &mut Scene, now: i64) -> Vec<Out> {
    let mut told = scene.settle_batteries(now);
    told.extend(scene.settle_batteries(now + 200 * SECOND));
    told
}

/// A scene where the sentry has locked and fires at pilot B.
fn firing_at_b() -> (Scene, i64) {
    let (mut scene, now) = sentry_scene(2);
    scene.set_sentry_policy(policy(Some(0.0), None));
    scene.set_sentry_pilot(TOWER, Some(pilot(ship_id(B), -2.0)));
    settle_twice(&mut scene, now);
    assert_eq!(scene.batteries[&BATTERY].firing, Some(ship_id(B)));
    (scene, now)
}

#[test]
fn sentry_locks_the_named_pilot_and_fires_at_it_through_the_fire_rule() {
    let (mut scene, now) = sentry_scene(2);
    scene.set_sentry_policy(policy(Some(0.0), None));
    scene.set_sentry_pilot(TOWER, Some(pilot(ship_id(A), 5.0)));
    scene.set_sentry_pilot(TOWER, Some(pilot(ship_id(B), -2.0)));
    let told = settle_twice(&mut scene, now);
    assert!(obo_of(&told).is_empty(), "a sentry has no holder to tell: {told:?}");
    let battery = &scene.batteries[&BATTERY];
    assert_eq!(battery.holder, None);
    assert_eq!(battery.targeting.targets(), vec![ship_id(B)], "A is not named by a standing of 5");
    assert_eq!(battery.firing, Some(ship_id(B)));
}

#[test]
fn the_owner_corporation_is_never_named_and_a_pilot_without_facts_is_not_a_target() {
    let (mut scene, now) = sentry_scene(2);
    scene.set_sentry_policy(policy(Some(0.0), Some(0.0)));
    scene.set_sentry_pilot(
        TOWER,
        Some(SentryPilot { corporation_id: OWNER_CORPORATION, ..pilot(ship_id(A), -10.0) }),
    );
    settle_twice(&mut scene, now);
    let battery = &scene.batteries[&BATTERY];
    assert!(
        battery.targeting.is_empty(),
        "the owner's pilot and a pilot without facts are not locked: {:?}",
        battery.targeting.targets()
    );
    assert_eq!(battery.firing, None);
}

#[test]
fn war_names_a_pilot_even_in_high_security_and_the_thresholds_do_not() {
    let (mut scene, now) = sentry_scene(2);
    let mut high = policy(Some(5.0), Some(5.0));
    high.high_security = true;
    high.war = true;
    scene.set_sentry_policy(high);
    scene.set_sentry_pilot(TOWER, Some(pilot(ship_id(A), 0.0)));
    scene.set_sentry_pilot(TOWER, Some(SentryPilot { at_war: true, ..pilot(ship_id(B), 0.0) }));
    settle_twice(&mut scene, now);
    assert_eq!(scene.batteries[&BATTERY].targeting.targets(), vec![ship_id(B)]);
}

#[test]
fn the_security_status_threshold_names_a_pilot_below_it() {
    let (mut scene, now) = sentry_scene(2);
    scene.set_sentry_policy(policy(None, Some(0.0)));
    scene
        .set_sentry_pilot(TOWER, Some(SentryPilot { security_status: Some(-1.0), ..pilot(ship_id(B), 0.0) }));
    scene.set_sentry_pilot(TOWER, Some(SentryPilot { security_status: Some(2.0), ..pilot(ship_id(A), 0.0) }));
    settle_twice(&mut scene, now);
    assert_eq!(scene.batteries[&BATTERY].targeting.targets(), vec![ship_id(B)]);
}

#[test]
fn a_holder_keeps_the_battery_and_the_sentry_stays_out() {
    let (mut scene, now) = crowd(3);
    let at = beside_a(&scene, 1_000.0);
    scene.place_battery(battery_placement(at, A, true)).expect("the battery is placed");
    scene.bind_sentry_battery(BATTERY, TOWER);
    scene.set_sentry_policy(policy(Some(0.0), None));
    scene.set_sentry_pilot(TOWER, Some(pilot(ship_id(B), -2.0)));
    let (answer, _) = scene.battery_order(BATTERY, BatteryOrder::Lock(ship_id(A + 2)), now);
    assert_eq!(answer.reason, None, "{answer:?}");
    settle_twice(&mut scene, now);
    let battery = &scene.batteries[&BATTERY];
    assert_eq!(battery.holder, Some(A));
    assert_eq!(
        battery.targeting.targets(),
        vec![ship_id(A + 2)],
        "the holder's lock only; B is named but the holder decides"
    );
    assert_eq!(battery.firing, None);
}

#[test]
fn a_pilot_the_policy_no_longer_names_loses_its_lock_and_its_fire_silently() {
    let (mut scene, now) = firing_at_b();
    scene.set_sentry_pilot(TOWER, Some(pilot(ship_id(B), 5.0)));
    let told = scene.settle_batteries(now + 201 * SECOND);
    assert!(obo_of(&told).is_empty(), "{told:?}");
    let battery = &scene.batteries[&BATTERY];
    assert!(battery.targeting.is_empty());
    assert_eq!(battery.firing, None);
}

#[test]
fn an_offline_policy_unbinding_forgetting_the_pilots_or_an_offline_battery_lets_go_silently() {
    let cases: [fn(&mut Scene); 4] = [
        |scene| scene.set_sentry_policy(SentryPolicy { online: false, ..policy(Some(0.0), None) }),
        |scene| scene.bind_sentry_battery(BATTERY, 0),
        |scene| scene.set_sentry_pilot(TOWER, None),
        |scene| {
            let at = beside_a(scene, 1_000.0);
            scene.place_battery(battery_placement(at, 0, false)).expect("the battery is placed offline");
        },
    ];
    for (case, change) in cases.iter().enumerate() {
        let (mut scene, now) = firing_at_b();
        change(&mut scene);
        let told = scene.settle_batteries(now + 201 * SECOND);
        assert!(obo_of(&told).is_empty(), "case {case}: {told:?}");
        let battery = &scene.batteries[&BATTERY];
        assert!(battery.targeting.is_empty(), "case {case}: {:?}", battery.targeting.targets());
        assert_eq!(battery.firing, None, "case {case}");
    }
}

/// A sentry battery is POS state the scene keeps while no pilot is in it: a scene holding one is not empty.
#[test]
fn a_scene_holding_a_sentry_battery_is_not_empty() {
    let (mut scene, _) = sentry_scene(2);
    scene.pilots.clear();
    assert!(!scene.is_empty(), "a scene with a battery and no pilot was taken as empty");
}

/// A binding the host sends before its battery is placed is kept, and applied when the battery is placed, so the sentry works.
#[test]
fn a_binding_sent_before_its_battery_is_applied_when_the_battery_is_placed() {
    let (mut scene, now) = crowd(2);
    scene.bind_sentry_battery(BATTERY, TOWER);
    let at = beside_a(&scene, 1_000.0);
    scene.place_battery(battery_placement(at, 0, true)).expect("the battery is placed");
    scene.set_sentry_policy(policy(Some(0.0), None));
    scene.set_sentry_pilot(TOWER, Some(pilot(ship_id(B), -2.0)));
    settle_twice(&mut scene, now);
    assert_eq!(scene.batteries[&BATTERY].firing, Some(ship_id(B)), "the binding sent first was lost");
}

/// An unbinding sent before its battery is placed drops the binding that was kept for it.
#[test]
fn an_unbinding_sent_before_its_battery_drops_the_binding_kept_for_it() {
    let (mut scene, _) = crowd(2);
    scene.bind_sentry_battery(BATTERY, TOWER);
    scene.bind_sentry_battery(BATTERY, 0);
    let at = beside_a(&scene, 1_000.0);
    scene.place_battery(battery_placement(at, 0, true)).expect("the battery is placed");
    assert_eq!(scene.batteries[&BATTERY].sentry_tower, None, "the unbinding did not drop the kept binding");
}

/// A sentry pilot whose ship is no longer a pilot of the scene is dropped when the batteries settle; a pilot still in the scene is kept.
#[test]
fn a_sentry_pilot_whose_ship_has_left_is_dropped_when_the_batteries_settle() {
    let (mut scene, now) = sentry_scene(2);
    scene.set_sentry_policy(policy(Some(0.0), None));
    scene.set_sentry_pilot(TOWER, Some(pilot(ship_id(A), 0.0)));
    scene.set_sentry_pilot(TOWER, Some(pilot(ship_id(B) + 1000, 0.0)));
    settle_twice(&mut scene, now);
    assert!(scene.sentry_pilots.contains_key(&(TOWER, ship_id(A))), "a pilot still in the scene is kept");
    assert!(
        !scene.sentry_pilots.contains_key(&(TOWER, ship_id(B) + 1000)),
        "a pilot that left is still known"
    );
}

/// Two named pilots at different distances: the battery locks the nearer one, and only that one, while it holds it.
#[test]
fn the_nearest_of_two_named_pilots_is_locked_and_only_it() {
    let (mut scene, now) = sentry_scene(2);
    scene.set_sentry_policy(policy(Some(0.0), None));
    scene.set_sentry_pilot(TOWER, Some(pilot(ship_id(A), -2.0)));
    scene.set_sentry_pilot(TOWER, Some(pilot(ship_id(B), -2.0)));
    let at = scene.batteries[&BATTERY].position;
    let (near_distance, far_distance) =
        (distance(at, scene.pilots[&A].ship.position), distance(at, scene.pilots[&B].ship.position));
    assert_ne!(near_distance, far_distance, "the two pilots must be at different distances");
    let nearest = if near_distance < far_distance { ship_id(A) } else { ship_id(B) };
    settle_twice(&mut scene, now);
    assert_eq!(
        scene.batteries[&BATTERY].targeting.targets(),
        vec![nearest],
        "only the nearest named pilot is locked"
    );
}

/// An owner alliance is never named, and a pilot in no alliance is named when the owner has no alliance.
#[test]
fn an_owner_alliance_is_never_named_and_a_pilot_in_no_alliance_is_named_when_the_owner_has_none() {
    let (mut scene, now) = sentry_scene(2);
    scene.set_sentry_policy(SentryPolicy { owner_alliance_id: OWNER_ALLIANCE, ..policy(Some(0.0), None) });
    scene.set_sentry_pilot(
        TOWER,
        Some(SentryPilot { alliance_id: OWNER_ALLIANCE, ..pilot(ship_id(A), -2.0) }),
    );
    scene.set_sentry_pilot(TOWER, Some(pilot(ship_id(B), -2.0)));
    settle_twice(&mut scene, now);
    assert_eq!(
        scene.batteries[&BATTERY].targeting.targets(),
        vec![ship_id(B)],
        "the owner's alliance was named"
    );

    let (mut open, now) = sentry_scene(1);
    open.set_sentry_policy(policy(Some(0.0), None));
    open.set_sentry_pilot(TOWER, Some(pilot(ship_id(A), -2.0)));
    settle_twice(&mut open, now);
    assert_eq!(
        open.batteries[&BATTERY].targeting.targets(),
        vec![ship_id(A)],
        "a pilot in no alliance was not named when the owner has none"
    );
}

/// The war flag names a pilot at war only when the policy is at war: at peace a pilot at war is not shot, and at war the pilot at war is shot while
/// the pilot not at war is not.
#[test]
fn the_war_flag_names_a_pilot_at_war_only_when_the_policy_is_at_war() {
    let (mut peace, now) = sentry_scene(2);
    peace.set_sentry_policy(policy(None, None));
    peace.set_sentry_pilot(TOWER, Some(SentryPilot { at_war: true, ..pilot(ship_id(A), 0.0) }));
    peace.set_sentry_pilot(TOWER, Some(SentryPilot { at_war: true, ..pilot(ship_id(B), 0.0) }));
    settle_twice(&mut peace, now);
    assert!(peace.batteries[&BATTERY].targeting.is_empty(), "a pilot at war was shot at peace");

    let (mut war, now) = sentry_scene(2);
    war.set_sentry_policy(SentryPolicy { war: true, ..policy(None, None) });
    war.set_sentry_pilot(TOWER, Some(pilot(ship_id(A), 0.0)));
    war.set_sentry_pilot(TOWER, Some(SentryPilot { at_war: true, ..pilot(ship_id(B), 0.0) }));
    settle_twice(&mut war, now);
    assert_eq!(
        war.batteries[&BATTERY].targeting.targets(),
        vec![ship_id(B)],
        "at war, only the pilot at war is named"
    );
}

/// A standing or a security status exactly at its threshold is not named, the comparison being strict; one a little below is.
#[test]
fn a_standing_or_security_status_exactly_at_its_threshold_is_not_named() {
    let (mut standing, now) = sentry_scene(2);
    standing.set_sentry_policy(policy(Some(0.0), None));
    standing.set_sentry_pilot(TOWER, Some(pilot(ship_id(A), 0.0)));
    standing.set_sentry_pilot(TOWER, Some(pilot(ship_id(B), -0.01)));
    settle_twice(&mut standing, now);
    assert_eq!(
        standing.batteries[&BATTERY].targeting.targets(),
        vec![ship_id(B)],
        "a standing at the threshold was shot"
    );

    let (mut security, now) = sentry_scene(2);
    security.set_sentry_policy(policy(None, Some(0.0)));
    security
        .set_sentry_pilot(TOWER, Some(SentryPilot { security_status: Some(0.0), ..pilot(ship_id(A), 0.0) }));
    security.set_sentry_pilot(
        TOWER,
        Some(SentryPilot { security_status: Some(-0.01), ..pilot(ship_id(B), 0.0) }),
    );
    settle_twice(&mut security, now);
    assert_eq!(
        security.batteries[&BATTERY].targeting.targets(),
        vec![ship_id(B)],
        "a security status at the threshold was shot"
    );
}

/// A pilot the host knows toward another tower is not named for this one, and forgetting this tower's pilots keeps the other tower's.
#[test]
fn a_pilot_of_another_tower_is_not_named_here_and_forgetting_this_tower_keeps_the_others() {
    let (mut scene, now) = sentry_scene(2);
    scene.set_sentry_policy(policy(Some(0.0), None));
    scene.set_sentry_pilot(OTHER_TOWER, Some(pilot(ship_id(B), -2.0)));
    settle_twice(&mut scene, now);
    assert!(scene.batteries[&BATTERY].targeting.is_empty(), "a pilot of another tower was named here");
    scene.set_sentry_pilot(TOWER, None);
    assert!(
        scene.sentry_pilots.contains_key(&(OTHER_TOWER, ship_id(B))),
        "forgetting this tower's pilots forgot another tower's pilot"
    );
}

/// A pilot the policy names is not locked when it is out of range, warping or cloaked: the battery refuses it as it refuses any target.
#[test]
fn a_named_pilot_out_of_range_warping_or_cloaked_is_not_locked() {
    let cases: [fn(&mut Scene, i64); 3] = [
        |scene, _| {
            // 50 200 m from the battery: its gap of 50 093 m is past the 50 000 m range but the pilot is still in view
            let at = scene.batteries[&BATTERY].position;
            scene.pilots.get_mut(&B).unwrap().ship.position = Vec3::new(at.x + 50_200.0, at.y, at.z);
        },
        |scene, now| {
            scene.pilots.get_mut(&B).unwrap().flight =
                Some(Flight::begin(now, Vec3::new(1.0e9, 0.0, 0.0), 0.0))
        },
        |scene, _| scene.pilots.get_mut(&B).unwrap().cloak = CloakState::Cloaked,
    ];
    for (case, change) in cases.iter().enumerate() {
        let (mut scene, now) = sentry_scene(2);
        scene.set_sentry_policy(policy(Some(0.0), None));
        scene.set_sentry_pilot(TOWER, Some(pilot(ship_id(B), -2.0)));
        change(&mut scene, now);
        settle_twice(&mut scene, now);
        assert!(
            scene.batteries[&BATTERY].targeting.is_empty(),
            "case {case}: {:?}",
            scene.batteries[&BATTERY].targeting.targets()
        );
        assert_eq!(scene.batteries[&BATTERY].firing, None, "case {case}");
    }
}

/// A binding for a battery that is not in the scene binds no battery that is: the battery in the scene keeps no tower.
#[test]
fn a_binding_for_a_battery_not_in_the_scene_binds_no_other_battery() {
    let (mut scene, _) = crowd(2);
    let at = beside_a(&scene, 1_000.0);
    scene.place_battery(battery_placement(at, 0, true)).expect("the battery is placed");
    scene.bind_sentry_battery(BATTERY + 1, TOWER);
    assert_eq!(
        scene.batteries[&BATTERY].sentry_tower, None,
        "a binding for an absent battery bound the one in the scene"
    );
    assert!(!scene.batteries.contains_key(&(BATTERY + 1)), "a binding created a battery");
}
