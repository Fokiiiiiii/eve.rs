//! The POS battery sentry (protocol 106): the policy names pilots, the battery locks the nearest one it can and fires at it through the holder's
//! fire rule, and a holder's battery is never the sentry's.

use super::tests::{A, B, BATTERY, SECOND, battery_placement, beside_a, crowd, obo_of, ship_id};
use super::*;

const TOWER: i64 = 1_060_000_001;
const OWNER_CORPORATION: i64 = 98_000_001;
const RIVAL_CORPORATION: i64 = 98_000_002;

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
