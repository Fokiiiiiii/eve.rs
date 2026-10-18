use super::*;
use crate::locators::{CAPITAL, LARGE, SUPERCAPITAL, category_of};
use crate::modules::DISALLOW_DOCKING;

impl Scene {
    /// Puts a ship in space at the station it undocks from, moving away at its top speed.
    pub fn undock(
        &mut self,
        session: SessionId,
        spec: ShipSpec,
        station_id: i64,
        now: i64,
    ) -> Result<Vec<Out>, SceneError> {
        if self.pilots.contains_key(&session) {
            return Err(SceneError::AlreadyInSpace(session));
        }
        let station = self
            .system
            .body(station_id)
            .filter(|b| is_dock_body(b))
            .ok_or(SceneError::UnknownStation(station_id))?;
        let (position, direction) = undock_place(station, spec.group_id);
        let speed = spec.max_velocity;
        let modules = Modules::with_charge(
            spec.capacitor_capacity,
            spec.capacitor_recharge_s,
            spec.condition.charge,
            now,
        );
        let velocity = Vec3::new(direction.x * speed, direction.y * speed, direction.z * speed);
        let ship = Ship::new(spec, position, velocity, direction, 1.0, Course::Goto, now);
        let entered = self.next_entry();
        let mut pilot = Pilot::new(ship, modules, entered, now, now + UNDOCK_PROTECTION);
        pilot.leaving_station = Some(station_id);
        self.pilots.insert(session, pilot);
        Ok(self.announce_arrival(session, now))
    }

    /// Restores a pilot whose ship is already in space at its saved position.
    pub fn enter_in_space(
        &mut self,
        session: SessionId,
        spec: ShipSpec,
        position: [f64; 3],
        direction: [f64; 3],
        now: i64,
    ) -> Result<Vec<Out>, SceneError> {
        if self.pilots.contains_key(&session) {
            return Err(SceneError::AlreadyInSpace(session));
        }
        let position = Vec3::new(position[0], position[1], position[2]);
        let direction = Vec3::new(direction[0], direction[1], direction[2]);
        let modules = Modules::with_charge(
            spec.capacitor_capacity,
            spec.capacitor_recharge_s,
            spec.condition.charge,
            now,
        );
        let ship = Ship::new(spec, position, Vec3::new(0.0, 0.0, 0.0), direction, 0.0, Course::Stop, now);
        let entered = self.next_entry();
        let mut pilot = Pilot::new(ship, modules, entered, now, now);
        pilot.jumped_in = true;
        self.pilots.insert(session, pilot);
        Ok(self.announce_arrival(session, now))
    }

    /// Puts a ship in space at the anchor of its system, where a game master's relocation brings a pilot (EVE.js `buildSolarSystemSpawnState`): off the
    /// system's first stargate, else its first station, else its first body, on the x axis past the anchor's surface by the ship's radius and
    /// `ANCHOR_CLEARANCE`, facing the anchor. A system with no body is entered at 1 000 000 m along x.
    pub fn enter_at_anchor(
        &mut self,
        session: SessionId,
        spec: ShipSpec,
        now: i64,
    ) -> Result<Vec<Out>, SceneError> {
        let anchor = self
            .system
            .bodies
            .iter()
            .find(|body| body.kind == BodyKind::Stargate)
            .or_else(|| self.system.bodies.iter().find(|body| is_dock_body(body)))
            .or_else(|| self.system.bodies.first());
        let (position, direction) = match anchor {
            Some(body) => {
                let offset = body.radius + spec.radius + ANCHOR_CLEARANCE;
                ([body.position.x + offset, body.position.y, body.position.z], [-1.0, 0.0, 0.0])
            }
            None => ([1_000_000.0, 0.0, 0.0], [1.0, 0.0, 0.0]),
        };
        self.enter_in_space(session, spec, position, direction, now)
    }

