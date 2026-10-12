use super::*;

impl Scene {
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
            let following: Vec<(SessionId, i64)> = self
                .drones
                .iter()
                .filter_map(|(drone, ai)| {
                    let follow = ai.follow?;
                    let followed_ship = self.ship_of_character(follow.character_id())?;
                    let target_ship = match follow {
                        DroneFollow::Assist { .. } if attacker == followed_ship => target,
                        DroneFollow::Guard { .. } if target == followed_ship => attacker,
                        _ => return None,
                    };
                    let controller = self.controller_of_drone(*drone)?;
                    let target_session = self.session_of(target_ship)?;
                    if target_session == controller
                        || self.controller_of_drone(target_session) == Some(controller)
                    {
                        return None;
                    }
                    self.apart(controller, target_session)
                        .filter(|apart| *apart <= follow.control_range())?;
                    Some((*drone, target_ship))
                })
                .collect();
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
    pub(super) fn threats_of(&self, controller: SessionId, now: i64) -> Vec<i64> {
        let mut recent: Vec<(i64, i64)> = self.drone_threats.get(&controller).cloned().unwrap_or_default();
        recent.retain(|(ship, at)| now - at <= THREAT_MEMORY && self.session_of(*ship).is_some());
        recent.sort_by_key(|(ship, at)| (std::cmp::Reverse(*at), *ship));
        recent.into_iter().map(|(ship, _)| ship).collect()
    }

    /// Whether a drone has a remote repair and no weapon: it is sent at friendly ships only.
    pub(super) fn only_repairs(&self, session: SessionId) -> bool {
        self.drones.get(&session).and_then(|ai| ai.remote.as_ref()).is_some_and(DroneEffect::repairs)
            && self
                .pilots
                .get(&session)
                .and_then(|pilot| pilot.ship.npc.as_ref())
                .is_some_and(|npc| npc.combat.weapons.is_empty())
    }

    /// Whether a ship is on the side of a pilot (the reference's `isFriendlyRepairTarget`: its character or owner, its corporation or its alliance).
    pub(super) fn is_friendly_to(&self, controller: SessionId, target: SessionId) -> bool {
        let (Some(controller), Some(target)) = (self.pilots.get(&controller), self.pilots.get(&target))
        else {
            return false;
        };
        let (c, t) = (&controller.ship.spec, &target.ship.spec);
        (c.character_id > 0 && (t.character_id == c.character_id || t.owner_id == c.character_id))
            || (c.owner_id > 0 && t.owner_id == c.owner_id)
            || (c.corporation_id > 0 && t.corporation_id == c.corporation_id)
            || (c.alliance_id > 0 && t.alliance_id == c.alliance_id)
    }

    /// The drone is sent at a target: it circles it at full speed and is told as pursuing, approaching or fighting by how far off it is.
    pub(super) fn engage(&mut self, session: SessionId, target: i64, now: i64) -> Vec<Out> {
        if self.drones.get(&session).is_some_and(|ai| !ai.can_combat) {
            return Vec::new();
        }
        let Some(target_session) = self.session_of(target) else {
            if !self.struck_structure(target) {
                return Vec::new();
            }
            return self.engage_structure(session, target, now);
        };
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
    /// A drone is set on a structure of the system: it fights it while the structure is in its engagement range, else it is approaching (see `fight_structure`).
    fn engage_structure(&mut self, session: SessionId, target: i64, now: i64) -> Vec<Out> {
        let (Some(attacker), Some(structure), Some(engagement)) = (
            self.kinematics_of(session),
            self.target_kinematics(target),
            self.drones.get(&session).map(|ai| ai.engagement_range),
        ) else {
            return Vec::new();
        };
        let apart = crate::weapons::surface_distance(&attacker, &structure);
        if let Some(ai) = self.drones.get_mut(&session) {
            ai.task = Task::Engage { target };
            ai.next_shot = now;
            ai.next_remote = now;
        }
        let activity = if apart > engagement + 1.0 { APPROACHING } else { COMBAT };
        self.set_activity(session, activity);
        self.told_state(session)
    }

    pub(crate) fn abandon(&mut self, session: SessionId, now: i64) -> Vec<Out> {
        let mut out = self.stop_remote(session, now);
        out.extend(self.let_go(session, now));
        out
    }

    pub(super) fn let_go(&mut self, session: SessionId, now: i64) -> Vec<Out> {
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
    pub(crate) fn release_drones(
        &mut self,
        controller: SessionId,
        logoff_at: Option<Vec3>,
        now: i64,
    ) -> Vec<Out> {
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
                    ((p.x - at.x).powi(2) + (p.y - at.y).powi(2) + (p.z - at.z).powi(2)).sqrt()
                        <= FIGHTER_RECOVERY_DISTANCE
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
}
