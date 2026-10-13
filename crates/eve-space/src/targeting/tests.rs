use std::sync::Arc;

use eve_marshal::Value;

use super::*;
use crate::scene::tests::{NOON, STATION, spec, system};
use crate::scene::{Command, Departure};
use crate::ship::ShipSpec;
use crate::world::{Body, BodyKind, Detail};

const A: SessionId = 7;
const B: SessionId = 8;
const C: SessionId = 9;
const SECOND: i64 = 10_000_000;
const PLANET: i64 = 40_000_001;

fn spec_of(offset: i64) -> ShipSpec {
    let mut other = spec();
    other.ship_id += offset;
    other.character_id += offset;
    other.owner_id += offset;
    other.name = format!("Ship {offset}");
    other
}

fn ship_id(session: SessionId) -> i64 {
    spec_of(session as i64 - A as i64).ship_id
}

/// The system of the tests with a planet, and a station of a size that gives a lock time of its own.
fn world() -> Arc<crate::world::SolarSystem> {
    let mut world = (*system()).clone();
    world.bodies.push(Body {
        id: PLANET,
        type_id: 2015,
        group_id: 7,
        category_id: 2,
        owner_id: 0,
        name: "Testing III".to_string(),
        kind: BodyKind::Planet,
        position: Vec3::new(45_000_000_000.0, 0.0, 20_000_000_000.0),
        radius: 6_000_000.0,
        graphic_id: None,
        detail: Detail::None,
    });
    Arc::new(world)
}

/// Ships A and B (and C when asked), bound, thirty seconds after they undocked one second apart: no one is protected.
fn crowd(ships: usize) -> (Scene, i64) {
    let mut scene = Scene::new(world());
    for n in 0..ships {
        let session = A + n as u64;
        scene.undock(session, spec_of(n as i64), STATION, NOON + n as i64 * SECOND).unwrap();
        scene.bind(session, NOON + n as i64 * SECOND);
    }
    let anchor = scene.pilots[&A].ship.position;
    let direction = scene.pilots[&A].ship.direction;
    for n in 1..ships {
        let ship = &mut scene.pilots.get_mut(&(A + n as u64)).unwrap().ship;
        let offset = 1_500.0 * n as f64;
        ship.position = Vec3::new(
            anchor.x + direction.x * offset,
            anchor.y + direction.y * offset,
            anchor.z + direction.z * offset,
        );
    }
    let mut t = NOON;
    while t < NOON + 40 * SECOND {
        t += SECOND / 4;
        scene.poll(t);
    }
    (scene, t)
}

fn poll_until(scene: &mut Scene, from: i64, seconds: f64) -> (Vec<Out>, i64) {
    let mut out = Vec::new();
    let mut t = from;
    while t < from + (seconds * SECOND as f64) as i64 {
        t += SECOND / 4;
        out.extend(scene.poll(t));
    }
    (out, t)
}

/// What each session was told by a notification of the given name: `(session, arguments)`.
fn told(outs: &[Out], name: &str) -> Vec<(SessionId, Vec<Value>)> {
    outs.iter()
        .filter_map(|o| match o {
            Out::Notify { session, name: n, args, .. } if *n == name => Some((*session, args.clone())),
            _ => None,
        })
        .collect()
}

fn announced_damage_state(outs: &[Out]) -> bool {
    let expected = Value::Str("OnDamageStateChange".into());
    told(outs, "DoDestinyUpdate").iter().any(|(_, args)| {
        let Some(Value::List(actions)) = args.first() else { return false };
        actions.iter().any(|action| {
            let Value::Tuple(action) = action else { return false };
            let Some(Value::Tuple(payload)) = action.get(1) else { return false };
            payload.first() == Some(&expected)
        })
    })
}

fn on_target(outs: &[Out]) -> Vec<(SessionId, Vec<String>)> {
    told(outs, "OnTarget")
        .into_iter()
        .map(|(s, args)| {
            (
                s,
                args.iter()
                    .map(|a| match a {
                        Value::Str(text) => text.to_string(),
                        Value::Int(n) | Value::Long(n) => n.to_string(),
                        other => panic!("{other:?}"),
                    })
                    .collect(),
            )
        })
        .collect()
}

fn words(items: &[&str]) -> Vec<String> {
    items.iter().map(|s| s.to_string()).collect()
}

fn answer(outs: &[Out]) -> (bool, Vec<i64>) {
    match outs.iter().find(|o| matches!(o, Out::Targets { .. })) {
        Some(Out::Targets { pending, targets, .. }) => (*pending, targets.clone()),
        other => panic!("no answer in {outs:?}: {other:?}"),
    }
}

fn refusal(outs: &[Out]) -> Refusal {
    match outs.iter().find(|o| matches!(o, Out::TargetRefused { .. })) {
        Some(Out::TargetRefused { reason, target_type, desired_range, actual_distance, .. }) => Refusal {
            reason,
            target_type: *target_type,
            desired_range: *desired_range,
            actual_distance: *actual_distance,
        },
        other => panic!("not refused in {outs:?}: {other:?}"),
    }
}

/// A lock asked for at `t` and let run until it is had.
fn locked(scene: &mut Scene, from: i64, source: SessionId, target: i64) -> (Vec<Out>, i64) {
    let asked = scene.target_order(source, Order::Lock(target), from);
    assert!(answer(&asked).0, "{asked:?}");
    poll_until(scene, from, 6.0)
}

