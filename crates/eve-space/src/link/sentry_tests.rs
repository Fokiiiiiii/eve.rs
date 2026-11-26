use super::kind;
use super::{Message, PROTOCOL_VERSION};

fn policy() -> Message {
    Message::SentryPolicy {
        tower_id: 1_060_000_001,
        solar_system_id: 30_000_001,
        online: true,
        standing: Some(-2.5),
        security_status: None,
        war: true,
        high_security: false,
        owner_corporation_id: 98_000_001,
        owner_alliance_id: 99_000_001,
    }
}

fn battery() -> Message {
    Message::SentryBattery { battery_id: 1_050_000_001, solar_system_id: 30_000_001, tower_id: 1_060_000_001 }
}

fn pilot() -> Message {
    Message::SentryPilot {
        tower_id: 1_060_000_001,
        solar_system_id: 30_000_001,
        ship_id: 1_790_646_377_000_016,
        corporation_id: 98_000_002,
        alliance_id: 0,
        security_status: Some(-4.9),
        standing: 0.0,
        at_war: false,
    }
}

#[test]
fn sentry_frames_roundtrip_under_their_kinds_at_protocol_106() {
    assert_eq!(PROTOCOL_VERSION, 106);
    let messages = [policy(), battery(), pilot()];
    let kinds: Vec<u8> = messages.iter().map(|message| message.encode()[4]).collect();
    assert_eq!(kinds, vec![kind::SENTRY_POLICY, kind::SENTRY_BATTERY, kind::SENTRY_PILOT]);
    for message in messages {
        let frame = message.encode();
        assert_eq!(Message::decode(&frame[4..]).expect("the frame decodes"), message);
    }
}

#[test]
fn sentry_frames_refuse_a_flag_that_is_not_a_boolean_and_a_non_finite_number() {
    // Payload offsets (after the kind byte): the policy's online flag is at 17, its standing flag at 18 with the number at 19..27.
    let mut frame = policy().encode();
    frame[17] = 2;
    assert!(Message::decode(&frame[4..]).is_err(), "a flag of 2 is refused");
    let mut frame = policy().encode();
    frame[19..27].copy_from_slice(&f64::NAN.to_le_bytes());
    assert!(Message::decode(&frame[4..]).is_err(), "a NaN standing is refused");
    // The pilot's standing is at 50..58 and its war flag at 58.
    let mut frame = pilot().encode();
    frame[50..58].copy_from_slice(&f64::INFINITY.to_le_bytes());
    assert!(Message::decode(&frame[4..]).is_err(), "an infinite standing is refused");
    let mut frame = pilot().encode();
    frame[58] = 9;
    assert!(Message::decode(&frame[4..]).is_err(), "a war flag of 9 is refused");
}
