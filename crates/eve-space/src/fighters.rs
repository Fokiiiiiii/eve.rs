//! Fighter squadrons: a pilot's squadron comes out of a launch tube into space as a companion of its ship, flies where it is sent and comes back to land in
//! a tube. It is the drone companion ([`crate::drones`]) with a fighter's kind ([`CompanionKind::Fighter`]): the same ball, the same circling and the same
//! release and landing, but no weapon, no work cycle and no drone notices (the reference: `fighterRuntime.js`, `fighterMgrService.js`).
//!
//! The orders are the client's movement commands and the recall and abandon the pilot gives (`ExecuteMovementCommandOnFighters`, `RecallFightersToTubes`,
//! `CmdAbandonFighter`). A squadron that lands is reported as [`Out::FighterLanded`] and one that is let go as [`Out::FighterReleased`]; the host moves the
//! item in each case. Abilities and damage are not in this module: a squadron is refused what it cannot do, with its reason.

use crate::drones::{DEPARTING, DroneFlight, DroneWorkProfile, FIGHTER_RECOVERY_DISTANCE, Task};
use crate::payload;
use crate::scene::{Out, Scene, SessionId};
use crate::ship::{CompanionKind, Course, ShipSpec, Squadron};
use crate::wire::{id, int, real};

/// How far off its controller a squadron circles when it is launched (the reference's default orbit, metres).
pub const LAUNCH_ORBIT_METERS: f64 = 1_500.0;

/// An order a pilot gives its fighter squadrons (the codes of the link: `FighterOrder::code`).
#[derive(Debug, Clone, Copy, PartialEq)]
pub enum FighterOrder {
    /// Circle the target (a ship of the scene) at this distance off its surface, metres.
    Orbit { target: i64, distance: f64 },
    /// Follow the target at this range, metres.
    Follow { target: i64, range: f64 },
    /// Stop where it is.
    Stop,
    /// Come back to the ship and land in a tube.
    Recall,
    /// Let go: the squadron stays in space under nobody's control.
    Abandon,
    /// Fly to a point of the scene and stop there (the reference's GOTO_POINT: the point, in metres).
    GotoPoint { point: [f64; 3] },
    /// Take back a squadron that was let go, from within reach of the ship it came from (the reference's scoop of an abandoned squadron): the host moves
    /// its item into the bay or the tube it is scooped to.
    Scoop,
}

impl FighterOrder {
    /// The code of the order on the link.
    pub fn code(self) -> u8 {
        match self {
            Self::Orbit { .. } => 1,
            Self::Follow { .. } => 2,
            Self::Stop => 3,
            Self::Recall => 4,
            Self::Abandon => 5,
            Self::GotoPoint { .. } => 6,
            Self::Scoop => 7,
        }
    }
}

/// The reason a squadron of the pilot is not under the order's control.
pub const NOT_CONTROLLED: &str = "NOT_CONTROLLED";
/// The reason the order names a target that is not in the scene.
pub const NO_TARGET: &str = "NO_TARGET";
/// The reason a scoop names a squadron that is not let go in space (it has a controller, or is not in the scene).
pub const NOT_ABANDONED: &str = "NOT_ABANDONED";
/// The reason a scoop names a squadron that was launched from another ship.
pub const NOT_YOURS: &str = "NOT_YOURS";
/// The reason a scoop names a squadron beyond the recovery distance of the ship.
pub const TOO_FAR: &str = "TOO_FAR";

impl Scene {
    /// Whether a ship of the scene is a fighter squadron (a companion of the fighter kind).
    pub(crate) fn is_fighter(&self, session: SessionId) -> bool {
        self.pilots
            .get(&session)
            .and_then(|pilot| pilot.ship.npc.as_ref())
            .and_then(|npc| npc.drone.as_ref())
            .is_some_and(|link| link.kind == CompanionKind::Fighter)
    }

