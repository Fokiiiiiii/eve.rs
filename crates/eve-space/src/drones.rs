//! Drones: ships that a pilot launches from the drone bay of its ship and that fly and fight for it, kept apart from the pilots. A drone is a pilot of the scene under a
//! session of its own like an NPC (its motion, health, damage and death are a ship's, and a player sees it as a ball of its own), tied to the ship that controls it
//! ([`DroneLink`](crate::ship::DroneLink)) and driven by [`DroneAi`] instead of a behavior tree: it circles its controller until it is sent at a target, flies to
//! it, fires its weapon each cycle (the pilot's combat log tells the shots as its own), goes home when called and is scooped into the bay when it comes near.
//!
//! What the reference does (`droneRuntime.js`, `droneDogma.js`, `oracle/fixtures/scenarios/drone_flight.json`): the launch is one update that adds the ball (and tells the
//! controller `OnDroneStateChange`) and another that sets it circling at 700 m at a fraction of its speed that is its cruise speed; an order to engage sets it circling the
//! target at full speed and it is told as pursuing (6), approaching (3) or fighting (1) by how far the target is from its weapon's range; a drone that has killed its
//! target goes idle (0) and circles the controller again; an order to return home sets it circling the controller (4) and one to return to the bay brings it in
//! until it is within 2,500 m of the ship, when it is scooped.

use eve_destiny::Vec3;
use eve_marshal::Value;

use crate::modules::{Action, Applies, Effect, Jam, Reach, Remote};
use crate::npc::NpcPlace;
use crate::payload;
use crate::scene::{Out, Scene, SessionId};
use crate::ship::{Course, CompanionKind, DroneLink, NpcCombat, NpcIdentity, NpcWeapon, ShipSpec};
use crate::wire::{id, int, real, text};

/// How near the controller's surface a drone has to be to be taken into the bay.
pub const SCOOP_DISTANCE: f64 = 2_500.0;

/// How near the controller's surface a fighter squadron has to come to land in its tube (EVE.js `FIGHTER_RECOVERY_DISTANCE_METERS`).
pub const FIGHTER_RECOVERY_DISTANCE: f64 = 5_000.0;

/// How far ahead of the ship's surface a drone comes into space, metres.
const LAUNCH_OFFSET: f64 = 75.0;

/// How far from its controller a drone may be sent at a target it finds by itself or takes up on its own (the reference's base drone control range: 20 km).
const CONTROL_RANGE: f64 = 20_000.0;
const DRONE_RECONNECT_RANGE: f64 = 250_000.0;

/// How long a drone remembers who attacked its controller (the reference's `DRONE_AGGRESSION_THREAT_RETENTION_MS`: 30 s), in ticks.
const THREAT_MEMORY: i64 = 30 * crate::scene::TICKS_PER_SECOND;

/// The states the client numbers a drone's activity by (`const.entity*`).
pub const IDLE: i32 = 0;
pub const COMBAT: i32 = 1;
pub const APPROACHING: i32 = 3;
pub const DEPARTING: i32 = 4;
pub const PURSUIT: i32 = 6;

/// What a pilot tells its drones to do (the codes of the link).
#[derive(Debug, Clone, Copy, PartialEq)]
pub enum DroneCommand {
    /// Fly at the target (a ship of the scene) and shoot it.
    Engage { target: i64 },
    /// Circle the controller.
    ReturnHome,
    /// Come back to the ship and be scooped into the bay.
    ReturnBay,
    /// Let the drone go: it stays in space under nobody's control.
    Abandon,
    Assist { character_id: i64 },
    Guard { character_id: i64 },
    MineRepeatedly { target: i64 },
    Salvage { target: Option<i64> },
    /// Reconnect to an owned abandoned drone that is still nearby in this scene.
    Reconnect,
}

impl DroneCommand {
    /// The code of the command on the link (ADR 0007).
    pub fn code(self) -> u8 {
        match self {
            Self::Engage { .. } => 1,
            Self::ReturnHome => 2,
            Self::ReturnBay => 3,
            Self::Abandon => 4,
            Self::Assist { .. } => 5,
            Self::Guard { .. } => 6,
            Self::MineRepeatedly { .. } => 7,
            Self::Salvage { .. } => 8,
            Self::Reconnect => 9,
        }
    }
}

/// A non-damage effect a drone runs at its target each cycle: the remote effect of its type (a stasis webifier, a remote repairer, an ECM jammer), as the host worked
/// it out from the type's dogma. Each cycle lands `action` on its target for `lasting_ms`; the effect runs on its own clock, beside a turret when its type has one.
#[derive(Debug, Clone, PartialEq)]
pub struct DroneEffect {
    /// The dogma effect: its id, and the name the destiny stream calls it (`effects.ModifyTargetSpeed`).
    pub effect_id: i32,
    pub fx: String,
    /// How long a cycle is, milliseconds.
    pub cycle_ms: f64,
    /// How long what a cycle lands lasts on its target, milliseconds (an ECM jam lasts less than its cycle).
    pub lasting_ms: f64,
    /// What a cycle does to its target: the remote effect, its reach, when in the cycle it lands and the name the client's HUD gives it.
    pub action: Action,
}

impl DroneEffect {
    fn remote(&self) -> Option<(&Remote, &Reach, Applies)> {
        match &self.action {
            Action::Remote { what, reach, when, .. } => Some((what, reach, *when)),
            _ => None,
        }
    }

    /// Whether the effect mends ships rather than harms them (a remote repairer): it is run on a friendly ship only.
    pub fn repairs(&self) -> bool {
        matches!(self.remote(), Some((Remote::Repair { .. } | Remote::MutadaptiveRepair { .. }, ..)))
    }

    /// How far off its target the effect reaches, metres: a drone's effects have no falloff, so this is the whole of it.
    fn optimal(&self) -> f64 {
        self.remote().map_or(0.0, |(_, reach, _)| reach.optimal)
    }
}

/// What a drone is set on.
#[derive(Debug, Clone, Copy, PartialEq)]
pub(crate) enum Task {
    /// Circling its controller.
    Idle,
    Engage {
        target: i64,
    },
    /// Coming back to the controller (circling it) to wait there.
    Home,
    /// Coming back to be scooped.
    Bay,
    MineRepeatedly {
        target: i64,
    },
    Salvage {
        target: Option<i64>,
    },
}

#[derive(Debug, Clone, Copy, PartialEq)]
pub(crate) enum DroneFollow {
    Assist { character_id: i64, control_range: f64 },
    Guard { character_id: i64, control_range: f64 },
}
impl DroneFollow {
    fn character_id(self) -> i64 {
        match self {
            Self::Assist { character_id, .. } | Self::Guard { character_id, .. } => character_id,
        }
    }

    fn control_range(self) -> f64 {
        match self {
            Self::Assist { control_range, .. } | Self::Guard { control_range, .. } => control_range,
        }
    }
}

/// What the scene keeps of a drone besides its ship.
#[derive(Debug, Clone)]
pub(crate) struct DroneAi {
    pub(crate) task: Task,
    pub(crate) follow: Option<DroneFollow>,
    /// The next moment its weapon fires.
    next_shot: i64,
    /// The remote effect of its type (what each cycle lands on its target, for how long), which runs on its own clock beside its weapon.
    remote: Option<DroneEffect>,
    /// The next moment its remote effect runs.
    next_remote: i64,
    /// The cycle whose effect is landing on a target now: its HUD is told as it begins and ends, and the target is let go of when the drone stops.
    landing: Option<Effect>,
    /// A cycle of a remote repair that lands when that cycle ends (at the next cycle's moment).
    ending: Option<Effect>,
    /// How far it can shoot (metres off its target's surface), how near it circles its target, and how far it follows one that runs.
    engagement_range: f64,
    movement_range: f64,
    chase_range: f64,
    work: DroneWorkProfile,
    can_combat: bool,
    work_control_range: f64,
    work_ready_at: i64,
    work_cycle_id: u64,
    awaiting_work: bool,
    auto_salvage: bool,
    work_phase: DroneWorkPhase,
}

