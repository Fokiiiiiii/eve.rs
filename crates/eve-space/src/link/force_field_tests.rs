use super::{ForceFieldPlacement, Message, kind};

fn placement() -> ForceFieldPlacement {
    ForceFieldPlacement {
        field_id: 1_050_000_100,
        tower_id: 1_050_000_099,
        solar_system_id: 30_000_001,
        type_id: 16_000,
        group_id: 411,
        display_name: "Force Field".to_string(),
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
fn force_field_kinds_are_the_protocol_104_codes_in_the_180_range() {
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

fn access() -> Message {
    Message::SetForceFieldAccess {
        tower_id: 1_050_000_099,
        solar_system_id: 30_000_001,
        corporation_id: 98_000_001,
        alliance_id: 97_000_001,
        allow_corporation: true,
        allow_alliance: false,
    }
}

fn password() -> Message {
    Message::ShipForceFieldPassword {
        session_id: 1_790_646_377_000_015,
        tower_id: 1_050_000_099,
        solar_system_id: 30_000_001,
        matched: true,
    }
}

#[test]
fn force_field_access_and_password_frames_roundtrip_and_have_their_kinds() {
    assert_eq!([kind::SET_FORCE_FIELD_ACCESS, kind::SHIP_FORCE_FIELD_PASSWORD], [184, 185]);
    assert_eq!(access().encode()[4], kind::SET_FORCE_FIELD_ACCESS);
    assert_eq!(password().encode()[4], kind::SHIP_FORCE_FIELD_PASSWORD);
    for message in [access(), password()] {
        let frame = message.encode();
        let decoded = Message::decode(&frame[4..]).unwrap();
        assert_eq!(decoded, message);
        assert_eq!(decoded.encode(), frame);
    }
}

#[test]
fn force_field_flags_that_are_not_zero_or_one_are_refused() {
    let mut bad_access = access().encode();
    let last = bad_access.len() - 1;
    bad_access[last] = 2;
    assert!(Message::decode(&bad_access[4..]).is_err());
    let mut bad_password = password().encode();
    let last = bad_password.len() - 1;
    bad_password[last] = 7;
    assert!(Message::decode(&bad_password[4..]).is_err());
}
