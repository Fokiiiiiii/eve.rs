//! `behaviors/trees/drifters/idle.py`: the client writes what the scouts do when idle again for the drifters, the same; it is what `scoutsidle` builds.

pub use crate::trees::patrol::have_a_valid_target;
pub use crate::trees::scoutsidle::{create_analyze_behavior, create_idle_behavior, create_idle_fallback};
