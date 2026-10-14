use super::*;

impl Sim {
    pub(super) fn enter(&mut self, enter: Enter, now: i64) -> Vec<Message> {
        let Enter { session_id, solar_system_id, how, station_id, ship, position, direction } = enter;
        let reply = |outcome| vec![Message::EnterReply { session_id, outcome }];
        if self.sessions.contains_key(&session_id) {
            return reply(Err(format!("ALREADY_IN_SPACE {session_id}")));
        }
        let system = match self.universe.system(solar_system_id) {
            Ok(system) => system,
            Err(reason) => return reply(Err(reason)),
        };
        let scene = self.scenes.entry(solar_system_id).or_insert_with(|| self.universe.scene_of(system));
        let entered = match how {
            How::Undock => scene.undock(session_id, ship, station_id, now),
            How::InSpace => {
                let (Some(position), Some(direction)) = (position, direction) else {
                    return reply(Err("SPACE_STATE_REQUIRED".to_string()));
                };
                if position.iter().chain(direction.iter()).any(|value| !value.is_finite())
                    || direction.iter().all(|value| *value == 0.0)
                {
                    return reply(Err("BAD_SPACE_STATE".to_string()));
                }
                scene.enter_in_space(session_id, ship, position, direction, now)
            }
        };
        match entered {
            Ok(told) => {
                self.sessions.insert(session_id, solar_system_id);
                let mut messages = reply(Ok(()));
                messages.extend(Self::messages(told));
                messages
            }
            Err(SceneError::UnknownStation(station)) => {
                let mut messages = reply(Err(format!("UNKNOWN_STATION {station}")));
                if scene.is_empty()
                    && let Some(mut scene) = self.scenes.remove(&solar_system_id)
                {
                    messages.extend(Self::messages(scene.take_sim_damageable_entity_removals()));
                }
                messages
            }
            Err(SceneError::AlreadyInSpace(_)) => reply(Err(format!("ALREADY_IN_SPACE {session_id}"))),
            Err(SceneError::NotDestroyed(_)) => reply(Err(format!("NOT_DESTROYED {session_id}"))),
        }
    }

    pub(super) fn forget(&mut self, session: SessionId) -> Vec<Out> {
        let system = self.sessions.remove(&session).or_else(|| {
            let system = self.widowed.remove(&session)?;
            if let Some(scene) = self.scenes.get_mut(&system) {
                scene.forget_widow(session);
            }
            Some(system)
        });
        if let Some(system) = system
            && self.scenes.get(&system).is_some_and(Scene::is_empty)
            && let Some(mut scene) = self.scenes.remove(&system)
        {
            return scene.take_sim_damageable_entity_removals();
        }
        Vec::new()
    }

    /// A player whose ship was destroyed is not in space any more; its scene keeps its ballpark until the host gives it a capsule (`Respawn`) or it leaves.
    pub(super) fn forget_the_destroyed(&mut self, outs: &[Out]) {
        for out in outs {
            if let Out::Destroyed { session, .. } = out
                && !is_npc_session(*session)
                && let Some(system) = self.sessions.remove(session)
            {
                self.widowed.insert(*session, system);
            }
        }
    }

    /// The host asks for an NPC: it comes into the scene of the system if there is one (a system has a scene while somebody is in it).
    pub(super) fn spawn(&mut self, spawn: Spawn, now: i64) -> Vec<Message> {
        let Spawn {
            request_id,
            solar_system_id,
            ship,
            anchor_ship_id,
            position,
            velocity,
            direction,
            behavior,
            identity,
        } = spawn;
        let reply = |outcome| Message::SpawnReply { request_id, outcome };
        let Some(scene) = self.scenes.get_mut(&solar_system_id) else {
            return vec![reply(Err(format!("NO_SCENE {solar_system_id}")))];
        };
        let mut position = Vec3::new(position[0], position[1], position[2]);
        if anchor_ship_id != 0 {
            let Some(anchor) = scene
                .session_of(anchor_ship_id)
                .and_then(|session| scene.ship(session, now))
                .map(|anchor| anchor.position)
            else {
                return vec![reply(Err(format!("NO_SHIP {anchor_ship_id}")))];
            };
            position = Vec3::new(anchor.x + position.x, anchor.y + position.y, anchor.z + position.z);
        }
        let place = NpcPlace {
            position,
            velocity: Vec3::new(velocity[0], velocity[1], velocity[2]),
            direction: Vec3::new(direction[0], direction[1], direction[2]),
            speed_fraction: 0.0,
            course: Course::Stop,
        };
        let spawned = if behavior.is_empty() {
            Ok(scene.spawn_npc(ship, identity, place, now))
        } else {
            scene.spawn_npc_with_behavior(ship, identity, place, &behavior, now)
        };
        match spawned {
            Ok((session, told)) => {
                let mut messages = vec![reply(Ok(session))];
                messages.extend(Self::messages(told));
                messages
            }
            Err(error) => vec![reply(Err(format!("{error:?}")))],
        }
    }

