use bytes::Bytes;
use eve_marshal::{SubStream, Value, decode_bytes, encode_packet};
use tokio::io::{AsyncRead, AsyncWrite, AsyncWriteExt};

use crate::frame::FrameReader;
use crate::handshake::HandshakeError;

pub const CALL_REQ: i64 = 6;
pub const CALL_RSP: i64 = 7;
pub const ERROR_RESPONSE: i64 = 15;
pub const SESSION_INITIAL_STATE_NOTIFICATION: i64 = 18;
pub const PING_REQ: i64 = 20;
pub const PING_RSP: i64 = 21;

/// What a login gave the client.
#[derive(Debug, Clone)]
pub struct ClientSession {
    pub user_id: i64,
    pub client_id: i64,
    pub session_id: i64,
    /// The server's `VersionExchangeServer` tuple.
    pub server_version: Vec<Value>,
    /// The dictionary in `CryptoServerHandshake` (macho_version, config_vals, ...).
    pub handshake: Vec<(Value, Value)>,
    /// The `session_init` dictionary of the acknowledgement.
    pub session_init: Vec<(Value, Value)>,
    /// The whole acknowledgement dictionary (session_init, sessionID, user_clientid, access_token, computer_hash, ...).
    pub acknowledgement: Vec<(Value, Value)>,
    /// The Python the server asked the client to run.
    pub signed_func: Bytes,
}

async fn send<W: AsyncWrite + Unpin>(writer: &mut W, value: &Value) -> Result<(), HandshakeError> {
    writer.write_all(&encode_packet(value)?).await?;
    Ok(())
}

async fn receive<R: AsyncRead + Unpin>(reader: &mut FrameReader<R>) -> Result<Value, HandshakeError> {
    let frame = reader.next().await?.ok_or(HandshakeError::Closed)?;
    Ok(decode_bytes(&frame)?)
}

pub fn text_of(value: &Value) -> Option<String> {
    match value {
        Value::Str(text) | Value::WString(text) | Value::Token(text) => Some(text.to_string()),
        Value::Bytes(bytes) | Value::Buffer(bytes) => String::from_utf8(bytes.to_vec()).ok(),
        _ => None,
    }
}

/// A dictionary entry by text key.
pub fn dict_get<'a>(entries: &'a [(Value, Value)], key: &str) -> Option<&'a Value> {
    entries.iter().find(|(name, _)| text_of(name).as_deref() == Some(key)).map(|(_, value)| value)
}

pub fn int_of(value: &Value) -> Option<i64> {
    match value {
        Value::Int(number) | Value::Long(number) => Some(*number),
        _ => None,
    }
}

/// CRC-HQX (CRC-16/XMODEM), the hash the Placebo crypto pack answers a challenge with.
pub fn crc_hqx(data: &[u8]) -> u16 {
    let mut crc = 0u16;
    for &byte in data {
        crc ^= u16::from(byte) << 8;
        for _ in 0..8 {
            crc = if crc & 0x8000 != 0 { (crc << 1) ^ 0x1021 } else { crc << 1 };
        }
    }
    crc
}

