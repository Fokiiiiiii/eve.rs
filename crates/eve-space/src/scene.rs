//! A running solar system: the ships in it, what commands do to them, and what the client is told (ADR 0007).
//!
//! The scene has no clock of its own. Every call says what time it is (a FILETIME) and a ship's motion is worked out
//! from the time it was last settled to that time, so the same calls at the same times give the same bytes.

use std::collections::BTreeMap;
use std::sync::Arc;

use eve_destiny::{DESTINY_SPACE_FRICTION, Vec3, integrate_carbon_motion};
use eve_marshal::Value;

use crate::payload;
use crate::ship::{Course, Ship, ShipSpec};
use crate::wire::{filetime, id, int, real, tuple, vector};
use crate::world::{BodyKind, SolarSystem};

/// The host's name for a player's connection.
pub type SessionId = u64;

/// FILETIME ticks in a second.
const TICKS_PER_SECOND: i64 = 10_000_000;
/// Seconds between 1601 and 1970.
const UNIX_EPOCH_SECONDS: i64 = 11_644_473_600;
/// How long a docking takes from the moment it is accepted.
const DOCKING_DELAY: i64 = 4 * TICKS_PER_SECOND;
/// How close (from surface to surface) a ship has to be to dock.
const DOCKING_RANGE: f64 = 2_500.0;
/// How far ahead of the clock a stamp may be sent (the client holds updates until their stamp).
const MAX_STAMP_LEAD: u32 = 2;
/// How far from the station's centre a ship is put, as a share of the station's radius (the reference's recorded ship left the station
/// at 0.34 of it, inside the sphere the station's docking range is measured from), and the least it is put from it.
const UNDOCK_RADIUS_SHARE: f64 = 0.34;
const UNDOCK_MIN_DISTANCE: f64 = 2_000.0;

/// What a player can ask of their ship (the numbers of the client's `CmdXxx` calls).
#[derive(Debug, Clone, Copy, PartialEq)]
pub enum Command {
    SetSpeedFraction(f64),
    GotoDirection(Vec3),
    Stop,
    Dock { station_id: i64 },
}

impl Command {
    /// The code of the command on the link (ADR 0007).
    pub fn code(self) -> u8 {
        match self {
            Self::SetSpeedFraction(_) => 1,
            Self::GotoDirection(_) => 2,
            Self::Stop => 3,
            Self::Dock { .. } => 4,
        }
    }
}

/// Why a ship leaves a scene.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Departure {
    Docked,
    LoggedOff,
    Jumped,
}

/// What the scene has to tell the host.
#[derive(Debug, Clone, PartialEq)]
pub enum Out {
    /// A notification for the player's client; the host numbers it and sends it.
    Notify { session: SessionId, name: &'static str, id_type: &'static str, args: Vec<Value> },
    /// A `Dock` was accepted (the call is answered with the time).
    Accepted { session: SessionId, command: u8, at: i64 },
    /// The ship has docked: the host moves the character into the station.
    Docked { session: SessionId, station_id: i64 },
    /// A command the scene cannot do.
    Refused { session: SessionId, command: u8, reason: &'static str },
}

/// Why the scene cannot take a ship.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum SceneError {
    UnknownStation(i64),
    AlreadyInSpace(SessionId),
}

#[derive(Debug, Clone)]
struct PendingDock {
    station_id: i64,
    accepted_at: i64,
}

#[derive(Debug, Clone)]
struct Pilot {
    ship: Ship,
    /// The moment the ship's position and velocity are those of.
    settled_at: i64,
    docking: Option<PendingDock>,
}

/// One solar system with the ships in it.
#[derive(Debug)]
pub struct Scene {
    system: Arc<SolarSystem>,
    pilots: BTreeMap<SessionId, Pilot>,
    next_stamp: u32,
}

/// The destiny stamp of a moment: whole seconds of the Unix clock.
pub fn stamp_at(now: i64) -> u32 {
    (now.div_euclid(TICKS_PER_SECOND) - UNIX_EPOCH_SECONDS) as u32
}

impl Scene {
    pub fn new(system: Arc<SolarSystem>) -> Self {
        Self { system, pilots: BTreeMap::new(), next_stamp: 0 }
    }

    pub fn system(&self) -> &SolarSystem {
        &self.system
    }

    pub fn is_empty(&self) -> bool {
        self.pilots.is_empty()
    }