#[test]
fn a_lock_takes_forty_million_over_the_scan_resolution_over_the_square_of_the_arc_sine_of_the_size() {
    // the Ibis: 475 mm and 52 m
    assert!((lock_duration_ms(475.0, 52.0) - 3_903.7).abs() < 1.0, "{}", lock_duration_ms(475.0, 52.0));
    // a bigger target is locked faster, a better scanner locks faster
    assert!(lock_duration_ms(475.0, 500.0) < lock_duration_ms(475.0, 52.0));
    assert!(lock_duration_ms(950.0, 52.0) < lock_duration_ms(475.0, 52.0));
    // a size below one metre counts as one
    assert_eq!(lock_duration_ms(475.0, 0.2), lock_duration_ms(475.0, 1.0));
    // no scan resolution is two seconds; nothing is longer than three minutes or shorter than a millisecond
    assert_eq!(lock_duration_ms(0.0, 52.0), 2_000.0);
    assert_eq!(lock_duration_ms(0.001, 52.0), 180_000.0);
    assert_eq!(lock_duration_ms(1.0e12, 52.0), 1.0);
}

#[test]
fn a_lock_is_answered_at_once_as_going_and_had_when_its_time_is_up() {
    let (mut scene, t) = crowd(2);
    let asked = scene.target_order(B, Order::Lock(ship_id(A)), t);
    assert_eq!(answer(&asked), (true, vec![]));
    assert_eq!(asked.len(), 1, "nothing else is said: {asked:?}");
    // 28 m of signature and 660 mm: 3.74 s
    let (early, t2) = poll_until(&mut scene, t, 3.4);
    assert!(on_target(&early).is_empty(), "{early:?}");
    let (out, _) = poll_until(&mut scene, t2, 1.0);
    assert_eq!(
        on_target(&out),
        vec![
            (B, words(&["add", &ship_id(A).to_string()])),
            (A, words(&["otheradd", &ship_id(B).to_string()])),
        ]
    );
    assert_eq!(answer(&scene.target_order(B, Order::Targets, t + 10 * SECOND)), (false, vec![ship_id(A)]));
    assert_eq!(answer(&scene.target_order(A, Order::Targeters, t + 10 * SECOND)), (false, vec![ship_id(B)]));
    assert_eq!(answer(&scene.target_order(A, Order::Targets, t + 10 * SECOND)), (false, vec![]));
    assert_eq!(answer(&scene.target_order(B, Order::Targeters, t + 10 * SECOND)), (false, vec![]));
}

#[test]
fn the_damage_state_of_what_is_locked_is_told_to_everyone_who_can_see_it_and_the_one_that_locks() {
    let (mut scene, t) = crowd(3);
    let (out, _) = locked(&mut scene, t, B, ship_id(A));
    let recipients: Vec<SessionId> = told(&out, "DoDestinyUpdate").into_iter().map(|(s, _)| s).collect();
    assert_eq!(
        recipients,
        [A, B, C],
        "the ship that is locked, the one that locks and the one that looks on, in the order they came"
    );
    let (_, args) = &told(&out, "DoDestinyUpdate")[0];
    let Value::List(actions) = &args[0] else { panic!("{args:?}") };
    let Value::Tuple(action) = &actions[0] else { panic!() };
    let Value::Tuple(inner) = &action[1] else { panic!() };
    assert_eq!(inner[0], Value::Str("OnDamageStateChange".into()));
    let Value::Tuple(arguments) = &inner[1] else { panic!() };
    assert_eq!(arguments[0], crate::wire::id(ship_id(A)));
    let Value::Tuple(state) = &arguments[1] else { panic!() };
    let Value::Tuple(shield) = &state[0] else { panic!() };
    assert_eq!(shield[0], Value::Real(1.0));
    assert_eq!(shield[1], Value::Real(172_000.0), "a fifth of the shield's recharge time");
    assert_eq!((&state[1], &state[2]), (&Value::Real(1.0), &Value::Real(1.0)));
}

#[test]
fn a_lock_asked_for_again_says_whether_it_is_had_or_going_and_starts_nothing() {
    let (mut scene, t) = crowd(2);
    scene.target_order(B, Order::Lock(ship_id(A)), t);
    let going = scene.target_order(B, Order::Lock(ship_id(A)), t + SECOND);
    assert_eq!(answer(&going), (true, vec![]));
    let (out, t2) = poll_until(&mut scene, t, 5.0);
    assert_eq!(on_target(&out).len(), 2, "one lock, however often it was asked for: {out:?}");
    let had = scene.target_order(B, Order::Lock(ship_id(A)), t2);
    assert_eq!(answer(&had), (false, vec![ship_id(A)]));
    assert_eq!(had.len(), 1);
}

#[test]
fn a_lock_given_up_says_nothing_and_is_never_had() {
    let (mut scene, t) = crowd(2);
    scene.target_order(B, Order::Lock(ship_id(A)), t);
    let out = scene.target_order(B, Order::Cancel(ship_id(A)), t + SECOND);
    assert_eq!(out.len(), 1, "{out:?}");
    assert_eq!(answer(&out), (false, vec![]));
    let (later, _) = poll_until(&mut scene, t, 8.0);
    assert!(on_target(&later).is_empty(), "{later:?}");
    assert_eq!(answer(&scene.target_order(B, Order::Targets, t + 9 * SECOND)), (false, vec![]));
}

#[test]
fn a_target_let_go_is_lost_to_the_ship_and_other_lost_to_the_one_that_was_locked() {
    let (mut scene, t) = crowd(2);
    let (_, t) = locked(&mut scene, t, B, ship_id(A));
    let out = scene.target_order(B, Order::Remove(vec![ship_id(A)]), t);
    assert_eq!(
        on_target(&out),
        vec![
            (B, words(&["lost", &ship_id(A).to_string()])),
            (A, words(&["otherlost", &ship_id(B).to_string()])),
        ]
    );
    assert_eq!(answer(&out), (false, vec![]));
    // what is not locked is nothing to let go of
    let again = scene.target_order(B, Order::Remove(vec![ship_id(A), 12345]), t);
    assert!(on_target(&again).is_empty());
}

#[test]
fn letting_go_of_a_lock_that_is_going_does_nothing_to_it() {
    let (mut scene, t) = crowd(2);
    scene.target_order(B, Order::Lock(ship_id(A)), t);
    let out = scene.target_order(B, Order::Remove(vec![ship_id(A)]), t + SECOND);
    assert!(on_target(&out).is_empty());
    let (later, _) = poll_until(&mut scene, t, 5.0);
    assert_eq!(on_target(&later).len(), 2, "the lock still comes");
}