/// What a drone fights with and how it flies, as the host works them out from its type.
#[derive(Debug, Clone, PartialEq)]
pub struct DroneFlight {
    pub weapon: Option<NpcWeapon>,
    pub graphic_id: i32,
    /// How far off its controller (or its target) it circles, metres.
    pub orbit_distance: f64,
    /// How far off its target it can shoot, metres: inside it the drone fights (the reference: `entityAttackRange`, at least the weapon's optimal range). 0 for the weapon's optimal.
    pub attack_range: f64,
    /// The fraction of its top speed it flies at while it only follows its controller.
    pub cruise_fraction: f64,
    pub work: DroneWorkProfile,
    /// The remote effect of the type (a web, a repair, a jam), when it has one.
    pub effect: Option<DroneEffect>,
}

/// Effective mining/salvage dogma values, resolved by SDE attribute names by the host.
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct DroneWorkProfile {
    pub mining_amount_m3: f64,
    pub cycle_ms: f64,
    pub waste_multiplier: f64,
    pub waste_probability: f64,
    pub salvage_access_bonus: f64,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
enum DroneWorkPhase {
    #[default]
    ToTarget,
    Cycling,
    Returning,
}

/// A unit vector perpendicular to `direction`.
fn perpendicular(direction: Vec3) -> Vec3 {
    let helper = if direction.z.abs() < 0.9 { Vec3::new(0.0, 0.0, 1.0) } else { Vec3::new(1.0, 0.0, 0.0) };
    let cross = Vec3::new(
        direction.y * helper.z - direction.z * helper.y,
        direction.z * helper.x - direction.x * helper.z,
        direction.x * helper.y - direction.y * helper.x,
    );
    let length = (cross.x * cross.x + cross.y * cross.y + cross.z * cross.z).sqrt().max(1e-9);
    Vec3::new(cross.x / length, cross.y / length, cross.z / length)
}

impl Scene {
    /// A drone comes into space beside the ship of `controller`: it is told as the reference tells it (the controller's update adds the ball and says the drone is
    /// idle, then sets it circling; others who see it are sent the ship as they would any other), and it circles its controller until it is given an order.
    pub fn launch_drone(
        &mut self,
        controller: SessionId,
        spec: ShipSpec,
        flight: DroneFlight,
        now: i64,
    ) -> Result<(SessionId, Vec<Out>), &'static str> {
        self.launch_companion(CompanionKind::Drone, controller, spec, flight, now)
    }

    /// A fighter squadron comes into space beside the ship of `controller` (see `fighters.rs`): the same companion as a drone, without its drone notices.
    pub fn launch_companion(
        &mut self,
        kind: CompanionKind,
        controller: SessionId,
        spec: ShipSpec,
        flight: DroneFlight,
        now: i64,
    ) -> Result<(SessionId, Vec<Out>), &'static str> {
        let fighter = kind == CompanionKind::Fighter;
        let DroneFlight { weapon, graphic_id, orbit_distance, attack_range, cruise_fraction, work, effect } = flight;
        self.settle_pilot(controller, now);
        let index = self.drones_of(controller).len();
        let (controller_ship, owner_character_id, position, direction, controller_radius) = {
            let ship = &self.pilots.get(&controller).ok_or("NO_CONTROLLER")?.ship;
            (ship.spec.ship_id, ship.spec.character_id, ship.position, ship.direction, ship.spec.radius)
        };
        let orbit_distance = orbit_distance.clamp(500.0, 5_000.0);
        let ahead = controller_radius.max(1.0) + LAUNCH_OFFSET;
        let sideways = (index % 5) as f64 * 30.0 * if index.is_multiple_of(2) { 1.0 } else { -1.0 };
        let side = perpendicular(direction);
        let place = NpcPlace {
            position: Vec3::new(
                position.x + direction.x * ahead + side.x * sideways,
                position.y + direction.y * ahead + side.y * sideways,
                position.z + direction.z * ahead + side.z * sideways,
            ),
            velocity: Vec3::new(
                direction.x * cruise_fraction * spec.max_velocity,
                direction.y * cruise_fraction * spec.max_velocity,
                direction.z * cruise_fraction * spec.max_velocity,
            ),
            direction,
            speed_fraction: cruise_fraction,
            // the ball is told as it flies round the ship at no distance; the order that follows gives the distance
            course: Course::Orbit { target: controller_ship, distance: 0.0 },
        };
        // the reach of what it does to its target: its weapon's optimal range, else its remote effect's (a drone's effects have no falloff)
        let optimal = weapon.as_ref().map_or_else(
            || effect.as_ref().map_or(0.0, DroneEffect::optimal),
            |weapon| weapon.turret.optimal_range,
        );
        let falloff = weapon.as_ref().map_or(0.0, |weapon| weapon.turret.falloff);
        let can_combat = weapon.is_some() || effect.is_some();
        // `resolveDroneEngagementRanges`: the drone fights inside its attack range, circles at the least of that and its orbit, and chases what runs out past the falloff of its weapon
        let engagement_range = if attack_range > 0.0 {
            attack_range.max(optimal)
        } else {
            optimal.max(orbit_distance.min(optimal.max(1.0)))
        };
        let identity = NpcIdentity {
            combat: NpcCombat { weapons: weapon.into_iter().collect(), aggression_range: 0.0 },
            drone: Some(DroneLink {
                controller,
                owner_character_id,
                launched_from_ship_id: controller_ship,
                controller_ship_id: controller_ship,
                graphic_id,
                orbit_distance,
                cruise_fraction,
                activity: IDLE,
                kind,
                squadron: None,
            }),
            ..NpcIdentity::default()
        };
        let session = self.insert_npc(spec, identity, place, now);
        self.drones.insert(
            session,
            DroneAi {
                task: Task::Idle,
                follow: None,
                next_shot: 0,
                remote: effect,
                next_remote: 0,
                landing: None,
                ending: None,
                engagement_range,
                movement_range: orbit_distance.min(engagement_range),
                chase_range: engagement_range.max(engagement_range + falloff),
                work,
                can_combat,
                work_control_range: CONTROL_RANGE,
                work_ready_at: 0,
                work_cycle_id: 0,
                awaiting_work: false,
                auto_salvage: false,
                work_phase: DroneWorkPhase::ToTarget,
            },
        );

        // the others who see the drone are sent it as they are sent any ship; its controller is told as the reference tells it
        let mut out: Vec<Out> =
            self.announce_arrival(session, now).into_iter().filter(|o| o.session() != controller).collect();
        let stamp = self.next_stamp(now);
        let drone_id = self.ship_id_of(session);
        let circling = vec![
            payload::action(stamp, "Orbit", [id(drone_id), id(controller_ship), int(orbit_distance as i64)]),
            payload::action(stamp, "SetSpeedFraction", [id(drone_id), real(cruise_fraction)]),
        ];
        let others: Vec<SessionId> =
            self.observers_of(session).into_iter().filter(|o| *o != controller).collect();
        out.extend(others.into_iter().map(|other| Self::update(other, circling.clone())));
        if self.pilots.get(&controller).is_some_and(|c| c.bound)
            && let Ok(added) = payload::add_ship(stamp, &self.pilots[&session].ship, now)
        {
            if let Some(pilot) = self.pilots.get_mut(&controller) {
                pilot.sees.insert(session);
            }
            // a drone's controller is told what it is doing; a fighter squadron is not a drone and the host hears of it through its own notices
            let told = if fighter {
                vec![added]
            } else {
                vec![self.drone_state_action(stamp, session), added]
            };
            out.push(Self::update(controller, told));
            out.push(Self::update(controller, circling));
        }
        if let Some(pilot) = self.pilots.get_mut(&session) {
            pilot.ship.course = Course::Orbit { target: controller_ship, distance: orbit_distance };
        }
        Ok((session, out))
    }

    /// The ships of the scene that are drones of this controller, by ship id.
    pub fn drones_of(&self, controller: SessionId) -> Vec<i64> {
        let mut ships: Vec<i64> = self
            .drones
            .keys()
            .filter_map(|session| self.pilots.get(session))
            .filter(|pilot| {
                pilot
                    .ship
                    .npc
                    .as_ref()
                    .and_then(|npc| npc.drone.as_ref())
                    .is_some_and(|link| link.controller == controller)
            })
            .map(|pilot| pilot.ship.spec.ship_id)
            .collect();
        ships.sort_unstable();
        ships
    }

    /// The session of the pilot whose drone this ship is, when it is a drone.
    pub(crate) fn controller_of_drone(&self, session: SessionId) -> Option<SessionId> {
        self.pilots.get(&session)?.ship.npc.as_ref()?.drone.as_ref().map(|link| link.controller)
    }

    /// The `OnDroneStateChange` of a drone as it goes in the stream of the controller: `(drone, owner, controlling ship, state, type, owner, target)`.
    fn drone_state_action(&self, stamp: u32, session: SessionId) -> Value {
        payload::action(stamp, "OnDroneStateChange", self.drone_state_args(session))
    }

