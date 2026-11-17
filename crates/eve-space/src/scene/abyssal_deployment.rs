use super::*;
use crate::link::{AbyssalDeploymentObject, AbyssalDeploymentSnapshot, DeploymentBall};
use crate::modules::WARP_SCRAMBLE_STATUS;

impl Scene {
    /// The live balls the client's `GetBallsAndItems` can include in structure placement, plus the warp-in points it derives from celestials.
    pub(crate) fn deployment_balls(&mut self, now: i64) -> Vec<DeploymentBall> {
        for resident in self.pilots.keys().copied().collect::<Vec<_>>() {
            self.settle_pilot(resident, now);
        }
        let mut balls = BTreeMap::new();
        let mut add = |id: i64, type_id: i32, position: Vec3, radius: f64, warp_in_point: bool| {
            if id > 0
                && type_id > 0
                && position.x.is_finite()
                && position.y.is_finite()
                && position.z.is_finite()
                && radius.is_finite()
                && radius >= 0.0
                && (!warp_in_point || radius == 0.0)
            {
                balls.insert(
                    (id, warp_in_point),
                    DeploymentBall {
                        item_id: id,
                        type_id,
                        position: [position.x, position.y, position.z],
                        radius,
                        warp_in_point,
                    },
                );
            }
        };

        for body in &self.system.bodies {
            add(body.id, body.type_id, body.position, body.radius, false);
            if let Some(point) = crate::warp::deployment_warp_in_point(body) {
                add(body.id, body.type_id, point, 0.0, true);
            }
        }
        for pilot in self.pilots.values() {
            add(
                pilot.ship.spec.ship_id,
                pilot.ship.spec.type_id,
                pilot.ship.position,
                pilot.ship.spec.radius,
                false,
            );
        }
        for wreck in self.wrecks.values() {
            if wreck.expires_at > now {
                add(wreck.id, wreck.kind.type_id, wreck.position, wreck.radius, false);
            }
        }
        for asteroid in self.asteroids.values() {
            if asteroid.units_remaining > 0 {
                add(asteroid.id, asteroid.type_id, asteroid.position, asteroid.radius, false);
            }
        }
        let now_unix_ms = now / (TICKS_PER_SECOND / 1_000) - UNIX_EPOCH_SECONDS * 1_000;
        for trace in self.abyssal_traces.values() {
            if trace.expires_at_ms > now_unix_ms {
                add(
                    trace.item_id,
                    trace.type_id,
                    Vec3::new(trace.position[0], trace.position[1], trace.position[2]),
                    trace.radius,
                    false,
                );
            }
        }
        for deployable in self.deployables.values().filter(|item| item.ends_at > now) {
            add(
                deployable.id,
                deployable.spec.charge_type_id,
                deployable.position,
                deployable.spec.radius,
                false,
            );
        }
        for missile in self.missiles.values().filter(|item| item.expires_at > now) {
            add(missile.id, missile.spec.charge_type_id, missile.position, missile.spec.radius, false);
        }

        balls.into_values().collect()
    }

    /// One simulation turn supplies the pose, ship state and candidate positions. Host applies official SDE typeList 50.
    pub(crate) fn abyssal_deployment_snapshot(
        &mut self,
        session: SessionId,
        now: i64,
    ) -> Option<AbyssalDeploymentSnapshot> {
        for resident in self.pilots.keys().copied().collect::<Vec<_>>() {
            self.settle_pilot(resident, now);
        }
        let own = self.pilots.get(&session)?;
        let origin = own.ship.position;
        let ship_id = own.ship.spec.ship_id;
        let mut candidates = BTreeMap::new();
        let mut add = |id: i64, type_id: i32, position: Vec3| {
            if id > 0 && id != ship_id && type_id > 0 {
                candidates.insert(id, (type_id, position));
            }
        };
        for body in &self.system.bodies {
            add(body.id, body.type_id, body.position);
        }
        for item in self.wrecks.values() {
            add(item.id, item.kind.type_id, item.position);
        }
        for item in self.asteroids.values() {
            add(item.id, item.type_id, item.position);
        }
        for item in self.abyssal_traces.values() {
            add(item.item_id, item.type_id, Vec3::new(item.position[0], item.position[1], item.position[2]));
        }
        for item in self.deployables.values().filter(|item| item.ends_at > now) {
            add(item.id, item.spec.charge_type_id, item.position);
        }
        for item in self.missiles.values().filter(|item| item.expires_at > now) {
            add(item.id, item.spec.charge_type_id, item.position);
        }
        // Live entities override a static entry with the same identity, including cloaked entities.
        for pilot in self.pilots.values() {
            add(pilot.ship.spec.ship_id, pilot.ship.spec.type_id, pilot.ship.position);
        }
        let nearby_objects = candidates
            .into_iter()
            .filter_map(|(item_id, (type_id, position))| {
                let distance = ((position.x - origin.x).powi(2)
                    + (position.y - origin.y).powi(2)
                    + (position.z - origin.z).powi(2))
                .sqrt();
                (distance.is_finite() && distance <= 1_000_000.0).then_some(AbyssalDeploymentObject {
                    item_id,
                    type_id,
                    center_distance_meters: distance,
                })
            })
            .collect();
        Some(AbyssalDeploymentSnapshot {
            warping: own.flight.as_ref().is_some_and(|flight| flight.is_in_warp_mode()),
            cloaked: own.cloak != crate::visibility::CloakState::Uncloaked,
            cyno_active: self.deployables.values().any(|item| {
                item.ship_id == ship_id
                    && item.ends_at > now
                    && matches!(item.role, crate::deployable::Role::CynoField)
            }),
            undock_invulnerable: own.protected_until > now,
            warp_scrambled: own.incoming.scrambled_with(self.scramble_status(session))
                || self.overlay(session, ship_id, WARP_SCRAMBLE_STATUS) > 0.0,
            nearby_objects,
        })
    }
}
