use super::*;

impl Scene {
    /// `CmdWarpToStuff`: resolve a body or ship to its warp-in point and start a warp.
    pub(super) fn warp_to(&mut self, session: SessionId, target_id: i64, range: f64, now: i64) -> Vec<Out> {
        let code = Command::WarpTo { target_id, range }.code();
        let refused = |reason| vec![Out::Refused { session, command: code, reason }];
        if !range.is_finite() {
            return refused("BAD_RANGE");
        }
        let Some(pilot) = self.pilots.get(&session) else { return refused("NO_SHIP_IN_SPACE") };
        let Some((raw, stop)) = self.warp_target_point(pilot, target_id, range, now) else {
            return refused("TARGET_NOT_FOUND");
        };
        self.begin_warp(session, code, Some(target_id), raw, stop, now)
    }

    /// Warp to an explicit point, used by saved coordinates and formation placements.
    pub(super) fn warp_to_point(
        &mut self,
        session: SessionId,
        destination: Vec3,
        range: f64,
        now: i64,
    ) -> Vec<Out> {
        let code = Command::WarpToPoint { destination, range }.code();
        let refused = |reason| vec![Out::Refused { session, command: code, reason }];
        if !finite_vector(destination) || !range.is_finite() {
            return refused("BAD_WARP_POINT");
        }
        self.begin_warp(session, code, None, destination, range.max(0.0), now)
    }

    pub(super) fn begin_warp(
        &mut self,
        session: SessionId,
        code: u8,
        target_id: Option<i64>,
        raw: Vec3,
        stop: f64,
        now: i64,
    ) -> Vec<Out> {
        let refused = |reason| vec![Out::Refused { session, command: code, reason }];
        let Some(pilot) = self.pilots.get(&session) else { return refused("NO_SHIP_IN_SPACE") };
        if pilot.docking.is_some() {
            return refused("DOCKING");
        }
        if pilot.jump.is_some() {
            return refused("JUMPING");
        }
        if pilot.flight.is_some() {
            return refused("IN_WARP");
        }
        if pilot.immobile_until > now {
            return refused("IMMOBILE");
        }
        if pilot.disrupted_until > now {
            return refused("WARP_DISRUPTED");
        }
        if pilot.warp_blocked_until > now {
            return refused("WARP_BLOCKED");
        }
        if pilot.incoming.scrambled() {
            return refused("WARP_SCRAMBLED");
        }
        let ship = &pilot.ship;
        let toward = motion::unit(Vec3::new(
            raw.x - ship.position.x,
            raw.y - ship.position.y,
            raw.z - ship.position.z,
        ))
        .unwrap_or(ship.direction);
        let end = Vec3::new(raw.x - toward.x * stop, raw.y - toward.y * stop, raw.z - toward.z * stop);
        let total = ((end.x - ship.position.x).powi(2)
            + (end.y - ship.position.y).powi(2)
            + (end.z - ship.position.z).powi(2))
        .sqrt();
        if total < warp::MINIMUM_WARP_DISTANCE {
            return refused("WARP_DISTANCE_TOO_CLOSE");
        }

        let profile = warp::Profile::new(total, ship.spec.warp_speed_au, ship.spec.max_velocity);
        let ship_id = id(ship.spec.ship_id);
        let boosted = ship.spec.max_velocity * warp::START_SPEED_SCALE;
        let stamp = self.next_stamp(now);
        let pilot = self.pilots.get_mut(&session).expect("checked above");
        pilot.ship.course = Course::Warp {
            raw_destination: raw,
            target_point: end,
            stop_distance: stop,
            total_distance: total,
            factor: profile.factor,
            active: false,
        };
        pilot.ship.speed_fraction = 1.0;
        pilot.ship.direction = toward;
        pilot.flight = Some(Flight::begin(now, raw, stop));
        pilot.warp_target = target_id;
        let own = vec![
            payload::action(stamp, "SetMaxSpeed", [ship_id.clone(), real(boosted)]),
            payload::action(
                stamp,
                "WarpTo",
                [ship_id.clone(), real(raw.x), real(raw.y), real(raw.z), real(stop), int(profile.factor)],
            ),
            warping_effect(stamp, &ship_id),
            payload::action(stamp, "SetSpeedFraction", [ship_id.clone(), real(1.0)]),
        ];
        let watchers = vec![payload::action(
            stamp,
            "GotoDirection",
            with_id(&ship_id, vector(toward.x, toward.y, toward.z)),
        )];
        let mut out = self.tell_split(session, own, watchers);
        out.push(Out::Accepted { session, command: code, at: now });
        out
    }