fn ship_of_character(&self, character_id: i64) -> Option<i64> {
        self.pilots
            .iter()
            .find(|(session, pilot)| {
                !crate::npc::is_npc_session(**session) && pilot.ship.spec.character_id == character_id
            })
            .map(|(_, pilot)| pilot.ship.spec.ship_id)
    }

    fn drone_state_args(&self, session: SessionId) -> Vec<Value> {
        let Some(pilot) = self.pilots.get(&session) else { return Vec::new() };
        let Some(link) = pilot.ship.npc.as_ref().and_then(|npc| npc.drone.as_ref()) else {
            return Vec::new();
        };
        let ai = self.drones.get(&session);
        let target = match ai.map(|ai| ai.task) {
            Some(Task::Engage { target }) => id(target),
            Some(Task::MineRepeatedly { target }) | Some(Task::Salvage { target: Some(target) }) => Value::Long(target),
            Some(Task::Home | Task::Bay) => id(link.controller_ship_id),
            _ => ai
                .and_then(|ai| ai.follow)
                .and_then(|follow| self.ship_of_character(follow.character_id()))
                .map_or(Value::None, id),
        };
        let owner = int(pilot.ship.spec.owner_id);
        vec![
            id(pilot.ship.spec.ship_id),
            owner.clone(),
            id(link.controller_ship_id),
            int(i64::from(link.activity)),
            int(i64::from(pilot.ship.spec.type_id)),
            owner,
            target,
        ]
    }

    /// The controller is told what the drone is doing now: `OnDroneStateChange` and `OnDroneActivityChange` (nothing about an assignment).
    fn told_state(&self, session: SessionId) -> Vec<Out> {
        if self.is_fighter(session) {
            return Vec::new();
        }
        let Some(controller) = self.controller_of_drone(session) else { return Vec::new() };
        let Some(pilot) = self.pilots.get(&session) else { return Vec::new() };
        vec![
            Out::Notify {
                session: controller,
                name: "OnDroneStateChange",
                id_type: "charid",
                args: self.drone_state_args(session),
            },
            Out::Notify {
                session: controller,
                name: "OnDroneActivityChange",
                id_type: "charid",
                args: vec![id(pilot.ship.spec.ship_id), Value::None, Value::None],
            },
        ]
    }

    fn set_activity(&mut self, session: SessionId, activity: i32) {
        if let Some(link) = self
            .pilots
            .get_mut(&session)
            .and_then(|pilot| pilot.ship.npc.as_mut())
            .and_then(|npc| npc.drone.as_mut())
        {
            link.activity = activity;
        }
    }

    /// Sets the drone circling `target` `distance` metres off its surface at `speed_fraction`, telling whoever sees it.
    pub(crate) fn circle(
        &mut self,
        session: SessionId,
        target: i64,
        distance: f64,
        speed_fraction: f64,
        now: i64,
    ) -> Vec<Out> {
        let Some(pilot) = self.pilots.get_mut(&session) else { return Vec::new() };
        pilot.ship.course = Course::Orbit { target, distance };
        pilot.ship.speed_fraction = speed_fraction;
        let drone_id = pilot.ship.spec.ship_id;
        let stamp = self.next_stamp(now);
        self.tell_all(
            session,
            vec![
                payload::action(stamp, "Orbit", [id(drone_id), id(target), int(distance as i64)]),
                payload::action(stamp, "SetSpeedFraction", [id(drone_id), real(speed_fraction)]),
            ],
        )
    }

    /// The drone stops what it is doing and goes back to its controller; what its remote effect lands on its target is lifted first.
    pub(crate) fn go_home(&mut self, session: SessionId, activity: i32, now: i64) -> Vec<Out> {
        let mut out = self.stop_remote(session, now);
        out.extend(self.fly_home(session, activity, now));
        out
    }

    /// The drone goes back to circling its controller.
    fn fly_home(&mut self, session: SessionId, activity: i32, now: i64) -> Vec<Out> {
        let Some((controller_ship, distance, cruise)) = self
            .pilots
            .get(&session)
            .and_then(|pilot| pilot.ship.npc.as_ref())
            .and_then(|npc| npc.drone.as_ref())
            .map(|link| (link.controller_ship_id, link.orbit_distance, link.cruise_fraction))
        else {
            return Vec::new();
        };
        // a drone that is coming back flies at its full speed; one that has only nothing to do again keeps pace with its controller
        let speed = if activity == IDLE { cruise } else { 1.0 };
        let mut out = self.circle(session, controller_ship, distance, speed, now);
        self.set_activity(session, activity);
        out.extend(self.told_state(session));
        out
    }

    /// A pilot gives its drones an order at the current 20 km base control range.
    pub fn drone_command(
        &mut self,
        controller: SessionId,
        drones: &[i64],
        command: DroneCommand,
        now: i64,
    ) -> Vec<Out> {
        self.drone_command_with_range(controller, drones, command, CONTROL_RANGE, now)
    }

