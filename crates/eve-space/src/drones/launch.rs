use super::*;

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
        let DroneFlight { weapon, graphic_id, orbit_distance, attack_range, cruise_fraction, work, effect } =
            flight;
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
            let told =
                if fighter { vec![added] } else { vec![self.drone_state_action(stamp, session), added] };
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
    pub(super) fn drone_state_action(&self, stamp: u32, session: SessionId) -> Value {
        payload::action(stamp, "OnDroneStateChange", self.drone_state_args(session))
    }

    pub(super) fn ship_of_character(&self, character_id: i64) -> Option<i64> {
        self.pilots
            .iter()
            .find(|(session, pilot)| {
                !crate::npc::is_npc_session(**session) && pilot.ship.spec.character_id == character_id
            })
            .map(|(_, pilot)| pilot.ship.spec.ship_id)
    }

    pub(super) fn drone_state_args(&self, session: SessionId) -> Vec<Value> {
        let Some(pilot) = self.pilots.get(&session) else { return Vec::new() };
        let Some(link) = pilot.ship.npc.as_ref().and_then(|npc| npc.drone.as_ref()) else {
            return Vec::new();
        };
        let ai = self.drones.get(&session);
        let target = match ai.map(|ai| ai.task) {
            Some(Task::Engage { target }) => id(target),
            Some(Task::MineRepeatedly { target }) | Some(Task::Salvage { target: Some(target) }) => {
                Value::Long(target)
            }
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
    pub(super) fn told_state(&self, session: SessionId) -> Vec<Out> {
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

    pub(super) fn set_activity(&mut self, session: SessionId, activity: i32) {
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
    pub(super) fn fly_home(&mut self, session: SessionId, activity: i32, now: i64) -> Vec<Out> {
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

    pub(super) fn can_reconnect(&self, controller: SessionId, drone_session: SessionId) -> bool {
        let Some(controller_pilot) = self.pilots.get(&controller) else { return false };
        let owner = controller_pilot.ship.spec.character_id;
        let controller_ship_id = controller_pilot.ship.spec.ship_id;
        let Some(drone) = self.pilots.get(&drone_session) else { return false };
        let is_abandoned = drone.ship.npc.as_ref().and_then(|npc| npc.drone.as_ref()).is_some_and(|link| {
            link.controller == 0
                && link.owner_character_id == owner
                && link.launched_from_ship_id == controller_ship_id
        });
        is_abandoned
            && self.apart(controller, drone_session).is_some_and(|distance| distance <= DRONE_RECONNECT_RANGE)
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
                    if target_session == controller
                        || self.controller_of_drone(target_session) == Some(controller)
                    {
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
                    let Some(controller_ship) =
                        self.pilots.get(&controller).map(|pilot| pilot.ship.spec.ship_id)
                    else {
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
                        DroneCommand::Assist { .. } => DroneFollow::Assist { character_id, control_range },
                        DroneCommand::Guard { .. } => DroneFollow::Guard { character_id, control_range },
                        _ => unreachable!(),
                    };
                    if let Some(ai) = self.drones.get_mut(&session) {
                        ai.task = Task::Idle;
                        ai.follow = Some(follow);
                    }
                    out.extend(self.go_home(session, IDLE, now));
                }
                DroneCommand::MineRepeatedly { target } => {
                    if self.asteroids.get(&target).is_none_or(|asteroid| asteroid.units_remaining <= 0) {
                        results.push((drone_id, Some("NO_TARGET")));
                        continue;
                    }
                    if self
                        .distance_to_asteroid(controller, target, now)
                        .is_none_or(|apart| apart > control_range)
                    {
                        results.push((drone_id, Some("OUT_OF_RANGE")));
                        continue;
                    }
                    let cycle_ms = self.drones[&session].work.cycle_ms;
                    if self.drones[&session].work.mining_amount_m3 <= 0.0
                        || !cycle_ms.is_finite()
                        || cycle_ms <= 0.0
                    {
                        results.push((drone_id, Some("NOT_MINING_DRONE")));
                        continue;
                    }
                    let range = self.pilots[&session]
                        .ship
                        .npc
                        .as_ref()
                        .and_then(|npc| npc.drone.as_ref())
                        .map(|link| link.orbit_distance)
                        .unwrap_or(0.0);
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
                            if self.wreck(id).is_none_or(|wreck| wreck.group_id != crate::wreck::WRECK_GROUP)
                            {
                                results.push((drone_id, Some("NO_WRECK")));
                                continue;
                            }
                            if self
                                .distance_to_wreck(controller, id, now)
                                .is_none_or(|apart| apart > control_range)
                            {
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
                            let candidates: Vec<i64> = self
                                .wrecks
                                .values()
                                .filter(|wreck| {
                                    wreck.group_id == crate::wreck::WRECK_GROUP
                                        && (wreck.rights.character_id == character_id
                                            || (corporation_id > 0
                                                && wreck.rights.corporation_id == corporation_id))
                                })
                                .map(|wreck| wreck.id)
                                .collect();
                            let mut reachable: Vec<(f64, i64)> = candidates
                                .into_iter()
                                .filter_map(|id| {
                                    self.distance_to_wreck(controller, id, now)
                                        .filter(|apart| *apart <= control_range)
                                        .map(|apart| (apart, id))
                                })
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
                    if self.drones[&session].work.salvage_access_bonus <= 0.0
                        || !cycle_ms.is_finite()
                        || cycle_ms <= 0.0
                    {
                        results.push((drone_id, Some("NOT_SALVAGE_DRONE")));
                        continue;
                    }
                    let range = self.pilots[&session]
                        .ship
                        .npc
                        .as_ref()
                        .and_then(|npc| npc.drone.as_ref())
                        .map(|link| link.orbit_distance)
                        .unwrap_or(0.0);
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
}
