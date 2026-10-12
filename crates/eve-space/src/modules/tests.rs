use std::sync::Arc;

use super::*;
use crate::modules::{ACTIVATION_BLOCKED, DISALLOW_DOCKING, WARP_SCRAMBLE_STATUS};
use crate::scene::tests::{NOON, STATION, clear_of_the_station, spec, system};
use crate::scene::{CloakTrigger, Command, DecloakCause, Out};
use crate::ship::ShipSpec;
use crate::weapons::tests::finishes_within;

const A: SessionId = 7;
const B: SessionId = 8;
const SECOND: i64 = 10_000_000;
const MODULE: i64 = 9_988_400_001_940;
const PLANET: i64 = 40_000_001;

pub(crate) fn afterburner() -> Effect {
    Effect {
        rules: Default::default(),
        burns_fuel: false,
        module_id: MODULE,
        module_type: 21857,
        effect_id: 6731,
        fx: "effects.Afterburner".to_string(),
        duration_ms: 10_000.0,
        capacitor_need: 5.0,
        repeat: 1000,
        target_id: 0,
        turret: None,
        action: None,
        on: Some(Numbers { mass: 1_663_000.0, max_velocity: 522.848918, signature_radius: 28.0 }),
        off: Some(Numbers { mass: 1_163_000.0, max_velocity: 339.25, signature_radius: 28.0 }),
        on_changes: vec![
            Change { item_id: MODULE, attribute_id: 20, value: 60.0 },
            Change { item_id: MODULE, attribute_id: 73, value: 10_000.0 },
            Change { item_id: spec().ship_id, attribute_id: 37, value: 522.848918 },
        ],
        off_changes: vec![
            Change { item_id: MODULE, attribute_id: 20, value: 60.0 },
            Change { item_id: MODULE, attribute_id: 73, value: 10_000.0 },
            Change { item_id: spec().ship_id, attribute_id: 37, value: 339.25 },
        ],
    }
}

fn micro_jump_drive() -> Effect {
    Effect {
        burns_fuel: false,
        module_id: MODULE + 200,
        module_type: 4383,
        effect_id: 4921,
        fx: "effects.MicroJumpDriveEngage".to_string(),
        duration_ms: 12_000.0,
        capacitor_need: 0.0,
        repeat: 1,
        action: Some(Action::MicroJumpDrive { distance_meters: 100_000.0, cooldown_ms: 180_000 }),
        on: None,
        off: None,
        on_changes: Vec::new(),
        off_changes: Vec::new(),
        ..afterburner()
    }
}

fn other_spec() -> ShipSpec {
    let mut other = spec();
    other.ship_id += 4;
    other.character_id += 1;
    other.owner_id += 1;
    other
}

/// A ship that has bound the ballpark, and one that looks on, thirty seconds after they undocked.
fn crowd() -> (Scene, i64) {
    crowd_in((*system()).clone())
}

/// The same, in a system that has a planet (to warp to).
fn crowd_with_a_planet() -> (Scene, i64) {
    let mut world = (*system()).clone();
    world.bodies.push(crate::world::Body {
        id: PLANET,
        type_id: 2015,
        group_id: 7,
        category_id: 2,
        owner_id: 0,
        name: "Testing III".to_string(),
        kind: crate::world::BodyKind::Planet,
        position: eve_destiny::Vec3::new(45_000_000_000.0, 0.0, 20_000_000_000.0),
        radius: 6_000_000.0,
        graphic_id: None,
        detail: crate::world::Detail::None,
    });
    crowd_in(world)
}