    pub fn contains(&self, session: SessionId) -> bool {
        self.pilots.contains_key(&session)
    }

    /// The ship of a session, as it is at `now`.
    pub fn ship(&mut self, session: SessionId, now: i64) -> Option<&Ship> {
        let pilot = self.pilots.get_mut(&session)?;
        settle(pilot, now);
        Some(&pilot.ship)
    }

    /// The next stamp a change can carry: one after the last, but never more than two ahead of the clock, and never behind it.
    fn next_stamp(&mut self, now: i64) -> u32 {
        let current = stamp_at(now);
        self.next_stamp = if self.next_stamp < current {
            current
        } else if self.next_stamp - current >= MAX_STAMP_LEAD {
            current + MAX_STAMP_LEAD
        } else {
            self.next_stamp + 1
        };
        self.next_stamp
    }

    /// Puts a ship in space at the station it undocks from, moving away at its top speed.
    pub fn undock(
        &mut self,
        session: SessionId,
        spec: ShipSpec,
        station_id: i64,
        now: i64,
    ) -> Result<(), SceneError> {
        if self.pilots.contains_key(&session) {
            return Err(SceneError::AlreadyInSpace(session));
        }
        let station = self
            .system
            .body(station_id)
            .filter(|b| b.kind == BodyKind::Station)
            .ok_or(SceneError::UnknownStation(station_id))?;
        let direction = undock_direction(station.id, station.position);
        let distance = UNDOCK_MIN_DISTANCE.max(station.radius * UNDOCK_RADIUS_SHARE);
        let position = Vec3::new(
            station.position.x + direction.x * distance,
            station.position.y + direction.y * distance,
            station.position.z + direction.z * distance,
        );
        let speed = spec.max_velocity;
        let ship = Ship {
            position,
            velocity: Vec3::new(direction.x * speed, direction.y * speed, direction.z * speed),
            direction,
            speed_fraction: 1.0,
            course: Course::Goto,
            spec,
        };
        self.pilots.insert(session, Pilot { ship, settled_at: now, docking: None });
        Ok(())
    }

    /// The ship leaves the scene (docked, gone offline, jumped).
    pub fn leave(&mut self, session: SessionId, _why: Departure) -> bool {
        self.pilots.remove(&session).is_some()
    }

    fn notify(session: SessionId, name: &'static str, args: Vec<Value>) -> Out {
        Out::Notify { session, name, id_type: "clientID", args }
    }

    /// The client bound the system's ballpark: it is told the time, and given its ship and then everything around it.
    pub fn bind(&mut self, session: SessionId, now: i64) -> Vec<Out> {
        let first = self.next_stamp(now);
        let second = self.next_stamp(now);
        let system = Arc::clone(&self.system);
        let Some(pilot) = self.pilots.get_mut(&session) else { return Vec::new() };
        settle(pilot, now);
        let ship = &pilot.ship;
        let ship_id = id(ship.spec.ship_id);
        let heading = |action_stamp: u32| match ship.course {
            Course::Goto => payload::action(
                action_stamp,
                "GotoDirection",
                with_id(&ship_id, vector(ship.direction.x, ship.direction.y, ship.direction.z)),
            ),
            Course::Stop => payload::action(action_stamp, "Stop", [ship_id.clone()]),
        };

        let Ok(added) = payload::add_ship(first, ship, now) else { return Vec::new() };
        let Ok(state) = payload::set_state(second, &system, ship, now) else { return Vec::new() };
        vec![
            Self::notify(session, "OnSetTimeDilation", vec![int(1), int(1), int(100_000_000)]),
            Self::notify(session, "DoSimClockRebase", vec![tuple([filetime(now), filetime(now)])]),
            Self::notify(session, "DoDestinyUpdate", payload::destiny_update([added], true)),
            Self::notify(
                session,
                "DoDestinyUpdate",
                payload::destiny_update(
                    [
                        payload::uncloak(first, ship.spec.ship_id),
                        payload::action(
                            first,
                            "SetBallPosition",
                            with_id(&ship_id, vector(ship.position.x, ship.position.y, ship.position.z)),
                        ),
                        payload::action(first, "SetBallMassive", [ship_id.clone(), int(0)]),
                        payload::action(first, "SetBallMass", [ship_id.clone(), real(ship.spec.mass)]),
                        payload::action(
                            first,
                            "SetBallVelocity",
                            with_id(&ship_id, vector(ship.velocity.x, ship.velocity.y, ship.velocity.z)),
                        ),
                        heading(first),
                    ],
                    false,
                ),
            ),
            Self::notify(session, "DoDestinyUpdate", payload::destiny_update([state], false)),
            Self::notify(
                session,
                "DoDestinyUpdate",
                payload::destiny_update(
                    [
                        payload::action(second, "SetBallAgility", [ship_id.clone(), real(ship.spec.agility)]),
                        payload::action(second, "SetMaxSpeed", [ship_id, real(ship.spec.max_velocity)]),
                    ],
                    false,
                ),
            ),
        ]
    }