#[test]
fn clearing_says_clear_once_to_the_ship_and_other_lost_to_each_ship_that_was_locked_and_gives_up_what_is_going()
 {
    let (mut scene, t) = crowd(3);
    let (_, t) = locked(&mut scene, t, C, ship_id(A));
    let (_, t) = locked(&mut scene, t, C, ship_id(B));
    scene.target_order(C, Order::Lock(STATION), t);
    let out = scene.target_order(C, Order::Clear, t + SECOND / 10);
    assert_eq!(
        on_target(&out),
        vec![
            (A, words(&["otherlost", &ship_id(C).to_string()])),
            (B, words(&["otherlost", &ship_id(C).to_string()])),
            (C, words(&["clear"])),
        ]
    );
    let (later, _) = poll_until(&mut scene, t, 3.0);
    assert!(on_target(&later).is_empty(), "the lock on the station that was going is given up: {later:?}");
    // nothing locked, nothing to clear
    assert!(on_target(&scene.target_order(C, Order::Clear, t + 5 * SECOND)).is_empty());
}

#[test]
fn the_targets_of_a_ship_are_listed_in_the_order_they_were_asked_for_not_the_order_they_were_had() {
    let (mut scene, t) = crowd(3);
    // the station is quick to lock (it is big), the ship is slow: asked for first, had last
    scene.target_order(C, Order::Lock(ship_id(A)), t);
    scene.target_order(C, Order::Lock(STATION), t);
    poll_until(&mut scene, t, 6.0);
    assert_eq!(
        answer(&scene.target_order(C, Order::Targets, t + 7 * SECOND)),
        (false, vec![ship_id(A), STATION])
    );
}

#[test]
fn a_station_can_be_locked_and_is_told_to_no_one_but_the_ones_who_look_on() {
    let (mut scene, t) = crowd(2);
    let (out, _) = locked(&mut scene, t, B, STATION);
    assert_eq!(on_target(&out), vec![(B, words(&["add", &STATION.to_string()]))]);
    let updates = told(&out, "DoDestinyUpdate");
    assert_eq!(
        updates.iter().map(|(s, _)| *s).collect::<Vec<_>>(),
        [A, B],
        "every ship in the ballpark hears of the station"
    );
    let Value::List(actions) = &updates[0].1[0] else { panic!() };
    let Value::Tuple(action) = &actions[0] else { panic!() };
    let Value::Tuple(inner) = &action[1] else { panic!() };
    let Value::Tuple(arguments) = &inner[1] else { panic!() };
    let Value::Tuple(state) = &arguments[1] else { panic!() };
    let Value::Tuple(shield) = &state[0] else { panic!() };
    assert_eq!(shield[1], Value::Real(110_000.0), "a station answers with the same recharge whatever it is");
}

#[test]
fn some_things_cannot_be_locked_and_the_client_is_told_which() {
    let (mut scene, t) = crowd(2);
    let refused = |scene: &mut Scene, target: i64| refusal(&scene.target_order(B, Order::Lock(target), t));
    assert_eq!(refused(&mut scene, ship_id(B)).reason, "TARGET_SELF");
    assert_eq!(refused(&mut scene, 987654321).reason, "TARGET_NOT_FOUND");
    assert_eq!(
        refused(&mut scene, PLANET),
        Refusal {
            reason: "TARGET_NOT_TARGETABLE",
            target_type: 2015,
            desired_range: 0.0,
            actual_distance: 0.0
        }
    );
    for (group, kind) in
        [(6, BodyKind::Star), (8, BodyKind::Moon), (9, BodyKind::Belt), (10, BodyKind::Stargate)]
    {
        let mut world = (*world()).clone();
        world.bodies.push(Body {
            id: 41_000_000 + i64::from(group),
            type_id: 77,
            group_id: group,
            category_id: 2,
            owner_id: 0,
            name: "Scenery".to_string(),
            kind,
            position: Vec3::new(1_000.0, 0.0, 0.0),
            radius: 100.0,
            graphic_id: None,
            detail: Detail::None,
        });
        let mut scene = Scene::new(Arc::new(world));
        scene.undock(B, spec_of(1), STATION, NOON).unwrap();
        let out = scene.target_order(B, Order::Lock(41_000_000 + i64::from(group)), NOON + 40 * SECOND);
        assert_eq!(refusal(&out).reason, "TARGET_NOT_TARGETABLE", "group {group}");
    }
}

#[test]
fn a_target_out_of_range_is_refused_with_the_range_and_the_distance_between_the_surfaces() {
    let (mut scene, t) = crowd(2);
    // the second ship goes on at top speed for a long while, the first stops
    scene.command(A, Command::Stop, t);
    let (_, t) = poll_until(&mut scene, t, 300.0);
    let out = scene.target_order(B, Order::Lock(ship_id(A)), t);
    let refusal = refusal(&out);
    assert_eq!(refusal.reason, "TARGET_OUT_OF_RANGE");
    assert_eq!(refusal.target_type, spec().type_id);
    assert_eq!(refusal.desired_range, 29_700.0);
    let apart = {
        let (a, b) = (scene.ship(A, t).unwrap().position, scene.ship(B, t).unwrap().position);
        distance(a, b) - 2.0 * spec().radius
    };
    assert!((refusal.actual_distance - apart).abs() < 1e-6, "{} m, {apart} m", refusal.actual_distance);
    assert!(apart > 29_700.0);
}

#[test]
fn a_station_three_grid_boxes_away_is_refused_for_its_range_and_not_as_not_there() {
    let (mut scene, t) = crowd(2);
    // a system body is in view at any distance: the grid box of a ship does not hide it from a lock (visibility.rs)
    scene.pilots.get_mut(&B).unwrap().ship.position = Vec3::new(3.0 * crate::visibility::GRID_BOX, 0.0, 0.0);
    assert_eq!(refusal(&scene.target_order(B, Order::Lock(STATION), t)).reason, "TARGET_OUT_OF_RANGE");
}