fn can_reconnect(&self, controller: SessionId, drone_session: SessionId) -> bool {
        let Some(controller_pilot) = self.pilots.get(&controller) else { return false };
        let owner = controller_pilot.ship.spec.character_id;
        let controller_ship_id = controller_pilot.ship.spec.ship_id;
        let Some(drone) = self.pilots.get(&drone_session) else { return false };
        let is_abandoned = drone.ship.npc.as_ref().and_then(|npc| npc.drone.as_ref())
            .is_some_and(|link| {
                link.controller == 0
                    && link.owner_character_id == owner
                    && link.launched_from_ship_id == controller_ship_id
            });
        is_abandoned && self.apart(controller, drone_session).is_some_and(|distance| distance <= DRONE_RECONNECT_RANGE)
    }

    /// A pilot gives its drones an order using the fitted Dogma control range.
    pub fn drone_command_with_range(
        &mut self,
        controller: SessionId,
        drones: &[i64],
        command: DroneCommand,
        control_range: f64,
        now: i64,
    ) -> Vec<Out> {
        let control_range =
            if control_range.is_finite() { control_range.max(CONTROL_RANGE) } else { CONTROL_RANGE };
        self.settle_all(now);
        let mut out = Vec::new();
        let mut results = Vec::new();
        // an order to engage that names nothing (the pilot has locked nothing) sends the drones to look for an opponent themselves
        let searching = matches!(command, DroneCommand::Engage { target } if target <= 0);
        let found =
            if searching { self.opponents_of_with_range(controller, now, control_range) } else { Vec::new() };
        let mut asked = 0;
        for &drone_id in drones {
            let session = self.session_of(drone_id).filter(|session| match command {
                DroneCommand::Reconnect => self.can_reconnect(controller, *session),
                _ => self.controller_of_drone(*session) == Some(controller) && !self.is_fighter(*session),
            });
            let Some(session) = session else {
                results.push((drone_id, Some("NOT_CONTROLLED")));
                continue;
            };
            if matches!(command, DroneCommand::Engage { .. })
                && self.drones.get(&session).is_some_and(|ai| !ai.can_combat)
            {
                results.push((drone_id, Some("NO_WEAPON")));
                continue;
            }
            // a drone that only mends finds nothing to mend by itself
            if searching && matches!(command, DroneCommand::Engage { .. }) && self.only_repairs(session) {
                results.push((drone_id, Some("NO_OPPONENT")));
                continue;
            }
            match command {
                DroneCommand::Engage { .. } if searching => {
                    let Some(&target) = found.get(asked.min(found.len().saturating_sub(1))) else {
                        results.push((drone_id, Some("NO_OPPONENT")));
                        continue;
                    };
                    asked += 1;
                    if let Some(ai) = self.drones.get_mut(&session) {
                        ai.follow = None;
                    }
                    out.extend(self.engage(session, target, now));
                }
                DroneCommand::Engage { target } => {
                    let Some(target_session) = self.session_of(target).filter(|t| *t != session) else {
                        results.push((drone_id, Some("NO_TARGET")));
                        continue;
                    };
                    if target_session == controller || self.controller_of_drone(target_session) == Some(controller) {
                        results.push((drone_id, Some("NO_TARGET")));
                        continue;
                    }
                    // a drone that only mends is sent at a friendly ship (the reference's repair task) and at nothing else
                    if self.only_repairs(session) && !self.is_friendly_to(controller, target_session) {
                        results.push((drone_id, Some("NO_TARGET")));
                        continue;
                    }
                    if self.apart(controller, target_session).is_some_and(|apart| apart > control_range) {
                        results.push((drone_id, Some("OUT_OF_RANGE")));
                        continue;
                    }
                    if let Some(ai) = self.drones.get_mut(&session) {
                        ai.follow = None;
                    }
                    out.extend(self.engage(session, target, now));
                }
                DroneCommand::ReturnHome => {
                    if let Some(ai) = self.drones.get_mut(&session) {
                        ai.task = Task::Home;
                        ai.follow = None;
                    }
                    out.extend(self.go_home(session, DEPARTING, now));
                }
                DroneCommand::ReturnBay => {
                    if let Some(ai) = self.drones.get_mut(&session) {
                        ai.task = Task::Bay;
                        ai.follow = None;
                    }
                    out.extend(self.go_home(session, DEPARTING, now));
                }
                DroneCommand::Reconnect => {
                    let Some(controller_ship) = self.pilots.get(&controller).map(|pilot| pilot.ship.spec.ship_id) else {
                        results.push((drone_id, Some("NO_CONTROLLER")));
                        continue;
                    };
                    let linked = if let Some(pilot) = self.pilots.get_mut(&session) {
                        if let Some(link) = pilot.ship.npc.as_mut().and_then(|npc| npc.drone.as_mut()) {
                            link.controller = controller;
                            link.controller_ship_id = controller_ship;
                            true
                        } else {
                            false
                        }
                    } else {
                        false
                    };
                    if !linked {
                        results.push((drone_id, Some("NOT_RECONNECTABLE")));
                        continue;
                    }
                    if let Some(ai) = self.drones.get_mut(&session) {
                        ai.task = Task::Home;
                        ai.follow = None;
                    }
                    out.extend(self.go_home(session, DEPARTING, now));
                }
                DroneCommand::Abandon => out.extend(self.abandon(session, now)),
                DroneCommand::Assist { character_id } | DroneCommand::Guard { character_id } => {
                    if self.ship_of_character(character_id).is_none() {
                        results.push((drone_id, Some("NO_PILOT")));
                        continue;
                    }
                    let follow = match command {
                        DroneCommand::Assist { .. } =>
                            DroneFollow::Assist { character_id, control_range },
                        DroneCommand::Guard { .. } =>
                            DroneFollow::Guard { character_id, control_range },
                        _ => unreachable!(),
                    };
                    if let Some(ai) = self.drones.get_mut(&session) {
                        ai.task = Task::Idle;
                        ai.follow = Some(follow);
                    }
                    out.extend(self.go_home(session, IDLE, now));
                }
                DroneCommand::MineRepeatedly { target } => {
                    if !self.asteroids.get(&target).is_some_and(|asteroid| asteroid.units_remaining > 0) {
                        results.push((drone_id, Some("NO_TARGET")));
                        continue;
                    }
                    if self.distance_to_asteroid(controller, target, now).is_none_or(|apart| apart > control_range) {
                        results.push((drone_id, Some("OUT_OF_RANGE")));
                        continue;
                    }
                    let cycle_ms = self.drones[&session].work.cycle_ms;
                    if self.drones[&session].work.mining_amount_m3 <= 0.0 || !cycle_ms.is_finite() || cycle_ms <= 0.0 {
                        results.push((drone_id, Some("NOT_MINING_DRONE")));
                        continue;
                    }
                    let range = self.pilots[&session].ship.npc.as_ref().and_then(|npc| npc.drone.as_ref())
                        .map(|link| link.orbit_distance).unwrap_or(0.0);
                    if let Some(ai) = self.drones.get_mut(&session) {
                        ai.task = Task::MineRepeatedly { target };
                        ai.follow = None;
                        ai.work_control_range = control_range;
                        ai.work_ready_at = 0;
                        ai.work_cycle_id = 0;
                        ai.awaiting_work = false;
                        ai.auto_salvage = false;
                        ai.work_phase = DroneWorkPhase::ToTarget;
                    }
                    out.extend(self.circle(session, target, range, 1.0, now));
                    self.set_activity(session, APPROACHING);
                    out.extend(self.told_state(session));
                }
                DroneCommand::Salvage { target } => {
                    let auto_salvage = target.is_none();
                    let target = match target {
                        Some(id) => {
                            if !self.wreck(id).is_some_and(|wreck| wreck.group_id == crate::wreck::WRECK_GROUP) {
                                results.push((drone_id, Some("NO_WRECK")));
                                continue;
                            }
                            if self.distance_to_wreck(controller, id, now).is_none_or(|apart| apart > control_range) {
                                results.push((drone_id, Some("OUT_OF_RANGE")));
                                continue;
                            }
                            id
                        }
                        None => {
                            let Some(pilot) = self.pilots.get(&controller) else {
                                results.push((drone_id, Some("NO_WRECK")));
                                continue;
                            };
                            let character_id = pilot.ship.spec.character_id;
                            let corporation_id = i64::from(pilot.ship.spec.corporation_id);
                            let candidates: Vec<i64> = self.wrecks.values()
                                .filter(|wreck| {
                                    wreck.group_id == crate::wreck::WRECK_GROUP
                                        && (wreck.rights.character_id == character_id
                                            || (corporation_id > 0 && wreck.rights.corporation_id == corporation_id))
                                })
                                .map(|wreck| wreck.id)
                                .collect();
                            let mut reachable: Vec<(f64, i64)> = candidates.into_iter()
                                .filter_map(|id| self.distance_to_wreck(controller, id, now)
                                    .filter(|apart| *apart <= control_range)
                                    .map(|apart| (apart, id)))
                                .collect();
                            reachable.sort_by(|a, b| a.0.total_cmp(&b.0).then(a.1.cmp(&b.1)));
                            let Some((_, id)) = reachable.first().copied() else {
                                results.push((drone_id, Some("NO_WRECK")));
                                continue;
                            };
                            id
                        }
                    };
                    let cycle_ms = self.drones[&session].work.cycle_ms;
                    if self.drones[&session].work.salvage_access_bonus <= 0.0 || !cycle_ms.is_finite() || cycle_ms <= 0.0 {
                        results.push((drone_id, Some("NOT_SALVAGE_DRONE")));
                        continue;
                    }
                    let range = self.pilots[&session].ship.npc.as_ref().and_then(|npc| npc.drone.as_ref())
                        .map(|link| link.orbit_distance).unwrap_or(0.0);
                    if let Some(ai) = self.drones.get_mut(&session) {
                        ai.task = Task::Salvage { target: Some(target) };
                        ai.follow = None;
                        ai.work_control_range = control_range;
                        ai.work_ready_at = 0;
                        ai.work_cycle_id = 0;
                        ai.awaiting_work = false;
                        ai.auto_salvage = auto_salvage;
                        ai.work_phase = DroneWorkPhase::ToTarget;
                    }
                    out.extend(self.circle(session, target, range, 1.0, now));
                    self.set_activity(session, APPROACHING);
                    out.extend(self.told_state(session));
                }
            }
            results.push((drone_id, None));
        }
        out.push(Out::DroneResults { session: controller, command: command.code(), results });
        out
    }

    /// Finds targets using the range passed with a drone command.
    pub(crate) fn opponents_of_with_range(
        &self,
        controller: SessionId,
        now: i64,
        control_range: f64,
    ) -> Vec<i64> {
        let control_range =
            if control_range.is_finite() { control_range.max(CONTROL_RANGE) } else { CONTROL_RANGE };
        let Some(controller_ship) = self.pilots.get(&controller).map(|pilot| pilot.ship.spec.ship_id) else {
            return Vec::new();
        };
        let reachable = |scene: &Scene, session: SessionId| {
            crate::npc::is_npc_session(session)
                && scene.controller_of_drone(session).is_none()
                && scene.apart(controller, session).is_some_and(|apart| apart <= control_range)
        };
        let mut found: Vec<i64> = Vec::new();
        let keep = |ship: i64, found: &mut Vec<i64>| {
            if !found.contains(&ship) {
                found.push(ship);
            }
        };
        let mut recent: Vec<(i64, i64)> = self.drone_threats.get(&controller).cloned().unwrap_or_default();
        recent.retain(|(_, at)| now - at <= THREAT_MEMORY);
        recent.sort_by_key(|(ship, at)| (std::cmp::Reverse(*at), *ship));
        for (ship, _) in recent {
            if self.session_of(ship).is_some_and(|session| reachable(self, session)) {
                keep(ship, &mut found);
            }
        }
        // those that have the controller or a drone of it locked
        let mine: Vec<i64> = std::iter::once(controller_ship).chain(self.drones_of(controller)).collect();
        let mut locking: Vec<i64> = self
            .pilots
            .iter()
            .filter(|(session, pilot)| {
                reachable(self, **session)
                    && pilot.targeting.targets().iter().any(|locked| mine.contains(locked))
            })
            .map(|(_, pilot)| pilot.ship.spec.ship_id)
            .collect();
        locking.sort_unstable();
        for ship in locking {
            keep(ship, &mut found);
        }
        // then the nearest
        let mut near: Vec<(f64, i64)> = self
            .pilots
            .iter()
            .filter(|(session, _)| reachable(self, **session))
            .filter_map(|(session, pilot)| Some((self.apart(controller, *session)?, pilot.ship.spec.ship_id)))
            .collect();
        near.sort_by(|a, b| a.0.total_cmp(&b.0).then(a.1.cmp(&b.1)));
        for (_, ship) in near {
            keep(ship, &mut found);
        }
        found
    }

    /// Idle drones take up the fight when the controller or a followed pilot is attacked.
    pub(crate) fn note_drone_threats(&mut self, now: i64) -> Vec<Out> {
        if self.drones.is_empty() || self.aggressions.is_empty() {
            return Vec::new();
        }
        let acts: Vec<(i64, i64)> =
            self.aggressions.iter().map(|a| (a.attacker_ship, a.target_ship)).collect();
        let mut out = Vec::new();
        for (attacker, target) in acts {
            let following: Vec<(SessionId, i64)> = self.drones.iter().filter_map(|(drone, ai)| {
                let follow = ai.follow?;
                let followed_ship = self.ship_of_character(follow.character_id())?;
                let target_ship = match follow {
                    DroneFollow::Assist { .. } if attacker == followed_ship => target,
                    DroneFollow::Guard { .. } if target == followed_ship => attacker,
                    _ => return None,
                };
                let controller = self.controller_of_drone(*drone)?;
                let target_session = self.session_of(target_ship)?;
                if target_session == controller || self.controller_of_drone(target_session) == Some(controller) {
                    return None;
                }
                self.apart(controller, target_session)
                    .filter(|apart| *apart <= follow.control_range())?;
                Some((*drone, target_ship))
            }).collect();
            for (drone, target) in following {
                out.extend(self.engage(drone, target, now));
            }

            let Some(controller) = self.session_of(target).filter(|s| !crate::npc::is_npc_session(*s)) else {
                continue;
            };
            if self.drones_of(controller).is_empty() || self.ship_id_of(controller) == attacker {
                continue;
            }
            let Some(attacker_session) = self.session_of(attacker) else { continue };
            if self.controller_of_drone(attacker_session) == Some(controller)
                || self.apart(controller, attacker_session).is_none_or(|apart| apart > CONTROL_RANGE)
            {
                continue;
            }
            let threats = self.drone_threats.entry(controller).or_default();
            threats.retain(|(ship, at)| *ship != attacker && now - at <= THREAT_MEMORY);
            threats.push((attacker, now));
            let idle: Vec<SessionId> = self
                .drones
                .iter()
                .filter(|(session, ai)| {
                    ai.task == Task::Idle
                        && ai.follow.is_none()
                        && self.controller_of_drone(**session) == Some(controller)
                })
                .map(|(session, _)| *session)
                .collect();
            if idle.is_empty() {
                continue;
            }
            let targets = self.threats_of(controller, now);
            for (index, session) in idle.into_iter().enumerate() {
                let Some(&ship) = targets.get(index.min(targets.len().saturating_sub(1))) else { break };
                out.extend(self.engage(session, ship, now));
            }
        }
        out
    }

    /// Who attacked a controller lately and is still in the scene, the newest first.
    fn threats_of(&self, controller: SessionId, now: i64) -> Vec<i64> {
        let mut recent: Vec<(i64, i64)> = self.drone_threats.get(&controller).cloned().unwrap_or_default();
        recent.retain(|(ship, at)| now - at <= THREAT_MEMORY && self.session_of(*ship).is_some());
        recent.sort_by_key(|(ship, at)| (std::cmp::Reverse(*at), *ship));
        recent.into_iter().map(|(ship, _)| ship).collect()
    }

    /// Whether a drone has a remote repair and no weapon: it is sent at friendly ships only.
    fn only_repairs(&self, session: SessionId) -> bool {
        self.drones.get(&session).and_then(|ai| ai.remote.as_ref()).is_some_and(DroneEffect::repairs)
            && self.pilots.get(&session).and_then(|pilot| pilot.ship.npc.as_ref()).is_some_and(|npc| npc.combat.weapons.is_empty())
    }

    /// Whether a ship is on the side of a pilot (the reference's `isFriendlyRepairTarget`: its character or owner, its corporation or its alliance).
    fn is_friendly_to(&self, controller: SessionId, target: SessionId) -> bool {
        let (Some(controller), Some(target)) = (self.pilots.get(&controller), self.pilots.get(&target)) else {
            return false;
        };
        let (c, t) = (&controller.ship.spec, &target.ship.spec);
        (c.character_id > 0 && (t.character_id == c.character_id || t.owner_id == c.character_id))
            || (c.owner_id > 0 && t.owner_id == c.owner_id)
            || (c.corporation_id > 0 && t.corporation_id == c.corporation_id)
            || (c.alliance_id > 0 && t.alliance_id == c.alliance_id)
    }

    /// The drone is sent at a target: it circles it at full speed and is told as pursuing, approaching or fighting by how far off it is.
    fn engage(&mut self, session: SessionId, target: i64, now: i64) -> Vec<Out> {
        if self.drones.get(&session).is_some_and(|ai| !ai.can_combat) {
            return Vec::new();
        }
        let Some(target_session) = self.session_of(target) else { return Vec::new() };
        let Some((engagement, movement, chase)) =
            self.drones.get(&session).map(|ai| (ai.engagement_range, ai.movement_range, ai.chase_range))
        else {
            return Vec::new();
        };
        let apart = self.apart(session, target_session).unwrap_or(0.0);
        if let Some(ai) = self.drones.get_mut(&session) {
            ai.task = Task::Engage { target };
            ai.next_shot = now;
            ai.next_remote = now;
        }
        let activity = if apart > chase + 1.0 {
            PURSUIT
        } else if apart > engagement + 1.0 {
            APPROACHING
        } else {
            COMBAT
        };
        let mut out = self.circle(session, target, movement, 1.0, now);
        self.set_activity(session, activity);
        out.extend(self.told_state(session));
        out
    }

    /// The drone is left in this scene under nobody's control; the host keeps it as the pilot's item and may reconnect it later.
    pub(crate) fn abandon(&mut self, session: SessionId, now: i64) -> Vec<Out> {
        let mut out = self.stop_remote(session, now);
        out.extend(self.let_go(session, now));
        out
    }

    fn let_go(&mut self, session: SessionId, now: i64) -> Vec<Out> {
        let Some(ship_id) = self.pilots.get(&session).map(|pilot| pilot.ship.spec.ship_id) else {
            return Vec::new();
        };
        let Some(controller) = self.controller_of_drone(session).filter(|controller| *controller != 0) else {
            return Vec::new();
        };
        let position = self.pilots[&session].ship.position;
        if let Some(pilot) = self.pilots.get_mut(&session) {
            if let Some(link) = pilot.ship.npc.as_mut().and_then(|npc| npc.drone.as_mut()) {
                link.controller = 0;
                link.controller_ship_id = 0;
            }
            pilot.ship.course = Course::Stop;
            pilot.ship.speed_fraction = 0.0;
        }
        if let Some(ai) = self.drones.get_mut(&session) {
            ai.task = Task::Idle;
            ai.follow = None;
            ai.awaiting_work = false;
            ai.auto_salvage = false;
        }
        let _ = now;
        if self.is_fighter(session) {
            return vec![Out::FighterReleased { session: controller, fighter_id: ship_id, position }];
        }
        vec![Out::DroneReleased { session: controller, drone_id: ship_id, position }]
    }

    /// The drones of a pilot that has left the scene (docked, jumped, gone offline or been destroyed) are let go: they are taken out of the scene and the host is told of each.
    ///
    /// When the pilot logs off (`logoff_at`: where its ship was), a squadron within the recovery distance of that place lands in its tube instead, as the
    /// reference's `attemptFighterTubeRecovery` does on a logoff (the host moves its item in).
    pub(crate) fn release_drones(&mut self, controller: SessionId, logoff_at: Option<Vec3>, now: i64) -> Vec<Out> {
        let sessions: Vec<SessionId> = self
            .drones
            .keys()
            .copied()
            .filter(|s| self.controller_of_drone(*s) == Some(controller))
            .collect();
        let mut out = Vec::new();
        for session in sessions {
            let recovered = logoff_at.is_some_and(|at| {
                self.is_fighter(session) && {
                    let p = self.pilots[&session].ship.position;
                    ((p.x - at.x).powi(2) + (p.y - at.y).powi(2) + (p.z - at.z).powi(2)).sqrt() <= FIGHTER_RECOVERY_DISTANCE
                }
            });
            if recovered {
                out.extend(self.take_in(session, controller, now));
            } else {
                out.extend(self.abandon(session, now));
            }
        }
        out
    }

    pub(crate) fn advance_drones(&mut self, now: i64) -> Vec<Out> {
        if self.drones.is_empty() {
            return Vec::new();
        }
        let sessions: Vec<SessionId> = self.drones.keys().copied().collect();
        let mut out = Vec::new();
        for session in sessions {
            if !self.pilots.contains_key(&session) {
                self.drones.remove(&session);
                continue;
            }
            let controller_lost = self
                .pilots
                .get(&session)
                .and_then(|pilot| pilot.ship.npc.as_ref())
                .and_then(|npc| npc.drone.as_ref())
                .is_some_and(|link| {
                    link.controller != 0
                        && self.pilots
                            .get(&link.controller)
                            .is_none_or(|controller| controller.ship.spec.ship_id != link.controller_ship_id)
                });
            if controller_lost {
                out.extend(self.abandon(session, now));
                continue;
            }
            let Some(task) = self.drones.get(&session).map(|ai| ai.task) else { continue };
            match task {
                Task::Idle | Task::Home => {}
                Task::MineRepeatedly { target } => out.extend(self.advance_drone_work(session, target, 7, now)),
                Task::Salvage { target: Some(target) } => out.extend(self.advance_drone_work(session, target, 8, now)),
                Task::Salvage { target: None } => {},
                Task::Bay => out.extend(self.scoop_if_near(session, now)),
                Task::Engage { target } => out.extend(self.fight(session, target, now)),
            }
        }
        out
    }

    fn work_round_trip_ms(&mut self, session: SessionId, target: i64, command: u8, now: i64) -> i64 {
        let Some(controller) = self.controller_of_drone(session) else { return 0 };
        let distance = if command == 7 {
            self.distance_to_asteroid(controller, target, now)
        } else {
            self.distance_to_wreck(controller, target, now)
        }.unwrap_or(0.0);
        let speed = self.pilots.get(&session)
            .and_then(|pilot| {
                pilot.ship.npc.as_ref()?.drone.as_ref()
                    .map(|link| pilot.ship.spec.max_velocity * link.cruise_fraction.max(0.01))
            })
            .unwrap_or(0.0);
        if speed.is_finite() && speed > 0.0 {
            (distance * 2_000.0 / speed).ceil().clamp(0.0, i64::MAX as f64) as i64
        } else {
            0
        }
    }

    fn advance_drone_work(&mut self, session: SessionId, target: i64, command: u8, now: i64) -> Vec<Out> {
        let Some(ai) = self.drones.get(&session) else { return Vec::new() };
        if ai.awaiting_work {
            return Vec::new();
        }
        let Some(link) = self.pilots.get(&session)
            .and_then(|pilot| pilot.ship.npc.as_ref())
            .and_then(|npc| npc.drone.as_ref()) else { return Vec::new() };
        let (controller, orbit_distance) = (link.controller, link.orbit_distance);
        let (phase, range, cycle_ms, control_range) = {
            let ai = &self.drones[&session];
            (ai.work_phase, orbit_distance.max(0.0), ai.work.cycle_ms.ceil().max(1.0).min(i64::MAX as f64) as i64, ai.work_control_range)
        };
        match phase {
            DroneWorkPhase::ToTarget => {
                let distance = if command == 7 {
                    self.distance_to_asteroid(session, target, now)
                } else {
                    self.distance_to_wreck(session, target, now)
                };
                let Some(distance) = distance else {
                    if let Some(ai) = self.drones.get_mut(&session) {
                        ai.task = Task::Idle;
                    }
                    return self.go_home(session, IDLE, now);
                };
                if distance > range {
                    let mut out = self.circle(session, target, range, 1.0, now);
                    self.set_activity(session, APPROACHING);
                    out.extend(self.told_state(session));
                    return out;
                }
                if let Some(ai) = self.drones.get_mut(&session) {
                    ai.work_phase = DroneWorkPhase::Cycling;
                    ai.work_ready_at = now.saturating_add(cycle_ms);
                }
                self.set_activity(session, COMBAT);
                self.told_state(session)
            }
            DroneWorkPhase::Cycling => {
                if now < self.drones[&session].work_ready_at {
                    return Vec::new();
                }
                let target_live = match command {
                    7 => self.asteroids.get(&target).is_some_and(|asteroid| asteroid.units_remaining > 0),
                    8 => self.wreck(target).is_some_and(|wreck| wreck.group_id == crate::wreck::WRECK_GROUP),
                    _ => false,
                };
                let control_distance = if command == 7 {
                    self.distance_to_asteroid(controller, target, now)
                } else {
                    self.distance_to_wreck(controller, target, now)
                };
                if !target_live || control_distance.is_none_or(|distance| distance > control_range) {
                    let depleted = command == 7
                        && self.asteroids.get(&target).is_some_and(|asteroid| asteroid.units_remaining <= 0);
                    if let Some(ai) = self.drones.get_mut(&session) {
                        ai.task = Task::Idle;
                        ai.follow = None;
                    }
                    let mut out = if depleted { self.remove_asteroid(target, true, now) } else { Vec::new() };
                    out.extend(self.go_home(session, IDLE, now));
                    return out;
                }
                if let Some(ai) = self.drones.get_mut(&session) {
                    ai.work_phase = DroneWorkPhase::Returning;
                }
                self.go_home(session, DEPARTING, now)
            }
            DroneWorkPhase::Returning => {
                if self.apart(session, controller).is_none_or(|distance| distance > SCOOP_DISTANCE) {
                    return Vec::new();
                }
                let Some(pilot) = self.pilots.get(&controller) else { return Vec::new() };
                let (character_id, ship_id, solar_system_id) =
                    (pilot.ship.spec.character_id, pilot.ship.spec.ship_id, self.solar_system_id());
                let drone_id = self.ship_id_of(session);
                let (profile, cycle_id) = {
                    let ai = self.drones.get_mut(&session).expect("drone was found");
                    ai.awaiting_work = true;
                    // a process-wide id: the host records the last cycle it settled per drone, so an id must never come back (modules.rs)
                    ai.work_cycle_id = crate::modules::next_work_cycle_id();
                    (ai.work, ai.work_cycle_id)
                };
                vec![Out::DroneWorkCycle {
                    session: controller,
                    character_id,
                    ship_id,
                    solar_system_id,
                    drone_id,
                    target_id: target,
                    cycle_id,
                    command,
                    mining_amount_m3: profile.mining_amount_m3,
                    waste_multiplier: profile.waste_multiplier,
                    waste_probability: profile.waste_probability,
                    access_difficulty_bonus: profile.salvage_access_bonus,
                    roll: self.rng.random(),
                }]
            }
        }
    }

    pub fn drone_work_result(
        &mut self,
        controller: SessionId,
        drone_id: i64,
        target_id: i64,
        cycle_id: u64,
        accepted: bool,
        units_remaining: i64,
        complete: bool,
        now: i64,
    ) -> Vec<Out> {
        let Some(session) = self.session_of(drone_id)
            .filter(|session| self.controller_of_drone(*session) == Some(controller)) else {
            return Vec::new();
        };
        let (operation, auto_salvage, control_range, movement_range) = {
            let Some(ai) = self.drones.get(&session) else { return Vec::new() };
            let operation = match ai.task {
                Task::MineRepeatedly { target } if target == target_id => 7,
                Task::Salvage { target: Some(target) } if target == target_id => 8,
                _ => return Vec::new(),
            };
            if !ai.awaiting_work || ai.work_cycle_id != cycle_id {
                return Vec::new();
            }
            (operation, ai.auto_salvage, ai.work_control_range, ai.movement_range)
        };
        if accepted && units_remaining < 0 {
            if let Some(ai) = self.drones.get_mut(&session) {
                ai.awaiting_work = false;
                ai.task = Task::Idle;
                ai.follow = None;
            }
            return self.go_home(session, IDLE, now);
        }
        if let Some(ai) = self.drones.get_mut(&session) {
            ai.awaiting_work = false;
        }
        if !accepted {
            if let Some(ai) = self.drones.get_mut(&session) {
                ai.task = Task::Idle;
                ai.follow = None;
            }
            return self.go_home(session, IDLE, now);
        }
        if operation == 7 && units_remaining <= 0 {
            if let Some(ai) = self.drones.get_mut(&session) {
                ai.task = Task::Idle;
                ai.follow = None;
            }
            let mut out = self.remove_asteroid(target_id, true, now);
            out.extend(self.go_home(session, IDLE, now));
            return out;
        }
        if operation == 7 {
            if !self.asteroids.get(&target_id).is_some_and(|asteroid| asteroid.units_remaining > 0) {
                if let Some(ai) = self.drones.get_mut(&session) {
                    ai.task = Task::Idle;
                    ai.follow = None;
                }
                return self.go_home(session, IDLE, now);
            }
            let remaining = self.asteroids[&target_id].units_remaining.min(units_remaining);
            let mut out = self.change_asteroid(target_id, remaining, now);
            if let Some(ai) = self.drones.get_mut(&session) {
                ai.work_phase = DroneWorkPhase::ToTarget;
                ai.work_ready_at = 0;
            }
            out.extend(self.circle(session, target_id, movement_range, 1.0, now));
            self.set_activity(session, APPROACHING);
            out.extend(self.told_state(session));
            return out;
        }
        if operation == 8 && complete {
            let mut out = self.remove_wreck(target_id, 0, now);
            let next = if auto_salvage {
                let Some(pilot) = self.pilots.get(&controller) else {
                    return out;
                };
                let character_id = pilot.ship.spec.character_id;
                let corporation_id = i64::from(pilot.ship.spec.corporation_id);
                let candidates: Vec<i64> = self.wrecks.values()
                    .filter(|wreck| {
                        wreck.group_id == crate::wreck::WRECK_GROUP
                            && (wreck.rights.character_id == character_id
                                || (corporation_id > 0 && wreck.rights.corporation_id == corporation_id))
                    })
                    .map(|wreck| wreck.id)
                    .collect();
                let mut reachable: Vec<(f64, i64)> = candidates.into_iter()
                    .filter_map(|id| self.distance_to_wreck(controller, id, now)
                        .filter(|distance| *distance <= control_range)
                        .map(|distance| (distance, id)))
                    .collect();
                reachable.sort_by(|a, b| a.0.total_cmp(&b.0).then(a.1.cmp(&b.1)));
                reachable.first().map(|(_, id)| *id)
            } else {
                None
            };
            if let Some(next) = next {
                if let Some(ai) = self.drones.get_mut(&session) {
                    ai.task = Task::Salvage { target: Some(next) };
                    ai.work_phase = DroneWorkPhase::ToTarget;
                    ai.work_ready_at = 0;
                }
                out.extend(self.circle(session, next, movement_range, 1.0, now));
                self.set_activity(session, APPROACHING);
                out.extend(self.told_state(session));
            } else {
                if let Some(ai) = self.drones.get_mut(&session) {
                    ai.task = Task::Idle;
                    ai.follow = None;
                }
                out.extend(self.go_home(session, IDLE, now));
            }
            return out;
        }
        if operation == 8
            && !self.wreck(target_id).is_some_and(|wreck| wreck.group_id == crate::wreck::WRECK_GROUP)
        {
            if let Some(ai) = self.drones.get_mut(&session) {
                ai.task = Task::Idle;
                ai.follow = None;
            }
            return self.go_home(session, IDLE, now);
        }
        if let Some(ai) = self.drones.get_mut(&session) {
            ai.work_phase = DroneWorkPhase::ToTarget;
            ai.work_ready_at = 0;
        }
        let mut out = self.circle(session, target_id, movement_range, 1.0, now);
        self.set_activity(session, APPROACHING);
        out.extend(self.told_state(session));
        out
    }

    /// A drone that is coming back to the bay is taken in once it is within reach of its controller.
    fn scoop_if_near(&mut self, session: SessionId, now: i64) -> Vec<Out> {
        let Some(controller) = self.controller_of_drone(session) else { return Vec::new() };
        let Some(apart) = self.apart(session, controller) else { return Vec::new() };
        let reach = if self.is_fighter(session) { FIGHTER_RECOVERY_DISTANCE } else { SCOOP_DISTANCE };
        if apart > reach {
            return Vec::new();
        }
        self.take_in(session, controller, now)
    }

    /// A companion leaves the scene for its controller's ship: its ball is removed from every pilot's view and the controller is told (a drone is
    /// scooped, a squadron lands); the host moves the item. Shared by the landing of a recalled squadron, the scoop of a drone and the scoop of an
    /// abandoned squadron.
    pub(crate) fn take_in(&mut self, session: SessionId, controller: SessionId, now: i64) -> Vec<Out> {
        let fighter = self.is_fighter(session);
        let mut out = self.stop_remote(session, now);
        let Some(pilot) = self.pilots.remove(&session) else { return Vec::new() };
        self.drones.remove(&session);
        let drone_id = pilot.ship.spec.ship_id;
        out.push(if fighter {
            Out::FighterLanded { session: controller, fighter_id: drone_id }
        } else {
            Out::DroneScooped { session: controller, drone_id }
        });
        // the controller's drone list drops it with the ball, the others see only the ball go
        let stamp = self.next_stamp(now);
        let watchers = self.observers_of(session);
        for other in self.pilots.values_mut() {
            other.sees.remove(&session);
        }
        for watcher in watchers {
            let mut actions = Vec::new();
            if watcher == controller && !fighter {
                actions.push(payload::action(
                    stamp,
                    "OnDroneStateChange",
                    [
                        id(drone_id),
                        Value::None,
                        Value::None,
                        Value::None,
                        Value::None,
                        Value::None,
                        Value::None,
                    ],
                ));
            }
            actions.push(payload::remove_ships(stamp, [drone_id]));
            out.push(Self::update(watcher, actions));
        }
        out
    }

    /// A drone sent at a target: flies to it, and when it is in range fires its weapon each cycle; a target that is gone leaves it idle.
    fn fight(&mut self, session: SessionId, target: i64, now: i64) -> Vec<Out> {
        let Some(target_session) = self.session_of(target) else {
            if let Some(ai) = self.drones.get_mut(&session) {
                ai.task = Task::Idle;
            }
            return self.go_home(session, IDLE, now);
        };
        let Some(apart) = self.apart(session, target_session) else { return Vec::new() };
        let Some((engagement, chase)) =
            self.drones.get(&session).map(|ai| (ai.engagement_range, ai.chase_range))
        else {
            return Vec::new();
        };
        let activity = self.activity_of(session);
        let in_range = apart <= engagement + 1.0;
        // what the drone is now: in range it fights (and fires at once), a target that runs out of reach is chased
        let wanted = if in_range {
            COMBAT
        } else if activity == COMBAT && apart > chase + 1.0 {
            PURSUIT
        } else if activity == PURSUIT && apart <= chase + 1.0 {
            APPROACHING
        } else {
            activity
        };
        let mut out = Vec::new();
        if wanted != activity {
            self.set_activity(session, wanted);
            if wanted == COMBAT
                && let Some(ai) = self.drones.get_mut(&session)
            {
                ai.next_shot = now;
                ai.next_remote = now;
            }
            out.extend(self.told_state(session));
        }
        if wanted != COMBAT {
            return out;
        }
        // the weapon and the remote effect each run on their own clocks
        let (next_shot, next_remote) =
            self.drones.get(&session).map_or((i64::MAX, i64::MAX), |ai| (ai.next_shot, ai.next_remote));
        if now >= next_shot
            && let Some(effect) = self.drone_effect(session, target)
        {
            if let Some(ai) = self.drones.get_mut(&session) {
                ai.next_shot = now + (effect.duration_ms * crate::scene::TICKS_PER_SECOND as f64 / 1000.0) as i64;
            }
            out.extend(self.cycle_fx(session, &effect, now));
            out.extend(self.fire_turret(session, &effect, now));
        }
        if now >= next_remote
            && let Some((effect, lasting_ms)) = self.remote_effect(session, target)
        {
            if let Some(ai) = self.drones.get_mut(&session) {
                ai.next_remote = now + (effect.duration_ms * crate::scene::TICKS_PER_SECOND as f64 / 1000.0) as i64;
            }
            out.extend(self.remote_cycle(session, &effect, lasting_ms, now));
        }
        // a target that went down with the shot leaves the drone nothing to do
        if !self.pilots.contains_key(&target_session) {
            if let Some(ai) = self.drones.get_mut(&session) {
                ai.task = Task::Idle;
            }
            out.extend(self.go_home(session, IDLE, now));
        }
        out
    }

    /// The client is told that the drone's effect plays at its target (`OnSpecialFX`), to those who look on as to its pilot.
    fn cycle_fx(&mut self, session: SessionId, effect: &Effect, now: i64) -> Vec<Out> {
        let drone_id = self.ship_id_of(session);
        let stamp = self.next_stamp(now);
        let fx = payload::action(
            stamp,
            "OnSpecialFX",
            [
                id(drone_id),
                id(drone_id),
                int(i64::from(effect.module_type)),
                id(effect.target_id),
                Value::None,
                text(effect.fx.clone()),
                int(i64::from(effect.offensive())),
                int(1),
                int(0),
                real(effect.duration_ms),
                int(1),
                Value::None,
                int(0),
                Value::None,
            ],
        );
        self.tell_all(session, vec![fx])
    }

    /// The remote effect a drone runs at a target each cycle, as an effect of the scene (its module is the drone), and how long what it lands lasts.
    fn remote_effect(&self, session: SessionId, target: i64) -> Option<(Effect, f64)> {
        let remote = self.drones.get(&session)?.remote.as_ref()?;
        let spec = &self.pilots.get(&session)?.ship.spec;
        let effect = Effect {
            burns_fuel: false,
            module_id: spec.ship_id,
            module_type: spec.type_id,
            effect_id: remote.effect_id,
            fx: remote.fx.clone(),
            duration_ms: remote.cycle_ms,
            capacitor_need: 0.0,
            repeat: i32::MAX,
            target_id: target,
            turret: None,
            action: Some(remote.action.clone()),
            on: None,
            off: None,
            on_changes: Vec::new(),
            off_changes: Vec::new(),
        };
        Some((effect, remote.lasting_ms))
    }

    /// A cycle of a remote effect: the cycle that has ended lands its end first; the target's HUD is told as the effect begins on it (refreshed while it goes on, and
    /// the previous target let go of when it changes); then the cycle lands now (a web, a jam) or at the end of the cycle (a repair).
    fn remote_cycle(&mut self, session: SessionId, effect: &Effect, lasting_ms: f64, now: i64) -> Vec<Out> {
        let mut out = Vec::new();
        if let Some(ended) = self.drones.get_mut(&session).and_then(|ai| ai.ending.take()) {
            out.extend(self.land_remote(session, &ended, now));
        }
        let landing = Effect { duration_ms: lasting_ms, ..effect.clone() };
        let Some((_, _, when)) = landing.remote() else { return out };
        let same = self
            .drones
            .get(&session)
            .and_then(|ai| ai.landing.as_ref())
            .is_some_and(|current| current.target_id == landing.target_id);
        if !same {
            out.extend(self.stop_remote(session, now));
        }
        out.extend(self.cycle_fx(session, effect, now));
        out.extend(self.jam_told(session, &landing, now, if same { Jam::Refresh } else { Jam::Start }));
        match when {
            Applies::CycleStart => {
                out.extend(self.land_remote(session, &landing, now));
                if let Some(ai) = self.drones.get_mut(&session) {
                    ai.landing = Some(landing);
                }
            }
            Applies::CycleEnd => {
                if let Some(ai) = self.drones.get_mut(&session) {
                    ai.landing = Some(landing.clone());
                    ai.ending = Some(landing);
                }
            }
        }
        out
    }

    /// What a cycle lands on its target now: the effect projected at the ship it is aimed at, weakened by distance and by the target's resistance (the modules' `project`).
    fn land_remote(&mut self, session: SessionId, landing: &Effect, now: i64) -> Vec<Out> {
        let Some((what, reach, _)) = landing.remote() else { return Vec::new() };
        self.project(session, landing, what, reach, now)
    }

    /// The drone stops its remote effect: a repair due at the end of its cycle lands first, then what it holds on its target is lifted and the target's HUD is told
    /// it ended.
    pub(crate) fn stop_remote(&mut self, session: SessionId, now: i64) -> Vec<Out> {
        let mut out = Vec::new();
        if let Some(ended) = self.drones.get_mut(&session).and_then(|ai| ai.ending.take()) {
            out.extend(self.land_remote(session, &ended, now));
        }
        let Some(landing) = self.drones.get_mut(&session).and_then(|ai| ai.landing.take()) else {
            return out;
        };
        let Some(source) = self.pilots.get(&session).map(|pilot| pilot.ship.spec.ship_id) else {
            return out;
        };
        out.extend(self.jam_told(session, &landing, now, Jam::End));
        out.extend(self.release(source, landing.module_id, landing.target_id, now));
        out
    }

    fn activity_of(&self, session: SessionId) -> i32 {
        self.pilots
            .get(&session)
            .and_then(|pilot| pilot.ship.npc.as_ref())
            .and_then(|npc| npc.drone.as_ref())
            .map_or(IDLE, |link| link.activity)
    }

    /// The effect a drone's weapon runs at a target: the weapon the host gave it, each cycle one shot.
    fn drone_effect(&self, session: SessionId, target: i64) -> Option<Effect> {
        let weapon = self.pilots.get(&session)?.ship.npc.as_ref()?.combat.weapons.first()?.clone();
        Some(Effect {
            burns_fuel: false,
            module_id: weapon.module_id,
            module_type: weapon.turret.weapon_type_id,
            effect_id: weapon.effect_id,
            fx: weapon.turret.fx.clone(),
            duration_ms: weapon.cycle_ms,
            capacitor_need: 0.0,
            repeat: i32::MAX,
            target_id: target,
            turret: Some(weapon.turret),
            action: None,
            on: None,
            off: None,
            on_changes: Vec::new(),
            off_changes: Vec::new(),
        })
    }
}

#[cfg(test)]
mod tests;