    /// The client asked for the state again (`UpdateStateRequest`): every ball as it is now.
    pub fn update_state(&mut self, session: SessionId, now: i64) -> Vec<Out> {
        let stamp = self.next_stamp(now);
        let system = Arc::clone(&self.system);
        let Some(pilot) = self.pilots.get_mut(&session) else { return Vec::new() };
        settle(pilot, now);
        match payload::set_state(stamp, &system, &pilot.ship, now) {
            Ok(state) => {
                vec![Self::notify(session, "DoDestinyUpdate", payload::destiny_update([state], false))]
            }
            Err(_) => Vec::new(),
        }
    }

    /// A command of the player's. Movement is applied from `now`; what the client is told is one update at the next stamp.
    pub fn command(&mut self, session: SessionId, command: Command, now: i64) -> Vec<Out> {
        let code = command.code();
        if !self.pilots.contains_key(&session) {
            return vec![Out::Refused { session, command: code, reason: "NO_SHIP_IN_SPACE" }];
        }
        if let Command::Dock { station_id } = command {
            return self.dock(session, station_id, now);
        }
        let stamp = self.next_stamp(now);
        let pilot = self.pilots.get_mut(&session).expect("checked above");
        if pilot.docking.is_some() {
            // a ship that is docking follows no orders
            return Vec::new();
        }
        settle(pilot, now);
        let ship = &mut pilot.ship;
        let ship_id = id(ship.spec.ship_id);
        let change = match command {
            Command::SetSpeedFraction(fraction) => {
                ship.speed_fraction = if fraction.is_finite() { fraction.clamp(0.0, 1.0) } else { 0.0 };
                ship.course = if ship.speed_fraction > 0.0 { Course::Goto } else { Course::Stop };
                payload::action(stamp, "SetSpeedFraction", [ship_id, real(ship.speed_fraction)])
            }
            Command::GotoDirection(direction) => {
                let Some(unit) = unit(direction) else {
                    return vec![Out::Refused { session, command: code, reason: "NO_DIRECTION" }];
                };
                ship.direction = unit;
                ship.course = Course::Goto;
                payload::action(stamp, "GotoDirection", with_id(&ship_id, vector(unit.x, unit.y, unit.z)))
            }
            Command::Stop => {
                ship.speed_fraction = 0.0;
                ship.course = Course::Stop;
                payload::action(stamp, "Stop", [ship_id])
            }
            Command::Dock { .. } => unreachable!("handled above"),
        };
        vec![Self::notify(session, "DoDestinyUpdate", payload::destiny_update([change], false))]
    }

    fn dock(&mut self, session: SessionId, station_id: i64, now: i64) -> Vec<Out> {
        let code = Command::Dock { station_id }.code();
        let Some(station) = self.system.body(station_id).filter(|b| b.kind == BodyKind::Station).cloned()
        else {
            return vec![Out::Refused { session, command: code, reason: "STATION_NOT_FOUND" }];
        };
        let stamp = self.next_stamp(now);
        let pilot = self.pilots.get_mut(&session).expect("the caller checked");
        if let Some(pending) = pilot.docking.as_ref().filter(|p| p.station_id == station_id) {
            return vec![Out::Accepted { session, command: code, at: pending.accepted_at }];
        }
        settle(pilot, now);
        let ship = &mut pilot.ship;
        if surface_distance(ship, station.position, station.radius) > DOCKING_RANGE {
            return vec![Out::Refused { session, command: code, reason: "DOCKING_APPROACH_REQUIRED" }];
        }
        ship.speed_fraction = 0.0;
        ship.course = Course::Stop;
        pilot.docking = Some(PendingDock { station_id, accepted_at: now });
        let ship_id = id(ship.spec.ship_id);
        vec![
            Self::notify_charid(session, "OnDockingAccepted", vec![id(station_id)]),
            Self::notify(
                session,
                "DoDestinyUpdate",
                payload::destiny_update(
                    [
                        payload::action(stamp, "SetBallMassive", [ship_id.clone(), int(0)]),
                        payload::action(stamp, "Stop", [ship_id]),
                    ],
                    false,
                ),
            ),
            Out::Accepted { session, command: code, at: now },
        ]
    }

