//! How a ship warps: where it comes out, how long each phase takes and where it is at each moment.
//!
//! The warp is the destiny kernel's own (Carbon's `Ballpark` warp: an exponential speed-up, a cruise if the distance is long enough and an
//! exponential slowing down until the speed falls below what a ship can drop out at), so the numbers here are the ones the client works out
//! for itself from `WarpTo`. Where a warp to a planet, a sun or another big celestial ends is the client's own rule too (a point worked out
//! from the body's place and size with a random number seeded by its id, Python's Mersenne twister), which the server has to reproduce or the
//! client's ball and the server's would land in different places.

use eve_destiny::Vec3;

use crate::world::{Body, BodyKind};

/// Metres in an astronomical unit.
pub const ONE_AU: f64 = 149_597_870_700.0;
/// The nearest a warp may end to where the ship starts (the service's rule: a warp of less is not offered).
pub const MINIMUM_WARP_DISTANCE: f64 = 150_000.0;
/// The share of its top speed a ship needs before it may enter warp.
pub const ENTRY_SPEED_FRACTION: f64 = 0.75;
/// How far off dead ahead the ship's heading may be (as `1 - cosine`) when it enters warp.
pub const ENTRY_ALIGNMENT: f64 = 0.01;
/// What the ship's top speed is set to at the start of a warp, as a share of what it was.
pub const START_SPEED_SCALE: f64 = 1.1;
/// The speed under which a ship that has dropped out of warp counts as at rest.
pub const REST_SPEED: f64 = 0.01;
/// The fastest a ship may be going when it drops out of warp, in metres a second.
const DROPOUT_SPEED_MAX: f64 = 100.0;
/// How much faster the warp speed builds than its factor says, and how much slower it is shed (per second, per unit of factor).
const ACCEL_PER_FACTOR: f64 = 1.0 / 1000.0;
const DECEL_PER_FACTOR: f64 = 1.0 / 3000.0;
const DECEL_RATE_MAX: f64 = 2.0;
/// The smallest body the client works out a warp-in point for.
const MIN_WARP_IN_RADIUS: f64 = 90_000.0;

const GROUP_SUN: i32 = 6;
const GROUP_PLANET: i32 = 7;
const GROUP_ASTEROID_BELT: i32 = 9;
const GROUP_WRECK: i32 = 186;
const CATEGORY_CELESTIAL: i32 = 2;

// ─── Python's random ────────────────────────────────────────────────────────

/// Python's `random.Random(seed)`: the Mersenne twister seeded the way CPython seeds it from an integer.
#[derive(Debug, Clone)]
pub struct PythonRandom {
    state: [u32; 624],
    index: usize,
}

impl PythonRandom {
    pub fn new(seed: u64) -> Self {
        let mut key = vec![(seed & 0xffff_ffff) as u32];
        if seed >> 32 != 0 {
            key.push((seed >> 32) as u32);
        }
        let mut random = Self { state: [0; 624], index: 624 };
        random.init_by_array(&key);
        random
    }

    fn init_genrand(&mut self, seed: u32) {
        self.state[0] = seed;
        for i in 1..624 {
            let previous = self.state[i - 1] ^ (self.state[i - 1] >> 30);
            self.state[i] = 1_812_433_253u32.wrapping_mul(previous).wrapping_add(i as u32);
        }
    }

    fn init_by_array(&mut self, key: &[u32]) {
        self.init_genrand(19_650_218);
        let (mut i, mut j) = (1usize, 0usize);
        for _ in 0..624.max(key.len()) {
            let previous = self.state[i - 1] ^ (self.state[i - 1] >> 30);
            self.state[i] = (self.state[i] ^ previous.wrapping_mul(1_664_525))
                .wrapping_add(key[j])
                .wrapping_add(j as u32);
            i += 1;
            j += 1;
            if i >= 624 {
                self.state[0] = self.state[623];
                i = 1;
            }
            if j >= key.len() {
                j = 0;
            }
        }
        for _ in 0..623 {
            let previous = self.state[i - 1] ^ (self.state[i - 1] >> 30);
            self.state[i] = (self.state[i] ^ previous.wrapping_mul(1_566_083_941)).wrapping_sub(i as u32);
            i += 1;
            if i >= 624 {
                self.state[0] = self.state[623];
                i = 1;
            }
        }
        self.state[0] = 0x8000_0000;
        self.index = 624;
    }