fn crowd_in(world: crate::world::SolarSystem) -> (Scene, i64) {
    let mut scene = Scene::new(Arc::new(world));
    scene.undock(A, spec(), STATION, NOON).unwrap();
    scene.bind(A, NOON);
    scene.undock(B, other_spec(), STATION, NOON).unwrap();
    scene.bind(B, NOON);

    let station = scene.system().body(STATION).unwrap().position;
    let ship_a = scene.pilots[&A].ship.position;
    let radial = (ship_a.x - station.x).hypot(ship_a.y - station.y).max(1.0);
    let ship_b = eve_destiny::Vec3::new(
        ship_a.x - (ship_a.y - station.y) / radial * 5_000.0,
        ship_a.y + (ship_a.x - station.x) / radial * 5_000.0,
        ship_a.z,
    );
    for (session, position) in [(A, ship_a), (B, ship_b)] {
        let ship = &mut scene.pilots.get_mut(&session).unwrap().ship;
        ship.position = position;
        ship.velocity = eve_destiny::Vec3::ZERO;
        ship.speed_fraction = 0.0;
        ship.course = crate::ship::Course::Stop;
    }

    let t = NOON + 40 * SECOND;
    scene.poll(t);
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

fn label(out: &Out) -> String {
    match out {
        Out::Notify { session, name, args, .. } if *name == "DoDestinyUpdate" => {
            let Some(Value::List(actions)) = args.first() else { panic!("{args:?}") };
            let names: Vec<String> = actions
                .iter()
                .map(|a| {
                    let Value::Tuple(pair) = a else { panic!() };
                    let Value::Tuple(inner) = &pair[1] else { panic!() };
                    let Value::Str(name) = &inner[0] else { panic!() };
                    name.to_string()
                })
                .collect();
            format!("{session}:destiny[{}]", names.join(","))
        }
        Out::Notify { session, name, args, .. } if *name == "OnModuleAttributeChanges" => {
            let Some(Value::List(lines)) = args.first() else { panic!("{args:?}") };
            let attributes: Vec<String> = lines
                .iter()
                .map(|l| {
                    let Value::Tuple(t) = l else { panic!() };
                    match &t[3] {
                        Value::Int(n) => n.to_string(),
                        other => panic!("{other:?}"),
                    }
                })
                .collect();
            format!("{session}:attributes[{}]", attributes.join(","))
        }
        Out::Notify { session, name, .. } => format!("{session}:{name}"),
        Out::Accepted { session, command, .. } => format!("{session}:accepted {command}"),
        Out::Refused { session, command, reason } => format!("{session}:refused {command} {reason}"),
        other => format!("{other:?}"),
    }
}

/// What the client is told: the host's messages (the work cycles and the end reports) are not the client's, so they are left out.
fn labels(outs: &[Out]) -> Vec<String> {
    outs.iter()
        .filter(|out| !matches!(out, Out::ModuleWorkCycle { .. } | Out::ModuleEnded { .. }))
        .map(label)
        .collect()
}

fn charge_of(out: &Out) -> Option<(f64, f64)> {
    let Out::Notify { name: "OnModuleAttributeChanges", args, .. } = out else { return None };
    let Some(Value::List(lines)) = args.first() else { return None };
    let Value::Tuple(t) = &lines[0] else { return None };
    (t[3] == Value::Int(18)).then(|| match (&t[5], &t[6]) {
        (Value::Real(new), Value::Real(old)) => (*new, *old),
        other => panic!("{other:?}"),
    })
}

fn charges(outs: &[Out]) -> Vec<(f64, f64)> {
    outs.iter().filter_map(charge_of).collect()
}

#[test]
fn the_capacitor_recharges_along_ccps_curve() {
    // from a charge a tenth of a second on: the recorded first sample of the afterburner's charge
    assert!(
        (recharged(144.5, 149.5, 55.25, 0.1) - 144.5449).abs() < 1e-3,
        "{}",
        recharged(144.5, 149.5, 55.25, 0.1)
    );
    // nothing to wait for when it is full, nothing to recharge without time or a recharge time, and never past the capacity
    assert_eq!(recharged(149.5, 149.5, 55.25, 10.0), 149.5);
    assert_eq!(recharged(100.0, 149.5, 55.25, 0.0), 100.0);
    assert_eq!(recharged(100.0, 149.5, 0.0, 10.0), 100.0);
    assert!(recharged(0.0, 149.5, 55.25, 1e6) <= 149.5);
    // it recharges fastest at about a quarter of the capacity, and a long time fills it
    let (low, mid) = (recharged(1.0, 149.5, 55.25, 1.0) - 1.0, recharged(37.0, 149.5, 55.25, 1.0) - 37.0);
    assert!(mid > low, "{mid} {low}");
    assert!((recharged(0.0, 149.5, 55.25, 600.0) - 149.5).abs() < 0.01);
    // the peak is 10 * capacity / (4 * recharge time) a second, 2.5 * capacity / recharge time
    assert!((recharged(37.375, 149.5, 55.25, 0.001) - 37.375) / 0.001 - 2.5 * 149.5 / 55.25 < 0.05);
}

#[test]
fn an_effect_started_takes_capacitor_changes_the_ship_and_tells_the_client_in_the_reference_order() {
    let (mut scene, t) = crowd();
    let out = scene.activate(A, afterburner(), t);
    assert_eq!(
        labels(&out),
        [
            "7:accepted 17",
            "7:attributes[18]",
            "7:destiny[SetBallMass,SetMaxSpeed]",
            "8:destiny[SetBallMass,SetMaxSpeed]",
            "7:attributes[20,73,37]",
            "7:destiny[OnSpecialFX]",
            "8:destiny[OnSpecialFX]",
            "7:OnGodmaShipEffect",
        ]
    );
    assert_eq!(charges(&out), [(144.5, 149.5)]);
    let ship = scene.ship(A, t).unwrap();
    assert_eq!((ship.spec.mass, ship.spec.max_velocity), (1_663_000.0, 522.848918));
    // both updates are at one stamp
    let stamps: Vec<i64> = out
        .iter()
        .filter(|o| label(o).starts_with("7:destiny"))
        .flat_map(|o| match o {
            Out::Notify { args, .. } => {
                let Some(Value::List(actions)) = args.first() else { panic!() };
                actions
                    .iter()
                    .map(|a| match a {
                        Value::Tuple(pair) => match &pair[0] {
                            Value::Int(n) => *n,
                            other => panic!("{other:?}"),
                        },
                        other => panic!("{other:?}"),
                    })
                    .collect::<Vec<_>>()
            }
            _ => Vec::new(),
        })
        .collect();
    assert!(stamps.windows(2).all(|w| w[0] == w[1]), "{stamps:?}");
}

#[test]
fn the_effect_says_which_module_which_ship_and_for_how_long_and_the_values_the_client_is_told_are_typed_as_the_reference_typed_them()
 {
    let (mut scene, t) = crowd();
    let out = scene.activate(A, afterburner(), t);
    let Some(Out::Notify { args, .. }) =
        out.iter().find(|o| matches!(o, Out::Notify { name: "OnGodmaShipEffect", .. }))
    else {
        panic!("{out:?}")
    };
    let ship = crate::wire::id(spec().ship_id);
    assert_eq!(args[0], crate::wire::id(MODULE));
    assert_eq!(args[1], Value::Int(6731));
    assert_eq!((&args[3], &args[4]), (&Value::Int(1), &Value::Int(1)), "started and active");
    assert_eq!(args[7], Value::Real(10_000.0));
    assert_eq!(args[8], Value::Int(1000), "the cycles asked for");
    let Value::Tuple(environment) = &args[5] else { panic!() };
    assert_eq!(environment[0], crate::wire::id(MODULE));
    assert_eq!(environment[2], ship);
    assert_eq!(environment[3], Value::None, "no target");
    // a whole value is an int, but the length of a cycle is always a real
    let Some(Out::Notify { args, .. }) = out.iter().find(|o| label(o) == "7:attributes[20,73,37]") else {
        panic!()
    };
    let Some(Value::List(lines)) = args.first() else { panic!() };
    let values: Vec<&Value> = lines
        .iter()
        .map(|l| match l {
            Value::Tuple(t) => &t[5],
            other => panic!("{other:?}"),
        })
        .collect();
    assert_eq!(values, [&Value::Int(60), &Value::Real(10_000.0), &Value::Real(522.848918)]);
}

#[test]
fn the_effect_played_on_the_ship_says_it_is_on_when_it_starts_and_off_when_it_ends_and_names_the_module_the_target_and_the_length()
 {
    fn fx_args(out: &[Out], session: SessionId) -> Vec<Vec<Value>> {
        out.iter()
            .filter_map(|o| match o {
                Out::Notify { session: s, name: "DoDestinyUpdate", args, .. } if *s == session => {
                    let Some(Value::List(actions)) = args.first() else { return None };
                    actions.iter().find_map(|a| {
                        let Value::Tuple(pair) = a else { return None };
                        let Value::Tuple(inner) = &pair[1] else { return None };
                        let Value::Str(name) = &inner[0] else { return None };
                        let Value::Tuple(arguments) = &inner[1] else { return None };
                        (name == "OnSpecialFX").then(|| arguments.clone())
                    })
                }
                _ => None,
            })
            .collect()
    }
    let (mut scene, t) = crowd();
    let mut effect = afterburner();
    effect.target_id = 9_988_400_002_215;
    let started = fx_args(&scene.activate(A, effect, t), 8);
    let ship = crate::wire::id(spec().ship_id);
    assert_eq!(
        started,
        [vec![
            ship.clone(),
            crate::wire::id(MODULE),
            Value::Int(21857),
            crate::wire::id(9_988_400_002_215),
            Value::None,
            Value::Str("effects.Afterburner".into()),
            Value::Int(0),
            Value::Int(1),
            Value::Int(1),
            Value::Int(10_000),
            Value::None,
            Value::None,
            Value::Int(0),
            Value::None,
        ]]
    );
    scene.deactivate(A, MODULE, t + SECOND);
    let (end, _) = poll_until(&mut scene, t, 10.5);
    let ended = fx_args(&end, 8);
    assert_eq!(ended.len(), 1, "{end:?}");
    assert_eq!(ended[0][1..3], [crate::wire::id(MODULE), Value::Int(21857)]);
    assert_eq!((&ended[0][7], &ended[0][8]), (&Value::Int(0), &Value::Int(0)), "off and not active");
    // the target is in the environment of the effect the pilot is told of
    let Some(Out::Notify { args, .. }) =
        end.iter().find(|o| matches!(o, Out::Notify { name: "OnGodmaShipEffect", .. }))
    else {
        panic!()
    };
    let Value::Tuple(environment) = &args[5] else { panic!() };
    assert_eq!(environment[3], crate::wire::id(9_988_400_002_215));
}

#[test]
fn a_ship_that_is_told_to_start_an_effect_it_is_running_is_refused() {
    let (mut scene, t) = crowd();
    scene.activate(A, afterburner(), t);
    assert_eq!(labels(&scene.activate(A, afterburner(), t + SECOND)), ["7:refused 17 ALREADY_ACTIVE"]);
    // a ship that is not in space has no modules to start
    assert_eq!(labels(&scene.activate(99, afterburner(), t)), ["99:refused 17 NO_SHIP_IN_SPACE"]);
}

#[test]
fn an_effect_the_capacitor_cannot_pay_for_is_refused_and_costs_nothing() {
    let (mut scene, t) = crowd();
    let mut greedy = afterburner();
    greedy.capacitor_need = 150.0;
    assert_eq!(labels(&scene.activate(A, greedy, t)), ["7:refused 17 NOT_ENOUGH_CAPACITOR"]);
    // it costs exactly what it holds, and no more
    let mut all = afterburner();
    all.capacitor_need = 149.5;
    assert_eq!(labels(&scene.activate(A, all, t))[0], "7:accepted 17");
}

#[test]
fn an_effect_with_no_cycle_or_no_cycles_is_not_one() {
    let (mut scene, t) = crowd();
    let mut quick = afterburner();
    quick.duration_ms = 0.0;
    assert_eq!(labels(&scene.activate(A, quick, t)), ["7:refused 17 BAD_EFFECT"]);
    let mut none = afterburner();
    none.repeat = 0;
    assert_eq!(labels(&scene.activate(A, none, t)), ["7:refused 17 BAD_EFFECT"]);
}

#[test]
fn starting_an_effect_ends_the_protection_of_a_ship_that_has_just_undocked() {
    let mut scene = Scene::new(Arc::new((*system()).clone()));
    scene.undock(A, spec(), STATION, NOON).unwrap();
    scene.undock(B, other_spec(), STATION, NOON).unwrap();
    let t = NOON + 5 * SECOND;
    assert!(scene.pilots[&A].protected_until > t);
    scene.activate(A, afterburner(), t);
    assert_eq!(scene.pilots[&A].protected_until, 0);
    assert!(scene.pilots[&B].protected_until > t);
}

#[test]
fn a_cycle_that_ends_begins_the_next_and_takes_capacitor_again() {
    let (mut scene, t) = crowd();
    scene.activate(A, afterburner(), t);
    let (before, t2) = poll_until(&mut scene, t, 9.5);
    assert!(
        labels(&before).iter().all(|l| l == "7:attributes[18]"),
        "only the capacitor's charge until the cycle ends: {:?}",
        labels(&before)
    );
    let (after, _) = poll_until(&mut scene, t2, 1.0);
    let told = labels(&after);
    let at =
        told.iter().position(|l| l == "7:OnGodmaShipEffect").expect("the effect goes on for another cycle");
    assert_eq!(told[at - 1], "7:attributes[18]", "the capacitor is drawn just before");
    let drawn = charges(&after);
    let (new, old) = drawn[told[..at].iter().filter(|l| *l == "7:attributes[18]").count() - 1];
    assert!(
        (old - new - 5.0).abs() < 0.2,
        "a cycle costs 5, less what it recharged in between: {old} to {new}"
    );
    // the ship goes on with the effect: its numbers are the same
    assert_eq!(scene.ship(A, t + 11 * SECOND).unwrap().spec.mass, 1_663_000.0);
}

#[test]
fn a_new_cycle_says_when_it_began_and_the_same_effect_again() {
    let (mut scene, t) = crowd();
    scene.activate(A, afterburner(), t);
    let (out, _) = poll_until(&mut scene, t, 10.5);
    let Some(Out::Notify { args, .. }) =
        out.iter().find(|o| matches!(o, Out::Notify { name: "OnGodmaShipEffect", .. }))
    else {
        panic!("{out:?}")
    };
    // when and started are the moment the cycle began: exactly ten seconds after the first
    assert_eq!(args[2], Value::Long(t + 10 * SECOND));
    assert_eq!(args[6], Value::Long(t + 10 * SECOND));
    assert_eq!((&args[3], &args[4], &args[8]), (&Value::Int(1), &Value::Int(1), &Value::Int(1000)));
}

#[test]
fn an_effect_stops_when_it_has_run_the_cycles_asked_for() {
    let (mut scene, t) = crowd();
    let mut once = afterburner();
    once.repeat = 2;
    scene.activate(A, once, t);
    let (out, _) = poll_until(&mut scene, t, 22.0);
    let told = labels(&out);
    assert_eq!(
        told.iter().filter(|l| *l == "7:OnGodmaShipEffect").count(),
        2,
        "one more cycle, then it is off: {told:?}"
    );
    assert_eq!(scene.ship(A, t + 23 * SECOND).unwrap().spec.mass, 1_163_000.0);
}

#[test]
fn an_effect_stops_when_the_capacitor_cannot_pay_for_the_next_cycle() {
    let (mut scene, t) = crowd();
    let mut all = afterburner();
    all.capacitor_need = 149.0; // the first cycle nearly empties it, and it recharges too slowly to pay for another in ten seconds
    scene.activate(A, all, t);
    let (out, _) = poll_until(&mut scene, t, 11.0);
    let told = labels(&out);
    assert!(told.iter().any(|l| l == "7:OnGodmaShipEffect"), "{told:?}");
    assert_eq!(
        scene.ship(A, t + 11 * SECOND).unwrap().spec.mass,
        1_163_000.0,
        "the ship has its own numbers again"
    );
}

#[test]
fn asking_for_an_effect_to_stop_says_so_and_it_stops_at_the_end_of_the_cycle_it_is_in() {
    let (mut scene, t) = crowd();
    scene.activate(A, afterburner(), t);
    let (_, t2) = poll_until(&mut scene, t, 4.0);
    assert_eq!(labels(&scene.deactivate(A, MODULE, t2)), ["7:accepted 16"]);
    // nothing changes until the cycle is over, six seconds on
    let (during, t3) = poll_until(&mut scene, t2, 5.5);
    assert!(labels(&during).iter().all(|l| l == "7:attributes[18]"), "{:?}", labels(&during));
    assert_eq!(scene.ship(A, t3).unwrap().spec.max_velocity, 522.848918);
    let (end, _) = poll_until(&mut scene, t3, 1.0);
    let told: Vec<String> = labels(&end).into_iter().filter(|l| l != "7:attributes[18]").collect();
    assert_eq!(
        told,
        [
            "7:attributes[20,73,37]",
            "7:OnGodmaShipEffect",
            "7:destiny[SetBallMass,SetMaxSpeed,OnSpecialFX]",
            "8:destiny[SetBallMass,SetMaxSpeed,OnSpecialFX]"
        ]
    );
    let ship = scene.ship(A, t + 12 * SECOND).unwrap();
    assert_eq!((ship.spec.mass, ship.spec.max_velocity), (1_163_000.0, 339.25));
    // the effect that ended says it is off, with the start of the cycle it ended
    let Some(Out::Notify { args, .. }) =
        end.iter().find(|o| matches!(o, Out::Notify { name: "OnGodmaShipEffect", .. }))
    else {
        panic!()
    };
    assert_eq!((&args[3], &args[4], &args[8]), (&Value::Int(0), &Value::Int(0), &Value::Bool(false)));
    assert_eq!(args[2], Value::Long(t + 10 * SECOND));
    // and nothing costs capacitor any more
    assert_eq!(labels(&scene.deactivate(A, MODULE, t + 13 * SECOND)), ["7:refused 16 NOT_ACTIVE"]);
}

#[test]
fn a_ship_that_is_not_running_the_effect_or_not_in_space_has_nothing_to_stop() {
    let (mut scene, t) = crowd();
    assert_eq!(labels(&scene.deactivate(A, MODULE, t)), ["7:refused 16 NOT_ACTIVE"]);
    assert_eq!(labels(&scene.deactivate(99, MODULE, t)), ["99:refused 16 NO_SHIP_IN_SPACE"]);
}

#[test]
fn the_charge_is_told_at_once_and_then_every_half_second_a_tenth_of_a_second_on_until_it_is_full() {
    let (mut scene, t) = crowd();
    let mut once = afterburner();
    once.repeat = 1;
    let first = scene.activate(A, once, t);
    let (out, _) = poll_until(&mut scene, t, 3.0);
    let series: Vec<(f64, f64)> = charges(&first).into_iter().chain(charges(&out)).collect();
    assert!(series.len() >= 6, "{series:?}");
    assert_eq!(series[0], (144.5, 149.5));
    // then a sample of the curve at 0.1 s, 0.6 s, 1.1 s…, each with the value a tenth of a second before
    for (k, (new, old)) in series[1..6].iter().enumerate() {
        let seconds = 0.1 + 0.5 * k as f64;
        assert!((new - recharged(144.5, 149.5, 55.25, seconds)).abs() < 1e-6, "{k}: {new}");
        assert!((old - recharged(144.5, 149.5, 55.25, (seconds - 0.1).max(0.0))).abs() < 1e-6, "{k}: {old}");
    }
}

#[test]
fn a_capacitor_within_a_gigajoule_of_full_is_full_and_that_is_not_told() {
    let cap = Capacitor { capacity: 149.5, recharge_s: 55.25, charge: 148.6, at: NOON, next_sample: None };
    assert_eq!(cap.charge_at(NOON), 149.5);
    let cap = Capacitor { charge: 148.4, ..cap };
    assert!(cap.charge_at(NOON) < 149.5);
    // a ship that draws a little and recharges past the threshold stops being told without a last word
    let (mut scene, t) = crowd();
    let mut small = afterburner();
    small.capacitor_need = 2.0;
    small.repeat = 1;
    scene.activate(A, small, t);
    let (out, _) = poll_until(&mut scene, t, 60.0);
    let told = charges(&out);
    assert!(told.iter().all(|(new, _)| *new < 149.5 - 1.0), "{told:?}");
    assert!(scene.pilots[&A].modules.is_idle());
}

#[test]
fn a_full_capacitor_is_not_told_and_the_telling_stops_when_it_is_full() {
    let (mut scene, t) = crowd();
    let (quiet, _) = poll_until(&mut scene, t, 5.0);
    assert!(charges(&quiet).is_empty(), "{quiet:?}");
    // a small draw fills up again in a while, and then nothing more is told
    let mut small = afterburner();
    small.capacitor_need = 3.0;
    small.repeat = 1;
    scene.activate(A, small, t + 5 * SECOND);
    let (recharging, t2) = poll_until(&mut scene, t + 5 * SECOND, 600.0);
    assert!(!charges(&recharging).is_empty());
    let (after, _) = poll_until(&mut scene, t2, 5.0);
    assert!(charges(&after).is_empty(), "{after:?}");
    assert!(scene.pilots[&A].modules.is_idle());
}

#[test]
fn only_the_pilot_is_told_of_the_effect_the_ones_who_look_on_see_the_ship_change() {
    let (mut scene, t) = crowd();
    let out = scene.activate(A, afterburner(), t);
    for o in &out {
        if let Out::Notify { session: 8, name, .. } = o {
            assert_eq!(*name, "DoDestinyUpdate");
        }
    }
    let (later, _) = poll_until(&mut scene, t, 3.0);
    assert!(later.iter().all(|o| !matches!(o, Out::Notify { session: 8, .. })), "{later:?}");
}

#[test]
fn a_ship_that_goes_faster_with_the_effect_gathers_speed_for_the_new_top_speed() {
    let (mut scene, t) = crowd();
    scene.command(A, Command::GotoDirection(eve_destiny::Vec3::new(1.0, 0.0, 0.0)), t);
    let (_, t) = poll_until(&mut scene, t, 60.0);
    let before = scene.ship(A, t).unwrap().velocity;
    let speed = |v: eve_destiny::Vec3| (v.x * v.x + v.y * v.y + v.z * v.z).sqrt();
    assert!((speed(before) - 339.25).abs() < 1.0, "{}", speed(before));
    scene.activate(A, afterburner(), t);
    let (_, t) = poll_until(&mut scene, t, 9.0);
    assert!(
        speed(scene.ship(A, t).unwrap().velocity) > 450.0,
        "{}",
        speed(scene.ship(A, t).unwrap().velocity)
    );
}

#[test]
fn a_ship_that_leaves_takes_its_effects_with_it() {
    let (mut scene, t) = crowd();
    scene.activate(A, afterburner(), t);
    scene.leave(A, crate::scene::Departure::LoggedOff, t + SECOND);
    let (out, _) = poll_until(&mut scene, t + SECOND, 12.0);
    assert!(out.iter().all(|o| !matches!(o, Out::Notify { session: 7, .. })), "{out:?}");
}

/// An armor repairer of the Ibis: 30 points a cycle of five seconds, applied at the end of the cycle.
fn repairer(layer: Layer, amount: f64, when: Applies) -> Effect {
    Effect {
        rules: Default::default(),
        burns_fuel: false,
        module_id: MODULE,
        module_type: 3530,
        effect_id: 27,
        fx: "effects.ArmorRepair".to_string(),
        duration_ms: 5_000.0,
        capacitor_need: 20.0,
        repeat: 1000,
        target_id: 0,
        turret: None,
        action: Some(Action::Repair { layer, amount, when }),
        on: None,
        off: None,
        on_changes: Vec::new(),
        off_changes: Vec::new(),
    }
}

/// A ship that has lost its shield and half its armor.
fn hurt() -> (Scene, i64) {
    let (mut scene, t) = crowd();
    // raw damage the layers' resistances let through: enough to strip the 220 shield and 68.75 of the armor
    let raw = [0.0, 600.0, 0.0, 0.0];
    let (_, applied) = scene.damage(spec().ship_id, raw, t);
    assert!(applied.is_some());
    (scene, t)
}

fn layers(scene: &Scene, at: i64) -> crate::health::Layers {
    scene.pilots[&A].ship.health.layers_at(at)
}

#[test]
fn an_armor_repairer_mends_the_armor_when_a_cycle_ends_and_tells_the_pilot_and_those_who_look_on() {
    let (mut scene, t) = hurt();
    let armor_before = layers(&scene, t).armor;
    assert!(armor_before < 137.5, "the armor took some: {armor_before}");
    let started = scene.activate(A, repairer(Layer::Armor, 30.0, Applies::CycleEnd), t);
    assert!(matches!(started.first(), Some(Out::Accepted { command: 17, .. })), "{started:?}");
    // nothing is mended while the cycle runs
    assert_eq!(layers(&scene, t + SECOND).armor, armor_before);
    let (out, after) = poll_until(&mut scene, t, 5.1);
    let armor = layers(&scene, after).armor;
    assert!((armor - (armor_before + 30.0).min(137.5)).abs() < 1e-3, "{armor} from {armor_before}");
    // the pilot is told the armor that is gone (attribute 266), those who look on its damage state
    let told = labels(&out);
    assert!(told.iter().any(|l| l.starts_with("7:attributes[") && l.contains("266")), "{told:?}");
    assert!(told.iter().any(|l| l.contains("OnDamageStateChange") || l.contains("destiny")), "{told:?}");
    assert!(told.iter().any(|l| l.starts_with("8:destiny[")), "the one who looks on is told: {told:?}");
}

#[test]
fn ancillary_repair_consumes_loaded_charges_each_cycle_then_uses_its_fallback() {
    let (mut scene, t) = hurt();
    let before = layers(&scene, t).armor;
    let mut effect = repairer(Layer::Armor, 10.0, Applies::CycleEnd);
    effect.repeat = 3;
    effect.action = Some(Action::Charged {
        action: Box::new(Action::Repair { layer: Layer::Armor, amount: 10.0, when: Applies::CycleEnd }),
        fallback: Some(Box::new(Action::Repair {
            layer: Layer::Armor,
            amount: 5.0,
            when: Applies::CycleEnd,
        })),
        charge_type_id: 91001,
        charge_flag: 27,
        charge_per_cycle: 1,
        charges: 2,
    });
    let started = scene.activate(A, effect, t);
    assert!(
        started
            .iter()
            .any(|event| matches!(event, Out::ChargeSpent { module_id: MODULE, rounds_left: 1, .. }))
    );
    let (spent, first_cycle) = poll_until(&mut scene, t, 5.1);
    let after_first = layers(&scene, first_cycle).armor;
    assert!(
        spent.iter().any(|event| matches!(event, Out::ChargeSpent { module_id: MODULE, rounds_left: 0, .. }))
    );
    assert!((after_first - (before + 10.0).min(137.5)).abs() < 1e-3);
    let (_, second_cycle) = poll_until(&mut scene, first_cycle, 5.1);
    let after_second = layers(&scene, second_cycle).armor;
    assert!((after_second - (after_first + 10.0).min(137.5)).abs() < 1e-3);
    let (third, third_cycle) = poll_until(&mut scene, second_cycle, 5.1);
    assert!(!third.iter().any(|event| matches!(event, Out::ChargeSpent { module_id: MODULE, .. })));
    let after_third = layers(&scene, third_cycle).armor;
    assert!((after_third - (after_second + 5.0).min(137.5)).abs() < 1e-3);
}

#[test]
fn an_active_cloak_hides_a_ship_from_locking_until_the_module_ends() {
    let (mut scene, t) = reaching(5_000.0);
    let ship_id = scene.pilots[&A].ship.spec.ship_id;
    let target_id = scene.pilots[&B].ship.spec.ship_id;
    scene.target_order(A, crate::targeting::Order::Clear, t);
    scene.target_order(B, crate::targeting::Order::Clear, t);
    let mut effect = repairer(Layer::Armor, 1.0, Applies::CycleStart);
    effect.module_type = 11577;
    effect.effect_id = 197;
    effect.fx = "effects.Cloaking".to_string();
    effect.action = Some(Action::Cloak { warp_safe: false });
    let started = scene.activate(A, effect, t);
    assert!(
        started.iter().any(|event| matches!(event, Out::Accepted { command: 17, .. })),
        "{:?}",
        labels(&started)
    );
    assert_eq!(scene.pilots[&A].cloak, crate::visibility::CloakState::Cloaked);
    for (session, target) in [(B, ship_id), (A, target_id)] {
        let refused = scene.target_order(session, crate::targeting::Order::Lock(target), t);
        assert!(
            refused
                .iter()
                .any(|event| matches!(event, Out::TargetRefused { reason: "TARGET_NOT_FOUND", .. }))
        );
    }
    let (_, cloaked_at) = poll_until(&mut scene, t, 1.1);
    assert!(!scene.pilots[&B].sees.contains(&A));
    scene.deactivate(A, MODULE, cloaked_at);
    let (_, ended) = poll_until(&mut scene, cloaked_at, 5.1);
    assert_eq!(scene.pilots[&A].cloak, crate::visibility::CloakState::Uncloaked);
    assert!(scene.pilots[&B].sees.contains(&A));
    assert!(ended >= cloaked_at);
}

#[test]
fn a_power_booster_transfers_to_its_own_ship_and_spends_one_charge_per_cycle() {
    let (mut scene, t) = reaching(2_000.0);
    let ship_id = scene.pilots[&A].ship.spec.ship_id;
    let effect = Effect {
        rules: Default::default(),
        burns_fuel: false,
        module_id: MODULE,
        module_type: 3566,
        effect_id: 13,
        fx: "effects.CapacitorBooster".to_string(),
        duration_ms: 5_000.0,
        capacitor_need: 0.0,
        repeat: 2,
        target_id: ship_id,
        turret: None,
        action: Some(Action::Charged {
            action: Box::new(Action::Remote {
                what: Remote::Transfer { amount: 25.0 },
                reach: Reach { optimal: 0.0, falloff: 0.0 },
                when: Applies::CycleStart,
                jam: "energyTransfer".to_string(),
            }),
            fallback: None,
            charge_type_id: 91002,
            charge_flag: 27,
            charge_per_cycle: 1,
            charges: 2,
        }),
        on: None,
        off: None,
        on_changes: Vec::new(),
        off_changes: Vec::new(),
    };
    let started = scene.activate(A, effect, t);
    assert!(started.iter().any(|event| matches!(event, Out::Accepted { command: 17, .. })));
    assert!(
        started
            .iter()
            .any(|event| matches!(event, Out::ChargeSpent { module_id: MODULE, rounds_left: 1, .. }))
    );
    let (second, later) = poll_until(&mut scene, t, 5.1);
    assert!(
        second
            .iter()
            .any(|event| matches!(event, Out::ChargeSpent { module_id: MODULE, rounds_left: 0, .. })),
        "{:?}",
        labels(&second)
    );
    assert!(!scene.pilots[&A].modules.running.iter().any(|run| run.effect.module_id == MODULE));
    assert!(scene.pilots[&A].modules.capacitor.as_ref().unwrap().charge_at(later) > 0.0);
}

#[test]
fn a_shield_booster_gives_its_points_as_the_cycle_begins_and_again_at_each_cycle() {
    let (mut scene, t) = hurt();
    let before = layers(&scene, t).shield;
    let out = scene.activate(A, repairer(Layer::Shield, 40.0, Applies::CycleStart), t);
    // at once, in the answer to the activation
    let shield = layers(&scene, t).shield;
    assert!((shield - (before + 40.0)).abs() < 1e-3, "{shield} from {before}");
    assert!(
        labels(&out).iter().any(|l| l.starts_with("7:attributes[") && l.contains("264")),
        "{:?}",
        labels(&out)
    );
    // and again as the next cycle begins
    let (_, after) = poll_until(&mut scene, t, 5.1);
    let second = layers(&scene, after).shield;
    assert!(second >= shield + 39.0, "{second} from {shield}");
}

#[test]
fn a_repair_gives_back_no_more_than_the_layer_holds_and_a_whole_ship_is_told_nothing_of_it() {
    let (mut scene, t) = crowd();
    scene.activate(A, repairer(Layer::Armor, 30.0, Applies::CycleEnd), t);
    let (out, after) = poll_until(&mut scene, t, 5.1);
    assert_eq!(layers(&scene, after).armor, 137.5);
    let told = labels(&out);
    assert!(!told.iter().any(|l| l.contains("266") || l.contains("OnDamageStateChange")), "{told:?}");
    // the capacitor is still drawn for each cycle: a repairer that has nothing to mend still runs
    assert!(told.iter().any(|l| l == "7:OnGodmaShipEffect"), "{told:?}");
}

#[test]
fn a_repairer_the_pilot_stops_still_mends_at_the_end_of_the_cycle_it_is_in() {
    let (mut scene, t) = hurt();
    let before = layers(&scene, t).armor;
    scene.activate(A, repairer(Layer::Armor, 30.0, Applies::CycleEnd), t);
    scene.deactivate(A, MODULE, t + SECOND);
    let (_, after) = poll_until(&mut scene, t, 5.1);
    assert!((layers(&scene, after).armor - (before + 30.0)).abs() < 1e-3);
    assert_eq!(scene.pilots[&A].modules.running_count(), 0, "and it has stopped");
}

// ---- effects projected at a ship ----

fn partner() -> i64 {
    other_spec().ship_id
}

/// A module that runs `what` on the ship `target`, reaching 10 km and falling off through another 5 km.
fn projected(what: Remote, when: Applies, target: i64) -> Effect {
    Effect {
        rules: Default::default(),
        burns_fuel: false,
        module_id: MODULE,
        module_type: 3520,
        effect_id: 6186,
        fx: "effects.RemoteArmorRepair".to_string(),
        duration_ms: 5_000.0,
        capacitor_need: 20.0,
        repeat: 1000,
        target_id: target,
        turret: None,
        action: Some(Action::Remote {
            what,
            reach: Reach { optimal: 10_000.0, falloff: 5_000.0 },
            when,
            jam: "remoteArmorRepair".to_string(),
        }),
        on: None,
        off: None,
        on_changes: Vec::new(),
        off_changes: Vec::new(),
    }
}

/// B stands `gap` metres off A's surface and A has it locked: the scene, and the moment.
fn reaching(gap: f64) -> (Scene, i64) {
    reaching_in(crowd(), gap)
}

fn reaching_in((mut scene, t): (Scene, i64), gap: f64) -> (Scene, i64) {
    // both ships are brought to rest so that nothing moves while the effect runs
    scene.command(A, Command::Stop, t);
    scene.command(B, Command::Stop, t);
    let (_, t) = poll_until(&mut scene, t, 40.0);
    let here = scene.pilots[&A].ship.position;
    let (ra, rb) = (scene.pilots[&A].ship.spec.radius, scene.pilots[&B].ship.spec.radius);
    let ship = &mut scene.pilots.get_mut(&B).unwrap().ship;
    ship.position = eve_destiny::Vec3 { x: here.x + gap + ra + rb, y: here.y, z: here.z };
    ship.velocity = eve_destiny::Vec3 { x: 0.0, y: 0.0, z: 0.0 };
    let asked = scene.target_order(A, crate::targeting::Order::Lock(partner()), t);
    assert!(matches!(asked.first(), Some(Out::Targets { .. })), "{asked:?}");
    let (_, t) = poll_until(&mut scene, t, 6.0);
    assert_eq!(scene.locked_by(A), vec![partner()]);
    (scene, t)
}

fn hurt_partner(scene: &mut Scene, t: i64) {
    let (_, applied) = scene.damage(partner(), [0.0, 600.0, 0.0, 0.0], t);
    assert!(applied.is_some());
}

fn partner_layers(scene: &Scene, at: i64) -> crate::health::Layers {
    scene.pilots[&B].ship.health.layers_at(at)
}

fn charge(scene: &Scene, who: SessionId, at: i64) -> f64 {
    scene.pilots[&who].modules.capacitor.as_ref().unwrap().charge_at(at)
}

#[test]
fn a_projected_effect_is_whole_in_the_optimal_range_half_at_one_falloff_and_out_of_range_beyond_it() {
    let reach = Reach { optimal: 10_000.0, falloff: 5_000.0 };
    assert_eq!(reach.share(0.0), Some(1.0));
    assert_eq!(reach.share(10_001.0), Some(1.0), "a metre of slack");
    assert_eq!(reach.share(15_000.0), Some(0.5));
    assert_eq!(reach.share(12_500.0), Some(0.840896));
    assert_eq!(reach.share(15_002.0), None);
    assert_eq!(reach.farthest(), 15_000.0);
    // a module with no falloff reaches its optimal range and no more
    let sharp = Reach { optimal: 9_000.0, falloff: 0.0 };
    assert_eq!(sharp.share(9_001.0), Some(1.0));
    assert_eq!(sharp.share(9_002.0), None);
}

#[test]
fn a_remote_armor_repairer_mends_the_target_when_the_cycle_ends_less_for_a_target_in_falloff() {
    // 12.5 km off: half way through the falloff, a share of 0.840896
    let (mut scene, t) = reaching(12_500.0);
    hurt_partner(&mut scene, t);
    let before = partner_layers(&scene, t).armor;
    let started = scene.activate(
        A,
        projected(Remote::Repair { layer: Layer::Armor, amount: 30.0 }, Applies::CycleEnd, partner()),
        t,
    );
    assert!(matches!(started.first(), Some(Out::Accepted { command: 17, .. })), "{started:?}");
    assert_eq!(partner_layers(&scene, t + SECOND).armor, before, "nothing is mended while the cycle runs");
    let (out, after) = poll_until(&mut scene, t, 5.1);
    let armor = partner_layers(&scene, after).armor;
    assert!((armor - (before + 30.0 * 0.840896)).abs() < 1e-3, "{armor} from {before}");
    // the target's pilot is told the armor that came back, the one who runs the repairer is told nothing of it
    let told = labels(&out);
    assert!(told.iter().any(|l| l.starts_with("8:attributes[") && l.contains("266")), "{told:?}");
    assert!(!told.iter().any(|l| l.starts_with("7:attributes[") && l.contains("266")), "{told:?}");
    // its own armor is not what is mended
    assert_eq!(layers(&scene, after).armor, 137.5);
}

fn remote_repair_cycle_request(out: &[Out]) -> (u64, i32, i64, i64, bool) {
    out.iter()
        .find_map(|item| match item {
            Out::RemoteRepairCycle {
                cycle_id,
                cycle_number,
                cycle_at_ms,
                cycle_duration_ms,
                ends_effect_after_cycle,
                ..
            } => Some((*cycle_id, *cycle_number, *cycle_at_ms, *cycle_duration_ms, *ends_effect_after_cycle)),
            _ => None,
        })
        .expect("remote repair cycle request")
}

#[test]
fn mutadaptive_remote_repair_waits_for_host_amount_on_every_cycle_and_marks_the_final_one() {
    let (mut scene, t) = reaching(10_000.0);
    hurt_partner(&mut scene, t);
    let before = partner_layers(&scene, t).armor;
    let mut effect = projected(
        Remote::MutadaptiveRepair { layer: Layer::Armor, amount: 10.0, bonus_per_cycle: 0.1, bonus_max: 1.5 },
        Applies::CycleEnd,
        partner(),
    );
    effect.duration_ms = 6_000.0;
    effect.capacitor_need = 0.0;
    effect.repeat = 3;
    let started = scene.activate(A, effect, t);
    assert!(matches!(started.first(), Some(Out::Accepted { command: 17, .. })), "{started:?}");

    let (out1, at1) = poll_until(&mut scene, t, 6.0);
    let (cycle1, number1, cycle_at1, duration1, ends1) = remote_repair_cycle_request(&out1);
    assert_eq!((number1, duration1, ends1), (1, 6_000, false));
    assert_eq!(partner_layers(&scene, at1).armor, before, "repair waits for the host result");
    assert!(scene.remote_repair_cycle_result(A, MODULE, partner(), cycle1 + 10, true, 999.0).is_empty());
    assert_eq!(partner_layers(&scene, at1).armor, before, "a stale result is ignored");
    scene.remote_repair_cycle_result(A, MODULE, partner(), cycle1, true, 11.0);
    assert!((partner_layers(&scene, at1).armor - (before + 11.0)).abs() < 1e-3);
    assert!(scene.remote_repair_cycle_result(A, MODULE, partner(), cycle1, true, 999.0).is_empty());
    assert!(
        (partner_layers(&scene, at1).armor - (before + 11.0)).abs() < 1e-3,
        "a replay cannot settle twice"
    );

    let (out2, at2) = poll_until(&mut scene, at1, 6.0);
    let (cycle2, number2, cycle_at2, duration2, ends2) = remote_repair_cycle_request(&out2);
    assert_eq!((number2, duration2, ends2), (2, 6_000, false));
    assert_eq!(cycle_at2 - cycle_at1, 6_000, "cycle timestamps use simulation milliseconds");
    scene.remote_repair_cycle_result(A, MODULE, partner(), cycle2, true, 12.0);
    let after_cycle2 = partner_layers(&scene, at2).armor;
    assert!((after_cycle2 - (before + 23.0)).abs() < 1e-3, "before={before}, after_cycle2={after_cycle2}");

    let (out3, at3) = poll_until(&mut scene, at2, 6.0);
    let (cycle3, number3, _, duration3, ends3) = remote_repair_cycle_request(&out3);
    assert_eq!((number3, duration3, ends3), (3, 6_000, true));
    scene.remote_repair_cycle_result(A, MODULE, partner(), cycle3, true, 13.0);
    assert!((partner_layers(&scene, at3).armor - (before + 36.0)).abs() < 1e-3);
    assert!(matches!(
        scene.deactivate(A, MODULE, at3).first(),
        Some(Out::Refused { reason: "NOT_ACTIVE", .. })
    ));
}

#[test]
fn deactivation_during_a_pending_mutadaptive_cycle_finishes_that_cycle_before_stopping() {
    let (mut scene, t) = reaching(10_000.0);
    hurt_partner(&mut scene, t);
    let before = partner_layers(&scene, t).armor;
    let mut effect = projected(
        Remote::MutadaptiveRepair {
            layer: Layer::Armor,
            amount: 100.0,
            bonus_per_cycle: 0.1,
            bonus_max: 1.5,
        },
        Applies::CycleEnd,
        partner(),
    );
    effect.duration_ms = 6_000.0;
    effect.capacitor_need = 0.0;
    let started = scene.activate(A, effect, t);
    assert!(matches!(started.first(), Some(Out::Accepted { .. })));
    let (out, at) = poll_until(&mut scene, t, 6.0);
    let (cycle_id, _, _, _, ends_after_cycle) = remote_repair_cycle_request(&out);
    assert!(!ends_after_cycle, "the stop request arrives after the cycle was dispatched");
    assert!(matches!(scene.deactivate(A, MODULE, at).first(), Some(Out::Accepted { command: 16, .. })));
    scene.remote_repair_cycle_result(A, MODULE, partner(), cycle_id, true, 110.0);
    assert!((partner_layers(&scene, at).armor - (before + 110.0)).abs() < 1e-3);
    assert!(matches!(
        scene.deactivate(A, MODULE, at).first(),
        Some(Out::Refused { reason: "NOT_ACTIVE", .. })
    ));
}

#[test]
fn deactivation_before_cycle_dispatch_marks_the_request_as_final() {
    let (mut scene, t) = reaching(10_000.0);
    hurt_partner(&mut scene, t);
    let before = partner_layers(&scene, t).armor;
    let mut effect = projected(
        Remote::MutadaptiveRepair {
            layer: Layer::Armor,
            amount: 100.0,
            bonus_per_cycle: 0.1,
            bonus_max: 1.5,
        },
        Applies::CycleEnd,
        partner(),
    );
    effect.duration_ms = 6_000.0;
    effect.capacitor_need = 0.0;
    let started = scene.activate(A, effect, t);
    assert!(matches!(started.first(), Some(Out::Accepted { .. })));
    assert!(matches!(scene.deactivate(A, MODULE, t + SECOND).first(), Some(Out::Accepted { .. })));
    let (out, at) = poll_until(&mut scene, t, 6.0);
    let (cycle_id, _, _, _, ends_after_cycle) = remote_repair_cycle_request(&out);
    assert!(ends_after_cycle);
    scene.remote_repair_cycle_result(A, MODULE, partner(), cycle_id, true, 110.0);
    assert!((partner_layers(&scene, at).armor - (before + 110.0)).abs() < 1e-3);
    assert!(matches!(
        scene.deactivate(A, MODULE, at).first(),
        Some(Out::Refused { reason: "NOT_ACTIVE", .. })
    ));
}

#[test]
fn offlining_a_mutadaptive_repairer_cancels_its_pending_callback_immediately() {
    let (mut scene, t) = reaching(10_000.0);
    hurt_partner(&mut scene, t);
    let before = partner_layers(&scene, t).armor;
    let mut effect = projected(
        Remote::MutadaptiveRepair {
            layer: Layer::Armor,
            amount: 100.0,
            bonus_per_cycle: 0.1,
            bonus_max: 1.5,
        },
        Applies::CycleEnd,
        partner(),
    );
    effect.duration_ms = 6_000.0;
    effect.capacitor_need = 0.0;
    let started = scene.activate(A, effect, t);
    assert!(matches!(started.first(), Some(Out::Accepted { .. })));
    let (out, at) = poll_until(&mut scene, t, 6.0);
    let (cycle_id, ..) = remote_repair_cycle_request(&out);
    scene.offline_module(A, MODULE, at);
    assert!(scene.remote_repair_cycle_result(A, MODULE, partner(), cycle_id, true, 999.0).is_empty());
    assert_eq!(partner_layers(&scene, at).armor, before, "offline cancels the in-flight repair immediately");
}

#[test]
fn a_remote_shield_booster_gives_its_points_as_the_cycle_begins() {
    let (mut scene, t) = reaching(2_000.0);
    hurt_partner(&mut scene, t);
    let before = partner_layers(&scene, t).shield;
    scene.activate(
        A,
        projected(Remote::Repair { layer: Layer::Shield, amount: 40.0 }, Applies::CycleStart, partner()),
        t,
    );
    let shield = partner_layers(&scene, t).shield;
    assert!(shield >= before + 39.0, "{shield} from {before}");
}

#[test]
fn an_effect_projected_at_a_ship_needs_a_target_that_is_there_locked_and_in_reach() {
    let (mut scene, t) = reaching(2_000.0);
    let refused = |out: &[Out]| match out.first() {
        Some(Out::Refused { reason, .. }) => (*reason).to_string(),
        other => panic!("{other:?}"),
    };
    let mend =
        |target| projected(Remote::Repair { layer: Layer::Armor, amount: 30.0 }, Applies::CycleEnd, target);
    assert_eq!(refused(&scene.activate(A, mend(0), t)), "NO_TARGET");
    assert_eq!(refused(&scene.activate(A, mend(42), t)), "TARGET_NOT_FOUND");
    // the ship that is not locked
    let (mut scene2, t2) = crowd();
    assert_eq!(refused(&scene2.activate(A, mend(partner()), t2)), "TARGET_NOT_LOCKED");
    // too far off: told with the farthest it reaches and how far the target is
    let (mut far, t3) = reaching(16_000.0);
    match far.activate(A, mend(partner()), t3).first() {
        Some(Out::TargetRefused {
            command: 17, reason, target_type, desired_range, actual_distance, ..
        }) => {
            assert_eq!(*reason, "TARGET_OUT_OF_RANGE");
            assert_eq!(*target_type, 601);
            assert_eq!(*desired_range, 15_000.0);
            assert!((actual_distance - 16_000.0).abs() < 1.0, "{actual_distance}");
        }
        other => panic!("{other:?}"),
    }
    assert_eq!(far.pilots[&A].modules.running_count(), 0);
}

#[test]
fn a_target_that_drifts_out_of_reach_ends_the_effect_at_the_end_of_the_cycle_and_a_lost_lock_ends_it_at_once()
{
    let (mut scene, t) = reaching(2_000.0);
    scene.activate(
        A,
        projected(Remote::Repair { layer: Layer::Armor, amount: 30.0 }, Applies::CycleEnd, partner()),
        t,
    );
    // the target is pulled out of reach during the cycle
    scene.pilots.get_mut(&B).unwrap().ship.position.x += 40_000.0;
    let before = partner_layers(&scene, t).armor;
    let (_, after) = poll_until(&mut scene, t, 5.1);
    assert_eq!(partner_layers(&scene, after).armor, before, "nothing lands out of reach");
    assert_eq!(scene.pilots[&A].modules.running_count(), 0, "and the effect has stopped");
    // a lock that is given up stops it at once
    let (mut scene, t) = reaching(2_000.0);
    scene.activate(
        A,
        projected(Remote::Repair { layer: Layer::Armor, amount: 30.0 }, Applies::CycleEnd, partner()),
        t,
    );
    scene.target_order(A, crate::targeting::Order::Remove(vec![partner()]), t + SECOND);
    assert_eq!(scene.pilots[&A].modules.running_count(), 0);
}

#[test]
fn a_neutralizer_is_weakened_by_resistance_and_immunity_keeps_the_capacitor() {
    let (mut scene, t) = reaching(5_000.0);
    scene.pilots.get_mut(&B).unwrap().ship.spec.energy_warfare_resistance = 0.5;
    let before = charge(&scene, B, t);
    scene.activate(
        A,
        projected(
            Remote::Neutralize { amount: 40.0, signature_resolution: 0.0 },
            Applies::CycleStart,
            partner(),
        ),
        t,
    );
    let after = charge(&scene, B, t);
    assert!(
        (before - after - 20.0).abs() < 1e-6,
        "half of 40 against a resistance of 0.5: {before} -> {after}"
    );

    scene.pilots.get_mut(&B).unwrap().ship.spec.energy_warfare_resistance = 0.0;
    let immune_before = charge(&scene, B, t + SECOND);
    scene.activate(
        A,
        projected(
            Remote::Neutralize { amount: 40.0, signature_resolution: 0.0 },
            Applies::CycleStart,
            partner(),
        ),
        t + SECOND,
    );
    assert!(
        (charge(&scene, B, t + SECOND) - immune_before).abs() < 1e-6,
        "an immune target keeps its capacitor"
    );
}

#[test]
fn an_energy_neutralizer_takes_capacitor_from_the_target_as_the_cycle_begins_and_is_an_attack() {
    let (mut scene, t) = reaching(5_000.0);
    let before = charge(&scene, B, t);
    let started = scene.activate(
        A,
        projected(
            Remote::Neutralize { amount: 40.0, signature_resolution: 0.0 },
            Applies::CycleStart,
            partner(),
        ),
        t,
    );
    let after = charge(&scene, B, t);
    assert!((before - after - 40.0).abs() < 1e-6, "{before} -> {after}");
    // the target's pilot is told its capacitor, and the effect is played as an attack
    assert!(started.iter().any(|o| o.session() == B && charge_of(o).is_some()), "{started:?}");
    assert_eq!(scene.aggressions.len(), 1);
    assert_eq!(
        (scene.aggressions[0].attacker_ship, scene.aggressions[0].target_ship),
        (spec().ship_id, partner())
    );
    // it takes no more than the target has
    let weak = scene.pilots[&B].modules.capacitor.as_ref().unwrap().capacity;
    assert!(weak > 0.0);
    let (mut scene, t) = reaching(5_000.0);
    scene.activate(
        A,
        projected(
            Remote::Neutralize { amount: 1.0e6, signature_resolution: 0.0 },
            Applies::CycleStart,
            partner(),
        ),
        t,
    );
    assert_eq!(charge(&scene, B, t), 0.0);
}

#[test]
fn a_neutralizer_made_for_bigger_ships_is_weaker_against_a_smaller_target_in_proportion_to_its_signature() {
    // the Ibis has a signature of 28 m: against a resolution of 56 m it takes half
    let (mut scene, t) = reaching(1_000.0);
    let before = charge(&scene, B, t);
    scene.activate(
        A,
        projected(
            Remote::Neutralize { amount: 40.0, signature_resolution: 56.0 },
            Applies::CycleStart,
            partner(),
        ),
        t,
    );
    assert!((before - charge(&scene, B, t) - 20.0).abs() < 1e-6);
}

#[test]
fn a_nosferatu_takes_from_a_ship_that_holds_more_than_it_does_and_nothing_from_one_that_holds_less() {
    let (mut scene, t) = reaching(5_000.0);
    // A holds less than B: it drains B and what is taken fills A
    scene.pilots.get_mut(&A).unwrap().modules.capacitor.as_mut().unwrap().draw(t, 100.0);
    let (own, theirs) = (charge(&scene, A, t), charge(&scene, B, t));
    scene.activate(
        A,
        projected(Remote::Drain { amount: 25.0, signature_resolution: 0.0 }, Applies::CycleEnd, partner()),
        t,
    );
    // the cost of the cycle is paid as it starts; what is drained comes at its end
    let paid = charge(&scene, A, t);
    let (_, after) = poll_until(&mut scene, t, 5.1);
    assert!(
        (charge(&scene, B, after) - (theirs - 25.0)).abs() < 1.0,
        "{} from {theirs}",
        charge(&scene, B, after)
    );
    assert!(
        charge(&scene, A, after) > paid + 20.0,
        "{} from {own} ({paid} after the cost)",
        charge(&scene, A, after)
    );
    // A holds more than B: nothing is drained
    let (mut scene, t) = reaching(5_000.0);
    scene.pilots.get_mut(&B).unwrap().modules.capacitor.as_mut().unwrap().draw(t, 100.0);
    let theirs = charge(&scene, B, t);
    scene.activate(
        A,
        projected(Remote::Drain { amount: 25.0, signature_resolution: 0.0 }, Applies::CycleEnd, partner()),
        t,
    );
    let (_, after) = poll_until(&mut scene, t, 5.1);
    // B only recharges
    assert!(charge(&scene, B, after) >= theirs, "{} from {theirs}", charge(&scene, B, after));
}

#[test]
fn a_remote_capacitor_transmitter_gives_the_target_capacitor_up_to_what_it_holds() {
    let (mut scene, t) = reaching(5_000.0);
    scene.pilots.get_mut(&B).unwrap().modules.capacitor.as_mut().unwrap().draw(t, 100.0);
    let before = charge(&scene, B, t);
    scene.activate(A, projected(Remote::Transfer { amount: 60.0 }, Applies::CycleStart, partner()), t);
    assert!((charge(&scene, B, t) - (before + 60.0)).abs() < 1e-6);
    // a full capacitor takes nothing and is told nothing
    let (mut scene, t) = reaching(5_000.0);
    let out =
        scene.activate(A, projected(Remote::Transfer { amount: 60.0 }, Applies::CycleStart, partner()), t);
    assert!(!out.iter().any(|o| o.session() == B && charge_of(o).is_some()), "{out:?}");
}

#[test]
fn a_projected_effect_plays_to_those_who_look_on_with_its_target_and_for_as_long_as_it_can() {
    let (mut scene, t) = reaching(5_000.0);
    let out = scene.activate(
        A,
        projected(
            Remote::Neutralize { amount: 40.0, signature_resolution: 0.0 },
            Applies::CycleStart,
            partner(),
        ),
        t,
    );
    let effect = projected(
        Remote::Neutralize { amount: 40.0, signature_resolution: 0.0 },
        Applies::CycleStart,
        partner(),
    );
    assert!(effect.offensive());
    assert!(!projected(Remote::Transfer { amount: 1.0 }, Applies::CycleStart, partner()).offensive());
    // twelve hours of 5 s cycles
    assert_eq!(effect.fx_repeat(), Some(8_640));
    assert!(
        labels(&out).iter().any(|l| l.starts_with("8:destiny[")),
        "the one who looks on sees it: {:?}",
        labels(&out)
    );
}

#[test]
fn the_target_is_told_of_an_effect_projected_at_it_for_its_hud_when_it_begins_each_cycle_and_ends() {
    let (mut scene, t) = reaching(5_000.0);
    let started = scene.activate(
        A,
        projected(Remote::Repair { layer: Layer::Armor, amount: 30.0 }, Applies::CycleEnd, partner()),
        t,
    );
    let told = labels(&started);
    let hud: Vec<&String> = told.iter().filter(|l| l.starts_with("8:On")).collect();
    assert!(hud.iter().any(|l| l.as_str() == "8:OnJamStart"), "{told:?}");
    assert!(hud.iter().any(|l| l.as_str() == "8:OnEwarStart"), "{told:?}");
    assert!(hud.iter().any(|l| l.as_str() == "8:OnRefreshBuffBar"), "{told:?}");
    // the timer carries who, the module, the target, what the HUD calls it, when, and a cycle and a tick
    let jam = started
        .iter()
        .find_map(|o| match o {
            Out::Notify { session: 8, name: "OnJamStart", args, .. } => Some(args.clone()),
            _ => None,
        })
        .expect("OnJamStart");
    assert_eq!(
        jam,
        vec![
            id(spec().ship_id),
            id(MODULE),
            id(partner()),
            text("remoteArmorRepair"),
            filetime(t),
            int(5_100)
        ]
    );
    // the one who runs it is not told
    assert!(!told.iter().any(|l| l.starts_with("7:OnJam") || l.starts_with("7:OnEwar")), "{told:?}");
    // the next cycle refreshes the timer only
    let (out, _) = poll_until(&mut scene, t, 5.1);
    let again = labels(&out);
    assert_eq!(again.iter().filter(|l| l.as_str() == "8:OnJamStart").count(), 1, "{again:?}");
    assert!(!again.iter().any(|l| l == "8:OnEwarStart"), "{again:?}");
    // it ends: the timer, the icon and the bar
    scene.deactivate(A, MODULE, t + 6 * SECOND);
    let (out, _) = poll_until(&mut scene, t + 6 * SECOND, 5.0);
    let ended = labels(&out);
    for expected in ["8:OnJamEnd", "8:OnEwarEnd", "8:OnRefreshBuffBar"] {
        assert!(ended.iter().any(|l| l == expected), "{expected} in {ended:?}");
    }
}

// ---- effects that last: webs, painters, dampeners, scramblers ----

/// A module (its own id) of A that holds B with `what` for as long as it runs, reaching 10 km over a falloff of 5 km.
fn holding(module: i64, what: Remote, jam: &str) -> Effect {
    let mut effect = projected(what, Applies::CycleStart, partner());
    effect.module_id = module;
    effect.fx = "effects.ModifyTargetSpeed".to_string();
    effect.capacitor_need = 5.0;
    if let Some(Action::Remote { jam: name, .. }) = effect.action.as_mut() {
        *name = jam.to_string();
    }
    effect
}

fn web(module: i64, percent: f64) -> Effect {
    holding(module, Remote::Web { percent }, "webify")
}

fn speed_of(scene: &Scene, who: SessionId) -> f64 {
    scene.pilots[&who].ship.spec.max_velocity
}

#[test]
fn a_web_slows_the_ship_it_holds_for_as_long_as_it_runs_and_the_ship_and_those_who_look_on_are_told() {
    let (mut scene, t) = reaching(5_000.0);
    let base = speed_of(&scene, B);
    let started = scene.activate(A, web(MODULE, -60.0), t);
    assert!((speed_of(&scene, B) - base * 0.4).abs() < 1e-9, "{}", speed_of(&scene, B));
    let told = labels(&started);
    assert!(
        told.iter().any(|l| l == "8:attributes[37]"),
        "the target's pilot is told its top speed: {told:?}"
    );
    assert!(told.iter().any(|l| l == "8:destiny[SetMaxSpeed]"), "{told:?}");
    assert!(
        told.iter().any(|l| l == "7:destiny[SetMaxSpeed]") || told.iter().any(|l| l.starts_with("7:destiny")),
        "those who look on: {told:?}"
    );
    // it goes on holding the ship through the cycles, and lets go when the effect stops
    let (_, t2) = poll_until(&mut scene, t, 6.0);
    assert!((speed_of(&scene, B) - base * 0.4).abs() < 1e-9);
    scene.deactivate(A, MODULE, t2);
    let (out, _) = poll_until(&mut scene, t2, 5.5);
    assert_eq!(speed_of(&scene, B), base);
    assert!(labels(&out).iter().any(|l| l == "8:destiny[SetMaxSpeed]"), "{:?}", labels(&out));
    assert!(scene.pilots[&B].incoming.is_empty() && scene.pilots[&B].incoming.base.is_none());
}

#[test]
fn a_web_in_falloff_slows_less_and_two_webs_stack_with_the_penalty() {
    let (mut scene, t) = reaching(12_500.0);
    let base = speed_of(&scene, B);
    scene.activate(A, web(MODULE, -60.0), t);
    // half way through the falloff: a share of 0.840896
    // (the ships stand still, to a few centimetres)
    assert!((speed_of(&scene, B) - base * (1.0 - 0.6 * 0.840896)).abs() < 0.1, "{}", speed_of(&scene, B));
    let (mut scene, t) = reaching(2_000.0);
    scene.activate(A, web(MODULE, -60.0), t);
    scene.activate(A, web(MODULE + 1, -60.0), t);
    let factor = 0.4 * (1.0 - 0.6 * 0.869_119);
    assert!((speed_of(&scene, B) / base - factor).abs() < 1e-4, "{}", speed_of(&scene, B) / base);
}

#[test]
fn what_a_ship_does_to_its_own_speed_is_worked_on_before_the_web_and_is_still_told_when_it_stops() {
    let (mut scene, t) = reaching(2_000.0);
    let base = speed_of(&scene, B);
    scene.activate(A, web(MODULE, -50.0), t);
    // the webbed ship starts its afterburner: the speed it gives is halved
    scene.activate(B, afterburner(), t + SECOND);
    assert!((speed_of(&scene, B) - 522.848918 * 0.5).abs() < 1e-6, "{}", speed_of(&scene, B));
    scene.deactivate(B, MODULE, t + 2 * SECOND);
    let (_, after) = poll_until(&mut scene, t + 2 * SECOND, 11.0);
    assert!((speed_of(&scene, B) - base * 0.5).abs() < 1e-6, "{}", speed_of(&scene, B));
    assert!(after > t);
}

/// A jammer whose strength is `strength` against radar and which runs at the partner, with the partner's radar strength made `sensor` (and its resistance `resistance`).
fn jammer(scene: &mut Scene, strength: f64, sensor: f64, resistance: f64) -> Effect {
    let spec = &mut scene.pilots.get_mut(&B).unwrap().ship.spec;
    spec.sensor_strength = [0.0, 0.0, 0.0, sensor];
    spec.ecm_resistance = resistance;
    let mut effect =
        projected(Remote::Jam { strength: [0.0, 0.0, 0.0, strength] }, Applies::CycleStart, partner());
    effect.fx = "effects.ElectronicAttributeModifyTarget".to_string();
    effect.duration_ms = 20_000.0;
    effect.capacitor_need = 5.0;
    effect
}

fn told_hud(outs: &[Out], session: SessionId, name: &str) -> bool {
    outs.iter().any(|o| matches!(o, Out::Notify { session: s, name: n, .. } if *s == session && *n == name))
}

#[test]
fn a_jammer_that_beats_the_sensor_breaks_the_locks_of_its_target_and_keeps_it_from_locking_but_the_jammer() {
    let (mut scene, t) = reaching(5_000.0);
    let mine = spec().ship_id;
    // the jammed ship holds a lock on the jammer and on a third party (the jammer's own partner is all there is: a lock on the jammer stays)
    let asked = scene.target_order(B, crate::targeting::Order::Lock(mine), t);
    assert!(matches!(asked.first(), Some(Out::Targets { .. })), "{asked:?}");
    let (_, t) = poll_until(&mut scene, t, 6.0);
    assert_eq!(scene.locked_by(B), vec![mine]);
    // a jammer that is twice as strong as the sensor always jams
    let effect = jammer(&mut scene, 10.0, 5.0, 1.0);
    let landed = scene.activate(A, effect, t);
    assert!(
        told_hud(&landed, B, "OnJamStart") && told_hud(&landed, B, "OnEwarStart"),
        "{:?}",
        labels(&landed)
    );
    assert!(scene.pilots[&B].incoming.jammed());
    // the ship that jams is the one it can still lock
    assert_eq!(scene.locked_by(B), vec![mine]);
    assert_eq!(scene.pilots[&B].incoming.jammers(), vec![mine]);
}

#[test]
fn a_jammed_ship_loses_the_locks_that_are_not_on_its_jammer_and_cannot_make_new_ones() {
    let (mut scene, t) = reaching(5_000.0);
    assert_eq!(scene.locked_by(A), vec![partner()]);
    // a ship that is not the partner jams A (a made-up one: the scene holds two ships)
    let until = t + 20 * SECOND;
    scene.pilots.get_mut(&A).unwrap().incoming.upsert(777, 888, crate::ewar::Lasting::Jam, until);
    let broken = scene.break_locks_except(A, &[777], t);
    assert!(scene.locked_by(A).is_empty(), "{:?}", labels(&broken));
    assert!(told_hud(&broken, A, "OnTarget"), "{:?}", labels(&broken));
    assert!(
        told_hud(&broken, B, "OnTarget"),
        "the ship that was locked is told it is not: {:?}",
        labels(&broken)
    );
    // and it cannot lock the partner again until the jam lapses
    let refused = scene.target_order(A, crate::targeting::Order::Lock(partner()), t + SECOND);
    assert!(
        refused.iter().any(|o| matches!(o, Out::TargetRefused { reason: "TARGET_JAMMED", .. })),
        "{refused:?}"
    );
    let (_, t2) = poll_until(&mut scene, t, 22.0);
    assert!(!scene.pilots[&A].incoming.jammed());
    let asked = scene.target_order(A, crate::targeting::Order::Lock(partner()), t2);
    assert!(matches!(asked.first(), Some(Out::Targets { .. })), "{asked:?}");
}

#[test]
fn a_jam_that_does_not_beat_the_sensor_does_nothing_and_a_ship_that_resists_it_is_not_jammed() {
    let (mut scene, t) = reaching(5_000.0);
    // no strength for the kind of sensor the ship has: no chance
    let effect = jammer(&mut scene, 0.0, 5.0, 1.0);
    let landed = scene.activate(A, effect, t);
    assert!(!scene.pilots[&B].incoming.jammed(), "{:?}", labels(&landed));
    assert!(!told_hud(&landed, B, "OnJamStart"));
    // a ship with no resistance to give is immune
    let (mut scene, t) = reaching(5_000.0);
    let effect = jammer(&mut scene, 100.0, 5.0, 0.0);
    scene.activate(A, effect, t);
    assert!(!scene.pilots[&B].incoming.jammed());
}

#[test]
fn a_jam_lapses_when_the_jammer_stops_and_the_ship_is_told_it_ended() {
    let (mut scene, t) = reaching(5_000.0);
    let effect = jammer(&mut scene, 10.0, 5.0, 1.0);
    scene.activate(A, effect, t);
    assert!(scene.pilots[&B].incoming.jammed());
    scene.deactivate(A, MODULE, t + SECOND);
    // the cycle that is going is played out (twenty seconds), and the ship is let go at its end
    let (told, _) = poll_until(&mut scene, t + SECOND, 25.0);
    assert!(!scene.pilots[&B].incoming.jammed());
    assert!(told_hud(&told, B, "OnJamEnd") && told_hud(&told, B, "OnEwarEnd"), "{:?}", labels(&told));
}

#[test]
fn the_chance_of_a_jam_is_the_strength_over_the_sensor_weakened_by_the_reach_and_the_resistance() {
    use crate::ewar::jam_chance;
    let jam = [0.0, 3.0, 0.0, 0.0];
    // the ship's strongest sensor is the kind that counts (a ladar ship against a ladar jammer)
    assert!((jam_chance(&jam, &[0.0, 6.0, 2.0, 0.0], 1.0, 1.0) - 0.5).abs() < 1e-9);
    // the strongest sensor is another kind than the jammer has: no chance
    assert_eq!(jam_chance(&jam, &[0.0, 2.0, 6.0, 0.0], 1.0, 1.0), 0.0);
    // two kinds that are alike: the first counts
    assert!((jam_chance(&[1.0, 0.0, 0.0, 0.0], &[4.0, 0.0, 0.0, 4.0], 1.0, 1.0) - 0.25).abs() < 1e-9);
    // out in the falloff and resisting
    assert!((jam_chance(&jam, &[0.0, 6.0, 0.0, 0.0], 0.5, 0.5) - 0.125).abs() < 1e-9);
    // a ship with no sensors, a jammer that is stronger than the sensor, a ship that cannot be jammed
    assert_eq!(jam_chance(&jam, &[0.0; 4], 1.0, 1.0), 0.0);
    assert_eq!(jam_chance(&jam, &[0.0, 1.0, 0.0, 0.0], 1.0, 1.0), 1.0);
    assert_eq!(jam_chance(&jam, &[0.0, 1.0, 0.0, 0.0], 0.0, 1.0), 0.0);
}

#[test]
fn a_lock_the_dampened_ship_can_no_longer_reach_is_given_up() {
    let (mut scene, t) = reaching(5_000.0);
    let mine = spec().ship_id;
    // the ship that is to be dampened holds a lock on the one that dampens it
    let asked = scene.target_order(B, crate::targeting::Order::Lock(mine), t);
    assert!(matches!(asked.first(), Some(Out::Targets { .. })), "{asked:?}");
    let (_, t) = poll_until(&mut scene, t, 6.0);
    assert_eq!(scene.locked_by(B), vec![mine]);
    // a dampener that takes nine tenths of its range leaves it less than the distance between them
    scene.activate(
        A,
        holding(
            MODULE,
            Remote::Damp {
                range_percent: -90.0,
                resolution_percent: 0.0,
                gravimetric_percent: 0.0,
                ladar_percent: 0.0,
                magnetometric_percent: 0.0,
                radar_percent: 0.0,
            },
            "ewRemoteSensorDamp",
        ),
        t,
    );
    let (told, _) = poll_until(&mut scene, t, 2.0);
    assert!(scene.locked_by(B).is_empty(), "the lock is given up: {:?}", scene.locked_by(B));
    assert!(
        told.iter().any(|o| matches!(o, Out::Notify { session, name: "OnTarget", .. } if *session == B)),
        "{:?}",
        labels(&told)
    );
}

#[test]
fn remote_sensor_boost_is_assistance_with_all_percentages_falloff_scaled_and_negative_or_mixed_damp_is_hostile()
 {
    let boost = Remote::Damp {
        range_percent: 20.0,
        resolution_percent: 10.0,
        gravimetric_percent: 30.0,
        ladar_percent: 40.0,
        magnetometric_percent: 50.0,
        radar_percent: 60.0,
    };
    assert!(!boost.hostile());
    assert_eq!(
        boost.lasting(0.5),
        Some(crate::ewar::Lasting::Damp {
            range_percent: 10.0,
            resolution_percent: 5.0,
            gravimetric_percent: 15.0,
            ladar_percent: 20.0,
            magnetometric_percent: 25.0,
            radar_percent: 30.0,
        })
    );
    assert!(
        Remote::Damp {
            range_percent: -25.0,
            resolution_percent: -25.0,
            gravimetric_percent: 0.0,
            ladar_percent: 0.0,
            magnetometric_percent: 0.0,
            radar_percent: 0.0,
        }
        .hostile()
    );
    assert!(
        Remote::Damp {
            range_percent: 25.0,
            resolution_percent: 25.0,
            gravimetric_percent: 20.0,
            ladar_percent: -1.0,
            magnetometric_percent: 20.0,
            radar_percent: 20.0,
        }
        .hostile()
    );
}

#[test]
fn two_local_sensor_boosters_stack_and_stopping_one_keeps_the_other() {
    let (mut scene, t) = crowd();
    let ship_id = spec().ship_id;
    let (base_range, base_resolution) = {
        let ship = &scene.pilots[&A].ship.spec;
        (ship.max_target_range, ship.scan_resolution)
    };
    let self_boost = |module_id, range_percent, resolution_percent| {
        let mut effect = projected(
            Remote::Damp {
                range_percent,
                resolution_percent,
                gravimetric_percent: 0.0,
                ladar_percent: 0.0,
                magnetometric_percent: 0.0,
                radar_percent: 0.0,
            },
            Applies::CycleStart,
            ship_id,
        );
        effect.module_id = module_id;
        if let Some(Action::Remote { reach, jam, .. }) = effect.action.as_mut() {
            *reach = Reach { optimal: 1.0, falloff: 0.0 };
            *jam = "remoteSensorBoost".to_string();
        }
        effect
    };
    let first_module = MODULE + 20;
    let second_module = MODULE + 21;
    let first = scene.activate(A, self_boost(first_module, 50.0, 40.0), t);
    assert!(matches!(first.first(), Some(Out::Accepted { .. })), "{first:?}");
    let second = scene.activate(A, self_boost(second_module, 30.0, 20.0), t);
    assert!(matches!(second.first(), Some(Out::Accepted { .. })), "{second:?}");
    assert!(first.iter().any(|out| matches!(out, Out::Notify { session: A, name: "OnJamStart", .. })));
    assert!(scene.pilots[&A].ship.spec.max_target_range > base_range * 1.3);
    assert!(scene.pilots[&A].ship.spec.scan_resolution > base_resolution * 1.2);

    scene.deactivate(A, first_module, t + SECOND);
    let (ended, _) = poll_until(&mut scene, t + SECOND, 5.1);
    assert_eq!(scene.pilots[&A].modules.running_count(), 1, "{:?}", labels(&ended));
    assert!((scene.pilots[&A].ship.spec.max_target_range - base_range * 1.3).abs() < 1e-6);
    assert!((scene.pilots[&A].ship.spec.scan_resolution - base_resolution * 1.2).abs() < 1e-6);
}

#[test]
fn a_target_painter_makes_the_ship_bigger_and_a_sensor_dampener_shortens_its_lock_range_and_slows_its_locks()
{
    let (mut scene, t) = reaching(3_000.0);
    let (signature, range, resolution) = {
        let spec = &scene.pilots[&B].ship.spec;
        (spec.signature_radius, spec.max_target_range, spec.scan_resolution)
    };
    let painted = scene.activate(A, holding(MODULE, Remote::Paint { percent: 25.0 }, "ewTargetPaint"), t);
    assert!((scene.pilots[&B].ship.spec.signature_radius - signature * 1.25).abs() < 1e-9);
    assert!(labels(&painted).iter().any(|l| l == "8:attributes[552]"), "{:?}", labels(&painted));
    let damped = scene.activate(
        A,
        holding(
            MODULE + 1,
            Remote::Damp {
                range_percent: -30.0,
                resolution_percent: -20.0,
                gravimetric_percent: 0.0,
                ladar_percent: 0.0,
                magnetometric_percent: 0.0,
                radar_percent: 0.0,
            },
            "ewRemoteSensorDamp",
        ),
        t,
    );
    let spec = &scene.pilots[&B].ship.spec;
    assert!(
        (spec.max_target_range - range * 0.7).abs() < 1e-6
            && (spec.scan_resolution - resolution * 0.8).abs() < 1e-9
    );
    assert!(labels(&damped).iter().any(|l| l == "8:attributes[76,564]"), "{:?}", labels(&damped));
}

/// A smartbomb of `range` metres that does `damage` (em, thermal, kinetic, explosive) a cycle.
fn smartbomb(range: f64, damage: [f64; 4], hurts_pilots: bool) -> Effect {
    Effect {
        burns_fuel: false,
        fx: "effects.EmpWave".to_string(),
        duration_ms: 10_000.0,
        capacitor_need: 5.0,
        target_id: 0,
        action: Some(Action::Smartbomb { range, damage, hurts_pilots }),
        ..projected(Remote::Web { percent: 0.0 }, Applies::CycleStart, 0)
    }
}

fn total(layers: &crate::health::Layers) -> f64 {
    layers.shield + layers.armor + layers.structure
}

/// A crowd with A moved `gap` metres off the surface of the test system's station.
fn crowd_off_the_station(gap: f64) -> (Scene, i64) {
    let (mut scene, t) = crowd();
    let (x, y, z, radius) = {
        let station = scene.system().body(STATION).unwrap();
        (station.position.x, station.position.y, station.position.z, station.radius)
    };
    scene.pilots.get_mut(&A).unwrap().ship.position = eve_destiny::Vec3::new(x + radius + gap, y, z);
    (scene, t)
}

#[test]
fn a_smartbomb_does_not_start_within_five_kilometres_of_a_station_or_its_range_when_that_is_more() {
    // the least restriction is 5 km; a bomb of 6 km range is held off to 6 km (the wiki's two data points, see DEVIATIONS)
    let (mut scene, t) = crowd_off_the_station(4_000.0);
    let refused = scene.activate(A, smartbomb(3_000.0, [0.0, 40.0, 0.0, 40.0], true), t);
    assert!(
        refused.iter().any(|o| matches!(o, Out::Refused { reason: "SMARTBOMB_NEAR_STATION", .. })),
        "{:?}",
        labels(&refused)
    );
    let (mut scene, t) = crowd_off_the_station(5_500.0);
    let refused = scene.activate(A, smartbomb(6_000.0, [0.0, 40.0, 0.0, 40.0], true), t);
    assert!(
        refused.iter().any(|o| matches!(o, Out::Refused { reason: "SMARTBOMB_NEAR_STATION", .. })),
        "{:?}",
        labels(&refused)
    );
    let (mut scene, t) = crowd_off_the_station(6_500.0);
    let started = scene.activate(A, smartbomb(6_000.0, [0.0, 40.0, 0.0, 40.0], true), t);
    assert!(started.iter().any(|o| matches!(o, Out::Accepted { .. })), "{:?}", labels(&started));
}

#[test]
fn a_smartbomb_hurts_every_ship_in_its_range_each_cycle_and_nothing_beyond_it() {
    // B stands two kilometres off A's surface: inside a three kilometre field, with no lock between them
    let (mut scene, t) = reaching(2_000.0);
    clear_of_the_station(&mut scene);
    scene.target_order(A, crate::targeting::Order::Clear, t);
    let before = total(&partner_layers(&scene, t));
    let started = scene.activate(A, smartbomb(3_000.0, [0.0, 40.0, 0.0, 40.0], true), t);
    assert!(started.iter().any(|o| matches!(o, Out::Accepted { .. })), "{:?}", labels(&started));
    let after = total(&partner_layers(&scene, t));
    assert!(after < before - 1.0, "{before} then {after}");
    assert!(
        told_hud(&started, B, "OnDamageMessage") && told_hud(&started, A, "OnDamageMessage"),
        "{:?}",
        labels(&started)
    );
    // the next cycle is another blast
    let (_, t2) = poll_until(&mut scene, t, 10.5);
    assert!(total(&partner_layers(&scene, t2)) < after - 1.0);
    // beyond the field nothing is hurt
    let (mut far, tf) = reaching(5_000.0);
    far.target_order(A, crate::targeting::Order::Clear, tf);
    let whole = total(&partner_layers(&far, tf));
    far.activate(A, smartbomb(3_000.0, [0.0, 40.0, 0.0, 40.0], true), tf);
    assert_eq!(total(&partner_layers(&far, tf)), whole);
    // and a ship whose undock protection is on takes nothing
    let (mut safe, ts) = reaching(2_000.0);
    safe.target_order(A, crate::targeting::Order::Clear, ts);
    safe.pilots.get_mut(&B).unwrap().protected_until = ts + 60 * SECOND;
    let kept = total(&partner_layers(&safe, ts));
    safe.activate(A, smartbomb(3_000.0, [0.0, 40.0, 0.0, 40.0], true), ts);
    assert_eq!(total(&partner_layers(&safe, ts)), kept);
}

#[test]
fn an_ecm_burst_breaks_nearby_locks_at_cycle_start_but_does_not_leave_a_jam() {
    let (mut scene, t) = reaching(2_000.0);
    scene.pilots.get_mut(&B).unwrap().ship.spec.sensor_strength = [10.0; 4];
    scene.pilots.get_mut(&B).unwrap().ship.spec.ecm_resistance = 1.0;
    let target_id = scene.pilots[&A].ship.spec.ship_id;
    scene.target_order(B, crate::targeting::Order::Lock(target_id), t);
    let (_, locked_at) = poll_until(&mut scene, t, 30.0);
    assert!(!scene.pilots[&B].targeting.targets().is_empty());

    let burst = Effect {
        burns_fuel: false,
        fx: "effects.EcmBurstJammer".to_string(),
        action: Some(Action::EcmBurst { range: 3_000.0, strength: [1_000.0; 4] }),
        ..smartbomb(0.0, [0.0; 4], true)
    };
    let out = scene.activate(A, burst, locked_at);
    assert!(out.iter().any(|event| matches!(event, Out::Accepted { .. })), "{:?}", labels(&out));
    assert!(
        scene.pilots[&B].targeting.pending_targets().is_empty()
            && scene.pilots[&B].targeting.targets().is_empty()
    );
    assert!(!scene.pilots[&B].incoming.jammed());
}

fn ecm_burst_of(strength: f64) -> Effect {
    Effect {
        burns_fuel: false,
        fx: "effects.EcmBurstJammer".to_string(),
        action: Some(Action::EcmBurst { range: 3_000.0, strength: [strength; 4] }),
        ..smartbomb(0.0, [0.0; 4], true)
    }
}

#[test]
fn an_ecm_burst_tells_the_host_each_ship_it_reached_and_whether_its_roll_jammed_it() {
    let (mut scene, t) = reaching(2_000.0);
    let (burster, reached) = (scene.pilots[&A].ship.spec.ship_id, scene.pilots[&B].ship.spec.ship_id);
    // B's sensors are under the burst: the roll is a jam every time
    scene.pilots.get_mut(&B).unwrap().ship.spec.sensor_strength = [10.0; 4];
    scene.pilots.get_mut(&B).unwrap().ship.spec.ecm_resistance = 1.0;
    let out = scene.activate(A, ecm_burst_of(1_000.0), t);
    assert!(
        out.iter().any(|o| matches!(o, Out::EcmBurst { session: A, ship_id, reached: r } if *ship_id == burster && r == &[(reached, true)])),
        "{:?}",
        labels(&out)
    );
}

#[test]
fn an_ecm_burst_reports_a_ship_it_reached_that_resists_the_jam_as_not_jammed() {
    let (mut scene, t) = reaching(2_000.0);
    let (burster, reached) = (scene.pilots[&A].ship.spec.ship_id, scene.pilots[&B].ship.spec.ship_id);
    // an ECM-immune hull is reached (it is told of the burst) but its roll cannot land
    scene.pilots.get_mut(&B).unwrap().ship.spec.ecm_resistance = 0.0;
    let out = scene.activate(A, ecm_burst_of(1_000.0), t);
    assert!(
        out.iter().any(|o| matches!(o, Out::EcmBurst { session: A, ship_id, reached: r } if *ship_id == burster && r == &[(reached, false)])),
        "{:?}",
        labels(&out)
    );
    assert!(!scene.pilots[&B].incoming.jammed());
}

#[test]
fn a_cloaked_ship_is_not_reached_by_an_ecm_burst() {
    let (mut scene, t) = reaching(2_000.0);
    scene.pilots.get_mut(&B).unwrap().ship.spec.sensor_strength = [10.0; 4];
    scene.pilots.get_mut(&B).unwrap().ship.spec.ecm_resistance = 1.0;
    scene.pilots.get_mut(&B).unwrap().cloak = crate::visibility::CloakState::Cloaked;
    let out = scene.activate(A, ecm_burst_of(1_000.0), t);
    assert!(!out.iter().any(|o| matches!(o, Out::EcmBurst { .. })), "{:?}", labels(&out));
    assert!(!scene.pilots[&B].incoming.jammed());
}

#[test]
fn a_smartbomb_the_safety_forbids_does_not_start_with_a_pilot_in_range_and_stops_when_one_comes_in() {
    let (mut scene, t) = reaching(2_000.0);
    clear_of_the_station(&mut scene);
    scene.target_order(A, crate::targeting::Order::Clear, t);
    let before = total(&partner_layers(&scene, t));
    let refused = scene.activate(A, smartbomb(3_000.0, [0.0, 40.0, 0.0, 40.0], false), t);
    assert!(
        refused.iter().any(|o| matches!(o, Out::Refused { reason: "SafetyActivated", .. })),
        "{:?}",
        labels(&refused)
    );
    assert_eq!(total(&partner_layers(&scene, t)), before);
    // with nobody in range it starts, and a pilot that comes in range stops it at the next cycle
    let (mut quiet, tq) = reaching(8_000.0);
    clear_of_the_station(&mut quiet);
    quiet.target_order(A, crate::targeting::Order::Clear, tq);
    let started = quiet.activate(A, smartbomb(3_000.0, [0.0, 40.0, 0.0, 40.0], false), tq);
    assert!(started.iter().any(|o| matches!(o, Out::Accepted { .. })), "{:?}", labels(&started));
    let here = quiet.pilots[&A].ship.position;
    let (ra, rb) = (quiet.pilots[&A].ship.spec.radius, quiet.pilots[&B].ship.spec.radius);
    quiet.pilots.get_mut(&B).unwrap().ship.position =
        eve_destiny::Vec3 { x: here.x + 1_000.0 + ra + rb, y: here.y, z: here.z };
    let whole = total(&partner_layers(&quiet, tq));
    let (told, t2) = poll_until(&mut quiet, tq, 11.0);
    assert_eq!(total(&partner_layers(&quiet, t2)), whole, "{:?}", labels(&told));
    assert!(!quiet.pilots[&A].modules.running.iter().any(|r| r.effect.module_id == MODULE), "it stopped");
}

/// The afterburner of the tests, as the dogma effect of a microwarpdrive.
fn microwarpdrive() -> Effect {
    Effect { effect_id: MICROWARPDRIVE, module_id: MODULE + 100, ..afterburner() }
}

#[test]
fn an_uninterrupted_mjd_jumps_at_spool_end_and_starts_cooldown() {
    let (mut scene, t) = crowd();
    let started = scene.activate(A, micro_jump_drive(), t);
    assert!(started.iter().any(|out| matches!(out, Out::Accepted { .. })));

    let deadline = t + 12 * SECOND;
    let (position, direction, speed) = {
        let ship = scene.ship(A, deadline).unwrap();
        (ship.position, ship.direction, ship.speed_fraction)
    };
    scene.poll(deadline);
    let after = scene.ship(A, deadline).unwrap();
    assert!((after.position.x - position.x - direction.x * 100_000.0).abs() < 0.001);
    assert!((after.position.y - position.y - direction.y * 100_000.0).abs() < 0.001);
    assert!((after.position.z - position.z - direction.z * 100_000.0).abs() < 0.001);
    assert_eq!(after.speed_fraction, speed);
    assert_eq!(
        (after.direction.x, after.direction.y, after.direction.z),
        (direction.x, direction.y, direction.z)
    );

    let cooling = scene.activate(A, micro_jump_drive(), deadline + SECOND);
    assert!(cooling.iter().any(|out| matches!(out, Out::Refused { reason: "MJD_COOLDOWN", .. })));
}

#[test]
fn a_scram_at_mjd_spool_end_aborts_and_cooldown_expires() {
    let (mut scene, t) = reaching(3_000.0);
    let repeated = Effect { repeat: 2, ..micro_jump_drive() };
    let refused = scene.activate(B, repeated, t);
    assert!(refused.iter().any(|out| matches!(out, Out::Refused { reason: "BAD_MJD", .. })));

    let started = scene.activate(B, micro_jump_drive(), t);
    assert!(started.iter().any(|out| matches!(out, Out::Accepted { .. })));
    let steering = scene.command(
        B,
        crate::scene::Command::GotoDirection(eve_destiny::Vec3::new(1.0, 0.0, 0.0)),
        t + SECOND,
    );
    assert!(steering.iter().any(|out| matches!(out, Out::Refused { reason: "MJD_SPOOLING", .. })));

    let scram =
        holding(MODULE + 201, Remote::Scramble { strength: 2.0, blocks_mwd: true }, "warpScramblerMWD");
    let held = scene.activate(A, scram, t + SECOND);
    assert!(held.iter().any(|out| matches!(out, Out::Accepted { .. })));
    assert!(scene.pilots[&B].incoming.mwd_blocked());

    let deadline = t + 12 * SECOND;
    let position_before = scene.ship(B, deadline).unwrap().position;
    scene.poll(deadline);
    assert_eq!(scene.ship(B, deadline).unwrap().position, position_before);
    assert!(!scene.pilots[&B].modules.running.iter().any(|run| run.effect.module_id == MODULE + 200));

    let cooling = scene.activate(B, micro_jump_drive(), deadline + SECOND);
    assert!(cooling.iter().any(|out| matches!(out, Out::Refused { reason: "MJD_COOLDOWN", .. })));
    let expired = scene.activate(B, micro_jump_drive(), deadline + 180 * SECOND);
    assert!(expired.iter().any(|out| matches!(out, Out::Accepted { .. })));
}

#[test]
fn a_scrambler_that_blocks_a_microwarpdrive_stops_the_one_that_runs_and_keeps_another_from_starting() {
    let (mut scene, t) = reaching(3_000.0);
    let started = scene.activate(B, microwarpdrive(), t);
    assert!(started.iter().any(|o| matches!(o, Out::Accepted { .. })), "{:?}", labels(&started));
    assert!(scene.pilots[&B].modules.running.iter().any(|r| r.effect.effect_id == MICROWARPDRIVE));
    // a disruptor does not block it, a scrambler does: the drive that runs is stopped at once and the ship is told it is off
    let disrupt = holding(MODULE, Remote::Scramble { strength: 1.0, blocks_mwd: false }, "warpScrambler");
    let free = scene.activate(A, disrupt, t + SECOND);
    assert!(
        scene.pilots[&B].modules.running.iter().any(|r| r.effect.effect_id == MICROWARPDRIVE),
        "{:?}",
        labels(&free)
    );
    scene.deactivate(A, MODULE, t + 2 * SECOND);
    let (_, t2) = poll_until(&mut scene, t + 2 * SECOND, 8.0);
    assert!(!scene.pilots[&B].incoming.scrambled_with(0.0));
    let scram = holding(MODULE + 1, Remote::Scramble { strength: 2.0, blocks_mwd: true }, "warpScramblerMWD");
    let stopped = scene.activate(A, scram, t2);
    assert!(
        !scene.pilots[&B].modules.running.iter().any(|r| r.effect.effect_id == MICROWARPDRIVE),
        "{:?}",
        labels(&stopped)
    );
    assert!(
        told_hud(&stopped, B, "OnGodmaShipEffect"),
        "the pilot is told the drive is off: {:?}",
        labels(&stopped)
    );
    // and another cannot be started while the scrambler holds the ship; an afterburner can
    let refused = scene.activate(B, microwarpdrive(), t2 + SECOND);
    assert!(
        refused.iter().any(|o| matches!(o, Out::Refused { reason: "MICROWARPDRIVE_BLOCKED", .. })),
        "{:?}",
        labels(&refused)
    );
    let ab = Effect { module_id: MODULE + 101, ..afterburner() };
    let accepted = scene.activate(B, ab, t2 + SECOND);
    assert!(accepted.iter().any(|o| matches!(o, Out::Accepted { .. })), "{:?}", labels(&accepted));
}

#[test]
fn a_ship_that_is_scrambled_cannot_warp_and_a_scramble_stops_one_that_is_lining_up() {
    let (mut scene, t) = reaching_in(crowd_with_a_planet(), 3_000.0);
    scene.activate(
        A,
        holding(MODULE, Remote::Scramble { strength: 2.0, blocks_mwd: true }, "warpScramblerMWD"),
        t,
    );
    assert!(scene.pilots[&B].incoming.scrambled_with(0.0));
    let refused = scene.command(B, Command::WarpTo { target_id: PLANET, range: 0.0 }, t + SECOND);
    assert!(
        refused.iter().any(|o| matches!(o, Out::Refused { command: 8, reason: "WARP_SCRAMBLED", .. })),
        "{refused:?}"
    );
    // it is let go when the effect stops, and the ship warps
    scene.deactivate(A, MODULE, t + 2 * SECOND);
    let (_, t2) = poll_until(&mut scene, t + 2 * SECOND, 6.0);
    assert!(!scene.pilots[&B].incoming.scrambled_with(0.0));
    let accepted = scene.command(B, Command::WarpTo { target_id: PLANET, range: 0.0 }, t2);
    assert!(!accepted.iter().any(|o| matches!(o, Out::Refused { .. })), "{accepted:?}");
    assert!(scene.pilots[&B].flight.is_some());
    // a scramble that lands while it lines up stops it
    let (mut scene, t) = reaching_in(crowd_with_a_planet(), 3_000.0);
    scene.command(B, Command::WarpTo { target_id: PLANET, range: 0.0 }, t);
    assert!(scene.pilots[&B].flight.as_ref().is_some_and(|f| !f.is_warping()));
    scene.activate(
        A,
        holding(MODULE, Remote::Scramble { strength: 1.0, blocks_mwd: false }, "warpScrambler"),
        t + SECOND / 2,
    );
    assert!(scene.pilots[&B].flight.is_none(), "the warp is called off");
}

#[test]
fn what_no_module_renews_lapses_by_itself_as_when_the_ship_that_held_it_is_gone() {
    let (mut scene, t) = reaching(3_000.0);
    let base = speed_of(&scene, B);
    scene.activate(A, web(MODULE, -60.0), t);
    // the ship that runs the web is gone: nothing renews the record
    scene.pilots.get_mut(&A).unwrap().modules.running.clear();
    let (out, _) = poll_until(&mut scene, t, 6.0);
    assert_eq!(speed_of(&scene, B), base, "{:?}", labels(&out));
    assert!(scene.pilots[&B].incoming.is_empty());
}

#[test]
fn offlining_a_module_during_a_cycle_stops_it_immediately() {
    let (mut scene, t) = hurt();
    let before = layers(&scene, t).armor;
    scene.activate(A, repairer(Layer::Armor, 30.0, Applies::CycleEnd), t);

    let out = scene.offline_module(A, MODULE, t + SECOND);
    assert_eq!(scene.pilots[&A].modules.running_count(), 0);
    assert!((layers(&scene, t + SECOND).armor - before).abs() < 1e-3);
    let Some(Out::Notify { args, .. }) =
        out.iter().find(|out| matches!(out, Out::Notify { name: "OnGodmaShipEffect", .. }))
    else {
        panic!("the offlined effect is told to the client: {out:?}");
    };
    assert_eq!(args[8], Value::Bool(false));

    let (later, _) = poll_until(&mut scene, t + SECOND, 5.0);
    assert!((layers(&scene, t + 6 * SECOND).armor - before).abs() < 1e-3);
    assert!(later.iter().all(|out| !matches!(out, Out::Notify { name: "OnGodmaShipEffect", .. })));
}

// ---- a reactive armor hardener ----

/// The Ibis's armor resonances (em, thermal, kinetic, explosive) before a hardener.
const BASE_ARMOR: [f64; 4] = [0.5, 0.35, 0.25, 0.1];
/// The attributes of the armor's resonances, in the order of the damage vector: em (267), thermal (270), kinetic (269), explosive (268).
const ARMOR_IDS: [i32; 4] = [267, 270, 269, 268];

/// A reactive armor hardener of the Ibis (type 4403): seeded at 0.85 each, 6 points a cycle, of ten seconds; told as the ship's armor resonances.
fn hardener(module: i64) -> Effect {
    let ship = spec().ship_id;
    let seeded = |base: f64| base * 0.85;
    Effect {
        rules: Default::default(),
        burns_fuel: false,
        module_id: module,
        module_type: 4403,
        effect_id: 4928,
        fx: "effects.AdaptiveArmorHardener".to_string(),
        duration_ms: 10_000.0,
        capacitor_need: 42.0,
        // a toggle: it runs until the pilot stops it (the type does not disallow repeating)
        repeat: 1000,
        target_id: 0,
        turret: None,
        action: Some(Action::ResistanceShift { seed: [0.85; 4], shift: 0.06, attribute_ids: ARMOR_IDS }),
        on: None,
        off: None,
        on_changes: ARMOR_IDS
            .iter()
            .zip(BASE_ARMOR)
            .map(|(&attribute_id, base)| Change { item_id: ship, attribute_id, value: round6(seeded(base)) })
            .collect(),
        off_changes: ARMOR_IDS
            .iter()
            .zip(BASE_ARMOR)
            .map(|(&attribute_id, base)| Change { item_id: ship, attribute_id, value: base })
            .collect(),
    }
}

/// The armor resonances the pilot of `A` was told of in `out`: `(attribute, new, old)`.
fn armor_told(out: &[Out]) -> Vec<(i64, f64, f64)> {
    // what an effect's change tells has no old value (`None`): it is not a number
    let number = |value: &Value| match value {
        Value::Real(x) => *x,
        Value::Int(x) => *x as f64,
        Value::None => f64::NAN,
        other => panic!("{other:?}"),
    };
    out.iter()
        .filter_map(|o| match o {
            Out::Notify { session: A, name: "OnModuleAttributeChanges", args, .. } => {
                let Some(Value::List(lines)) = args.first() else { panic!("{args:?}") };
                Some(lines.clone())
            }
            _ => None,
        })
        .flatten()
        .filter_map(|line| {
            let Value::Tuple(t) = line else { panic!() };
            let Value::Int(attribute) = t[3] else { panic!() };
            ARMOR_IDS.contains(&(attribute as i32)).then(|| (attribute, number(&t[5]), number(&t[6])))
        })
        .collect()
}

fn round6(value: f64) -> f64 {
    crate::health::round6(value)
}

/// The Ibis with armor enough to take a long run of hits, so that the hits count in full.
fn with_armor(scene: &mut Scene, t: i64) {
    let mut big = spec();
    big.vitals.armor_hp = 1.0e7;
    big.vitals.structure_hp = 1.0e7;
    scene.respec(A, big, t);
}

#[test]
fn a_hardener_moves_the_armor_resonances_at_the_end_of_its_cycle_as_the_hit_says_and_tells_the_pilot() {
    let (mut scene, t) = crowd();
    with_armor(&mut scene, t);
    let started = scene.activate(A, hardener(MODULE), t);
    assert!(matches!(started.first(), Some(Out::Accepted { command: 17, .. })), "{started:?}");
    // the armor's resonances are the base times the seed while it runs (the host's told values, and the simulation's)
    assert!((scene.pilots[&A].ship.health.armor_resonance()[0] - 0.425).abs() < 1e-12);
    // a hit of 1000 em lands before the cycle ends: 1000 x 0.425 goes into the armor
    scene.damage(spec().ship_id, [1000.0, 0.0, 0.0, 0.0], t + SECOND);
    let (out, _) = poll_until(&mut scene, t, 10.5);
    // the cycle ended: em falls to 0.67 (0.5 x 0.67), the others rise to 0.91 (0.35 x 0.91 for thermal)
    let told = armor_told(&out);
    assert!(
        told.iter().any(|&(attribute, new, old)| attribute == 267 && new == 0.335 && old == 0.425),
        "{told:?}"
    );
    assert!(
        told.iter().any(|&(attribute, new, old)| attribute == 270 && new == 0.3185 && old == 0.2975),
        "{told:?}"
    );
    assert_eq!(scene.pilots[&A].ship.health.shift_profile(), Some([0.67, 0.91, 0.91, 0.91]));
}

#[test]
fn a_hardener_is_told_nothing_at_a_cycle_in_which_the_armor_took_nothing() {
    let (mut scene, t) = crowd();
    with_armor(&mut scene, t);
    scene.activate(A, hardener(MODULE), t);
    let (out, _) = poll_until(&mut scene, t, 25.0);
    assert!(armor_told(&out).is_empty(), "{:?}", armor_told(&out));
    // the profile is the seed still, after two cycles with nothing in them
    assert_eq!(scene.pilots[&A].ship.health.shift_profile(), Some([0.85; 4]));
}

#[test]
fn the_profile_keeps_its_total_and_its_bounds_over_many_cycles_of_hits() {
    let (mut scene, t) = crowd();
    with_armor(&mut scene, t);
    scene.activate(A, hardener(MODULE), t);
    let mut now = t;
    for cycle in 0..30 {
        // a hit in one kind a cycle, moving round the four
        let kind = cycle % 4;
        let mut raw = [0.0; 4];
        raw[kind] = 400.0;
        scene.damage(spec().ship_id, raw, now + SECOND);
        let (_, next) = poll_until(&mut scene, now, 10.5);
        now = next;
        let profile = scene.pilots[&A].ship.health.shift_profile().expect("the hardener runs");
        assert!((profile.iter().sum::<f64>() - 3.4).abs() < 1e-6, "cycle {cycle}: {profile:?}");
        assert!(profile.iter().all(|r| (0.0..=1.0).contains(r)), "cycle {cycle}: {profile:?}");
    }
}

#[test]
fn a_second_reactive_armor_hardener_is_refused_while_one_runs() {
    let (mut scene, t) = crowd();
    scene.activate(A, hardener(MODULE), t);
    let second = scene.activate(A, hardener(MODULE + 1), t);
    assert!(
        matches!(second.first(), Some(Out::Refused { command: 17, reason: "MAX_GROUP_ACTIVE", .. })),
        "{second:?}"
    );
    assert_eq!(scene.pilots[&A].modules.running_count(), 1);
}

#[test]
fn stopping_a_hardener_gives_the_armor_its_base_and_a_later_start_begins_from_the_seed() {
    let (mut scene, t) = crowd();
    with_armor(&mut scene, t);
    scene.activate(A, hardener(MODULE), t);
    scene.damage(spec().ship_id, [1000.0, 0.0, 0.0, 0.0], t + SECOND);
    scene.deactivate(A, MODULE, t + 2 * SECOND);
    let (out, t) = poll_until(&mut scene, t, 10.5);
    // the cycle ends with the hardener stopping: the armor is told its base (the off values) and nothing runs
    let told = armor_told(&out);
    assert!(told.iter().any(|&(attribute, new, _)| attribute == 267 && new == 0.5), "{told:?}");
    assert_eq!(scene.pilots[&A].ship.health.armor_resonance(), BASE_ARMOR);
    assert_eq!(scene.pilots[&A].ship.health.shift_profile(), None);
    // a later start is from the seed, not from where it stopped
    scene.activate(A, hardener(MODULE), t);
    assert_eq!(scene.pilots[&A].ship.health.shift_profile(), Some([0.85; 4]));
}

#[test]
fn a_fitted_polarized_weapon_holds_the_armor_at_one_and_the_hardener_moves_nothing_until_it_is_taken_off() {
    let (mut scene, t) = crowd();
    with_armor(&mut scene, t);
    let mut pinned = spec();
    pinned.vitals.armor_pinned = true;
    pinned.vitals.armor_hp = 1.0e7;
    pinned.vitals.structure_hp = 1.0e7;
    // the Polarized weapon is fitted: the simulation is sent the pin with the ship
    scene.respec(A, pinned, t);
    let started = scene.activate(A, hardener(MODULE), t);
    assert!(matches!(started.first(), Some(Out::Accepted { command: 17, .. })), "{started:?}");
    assert_eq!(scene.pilots[&A].ship.health.armor_resonance(), [1.0; 4]);
    scene.damage(spec().ship_id, [1000.0, 0.0, 0.0, 0.0], t + SECOND);
    let (out, t) = poll_until(&mut scene, t, 10.5);
    // nothing moves while pinned: no shift is told and the profile is the seed
    assert!(armor_told(&out).is_empty(), "{:?}", armor_told(&out));
    assert_eq!(scene.pilots[&A].ship.health.shift_profile(), Some([0.85; 4]));
    // the Polarized weapon comes off: the armor is the base times the profile again, and the pilot is told it at once
    let mut unpinned = spec();
    unpinned.vitals.armor_hp = 1.0e7;
    unpinned.vitals.structure_hp = 1.0e7;
    let out = scene.respec(A, unpinned, t);
    let told = armor_told(&out);
    assert!(
        told.iter().any(|&(attribute, new, old)| attribute == 267 && new == 0.425 && old == 1.0),
        "{told:?}"
    );
    assert!((scene.pilots[&A].ship.health.armor_resonance()[0] - 0.425).abs() < 1e-12);
}

#[test]
fn a_refit_that_changes_the_base_moves_the_told_armor_and_keeps_the_profile() {
    let (mut scene, t) = crowd();
    with_armor(&mut scene, t);
    scene.activate(A, hardener(MODULE), t);
    let mut refit = spec();
    refit.vitals.armor_hp = 1.0e7;
    refit.vitals.structure_hp = 1.0e7;
    refit.vitals.resonances.armor = [0.4, 0.35, 0.25, 0.1];
    let out = scene.respec(A, refit, t + SECOND);
    let told = armor_told(&out);
    assert!(
        told.iter().any(|&(attribute, new, old)| attribute == 267 && new == 0.34 && old == 0.425),
        "{told:?}"
    );
    assert_eq!(scene.pilots[&A].ship.health.shift_profile(), Some([0.85; 4]));
}

/// A mining laser on the partner (locked, in reach): a cycle of 2 s, `repeat` cycles, no capacitor.
fn mining(repeat: i32) -> Effect {
    let mut effect = afterburner();
    effect.target_id = partner();
    effect.action = Some(Action::Mining {
        family: MiningFamily::Ore,
        mining_amount_m3: 20.0,
        waste_multiplier: 1.0,
        waste_probability: 0.1,
        crit_probability: 0.05,
        crit_multiplier: 2.0,
    });
    effect.effect_id = 4_025;
    effect.duration_ms = 2_000.0;
    effect.capacitor_need = 0.0;
    effect.repeat = repeat;
    effect
}

/// The mining laser of `mining`, on a rock instead of the partner.
fn mining_on(rock: i64, repeat: i32) -> Effect {
    let mut effect = mining(repeat);
    effect.target_id = rock;
    effect
}

/// A rock of 100 units beside the miner, which has locked it. Both pilots are in one bubble, so both see the rock.
fn mining_rock(rock: i64) -> (Scene, i64) {
    let (mut scene, t) = reaching(0.0);
    let mut position = scene.pilots[&A].ship.position;
    position.x += 1_000.0;
    scene.place_asteroid(
        crate::wreck::Asteroid {
            id: rock,
            type_id: 0,
            group_id: 0,
            category_id: 0,
            name: "Test ore".into(),
            graphic_id: None,
            position,
            direction: eve_destiny::Vec3::new(1.0, 0.0, 0.0),
            radius: 10.0,
            units_remaining: 100,
        },
        t,
    );
    let asked = scene.target_order(A, crate::targeting::Order::Lock(rock), t);
    let (_, t) = poll_until(&mut scene, t, 40.0);
    assert!(scene.locked_by(A).contains(&rock), "the miner locked the rock: {asked:?}");
    (scene, t)
}

#[test]
fn a_granted_mining_cycle_tells_the_other_pilot_what_came_off_the_rock_and_not_the_miner() {
    let rock = 99_200_001;
    let (mut scene, t) = mining_rock(rock);
    scene.activate(A, mining_on(rock, 3), t);
    let (first, at) = poll_until(&mut scene, t, 2.5);
    let (cycle, ..) = work_cycles(&first)[0];

    let told = scene.module_mined(A, MODULE, rock, cycle, 90, at);
    assert!(
        matches!(told.as_slice(), [Out::Notify { session: B, name: "OnOreMinedByOther", id_type: "charid", args, .. }]
            if args == &[crate::wire::id(rock), crate::wire::int(10)]),
        "{told:?}"
    );
    assert_eq!(scene.asteroids[&rock].units_remaining, 90, "the rock's stock follows the grant");

    // the answer settles the cycle, so a repeat of the same grant tells nobody again
    scene.module_work_result(
        A,
        MODULE,
        ModuleWorkAnswer { target_id: rock, cycle_id: cycle, accepted: true, reason: "", depleted: false },
        at,
    );
    assert!(scene.module_mined(A, MODULE, rock, cycle, 90, at).is_empty(), "a replay tells nobody");
}

#[test]
fn a_rock_mined_out_by_a_granted_cycle_is_told_to_the_other_pilot_and_removed() {
    let rock = 99_200_002;
    let (mut scene, t) = mining_rock(rock);
    scene.activate(A, mining_on(rock, 3), t);
    let (first, at) = poll_until(&mut scene, t, 2.5);
    let (cycle, ..) = work_cycles(&first)[0];

    let told = scene.module_mined(A, MODULE, rock, cycle, 0, at);
    assert!(
        told.iter().any(|o| matches!(o, Out::Notify { session: B, name: "OnOreMinedByOther", args, .. }
            if args == &[crate::wire::id(rock), crate::wire::int(100)])),
        "{told:?}"
    );
    assert!(!scene.asteroids.contains_key(&rock), "a rock mined out is removed");
}

#[test]
fn a_short_cycled_ore_cycle_asks_for_what_it_mined_so_far_and_stops_when_the_host_answers() {
    let (mut scene, t) = reaching(0.0);
    scene.activate(A, mining(3), t);
    let (_, at) = poll_until(&mut scene, t, 1.0);
    let out = scene.deactivate(A, MODULE, at);
    let asked = work_cycles(&out);
    assert_eq!(asked.len(), 1, "a short cycle still asks the host for what it mined: {out:?}");
    // a 2 s cycle stopped about half way: about half of its 20 m3
    assert!(asked[0].2 > 8.0 && asked[0].2 < 12.0, "{asked:?}");
    assert!(ended(&out).is_empty(), "the effect ends with the answer, not before: {out:?}");
    let answer = scene.module_work_result(
        A,
        MODULE,
        ModuleWorkAnswer {
            target_id: partner(),
            cycle_id: asked[0].0,
            accepted: true,
            reason: "",
            depleted: false,
        },
        at,
    );
    assert_eq!(ended(&answer), [1], "{answer:?}");
    assert!(scene.pilots[&A].modules.running.is_empty());
}

#[test]
fn a_short_cycled_ice_cycle_mines_nothing_short_and_runs_to_its_boundary() {
    let (mut scene, t) = reaching(0.0);
    let mut effect = mining(3);
    effect.action = Some(Action::Mining {
        family: MiningFamily::Ice,
        mining_amount_m3: 20.0,
        waste_multiplier: 0.0,
        waste_probability: 0.0,
        crit_probability: 0.0,
        crit_multiplier: 0.0,
    });
    scene.activate(A, effect, t);
    let (_, at) = poll_until(&mut scene, t, 1.0);
    let out = scene.deactivate(A, MODULE, at);
    assert!(work_cycles(&out).is_empty(), "an ice cycle is not short-cycled: {out:?}");
    let (boundary, _) = poll_until(&mut scene, at, 1.5);
    let asked = work_cycles(&boundary);
    assert_eq!(asked.len(), 1, "the cycle ends at its boundary and asks in full: {boundary:?}");
    assert_eq!(asked[0].2, 20.0, "{asked:?}");
}

#[test]
fn a_depleted_rock_stops_the_effect_as_its_target_is_lost_and_no_next_cycle_is_asked() {
    let (mut scene, t) = reaching(0.0);
    scene.activate(A, mining(3), t);
    let (first, at) = poll_until(&mut scene, t, 2.5);
    let asked = work_cycles(&first);
    assert_eq!(asked.len(), 1, "{first:?}");
    let answer = scene.module_work_result(
        A,
        MODULE,
        ModuleWorkAnswer {
            target_id: partner(),
            cycle_id: asked[0].0,
            accepted: true,
            reason: "",
            depleted: true,
        },
        at,
    );
    assert_eq!(ended(&answer), [2], "a depleted target ends the effect as lost: {answer:?}");
    let (later, _) = poll_until(&mut scene, at, 3.0);
    assert!(work_cycles(&later).is_empty(), "{later:?}");
}

fn work_cycles(out: &[Out]) -> Vec<(u64, u8, f64, [f64; 3])> {
    out.iter()
        .filter_map(|item| match item {
            Out::ModuleWorkCycle {
                cycle_id,
                command,
                mining_amount_m3,
                waste_roll,
                rounding_roll,
                crit_roll,
                ..
            } => Some((*cycle_id, *command, *mining_amount_m3, [*waste_roll, *rounding_roll, *crit_roll])),
            _ => None,
        })
        .collect()
}

fn ended(out: &[Out]) -> Vec<u8> {
    out.iter()
        .filter_map(|item| match item {
            Out::ModuleEnded { reason, .. } => Some(*reason),
            _ => None,
        })
        .collect()
}

fn cycles_of(scene: &Scene) -> i32 {
    scene.pilots[&A].modules.running.first().map_or(0, |run| run.cycles)
}

#[test]
fn a_mining_cycle_asks_the_host_at_its_end_and_parks_until_the_answer() {
    let (mut scene, t) = reaching(0.0);
    assert!(matches!(scene.activate(A, mining(3), t).first(), Some(Out::Accepted { command: 17, .. })));

    let (first, at) = poll_until(&mut scene, t, 2.5);
    let asked = work_cycles(&first);
    assert_eq!(asked.len(), 1, "the cycle's end asks the host once: {first:?}");
    let (cycle, command, amount, rolls) = asked[0];
    assert_eq!((command, amount), (7, 20.0));
    assert!(cycle > 0 && rolls.iter().all(|roll| (0.0..1.0).contains(roll)));
    assert_eq!(cycles_of(&scene), 1);

    // parked: no cycle is timed while the host decides, and the same id goes again with the same rolls
    let (waiting, at) = poll_until(&mut scene, at, 9.0);
    assert_eq!(cycles_of(&scene), 1, "no cycle begins while parked");
    let again = work_cycles(&waiting);
    assert!(again.len() >= 3, "sent again every 2 s: {waiting:?}");
    assert!(again.iter().all(|sent| *sent == asked[0]), "a repeat is the same cycle with the same rolls");

    // the answer resumes the module: the next cycle begins from the answer and asks again under a new id
    let out = scene.module_work_result(
        A,
        MODULE,
        ModuleWorkAnswer {
            target_id: partner(),
            cycle_id: cycle,
            accepted: true,
            reason: "",
            depleted: false,
        },
        at,
    );
    assert!(ended(&out).is_empty(), "an accepted cycle does not end the effect: {out:?}");
    assert_eq!(cycles_of(&scene), 2);
    let (next, _) = poll_until(&mut scene, at, 2.5);
    let next_cycles = work_cycles(&next);
    assert_eq!(next_cycles.len(), 1, "{next:?}");
    assert_ne!(next_cycles[0].0, cycle, "a new cycle is a new id");
}

#[test]
fn a_repeated_or_stale_answer_settles_nothing() {
    let (mut scene, t) = reaching(0.0);
    scene.activate(A, mining(3), t);
    let (first, at) = poll_until(&mut scene, t, 2.5);
    let (cycle, ..) = work_cycles(&first)[0];

    assert!(
        scene
            .module_work_result(
                A,
                MODULE,
                ModuleWorkAnswer {
                    target_id: partner(),
                    cycle_id: cycle + 1,
                    accepted: true,
                    reason: "",
                    depleted: false
                },
                at
            )
            .is_empty(),
        "another cycle"
    );
    assert!(
        scene
            .module_work_result(
                A,
                MODULE,
                ModuleWorkAnswer {
                    target_id: partner() + 1,
                    cycle_id: cycle,
                    accepted: true,
                    reason: "",
                    depleted: false
                },
                at
            )
            .is_empty(),
        "another target"
    );
    assert!(
        scene
            .module_work_result(
                B,
                MODULE,
                ModuleWorkAnswer {
                    target_id: partner(),
                    cycle_id: cycle,
                    accepted: true,
                    reason: "",
                    depleted: false
                },
                at
            )
            .is_empty(),
        "another pilot"
    );
    assert_eq!(cycles_of(&scene), 1);

    scene.module_work_result(
        A,
        MODULE,
        ModuleWorkAnswer {
            target_id: partner(),
            cycle_id: cycle,
            accepted: true,
            reason: "",
            depleted: false,
        },
        at,
    );
    assert_eq!(cycles_of(&scene), 2);
    assert!(
        scene
            .module_work_result(
                A,
                MODULE,
                ModuleWorkAnswer {
                    target_id: partner(),
                    cycle_id: cycle,
                    accepted: true,
                    reason: "",
                    depleted: false
                },
                at + SECOND
            )
            .is_empty(),
        "a replay settles nothing"
    );
    assert_eq!(cycles_of(&scene), 2, "the replay does not start a cycle either");
}

#[test]
fn a_refused_work_cycle_stops_the_effect_and_tells_the_host_once() {
    let (mut scene, t) = reaching(0.0);
    scene.activate(A, mining(3), t);
    let (first, at) = poll_until(&mut scene, t, 2.5);
    let (cycle, ..) = work_cycles(&first)[0];

    let out = scene.module_work_result(
        A,
        MODULE,
        ModuleWorkAnswer {
            target_id: partner(),
            cycle_id: cycle,
            accepted: false,
            reason: "",
            depleted: false,
        },
        at,
    );
    assert_eq!(ended(&out), [5], "the host refused: reason 5 once: {out:?}");
    assert!(out.iter().any(|item| matches!(item, Out::ModuleEnded { session: A, module_id: MODULE, .. })));
    assert_eq!(scene.pilots[&A].modules.running.len(), 0);
    let (later, _) = poll_until(&mut scene, at, 6.0);
    assert!(
        work_cycles(&later).is_empty() && ended(&later).is_empty(),
        "nothing follows a refusal: {later:?}"
    );
}

#[test]
fn a_parked_work_cycle_stops_at_once_when_its_target_is_lost() {
    let (mut scene, t) = reaching(0.0);
    scene.activate(A, mining(3), t);
    let (first, at) = poll_until(&mut scene, t, 2.5);
    assert_eq!(work_cycles(&first).len(), 1);

    let dropped = scene.target_order(A, crate::targeting::Order::Remove(vec![partner()]), at);
    assert!(!dropped.is_empty());
    let (out, _) = poll_until(&mut scene, at, 0.5);
    assert_eq!(ended(&out), [2], "the target is gone: reason 2 once: {out:?}");
    assert!(work_cycles(&out).is_empty(), "no resend once the target is gone");
    assert_eq!(scene.pilots[&A].modules.running.len(), 0);
}

#[test]
fn deactivating_a_parked_work_cycle_stops_it_now_and_tells_the_host_once() {
    let (mut scene, t) = reaching(0.0);
    scene.activate(A, mining(3), t);
    let (first, at) = poll_until(&mut scene, t, 2.5);
    assert_eq!(work_cycles(&first).len(), 1);

    let out = scene.deactivate(A, MODULE, at);
    assert!(matches!(out.first(), Some(Out::Accepted { command: 16, .. })), "{out:?}");
    assert_eq!(ended(&out), [1], "stopped by the pilot: reason 1 once: {out:?}");
    assert_eq!(scene.pilots[&A].modules.running.len(), 0);
    let (later, _) = poll_until(&mut scene, at, 6.0);
    assert!(work_cycles(&later).is_empty(), "a stopped module asks for nothing: {later:?}");
}

#[test]
fn a_work_cycle_that_ends_with_the_target_gone_is_not_asked_for() {
    let (mut scene, t) = reaching(0.0);
    scene.activate(A, mining(3), t);
    let dropped = scene.target_order(A, crate::targeting::Order::Remove(vec![partner()]), t + SECOND / 2);
    assert!(!dropped.is_empty());
    let (out, _) = poll_until(&mut scene, t, 3.0);
    assert!(work_cycles(&out).is_empty(), "no yield is owed for a cycle with no target: {out:?}");
    assert_eq!(ended(&out), [2]);
}

#[test]
fn a_passive_targeter_locks_its_target_as_its_cycle_starts() {
    let (mut scene, t) = crowd();
    let mut passive = afterburner();
    passive.action = Some(Action::PassiveLock);
    passive.target_id = partner();
    let out = scene.activate(A, passive, t);
    assert!(!out.iter().any(|o| matches!(o, Out::Refused { .. })), "{out:?}");
    let _ = poll_until(&mut scene, t, 6.0);
    assert_eq!(scene.locked_by(A), vec![partner()]);
    assert_eq!(scene.pilots[&A].modules.running.len(), 1);
}

#[test]
fn an_auto_targeter_locks_the_ship_that_targets_its_pilot_within_its_range() {
    // the partner locks the pilot first; the pilot's auto-targeter then locks the partner, and only within its range
    for (range, locked) in [(50_000.0, true), (1.0, false)] {
        let (mut scene, t) = crowd();
        let (a_ship, b_ship) = (scene.pilots[&A].ship.spec.ship_id, scene.pilots[&B].ship.spec.ship_id);
        scene.target_order(B, Order::Lock(a_ship), t);
        let (_, t) = poll_until(&mut scene, t, 6.0);
        assert_eq!(scene.locked_by(B), vec![a_ship]);
        let mut auto = afterburner();
        auto.action = Some(Action::AutoTarget { range });
        scene.activate(A, auto, t);
        let (_, _) = poll_until(&mut scene, t, 6.0);
        assert_eq!(scene.locked_by(A), if locked { vec![b_ship] } else { Vec::new() }, "range {range}");
    }
}

#[test]
fn an_auto_targeter_is_told_nothing_and_locks_nothing_when_nothing_targets_its_pilot() {
    let (mut scene, t) = crowd();
    let mut auto = afterburner();
    auto.action = Some(Action::AutoTarget { range: 50_000.0 });
    let out = scene.activate(A, auto, t);
    assert!(!out.iter().any(|o| matches!(o, Out::Refused { .. })), "{out:?}");
    let (_, _) = poll_until(&mut scene, t, 6.0);
    assert!(scene.locked_by(A).is_empty());
}

const WRECK: i64 = 90_010;

/// The first outcome's refusal reason (the activation is refused).
fn refusal_of(out: &[Out]) -> &'static str {
    match out.first() {
        Some(Out::Refused { reason, .. }) => reason,
        other => panic!("the activation is refused, not {other:?}"),
    }
}

