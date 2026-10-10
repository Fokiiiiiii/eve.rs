use super::*;

impl Scene {
    pub(crate) fn settle_pilot(&mut self, session: SessionId, now: i64) {
        if !self.pilots.contains_key(&session) {
            return;
        }
        let starts = self.settle_all_motion(now);
        self.resolve_collisions(now, Some(session), Some(&starts));
    }

    /// As [`Scene::settle_pilot`]; a ship that follows or orbits another ship is moved after the ship it follows (`depth` stops a ring of ships
    /// that follow one another).
    pub(super) fn settle_pilot_after(&mut self, session: SessionId, now: i64, depth: u8) {
        let system = Arc::clone(&self.system);
        let course_target = match self.pilots.get(&session).map(|pilot| pilot.ship.course) {
            Some(Course::Follow { target, .. } | Course::Orbit { target, .. })
                if system.body(target).is_none() =>
            {
                Some(target)
            }
            _ => None,
        };
        let followed =
            course_target.and_then(|target| self.session_of(target).filter(|other| *other != session));
        let mut ship_target = None;
        if let Some(other) = followed {
            if depth < MAX_FOLLOW_CHAIN {
                self.settle_pilot_after(other, now, depth + 1);
            }
            ship_target = self.target_of_ship(other);
        } else if let Some(wreck) = course_target.and_then(|target| self.wrecks.get(&target)) {
            // a wreck or a can does not move
            ship_target = Some(Target::at_rest(wreck.position, wreck.radius));
        } else if let Some(asteroid) = course_target.and_then(|target| self.asteroids.get(&target)) {
            // mineable rocks are stationary targets in this scene.
            ship_target = Some(Target::at_rest(asteroid.position, asteroid.radius));
        }
        let Some(pilot) = self.pilots.get_mut(&session) else { return };
        let from = pilot.settled_at;
        let milestones = settle(pilot, &system, ship_target, now);
        // a wreck this ship's beam pulls moves over the same time as the ship
        self.advance_pulled_by(session, from, now);
        for milestone in milestones {
            let told = self.tell(session, milestone);
            self.backlog.extend(told);
        }
    }

    pub(crate) fn settle_all(&mut self, now: i64) {
        let starts = self.settle_all_motion(now);
        self.resolve_collisions(now, None, Some(&starts));
        self.release_orphaned_pulls(now);
    }

    pub(super) fn settle_all_motion(&mut self, now: i64) -> BTreeMap<SessionId, (i64, Vec3, bool)> {
        let starts = self
            .pilots
            .iter()
            .map(|(session, pilot)| {
                (
                    *session,
                    (pilot.settled_at, pilot.ship.position, pilot.flight.is_some() || pilot.jump.is_some()),
                )
            })
            .collect();
        let sessions: Vec<SessionId> = self.pilots.keys().copied().collect();
        for session in sessions {
            self.settle_pilot_after(session, now, 0);
        }
        starts
    }

    /// The belt a ship that came to rest at `position` has arrived at: the one it warped to, else the nearest within the landing radius (its id and
    /// where it is).
    pub(super) fn belt_arrived_at(&self, session: SessionId, position: Vec3) -> Option<(i64, Vec3)> {
        let system = &self.system;
        let warped_to = self.pilots.get(&session).and_then(|pilot| pilot.warp_target);
        if let Some(belt) =
            warped_to.and_then(|id| system.body(id)).filter(|body| body.kind == BodyKind::Belt)
        {
            return Some((belt.id, belt.position));
        }
        let candidates = system.bodies.iter().filter(|body| body.kind == BodyKind::Belt).map(|body| {
            crate::geo::SpatialEntity {
                id: body.id,
                kind: crate::geo::SpatialKind::SystemBody,
                character_id: None,
                owner_id: None,
                corporation_id: None,
                alliance_id: None,
                type_id: None,
                group_id: None,
                category_id: None,
                position: [body.position.x, body.position.y, body.position.z],
                radius: body.radius,
                warping: false,
                item: body,
            }
        });
        crate::geo::SpatialQuery::radius(
            [position.x, position.y, position.z],
            f64::MAX,
            crate::geo::SpatialFilter::default(),
        )
        .execute(candidates)
        .into_iter()
        .find(|hit| hit.distance <= BELT_LANDING_RADIUS + hit.item.radius)
        .map(|hit| (hit.id, hit.item.position))
    }

