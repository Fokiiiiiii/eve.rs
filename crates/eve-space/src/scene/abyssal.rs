use super::*;

use crate::link::{AbyssalBoundaryDamagePoint, AbyssalSceneObject};
use crate::world::Body;

impl Scene {
    pub(crate) fn abyssal_scene_is_clear(&self) -> bool {
        self.abyssal_scene_is_clear_except_missiles() && self.missiles.is_empty()
    }

    /// Whether only in-flight missiles keep an otherwise-settled Abyssal scene alive.
    pub(crate) fn abyssal_scene_is_clear_except_missiles(&self) -> bool {
        self.abyssal.is_some()
            && self
                .pilots
                .values()
                .all(|pilot| self.abyssal_reward_caches.contains_key(&pilot.ship.spec.ship_id))
            && self.widowed.is_empty()
            && self.abyssal_participant_deaths.is_empty()
            && self.unpiloted.keys().all(|id| self.abyssal_reward_caches.contains_key(id))
            && self.controlled_structures.is_empty()
            && self.structure_health.is_empty()
            && self.structure_modules.is_empty()
            && self.wrecks.is_empty()
            && self.abyssal_traces.is_empty()
            && self.asteroids.is_empty()
            && self.deployables.is_empty()
            && self.drones.is_empty()
            && self.dots.is_empty()
    }

    pub(crate) fn set_abyssal_spec(&mut self, spec: AbyssalSceneSpec, now: i64) {
        let mechanics_changed = self.abyssal.as_ref().is_none_or(|current| {
            current.environment != spec.environment || current.boundary != spec.boundary
        });
        let interval = spec.boundary.tick_interval_ms.saturating_mul(TICKS_PER_SECOND / 1_000);
        self.abyssal = Some(spec);
        if mechanics_changed {
            self.abyssal_boundary_at = now.checked_add(interval);
            self.abyssal_boundary_exposure_ms.clear();
        }
    }

    /// Read-only identity summary for the two exact-scene cleanup refusal branches.
    pub(crate) fn abyssal_cleanup_diagnostic(&self) -> String {
        let pilots: Vec<_> = self
            .pilots
            .iter()
            .map(|(session, pilot)| {
                (
                    *session,
                    pilot.ship.spec.ship_id,
                    pilot.ship.spec.type_id,
                    crate::npc::is_npc_session(*session),
                    self.abyssal_reward_caches.contains_key(&pilot.ship.spec.ship_id),
                )
            })
            .collect();
        let deaths: Vec<_> =
            self.abyssal_participant_deaths.iter().map(|(ship, session)| (*ship, *session)).collect();
        let unpiloted: Vec<_> = self.unpiloted.iter().map(|(ship, session)| (*ship, *session)).collect();
        let wrecks: Vec<_> = self
            .wrecks
            .values()
            .map(|wreck| (wreck.id, wreck.kind.type_id, wreck.group_id, wreck.launcher_id))
            .collect();
        let asteroids: Vec<_> = self
            .asteroids
            .values()
            .map(|asteroid| (asteroid.id, asteroid.type_id, asteroid.group_id))
            .collect();
        let missiles: Vec<_> = self
            .missiles
            .values()
            .map(|missile| (missile.id, missile.shooter.ship_id, missile.target))
            .collect();
        let dots: Vec<_> = self.dots.iter().map(|(target, dot)| (*target, dot.applications.len())).collect();
        let mut parts = Vec::new();
        parts.push(format!("abyssal={}", self.abyssal.is_some()));
        parts.push(format!("pilots[{}]={pilots:?}", pilots.len()));
        parts.push(format!("widowed[{}]={:?}", self.widowed.len(), self.widowed.keys().collect::<Vec<_>>()));
        parts.push(format!("deaths[{}]={deaths:?}", deaths.len()));
        parts.push(format!("unpiloted[{}]={unpiloted:?}", unpiloted.len()));
        parts.push(format!(
            "controlled_structures[{}]={:?}",
            self.controlled_structures.len(),
            self.controlled_structures.keys().collect::<Vec<_>>()
        ));
        parts.push(format!(
            "structure_health[{}]={:?}",
            self.structure_health.len(),
            self.structure_health.keys().collect::<Vec<_>>()
        ));
        parts.push(format!(
            "structure_modules[{}]={:?}",
            self.structure_modules.len(),
            self.structure_modules.keys().collect::<Vec<_>>()
        ));
        parts.push(format!("wrecks[{}]={wrecks:?}", wrecks.len()));
        parts.push(format!(
            "traces[{}]={:?}",
            self.abyssal_traces.len(),
            self.abyssal_traces.keys().collect::<Vec<_>>()
        ));
        parts.push(format!("asteroids[{}]={asteroids:?}", asteroids.len()));
        parts.push(format!(
            "deployables[{}]={:?}",
            self.deployables.len(),
            self.deployables.keys().collect::<Vec<_>>()
        ));
        parts.push(format!("drones[{}]={:?}", self.drones.len(), self.drones.keys().collect::<Vec<_>>()));
        parts.push(format!("missiles[{}]={missiles:?}", missiles.len()));
        parts.push(format!("dots[{}]={dots:?}", dots.len()));
        parts.push(format!(
            "receipts[npc={:?},cache={:?},loot={:?},environment={:?}]",
            self.abyssal_npc_spawn_receipts.keys().collect::<Vec<_>>(),
            self.abyssal_reward_caches.keys().collect::<Vec<_>>(),
            self.abyssal_loot_receipts.keys().collect::<Vec<_>>(),
            self.abyssal_environment_objects.keys().collect::<Vec<_>>()
        ));
        parts.push(format!(
            "sim_damageables[{}]={:?}",
            self.sim_damageable_entities.len(),
            self.sim_damageable_entities.iter().collect::<Vec<_>>()
        ));
        parts.join("; ")
    }