/// A wreck three kilometres off the test pilot's ship, along +x.
fn tractor_wreck(scene: &Scene, id: i64) -> crate::wreck::Wreck {
    let ship = scene.pilots[&A].ship.position;
    crate::wreck::Wreck {
        id,
        group_id: crate::wreck::WRECK_GROUP,
        kind: crate::wreck::WreckKind { type_id: 1, name: "Wreck".into(), graphic_id: None },
        position: eve_destiny::Vec3::new(ship.x + 3_000.0, ship.y, ship.z),
        direction: eve_destiny::Vec3::new(1.0, 0.0, 0.0),
        radius: 20.0,
        owner_id: 0,
        corporation_id: 0,
        alliance_id: 0,
        war_faction_id: 0,
        rights: crate::wreck::LootRights::default(),
        launcher_id: 0,
        is_empty: false,
        rights_known: false,
        pull: None,
        velocity: eve_destiny::Vec3::ZERO,
        expires_at: NOON + 3_600 * TICKS_PER_SECOND,
    }
}

/// Locks `target` for the test pilot, as the client's order does, and returns the time the lock is had.
fn lock_for_tractor(scene: &mut Scene, target: i64, now: i64) -> i64 {
    scene.target_order(A, Order::Lock(target), now);
    let mut now = now;
    for _ in 0..40 {
        now += TICKS_PER_SECOND / 4;
        scene.poll(now);
    }
    assert!(scene.locked_by(A).contains(&target), "the lock on {target} is had");
    now
}

