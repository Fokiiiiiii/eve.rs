#![forbid(unsafe_code)]
//! The network front of eve.rs.
//!
//! It owns the client's TCP connection: framing ([`frame`]), the login handshake ([`handshake`]) and the
//! link to the service host ([`hostlink`] is the message set, [`host`] the client of it). After the
//! handshake it forwards each client packet to the host as the raw bytes it received and writes back what
//! the host returns; it never decodes a call. See `docs/design/adr/0002-front-host-link.md`.

pub mod client;
pub mod frame;
pub mod handshake;
pub mod host;
pub mod hostlink;
pub mod profile;
pub mod server;
