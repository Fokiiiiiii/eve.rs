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
pub mod spawning;
pub mod testing;
pub mod universe;

pub use ballpark::{Ball, Ballpark, Formation, FormationSettings, MiniBall, ProximitySensor, SlimItem};
pub use dogma::{Dogma, EffectCycle, EffectInfo};
pub use entities::{
    DungeonObject, Entities, FleetReport, INVALID_GROUP_ID, PlayerFleetReport, ReinforcementWave,
    ResponseThresholds, StructureState,
};
pub use inventory::{CargoCapacity, CustomInfo, FakeLocation, Inventory, InventoryItem};
pub use logging::Logging;
pub use spawning::{Deployed, Deployment, EntitySpawnData, ReactiveSpawnConfig, SpawnPick, Spawning};
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
    /// `ZeroDivisionError`.
    ZeroDivision,
    /// `inventorycommon.WrongInventoryLocation`: an item that is not where the call looked for it.
    WrongInventoryLocation,
    /// `inventorycommon.FakeItemNotHere`: a fake item (an NPC's) that is not in this system.
    FakeItemNotHere,
    /// `behaviors.entity.NPCWarpError` and what is derived from it: why the warp of an NPC did not start.
    NpcWarp(NpcWarpKind),
}

/// The `NPCWarpError`s (`behaviors/entity.py`).
#[derive(Debug, Clone, PartialEq)]
pub enum NpcWarpKind {
    /// `NpcWarpScrambledError`.
    Scrambled,
    /// `NpcWarpBubbledError`.
    Bubbled,
    /// `NpcWarpPathBubbledError`.
    PathBubbled,
    /// `NPCWarpError` itself.
    Other(String),
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

    /// `random.getrandbits(k)`: an unbiased integer from `[0, 2^k)`, for `k` through 64.
    fn random_bits(&mut self, bits: u32) -> u64;

    /// `random.uniform(a, b)`.
    fn random_uniform(&mut self, a: f64, b: f64) -> f64 {
        a + (b - a) * self.random()
    }

    /// `random.randint(a, b)`: both ends included.
    fn random_int(&mut self, a: i64, b: i64) -> Result<i64, RandomRangeError> {
        let width = b as i128 - a as i128 + 1;
        if width <= 0 {
            return Err(RandomRangeError { start: a, stop: b as i128 + 1, width });
        }

        let offset = if width < (1i128 << 53) {
            (self.random() * width as f64).floor() as i128
        } else {
            let width = width as u128;
            let bits = u128::BITS - (width - 1).leading_zeros();
            loop {
                let candidate = self.random_bits(bits) as u128;
                if candidate < width {
                    break candidate as i128;
                }
            }
        };
        Ok((a as i128 + offset) as i64)
    }

    /// `random.choice`: the index of one of `len` things (`len` is not 0).
    fn random_index(&mut self, len: usize) -> usize {
        ((self.random() * len as f64).floor() as usize).min(len.saturating_sub(1))
    }
}

/// Python's `ValueError` for an empty `randrange(start, stop)` interval.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct RandomRangeError {
    start: i64,
    stop: i128,
    width: i128,
}

impl std::fmt::Display for RandomRangeError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "ValueError: empty range for randrange() ({}, {}, {})", self.start, self.stop, self.width)
    }
}

impl std::error::Error for RandomRangeError {}

/// The world a behavior tree acts in.
pub trait World: Random + Ballpark + Dogma + Entities + Inventory + Universe + Logging + Spawning {}

impl<T: Random + Ballpark + Dogma + Entities + Inventory + Universe + Logging + Spawning> World for T {}

/// A world with nothing in it: its numbers are a fixed sequence, and it does nothing.
#[derive(Debug, Default)]
pub struct NullWorld {
    state: u64,
}

impl NullWorld {
    pub fn new(seed: u64) -> Self {
        Self { state: seed }
    }

