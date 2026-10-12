#![forbid(unsafe_code)]

//! Space: what a solar system holds, how a ship moves in it, and what the client is sent about it.
//!
//! `world` builds a system's stations and celestials from the static data; `slim` and `payload` put them in the shapes
//! of the destiny stream; `scene` is the running system (`docs/design/adr/0007-space-simulation-process.md`).

pub mod brains;
pub mod crowd;
pub mod death;
pub mod deployable;
pub mod destruction;
pub mod dot_weapons;
pub mod drones;
pub mod ewar;
pub mod fighters;
pub mod flight;
pub mod gate;
pub mod geo;
pub mod health;
pub mod link;
pub mod locators;
pub mod missile_entity;
pub mod missiles;
pub mod modules;
pub mod motion;
pub mod names;
pub mod npc;
pub mod payload;
pub mod pochven;
pub mod resistance_shift;
pub mod scene;
pub mod sde;
pub mod security;
pub mod ship;
pub mod sim;
pub mod slim;
pub mod strike;
pub mod targeting;
pub mod tractor;
pub mod visibility;
pub mod warp;
pub mod weapons;
pub mod wire;
pub mod world;
pub mod wreck;
pub mod wreck_types;