#[test]
fn a_tractor_refuses_what_it_cannot_pull() {
    let (mut scene, t) = crowd();
    let mut tractor = afterburner();
    tractor.action = Some(Action::Tractor { velocity: 500.0, hold: 500.0, range: 20_000.0 });
    tractor.repeat = 1000;
    assert_eq!(refusal_of(&scene.activate(A, tractor.clone(), t)), "TARGET_REQUIRED");
    // a ship is not a wreck or a can, so it is not there for a tractor (the reference's TARGET_NOT_FOUND)
    tractor.target_id = scene.pilots[&B].ship.spec.ship_id;
    assert_eq!(refusal_of(&scene.activate(A, tractor.clone(), t)), "TARGET_NOT_FOUND");
    let wreck = tractor_wreck(&scene, WRECK);
    scene.place_wreck(wreck, t);
    tractor.target_id = WRECK;
    assert_eq!(refusal_of(&scene.activate(A, tractor.clone(), t)), "TARGET_NOT_LOCKED");
    // a corpse is not a wreck for a beam, though it lies where a wreck does
    let corpse = crate::wreck::Wreck {
        id: WRECK + 1,
        group_id: crate::wreck::CORPSE_GROUP,
        ..tractor_wreck(&scene, WRECK + 1)
    };
    scene.place_wreck(corpse, t);
    tractor.target_id = WRECK + 1;
    assert_eq!(refusal_of(&scene.activate(A, tractor.clone(), t)), "TARGET_NOT_FOUND");
}

