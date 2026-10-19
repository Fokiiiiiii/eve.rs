//! A warp in progress: the ship aligns, enters warp, drops out and coasts to a stop, at the moments the destiny kernel's model gives.
//!
//! What the pilot sees is the client's own simulation of `WarpTo`; this is the server's copy of it (`crate::warp` has the numbers). It tells the
//! scene when the ship has dropped out and when it is at rest, so the scene can say so, and it answers where the ship is between those moments.
use eve_destiny::{DESTINY_SPACE_FRICTION, Vec3, integrate_carbon_motion};

use crate::motion;
use crate::ship::{Course, Ship};
use crate::warp::{ENTRY_ALIGNMENT, ENTRY_SPEED_FRACTION, Profile, REST_SPEED};

const TICKS_PER_SECOND: i64 = 10_000_000;
/// How long before it drops out of warp the client is told the ship is massless again (the reference's lead).
const DROPOUT_WARNING: f64 = 2.0;

/// The ship's warp: where it is going and how far along it is.
#[derive(Debug, Clone)]
pub struct Flight {
    raw_destination: Vec3,
    stop_distance: f64,
    phase: Phase,
}

#[derive(Debug, Clone)]
enum Phase {
    /// Turning to face the destination at speed; the entry is checked once a second.
    Aligning { next_check: i64 },
    /// In warp: the ship is where the profile puts it along the line from where it entered.
    Warping {
        started_at: i64,
        origin: Vec3,
        direction: Vec3,
        profile: Profile,
        activation_speed: f64,
        announced: bool,
    },
    /// Out of warp and slowing to a stop.
    Coasting { since: i64, position: Vec3, velocity: Vec3 },
}

/// Something that happened to the ship in a warp, for the scene to tell the client of.
#[derive(Debug, Clone, Copy, PartialEq)]
pub enum Milestone {
    /// The ship has lined up and entered warp: the ones watching it are told (the pilot was told when the warp was asked for).
    Entered { at: i64 },
    /// The ship is about to drop out (two seconds before it does): the client is told the ball is massless again.
    Dropout { at: i64 },
    /// The ship has slowed to a stop.
    Rest { at: i64, position: Vec3, velocity: Vec3 },
}

/// What moving the ship on did.
#[derive(Debug, Clone, PartialEq)]
pub struct Advance {
    pub milestones: Vec<Milestone>,
    /// The moment the warp ended, when it did within the time: the ship is at rest there and the scene takes it on as an ordinary stopped ship.
    pub finished_at: Option<i64>,
}

fn sub(a: Vec3, b: Vec3) -> Vec3 {
    Vec3::new(a.x - b.x, a.y - b.y, a.z - b.z)
}

fn length(a: Vec3) -> f64 {
    (a.x * a.x + a.y * a.y + a.z * a.z).sqrt()
}

fn seconds(ticks: i64) -> f64 {
    ticks as f64 / TICKS_PER_SECOND as f64
}

fn ticks(seconds: f64) -> i64 {
    (seconds * TICKS_PER_SECOND as f64).round() as i64
}

/// The way the ship is going for the purpose of lining up: its velocity when it has any to speak of, else where it faces.
fn heading(ship: &Ship) -> Vec3 {
    let speed = length(ship.velocity);
    if speed > 0.5f64.max(ship.spec.max_velocity * 0.01) {
        motion::unit(ship.velocity).unwrap_or(ship.direction)
    } else {
        ship.direction
    }
}

impl Flight {
    /// A warp asked for at `now`, to `raw_destination`, ending `stop_distance` short of it.
    pub fn begin(now: i64, raw_destination: Vec3, stop_distance: f64) -> Self {
        Self { raw_destination, stop_distance, phase: Phase::Aligning { next_check: now + TICKS_PER_SECOND } }
    }

    /// Whether the ship is in warp itself, not still lining up.
    pub fn is_warping(&self) -> bool {
        matches!(self.phase, Phase::Warping { .. })
    }

    /// The ship keeps flying at its top speed towards the destination while it lines up.
    fn fly(&self, ship: &mut Ship, span: i64) {
        if span <= 0 {
            return;
        }
        let wanted = motion::unit(sub(self.raw_destination, ship.position)).unwrap_or(ship.direction);
        ship.direction = wanted;
        motion::integrate_ship(ship, wanted, ship.spec.max_velocity, seconds(span));
    }

    fn lined_up(&self, ship: &Ship) -> bool {
        let Some(wanted) = motion::unit(sub(self.raw_destination, ship.position)) else { return true };
        let way = heading(ship);
        let alignment = way.x * wanted.x + way.y * wanted.y + way.z * wanted.z;
        let fraction = (length(ship.velocity) / ship.spec.max_velocity.max(0.001)).clamp(0.0, 1.0);
        (1.0 - alignment).abs() < ENTRY_ALIGNMENT && fraction > ENTRY_SPEED_FRACTION
    }

