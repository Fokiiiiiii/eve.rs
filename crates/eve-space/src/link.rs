//! The messages between the service host and the simulation (`docs/adr/0007-space-simulation-process.md`).
//!
//! A frame is `u32 length | u8 kind | body`, little endian, text as `u16 length + UTF-8` and byte strings as
//! `u32 length + bytes`, like the front's link (ADR 0002). [`Message::encode`] writes a whole frame and
//! [`Message::decode`] reads a frame's payload (the kind and the body), so the same functions serve any framing layer.
//! `oracle/fixtures/simlink/messages.json` pins the layout for both sides.

use crate::ship::{ModuleSpec, ShipSpec};

pub const PROTOCOL_VERSION: u16 = 1;

/// The message kinds.
pub mod kind {
    pub const HELLO: u8 = 1;
    pub const HELLO_ACK: u8 = 2;
    pub const ENTER: u8 = 3;
    pub const LEAVE: u8 = 4;
    pub const BIND: u8 = 5;
    pub const UPDATE_STATE: u8 = 6;
    pub const COMMAND: u8 = 7;
    pub const SHIP_SPEC: u8 = 8;
    pub const NOTIFY: u8 = 9;
    pub const DOCKED: u8 = 10;
    pub const REFUSED: u8 = 11;
    pub const ACCEPTED: u8 = 12;
    pub const ENTER_REPLY: u8 = 13;
    pub const APPROACHING: u8 = 14;
}

/// How a ship comes into space.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum How {
    /// It undocks from a station.
    Undock,
    /// It is already in space (a login in space, a jump arrival).
    InSpace,
}

/// A ship enters a solar system.
#[derive(Debug, Clone, PartialEq)]
pub struct Enter {
    pub session_id: u64,
    pub solar_system_id: i32,
    pub how: How,
    /// The station undocked from (0 when the ship was already in space).
    pub station_id: i64,
    pub ship: ShipSpec,
}

#[derive(Debug, Clone, PartialEq)]
pub enum Message {
    Hello {
        version: u16,
    },
    HelloAck {
        version: u16,
        poll_hz: u32,
    },
    Enter(Box<Enter>),
    /// Whether an `Enter` was done (`Err` carries why not).
    EnterReply {
        session_id: u64,
        outcome: Result<(), String>,
    },
    /// `why`: 0 docked, 1 logged off, 2 jumped.
    Leave {
        session_id: u64,
        why: u8,
    },
    Bind {
        session_id: u64,
    },
    UpdateState {
        session_id: u64,
    },
    Command {
        session_id: u64,
        command: u8,
        args: Vec<f64>,
    },
    ShipSpec {
        session_id: u64,
        ship: ShipSpec,
    },
    Notify {
        session_id: u64,
        name: String,
        id_type: String,
        args: Vec<u8>,
    },
    Docked {
        session_id: u64,
        station_id: i64,
    },
    Refused {
        session_id: u64,
        command: u8,
        reason: String,
    },
    Accepted {
        session_id: u64,
        command: u8,
        at: i64,
    },
    Approaching {
        session_id: u64,
        command: u8,
        distance: f64,
    },
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum DecodeError {
    Empty,
    UnknownKind(u8),
    Truncated,
    Trailing,
    Utf8,
    BadValue(&'static str),
}

impl std::fmt::Display for DecodeError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::Empty => write!(f, "empty frame"),
            Self::UnknownKind(kind) => write!(f, "unknown message kind {kind}"),
            Self::Truncated => write!(f, "the frame ends before the message does"),
            Self::Trailing => write!(f, "bytes follow the end of the message"),
            Self::Utf8 => write!(f, "text is not UTF-8"),
            Self::BadValue(what) => write!(f, "bad {what}"),
        }
    }
}

impl std::error::Error for DecodeError {}

struct Reader<'a> {
    data: &'a [u8],
}

