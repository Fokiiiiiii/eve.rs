//! A running solar system: the ships in it, what commands do to them, and what the client is told (ADR 0007).
//!
//! The scene has no clock of its own. Every call says what time it is (a FILETIME) and a ship's motion is worked out
//! from the time it was last settled to that time, so the same calls at the same times give the same bytes.

use std::collections::{BTreeMap, BTreeSet, HashMap};
use std::sync::Arc;

use eve_destiny::Vec3;
use eve_marshal::Value;

use crate::flight::{Flight, Milestone};
use crate::gate;
use crate::health::Condition;
use crate::modules::Modules;
use crate::motion::{self, Target};
use crate::payload;
use crate::ship::{Course, Ship, ShipSpec};
use crate::targeting::{Targeting, UNDOCK_PROTECTION};
use crate::warp::{self, PythonRandom};
use crate::wire::{filetime, id, int, real, text, tuple, vector};
use crate::world::{Body, BodyKind, Detail, SolarSystem};
use crate::wreck::{Wreck, WreckKind};

/// The host's name for a player's connection.
pub type SessionId = u64;

/// FILETIME ticks in a second.
pub(crate) const TICKS_PER_SECOND: i64 = 10_000_000;
/// Seconds between 1601 and 1970.
const UNIX_EPOCH_SECONDS: i64 = 11_644_473_600;
/// How long a docking takes from the moment it is accepted.
const DOCKING_DELAY: i64 = 4 * TICKS_PER_SECOND;
/// How close (from surface to surface) a ship has to be to dock.
const DOCKING_RANGE: f64 = 2_500.0;
/// How long after a jump was accepted the ship leaves for the other system at the soonest, and how long after the jump-out effect begins.
const JUMP_DELAY: i64 = 4 * TICKS_PER_SECOND;
const JUMP_EFFECT_DELAY: i64 = 3 * TICKS_PER_SECOND;
/// How many ships deep a ship that follows a ship that follows a ship is moved after the one it follows.
const MAX_FOLLOW_CHAIN: u8 = 4;
/// How far ahead of the clock a stamp may be sent (the client holds updates until their stamp).
const MAX_STAMP_LEAD: u32 = 2;
/// How far from the station's centre a ship is put when the table of undock points does not know the station's type, as a share of the
/// station's radius (the recorded ship left its station at 0.34 of it, inside the sphere the docking range is measured from), and the least.
const UNDOCK_RADIUS_SHARE: f64 = 0.34;
const UNDOCK_MIN_DISTANCE: f64 = 2_000.0;
/// How far from a belt's surface a warp may end for the ship to count as having arrived at the belt (the reference's `landingRadiusMeters`).
pub const BELT_LANDING_RADIUS: f64 = 250_000.0;

/// What a player can ask of their ship (the numbers of the client's `CmdXxx` calls).
#[derive(Debug, Clone, Copy, PartialEq)]
pub enum Command {
    SetSpeedFraction(f64),
    GotoDirection(Vec3),
    Stop,
    Dock {
        station_id: i64,
    },
    /// Approach a body and stay `range` metres off its surface.
    FollowBall {
        target_id: i64,
        range: f64,
    },
    /// Circle a body `distance` metres off its surface.
    Orbit {
        target_id: i64,
        distance: f64,
    },
    /// Turn towards a body and fly at it.
    AlignTo {
        target_id: i64,
    },
    /// Warp to a body, ending `range` metres off it (further, for bodies that keep ships at a distance).
    WarpTo {
        target_id: i64,
        range: f64,
    },
    /// Jump through a stargate; `destination_gate` is the gate the client says it leads to (0 when it does not say).
    Jump {
        gate_id: i64,
        destination_gate: i64,
    },
}