    fn next_u32(&mut self) -> u32 {
        if self.index >= 624 {
            for k in 0..624 {
                let y = (self.state[k] & 0x8000_0000) | (self.state[(k + 1) % 624] & 0x7fff_ffff);
                let mut next = self.state[(k + 397) % 624] ^ (y >> 1);
                if y & 1 != 0 {
                    next ^= 0x9908_b0df;
                }
                self.state[k] = next;
            }
            self.index = 0;
        }
        let mut y = self.state[self.index];
        self.index += 1;
        y ^= y >> 11;
        y ^= (y << 7) & 0x9d2c_5680;
        y ^= (y << 15) & 0xefc6_0000;
        y ^ (y >> 18)
    }

    /// The next number in `[0, 1)` with 53 bits, as `random.random()` gives it.
    pub fn random(&mut self) -> f64 {
        let a = f64::from(self.next_u32() >> 5);
        let b = f64::from(self.next_u32() >> 6);
        (a * 67_108_864.0 + b) / 9_007_199_254_740_992.0
    }
}

// ─── where a warp ends ──────────────────────────────────────────────────────

fn is_asteroid_belt(body: &Body) -> bool {
    body.kind == BodyKind::Belt || body.group_id == GROUP_ASTEROID_BELT
}

/// Whether the client works out a point of its own to warp in at for this body (the big celestials).
fn has_warp_in_point(body: &Body) -> bool {
    !is_asteroid_belt(body)
        && body.radius >= MIN_WARP_IN_RADIUS
        && body.group_id != GROUP_WRECK
        && if body.category_id > 0 { body.category_id == CATEGORY_CELESTIAL } else { false }
}

fn sun_point(body: &Body) -> Vec3 {
    let radius = body.radius;
    Vec3::new(
        body.position.x + (radius + 100_000.0) * radius.cos(),
        body.position.y + radius / 5.0,
        body.position.z - (radius + 100_000.0) * radius.sin(),
    )
}

fn planet_point(body: &Body) -> Option<Vec3> {
    let radius = body.radius;
    let (dx, dz) = (body.position.x, -body.position.z);
    let denominator = (dx * dx + dz * dz).sqrt();
    if !denominator.is_finite() || denominator <= 0.0 || radius <= 0.0 {
        return None;
    }
    let mut f = dz / denominator;
    if dz != 0.0 && dx > 0.0 {
        f = -f;
    }
    let random = (PythonRandom::new(body.id.unsigned_abs()).random() - 1.0) / 3.0;
    let theta = f.asin() + random;
    let factor = 20.0;
    let mut spread = ((factor - 5.0 * (radius / 1_000_000.0).log10() - 0.5) / factor).powf(factor) * factor;
    spread = spread.clamp(0.0, 10.0) + 0.5;
    let distance = radius + 1_000_000.0 + radius * spread;
    Some(Vec3::new(
        body.position.x + theta.sin() * distance,
        body.position.y + radius * random.sin() * 0.5,
        body.position.z - theta.cos() * distance,
    ))
}

fn generic_point(body: &Body) -> Vec3 {
    let radius = body.radius;
    Vec3::new(
        body.position.x + (radius + 5_000_000.0) * radius.cos(),
        body.position.y + 1.3 * radius - 7_500.0,
        body.position.z - (radius + 5_000_000.0) * radius.sin(),
    )
}

/// The point the client warps in at for a big celestial (a planet, a sun, a big moon), or None for a body it has no such point for.
pub fn warp_in_point(body: &Body) -> Option<Vec3> {
    if !has_warp_in_point(body) {
        return None;
    }
    match body.group_id {
        GROUP_PLANET => planet_point(body),
        GROUP_SUN => Some(sun_point(body)),
        _ => Some(generic_point(body)),
    }
}

