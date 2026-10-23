//! Front-to-host frames use `u32 length | u8 kind | body`.

use bytes::{BufMut, Bytes, BytesMut};
use std::fmt;

pub const PROTOCOL_VERSION: u16 = 2;

pub mod kind {
    pub const HELLO: u8 = 1;
    pub const HELLO_ACK: u8 = 2;
    pub const AUTH_REQUEST: u8 = 3;
    pub const AUTH_REPLY: u8 = 4;
    pub const SESSION_OPEN: u8 = 5;
    pub const SESSION_CLOSE: u8 = 6;
    pub const PACKET: u8 = 7;
    pub const SEND: u8 = 8;
    pub const DISCONNECT: u8 = 9;
    pub const CLUSTER_CONFIG: u8 = 10;
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum AuthOutcome {
    Accepted { user_id: u32, role: u64, country_code: String },
    Rejected { reason: String },
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
#[repr(u8)]
pub enum AuthCredentialKind {
    Password = 0,
    Sso = 1,
}

/// A CCP SSO token. Debug output is always redacted.
#[derive(Clone, PartialEq, Eq)]
pub struct SsoToken(String);

impl SsoToken {
    pub fn new(token: String) -> Self {
        Self(token)
    }

    pub fn as_str(&self) -> &str {
        &self.0
    }
}

impl fmt::Debug for SsoToken {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter.write_str("<redacted>")
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SessionOpen {
    pub session_id: u64,
    pub client_id: u64,
    pub user_id: u32,
    pub role: u64,
    pub address: String,
    pub language: String,
    pub country_code: String,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Message {
    Hello {
        version: u16,
        node_id: u32,
    },
    HelloAck {
        version: u16,
    },
    AuthRequest {
        corr: u32,
        credential_kind: AuthCredentialKind,
        user_name: String,
        password_hash: Bytes,
        sso_token: Option<SsoToken>,
        language: String,
        address: String,
    },
    AuthReply {
        corr: u32,
        outcome: AuthOutcome,
    },
    SessionOpen(SessionOpen),
    SessionClose {
        session_id: u64,
    },
    Packet {
        session_id: u64,
        payload: Bytes,
    },
    Send {
        session_id: u64,
        payload: Bytes,
    },
    Disconnect {
        session_id: u64,
        reason: String,
    },
    ClusterConfig {
        payload: Bytes,
    },
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum DecodeError {
    Empty,
    UnknownKind(u8),
    Truncated,
    Trailing,
    Utf8,
    BadStatus(u8),
    BadCredentialKind(u8),
}

impl std::fmt::Display for DecodeError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::Empty => write!(f, "empty frame"),
            Self::UnknownKind(kind) => write!(f, "unknown message kind {kind}"),
            Self::Truncated => write!(f, "the frame ends before the message does"),
            Self::Trailing => write!(f, "bytes follow the end of the message"),
            Self::Utf8 => write!(f, "text is not UTF-8"),
            Self::BadStatus(status) => write!(f, "unknown status {status}"),
            Self::BadCredentialKind(_) => write!(f, "unknown or inconsistent authentication credential kind"),
        }
    }
}

impl std::error::Error for DecodeError {}

/// A cursor over a frame's body.
struct Reader {
    data: Bytes,
    at: usize,
}

impl Reader {
    fn take(&mut self, count: usize) -> Result<Bytes, DecodeError> {
        if self.data.len() - self.at < count {
            return Err(DecodeError::Truncated);
        }
        let slice = self.data.slice(self.at..self.at + count);
        self.at += count;
        Ok(slice)
    }

    fn array<const N: usize>(&mut self) -> Result<[u8; N], DecodeError> {
        let mut out = [0u8; N];
        out.copy_from_slice(&self.take(N)?);
        Ok(out)
    }

    fn u8(&mut self) -> Result<u8, DecodeError> {
        Ok(self.array::<1>()?[0])
    }

    fn u16(&mut self) -> Result<u16, DecodeError> {
        Ok(u16::from_le_bytes(self.array()?))
    }

    fn u32(&mut self) -> Result<u32, DecodeError> {
        Ok(u32::from_le_bytes(self.array()?))
    }

    fn u64(&mut self) -> Result<u64, DecodeError> {
        Ok(u64::from_le_bytes(self.array()?))
    }

    fn text(&mut self) -> Result<String, DecodeError> {
        let length = self.u16()? as usize;
        String::from_utf8(self.take(length)?.to_vec()).map_err(|_| DecodeError::Utf8)
    }

    fn bytes(&mut self) -> Result<Bytes, DecodeError> {
        let length = self.u32()? as usize;
        self.take(length)
    }

    /// Everything that is left (a body that ends in raw bytes).
    fn rest(&mut self) -> Bytes {
        let rest = self.data.slice(self.at..);
        self.at = self.data.len();
        rest
    }

    fn finish(&self) -> Result<(), DecodeError> {
        if self.at == self.data.len() { Ok(()) } else { Err(DecodeError::Trailing) }
    }
}

fn put_text(out: &mut BytesMut, text: &str) {
    debug_assert!(text.len() <= u16::MAX as usize, "text longer than a u16 length");
    out.put_u16_le(text.len() as u16);
    out.put_slice(text.as_bytes());
}

impl Message {
    /// Decodes a frame's payload: the kind byte and the body.
    pub fn decode(payload: Bytes) -> Result<Self, DecodeError> {
        let Some(&kind) = payload.first() else { return Err(DecodeError::Empty) };
        let mut reader = Reader { data: payload, at: 1 };
        let message = match kind {
            kind::HELLO => Self::Hello { version: reader.u16()?, node_id: reader.u32()? },
            kind::HELLO_ACK => Self::HelloAck { version: reader.u16()? },
            kind::AUTH_REQUEST => {
                let corr = reader.u32()?;
                let credential_kind = match reader.u8()? {
                    0 => AuthCredentialKind::Password,
                    1 => AuthCredentialKind::Sso,
                    other => return Err(DecodeError::BadCredentialKind(other)),
                };
                let user_name = reader.text()?;
                let password_hash = reader.bytes()?;
                let token = reader.text()?;
                if credential_kind == AuthCredentialKind::Password && !token.is_empty() {
                    return Err(DecodeError::BadCredentialKind(credential_kind as u8));
                }
                let sso_token = (!token.is_empty()).then(|| SsoToken::new(token));
                Self::AuthRequest {
                    corr,
                    credential_kind,
                    user_name,
                    password_hash,
                    sso_token,
                    language: reader.text()?,
                    address: reader.text()?,
                }
            }
            kind::AUTH_REPLY => {
                let corr = reader.u32()?;
                let outcome = match reader.u8()? {
                    0 => AuthOutcome::Accepted {
                        user_id: reader.u32()?,
                        role: reader.u64()?,
                        country_code: reader.text()?,
                    },
                    1 => AuthOutcome::Rejected { reason: reader.text()? },
                    status => return Err(DecodeError::BadStatus(status)),
                };
                Self::AuthReply { corr, outcome }
            }
            kind::SESSION_OPEN => Self::SessionOpen(SessionOpen {
                session_id: reader.u64()?,
                client_id: reader.u64()?,
                user_id: reader.u32()?,
                role: reader.u64()?,
                address: reader.text()?,
                language: reader.text()?,
                country_code: reader.text()?,
            }),
            kind::SESSION_CLOSE => Self::SessionClose { session_id: reader.u64()? },
            kind::PACKET => Self::Packet { session_id: reader.u64()?, payload: reader.rest() },
            kind::SEND => Self::Send { session_id: reader.u64()?, payload: reader.rest() },
            kind::DISCONNECT => Self::Disconnect { session_id: reader.u64()?, reason: reader.text()? },
            kind::CLUSTER_CONFIG => Self::ClusterConfig { payload: reader.rest() },
            other => return Err(DecodeError::UnknownKind(other)),
        };
        reader.finish()?;
        Ok(message)
    }

    /// Appends the whole frame, length prefix included.
    pub fn encode(&self, out: &mut BytesMut) {
        let start = out.len();
        out.put_u32_le(0); // the length, patched below
        match self {
            Self::Hello { version, node_id } => {
                out.put_u8(kind::HELLO);
                out.put_u16_le(*version);
                out.put_u32_le(*node_id);
            }
            Self::HelloAck { version } => {
                out.put_u8(kind::HELLO_ACK);
                out.put_u16_le(*version);
            }
            Self::AuthRequest {
                corr,
                credential_kind,
                user_name,
                password_hash,
                sso_token,
                language,
                address,
            } => {
                out.put_u8(kind::AUTH_REQUEST);
                out.put_u32_le(*corr);
                out.put_u8(*credential_kind as u8);
                put_text(out, user_name);
                out.put_u32_le(password_hash.len() as u32);
                out.put_slice(password_hash);
                put_text(out, sso_token.as_ref().map_or("", SsoToken::as_str));
                put_text(out, language);
                put_text(out, address);
            }
            Self::AuthReply { corr, outcome } => {
                out.put_u8(kind::AUTH_REPLY);
                out.put_u32_le(*corr);
                match outcome {
                    AuthOutcome::Accepted { user_id, role, country_code } => {
                        out.put_u8(0);
                        out.put_u32_le(*user_id);
                        out.put_u64_le(*role);
                        put_text(out, country_code);
                    }
                    AuthOutcome::Rejected { reason } => {
                        out.put_u8(1);
                        put_text(out, reason);
                    }
                }
            }
            Self::SessionOpen(open) => {
                out.put_u8(kind::SESSION_OPEN);
                out.put_u64_le(open.session_id);
                out.put_u64_le(open.client_id);
                out.put_u32_le(open.user_id);
                out.put_u64_le(open.role);
                put_text(out, &open.address);
                put_text(out, &open.language);
                put_text(out, &open.country_code);
            }
            Self::SessionClose { session_id } => {
                out.put_u8(kind::SESSION_CLOSE);
                out.put_u64_le(*session_id);
            }
            Self::Packet { session_id, payload } => {
                out.put_u8(kind::PACKET);
                out.put_u64_le(*session_id);
                out.put_slice(payload);
            }
            Self::Send { session_id, payload } => {
                out.put_u8(kind::SEND);
                out.put_u64_le(*session_id);
                out.put_slice(payload);
            }
            Self::Disconnect { session_id, reason } => {
                out.put_u8(kind::DISCONNECT);
                out.put_u64_le(*session_id);
                put_text(out, reason);
            }
            Self::ClusterConfig { payload } => {
                out.put_u8(kind::CLUSTER_CONFIG);
                out.put_slice(payload);
            }
        }
        let length = (out.len() - start - 4) as u32;
        out[start..start + 4].copy_from_slice(&length.to_le_bytes());
    }
}