    pub(super) fn warp_target_point(
        &self,
        pilot: &Pilot,
        target_id: i64,
        range: f64,
        now: i64,
    ) -> Option<(Vec3, f64)> {
        if let Some(body) = self.system.body(target_id) {
            let ship = &pilot.ship;
            return Some(match warp::warp_in_point(body) {
                Some(point) => (point, range.max(0.0)),
                None if body.kind == BodyKind::Stargate => {
                    let mut random = PythonRandom::new(seed_of(ship.spec.ship_id, target_id, now));
                    gate::warp_landing(body, ship.position, ship.direction, range, &mut random)
                }
                None => (body.position, warp::stop_distance(body, ship.spec.radius, range)),
            });
        }

        self.pilots.values().find(|target| target.ship.spec.ship_id == target_id).map(|target| {
            (target.ship.position, range.max(0.0) + target.ship.spec.radius + pilot.ship.spec.radius)
        })
    }

    pub(super) fn resolve_warp_target(
        &self,
        session: SessionId,
        target_id: i64,
        range: f64,
        now: i64,
    ) -> Vec<Out> {
        let command = Command::ResolveWarpTarget { target_id, range }.code();
        let refused = |reason| vec![Out::Refused { session, command, reason }];
        if !range.is_finite() {
            return refused("BAD_RANGE");
        }
        let Some(pilot) = self.pilots.get(&session) else { return refused("NO_SHIP_IN_SPACE") };
        let Some((raw, stop)) = self.warp_target_point(pilot, target_id, range, now) else {
            return refused("TARGET_NOT_FOUND");
        };
        let toward = motion::unit(Vec3::new(
            raw.x - pilot.ship.position.x,
            raw.y - pilot.ship.position.y,
            raw.z - pilot.ship.position.z,
        ))
        .unwrap_or(pilot.ship.direction);
        let position = Vec3::new(raw.x - toward.x * stop, raw.y - toward.y * stop, raw.z - toward.z * stop);
        let distance = ((position.x - pilot.ship.position.x).powi(2)
            + (position.y - pilot.ship.position.y).powi(2)
            + (position.z - pilot.ship.position.z).powi(2))
        .sqrt();
        if distance < warp::MINIMUM_WARP_DISTANCE {
            return refused("WARP_DISTANCE_TOO_CLOSE");
        }
        vec![Out::Position { session, command, position }]
    }
}

/// The seed of the draws that pick a point for a ship at a gate: what and where and when, so the same calls at the same times give the same
/// points (the reference draws them from the clock).
pub(super) fn seed_of(ship_id: i64, gate_id: i64, now: i64) -> u64 {
    (ship_id as u64).rotate_left(11) ^ (gate_id as u64).rotate_left(37) ^ (now as u64)
}

/// The effect the client plays on a ship that goes into warp.
pub(super) fn warping_effect(stamp: u32, ship_id: &Value) -> Value {
    payload::action(
        stamp,
        "OnSpecialFX",
        [
            ship_id.clone(),
            Value::None,
            Value::None,
            Value::None,
            Value::None,
            Value::Str("effects.Warping".into()),
            int(0),
            int(1),
            int(0),
            int(-1),
            Value::None,
            Value::None,
            int(0),
            Value::None,
        ],
    )
}