    fn next_u64(&mut self) -> u64 {
        // splitmix64: any seed (0 too) gives a good sequence
        self.state = self.state.wrapping_add(0x9E37_79B9_7F4A_7C15);
        let mut z = self.state;
        z = (z ^ (z >> 30)).wrapping_mul(0xBF58_476D_1CE4_E5B9);
        z = (z ^ (z >> 27)).wrapping_mul(0x94D0_49BB_1331_11EB);
        z ^ (z >> 31)
    }
}

impl Random for NullWorld {
    fn random(&mut self) -> f64 {
        (self.next_u64() >> 11) as f64 / (1u64 << 53) as f64
    }

    fn random_bits(&mut self, bits: u32) -> u64 {
        assert!(bits <= 64);
        if bits == 0 { 0 } else { self.next_u64() >> (64 - bits) }
    }
}

impl Ballpark for NullWorld {}
impl Dogma for NullWorld {}
impl Entities for NullWorld {}
impl Inventory for NullWorld {}
impl Universe for NullWorld {}
impl Logging for NullWorld {}
impl Spawning for NullWorld {}

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
        assert_eq!(w.random_int(3, 5), Ok(3));
        assert_eq!(w.random_int(3, 5), Ok(5));
        let mut w = TestWorld::with_numbers(&[0.999_999_9]);
        assert_eq!(w.random_index(4), 3);
        assert_eq!(seconds_to_ticks(1.5), 15_000_000);
    }

    #[test]
    fn a_random_int_reports_python_empty_range_details_without_drawing() {
        let mut w = TestWorld::with_numbers(&[0.5]);
        assert_eq!(
            w.random_int(2, 1).unwrap_err().to_string(),
            "ValueError: empty range for randrange() (2, 2, 0)"
        );
        assert_eq!(w.next, 0);

        assert_eq!(
            w.random_int(i64::MAX, i64::MIN).unwrap_err().to_string(),
            "ValueError: empty range for randrange() (9223372036854775807, -9223372036854775807, -18446744073709551614)"
        );
        assert_eq!(w.next, 0);
    }

    #[test]
    fn a_random_int_includes_negative_and_singleton_boundary_values() {
        let mut w = TestWorld::with_numbers(&[0.0, 0.999_999_9, 0.5]);
        assert_eq!(w.random_int(-5, -2), Ok(-5));
        assert_eq!(w.random_int(-5, -2), Ok(-2));
        assert_eq!(w.random_int(i64::MAX, i64::MAX), Ok(i64::MAX));

        let mut w = TestWorld::with_numbers(&[0.0]);
        assert_eq!(w.random_int(i64::MIN, i64::MIN), Ok(i64::MIN));
    }

    #[test]
    fn wide_random_ints_use_integer_bits_and_reject_the_biased_tail() {
        let mut below = TestWorld::with_numbers(&[0.5]);
        assert_eq!(below.random_int(0, (1i64 << 53) - 2), Ok((1i64 << 52) - 1));
        assert_eq!(below.next, 1);

        let mut at = TestWorld::with_integer_values(&[1u64 << 52]);
        assert_eq!(at.random_int(0, (1i64 << 53) - 1), Ok(1i64 << 52));
        assert_eq!(at.next, 0);

        let mut above = TestWorld::with_integer_values(&[u64::MAX, 1u64 << 53]);
        assert_eq!(above.random_int(0, 1i64 << 53), Ok(1i64 << 53));
        assert_eq!(above.next_integer, 2, "the out-of-range 54-bit draw was rejected");
    }

    #[test]
    fn a_random_int_spans_the_full_i64_range_without_signed_overflow() {
        let mut w = TestWorld::with_integer_values(&[0, u64::MAX]);
        assert_eq!(w.random_int(i64::MIN, i64::MAX), Ok(i64::MIN));
        assert_eq!(w.random_int(i64::MIN, i64::MAX), Ok(i64::MAX));
        assert_eq!(w.next_integer, 2);
    }
}