    pub(super) fn position_of(&self, session: SessionId, target_id: i64) -> Vec<Out> {
        let command = Command::PositionOf { target_id }.code();
        let refused = |reason| vec![Out::Refused { session, command, reason }];
        let Some(position) = self.system.body(target_id).map(|body| body.position).or_else(|| {
            self.pilots
                .values()
                .find(|pilot| pilot.ship.spec.ship_id == target_id)
                .map(|pilot| pilot.ship.position)
        }) else {
            return refused("TARGET_NOT_FOUND");
        };
        vec![Out::Position { session, command, position }]
    }

    /// The point `distance` metres ahead of a ship along its heading (EVE.js `buildDeployableSpawnState`: the ship's position plus its direction times the distance).
    /// A ship with no heading gets +x. The ship is the pilot's, so a session with no ship in space is refused.
    pub(super) fn drop_point(&self, session: SessionId, distance: f64) -> Vec<Out> {
        let command = Command::DropPoint { distance }.code();
        let refused = |reason| vec![Out::Refused { session, command, reason }];
        if !distance.is_finite() || distance < 0.0 {
            return refused("BAD_DISTANCE");
        }
        let Some(pilot) = self.pilots.get(&session) else {
            return refused("NO_SHIP_IN_SPACE");
        };
        let heading = pilot.ship.direction;
        let length = (heading.x * heading.x + heading.y * heading.y + heading.z * heading.z).sqrt();
        let (ux, uy, uz) = if length > 0.0 {
            (heading.x / length, heading.y / length, heading.z / length)
        } else {
            (1.0, 0.0, 0.0)
        };
        let at = pilot.ship.position;
        let position = Vec3::new(at.x + ux * distance, at.y + uy * distance, at.z + uz * distance);
        vec![Out::Position { session, command, position }]
    }

    /// A movement command of a ship that is in the scene (settled first, at `now`).
    pub(super) fn steer_pilot(&mut self, session: SessionId, command: Command, now: i64) -> Vec<Out> {
        let code = command.code();
        let target = match command {
            Command::FollowBall { target_id, .. }
            | Command::Orbit { target_id, .. }
            | Command::AlignTo { target_id } => self.target_of(target_id, session),
            _ => None,
        };
        let pilot = self.pilots.get_mut(&session).expect("the caller checked");
        if pilot.immobile_until > now {
            // a ship held by a doomsday's immobility window follows no orders
            return vec![Out::Refused { session, command: code, reason: "IMMOBILE" }];
        }
        if pilot.docking.is_some() {
            // a ship that is docking follows no orders
            return vec![Out::Refused { session, command: code, reason: "DOCKING" }];
        }
        if pilot.jump.is_some() {
            // nor does one that has gone into a gate
            return vec![Out::Refused { session, command: code, reason: "JUMPING" }];
        }
        if pilot.modules.is_mjd_spooling() {
            return vec![Out::Refused { session, command: code, reason: "MJD_SPOOLING" }];
        }
        if pilot.flight.is_some() {
            // a ship in warp, or lining up for it, follows none either
            return vec![Out::Refused { session, command: code, reason: "IN_WARP" }];
        }
        let ship_id = id(pilot.ship.spec.ship_id);
        match steer(&mut pilot.ship, target, command) {
            Steering::Refused(reason) => vec![Out::Refused { session, command: code, reason }],
            Steering::Nothing => vec![Out::Accepted { session, command: code, at: now }],
            Steering::Actions(actions) => {
                let stamp = self.next_stamp(now);
                let actions: Vec<Value> = actions
                    .into_iter()
                    .map(|(name, args)| {
                        let mut all = vec![ship_id.clone()];
                        all.extend(args);
                        payload::action(stamp, name, all)
                    })
                    .collect();
                let mut out = self.tell_all(session, actions);
                out.push(Out::Accepted { session, command: code, at: now });
                out
            }
        }
    }
}

pub(super) fn with_id(ship_id: &Value, rest: [Value; 3]) -> Vec<Value> {
    let mut args = vec![ship_id.clone()];
    args.extend(rest);
    args
}