#[test]
fn a_tractor_locked_on_a_wreck_out_of_its_reach_is_refused_with_the_range() {
    let (mut scene, t) = crowd();
    let wreck = tractor_wreck(&scene, WRECK);
    scene.place_wreck(wreck, t);
    let t = lock_for_tractor(&mut scene, WRECK, t);
    let mut tractor = afterburner();
    // the wreck is 3 km off, past the module's 2 km reach
    tractor.action = Some(Action::Tractor { velocity: 500.0, hold: 500.0, range: 2_000.0 });
    tractor.repeat = 1000;
    tractor.target_id = WRECK;
    assert!(matches!(
        scene.activate(A, tractor, t).first(),
        Some(Out::TargetRefused { reason: "TARGET_OUT_OF_RANGE", desired_range, .. }) if *desired_range == 2_000.0
    ));
}

#[test]
fn a_tractor_pulls_its_locked_wreck_to_the_hold_distance_and_lets_go_when_stopped() {
    let (mut scene, t) = crowd();
    let wreck = tractor_wreck(&scene, WRECK);
    scene.place_wreck(wreck, t);
    let t = lock_for_tractor(&mut scene, WRECK, t);
    let mut tractor = afterburner();
    tractor.action = Some(Action::Tractor { velocity: 500.0, hold: 500.0, range: 20_000.0 });
    tractor.repeat = 1000;
    tractor.target_id = WRECK;
    let module = tractor.module_id;
    let accepted = scene.activate(A, tractor, t);
    assert!(!accepted.iter().any(|out| matches!(out, Out::Refused { .. })), "the beam is accepted");
    let source = scene.pilots[&A].ship.spec.ship_id;
    assert_eq!(scene.wrecks[&WRECK].pull.map(|pull| pull.source_ship), Some(source));
    // over a minute the wreck comes in to the hold distance, the surface of the ship being the reference's
    let mut now = t;
    for _ in 0..240 {
        now += TICKS_PER_SECOND / 4;
        scene.settle_all(now);
    }
    let ship_radius = scene.pilots[&A].ship.spec.radius;
    let apart = scene.distance_to_wreck(A, WRECK, now).unwrap();
    // the follow step holds the wreck at 499.5 m (the 0.999 standoff share of 500 m) from the ship's centre plus the ship's radius
    assert!((apart - ship_radius - 499.5).abs() < 5.0, "the wreck is held at the hold distance, {apart} m");
    // stopping the beam lets the wreck go where it is
    let stop = scene.stop_module(A, module, now);
    assert!(scene.wrecks[&WRECK].pull.is_none(), "the pull ends with the beam");
    assert!(stop.iter().any(|out| matches!(out, Out::ModuleEnded { module_id, .. } if *module_id == module)));
}

