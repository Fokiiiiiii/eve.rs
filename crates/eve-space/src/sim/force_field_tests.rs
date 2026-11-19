use super::*;
use crate::deployable::Role;
use crate::link::ForceFieldPlacement;
use crate::scene::tests::{NOON, SESSION, STATION, spec, system};

const FIELD: i64 = 1_050_000_100;
const OTHER_FIELD: i64 = 1_050_000_101;
const TOWER: i64 = 1_050_000_099;
const OTHER_TOWER: i64 = 1_050_000_098;

fn placement(field_id: i64, tower_id: i64, radius: f64) -> ForceFieldPlacement {
    ForceFieldPlacement {
        field_id,
        tower_id,
        solar_system_id: SYSTEM,
        type_id: 16_000,
        group_id: 411,
        display_name: "Force Field".to_string(),
        position: [1.0, 2.0, 3.0],
        radius,
    }
}

fn fields_of(sim: &Sim, tower_id: i64) -> Vec<(i64, f64)> {
    sim.scenes[&SceneKey::solar_system(SYSTEM)]
        .deployables
        .values()
        .filter(|held| matches!(held.role, Role::ForceField { tower_id: t } if t == tower_id))
        .map(|held| (held.id, held.spec.radius))
        .collect()
}

fn new_sim() -> Sim {
    let mut sim = Sim::new(Arc::new(Universe::with_systems([(*system()).clone()])));
    sim.handle(enter(SESSION, STATION, SYSTEM), NOON).unwrap();
    sim
}

/// The field of an online tower is put in its system's scene, and taken out with the tower; each answer says what happened.
#[test]
fn a_force_field_is_placed_for_its_tower_and_taken_out_with_it() {
    let mut sim = new_sim();
    let placed =
        sim.handle(Message::PlaceForceField(Box::new(placement(FIELD, TOWER, 1_000.0))), NOON).unwrap();
    assert_eq!(placed.first(), Some(&Message::ForceFieldPlaced { field_id: FIELD, reason: String::new() }));
    assert_eq!(fields_of(&sim, TOWER), [(FIELD, 1_000.0)]);

    let removed =
        sim.handle(Message::RemoveForceField { tower_id: TOWER, solar_system_id: SYSTEM }, NOON).unwrap();
    assert_eq!(removed.first(), Some(&Message::ForceFieldRemoved { tower_id: TOWER, reason: String::new() }));
    assert!(fields_of(&sim, TOWER).is_empty());
}

/// The same field, place and radius again changes nothing; a new id replaces the tower's field.
#[test]
fn a_field_placed_again_changes_nothing_and_a_new_id_replaces_it() {
    let mut sim = new_sim();
    sim.handle(Message::PlaceForceField(Box::new(placement(FIELD, TOWER, 1_000.0))), NOON).unwrap();
    let again =
        sim.handle(Message::PlaceForceField(Box::new(placement(FIELD, TOWER, 1_000.0))), NOON).unwrap();
    assert_eq!(again, vec![Message::ForceFieldPlaced { field_id: FIELD, reason: String::new() }]);

    let replaced =
        sim.handle(Message::PlaceForceField(Box::new(placement(OTHER_FIELD, TOWER, 1_000.0))), NOON).unwrap();
    assert_eq!(
        replaced.first(),
        Some(&Message::ForceFieldPlaced { field_id: OTHER_FIELD, reason: String::new() })
    );
    assert_eq!(fields_of(&sim, TOWER), [(OTHER_FIELD, 1_000.0)]);
}

/// A field needs a positive radius; a refused placement leaves no field and frees its id.
#[test]
fn a_field_without_a_positive_radius_is_refused_and_takes_no_place() {
    let mut sim = new_sim();
    for radius in [0.0, -5.0, f64::NAN] {
        let answer =
            sim.handle(Message::PlaceForceField(Box::new(placement(FIELD, TOWER, radius))), NOON).unwrap();
        assert_eq!(
            answer,
            vec![Message::ForceFieldPlaced {
                field_id: FIELD,
                reason: "FORCE_FIELD_RADIUS_INVALID".to_string()
            }]
        );
    }
    assert!(fields_of(&sim, TOWER).is_empty());
}

/// An id held by another tower's field is refused, and that tower keeps its field.
#[test]
fn an_id_held_by_another_towers_field_is_refused() {
    let mut sim = new_sim();
    sim.handle(Message::PlaceForceField(Box::new(placement(FIELD, TOWER, 1_000.0))), NOON).unwrap();
    let answer =
        sim.handle(Message::PlaceForceField(Box::new(placement(FIELD, OTHER_TOWER, 500.0))), NOON).unwrap();
    assert_eq!(
        answer,
        vec![Message::ForceFieldPlaced { field_id: FIELD, reason: "FORCE_FIELD_ID_TAKEN".to_string() }]
    );
    assert_eq!(fields_of(&sim, TOWER), [(FIELD, 1_000.0)]);
    assert!(fields_of(&sim, OTHER_TOWER).is_empty());
}