    fn notify_charid(session: SessionId, name: &'static str, args: Vec<Value>) -> Out {
        Out::Notify { session, name, id_type: "charid", args }
    }

    /// The numbers of a ship in space changed (a skill finished, a module went on): the client is told the ones that differ.
    pub fn respec(&mut self, session: SessionId, spec: ShipSpec, now: i64) -> Vec<Out> {
        let stamp = self.next_stamp(now);
        let Some(pilot) = self.pilots.get_mut(&session) else { return Vec::new() };
        settle(pilot, now);
        let old = std::mem::replace(&mut pilot.ship.spec, spec);
        let new = &pilot.ship.spec;
        let ship_id = id(new.ship_id);
        let mut actions = Vec::new();
        if new.mass != old.mass {
            actions.push(payload::action(stamp, "SetBallMass", [ship_id.clone(), real(new.mass)]));
        }
        if new.agility != old.agility {
            actions.push(payload::action(stamp, "SetBallAgility", [ship_id.clone(), real(new.agility)]));
        }
        if new.max_velocity != old.max_velocity {
            actions.push(payload::action(stamp, "SetMaxSpeed", [ship_id, real(new.max_velocity)]));
        }
        if actions.is_empty() {
            return Vec::new();
        }
        vec![Self::notify(session, "DoDestinyUpdate", payload::destiny_update(actions, false))]
    }

    /// Finishes what is due at `now`: a docking that has taken its time takes the ship out of space.
    pub fn poll(&mut self, now: i64) -> Vec<Out> {
        let due: Vec<(SessionId, i64)> = self
            .pilots
            .iter()
            .filter_map(|(session, pilot)| {
                pilot
                    .docking
                    .as_ref()
                    .filter(|d| now - d.accepted_at >= DOCKING_DELAY)
                    .map(|d| (*session, d.station_id))
            })
            .collect();
        due.into_iter()
            .map(|(session, station_id)| {
                self.pilots.remove(&session);
                Out::Docked { session, station_id }
            })
            .collect()
    }
}

fn with_id(ship_id: &Value, rest: [Value; 3]) -> Vec<Value> {
    let mut args = vec![ship_id.clone()];
    args.extend(rest);
    args
}

/// A vector of length 1 along the given one, if it has a direction.
fn unit(v: Vec3) -> Option<Vec3> {
    let length = (v.x * v.x + v.y * v.y + v.z * v.z).sqrt();
    (length.is_finite() && length > 1e-12).then(|| Vec3::new(v.x / length, v.y / length, v.z / length))
}

/// Which way a ship leaves a station: the station's own orientation when it has one, otherwise away from the star.
fn undock_direction(station_id: i64, position: Vec3) -> Vec3 {
    if let Some(angles) = crate::slim::station_rotation(station_id) {
        let (yaw, pitch) = (angles[0].to_radians(), angles[1].to_radians());
        return Vec3::new(yaw.sin() * pitch.cos(), -pitch.sin(), yaw.cos() * pitch.cos());
    }
    unit(position).unwrap_or(Vec3::new(0.0, 0.0, 1.0))
}

fn surface_distance(ship: &Ship, station: Vec3, station_radius: f64) -> f64 {
    let (dx, dy, dz) =
        (ship.position.x - station.x, ship.position.y - station.y, ship.position.z - station.z);
    ((dx * dx + dy * dy + dz * dz).sqrt() - ship.spec.radius - station_radius).max(0.0)
}

