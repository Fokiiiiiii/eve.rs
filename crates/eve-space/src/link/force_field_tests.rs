use super::{ForceFieldPlacement, Message, kind};

fn placement() -> ForceFieldPlacement {
    ForceFieldPlacement {
        field_id: 1_050_000_100,
        tower_id: 1_050_000_099,
        solar_system_id: 30_000_001,
        type_id: 16_000,
        group_id: 411,
        name: "Force Field".to_string(),
        position: [1.0, 2.0, 3.0],
        radius: 1_000.0,
    }
}

#[test]
fn force_field_frames_roundtrip_byte_for_byte() {
    let messages = [
        Message::PlaceForceField(Box::new(placement())),
        Message::ForceFieldPlaced { field_id: 1_050_000_100, reason: String::new() },
        Message::RemoveForceField { tower_id: 1_050_000_099, solar_system_id: 30_000_001 },
        Message::ForceFieldRemoved {
            tower_id: 1_050_000_099,
            reason: "FORCE_FIELD_NOT_IN_SPACE".to_string(),
        },
    ];
    for message in messages {
        let frame = message.encode();
        let decoded = Message::decode(&frame[4..]).unwrap();
        assert_eq!(decoded, message);
        assert_eq!(decoded.encode(), frame);
    }
}

#[test]
fn force_field_kinds_are_the_protocol_103_codes_in_the_180_range() {
    assert_eq!(
        [
            kind::PLACE_FORCE_FIELD,
            kind::FORCE_FIELD_PLACED,
            kind::REMOVE_FORCE_FIELD,
            kind::FORCE_FIELD_REMOVED
        ],
        [180, 181, 182, 183]
    );
    assert_eq!(Message::PlaceForceField(Box::new(placement())).encode()[4], kind::PLACE_FORCE_FIELD);
    assert_eq!(
        Message::ForceFieldPlaced { field_id: 1, reason: String::new() }.encode()[4],
        kind::FORCE_FIELD_PLACED
    );
    assert_eq!(
        Message::RemoveForceField { tower_id: 1, solar_system_id: 2 }.encode()[4],
        kind::REMOVE_FORCE_FIELD
    );
    assert_eq!(
        Message::ForceFieldRemoved { tower_id: 1, reason: String::new() }.encode()[4],
        kind::FORCE_FIELD_REMOVED
    );
}

#[test]
fn force_field_frames_refuse_a_non_finite_radius_or_position() {
    let radius = ForceFieldPlacement { radius: f64::NAN, ..placement() };
    assert!(Message::decode(&Message::PlaceForceField(Box::new(radius)).encode()[4..]).is_err());
    let position = ForceFieldPlacement { position: [f64::INFINITY, 2.0, 3.0], ..placement() };
    assert!(Message::decode(&Message::PlaceForceField(Box::new(position)).encode()[4..]).is_err());
}

#[test]
fn a_truncated_force_field_frame_is_refused() {
    let frame = Message::RemoveForceField { tower_id: 1_050_000_099, solar_system_id: 30_000_001 }.encode();
    assert!(Message::decode(&frame[4..frame.len() - 1]).is_err());
}
