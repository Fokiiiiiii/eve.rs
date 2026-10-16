//! The warp-disruption sources and the bombs of the simulation (`deployable`): a bubble holds the ships in its range unless they are immune, a cloak that is not
//! warp-safe holds its ship, a probe holds the ships in its range until its lifetime is up and is then taken out, and a bomb's blast reaches the ships within its
//! radius but not its launcher.

use super::*;
use crate::modules::{Action, Effect};
use crate::scene::tests::{NOON, STATION, spec, system};
use crate::scene::{Command, Out, Scene};
use crate::visibility::CloakState;
use crate::world::SolarSystem;
use std::sync::Arc;

const A: SessionId = 7;
const B: SessionId = 8;
const SECOND: i64 = 10_000_000;
const MODULE: i64 = 9_988_400_001_940;
const PROBE_SPEC: fn() -> Spec =
    || Spec { charge_type_id: 22_778, group_id: 548, name: "Warp Disrupt Probe".to_string(), radius: 40.0 };

/// A ship of its own: the scene's spec, with its ids moved on.
fn other_spec() -> crate::ship::ShipSpec {
    let mut other = spec();
    other.ship_id += 4;
    other.character_id += 1;
    other.owner_id += 1;
    other
}

/// Two ships in a system, five kilometres apart, both bound: `A` and `B`, with the clock thirty seconds after they undocked.
fn crowd() -> (Scene, i64) {
    let mut scene = Scene::new(system());
    scene.undock(A, spec(), STATION, NOON).unwrap();
    scene.bind(A, NOON);
    scene.undock(B, other_spec(), STATION, NOON).unwrap();
    scene.bind(B, NOON);
    let a = scene.pilots[&A].ship.position;
    place(&mut scene, B, eve_destiny::Vec3::new(a.x + 5_000.0, a.y, a.z));
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

/// A module's effect that runs one cycle of `duration_ms` and does `action`.
fn module(module_id: i64, action: Action, duration_ms: f64) -> Effect {
    Effect {
        burns_fuel: false,
        module_id,
        module_type: 1,
        effect_id: 1,
        fx: String::new(),
        duration_ms,
        capacitor_need: 0.0,
        repeat: 1,
        target_id: 0,
        turret: None,
        action: Some(action),
        on: None,
        off: None,
        on_changes: Vec::new(),
        off_changes: Vec::new(),
        rules: Default::default(),
    }
}

/// A warp to a point far off, which nothing blocks but the warp sources.
fn warp(scene: &mut Scene, session: SessionId, now: i64) -> Vec<Out> {
    scene.command(
        session,
        Command::WarpToPoint { destination: eve_destiny::Vec3::new(1.0e11, 0.0, 0.0), range: 0.0 },
        now,
    )
}

/// Polls the scene every quarter second for `seconds` from `from`: what it told, and the clock at the end.
fn poll_for(scene: &mut Scene, from: i64, seconds: f64) -> (Vec<Out>, i64) {
    let mut out = Vec::new();
    let mut t = from;
    while t < from + (seconds * SECOND as f64) as i64 {
        t += SECOND / 4;
        out.extend(scene.poll(t));
    }
    (out, t)
}

fn accepted(out: &[Out]) -> bool {
    out.iter().any(|o| matches!(o, Out::Accepted { .. }))
}

fn refused_with(out: &[Out], reason: &str) -> bool {
    out.iter().any(|o| matches!(o, Out::Refused { reason: r, .. } if *r == reason))
}

#[test]
fn a_bubble_holds_the_ships_in_its_range_and_lets_the_others_warp() {
    let (mut scene, t) = crowd();
    let started = scene.activate(A, module(MODULE, Action::WarpBubble { range: 16_000.0 }, 30_000.0), t);
    assert!(accepted(&started), "{started:?}");
    let held = warp(&mut scene, B, t + SECOND);
    assert!(refused_with(&held, "WARP_DISRUPTED_BY_BUBBLE"), "{held:?}");
    // a ship beyond the field's range is not in it
    place(&mut scene, B, eve_destiny::Vec3::new(40_000.0, 0.0, 0.0));
    let free = warp(&mut scene, B, t + 2 * SECOND);
    assert!(accepted(&free), "{free:?}");
}

#[test]
fn an_immune_ship_is_not_held_by_a_bubble() {
    let (mut scene, t) = crowd();
    assert!(accepted(&scene.activate(
        A,
        module(MODULE, Action::WarpBubble { range: 16_000.0 }, 30_000.0),
        t
    )));
    assert!(accepted(&scene.activate(B, module(MODULE + 1, Action::WarpImmunity, 10_000.0), t + SECOND)));
    let through = warp(&mut scene, B, t + 2 * SECOND);
    assert!(accepted(&through), "{through:?}");
}

#[test]
fn a_cloak_that_is_not_warp_safe_holds_its_ship_and_a_covert_one_does_not() {
    let (mut scene, t) = crowd();
    // a cycle of two seconds: the cloak runs until its cycle ends (the simulation keeps an effect to the end of its cycle)
    assert!(accepted(&scene.activate(
        B,
        module(MODULE, Action::Cloak { warp_safe: false, targeting_delay_ms: 0.0 }, 2_000.0),
        t
    )));
    assert_eq!(scene.pilots[&B].cloak, CloakState::Cloaked);
    let held = warp(&mut scene, B, t + SECOND);
    assert!(refused_with(&held, "WARP_BLOCKED_BY_CLOAK"), "{held:?}");
    let (_, t2) = poll_for(&mut scene, t + SECOND, 6.0);
    assert_eq!(scene.pilots[&B].cloak, CloakState::Uncloaked);
    assert!(accepted(&scene.activate(
        B,
        module(MODULE + 1, Action::Cloak { warp_safe: true, targeting_delay_ms: 0.0 }, 30_000.0),
        t2
    )));
    let covert = warp(&mut scene, B, t2 + SECOND);
    assert!(accepted(&covert), "{covert:?}");
}

#[test]
fn a_probe_holds_the_ships_in_its_range_until_its_lifetime_is_up_and_is_then_taken_out() {
    let (mut scene, t) = crowd();
    let probe = module(
        MODULE,
        Action::WarpProbe { ball: PROBE_SPEC(), range: 20_000.0, lifetime_ms: 120_000.0 },
        10_000.0,
    );
    let released = scene.activate(A, probe, t);
    assert!(accepted(&released), "{released:?}");
    assert_eq!(scene.deployables.len(), 1);
    let held = warp(&mut scene, B, t + SECOND);
    assert!(refused_with(&held, "WARP_DISRUPTED_BY_BUBBLE"), "{held:?}");
    let (gone, t2) = poll_for(&mut scene, t + SECOND, 125.0);
    assert!(scene.deployables.is_empty());
    assert!(
        gone.iter().any(|o| format!("{o:?}").contains("RemoveBalls")),
        "the probe is taken out of the ballpark of the pilots who saw it"
    );
    let free = warp(&mut scene, B, t2);
    assert!(accepted(&free), "{free:?}");
}

#[test]
fn a_bomb_goes_off_where_it_is_and_hurts_the_ships_in_its_blast_but_not_its_launcher() {
    let (mut scene, t) = crowd();
    // both ships hold still: the bomb goes off where the launcher released it, and the target is 200 m from there
    let a = scene.pilots[&A].ship.position;
    place(&mut scene, A, a);
    place(&mut scene, B, eve_destiny::Vec3::new(a.x + 200.0, a.y, a.z));
    let bomb = Action::Bomb {
        ball: Spec { charge_type_id: 27_920, group_id: 90, name: "Electron Bomb".to_string(), radius: 40.0 },
        kind: BombKind::Damage,
        lifetime_ms: 12_000.0,
        blast_radius: 400.0,
        damage: [5_800.0, 0.0, 0.0, 0.0],
        ecm: [0.0; 4],
        neutralize: 0.0,
    };
    let released = scene.activate(A, module(MODULE, bomb, 10_000.0), t);
    assert!(accepted(&released), "{released:?}");
    let (out, _) = poll_for(&mut scene, t, 14.0);
    assert!(scene.deployables.is_empty(), "the bomb went off");
    // a hit tells its attacker and its target once each (the attacker's copy and the target's); a launcher hit by its own blast would be told twice
    let told = |who: SessionId| {
        out.iter()
            .filter(|o| matches!(o, Out::Notify { session, name: "OnDamageMessage", .. } if *session == who))
            .count()
    };
    assert_eq!((told(B), told(A)), (1, 1), "the blast hits the ship in its radius, and not its launcher");
    assert!(out.iter().any(|o| format!("{o:?}").contains("TerminalPlayDestructionEffect")));
}

#[test]
fn a_cynosural_field_is_lit_where_the_ship_is_as_its_activation_ends_and_is_taken_out_when_its_lifetime_is_up()
 {
    // the wiki: a field is lit after the generator's activation time; the SDE: a field lives its explosionDelay (600000 ms) and does not explode
    let (mut scene, t) = crowd();
    let field = Action::CynoField {
        ball: Spec {
            charge_type_id: 21_094,
            group_id: 2_020,
            name: "Cynosural Field".to_string(),
            radius: 250.0,
        },
        lifetime_ms: 600_000.0,
    };
    let started = scene.activate(A, module(MODULE, field, 20_000.0), t);
    assert!(accepted(&started), "{started:?}");
    assert!(scene.deployables.is_empty(), "no field while the generator is still activating");
    let (lit, t2) = poll_for(&mut scene, t, 21.0);
    assert_eq!(scene.deployables.len(), 1, "the field is lit at the end of the activation");
    assert!(lit.iter().any(|o| format!("{o:?}").contains("AddBalls2")), "the pilots are told of the field");
    let (ended, _) = poll_for(&mut scene, t2, 601.0);
    assert!(scene.deployables.is_empty(), "the field is gone when its lifetime is up");
    assert!(ended.iter().any(|o| format!("{o:?}").contains("RemoveBalls")));
    assert!(
        !ended.iter().any(|o| format!("{o:?}").contains("TerminalPlayDestructionEffect")),
        "a field does not explode"
    );
}

#[test]
fn a_lit_cynosural_field_is_told_to_the_host_with_its_place_and_its_whole_life() {
    let (mut scene, t) = crowd();
    let field = Action::CynoField {
        ball: Spec {
            charge_type_id: 21_094,
            group_id: 2_020,
            name: "Cynosural Field".to_string(),
            radius: 250.0,
        },
        lifetime_ms: 600_000.0,
    };
    assert!(accepted(&scene.activate(A, module(MODULE, field, 20_000.0), t)));
    let (lit, _) = poll_for(&mut scene, t, 21.0);
    let told: Vec<_> = lit
        .iter()
        .filter_map(|out| match out {
            Out::DeployedLit { session, placement } => Some((*session, placement.clone())),
            _ => None,
        })
        .collect();
    let [(session, placement)] = told.as_slice() else { panic!("one field lit: {lit:?}") };
    assert_eq!(*session, A, "the pilot whose generator lit it");
    assert_eq!(Some(&placement.deployable_id), scene.deployables.keys().next());
    assert_eq!(
        (placement.type_id, placement.group_id, placement.solar_system_id),
        (21_094, 2_020, scene.solar_system_id())
    );
    assert!(
        (placement.lifetime_ms - 600_000.0).abs() < 1.0,
        "the whole life is told: {}",
        placement.lifetime_ms
    );
}

#[test]
fn a_field_the_host_puts_back_after_a_restart_lives_what_was_left_and_is_removed_on_request() {
    let (mut scene, t) = crowd();
    let a = scene.pilots[&A].ship.position;
    let placement = crate::link::DeployedPlacement {
        solar_system_id: scene.solar_system_id(),
        deployable_id: DEPLOYABLE_ID_BASE + 900,
        name: "Cynosural Field".to_string(),
        type_id: 21_094,
        group_id: 2_020,
        position: [a.x, a.y, a.z],
        radius: 250.0,
        owner_id: 4_242,
        corporation_id: 0,
        alliance_id: 0,
        ship_id: scene.pilots[&A].ship.spec.ship_id,
        lifetime_ms: 120_000.0,
    };
    assert!(scene.restore_field(placement.clone(), t).iter().any(|o| format!("{o:?}").contains("AddBalls2")));
    assert_eq!(scene.deployables.len(), 1, "the field is in space again");
    assert!(
        scene.restore_field(placement.clone(), t).is_empty(),
        "a field already in space is not put in twice"
    );
    assert!(
        scene.deployable_sequence > 900,
        "a field made after the restore takes a new id, not the restored one"
    );
    // the restored field lives what was left of its life from the restore, not the time the record was made
    let (ended, _) = poll_for(&mut scene, t, 121.0);
    assert!(scene.deployables.is_empty(), "gone when what was left of its life is up");
    assert!(ended.iter().any(|o| format!("{o:?}").contains("RemoveBalls")));

    // the host takes a field out: told, and once only
    assert!(scene.restore_field(placement.clone(), t + 200 * SECOND).len() == 1);
    let removed = scene.remove_field(placement.deployable_id, t + 201 * SECOND);
    assert!(scene.deployables.is_empty());
    assert!(removed.iter().any(|o| format!("{o:?}").contains("RemoveBalls")));
    assert!(
        scene.remove_field(placement.deployable_id, t + 201 * SECOND).is_empty(),
        "a field already gone is left alone"
    );

    // no life left, nothing to put back
    let spent = crate::link::DeployedPlacement { lifetime_ms: 0.0, ..placement };
    assert!(scene.restore_field(spent, t).is_empty());
    assert!(scene.deployables.is_empty());
}

/// A Mobile Cynosural Inhibitor (protocol 50): an anchored one within 200 km stops a cynosural field from lighting there, but not a covert field, and an
/// unanchored one stops nothing. A field activation is refused with `CYNOSURAL_FIELD_INHIBITED`.
#[test]
fn an_anchored_cynosural_inhibitor_stops_a_field_within_200_km_but_not_a_covert_one_or_an_unanchored_one() {
    let inhibitor = |system: i32, a: eve_destiny::Vec3, active: bool| crate::link::DisruptorPlacement {
        deployable_id: 990_000_000_200,
        solar_system_id: system,
        name: "Mobile Cynosural Inhibitor".to_string(),
        type_id: 33_476,
        group_id: 1_249,
        position: [a.x + 50_000.0, a.y, a.z],
        radius: 100.0,
        range: 200_000.0,
        active,
        owner_id: 4_242,
        corporation_id: 0,
        alliance_id: 0,
        ship_id: 0,
    };
    let field = |charge_type_id: i32| Action::CynoField {
        ball: Spec { charge_type_id, group_id: 2_020, name: "Cynosural Field".to_string(), radius: 250.0 },
        lifetime_ms: 600_000.0,
    };

    // an unanchored inhibitor stops nothing
    let (mut scene, t) = crowd();
    let (a, system) = (scene.pilots[&A].ship.position, scene.solar_system_id());
    scene.place_inhibitor(inhibitor(system, a, false), t);
    let lit = scene.activate(A, module(MODULE, field(21_094), 20_000.0), t);
    assert!(accepted(&lit), "{lit:?}");

    // an anchored one 50 km away refuses the field, and a covert field is still lit
    let (mut scene, t) = crowd();
    let (a, system) = (scene.pilots[&A].ship.position, scene.solar_system_id());
    scene.place_inhibitor(inhibitor(system, a, true), t);
    assert!(scene.inhibited_at(a), "the inhibitor covers the ship");
    assert!(
        !scene.inhibited_at(eve_destiny::Vec3::new(a.x + 300_000.0, a.y, a.z)),
        "nothing 250 km beyond the inhibitor (300 km from the ship) is covered"
    );
    let refused = scene.activate(A, module(MODULE, field(21_094), 20_000.0), t);
    assert!(refused_with(&refused, "CYNOSURAL_FIELD_INHIBITED"), "{refused:?}");

    let (mut scene, t) = crowd();
    let (a, system) = (scene.pilots[&A].ship.position, scene.solar_system_id());
    scene.place_inhibitor(inhibitor(system, a, true), t);
    let covert = scene.activate(A, module(MODULE, field(28_650), 20_000.0), t);
    assert!(accepted(&covert), "{covert:?}");
}

/// A player control tower (protocol 73): the host puts it in space where it lies, unanchored until the host anchors it, given once; it stops no field.
#[test]
fn a_control_tower_is_in_space_unanchored_until_the_host_anchors_it_and_stops_no_field() {
    let tower = |system: i32, a: eve_destiny::Vec3, active: bool| crate::link::DisruptorPlacement {
        deployable_id: 990_000_000_300,
        solar_system_id: system,
        name: "Amarr Control Tower".to_string(),
        type_id: 12_235,
        group_id: 365,
        position: [a.x + 50_000.0, a.y, a.z],
        radius: 3_394.0,
        range: 0.0,
        active,
        owner_id: 4_242,
        corporation_id: 0,
        alliance_id: 0,
        ship_id: 0,
    };
    let field = |charge_type_id: i32| Action::CynoField {
        ball: Spec { charge_type_id, group_id: 2_020, name: "Cynosural Field".to_string(), radius: 250.0 },
        lifetime_ms: 600_000.0,
    };
    let (mut scene, t) = crowd();
    let (a, system) = (scene.pilots[&A].ship.position, scene.solar_system_id());
    scene.place_control_tower(tower(system, a, false), t);
    scene.place_control_tower(tower(system, a, true), t);
    let role = |scene: &Scene| scene.deployables[&990_000_000_300].role.clone();
    assert_eq!(role(&scene), Role::ControlTower { active: false }, "given once: the first placement stands");

    scene.set_disruptor_active(990_000_000_300, true);
    assert_eq!(role(&scene), Role::ControlTower { active: true });
    let lit = scene.activate(A, module(MODULE, field(21_094), 20_000.0), t);
    assert!(accepted(&lit), "a control tower stops no field: {lit:?}");
}

/// Pochven (SDE region 10000070) is a region-wide cynosural jammer: no field lights there, a covert or an industrial one neither (the wiki, "Pochven":
/// "stops anyone from lighting a cyno"), and the same field lights in a system of another region. Each scene is made anew from its system, as a restarted
/// simulation makes it: the region is the static data's and nothing of the refusal is kept.
#[test]
fn a_cynosural_field_is_not_lit_in_pochven_of_any_kind_and_is_lit_elsewhere() {
    let lit = |region_id: i32, charge_type_id: i32| {
        let mut scene = Scene::new(Arc::new(SolarSystem { region_id, ..(*system()).clone() }));
        scene.undock(A, spec(), STATION, NOON).unwrap();
        scene.bind(A, NOON);
        scene.undock(B, other_spec(), STATION, NOON).unwrap();
        scene.bind(B, NOON);
        let a = scene.pilots[&A].ship.position;
        place(&mut scene, B, eve_destiny::Vec3::new(a.x + 5_000.0, a.y, a.z));
        let t = NOON + 40 * SECOND;
        scene.poll(t);
        let field = Action::CynoField {
            ball: Spec {
                charge_type_id,
                group_id: 2_020,
                name: "Cynosural Field".to_string(),
                radius: 250.0,
            },
            lifetime_ms: 600_000.0,
        };
        scene.activate(A, module(MODULE, field, 20_000.0), t)
    };
    for charge_type_id in [21_094, 28_650, 52_696] {
        let refused = lit(10_000_070, charge_type_id);
        assert!(refused_with(&refused, "CYNOSURAL_FIELD_JAMMED"), "{charge_type_id}: {refused:?}");
    }
    assert!(accepted(&lit(10_000_001, 21_094)), "a field lights outside Pochven");
}

#[test]
fn a_mobile_warp_disruptor_holds_warps_only_once_anchored_and_is_taken_out_on_request() {
    let disruptor = |active: bool, position: eve_destiny::Vec3| crate::link::DisruptorPlacement {
        deployable_id: 990_000_000_100,
        solar_system_id: 0,
        name: "Mobile Small Warp Disruptor I".to_string(),
        type_id: 12_198,
        group_id: 361,
        position: [position.x, position.y, position.z],
        radius: 50.0,
        range: 5_000.0,
        active,
        owner_id: 4_242,
        corporation_id: 0,
        alliance_id: 0,
        ship_id: 0,
    };

    // not anchored yet: the ship it is next to warps
    let (mut scene, t) = crowd();
    let b = scene.pilots[&B].ship.position;
    let placement =
        crate::link::DisruptorPlacement { solar_system_id: scene.solar_system_id(), ..disruptor(false, b) };
    let placed = scene.place_disruptor(placement.clone(), t);
    assert!(
        placed.iter().any(|o| format!("{o:?}").contains("AddBalls2")),
        "the pilots see it when it is dropped"
    );
    assert!(
        scene.place_disruptor(placement.clone(), t).is_empty(),
        "a disruptor already in space is not put in twice"
    );
    assert!(accepted(&warp(&mut scene, B, t + SECOND)), "an unanchored disruptor disrupts nothing");

    // anchored: the ship next to it cannot warp
    let (mut scene, t) = crowd();
    let b = scene.pilots[&B].ship.position;
    let placement =
        crate::link::DisruptorPlacement { solar_system_id: scene.solar_system_id(), ..disruptor(true, b) };
    scene.place_disruptor(placement.clone(), t);
    let held = warp(&mut scene, B, t + SECOND);
    assert!(refused_with(&held, "WARP_DISRUPTED_BY_BUBBLE"), "{held:?}");

    // the host unanchors it: the disruption is off; it stays in space until the host takes it out
    scene.set_disruptor_active(placement.deployable_id, false);
    scene.poll(t + 7_200 * SECOND);
    assert!(
        scene.deployables.contains_key(&placement.deployable_id),
        "its end is the host's, not the clock's"
    );
    let removed = scene.remove_field(placement.deployable_id, t + 7_201 * SECOND);
    assert!(
        removed.iter().any(|o| format!("{o:?}").contains("RemoveBalls")),
        "those who see it are told it is gone"
    );
    assert!(!scene.deployables.contains_key(&placement.deployable_id));
}

#[test]
fn a_scanner_probe_is_in_space_until_the_host_takes_it_out_and_a_field_is_not_taken_that_way() {
    let (mut scene, t) = crowd();
    let a = scene.pilots[&A].ship.position;
    let probe = crate::link::ProbePlacement {
        probe_id: 990_000_000_001,
        solar_system_id: scene.solar_system_id(),
        name: "Scanner Probe I".to_string(),
        type_id: 19_415,
        group_id: 479,
        position: [a.x + 10_000.0, a.y, a.z],
        radius: 40.0,
        owner_id: 4_242,
        corporation_id: 0,
        alliance_id: 0,
        ship_id: scene.pilots[&A].ship.spec.ship_id,
    };
    let placed = scene.place_probe(probe.clone(), t);
    assert!(
        placed.iter().any(|o| format!("{o:?}").contains("AddBalls2")),
        "the pilots are told of the probe"
    );
    assert!(scene.place_probe(probe.clone(), t).is_empty(), "a probe already in space is not put in twice");

    // two hours on, it is still there: its end is the host's
    scene.poll(t + 7_200 * SECOND);
    assert!(scene.deployables.contains_key(&probe.probe_id), "a probe stays until the host takes it out");

    let removed = scene.remove_probe(probe.probe_id, t + 7_201 * SECOND);
    assert!(
        removed.iter().any(|o| format!("{o:?}").contains("RemoveBalls")),
        "those who see it are told it is gone"
    );
    assert!(!scene.deployables.contains_key(&probe.probe_id));
    assert!(
        scene.remove_probe(probe.probe_id, t + 7_201 * SECOND).is_empty(),
        "a probe already gone is left alone"
    );

    // a cynosural field is not a probe: the host's probe removal leaves it in space
    let field = crate::link::DeployedPlacement {
        solar_system_id: scene.solar_system_id(),
        deployable_id: DEPLOYABLE_ID_BASE + 901,
        name: "Cynosural Field".to_string(),
        type_id: 21_094,
        group_id: 2_020,
        position: [a.x, a.y, a.z],
        radius: 250.0,
        owner_id: 4_242,
        corporation_id: 0,
        alliance_id: 0,
        ship_id: scene.pilots[&A].ship.spec.ship_id,
        lifetime_ms: 600_000.0,
    };
    scene.restore_field(field.clone(), t);
    assert!(scene.remove_probe(field.deployable_id, t + SECOND).is_empty(), "only a probe is taken this way");
    assert!(scene.deployables.contains_key(&field.deployable_id));
}

#[test]
fn a_stasis_burst_webs_the_ships_in_its_range_for_its_duration_and_spares_its_own_ship() {
    let (mut scene, t) = crowd();
    let base = scene.pilots[&B].ship.spec.max_velocity;
    let own = scene.pilots[&A].ship.spec.max_velocity;
    let burst = Action::StasisBurst { range: 15_000.0, duration_ms: 30_000.0, speed_factor: -20.0 };
    let started = scene.activate(A, module(MODULE, burst, 10_000.0), t);
    assert!(accepted(&started), "{started:?}");
    let webbed = scene.pilots[&B].ship.spec.max_velocity;
    assert!((webbed - base * 0.8).abs() < 1e-6, "webbed to {webbed}, from {base}");
    assert_eq!(scene.pilots[&A].ship.spec.max_velocity, own, "the launcher is not webbed by its own burst");
    // the web lasts its duration from the start of the cycle, and then the ship is free
    let (_, _) = poll_for(&mut scene, t, 40.0);
    assert!((scene.pilots[&B].ship.spec.max_velocity - base).abs() < 1e-6);
}

#[test]
fn a_defender_launcher_shoots_down_the_hostile_missile_in_its_reach() {
    use crate::missile_entity::{MISSILE_ID_BASE, MissileEntity, Shooter};
    use crate::missiles::Missile;
    let (mut scene, t) = crowd();
    // a missile of another owner flies from B at A: five kilometres off, with little hit points
    let incoming = Missile {
        charge_type_id: 27_000,
        group_id: 385,
        name: "Heavy Missile".to_string(),
        radius: 1.0,
        mass: 1.0,
        agility: 1.0,
        hit_points: 5.0,
        max_velocity: 1_000.0,
        flight_time_ms: 10_000.0,
        explosion_radius: 10.0,
        explosion_velocity: 400.0,
        damage_reduction_factor: 0.1,
        damage_reduction_sensitivity: 5.5,
        dot: None,
    };
    let shooter = Shooter {
        session: B,
        ship_id: scene.pilots[&B].ship.spec.ship_id,
        module_id: MODULE + 300,
        weapon_type_id: 1,
        owner_id: 999,
        corporation_id: 0,
        alliance_id: 0,
    };
    let attacker = scene.kinematics_of(B).unwrap();
    let target = scene.kinematics_of(A).unwrap();
    let mut missile = MissileEntity::launch(
        MISSILE_ID_BASE + 1,
        shooter,
        scene.pilots[&A].ship.spec.ship_id,
        incoming,
        [0.0; 4],
        &attacker,
        &target,
        t,
    );
    // the pilot of the launcher sees it, and is told when it is gone
    missile.watchers.insert(A);
    scene.missiles.insert(MISSILE_ID_BASE + 1, missile);
    let defender = Missile {
        charge_type_id: 32_782,
        group_id: 88,
        name: "Defender Missile I".to_string(),
        radius: 1.0,
        mass: 1.0,
        agility: 1.0,
        hit_points: 5.0,
        max_velocity: 10_000.0,
        flight_time_ms: 3_000.0,
        explosion_radius: 10.0,
        explosion_velocity: 400.0,
        damage_reduction_factor: 0.1,
        damage_reduction_sensitivity: 5.5,
        dot: None,
    };
    let launcher = Effect {
        module_id: MODULE + 301,
        ..module(MODULE + 301, Action::Defender { missile: defender, damage: [200.0; 4] }, 10_000.0)
    };
    let started = scene.activate(A, launcher, t + SECOND);
    assert!(accepted(&started), "{started:?}");
    assert!(!scene.missiles.contains_key(&(MISSILE_ID_BASE + 1)), "the incoming missile is shot down");
    assert!(
        started.iter().any(|o| format!("{o:?}").contains("TerminalPlayDestructionEffect")),
        "the interception is played as an explosion"
    );
}

/// An F.O.F. (auto-targeting) missile of the charge's flight: 1 km a second for ten seconds, so its reach is 10 km.
fn fof_missile() -> crate::missiles::Missile {
    crate::missiles::Missile {
        charge_type_id: 27_100,
        group_id: 394,
        name: "Auto-Targeting Light Missile".to_string(),
        radius: 1.0,
        mass: 1.0,
        agility: 1.0,
        hit_points: 5.0,
        max_velocity: 1_000.0,
        flight_time_ms: 10_000.0,
        explosion_radius: 10.0,
        explosion_velocity: 400.0,
        damage_reduction_factor: 0.1,
        damage_reduction_sensitivity: 5.5,
        dot: None,
    }
}

/// A launcher of A that fires at `target_id` (the F.O.F. sentinel, or a locked ship), with `damage` a shot.
fn launch_from_a(
    scene: &mut Scene,
    target_id: i64,
    damage: f64,
    missile: crate::missiles::Missile,
    t: i64,
) -> Vec<Out> {
    let mut effect = module(MODULE + 500, Action::WarpBubble { range: 0.0 }, 5_000.0);
    effect.action = None;
    effect.target_id = target_id;
    effect.turret = Some(crate::weapons::Turret {
        weapon_type_id: 1,
        fx: String::new(),
        raw_damage: [damage, 0.0, 0.0, 0.0],
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
    });
    scene.launch_missile(A, &effect, t)
}

/// Records that `attacker` (a ship) fired on `target` (a ship), as the scene's aggressions do for a launch or a shot.
fn fired_on(scene: &mut Scene, attacker: i64, target: i64) {
    let owner = scene.combat_owner(A);
    scene.aggressions.push(crate::weapons::Aggression {
        attacker_ship: attacker,
        target_ship: target,
        module_type_id: 1,
        source_owner_id: owner,
        target_type_id: 0,
    });
}

/// A third ship, of `corporation` (A's own, or another), undocked and placed `metres` from A along x.
fn third_ship(scene: &mut Scene, corporation: i32, metres: f64, t: i64) -> i64 {
    let mut spec = other_spec();
    spec.ship_id += 4;
    spec.character_id += 1;
    spec.owner_id += 1;
    spec.corporation_id = corporation;
    let ship = spec.ship_id;
    scene.undock(10, spec, STATION, NOON).unwrap();
    scene.bind(10, NOON);
    let a = scene.pilots[&A].ship.position;
    place(scene, 10, eve_destiny::Vec3::new(a.x + metres, a.y, a.z));
    let _ = t;
    ship
}

#[test]
fn an_fof_launch_goes_for_the_nearest_enemy_that_fired_on_the_shooter_and_locks_none() {
    let (mut scene, t) = crowd();
    // B is of another corporation and fired on A (the wiki: the nearest enemy that has previously fired on you); the corporation-mate below is A's
    scene.pilots.get_mut(&B).unwrap().ship.spec.corporation_id = 98_000_001;
    let a_corp = scene.pilots[&A].ship.spec.corporation_id;
    let b_ship = scene.pilots[&B].ship.spec.ship_id;
    let a_ship = scene.pilots[&A].ship.spec.ship_id;
    fired_on(&mut scene, b_ship, a_ship);
    // a ship of A's own corporation is nearer, and fired on A too: it is not an enemy
    let mate = third_ship(&mut scene, a_corp, 3_000.0, t);
    fired_on(&mut scene, mate, a_ship);
    let out = launch_from_a(&mut scene, crate::missile_entity::AUTO_TARGET_ID, 100.0, fof_missile(), t);
    let _ = out;
    let missile = scene.missiles.values().next().expect("a missile was fired");
    assert_eq!(missile.target, b_ship, "the nearest hostile in reach, not the corporation-mate");
    assert_eq!(
        scene.aggressions.last().map(|a| a.target_ship),
        Some(b_ship),
        "the aggression is the target it chose"
    );
}

/// The `OnDamageMessage` notices a list of outputs carries for a session.
fn damage_told(out: &[Out], who: SessionId) -> usize {
    out.iter()
        .filter(|o| matches!(o, Out::Notify { session, name: "OnDamageMessage", .. } if *session == who))
        .count()
}

#[test]
fn a_fighter_blast_goes_off_when_its_window_ends_and_hurts_every_ship_in_its_radius_the_launcher_too() {
    let (mut scene, t) = crowd();
    // the target and the launcher's ship hold still, the launcher 100 m from the blast's centre: both are within its 15 km
    let b = scene.pilots[&B].ship.position;
    place(&mut scene, B, b);
    place(&mut scene, A, eve_destiny::Vec3::new(b.x + 100.0, b.y, b.z));
    let blast = Action::Blast { centre: [b.x, b.y, b.z], radius: 15_000.0, damage: [640.0, 0.0, 0.0, 0.0] };
    let started = scene.activate(A, module(MODULE, blast, 60_000.0), t);
    assert!(accepted(&started), "{started:?}");
    let (before, t2) = poll_for(&mut scene, t, 59.0);
    assert_eq!(
        damage_told(&before, B) + damage_told(&before, A),
        0,
        "nothing goes off before the window ends"
    );
    let (after, _) = poll_for(&mut scene, t2, 2.0);
    assert!(damage_told(&after, B) >= 1, "the ship in the blast is hit once its window ends");
    assert!(
        damage_told(&after, A) >= 1,
        "the launcher's own ship is not spared (EVE.js executeFighterBombCycle)"
    );
    assert!(
        scene.pilots[&A].modules.running.is_empty(),
        "the window is one cycle: the blast ends the ability"
    );
}

#[test]
fn an_fof_launch_with_no_enemy_in_its_reach_fires_nothing() {
    let (mut scene, t) = crowd();
    // B fired on A, but is pushed beyond the missile's ten kilometres of reach
    let (b_ship, a_ship) = (scene.pilots[&B].ship.spec.ship_id, scene.pilots[&A].ship.spec.ship_id);
    fired_on(&mut scene, b_ship, a_ship);
    let a = scene.pilots[&A].ship.position;
    place(&mut scene, B, eve_destiny::Vec3::new(a.x + 30_000.0, a.y, a.z));
    let out = launch_from_a(&mut scene, crate::missile_entity::AUTO_TARGET_ID, 100.0, fof_missile(), t);
    assert!(scene.missiles.is_empty(), "nothing in reach, nothing fired");
    assert!(out.is_empty());
}

#[test]
fn a_launch_that_does_no_damage_makes_no_aggression_and_a_damaging_one_does() {
    let (mut scene, t) = crowd();
    scene.pilots.get_mut(&B).unwrap().ship.spec.corporation_id = 98_000_001;
    let b_ship = scene.pilots[&B].ship.spec.ship_id;
    launch_from_a(&mut scene, b_ship, 0.0, fof_missile(), t);
    assert!(scene.aggressions.is_empty(), "a festival-style snowball is not offensive");
    assert_eq!(scene.missiles.len(), 1, "the snowball still flies");
    launch_from_a(&mut scene, b_ship, 100.0, fof_missile(), t);
    assert_eq!(scene.aggressions.len(), 1, "a damaging missile aggresses its target");
}

#[test]
fn a_fighter_blast_does_not_reach_a_ship_beyond_its_radius() {
    let (mut scene, t) = crowd();
    // the blast is centred on the launcher's ship; the other ship is 40 km off, beyond the 15 km radius
    let a = scene.pilots[&A].ship.position;
    place(&mut scene, A, a);
    place(&mut scene, B, eve_destiny::Vec3::new(a.x + 40_000.0, a.y, a.z));
    let blast = Action::Blast { centre: [a.x, a.y, a.z], radius: 15_000.0, damage: [640.0, 0.0, 0.0, 0.0] };
    assert!(accepted(&scene.activate(A, module(MODULE, blast, 10_000.0), t)));
    let (out, _) = poll_for(&mut scene, t, 12.0);
    assert_eq!(damage_told(&out, B), 0, "a ship 40 km off is outside the blast");
    assert!(damage_told(&out, A) >= 1, "the blast went off at the launcher's ship");
}

#[test]
fn a_bomb_launcher_refuses_a_ship_at_rest_and_releases_its_bomb_once_the_ship_moves() {
    // the launcher starts only above minVelocityActivationLimit 2608 (1 m/s in the SDE): a stopped ship may not release a bomb
    let (mut scene, t) = crowd();
    let a = scene.pilots[&A].ship.position;
    place(&mut scene, A, a);
    let bomb = Action::Bomb {
        ball: Spec { charge_type_id: 27_920, group_id: 90, name: "Electron Bomb".to_string(), radius: 40.0 },
        kind: BombKind::Damage,
        lifetime_ms: 12_000.0,
        blast_radius: 400.0,
        damage: [5_800.0, 0.0, 0.0, 0.0],
        ecm: [0.0; 4],
        neutralize: 0.0,
    };
    let mut launcher = module(MODULE, bomb, 10_000.0);
    launcher.rules = crate::modules::Rules { min_velocity_limit: 1.0, ..Default::default() };
    let at_rest = scene.activate(A, launcher.clone(), t);
    assert!(refused_with(&at_rest, "VELOCITY_TOO_LOW"), "{at_rest:?}");
    assert!(scene.deployables.is_empty(), "a refused launch releases nothing");
    scene.pilots.get_mut(&A).unwrap().ship.velocity = eve_destiny::Vec3::new(20.0, 0.0, 0.0);
    let moving = scene.activate(A, launcher, t);
    assert!(accepted(&moving), "{moving:?}");
}

/// A lance's strike: a cylinder of 2.5 km for 50 km, its beacon `offset` from the firing ship (protocol 37).
fn lance(offset: Option<[f64; 3]>) -> crate::strike::Strike {
    use crate::strike::{Strike, StrikeShape};
    Strike {
        shape: StrikeShape::Cylinder,
        direction: [1.0, 0.0, 0.0],
        length: 50_000.0,
        radius: 2_500.0,
        warning_ms: 15_000.0,
        pulse_ms: 1_000.0,
        pulses: 16,
        damage: [0.0, 0.0, 0.0, 17_000.0],
        signature_radius: 20_000.0,
        immobile_ms: 30_000.0,
        no_jump_ms: 120_000.0,
        neut_amount: 31_000.0,
        neut_radius: 10_000.0,
        neut_signature_radius: 20_000.0,
        debuff_ms: 60_000.0,
        sweep_end: None,
        beacon: offset,
    }
}

#[test]
fn a_lances_beacon_is_put_in_space_at_its_offset_and_taken_out_when_its_effect_ends() {
    let (mut scene, t) = crowd();
    let a = scene.pilots[&A].ship.position;
    place(&mut scene, A, a);
    let started = scene.activate(
        A,
        module(MODULE, Action::Strike { strike: lance(Some([4_000.0, 0.0, 0.0])) }, 20_000.0),
        t,
    );
    assert!(accepted(&started), "{started:?}");
    let beacon = scene.beacon_of(MODULE);
    assert!(beacon > 0, "the lance has its beacon in space");
    let placed = scene.deployables[&beacon].position;
    assert!(
        (placed.x - (a.x + 4_000.0)).abs() < 1e-6
            && (placed.y - a.y).abs() < 1e-6
            && (placed.z - a.z).abs() < 1e-6,
        "the beacon is at the offset from the firing ship: {placed:?} from {a:?}"
    );
    // the beacon is a container of the lance's type, and the beam is played on it (after the beacon is in the ballpark)
    let told = format!("{started:?}");
    assert!(told.contains("AddBalls2"), "the beacon is shown to the pilots who see the place");
    let fx = told.find("OnSpecialFX").expect("the beam is played");
    assert!(told[..fx].contains("AddBalls2"), "the beacon comes before the beam that is drawn to it");
    assert!(told[fx..].contains(&beacon.to_string()), "the beam is drawn to the beacon");
    // the beacon goes when the effect ends, and the beam stops on it
    let (out, _) = poll_for(&mut scene, t, 22.0);
    assert_eq!(scene.beacon_of(MODULE), 0, "the beacon is gone with its effect");
    assert!(format!("{out:?}").contains("RemoveBalls"), "the pilots who saw it are told it is gone");
}

#[test]
fn a_strike_with_no_beacon_puts_nothing_in_space() {
    let (mut scene, t) = crowd();
    let started = scene.activate(A, module(MODULE, Action::Strike { strike: lance(None) }, 20_000.0), t);
    assert!(accepted(&started), "{started:?}");
    assert_eq!(scene.beacon_of(MODULE), 0);
    assert!(scene.deployables.is_empty());
}

#[test]

fn a_cyno_beacon_removal_is_scoped_to_system_and_beacon_id() {
    let (mut scene, t) = crowd();
    let b = scene.pilots[&B].ship.position;
    let system = scene.solar_system_id();
    let beacon = crate::link::DisruptorPlacement {
        deployable_id: 990_000_000_200,
        solar_system_id: system,
        name: "Mobile Cynosural Beacon".to_string(),
        type_id: 57_319,
        group_id: 4_093,
        position: [b.x, b.y, b.z],
        radius: 100.0,
        range: 0.0,
        active: false,
        owner_id: 4_242,
        corporation_id: 0,
        alliance_id: 0,
        ship_id: 0,
    };
    let unrelated = crate::link::DisruptorPlacement {
        deployable_id: 990_000_000_201,
        solar_system_id: system,
        name: "Mobile Cynosural Inhibitor".to_string(),
        type_id: 33_476,
        group_id: 1_249,
        position: [b.x, b.y, b.z],
        radius: 100.0,
        range: 200_000.0,
        active: true,
        owner_id: 4_242,
        corporation_id: 0,
        alliance_id: 0,
        ship_id: 0,
    };
    scene.place_cyno_beacon(beacon.clone(), t);
    scene.place_inhibitor(unrelated.clone(), t);

    assert!(scene.remove_cyno_beacon(system + 1, beacon.deployable_id, t).is_empty());
    assert!(scene.deployables.contains_key(&beacon.deployable_id));
    assert!(scene.remove_cyno_beacon(system, unrelated.deployable_id, t).is_empty());
    assert!(scene.deployables.contains_key(&unrelated.deployable_id));

    let removed = scene.remove_cyno_beacon(system, beacon.deployable_id, t + SECOND);
    assert!(!removed.is_empty(), "observers are told the beacon left space");
    assert!(!scene.deployables.contains_key(&beacon.deployable_id));
    assert!(scene.deployables.contains_key(&unrelated.deployable_id));
}

#[test]
fn a_mobile_cynosural_beacon_is_a_field_only_once_anchored_and_is_taken_out_on_request() {
    let (mut scene, t) = crowd();
    let b = scene.pilots[&B].ship.position;
    let beacon = crate::link::DisruptorPlacement {
        deployable_id: 990_000_000_200,
        solar_system_id: scene.solar_system_id(),
        name: "Mobile Cynosural Beacon".to_string(),
        type_id: 57_319,
        group_id: 4_093,
        position: [b.x, b.y, b.z],
        radius: 100.0,
        range: 0.0,
        active: false,
        owner_id: 4_242,
        corporation_id: 0,
        alliance_id: 0,
        ship_id: 0,
    };
    let placed = scene.place_cyno_beacon(beacon.clone(), t);
    assert!(
        placed.iter().any(|o| format!("{o:?}").contains("AddBalls2")),
        "the pilots see it when it is dropped"
    );
    assert!(
        scene.place_cyno_beacon(beacon.clone(), t).is_empty(),
        "a beacon already in space is not put in twice"
    );
    assert_eq!(scene.deployables[&beacon.deployable_id].role, Role::CynoBeacon { active: false });

    // the host anchors it when its activation time is over: it is a field from then on
    scene.set_disruptor_active(beacon.deployable_id, true);
    assert_eq!(scene.deployables[&beacon.deployable_id].role, Role::CynoBeacon { active: true });

    // its end is the host's: the host takes it out, and it leaves the pilots' space
    let removed = scene.remove_field(beacon.deployable_id, t + SECOND);
    assert!(!scene.deployables.contains_key(&beacon.deployable_id));
    assert!(!removed.is_empty(), "those who see it are told it is gone");
}

/// The id of the CRAB in these tests, and of the ship it is linked to.
const CRAB: i64 = 4_250_000_000_000_050;
const LINKED_SHIP: i64 = 9_988_400_001_944;

/// A CRAB the host places 1 km ahead of `A`'s ship: not yet active, with the link idle.
fn crab_placement(scene: &Scene, state: AnalysisBeaconState) -> crate::link::AnalysisBeaconPlacement {
    let a = scene.pilots[&A].ship.position;
    crate::link::AnalysisBeaconPlacement {
        deployable_id: CRAB,
        solar_system_id: 30_100_032,
        name: "CONCORD Rogue Analysis Beacon".to_string(),
        type_id: 60_244,
        group_id: 4_137,
        position: [a.x + 1_000.0, a.y, a.z],
        radius: 100.0,
        owner_id: 1_790_646_377_000_015,
        corporation_id: 98_000_001,
        alliance_id: 0,
        ship_id: 9_988_400_001_943,
        state,
    }
}

#[test]
fn a_crab_is_put_in_space_once_and_the_state_the_host_sets_is_told_only_when_it_changes() {
    let (mut scene, t) = crowd();
    let idle = AnalysisBeaconState {
        active: false,
        activate_at: 133_900_000_000_000_000,
        deployed_at: 133_899_999_000_000_000,
        activation_seconds: 20,
        link_state: 1,
        link_complete_at: 0,
        linked_ship_id: 0,
    };
    let placement = crab_placement(&scene, idle);

    let shown = format!("{:?}", scene.place_analysis_beacon(placement.clone(), t));
    assert!(shown.contains("AddBalls2"), "the pilots who see the place are given the ball");
    assert!(shown.contains("component_linkWithShip") && shown.contains("component_activate"));
    assert!(
        shown.contains("Int(20)"),
        "the activation wait is in the slim item (activate_comp_durationSeconds)"
    );
    assert!(scene.place_analysis_beacon(placement, t).is_empty(), "a CRAB already in space is given once");

    let linked = AnalysisBeaconState {
        active: true,
        activate_at: 0,
        link_state: 2,
        link_complete_at: 133_900_240_000_000_000,
        linked_ship_id: LINKED_SHIP,
        ..idle
    };
    let told = scene.set_analysis_beacon_state(CRAB, linked, t);
    let text = format!("{told:?}");
    assert!(
        !told.is_empty() && text.contains("OnSlimItemChange"),
        "those who see it are told its slim item again"
    );
    assert!(text.contains("Long(133899999000000000)"), "linkWithShip keeps when it was put in space");
    assert!(text.contains("Long(133900240000000000)"), "linkWithShip keeps when the link completes");
    assert!(text.contains(&format!("Long({LINKED_SHIP})")), "linkWithShip keeps the linked ship");
    assert!(text.contains("Int(2)"), "the link is running (state 2)");
    assert!(
        scene.set_analysis_beacon_state(CRAB, linked, t).is_empty(),
        "a state already set is not told again"
    );
    assert!(
        scene.set_analysis_beacon_state(CRAB + 1, linked, t).is_empty(),
        "a deployable that is not in space is left alone"
    );
}

#[test]
fn a_linked_ship_is_held_until_its_link_is_complete_and_out_of_warp_for_a_minute_after() {
    let (mut scene, t) = crowd();
    let idle = AnalysisBeaconState {
        active: true,
        activate_at: 0,
        deployed_at: t,
        activation_seconds: 20,
        link_state: 1,
        link_complete_at: 0,
        linked_ship_id: 0,
    };
    scene.place_analysis_beacon(crab_placement(&scene, idle), t);
    let ship = scene.pilots[&A].ship.spec.ship_id;
    let link_done = t + 240 * SECOND;
    scene.set_analysis_beacon_state(
        CRAB,
        AnalysisBeaconState { link_state: 2, link_complete_at: link_done, linked_ship_id: ship, ..idle },
        t,
    );

    let held = |out: &[Out], reason: &str| {
        out.iter().any(|o| matches!(o, Out::Refused { reason: r, .. } if *r == reason))
    };
    let goto = Command::GotoDirection(eve_destiny::Vec3::new(1.0, 0.0, 0.0));
    let warp = Command::WarpTo { target_id: STATION, range: 0.0 };
    assert!(held(&scene.command(A, goto, t), "LINK_HOLD"), "the ship is immobile during the link");
    assert!(held(&scene.command(A, warp, t), "LINK_HOLD"), "the ship cannot warp during the link");
    assert!(!held(&scene.command(A, Command::Stop, t), "LINK_HOLD"), "a stop is never held");
    assert!(!held(&scene.command(B, goto, t), "LINK_HOLD"), "another pilot's ship is not held");

    let after_link = link_done + SECOND;
    assert!(
        !held(&scene.command(A, goto, after_link), "LINK_HOLD"),
        "the ship moves again when the link is complete"
    );
    assert!(
        held(&scene.command(A, warp, after_link), "LINK_HOLD"),
        "the warp is held for a minute after the link"
    );
    assert!(
        !held(&scene.command(A, warp, link_done + 61 * SECOND), "LINK_HOLD"),
        "the warp is free after the status clear delay"
    );
}

#[test]
fn a_bomb_takes_a_structure_in_its_blast_in_its_layers_and_one_out_of_it_is_left_alone() {
    let (mut scene, t) = crowd();
    let a = scene.pilots[&A].ship.position;
    place(&mut scene, A, a);
    // a structure whose surface is 100 m from the bomb (inside the 400 m blast) and one 10 km beyond it
    let mut near = crate::scene::tests::structure_placement(&scene, t);
    near.structure_id = 98_100_002;
    near.position = [a.x + 60_000.0 + 100.0, a.y, a.z];
    near.vitals = crate::health::Vitals {
        shield_capacity: 1.0,
        armor_hp: 500.0,
        structure_hp: 300.0,
        ..Default::default()
    };
    near.limits = crate::health::StructureLimits { per_second: [1e9; 3], repair_pause: [0.0; 3] };
    scene.place_structure(near.clone(), t);
    let mut far = near.clone();
    far.structure_id = 98_100_003;
    far.position = [a.x - 60_000.0 - 10_000.0, a.y, a.z];
    scene.place_structure(far, t);
    let bomb = Action::Bomb {
        ball: Spec { charge_type_id: 27_920, group_id: 90, name: "Electron Bomb".to_string(), radius: 40.0 },
        kind: BombKind::Damage,
        lifetime_ms: 12_000.0,
        blast_radius: 400.0,
        damage: [5_800.0, 0.0, 0.0, 0.0],
        ecm: [0.0; 4],
        neutralize: 0.0,
    };
    assert!(accepted(&scene.activate(A, module(MODULE, bomb, 10_000.0), t)));
    let (out, _) = poll_for(&mut scene, t, 14.0);
    let told = |id: i64| {
        out.iter().any(|o| matches!(
            o,
            Out::StructureDamaged { structure_id, depleted: Some(crate::health::Layer::Shield), .. } if *structure_id == id
        ))
    };
    // one hit empties all three layers: each is told, from the shallowest, so the host's timeline sees the shield's depletion too
    let layers: Vec<_> = out
        .iter()
        .filter_map(|o| match o {
            Out::StructureDamaged { structure_id: 98_100_002, depleted: Some(layer), .. } => Some(*layer),
            _ => None,
        })
        .collect();
    assert_eq!(
        layers,
        [crate::health::Layer::Shield, crate::health::Layer::Armor, crate::health::Layer::Structure]
    );
    assert!(!told(98_100_003), "a structure beyond the blast is left alone");
}

/// A deadspace pocket of a dungeon (protocol 74): the host puts it in the scene at its origin with its radius. It is in no client ball and no scan, and it stops
/// no field; given once.
#[test]
fn a_deadspace_pocket_is_in_the_scene_in_no_client_ball_and_stops_no_field() {
    let pocket = |system: i32, a: eve_destiny::Vec3| crate::link::DisruptorPlacement {
        deployable_id: 990_000_000_400,
        solar_system_id: system,
        name: "Deadspace Pocket".to_string(),
        type_id: 0,
        group_id: 0,
        position: [a.x + 20_000.0, a.y, a.z],
        radius: 1_000_000_000.0,
        range: 0.0,
        active: true,
        owner_id: 0,
        corporation_id: 0,
        alliance_id: 0,
        ship_id: 0,
    };
    let field = |charge_type_id: i32| Action::CynoField {
        ball: Spec { charge_type_id, group_id: 2_020, name: "Cynosural Field".to_string(), radius: 250.0 },
        lifetime_ms: 600_000.0,
    };
    let (mut scene, t) = crowd();
    let (a, system) = (scene.pilots[&A].ship.position, scene.solar_system_id());
    let out = scene.place_deadspace_pocket(pocket(system, a), t);
    assert!(out.is_empty(), "a pocket is in no client ball: {out:?}");
    assert_eq!(scene.deployables[&990_000_000_400].role, Role::DeadspacePocket);
    assert!(scene.place_deadspace_pocket(pocket(system, a), t).is_empty());

    let lit = scene.activate(A, module(MODULE, field(21_094), 20_000.0), t);
    assert!(accepted(&lit), "a deadspace pocket stops no field: {lit:?}");
}