    pub(crate) fn ensure_abyssal_scene_object(
        &mut self,
        object: AbyssalSceneObject,
        now: i64,
    ) -> Result<Vec<Out>, String> {
        if !crate::link::valid_abyssal_scene_object(&object) {
            return Err("BAD_ABYSSAL_SCENE_OBJECT".to_string());
        }
        if self.abyssal.as_ref().is_none_or(|spec| spec.scene != object.scene)
            || self.system.id != object.scene.location_id
        {
            return Err("ABYSSAL_SCENE_OBJECT_SCENE_MISMATCH".to_string());
        }

        let position = Vec3::new(object.position[0], object.position[1], object.position[2]);
        let direction = Vec3::new(object.direction[0], object.direction[1], object.direction[2]);
        if let Some(index) = self.system.bodies.iter().position(|body| body.id == object.item_id) {
            let body = &self.system.bodies[index];
            let Detail::AbyssalConduit { direction: current_direction, is_open: current_open } = &body.detail
            else {
                return Err("ABYSSAL_SCENE_OBJECT_ID_CONFLICT".to_string());
            };
            if body.kind != BodyKind::AbyssalConduit
                || body.type_id != object.type_id
                || body.group_id != object.group_id
                || body.category_id != object.category_id
                || body.owner_id != object.owner_id
                || body.radius != object.radius
                || body.graphic_id != Some(i64::from(object.graphic_id))
                || body.name != object.name
                || body.position != position
                || *current_direction != direction
            {
                return Err("ABYSSAL_SCENE_OBJECT_ID_CONFLICT".to_string());
            }
            if *current_open == object.is_open {
                return Ok(Vec::new());
            }

            let stamp = self.next_stamp(now);
            let system = Arc::make_mut(&mut self.system);
            let Detail::AbyssalConduit { is_open, .. } = &mut system.bodies[index].detail else {
                return Err("ABYSSAL_SCENE_OBJECT_ID_CONFLICT".to_string());
            };
            *is_open = object.is_open;
            let change = payload::abyssal_scene_object_slim_change(stamp, system, index);
            return Ok(self
                .view_sessions()
                .into_iter()
                .map(|session| Self::update(session, vec![change.clone()]))
                .collect());
        }

        let body = Body {
            id: object.item_id,
            type_id: object.type_id,
            group_id: object.group_id,
            category_id: object.category_id,
            owner_id: object.owner_id,
            name: object.name,
            kind: BodyKind::AbyssalConduit,
            position,
            radius: object.radius,
            graphic_id: Some(i64::from(object.graphic_id)),
            detail: Detail::AbyssalConduit { direction, is_open: object.is_open },
        };
        let stamp = self.next_stamp(now);
        let system = Arc::make_mut(&mut self.system);
        system.bodies.push(body);
        let index = system.bodies.len() - 1;
        let added = match payload::add_abyssal_scene_object(stamp, system, index) {
            Ok(added) => added,
            Err(_) => {
                system.bodies.pop();
                return Err("ABYSSAL_SCENE_OBJECT_SERIALIZATION_FAILED".to_string());
            }
        };
        Ok(self
            .view_sessions()
            .into_iter()
            .map(|session| Self::update(session, vec![added.clone()]))
            .collect())
    }

