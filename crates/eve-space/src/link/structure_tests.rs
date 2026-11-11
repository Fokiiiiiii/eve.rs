use super::{DecodeError, Message, StructurePlacement};
use crate::health::{StructureLimits, Vitals};
use crate::world::StructureStatus;

fn ess(owner_id: i64) -> Message {
    Message::PlaceStructure(Box::new(StructurePlacement {
        structure_id: 8_440_000_030_000_903,
        solar_system_id: 30_000_903,
        name: "Encounter Surveillance System".to_string(),
        type_id: 55_914,
        group_id: 4_079,
        position: [1.0, 2.0, 3.0],
        radius: 1_000.0,
        owner_id,
        corporation_id: 0,
        alliance_id: 0,
        vitals: Vitals::default(),
        shield_recharge_ms: 0.0,
        limits: StructureLimits { per_second: [0.0; 3], repair_pause: [0.0; 3] },
        status: StructureStatus { state: 110, upkeep_state: 3, ..Default::default() },
        planet_id: 0,
        modules: Vec::new(),
        closest_moon_id: 0,
    }))
}

#[test]
fn an_ess_without_an_assigned_owner_round_trips_as_a_structure() {
    let message = ess(0);
    let frame = message.encode();

    assert_eq!(Message::decode(&frame[4..]).unwrap(), message);
}

#[test]
fn a_structure_cannot_have_a_negative_owner_id() {
    let frame = ess(-1).encode();

    assert_eq!(Message::decode(&frame[4..]), Err(DecodeError::BadValue("structure")));
}

#[test]
fn a_warp_dropout_message_round_trips() {
    let message = Message::WarpDropped { session_id: 42 };
    let frame = message.encode();

    assert_eq!(Message::decode(&frame[4..]).unwrap(), message);
}
