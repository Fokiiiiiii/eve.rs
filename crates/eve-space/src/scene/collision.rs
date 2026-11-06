use super::*;

/// Until when the protection spares a ship from every station, and the station it is still leaving (a ship that undocked: `leaving_station`).
pub(super) type Spare = (i64, Option<i64>);

/// Whether a station spares the ship: a protected ship is spared by every station, a ship that is leaving one by that station alone (the
/// reference's ships fly out of their station's sphere without being bumped; the undock point lies inside it).
pub(super) fn spares(spare: Spare, body: &crate::world::Body, now: i64) -> bool {
    body.kind == BodyKind::Station && (spare.0 > now || spare.1 == Some(body.id))
}

impl Scene {
    /// Resolves swept contacts and remaining overlaps after ordinary movement settlement.
    pub(super) fn resolve_collisions(
        &mut self,
        now: i64,
        only: Option<SessionId>,
        starts: Option<&BTreeMap<SessionId, (i64, Vec3, bool)>>,
    ) {
        // a ship that is out of the sphere of the station it undocked from is no longer spared by it
        let system = Arc::clone(&self.system);
        for pilot in self.pilots.values_mut() {
            if let Some(station) = pilot.leaving_station
                && system.body(station).is_none_or(|body| {
                    collision_contact(body.position, pilot.ship.position, body.radius, pilot.ship.spec.radius)
                        .is_none()
                })
            {
                pilot.leaving_station = None;
            }
        }
        // ponytail: all-pairs costs O(ships^2 + ships*bodies); use spatial buckets if populated systems make this tick expensive.
        let mut ships: Vec<(SessionId, Spare, Ship)> = self
            .pilots
            .iter()
            .filter(|&(session, pilot)| {
                !self.controlled_structures.contains_key(session)
                    && pilot.settled_at == now
                    && !self.drones.contains_key(session)
                    && pilot.flight.is_none()
                    && pilot.docking.is_none()
                    && pilot.jump.is_none()
            })
            .map(|(session, pilot)| {
                (*session, (pilot.protected_until, pilot.leaving_station), pilot.ship.clone())
            })
            .collect();
        let mut active: BTreeSet<SessionId> = match only {
            Some(session) => [session].into_iter().collect(),
            None => ships.iter().map(|(session, _, _)| *session).collect(),
        };
        if !ships.iter().any(|(session, _, _)| active.contains(session)) {
            return;
        }
        let mut bumped = BTreeSet::new();
        if let Some(starts) = starts {
            resolve_swept_collisions(&mut ships, &self.system.bodies, starts, now, &mut active, &mut bumped);
        }

        for _ in 0..4 {
            let mut collided = BTreeSet::new();
            for left_index in 0..ships.len() {
                for right_index in left_index + 1..ships.len() {
                    let left_session = ships[left_index].0;
                    let right_session = ships[right_index].0;
                    if !active.contains(&left_session) && !active.contains(&right_session) {
                        continue;
                    }
                    let (left, right) = ships.split_at_mut(right_index);
                    if bump_ships(&mut left[left_index].2, &mut right[0].2) {
                        collided.insert(left_session);
                        collided.insert(right_session);
                    }
                }
            }
            for (session, spare, ship) in &mut ships {
                if !active.contains(session) {
                    continue;
                }
                for body in &self.system.bodies {
                    if matches!(body.kind, BodyKind::Belt | BodyKind::AbyssalEnvironment)
                        || spares(*spare, body, now)
                    {
                        continue;
                    }
                    if bump_ship_body(ship, body) {
                        collided.insert(*session);
                    }
                }
            }
            if collided.is_empty() {
                break;
            }
            active.extend(collided.iter().copied());
            bumped.extend(collided);
        }

        for (session, _, ship) in &mut ships {
            if !bumped.contains(session) {
                continue;
            }
            settle_bumped_ship(ship);
            if let Some(pilot) = self.pilots.get_mut(session) {
                pilot.ship = ship.clone();
            }
        }
        for session in bumped {
            let Some((ship_id, position, velocity, direction, speed_fraction, stopped)) =
                self.pilots.get(&session).map(|pilot| {
                    let ship = &pilot.ship;
                    (
                        id(ship.spec.ship_id),
                        ship.position,
                        ship.velocity,
                        ship.direction,
                        ship.speed_fraction,
                        matches!(ship.course, Course::Stop),
                    )
                })
            else {
                continue;
            };
            if !self.pilots.get(&session).is_some_and(|pilot| pilot.bound)
                && self.observers_of(session).is_empty()
            {
                continue;
            }
            let stamp = self.next_stamp(now);
            let mut actions = vec![
                payload::action(
                    stamp,
                    "SetBallPosition",
                    with_id(&ship_id, vector(position.x, position.y, position.z)),
                ),
                payload::action(
                    stamp,
                    "SetBallVelocity",
                    with_id(&ship_id, vector(velocity.x, velocity.y, velocity.z)),
                ),
                payload::action(stamp, "SetSpeedFraction", [ship_id.clone(), real(speed_fraction)]),
            ];
            if stopped {
                actions.push(payload::action(stamp, "Stop", [ship_id]));
            } else {
                actions.push(payload::action(
                    stamp,
                    "GotoDirection",
                    [ship_id, real(direction.x), real(direction.y), real(direction.z)],
                ));
            }
            let own = if self.pilots.get(&session).is_some_and(|pilot| pilot.bound) {
                actions.clone()
            } else {
                Vec::new()
            };
            self.backlog.extend(self.tell_split(session, own, actions));
        }
    }
}