#[test]
fn a_wreck_pulled_by_a_ship_that_leaves_the_scene_is_let_go() {
    let (mut scene, t) = crowd();
    let wreck = tractor_wreck(&scene, WRECK);
    scene.place_wreck(wreck, t);
    let t = lock_for_tractor(&mut scene, WRECK, t);
    let mut tractor = afterburner();
    tractor.action = Some(Action::Tractor { velocity: 500.0, hold: 500.0, range: 20_000.0 });
    tractor.repeat = 1000;
    tractor.target_id = WRECK;
    scene.activate(A, tractor, t);
    assert!(scene.wrecks[&WRECK].pull.is_some(), "the beam pulls the wreck");
    // the pilot logs off while the beam runs: nothing follows the wreck any more, so the next settlement lets it go
    let later = t + TICKS_PER_SECOND;
    let _ = scene.leave(A, crate::scene::Departure::LoggedOff, later);
    scene.settle_all(later + TICKS_PER_SECOND / 4);
    assert!(scene.wrecks[&WRECK].pull.is_none(), "a wreck whose puller left is let go");
}

#[test]
fn every_end_of_an_effect_is_told_to_the_host_once_natural_or_asked() {
    // the cycles are done: one end
    let (mut scene, t) = crowd();
    let mut once = afterburner();
    once.repeat = 1;
    scene.activate(A, once, t);
    let (out, _) = poll_until(&mut scene, t, 12.0);
    assert_eq!(ended(&out), [1], "a one-cycle run ends by itself: {out:?}");

    // the capacitor cannot pay for the next cycle: one end, as it was before
    let (mut scene, t) = crowd();
    let mut all = afterburner();
    all.capacitor_need = 149.0;
    scene.activate(A, all, t);
    let (out, _) = poll_until(&mut scene, t, 11.0);
    assert_eq!(ended(&out), [1], "a capacitor that is short ends the effect: {out:?}");

    // the pilot asks to stop: one end, when the cycle it is in ends
    let (mut scene, t) = crowd();
    let mut long = afterburner();
    long.repeat = 1000;
    scene.activate(A, long, t);
    let asked = scene.deactivate(A, MODULE, t + SECOND);
    assert!(matches!(asked.first(), Some(Out::Accepted { .. })));
    let (out, _) = poll_until(&mut scene, t + SECOND, 12.0);
    assert_eq!(ended(&out), [1], "a host-requested stop is told once: {out:?}");
}