/// How far short of its place a ship is to stop when it warps to a body it has no warp-in point for, `range` being what the pilot asked for.
pub fn stop_distance(body: &Body, ship_radius: f64, range: f64) -> f64 {
    let range = range.max(0.0);
    let radius = body.radius.max(0.0);
    let ship = ship_radius * 2.0;
    match body.kind {
        BodyKind::Station | BodyKind::Structure => radius + range + ship,
        BodyKind::Belt => 2_500.0f64.max(range) + ship,
        _ => match body.group_id {
            GROUP_PLANET | 8 => (radius + 1_000_000.0).max(range) + ship,
            GROUP_SUN => (radius + 5_000_000.0).max(range) + ship,
            _ => 1_000.0f64.max(radius).max(range) + ship,
        },
    }
}

// ─── the phases ─────────────────────────────────────────────────────────────

/// A warp worked out from its length: how the speed builds, holds and falls, and where the ship is at each moment.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Profile {
    pub total_distance: f64,
    pub warp_speed_au: f64,
    /// The warp factor the client is told (a thousand times the speed in AU a second).
    pub factor: i64,
    pub accel_rate: f64,
    pub decel_rate: f64,
    /// The speed the ship reaches: the warp speed, or less when the warp is too short to reach it.
    pub max_speed: f64,
    pub cruise_speed: f64,
    pub dropout_speed: f64,
    pub accel_distance: f64,
    pub cruise_distance: f64,
    pub accel_seconds: f64,
    pub cruise_seconds: f64,
}

/// Where the ship is in a warp `elapsed` seconds after it began.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Progress {
    /// Metres along the line to the end of the warp.
    pub traveled: f64,
    pub speed: f64,
    /// The speed has fallen below what a ship can drop out at: the warp is over.
    pub complete: bool,
}

impl Profile {
    /// The warp of a ship whose top speed is `max_velocity` and whose warp speed is `warp_speed_au` over `total_distance` metres.
    pub fn new(total_distance: f64, warp_speed_au: f64, max_velocity: f64) -> Self {
        let warp_speed_au = if warp_speed_au > 0.0 { warp_speed_au } else { 3.0 };
        let factor = ((warp_speed_au * 1000.0).round().max(1.0)) as i64;
        let warp_speed_au = factor as f64 / 1000.0;
        let cruise_speed = warp_speed_au * ONE_AU;
        let accel_rate = (factor as f64 * ACCEL_PER_FACTOR).max(0.001);
        let decel_rate = (factor as f64 * DECEL_PER_FACTOR).clamp(0.001, DECEL_RATE_MAX);
        // a short warp never reaches the warp speed: the speed builds and falls over exactly the distance
        let limited =
            ((total_distance + 1.0) * accel_rate * decel_rate) / (accel_rate + decel_rate).max(0.001);
        let max_speed = cruise_speed.min(limited).max(0.0);
        let accel_distance = (max_speed / accel_rate).max(0.0);
        let decel_distance = (max_speed / decel_rate - 1.0).max(0.0);
        let cruise_distance = (total_distance - accel_distance - decel_distance).max(0.0);
        Self {
            total_distance,
            warp_speed_au,
            factor,
            accel_rate,
            decel_rate,
            max_speed,
            cruise_speed,
            dropout_speed: (max_velocity / 2.0).clamp(0.0, DROPOUT_SPEED_MAX),
            accel_distance,
            cruise_distance,
            accel_seconds: (max_speed / accel_rate).max(1.0).ln() / accel_rate,
            cruise_seconds: if max_speed > 0.0 { cruise_distance / max_speed } else { 0.0 },
        }
    }