    pub fn leave(&mut self, session: SessionId, why: Departure, now: i64) -> Option<Vec<Out>> {
        let mut out = self.clear_tether_policy(session, now);
        if let Some(structure_id) = self.controlled_structure_id(session) {
            out.extend(self.teardown_controlled_structure(session, structure_id, now));
            self.controlled_structures.remove(&session);
            if !matches!(why, Departure::Ejected | Departure::Boarded) {
                self.widowed.remove(&session);
            }
            return Some(out);
        }
        let mut pilot = self.pilots.remove(&session)?;
        if let Some(brains) = self.brains.as_mut() {
            brains.forget(session);
        }
        let ship_id = pilot.ship.spec.ship_id;
        out.extend(self.drop_targeting(ship_id, &pilot.targeting, now));
        let logoff_at = matches!(why, Departure::LoggedOff).then_some(pilot.ship.position);
        out.extend(self.release_drones(session, logoff_at, now));

        if matches!(why, Departure::Ejected | Departure::Boarded) {
            // Preserve the client's ballpark while its active ship changes.
            self.widowed.insert(
                session,
                Widow {
                    bound: pilot.bound,
                    sees: pilot.sees.clone(),
                    sees_wrecks: pilot.sees_wrecks.clone(),
                    sees_abyssal_traces: pilot.sees_abyssal_traces.clone(),
                    sees_asteroids: pilot.sees_asteroids.clone(),
                },
            );
            // An ejected hull (or the capsule left when boarding) remains a moving,
            // targetable ship. Give it a simulation-only session until it is boarded.
            let fake_session = (1_u64 << 61).saturating_add(self.next_entry());
            pilot.bound = false;
            pilot.sees.clear();
            pilot.sees_wrecks.clear();
            pilot.sees_abyssal_traces.clear();
            pilot.sees_asteroids.clear();
            self.unpiloted.insert(ship_id, fake_session);
            self.pilots.insert(fake_session, pilot);
            out.extend(self.announce_departure(session, ship_id, now));
            out.extend(self.announce_arrival(fake_session, now));
            return Some(out);
        }

        out.extend(self.announce_departure(session, ship_id, now));
        Some(out)
    }

    pub(super) fn dock(&mut self, session: SessionId, station_id: i64, now: i64) -> Vec<Out> {
        let code = Command::Dock { station_id }.code();
        let Some(station) = self.system.body(station_id).filter(|b| is_dock_body(b)).cloned() else {
            return vec![Out::Refused { session, command: code, reason: "STATION_NOT_FOUND" }];
        };
        let pilot = self.pilots.get(&session).expect("the caller checked");
        if let Some(pending) = pilot.docking.as_ref().filter(|p| p.station_id == station_id) {
            return vec![Out::Accepted { session, command: code, at: pending.accepted_at }];
        }
        // a doomsday's windows hold the ship: a held ship follows no orders (IMMOBILE), and a ship in its no-jump window does not dock either (the
        // reference's disallowDocking on the running module: the module runs for that window)
        if pilot.immobile_until > now {
            return vec![Out::Refused { session, command: code, reason: "IMMOBILE" }];
        }
        if pilot.no_jump_until > now {
            return vec![Out::Refused { session, command: code, reason: "DOCKING_BLOCKED" }];
        }
        if pilot.disrupted_until > now {
            return vec![Out::Refused { session, command: code, reason: "DOCKING_DISRUPTED" }];
        }
        // a running module that disallows docking (disallowDocking 2354: a siege, a triage, a sensor array) keeps the ship from the station
        if self.overlay(session, pilot.ship.spec.ship_id, DISALLOW_DOCKING) > 0.0 {
            return vec![Out::Refused { session, command: code, reason: "DOCKING_DISALLOWED_BY_MODULE" }];
        }
        if pilot.jump.is_some() {
            return vec![Out::Refused { session, command: code, reason: "JUMPING" }];
        }
        if pilot.flight.is_some() {
            return vec![Out::Refused { session, command: code, reason: "IN_WARP" }];
        }
        if let Some(reason) = dock_refusal(&station, pilot.ship.spec.group_id) {
            return vec![Out::Refused { session, command: code, reason }];
        }
        let distance = surface_distance(&pilot.ship, station.position, station.radius);
        if distance > DOCKING_RANGE {
            return self.approach(session, station_id, distance, now);
        }
        let mut out = self.accept_docking(session, station_id, now);
        out.push(Out::Accepted { session, command: code, at: now });
        out
    }

    /// The ship is in range and the docking begins: the client is told, and the ship stops and waits for the docking to take its time.
    pub(super) fn accept_docking(&mut self, session: SessionId, station_id: i64, now: i64) -> Vec<Out> {
        let stamp = self.next_stamp(now);
        let pilot = self.pilots.get_mut(&session).expect("the caller checked");
        let ship = &mut pilot.ship;
        ship.speed_fraction = 0.0;
        ship.course = Course::Stop;
        pilot.docking = Some(PendingDock { station_id, accepted_at: now });
        let ship_id = id(ship.spec.ship_id);
        let mut out = vec![Self::notify_charid(session, "OnDockingAccepted", vec![id(station_id)])];
        out.extend(self.tell_all(
            session,
            vec![
                payload::action(stamp, "SetBallMassive", [ship_id.clone(), int(0)]),
                payload::action(stamp, "Stop", [ship_id]),
            ],
        ));
        out
    }

