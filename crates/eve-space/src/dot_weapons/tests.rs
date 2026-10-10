//! The damage over time of a breacher pod (`dot_weapons`): the formula of a tick, a pod's application ticking once a second for its duration, the strongest of
//! several working alone and the others inert until it runs out, and the effect ending when the ship leaves the scene.

use super::*;
use crate::brains::rat_tests::actions_named;
use crate::missiles::Missile;
use crate::modules::{Action, Effect};
use crate::scene::Scene;
use crate::scene::tests::{NOON, STATION, spec, system};
use crate::weapons::Turret;
use crate::wire::{id, int, real};

const A: SessionId = 7;
const B: SessionId = 8;
const SECOND: i64 = 10_000_000;
const MODULE: i64 = 9_988_400_001_940;
/// The small breacher pod's charge (SDE 85088): 40 s, 200 a tick at most, 0.6 % of the target's hit points a tick.
const SMALL: Dot = Dot { duration_ms: 40_000.0, max_damage_per_tick: 200.0, max_hp_percent_per_tick: 0.6 };
/// The medium breacher pod's charge (SDE 85089): the same duration and percentage, 600 a tick at most.
const MEDIUM: Dot = Dot { duration_ms: 40_000.0, max_damage_per_tick: 600.0, max_hp_percent_per_tick: 0.6 };
/// Two effects of the same cap that differ only by percentage: at any hit points above 100 the first is the stronger.
const STRONG: Dot = Dot { duration_ms: 40_000.0, max_damage_per_tick: 600.0, max_hp_percent_per_tick: 1.0 };
const WEAK: Dot = Dot { duration_ms: 40_000.0, max_damage_per_tick: 600.0, max_hp_percent_per_tick: 0.5 };

fn other_spec() -> crate::ship::ShipSpec {
    let mut other = spec();
    other.ship_id += 4;
    other.character_id += 1;
    other.owner_id += 1;
    other
}

/// Two ships in a system, both bound, the clock forty seconds after they undocked: `A` and `B`, a kilometre apart (inside a pod's 4 km reach).
fn crowd() -> (Scene, i64) {
    let mut scene = Scene::new(system());
    scene.undock(A, spec(), STATION, NOON).unwrap();
    scene.bind(A, NOON);
    scene.undock(B, other_spec(), STATION, NOON).unwrap();
    scene.bind(B, NOON);
    let a = scene.pilots[&A].ship.position;
    place(&mut scene, A, a);
    place(&mut scene, B, eve_destiny::Vec3::new(a.x + 1_000.0, a.y, a.z));
    let t = NOON + 40 * SECOND;
    scene.poll(t);
    (scene, t)
}

fn place(scene: &mut Scene, session: SessionId, position: eve_destiny::Vec3) {
    let pilot = scene.pilots.get_mut(&session).unwrap();
    pilot.ship.position = position;
    pilot.ship.velocity = eve_destiny::Vec3::ZERO;
    pilot.ship.speed_fraction = 0.0;
    pilot.ship.course = crate::ship::Course::Stop;
    pilot.protected_until = 0;
}

/// The breacher pod's flight: 2 km a second for two seconds (SDE 2000 m/s, explosion delay 2000 ms), no damage of its own.
fn pod(dot: Dot) -> Missile {
    Missile {
        charge_type_id: 85_088,
        group_id: 4_808,
        name: "SCARAB Breacher Pod S".to_string(),
        radius: 1.0,
        mass: 1.0,
        agility: 1.0,
        hit_points: 0.0,
        max_velocity: 2_000.0,
        flight_time_ms: 2_000.0,
        explosion_radius: 35.0,
        explosion_velocity: 2_000.0,
        damage_reduction_factor: 1.0,
        damage_reduction_sensitivity: 5.5,
        dot: Some(dot),
    }
}