/// Logs in with an already computed password hash (`machobase.PasswordHash`; the server treats it as opaque).
pub async fn login<R, W>(
    reader: &mut FrameReader<R>,
    writer: &mut W,
    user: &str,
    password_hash: &[u8],
    language: &str,
) -> Result<ClientSession, HandshakeError>
where
    R: AsyncRead + Unpin,
    W: AsyncWrite + Unpin,
{
    let Value::Tuple(server_version) = receive(reader).await? else {
        return Err(HandshakeError::Protocol("expected the server's version tuple".into()));
    };
    if server_version.len() < 6 {
        return Err(HandshakeError::Protocol("the server's version tuple is short".into()));
    }
    send(
        writer,
        &Value::Tuple(vec![
            server_version[0].clone(),
            server_version[1].clone(),
            Value::Int(0),
            server_version[3].clone(),
            server_version[4].clone(),
            server_version[5].clone(),
            Value::None,
        ]),
    )
    .await?;
    send(writer, &Value::Tuple(vec![Value::None, Value::str("VK"), Value::None])).await?;
    send(writer, &Value::Tuple(vec![Value::str("placebo"), Value::Dict(vec![])])).await?;
    let ok = receive(reader).await?;
    if text_of(&ok).as_deref() != Some("OK CC") {
        return Err(HandshakeError::Protocol(format!("expected \"OK CC\", got {ok:?}")));
    }

    let macho_version = server_version[1].clone();
    let login = Value::dict([
        ("macho_version", macho_version),
        ("boot_version", server_version[3].clone()),
        ("boot_build", server_version[4].clone()),
        ("boot_codename", Value::str("eve-net-client")),
        ("boot_region", Value::str("local")),
        ("user_name", Value::string(user.to_owned())),
        ("user_password", Value::None),
        ("user_password_hash", Value::Buffer(Bytes::copy_from_slice(password_hash))),
        ("user_languageid", Value::string(language.to_owned())),
        ("user_affiliateid", Value::str("")),
        ("user_sso_token", Value::None),
    ]);
    send(writer, &Value::Tuple(vec![Value::Buffer(Bytes::from_static(&[7u8; 64])), login])).await?;

    let reply = receive(reader).await?;
    if let Value::CPickled(pickle) = &reply {
        return Err(HandshakeError::Rejected(String::from_utf8_lossy(pickle).into_owned()));
    }
    if int_of(&reply) != Some(2) {
        return Err(HandshakeError::Protocol(format!("expected password version 2, got {reply:?}")));
    }
    let handshake = receive(reader).await?;
    if let Value::CPickled(pickle) = &handshake {
        return Err(HandshakeError::Rejected(String::from_utf8_lossy(pickle).into_owned()));
    }
    let Value::Tuple(parts) = handshake else {
        return Err(HandshakeError::Protocol("expected the server handshake tuple".into()));
    };
    let (Some(challenge), Some(Value::Tuple(func)), Some(Value::Dict(details))) =
        (parts.first(), parts.get(1), parts.get(3))
    else {
        return Err(HandshakeError::Protocol("the server handshake tuple has the wrong shape".into()));
    };
    let signed_func = match func.first() {
        Some(Value::Buffer(bytes) | Value::Bytes(bytes)) => bytes.clone(),
        _ => Bytes::new(),
    };
    // The Placebo crypto hash is the CRC-HQX of the marshalled challenge.
    let hash = crc_hqx(&eve_marshal::encode(challenge)?).to_string();
    send(writer, &Value::Tuple(vec![Value::string(hash), Value::Bytes(Bytes::new()), Value::None])).await?;

    let ack = receive(reader).await?;
    if let Value::CPickled(pickle) = &ack {
        return Err(HandshakeError::Rejected(String::from_utf8_lossy(pickle).into_owned()));
    }
    let Value::Dict(ack) = ack else {
        return Err(HandshakeError::Protocol("expected the acknowledgement dictionary".into()));
    };
    let Some(Value::Dict(session_init)) = dict_get(&ack, "session_init") else {
        return Err(HandshakeError::Protocol("the acknowledgement has no session_init".into()));
    };
    let field = |name: &str| {
        dict_get(&ack, name).and_then(int_of).ok_or_else(|| HandshakeError::Protocol(format!("no {name}")))
    };
    Ok(ClientSession {
        user_id: dict_get(session_init, "userid").and_then(int_of).unwrap_or(0),
        client_id: field("user_clientid")?,
        session_id: field("sessionID")?,
        server_version,
        handshake: details.clone(),
        session_init: session_init.clone(),
        acknowledgement: ack.clone(),
        signed_func,
    })
}

const PACKET_STATE_LENGTH: usize = 14;

fn packet(class: &'static str, mut state: Vec<Value>) -> Value {
    state.resize(PACKET_STATE_LENGTH, Value::None);
    Value::Object { name: Box::new(Value::str(class)), args: Box::new(Value::Tuple(state)) }
}

impl ClientSession {
    fn source(&self, call_id: i64) -> Value {
        Value::Tuple(vec![Value::Int(2), Value::Long(self.client_id), Value::Int(call_id), Value::None])
    }

    /// A `CallReq` for `service.method(*args)`.
    pub fn call_packet(&self, call_id: i64, service: &str, method: &str, args: Vec<Value>) -> Value {
        let body = Value::Tuple(vec![
            Value::Int(1),
            Value::string(method.to_owned()),
            Value::Tuple(args),
            Value::None,
        ]);
        let payload = Value::Tuple(vec![Value::Tuple(vec![
            Value::Int(0),
            Value::SubStream(SubStream::Decoded(Box::new(body))),
        ])]);
        packet(
            "carbon.common.script.net.machoNetPacket.CallReq",
            vec![
                Value::Int(CALL_REQ),
                self.source(call_id),
                Value::Tuple(vec![Value::Int(8), Value::string(service.to_owned()), Value::None]),
                Value::Int(self.user_id),
                payload,
                Value::None,
            ],
        )
    }

    /// A `PingReq`.
    pub fn ping_packet(&self, call_id: i64) -> Value {
        packet(
            "carbon.common.script.net.machoNetPacket.PingReq",
            vec![
                Value::Int(PING_REQ),
                self.source(call_id),
                Value::Tuple(vec![Value::Int(8), Value::None, Value::None]),
                Value::Int(self.user_id),
                Value::Tuple(vec![]),
                Value::None,
            ],
        )
    }
}

/// The state tuple of a decoded packet.
pub fn packet_state(value: &Value) -> Option<&[Value]> {
    match value {
        Value::Object { args, .. } => match &**args {
            Value::Tuple(items) => Some(items),
            _ => None,
        },
        Value::Tuple(items) => Some(items),
        _ => None,
    }
}

/// The result a call response carries: its payload is `(SubStream(result),)` or `(result,)`.
pub fn result_of(state: &[Value]) -> Option<&Value> {
    let Value::Tuple(payload) = state.get(4)? else { return None };
    match payload.first()? {
        Value::SubStream(SubStream::Decoded(value)) => Some(value),
        other => Some(other),
    }
}