/// Brings a ship's position and velocity to `now`: it eases towards the speed it is asked for, the way the client integrates it.
fn settle(pilot: &mut Pilot, now: i64) {
    let seconds = (now - pilot.settled_at) as f64 / TICKS_PER_SECOND as f64;
    pilot.settled_at = now;
    if seconds <= 0.0 {
        return;
    }
    let ship = &mut pilot.ship;
    let inertial_mass = ship.spec.mass * ship.spec.agility;
    let agility_seconds = (inertial_mass / DESTINY_SPACE_FRICTION).max(0.05);
    let cruise = match ship.course {
        Course::Goto => ship.spec.max_velocity * ship.speed_fraction,
        Course::Stop => 0.0,
    };
    let thrust = cruise / agility_seconds;
    let acceleration =
        Vec3::new(ship.direction.x * thrust, ship.direction.y * thrust, ship.direction.z * thrust);
    let motion = integrate_carbon_motion(
        ship.position,
        ship.velocity,
        acceleration,
        inertial_mass,
        DESTINY_SPACE_FRICTION,
        seconds,
    );
    ship.position = motion.position;
    ship.velocity = motion.velocity;
}

#[cfg(test)]
pub(crate) mod tests {
    use super::*;
    use crate::ship::ModuleSpec;
    use crate::world::{Body, Detail};

    pub(crate) const STATION: i64 = 60_000_001;
    pub(crate) const SESSION: SessionId = 7;
    /// 2026-09-29 12:00:00 UTC as a FILETIME.
    pub(crate) const NOON: i64 = 134_351_688_000_000_000;

    pub(crate) fn system() -> Arc<SolarSystem> {
        let station = Body {
            id: STATION,
            type_id: 1531,
            group_id: 15,
            category_id: 3,
            owner_id: 1_000_001,
            name: "Testing I - Trade Hub".to_string(),
            kind: BodyKind::Station,
            position: Vec3::new(1_000_000.0, 0.0, 0.0),
            radius: 1_000.0,
            graphic_id: None,
            detail: Detail::Station { corporation_id: 1_000_001 },
        };
        Arc::new(SolarSystem {
            id: 30_000_001,
            name: "Testing".to_string(),
            constellation_id: 20_000_001,
            region_id: 10_000_001,
            faction_id: None,
            security: 0.9,
            bodies: vec![station],
        })
    }

    pub(crate) fn spec() -> ShipSpec {
        ShipSpec {
            ship_id: 9_988_400_001_939,
            character_id: 140_000_342,
            type_id: 601,
            group_id: 237,
            category_id: 6,
            name: "Ibis".to_string(),
            owner_id: 140_000_342,
            corporation_id: 1_000_006,
            alliance_id: 0,
            war_faction_id: 0,
            security_status: 0.0,
            radius: 47.0,
            mass: 1_163_000.0,
            max_velocity: 339.25,
            agility: 4.0185,
            warp_speed_au: 3.0,
            shield_recharge_ms: 860_000.0,
            modules: vec![ModuleSpec { item_id: 9_988_400_001_940, type_id: 21857, flag: 19 }],
        }
    }

    fn scene() -> Scene {
        let mut scene = Scene::new(system());
        scene.undock(SESSION, spec(), STATION, NOON).unwrap();
        scene
    }

    fn seconds(n: i64) -> i64 {
        n * TICKS_PER_SECOND
    }

    fn speed(ship: &Ship) -> f64 {
        (ship.velocity.x.powi(2) + ship.velocity.y.powi(2) + ship.velocity.z.powi(2)).sqrt()
    }

    fn action_names(out: &Out) -> Vec<String> {
        let Out::Notify { args, .. } = out else { return Vec::new() };
        let Some(Value::List(actions)) = args.first() else { return Vec::new() };
        actions
            .iter()
            .map(|a| match a {
                Value::Tuple(pair) => match &pair[1] {
                    Value::Tuple(inner) => match &inner[0] {
                        Value::Str(name) => name.to_string(),
                        other => panic!("{other:?}"),
                    },
                    other => panic!("{other:?}"),
                },
                other => panic!("{other:?}"),
            })
            .collect()
    }

    fn stamps(out: &Out) -> Vec<i64> {
        let Out::Notify { args, .. } = out else { return Vec::new() };
        let Some(Value::List(actions)) = args.first() else { return Vec::new() };
        actions
            .iter()
            .map(|a| match a {
                Value::Tuple(pair) => match &pair[0] {
                    Value::Int(n) => *n,
                    other => panic!("{other:?}"),
                },
                other => panic!("{other:?}"),
            })
            .collect()
    }

    #[test]
    fn stamps_are_whole_seconds_of_the_unix_clock() {
        assert_eq!(stamp_at(134_351_527_024_310_000), 1_790_679_102);
    }