pub(super) fn finite_vector(vector: Vec3) -> bool {
    vector.x.is_finite() && vector.y.is_finite() && vector.z.is_finite()
}

pub(super) fn collision_contact(
    from: Vec3,
    to: Vec3,
    from_radius: f64,
    to_radius: f64,
) -> Option<(Vec3, f64)> {
    if !finite_vector(from)
        || !finite_vector(to)
        || !from_radius.is_finite()
        || !to_radius.is_finite()
        || from_radius <= 0.0
        || to_radius <= 0.0
    {
        return None;
    }
    let radii = from_radius + to_radius;
    if !radii.is_finite() {
        return None;
    }
    let delta = vector_sub(to, from);
    if !finite_vector(delta) {
        return None;
    }
    let distance = delta.x.hypot(delta.y).hypot(delta.z);
    if !distance.is_finite() || distance >= radii {
        return None;
    }
    let normal = if distance == 0.0 {
        // Identical centers use a fixed axis; BTreeMap order makes ship pairs deterministic.
        Vec3::new(1.0, 0.0, 0.0)
    } else {
        Vec3::new(delta.x / distance, delta.y / distance, delta.z / distance)
    };
    Some((normal, radii - distance))
}

pub(super) fn collision_mass_shares(a_mass: f64, b_mass: f64) -> (f64, f64) {
    let a_mass = if a_mass.is_finite() && a_mass > 0.0 { a_mass } else { 1.0 };
    let b_mass = if b_mass.is_finite() && b_mass > 0.0 { b_mass } else { 1.0 };
    let scale = a_mass.max(b_mass);
    let a = a_mass / scale;
    let b = b_mass / scale;
    let total = a + b;
    (b / total, a / total)
}

pub(super) fn bump_ships(a: &mut Ship, b: &mut Ship) -> bool {
    let Some((normal, penetration)) = collision_contact(a.position, b.position, a.spec.radius, b.spec.radius)
    else {
        return false;
    };
    let (a_share, b_share) = collision_mass_shares(a.spec.mass, b.spec.mass);
    let a_position = Vec3::new(
        a.position.x - normal.x * penetration * a_share,
        a.position.y - normal.y * penetration * a_share,
        a.position.z - normal.z * penetration * a_share,
    );
    let b_position = Vec3::new(
        b.position.x + normal.x * penetration * b_share,
        b.position.y + normal.y * penetration * b_share,
        b.position.z + normal.z * penetration * b_share,
    );
    if !finite_vector(a_position) || !finite_vector(b_position) {
        return false;
    }
    a.position = a_position;
    b.position = b_position;
    let (a_velocity, b_velocity) = bumped_ship_pair_velocity(a, b, normal);
    a.velocity = a_velocity;
    b.velocity = b_velocity;
    true
}

