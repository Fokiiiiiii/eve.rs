//! The tractor beam (`tractorBeamCan`, `SimAction::Tractor`): it pulls the wreck or can its pilot holds a lock on until that wreck is `hold` metres off
//! the ship's surface, and lets go when the beam stops, the target is lost or out of reach, or the ship is gone.
//!
//! The pull is the wreck's own: its ball follows the ship that pulls it (`WreckPull`), and whoever has the wreck in view is told each change as a ball
//! action. The actions are EVE.js's presentation of the same change (`applyTractorActivationCommand` and `applyTractorStopCommand` in
//! `destiny/commands/tractorBeam.js`), and the client follows a ball by its follow id whatever kind it is (`ActionDisplay._GetMode` reads `followId` of
//! any ball). Loot rights are not checked here: the host refuses a pull on a wreck the pilot may not take from, before the activation is sent.

use eve_marshal::Value;

use crate::motion::{self, Kinematics, Target};
use crate::payload;
use crate::scene::{Scene, SessionId, TICKS_PER_SECOND};
use crate::wire::{id, int, real, vector};
use crate::wreck::{PULLED_MASS, WreckPull};

/// The mass a pulled body is given by the beam, and its inertia: the ball's default (`BallEntity::default`), which EVE.js's beam does not change.
const PULLED_INERTIA: f64 = 1.0;

/// How far past its range a beam still holds its target, metres (EVE.js `executeTractorBeamCycle`: `surfaceDistance > maxRange + 1` is out of reach).
pub(crate) const REACH_GRACE_METERS: f64 = 1.0;

impl Scene {
    /// The distance from the pilot's ship to the surface of a wreck or a can, or `None` when the pilot has no ship or the wreck is not in the scene.
    pub(crate) fn surface_distance_to_wreck(&self, session: SessionId, wreck_id: i64) -> Option<f64> {
        let wreck = self.wrecks.get(&wreck_id)?;
        let ship = self.pilots.get(&session)?.ship.position;
        let centre = ((ship.x - wreck.position.x).powi(2)
            + (ship.y - wreck.position.y).powi(2)
            + (ship.z - wreck.position.z).powi(2))
        .sqrt();
        Some((centre - wreck.radius.max(0.0)).max(0.0))
    }

    /// Whether a beam of `session` still has its target: a wreck or a can its pilot holds the lock on, within the module's `range` (and its grace).
    pub(crate) fn tractor_target_ok(&self, session: SessionId, wreck_id: i64, range: f64) -> bool {
        self.locked_by(session).contains(&wreck_id)
            && self
                .surface_distance_to_wreck(session, wreck_id)
                .is_some_and(|apart| apart <= range + REACH_GRACE_METERS)
    }

    /// The pull of a beam starts, or goes on: the wreck follows the ship at `hold` metres, at `velocity` (EVE.js's presentation of the activation). A wreck
    /// already pulled by this ship is not told again; one pulled by another ship is taken over by this one.
    pub(crate) fn tractor_pull(
        &mut self,
        session: SessionId,
        wreck_id: i64,
        velocity: f64,
        hold: f64,
        now: i64,
    ) -> Vec<crate::scene::Out> {
        let Some(source) = self.pilots.get(&session).map(|pilot| pilot.ship.spec.ship_id) else {
            return Vec::new();
        };
        let pull = WreckPull { source_ship: source, hold_meters: hold, speed: velocity };
        let Some(wreck) = self.wrecks.get_mut(&wreck_id) else {
            return Vec::new();
        };
        if wreck.pull == Some(pull) {
            return Vec::new();
        }
        wreck.pull = Some(pull);
        let stamp = self.next_stamp(now);
        let actions = vec![
            payload::action(stamp, "SetMaxSpeed", [id(wreck_id), real(velocity)]),
            payload::action(stamp, "SetBallFree", [id(wreck_id), int(1)]),
            payload::action(stamp, "SetBallMass", [id(wreck_id), real(PULLED_MASS)]),
            payload::action(stamp, "SetSpeedFraction", [id(wreck_id), real(1.0)]),
            payload::action(stamp, "FollowBall", [id(wreck_id), id(source), int(hold.trunc() as i64)]),
        ];
        self.tell_wreck(wreck_id, actions)
    }

