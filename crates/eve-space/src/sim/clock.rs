//! A continuous, monotonic rational clock for a scene. Times are FILETIME ticks (100 ns).
//!
//! This primitive is not yet wired to scene commands, polling, or client notifications.
//! Callers must rebase all scene deadlines and cross-scene transfers before activating it.

use std::collections::BTreeMap;

use crate::link::SceneKey;

/// Opt-in clock mappings by exact public scene key; private scenes always use wall time.
#[derive(Debug, Default)]
pub struct SceneClocks {
    clocks: BTreeMap<SceneKey, SceneClock>,
}

impl SceneClocks {
    /// Changes a public system's rate; private scenes cannot be configured by this API.
    pub fn set_public_rate(
        &mut self,
        scene: SceneKey,
        wall: i64,
        numerator: u32,
        denominator: u32,
    ) -> Result<i64, &'static str> {
        if !scene.is_valid() || scene.instance_id != 0 {
            return Err("public scene required");
        }
        if numerator == 0 || denominator == 0 {
            return Err("rate must be positive");
        }
        self.clocks.entry(scene).or_insert_with(|| SceneClock::new(wall)).set_rate(
            wall,
            numerator,
            denominator,
        )
    }

    /// Logical time for the exact scene, or unchanged wall time when not configured.
    pub fn observe(&mut self, scene: SceneKey, wall: i64) -> i64 {
        self.clocks.get_mut(&scene).map_or(wall, |clock| clock.observe(wall))
    }

    /// Converts a logical deadline to its earliest wall deadline for this exact scene.
    pub fn wall_deadline(&mut self, scene: SceneKey, wall: i64, deadline: i64) -> i64 {
        self.clocks.get_mut(&scene).map_or(deadline, |clock| {
            clock.observe(wall);
            clock.wall_deadline(deadline)
        })
    }
}

/// Rebase an active relative timer between logical domains; zero and MAX remain sentinels.
pub fn rebase_timer(value: i64, source_now: i64, destination_now: i64) -> i64 {
    if value == 0 || value == i64::MAX {
        return value;
    }
    (i128::from(value) + i128::from(destination_now) - i128::from(source_now))
        .clamp(i128::from(i64::MIN), i128::from(i64::MAX)) as i64
}

/// Continuous mapping from observed wall FILETIME to logical scene FILETIME.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SceneClock {
    wall: i64,
    logical: i64,
    numerator: u32,
    denominator: u32,
    remainder: u32,
}

impl SceneClock {
    /// Starts at wall time with a 1:1 rate.
    pub fn new(wall: i64) -> Self {
        Self { wall, logical: wall, numerator: 1, denominator: 1, remainder: 0 }
    }

    /// Advances on monotonic wall observations; a backward wall observation never rewinds it.
    pub fn observe(&mut self, wall: i64) -> i64 {
        let wall = wall.max(self.wall);
        let delta = i128::from(wall) - i128::from(self.wall);
        let scaled = delta * i128::from(self.numerator) + i128::from(self.remainder);
        let ticks = scaled / i128::from(self.denominator);
        let next = i128::from(self.logical) + ticks;
        self.wall = wall;
        if next >= i128::from(i64::MAX) {
            self.logical = i64::MAX;
            self.remainder = 0;
        } else {
            self.logical = next as i64;
            self.remainder = (scaled % i128::from(self.denominator)) as u32;
        }
        self.logical
    }

    /// Changes rate without rewinding or jumping the logical time. A sub-tick fraction is discarded.
    pub fn set_rate(&mut self, wall: i64, numerator: u32, denominator: u32) -> Result<i64, &'static str> {
        if numerator == 0 || denominator == 0 {
            return Err("rate must be positive");
        }
        let logical = self.observe(wall);
        self.numerator = numerator;
        self.denominator = denominator;
        self.remainder = 0;
        Ok(logical)
    }

    /// Earliest wall FILETIME at or after the current anchor when a logical deadline is reached.
    pub fn wall_deadline(&self, logical_deadline: i64) -> i64 {
        if logical_deadline <= self.logical {
            return self.wall;
        }
        let needed = (i128::from(logical_deadline) - i128::from(self.logical)) * i128::from(self.denominator)
            - i128::from(self.remainder);
        let numerator = i128::from(self.numerator);
        let ticks = (needed + numerator - 1) / numerator;
        (i128::from(self.wall) + ticks).min(i128::from(i64::MAX)) as i64
    }
}

#[cfg(test)]
mod tests {
    use super::{SceneClock, SceneClocks, rebase_timer};
    use crate::link::SceneKey;

    #[test]
    fn public_rate_is_opt_in_and_does_not_affect_private_scene() {
        let public = SceneKey::solar_system(30000142);
        let private = SceneKey { location_id: 30000142, instance_id: 77, room_index: 1 };
        let other = SceneKey::solar_system(30000144);
        let mut clocks = SceneClocks::default();
        assert_eq!(clocks.observe(public, 100), 100);
        assert_eq!(clocks.set_public_rate(public, 100, 3, 2), Ok(100));
        assert_eq!(clocks.set_public_rate(private, 100, 2, 1), Err("public scene required"));
        assert_eq!(clocks.observe(public, 120), 130);
        assert_eq!(clocks.observe(private, 120), 120);
        assert_eq!(clocks.observe(other, 120), 120);
        assert_eq!(clocks.wall_deadline(public, 120, 145), 130);
        assert_eq!(rebase_timer(150, 130, 120), 140);
        assert_eq!(rebase_timer(0, 130, 120), 0);
        assert_eq!(rebase_timer(i64::MAX, 130, 120), i64::MAX);
    }

    #[test]
    fn rate_changes_are_continuous_and_deadlines_use_the_inverse_rate() {
        let mut clock = SceneClock::new(1_000);
        assert_eq!(clock.observe(1_100), 1_100);
        assert_eq!(clock.set_rate(1_100, 2, 1), Ok(1_100));
        assert_eq!(clock.observe(1_125), 1_150);
        assert_eq!(clock.wall_deadline(1_200), 1_150);
        assert_eq!(clock.set_rate(1_125, 1, 2), Ok(1_150));
        assert_eq!(clock.observe(1_225), 1_200);
        assert_eq!(clock.wall_deadline(1_210), 1_245);
    }

    #[test]
    fn deadline_rounds_up_and_extreme_times_saturate() {
        let mut clock = SceneClock::new(100);
        clock.set_rate(100, 3, 2).unwrap();
        assert_eq!(clock.observe(101), 101);
        assert_eq!(clock.wall_deadline(103), 102);
        assert_eq!(clock.observe(102), 103);
        let mut near_limit = SceneClock::new(i64::MAX - 5);
        near_limit.set_rate(i64::MAX - 5, 7, 2).unwrap();
        assert_eq!(near_limit.observe(i64::MAX), i64::MAX);
        assert_eq!(near_limit.wall_deadline(i64::MAX), i64::MAX);
    }

    #[test]
    fn fractional_ticks_are_conserved_and_backward_wall_does_not_rewind() {
        let mut clock = SceneClock::new(10);
        clock.set_rate(10, 1, 2).unwrap();
        assert_eq!(clock.observe(11), 10);
        assert_eq!(clock.wall_deadline(11), 12);
        assert_eq!(clock.observe(9), 10);
        assert_eq!(clock.observe(12), 11);
        assert_eq!(clock.set_rate(13, 0, 1), Err("rate must be positive"));
        assert_eq!(clock.observe(14), 12);
    }
}
