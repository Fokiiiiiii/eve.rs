use super::{Message, MoonChunkPlacement, StructureModule, StructurePlacement};
use crate::health::{StructureLimits, Vitals};
use crate::world::StructureStatus;

fn messages() -> [Message; 6] {
    let structure_id = 1_040_000_000_001;
    let drill_id = 1_040_000_000_002;
    let chunk_id = 1_040_000_000_003;
    let moon_id = 40_000_004;
    let module = StructureModule { item_id: drill_id, type_id: 45_009, flag_id: 164 };
    [
        Message::PlaceStructure(Box::new(StructurePlacement {
            structure_id,
            solar_system_id: 30_000_001,
            name: "Athanor".to_string(),
            type_id: 35_835,
            group_id: 1_406,
            position: [1.0, 2.0, 3.0],
            radius: 30_000.0,
            owner_id: 98_000_001,
            corporation_id: 98_000_001,
            alliance_id: 0,
            vitals: Vitals::default(),
            shield_recharge_ms: 110_000.0,
            limits: StructureLimits { per_second: [5_000.0; 3], repair_pause: [500.0; 3] },
            status: StructureStatus { state: 110, upkeep_state: 3, ..Default::default() },
            planet_id: 0,
            modules: vec![module.clone()],
            closest_moon_id: moon_id,
        })),
        Message::SetStructureMoonMiningState {
            solar_system_id: 30_000_001,
            structure_id,
            closest_moon_id: moon_id,
            modules: vec![module],
        },
        Message::UpsertMoonChunk(Box::new(MoonChunkPlacement {
            chunk_id,
            solar_system_id: 30_000_001,
            name: "Moon chunk".to_string(),
            type_id: 46_030,
            group_id: 1_940,
            category_id: 25,
            owner_id: 98_000_001,
            graphic_id: 12_345,
            position: [100.0, 200.0, 300.0],
            direction: [0.0, 0.0, 1.0],
            radius: 10_000.0,
            source_structure_id: structure_id,
            module_id: drill_id,
            module_type_id: 45_009,
            moon_id,
            phase: "tractor_phase".to_string(),
            phase_started_filetime: 134_351_688_000_000_000,
        })),
        Message::RemoveMoonChunk { solar_system_id: 30_000_001, chunk_id, effect: 3 },
        Message::RemoveMoonChunk { solar_system_id: 30_000_001, chunk_id, effect: 0 },
        Message::SetStructureMoonMiningState {
            solar_system_id: 30_000_001,
            structure_id,
            closest_moon_id: 0,
            modules: vec![],
        },
    ]
}

#[test]
fn moon_mining_link_messages_round_trip_the_drill_slim_and_chunk_lifecycle() {
    for message in messages() {
        let frame = message.encode();
        let payload_len = u32::from_le_bytes(frame[..4].try_into().unwrap()) as usize;
        assert_eq!(frame.len() - 4, payload_len);
        let decoded = Message::decode(&frame[4..]).unwrap();
        assert_eq!(message, decoded);
        assert_eq!(frame, decoded.encode());
    }
}

#[test]
fn moon_mining_link_messages_reject_bad_phase_and_removal_effects() {
    let mut invalid_phase = messages()[2].clone();
    let Message::UpsertMoonChunk(chunk) = &mut invalid_phase else { panic!("upsert fixture changed") };
    chunk.phase = "unknown_phase".to_string();
    let frame = invalid_phase.encode();
    assert!(matches!(Message::decode(&frame[4..]), Err(super::DecodeError::BadValue("moon chunk"))));

    let invalid_removal =
        Message::RemoveMoonChunk { solar_system_id: 30_000_001, chunk_id: 1_040_000_000_003, effect: 1 };
    let frame = invalid_removal.encode();
    assert!(matches!(Message::decode(&frame[4..]), Err(super::DecodeError::BadValue("moon chunk removal"))));
}
