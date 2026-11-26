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
    // the encoder makes no frame with a non-finite number, so the number is set in the bytes of a valid frame
    let frame = Message::PlaceForceField(Box::new(placement())).encode();
    assert!(Message::decode(&with_f64_from_end(frame.clone(), 8, f64::NAN)[4..]).is_err());
    assert!(Message::decode(&with_f64_from_end(frame, 24, f64::INFINITY)[4..]).is_err());
}

/// The decoder refuses a field or tower id that is zero or negative, as the placement refuses it.
#[test]
fn force_field_frames_refuse_an_id_that_is_not_positive() {
    for (field_id, tower_id) in
        [(0, 1_050_000_099), (-1, 1_050_000_099), (1_050_000_100, 0), (1_050_000_100, -1)]
    {
        let bad = ForceFieldPlacement { field_id, tower_id, ..placement() };
        assert!(
            Message::decode(&Message::PlaceForceField(Box::new(bad)).encode()[4..]).is_err(),
            "field {field_id} of tower {tower_id} was decoded"
        );
    }
}

/// The decoder refuses a radius above one billion metres, whose square overflows; the largest radius is decoded.
#[test]
fn force_field_frames_refuse_a_radius_above_a_billion_metres() {
    let too_large = ForceFieldPlacement { radius: 1.0e9 + 1.0, ..placement() };
    assert!(Message::decode(&Message::PlaceForceField(Box::new(too_large)).encode()[4..]).is_err());
    let largest = ForceFieldPlacement { radius: 1.0e9, ..placement() };
    assert!(Message::decode(&Message::PlaceForceField(Box::new(largest)).encode()[4..]).is_ok());
}

/// The frame with the eight bytes `from_end` bytes before its end set to `value`: how a frame with a non-finite number is made, since the encoder makes none.
fn with_f64_from_end(mut frame: Vec<u8>, from_end: usize, value: f64) -> Vec<u8> {
    let at = frame.len() - from_end;
    frame[at..at + 8].copy_from_slice(&value.to_le_bytes());
    frame
}

/// The encoder makes no force field frame with a non-finite number, as the C# encoder refuses it.
#[test]
#[should_panic(expected = "finite")]
fn force_field_placement_with_a_non_finite_number_is_not_encoded() {
    let radius = ForceFieldPlacement { radius: f64::NAN, ..placement() };
    let _ = Message::PlaceForceField(Box::new(radius)).encode();
}

#[test]
fn force_field_frames_refuse_a_non_positive_field_or_tower_id() {
    // the frame layout is unchanged: a field and a tower are named by a positive id, so a zero or negative one is refused
    let refused = [
        Message::PlaceForceField(Box::new(ForceFieldPlacement { field_id: 0, ..placement() })),
        Message::PlaceForceField(Box::new(ForceFieldPlacement { field_id: -1, ..placement() })),
        Message::PlaceForceField(Box::new(ForceFieldPlacement { tower_id: 0, ..placement() })),
        Message::PlaceForceField(Box::new(ForceFieldPlacement { tower_id: -1, ..placement() })),
        Message::ForceFieldPlaced { field_id: 0, reason: String::new() },
        Message::RemoveForceField { tower_id: 0, solar_system_id: 30_000_001 },
        Message::ForceFieldRemoved { tower_id: -1, reason: String::new() },
        Message::SetForceFieldAccess {
            tower_id: 0,
            solar_system_id: 30_000_001,
            corporation_id: 98_000_001,
            alliance_id: 97_000_001,
            allow_corporation: true,
            allow_alliance: false,
        },
        Message::ShipForceFieldPassword {
            session_id: 1_790_646_377_000_015,
            tower_id: -1,
            solar_system_id: 30_000_001,
            matched: true,
        },
    ];
    for message in refused {
        let frame = message.encode();
        assert!(Message::decode(&frame[4..]).is_err(), "decoded {message:?}");
    }
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
fn ship_force_field_harmonic_frame_roundtrips_and_refuses_a_harmonic_that_is_not_a_tower_or_none() {
    assert_eq!(kind::SHIP_FORCE_FIELD_HARMONIC, 186);
    let report = |harmonic| Message::ShipForceFieldHarmonic {
        session_id: 1_790_646_377_000_015,
        ship_id: 1_790_000_001,
        harmonic,
    };
    let frame = report(1_050_000_099).encode();
    assert_eq!(frame[4], kind::SHIP_FORCE_FIELD_HARMONIC);
    let decoded = Message::decode(&frame[4..]).unwrap();
    assert_eq!(decoded, report(1_050_000_099));
    assert_eq!(decoded.encode(), frame);

    // -1 is the only negative harmonic; zero and other negatives are not a tower
    let none = report(-1).encode();
    assert_eq!(Message::decode(&none[4..]).unwrap(), report(-1));
    for bad in [0, -2] {
        let frame = report(bad).encode();
        assert!(Message::decode(&frame[4..]).is_err(), "harmonic {bad} is refused");
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
