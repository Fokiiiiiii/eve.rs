//! What the tasks ask of the world they act in: the server's ballpark and what hangs on it. The client's `behaviors` package calls the server's
//! `ballpark`, `dogmaLM`, `entityLocation` and the like directly and none of them are part of the client; this trait is those calls, grown as the
//! tasks were ported, with the names of the client's methods in snake case.
//!
//! Every method has an answer for a world that has nothing of the kind (`NullWorld`), so a test needs to say only what it uses.

/// The simulation clock's unit: ticks of `gametime` in a second (`SEC` of the client's `const`).
pub const SECOND: i64 = 10_000_000;

/// Seconds (as a task attribute gives them) in ticks of the clock.
pub fn seconds_to_ticks(seconds: f64) -> i64 {
    (seconds * SECOND as f64).round() as i64
}

/// The world a behavior tree acts in.
pub trait World {
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

    // ---- the ballpark: what a ship can be made to do to itself ----

    /// `ballpark.MakeInvulnerablePermanently(ballId, reason)`.
    fn make_invulnerable_permanently(&mut self, _ball_id: i64, _reason: &str) {}

    /// `ballpark.CancelCurrentInvulnerability(ballId, reason)`.
    fn cancel_current_invulnerability(&mut self, _ball_id: i64, _reason: &str) {}

    /// `ballpark.CloakShip(itemId)`.
    fn cloak_ship(&mut self, _item_id: i64) {}

    /// `ballpark.UncloakShip(itemId)`.
    fn uncloak_ship(&mut self, _item_id: i64) {}
}

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

impl World for NullWorld {
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

/// A world that writes down what it was asked, and answers its random numbers from a list (the last one over and over when it runs out): for tests.
#[derive(Debug, Default)]
pub struct RecordingWorld {
    pub calls: Vec<String>,
    pub numbers: Vec<f64>,
    next: usize,
}

impl RecordingWorld {
    pub fn with_numbers(numbers: &[f64]) -> Self {
        Self { calls: Vec::new(), numbers: numbers.to_vec(), next: 0 }
    }

    pub fn take_calls(&mut self) -> Vec<String> {
        std::mem::take(&mut self.calls)
    }
}

impl World for RecordingWorld {
    fn random(&mut self) -> f64 {
        let value = self.numbers.get(self.next).or(self.numbers.last()).copied().unwrap_or(0.0);
        self.next += 1;
        value
    }

    fn make_invulnerable_permanently(&mut self, ball_id: i64, reason: &str) {
        self.calls.push(format!("make_invulnerable_permanently({ball_id}, {reason})"));
    }

    fn cancel_current_invulnerability(&mut self, ball_id: i64, reason: &str) {
        self.calls.push(format!("cancel_current_invulnerability({ball_id}, {reason})"));
    }

    fn cloak_ship(&mut self, item_id: i64) {
        self.calls.push(format!("cloak_ship({item_id})"));
    }

    fn uncloak_ship(&mut self, item_id: i64) {
        self.calls.push(format!("uncloak_ship({item_id})"));
    }
}

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
        let mut w = RecordingWorld::with_numbers(&[0.0, 0.999_999]);
        assert_eq!(w.random_int(3, 5), 3);
        assert_eq!(w.random_int(3, 5), 5);
        let mut w = RecordingWorld::with_numbers(&[0.999_999_9]);
        assert_eq!(w.random_index(4), 3);
        assert_eq!(seconds_to_ticks(1.5), 15_000_000);
    }
}