pub(super) fn bump_ship_body(ship: &mut Ship, body: &crate::world::Body) -> bool {
    let Some((normal, _)) = collision_contact(body.position, ship.position, body.radius, ship.spec.radius)
    else {
        return false;
    };
    let position = Vec3::new(
        body.position.x + normal.x * (body.radius + ship.spec.radius),
        body.position.y + normal.y * (body.radius + ship.spec.radius),
        body.position.z + normal.z * (body.radius + ship.spec.radius),
    );
    if !finite_vector(position) {
        return false;
    }
    ship.position = position;
    ship.velocity = bumped_ship_body_velocity(ship.velocity, normal);
    true
}

pub(super) fn collision_dot(a: Vec3, b: Vec3) -> f64 {
    a.x * b.x + a.y * b.y + a.z * b.z
}

pub(super) fn collision_lerp(from: Vec3, to: Vec3, fraction: f64) -> Vec3 {
    let delta = vector_sub(to, from);
    Vec3::new(from.x + delta.x * fraction, from.y + delta.y * fraction, from.z + delta.z * fraction)
}

pub(super) fn swept_collision_contact(
    from_a: Vec3,
    to_a: Vec3,
    from_b: Vec3,
    to_b: Vec3,
    radius_a: f64,
    radius_b: f64,
) -> Option<(Vec3, f64)> {
    let radii = radius_a + radius_b;
    if !finite_vector(from_a)
        || !finite_vector(to_a)
        || !finite_vector(from_b)
        || !finite_vector(to_b)
        || !radius_a.is_finite()
        || !radius_b.is_finite()
        || radius_a <= 0.0
        || radius_b <= 0.0
        || !radii.is_finite()
    {
        return None;
    }
    let start = vector_sub(from_b, from_a);
    let end = vector_sub(to_b, to_a);
    let travel = vector_sub(end, start);
    if !finite_vector(start) || !finite_vector(end) || !finite_vector(travel) {
        return None;
    }
    let distance_squared = collision_dot(start, start);
    let travel_squared = collision_dot(travel, travel);
    let approach = collision_dot(start, travel);
    let c = distance_squared - radii * radii;
    if !distance_squared.is_finite()
        || !travel_squared.is_finite()
        || !approach.is_finite()
        || !c.is_finite()
        || travel_squared <= 0.0
        || c < 0.0
        || approach >= 0.0
    {
        return None;
    }
    let fraction = if c == 0.0 {
        0.0
    } else {
        let b = 2.0 * approach;
        let discriminant = b * b - 4.0 * travel_squared * c;
        if !discriminant.is_finite() || discriminant < 0.0 {
            return None;
        }
        let denominator = -b + discriminant.sqrt();
        if denominator <= 0.0 {
            return None;
        }
        2.0 * c / denominator
    };
    if !fraction.is_finite() || !(0.0..=1.0).contains(&fraction) {
        return None;
    }
    let at_contact = Vec3::new(
        start.x + travel.x * fraction,
        start.y + travel.y * fraction,
        start.z + travel.z * fraction,
    );
    let distance = at_contact.x.hypot(at_contact.y).hypot(at_contact.z);
    if !distance.is_finite() || distance == 0.0 {
        return None;
    }
    Some((Vec3::new(at_contact.x / distance, at_contact.y / distance, at_contact.z / distance), fraction))
}