#[test]
fn range_is_measured_between_surfaces_and_the_range_itself_is_out() {
    let (mut scene, t) = crowd(2);
    scene.command(A, Command::Stop, t);
    scene.command(B, Command::Stop, t);
    let (_, t) = poll_until(&mut scene, t, 60.0);
    // put the ships a set distance apart (surfaces) on a line
    for (session, x) in [(A, 0.0), (B, 29_700.0 + 2.0 * spec().radius)] {
        scene.pilots.get_mut(&session).unwrap().ship.position = Vec3::new(x, 0.0, 0.0);
    }
    assert_eq!(refusal(&scene.target_order(B, Order::Lock(ship_id(A)), t)).reason, "TARGET_OUT_OF_RANGE");
    scene.pilots.get_mut(&B).unwrap().ship.position = Vec3::new(29_699.0 + 2.0 * spec().radius, 0.0, 0.0);
    assert!(answer(&scene.target_order(B, Order::Lock(ship_id(A)), t)).0);
}

#[test]
fn a_ship_that_has_just_undocked_cannot_be_locked_and_the_reason_comes_before_the_one_for_locking_itself() {
    let mut scene = Scene::new(world());
    scene.undock(A, spec_of(0), STATION, NOON).unwrap();
    scene.undock(B, spec_of(1), STATION, NOON).unwrap();
    let t = NOON + 5 * SECOND;
    assert_eq!(refusal(&scene.target_order(B, Order::Lock(ship_id(A)), t)).reason, "TARGET_INVULNERABLE");
    assert_eq!(refusal(&scene.target_order(B, Order::Lock(ship_id(B)), t)).reason, "TARGET_INVULNERABLE");
    // thirty seconds after it undocked it can
    assert_eq!(
        refusal(&scene.target_order(B, Order::Lock(ship_id(B)), NOON + 30 * SECOND)).reason,
        "TARGET_SELF"
    );
    assert!(answer(&scene.target_order(B, Order::Lock(ship_id(A)), NOON + 30 * SECOND)).0);
}

#[test]
fn a_ship_that_is_flown_is_no_longer_protected_and_a_ship_that_locks_gives_up_its_protection() {
    let mut scene = Scene::new(world());
    for (session, n) in [(A, 0), (B, 1), (C, 2)] {
        scene.undock(session, spec_of(n), STATION, NOON).unwrap();
    }
    let t = NOON + 5 * SECOND;
    // flown: a ship that is told to go is no longer protected, one that is not is
    scene.command(A, Command::GotoDirection(Vec3::new(0.0, 1.0, 0.0)), t);
    assert!(answer(&scene.target_order(B, Order::Lock(ship_id(A)), t + SECOND)).0);
    let refused = scene.target_order(A, Order::Lock(ship_id(C)), t + SECOND);
    assert_eq!(refusal(&refused).reason, "TARGET_INVULNERABLE", "C is still protected");
    // a lock that is asked for (and valid) ends the locker's protection, one that is refused does not
    let mut scene = Scene::new(world());
    for (session, n) in [(A, 0), (B, 1)] {
        scene.undock(session, spec_of(n), STATION, NOON - 40 * SECOND).unwrap();
    }
    scene.undock(C, spec_of(2), STATION, NOON).unwrap();
    for session in [A, B, C] {
        scene.pilots.get_mut(&session).unwrap().ship.position = Vec3::new(100_000.0, 100_000.0, 100_000.0);
    }
    let t = NOON + 5 * SECOND;
    assert_eq!(refusal(&scene.target_order(A, Order::Lock(ship_id(C)), t)).reason, "TARGET_INVULNERABLE");
    assert!(answer(&scene.target_order(C, Order::Lock(ship_id(A)), t)).0, "C is protected and locks");
    assert!(answer(&scene.target_order(A, Order::Lock(ship_id(C)), t)).0, "and is not protected any more");
}

#[test]
fn a_ship_that_is_docking_is_still_protected() {
    let mut world = (*world()).clone();
    world.bodies[0].radius = 70_000.0;
    let mut scene = Scene::new(Arc::new(world));
    for (session, n) in [(A, 0), (B, 1)] {
        scene.undock(session, spec_of(n), STATION, NOON).unwrap();
    }
    let t = NOON + 2 * SECOND;
    let accepted = scene.command(A, Command::Dock { station_id: STATION }, t);
    assert!(accepted.iter().any(|o| matches!(o, Out::Accepted { command: 4, .. })), "{accepted:?}");
    assert_eq!(
        refusal(&scene.target_order(B, Order::Lock(ship_id(A)), t + SECOND)).reason,
        "TARGET_INVULNERABLE"
    );
}

#[test]
fn the_damage_state_reaches_a_ship_that_locks_though_it_does_not_have_the_target_in_its_ballpark() {
    let (mut scene, t) = crowd(2);
    scene.pilots.get_mut(&B).unwrap().sees.remove(&A);
    let (out, _) = locked(&mut scene, t, B, ship_id(A));
    let recipients: Vec<SessionId> = told(&out, "DoDestinyUpdate").into_iter().map(|(s, _)| s).collect();
    assert!(recipients.contains(&B), "{recipients:?}");
}