impl<'a> Reader<'a> {
    fn take(&mut self, count: usize) -> Result<&'a [u8], DecodeError> {
        if self.data.len() < count {
            return Err(DecodeError::Truncated);
        }
        let (head, rest) = self.data.split_at(count);
        self.data = rest;
        Ok(head)
    }

    fn array<const N: usize>(&mut self) -> Result<[u8; N], DecodeError> {
        Ok(self.take(N)?.try_into().expect("took N bytes"))
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

    fn i32(&mut self) -> Result<i32, DecodeError> {
        Ok(i32::from_le_bytes(self.array()?))
    }

    fn u64(&mut self) -> Result<u64, DecodeError> {
        Ok(u64::from_le_bytes(self.array()?))
    }

    fn i64(&mut self) -> Result<i64, DecodeError> {
        Ok(i64::from_le_bytes(self.array()?))
    }

    fn f64(&mut self) -> Result<f64, DecodeError> {
        Ok(f64::from_le_bytes(self.array()?))
    }

    fn text(&mut self) -> Result<String, DecodeError> {
        let length = self.u16()? as usize;
        String::from_utf8(self.take(length)?.to_vec()).map_err(|_| DecodeError::Utf8)
    }

    fn bytes(&mut self) -> Result<Vec<u8>, DecodeError> {
        let length = self.u32()? as usize;
        Ok(self.take(length)?.to_vec())
    }

    fn finish(&self) -> Result<(), DecodeError> {
        if self.data.is_empty() { Ok(()) } else { Err(DecodeError::Trailing) }
    }
}

fn put_text(out: &mut Vec<u8>, text: &str) {
    out.extend_from_slice(&(text.len() as u16).to_le_bytes());
    out.extend_from_slice(text.as_bytes());
}

fn put_bytes(out: &mut Vec<u8>, bytes: &[u8]) {
    out.extend_from_slice(&(bytes.len() as u32).to_le_bytes());
    out.extend_from_slice(bytes);
}

fn put_ship(out: &mut Vec<u8>, ship: &ShipSpec) {
    out.extend_from_slice(&ship.ship_id.to_le_bytes());
    out.extend_from_slice(&ship.character_id.to_le_bytes());
    out.extend_from_slice(&ship.type_id.to_le_bytes());
    out.extend_from_slice(&ship.group_id.to_le_bytes());
    out.extend_from_slice(&ship.category_id.to_le_bytes());
    put_text(out, &ship.name);
    out.extend_from_slice(&ship.owner_id.to_le_bytes());
    out.extend_from_slice(&ship.corporation_id.to_le_bytes());
    out.extend_from_slice(&ship.alliance_id.to_le_bytes());
    out.extend_from_slice(&ship.war_faction_id.to_le_bytes());
    for number in [
        ship.security_status,
        ship.radius,
        ship.mass,
        ship.max_velocity,
        ship.agility,
        ship.warp_speed_au,
        ship.shield_recharge_ms,
    ] {
        out.extend_from_slice(&number.to_le_bytes());
    }
    out.extend_from_slice(&(ship.modules.len() as u32).to_le_bytes());
    for module in &ship.modules {
        out.extend_from_slice(&module.item_id.to_le_bytes());
        out.extend_from_slice(&module.type_id.to_le_bytes());
        out.extend_from_slice(&module.flag.to_le_bytes());
    }
}