    pub(crate) fn ensure_abyssal_reward_cache(
        &mut self,
        cache: crate::link::AbyssalRewardCachePlacement,
        now: i64,
    ) -> (Vec<Out>, Option<String>) {
        let failed = |reason: &str| (Vec::new(), Some(reason.to_string()));
        if !crate::link::valid_abyssal_reward_cache(&cache) {
            return failed("BAD_ABYSSAL_REWARD_CACHE");
        }
        if self.abyssal.as_ref().is_none_or(|spec| spec.scene != cache.scene) {
            return failed("ABYSSAL_CACHE_SCENE_MISMATCH");
        }
        let id = cache.ship.ship_id;
        if let Some(receipt) = self.abyssal_reward_caches.get(&id) {
            if receipt.placement != cache {
                return failed("ABYSSAL_CACHE_IDENTITY_CONFLICT");
            }
            if let Some(destruction) = &receipt.destruction {
                return (vec![destruction.clone()], Some("ABYSSAL_CACHE_ALREADY_DESTROYED".to_string()));
            }
            return if self.session_of(id).is_some() {
                (Vec::new(), None)
            } else {
                failed("ABYSSAL_CACHE_RESIDENT_MISSING")
            };
        }
        if self.session_of(id).is_some()
            || self.system.body(id).is_some()
            || self.wrecks.contains_key(&id)
            || self.abyssal_traces.contains_key(&id)
        {
            return failed("ABYSSAL_CACHE_IDENTITY_CONFLICT");
        }
        // Reuse non-NPC unpiloted entity health, targeting, visibility and weapons, preserving its real SDE identity.
        let session = (1_u64 << 61).saturating_add(self.next_entry());
        self.unpiloted.insert(id, session);
        match self.enter_in_space(session, cache.ship.clone(), cache.position, cache.direction, now) {
            Ok(out) => {
                self.abyssal_reward_caches
                    .insert(id, AbyssalRewardCacheReceipt { placement: cache, destruction: None });
                (out, None)
            }
            Err(_) => {
                self.unpiloted.remove(&id);
                failed("ABYSSAL_CACHE_ENTER_FAILED")
            }
        }
    }

    pub(crate) fn move_abyssal_participant_to(
        &mut self,
        destination: &mut Scene,
        session: SessionId,
        ship_id: i64,
        position: [f64; 3],
        direction: [f64; 3],
        now: i64,
    ) -> Result<Vec<Out>, String> {
        if (self.abyssal.is_none() && destination.abyssal.is_none())
            || self.pilots.get(&session).is_none_or(|pilot| pilot.ship.spec.ship_id != ship_id)
        {
            return Err("ABYSSAL_PARTICIPANT_NOT_IN_SOURCE".to_string());
        }
        if destination.pilots.contains_key(&session) {
            return Err("ABYSSAL_PARTICIPANT_ALREADY_IN_DESTINATION".to_string());
        }
        if self
            .pilots
            .get(&session)
            .is_some_and(|pilot| pilot.docking.is_some() || pilot.flight.is_some() || pilot.jump.is_some())
        {
            return Err("ABYSSAL_PARTICIPANT_IN_TRANSITION".to_string());
        }

        self.abyssal_boundary_exposure_ms.remove(&ship_id);
        destination.abyssal_boundary_exposure_ms.remove(&ship_id);
        self.settle_pilot(session, now);
        let mut told = self.abyssal_environment_fx(session, None, now, false);
        let mut pilot = self.pilots.remove(&session).expect("participant checked above");
        let ship_id = pilot.ship.spec.ship_id;
        told.extend(self.clear_tether_policy(session, now));
        told.extend(self.drop_targeting(ship_id, &pilot.targeting, now));
        if let Some(brains) = self.brains.as_mut() {
            brains.forget(session);
        }
        told.extend(self.announce_departure(session, ship_id, now));

        pilot.ship.position = Vec3::new(position[0], position[1], position[2]);
        pilot.ship.direction = Vec3::new(direction[0], direction[1], direction[2]);
        pilot.ship.velocity = Vec3::ZERO;
        pilot.ship.speed_fraction = 0.0;
        pilot.ship.course = Course::Stop;
        pilot.settled_at = now;
        pilot.docking = None;
        pilot.flight = None;
        pilot.jump = None;
        pilot.tether = Default::default();
        pilot.jumped_in = true;
        pilot.sees.clear();
        pilot.sees_wrecks.clear();
        pilot.sees_abyssal_traces.clear();
        pilot.sees_asteroids.clear();
        pilot.login_visuals_sent = false;
        pilot.entered = destination.next_entry();
        destination.pilots.insert(session, pilot);
        destination.boxes.clear();
        destination.take_in_view(session);
        told.extend(destination.announce_arrival(session, now));
        told.extend(destination.abyssal_environment_fx(session, None, now, true));
        Ok(told)
    }