/// A launcher of A that fires `missile` at B (`A`'s own module, a launch that does no damage of its own).
fn launch_pod_at_b(scene: &mut Scene, missile: Missile, t: i64) -> Vec<Out> {
    let effect = Effect {
        burns_fuel: false,
        module_id: MODULE,
        module_type: 1,
        effect_id: 1,
        fx: String::new(),
        duration_ms: 5_000.0,
        capacitor_need: 0.0,
        repeat: 1,
        target_id: scene.pilots[&B].ship.spec.ship_id,
        turret: Some(Turret {
            weapon_type_id: 85_084,
            fx: String::new(),
            raw_damage: [0.0, 0.0, 0.0, 0.0],
            optimal_range: 0.0,
            falloff: 0.0,
            tracking_speed: 0.0,
            signature_resolution: 0.0,
            charge: None,
            missile: Some(missile),
            spool: None,
            aoe: None,
            arc: None,
            requires_gunnery: false,
        }),
        action: Some(Action::WarpBubble { range: 0.0 }),
        on: None,
        off: None,
        on_changes: Vec::new(),
        off_changes: Vec::new(),
        rules: Default::default(),
    };
    scene.launch_missile(A, &effect, t)
}

/// Polls the scene every quarter second for `seconds` from `from`: the clock at the end.
fn poll_for(scene: &mut Scene, from: i64, seconds: f64) -> i64 {
    let mut t = from;
    while t < from + (seconds * SECOND as f64) as i64 {
        t += SECOND / 4;
        scene.poll(t);
    }
    t
}

/// The total points of B's layers when whole (shield, armor and hull capacities).
fn total_hp_of_b(scene: &Scene) -> f64 {
    let max = scene.pilots[&B].ship.health.vitals().layers();
    max.shield + max.armor + max.structure
}

/// The remaining effect of B's applications, in order of landing.
fn remaining_of_b(scene: &Scene) -> Vec<f64> {
    scene
        .dots
        .get(&scene.pilots[&B].ship.spec.ship_id)
        .map_or(Vec::new(), |t| t.applications.iter().map(|a| a.remaining_ms).collect())
}

#[test]
fn a_tick_does_the_lower_of_the_percentage_and_the_cap_rounded_down() {
    // 0.6 % of 1,234 is 7.404: a whole point
    assert_eq!(tick_damage(&SMALL, 1_234.0), 7.0);
    // 0.6 % of 100,000 is 600, and the small pod caps a tick at 200
    assert_eq!(tick_damage(&SMALL, 100_000.0), 200.0);
    // the medium pod's cap is 600: 0.6 % of 200,000 is 1,200 and is capped at 600
    assert_eq!(tick_damage(&MEDIUM, 200_000.0), 600.0);
    // nothing to hurt, nothing taken
    assert_eq!(tick_damage(&SMALL, 0.0), 0.0);
}

#[test]
fn a_pod_that_lands_ticks_once_a_second_for_its_duration_and_then_ends() {
    let (mut scene, mut t) = crowd();
    launch_pod_at_b(&mut scene, pod(SMALL), t);
    assert_eq!(scene.aggressions.len(), 1, "a launch whose effect is offensive aggresses its target");
    // the pod flies about a kilometre at 2 km a second: it lands within a second, and the first tick is a second after that
    while remaining_of_b(&scene).is_empty() {
        t = poll_for(&mut scene, t, 0.25);
        assert!(
            t < NOON + 200 * SECOND,
            "the pod lands: missiles {:?} dots {:?}",
            scene
                .missiles
                .values()
                .map(|m| (m.target, m.position, m.phase, m.expires_at, t))
                .collect::<Vec<_>>(),
            scene.dots.len()
        );
    }
    assert_eq!(remaining_of_b(&scene), [40_000.0], "the effect is on B from its landing, not yet ticking");
    let t = poll_for(&mut scene, t, 10.0);
    let done = 40_000.0 - remaining_of_b(&scene)[0];
    assert!(
        (9_000.0..=11_000.0).contains(&done) && done % 1_000.0 == 0.0,
        "about ten ticks have passed: {done}"
    );
    poll_for(&mut scene, t, 40.0);
    assert!(remaining_of_b(&scene).is_empty(), "the effect is over after its forty seconds");
    assert!(scene.dots.is_empty());
}