/// An effect that burns fuel and has no target (a siege module's shape): cycles of 2 s, `repeat` of them, no capacitor.
fn burning(repeat: i32) -> Effect {
    let mut effect = afterburner();
    effect.target_id = 0;
    effect.action = None;
    effect.burns_fuel = true;
    effect.duration_ms = 2_000.0;
    effect.capacitor_need = 0.0;
    effect.repeat = repeat;
    effect
}

/// The fuel the host is asked for (`ModuleWorkCycle` with the fuel command): (cycle id, module id, target id).
fn fuel_asks(out: &[Out]) -> Vec<(u64, i64, i64)> {
    out.iter()
        .filter_map(|item| match item {
            Out::ModuleWorkCycle { cycle_id, command: WORK_FUEL, module_id, target_id, .. } => {
                Some((*cycle_id, *module_id, *target_id))
            }
            _ => None,
        })
        .collect()
}

/// The reasons of the activation refusals in `out`.
fn refusals(out: &[Out]) -> Vec<&'static str> {
    out.iter()
        .filter_map(|item| match item {
            Out::Refused { command: 17, reason, .. } => Some(*reason),
            _ => None,
        })
        .collect()
}

#[test]
fn a_fuel_burning_effect_asks_the_host_for_the_fuel_of_its_first_cycle_before_it_starts() {
    let (mut scene, t) = reaching(0.0);
    let out = scene.activate(A, burning(3), t);
    let asks = fuel_asks(&out);
    assert_eq!(asks.len(), 1, "{out:?}");
    assert_eq!(asks[0].1, MODULE);
    assert!(
        !out.iter().any(|item| matches!(item, Out::Accepted { .. })),
        "nothing starts before the fuel is taken: {out:?}"
    );
    assert!(scene.pilots[&A].modules.running.is_empty());
}

#[test]
fn an_accepted_fuel_ask_starts_the_effect_and_a_repeated_answer_starts_nothing_more() {
    let (mut scene, t) = reaching(0.0);
    let (cycle, module, target) = fuel_asks(&scene.activate(A, burning(3), t))[0];
    let started = scene.module_work_result(
        A,
        module,
        ModuleWorkAnswer { target_id: target, cycle_id: cycle, accepted: true, reason: "", depleted: false },
        t,
    );
    assert!(matches!(started.first(), Some(Out::Accepted { command: 17, .. })), "{started:?}");
    assert_eq!(scene.pilots[&A].modules.running.len(), 1);
    assert!(
        scene
            .module_work_result(
                A,
                module,
                ModuleWorkAnswer {
                    target_id: target,
                    cycle_id: cycle,
                    accepted: true,
                    reason: "",
                    depleted: false
                },
                t + SECOND
            )
            .is_empty(),
        "a replay settles nothing"
    );
    assert_eq!(scene.pilots[&A].modules.running.len(), 1);
}

#[test]
fn a_refused_fuel_ask_starts_nothing_and_says_the_host_reason() {
    let (mut scene, t) = reaching(0.0);
    let (cycle, module, target) = fuel_asks(&scene.activate(A, burning(3), t))[0];
    let out = scene.module_work_result(
        A,
        module,
        ModuleWorkAnswer {
            target_id: target,
            cycle_id: cycle,
            accepted: false,
            reason: "NO_FUEL",
            depleted: false,
        },
        t,
    );
    assert_eq!(refusals(&out), ["NO_FUEL"], "{out:?}");
    assert!(scene.pilots[&A].modules.running.is_empty());
    assert!(scene.pilots[&A].modules.pending_starts.is_empty());
}

#[test]
fn a_second_activation_while_the_first_waits_for_its_fuel_is_already_active() {
    let (mut scene, t) = reaching(0.0);
    scene.activate(A, burning(3), t);
    let again = scene.activate(A, burning(3), t);
    assert_eq!(refusals(&again), ["ALREADY_ACTIVE"], "{again:?}");
}

#[test]
fn a_parked_start_is_asked_again_with_the_same_cycle_id() {
    let (mut scene, t) = reaching(0.0);
    let (cycle, ..) = fuel_asks(&scene.activate(A, burning(3), t))[0];
    let (again, _) = poll_until(&mut scene, t, 2.5);
    assert_eq!(fuel_asks(&again).first().map(|ask| ask.0), Some(cycle), "{again:?}");
}

#[test]
fn the_fuel_of_each_following_cycle_is_asked_at_the_cycle_end_and_taken_before_it_starts() {
    let (mut scene, t) = reaching(0.0);
    let (cycle, module, target) = fuel_asks(&scene.activate(A, burning(3), t))[0];
    scene.module_work_result(
        A,
        module,
        ModuleWorkAnswer { target_id: target, cycle_id: cycle, accepted: true, reason: "", depleted: false },
        t,
    );
    // the first cycle ends after 2 s: the fuel of the next is asked, and the next cycle does not start until it is taken
    let (first, at) = poll_until(&mut scene, t, 2.5);
    let asks = fuel_asks(&first);
    assert_eq!(asks.len(), 1, "{first:?}");
    assert_eq!(cycles_of(&scene), 1, "the next cycle has not started");
    scene.module_work_result(
        A,
        MODULE,
        ModuleWorkAnswer {
            target_id: target,
            cycle_id: asks[0].0,
            accepted: true,
            reason: "",
            depleted: false,
        },
        at,
    );
    assert_eq!(cycles_of(&scene), 2);
}

#[test]
fn a_refused_fuel_ask_at_a_cycle_end_stops_the_effect_with_the_fuel_reason_and_nothing_follows() {
    let (mut scene, t) = reaching(0.0);
    let (cycle, module, target) = fuel_asks(&scene.activate(A, burning(3), t))[0];
    scene.module_work_result(
        A,
        module,
        ModuleWorkAnswer { target_id: target, cycle_id: cycle, accepted: true, reason: "", depleted: false },
        t,
    );
    let (first, at) = poll_until(&mut scene, t, 2.5);
    let ask = fuel_asks(&first)[0].0;
    let out = scene.module_work_result(
        A,
        MODULE,
        ModuleWorkAnswer {
            target_id: target,
            cycle_id: ask,
            accepted: false,
            reason: "NO_FUEL",
            depleted: false,
        },
        at,
    );
    assert_eq!(ended(&out), [6], "the stop names the fuel: {out:?}");
    assert!(scene.pilots[&A].modules.running.is_empty());
    let (later, _) = poll_until(&mut scene, at, 3.0);
    assert!(fuel_asks(&later).is_empty() && work_cycles(&later).is_empty(), "{later:?}");
}

#[test]
fn the_last_cycle_of_a_fuel_effect_asks_for_no_more_fuel() {
    let (mut scene, t) = reaching(0.0);
    let (cycle, module, target) = fuel_asks(&scene.activate(A, burning(1), t))[0];
    scene.module_work_result(
        A,
        module,
        ModuleWorkAnswer { target_id: target, cycle_id: cycle, accepted: true, reason: "", depleted: false },
        t,
    );
    let (out, _) = poll_until(&mut scene, t, 2.5);
    assert!(fuel_asks(&out).is_empty(), "{out:?}");
    assert_eq!(ended(&out), [1], "the cycles were done: {out:?}");
}

#[test]
fn an_effect_that_burns_no_fuel_is_never_asked_for_any() {
    let (mut scene, t) = reaching(0.0);
    let mut free = burning(3);
    free.burns_fuel = false;
    let out = scene.activate(A, free, t);
    assert!(fuel_asks(&out).is_empty() && matches!(out.first(), Some(Out::Accepted { .. })), "{out:?}");
    let (later, _) = poll_until(&mut scene, t, 5.0);
    assert!(fuel_asks(&later).is_empty(), "{later:?}");
}

#[test]
fn a_deactivation_while_the_fuel_of_the_first_cycle_is_asked_cancels_the_start() {
    let (mut scene, t) = reaching(0.0);
    let (cycle, module, target) = fuel_asks(&scene.activate(A, burning(3), t))[0];
    let out = scene.deactivate(A, module, t);
    assert_eq!(refusals(&out), ["DEACTIVATED"], "{out:?}");
    assert!(out.iter().any(|item| matches!(item, Out::Accepted { command: 16, .. })), "{out:?}");
    // the host's answer to the cancelled ask starts nothing
    assert!(
        scene
            .module_work_result(
                A,
                module,
                ModuleWorkAnswer {
                    target_id: target,
                    cycle_id: cycle,
                    accepted: true,
                    reason: "",
                    depleted: false
                },
                t + SECOND
            )
            .is_empty()
    );
    assert!(scene.pilots[&A].modules.running.is_empty());
}

#[test]
fn a_settle_takes_a_bounded_number_of_moments_and_the_next_one_takes_the_rest() {
    // bounded: a millisecond cycle left for a thousand seconds is a million cycle ends, which held the sim thread in one settle (M-1)
    finishes_within(20, || {
        let (mut scene, t) = crowd();
        let quick = Effect { duration_ms: 1.0, capacitor_need: 0.0, repeat: 10_000_000, ..afterburner() };
        assert!(scene.activate(A, quick, t).iter().any(|out| matches!(out, Out::Accepted { .. })));
        let later = t + 1_000 * SECOND;
        scene.settle_modules(later);
        let first = scene.pilots[&A].modules.running[0].cycles as usize;
        assert!(first <= MAX_MOMENTS_PER_SETTLE + 1, "one settle took {first} cycles");
        assert!(scene.next_module_moment(A, later).is_some(), "the rest is still due");
        scene.settle_modules(later);
        assert!(scene.pilots[&A].modules.running[0].cycles as usize > first, "the next settle goes on");
    });
}

#[test]
fn a_parked_work_cycle_whose_action_is_gone_ends_the_effect_and_the_settle_comes_back() {
    // bounded: the parked cycle was sent again at the same tick with nothing changed, so the settle never came back (M-1)
    finishes_within(20, || {
        let (mut scene, t) = crowd();
        let now = lock_for_tractor(&mut scene, other_spec().ship_id, t);
        let effect = Effect { target_id: other_spec().ship_id, ..afterburner() };
        assert!(scene.activate(A, effect, now).iter().any(|out| matches!(out, Out::Accepted { .. })));
        let pilot = scene.pilots.get_mut(&A).expect("the pilot is in the scene");
        pilot.modules.running[0].pending_work = Some(PendingWorkCycle {
            cycle_id: 1,
            at: now,
            sent_at: now - 3 * SECOND,
            rolls: [0.0; 3],
            fuel: false,
        });
        let out = scene.settle_modules(now + SECOND);
        assert!(out.iter().any(|item| matches!(item, Out::ModuleEnded { module_id: MODULE, .. })), "{out:?}");
        assert!(scene.pilots[&A].modules.running.is_empty());
    });
}

#[test]
fn a_stalled_moment_ends_its_effect_and_a_charge_moment_is_left_to_the_settle() {
    let (mut scene, t) = crowd();
    assert!(scene.activate(A, afterburner(), t).iter().any(|out| matches!(out, Out::Accepted { .. })));
    let out = scene.stalled(A, Moment::CycleEnd(0), t + SECOND).expect("the effect is ended");
    assert!(out.iter().any(|item| matches!(item, Out::ModuleEnded { module_id: MODULE, .. })));
    assert!(scene.pilots[&A].modules.running.is_empty());
    assert!(scene.stalled(A, Moment::Charge, t).is_none());
}

/// An effect that, while it runs, sets the given attributes of items (what a siege or a sensor array gives its ship or its modules through their modifiers).
fn sets(module: i64, changes: &[(i64, i32, f64)]) -> Effect {
    Effect {
        module_id: module,
        on_changes: changes
            .iter()
            .map(|&(item_id, attribute_id, value)| Change { item_id, attribute_id, value })
            .collect(),
        ..afterburner()
    }
}

#[test]
fn a_running_module_that_holds_the_ship_stops_its_warp_and_its_dock_until_it_ends() {
    let (mut scene, t) = crowd();
    let ship = spec().ship_id;
    let siege = sets(MODULE + 400, &[(ship, WARP_SCRAMBLE_STATUS, 100.0), (ship, DISALLOW_DOCKING, 1.0)]);
    assert!(scene.activate(A, siege, t).iter().any(|out| matches!(out, Out::Accepted { .. })));
    let warp =
        Command::WarpToPoint { destination: eve_destiny::Vec3::new(9_000_000.0, 0.0, 0.0), range: 0.0 };
    let out = scene.command(A, warp, t + SECOND);
    assert!(matches!(out.as_slice(), [Out::Refused { reason: "WARP_SCRAMBLED", .. }]), "{out:?}");
    let out = scene.command(A, Command::Dock { station_id: STATION }, t + SECOND);
    assert!(
        matches!(out.as_slice(), [Out::Refused { reason: "DOCKING_DISALLOWED_BY_MODULE", .. }]),
        "{out:?}"
    );

    scene.deactivate(A, MODULE + 400, t + SECOND);
    let (ended, _) = poll_until(&mut scene, t + SECOND, 15.0);
    assert!(
        ended
            .iter()
            .any(|out| matches!(out, Out::ModuleEnded { module_id, .. } if *module_id == MODULE + 400))
    );
    let out = scene.command(A, warp, t + 20 * SECOND);
    assert!(!out.iter().any(|out| matches!(out, Out::Refused { reason: "WARP_SCRAMBLED", .. })), "{out:?}");
    assert!(scene.pilots[&A].flight.is_some(), "the warp goes once the module has ended");
}

#[test]
fn a_running_module_blocks_the_module_it_names_and_no_other() {
    let (mut scene, t) = crowd();
    let blocked = MODULE + 410;
    let blocker = sets(MODULE + 411, &[(blocked, ACTIVATION_BLOCKED, 1.0)]);
    assert!(scene.activate(A, blocker, t).iter().any(|out| matches!(out, Out::Accepted { .. })));
    let out = scene.activate(A, Effect { module_id: blocked, ..afterburner() }, t);
    assert_eq!(out, [Out::Refused { session: A, command: 17, reason: "MODULE_BLOCKED" }]);
    let out = scene.activate(A, Effect { module_id: MODULE + 412, ..afterburner() }, t);
    assert!(out.iter().any(|out| matches!(out, Out::Accepted { .. })), "{out:?}");
}

#[test]
fn a_computer_is_not_hostile_and_a_disruptor_is_whatever_its_percentages_say() {
    // FP-3 and FP-C6: a positive tracking disruption (a tracking computer) helps; a guidance computer does the other way round from a guidance disruptor
    let tracking_computer =
        Remote::Disrupt { range_percent: 20.0, falloff_percent: 10.0, tracking_percent: 15.0 };
    let tracking_disruptor =
        Remote::Disrupt { range_percent: -20.0, falloff_percent: -10.0, tracking_percent: -15.0 };
    assert!(!tracking_computer.hostile());
    assert!(tracking_disruptor.hostile());
    let guidance_computer = Remote::GuidanceDisrupt {
        velocity_percent: 10.0,
        delay_percent: -10.0,
        aoe_velocity_percent: 10.0,
        aoe_cloud_percent: -10.0,
    };
    let guidance_disruptor = Remote::GuidanceDisrupt {
        velocity_percent: -10.0,
        delay_percent: 10.0,
        aoe_velocity_percent: -10.0,
        aoe_cloud_percent: 10.0,
    };
    assert!(!guidance_computer.hostile());
    assert!(guidance_disruptor.hostile());
}

fn accepted(out: &[Out]) -> bool {
    out.iter().any(|out| matches!(out, Out::Accepted { .. }))
}

#[test]
fn a_group_with_a_limit_runs_that_many_modules_at_once_and_the_next_one_waits_for_it() {
    // maxGroupActive 763 (the afterburner and the microwarpdrive share the propulsion group, one at a time)
    let (mut scene, t) = crowd();
    let propulsion = Rules { group_id: 46, max_group_active: 1, ..Default::default() };
    assert!(accepted(&scene.activate(A, Effect { rules: propulsion, ..afterburner() }, t)));
    let mwd = Effect { rules: propulsion, ..microwarpdrive() };
    assert_eq!(
        scene.activate(A, mwd.clone(), t),
        [Out::Refused { session: A, command: 17, reason: "MAX_GROUP_ACTIVE" }]
    );
    scene.deactivate(A, MODULE, t + SECOND);
    poll_until(&mut scene, t + SECOND, 15.0);
    assert!(
        accepted(&scene.activate(A, mwd, t + 20 * SECOND)),
        "the group has room once the afterburner has ended"
    );
}

#[test]
fn two_smartbombs_run_at_once_and_only_the_same_module_twice_is_refused() {
    // the QA Smartbomb has no maxGroupActive in the SDE, so the bombs of a ship run together; the refusal is per module (ALREADY_ACTIVE)
    let (mut scene, t) = crowd();
    let bomb = |module_id| Effect {
        module_id,
        effect_id: 9,
        fx: "effects.EMPWave".to_string(),
        action: None,
        duration_ms: 3_000.0,
        capacitor_need: 1.0,
        ..afterburner()
    };
    assert!(accepted(&scene.activate(A, bomb(MODULE + 300), t)));
    assert!(accepted(&scene.activate(A, bomb(MODULE + 301), t)), "a second bomb runs with the first");
    assert_eq!(
        scene.activate(A, bomb(MODULE + 300), t),
        [Out::Refused { session: A, command: 17, reason: "ALREADY_ACTIVE" }]
    );
}

#[test]
fn a_cloak_is_refused_while_a_module_that_cannot_be_deactivated_early_runs() {
    // disallowEarlyDeactivation 906 on a running module: the ship cannot cloak (EveJS CLOAK_MODULE_ACTIVE)
    let (mut scene, t) = crowd();
    let held =
        Effect { rules: Rules { disallow_early_deactivation: true, ..Default::default() }, ..afterburner() };
    assert!(accepted(&scene.activate(A, held, t)));
    let cloak =
        Effect { module_id: MODULE + 500, action: Some(Action::Cloak { warp_safe: false }), ..afterburner() };
    assert_eq!(
        scene.activate(A, cloak.clone(), t),
        [Out::Refused { session: A, command: 17, reason: "CLOAK_MODULE_ACTIVE" }]
    );
    scene.deactivate(A, MODULE, t + SECOND);
    poll_until(&mut scene, t + SECOND, 15.0);
    assert!(accepted(&scene.activate(A, cloak, t + 20 * SECOND)), "the cloak goes once the module has ended");
}

#[test]
fn a_module_that_needs_an_industrial_core_runs_only_while_one_runs_and_stops_with_it() {
    // activationRequiresActiveIndustrialCore 3265: a compressor waits for a core, and stops when the last core that runs stops
    let (mut scene, t) = crowd();
    let compressor = Effect {
        module_id: MODULE + 510,
        rules: Rules { requires_industrial_core: true, ..Default::default() },
        ..afterburner()
    };
    assert_eq!(
        scene.activate(A, compressor.clone(), t),
        [Out::Refused { session: A, command: 17, reason: "ACTIVE_INDUSTRIAL_CORE_REQUIRED" }]
    );
    let core = Effect {
        module_id: MODULE + 511,
        rules: Rules { provides_industrial_core: true, ..Default::default() },
        ..afterburner()
    };
    assert!(accepted(&scene.activate(A, core, t)));
    assert!(accepted(&scene.activate(A, compressor, t)));
    scene.deactivate(A, MODULE + 511, t + SECOND);
    let (ended, _) = poll_until(&mut scene, t + SECOND, 15.0);
    assert!(
        ended
            .iter()
            .any(|out| matches!(out, Out::ModuleEnded { module_id, .. } if *module_id == MODULE + 510)),
        "the compressor stops with its core"
    );
}

#[test]
fn a_module_with_a_reactivation_delay_waits_after_it_stops() {
    // the reactivation delay (669) runs from the stop, on the simulation's clock
    let (mut scene, t) = crowd();
    let cloaking = Effect {
        module_id: MODULE + 520,
        rules: Rules { reactivation_delay_ms: 30_000.0, ..Default::default() },
        ..afterburner()
    };
    assert!(accepted(&scene.activate(A, cloaking.clone(), t)));
    scene.deactivate(A, MODULE + 520, t + SECOND);
    poll_until(&mut scene, t + SECOND, 15.0);
    assert_eq!(
        scene.activate(A, cloaking.clone(), t + 20 * SECOND),
        [Out::Refused { session: A, command: 17, reason: "MODULE_REACTIVATING" }]
    );
    assert!(
        accepted(&scene.activate(A, cloaking, t + 60 * SECOND)),
        "it starts again once the delay has passed"
    );
}

#[test]
fn a_module_that_its_own_effect_burns_out_does_not_start_again() {
    // an emergency hull energizer sets its module's damage to its hit points as it ends, and stays burnt out (MODULE_INCAPACITATED)
    let (mut scene, t) = crowd();
    let burning = Effect {
        off_changes: vec![Change { item_id: MODULE, attribute_id: MODULE_DAMAGE, value: 100.0 }],
        ..afterburner()
    };
    assert!(accepted(&scene.activate(A, burning.clone(), t)));
    scene.deactivate(A, MODULE, t + SECOND);
    poll_until(&mut scene, t + SECOND, 15.0);
    assert_eq!(
        scene.activate(A, burning, t + 20 * SECOND),
        [Out::Refused { session: A, command: 17, reason: "MODULE_INCAPACITATED" }]
    );
}

// ---- scanners (protocol 40): a cargo or ship scan is a work cycle the host settles; the simulation keeps the lock and the reach ----

