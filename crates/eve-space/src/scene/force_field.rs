//! POS force field admission, boundary and presentation (protocol 104, slices 2 and 3).
//!
//! A field admits the ships of its tower's corporation and alliance (where the tower allows each) and the ships whose pilot has set the tower's password
//! (EVE.js `canShipEnterForceField`, `starbaseAccessRuntime.js`). Any other ship is stopped at the field's boundary (EVE.js `enforceForceFieldMovement`,
//! `starbaseForceFieldBoundary.js`): its position is put where its move entered the field, it is pushed outward and an order that pulls it back in is
//! cancelled. An admitted ship inside a field takes the tower's harmonic, which the client compares with its own (`playerOwned.py:91-115`). The
//! clients that see the ship are told the correction and the harmonic as EVE.js tells them (`starbaseForceField.js`, `stream/actions.js`).
//! Standings admission is not modelled: the pinned EveJS tree does not back it, so it is a documented gap.

use eve_destiny::Vec3;

use crate::deployable::Role;
use crate::payload;
use crate::scene::{Out, Scene, SessionId};
use crate::ship::Course;
use crate::wire::{id, int, real, vector};

/// The distance, in metres, a ship keeps from the field's surface on top of its own radius (EVE.js `BOUNDARY_CLEARANCE_METERS`).
pub(crate) const BOUNDARY_CLEARANCE_METERS: f64 = 1.0;
/// The outward speed, in metres per second, given to a ship stranded deep inside a field (EVE.js `MIN_REPULSION_SPEED` and `MAX_REPULSION_SPEED`).
const MIN_REPULSION_SPEED: f64 = 500.0;
const MAX_REPULSION_SPEED: f64 = 2500.0;
/// The harmonic of a ship that has none (EVE.js `-1`).
pub(crate) const NO_HARMONIC: i64 = -1;

/// Who a field admits by corporation and alliance (EVE.js `getForceFieldAccess`): the tower's owner corporation and its alliance, each only while the
/// tower allows it. A zero id admits no ship by that group.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Access {
    pub corporation_id: i32,
    pub alliance_id: i32,
    pub allow_corporation: bool,
    pub allow_alliance: bool,
}

impl Access {
    /// Whether a ship of this corporation and alliance is admitted by the group rules alone.
    pub(crate) fn admits_group(&self, corporation_id: i32, alliance_id: i32) -> bool {
        (self.allow_corporation && self.corporation_id != 0 && corporation_id == self.corporation_id)
            || (self.allow_alliance && self.alliance_id != 0 && alliance_id == self.alliance_id)
    }
}

/// What the boundary did to a ship's move: where the ship is now, its velocity, whether an order was cancelled and whether it was moved (EVE.js
/// `buildForceFieldBoundaryCorrectionUpdates`).
#[derive(Debug, Clone, Copy, PartialEq)]
pub(crate) struct Correction {
    pub position: Vec3,
    pub velocity: Vec3,
    pub cancelled: bool,
    pub moved: bool,
}

fn origin() -> Vec3 {
    Vec3::new(0.0, 0.0, 0.0)
}

fn minus(a: Vec3, b: Vec3) -> Vec3 {
    Vec3::new(a.x - b.x, a.y - b.y, a.z - b.z)
}

fn plus_scaled(a: Vec3, b: Vec3, scale: f64) -> Vec3 {
    Vec3::new(a.x + b.x * scale, a.y + b.y * scale, a.z + b.z * scale)
}

fn dot(a: Vec3, b: Vec3) -> f64 {
    a.x * b.x + a.y * b.y + a.z * b.z
}

fn norm(a: Vec3) -> f64 {
    dot(a, a).sqrt()
}

fn is_finite(a: Vec3) -> bool {
    a.x.is_finite() && a.y.is_finite() && a.z.is_finite()
}