    pub(crate) fn rebind_abyssal_session(
        &mut self,
        old_session: SessionId,
        new_session: SessionId,
        ship_id: i64,
        now: i64,
    ) -> Result<(), String> {
        if new_session == 0 || old_session == new_session || self.pilots.contains_key(&new_session) {
            return Err("ABYSSAL_SESSION_ALREADY_BOUND".to_string());
        }
        if self.pilots.get(&old_session).is_none_or(|pilot| pilot.ship.spec.ship_id != ship_id) {
            return Err("ABYSSAL_RESIDENT_SHIP_NOT_FOUND".to_string());
        }

        self.settle_pilot(old_session, now);
        let mut pilot = self.pilots.remove(&old_session).expect("resident pilot checked above");
        pilot.bound = false;
        pilot.login_visuals_sent = false;
        pilot.sees.clear();
        pilot.sees_wrecks.clear();
        pilot.sees_abyssal_traces.clear();
        pilot.sees_asteroids.clear();
        self.pilots.insert(new_session, pilot);

        for sessions in self.boxes.values_mut() {
            for session in sessions.iter_mut() {
                if *session == old_session {
                    *session = new_session;
                }
            }
        }
        for resident in self.pilots.values_mut() {
            if let Some(link) = resident.ship.npc.as_mut().and_then(|npc| npc.drone.as_mut())
                && link.controller == old_session
            {
                link.controller = new_session;
            }
        }
        self.boxes.clear();
        if let Some(threats) = self.drone_threats.remove(&old_session) {
            self.drone_threats.insert(new_session, threats);
        }
        if let Some(observer) = self.controlled_structures.remove(&old_session) {
            self.controlled_structures.insert(new_session, observer);
        }
        Ok(())
    }

    pub(crate) fn advance_abyssal_boundary(&mut self, now: i64) -> Vec<Out> {
        let Some(spec) = self.abyssal.clone() else { return Vec::new() };
        let interval = spec.boundary.tick_interval_ms.saturating_mul(TICKS_PER_SECOND / 1_000);
        if interval <= 0 {
            return Vec::new();
        }
        let mut tick = self.abyssal_boundary_at.unwrap_or(now);
        if tick > now {
            self.abyssal_boundary_at = Some(tick);
            return Vec::new();
        }
        let mut told = Vec::new();
        while tick <= now {
            let sessions: Vec<SessionId> = self.pilots.keys().copied().collect();
            let positions: Vec<(SessionId, i64, Vec3)> = sessions
                .into_iter()
                .filter_map(|session| {
                    self.pilots
                        .get(&session)
                        .map(|pilot| (session, pilot.ship.spec.ship_id, pilot.ship.position))
                })
                .collect();
            for (session, ship_id, position) in positions {
                if self.abyssal_reward_caches.contains_key(&ship_id) {
                    continue;
                }
                let dx = position.x - spec.boundary.center[0];
                let dy = position.y - spec.boundary.center[1];
                let dz = position.z - spec.boundary.center[2];
                let outside = (dx * dx + dy * dy + dz * dz).sqrt() - spec.boundary.radius_meters;
                if outside <= 0.0 {
                    self.abyssal_boundary_exposure_ms.remove(&ship_id);
                    continue;
                }
                let exposed_ms = {
                    let elapsed = self.abyssal_boundary_exposure_ms.entry(ship_id).or_default();
                    *elapsed = elapsed.saturating_add(spec.boundary.tick_interval_ms);
                    *elapsed
                };
                if let Some(dps) = boundary_damage_at(&spec.boundary.damage_curve, outside) {
                    let seconds = spec.boundary.tick_interval_ms as f64 / 1_000.0;
                    let applied_amount = dps * seconds;
                    let (damage_out, applied) = self.damage_resolved(ship_id, applied_amount, tick);
                    told.extend(damage_out);
                    if applied.is_some_and(|hit| hit.destroyed) {
                        self.abyssal_boundary_exposure_ms.remove(&ship_id);
                        told.extend(self.destroy(session, None, tick));
                        continue;
                    }
                }
                if exposed_ms >= spec.boundary.terminal_exposure_ms {
                    self.abyssal_boundary_exposure_ms.remove(&ship_id);
                    told.extend(self.destroy(session, None, tick));
                }
            }
            let Some(next) = tick.checked_add(interval) else { break };
            tick = next;
        }
        self.abyssal_boundary_at = Some(tick);
        told
    }
}

fn boundary_damage_at(curve: &[AbyssalBoundaryDamagePoint], distance: f64) -> Option<f64> {
    let first = curve.first()?;
    if distance <= first.distance_outside_meters {
        return Some(first.applied_damage_per_second);
    }
    for pair in curve.windows(2) {
        let left = &pair[0];
        let right = &pair[1];
        if distance <= right.distance_outside_meters {
            let fraction = (distance - left.distance_outside_meters)
                / (right.distance_outside_meters - left.distance_outside_meters);
            return Some(
                left.applied_damage_per_second
                    + (right.applied_damage_per_second - left.applied_damage_per_second) * fraction,
            );
        }
    }
    curve.last().map(|point| point.applied_damage_per_second)
}