/// A scanner of `cargo` or ship kind, one cycle, locked on the partner ship: its reach is `optimal` metres, then `falloff` metres of falloff.
fn scanner(cargo: bool, optimal: f64, falloff: f64) -> Effect {
    let mut effect = afterburner();
    effect.target_id = partner();
    effect.action = Some(Action::Scan { cargo, reach: Reach { optimal, falloff } });
    effect.effect_id = if cargo { 47 } else { 46 };
    effect.duration_ms = 2_000.0;
    effect.capacitor_need = 0.0;
    effect.repeat = 1;
    effect
}

#[test]
fn a_scanner_needs_a_target_it_holds_a_lock_on_that_is_a_ship_and_in_reach() {
    let (mut scene, t) = crowd();
    let mut scan = scanner(true, 50_000.0, 0.0);
    scan.target_id = 0;
    assert_eq!(refusal_of(&scene.activate(A, scan.clone(), t)), "TARGET_REQUIRED");
    scan.target_id = scene.pilots[&A].ship.spec.ship_id;
    assert_eq!(refusal_of(&scene.activate(A, scan.clone(), t)), "TARGET_SELF");
    scan.target_id = WRECK;
    assert_eq!(refusal_of(&scene.activate(A, scan.clone(), t)), "TARGET_NOT_FOUND");
    // the partner is there but not locked: the scanner does not reach what the pilot does not hold
    scan.target_id = partner();
    assert_eq!(refusal_of(&scene.activate(A, scan.clone(), t)), "TARGET_NOT_LOCKED");
    let (mut locked, t) = reaching(0.0);
    assert!(matches!(
        locked.activate(A, scanner(true, 50_000.0, 0.0), t).first(),
        Some(Out::Accepted { command: 17, .. })
    ));
}

#[test]
fn a_cargo_scan_is_a_hard_cutoff_and_a_ship_scan_falls_off_past_its_optimal_range() {
    // 10 km off: a 5 km cargo scanner is out of its reach and told how far it reaches; a 5 km ship scanner with 3 km falloff still reaches it at 7 km
    let (mut scene, t) = reaching(10_000.0);
    assert!(matches!(
        scene.activate(A, scanner(true, 5_000.0, 0.0), t).first(),
        Some(Out::TargetRefused { reason: "TARGET_OUT_OF_RANGE", desired_range, .. }) if *desired_range == 5_000.0
    ));
    let (mut scene, t) = reaching(7_000.0);
    assert!(matches!(
        scene.activate(A, scanner(false, 5_000.0, 3_000.0), t).first(),
        Some(Out::Accepted { command: 17, .. })
    ));
    let (mut scene, t) = reaching(9_000.0);
    assert!(matches!(
        scene.activate(A, scanner(false, 5_000.0, 3_000.0), t).first(),
        Some(Out::TargetRefused { reason: "TARGET_OUT_OF_RANGE", desired_range, .. }) if *desired_range == 8_000.0
    ));
}

#[test]
fn a_scan_asks_the_host_at_its_end_for_the_reveal_and_ends_when_the_host_grants_it() {
    let (mut scene, t) = reaching(0.0);
    assert!(matches!(
        scene.activate(A, scanner(true, 50_000.0, 0.0), t).first(),
        Some(Out::Accepted { command: 17, .. })
    ));
    let (first, at) = poll_until(&mut scene, t, 3.0);
    let asked = work_cycles(&first);
    assert_eq!(asked.len(), 1, "the scan's end asks the host once: {first:?}");
    let (cycle, command, amount, _) = asked[0];
    assert_eq!((command, amount), (10, 0.0), "a cargo scan is command 10 with no mining numbers");
    // parked until the answer; the host reveals the hold and says the target stays (complete = false), so the effect ends as done, not as target lost
    let out = scene.module_work_result(
        A,
        MODULE,
        ModuleWorkAnswer {
            target_id: partner(),
            cycle_id: cycle,
            accepted: true,
            reason: "",
            depleted: false,
        },
        at,
    );
    assert_eq!(ended(&out), vec![1], "the one cycle is done: {out:?}");
    assert_eq!(scene.pilots[&A].modules.running_count(), 0);
}

#[test]
fn a_ship_scan_asks_for_command_11_and_a_refused_answer_ends_the_scan_with_the_host_reason() {
    let (mut scene, t) = reaching(0.0);
    assert!(matches!(
        scene.activate(A, scanner(false, 50_000.0, 10_000.0), t).first(),
        Some(Out::Accepted { command: 17, .. })
    ));
    let (first, at) = poll_until(&mut scene, t, 3.0);
    let asked = work_cycles(&first);
    assert_eq!(asked.len(), 1, "{first:?}");
    assert_eq!(asked[0].1, 11, "a ship scan is command 11");
    let out = scene.module_work_result(
        A,
        MODULE,
        ModuleWorkAnswer {
            target_id: partner(),
            cycle_id: asked[0].0,
            accepted: false,
            reason: "TARGET_RESISTS_SCAN",
            depleted: false,
        },
        at,
    );
    assert_eq!(ended(&out), vec![5], "a refused answer stops the scan (WorkRefused): {out:?}");
}

#[test]
fn a_scan_whose_target_leaves_its_reach_by_the_cycle_end_stops_and_asks_the_host_nothing() {
    let (mut scene, t) = reaching(0.0);
    assert!(matches!(
        scene.activate(A, scanner(true, 50_000.0, 0.0), t).first(),
        Some(Out::Accepted { command: 17, .. })
    ));
    let mut far = scene.pilots[&B].ship.position;
    far.x += 500_000.0;
    scene.pilots.get_mut(&B).unwrap().ship.position = far;
    let (out, _) = poll_until(&mut scene, t, 3.0);
    assert!(work_cycles(&out).is_empty(), "no reveal is asked for a target out of reach: {out:?}");
    assert_eq!(ended(&out), vec![2], "the scan stops as its target is lost: {out:?}");
}

/// A command burst's effect (protocol 43): a Shield Command Burst I's cycle of one minute, reaching `range` metres, charged with two rounds, one a cycle.
fn burst(range: f64) -> Effect {
    let mut effect = repairer(Layer::Shield, 0.0, Applies::CycleEnd);
    effect.module_type = 42529;
    effect.effect_id = 6733;
    effect.fx = String::new();
    effect.duration_ms = 60_000.0;
    effect.capacitor_need = 25.0;
    effect.action = Some(Action::Charged {
        action: Box::new(Action::CommandBurst { range_meters: range }),
        fallback: None,
        charge_type_id: 42695,
        charge_flag: 27,
        charge_per_cycle: 1,
        charges: 2,
    });
    effect
}

/// The burst cycles asked of the host: the cycle id and the ships the simulation found in reach.
fn burst_asks(out: &[Out]) -> Vec<(u64, Vec<i64>)> {
    out.iter()
        .filter_map(|item| match item {
            Out::ModuleWorkCycle { cycle_id, command: WORK_BURST, recipients, .. } => {
                Some((*cycle_id, recipients.clone()))
            }
            _ => None,
        })
        .collect()
}

/// The arguments of the `OnDbuffUpdated` the pilot is told (the ship, then the list of buffs), if one is told.
fn dbuff_arguments(out: &[Out], session: SessionId) -> Option<Vec<Value>> {
    out.iter().find_map(|o| match o {
        Out::Notify { session: s, name: "DoDestinyUpdate", args, .. } if *s == session => {
            let Some(Value::List(actions)) = args.first() else { return None };
            actions.iter().find_map(|a| {
                let Value::Tuple(pair) = a else { return None };
                let Value::Tuple(inner) = &pair[1] else { return None };
                let (Value::Str(name), Value::Tuple(arguments)) = (&inner[0], &inner[1]) else { return None };
                (name == "OnDbuffUpdated").then(|| arguments.clone())
            })
        }
        _ => None,
    })
}

#[test]
fn a_command_burst_cycle_asks_the_host_for_the_ships_in_reach_and_is_parked_until_it_is_answered() {
    let (mut scene, t) = crowd();
    let (ship_a, ship_b) = (scene.pilots[&A].ship.spec.ship_id, scene.pilots[&B].ship.spec.ship_id);
    scene.activate(A, burst(15_000.0), t);
    let (out, at) = poll_until(&mut scene, t, 61.0);
    let asks = burst_asks(&out);
    assert_eq!(asks.len(), 1, "{out:?}");
    let (cycle, recipients) = asks[0].clone();
    let mut expected = vec![ship_a, ship_b];
    expected.sort_unstable();
    assert_eq!(recipients, expected);

    // parked: the same cycle is asked again while the host has not answered, and no other
    let (waiting, later) = poll_until(&mut scene, at, 10.0);
    assert!(burst_asks(&waiting).iter().all(|(id, _)| *id == cycle), "{waiting:?}");

    // granted: the effect goes on, and its next cycle asks the host again under a new cycle id
    scene.module_work_result(
        A,
        MODULE,
        ModuleWorkAnswer { target_id: 0, cycle_id: cycle, accepted: true, reason: "", depleted: false },
        later,
    );
    let (next, end_at) = poll_until(&mut scene, later, 61.0);
    let second = burst_asks(&next).into_iter().map(|(id, _)| id).find(|id| *id != cycle);
    let second = second.unwrap_or_else(|| panic!("the second cycle is asked: {:?}", labels(&next)));

    // the second cycle spent the last charge, so it runs to its end: its answer is the last grant, and the burst ends with it
    let last = scene.module_work_result(
        A,
        MODULE,
        ModuleWorkAnswer { target_id: 0, cycle_id: second, accepted: true, reason: "", depleted: false },
        end_at,
    );
    assert!(
        last.iter().any(|o| matches!(o, Out::ModuleEnded { module_id: MODULE, reason: 4, .. })),
        "{last:?}"
    );
}

#[test]
fn a_command_burst_reaches_the_ships_within_its_range_and_no_others() {
    let (mut scene, t) = crowd();
    let ship_a = scene.pilots[&A].ship.spec.ship_id;
    // B is moved 40 km off, beyond a 15 km reach
    scene.pilots.get_mut(&B).unwrap().ship.position.x += 40_000.0;
    scene.activate(A, burst(15_000.0), t);
    let (out, _) = poll_until(&mut scene, t, 61.0);
    assert_eq!(burst_asks(&out).first().map(|(_, ships)| ships.clone()), Some(vec![ship_a]), "{out:?}");
}

#[test]
fn a_refused_command_burst_cycle_ends_the_burst_with_its_work_refused() {
    let (mut scene, t) = crowd();
    scene.activate(A, burst(15_000.0), t);
    let (out, at) = poll_until(&mut scene, t, 61.0);
    let (cycle, _) = burst_asks(&out)[0].clone();
    let told = scene.module_work_result(
        A,
        MODULE,
        ModuleWorkAnswer {
            target_id: 0,
            cycle_id: cycle,
            accepted: false,
            reason: "NOT_CONTROLLED",
            depleted: false,
        },
        at,
    );
    assert!(
        told.iter().any(|o| matches!(o, Out::ModuleEnded { module_id: MODULE, reason: 5, .. })),
        "{told:?}"
    );
}

#[test]
fn a_ships_buffs_are_told_to_its_pilot_as_on_dbuff_updated_with_the_time_left() {
    let (mut scene, t) = crowd();
    let ship = scene.pilots[&A].ship.spec.ship_id;
    let entries = vec![crate::link::BuffEntry { collection_id: 10, value: -10.0, remaining_ms: 60_000.0 }];
    let told = scene.set_buffs(A, ship, entries, t);
    let arguments = dbuff_arguments(&told, A).expect("the pilot is told its buffs");
    assert_eq!(arguments[0], crate::wire::id(ship));
    assert_eq!(
        arguments[1],
        crate::wire::list([crate::wire::tuple([
            crate::wire::int(10),
            crate::wire::tuple([crate::wire::real(-10.0), crate::wire::filetime(t + 600_000_000)]),
        ])])
    );
    // a state for a ship that is not the session's is told to nobody
    assert!(scene.set_buffs(A, ship + 1, Vec::new(), t).is_empty());
}

/// A P.A.N.I.C. (type 42522, protocol 43): one cycle of its duration (240 s), reaching `range` metres, with no capacitor and no charge.
fn panic_effect(range: f64) -> Effect {
    let mut effect = repairer(Layer::Shield, 0.0, Applies::CycleEnd);
    effect.module_type = 42522;
    effect.effect_id = 6719;
    effect.fx = String::new();
    effect.duration_ms = 240_000.0;
    effect.capacitor_need = 0.0;
    effect.repeat = 1;
    effect.action = Some(Action::Panic { range_meters: range });
    effect
}

#[test]
fn a_panic_without_a_locked_asteroid_is_refused_and_asks_nobody() {
    let (mut scene, t) = crowd();
    let out = scene.activate(A, panic_effect(200_000.0), t);
    assert!(
        matches!(out.as_slice(), [Out::Refused { session: A, command: 17, reason: "TARGET_REQUIRED" }]),
        "{out:?}"
    );
    assert!(!scene.pilots[&A].modules.running.iter().any(|run| run.effect.module_id == MODULE));
}

#[test]
fn a_panic_asks_the_ships_in_its_reach_for_their_buffs_and_starts_when_they_are_granted() {
    let (mut scene, t) = mining_rock(99_300_001);
    let (ship_a, ship_b) = (scene.pilots[&A].ship.spec.ship_id, scene.pilots[&B].ship.spec.ship_id);
    let asked = scene.activate(A, panic_effect(200_000.0), t);
    let (cycle, recipients) = asked
        .iter()
        .find_map(|o| match o {
            Out::ModuleWorkCycle { cycle_id, command: WORK_PANIC, recipients, .. } => {
                Some((*cycle_id, recipients.clone()))
            }
            _ => None,
        })
        .expect("the P.A.N.I.C. asks the host for its fleet's buffs");
    let mut expected = vec![ship_a, ship_b];
    expected.sort_unstable();
    assert_eq!(recipients, expected);
    assert!(asked.iter().any(|o| matches!(o, Out::ModuleWorkCycle { command: WORK_PANIC, .. })), "{asked:?}");
    assert!(
        !scene.pilots[&A].modules.running.iter().any(|run| run.effect.module_id == MODULE),
        "parked, not running"
    );

    scene.module_work_result(
        A,
        MODULE,
        ModuleWorkAnswer { target_id: 0, cycle_id: cycle, accepted: true, reason: "", depleted: false },
        t,
    );
    assert!(
        scene.pilots[&A].modules.running.iter().any(|run| run.effect.module_id == MODULE),
        "granted, it starts"
    );
}

#[test]
fn a_panic_reaches_only_the_ships_within_200_km() {
    let (mut scene, t) = mining_rock(99_300_002);
    let ship_a = scene.pilots[&A].ship.spec.ship_id;
    scene.pilots.get_mut(&B).unwrap().ship.position.x += 300_000.0;
    let asked = scene.activate(A, panic_effect(200_000.0), t);
    let recipients = asked
        .iter()
        .find_map(|o| match o {
            Out::ModuleWorkCycle { command: WORK_PANIC, recipients, .. } => Some(recipients.clone()),
            _ => None,
        })
        .expect("the P.A.N.I.C. asks");
    assert_eq!(recipients, vec![ship_a], "{asked:?}");
}

#[test]
fn a_refused_panic_grant_ends_the_start_with_the_host_reason() {
    let (mut scene, t) = mining_rock(99_300_003);
    let asked = scene.activate(A, panic_effect(200_000.0), t);
    let cycle = asked
        .iter()
        .find_map(|o| match o {
            Out::ModuleWorkCycle { cycle_id, command: WORK_PANIC, .. } => Some(*cycle_id),
            _ => None,
        })
        .expect("the P.A.N.I.C. asks");
    let refused = scene.module_work_result(
        A,
        MODULE,
        ModuleWorkAnswer {
            target_id: 0,
            cycle_id: cycle,
            accepted: false,
            reason: "NOT_CONTROLLED",
            depleted: false,
        },
        t,
    );
    assert!(
        matches!(refused.as_slice(), [Out::Refused { session: A, command: 17, reason: "NO_SHIP_IN_SPACE" }]),
        "{refused:?}"
    );
    assert!(!scene.pilots[&A].modules.running.iter().any(|run| run.effect.module_id == MODULE));
}

fn cloaking_device() -> Effect {
    let mut effect = repairer(Layer::Armor, 1.0, Applies::CycleStart);
    effect.module_type = 11577;
    effect.effect_id = 197;
    effect.fx = "effects.Cloaking".to_string();
    effect.action = Some(Action::Cloak { warp_safe: false });
    effect
}

fn cloak_notice(out: &Out) -> Option<(&'static str, Vec<Value>)> {
    match out {
        Out::Notify { name: "OnCloakModuleForceDeactivated", id_type, args, .. } => {
            Some((id_type, args.clone()))
        }
        _ => None,
    }
}

#[test]
fn a_ship_that_comes_within_two_kilometres_breaks_a_cloak_and_the_pilot_is_told_the_type() {
    let (mut scene, t) = reaching(5_000.0);
    scene.target_order(A, crate::targeting::Order::Clear, t);
    assert!(accepted(&scene.activate(A, cloaking_device(), t)));
    assert_eq!(scene.pilots[&A].cloak, crate::visibility::CloakState::Cloaked);
    let (quiet, t) = poll_until(&mut scene, t, 3.0);
    assert!(quiet.iter().all(|out| cloak_notice(out).is_none()), "5 km off is no trigger");
    assert_eq!(scene.pilots[&A].cloak, crate::visibility::CloakState::Cloaked);

    let (here, ra) = (scene.pilots[&A].ship.position, scene.pilots[&A].ship.spec.radius);
    let (b_type, rb) = (scene.pilots[&B].ship.spec.type_id, scene.pilots[&B].ship.spec.radius);
    scene.pilots.get_mut(&B).unwrap().ship.position =
        eve_destiny::Vec3 { x: here.x + 1_999.0 + ra + rb, y: here.y, z: here.z };
    let told = scene.poll(t + SECOND);

    let notices: Vec<_> = told
        .iter()
        .filter(|out| matches!(out, Out::Notify { session: A, .. }))
        .filter_map(cloak_notice)
        .collect();
    assert_eq!(
        notices,
        [(
            "shipid",
            vec![
                Value::Str("CloakDeactivatedByProximityToType".into()),
                Value::Dict(vec![(Value::Str("proximityTypeID".into()), Value::Int(i64::from(b_type)))]),
            ],
        )]
    );
    let order = labels(&told);
    let notice_at = order.iter().position(|l| l == "7:OnCloakModuleForceDeactivated").expect("the notice");
    let off_at = order.iter().position(|l| l.starts_with("7:destiny")).expect("the module goes off");
    assert!(notice_at < off_at, "{order:?}");
    assert_eq!(scene.pilots[&A].cloak, crate::visibility::CloakState::Uncloaked);
    assert!(scene.pilots[&A].modules.running.is_empty());
    assert!(
        poll_until(&mut scene, t + SECOND, 3.0).0.iter().all(|out| cloak_notice(out).is_none()),
        "told once"
    );
}

#[test]
fn a_station_within_two_kilometres_refuses_a_cloak_and_breaks_one_that_runs() {
    let (mut scene, t) = crowd();
    let station = scene.system().body(STATION).unwrap().clone();
    let away = scene.pilots[&A].ship.position;
    let radius = scene.pilots[&A].ship.spec.radius;
    let beside =
        eve_destiny::Vec3 { x: station.position.x + station.radius + radius + 1_000.0, ..station.position };
    scene.pilots.get_mut(&A).unwrap().ship.position = beside;
    assert_eq!(
        scene.activate(A, cloaking_device(), t),
        [Out::Refused { session: A, command: 17, reason: "CLOAK_PROXIMITY_BLOCKED" }]
    );

    scene.pilots.get_mut(&A).unwrap().ship.position = away;
    assert!(accepted(&scene.activate(A, cloaking_device(), t)));
    scene.pilots.get_mut(&A).unwrap().ship.position = beside;
    let told = scene.poll(t + SECOND);
    let notices: Vec<_> = told.iter().filter_map(cloak_notice).collect();
    assert_eq!(
        notices,
        [(
            "shipid",
            vec![
                Value::Str("CloakDeactivatedByProximityToType".into()),
                Value::Dict(vec![(
                    Value::Str("proximityTypeID".into()),
                    Value::Int(i64::from(station.type_id))
                )]),
            ],
        )]
    );
    assert_eq!(scene.pilots[&A].cloak, crate::visibility::CloakState::Uncloaked);
}

#[test]
fn a_cloak_ended_for_another_reason_tells_the_custom_notice_after_the_module_has_stopped() {
    let (mut scene, t) = reaching(5_000.0);
    scene.target_order(A, crate::targeting::Order::Clear, t);
    assert!(accepted(&scene.activate(A, cloaking_device(), t)));
    let cause = DecloakCause::Custom {
        reason: "remoteDecloakPing".to_string(),
        notice: "Your cloak has been deactivated by a Mobile Observatory decloaking ping.".to_string(),
        trigger: CloakTrigger { item_id: 1_000_000_000_042, type_id: 56_173, distance: 1_234.567_89 },
    };
    let told = scene.force_decloak(A, cause.clone(), t + SECOND);
    let order = labels(&told);
    assert_eq!(order.last().map(String::as_str), Some("7:OnCloakModuleForceDeactivated"), "{order:?}");
    let (id_type, args) = cloak_notice(told.last().unwrap()).unwrap();
    assert_eq!(id_type, "shipid");
    assert_eq!(
        args,
        vec![
            Value::Str("CustomNotify".into()),
            Value::Dict(vec![
                (
                    Value::Str("notify".into()),
                    Value::Str(
                        "Your cloak has been deactivated by a Mobile Observatory decloaking ping.".into()
                    )
                ),
                (Value::Str("decloakReason".into()), Value::Str("remoteDecloakPing".into())),
                (Value::Str("itemID".into()), Value::Long(1_000_000_000_042)),
                (Value::Str("distanceMeters".into()), Value::Real(1_234.568)),
            ]),
        ]
    );
    assert_eq!(scene.pilots[&A].cloak, crate::visibility::CloakState::Uncloaked);
    assert!(
        scene.force_decloak(A, cause, t + 2 * SECOND).is_empty(),
        "an uncloaked ship has nothing to break"
    );
}