fn read_ship(reader: &mut Reader) -> Result<ShipSpec, DecodeError> {
    let ship_id = reader.i64()?;
    let character_id = reader.i64()?;
    let type_id = reader.i32()?;
    let group_id = reader.i32()?;
    let category_id = reader.i32()?;
    let name = reader.text()?;
    let owner_id = reader.i64()?;
    let corporation_id = reader.i32()?;
    let alliance_id = reader.i32()?;
    let war_faction_id = reader.i32()?;
    let [security_status, radius, mass, max_velocity, agility, warp_speed_au, shield_recharge_ms] = [
        reader.f64()?,
        reader.f64()?,
        reader.f64()?,
        reader.f64()?,
        reader.f64()?,
        reader.f64()?,
        reader.f64()?,
    ];
    let count = reader.u32()? as usize;
    // a module takes 16 bytes, so a count the frame cannot hold is refused before anything is allocated for it
    if count > reader.data.len() / 16 {
        return Err(DecodeError::Truncated);
    }
    let mut modules = Vec::with_capacity(count);
    for _ in 0..count {
        modules.push(ModuleSpec { item_id: reader.i64()?, type_id: reader.i32()?, flag: reader.i32()? });
    }
    Ok(ShipSpec {
        ship_id,
        character_id,
        type_id,
        group_id,
        category_id,
        name,
        owner_id,
        corporation_id,
        alliance_id,
        war_faction_id,
        security_status,
        radius,
        mass,
        max_velocity,
        agility,
        warp_speed_au,
        shield_recharge_ms,
        modules,
    })
}

impl Message {
    /// The kind byte of the message.
    pub fn kind(&self) -> u8 {
        match self {
            Self::Hello { .. } => kind::HELLO,
            Self::HelloAck { .. } => kind::HELLO_ACK,
            Self::Enter(_) => kind::ENTER,
            Self::EnterReply { .. } => kind::ENTER_REPLY,
            Self::Leave { .. } => kind::LEAVE,
            Self::Bind { .. } => kind::BIND,
            Self::UpdateState { .. } => kind::UPDATE_STATE,
            Self::Command { .. } => kind::COMMAND,
            Self::ShipSpec { .. } => kind::SHIP_SPEC,
            Self::Notify { .. } => kind::NOTIFY,
            Self::Docked { .. } => kind::DOCKED,
            Self::Refused { .. } => kind::REFUSED,
            Self::Accepted { .. } => kind::ACCEPTED,
            Self::Approaching { .. } => kind::APPROACHING,
        }
    }

    /// The frame's payload: the kind and the body, without the length prefix.
    pub fn payload(&self) -> Vec<u8> {
        let mut out = vec![self.kind()];
        match self {
            Self::Hello { version } => out.extend_from_slice(&version.to_le_bytes()),
            Self::HelloAck { version, poll_hz } => {
                out.extend_from_slice(&version.to_le_bytes());
                out.extend_from_slice(&poll_hz.to_le_bytes());
            }
            Self::Enter(enter) => {
                out.extend_from_slice(&enter.session_id.to_le_bytes());
                out.extend_from_slice(&enter.solar_system_id.to_le_bytes());
                out.push(match enter.how {
                    How::Undock => 0,
                    How::InSpace => 1,
                });
                out.extend_from_slice(&enter.station_id.to_le_bytes());
                put_ship(&mut out, &enter.ship);
            }
            Self::EnterReply { session_id, outcome } => {
                out.extend_from_slice(&session_id.to_le_bytes());
                match outcome {
                    Ok(()) => {
                        out.push(0);
                        put_text(&mut out, "");
                    }
                    Err(reason) => {
                        out.push(1);
                        put_text(&mut out, reason);
                    }
                }
            }
            Self::Leave { session_id, why } => {
                out.extend_from_slice(&session_id.to_le_bytes());
                out.push(*why);
            }
            Self::Bind { session_id } | Self::UpdateState { session_id } => {
                out.extend_from_slice(&session_id.to_le_bytes())
            }
            Self::Command { session_id, command, args } => {
                out.extend_from_slice(&session_id.to_le_bytes());
                out.push(*command);
                out.push(args.len() as u8);
                for arg in args {
                    out.extend_from_slice(&arg.to_le_bytes());
                }
            }
            Self::ShipSpec { session_id, ship } => {
                out.extend_from_slice(&session_id.to_le_bytes());
                put_ship(&mut out, ship);
            }
            Self::Notify { session_id, name, id_type, args } => {
                out.extend_from_slice(&session_id.to_le_bytes());
                put_text(&mut out, name);
                put_text(&mut out, id_type);
                put_bytes(&mut out, args);
            }
            Self::Docked { session_id, station_id } => {
                out.extend_from_slice(&session_id.to_le_bytes());
                out.extend_from_slice(&station_id.to_le_bytes());
            }
            Self::Refused { session_id, command, reason } => {
                out.extend_from_slice(&session_id.to_le_bytes());
                out.push(*command);
                put_text(&mut out, reason);
            }
            Self::Accepted { session_id, command, at } => {
                out.extend_from_slice(&session_id.to_le_bytes());
                out.push(*command);
                out.extend_from_slice(&at.to_le_bytes());
            }
            Self::Approaching { session_id, command, distance } => {
                out.extend_from_slice(&session_id.to_le_bytes());
                out.push(*command);
                out.extend_from_slice(&distance.to_le_bytes());
            }
        }
        out
    }