pub(super) fn bumped_ship_pair_velocity(a: &Ship, b: &Ship, normal: Vec3) -> (Vec3, Vec3) {
    let a_velocity = if finite_vector(a.velocity) { a.velocity } else { Vec3::ZERO };
    let b_velocity = if finite_vector(b.velocity) { b.velocity } else { Vec3::ZERO };
    let normal_speed = collision_dot(vector_sub(b_velocity, a_velocity), normal);
    if !normal_speed.is_finite() || normal_speed >= 0.0 {
        return (a_velocity, b_velocity);
    }
    let (a_share, b_share) = collision_mass_shares(a.spec.mass, b.spec.mass);
    let impulse = -normal_speed * (1.0 + BUMP_RESTITUTION);
    let next_a = Vec3::new(
        a_velocity.x - normal.x * impulse * a_share,
        a_velocity.y - normal.y * impulse * a_share,
        a_velocity.z - normal.z * impulse * a_share,
    );
    let next_b = Vec3::new(
        b_velocity.x + normal.x * impulse * b_share,
        b_velocity.y + normal.y * impulse * b_share,
        b_velocity.z + normal.z * impulse * b_share,
    );
    if finite_vector(next_a) && finite_vector(next_b) { (next_a, next_b) } else { (a_velocity, b_velocity) }
}

pub(super) fn bumped_ship_body_velocity(velocity: Vec3, normal: Vec3) -> Vec3 {
    let velocity = if finite_vector(velocity) { velocity } else { Vec3::ZERO };
    let into_surface = collision_dot(velocity, normal);
    if !into_surface.is_finite() || into_surface >= 0.0 {
        return velocity;
    }
    let impulse = (1.0 + BUMP_RESTITUTION) * into_surface;
    let bounced = Vec3::new(
        velocity.x - normal.x * impulse,
        velocity.y - normal.y * impulse,
        velocity.z - normal.z * impulse,
    );
    if finite_vector(bounced) { bounced } else { Vec3::ZERO }
}

pub(super) fn bump_ships_swept(
    a: &mut Ship,
    b: &mut Ship,
    a_start: Vec3,
    b_start: Vec3,
    seconds: f64,
) -> bool {
    let a_end = a.position;
    let b_end = b.position;
    let Some((normal, fraction)) =
        swept_collision_contact(a_start, a_end, b_start, b_end, a.spec.radius, b.spec.radius)
    else {
        return false;
    };
    let mut a_contact = collision_lerp(a_start, a_end, fraction);
    let mut b_contact = collision_lerp(b_start, b_end, fraction);
    let distance = speed_of(vector_sub(b_contact, a_contact));
    let penetration = (a.spec.radius + b.spec.radius - distance).max(0.0);
    if penetration > 0.0 {
        let (a_share, b_share) = collision_mass_shares(a.spec.mass, b.spec.mass);
        a_contact = Vec3::new(
            a_contact.x - normal.x * penetration * a_share,
            a_contact.y - normal.y * penetration * a_share,
            a_contact.z - normal.z * penetration * a_share,
        );
        b_contact = Vec3::new(
            b_contact.x + normal.x * penetration * b_share,
            b_contact.y + normal.y * penetration * b_share,
            b_contact.z + normal.z * penetration * b_share,
        );
    }
    let (a_velocity, b_velocity) = bumped_ship_pair_velocity(a, b, normal);
    let remaining = seconds * (1.0 - fraction);
    let a_position = Vec3::new(
        a_contact.x + a_velocity.x * remaining,
        a_contact.y + a_velocity.y * remaining,
        a_contact.z + a_velocity.z * remaining,
    );
    let b_position = Vec3::new(
        b_contact.x + b_velocity.x * remaining,
        b_contact.y + b_velocity.y * remaining,
        b_contact.z + b_velocity.z * remaining,
    );
    if !seconds.is_finite() || seconds <= 0.0 || !finite_vector(a_position) || !finite_vector(b_position) {
        return false;
    }
    a.position = a_position;
    b.position = b_position;
    a.velocity = a_velocity;
    b.velocity = b_velocity;
    true
}

pub(super) fn bump_ship_body_swept(
    ship: &mut Ship,
    body: &crate::world::Body,
    start: Vec3,
    seconds: f64,
) -> bool {
    let end = ship.position;
    let Some((normal, fraction)) =
        swept_collision_contact(body.position, body.position, start, end, body.radius, ship.spec.radius)
    else {
        return false;
    };
    let contact_distance = body.radius + ship.spec.radius;
    let contact = Vec3::new(
        body.position.x + normal.x * contact_distance,
        body.position.y + normal.y * contact_distance,
        body.position.z + normal.z * contact_distance,
    );
    let velocity = bumped_ship_body_velocity(ship.velocity, normal);
    let remaining = seconds * (1.0 - fraction);
    let position = Vec3::new(
        contact.x + velocity.x * remaining,
        contact.y + velocity.y * remaining,
        contact.z + velocity.z * remaining,
    );
    if !seconds.is_finite() || seconds <= 0.0 || !finite_vector(position) {
        return false;
    }
    ship.position = position;
    ship.velocity = velocity;
    true
}

