use super::{BatteryPlacement, Message};

fn placement() -> BatteryPlacement {
    BatteryPlacement {
        battery_id: 1_050_000_001,
        solar_system_id: 30_000_001,
        type_id: 16_631,
        group_id: 1_000,
        display_name: "Battery".to_string(),
        holder_session: 1_790_646_377_000_015,
        position: [1.0, 2.0, 3.0],
        radius: 60.0,
        scan_resolution: 400.0,
        max_target_range: 50_000.0,
        max_locked_targets: 2,
        online: true,
    }
}

#[test]
fn battery_frames_roundtrip_byte_for_byte() {
    let messages = [
        Message::PlaceBattery(Box::new(placement())),
        Message::BatteryPlaced { battery_id: 1_050_000_001, reason: String::new() },
        Message::BatteryTarget {
            request_id: 70,
            battery_id: 1_050_000_001,
            solar_system_id: 30_000_001,
            order: 1,
            target: 1_790_646_377_000_016,
        },
        Message::BatteryTargets {
            request_id: 70,
            battery_id: 1_050_000_001,
            reason: String::new(),
            pending: true,
            targets: vec![1_790_646_377_000_015],
        },
        Message::BatteryFire {
            request_id: 71,
            battery_id: 1_050_000_001,
            solar_system_id: 30_000_001,
            on: true,
            target: 1_790_646_377_000_016,
        },
        Message::BatteryFire {
            request_id: 72,
            battery_id: 1_050_000_001,
            solar_system_id: 30_000_001,
            on: false,
            target: 0,
        },
        Message::BatteryFired {
            request_id: 71,
            battery_id: 1_050_000_001,
            reason: String::new(),
            active: true,
            target: 1_790_646_377_000_016,
        },
    ];
    for message in messages {
        let frame = message.encode();
        let decoded = Message::decode(&frame[4..]).unwrap();
        assert_eq!(message, decoded);
        assert_eq!(frame, decoded.encode());
    }
}

#[test]
fn battery_frames_refuse_an_order_outside_one_to_four_and_non_finite_numbers() {
    // the order byte sits after the kind (1), the request (8), the battery (8) and the system (4): frame offset 4 + 1 + 8 + 8 + 4
    let order =
        Message::BatteryTarget { request_id: 70, battery_id: 1, solar_system_id: 2, order: 1, target: 3 }
            .encode();
    let mut frame = order.clone();
    frame[25] = 5;
    assert!(Message::decode(&frame[4..]).is_err());
    frame[25] = 0;
    assert!(Message::decode(&frame[4..]).is_err());
    // the encoder makes no frame with a non-finite number, so the number is set in the bytes of a valid frame
    let frame = Message::PlaceBattery(Box::new(placement())).encode();
    assert!(Message::decode(&with_f64_from_end(frame.clone(), 29, f64::NAN)[4..]).is_err());
    assert!(Message::decode(&with_f64_from_end(frame, 45, f64::INFINITY)[4..]).is_err());
}

#[test]
fn battery_fire_refuses_a_flag_that_is_not_a_boolean() {
    // the on flag sits where the order byte of BatteryTarget does: frame offset 4 + kind (1) + request (8) + battery (8) + system (4)
    let mut frame =
        Message::BatteryFire { request_id: 71, battery_id: 1, solar_system_id: 2, on: true, target: 3 }
            .encode();
    assert!(Message::decode(&frame[4..]).is_ok());
    frame[25] = 2;
    assert!(Message::decode(&frame[4..]).is_err());
}

/// A ship's radii and its lock range and scan resolution are finite and not negative: the decoder refuses a ship that has one that is not.
#[test]
fn ship_frames_refuse_a_radius_or_range_that_is_not_finite_or_is_negative() {
    let good = crate::scene::tests::spec();
    for corrupt in [
        crate::ship::ShipSpec { radius: f64::NAN, ..good.clone() },
        crate::ship::ShipSpec { radius: -1.0, ..good.clone() },
        crate::ship::ShipSpec { signature_radius: f64::INFINITY, ..good.clone() },
        crate::ship::ShipSpec { max_target_range: -5.0, ..good.clone() },
        crate::ship::ShipSpec { scan_resolution: f64::NAN, ..good.clone() },
    ] {
        let frame = Message::ShipSpec { session_id: 7, ship: Box::new(corrupt.clone()) }.encode();
        assert!(Message::decode(&frame[4..]).is_err(), "a ship with {corrupt:?} was decoded");
    }
    let frame = Message::ShipSpec { session_id: 7, ship: Box::new(good) }.encode();
    assert!(Message::decode(&frame[4..]).is_ok(), "a valid ship is refused");
}

/// The frame with the eight bytes `from_end` bytes before its end set to `value` (see the force field tests).
fn with_f64_from_end(mut frame: Vec<u8>, from_end: usize, value: f64) -> Vec<u8> {
    let at = frame.len() - from_end;
    frame[at..at + 8].copy_from_slice(&value.to_le_bytes());
    frame
}

/// The encoder makes no battery placement with a non-finite number, as the C# encoder refuses it.
#[test]
#[should_panic(expected = "finite")]
fn battery_placement_with_a_non_finite_number_is_not_encoded() {
    let radius = BatteryPlacement { radius: f64::NAN, ..placement() };
    let _ = Message::PlaceBattery(Box::new(radius)).encode();
}

/// The encoder makes no battery order outside one to four, as the C# encoder refuses it.
#[test]
#[should_panic(expected = "order")]
fn a_battery_order_outside_one_to_four_is_not_encoded() {
    let _ = Message::BatteryTarget { request_id: 70, battery_id: 1, solar_system_id: 2, order: 5, target: 3 }
        .encode();
}

/// The encoder makes no frame whose text is longer than a u16 length allows: it is refused, not cut short.
#[test]
#[should_panic(expected = "u16")]
fn a_text_longer_than_a_u16_length_is_not_encoded() {
    let _ = Message::BatteryPlaced { battery_id: 1, reason: "x".repeat(usize::from(u16::MAX) + 1) }.encode();
}
