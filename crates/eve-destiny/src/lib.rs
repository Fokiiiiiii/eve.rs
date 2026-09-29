#![forbid(unsafe_code)]

//! Destiny kernels.
//!
//! Ports of the pure parts of `server/src/space/destiny`, verified bit for bit
//! against the JS reference through the golden fixtures in `oracle/fixtures`
//! (replayed by the `eve-oracle` crate).

pub mod ball;
pub mod carbon;
pub mod math;

pub use ball::{BallEntity, BallError, IdRef, Kind, encode_entity_ball, encode_free_ball, encode_rigid_ball};
pub use carbon::{
    CARBON_INTEGRATION_EPSILON, DESTINY_SPACE_FRICTION, Motion, Vec3, derive_carbon_intrinsic_acceleration,
    integrate_carbon_motion,
};