    /// A docking asked for from too far: the ship goes to the station on its own (following it to the docking range) and the host is told how far
    /// off it was, for the client's message. As in the reference the docking is not done on arrival: the pilot asks again.
    pub(super) fn approach(
        &mut self,
        session: SessionId,
        station_id: i64,
        distance: f64,
        now: i64,
    ) -> Vec<Out> {
        let code = Command::Dock { station_id }.code();
        let follow = Command::FollowBall { target_id: station_id, range: DOCKING_RANGE };
        let mut out = self.steer_pilot(session, follow, now);
        out.retain(|o| !matches!(o, Out::Accepted { .. }));
        out.push(Out::Approaching { session, command: code, distance });
        out
    }
}

/// Where a ship comes out of a station and which way it faces: the station type's undock point that suits the ship, turned by the station's
/// orientation (the reference's own table); a type it does not know puts the ship a third of the station's radius from it, out along the
/// station's orientation or away from the star.
pub(super) fn undock_place(station: &crate::world::Body, ship_group: i32) -> (Vec3, Vec3) {
    let rotation = crate::slim::station_rotation(station.id).unwrap_or([0.0; 3]);
    if let Some(point) = crate::locators::undock_point(station.type_id, ship_group, rotation) {
        let at = Vec3::new(
            station.position.x + point.offset.x,
            station.position.y + point.offset.y,
            station.position.z + point.offset.z,
        );
        return (at, motion::unit(point.direction).unwrap_or(point.direction));
    }
    let direction = undock_direction(station.id, station.position);
    let distance = UNDOCK_MIN_DISTANCE.max(station.radius * UNDOCK_RADIUS_SHARE);
    let at = Vec3::new(
        station.position.x + direction.x * distance,
        station.position.y + direction.y * distance,
        station.position.z + direction.z * distance,
    );
    (at, direction)
}

/// Which way a ship leaves a station with no known undock point: the station's own orientation when it has one, otherwise away from the star.
pub(super) fn undock_direction(station_id: i64, position: Vec3) -> Vec3 {
    if let Some(angles) = crate::slim::station_rotation(station_id) {
        let (yaw, pitch) = (angles[0].to_radians(), angles[1].to_radians());
        return Vec3::new(yaw.sin() * pitch.cos(), -pitch.sin(), yaw.cos() * pitch.cos());
    }
    motion::unit(position).unwrap_or(Vec3::new(0.0, 0.0, 1.0))
}

pub(super) fn surface_distance(ship: &Ship, station: Vec3, station_radius: f64) -> f64 {
    let (dx, dy, dz) =
        (ship.position.x - station.x, ship.position.y - station.y, ship.position.z - station.z);
    ((dx * dx + dy * dy + dz * dz).sqrt() - ship.spec.radius - station_radius).max(0.0)
}

/// The states of an Upwell structure in which it takes no ship (EVE.js `STRUCTURE_DISABLED_STATES`, `structureConstants.js`): unknown, unanchored,
/// anchoring, onlining, hull reinforce and hull vulnerable, anchor and deploy vulnerable.
const STRUCTURE_DISABLED_STATES: [i32; 8] = [0, 1, 2, 102, 113, 114, 115, 116];

/// How far past the surface of its anchor body a ship that is put in space at the anchor is placed, metres (the clearance of EVE.js's spawn state).
const ANCHOR_CLEARANCE: f64 = 5_000.0;

/// Whether a body is one a ship docks at or undocks from: a station, or an Upwell structure (ST-1 c).
pub(super) fn is_dock_body(body: &crate::world::Body) -> bool {
    matches!(body.kind, BodyKind::Station | BodyKind::Structure)
}

/// The size of a hull as the undock points name it: 0 small, 1 large (a battleship is a subcapital), 2 capital, 3 supercapital.
fn hull_size(ship_group: i32) -> u8 {
    match category_of(ship_group) {
        SUPERCAPITAL => 3,
        CAPITAL => 2,
        LARGE => 1,
        _ => 0,
    }
}

/// The largest hull a structure type takes in its docks (the wiki's "Max docking": Astrahus subcapitals, Fortizar capitals, Keepstar supercapitals).
/// Other types are taken as subcapital only; their own limits are not in the sim yet (DEVIATIONS "Structure docking (ST-1 c)").
fn largest_hull_docked(structure_type: i32) -> u8 {
    match structure_type {
        35_834 => 3,
        35_833 => 2,
        _ => 1,
    }
}

/// Why a ship of `ship_group` may not dock at `body`: a structure in a state that takes no ship, or one whose docks do not take the ship's hull.
/// None when it may. A station is judged by its own rules (`locators::can_dock`), which the docking does not apply yet.
pub(super) fn dock_refusal(body: &crate::world::Body, ship_group: i32) -> Option<&'static str> {
    let Detail::Structure { status, .. } = &body.detail else { return None };
    if STRUCTURE_DISABLED_STATES.contains(&status.state) {
        return Some("STRUCTURE_DISABLED");
    }
    (hull_size(ship_group) > largest_hull_docked(body.type_id)).then_some("SHIP_TOO_LARGE")
}