    /// A drone comes into the scene of the pilot's ship: the answer carries the session the drone is known by.
    pub(super) fn launch_drone(&mut self, launch: LaunchDrone, now: i64) -> Vec<Message> {
        let LaunchDrone {
            request_id,
            session_id,
            ship,
            weapon,
            graphic_id,
            orbit_distance,
            attack_range,
            cruise_fraction,
            mining_amount_m3,
            work_cycle_ms,
            mining_waste_multiplier,
            mining_waste_probability,
            salvage_access_bonus,
            effect,
        } = launch;
        let reply = |outcome| Message::SpawnReply { request_id, outcome };
        let Some(scene) = self.scene_of(session_id) else {
            return vec![reply(Err(format!("NO_SHIP {session_id}")))];
        };
        match scene.launch_drone(
            session_id,
            ship,
            DroneFlight {
                weapon,
                graphic_id,
                orbit_distance,
                attack_range,
                cruise_fraction,
                work: crate::drones::DroneWorkProfile {
                    mining_amount_m3,
                    cycle_ms: work_cycle_ms,
                    waste_multiplier: mining_waste_multiplier,
                    waste_probability: mining_waste_probability,
                    salvage_access_bonus,
                },
                effect,
            },
            now,
        ) {
            Ok((session, told)) => {
                let mut messages = Self::messages(told);
                messages.push(reply(Ok(session)));
                messages
            }
            Err(reason) => vec![reply(Err(reason.to_string()))],
        }
    }

    pub fn poll(&mut self, now: i64) -> Vec<Message> {
        let mut outs: Vec<Out> = self.scenes.values_mut().flat_map(|scene| scene.poll(now)).collect();
        let mut teardown = Vec::new();
        for out in &outs {
            if let Out::Docked { session, .. } = out {
                teardown.extend(self.forget(*session));
            }
        }
        self.forget_the_destroyed(&outs);
        outs.extend(teardown);
        let mut messages: Vec<Message> = Self::messages(outs);
        let mut travellers: Vec<Traveller> = Vec::new();
        for scene in self.scenes.values_mut() {
            let (gone, told) = scene.due_jumps(now);
            travellers.extend(gone);
            messages.extend(Self::messages(told));
        }
        for traveller in travellers {
            messages.extend(self.arrive(traveller, now));
        }
        let empty_systems: Vec<i32> = self
            .scenes
            .iter()
            .filter_map(|(system_id, scene)| scene.is_empty().then_some(*system_id))
            .collect();
        for system_id in empty_systems {
            if let Some(mut scene) = self.scenes.remove(&system_id) {
                messages.extend(Self::messages(scene.take_sim_damageable_entity_removals()));
            }
        }
        messages
    }

    pub(super) fn arrive(&mut self, traveller: Traveller, now: i64) -> Vec<Message> {
        let (session, from_system, from_gate, to_system, to_gate) = (
            traveller.session,
            traveller.from_system,
            traveller.from_gate,
            traveller.to_system,
            traveller.to_gate,
        );
        let failed = |sim: &mut Self, traveller: Traveller| {
            let mut messages = vec![Message::Refused {
                session_id: session,
                command: 9,
                reason: "DESTINATION_UNAVAILABLE".to_string(),
            }];
            if let Some(scene) = sim.scenes.get_mut(&traveller.from_system) {
                messages.extend(Self::messages(scene.restore(traveller, now)));
            }
            messages
        };
        let Ok(system) = self.universe.system(to_system) else { return failed(self, traveller) };
        let scene = self.scenes.entry(to_system).or_insert_with(|| self.universe.scene_of(system));
        match scene.arrive(traveller, now) {
            Ok(told) => {
                self.sessions.insert(session, to_system);
                let mut messages =
                    vec![Message::Jumped { session_id: session, from_system, from_gate, to_system, to_gate }];
                messages.extend(Self::messages(told));
                messages
            }
            Err(traveller) => {
                let should_remove = scene.is_empty();
                let mut removals = if should_remove {
                    self.scenes
                        .remove(&to_system)
                        .map(|mut scene| Self::messages(scene.take_sim_damageable_entity_removals()))
                        .unwrap_or_default()
                } else {
                    Vec::new()
                };
                let mut messages = failed(self, *traveller);
                messages.append(&mut removals);
                messages
            }
        }
    }
}
