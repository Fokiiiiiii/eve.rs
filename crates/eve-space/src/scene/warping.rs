use super::*;
use crate::modules::WARP_SCRAMBLE_STATUS;

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
        self.begin_warp_min(session, code, target_id, raw, stop, now, warp::MINIMUM_WARP_DISTANCE)
    }

    /// A warp that must cover at least `minimum` metres. A player's warp covers the minimum; a dungeon's gate transit (a teleport into the room of the same site) does not.
    #[allow(clippy::too_many_arguments)] // the warp's own arguments (the code, the target and the point) plus the minimum
    pub(super) fn begin_warp_min(
        &mut self,
        session: SessionId,
        code: u8,
        target_id: Option<i64>,
        raw: Vec3,
        stop: f64,
        now: i64,
        minimum: f64,
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
        // a warp core stabilizer takes its scramble status off the ship's own (`Action::ScrambleStatus`): a point it cancels is not a scramble
        if pilot.incoming.scrambled_with(self.scramble_status(session)) {
            return refused("WARP_SCRAMBLED");
        }
        // a running module of the ship that scrambles it (warpScrambleStatus 104: a siege, a triage, a sensor array) holds it in place
        if self.overlay(session, pilot.ship.spec.ship_id, WARP_SCRAMBLE_STATUS) > 0.0 {
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
        if total < minimum {
            return refused("WARP_DISTANCE_TOO_CLOSE");
        }
        // the reference's order: a bubble holds the ship unless it is immune to bubbles (the bubble is tested where the ship is), then a cloak that is not
        // warp-safe (a standard cloaking device) holds it until it decloaks
        if !self.warp_immune(session) && self.warp_disrupted_at(ship.position, now) {
            return refused("WARP_DISRUPTED_BY_BUBBLE");
        }
        if self.cloak_blocks_warp(session) {
            return refused("WARP_BLOCKED_BY_CLOAK");
        }

        let profile = warp::Profile::new(total, ship.spec.warp_speed_au, ship.spec.max_velocity);
        let ship_id = id(ship.spec.ship_id);
        let boosted = ship.spec.max_velocity * warp::START_SPEED_SCALE;
        let mut out = self.break_tether(session, now);
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
        out.extend(self.tell_split(session, own, watchers));
        out.push(Out::Accepted { session, command: code, at: now });
        out
    }

    /// Whether the ship of `session` is immune to warp bubbles: an interdiction nullifier runs (`Action::WarpImmunity`).
    pub(crate) fn warp_immune(&self, session: SessionId) -> bool {
        self.running_actions(session).any(|action| matches!(action, crate::modules::Action::WarpImmunity))
    }

    /// The change the running modules of `session` make to its warp scramble status (a warp core stabilizer's -2): what the scrambles on the ship add to.
    pub(crate) fn scramble_status(&self, session: SessionId) -> f64 {
        self.running_actions(session)
            .map(|action| match action {
                crate::modules::Action::ScrambleStatus { delta } => *delta,
                _ => 0.0,
            })
            .sum()
    }

    /// Whether a cloaking device that is not warp-safe runs on the ship of `session`: such a cloak holds the ship out of warp until it decloaks.
    pub(crate) fn cloak_blocks_warp(&self, session: SessionId) -> bool {
        self.running_actions(session)
            .any(|action| matches!(action, crate::modules::Action::Cloak { warp_safe: false, .. }))
    }

    /// The actions of the effects that run on the ship of `session` (a state of the ship while they run, not a cycle).
    fn running_actions(&self, session: SessionId) -> impl Iterator<Item = &crate::modules::Action> + '_ {
        self.pilots
            .get(&session)
            .into_iter()
            .flat_map(|pilot| pilot.modules.running.iter())
            .filter_map(|run| run.effect.action.as_ref())
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

        // a dungeon room's acceleration gate is a deployable ball of the scene: a warp to it ends at the range from its centre (the gate's radius is its
        // reach for a transit, not a stop: a stop a radius away would leave the pilot outside the complex's presence)
        if let Some(gate) = self
            .deployables
            .get(&target_id)
            .filter(|gate| gate.role == crate::deployable::Role::AccelerationGate)
        {
            return Some((gate.position, range.max(0.0) + pilot.ship.spec.radius));
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
