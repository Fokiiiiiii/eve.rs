use super::*;

use crate::scene::tests::{NOON, STATION, spec, structure_placement, system};
use crate::scene::{Command, Out, Scene};
use crate::ship::{Course, ModuleSpec, ShipSpec};
use crate::strike::{Strike, StrikeShape};
use eve_destiny::Vec3;
use std::collections::HashMap;
use std::sync::Arc;

const A: SessionId = 7;
const B: SessionId = 8;
const C: SessionId = 9;
const SECOND: i64 = 10_000_000;
const MODULE: i64 = 9_988_400_001_940;
const PUBLIC_HOG_TYPE: i32 = 40_634;
const STRUCTURE_HOG_TYPE: i32 = 47_068;
const AVATAR_TYPE: i32 = 11_567;
const RIFTER_TYPE: i32 = 587;

fn ship_spec(ship_offset: i64, pilot_offset: i64, type_id: i32) -> ShipSpec {
    let mut ship = spec();
    ship.ship_id += ship_offset;
    ship.character_id += pilot_offset;
    ship.owner_id += pilot_offset;
    ship.type_id = type_id;
    ship
}

fn scene_with_three_ships() -> (Scene, i64, Vec3, i64, i64) {
    let mut scene = Scene::new(Arc::new((*system()).clone()));
    let a = spec();
    let b = ship_spec(4, 1, RIFTER_TYPE);
    let c = ship_spec(8, 2, AVATAR_TYPE);
    let (b_id, c_id) = (b.ship_id, c.ship_id);
    scene.undock(A, a, STATION, NOON).unwrap();
    scene.bind(A, NOON);
    scene.undock(B, b, STATION, NOON).unwrap();
    scene.bind(B, NOON);
    scene.undock(C, c, STATION, NOON).unwrap();
    scene.bind(C, NOON);

    let now = NOON + 80 * SECOND;
    scene.poll(now);
    let station = scene.system().body(STATION).unwrap().position;
    let origin = Vec3::new(station.x + 50_000_000.0, station.y, station.z);
    for (session, position) in [
        (A, origin),
        (B, Vec3::new(origin.x + 2_000.0, origin.y, origin.z)),
        (C, Vec3::new(origin.x + 50_000.0, origin.y, origin.z)),
    ] {
        let ship = &mut scene.pilots.get_mut(&session).unwrap().ship;
        ship.position = position;
        ship.velocity = Vec3::ZERO;
        ship.speed_fraction = 0.0;
        ship.course = Course::Stop;
    }
    (scene, now, origin, b_id, c_id)
}

fn field_effect(module_type: i32, pulses: i32, moved: Vec<i64>, held: Vec<i64>) -> Effect {
    let strike = Strike {
        shape: StrikeShape::Sphere,
        direction: [1.0, 0.0, 0.0],
        length: 2_000.0,
        radius: 100_000.0,
        warning_ms: 2_000.0,
        pulse_ms: 1_000.0,
        pulses,
        damage: [0.0; 4],
        signature_radius: 1.0,
        immobile_ms: 0.0,
        no_jump_ms: 0.0,
        neut_amount: 0.0,
        neut_radius: 0.0,
        neut_signature_radius: 1.0,
        debuff_ms: 0.0,
        sweep_end: None,
        beacon: None,
    };
    let mut effect = crate::modules::tests::afterburner();
    effect.module_id = MODULE;
    effect.module_type = module_type;
    effect.effect_id = 6_474;
    effect.duration_ms = 10_000.0;
    effect.capacitor_need = 0.0;
    effect.repeat = 1;
    effect.target_id = 0;
    effect.action = Some(Action::Field {
        strike,
        destination: [0.0, 0.0, 5_000.0],
        warp_block_ms: 60_000.0,
        moved,
        held,
    });
    effect.on = None;
    effect.off = None;
    effect.on_changes.clear();
    effect.off_changes.clear();
    effect
}

fn controlled_structure_scene() -> (Scene, i64, i64, Vec3, i64, i64, i64) {
    let (mut scene, now, origin, b_id, c_id) = scene_with_three_ships();
    let mut placement = structure_placement(&scene, now);
    let structure_id = placement.structure_id;
    placement.position = [origin.x, origin.y, origin.z];
    scene.place_structure(placement, now);

    let second_structure_id = structure_id + 1;
    let mut second = structure_placement(&scene, now);
    second.structure_id = second_structure_id;
    second.position = [origin.x + 4_000.0, origin.y, origin.z];
    scene.place_structure(second, now);

    let controlled =
        scene.control_structure(A, scene.solar_system_id(), structure_id, spec().character_id, true, 39, now);
    assert!(controlled.iter().any(|out| matches!(out, Out::Accepted { session, .. } if *session == A)));

    let mut structure_spec = spec();
    structure_spec.ship_id = structure_id;
    structure_spec.capacitor_capacity = 100.0;
    structure_spec.capacitor_recharge_s = 100.0;
    structure_spec.modules = vec![ModuleSpec { item_id: MODULE, type_id: STRUCTURE_HOG_TYPE, flag: 27 }];
    scene.respec(A, structure_spec, now);
    (scene, structure_id, second_structure_id, origin, b_id, c_id, now)
}

