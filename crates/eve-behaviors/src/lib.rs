//! The NPC behavior trees of the EVE client's `behaviors` package (V24.01), ported to Rust as they are: the runtime (`tree`), the blackboards the
//! tasks talk through (`blackboards`), the messages about items (`events`), the timers (`timers`), the composites and decorators, and the tasks and
//! trees built on them. What the tasks ask of the server (the client's package calls the server's `ballpark` and the like) is the `env::World`
//! trait.

#[macro_use]
mod macros;

pub mod blackboards;
pub mod composites;
pub mod conditions;
pub mod constants;
pub mod consts;
pub mod decorators;
pub mod env;
pub mod evemath;
pub mod events;
pub mod groups;
pub mod timers;
pub mod tree;
pub mod utility;
pub mod value;