// ponytail: one swept impact per ship per settlement; iterate events further if multi-contact ticks appear in play.
pub(super) fn resolve_swept_collisions(
    ships: &mut [(SessionId, Spare, Ship)],
    bodies: &[crate::world::Body],
    starts: &BTreeMap<SessionId, (i64, Vec3, bool)>,
    now: i64,
    active: &mut BTreeSet<SessionId>,
    bumped: &mut BTreeSet<SessionId>,
) {
    let mut swept = BTreeSet::new();
    for left_index in 0..ships.len() {
        for right_index in left_index + 1..ships.len() {
            let left_session = ships[left_index].0;
            let right_session = ships[right_index].0;
            if (!active.contains(&left_session) && !active.contains(&right_session))
                || swept.contains(&left_session)
                || swept.contains(&right_session)
            {
                continue;
            }
            let (Some((left_at, left_start, left_warping)), Some((right_at, right_start, right_warping))) =
                (starts.get(&left_session).copied(), starts.get(&right_session).copied())
            else {
                continue;
            };
            if left_at >= now || right_at >= now || left_warping || right_warping {
                continue;
            }
            let common_at = left_at.max(right_at);
            if common_at >= now {
                continue;
            }
            let left_fraction = (common_at - left_at) as f64 / (now - left_at) as f64;
            let right_fraction = (common_at - right_at) as f64 / (now - right_at) as f64;
            let left_from = collision_lerp(left_start, ships[left_index].2.position, left_fraction);
            let right_from = collision_lerp(right_start, ships[right_index].2.position, right_fraction);
            let seconds = (now - common_at) as f64 / TICKS_PER_SECOND as f64;
            let (left, right) = ships.split_at_mut(right_index);
            if bump_ships_swept(&mut left[left_index].2, &mut right[0].2, left_from, right_from, seconds) {
                swept.insert(left_session);
                swept.insert(right_session);
                active.insert(left_session);
                active.insert(right_session);
                bumped.insert(left_session);
                bumped.insert(right_session);
            }
        }
    }
    for (session, spare, ship) in ships {
        if !active.contains(session) || swept.contains(session) {
            continue;
        }
        let Some((start_at, start, warping)) = starts.get(session).copied() else { continue };
        if start_at >= now || warping {
            continue;
        }
        let seconds = (now - start_at) as f64 / TICKS_PER_SECOND as f64;
        for body in bodies {
            if matches!(body.kind, BodyKind::Belt | BodyKind::AbyssalEnvironment) || spares(*spare, body, now)
            {
                continue;
            }
            if bump_ship_body_swept(ship, body, start, seconds) {
                swept.insert(*session);
                bumped.insert(*session);
                break;
            }
        }
    }
}

pub(super) fn settle_bumped_ship(ship: &mut Ship) {
    if !finite_vector(ship.velocity) {
        ship.velocity = Vec3::ZERO;
    }
    let speed = ship.velocity.x.hypot(ship.velocity.y).hypot(ship.velocity.z);
    if !speed.is_finite() {
        ship.velocity = Vec3::ZERO;
    }
    let speed = ship.velocity.x.hypot(ship.velocity.y).hypot(ship.velocity.z);
    if speed == 0.0 {
        ship.speed_fraction = 0.0;
        ship.course = Course::Stop;
        return;
    }
    ship.direction = Vec3::new(ship.velocity.x / speed, ship.velocity.y / speed, ship.velocity.z / speed);
    ship.speed_fraction = if ship.spec.max_velocity.is_finite() && ship.spec.max_velocity > 0.0 {
        (speed / ship.spec.max_velocity).clamp(0.0, 1.0)
    } else {
        0.0
    };
    ship.course = Course::Goto;
}