fn structure_candidate_request(out: &[Out]) -> (u64, i64, u64, u32, Vec<i64>) {
    out.iter()
        .find_map(|out| match out {
            Out::StructureDamageCandidatesRequest {
                activation_nonce,
                control_lease_generation,
                cycle_id,
                pulse_hop_ordinal,
                candidate_ids,
                ..
            } => Some((
                *activation_nonce,
                *control_lease_generation,
                *cycle_id,
                *pulse_hop_ordinal,
                candidate_ids.clone(),
            )),
            _ => None,
        })
        .expect("the controlled Field pulse asks the Host for fresh candidates")
}

#[test]
fn published_ship_hog_recomputes_moved_and_held_ships_each_pulse() {
    let (mut scene, now, origin, b_id, c_id) = scene_with_three_ships();
    scene
        .set_field_capital_size_by_type(Arc::new(HashMap::from([(RIFTER_TYPE, false), (AVATAR_TYPE, true)])));
    let effect = field_effect(PUBLIC_HOG_TYPE, 2, Vec::new(), Vec::new());

    let activation = scene.activate(A, effect, now);
    assert!(activation.iter().any(|out| matches!(out, Out::Accepted { session, .. } if *session == A)));
    assert!(scene.poll(now + 2 * SECOND).iter().all(|out| !matches!(out, Out::Refused { .. })));

    let carried = scene.pilots[&B].ship.position;
    assert_eq!(carried, Vec3::new(origin.x, origin.y, origin.z + 5_000.0));

    let capital_before = Vec3::new(origin.x + 2_000.0, origin.y, origin.z);
    scene.pilots.get_mut(&C).unwrap().ship.position = capital_before;
    scene.poll(now + 3 * SECOND);

    assert_eq!(scene.pilots[&C].ship.position, capital_before, "a new capital entrant is held, not carried");
    let warp = scene.command(
        C,
        Command::WarpToPoint {
            destination: Vec3::new(origin.x + 1_000_000.0, origin.y, origin.z),
            range: 0.0,
        },
        now + 3 * SECOND + 1,
    );
    assert!(warp.iter().any(|out| matches!(out, Out::Refused { reason: "WARP_BLOCKED", .. })));
    assert_ne!(b_id, c_id);
}

#[test]
fn controlled_field_uses_a_fresh_candidate_exchange_and_only_the_host_partition() {
    // The Host refuses unpublished 47068; this direct Scene test covers the already-authorized Rust Field execution path.
    let (mut scene, structure_id, second_structure_id, origin, b_id, c_id, now) =
        controlled_structure_scene();
    let effect = field_effect(STRUCTURE_HOG_TYPE, 2, vec![second_structure_id], vec![c_id]);
    let activation = scene.activate(A, effect, now);
    assert!(activation.iter().any(|out| matches!(out, Out::Accepted { session, .. } if *session == A)));
    let first = scene.poll(now + 2 * SECOND);
    let (activation_nonce, lease, cycle_id, ordinal, candidates) = structure_candidate_request(&first);
    assert_eq!(lease, 39);
    assert_eq!(ordinal, 0);
    assert!(
        candidates.contains(&b_id),
        "candidates={candidates:?}, B={b_id}, origin={origin:?}, structure={:?}, B-position={:?}",
        scene.structure_body(structure_id).map(|body| body.position),
        scene.pilots[&B].ship.position
    );
    assert!(candidates.contains(&second_structure_id), "the candidate scan is not ship-only");
    assert!(!first.iter().any(|out| matches!(out, Out::ChargeSpent { .. })));

    let moved = scene.structure_field_damage_candidates_result(
        A,
        structure_id,
        MODULE,
        6_474,
        activation_nonce,
        lease,
        cycle_id,
        ordinal,
        &[b_id],
        &[b_id],
        &[],
        now + 2 * SECOND + 1,
    );
    assert!(!moved.iter().any(|out| matches!(out, Out::Refused { .. })));
    let expected = Vec3::new(origin.x, origin.y, origin.z + 5_000.0);
    assert_eq!(scene.pilots[&B].ship.position, expected, "the Host-authorized moved partition is applied");

    let capital_before = Vec3::new(origin.x + 2_000.0, origin.y, origin.z);
    scene.pilots.get_mut(&C).unwrap().ship.position = capital_before;
    let second = scene.poll(now + 3 * SECOND);
    let (nonce2, lease2, cycle2, ordinal2, candidates2) = structure_candidate_request(&second);
    assert_eq!((nonce2, lease2, cycle2), (activation_nonce, lease, cycle_id));
    assert_eq!(ordinal2, 1);
    let capital_at_pulse = scene.pilots[&C].ship.position;
    assert!(candidates2.contains(&c_id), "a ship that entered after pulse 0 is a fresh candidate");
    assert!(candidates2.contains(&second_structure_id));

    let held = scene.structure_field_damage_candidates_result(
        A,
        structure_id,
        MODULE,
        6_474,
        nonce2,
        lease2,
        cycle2,
        ordinal2,
        &[c_id],
        &[],
        &[c_id],
        now + 3 * SECOND + 1,
    );
    assert!(!held.iter().any(|out| matches!(out, Out::Refused { .. })));
    assert_eq!(scene.pilots[&C].ship.position, capital_at_pulse, "held targets stay where they are");
    assert!(scene.pilots[&C].warp_blocked_until > now + 3 * SECOND);
}
