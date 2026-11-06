use super::*;

fn result(allowed_target_ids: Vec<i64>) -> Message {
    Message::StructureDamageCandidatesResult {
        session_id: 7,
        structure_id: 1_040_000_000_000_001,
        module_id: 9_988_400_001_940,
        effect_id: 6_474,
        activation_nonce: 31,
        control_lease_generation: 20,
        cycle_id: 4_300,
        pulse_hop_ordinal: 0,
        allowed_target_ids,
    }
}

#[test]
fn a_legacy_kind_86_result_keeps_its_original_layout() {
    let frame = result(vec![101, 102]).encode();

    assert_eq!(frame.len(), 81);
    assert_eq!(frame[4], kind::STRUCTURE_DAMAGE_CANDIDATES_RESULT);
    assert!(matches!(
        Message::decode(&frame[4..]),
        Ok(Message::StructureDamageCandidatesResult { allowed_target_ids, .. })
            if allowed_target_ids == [101, 102]
    ));
}

#[test]
fn a_field_partition_round_trips_in_the_optional_kind_86_tail() {
    let message = Message::StructureDamageCandidatesFieldResult {
        session_id: 7,
        structure_id: 1_040_000_000_000_001,
        module_id: 9_988_400_001_940,
        effect_id: 6_474,
        activation_nonce: 31,
        control_lease_generation: 20,
        cycle_id: 4_300,
        pulse_hop_ordinal: 0,
        allowed_target_ids: vec![101, 102],
        field_moved_target_ids: vec![101],
        field_held_target_ids: vec![102],
    };
    let frame = message.encode();
    let decoded = Message::decode(&frame[4..]).expect("the partition is complete");

    assert_eq!(frame[4], kind::STRUCTURE_DAMAGE_CANDIDATES_RESULT);
    assert!(matches!(
        decoded,
        Message::StructureDamageCandidatesFieldResult {
            allowed_target_ids,
            field_moved_target_ids,
            field_held_target_ids,
            ..
        } if allowed_target_ids == [101, 102]
            && field_moved_target_ids == [101]
            && field_held_target_ids == [102]
    ));
}

#[test]
fn a_field_partition_must_be_disjoint_and_cover_the_allowed_ids() {
    let frame = result(vec![101, 102]).encode();
    let mut payload = frame[4..].to_vec();
    payload.push(STRUCTURE_FIELD_PARTITION_EXTENSION_VERSION);
    payload.extend_from_slice(&1_u32.to_le_bytes());
    payload.extend_from_slice(&101_i64.to_le_bytes());
    payload.extend_from_slice(&0_u32.to_le_bytes());

    assert!(matches!(
        Message::decode(&payload),
        Err(DecodeError::BadValue("structure-field target partition"))
    ));
}

#[test]
fn a_field_partition_cannot_assign_one_target_twice() {
    let frame = result(vec![101, 102]).encode();
    let mut payload = frame[4..].to_vec();
    payload.push(STRUCTURE_FIELD_PARTITION_EXTENSION_VERSION);
    payload.extend_from_slice(&1_u32.to_le_bytes());
    payload.extend_from_slice(&101_i64.to_le_bytes());
    payload.extend_from_slice(&1_u32.to_le_bytes());
    payload.extend_from_slice(&101_i64.to_le_bytes());

    assert!(matches!(
        Message::decode(&payload),
        Err(DecodeError::BadValue("structure-field target partition"))
    ));
}
