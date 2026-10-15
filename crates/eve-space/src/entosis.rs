//! The Entosis Link (`entosisLink`, effect 6063; `SimAction` tag 31, protocol 70): a cycle that works on a structure the pilot holds the lock on. The
//! simulation keeps the target and its reach; the host decides the capture (ADR 0013: a work cycle, command `WORK_ENTOSIS`).
//!
//! The reach is EVE.js's `resolveEntosisLinkActivation` and `executeEntosisLinkCycle`: a surface-to-surface distance with a grace of one metre.

use crate::scene::{Scene, SessionId};
use crate::world::BodyKind;

/// How far past its range a link still holds its target, metres (EVE.js `RANGE_GRACE_METERS`).
pub(crate) const REACH_GRACE_METERS: f64 = 1.0;

impl Scene {
    /// The distance from the pilot's ship to the surface of a structure body of the scene, or `None` when the pilot has no ship or the body is not a structure.
    pub(crate) fn surface_distance_to_structure(&self, session: SessionId, structure_id: i64) -> Option<f64> {
        let body = self.system().body(structure_id).filter(|body| body.kind == BodyKind::Structure)?;
        let ship = self.pilots.get(&session)?.ship.position;
        let centre = ((ship.x - body.position.x).powi(2)
            + (ship.y - body.position.y).powi(2)
            + (ship.z - body.position.z).powi(2))
        .sqrt();
        Some((centre - body.radius.max(0.0)).max(0.0))
    }

    /// Whether a link of `session` still has its target: a structure the pilot holds the lock on, within `range` (and the grace).
    pub(crate) fn entosis_target_ok(&self, session: SessionId, structure_id: i64, range: f64) -> bool {
        self.locked_by(session).contains(&structure_id)
            && self
                .surface_distance_to_structure(session, structure_id)
                .is_some_and(|apart| apart <= range + REACH_GRACE_METERS)
    }
}