    /// Where the ship is, and how fast it goes, `elapsed` seconds into the warp. `activation_speed` is the speed it had when it entered warp (it is
    /// never slower than that while it builds speed).
    pub fn progress(&self, elapsed: f64, activation_speed: f64) -> Progress {
        let elapsed = elapsed.max(0.0);
        if elapsed < self.accel_seconds {
            let base = self.max_speed.min(self.accel_rate * (self.accel_rate * elapsed).exp());
            return Progress {
                traveled: self.accel_distance.min((base / self.accel_rate).max(0.0)),
                speed: base.max(activation_speed),
                complete: false,
            };
        }
        if elapsed < self.accel_seconds + self.cruise_seconds {
            let seconds = elapsed - self.accel_seconds;
            return Progress {
                traveled: self.accel_distance + self.max_speed * seconds,
                speed: self.max_speed,
                complete: false,
            };
        }
        let seconds = (elapsed - self.accel_seconds - self.cruise_seconds).max(0.0);
        let speed = (self.max_speed * (-self.decel_rate * seconds).exp()).max(0.0);
        Progress {
            traveled: self.accel_distance
                + self.cruise_distance
                + ((self.max_speed - speed) / self.decel_rate).max(0.0),
            speed,
            complete: self.dropout_speed > 0.0 && speed < self.dropout_speed,
        }
    }