/// The point on the sphere of `radius` around `center` in the direction of `offset` (EVE.js `outsidePoint`): `fallback` when `offset` is empty, and
/// the x axis when that is empty too.
fn outside_point(center: Vec3, radius: f64, offset: Vec3, fallback: Vec3) -> Vec3 {
    let mut direction = offset;
    let mut length = norm(direction);
    if length < 1e-6 {
        direction = fallback;
        length = norm(direction);
    }
    if length < 1e-6 {
        direction = Vec3::new(1.0, 0.0, 0.0);
        length = 1.0;
    }
    Vec3::new(
        center.x + direction.x * radius / length,
        center.y + direction.y * radius / length,
        center.z + direction.z * radius / length,
    )
}

/// Where a ship moving from `start` to `end` is stopped by the sphere of `radius` around `center` (EVE.js `resolveDeniedEntry`). A ship that enters the
/// sphere on this move is stopped on its surface where it enters. A ship that starts and ends inside is not moved back; `end` is its point. A warp that
/// just completed at `end` only has `end` when it ends inside. `None` when the move does not reach the sphere.
pub(crate) fn denied_entry(
    start: Vec3,
    end: Vec3,
    center: Vec3,
    radius: f64,
    warp_completed: bool,
) -> Option<Vec3> {
    let from = minus(start, center);
    let to = minus(end, center);
    let radius_squared = radius * radius;
    let starts_inside = dot(from, from) < radius_squared;
    let ends_inside = dot(to, to) < radius_squared;
    if warp_completed || starts_inside {
        return ends_inside.then_some(end);
    }
    let step = minus(end, start);
    let length_squared = dot(step, step);
    if length_squared <= 0.0 {
        return None;
    }
    let inward = dot(from, step);
    let discriminant = inward * inward - length_squared * (dot(from, from) - radius_squared);
    if inward >= 0.0 || discriminant <= 0.0 {
        return None;
    }
    let fraction = (-inward - discriminant.sqrt()) / length_squared;
    if !(0.0..=1.0).contains(&fraction) {
        return None;
    }
    Some(outside_point(center, radius, plus_scaled(from, step, fraction), Vec3::new(1.0, 0.0, 0.0)))
}

/// The field a ship is stopped by on this move: the nearest one that denies it.
struct Denial {
    point: Vec3,
    center: Vec3,
    radius: f64,
    travel: f64,
}

impl Scene {
    /// Records who a tower's field admits by corporation and alliance (protocol 104, kind 184). It applies to the field of the tower now and to one placed
    /// later.
    pub(crate) fn set_force_field_access(&mut self, tower_id: i64, access: Access) {
        self.force_field_access.insert(tower_id, access);
    }

    /// Records whether a pilot's ship password matches a tower's (protocol 104, kind 185): the Host checks the password, and a matching ship is admitted.
    pub(crate) fn set_force_field_password(&mut self, session: SessionId, tower_id: i64, matched: bool) {
        if matched {
            self.force_field_passwords.insert((session, tower_id));
        } else {
            self.force_field_passwords.remove(&(session, tower_id));
        }
    }

    /// Forgets the password and the harmonic of each pilot no longer in the scene (one that left, docked or was destroyed).
    pub(crate) fn prune_force_field_sessions(&mut self) {
        let pilots = &self.pilots;
        self.force_field_harmonics.retain(|session, _| pilots.contains_key(session));
        self.force_field_passwords.retain(|(session, _)| pilots.contains_key(session));
    }

