//! Fighter squadrons: a pilot's squadron comes out of a launch tube into space as a companion of its ship, flies where it is sent and comes back to land in
//! a tube. It is the drone companion ([`crate::drones`]) with a fighter's kind ([`CompanionKind::Fighter`]): the same ball, the same circling and the same
//! release and landing, but no weapon, no work cycle and no drone notices (the reference: `fighterRuntime.js`, `fighterMgrService.js`).
//!
//! The orders are the client's movement commands and the recall and abandon the pilot gives (`ExecuteMovementCommandOnFighters`, `RecallFightersToTubes`,
//! `CmdAbandonFighter`). A squadron that lands is reported as [`Out::FighterLanded`] and one that is let go as [`Out::FighterReleased`]; the host moves the
//! item in each case. Abilities and damage are not in this module: a squadron is refused what it cannot do, with its reason.

use crate::drones::{DroneFlight, DroneWorkProfile, Task, DEPARTING};
use crate::payload;
use crate::scene::{Out, Scene, SessionId};
use crate::ship::{CompanionKind, Course, ShipSpec};
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
        }
    }
}

/// The reason a squadron of the pilot is not under the order's control.
pub const NOT_CONTROLLED: &str = "NOT_CONTROLLED";
/// The reason the order names a target that is not in the scene.
pub const NO_TARGET: &str = "NO_TARGET";

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
        now: i64,
    ) -> Result<(SessionId, Vec<Out>), &'static str> {
        let flight = DroneFlight {
            weapon: None,
            graphic_id,
            orbit_distance: LAUNCH_ORBIT_METERS,
            attack_range: 0.0,
            cruise_fraction,
            work: DroneWorkProfile::default(),
            effect: None,
        };
        self.launch_companion(CompanionKind::Fighter, controller, spec, flight, now)
    }

    /// Gives the squadrons of `controller` an order and answers, for each squadron, `None` when it took the order or the reason it did not
    /// (as [`Out::FighterResults`] to the controller). A squadron that is not one of the controller's is refused with [`NOT_CONTROLLED`].
    pub fn fighter_order(&mut self, controller: SessionId, fighters: &[i64], order: FighterOrder, now: i64) -> Vec<Out> {
        self.settle_all(now);
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
                payload::action(stamp, "GotoPoint", [id(fighter_id), real(point[0]), real(point[1]), real(point[2])]),
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
mod tests {
    use eve_destiny::Kind;

    use super::*;
    use crate::brains::rat_tests::{actions_named, notified, run, scene_with_a_player, LATER, PLAYER, SECOND};

    const SQUADRON_SHIP: i64 = 9_988_400_004_100;

    /// A light fighter of a squadron of six (Templar I's class and graphic), as a ship of its own.
    fn squadron() -> ShipSpec {
        let mut squadron = crate::scene::tests::spec();
        squadron.ship_id = SQUADRON_SHIP;
        squadron.character_id = 0;
        squadron.type_id = 23_055;
        squadron.group_id = 1_652;
        squadron.category_id = 87;
        squadron.name = "Templar I".to_string();
        squadron.radius = 6.0;
        squadron.mass = 1_000.0;
        squadron.max_velocity = 800.0;
        squadron.agility = 100.0;
        squadron.modules = Vec::new();
        squadron
    }

    /// The pilot's squadron launched beside its ship at a cruise of half its top speed.
    fn launched() -> (Scene, SessionId, Vec<Out>) {
        let mut scene = scene_with_a_player();
        let (squadron, told) = scene.launch_fighter(PLAYER, squadron(), 1_200, 0.5, LATER).unwrap();
        (scene, squadron, told)
    }

    fn results_of(told: &[Out]) -> Vec<(i64, Option<&'static str>)> {
        told.iter()
            .find_map(|out| match out {
                Out::FighterResults { results, .. } => Some(results.clone()),
                _ => None,
            })
            .unwrap_or_default()
    }

    #[test]
    fn a_squadron_launched_is_a_fighter_of_the_pilot_that_circles_its_ship_and_is_no_drone() {
        let (scene, squadron, told) = launched();
        let told: Vec<Out> = told.into_iter().filter(|out| out.session() == PLAYER).collect();
        assert!(scene.is_fighter(squadron));
        assert_eq!(scene.controller_of_drone(squadron), Some(PLAYER));
        assert_eq!(scene.pilots[&squadron].ship.ball().kind, Kind::Fighter);
        // the pilot hears of its squadron as it does of a ship that came into space, and it is circling its ship
        assert!(notified(&told, "OnDroneStateChange").is_empty());
        assert!(!actions_named(&told, "Orbit").is_empty());
    }

    #[test]
    fn a_drone_order_is_refused_for_a_squadron_as_not_under_control() {
        let (mut scene, squadron, _) = launched();
        let told = scene.drone_command(PLAYER, &[SQUADRON_SHIP], crate::drones::DroneCommand::ReturnHome, LATER + SECOND);
        let refused = told.iter().any(|out| {
            matches!(out, Out::DroneResults { results, .. } if results.as_slice() == [(SQUADRON_SHIP, Some(NOT_CONTROLLED))])
        });
        assert!(refused, "{told:?}");
        assert!(scene.is_fighter(squadron));
    }

    #[test]
    fn an_orbit_order_circles_the_ship_it_names_and_a_follow_order_follows_it() {
        let (mut scene, squadron, _) = launched();
        let pilot_ship = scene.pilots[&PLAYER].ship.spec.ship_id;
        let told = scene.fighter_order(PLAYER, &[SQUADRON_SHIP], FighterOrder::Orbit { target: pilot_ship, distance: 800.0 }, LATER + SECOND);
        assert_eq!(results_of(&told), [(SQUADRON_SHIP, None)]);
        assert!(matches!(
            scene.pilots[&squadron].ship.course,
            Course::Orbit { target, distance } if target == pilot_ship && distance == 800.0
        ));
        let told = scene.fighter_order(PLAYER, &[SQUADRON_SHIP], FighterOrder::Follow { target: pilot_ship, range: 300.0 }, LATER + 2 * SECOND);
        assert_eq!(results_of(&told), [(SQUADRON_SHIP, None)]);
        assert!(matches!(
            scene.pilots[&squadron].ship.course,
            Course::Follow { target, range } if target == pilot_ship && range == 300.0
        ));
        // a target that is not in the scene is refused and the squadron keeps its course
        let told = scene.fighter_order(PLAYER, &[SQUADRON_SHIP], FighterOrder::Orbit { target: 42, distance: 800.0 }, LATER + 3 * SECOND);
        assert_eq!(results_of(&told), [(SQUADRON_SHIP, Some(NO_TARGET))]);
        assert!(matches!(scene.pilots[&squadron].ship.course, Course::Follow { .. }));
    }

    #[test]
    fn a_recalled_squadron_lands_when_it_is_within_reach_of_its_ship_and_the_host_is_told() {
        let (mut scene, _squadron, _) = launched();
        let order = scene.fighter_order(PLAYER, &[SQUADRON_SHIP], FighterOrder::Recall, LATER + SECOND);
        assert_eq!(results_of(&order), [(SQUADRON_SHIP, None)]);
        let told = run(&mut scene, LATER + 2 * SECOND, 20);
        assert!(
            told.iter().any(|out| matches!(out, Out::FighterLanded { session, fighter_id } if *session == PLAYER && *fighter_id == SQUADRON_SHIP)),
            "{told:?}"
        );
        assert!(scene.session_of(SQUADRON_SHIP).is_none(), "a squadron that landed is out of the scene");
        assert!(!told.iter().any(|out| matches!(out, Out::DroneScooped { .. })), "a squadron is not scooped as a drone");
    }

    #[test]
    fn a_squadron_sent_to_a_point_flies_there_and_is_told_the_point_as_the_reference_does() {
        let (mut scene, squadron, _) = launched();
        let start = scene.pilots[&squadron].ship.position;
        let point = [start.x + 2_000.0, start.y, start.z];
        let order = scene.fighter_order(PLAYER, &[SQUADRON_SHIP], FighterOrder::GotoPoint { point }, LATER + SECOND);
        assert_eq!(results_of(&order), [(SQUADRON_SHIP, None)]);
        assert!(matches!(scene.pilots[&squadron].ship.course, Course::GotoPoint { .. }));
        let told: Vec<Out> = order.into_iter().filter(|out| out.session() == PLAYER).collect();
        assert!(!actions_named(&told, "GotoPoint").is_empty(), "{told:?}");
        let told = run(&mut scene, LATER + 2 * SECOND, 200);
        let at = scene.pilots[&squadron].ship.position;
        let apart = ((at.x - point[0]).powi(2) + (at.y - point[1]).powi(2) + (at.z - point[2]).powi(2)).sqrt();
        assert!(apart < 5.0, "{apart} m from the point, {told:?}");
        // a squadron sent to a point is not recalled or launched by it
        assert!(scene.is_fighter(squadron));
    }

    #[test]
    fn an_abandoned_squadron_stays_in_space_under_nobody_and_the_host_is_told_where_it_is() {
        let (mut scene, squadron, _) = launched();
        let told = scene.fighter_order(PLAYER, &[SQUADRON_SHIP], FighterOrder::Abandon, LATER + SECOND);
        assert!(
            told.iter().any(|out| matches!(out, Out::FighterReleased { session, fighter_id, .. } if *session == PLAYER && *fighter_id == SQUADRON_SHIP)),
            "{told:?}"
        );
        assert_ne!(scene.controller_of_drone(squadron), Some(PLAYER));
        assert!(scene.session_of(SQUADRON_SHIP).is_some(), "it is still in the scene");
        let refused = scene.fighter_order(PLAYER, &[SQUADRON_SHIP], FighterOrder::Stop, LATER + 2 * SECOND);
        assert_eq!(results_of(&refused), [(SQUADRON_SHIP, Some(NOT_CONTROLLED))]);
    }
}