#[test]
fn a_ship_that_warps_lets_go_of_all_it_has_locked_when_the_warp_is_entered_and_cannot_lock_meanwhile() {
    let (mut scene, t) = crowd(2);
    let (_, t) = locked(&mut scene, t, B, ship_id(A));
    let (_, t) = locked(&mut scene, t, B, STATION);
    let out = scene.command(B, Command::WarpTo { target_id: PLANET, range: 0.0 }, t);
    assert!(on_target(&out).is_empty(), "the ship is only aligning: {out:?}");
    // the ship enters warp some seconds on
    let (later, t) = poll_until(&mut scene, t, 20.0);
    assert_eq!(
        on_target(&later),
        vec![(A, words(&["otherlost", &ship_id(B).to_string()])), (B, words(&["clear"])),]
    );
    assert_eq!(refusal(&scene.target_order(B, Order::Lock(ship_id(A)), t)).reason, "SOURCE_WARPING");
    assert_eq!(refusal(&scene.target_order(A, Order::Lock(ship_id(B)), t)).reason, "TARGET_WARPING");
    assert_eq!(answer(&scene.target_order(B, Order::Targets, t)), (false, vec![]));
}

#[test]
fn a_ship_that_is_locked_and_warps_is_let_go_of_without_a_reason() {
    let (mut scene, t) = crowd(2);
    let (_, t) = locked(&mut scene, t, B, ship_id(A));
    scene.command(A, Command::WarpTo { target_id: PLANET, range: 0.0 }, t);
    let (later, _) = poll_until(&mut scene, t, 20.0);
    assert_eq!(
        on_target(&later),
        vec![
            (B, words(&["lost", &ship_id(A).to_string()])),
            (A, words(&["otherlost", &ship_id(B).to_string()])),
        ]
    );
}

#[test]
fn a_ship_that_leaves_is_lost_to_the_ones_that_had_it_locked_before_they_are_told_it_is_gone() {
    let (mut scene, t) = crowd(2);
    let (_, t) = locked(&mut scene, t, B, ship_id(A));
    let out = scene.leave(A, Departure::Docked, t).unwrap();
    let order: Vec<String> = out
        .iter()
        .map(|o| match o {
            Out::Notify { name, session, .. } => format!("{name} to {session}"),
            other => format!("{other:?}"),
        })
        .collect();
    assert_eq!(order, ["OnTarget to 8", "DoDestinyUpdate to 8"]);
    assert_eq!(on_target(&out), vec![(B, words(&["lost", &ship_id(A).to_string()]))]);
    assert_eq!(answer(&scene.target_order(B, Order::Targets, t)), (false, vec![]));
}

#[test]
fn a_ship_that_leaves_lets_go_of_what_it_locked_and_the_ship_that_was_locked_is_told() {
    let (mut scene, t) = crowd(2);
    let (_, t) = locked(&mut scene, t, B, ship_id(A));
    let out = scene.leave(B, Departure::LoggedOff, t).unwrap();
    assert_eq!(on_target(&out), vec![(A, words(&["otherlost", &ship_id(B).to_string()]))]);
    assert_eq!(answer(&scene.target_order(A, Order::Targeters, t)), (false, vec![]));
}

#[test]
fn a_ship_that_docks_is_lost_when_it_has_docked() {
    let mut world = (*world()).clone();
    world.bodies[0].radius = 70_000.0;
    let mut scene = Scene::new(Arc::new(world));
    for (session, n) in [(A, 0), (B, 1)] {
        scene.undock(session, spec_of(n), STATION, NOON).unwrap();
    }
    let station = scene.system().body(STATION).unwrap().clone();
    for (session, gap) in [(A, 1_000.0), (B, 15_000.0)] {
        let pilot = scene.pilots.get_mut(&session).unwrap();
        let distance = station.radius + pilot.ship.spec.radius + gap;
        pilot.ship.position =
            Vec3::new(station.position.x + distance, station.position.y, station.position.z);
        pilot.ship.velocity = Vec3::ZERO;
        pilot.ship.speed_fraction = 0.0;
        pilot.ship.course = crate::ship::Course::Stop;
        scene.bind(session, NOON);
    }
    let (_, t) = poll_until(&mut scene, NOON, 40.0);
    let (_, t) = locked(&mut scene, t, B, ship_id(A));
    let accepted = scene.command(A, Command::Dock { station_id: STATION }, t);
    assert!(accepted.iter().any(|o| matches!(o, Out::Accepted { command: 4, .. })), "{accepted:?}");
    let (out, _) = poll_until(&mut scene, t, 6.0);
    let kinds: Vec<String> = out
        .iter()
        .filter(|o| matches!(o, Out::Docked { .. }) || matches!(o, Out::Notify { name: "OnTarget", .. }))
        .map(|o| match o {
            Out::Docked { .. } => "docked".to_string(),
            _ => "OnTarget".to_string(),
        })
        .collect();
    assert_eq!(kinds, ["docked", "OnTarget"]);
    assert_eq!(on_target(&out), vec![(B, words(&["lost", &ship_id(A).to_string()]))]);
}

#[test]
fn a_lock_that_is_going_is_dropped_when_the_target_gets_out_of_range_and_one_that_is_held_is_lost() {
    let (mut scene, t) = crowd(2);
    scene.command(A, Command::Stop, t);
    scene.command(B, Command::Stop, t);
    let (_, t) = poll_until(&mut scene, t, 60.0);
    for (session, x) in [(A, 0.0), (B, 100.0)] {
        let pilot = scene.pilots.get_mut(&session).unwrap();
        pilot.ship.position = Vec3::new(x, 0.0, 0.0);
    }
    scene.target_order(B, Order::Lock(ship_id(A)), t);
    scene.pilots.get_mut(&A).unwrap().ship.position = Vec3::new(50_000.0, 0.0, 0.0);
    let (out, t) = poll_until(&mut scene, t, 6.0);
    assert!(on_target(&out).is_empty(), "{out:?}");
    assert_eq!(answer(&scene.target_order(B, Order::Targets, t)), (false, vec![]));

    scene.pilots.get_mut(&A).unwrap().ship.position = Vec3::new(1_000.0, 0.0, 0.0);
    let (_, t) = locked(&mut scene, t, B, ship_id(A));
    scene.pilots.get_mut(&A).unwrap().ship.position = Vec3::new(50_000.0, 0.0, 0.0);
    let (out, _) = poll_until(&mut scene, t, 1.0);
    assert_eq!(
        on_target(&out),
        vec![
            (B, words(&["lost", &ship_id(A).to_string()])),
            (A, words(&["otherlost", &ship_id(B).to_string()])),
        ]
    );
}