/// Brings a ship's position and velocity to `now`: it eases towards the speed it is asked for, the way the client integrates it, and a ship
/// that follows or orbits a body is steered by where the body is.
pub(super) fn settle(
    pilot: &mut Pilot,
    system: &SolarSystem,
    ship_target: Option<Target>,
    now: i64,
) -> Vec<Milestone> {
    let from = pilot.settled_at;
    if now <= from {
        return Vec::new();
    }
    pilot.settled_at = now;
    let seconds = (now - from) as f64 / TICKS_PER_SECOND as f64;
    if let Some(flight) = pilot.flight.as_mut() {
        let advance = flight.advance(&mut pilot.ship, from, now);
        if let Some(rested) = advance.finished_at {
            // the warp is over: what is left of the time is a stopped ship's
            pilot.flight = None;
            pilot.ship.course = Course::Stop;
            pilot.ship.speed_fraction = 0.0;
            motion::advance(&mut pilot.ship, (now - rested) as f64 / TICKS_PER_SECOND as f64, None);
        }
        return advance.milestones;
    }
    let target = match pilot.ship.course {
        Course::Follow { target, .. } | Course::Orbit { target, .. } => ship_target
            .or_else(|| system.body(target).map(|body| Target::at_rest(body.position, body.radius))),
        Course::Goto | Course::GotoPoint { .. } | Course::Stop | Course::Warp { .. } => None,
    };
    motion::advance(&mut pilot.ship, seconds, target);
    Vec::new()
}

/// The heading of a ship as the client's action names it.
pub(super) fn heading_name(ship: &Ship) -> &'static str {
    match ship.course {
        Course::Goto => "GotoDirection",
        Course::GotoPoint { .. } => "GotoPoint",
        Course::Stop => "Stop",
        Course::Follow { .. } => "FollowBall",
        Course::Orbit { .. } => "Orbit",
        Course::Warp { .. } => "WarpTo",
    }
}

/// The arguments of that action, the ship's id first.
pub(super) fn heading_args(ship: &Ship) -> Vec<Value> {
    let ship_id = id(ship.spec.ship_id);
    match ship.course {
        Course::Goto => {
            with_id(&ship_id, vector(ship.direction.x, ship.direction.y, ship.direction.z)).to_vec()
        }
        Course::Stop => vec![ship_id],
        Course::GotoPoint { point } => vec![ship_id, real(point.x), real(point.y), real(point.z)],
        Course::Follow { target, range } => vec![ship_id, id(target), int(whole(range))],
        Course::Orbit { target, distance } => vec![ship_id, id(target), int(whole(distance))],
        Course::Warp { raw_destination: r, stop_distance, factor, .. } => {
            vec![ship_id, real(r.x), real(r.y), real(r.z), real(stop_distance), int(factor)]
        }
    }
}

/// A distance as the client's actions carry it: whole metres, cut off.
pub(super) fn whole(metres: f64) -> i64 {
    metres.trunc().clamp(i32::MIN as f64, i32::MAX as f64) as i64
}

pub(super) fn speed_of(velocity: Vec3) -> f64 {
    (velocity.x * velocity.x + velocity.y * velocity.y + velocity.z * velocity.z).sqrt()
}

pub(super) fn vector_sub(a: Vec3, b: Vec3) -> Vec3 {
    Vec3::new(a.x - b.x, a.y - b.y, a.z - b.z)
}

/// The speed a ship that is not moving at one has to be asked for to move: what it had, or `otherwise`.
pub(super) fn resumed_speed(ship: &Ship, otherwise: f64) -> (f64, bool) {
    let before = ship.speed_fraction;
    let after = if before > 0.0 { before } else { otherwise };
    (after, (after - before).abs() > 1e-6)
}