    /// Applies the force field boundary to a pilot's move from `from` to its position (EVE.js `enforceForceFieldMovement`). A ship in warp is not stopped
    /// by a field: `warp_completed` is the warp that ended on this move, which is checked at its end. Returns what the boundary did, when it stopped the move.
    pub(crate) fn enforce_force_fields(
        &mut self,
        session: SessionId,
        from: Vec3,
        warp_completed: bool,
    ) -> Option<Correction> {
        let pilot = self.pilots.get(&session)?;
        if pilot.flight.is_some() {
            return None;
        }
        let ship = &pilot.ship;
        let end = ship.position;
        let (corporation_id, alliance_id) = (ship.spec.corporation_id, ship.spec.alliance_id);
        let ship_radius = ship.spec.radius.max(0.0);
        let heading = if is_finite(ship.direction) { ship.direction } else { Vec3::new(1.0, 0.0, 0.0) };
        let fields: Vec<(i64, Vec3, f64)> = self
            .deployables
            .values()
            .filter_map(|held| match &held.role {
                Role::ForceField { tower_id } => Some((*tower_id, held.position, held.spec.radius)),
                _ => None,
            })
            .collect();

        let mut nearest: Option<Denial> = None;
        let mut admitted_harmonic = NO_HARMONIC;
        for (tower_id, center, field_radius) in fields {
            if !field_radius.is_finite() || field_radius <= 0.0 || !is_finite(center) {
                continue;
            }
            let boundary = field_radius + ship_radius + BOUNDARY_CLEARANCE_METERS;
            let Some(point) = denied_entry(from, end, center, boundary, warp_completed) else { continue };
            let by_group = self
                .force_field_access
                .get(&tower_id)
                .is_some_and(|access| access.admits_group(corporation_id, alliance_id));
            if by_group || self.force_field_passwords.contains(&(session, tower_id)) {
                if norm(minus(end, center)) < boundary {
                    admitted_harmonic = tower_id;
                }
                continue;
            }
            let travel = minus(point, from);
            let travel = dot(travel, travel);
            if nearest.as_ref().is_none_or(|held| travel < held.travel) {
                nearest = Some(Denial { point, center, radius: boundary, travel });
            }
        }

        let harmonic = if nearest.is_some() { NO_HARMONIC } else { admitted_harmonic };
        if harmonic == NO_HARMONIC {
            self.force_field_harmonics.remove(&session);
        } else {
            self.force_field_harmonics.insert(session, harmonic);
        }
        let denial = nearest?;
        let pilot = self.pilots.get_mut(&session)?;
        let ship = &mut pilot.ship;
        ship.position = denial.point;
        let normal = outside_point(
            origin(),
            1.0,
            minus(denial.point, denial.center),
            outside_point(
                origin(),
                1.0,
                minus(from, denial.center),
                Vec3::new(-heading.x, -heading.y, -heading.z),
            ),
        );
        let velocity = ship.velocity;
        let radial_speed = dot(velocity, normal);
        let boundary_contact =
            !warp_completed && norm(minus(from, denial.center)) >= denial.radius - BOUNDARY_CLEARANCE_METERS;
        let outward_speed = if boundary_contact {
            20.0_f64.max(100.0_f64.min((-radial_speed).max(0.0) * 0.1))
        } else {
            MIN_REPULSION_SPEED.max(MAX_REPULSION_SPEED.min(denial.radius / 5.0))
        };
        let impulse = (outward_speed - radial_speed).max(0.0);
        ship.velocity = plus_scaled(velocity, normal, impulse);
        // Only an order that pulls the ship back into the field is cancelled; outward orders and sideways motion stay.
        let pulls_back = match ship.course {
            Course::Stop => false,
            Course::GotoPoint { point } => dot(minus(point, ship.position), normal) <= 0.0,
            Course::Goto => dot(ship.direction, normal) <= 0.0,
            Course::Follow { .. } | Course::Orbit { .. } | Course::Warp { .. } => true,
        };
        if pulls_back {
            ship.course = Course::Stop;
            ship.speed_fraction = 0.0;
        }
        Some(Correction {
            position: ship.position,
            velocity: ship.velocity,
            cancelled: pulls_back,
            moved: norm(minus(denial.point, end)) > 0.0,
        })
    }

