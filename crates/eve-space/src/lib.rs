#![forbid(unsafe_code)]

//! Space: what a solar system holds, how a ship moves in it, and what the client is sent about it.
//!
//! `world` builds a system's stations and celestials from the static data; `slim` and `payload` put them in the shapes
//! of the destiny stream; `scene` is the running system (`docs/adr/0007-space-simulation-process.md`).

pub mod geo;
pub mod names;
pub mod sde;
pub mod world;
