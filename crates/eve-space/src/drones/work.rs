use super::*;

/// The host's answer to one drone work cycle (`DroneWorkResult`): the cycle it answers and what the host granted.
pub struct DroneWorkAnswer {
    pub target_id: i64,
    pub cycle_id: u64,
    pub accepted: bool,
    pub units_remaining: i64,
    pub complete: bool,
}

impl Scene {
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
            let controller_lost =
                self.pilots
                    .get(&session)
                    .and_then(|pilot| pilot.ship.npc.as_ref())
                    .and_then(|npc| npc.drone.as_ref())
                    .is_some_and(|link| {
                        link.controller != 0
                            && self.pilots.get(&link.controller).is_none_or(|controller| {
                                controller.ship.spec.ship_id != link.controller_ship_id
                            })
                    });
            if controller_lost {
                out.extend(self.abandon(session, now));
                continue;
            }
            let Some(task) = self.drones.get(&session).map(|ai| ai.task) else { continue };
            match task {
                Task::Idle | Task::Home => {}
                Task::MineRepeatedly { target } => {
                    out.extend(self.advance_drone_work(session, target, 7, now))
                }
                Task::Salvage { target: Some(target) } => {
                    out.extend(self.advance_drone_work(session, target, 8, now))
                }
                Task::Salvage { target: None } => {}
                Task::Bay => out.extend(self.scoop_if_near(session, now)),
                Task::Engage { target } => out.extend(self.fight(session, target, now)),
            }
        }
        out
    }

    pub(super) fn advance_drone_work(
        &mut self,
        session: SessionId,
        target: i64,
        command: u8,
        now: i64,
    ) -> Vec<Out> {
        let Some(ai) = self.drones.get(&session) else { return Vec::new() };
        if ai.awaiting_work {
            return Vec::new();
        }
        let Some(link) = self
            .pilots
            .get(&session)
            .and_then(|pilot| pilot.ship.npc.as_ref())
            .and_then(|npc| npc.drone.as_ref())
        else {
            return Vec::new();
        };
        let (controller, orbit_distance) = (link.controller, link.orbit_distance);
        let (phase, range, cycle_ms, control_range) = {
            let ai = &self.drones[&session];
            (
                ai.work_phase,
                orbit_distance.max(0.0),
                ai.work.cycle_ms.ceil().max(1.0).min(i64::MAX as f64) as i64,
                ai.work_control_range,
            )
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
        answer: DroneWorkAnswer,
        now: i64,
    ) -> Vec<Out> {
        let DroneWorkAnswer { target_id, cycle_id, accepted, units_remaining, complete } = answer;
        let Some(session) = self
            .session_of(drone_id)
            .filter(|session| self.controller_of_drone(*session) == Some(controller))
        else {
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
            if self.asteroids.get(&target_id).is_none_or(|asteroid| asteroid.units_remaining <= 0) {
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
                let candidates: Vec<i64> = self
                    .wrecks
                    .values()
                    .filter(|wreck| {
                        wreck.group_id == crate::wreck::WRECK_GROUP
                            && (wreck.rights.character_id == character_id
                                || (corporation_id > 0 && wreck.rights.corporation_id == corporation_id))
                    })
                    .map(|wreck| wreck.id)
                    .collect();
                let mut reachable: Vec<(f64, i64)> = candidates
                    .into_iter()
                    .filter_map(|id| {
                        self.distance_to_wreck(controller, id, now)
                            .filter(|distance| *distance <= control_range)
                            .map(|distance| (distance, id))
                    })
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
            && self.wreck(target_id).is_none_or(|wreck| wreck.group_id != crate::wreck::WRECK_GROUP)
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
    pub(super) fn scoop_if_near(&mut self, session: SessionId, now: i64) -> Vec<Out> {
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
}