    /// Recomputes a pilot's harmonic where its ship is now, after the admission of a field changed (a password, or a tower's access). The ship is not
    /// moved: inside a field it is admitted to it takes that tower's harmonic, and inside a field that does not admit it it has none.
    pub(crate) fn refresh_force_field_harmonic(&mut self, session: SessionId, now: i64) -> Vec<Out> {
        let harmonic = {
            let Some(pilot) = self.pilots.get(&session) else { return Vec::new() };
            if pilot.flight.is_some() {
                return Vec::new();
            }
            let ship = &pilot.ship;
            let position = ship.position;
            let (corporation_id, alliance_id) = (ship.spec.corporation_id, ship.spec.alliance_id);
            let ship_radius = ship.spec.radius.max(0.0);
            let mut harmonic = NO_HARMONIC;
            for held in self.deployables.values() {
                let Role::ForceField { tower_id } = &held.role else { continue };
                let (tower_id, center, field_radius) = (*tower_id, held.position, held.spec.radius);
                if !field_radius.is_finite() || field_radius <= 0.0 || !is_finite(center) {
                    continue;
                }
                let boundary = field_radius + ship_radius + BOUNDARY_CLEARANCE_METERS;
                if norm(minus(position, center)) >= boundary {
                    continue;
                }
                let admitted = self
                    .force_field_access
                    .get(&tower_id)
                    .is_some_and(|access| access.admits_group(corporation_id, alliance_id))
                    || self.force_field_passwords.contains(&(session, tower_id));
                if !admitted {
                    harmonic = NO_HARMONIC;
                    break;
                }
                harmonic = tower_id;
            }
            harmonic
        };
        let before = self.force_field_harmonics.get(&session).copied().unwrap_or(NO_HARMONIC);
        if harmonic == NO_HARMONIC {
            self.force_field_harmonics.remove(&session);
        } else {
            self.force_field_harmonics.insert(session, harmonic);
        }
        self.publish_force_field(session, now, before, None);
        self.with_backlog(Vec::new())
    }

    /// Recomputes the harmonic of every pilot in the scene, after a tower's access changed.
    pub(crate) fn refresh_all_force_field_harmonics(&mut self, now: i64) -> Vec<Out> {
        let sessions: Vec<SessionId> = self.pilots.keys().cloned().collect();
        let mut out = Vec::new();
        for session in sessions {
            out.extend(self.refresh_force_field_harmonic(session, now));
        }
        out
    }

    /// Tells the clients that see a pilot's ship what the field did to its move (`correction`), and its harmonic when it changed from `harmonic_before`:
    /// the correction is the cancel, the position and the velocity, in that order (EVE.js `buildForceFieldBoundaryCorrectionUpdates`), and the harmonic is
    /// `SetBallHarmonic` with the ship's corporation and alliance (`buildStarbaseHarmonicUpdates`).
    pub(crate) fn publish_force_field(
        &mut self,
        session: SessionId,
        now: i64,
        harmonic_before: i64,
        correction: Option<Correction>,
    ) {
        let Some(pilot) = self.pilots.get(&session) else { return };
        let ship_id = pilot.ship.spec.ship_id;
        let (corporation_id, alliance_id) = (pilot.ship.spec.corporation_id, pilot.ship.spec.alliance_id);
        let bound = pilot.bound;
        let harmonic = self.force_field_harmonics.get(&session).copied().unwrap_or(NO_HARMONIC);
        if harmonic != harmonic_before {
            // the host keeps the ship's slim harmonic in step with this one (protocol 105)
            self.backlog.push(Out::ShipForceFieldHarmonic { session, ship_id, harmonic });
        }
        if correction.is_none() && harmonic == harmonic_before {
            return;
        }
        let stamp = self.next_stamp(now);
        let mut actions = Vec::new();
        if let Some(correction) = correction {
            if correction.cancelled {
                actions.push(payload::action(stamp, "SetSpeedFraction", [id(ship_id), real(0.0)]));
                actions.push(payload::action(stamp, "Stop", [id(ship_id)]));
            }
            if correction.moved {
                let [x, y, z] = vector(correction.position.x, correction.position.y, correction.position.z);
                actions.push(payload::action(stamp, "SetBallPosition", [id(ship_id), x, y, z]));
            }
            let [x, y, z] = vector(correction.velocity.x, correction.velocity.y, correction.velocity.z);
            actions.push(payload::action(stamp, "SetBallVelocity", [id(ship_id), x, y, z]));
        }
        if harmonic != harmonic_before {
            actions.push(payload::action(
                stamp,
                "SetBallHarmonic",
                [
                    id(ship_id),
                    int(harmonic),
                    int(i64::from(corporation_id)),
                    int(i64::from(alliance_id)),
                    int(0),
                ],
            ));
        }
        if actions.is_empty() {
            return;
        }
        let own = if bound { actions.clone() } else { Vec::new() };
        self.backlog.extend(self.tell_split(session, own, actions));
    }
}
