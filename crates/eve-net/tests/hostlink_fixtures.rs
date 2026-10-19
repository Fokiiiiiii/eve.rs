use bytes::{Bytes, BytesMut};
use eve_net::hostlink::{AuthOutcome, DecodeError, Message, SessionOpen};
use serde_json::Value;

fn fixtures() -> Value {
    let path = concat!(env!("CARGO_MANIFEST_DIR"), "/../../oracle/fixtures/hostlink/messages.json");
    serde_json::from_str(&std::fs::read_to_string(path).unwrap()).unwrap()
}

fn hex(text: &str) -> Vec<u8> {
    (0..text.len() / 2).map(|i| u8::from_str_radix(&text[2 * i..2 * i + 2], 16).unwrap()).collect()
}

fn text(case: &Value, field: &str) -> String {
    case[field].as_str().unwrap_or_else(|| panic!("{field} in {case}")).to_owned()
}

fn u64_of(case: &Value, field: &str) -> u64 {
    text(case, field).parse().unwrap()
}

fn message_of(case: &Value) -> Message {
    let number = |field: &str| case[field].as_u64().unwrap_or_else(|| panic!("{field} in {case}"));
    match case["kind"].as_str().unwrap() {
        "Hello" => Message::Hello { version: number("version") as u16, node_id: number("nodeId") as u32 },
        "HelloAck" => Message::HelloAck { version: number("version") as u16 },
        "AuthRequest" => Message::AuthRequest {
            corr: number("corr") as u32,
            user_name: text(case, "userName"),
            password_hash: Bytes::from(hex(case["passwordHash"].as_str().unwrap())),
            language: text(case, "language"),
            address: text(case, "address"),
        },
        "AuthReply" => Message::AuthReply {
            corr: number("corr") as u32,
            outcome: if case["ok"].as_bool().unwrap() {
                AuthOutcome::Accepted {
                    user_id: number("userId") as u32,
                    role: u64_of(case, "role"),
                    country_code: text(case, "countryCode"),
                }
            } else {
                AuthOutcome::Rejected { reason: text(case, "reason") }
            },
        },
        "SessionOpen" => Message::SessionOpen(SessionOpen {
            session_id: u64_of(case, "sessionId"),
            client_id: u64_of(case, "clientId"),
            user_id: number("userId") as u32,
            role: u64_of(case, "role"),
            address: text(case, "address"),
            language: text(case, "language"),
            country_code: text(case, "countryCode"),
        }),
        "SessionClose" => Message::SessionClose { session_id: u64_of(case, "sessionId") },
        "Packet" => Message::Packet {
            session_id: u64_of(case, "sessionId"),
            payload: Bytes::from(hex(case["payload"].as_str().unwrap())),
        },
        "Send" => Message::Send {
            session_id: u64_of(case, "sessionId"),
            payload: Bytes::from(hex(case["payload"].as_str().unwrap())),
        },
        "Disconnect" => {
            Message::Disconnect { session_id: u64_of(case, "sessionId"), reason: text(case, "reason") }
        }
        "ClusterConfig" => {
            Message::ClusterConfig { payload: Bytes::from(hex(case["payload"].as_str().unwrap())) }
        }
        other => panic!("unknown kind {other}"),
    }
}

#[test]
fn every_valid_frame_encodes_to_its_bytes_and_decodes_back() {
    let fixtures = fixtures();
    let cases = fixtures["valid"].as_array().unwrap();
    assert!(cases.len() >= 13);
    for case in cases {
        let name = case["name"].as_str().unwrap();
        let expected = hex(case["hex"].as_str().unwrap());
        let message = message_of(case);

        let mut out = BytesMut::new();
        message.encode(&mut out);
        assert_eq!(&out[..], &expected[..], "encoding {name}");

        let decoded =
            Message::decode(Bytes::from(expected[4..].to_vec())).unwrap_or_else(|e| panic!("{name}: {e}"));
        assert_eq!(decoded, message, "decoding {name}");
    }
}

#[test]
fn every_invalid_frame_is_refused_for_its_reason() {
    let fixtures = fixtures();
    let cases = fixtures["invalid"].as_array().unwrap();
    assert!(cases.len() >= 9);
    for case in cases {
        let name = case["name"].as_str().unwrap();
        let raw = hex(case["hex"].as_str().unwrap());
        let error = Message::decode(Bytes::from(raw[4..].to_vec())).expect_err(name);
        let expected = case["reason"].as_str().unwrap();
        let matches = match error {
            DecodeError::Empty => expected == "empty",
            DecodeError::UnknownKind(_) => expected == "unknown-kind",
            DecodeError::Truncated => expected == "truncated",
            DecodeError::Trailing => expected == "trailing",
            DecodeError::Utf8 => expected == "utf8",
            DecodeError::BadStatus(_) => expected == "bad-status",
        };
        assert!(matches, "{name}: refused as {error:?}, expected {expected}");
    }
}

#[test]
fn a_frame_is_never_read_past_its_end() {
    // A decoder must not read beyond the payload it was given, whatever the declared lengths say.
    for length in 0..40usize {
        let payload: Vec<u8> = (0..length).map(|i| (i * 37 + 3) as u8).collect();
        let _ = Message::decode(Bytes::from(payload));
    }
}