pub(super) fn speed_action(ship: &Ship, changed: bool, actions: &mut Vec<(&'static str, Vec<Value>)>) {
    if changed {
        actions.push(("SetSpeedFraction", vec![real(ship.speed_fraction)]));
    }
}

/// Puts a movement command into effect on a ship whose motion is settled, and says what the client is to be told.
/// `target` is what the command's target is (a body or another ship), when the scene has it.
pub(super) fn steer(ship: &mut Ship, target: Option<Target>, command: Command) -> Steering {
    match command {
        Command::SetSpeedFraction(fraction) => {
            let fraction = if fraction.is_finite() { fraction.clamp(0.0, 1.0) } else { 0.0 };
            if fraction <= 0.0 {
                return stop(ship);
            }
            ship.speed_fraction = fraction;
            if matches!(ship.course, Course::Stop) {
                ship.course = Course::Goto;
            }
            Steering::Actions(vec![("SetSpeedFraction", vec![real(fraction)])])
        }
        Command::GotoDirection(direction) => {
            let Some(unit) = motion::unit(direction) else { return Steering::Refused("NO_DIRECTION") };
            let stopped = ship.speed_fraction <= 0.0 || matches!(ship.course, Course::Stop);
            let same = matches!(ship.course, Course::Goto)
                && ship.direction.x * unit.x + ship.direction.y * unit.y + ship.direction.z * unit.z
                    >= 0.999_999;
            if same && !stopped {
                return Steering::Nothing;
            }
            ship.direction = unit;
            ship.course = Course::Goto;
            if stopped {
                ship.speed_fraction = 1.0;
            }
            let mut actions = vec![("GotoDirection", vector(unit.x, unit.y, unit.z).to_vec())];
            speed_action(ship, stopped, &mut actions);
            Steering::Actions(actions)
        }
        Command::Stop => stop(ship),
        Command::Dock { .. }
        | Command::WarpTo { .. }
        | Command::WarpToPoint { .. }
        | Command::ResolveWarpTarget { .. }
        | Command::PositionOf { .. }
        | Command::DropPoint { .. }
        | Command::ScanTargets
        | Command::DirectionalScan
        | Command::Jump { .. }
        | Command::CynoJump { .. } => Steering::Refused("NOT_A_MOVEMENT"),
        Command::FollowBall { target_id, range } => {
            if !range.is_finite() {
                return Steering::Refused("BAD_RANGE");
            }
            if target.is_none() {
                return Steering::Refused("UNKNOWN_TARGET");
            }
            let range = range.max(0.0);
            if matches!(ship.course, Course::Follow { target, range: had } if target == target_id && (had - range).abs() < 1.0)
            {
                return Steering::Nothing;
            }
            let (speed, changed) = resumed_speed(ship, 1.0);
            ship.course = Course::Follow { target: target_id, range };
            ship.speed_fraction = speed;
            let mut actions = vec![("FollowBall", vec![id(target_id), int(whole(range))])];
            speed_action(ship, changed, &mut actions);
            Steering::Actions(actions)
        }
        Command::Orbit { target_id, distance } => {
            if !distance.is_finite() {
                return Steering::Refused("BAD_RANGE");
            }
            if target.is_none() {
                return Steering::Refused("UNKNOWN_TARGET");
            }
            let distance = distance.max(0.0);
            if matches!(ship.course, Course::Orbit { target, distance: had } if target == target_id && (had - distance).abs() < 1.0)
                && ship.speed_fraction > 0.0
            {
                return Steering::Nothing;
            }
            let (speed, changed) = resumed_speed(ship, 1.0);
            ship.course = Course::Orbit { target: target_id, distance };
            ship.speed_fraction = speed;
            let mut actions = vec![("Orbit", vec![id(target_id), int(whole(distance))])];
            speed_action(ship, changed, &mut actions);
            Steering::Actions(actions)
        }
        Command::AlignTo { .. } => {
            let Some(target) = target else { return Steering::Refused("UNKNOWN_TARGET") };
            let towards = Vec3::new(
                target.position.x - ship.position.x,
                target.position.y - ship.position.y,
                target.position.z - ship.position.z,
            );
            let direction = motion::unit(towards).unwrap_or(ship.direction);
            let (speed, changed) = resumed_speed(ship, 0.75);
            ship.direction = direction;
            ship.course = Course::Goto;
            ship.speed_fraction = speed;
            let mut actions = vec![("GotoDirection", vector(direction.x, direction.y, direction.z).to_vec())];
            speed_action(ship, changed, &mut actions);
            Steering::Actions(actions)
        }
    }
}

/// A ship told to stop: it says so unless it stands still already.
pub(super) fn stop(ship: &mut Ship) -> Steering {
    let standing =
        matches!(ship.course, Course::Stop) && ship.speed_fraction <= 0.0 && speed_of(ship.velocity) < 0.1;
    ship.course = Course::Stop;
    ship.speed_fraction = 0.0;
    if standing { Steering::Nothing } else { Steering::Actions(vec![("Stop", Vec::new())]) }
}