#[test]
fn a_ship_can_lock_no_more_than_it_may_and_a_lock_that_is_going_counts() {
    let (mut scene, t) = crowd(3);
    scene.pilots.get_mut(&C).unwrap().ship.spec.max_locked_targets = 2;
    assert!(answer(&scene.target_order(C, Order::Lock(ship_id(A)), t)).0);
    assert!(answer(&scene.target_order(C, Order::Lock(ship_id(B)), t)).0);
    let full = refusal(&scene.target_order(C, Order::Lock(STATION), t));
    assert_eq!(
        (full.reason, full.desired_range),
        ("TARGET_LOCK_LIMIT_REACHED", 2.0),
        "the limit is told in the place of the range"
    );
    scene.target_order(C, Order::Cancel(ship_id(B)), t);
    assert!(answer(&scene.target_order(C, Order::Lock(STATION), t)).0);
    // none at all
    scene.pilots.get_mut(&C).unwrap().ship.spec.max_locked_targets = 0;
    scene.target_order(C, Order::Clear, t);
    assert_eq!(
        refusal(&scene.target_order(C, Order::Lock(ship_id(B)), t)).reason,
        "TARGET_LOCK_LIMIT_REACHED"
    );
}

#[test]
fn a_ship_that_is_not_in_space_is_refused_every_order() {
    let mut scene = Scene::new(world());
    for order in [
        Order::Lock(1),
        Order::Cancel(1),
        Order::Remove(vec![1]),
        Order::Clear,
        Order::Targets,
        Order::Targeters,
    ] {
        let code = order.code();
        let out = scene.target_order(A, order, NOON);
        assert_eq!(out, [Out::Refused { session: A, command: code, reason: "NO_SHIP_IN_SPACE" }]);
    }
}

#[test]
fn the_codes_of_the_orders_are_those_of_the_link() {
    let codes: Vec<u8> = [
        Order::Lock(1),
        Order::Cancel(1),
        Order::Remove(vec![]),
        Order::Clear,
        Order::Targets,
        Order::Targeters,
    ]
    .iter()
    .map(Order::code)
    .collect();
    assert_eq!(codes, [10, 11, 12, 13, 14, 15]);
}

#[test]
fn a_pilot_that_has_not_bound_the_ballpark_is_told_nothing_of_damage() {
    let mut scene = Scene::new(world());
    scene.undock(A, spec_of(0), STATION, NOON).unwrap();
    scene.undock(B, spec_of(1), STATION, NOON).unwrap();
    // neither has bound: the lock is had and the client is told with the notification, but no update is sent
    let t = NOON + 40 * SECOND;
    scene.target_order(B, Order::Lock(ship_id(A)), t);
    let (out, _) = poll_until(&mut scene, t, 6.0);
    assert_eq!(on_target(&out).len(), 2);
    assert!(told(&out, "DoDestinyUpdate").is_empty(), "{out:?}");
}

fn insert_wreck(scene: &mut Scene, id: i64, group_id: i32, type_id: i32, position: Vec3) {
    scene.wrecks.insert(
        id,
        crate::wreck::Wreck {
            id,
            group_id,
            kind: crate::wreck::WreckKind { type_id, name: "Test wreck".to_string(), graphic_id: None },
            position,
            direction: Vec3::new(0.0, 0.0, 1.0),
            radius: 100.0,
            owner_id: 1_000_001,
            corporation_id: 0,
            alliance_id: 0,
            war_faction_id: 0,
            rights: crate::wreck::LootRights::default(),
            launcher_id: 1_000_001,
            is_empty: true,
            rights_known: false,
            pull: None,
            velocity: eve_destiny::Vec3::ZERO,
            expires_at: i64::MAX,
        },
    );
}

#[test]
fn npc_and_drone_groups_can_be_locked_and_receive_target_events() {
    let mut scene = Scene::new(world());
    scene.undock(B, spec_of(1), STATION, NOON).unwrap();

    let mut npc = spec_of(0);
    npc.ship_id = 980_050_000_055;
    npc.character_id = 0;
    npc.owner_id = 1_000_125;
    npc.corporation_id = 1_000_125;
    npc.type_id = 3863;
    npc.group_id = 301;
    npc.category_id = 11;
    npc.name.clear();
    scene.undock(crate::npc::NPC_SESSION_BASE, npc, STATION, NOON).unwrap();

    let mut drone = spec_of(2);
    drone.ship_id = 980_050_000_056;
    drone.character_id = 0;
    drone.type_id = 2454;
    drone.group_id = 100;
    drone.category_id = 18;
    drone.name = "Hobgoblin I".to_string();
    drone.radius = 15.0;
    drone.signature_radius = 15.0;
    scene.undock(crate::npc::NPC_SESSION_BASE + 1, drone, STATION, NOON).unwrap();

    scene.bind(B, NOON);
    let (_, mut now) = poll_until(&mut scene, NOON, 40.0);
    let mut expected_targets = Vec::new();
    for (session, target) in
        [(crate::npc::NPC_SESSION_BASE, 980_050_000_055), (crate::npc::NPC_SESSION_BASE + 1, 980_050_000_056)]
    {
        let asked = scene.target_order(B, Order::Lock(target), now);
        assert_eq!(answer(&asked), (true, expected_targets.clone()), "{asked:?}");
        let (out, next) = poll_until(&mut scene, now, 12.0);
        assert_eq!(
            on_target(&out),
            vec![
                (B, words(&["add", &target.to_string()])),
                (session, words(&["otheradd", &ship_id(B).to_string()])),
            ],
            "{out:?}"
        );
        expected_targets.push(target);
        assert_eq!(answer(&scene.target_order(B, Order::Targets, next)), (false, expected_targets.clone()));
        now = next;
    }
}