/// The access of a tower and the password of a pilot are kept in the scene, and neither is answered.
#[test]
fn force_field_access_and_a_password_are_kept_in_the_scene_without_an_answer() {
    let mut sim = new_sim();
    let access = Message::SetForceFieldAccess {
        tower_id: TOWER,
        solar_system_id: SYSTEM,
        corporation_id: 98_000_001,
        alliance_id: 0,
        allow_corporation: true,
        allow_alliance: false,
    };
    assert!(sim.handle(access, NOON).unwrap().is_empty());
    let password = Message::ShipForceFieldPassword {
        session_id: SESSION,
        tower_id: TOWER,
        solar_system_id: SYSTEM,
        matched: true,
    };
    assert!(sim.handle(password, NOON).unwrap().is_empty());
    let scene = &sim.scenes[&SceneKey::solar_system(SYSTEM)];
    assert_eq!(scene.force_field_access.get(&TOWER).map(|access| access.corporation_id), Some(98_000_001));
    assert!(scene.force_field_passwords.contains(&(SESSION, TOWER)));
}

/// A password set through the simulation for a pilot in space takes effect there, and the answer tells the host the pilot's harmonic (protocol 105):
/// the tower while the ship is inside the admitted field, and -1 once the password no longer matches.
#[test]
fn a_password_through_the_simulation_tells_the_host_the_pilot_harmonic() {
    let mut sim = new_sim();
    sim.handle(Message::PlaceForceField(Box::new(placement(FIELD, TOWER, 1_000.0))), NOON).unwrap();
    let pilot = SESSION + 1;
    sim.scenes
        .get_mut(&SceneKey::solar_system(SYSTEM))
        .unwrap()
        .enter_in_space(pilot, spec(), [-50.0, 0.0, 0.0], [1.0, 0.0, 0.0], NOON)
        .unwrap();
    let ship_id = spec().ship_id;
    let password = |matched| Message::ShipForceFieldPassword {
        session_id: pilot,
        tower_id: TOWER,
        solar_system_id: SYSTEM,
        matched,
    };

    let admitted = sim.handle(password(true), NOON).unwrap();
    assert!(
        admitted.contains(&Message::ShipForceFieldHarmonic { session_id: pilot, ship_id, harmonic: TOWER }),
        "the matching password gives the ship the tower's harmonic: {admitted:?}"
    );
    let refused = sim.handle(password(false), NOON).unwrap();
    assert!(
        refused.contains(&Message::ShipForceFieldHarmonic { session_id: pilot, ship_id, harmonic: -1 }),
        "the password that no longer matches takes the harmonic away: {refused:?}"
    );
}

/// A field that goes up around a pilot already admitted inside it gives the pilot the tower's harmonic at once, and a field that goes down takes it away;
/// the host is told each change, with no move of the ship.
#[test]
fn a_field_going_up_or_down_around_an_admitted_pilot_changes_its_harmonic_at_once() {
    let mut sim = new_sim();
    let pilot = SESSION + 1;
    let ship_id = spec().ship_id;
    sim.scenes
        .get_mut(&SceneKey::solar_system(SYSTEM))
        .unwrap()
        .enter_in_space(pilot, spec(), [-50.0, 0.0, 0.0], [1.0, 0.0, 0.0], NOON)
        .unwrap();
    // admitted by its password before any field is in space: no harmonic yet
    let password = Message::ShipForceFieldPassword {
        session_id: pilot,
        tower_id: TOWER,
        solar_system_id: SYSTEM,
        matched: true,
    };
    assert!(
        !sim.handle(password, NOON)
            .unwrap()
            .iter()
            .any(|m| matches!(m, Message::ShipForceFieldHarmonic { .. }))
    );

    let placed =
        sim.handle(Message::PlaceForceField(Box::new(placement(FIELD, TOWER, 1_000.0))), NOON).unwrap();
    assert!(
        placed.contains(&Message::ShipForceFieldHarmonic { session_id: pilot, ship_id, harmonic: TOWER }),
        "the field going up gives the admitted pilot its harmonic: {placed:?}"
    );
    let removed =
        sim.handle(Message::RemoveForceField { tower_id: TOWER, solar_system_id: SYSTEM }, NOON).unwrap();
    assert!(
        removed.contains(&Message::ShipForceFieldHarmonic { session_id: pilot, ship_id, harmonic: -1 }),
        "the field going down takes the harmonic away: {removed:?}"
    );
}

/// A tower with no field in the scene cannot have one removed, and a system the universe does not know refuses a placement.
#[test]
fn removing_a_missing_field_and_placing_in_an_unknown_system_are_refused() {
    let mut sim = new_sim();
    let removed =
        sim.handle(Message::RemoveForceField { tower_id: TOWER, solar_system_id: SYSTEM }, NOON).unwrap();
    assert_eq!(
        removed,
        vec![Message::ForceFieldRemoved { tower_id: TOWER, reason: "FORCE_FIELD_NOT_IN_SPACE".to_string() }]
    );

    let mut unknown = placement(FIELD, TOWER, 1_000.0);
    unknown.solar_system_id = 999_999;
    let answer = sim.handle(Message::PlaceForceField(Box::new(unknown)), NOON).unwrap();
    assert_eq!(
        answer,
        vec![Message::ForceFieldPlaced { field_id: FIELD, reason: "SYSTEM_UNKNOWN".to_string() }]
    );
}
