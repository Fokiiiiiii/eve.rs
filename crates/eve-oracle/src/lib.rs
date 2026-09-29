//! Replays the golden fixtures recorded from the JS implementation
//! (`tools/oracle`) against the Rust implementations.
//!
//! The fixtures describe JS values in EJSON; [`js`] parses them into a small
//! model of the JS value graph and [`marshal_adapter`] maps that model to and
//! from the typed [`eve_marshal::Value`], applying the same coercions the JS
//! encoder applies to loosely typed input.

pub mod destiny_adapter;
pub mod fixtures;
pub mod js;
pub mod js_semantics;
pub mod marshal_adapter;