    /// Moves the ship on from `from` to `to`: it stands where the warp puts it at `to` (unless the warp ended: then at the moment it ended).
    pub fn advance(&mut self, ship: &mut Ship, from: i64, to: i64) -> Advance {
        let mut milestones = Vec::new();
        let mut at = from;
        loop {
            match &mut self.phase {
                Phase::Aligning { next_check } => {
                    if *next_check > to {
                        let span = to - at;
                        let this = self.clone();
                        this.fly(ship, span);
                        return Advance { milestones, finished_at: None };
                    }
                    let check = *next_check;
                    let this = self.clone();
                    this.fly(ship, check - at);
                    at = check;
                    if this.lined_up(ship) {
                        let toward =
                            motion::unit(sub(self.raw_destination, ship.position)).unwrap_or(ship.direction);
                        let target_point = Vec3::new(
                            self.raw_destination.x - toward.x * self.stop_distance,
                            self.raw_destination.y - toward.y * self.stop_distance,
                            self.raw_destination.z - toward.z * self.stop_distance,
                        );
                        let total = length(sub(target_point, ship.position));
                        let profile = Profile::new(total, ship.spec.warp_speed_au, ship.spec.max_velocity);
                        let direction = motion::unit(sub(target_point, ship.position)).unwrap_or(toward);
                        ship.course = Course::Warp {
                            raw_destination: self.raw_destination,
                            target_point,
                            stop_distance: self.stop_distance,
                            total_distance: total,
                            factor: profile.factor,
                            active: true,
                        };
                        milestones.push(Milestone::Entered { at: check });
                        self.phase = Phase::Warping {
                            started_at: check,
                            origin: ship.position,
                            direction,
                            profile,
                            activation_speed: length(ship.velocity),
                            announced: false,
                        };
                    } else if let Phase::Aligning { next_check } = &mut self.phase {
                        *next_check += TICKS_PER_SECOND;
                    }
                }
                Phase::Warping { started_at, origin, direction, profile, activation_speed, announced } => {
                    let heads_up =
                        *started_at + ticks((profile.dropout_seconds() - DROPOUT_WARNING).max(0.0));
                    if !*announced && to >= heads_up {
                        *announced = true;
                        milestones.push(Milestone::Dropout { at: heads_up });
                    }
                    let elapsed = seconds(to - *started_at);
                    let progress = profile.progress(elapsed, *activation_speed);
                    if !progress.complete {
                        ship.position = Vec3::new(
                            origin.x + direction.x * progress.traveled,
                            origin.y + direction.y * progress.traveled,
                            origin.z + direction.z * progress.traveled,
                        );
                        ship.velocity = Vec3::new(
                            direction.x * progress.speed,
                            direction.y * progress.speed,
                            direction.z * progress.speed,
                        );
                        ship.direction = *direction;
                        return Advance { milestones, finished_at: None };
                    }
                    let dropout = profile.dropout_seconds();
                    let at_dropout = (*started_at + ticks(dropout)).min(to);
                    let there = profile.progress(dropout, *activation_speed);
                    let position = Vec3::new(
                        origin.x + direction.x * there.traveled,
                        origin.y + direction.y * there.traveled,
                        origin.z + direction.z * there.traveled,
                    );
                    let velocity = Vec3::new(
                        direction.x * there.speed,
                        direction.y * there.speed,
                        direction.z * there.speed,
                    );
                    ship.direction = *direction;
                    self.phase = Phase::Coasting { since: at_dropout, position, velocity };
                    at = at_dropout;
                }
                Phase::Coasting { since, position, velocity } => {
                    let inertial_mass = ship.spec.mass * ship.spec.agility;
                    let lambda = DESTINY_SPACE_FRICTION / inertial_mass.max(1e-9);
                    let speed = length(*velocity);
                    let rest_after = if speed > REST_SPEED && lambda > 0.0 {
                        (speed / REST_SPEED).ln() / lambda
                    } else {
                        0.0
                    };
                    let rest_at = *since + ticks(rest_after);
                    let until = to.min(rest_at);
                    let motion = integrate_carbon_motion(
                        *position,
                        *velocity,
                        Vec3::ZERO,
                        inertial_mass,
                        DESTINY_SPACE_FRICTION,
                        seconds(until - *since),
                    );
                    ship.position = motion.position;
                    ship.velocity = motion.velocity;
                    if to < rest_at {
                        return Advance { milestones, finished_at: None };
                    }
                    milestones.push(Milestone::Rest {
                        at: rest_at,
                        position: motion.position,
                        velocity: motion.velocity,
                    });
                    return Advance { milestones, finished_at: Some(rest_at) };
                }
            }
        }
    }
}