    /// The second of the warp at which the ship drops out: the speed has fallen to the dropout speed.
    pub fn dropout_seconds(&self) -> f64 {
        let decel = if self.dropout_speed > 0.0 && self.max_speed > self.dropout_speed {
            (self.max_speed / self.dropout_speed).ln() / self.decel_rate
        } else {
            0.0
        };
        self.accel_seconds + self.cruise_seconds + decel
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn python_s_random_is_reproduced_for_seeds_of_one_and_two_words() {
        // random.Random(seed).random(), from CPython
        for (seed, expected) in [
            (0u64, 0.8444218515250481),
            (1, 0.13436424411240122),
            (42, 0.6394267984578837),
            (40_491_233, 0.6982988071690184),
            (60_015_249, 0.8074333179616849),
            (4_294_967_296, 0.11299430095636409),
            (1_099_511_640_121, 0.31036222241287226),
            (30_000_142, 0.05226472064535026),
        ] {
            assert_eq!(PythonRandom::new(seed).random(), expected, "seed {seed}");
        }
        let mut random = PythonRandom::new(40_491_233);
        assert_eq!(
            [random.random(), random.random(), random.random()],
            [0.6982988071690184, 0.43264158720392076, 0.08769548440226826]
        );
    }

    fn planet() -> Body {
        Body {
            id: 40_491_233,
            type_id: 2015,
            group_id: GROUP_PLANET,
            category_id: CATEGORY_CELESTIAL,
            owner_id: 0,
            name: "Planet".to_string(),
            kind: BodyKind::Planet,
            position: Vec3::new(68_738_000_000.0, -2_880_000_000.0, -29_199_000_000.0),
            radius: 3_740_000.0,
            graphic_id: None,
            detail: crate::world::Detail::None,
        }
    }

    #[test]
    fn a_planet_is_warped_to_at_a_point_that_depends_on_its_id_and_size() {
        let body = planet();
        let a = warp_in_point(&body).unwrap();
        assert_eq!(a, warp_in_point(&body).unwrap(), "the same every time");
        let distance = ((a.x - body.position.x).powi(2) + (a.z - body.position.z).powi(2)).sqrt();
        assert!(distance > body.radius + 1_000_000.0 && distance < body.radius * 12.0, "{distance}");
        let mut other = body.clone();
        other.id += 1;
        assert_ne!(warp_in_point(&other), Some(a), "another body, another point");
    }

    #[test]
    fn a_sun_and_a_big_moon_have_the_points_the_client_works_out_for_them() {
        // worked out apart from this code, from the rule
        let mut sun = planet();
        sun.group_id = GROUP_SUN;
        sun.kind = BodyKind::Star;
        sun.position = Vec3::new(1.0e9, 2.0e9, -3.0e9);
        sun.radius = 1.2e9;
        let point = warp_in_point(&sun).unwrap();
        assert!((point.x - 703_551_825.4549474).abs() < 1e-3 && (point.y - 2_240_000_000.0).abs() < 1e-3);
        assert!((point.z + 1_837_090_506.6133022).abs() < 1e-3, "{point:?}");

        let mut moon = planet();
        moon.group_id = 8;
        moon.kind = BodyKind::Moon;
        moon.position = Vec3::new(5.0e10, -1.0e9, 7.0e10);
        moon.radius = 200_000.0;
        let point = warp_in_point(&moon).unwrap();
        assert!((point.x - 50_005_186_709.043816).abs() < 1e-3 && (point.y + 999_747_500.0).abs() < 1e-3);
        assert!((point.z - 70_000_371_549.855_1).abs() < 1e-3, "{point:?}");

        // a moon under 90 km, a wreck and a belt have none
        moon.radius = 89_999.0;
        assert_eq!(warp_in_point(&moon), None);
        moon.radius = 200_000.0;
        moon.group_id = GROUP_WRECK;
        assert_eq!(warp_in_point(&moon), None);
        moon.group_id = GROUP_ASTEROID_BELT;
        assert_eq!(warp_in_point(&moon), None);
    }

    #[test]
    fn a_body_the_client_has_no_point_for_gets_a_stop_distance_by_kind() {
        let mut station = planet();
        station.kind = BodyKind::Station;
        station.group_id = 15;
        station.radius = 1_000.0;
        assert_eq!(warp_in_point(&station), None, "too small for a point of the client's");
        assert_eq!(stop_distance(&station, 47.0, 0.0), 1_000.0 + 94.0);
        assert_eq!(stop_distance(&station, 47.0, 5_000.0), 1_000.0 + 5_000.0 + 94.0);
        let mut belt = station.clone();
        belt.kind = BodyKind::Belt;
        assert_eq!(stop_distance(&belt, 47.0, 0.0), 2_500.0 + 94.0);
        assert_eq!(stop_distance(&belt, 47.0, 9_000.0), 9_000.0 + 94.0);
    }

    #[test]
    fn a_short_warp_never_reaches_the_warp_speed_and_spends_exactly_its_length() {
        // the recorded warp: 2.047 AU at 3 AU a second, by a ship that goes 339.25 m/s
        let profile = Profile::new(3.0629e11, 3.0, 339.25);
        assert_eq!((profile.factor, profile.accel_rate, profile.decel_rate), (3000, 3.0, 1.0));
        assert!(profile.max_speed < profile.cruise_speed);
        assert!(profile.cruise_distance < 1.0e-3 * profile.total_distance);
        assert!((profile.accel_seconds - 8.35).abs() < 0.02, "{}", profile.accel_seconds);
        assert!((profile.dropout_seconds() - 29.9).abs() < 0.1, "{}", profile.dropout_seconds());
    }

    #[test]
    fn a_long_warp_cruises_at_the_warp_speed() {
        let profile = Profile::new(30.0 * ONE_AU, 3.0, 339.25);
        assert_eq!(profile.max_speed, profile.cruise_speed);
        assert!(profile.cruise_seconds > 5.0);
        let middle = profile.progress(profile.accel_seconds + profile.cruise_seconds / 2.0, 0.0);
        assert_eq!(middle.speed, profile.cruise_speed);
        assert!(!middle.complete);
    }

    #[test]
    fn the_ship_is_where_the_phases_put_it_and_drops_out_at_the_end() {
        let profile = Profile::new(3.0629e11, 3.0, 339.25);
        let start = profile.progress(0.0, 373.0);
        assert_eq!((start.traveled, start.speed, start.complete), (1.0, 373.0, false));
        let end = profile.progress(profile.dropout_seconds() + 0.5, 373.0);
        assert!(end.complete && end.speed < 100.0);
        // it has covered all but a few metres
        assert!(
            (profile.total_distance - end.traveled).abs() < 200.0,
            "{}",
            profile.total_distance - end.traveled
        );
        // never backwards, never faster than the peak
        let mut last = 0.0;
        for step in 0..=300 {
            let p = profile.progress(step as f64 / 10.0, 0.0);
            assert!(p.traveled >= last && p.speed <= profile.max_speed + 1.0);
            last = p.traveled;
        }
    }
}
