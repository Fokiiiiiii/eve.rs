//! What the tasks ask of the world they act in: the server's ballpark and what hangs on it. The client's `behaviors` package calls the server's
//! `ballpark`, `dogmaLM`, `entityLocation` and the like directly and none of them are part of the client; the traits of this module are those
//! calls, grown as the tasks were ported, with the names of the client's methods in snake case.
//!
//! Every method has an answer for a world that has nothing of the kind (`NullWorld`), so a test needs to say only what it uses. What a call raises
//! in the client (`UserError`, `RuntimeError`, `ReferenceError`, `KeyError`) is a `WorldError`.

pub mod ballpark;
pub mod dogma;
pub mod entities;
pub mod inventory;
pub mod logging;
pub mod testing;
pub mod universe;

pub use ballpark::{Ball, Ballpark, ProximitySensor, SlimItem};
pub use dogma::{Dogma, EffectInfo};
pub use entities::{DungeonObject, Entities, INVALID_GROUP_ID, ReinforcementWave, ResponseThresholds};
pub use inventory::{CargoCapacity, CustomInfo, FakeLocation, Inventory, InventoryItem};
pub use logging::Logging;
pub use testing::TestWorld;
pub use universe::{DamageTotals, Universe};

/// What the server's calls raise.
#[derive(Debug, Clone, PartialEq)]
pub enum WorldError {
    /// `eveexceptions.UserError`.
    User(String),
    /// `RuntimeError`.
    Runtime(String),
    /// `ReferenceError`: what a dead weak reference raises (an item that left while a task slept).
    Reference,
    /// `KeyError`.
    Key(String),
    /// `ReinforcedEntityGroupNotFoundError`.
    ReinforcedGroupNotFound,
}

pub type WorldResult<T> = Result<T, WorldError>;

/// The simulation clock's unit: ticks of `gametime` in a second (`SEC` of the client's `const`).
pub const SECOND: i64 = 10_000_000;

/// Seconds (as a task attribute gives them) in ticks of the clock.
pub fn seconds_to_ticks(seconds: f64) -> i64 {
    (seconds * SECOND as f64).round() as i64
}

/// The dice: Python's `random`.
pub trait Random {
    /// `random.random()`: a number of `[0, 1)`.
    fn random(&mut self) -> f64;

    /// `random.uniform(a, b)`.
    fn random_uniform(&mut self, a: f64, b: f64) -> f64 {
        a + (b - a) * self.random()
    }

    /// `random.randint(a, b)`: both ends included.
    fn random_int(&mut self, a: i64, b: i64) -> i64 {
        if b < a {
            return a;
        }
        a + (self.random() * (b - a + 1) as f64).floor() as i64
    }

    /// `random.choice`: the index of one of `len` things (`len` is not 0).
    fn random_index(&mut self, len: usize) -> usize {
        ((self.random() * len as f64).floor() as usize).min(len.saturating_sub(1))
    }
}

/// The world a behavior tree acts in.
pub trait World: Random + Ballpark + Dogma + Entities + Inventory + Universe + Logging {}

impl<T: Random + Ballpark + Dogma + Entities + Inventory + Universe + Logging> World for T {}

/// A world with nothing in it: its numbers are a fixed sequence, and it does nothing.
#[derive(Debug, Default)]
pub struct NullWorld {
    state: u64,
}

impl NullWorld {
    pub fn new(seed: u64) -> Self {
        Self { state: seed }
    }
}

impl Random for NullWorld {
    fn random(&mut self) -> f64 {
        // splitmix64: any seed (0 too) gives a good sequence
        self.state = self.state.wrapping_add(0x9E37_79B9_7F4A_7C15);
        let mut z = self.state;
        z = (z ^ (z >> 30)).wrapping_mul(0xBF58_476D_1CE4_E5B9);
        z = (z ^ (z >> 27)).wrapping_mul(0x94D0_49BB_1331_11EB);
        z ^= z >> 31;
        (z >> 11) as f64 / (1u64 << 53) as f64
    }
}

impl Ballpark for NullWorld {}
impl Dogma for NullWorld {}
impl Entities for NullWorld {}
impl Inventory for NullWorld {}
impl Universe for NullWorld {}
impl Logging for NullWorld {}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_null_worlds_numbers_are_in_range_and_repeat_for_a_seed() {
        let mut a = NullWorld::new(7);
        let mut b = NullWorld::new(7);
        for _ in 0..100 {
            let x = a.random();
            assert!((0.0..1.0).contains(&x));
            assert_eq!(x, b.random());
        }
    }

    #[test]
    fn a_random_int_includes_both_ends_and_a_random_index_stays_in_the_list() {
        let mut w = TestWorld::with_numbers(&[0.0, 0.999_999]);
        assert_eq!(w.random_int(3, 5), 3);
        assert_eq!(w.random_int(3, 5), 5);
        let mut w = TestWorld::with_numbers(&[0.999_999_9]);
        assert_eq!(w.random_index(4), 3);
        assert_eq!(seconds_to_ticks(1.5), 15_000_000);
    }
}