    #[test]
    fn an_undocked_ship_is_put_off_the_station_and_moves_away_at_top_speed() {
        let mut scene = scene();
        let ship = scene.ship(SESSION, NOON).unwrap().clone();
        let gap =
            ((ship.position.x - 1_000_000.0).powi(2) + ship.position.y.powi(2) + ship.position.z.powi(2))
                .sqrt();
        assert!((gap - 2_000.0).abs() < 1e-6, "{gap}");
        assert!((speed(&ship) - 339.25).abs() < 1e-9);
        assert_eq!((ship.course, ship.speed_fraction), (Course::Goto, 1.0));
        // away from the star at the origin: the station is on +x, so the ship is further out
        assert!(ship.direction.x > 0.99);
    }

    #[test]
    fn a_ship_cannot_undock_twice_or_from_a_station_that_is_not_there() {
        let mut scene = scene();
        assert_eq!(scene.undock(SESSION, spec(), STATION, NOON), Err(SceneError::AlreadyInSpace(SESSION)));
        assert_eq!(scene.undock(8, spec(), 42, NOON), Err(SceneError::UnknownStation(42)));
    }

    #[test]
    fn binding_sends_the_time_the_ship_then_the_state_then_the_ship_numbers() {
        let mut scene = scene();
        let out = scene.bind(SESSION, NOON);
        let names: Vec<_> = out
            .iter()
            .map(|o| match o {
                Out::Notify { name, .. } => *name,
                other => panic!("{other:?}"),
            })
            .collect();
        assert_eq!(
            names,
            [
                "OnSetTimeDilation",
                "DoSimClockRebase",
                "DoDestinyUpdate",
                "DoDestinyUpdate",
                "DoDestinyUpdate",
                "DoDestinyUpdate"
            ]
        );
        assert_eq!(action_names(&out[2]), ["AddBalls2"]);
        assert_eq!(
            action_names(&out[3]),
            [
                "OnSpecialFX",
                "SetBallPosition",
                "SetBallMassive",
                "SetBallMass",
                "SetBallVelocity",
                "GotoDirection"
            ]
        );
        assert_eq!(action_names(&out[4]), ["SetState"]);
        assert_eq!(action_names(&out[5]), ["SetBallAgility", "SetMaxSpeed"]);
        // the ship comes at the stamp of the moment and the state a stamp after (the reference's 102 and 103)
        let now_stamp = i64::from(stamp_at(NOON));
        assert_eq!(stamps(&out[2]), [now_stamp]);
        assert_eq!(stamps(&out[4]), [now_stamp + 1]);
        assert!(
            matches!(&out[2], Out::Notify { args, .. } if args[1] == Value::Bool(true)),
            "the client waits for the ship"
        );
    }

    #[test]
    fn binding_a_session_without_a_ship_says_nothing() {
        assert!(Scene::new(system()).bind(1, NOON).is_empty());
    }

    #[test]
    fn stamps_run_one_after_another_but_never_more_than_two_ahead() {
        let mut scene = scene();
        let now_stamp = i64::from(stamp_at(NOON));
        let sent: Vec<i64> =
            (0..5).map(|_| stamps(&scene.command(SESSION, Command::Stop, NOON)[0])[0]).collect();
        assert_eq!(sent, [now_stamp, now_stamp + 1, now_stamp + 2, now_stamp + 2, now_stamp + 2]);
        // and a stamp that has been overtaken by the clock starts from the clock again
        let later = stamps(&scene.command(SESSION, Command::Stop, NOON + seconds(10))[0])[0];
        assert_eq!(later, now_stamp + 10, "a stamp the clock has passed starts again from the clock");
    }

    #[test]
    fn a_ship_told_to_stop_slows_down_and_one_told_to_go_gathers_speed() {
        let mut scene = scene();
        scene.command(SESSION, Command::Stop, NOON);
        assert!(speed(scene.ship(SESSION, NOON + seconds(60)).unwrap()) < 0.01);
        scene.command(SESSION, Command::SetSpeedFraction(0.5), NOON + seconds(60));
        let cruise = speed(scene.ship(SESSION, NOON + seconds(120)).unwrap());
        assert!((cruise - 339.25 * 0.5).abs() < 0.01, "{cruise}");
    }

