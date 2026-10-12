//! Wormhole ends in the scene. A wormhole end is a body of its system (`BodyKind::Wormhole`) that the host puts in space and takes out
//! (protocol 63). Its far end is in another system; a ship that comes out of this end is placed by the far end's position and radius.
//! The simulation decides no mass, lifetime or collapse: the host does that before it sends the jump (ADR 0012, rule 3).

use std::f64::consts::PI;
use std::sync::Arc;

use eve_destiny::Vec3;

use super::{Out, Scene, SessionId};
use crate::gate;
use crate::link::WormholePlacement;
use crate::payload;
use crate::warp::PythonRandom;
use crate::world::{Body, BodyKind, Detail};

/// The category of a wormhole's item (SDE group 988 `Wormhole` is in category 2, `Celestial`).
const WORMHOLE_CATEGORY: i32 = 2;

impl Scene {
    /// The index of a wormhole end of this system, if it is in space.
    pub(crate) fn wormhole_index(&self, wormhole_id: i64) -> Option<usize> {
        self.system.bodies.iter().position(|body| body.id == wormhole_id && body.kind == BodyKind::Wormhole)
    }

    /// Every bound pilot of the scene sees a wormhole end from anywhere in its system.
    fn wormhole_audience(&self) -> Vec<SessionId> {
        self.pilots.iter().filter(|(_, pilot)| pilot.bound).map(|(session, _)| *session).collect()
    }

    /// Puts a wormhole end in space where the host placed it. Given once; one already in space is left as it is.
    pub(crate) fn place_wormhole(&mut self, placement: WormholePlacement, now: i64) -> Vec<Out> {
        if self.wormhole_index(placement.wormhole_id).is_some() {
            return Vec::new();
        }
        let [x, y, z] = placement.position;
        let [far_x, far_y, far_z] = placement.destination_position;
        let position = clear_of_bodies(&self.system.bodies, Vec3::new(x, y, z), placement.radius);
        let body = Body {
            id: placement.wormhole_id,
            type_id: placement.type_id,
            group_id: placement.group_id,
            category_id: WORMHOLE_CATEGORY,
            owner_id: 0,
            name: placement.name,
            kind: BodyKind::Wormhole,
            position,
            radius: placement.radius,
            graphic_id: None,
            detail: Detail::Wormhole {
                destination_system: placement.destination_system_id,
                destination_end: placement.destination_end,
                destination_class: placement.destination_class,
                destination_position: Vec3::new(far_x, far_y, far_z),
                destination_radius: placement.destination_radius,
                max_jump_mass: placement.max_jump_mass,
            },
        };
        let stamp = self.next_stamp(now);
        let system = Arc::make_mut(&mut self.system);
        system.bodies.push(body);
        let index = system.bodies.len() - 1;
        match payload::add_wormhole(stamp, system, index, now) {
            Ok(added) => self
                .wormhole_audience()
                .into_iter()
                .map(|session| Self::update(session, vec![added.clone()]))
                .collect(),
            Err(_) => {
                system.bodies.pop();
                Vec::new()
            }
        }
    }

    /// The host takes a wormhole end out of space: its body goes and those who had it in view are told (`RemoveBalls`).
    pub(crate) fn remove_wormhole(&mut self, wormhole_id: i64, at: i64) -> Vec<Out> {
        let Some(index) = self.wormhole_index(wormhole_id) else { return Vec::new() };
        Arc::make_mut(&mut self.system).bodies.remove(index);
        let stamp = self.next_stamp(at);
        let removed = payload::remove_ships(stamp, [wormhole_id]);
        self.wormhole_audience()
            .into_iter()
            .map(|session| Self::update(session, vec![removed.clone()]))
            .collect()
    }
}

/// The distance a wormhole end keeps outside the body it would lie inside (metres).
const CLEARANCE_OUTSIDE_BODY: f64 = 50_000.0;

/// `position` moved out of every star and planet of the system it lies inside. A ship that warps to an end inside a star is bumped
/// out to the star's surface, far from the end, and cannot jump. The end goes out along its bearing from that body, to the body's surface, the
/// end's radius and the clearance. shortcut: the host places an end 50 km from its system's centre, which lies inside a star in some systems;
/// the reference's placement is not in the sources, so the end is moved out here (DEVIATIONS).
pub(crate) fn clear_of_bodies(bodies: &[Body], position: Vec3, radius: f64) -> Vec3 {
    let mut at = position;
    for body in bodies {
        if !matches!(body.kind, BodyKind::Star | BodyKind::Planet) || body.radius <= 0.0 {
            continue;
        }
        let (dx, dy, dz) = (at.x - body.position.x, at.y - body.position.y, at.z - body.position.z);
        let distance = (dx * dx + dy * dy + dz * dz).sqrt();
        let reach = body.radius + radius.max(0.0);
        if !distance.is_finite() || distance >= reach {
            continue;
        }
        let direction = if distance > 0.0 {
            Vec3::new(dx / distance, dy / distance, dz / distance)
        } else {
            Vec3::new(1.0, 0.0, 0.0)
        };
        let target = reach + CLEARANCE_OUTSIDE_BODY;
        at = Vec3::new(
            body.position.x + direction.x * target,
            body.position.y + direction.y * target,
            body.position.z + direction.z * target,
        );
    }
    at
}

/// Where a ship that came out of a wormhole appears: a uniform direction from the far end, at the far end's radius, the ship's radius and
/// the arrival clearance, facing the far end.
/// shortcut: the reference's landing for a wormhole is not in the sources read; this is the gate's rule without its cone.
pub(crate) fn wormhole_arrival(
    far_position: Vec3,
    far_radius: f64,
    ship_radius: f64,
    random: &mut PythonRandom,
) -> (Vec3, Vec3) {
    let cos_theta = 1.0 - 2.0 * random.random();
    let sin_theta = (1.0 - cos_theta * cos_theta).max(0.0).sqrt();
    let phi = random.random() * PI * 2.0;
    let direction = Vec3::new(sin_theta * phi.cos(), sin_theta * phi.sin(), cos_theta);
    let offset = far_radius.max(0.0) + ship_radius.max(0.0) + gate::ARRIVAL_CLEARANCE;
    let position = Vec3::new(
        far_position.x + direction.x * offset,
        far_position.y + direction.y * offset,
        far_position.z + direction.z * offset,
    );
    (position, Vec3::new(-direction.x, -direction.y, -direction.z))
}