#[test]
fn wrecks_and_containers_can_be_locked_without_damage_state_notifications() {
    let (mut scene, mut now) = crowd(2);
    let position = scene.pilots.get(&B).unwrap().ship.position;
    let mut expected_targets = Vec::new();
    for (target, group, type_id) in
        [(99_000_001, crate::wreck::WRECK_GROUP, 27_002), (99_000_002, crate::wreck::CONTAINER_GROUP, 23_611)]
    {
        insert_wreck(&mut scene, target, group, type_id, position);
        let asked = scene.target_order(B, Order::Lock(target), now);
        assert_eq!(answer(&asked), (true, expected_targets.clone()), "{asked:?}");
        let (out, next) = poll_until(&mut scene, now, 10.0);
        assert_eq!(on_target(&out), vec![(B, words(&["add", &target.to_string()]))]);
        assert!(!announced_damage_state(&out), "{out:?}");
        expected_targets.push(target);
        assert_eq!(answer(&scene.target_order(B, Order::Targets, next)), (false, expected_targets.clone()));
        now = next;
    }
}

#[test]
fn an_individual_asteroid_can_be_locked_but_all_untargetable_groups_remain_refused() {
    let mut system = (*world()).clone();
    let position = system.body(STATION).unwrap().position;
    system.bodies.push(Body {
        id: 41_000_025,
        type_id: 1234,
        group_id: 25,
        category_id: 25,
        owner_id: 0,
        name: "Asteroid".to_string(),
        kind: BodyKind::Belt,
        position,
        radius: 100.0,
        graphic_id: None,
        detail: Detail::None,
    });

    let mut scene = Scene::new(Arc::new(system));
    for (session, offset) in [(A, 0), (B, 1)] {
        scene.undock(session, spec_of(offset), STATION, NOON).unwrap();
        scene.bind(session, NOON);
    }
    let (_, now) = poll_until(&mut scene, NOON, 40.0);
    let asked = scene.target_order(B, Order::Lock(41_000_025), now);
    assert_eq!(answer(&asked), (true, vec![]), "{asked:?}");
    let (out, now) = poll_until(&mut scene, now, 10.0);
    assert_eq!(on_target(&out), vec![(B, words(&["add", "41000025"]))]);
    assert!(!announced_damage_state(&out), "{out:?}");

    let mut system = (*world()).clone();
    let position = system.body(STATION).unwrap().position;
    for (index, group_id) in UNTARGETABLE_GROUPS.iter().copied().enumerate() {
        system.bodies.push(Body {
            id: 41_001_000 + index as i64,
            type_id: 5_000 + index as i32,
            group_id,
            category_id: 2,
            owner_id: 0,
            name: "Untargetable scenery".to_string(),
            kind: BodyKind::Star,
            position,
            radius: 100.0,
            graphic_id: None,
            detail: Detail::None,
        });
    }
    let mut scene = Scene::new(Arc::new(system));
    for (session, offset) in [(A, 0), (B, 1)] {
        scene.undock(session, spec_of(offset), STATION, now).unwrap();
        scene.bind(session, now);
    }
    let (_, now) = poll_until(&mut scene, now, 40.0);
    for (index, group_id) in UNTARGETABLE_GROUPS.iter().copied().enumerate() {
        let target = 41_001_000 + index as i64;
        let out = scene.target_order(B, Order::Lock(target), now);
        let refusal = refusal(&out);
        assert_eq!(
            (refusal.reason, refusal.target_type),
            ("TARGET_NOT_TARGETABLE", 5_000 + index as i32),
            "group {group_id}: {out:?}"
        );
        assert!(on_target(&out).is_empty(), "{out:?}");
    }
}

#[test]
fn an_out_of_range_wreck_refusal_includes_its_type_and_surface_distance() {
    let (mut scene, now) = crowd(2);
    let source_position = Vec3::new(100_000.0, 100_000.0, 100_000.0);
    scene.pilots.get_mut(&B).unwrap().ship.position = source_position;
    insert_wreck(
        &mut scene,
        99_000_003,
        crate::wreck::WRECK_GROUP,
        27_003,
        Vec3::new(135_000.0, 100_000.0, 100_000.0),
    );
    let out = scene.target_order(B, Order::Lock(99_000_003), now);
    let refusal = refusal(&out);
    assert_eq!(refusal.reason, "TARGET_OUT_OF_RANGE");
    assert_eq!(refusal.target_type, 27_003);
    assert!(refusal.actual_distance > refusal.desired_range, "{refusal:?}");
    assert!(on_target(&out).is_empty(), "{out:?}");
}

fn rock(id: i64, position: Vec3) -> crate::wreck::Asteroid {
    crate::wreck::Asteroid {
        id,
        type_id: 1_230,
        group_id: 462,
        category_id: 25,
        name: "Veldspar".to_string(),
        graphic_id: None,
        position,
        direction: Vec3::new(0.0, 0.0, 1.0),
        radius: 50.0,
        units_remaining: 1_000,
    }
}

#[test]
fn a_mineable_rock_in_range_is_locked_and_an_out_of_range_one_is_refused() {
    let (mut scene, now) = crowd(2);
    let at = scene.pilots[&B].ship.position;
    let near = Vec3::new(at.x + 2_000.0, at.y, at.z);
    let far = Vec3::new(at.x + 90_000.0, at.y, at.z);
    scene.place_asteroid(rock(99_100_001, near), now);
    scene.place_asteroid(rock(99_100_002, far), now);

    let asked = scene.target_order(B, Order::Lock(99_100_001), now);
    assert_eq!(answer(&asked), (true, vec![]), "{asked:?}");
    let (out, now) = poll_until(&mut scene, now, 10.0);
    assert_eq!(on_target(&out), vec![(B, words(&["add", "99100001"]))], "{out:?}");
    assert_eq!(scene.locked_by(B), vec![99_100_001]);

    let out = scene.target_order(B, Order::Lock(99_100_002), now);
    let far_refusal = refusal(&out);
    assert_eq!(far_refusal.reason, "TARGET_OUT_OF_RANGE", "{out:?}");
    assert_eq!(far_refusal.target_type, 1_230);
    assert!(on_target(&out).is_empty(), "{out:?}");

    // a rock of another system is not in this scene's map, so it does not resolve here
    let out = scene.target_order(B, Order::Lock(99_100_009), now);
    assert_eq!(refusal(&out).reason, "TARGET_NOT_FOUND", "{out:?}");
    assert!(on_target(&out).is_empty(), "{out:?}");
}