    #[test]
    fn a_ship_at_full_speed_keeps_its_place_on_a_straight_line() {
        let mut scene = scene();
        let before = scene.ship(SESSION, NOON).unwrap().clone();
        let after = scene.ship(SESSION, NOON + seconds(10)).unwrap().clone();
        let travelled = ((after.position.x - before.position.x).powi(2)
            + (after.position.y - before.position.y).powi(2)
            + (after.position.z - before.position.z).powi(2))
        .sqrt();
        assert!((travelled - 3392.5).abs() < 0.01, "{travelled}");
    }

    #[test]
    fn a_direction_is_answered_as_a_unit_vector_and_turns_the_ship() {
        let mut scene = scene();
        let out = scene.command(SESSION, Command::GotoDirection(Vec3::new(0.0, 3.0, 4.0)), NOON);
        assert_eq!(action_names(&out[0]), ["GotoDirection"]);
        let ship = scene.ship(SESSION, NOON).unwrap();
        assert_eq!((ship.direction.x, ship.direction.y, ship.direction.z), (0.0, 0.6, 0.8));
        // a direction with no length is no direction
        let out = scene.command(SESSION, Command::GotoDirection(Vec3::ZERO), NOON);
        assert_eq!(out, [Out::Refused { session: SESSION, command: 2, reason: "NO_DIRECTION" }]);
    }

    #[test]
    fn a_speed_fraction_stays_between_nothing_and_everything() {
        let mut scene = scene();
        scene.command(SESSION, Command::SetSpeedFraction(7.0), NOON);
        assert_eq!(scene.ship(SESSION, NOON).unwrap().speed_fraction, 1.0);
        scene.command(SESSION, Command::SetSpeedFraction(-1.0), NOON);
        assert_eq!(scene.ship(SESSION, NOON).unwrap().speed_fraction, 0.0);
        scene.command(SESSION, Command::SetSpeedFraction(f64::NAN), NOON);
        assert_eq!(scene.ship(SESSION, NOON).unwrap().speed_fraction, 0.0);
    }

    #[test]
    fn a_command_from_a_session_with_no_ship_is_refused() {
        let out = Scene::new(system()).command(9, Command::Stop, NOON);
        assert_eq!(out, [Out::Refused { session: 9, command: 3, reason: "NO_SHIP_IN_SPACE" }]);
    }

    #[test]
    fn docking_needs_the_station_to_be_close_and_to_exist() {
        let mut scene = scene();
        // 10 km off the station's surface
        scene.pilots.get_mut(&SESSION).unwrap().ship.position =
            Vec3::new(1_000_000.0 + 1_000.0 + 47.0 + 10_000.0, 0.0, 0.0);
        let out = scene.command(SESSION, Command::Dock { station_id: STATION }, NOON);
        assert_eq!(out, [Out::Refused { session: SESSION, command: 4, reason: "DOCKING_APPROACH_REQUIRED" }]);
        let out = scene.command(SESSION, Command::Dock { station_id: 42 }, NOON);
        assert_eq!(out, [Out::Refused { session: SESSION, command: 4, reason: "STATION_NOT_FOUND" }]);
        assert!(scene.contains(SESSION));
    }

    fn near_the_station() -> Scene {
        let mut scene = Scene::new(system());
        let mut near = spec();
        near.max_velocity = 0.0;
        scene.undock(SESSION, near, STATION, NOON).unwrap();
        // 8 km out at the reference's spawn distance is beyond range for a small station: stand 500 m off its surface
        let pilot = scene.pilots.get_mut(&SESSION).unwrap();
        pilot.ship.position = Vec3::new(1_000_000.0 + 1_000.0 + 47.0 + 500.0, 0.0, 0.0);
        scene
    }

    #[test]
    fn an_accepted_docking_stops_the_ship_and_takes_four_seconds() {
        let mut scene = near_the_station();
        let out = scene.command(SESSION, Command::Dock { station_id: STATION }, NOON);
        assert!(
            matches!(&out[0], Out::Notify { name: "OnDockingAccepted", id_type: "charid", args, .. } if args == &[Value::Int(STATION)])
        );
        assert_eq!(action_names(&out[1]), ["SetBallMassive", "Stop"]);
        assert_eq!(out[2], Out::Accepted { session: SESSION, command: 4, at: NOON });

        assert!(scene.poll(NOON + seconds(3)).is_empty());
        assert!(scene.contains(SESSION));
        assert_eq!(scene.poll(NOON + seconds(4)), [Out::Docked { session: SESSION, station_id: STATION }]);
        assert!(!scene.contains(SESSION));
        assert!(scene.poll(NOON + seconds(9)).is_empty(), "docked once");
    }