impl Command {
    /// The code of the command on the link (ADR 0007).
    pub fn code(self) -> u8 {
        match self {
            Self::SetSpeedFraction(_) => 1,
            Self::GotoDirection(_) => 2,
            Self::Stop => 3,
            Self::Dock { .. } => 4,
            Self::FollowBall { .. } => 5,
            Self::Orbit { .. } => 6,
            Self::AlignTo { .. } => 7,
            Self::WarpTo { .. } => 8,
            Self::Jump { .. } => 9,
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
    /// A command was accepted, after the update it made (a `Dock` is answered with the time it was accepted at).
    Accepted { session: SessionId, command: u8, at: i64 },
    /// A `Dock` could not be done from here: the ship now approaches the station, `distance` metres off its surface.
    Approaching { session: SessionId, command: u8, distance: f64 },
    /// A `Jump` was accepted: the ship's jump-out effect has begun and it will be in `destination_system` a few seconds on (the host is then told
    /// with a `Jumped`).
    Jumping { session: SessionId, command: u8, destination_system: i32, destination_gate: i64 },
    /// The ship has docked: the host moves the character into the station and keeps what the ship has taken.
    Docked { session: SessionId, station_id: i64, condition: Condition },
    /// A player's ship came to rest at a belt after a warp (the belt it warped to, or the nearest within [`BELT_LANDING_RADIUS`] of it): the host
    /// decides whether anything lives there. `position` is where the belt is (what the host places what lives there around).
    Arrived { session: SessionId, belt_id: i64, position: Vec3 },
    /// The answer to a question about the ship's condition.
    Condition { session: SessionId, command: u8, condition: Condition },
    /// A command the scene cannot do.
    Refused { session: SessionId, command: u8, reason: &'static str },
    /// The answer to a targeting order: whether a lock is going, and the ships and bodies locked (or, to `GetTargeters`, the ships that lock this one).
    Targets { session: SessionId, command: u8, pending: bool, targets: Vec<i64> },
    /// The ship of this session was destroyed (its hull was gone): it is no longer in the scene. `wreck` is the wreck the scene made of it, when it
    /// did (an NPC's; for a player's ship the host makes the wreck, and the pod).
    Destroyed {
        session: SessionId,
        ship_id: i64,
        wreck: Option<i64>,
        /// The wreck the ship's type leaves, whether or not the scene made it (the host makes a player's from this).
        wreck_kind: Option<WreckKind>,
        position: Vec3,
        direction: Vec3,
        radius: f64,
        killer_ship: Option<i64>,
    },
    /// A turret spent rounds: how many it has left (the host keeps the quantity of the charge item).
    ChargeSpent { session: SessionId, module_id: i64, rounds_left: i64 },
    /// The answer to `AdjustCharge`: whether the gun runs, the rounds it had and the rounds it has.
    ChargeAdjusted { session: SessionId, request_id: u64, running: bool, before: i64, after: i64 },
    /// A can the host asked for (`PlaceContainer`) is in space: where, and how it lies.
    ContainerPlaced { session: SessionId, container_id: i64, position: Vec3, direction: Vec3 },
    /// The answer to a question about how far a wreck or can is: metres from the ship's centre to its surface, or `None` when there is no such wreck.
    Measured { session: SessionId, command: u8, distance: Option<f64> },
    /// A targeting order the scene refused, with what the client is told about the target.
    TargetRefused {
        session: SessionId,
        command: u8,
        reason: &'static str,
        target_type: i32,
        desired_range: f64,
        actual_distance: f64,
    },
}

impl Out {
    /// The session the message is for.
    pub fn session(&self) -> SessionId {
        match self {
            Out::Notify { session, .. }
            | Out::Accepted { session, .. }
            | Out::Approaching { session, .. }
            | Out::Jumping { session, .. }
            | Out::Docked { session, .. }
            | Out::Arrived { session, .. }
            | Out::Destroyed { session, .. }
            | Out::Condition { session, .. }
            | Out::Refused { session, .. }
            | Out::Targets { session, .. }
            | Out::ChargeSpent { session, .. }
            | Out::ChargeAdjusted { session, .. }
            | Out::ContainerPlaced { session, .. }
            | Out::Measured { session, .. }
            | Out::TargetRefused { session, .. } => *session,
        }
    }
}

/// Why the scene cannot take a ship.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum SceneError {
    UnknownStation(i64),
    AlreadyInSpace(SessionId),
    /// The session's ship was not destroyed (a capsule is only given where a ship was lost).
    NotDestroyed(SessionId),
}

#[derive(Debug, Clone)]
struct PendingDock {
    station_id: i64,
    accepted_at: i64,
}

#[derive(Debug, Clone)]
pub(crate) struct Pilot {
    pub(crate) ship: Ship,
    /// The moment the ship's position and velocity are those of.
    settled_at: i64,
    docking: Option<PendingDock>,
    /// The warp the ship is in, from the command until it is at rest again.
    pub(crate) flight: Option<Flight>,
    /// What the ship's last warp was to (the body), kept until the next warp: where it came to rest is judged against it.
    pub(crate) warp_target: Option<i64>,
    /// The jump the ship has begun, until it leaves for the other system.
    jump: Option<PendingJump>,
    /// The ship came through a gate (it is bound differently from one that undocked).
    jumped_in: bool,
    /// The client has bound the ballpark: it can be sent destiny updates.
    pub(crate) bound: bool,
    /// When the ship came into the scene (the ships are listed in this order).
    pub(crate) entered: u64,
    /// The other ships this pilot has been told of and not told to forget: those in its ballpark.
    pub(crate) sees: BTreeSet<SessionId>,
    /// The wrecks this pilot has been told of and not told to forget.
    pub(crate) sees_wrecks: BTreeSet<i64>,
    /// What the ship has locked and is locking.
    pub(crate) targeting: Targeting,
    /// The capacitor and the effects of the ship's modules that run.
    pub(crate) modules: Modules,
    /// Until when the ship cannot be locked (the undocking protection; it ends sooner when the ship is flown or locks something).
    pub(crate) protected_until: i64,
    /// What other ships' modules do to this ship's numbers while they run (webs, painters, dampeners, scramblers).
    pub(crate) incoming: crate::ewar::Incoming,
}

impl Pilot {
    /// A ship that has just come into the scene at `now`, at rest in the order of arrival `entered`, that has not bound the ballpark and holds nothing
    /// locked.
    pub(crate) fn new(ship: Ship, modules: Modules, entered: u64, now: i64, protected_until: i64) -> Self {
        Self {
            ship,
            settled_at: now,
            docking: None,
            flight: None,
            warp_target: None,
            jump: None,
            jumped_in: false,
            bound: false,
            entered,
            sees: BTreeSet::new(),
            sees_wrecks: BTreeSet::new(),
            targeting: Targeting::default(),
            modules,
            protected_until,
            incoming: crate::ewar::Incoming::default(),
        }
    }
}

#[derive(Debug, Clone)]
struct PendingJump {
    gate_id: i64,
    destination_system: i32,
    destination_gate: i64,
    leaves_at: i64,
}

/// A ship whose jump is due: it has left the scene it was in and is to appear at the gate it leads to.
#[derive(Debug, Clone)]
pub struct Traveller {
    pub session: SessionId,
    pub from_system: i32,
    pub from_gate: i64,
    pub to_system: i32,
    pub to_gate: i64,
    pilot: Pilot,
}

/// One solar system with the ships in it.
#[derive(Debug)]
pub struct Scene {
    system: Arc<SolarSystem>,
    pub(crate) pilots: BTreeMap<SessionId, Pilot>,
    next_stamp: u32,
    /// How many ships have come into the scene.
    pub(crate) entries: u64,
    /// Who was in each box of the grid at the last look at what the ships see, and whether a ship was in warp then: while neither changes
    /// no ship can have come into view or left it, and the look is skipped.
    pub(crate) boxes: HashMap<(i64, i64, i64), Vec<SessionId>>,
    pub(crate) warped_last: bool,
    /// What happened to ships while their motion was worked out (a ship dropped out of warp), to be told with the next call.
    backlog: Vec<Out>,
    /// The number the last lock of a ship in this scene was given (locks are listed in the order they were asked for).
    pub(crate) target_sequence: u64,
    /// How many NPC ships have been made in this scene (the sessions they are known by are numbered from it, `crate::npc`).
    pub(crate) npc_sequence: u64,
    /// How many entity groups have been made in this scene.
    pub(crate) group_sequence: u64,
    /// The dice a shot is rolled with (`crate::weapons`).
    pub(crate) rng: PythonRandom,
    /// The offensive acts since the brains last looked (`crate::brains` tells the NPCs that were their targets).
    pub(crate) aggressions: Vec<crate::weapons::Aggression>,
    /// The missiles that are on their way to a ship, by id: entities of their own, apart from the ships (`crate::missile_entity`).
    pub(crate) missiles: BTreeMap<i64, crate::missile_entity::MissileEntity>,
    /// How many missiles have been fired in this scene (their ids are numbered from it).
    pub(crate) missile_sequence: u64,
    /// The trees of the NPCs (`crate::brains`).
    pub(crate) brains: Option<crate::brains::Brains>,
    /// The wrecks in the scene, by item id (`crate::wreck`).
    pub(crate) wrecks: BTreeMap<i64, Wreck>,
    /// How many wrecks of ships that died in this scene have been given an id by it (`crate::wreck::TRANSIENT_ID_BASE`).
    pub(crate) wreck_sequence: u64,
    /// The ships destroyed since the brains last looked (`crate::brains` tells the trees that watched them).
    pub(crate) destroyed: Vec<i64>,
    /// Which wreck a ship of some type leaves (`crate::wreck_types`), when the static data is there to say.
    pub(crate) wreck_catalog: Option<Arc<crate::wreck_types::WreckCatalog>>,
    /// The sessions whose ship was destroyed and have no other yet (the host gives them a capsule with `respawn`): what they had bound and seen.
    pub(crate) widowed: BTreeMap<SessionId, Widow>,
}

/// What the client of a destroyed ship still has of the ballpark: it keeps it when it is given a new ship.
#[derive(Debug, Default)]
pub(crate) struct Widow {
    pub(crate) bound: bool,
    pub(crate) sees: BTreeSet<SessionId>,
    pub(crate) sees_wrecks: BTreeSet<i64>,
}

/// The destiny stamp of a moment: whole seconds of the Unix clock.
pub fn stamp_at(now: i64) -> u32 {
    (now.div_euclid(TICKS_PER_SECOND) - UNIX_EPOCH_SECONDS) as u32
}

impl Scene {
    pub fn new(system: Arc<SolarSystem>) -> Self {
        Self {
            system,
            pilots: BTreeMap::new(),
            next_stamp: 0,
            entries: 0,
            boxes: HashMap::new(),
            warped_last: false,
            backlog: Vec::new(),
            target_sequence: 0,
            npc_sequence: 0,
            group_sequence: 0,
            rng: PythonRandom::new(0x5348_4f54),
            aggressions: Vec::new(),
            missiles: BTreeMap::new(),
            missile_sequence: 0,
            brains: None,
            wrecks: BTreeMap::new(),
            wreck_sequence: 0,
            destroyed: Vec::new(),
            widowed: BTreeMap::new(),
            wreck_catalog: None,
        }
    }

    /// Gives the scene the item types the wreck rules look up, so that a ship that is destroyed leaves the wreck of its type.
    pub fn set_wreck_catalog(&mut self, catalog: Arc<crate::wreck_types::WreckCatalog>) {
        self.wreck_catalog = Some(catalog);
    }

    pub fn system(&self) -> &SolarSystem {
        &self.system
    }

    /// Whether no player is in the scene (NPCs do not keep a scene: a system has one while somebody is there to see it).
    pub fn is_empty(&self) -> bool {
        self.widowed.is_empty() && self.pilots.keys().all(|session| crate::npc::is_npc_session(*session))
    }

    /// The wreck with this item id, if it is in the scene.
    pub fn wreck(&self, id: i64) -> Option<&Wreck> {
        self.wrecks.get(&id)
    }

    /// A session whose ship was destroyed will not be given another (it left): its ballpark is not kept.
    pub fn forget_widow(&mut self, session: SessionId) {
        self.widowed.remove(&session);
    }

    pub fn contains(&self, session: SessionId) -> bool {
        self.pilots.contains_key(&session)
    }

    /// The ship of a session, as it is at `now`.
    pub fn ship(&mut self, session: SessionId, now: i64) -> Option<&Ship> {
        self.pilots.contains_key(&session).then_some(())?;
        self.settle_pilot(session, now);
        self.pilots.get(&session).map(|pilot| &pilot.ship)
    }

    /// The next stamp a change can carry: one after the last, but never more than two ahead of the clock, and never behind it.
    pub(crate) fn next_stamp(&mut self, now: i64) -> u32 {
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
    ) -> Result<Vec<Out>, SceneError> {
        if self.pilots.contains_key(&session) {
            return Err(SceneError::AlreadyInSpace(session));
        }
        let station = self
            .system
            .body(station_id)
            .filter(|b| b.kind == BodyKind::Station)
            .ok_or(SceneError::UnknownStation(station_id))?;
        let (position, direction) = undock_place(station, spec.group_id);
        let speed = spec.max_velocity;
        let modules = Modules::with_charge(
            spec.capacitor_capacity,
            spec.capacitor_recharge_s,
            spec.condition.charge,
            now,
        );
        let velocity = Vec3::new(direction.x * speed, direction.y * speed, direction.z * speed);
        let ship = Ship::new(spec, position, velocity, direction, 1.0, Course::Goto, now);
        let entered = self.next_entry();
        self.pilots.insert(session, Pilot::new(ship, modules, entered, now, now + UNDOCK_PROTECTION));
        Ok(self.announce_arrival(session, now))
    }

    /// The ship leaves the scene (docked, gone offline, jumped): the ones who had it in view are told. None when it was not here.
    pub fn leave(&mut self, session: SessionId, _why: Departure, now: i64) -> Option<Vec<Out>> {
        let pilot = self.pilots.remove(&session)?;
        if let Some(brains) = self.brains.as_mut() {
            brains.forget(session);
        }
        let mut out = self.drop_targeting(pilot.ship.spec.ship_id, &pilot.targeting, now);
        out.extend(self.announce_departure(session, pilot.ship.spec.ship_id, now));
        Some(out)
    }

    pub(crate) fn notify(session: SessionId, name: &'static str, args: Vec<Value>) -> Out {
        Out::Notify { session, name, id_type: "clientID", args }
    }

    /// The client bound the system's ballpark: it is told the time, and given the ships in view (its own among them, in the order they came into
    /// the scene) and then everything around them.
    pub fn bind(&mut self, session: SessionId, now: i64) -> Vec<Out> {
        if !self.pilots.contains_key(&session) {
            return Vec::new();
        }
        self.settle_all(now);
        self.take_in_view(session);
        let first = self.next_stamp(now);
        let second = self.next_stamp(now);
        let system = Arc::clone(&self.system);
        let ships = self.ships_in_view(session);
        let Some(pilot) = self.pilots.get(&session) else { return Vec::new() };
        let ship = &pilot.ship;
        let ship_id = id(ship.spec.ship_id);
        let heading =
            |action_stamp: u32| payload::action(action_stamp, heading_name(ship), heading_args(ship));

        let Ok(added) = payload::add_ships(first, &ships, now) else { return Vec::new() };
        let wrecks = self.wrecks_in_view(session);
        let Ok(state) = payload::set_state(second, &system, ship, &ships, &wrecks, now) else {
            return Vec::new();
        };
        let update = |actions: Vec<Value>| {
            Self::notify(session, "DoDestinyUpdate", payload::destiny_update(actions, false))
        };
        let mut told = vec![
            Self::notify(session, "OnSetTimeDilation", vec![int(1), int(1), int(100_000_000)]),
            Self::notify(session, "DoSimClockRebase", vec![tuple([filetime(now), filetime(now)])]),
            // the client waits for the ship of an undock; a ship that came through a gate is not waited for
            Self::notify(session, "DoDestinyUpdate", payload::destiny_update([added], !pilot.jumped_in)),
        ];
        if !pilot.jumped_in {
            // a ship that undocked is put in motion as it is bound; one that came through a gate arrives at rest
            told.push(update(vec![
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
            ]));
        }
        told.push(update(vec![state]));
        // the numbers of every ship in view; the pilot's own lack the mass when the bind has sent it already
        let mut numbers = Vec::new();
        for other in &ships {
            if other.spec.ship_id != ship.spec.ship_id || pilot.jumped_in {
                numbers.extend(Self::numbers_of(other, second));
            } else {
                let own = id(ship.spec.ship_id);
                numbers.push(payload::action(
                    second,
                    "SetBallAgility",
                    [own.clone(), real(ship.spec.agility)],
                ));
                numbers.push(payload::action(second, "SetMaxSpeed", [own, real(ship.spec.max_velocity)]));
            }
        }
        told.push(update(numbers));
        self.with_backlog(told)
    }

    /// The client asked for the state again (`UpdateStateRequest`): every ball as it is now.
    pub fn update_state(&mut self, session: SessionId, now: i64) -> Vec<Out> {
        if !self.pilots.contains_key(&session) {
            return Vec::new();
        }
        self.settle_all(now);
        let stamp = self.next_stamp(now);
        let system = Arc::clone(&self.system);
        let ships = self.ships_in_view(session);
        let Some(pilot) = self.pilots.get(&session) else { return Vec::new() };
        let wrecks = self.wrecks_in_view(session);
        let told = match payload::set_state(stamp, &system, &pilot.ship, &ships, &wrecks, now) {
            Ok(state) => {
                vec![Self::notify(session, "DoDestinyUpdate", payload::destiny_update([state], false))]
            }
            Err(_) => Vec::new(),
        };
        self.with_backlog(told)
    }

    /// A command of the player's. Movement is applied from `now`; what the client is told is one update at the next stamp (none when the
    /// ship already does what it is asked), and the host is told the command was accepted, after the update.
    pub fn command(&mut self, session: SessionId, command: Command, now: i64) -> Vec<Out> {
        let code = command.code();
        if !self.pilots.contains_key(&session) {
            return vec![Out::Refused { session, command: code, reason: "NO_SHIP_IN_SPACE" }];
        }
        self.settle_pilot(session, now);
        let told = match command {
            Command::Dock { station_id } => self.dock(session, station_id, now),
            Command::WarpTo { target_id, range } => self.warp_to(session, target_id, range, now),
            Command::Jump { gate_id, destination_gate } => self.jump(session, gate_id, destination_gate, now),
            other => self.steer_pilot(session, other, now),
        };
        // the ship was flown: the undocking protection is over (a docking does not end it)
        if told.iter().any(|o| matches!(o, Out::Accepted { command, .. } if *command != 4))
            && let Some(pilot) = self.pilots.get_mut(&session)
        {
            pilot.protected_until = 0;
        }
        self.with_backlog(told)
    }

    /// Tells what happened while ships were moved on, then `told`.
    pub(crate) fn with_backlog(&mut self, told: Vec<Out>) -> Vec<Out> {
        let mut out = std::mem::take(&mut self.backlog);
        out.extend(told);
        out
    }

    /// Brings a ship's motion to `now`; what happens to it on the way (a warp's dropout, coming to rest) is told with the next call.
    pub(crate) fn settle_pilot(&mut self, session: SessionId, now: i64) {
        self.settle_pilot_after(session, now, 0);
    }

    /// As [`Scene::settle_pilot`]; a ship that follows or orbits another ship is moved after the ship it follows (`depth` stops a ring of ships
    /// that follow one another).
    fn settle_pilot_after(&mut self, session: SessionId, now: i64, depth: u8) {
        let system = Arc::clone(&self.system);
        let course_target = match self.pilots.get(&session).map(|pilot| pilot.ship.course) {
            Some(Course::Follow { target, .. } | Course::Orbit { target, .. })
                if system.body(target).is_none() =>
            {
                Some(target)
            }
            _ => None,
        };
        let followed =
            course_target.and_then(|target| self.session_of(target).filter(|other| *other != session));
        let mut ship_target = None;
        if let Some(other) = followed {
            if depth < MAX_FOLLOW_CHAIN {
                self.settle_pilot_after(other, now, depth + 1);
            }
            ship_target = self.target_of_ship(other);
        } else if let Some(wreck) = course_target.and_then(|target| self.wrecks.get(&target)) {
            // a wreck or a can does not move
            ship_target = Some(Target::at_rest(wreck.position, wreck.radius));
        }
        let Some(pilot) = self.pilots.get_mut(&session) else { return };
        let milestones = settle(pilot, &system, ship_target, now);
        for milestone in milestones {
            let told = self.tell(session, milestone);
            self.backlog.extend(told);
        }
    }

    /// Brings the motion of every ship to `now`.
    pub(crate) fn settle_all(&mut self, now: i64) {
        let sessions: Vec<SessionId> = self.pilots.keys().copied().collect();
        for session in sessions {
            self.settle_pilot(session, now);
        }
    }

    /// What is told of a milestone of a warp: to the pilot, and to the ones who watch it.
    fn tell(&mut self, session: SessionId, milestone: Milestone) -> Vec<Out> {
        let Some(pilot) = self.pilots.get(&session) else { return Vec::new() };
        let ship_id = id(pilot.ship.spec.ship_id);
        match milestone {
            Milestone::Entered { at } => {
                // the ship is in warp: those who watch it are told now (the pilot was told when it asked for the warp)
                let Course::Warp { raw_destination: r, stop_distance, factor, .. } = pilot.ship.course else {
                    return Vec::new();
                };
                let stamp = self.next_stamp(at);
                let watchers = vec![
                    warping_effect(stamp, &ship_id),
                    payload::action(stamp, "SetSpeedFraction", [ship_id.clone(), real(1.0)]),
                    payload::action(
                        stamp,
                        "WarpTo",
                        [ship_id, real(r.x), real(r.y), real(r.z), real(stop_distance), int(factor)],
                    ),
                ];
                self.tell_split(session, Vec::new(), watchers)
            }
            Milestone::Dropout { at } => {
                // the ship is set massless again, told twice: at the stamp and two on
                let stamp = self.next_stamp(at);
                let massless =
                    |stamp: u32| vec![payload::action(stamp, "SetBallMassive", [ship_id.clone(), int(0)])];
                let mut out = self.tell_all(session, massless(stamp));
                out.extend(self.tell_all(session, massless(stamp + 2)));
                out
            }
            Milestone::Rest { at, position, velocity } => {
                let stamp = self.next_stamp(at);
                let actions = vec![
                    payload::action(
                        stamp,
                        "SetBallPosition",
                        with_id(&ship_id, vector(position.x, position.y, position.z)),
                    ),
                    payload::action(
                        stamp,
                        "SetBallVelocity",
                        with_id(&ship_id, vector(velocity.x, velocity.y, velocity.z)),
                    ),
                    payload::action(stamp, "SetSpeedFraction", [ship_id.clone(), real(0.0)]),
                    payload::action(stamp, "Stop", [ship_id]),
                ];
                let mut out = self.tell_all(session, actions);
                if !crate::npc::is_npc_session(session)
                    && let Some((belt_id, position)) = self.belt_arrived_at(session, position)
                {
                    out.push(Out::Arrived { session, belt_id, position });
                }
                out
            }
        }
    }

    /// The belt a ship that came to rest at `position` has arrived at: the one it warped to, else the nearest within the landing radius (its id and
    /// where it is).
    fn belt_arrived_at(&self, session: SessionId, position: Vec3) -> Option<(i64, Vec3)> {
        let system = &self.system;
        let warped_to = self.pilots.get(&session).and_then(|pilot| pilot.warp_target);
        if let Some(belt) =
            warped_to.and_then(|id| system.body(id)).filter(|body| body.kind == BodyKind::Belt)
        {
            return Some((belt.id, belt.position));
        }
        let away = |body: &Body| {
            ((body.position.x - position.x).powi(2)
                + (body.position.y - position.y).powi(2)
                + (body.position.z - position.z).powi(2))
            .sqrt()
        };
        system
            .bodies
            .iter()
            .filter(|body| body.kind == BodyKind::Belt && away(body) <= BELT_LANDING_RADIUS + body.radius)
            .min_by(|a, b| away(a).total_cmp(&away(b)).then(a.id.cmp(&b.id)))
            .map(|body| (body.id, body.position))
    }

    /// `CmdWarpToStuff`: the ship lines up with where it is to end and goes into warp. The client is told at once (`WarpTo` with the point and
    /// the warp factor); it works out the warp itself, and this side keeps its own copy of it to know where the ship is.
    fn warp_to(&mut self, session: SessionId, target_id: i64, range: f64, now: i64) -> Vec<Out> {
        let code = Command::WarpTo { target_id, range }.code();
        let refused = |reason: &'static str| vec![Out::Refused { session, command: code, reason }];
        let system = Arc::clone(&self.system);
        let Some(pilot) = self.pilots.get(&session) else { return refused("NO_SHIP_IN_SPACE") };
        if pilot.docking.is_some() {
            return refused("DOCKING");
        }
        if pilot.jump.is_some() {
            return refused("JUMPING");
        }
        if pilot.flight.is_some() {
            return refused("IN_WARP");
        }
        // a ship that other ships' scramblers or disruptors hold cannot warp
        if pilot.incoming.scrambled() {
            return refused("WARP_SCRAMBLED");
        }
        let Some(body) = system.body(target_id) else { return refused("TARGET_NOT_FOUND") };
        let ship = &pilot.ship;
        let (raw, stop) = match warp::warp_in_point(body) {
            Some(point) => (point, range.max(0.0)),
            None if body.kind == BodyKind::Stargate => {
                let mut random = PythonRandom::new(seed_of(ship.spec.ship_id, target_id, now));
                gate::warp_landing(body, ship.position, ship.direction, range, &mut random)
            }
            None => (body.position, warp::stop_distance(body, ship.spec.radius, range)),
        };
        let toward = motion::unit(Vec3::new(
            raw.x - ship.position.x,
            raw.y - ship.position.y,
            raw.z - ship.position.z,
        ))
        .unwrap_or(ship.direction);
        let end = Vec3::new(raw.x - toward.x * stop, raw.y - toward.y * stop, raw.z - toward.z * stop);
        let total = ((end.x - ship.position.x).powi(2)
            + (end.y - ship.position.y).powi(2)
            + (end.z - ship.position.z).powi(2))
        .sqrt();
        if total < warp::MINIMUM_WARP_DISTANCE {
            return refused("WARP_DISTANCE_TOO_CLOSE");
        }

        let profile = warp::Profile::new(total, ship.spec.warp_speed_au, ship.spec.max_velocity);
        let ship_id = id(ship.spec.ship_id);
        let boosted = ship.spec.max_velocity * warp::START_SPEED_SCALE;
        let stamp = self.next_stamp(now);
        let pilot = self.pilots.get_mut(&session).expect("checked above");
        pilot.ship.course = Course::Warp {
            raw_destination: raw,
            target_point: end,
            stop_distance: stop,
            total_distance: total,
            factor: profile.factor,
            active: false,
        };
        pilot.ship.speed_fraction = 1.0;
        pilot.ship.direction = toward;
        pilot.flight = Some(Flight::begin(now, raw, stop));
        pilot.warp_target = Some(target_id);
        let own = vec![
            payload::action(stamp, "SetMaxSpeed", [ship_id.clone(), real(boosted)]),
            payload::action(
                stamp,
                "WarpTo",
                [ship_id.clone(), real(raw.x), real(raw.y), real(raw.z), real(stop), int(profile.factor)],
            ),
            warping_effect(stamp, &ship_id),
            payload::action(stamp, "SetSpeedFraction", [ship_id.clone(), real(1.0)]),
        ];
        // those who watch see the ship turn towards the place at once and go into warp when it does (`Milestone::Entered`)
        let watchers = vec![payload::action(
            stamp,
            "GotoDirection",
            with_id(&ship_id, vector(toward.x, toward.y, toward.z)),
        )];
        let mut out = self.tell_split(session, own, watchers);
        out.push(Out::Accepted { session, command: code, at: now });
        out
    }

    /// A movement command of a ship that is in the scene (settled first, at `now`).
    fn steer_pilot(&mut self, session: SessionId, command: Command, now: i64) -> Vec<Out> {
        let code = command.code();
        let target = match command {
            Command::FollowBall { target_id, .. }
            | Command::Orbit { target_id, .. }
            | Command::AlignTo { target_id } => self.target_of(target_id, session),
            _ => None,
        };
        let pilot = self.pilots.get_mut(&session).expect("the caller checked");
        if pilot.docking.is_some() {
            // a ship that is docking follows no orders
            return vec![Out::Refused { session, command: code, reason: "DOCKING" }];
        }
        if pilot.jump.is_some() {
            // nor does one that has gone into a gate
            return vec![Out::Refused { session, command: code, reason: "JUMPING" }];
        }
        if pilot.flight.is_some() {
            // a ship in warp, or lining up for it, follows none either
            return vec![Out::Refused { session, command: code, reason: "IN_WARP" }];
        }
        let ship_id = id(pilot.ship.spec.ship_id);
        match steer(&mut pilot.ship, target, command) {
            Steering::Refused(reason) => vec![Out::Refused { session, command: code, reason }],
            Steering::Nothing => vec![Out::Accepted { session, command: code, at: now }],
            Steering::Actions(actions) => {
                let stamp = self.next_stamp(now);
                let actions: Vec<Value> = actions
                    .into_iter()
                    .map(|(name, args)| {
                        let mut all = vec![ship_id.clone()];
                        all.extend(args);
                        payload::action(stamp, name, all)
                    })
                    .collect();
                let mut out = self.tell_all(session, actions);
                out.push(Out::Accepted { session, command: code, at: now });
                out
            }
        }
    }

    fn dock(&mut self, session: SessionId, station_id: i64, now: i64) -> Vec<Out> {
        let code = Command::Dock { station_id }.code();
        let Some(station) = self.system.body(station_id).filter(|b| b.kind == BodyKind::Station).cloned()
        else {
            return vec![Out::Refused { session, command: code, reason: "STATION_NOT_FOUND" }];
        };
        let pilot = self.pilots.get(&session).expect("the caller checked");
        if let Some(pending) = pilot.docking.as_ref().filter(|p| p.station_id == station_id) {
            return vec![Out::Accepted { session, command: code, at: pending.accepted_at }];
        }
        if pilot.jump.is_some() {
            return vec![Out::Refused { session, command: code, reason: "JUMPING" }];
        }
        if pilot.flight.is_some() {
            return vec![Out::Refused { session, command: code, reason: "IN_WARP" }];
        }
        let distance = surface_distance(&pilot.ship, station.position, station.radius);
        if distance > DOCKING_RANGE {
            return self.approach(session, station_id, distance, now);
        }
        let mut out = self.accept_docking(session, station_id, now);
        out.push(Out::Accepted { session, command: code, at: now });
        out
    }

    /// The ship is in range and the docking begins: the client is told, and the ship stops and waits for the docking to take its time.
    fn accept_docking(&mut self, session: SessionId, station_id: i64, now: i64) -> Vec<Out> {
        let stamp = self.next_stamp(now);
        let pilot = self.pilots.get_mut(&session).expect("the caller checked");
        let ship = &mut pilot.ship;
        ship.speed_fraction = 0.0;
        ship.course = Course::Stop;
        pilot.docking = Some(PendingDock { station_id, accepted_at: now });
        let ship_id = id(ship.spec.ship_id);
        let mut out = vec![Self::notify_charid(session, "OnDockingAccepted", vec![id(station_id)])];
        out.extend(self.tell_all(
            session,
            vec![
                payload::action(stamp, "SetBallMassive", [ship_id.clone(), int(0)]),
                payload::action(stamp, "Stop", [ship_id]),
            ],
        ));
        out
    }

    /// A docking asked for from too far: the ship goes to the station on its own (following it to the docking range) and the host is told how far
    /// off it was, for the client's message. As in the reference the docking is not done on arrival: the pilot asks again.
    fn approach(&mut self, session: SessionId, station_id: i64, distance: f64, now: i64) -> Vec<Out> {
        let code = Command::Dock { station_id }.code();
        let follow = Command::FollowBall { target_id: station_id, range: DOCKING_RANGE };
        let mut out = self.steer_pilot(session, follow, now);
        out.retain(|o| !matches!(o, Out::Accepted { .. }));
        out.push(Out::Approaching { session, command: code, distance });
        out
    }

    /// `CmdStargateJump`: the ship stops where it is and the jump-out effect begins. The ship leaves for the other system a few seconds on
    /// (the later of four seconds from now and three from the effect's stamp, the reference's rule), which [`Scene::due_jumps`] finds.
    fn jump(&mut self, session: SessionId, gate_id: i64, destination_gate: i64, now: i64) -> Vec<Out> {
        let code = Command::Jump { gate_id, destination_gate }.code();
        let refused = |reason: &'static str| vec![Out::Refused { session, command: code, reason }];
        let system = Arc::clone(&self.system);
        let Some(pilot) = self.pilots.get(&session) else { return refused("NO_SHIP_IN_SPACE") };
        if pilot.jump.is_some() {
            return refused("STARGATE_JUMP_IN_PROGRESS");
        }
        if pilot.docking.is_some() {
            return refused("DOCKING");
        }
        if pilot.flight.is_some() {
            return refused("IN_WARP");
        }
        let Some((gate, Detail::Stargate { destination_system, destination_gate: leads_to, .. })) =
            system.body(gate_id).filter(|b| b.kind == BodyKind::Stargate).map(|b| (b, &b.detail))
        else {
            return refused("STARGATE_NOT_FOUND");
        };
        if destination_gate != 0 && destination_gate != *leads_to {
            return refused("STARGATE_DESTINATION_MISMATCH");
        }
        if gate::surface_distance(gate, pilot.ship.position, pilot.ship.spec.radius) > gate::JUMP_RANGE {
            return refused("TOO_FAR_FROM_STARGATE");
        }

        let stamp = self.next_stamp(now);
        let boundary = if stamp > stamp_at(now) {
            (i64::from(stamp) + UNIX_EPOCH_SECONDS) * TICKS_PER_SECOND
        } else {
            now
        };
        let leaves_at = (now + JUMP_DELAY).max(boundary + JUMP_EFFECT_DELAY);
        let pilot = self.pilots.get_mut(&session).expect("checked above");
        let ship = &mut pilot.ship;
        if !matches!(ship.course, Course::Stop) || ship.speed_fraction > 0.0 || speed_of(ship.velocity) > 0.5
        {
            ship.course = Course::Stop;
            ship.speed_fraction = 0.0;
            ship.velocity = Vec3::ZERO;
        }
        pilot.jump = Some(PendingJump {
            gate_id,
            destination_system: *destination_system,
            destination_gate: *leads_to,
            leaves_at,
        });
        let ship_id = id(ship.spec.ship_id);
        let actions = [
            payload::action(
                stamp,
                "OnSpecialFX",
                [
                    ship_id.clone(),
                    Value::None,
                    Value::None,
                    id(gate_id),
                    Value::None,
                    text("effects.JumpOut"),
                    int(0),
                    int(1),
                    int(0),
                    int(-1),
                    Value::None,
                    Value::None,
                    int(0),
                    tuple([int(i64::from(*destination_system)), Value::None]),
                ],
            ),
            payload::action(stamp, "Stop", [ship_id]),
        ];
        let mut out = self.tell_all(session, actions.into());
        out.push(Out::Jumping {
            session,
            command: code,
            destination_system: *destination_system,
            destination_gate: *leads_to,
        });
        out
    }

    /// The ships whose jump is due at `now`, and what the ones who watched them go are told. Each ship has left this scene; the caller puts it in
    /// the scene of the system it is going to ([`Scene::arrive`]), or back ([`Scene::restore`]) when that cannot be done.
    pub fn due_jumps(&mut self, now: i64) -> (Vec<Traveller>, Vec<Out>) {
        let due: Vec<SessionId> = self
            .pilots
            .iter()
            .filter(|(_, pilot)| pilot.jump.as_ref().is_some_and(|jump| now >= jump.leaves_at))
            .map(|(session, _)| *session)
            .collect();
        let mut travellers = Vec::new();
        let mut told = Vec::new();
        for session in due {
            self.settle_pilot(session, now);
            let Some(mut pilot) = self.pilots.remove(&session) else { continue };
            let Some(jump) = pilot.jump.take() else { continue };
            let held = std::mem::take(&mut pilot.targeting);
            told.extend(self.drop_targeting(pilot.ship.spec.ship_id, &held, now));
            told.extend(self.announce_departure(session, pilot.ship.spec.ship_id, now));
            travellers.push(Traveller {
                session,
                from_system: self.system.id,
                from_gate: jump.gate_id,
                to_system: jump.destination_system,
                to_gate: jump.destination_gate,
                pilot,
            });
        }
        (travellers, told)
    }

    /// A ship that has jumped appears in front of the gate it came out of, at rest, and is bound like any ship that arrives (the client binds
    /// the ballpark of the new system when its session has changed). Gives the ship back when this system has no such gate.
    pub fn arrive(&mut self, traveller: Traveller, now: i64) -> Result<Vec<Out>, Box<Traveller>> {
        let system = Arc::clone(&self.system);
        let Some(gate) = system.body(traveller.to_gate).filter(|b| b.kind == BodyKind::Stargate) else {
            return Err(Box::new(traveller));
        };
        if self.pilots.contains_key(&traveller.session) {
            return Err(Box::new(traveller));
        }
        let Traveller { session, mut pilot, .. } = traveller;
        let mut random = PythonRandom::new(seed_of(pilot.ship.spec.ship_id, gate.id, now));
        let (position, direction) = gate::arrival(gate, pilot.ship.spec.radius, &mut random);
        pilot.ship.position = position;
        pilot.ship.direction = direction;
        pilot.ship.velocity = Vec3::ZERO;
        pilot.ship.speed_fraction = 0.0;
        pilot.ship.course = Course::Stop;
        pilot.settled_at = now;
        pilot.docking = None;
        pilot.flight = None;
        pilot.jump = None;
        pilot.jumped_in = true;
        // the client binds the ballpark of this system afresh, and is then sent what is in view
        pilot.bound = false;
        pilot.sees.clear();
        pilot.entered = self.next_entry();
        self.pilots.insert(session, pilot);
        Ok(self.announce_arrival(session, now))
    }

    /// A ship whose jump could not be finished goes on in the scene it left (and those who saw it go are shown it again).
    pub fn restore(&mut self, traveller: Traveller, now: i64) -> Vec<Out> {
        let Traveller { session, mut pilot, .. } = traveller;
        pilot.entered = self.next_entry();
        self.pilots.insert(session, pilot);
        self.announce_arrival(session, now)
    }

    fn notify_charid(session: SessionId, name: &'static str, args: Vec<Value>) -> Out {
        Out::Notify { session, name, id_type: "charid", args }
    }

    /// The numbers of a ship in space changed (a skill finished, a module went on): the client is told the ones that differ.
    pub fn respec(&mut self, session: SessionId, spec: ShipSpec, now: i64) -> Vec<Out> {
        if !self.pilots.contains_key(&session) {
            return Vec::new();
        }
        self.settle_pilot(session, now);
        let stamp = self.next_stamp(now);
        let Some(pilot) = self.pilots.get_mut(&session) else { return Vec::new() };
        let old = std::mem::replace(&mut pilot.ship.spec, spec);
        let new = &pilot.ship.spec;
        pilot.ship.health.respec(new.vitals, new.shield_recharge_ms, now);
        let security_changed = new.security_status != old.security_status;
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
        let mut told = if actions.is_empty() { Vec::new() } else { self.tell_all(session, actions) };
        // a security status that changed is in the slim item: the pilot and whoever sees the ship are shown it again (the reference does, when a crime costs a pilot some)
        if security_changed {
            let ship = &self.pilots[&session].ship;
            let change = payload::ship_slim_item_change(stamp, ship);
            told.extend(self.tell_all(session, vec![change]));
        }
        self.with_backlog(told)
    }

    /// Finishes what is due at `now`: a docking that has taken its time takes the ship out of space (the ones who watched it are told it is gone),
    /// and ships that have come into view or left it are told of one another.
    pub fn poll(&mut self, now: i64) -> Vec<Out> {
        self.settle_all(now);
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
        let mut told = Vec::new();
        for (session, station_id) in due {
            if let Some(pilot) = self.pilots.remove(&session) {
                told.push(Out::Docked { session, station_id, condition: Self::condition_of(&pilot, now) });
                told.extend(self.drop_targeting(pilot.ship.spec.ship_id, &pilot.targeting, now));
                told.extend(self.announce_departure(session, pilot.ship.spec.ship_id, now));
            }
        }
        told.extend(self.tick_brains(now));
        told.extend(self.settle_targets(now));
        told.extend(self.settle_modules(now));
        told.extend(self.advance_missiles(now));
        told.extend(self.reconcile(now));
        told.extend(self.reconcile_wrecks(now));
        told.extend(self.expire_wrecks(now));
        self.with_backlog(told)
    }
}

/// The seed of the draws that pick a point for a ship at a gate: what and where and when, so the same calls at the same times give the same
/// points (the reference draws them from the clock).
fn seed_of(ship_id: i64, gate_id: i64, now: i64) -> u64 {
    (ship_id as u64).rotate_left(11) ^ (gate_id as u64).rotate_left(37) ^ (now as u64)
}

/// The effect the client plays on a ship that goes into warp.
fn warping_effect(stamp: u32, ship_id: &Value) -> Value {
    payload::action(
        stamp,
        "OnSpecialFX",
        [
            ship_id.clone(),
            Value::None,
            Value::None,
            Value::None,
            Value::None,
            Value::Str("effects.Warping".into()),
            int(0),
            int(1),
            int(0),
            int(-1),
            Value::None,
            Value::None,
            int(0),
            Value::None,
        ],
    )
}

fn with_id(ship_id: &Value, rest: [Value; 3]) -> Vec<Value> {
    let mut args = vec![ship_id.clone()];
    args.extend(rest);
    args
}

/// Where a ship comes out of a station and which way it faces: the station type's undock point that suits the ship, turned by the station's
/// orientation (the reference's own table); a type it does not know puts the ship a third of the station's radius from it, out along the
/// station's orientation or away from the star.
fn undock_place(station: &crate::world::Body, ship_group: i32) -> (Vec3, Vec3) {
    let rotation = crate::slim::station_rotation(station.id).unwrap_or([0.0; 3]);
    if let Some(point) = crate::locators::undock_point(station.type_id, ship_group, rotation) {
        let at = Vec3::new(
            station.position.x + point.offset.x,
            station.position.y + point.offset.y,
            station.position.z + point.offset.z,
        );
        return (at, motion::unit(point.direction).unwrap_or(point.direction));
    }
    let direction = undock_direction(station.id, station.position);
    let distance = UNDOCK_MIN_DISTANCE.max(station.radius * UNDOCK_RADIUS_SHARE);
    let at = Vec3::new(
        station.position.x + direction.x * distance,
        station.position.y + direction.y * distance,
        station.position.z + direction.z * distance,
    );
    (at, direction)
}

/// Which way a ship leaves a station with no known undock point: the station's own orientation when it has one, otherwise away from the star.
fn undock_direction(station_id: i64, position: Vec3) -> Vec3 {
    if let Some(angles) = crate::slim::station_rotation(station_id) {
        let (yaw, pitch) = (angles[0].to_radians(), angles[1].to_radians());
        return Vec3::new(yaw.sin() * pitch.cos(), -pitch.sin(), yaw.cos() * pitch.cos());
    }
    motion::unit(position).unwrap_or(Vec3::new(0.0, 0.0, 1.0))
}

fn surface_distance(ship: &Ship, station: Vec3, station_radius: f64) -> f64 {
    let (dx, dy, dz) =
        (ship.position.x - station.x, ship.position.y - station.y, ship.position.z - station.z);
    ((dx * dx + dy * dy + dz * dz).sqrt() - ship.spec.radius - station_radius).max(0.0)
}

/// Brings a ship's position and velocity to `now`: it eases towards the speed it is asked for, the way the client integrates it, and a ship
/// that follows or orbits a body is steered by where the body is.
fn settle(pilot: &mut Pilot, system: &SolarSystem, ship_target: Option<Target>, now: i64) -> Vec<Milestone> {
    let from = pilot.settled_at;
    pilot.settled_at = now;
    let seconds = (now - from) as f64 / TICKS_PER_SECOND as f64;
    if seconds <= 0.0 {
        return Vec::new();
    }
    if let Some(flight) = pilot.flight.as_mut() {
        let advance = flight.advance(&mut pilot.ship, from, now);
        if let Some(rested) = advance.finished_at {
            // the warp is over: what is left of the time is a stopped ship's
            pilot.flight = None;
            pilot.ship.course = Course::Stop;
            pilot.ship.speed_fraction = 0.0;
            motion::advance(&mut pilot.ship, (now - rested) as f64 / TICKS_PER_SECOND as f64, None);
        }
        return advance.milestones;
    }
    let target = match pilot.ship.course {
        Course::Follow { target, .. } | Course::Orbit { target, .. } => ship_target
            .or_else(|| system.body(target).map(|body| Target::at_rest(body.position, body.radius))),
        Course::Goto | Course::Stop | Course::Warp { .. } => None,
    };
    motion::advance(&mut pilot.ship, seconds, target);
    Vec::new()
}

/// What a movement command comes to once the ship has looked at it.
enum Steering {
    /// Accepted, and the ship already does it: the client has nothing to be told.
    Nothing,
    /// Accepted: the actions of one update, each with what follows the ship's id.
    Actions(Vec<(&'static str, Vec<Value>)>),
    Refused(&'static str),
}

/// The heading of a ship as the client's action names it.
fn heading_name(ship: &Ship) -> &'static str {
    match ship.course {
        Course::Goto => "GotoDirection",
        Course::Stop => "Stop",
        Course::Follow { .. } => "FollowBall",
        Course::Orbit { .. } => "Orbit",
        Course::Warp { .. } => "WarpTo",
    }
}

/// The arguments of that action, the ship's id first.
fn heading_args(ship: &Ship) -> Vec<Value> {
    let ship_id = id(ship.spec.ship_id);
    match ship.course {
        Course::Goto => {
            with_id(&ship_id, vector(ship.direction.x, ship.direction.y, ship.direction.z)).to_vec()
        }
        Course::Stop => vec![ship_id],
        Course::Follow { target, range } => vec![ship_id, id(target), int(whole(range))],
        Course::Orbit { target, distance } => vec![ship_id, id(target), int(whole(distance))],
        Course::Warp { raw_destination: r, stop_distance, factor, .. } => {
            vec![ship_id, real(r.x), real(r.y), real(r.z), real(stop_distance), int(factor)]
        }
    }
}

/// A distance as the client's actions carry it: whole metres, cut off.
fn whole(metres: f64) -> i64 {
    metres.trunc().clamp(i32::MIN as f64, i32::MAX as f64) as i64
}

fn speed_of(velocity: Vec3) -> f64 {
    (velocity.x * velocity.x + velocity.y * velocity.y + velocity.z * velocity.z).sqrt()
}

/// The speed a ship that is not moving at one has to be asked for to move: what it had, or `otherwise`.
fn resumed_speed(ship: &Ship, otherwise: f64) -> (f64, bool) {
    let before = ship.speed_fraction;
    let after = if before > 0.0 { before } else { otherwise };
    (after, (after - before).abs() > 1e-6)
}

fn speed_action(ship: &Ship, changed: bool, actions: &mut Vec<(&'static str, Vec<Value>)>) {
    if changed {
        actions.push(("SetSpeedFraction", vec![real(ship.speed_fraction)]));
    }
}

/// Puts a movement command into effect on a ship whose motion is settled, and says what the client is to be told.
/// `target` is what the command's target is (a body or another ship), when the scene has it.
fn steer(ship: &mut Ship, target: Option<Target>, command: Command) -> Steering {
    match command {
        Command::SetSpeedFraction(fraction) => {
            let fraction = if fraction.is_finite() { fraction.clamp(0.0, 1.0) } else { 0.0 };
            if fraction <= 0.0 {
                return stop(ship);
            }
            ship.speed_fraction = fraction;
            if matches!(ship.course, Course::Stop) {
                ship.course = Course::Goto;
            }
            Steering::Actions(vec![("SetSpeedFraction", vec![real(fraction)])])
        }
        Command::GotoDirection(direction) => {
            let Some(unit) = motion::unit(direction) else { return Steering::Refused("NO_DIRECTION") };
            let stopped = ship.speed_fraction <= 0.0 || matches!(ship.course, Course::Stop);
            let same = matches!(ship.course, Course::Goto)
                && ship.direction.x * unit.x + ship.direction.y * unit.y + ship.direction.z * unit.z
                    >= 0.999_999;
            if same && !stopped {
                return Steering::Nothing;
            }
            ship.direction = unit;
            ship.course = Course::Goto;
            if stopped {
                ship.speed_fraction = 1.0;
            }
            let mut actions = vec![("GotoDirection", vector(unit.x, unit.y, unit.z).to_vec())];
            speed_action(ship, stopped, &mut actions);
            Steering::Actions(actions)
        }
        Command::Stop => stop(ship),
        Command::Dock { .. } | Command::WarpTo { .. } | Command::Jump { .. } => {
            Steering::Refused("NOT_A_MOVEMENT")
        }
        Command::FollowBall { target_id, range } => {
            if !range.is_finite() {
                return Steering::Refused("BAD_RANGE");
            }
            if target.is_none() {
                return Steering::Refused("UNKNOWN_TARGET");
            }
            let range = range.max(0.0);
            if matches!(ship.course, Course::Follow { target, range: had } if target == target_id && (had - range).abs() < 1.0)
            {
                return Steering::Nothing;
            }
            let (speed, changed) = resumed_speed(ship, 1.0);
            ship.course = Course::Follow { target: target_id, range };
            ship.speed_fraction = speed;
            let mut actions = vec![("FollowBall", vec![id(target_id), int(whole(range))])];
            speed_action(ship, changed, &mut actions);
            Steering::Actions(actions)
        }
        Command::Orbit { target_id, distance } => {
            if !distance.is_finite() {
                return Steering::Refused("BAD_RANGE");
            }
            if target.is_none() {
                return Steering::Refused("UNKNOWN_TARGET");
            }
            let distance = distance.max(0.0);
            if matches!(ship.course, Course::Orbit { target, distance: had } if target == target_id && (had - distance).abs() < 1.0)
                && ship.speed_fraction > 0.0
            {
                return Steering::Nothing;
            }
            let (speed, changed) = resumed_speed(ship, 1.0);
            ship.course = Course::Orbit { target: target_id, distance };
            ship.speed_fraction = speed;
            let mut actions = vec![("Orbit", vec![id(target_id), int(whole(distance))])];
            speed_action(ship, changed, &mut actions);
            Steering::Actions(actions)
        }
        Command::AlignTo { .. } => {
            let Some(target) = target else { return Steering::Refused("UNKNOWN_TARGET") };
            let towards = Vec3::new(
                target.position.x - ship.position.x,
                target.position.y - ship.position.y,
                target.position.z - ship.position.z,
            );
            let direction = motion::unit(towards).unwrap_or(ship.direction);
            let (speed, changed) = resumed_speed(ship, 0.75);
            ship.direction = direction;
            ship.course = Course::Goto;
            ship.speed_fraction = speed;
            let mut actions = vec![("GotoDirection", vector(direction.x, direction.y, direction.z).to_vec())];
            speed_action(ship, changed, &mut actions);
            Steering::Actions(actions)
        }
    }
}

/// A ship told to stop: it says so unless it stands still already.
fn stop(ship: &mut Ship) -> Steering {
    let standing =
        matches!(ship.course, Course::Stop) && ship.speed_fraction <= 0.0 && speed_of(ship.velocity) < 0.1;
    ship.course = Course::Stop;
    ship.speed_fraction = 0.0;
    if standing { Steering::Nothing } else { Steering::Actions(vec![("Stop", Vec::new())]) }
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
            max_target_range: 29_700.0,
            max_locked_targets: 4,
            scan_resolution: 660.0,
            signature_radius: 28.0,
            capacitor_capacity: 149.5,
            capacitor_recharge_s: 55.25,
            sensor_strength: [0.0, 0.0, 0.0, 0.0],
            ecm_resistance: 1.0,
            // an Ibis's points, with resistances made up so that each kind of damage does something different to each layer
            vitals: crate::health::Vitals {
                shield_capacity: 220.0,
                armor_hp: 137.5,
                structure_hp: 201.25,
                resonances: crate::health::Resonances {
                    shield: [0.0, 0.8, 0.6, 0.5],
                    armor: [0.5, 0.35, 0.25, 0.1],
                    structure: [0.33, 0.33, 0.33, 0.33],
                },
            },
            condition: Condition::FULL,
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

    /// The scene of `system()` with the station of the given type.
    fn scene_with_station_type(station_type: i32, radius: f64) -> Scene {
        let mut world = (*system()).clone();
        world.bodies[0].type_id = station_type;
        world.bodies[0].radius = radius;
        let mut scene = Scene::new(Arc::new(world));
        scene.undock(SESSION, spec(), STATION, NOON).unwrap();
        scene
    }

    #[test]
    fn an_undocked_ship_comes_out_at_the_undock_point_of_its_stations_type_and_moves_away_at_top_speed() {
        let mut scene = scene(); // a Caldari Trading Station (1531), a ship of the rookie group
        let ship = scene.ship(SESSION, NOON).unwrap().clone();
        let point = crate::locators::undock_point(1531, spec().group_id, [0.0; 3]).unwrap();
        assert_eq!(
            (ship.position.x, ship.position.y, ship.position.z),
            (1_000_000.0 + point.offset.x, point.offset.y, point.offset.z)
        );
        // it faces the point's direction (normalized: the table's has five decimals)
        let facing = motion::unit(point.direction).unwrap();
        assert_eq!((ship.direction.x, ship.direction.y, ship.direction.z), (facing.x, facing.y, facing.z));
        assert!((speed(&ship) - 339.25).abs() < 1e-9);
        assert_eq!((ship.course, ship.speed_fraction), (Course::Goto, 1.0));
    }

    #[test]
    fn a_ship_of_a_big_hull_comes_out_at_a_point_for_big_hulls_and_a_station_turned_by_its_rotation_turns_the_point()
     {
        let mut big = spec();
        big.group_id = 27; // a battleship
        let mut world = (*system()).clone();
        world.bodies[0].type_id = 92_885; // a station type with points of more than one size
        let mut scene = Scene::new(Arc::new(world));
        scene.undock(SESSION, big, STATION, NOON).unwrap();
        let large = crate::locators::undock_point(92_885, 27, [0.0; 3]).unwrap();
        let small = crate::locators::undock_point(92_885, 237, [0.0; 3]).unwrap();
        assert_ne!(large.offset, small.offset, "the table has points of more than one size for this type");
        let ship = scene.ship(SESSION, NOON).unwrap();
        assert_eq!(ship.position.y, large.offset.y);

        // the station the reference authors an orientation for (the recorded one, 305 degrees of yaw) turns the point
        let mut turned = (*system()).clone();
        turned.bodies[0].id = 60_015_249;
        turned.bodies[0].type_id = 92_885;
        let mut scene = Scene::new(Arc::new(turned));
        scene.undock(SESSION, spec(), 60_015_249, NOON).unwrap();
        let ship = scene.ship(SESSION, NOON).unwrap();
        assert!(
            (ship.direction.x + 0.8191520442889917).abs() < 1e-9
                && (ship.direction.z - 0.5735764363510462).abs() < 1e-9
        );
        assert!((ship.position.x - (1_000_000.0 - 19_613.851392826436)).abs() < 1e-6, "{}", ship.position.x);
    }

    #[test]
    fn a_ship_leaves_a_station_of_a_type_the_table_does_not_know_a_third_of_its_radius_out_along_the_star_line()
     {
        let mut scene = scene_with_station_type(1, 1_000.0);
        let ship = scene.ship(SESSION, NOON).unwrap().clone();
        let gap =
            ((ship.position.x - 1_000_000.0).powi(2) + ship.position.y.powi(2) + ship.position.z.powi(2))
                .sqrt();
        assert!((gap - 2_000.0).abs() < 1e-6, "{gap}");
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
        assert_eq!(action_names(&out[0]), ["FollowBall"], "it goes there by itself");
        assert_eq!(out[1], Out::Approaching { session: SESSION, command: 4, distance: 10_000.0 });
        assert_eq!(out.len(), 2);
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
        assert_eq!(
            scene.poll(NOON + seconds(4)),
            [Out::Docked { session: SESSION, station_id: STATION, condition: Condition::FULL }]
        );
        assert!(!scene.contains(SESSION));
        assert!(scene.poll(NOON + seconds(9)).is_empty(), "docked once");
    }

    #[test]
    fn a_docking_asked_for_again_is_accepted_with_the_first_time_and_orders_are_ignored_meanwhile() {
        let mut scene = near_the_station();
        scene.command(SESSION, Command::Dock { station_id: STATION }, NOON);
        let again = scene.command(SESSION, Command::Dock { station_id: STATION }, NOON + seconds(1));
        assert_eq!(again, [Out::Accepted { session: SESSION, command: 4, at: NOON }]);
        assert_eq!(
            scene.command(SESSION, Command::SetSpeedFraction(1.0), NOON + seconds(1)),
            [Out::Refused { session: SESSION, command: 1, reason: "DOCKING" }]
        );
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
        assert!(
            matches!(dock_from(2_500.5).last(), Some(Out::Approaching { distance, .. }) if *distance == 2_500.5)
        );
    }

    #[test]
    fn a_ship_comes_out_of_a_big_station_within_docking_range_of_it_as_the_reference_ships_did() {
        // the docking range is measured from a sphere of the station's radius, and the undock points are inside it
        for station_type in [1531, 92_885, 1, 54] {
            let mut scene = scene_with_station_type(station_type, 70_000.0);
            let out = scene.command(SESSION, Command::Dock { station_id: STATION }, NOON);
            assert!(matches!(out.last(), Some(Out::Accepted { .. })), "type {station_type}: {out:?}");
        }
    }

    /// The arguments (without the ship's id) of the actions in an update, by name.
    fn actions_of(out: &Out) -> Vec<(String, Vec<Value>)> {
        let Out::Notify { args, .. } = out else { return Vec::new() };
        let Some(Value::List(actions)) = args.first() else { return Vec::new() };
        actions
            .iter()
            .map(|a| {
                let Value::Tuple(pair) = a else { panic!("{a:?}") };
                let Value::Tuple(inner) = &pair[1] else { panic!("{:?}", pair[1]) };
                let Value::Str(name) = &inner[0] else { panic!("{:?}", inner[0]) };
                let Value::Tuple(args) = &inner[1] else { panic!("{:?}", inner[1]) };
                (name.to_string(), args[1..].to_vec())
            })
            .collect()
    }

    fn accepted(command: u8, at: i64) -> Out {
        Out::Accepted { session: SESSION, command, at }
    }

    #[test]
    fn a_ship_told_to_follow_says_so_and_a_repeat_says_nothing() {
        let mut scene = scene();
        let out = scene.command(SESSION, Command::FollowBall { target_id: STATION, range: 3_000.0 }, NOON);
        assert_eq!(
            actions_of(&out[0]),
            [("FollowBall".to_string(), vec![Value::Int(STATION), Value::Int(3_000)])]
        );
        assert_eq!(out[1], accepted(5, NOON));
        assert_eq!(out.len(), 2);
        // the same again, or within a metre of it, is done already
        let again = scene.command(
            SESSION,
            Command::FollowBall { target_id: STATION, range: 3_000.5 },
            NOON + seconds(1),
        );
        assert_eq!(again, [accepted(5, NOON + seconds(1))]);
        // another range is a new order, and a fraction of a metre is cut off
        let other = scene.command(
            SESSION,
            Command::FollowBall { target_id: STATION, range: 1_000.9 },
            NOON + seconds(2),
        );
        assert_eq!(
            actions_of(&other[0]),
            [("FollowBall".to_string(), vec![Value::Int(STATION), Value::Int(1_000)])]
        );
    }

    #[test]
    fn a_ship_that_stands_still_is_set_going_by_the_command_that_follows_orbits_or_aligns() {
        let stopped = || {
            let mut scene = scene();
            scene.command(SESSION, Command::Stop, NOON);
            scene
        };
        let later = NOON + seconds(120);
        let speed_set = |out: &[Out]| {
            actions_of(&out[0]).into_iter().find(|(name, _)| name == "SetSpeedFraction").map(|(_, a)| a)
        };

        let follow =
            stopped().command(SESSION, Command::FollowBall { target_id: STATION, range: 1_000.0 }, later);
        assert_eq!(action_names(&follow[0]), ["FollowBall", "SetSpeedFraction"]);
        assert_eq!(speed_set(&follow), Some(vec![Value::Real(1.0)]));

        let orbit =
            stopped().command(SESSION, Command::Orbit { target_id: STATION, distance: 5_000.0 }, later);
        assert_eq!(action_names(&orbit[0]), ["Orbit", "SetSpeedFraction"]);
        assert_eq!(speed_set(&orbit), Some(vec![Value::Real(1.0)]));

        // aligning starts at three quarters
        let align = stopped().command(SESSION, Command::AlignTo { target_id: STATION }, later);
        assert_eq!(action_names(&align[0]), ["GotoDirection", "SetSpeedFraction"]);
        assert_eq!(speed_set(&align), Some(vec![Value::Real(0.75)]));

        // a ship already moving keeps its speed and says nothing of it
        let moving = scene().command(SESSION, Command::AlignTo { target_id: STATION }, later);
        assert_eq!(action_names(&moving[0]), ["GotoDirection"]);
    }

    #[test]
    fn aligning_points_the_ship_at_the_body() {
        let mut scene = scene();
        let at = NOON + seconds(30);
        let position = scene.ship(SESSION, at).unwrap().position;
        let out = scene.command(SESSION, Command::AlignTo { target_id: STATION }, at);
        let (_, args) = actions_of(&out[0]).remove(0);
        let toward = motion::unit(Vec3::new(1_000_000.0 - position.x, -position.y, -position.z)).unwrap();
        let Some(Value::Real(x)) = args.first() else { panic!("{args:?}") };
        assert!((x - toward.x).abs() < 1e-12, "{x} against {}", toward.x);
        let ship = scene.ship(SESSION, at).unwrap();
        assert_eq!((ship.direction.x, ship.direction.y, ship.direction.z), (toward.x, toward.y, toward.z));
        assert_eq!(ship.course, Course::Goto);
    }

    #[test]
    fn a_range_below_nothing_is_nothing() {
        let mut scene = scene();
        let out = scene.command(SESSION, Command::FollowBall { target_id: STATION, range: -100.0 }, NOON);
        assert_eq!(
            actions_of(&out[0]),
            [("FollowBall".to_string(), vec![Value::Int(STATION), Value::Int(0)])]
        );
        let out =
            scene.command(SESSION, Command::Orbit { target_id: STATION, distance: -1.0 }, NOON + seconds(1));
        assert_eq!(actions_of(&out[0]), [("Orbit".to_string(), vec![Value::Int(STATION), Value::Int(0)])]);
        assert_eq!(
            scene.ship(SESSION, NOON + seconds(1)).unwrap().course,
            Course::Orbit { target: STATION, distance: 0.0 }
        );
    }

    const PLANET: i64 = 40_000_001;

    /// The test system with a planet 0.3 AU from the star, far from the station.
    fn scene_with_a_planet() -> Scene {
        let mut world = (*system()).clone();
        world.bodies.push(Body {
            id: PLANET,
            type_id: 2015,
            group_id: 7,
            category_id: 2,
            owner_id: 0,
            name: "Testing III".to_string(),
            kind: BodyKind::Planet,
            position: Vec3::new(45_000_000_000.0, 0.0, 20_000_000_000.0),
            radius: 6_000_000.0,
            graphic_id: None,
            detail: Detail::None,
        });
        let mut scene = Scene::new(Arc::new(world));
        scene.undock(SESSION, spec(), STATION, NOON).unwrap();
        scene
    }

    #[test]
    fn a_warp_to_what_is_not_there_or_too_near_is_refused() {
        let mut scene = scene_with_a_planet();
        let refused = |reason| vec![Out::Refused { session: SESSION, command: 8, reason }];
        assert_eq!(
            scene.command(SESSION, Command::WarpTo { target_id: 42, range: 0.0 }, NOON),
            refused("TARGET_NOT_FOUND")
        );
        // the station is a thousand kilometres away; 100 km off it, a warp there would end within 150 km of the ship
        scene.pilots.get_mut(&SESSION).unwrap().ship.position = Vec3::new(1_000_000.0 - 100_000.0, 0.0, 0.0);
        assert_eq!(
            scene.command(SESSION, Command::WarpTo { target_id: STATION, range: 0.0 }, NOON),
            refused("WARP_DISTANCE_TOO_CLOSE")
        );
        // and nothing was set going
        assert!(!matches!(scene.ship(SESSION, NOON).unwrap().course, Course::Warp { .. }));
    }

    #[test]
    fn a_ship_lining_up_for_warp_or_in_it_takes_no_orders() {
        let mut scene = scene_with_a_planet();
        let out = scene.command(SESSION, Command::WarpTo { target_id: PLANET, range: 0.0 }, NOON);
        assert_eq!(action_names(&out[0]), ["SetMaxSpeed", "WarpTo", "OnSpecialFX", "SetSpeedFraction"]);
        for command in [
            Command::Stop,
            Command::SetSpeedFraction(0.5),
            Command::GotoDirection(Vec3::new(0.0, 1.0, 0.0)),
            Command::Orbit { target_id: STATION, distance: 5_000.0 },
            Command::Dock { station_id: STATION },
            Command::WarpTo { target_id: PLANET, range: 0.0 },
        ] {
            let told = scene.command(SESSION, command, NOON + seconds(2));
            assert_eq!(
                told,
                [Out::Refused { session: SESSION, command: command.code(), reason: "IN_WARP" }],
                "{command:?}"
            );
        }
        // it is told to the client as a ball that is turning to warp, and, once in warp, as one in warp
        let bound = scene.bind(SESSION, NOON + seconds(3));
        assert_eq!(action_names(&bound[3]).last().map(String::as_str), Some("WarpTo"));
    }

    #[test]
    fn a_warp_ends_with_the_ship_at_rest_by_the_planet_and_the_pilot_may_order_it_about_again() {
        let mut scene = scene_with_a_planet();
        let start = scene.ship(SESSION, NOON).unwrap().position;
        scene.command(SESSION, Command::WarpTo { target_id: PLANET, range: 0.0 }, NOON);
        let mut told = Vec::new();
        let mut t = NOON;
        while t < NOON + seconds(200) {
            t += 2_500_000;
            told.extend(scene.poll(t));
        }
        let names: Vec<String> =
            told.iter().filter(|o| matches!(o, Out::Notify { .. })).flat_map(action_names).collect();
        assert_eq!(
            names,
            [
                "SetBallMassive",
                "SetBallMassive",
                "SetBallPosition",
                "SetBallVelocity",
                "SetSpeedFraction",
                "Stop"
            ]
        );
        let ship = scene.ship(SESSION, t).unwrap().clone();
        assert_eq!(ship.course, Course::Stop);
        let point = crate::warp::warp_in_point(scene.system().body(PLANET).unwrap()).unwrap();
        let apart = ((ship.position.x - point.x).powi(2)
            + (ship.position.y - point.y).powi(2)
            + (ship.position.z - point.z).powi(2))
        .sqrt();
        assert!(apart < 2_000.0, "{apart} m from the point the client warps in at");
        assert!(
            ((point.x - start.x).powi(2) + (point.z - start.z).powi(2)).sqrt() > 1.0e10,
            "it went a long way"
        );
        // it takes orders again, and it is no longer in warp for another warp: the planet is where it stands
        let told = scene.command(SESSION, Command::SetSpeedFraction(1.0), t);
        assert_eq!(action_names(&told[0]), ["SetSpeedFraction"]);
        let told = scene.command(SESSION, Command::WarpTo { target_id: PLANET, range: 0.0 }, t + seconds(1));
        assert_eq!(told, [Out::Refused { session: SESSION, command: 8, reason: "WARP_DISTANCE_TOO_CLOSE" }]);
    }

    #[test]
    fn a_ship_that_stands_still_enters_warp_only_when_it_has_speed_enough_and_faces_the_place() {
        let mut scene = scene_with_a_planet();
        let planet = scene.system().body(PLANET).unwrap().position;
        {
            let ship = &mut scene.pilots.get_mut(&SESSION).unwrap().ship;
            let toward =
                Vec3::new(planet.x - ship.position.x, planet.y - ship.position.y, planet.z - ship.position.z);
            ship.direction = motion::unit(toward).unwrap();
            ship.velocity = Vec3::ZERO;
            ship.speed_fraction = 0.0;
            ship.course = Course::Stop;
        }
        scene.command(SESSION, Command::WarpTo { target_id: PLANET, range: 0.0 }, NOON);
        let entered = |scene: &mut Scene, seconds_after: i64| {
            matches!(
                scene.ship(SESSION, NOON + seconds(seconds_after)).unwrap().course,
                Course::Warp { active: true, .. }
            )
        };
        // its speed builds with the time it takes a ship to get to 75 % (1.39 times its reaction time: about 6.5 s here)
        assert!(!entered(&mut scene, 3));
        assert!(!entered(&mut scene, 6));
        assert!(entered(&mut scene, 8));
    }

    #[test]
    fn what_happened_to_a_ship_while_its_motion_was_worked_out_is_told_before_the_answer_to_the_next_call() {
        let mut scene = scene_with_a_planet();
        scene.command(SESSION, Command::WarpTo { target_id: PLANET, range: 0.0 }, NOON);
        // no poll in between: the next call finds the ship at rest and says so first
        let told = scene.command(SESSION, Command::SetSpeedFraction(1.0), NOON + seconds(600));
        let names: Vec<Vec<String>> =
            told.iter().filter(|o| matches!(o, Out::Notify { .. })).map(action_names).collect();
        assert_eq!(
            names,
            [
                vec!["SetBallMassive"],
                vec!["SetBallMassive"],
                vec!["SetBallPosition", "SetBallVelocity", "SetSpeedFraction", "Stop"],
                vec!["SetSpeedFraction"]
            ]
        );
    }

    #[test]
    fn a_ship_told_to_orbit_says_so_and_a_repeat_says_nothing() {
        let mut scene = scene();
        let out = scene.command(SESSION, Command::Orbit { target_id: STATION, distance: 5_000.0 }, NOON);
        assert_eq!(
            actions_of(&out[0]),
            [("Orbit".to_string(), vec![Value::Int(STATION), Value::Int(5_000)])]
        );
        assert_eq!(out[1], accepted(6, NOON));
        let again = scene.command(
            SESSION,
            Command::Orbit { target_id: STATION, distance: 5_000.0 },
            NOON + seconds(1),
        );
        assert_eq!(again, [accepted(6, NOON + seconds(1))]);
        let closer = scene.command(
            SESSION,
            Command::Orbit { target_id: STATION, distance: 2_500.7 },
            NOON + seconds(2),
        );
        assert_eq!(
            actions_of(&closer[0]),
            [("Orbit".to_string(), vec![Value::Int(STATION), Value::Int(2_500)])]
        );
    }

    #[test]
    fn a_target_that_is_not_there_or_a_range_that_is_no_number_is_refused() {
        let mut scene = scene();
        let refused = |reason| vec![Out::Refused { session: SESSION, command: 0, reason }];
        let mut check = |command: Command, reason: &'static str| {
            let mut expected = refused(reason);
            if let Out::Refused { command: code, .. } = &mut expected[0] {
                *code = command.code();
            }
            assert_eq!(scene.command(SESSION, command, NOON), expected);
        };
        check(Command::FollowBall { target_id: 42, range: 500.0 }, "UNKNOWN_TARGET");
        check(Command::Orbit { target_id: 42, distance: 500.0 }, "UNKNOWN_TARGET");
        check(Command::AlignTo { target_id: 42 }, "UNKNOWN_TARGET");
        check(Command::FollowBall { target_id: STATION, range: f64::NAN }, "BAD_RANGE");
        check(Command::Orbit { target_id: STATION, distance: f64::INFINITY }, "BAD_RANGE");
        // and nothing changed
        assert_eq!(scene.ship(SESSION, NOON).unwrap().course, Course::Goto);
    }

    #[test]
    fn a_ship_that_stands_still_is_not_told_to_stop_again() {
        let mut scene = scene();
        let first = scene.command(SESSION, Command::Stop, NOON);
        assert_eq!(action_names(&first[0]), ["Stop"]);
        // it has not slowed yet, so it is told again; a minute later it stands
        assert_eq!(action_names(&scene.command(SESSION, Command::Stop, NOON + seconds(1))[0]), ["Stop"]);
        let later = scene.command(SESSION, Command::Stop, NOON + seconds(120));
        assert_eq!(later, [accepted(3, NOON + seconds(120))]);
        // an order to go at no speed at all is an order to stop
        let none = scene.command(SESSION, Command::SetSpeedFraction(0.0), NOON + seconds(121));
        assert_eq!(none, [accepted(1, NOON + seconds(121))]);
    }

    #[test]
    fn a_direction_given_to_a_ship_that_stands_sets_it_going_and_the_same_direction_twice_says_nothing() {
        let mut scene = scene();
        let west = Vec3::new(-1.0, 0.0, 0.0);
        let first = scene.command(SESSION, Command::GotoDirection(west), NOON);
        assert_eq!(action_names(&first[0]), ["GotoDirection"], "it is going already");
        let again = scene.command(SESSION, Command::GotoDirection(west), NOON + seconds(1));
        assert_eq!(again, [accepted(2, NOON + seconds(1))]);

        scene.command(SESSION, Command::Stop, NOON + seconds(2));
        let restart = scene.command(SESSION, Command::GotoDirection(west), NOON + seconds(3));
        assert_eq!(action_names(&restart[0]), ["GotoDirection", "SetSpeedFraction"]);
        assert_eq!(scene.ship(SESSION, NOON + seconds(3)).unwrap().speed_fraction, 1.0);
    }

    #[test]
    fn a_speed_fraction_keeps_the_course_but_wakes_a_stopped_ship() {
        let mut scene = scene();
        scene.command(SESSION, Command::Orbit { target_id: STATION, distance: 5_000.0 }, NOON);
        scene.command(SESSION, Command::SetSpeedFraction(0.5), NOON + seconds(1));
        let ship = scene.ship(SESSION, NOON + seconds(1)).unwrap();
        assert_eq!(
            (ship.course, ship.speed_fraction),
            (Course::Orbit { target: STATION, distance: 5_000.0 }, 0.5)
        );

        scene.command(SESSION, Command::Stop, NOON + seconds(2));
        scene.command(SESSION, Command::SetSpeedFraction(0.5), NOON + seconds(3));
        assert_eq!(scene.ship(SESSION, NOON + seconds(3)).unwrap().course, Course::Goto);
    }

    #[test]
    fn a_ship_that_follows_the_station_comes_to_rest_in_docking_range_and_docks_when_asked_again() {
        let mut scene = scene();
        // 50 km off the station's surface, at rest
        scene.pilots.get_mut(&SESSION).unwrap().ship.position =
            Vec3::new(1_000_000.0 + 1_047.0 + 50_000.0, 0.0, 0.0);
        scene.pilots.get_mut(&SESSION).unwrap().ship.velocity = Vec3::ZERO;
        let first = scene.command(SESSION, Command::Dock { station_id: STATION }, NOON);
        assert_eq!(
            first.last(),
            Some(&Out::Approaching { session: SESSION, command: 4, distance: 50_000.0 })
        );
        assert!(scene.poll(NOON + seconds(400)).is_empty(), "it does not dock by itself");

        let ship = scene.ship(SESSION, NOON + seconds(400)).unwrap().clone();
        let gap = ((ship.position.x - 1_000_000.0).powi(2) + ship.position.y.powi(2)).sqrt() - 47.0 - 1_000.0;
        assert!((2_400.0..=2_500.0).contains(&gap), "it stands {gap} m off the surface");
        assert!(speed(&ship) < 1.0);

        let second = scene.command(SESSION, Command::Dock { station_id: STATION }, NOON + seconds(400));
        assert_eq!(action_names(&second[1]), ["SetBallMassive", "Stop"]);
        assert_eq!(second.last(), Some(&accepted(4, NOON + seconds(400))));
    }

    #[test]
    fn binding_a_ship_that_follows_sends_the_follow() {
        let mut scene = scene();
        scene.command(SESSION, Command::FollowBall { target_id: STATION, range: 3_000.0 }, NOON);
        let out = scene.bind(SESSION, NOON);
        let heading = actions_of(&out[3]);
        assert_eq!(
            heading.last().unwrap(),
            &("FollowBall".to_string(), vec![Value::Int(STATION), Value::Int(3_000)])
        );
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
    fn a_change_of_the_security_status_shows_the_slim_item_again_to_the_pilot() {
        let mut scene = scene();
        let mut penalised = spec();
        penalised.security_status = -0.25;
        let out = scene.respec(SESSION, penalised.clone(), NOON);
        assert_eq!(out.len(), 1, "only the pilot is in the scene");
        assert_eq!(action_names(&out[0]), ["OnSlimItemChange"]);
        let told = format!("{:?}", out[0]);
        assert!(
            told.contains("foo.SlimItem") && told.contains("Real(-0.25)"),
            "the slim item with the new status: {told}"
        );
        assert!(told.contains(&spec().ship_id.to_string()), "the item is the ship");
        assert!(scene.respec(SESSION, penalised, NOON).is_empty(), "the same status says nothing more");
    }

    #[test]
    fn leaving_removes_the_ship() {
        let mut scene = scene();
        assert!(scene.leave(SESSION, Departure::LoggedOff, NOON).is_some());
        assert!(scene.leave(SESSION, Departure::LoggedOff, NOON).is_none());
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

    // ── stargates ────────────────────────────────────────────────────────────

    pub(crate) const NEAR_GATE: i64 = 50_000_001;
    pub(crate) const FAR_GATE: i64 = 50_000_003;
    pub(crate) const ARRIVAL_GATE: i64 = 50_000_002;
    pub(crate) const OTHER_SYSTEM: i32 = 30_000_002;

    fn gate(id: i64, position: Vec3, radius: f64, to: (i32, i64), facing: Option<[f64; 3]>) -> Body {
        Body {
            id,
            type_id: 16_922,
            group_id: 10,
            category_id: 2,
            owner_id: 500_006,
            name: format!("Stargate ({})", to.0),
            kind: BodyKind::Stargate,
            position,
            radius,
            graphic_id: None,
            detail: Detail::Stargate {
                destination_system: to.0,
                destination_gate: to.1,
                destination_name: "Elsewhere".to_string(),
                destination_security: 0.5,
                destination_faction: None,
                hazardous: false,
                facing,
                heading: facing.map(|_| Vec3::new(0.0, 0.0, -1.0)),
            },
        }
    }

    /// The test system with a gate a few kilometres from the station and another a very long way off, and the system they lead to.
    pub(crate) fn gated_systems() -> (SolarSystem, SolarSystem) {
        let mut here = (*system()).clone();
        // a few kilometres from where a ship comes out of the station
        let mut undocked = Scene::new(system());
        undocked.undock(SESSION, spec(), STATION, NOON).unwrap();
        let out = undocked.ship(SESSION, NOON).unwrap().position;
        here.bodies.push(gate(
            NEAR_GATE,
            Vec3::new(out.x + 8_000.0, out.y, out.z),
            6_000.0,
            (OTHER_SYSTEM, ARRIVAL_GATE),
            None,
        ));
        here.bodies.push(gate(
            FAR_GATE,
            Vec3::new(9.0e9, 0.0, 2.0e9),
            12_000.0,
            (OTHER_SYSTEM, 50_000_004),
            None,
        ));
        let mut there = (*system()).clone();
        there.id = OTHER_SYSTEM;
        there.name = "Elsewhere".to_string();
        there.bodies = vec![
            gate(
                ARRIVAL_GATE,
                Vec3::new(2.0e9, 1.0e8, 5.0e8),
                10_000.0,
                (30_000_001, NEAR_GATE),
                Some([180.0, 0.0, 0.0]),
            ),
            gate(50_000_004, Vec3::new(-3.0e9, 0.0, 4.0e9), 10_000.0, (30_000_001, FAR_GATE), None),
        ];
        (here, there)
    }

    fn scene_by_a_gate() -> Scene {
        let mut scene = Scene::new(Arc::new(gated_systems().0));
        scene.undock(SESSION, spec(), STATION, NOON).unwrap();
        scene
    }

    fn jump(gate_id: i64, leads_to: i64) -> Command {
        Command::Jump { gate_id, destination_gate: leads_to }
    }

    fn refusal(out: &[Out]) -> &'static str {
        match out {
            [Out::Refused { command: 9, reason, .. }] => reason,
            other => panic!("{other:?}"),
        }
    }

    const BELT_A: i64 = 40_000_101;
    const BELT_B: i64 = 40_000_102;
    const MOON_BY_THE_BELTS: i64 = 40_000_201;
    const MOON_FAR_OFF: i64 = 40_000_202;

    fn celestial(id: i64, kind: BodyKind, group_id: i32, x: f64) -> Body {
        Body {
            id,
            type_id: 15,
            group_id,
            category_id: 2,
            owner_id: 0,
            name: format!("Body {id}"),
            kind,
            position: Vec3::new(x, 0.0, 0.0),
            radius: 1_000.0,
            graphic_id: None,
            detail: Detail::None,
        }
    }

    /// Two belts 100 km apart a long way from the station, a moon between them (90 km off the first) and another moon far from both.
    fn scene_with_belts() -> Scene {
        let mut world = (*system()).clone();
        world.bodies.push(celestial(BELT_A, BodyKind::Belt, 9, 20_000_000.0));
        world.bodies.push(celestial(BELT_B, BodyKind::Belt, 9, 20_100_000.0));
        // of a kind that has no warp-in point and no wide berth of its own: a ship stops about a kilometre off it
        world.bodies.push(celestial(MOON_BY_THE_BELTS, BodyKind::Moon, 0, 20_090_000.0));
        world.bodies.push(celestial(MOON_FAR_OFF, BodyKind::Moon, 0, 60_000_000.0));
        let mut scene = Scene::new(Arc::new(world));
        scene.undock(SESSION, spec(), STATION, NOON).unwrap();
        scene.bind(SESSION, NOON);
        scene
    }

    /// Warps to `target` and polls until the ship is at rest again; what the scene told of an arrival on the way.
    fn arrivals_of_a_warp(scene: &mut Scene, target: i64, from: i64) -> (Vec<Out>, i64) {
        scene.command(SESSION, Command::SetSpeedFraction(0.0), from);
        scene.command(SESSION, Command::WarpTo { target_id: target, range: 0.0 }, from + seconds(1));
        let mut arrived = Vec::new();
        let mut t = from + seconds(1);
        while t < from + seconds(600) {
            t += 2_500_000;
            arrived.extend(scene.poll(t).into_iter().filter(|out| matches!(out, Out::Arrived { .. })));
            if scene.ship(SESSION, t).is_some_and(|ship| ship.course == Course::Stop) && !arrived.is_empty() {
                break;
            }
        }
        (arrived, t)
    }

    #[test]
    fn a_warp_to_a_belt_ends_with_an_arrival_at_that_belt_told_where_it_is() {
        let mut scene = scene_with_belts();
        let (arrived, _) = arrivals_of_a_warp(&mut scene, BELT_B, NOON);
        assert_eq!(
            arrived,
            [Out::Arrived { session: SESSION, belt_id: BELT_B, position: Vec3::new(20_100_000.0, 0.0, 0.0) }],
            "the position is the belt's, not where the ship stopped"
        );
    }

    #[test]
    fn a_warp_to_what_lies_by_belts_arrives_at_the_nearest_of_them() {
        let mut scene = scene_with_belts();
        let (arrived, _) = arrivals_of_a_warp(&mut scene, MOON_BY_THE_BELTS, NOON);
        assert!(matches!(arrived[..], [Out::Arrived { belt_id: BELT_B, .. }]), "{arrived:?}");
    }

    #[test]
    fn a_warp_that_ends_far_from_every_belt_is_no_arrival() {
        let mut scene = scene_with_belts();
        let mut told = Vec::new();
        scene.command(SESSION, Command::SetSpeedFraction(0.0), NOON);
        scene.command(SESSION, Command::WarpTo { target_id: MOON_FAR_OFF, range: 0.0 }, NOON + seconds(1));
        let mut t = NOON + seconds(1);
        while t < NOON + seconds(600) {
            t += 2_500_000;
            told.extend(scene.poll(t));
        }
        assert_eq!(scene.ship(SESSION, t).unwrap().course, Course::Stop, "the warp is over");
        assert!(told.iter().all(|out| !matches!(out, Out::Arrived { .. })), "{told:?}");
    }

    #[test]
    fn a_belt_just_out_of_the_landing_radius_is_not_arrived_at_and_one_just_inside_is() {
        // 250 km (plus the belt's radius) off belt A, on the side away from belt B, is the edge of it
        let scene = scene_with_belts();
        let at = |x: f64| Vec3::new(x, 0.0, 0.0);
        let edge = 20_000_000.0 - BELT_LANDING_RADIUS - 1_000.0;
        assert_eq!(scene.belt_arrived_at(SESSION, at(edge)).map(|(id, _)| id), Some(BELT_A));
        assert_eq!(scene.belt_arrived_at(SESSION, at(edge - 1.0)), None);
        assert_eq!(scene.belt_arrived_at(SESSION, at(60_000_000.0)), None);
    }

    #[test]
    fn a_jump_needs_a_gate_that_leads_where_the_client_says_and_a_ship_within_reach_of_it() {
        let mut scene = scene_by_a_gate();
        assert_eq!(refusal(&scene.command(SESSION, jump(42, 0), NOON)), "STARGATE_NOT_FOUND");
        assert_eq!(
            refusal(&scene.command(SESSION, jump(STATION, 0), NOON)),
            "STARGATE_NOT_FOUND",
            "a station is not a gate"
        );
        assert_eq!(
            refusal(&scene.command(SESSION, jump(NEAR_GATE, 50_000_009), NOON)),
            "STARGATE_DESTINATION_MISMATCH"
        );
        assert_eq!(refusal(&scene.command(SESSION, jump(FAR_GATE, 0), NOON)), "TOO_FAR_FROM_STARGATE");
        assert_eq!(
            refusal(&Scene::new(system()).command(SESSION, jump(NEAR_GATE, 0), NOON)),
            "NO_SHIP_IN_SPACE"
        );
        // none of it touched the ship
        assert!(matches!(scene.ship(SESSION, NOON).unwrap().course, Course::Goto));
    }

    #[test]
    fn a_ship_may_jump_from_two_and_a_half_kilometres_off_the_gates_surface_and_not_from_further() {
        let at = |gap: f64| {
            let mut scene = scene_by_a_gate();
            let gate = scene.system().body(NEAR_GATE).unwrap().clone();
            let pilot = scene.pilots.get_mut(&SESSION).unwrap();
            let offset = gate.radius + pilot.ship.spec.radius + gap;
            pilot.ship.position = Vec3::new(gate.position.x, gate.position.y + offset, gate.position.z);
            scene.command(SESSION, jump(NEAR_GATE, 0), NOON)
        };
        assert!(matches!(at(2_499.0)[1], Out::Jumping { .. }));
        assert!(matches!(at(2_500.0)[1], Out::Jumping { .. }), "the range itself is in");
        assert_eq!(refusal(&at(2_501.0)), "TOO_FAR_FROM_STARGATE");
    }

    #[test]
    fn a_jump_stops_the_ship_and_tells_the_client_the_jump_out_effect_and_the_stop_in_one_update() {
        let mut scene = scene_by_a_gate();
        let out = scene.command(SESSION, jump(NEAR_GATE, ARRIVAL_GATE), NOON);
        assert_eq!(out.len(), 2);
        assert_eq!(action_names(&out[0]), ["OnSpecialFX", "Stop"]);
        let stamps = stamps(&out[0]);
        assert_eq!(stamps[0], stamps[1], "at one stamp, as the reference sends them");
        let (_, fx) = &actions_of(&out[0])[0];
        assert_eq!(
            fx[..],
            [
                Value::None,
                Value::None,
                Value::Int(NEAR_GATE),
                Value::None,
                Value::Str("effects.JumpOut".into()),
                Value::Int(0),
                Value::Int(1),
                Value::Int(0),
                Value::Int(-1),
                Value::None,
                Value::None,
                Value::Int(0),
                Value::Tuple(vec![Value::Int(i64::from(OTHER_SYSTEM)), Value::None]),
            ]
        );
        assert_eq!(
            out[1],
            Out::Jumping {
                session: SESSION,
                command: 9,
                destination_system: OTHER_SYSTEM,
                destination_gate: ARRIVAL_GATE
            }
        );
        let ship = scene.ship(SESSION, NOON + seconds(1)).unwrap();
        assert_eq!(
            (ship.course, speed(ship)),
            (Course::Stop, 0.0),
            "a ship that went in flying stands still"
        );
    }

    #[test]
    fn a_ship_that_is_told_the_gate_only_is_taken_too() {
        let mut scene = scene_by_a_gate();
        let out = scene.command(SESSION, jump(NEAR_GATE, 0), NOON);
        assert!(matches!(out[1], Out::Jumping { destination_gate: ARRIVAL_GATE, .. }));
    }

    #[test]
    fn the_ship_leaves_four_seconds_after_the_command_and_not_before() {
        let mut scene = scene_by_a_gate();
        scene.command(SESSION, jump(NEAR_GATE, 0), NOON);
        assert!(scene.due_jumps(NOON + seconds(3)).0.is_empty());
        assert!(scene.due_jumps(NOON + seconds(4) - 1).0.is_empty());
        let (gone, _) = scene.due_jumps(NOON + seconds(4));
        assert_eq!(gone.len(), 1);
        let traveller = &gone[0];
        assert_eq!(
            (
                traveller.session,
                traveller.from_system,
                traveller.from_gate,
                traveller.to_system,
                traveller.to_gate
            ),
            (SESSION, 30_000_001, NEAR_GATE, OTHER_SYSTEM, ARRIVAL_GATE)
        );
        assert!(scene.is_empty(), "it is no longer here");
        assert!(scene.due_jumps(NOON + seconds(9)).0.is_empty(), "and goes once");
    }

    #[test]
    fn the_effects_stamp_can_hold_the_ship_back_beyond_the_four_seconds() {
        // stamps run at most two ahead of the clock: when the effect is stamped ahead, the ship leaves three seconds after that stamp begins
        let mut scene = scene_by_a_gate();
        scene.next_stamp = stamp_at(NOON) + 2; // the update for the effect will be the last that may be sent ahead of the clock
        let out = scene.command(SESSION, jump(NEAR_GATE, 0), NOON);
        let stamp = stamps(&out[0])[0];
        assert_eq!(stamp, i64::from(stamp_at(NOON)) + 2);
        let leaves = (stamp + UNIX_EPOCH_SECONDS + 3) * TICKS_PER_SECOND;
        assert!(leaves > NOON + seconds(4), "a later moment than the four seconds");
        assert!(scene.due_jumps(leaves - 1).0.is_empty());
        assert_eq!(scene.due_jumps(leaves).0.len(), 1);
    }

    #[test]
    fn a_ship_that_has_gone_into_a_gate_takes_no_orders_and_cannot_jump_again() {
        let mut scene = scene_by_a_gate();
        scene.command(SESSION, jump(NEAR_GATE, 0), NOON);
        assert_eq!(
            refusal(&scene.command(SESSION, jump(NEAR_GATE, 0), NOON + seconds(1))),
            "STARGATE_JUMP_IN_PROGRESS"
        );
        for command in [
            Command::SetSpeedFraction(1.0),
            Command::Stop,
            Command::Dock { station_id: STATION },
            Command::WarpTo { target_id: FAR_GATE, range: 0.0 },
        ] {
            let out = scene.command(SESSION, command, NOON + seconds(1));
            assert_eq!(
                out,
                [Out::Refused { session: SESSION, command: command.code(), reason: "JUMPING" }],
                "{command:?}"
            );
        }
    }

    fn other_scene() -> Scene {
        Scene::new(Arc::new(gated_systems().1))
    }

    fn jumped(scene: &mut Scene) -> Traveller {
        scene.command(SESSION, jump(NEAR_GATE, 0), NOON);
        // (a scene that has been bound has stamped ahead: the ship may leave a second or two after the four)
        scene.due_jumps(NOON + seconds(6)).0.pop().unwrap()
    }

    #[test]
    fn a_ship_appears_in_front_of_the_gate_it_comes_out_of_at_rest() {
        let mut scene = scene_by_a_gate();
        let traveller = jumped(&mut scene);
        let mut there = other_scene();
        there.arrive(traveller, NOON + seconds(5)).unwrap();
        let gate = there.system().body(ARRIVAL_GATE).unwrap().clone();
        let ship = there.ship(SESSION, NOON + seconds(5)).unwrap().clone();
        assert_eq!((ship.course, ship.velocity, ship.speed_fraction), (Course::Stop, Vec3::ZERO, 0.0));
        let apart = ((ship.position.x - gate.position.x).powi(2)
            + (ship.position.y - gate.position.y).powi(2)
            + (ship.position.z - gate.position.z).powi(2))
        .sqrt();
        assert!((apart - (10_000.0 + 47.0 + 12_000.0)).abs() < 1e-6, "{apart} m from the gate's centre");
        let facing = crate::gate::forward(&gate);
        let along = ((ship.position.x - gate.position.x) * facing.x
            + (ship.position.y - gate.position.y) * facing.y
            + (ship.position.z - gate.position.z) * facing.z)
            / apart;
        assert!(
            along >= (std::f64::consts::PI / 4.0).cos() - 1e-9,
            "in the cone in front of the gate: {along}"
        );
        let away = motion::unit(Vec3::new(
            ship.position.x - gate.position.x,
            ship.position.y - gate.position.y,
            ship.position.z - gate.position.z,
        ))
        .unwrap();
        let alike = ship.direction.x * away.x + ship.direction.y * away.y + ship.direction.z * away.z;
        assert!(alike > 1.0 - 1e-9, "the ship faces away from the gate: {alike}");
    }

    #[test]
    fn binding_a_ship_that_came_through_a_gate_sends_the_ship_the_state_and_the_numbers_and_no_undock_priming()
     {
        let mut scene = scene_by_a_gate();
        let traveller = jumped(&mut scene);
        let mut there = other_scene();
        there.arrive(traveller, NOON + seconds(5)).unwrap();
        let bound = there.bind(SESSION, NOON + seconds(6));
        let names: Vec<Vec<String>> = bound.iter().map(action_names).collect();
        let notifications: Vec<&str> = bound
            .iter()
            .map(|o| match o {
                Out::Notify { name, .. } => *name,
                other => panic!("{other:?}"),
            })
            .collect();
        assert_eq!(
            notifications,
            [
                "OnSetTimeDilation",
                "DoSimClockRebase",
                "DoDestinyUpdate",
                "DoDestinyUpdate",
                "DoDestinyUpdate"
            ]
        );
        assert_eq!(names[2], ["AddBalls2"]);
        assert_eq!(names[3], ["SetState"]);
        assert_eq!(names[4], ["SetBallAgility", "SetBallMass", "SetMaxSpeed"]);
        // the ship's own ball says it is stopped
        assert_eq!(there.ship(SESSION, NOON + seconds(6)).unwrap().course, Course::Stop);
    }

    #[test]
    fn a_ship_that_has_jumped_is_not_told_of_the_others_until_it_binds_the_ballpark_there() {
        let mut scene = scene_by_a_gate();
        scene.bind(SESSION, NOON); // it had bound the ballpark of the system it leaves
        let traveller = jumped(&mut scene);
        let mut there = other_scene();
        there.arrive(traveller, NOON + seconds(5)).unwrap();
        // the ship arrived: it has bound nothing here, so it is told nothing and holds nothing in view until it does
        let pilot = there.pilots.get(&SESSION).unwrap();
        assert!(!pilot.bound && pilot.sees.is_empty());
        there.bind(SESSION, NOON + seconds(6));
        assert!(there.pilots.get(&SESSION).unwrap().bound);
    }

    #[test]
    fn a_ship_sent_to_a_system_without_the_gate_is_given_back_and_goes_on_where_it_was() {
        let mut scene = scene_by_a_gate();
        let traveller = jumped(&mut scene);
        // a system that has no gate with that id
        let mut wrong = Scene::new(system());
        let back = wrong.arrive(traveller, NOON + seconds(5)).unwrap_err();
        assert!(wrong.is_empty());
        scene.restore(*back, NOON + seconds(6));
        let told = scene.command(SESSION, Command::SetSpeedFraction(1.0), NOON + seconds(6));
        assert!(matches!(told[0], Out::Notify { .. }), "it takes orders again: {told:?}");
        // and it can try the jump again
        assert!(matches!(
            scene.command(SESSION, jump(NEAR_GATE, 0), NOON + seconds(7))[1],
            Out::Jumping { .. }
        ));
    }

    #[test]
    fn a_warp_to_a_gate_ends_within_jump_range_of_it_and_the_jump_is_accepted() {
        let mut scene = scene_by_a_gate();
        scene.command(SESSION, Command::SetSpeedFraction(0.0), NOON);
        let told =
            scene.command(SESSION, Command::WarpTo { target_id: FAR_GATE, range: 0.0 }, NOON + seconds(1));
        assert_eq!(
            action_names(&told[0]),
            ["SetMaxSpeed", "WarpTo", "OnSpecialFX", "SetSpeedFraction"],
            "{told:?}"
        );
        let mut t = NOON + seconds(1);
        while t < NOON + seconds(300) {
            t += 2_500_000;
            scene.poll(t);
        }
        let ship = scene.ship(SESSION, t).unwrap().clone();
        assert_eq!(ship.course, Course::Stop, "the warp is over");
        let gate = scene.system().body(FAR_GATE).unwrap();
        let surface = crate::gate::surface_distance(gate, ship.position, ship.spec.radius);
        assert!(surface <= crate::gate::JUMP_RANGE, "{surface} m off the gate's surface");
        let out = scene.command(SESSION, jump(FAR_GATE, 0), t + seconds(1));
        assert!(matches!(out[1], Out::Jumping { .. }), "{out:?}");
    }

    #[test]
    fn a_warp_to_a_gate_with_the_same_calls_ends_at_the_same_point() {
        let land = || {
            let mut scene = scene_by_a_gate();
            scene.command(SESSION, Command::SetSpeedFraction(0.0), NOON);
            let told = scene.command(
                SESSION,
                Command::WarpTo { target_id: FAR_GATE, range: 0.0 },
                NOON + seconds(1),
            );
            actions_of(&told[0])[1].1.clone()
        };
        assert_eq!(land(), land());
    }
}