    /// The beam of `source_ship` lets go of the wreck it pulled: the wreck stops where it is (EVE.js `stopTractorTarget`: the top speed is restored,
    /// the speed fraction and the velocity go to zero, and the ball stops). Another ship's pull on the wreck is left alone.
    pub(crate) fn tractor_release(
        &mut self,
        wreck_id: i64,
        source_ship: i64,
        now: i64,
    ) -> Vec<crate::scene::Out> {
        let Some(wreck) = self.wrecks.get_mut(&wreck_id) else {
            return Vec::new();
        };
        if wreck.pull.is_none_or(|pull| pull.source_ship != source_ship) {
            return Vec::new();
        }
        wreck.pull = None;
        wreck.velocity = eve_destiny::Vec3::ZERO;
        let stamp = self.next_stamp(now);
        let mut velocity_args = vec![id(wreck_id)];
        velocity_args.extend(vector(0.0, 0.0, 0.0));
        let actions: Vec<Value> = vec![
            payload::action(stamp, "SetMaxSpeed", [id(wreck_id), real(0.0)]),
            payload::action(stamp, "SetSpeedFraction", [id(wreck_id), real(0.0)]),
            payload::action(stamp, "SetBallVelocity", velocity_args),
            payload::action(stamp, "Stop", [id(wreck_id)]),
        ];
        self.tell_wreck(wreck_id, actions)
    }

    /// The pulled wreck moves over the time its puller settled from `from` to `now`: it follows the puller's place, at the hold distance (the follow step
    /// of a ship, `motion::pull`). Called when the puller settles, so both move over the same time.
    pub(crate) fn advance_pulled_by(&mut self, session: SessionId, from: i64, now: i64) {
        if now <= from {
            return;
        }
        let Some(source_ship) = self.pilots.get(&session).map(|pilot| pilot.ship.spec.ship_id) else {
            return;
        };
        let pulled: Vec<i64> = self
            .wrecks
            .values()
            .filter(|wreck| wreck.pull.is_some_and(|pull| pull.source_ship == source_ship))
            .map(|wreck| wreck.id)
            .collect();
        if pulled.is_empty() {
            return;
        }
        let seconds = (now - from) as f64 / TICKS_PER_SECOND as f64;
        let ship = &self.pilots[&session].ship;
        let target = Target { position: ship.position, radius: ship.spec.radius, velocity: ship.velocity };
        for wreck_id in pulled {
            let Some(wreck) = self.wrecks.get_mut(&wreck_id) else { continue };
            let Some(pull) = wreck.pull else { continue };
            let mut body = Kinematics {
                position: wreck.position,
                velocity: wreck.velocity,
                direction: wreck.direction,
                speed_fraction: 1.0,
                radius: wreck.radius,
                mass: PULLED_MASS,
                agility: PULLED_INERTIA,
                max_velocity: pull.speed,
            };
            motion::pull(&mut body, pull.hold_meters, target, seconds);
            wreck.position = body.position;
            wreck.velocity = body.velocity;
            wreck.direction = body.direction;
        }
    }

    /// A wreck whose puller is no longer in the scene (a ship that left, was destroyed or was boarded without its beam being ended) has no one to follow:
    /// it is let go, as the beam would let it go. Run at each settlement, so every removal path is covered.
    pub(crate) fn release_orphaned_pulls(&mut self, now: i64) -> Vec<crate::scene::Out> {
        let orphans: Vec<(i64, i64)> = self
            .wrecks
            .values()
            .filter_map(|wreck| wreck.pull.map(|pull| (wreck.id, pull.source_ship)))
            .filter(|(_, source)| self.session_of(*source).is_none())
            .collect();
        orphans
            .into_iter()
            .flat_map(|(wreck_id, source)| self.tractor_release(wreck_id, source, now))
            .collect()
    }

    /// What happens to a wreck, told to whoever has it in view, at the same stamp (as `remove_wreck` tells its watchers).
    fn tell_wreck(&self, wreck_id: i64, actions: Vec<Value>) -> Vec<crate::scene::Out> {
        self.in_entry_order()
            .into_iter()
            .filter(|s| self.pilots[s].bound && self.pilots[s].sees_wrecks.contains(&wreck_id))
            .map(|watcher| Scene::update(watcher, actions.clone()))
            .collect()
    }
}