#[test]
fn of_two_applications_the_stronger_works_and_the_other_waits_inert_until_it_runs_out() {
    let (mut scene, t) = crowd();
    let shooter = Shooter {
        session: A,
        ship_id: scene.pilots[&A].ship.spec.ship_id,
        module_id: MODULE,
        weapon_type_id: 85_084,
        owner_id: 0,
        corporation_id: 0,
        alliance_id: 0,
    };
    let b_ship = scene.pilots[&B].ship.spec.ship_id;
    // two effects land together: the strong one (1 %) is the stronger than the weak one (0.5 %) at this ship's hit points
    scene.apply_dot(b_ship, STRONG, shooter.clone(), t);
    scene.apply_dot(b_ship, WEAK, shooter.clone(), t);
    let total = total_hp_of_b(&scene);
    assert!(
        tick_damage(&STRONG, total) > tick_damage(&WEAK, total),
        "the strong one is the stronger at {total} points"
    );
    // three ticks: the medium one works, the small one has not moved
    let t = poll_for(&mut scene, t, 3.0);
    assert_eq!(
        remaining_of_b(&scene),
        [37_000.0, 40_000.0],
        "the stronger one works, the weaker one is inert"
    );
    // forty ticks in all for the strong one: it is gone at the fortieth second, and the weak one works from the forty-first (8 ticks by 48 s)
    let t = poll_for(&mut scene, t, 45.0);
    assert_eq!(
        remaining_of_b(&scene),
        [32_000.0],
        "the weaker effect starts when the stronger one has run out, then runs its own ticks"
    );
    let _ = t;
}

#[test]
fn the_slim_item_shows_the_damage_over_time_and_drops_it_when_the_effect_ends() {
    let (mut scene, t) = crowd();
    let b_ship = scene.pilots[&B].ship.spec.ship_id;
    let shooter = Shooter {
        session: A,
        ship_id: scene.pilots[&A].ship.spec.ship_id,
        module_id: MODULE,
        weapon_type_id: 85_084,
        owner_id: 0,
        corporation_id: 0,
        alliance_id: 0,
    };
    scene.apply_dot(b_ship, SMALL, shooter, t);
    // the first tick (a second on) tells the ship's slim item the percentage it takes
    let mut told = Vec::new();
    let mut now = t;
    while now < t + SECOND + SECOND / 2 {
        now += SECOND / 4;
        told.extend(scene.poll(now));
    }
    let shown = |out: &Out| format!("{out:?}");
    assert!(
        told.iter().any(|o| shown(o).contains("dotHpPercentageApplied")),
        "the first tick shows the percentage"
    );
    // the effect runs its forty seconds: its last tick takes the field away (the slim item is told again without it)
    let mut later = Vec::new();
    while now < t + 42 * SECOND {
        now += SECOND / 4;
        later.extend(scene.poll(now));
    }
    let changes: Vec<String> = later.iter().map(shown).filter(|s| s.contains("OnSlimItemChange")).collect();
    let last = changes.last().expect("the slim item is told when the effect ends");
    assert!(!last.contains("dotHpPercentageApplied"), "the field is gone once the effect is over");
}

#[test]
fn the_effect_ends_when_the_ship_leaves_the_scene() {
    let (mut scene, t) = crowd();
    let b_ship = scene.pilots[&B].ship.spec.ship_id;
    let shooter = Shooter {
        session: A,
        ship_id: scene.pilots[&A].ship.spec.ship_id,
        module_id: MODULE,
        weapon_type_id: 85_084,
        owner_id: 0,
        corporation_id: 0,
        alliance_id: 0,
    };
    scene.apply_dot(b_ship, SMALL, shooter, t);
    scene.pilots.remove(&B);
    scene.poll(t + SECOND);
    assert!(scene.dots.is_empty(), "a ship that left the scene carries no effect");
}

/// The applications of each `OnDotVictimUpdated` that `B`, the ship hit, was told: the state of every `DotApplicationInfo` in its set.
/// (Each update goes to everyone who has the ship in view, so the same update is told to `A` too.)
fn infos_told(told: &[Out]) -> Vec<Vec<Vec<(String, Value)>>> {
    let told: Vec<Out> =
        told.iter().filter(|o| matches!(o, Out::Notify { session, .. } if *session == B)).cloned().collect();
    actions_named(&told, "OnDotVictimUpdated")
        .into_iter()
        .map(|args| {
            let Value::ObjectEx { header, .. } = &args[1] else { panic!("a set: {args:?}") };
            let Value::Tuple(parts) = header.as_ref() else { panic!("a set header") };
            let Value::Tuple(set_args) = &parts[1] else { panic!("a set argument tuple") };
            let Value::List(items) = &set_args[0] else { panic!("a set of applications") };
            items
                .iter()
                .map(|item| {
                    let Value::ObjectEx { header, .. } = item else { panic!("an application: {item:?}") };
                    let Value::Tuple(parts) = header.as_ref() else { panic!("an application header") };
                    let Value::Dict(entries) = &parts[2] else { panic!("an application state") };
                    entries
                        .iter()
                        .map(|(key, value)| match key {
                            Value::Str(key) => (key.to_string(), value.clone()),
                            other => panic!("a state key {other:?}"),
                        })
                        .collect()
                })
                .collect()
        })
        .collect()
}