    /// The whole frame, length prefix included.
    pub fn encode(&self) -> Vec<u8> {
        let payload = self.payload();
        let mut frame = Vec::with_capacity(4 + payload.len());
        frame.extend_from_slice(&(payload.len() as u32).to_le_bytes());
        frame.extend_from_slice(&payload);
        frame
    }

    /// Reads a frame's payload.
    pub fn decode(payload: &[u8]) -> Result<Message, DecodeError> {
        let (&kind, body) = payload.split_first().ok_or(DecodeError::Empty)?;
        let mut r = Reader { data: body };
        let message = match kind {
            kind::HELLO => Message::Hello { version: r.u16()? },
            kind::HELLO_ACK => Message::HelloAck { version: r.u16()?, poll_hz: r.u32()? },
            kind::ENTER => {
                let session_id = r.u64()?;
                let solar_system_id = r.i32()?;
                let how = match r.u8()? {
                    0 => How::Undock,
                    1 => How::InSpace,
                    _ => return Err(DecodeError::BadValue("how")),
                };
                let station_id = r.i64()?;
                let ship = read_ship(&mut r)?;
                Message::Enter(Box::new(Enter { session_id, solar_system_id, how, station_id, ship }))
            }
            kind::ENTER_REPLY => {
                let session_id = r.u64()?;
                let status = r.u8()?;
                let reason = r.text()?;
                let outcome = match status {
                    0 => Ok(()),
                    1 => Err(reason),
                    _ => return Err(DecodeError::BadValue("status")),
                };
                Message::EnterReply { session_id, outcome }
            }
            kind::LEAVE => Message::Leave { session_id: r.u64()?, why: r.u8()? },
            kind::BIND => Message::Bind { session_id: r.u64()? },
            kind::UPDATE_STATE => Message::UpdateState { session_id: r.u64()? },
            kind::COMMAND => {
                let session_id = r.u64()?;
                let command = r.u8()?;
                let count = r.u8()? as usize;
                let mut args = Vec::with_capacity(count);
                for _ in 0..count {
                    args.push(r.f64()?);
                }
                Message::Command { session_id, command, args }
            }
            kind::SHIP_SPEC => Message::ShipSpec { session_id: r.u64()?, ship: read_ship(&mut r)? },
            kind::NOTIFY => Message::Notify {
                session_id: r.u64()?,
                name: r.text()?,
                id_type: r.text()?,
                args: r.bytes()?,
            },
            kind::DOCKED => Message::Docked { session_id: r.u64()?, station_id: r.i64()? },
            kind::REFUSED => Message::Refused { session_id: r.u64()?, command: r.u8()?, reason: r.text()? },
            kind::ACCEPTED => Message::Accepted { session_id: r.u64()?, command: r.u8()?, at: r.i64()? },
            kind::APPROACHING => {
                Message::Approaching { session_id: r.u64()?, command: r.u8()?, distance: r.f64()? }
            }
            other => return Err(DecodeError::UnknownKind(other)),
        };
        r.finish()?;
        Ok(message)
    }
}