    /// A squadron of `controller` comes into space beside its ship, at `graphic_id` (the type's graphic) and circling at its orbit distance.
    pub fn launch_fighter(
        &mut self,
        controller: SessionId,
        spec: ShipSpec,
        graphic_id: i32,
        cruise_fraction: f64,
        squadron_size: i32,
        now: i64,
    ) -> Result<(SessionId, Vec<Out>), &'static str> {
        if squadron_size < 1 {
            return Err("NO_MEMBERS");
        }
        let flight = DroneFlight {
            weapon: None,
            graphic_id,
            orbit_distance: LAUNCH_ORBIT_METERS,
            attack_range: 0.0,
            cruise_fraction,
            work: DroneWorkProfile::default(),
            effect: None,
        };
        let (session, out) = self.launch_companion(CompanionKind::Fighter, controller, spec, flight, now)?;
        if let Some(link) = self
            .pilots
            .get_mut(&session)
            .and_then(|pilot| pilot.ship.npc.as_mut())
            .and_then(|npc| npc.drone.as_mut())
        {
            link.squadron = Some(Squadron { members_max: squadron_size, told_size: squadron_size });
        }
        Ok((session, out))
    }

    /// The squadron lost members when it was hit: its size is the members that still have points (its points left over one member's worth, rounded
    /// up), and it is told the size once it is smaller than it was. A size only falls: the shield recharges, a member does not come back.
    /// A squadron with no member left is not told a size: its destruction is.
    pub(crate) fn tell_squadron_size(&mut self, session: SessionId, now: i64) -> Vec<Out> {
        let Some(pilot) = self.pilots.get_mut(&session) else { return Vec::new() };
        let fighter_id = pilot.ship.spec.ship_id;
        let (left, whole) = pilot.ship.health.points_left(now);
        let Some(link) = pilot.ship.npc.as_mut().and_then(|npc| npc.drone.as_mut()) else {
            return Vec::new();
        };
        let controller = link.controller;
        let Some(squadron) = link.squadron.as_mut() else { return Vec::new() };
        let member = whole / f64::from(squadron.members_max.max(1));
        let members = if member > 0.0 {
            ((left / member) - 1e-6).ceil().clamp(0.0, f64::from(squadron.members_max)) as i32
        } else {
            0
        };
        let size = members.min(squadron.told_size);
        if size == squadron.told_size || size == 0 {
            return Vec::new();
        }
        squadron.told_size = size;
        vec![Out::FighterSize { session: controller, fighter_id, size }]
    }

    /// Gives the squadrons of `controller` an order and answers, for each squadron, `None` when it took the order or the reason it did not
    /// (as [`Out::FighterResults`] to the controller). A squadron that is not one of the controller's is refused with [`NOT_CONTROLLED`].
    pub fn fighter_order(
        &mut self,
        controller: SessionId,
        fighters: &[i64],
        order: FighterOrder,
        now: i64,
    ) -> Vec<Out> {
        self.settle_all(now);
        if order == FighterOrder::Scoop {
            return self.scoop_abandoned(controller, fighters, now);
        }
        let mut out = Vec::new();
        let mut results = Vec::new();
        for &fighter_id in fighters {
            let session = self.session_of(fighter_id).filter(|session| {
                self.is_fighter(*session) && self.controller_of_drone(*session) == Some(controller)
            });
            let Some(session) = session else {
                results.push((fighter_id, Some(NOT_CONTROLLED)));
                continue;
            };
            match order {
                FighterOrder::Orbit { target, distance } => {
                    if self.session_of(target).filter(|t| *t != session).is_none() {
                        results.push((fighter_id, Some(NO_TARGET)));
                        continue;
                    }
                    self.clear_task(session);
                    out.extend(self.circle(session, target, distance.max(0.0), 1.0, now));
                }
                FighterOrder::Follow { target, range } => {
                    if self.session_of(target).filter(|t| *t != session).is_none() {
                        results.push((fighter_id, Some(NO_TARGET)));
                        continue;
                    }
                    self.clear_task(session);
                    out.extend(self.follow(session, target, range.max(0.0), now));
                }
                FighterOrder::Stop => {
                    self.clear_task(session);
                    out.extend(self.halt(session, now));
                }
                FighterOrder::Recall => {
                    if let Some(ai) = self.drones.get_mut(&session) {
                        ai.task = Task::Bay;
                        ai.follow = None;
                    }
                    out.extend(self.go_home(session, DEPARTING, now));
                }
                FighterOrder::Abandon => out.extend(self.abandon(session, now)),
                // a scoop is answered for the squadrons let go, before the control check above (`scoop_abandoned`)
                FighterOrder::Scoop => unreachable!("a scoop is answered by scoop_abandoned"),
                FighterOrder::GotoPoint { point } => {
                    self.clear_task(session);
                    out.extend(self.goto_point(session, point, now));
                }
            }
            results.push((fighter_id, None));
        }
        out.insert(0, Out::FighterResults { session: controller, command: order.code(), results });
        out
    }

    /// Takes back the squadrons let go in space that are within reach of the pilot's ship (the reference's `scoopAbandonedFighterFromSpace`): one with a
    /// controller is refused, as is one launched from another ship and one beyond the recovery distance. A squadron taken back leaves the scene as a
    /// landing does (`take_in`), and the answer says which were taken (the host moves each item).
    fn scoop_abandoned(&mut self, controller: SessionId, fighters: &[i64], now: i64) -> Vec<Out> {
        let ship_id = self.pilots.get(&controller).map(|pilot| pilot.ship.spec.ship_id);
        let mut out = Vec::new();
        let mut results = Vec::new();
        for &fighter_id in fighters {
            let let_go = self
                .session_of(fighter_id)
                .filter(|session| self.is_fighter(*session) && self.controller_of_drone(*session) == Some(0));
            let Some(session) = let_go else {
                results.push((fighter_id, Some(NOT_ABANDONED)));
                continue;
            };
            let launched_here = self
                .pilots
                .get(&session)
                .and_then(|pilot| {
                    pilot.ship.npc.as_ref()?.drone.as_ref().map(|link| link.launched_from_ship_id)
                })
                .is_some_and(|from| Some(from) == ship_id);
            if ship_id.is_none() || !launched_here {
                results.push((fighter_id, Some(NOT_YOURS)));
                continue;
            }
            if self.apart(session, controller).is_none_or(|apart| apart > FIGHTER_RECOVERY_DISTANCE) {
                results.push((fighter_id, Some(TOO_FAR)));
                continue;
            }
            out.extend(self.take_in(session, controller, now));
            results.push((fighter_id, None));
        }
        out.insert(0, Out::FighterResults { session: controller, command: 7, results });
        out
    }

    /// The squadron is no longer doing a task it was given: it drops its task and any follow.
    fn clear_task(&mut self, session: SessionId) {
        if let Some(ai) = self.drones.get_mut(&session) {
            ai.task = Task::Idle;
            ai.follow = None;
        }
    }

    /// The squadron follows the ship `target` at `range`, telling whoever sees it (the action the reference sends for a follow).
    fn follow(&mut self, session: SessionId, target: i64, range: f64, now: i64) -> Vec<Out> {
        let Some(pilot) = self.pilots.get_mut(&session) else { return Vec::new() };
        pilot.ship.course = Course::Follow { target, range };
        pilot.ship.speed_fraction = 1.0;
        let fighter_id = pilot.ship.spec.ship_id;
        let stamp = self.next_stamp(now);
        self.tell_all(
            session,
            vec![
                payload::action(stamp, "SetSpeedFraction", [id(fighter_id), real(1.0)]),
                payload::action(stamp, "FollowBall", [id(fighter_id), id(target), int(range as i64)]),
            ],
        )
    }

    /// The squadron flies to `point` and stops there, at its speed (full when it had none: the reference's `issueFighterGotoPoint`).
    fn goto_point(&mut self, session: SessionId, point: [f64; 3], now: i64) -> Vec<Out> {
        let Some(pilot) = self.pilots.get_mut(&session) else { return Vec::new() };
        let target = eve_destiny::Vec3::new(point[0], point[1], point[2]);
        pilot.ship.course = Course::GotoPoint { point: target };
        if pilot.ship.speed_fraction <= 0.0 {
            pilot.ship.speed_fraction = 1.0;
        }
        let fighter_id = pilot.ship.spec.ship_id;
        let speed = pilot.ship.speed_fraction;
        let stamp = self.next_stamp(now);
        self.tell_all(
            session,
            vec![
                payload::action(
                    stamp,
                    "GotoPoint",
                    [id(fighter_id), real(point[0]), real(point[1]), real(point[2])],
                ),
                payload::action(stamp, "SetSpeedFraction", [id(fighter_id), real(speed)]),
            ],
        )
    }

    /// The squadron stops where it is.
    fn halt(&mut self, session: SessionId, now: i64) -> Vec<Out> {
        let Some(pilot) = self.pilots.get_mut(&session) else { return Vec::new() };
        pilot.ship.course = Course::Stop;
        pilot.ship.speed_fraction = 0.0;
        let fighter_id = pilot.ship.spec.ship_id;
        let stamp = self.next_stamp(now);
        self.tell_all(
            session,
            vec![
                payload::action(stamp, "SetSpeedFraction", [id(fighter_id), real(0.0)]),
                payload::action(stamp, "Stop", [id(fighter_id)]),
            ],
        )
    }
}

#[cfg(test)]
mod tests;