fn state<'a>(application: &'a [(String, Value)], key: &str) -> &'a Value {
    &application.iter().find(|(k, _)| k == key).unwrap_or_else(|| panic!("no {key}")).1
}

fn shooter_a(scene: &Scene) -> Shooter {
    Shooter {
        session: A,
        ship_id: scene.pilots[&A].ship.spec.ship_id,
        module_id: MODULE,
        weapon_type_id: 85_084,
        owner_id: 4_242,
        corporation_id: 0,
        alliance_id: 0,
    }
}

#[test]
fn a_hit_tells_the_ship_its_applications_as_a_set_and_the_first_tick_makes_one_working() {
    let (mut scene, t) = crowd();
    let b_ship = scene.pilots[&B].ship.spec.ship_id;
    let shooter = shooter_a(&scene);
    let mut told = scene.apply_dot(b_ship, STRONG, shooter.clone(), t);
    told.extend(scene.apply_dot(b_ship, WEAK, shooter.clone(), t));
    let sets = infos_told(&told);
    assert_eq!(sets.len(), 2, "each hit tells the ship what it holds now");
    let held = &sets[1];
    assert_eq!(held.len(), 2);
    for application in held {
        assert_eq!(state(application, "_attackerID"), &id(4_242), "the character that fired the pod");
        assert_eq!(state(application, "_weaponTypeID"), &int(85_084), "the launcher's type");
        assert_eq!(state(application, "_targetID"), &id(b_ship));
        assert_eq!(
            state(application, "_activityState"),
            &int(DOT_IDLE),
            "nothing works before the first tick"
        );
    }
    // the first tick: the stronger one (1 % a tick) works, and the weaker one (0.5 %) waits
    let mut now = t;
    let mut later = Vec::new();
    while now < t + SECOND + SECOND / 2 {
        now += SECOND / 4;
        later.extend(scene.poll(now));
    }
    let after = infos_told(&later);
    assert_eq!(after.len(), 1, "the first tick is a change of the working application, told once");
    let working: Vec<&Value> = after[0]
        .iter()
        .filter(|application| state(application, "_activityState") == &int(DOT_ACTIVE))
        .map(|application| state(application, "_maxHPNormalizedRatio"))
        .collect();
    assert_eq!(working, [&real(0.01)], "the strong application is the one working");
    // the working one expires after its remaining time, the waiting one at the end of the queue: it runs its whole length after the working one
    let expiry = |application: &[(String, Value)]| match state(application, "_expiryTime") {
        Value::Long(ticks) => *ticks,
        other => panic!("an expiry: {other:?}"),
    };
    let set = &after[0];
    let working_end = expiry(set.iter().find(|a| state(a, "_activityState") == &int(DOT_ACTIVE)).unwrap());
    let waiting_end = expiry(set.iter().find(|a| state(a, "_activityState") == &int(DOT_IDLE)).unwrap());
    assert_eq!(
        waiting_end - working_end,
        40 * SECOND,
        "the waiting application ends 40 s after the working one"
    );
}

#[test]
fn the_ship_is_told_an_empty_set_when_its_last_application_ends() {
    let (mut scene, t) = crowd();
    let b_ship = scene.pilots[&B].ship.spec.ship_id;
    let mut told = scene.apply_dot(b_ship, SMALL, shooter_a(&scene), t);
    let mut now = t;
    while now < t + 42 * SECOND {
        now += SECOND / 4;
        told.extend(scene.poll(now));
    }
    let sets = infos_told(&told);
    // the hit, the first tick (the application starts working) and the end of its forty seconds, and nothing between
    assert_eq!(sets.len(), 3, "told on the hit, on the change to working and on the end: {sets:?}");
    assert_eq!(sets[0].len(), 1);
    assert_eq!(sets[1].len(), 1);
    assert!(sets[2].is_empty(), "the ship holds nothing once its effect is over");
}
