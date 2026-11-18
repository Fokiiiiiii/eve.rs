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
    let mut radius = placement();
    radius.radius = f64::NAN;
    assert!(Message::decode(&Message::PlaceBattery(Box::new(radius)).encode()[4..]).is_err());
    let mut position = placement();
    position.position[1] = f64::INFINITY;
    assert!(Message::decode(&Message::PlaceBattery(Box::new(position)).encode()[4..]).is_err());
}