    #[test]
    fn a_docking_asked_for_again_is_accepted_with_the_first_time_and_orders_are_ignored_meanwhile() {
        let mut scene = near_the_station();
        scene.command(SESSION, Command::Dock { station_id: STATION }, NOON);
        let again = scene.command(SESSION, Command::Dock { station_id: STATION }, NOON + seconds(1));
        assert_eq!(again, [Out::Accepted { session: SESSION, command: 4, at: NOON }]);
        assert!(scene.command(SESSION, Command::SetSpeedFraction(1.0), NOON + seconds(1)).is_empty());
        assert_eq!(scene.ship(SESSION, NOON + seconds(1)).unwrap().speed_fraction, 0.0);
    }

    #[test]
    fn docking_range_is_measured_between_surfaces_and_two_and_a_half_kilometres_is_in() {
        let dock_from = |gap: f64| {
            let mut scene = near_the_station();
            scene.pilots.get_mut(&SESSION).unwrap().ship.position =
                Vec3::new(1_000_000.0 + 1_000.0 + 47.0 + gap, 0.0, 0.0);
            scene.command(SESSION, Command::Dock { station_id: STATION }, NOON)
        };
        assert!(matches!(dock_from(2_500.0).last(), Some(Out::Accepted { .. })));
        assert_eq!(
            dock_from(2_500.5),
            [Out::Refused { session: SESSION, command: 4, reason: "DOCKING_APPROACH_REQUIRED" }]
        );
    }

    #[test]
    fn a_ship_leaves_a_big_station_at_a_third_of_its_radius_and_is_within_docking_range() {
        let mut big = (*system()).clone();
        big.bodies[0].radius = 70_000.0;
        let mut scene = Scene::new(Arc::new(big));
        scene.undock(SESSION, spec(), STATION, NOON).unwrap();
        let ship = scene.ship(SESSION, NOON).unwrap();
        let gap =
            ((ship.position.x - 1_000_000.0).powi(2) + ship.position.y.powi(2) + ship.position.z.powi(2))
                .sqrt();
        assert!((gap - 23_800.0).abs() < 1e-6, "{gap}");
        // and is at once within docking range of it, as the reference's ship was
        let out = scene.command(SESSION, Command::Dock { station_id: STATION }, NOON);
        assert!(matches!(out.last(), Some(Out::Accepted { .. })));
    }

    #[test]
    fn a_change_of_the_ships_numbers_tells_the_client_what_differs() {
        let mut scene = scene();
        let mut faster = spec();
        faster.max_velocity = 400.0;
        faster.agility = 3.5;
        let out = scene.respec(SESSION, faster.clone(), NOON);
        assert_eq!(action_names(&out[0]), ["SetBallAgility", "SetMaxSpeed"]);
        assert_eq!(scene.ship(SESSION, NOON).unwrap().spec, faster);
        assert!(scene.respec(SESSION, faster.clone(), NOON).is_empty(), "nothing changed");
        assert!(scene.respec(9, faster, NOON).is_empty(), "no ship, nothing to say");
        let mut heavier = spec();
        heavier.mass = 2_000_000.0;
        assert_eq!(
            action_names(&scene.respec(SESSION, heavier, NOON)[0]),
            ["SetBallMass", "SetBallAgility", "SetMaxSpeed"]
        );
    }

    #[test]
    fn leaving_removes_the_ship() {
        let mut scene = scene();
        assert!(scene.leave(SESSION, Departure::LoggedOff));
        assert!(!scene.leave(SESSION, Departure::LoggedOff));
        assert!(scene.is_empty());
    }

    #[test]
    fn the_same_calls_at_the_same_times_give_the_same_bytes() {
        let run = || {
            let mut scene = scene();
            let mut out = scene.bind(SESSION, NOON);
            out.extend(scene.command(SESSION, Command::SetSpeedFraction(0.5), NOON + seconds(3)));
            out.extend(scene.command(
                SESSION,
                Command::GotoDirection(Vec3::new(1.0, 0.0, 0.0)),
                NOON + seconds(5),
            ));
            out.extend(scene.update_state(SESSION, NOON + seconds(9)));
            out
        };
        assert_eq!(run(), run());
    }
}