#[test]
fn a_depleted_rock_refuses_a_new_lock_and_drops_the_lock_held_on_it() {
    let (mut scene, now) = crowd(2);
    let at = scene.pilots[&B].ship.position;
    let near = Vec3::new(at.x + 2_000.0, at.y, at.z);
    scene.place_asteroid(rock(99_100_003, near), now);
    let asked = scene.target_order(B, Order::Lock(99_100_003), now);
    assert_eq!(answer(&asked), (true, vec![]), "{asked:?}");
    let (_, now) = poll_until(&mut scene, now, 10.0);
    assert_eq!(scene.locked_by(B), vec![99_100_003]);

    let _ = scene.remove_asteroid(99_100_003, true, now);
    let (_, now) = poll_until(&mut scene, now, 1.0);
    assert!(scene.locked_by(B).is_empty(), "the lock on a depleted rock is dropped");

    let out = scene.target_order(B, Order::Lock(99_100_003), now);
    assert_eq!(refusal(&out).reason, "TARGET_NOT_FOUND", "{out:?}");
    assert!(on_target(&out).is_empty(), "{out:?}");
}

/// A lock asked for with an origin other than the pilot's own (a passive targeter or a sweep), through the one lock path.
fn lock_as(scene: &mut Scene, session: SessionId, target: i64, origin: LockOrigin, now: i64) -> Vec<Out> {
    scene.lock(session, target, &Order::Lock(target), origin, now)
}

#[test]
fn only_a_passive_lock_that_suppresses_its_notice_leaves_the_ship_locked_untold() {
    let cases = [
        (LockOrigin::Player, true),
        (LockOrigin::Passive { suppress_notice: false }, true),
        (LockOrigin::Passive { suppress_notice: true }, false),
        (LockOrigin::Auto, true),
    ];
    for (origin, told) in cases {
        let (mut scene, t) = crowd(2);
        let asked = lock_as(&mut scene, B, ship_id(A), origin, t);
        assert!(answer(&asked).0, "{origin:?} is going: {asked:?}");
        // the lock is pending, so the notice (or its absence) comes when settle_targets has it had
        let (out, t2) = poll_until(&mut scene, t, 6.0);
        let mut expected = vec![(B, words(&["add", &ship_id(A).to_string()]))];
        if told {
            expected.push((A, words(&["otheradd", &ship_id(B).to_string()])));
        }
        assert_eq!(on_target(&out), expected, "{origin:?}");
        assert_eq!(
            answer(&scene.target_order(B, Order::Targets, t2)),
            (false, vec![ship_id(A)]),
            "{origin:?}"
        );
        assert_eq!(
            answer(&scene.target_order(A, Order::Targeters, t2)),
            (false, vec![ship_id(B)]),
            "{origin:?}"
        );
    }
}

#[test]
fn a_passive_lock_that_is_had_at_once_tells_its_locker_and_not_the_ship_locked() {
    let (mut scene, t) = crowd(2);
    // a scanner that locks in a millisecond: the lock is had at once, without waiting for settle_targets
    scene.pilots.get_mut(&B).unwrap().ship.spec.scan_resolution = 1.0e12;
    let asked = lock_as(&mut scene, B, ship_id(A), LockOrigin::Passive { suppress_notice: true }, t);
    assert_eq!(on_target(&asked), vec![(B, words(&["add", &ship_id(A).to_string()]))], "{asked:?}");
    assert_eq!(answer(&scene.target_order(B, Order::Targets, t)), (false, vec![ship_id(A)]));
}

#[test]
fn a_passive_lock_is_held_to_the_range_and_the_capacity_like_any_other() {
    let quiet = LockOrigin::Passive { suppress_notice: true };
    let (mut scene, t) = crowd(3);
    scene.command(A, Command::Stop, t);
    scene.command(B, Command::Stop, t);
    scene.command(C, Command::Stop, t);
    let (_, t) = poll_until(&mut scene, t, 60.0);
    // the range is 29 700 m between surfaces: one metre beyond it is refused, one metre inside it is accepted
    let reach = 29_700.0 + 2.0 * spec().radius;
    scene.pilots.get_mut(&A).unwrap().ship.position = Vec3::new(0.0, 0.0, 0.0);
    scene.pilots.get_mut(&C).unwrap().ship.position = Vec3::new(reach - 1.0, 0.0, 1_000.0);
    scene.pilots.get_mut(&B).unwrap().ship.position = Vec3::new(reach + 1.0, 0.0, 0.0);
    let far = lock_as(&mut scene, B, ship_id(A), quiet, t);
    assert_eq!(refusal(&far).reason, "TARGET_OUT_OF_RANGE", "{far:?}");
    assert!(on_target(&far).is_empty(), "{far:?}");
    scene.pilots.get_mut(&B).unwrap().ship.position = Vec3::new(reach - 1.0, 0.0, 0.0);
    // the passive lock takes one of the ship's places: a pilot's own lock of another ship is then refused
    scene.pilots.get_mut(&B).unwrap().ship.spec.max_locked_targets = 1;
    assert!(answer(&lock_as(&mut scene, B, ship_id(A), quiet, t)).0);
    let full = refusal(&lock_as(&mut scene, B, ship_id(C), LockOrigin::Player, t));
    assert_eq!((full.reason, full.desired_range), ("TARGET_LOCK_LIMIT_REACHED", 1.0));
}
