//! The jump bridges and the conduit jumps (EVE University wiki, "Jump drives"). A Titan's Jump Portal Generator, a Black Ops' Covert one or a Rorqual's
//! Industrial one opens a bridge where the ship is, to a cynosural field lit in another system; the fleet ships within the bridge's range of it jump through
//! to the field (command 31). A Black Ops with its Covert generator fitted makes a conduit jump instead (command 32): the ship and the fleet ships in its
//! 10 km jump with it at once. Command 30 opens a bridge. The host has checked the module, the fleet, the range, the fuel and the passengers' eligibility.
use super::*;

/// The conduit's reach: the ships within 10 km of the anchor go with it (EveJS conduitJumpRuntime.js CONDUIT_JUMP_RANGE_METERS; the wiki: a 10 km radius).
pub(crate) const CONDUIT_RANGE_METERS: f64 = 10_000.0;

/// The reason a ship cannot start a jump now, by the rules the stargate and cynosural jumps share (immobile, cloaked, disrupted, spooling, docking, in warp).
fn jump_block(pilot: &Pilot, now: i64) -> Option<&'static str> {
    if pilot.immobile_until > now {
        Some("IMMOBILE")
    } else if pilot.no_jump_until > now {
        Some("NO_JUMP_OR_CLOAK")
    } else if pilot.disrupted_until > now {
        Some("JUMP_DISRUPTED")
    } else if pilot.jump.is_some() {
        Some("STARGATE_JUMP_IN_PROGRESS")
    } else if pilot.docking.is_some() {
        Some("DOCKING")
    } else if pilot.flight.is_some() {
        Some("IN_WARP")
    } else {
        None
    }
}

fn ticks_of(milliseconds: f64) -> i64 {
    (milliseconds * TICKS_PER_SECOND as f64 / 1000.0).round() as i64
}

fn range_between(a: Vec3, b: Vec3) -> f64 {
    ((a.x - b.x).powi(2) + (a.y - b.y).powi(2) + (a.z - b.z).powi(2)).sqrt()
}

impl Scene {
    /// `CmdBridgeToFleetModuleBeacon` (command 30): the ship opens a bridge where it is, to the field `field_id` lit in `destination_system`. The bridge lives
    /// for `lifetime_ms` (the module's duration), the ship holds still for `immobile_ms` (zero when the reference does not hold it: a covert bridge), and a
    /// fleet ship within `range_meters` of the bridge may pass through it. The activation takes `capacitor` GJ of the charge; a warp bubble over the ship refuses it.
    pub(super) fn open_bridge(
        &mut self,
        session: SessionId,
        field_id: i64,
        destination_system: i32,
        (lifetime_ms, immobile_ms, range_meters): (f64, f64, f64),
        capacitor: f64,
        now: i64,
    ) -> Vec<Out> {
        let code = Command::OpenBridge {
            field_id,
            destination_system,
            lifetime_ms,
            immobile_ms,
            range_meters,
            capacitor,
        }
        .code();
        let refused = |reason: &'static str| vec![Out::Refused { session, command: code, reason }];
        let Some(pilot) = self.pilots.get(&session) else { return refused("NO_SHIP_IN_SPACE") };
        if let Some(reason) = jump_block(pilot, now) {
            return refused(reason);
        }
        if destination_system == self.system.id {
            return refused("WRONG_SOLAR_SYSTEM");
        }
        if self.warp_disrupted_at(pilot.ship.position, now) {
            return refused("WARP_DISRUPTED_BY_BUBBLE");
        }
        let Some(store) = pilot.modules.capacitor.as_ref() else { return refused("NO_CAPACITOR") };
        if store.charge_at(now) < capacitor {
            return refused("NOT_ENOUGH_CAPACITOR");
        }
        if !self.place_bridge(session, field_id, destination_system, lifetime_ms, range_meters, now) {
            return refused("NO_SHIP_IN_SPACE");
        }
        let pilot = self.pilots.get_mut(&session).expect("checked above");
        let (before, after) = match pilot.modules.capacitor.as_mut() {
            Some(store) => store.draw(now, capacitor),
            None => (0.0, 0.0),
        };
        let (owner, ship_no) = (pilot.ship.spec.character_id, pilot.ship.spec.ship_id);
        if immobile_ms > 0.0 {
            pilot.immobile_until = now + ticks_of(immobile_ms);
            let ship = &mut pilot.ship;
            ship.course = Course::Stop;
            ship.speed_fraction = 0.0;
            ship.velocity = Vec3::ZERO;
        }
        vec![
            Self::charge_told(session, owner, ship_no, now, after, before),
            Out::Accepted { session, command: code, at: now },
        ]
    }

    /// `CmdJumpThroughFleet` (command 31): the ship of `session` passes through the bridge of the ship `bridge_ship_id` to the field `field_id` in
    /// `destination_system`. It must be within the bridge's range, not inside a warp bubble, and the bridge must be open. The ship leaves at once and appears
    /// by the field (the same arrival as a cynosural jump).
    pub(super) fn bridge_jump(
        &mut self,
        session: SessionId,
        bridge_ship_id: i64,
        field_id: i64,
        destination_system: i32,
        now: i64,
    ) -> Vec<Out> {
        let code = Command::BridgeJump { bridge_ship_id, field_id, destination_system }.code();
        let refused = |reason: &'static str| vec![Out::Refused { session, command: code, reason }];
        let Some(pilot) = self.pilots.get(&session) else { return refused("NO_SHIP_IN_SPACE") };
        if let Some(reason) = jump_block(pilot, now) {
            return refused(reason);
        }
        if destination_system == self.system.id {
            return refused("WRONG_SOLAR_SYSTEM");
        }
        if self.warp_disrupted_at(pilot.ship.position, now) {
            return refused("WARP_DISRUPTED_BY_BUBBLE");
        }
        let Some((bridge_at, range)) = self.bridge_of(bridge_ship_id, field_id, destination_system, now)
        else {
            return refused("BRIDGE_NOT_FOUND");
        };
        if range_between(pilot.ship.position, bridge_at) > range {
            return refused("OUT_OF_BRIDGE_RANGE");
        }
        let stamp = self.next_stamp(now);
        self.leave_for_field(session, field_id, destination_system, code, stamp, now)
    }

    /// `CmdGroupJumpToFleetModuleBeacon` (command 32): the Black Ops of `session` makes a conduit jump to the field `field_id` in `destination_system`, and
    /// the `passengers` (ship ids) jump with it at once. Every passenger must be in the conduit's 10 km, not in a bubble and ready to jump; one that is not
    /// refuses the whole jump (the host has filtered the fleet by the same rules, so this is the simulation's own check).
    pub(super) fn conduit_jump(
        &mut self,
        session: SessionId,
        field_id: i64,
        destination_system: i32,
        passengers: &[i64],
        now: i64,
    ) -> Vec<Out> {
        let code = Command::ConduitJump {
            field_id,
            destination_system,
            range_meters: CONDUIT_RANGE_METERS,
            passenger_count: passengers.len() as u8,
            passengers: conduit_passengers(passengers),
        }
        .code();
        let refused = |reason: &'static str| vec![Out::Refused { session, command: code, reason }];
        if passengers.len() > MAX_CONDUIT_PASSENGERS {
            return refused("TOO_MANY_PASSENGERS");
        }
        let Some(anchor) = self.pilots.get(&session) else { return refused("NO_SHIP_IN_SPACE") };
        if let Some(reason) = jump_block(anchor, now) {
            return refused(reason);
        }
        if destination_system == self.system.id {
            return refused("WRONG_SOLAR_SYSTEM");
        }
        if self.warp_disrupted_at(anchor.ship.position, now) {
            return refused("WARP_DISRUPTED_BY_BUBBLE");
        }
        let mut ships: Vec<SessionId> = Vec::new();
        for ship_id in passengers {
            let Some((&passenger, pilot)) =
                self.pilots.iter().find(|(s, p)| **s != session && p.ship.spec.ship_id == *ship_id)
            else {
                return refused("PASSENGER_NOT_IN_SPACE");
            };
            if ships.contains(&passenger) {
                return refused("DUPLICATE_PASSENGER");
            }
            if let Some(reason) = jump_block(pilot, now) {
                return refused(reason);
            }
            if self.warp_disrupted_at(pilot.ship.position, now) {
                return refused("WARP_DISRUPTED_BY_BUBBLE");
            }
            if range_between(anchor.ship.position, pilot.ship.position) > CONDUIT_RANGE_METERS {
                return refused("OUT_OF_CONDUIT_RANGE");
            }
            ships.push(passenger);
        }
        let stamp = self.next_stamp(now);
        let mut out = self.leave_for_field(session, field_id, destination_system, code, stamp, now);
        if out.iter().any(|o| matches!(o, Out::Refused { .. })) {
            return out;
        }
        for passenger in ships {
            out.extend(self.leave_for_field(passenger, field_id, destination_system, code, stamp, now));
        }
        out
    }

    /// The bridge of the ship `bridge_ship_id` to the field `field_id` in `destination_system`, open now: where it is and its range.
    fn bridge_of(
        &self,
        bridge_ship_id: i64,
        field_id: i64,
        destination_system: i32,
        now: i64,
    ) -> Option<(Vec3, f64)> {
        self.deployables.values().find_map(|d| match d.role {
            crate::deployable::Role::JumpBridge { field_id: f, destination_system: s, range_meters }
                if d.ship_id == bridge_ship_id
                    && d.ends_at > now
                    && f == field_id
                    && s == destination_system =>
            {
                Some((d.position, range_meters))
            }
            _ => None,
        })
    }

    /// The ship of `session` stops and leaves for the field at `stamp`; the jump-out effect is told as a cynosural jump's, and the answer is the command's.
    fn leave_for_field(
        &mut self,
        session: SessionId,
        field_id: i64,
        destination_system: i32,
        code: u8,
        stamp: u32,
        now: i64,
    ) -> Vec<Out> {
        let mut out = self.clear_tether_policy(session, now);
        let pilot = self.pilots.get_mut(&session).expect("checked by the caller");
        let ship = &mut pilot.ship;
        if !matches!(ship.course, Course::Stop) || ship.speed_fraction > 0.0 || speed_of(ship.velocity) > 0.5
        {
            ship.course = Course::Stop;
            ship.speed_fraction = 0.0;
            ship.velocity = Vec3::ZERO;
        }
        pilot.jump = Some(PendingJump {
            gate_id: 0,
            destination_system,
            destination_gate: 0,
            field_id: Some(field_id),
            far_end: None,
            leaves_at: now,
        });
        let ship_id = id(ship.spec.ship_id);
        let actions = [payload::action(stamp, "Stop", [ship_id])];
        out.extend(self.tell_all(session, actions.into()));
        out.push(Out::Jumping { session, command: code, destination_system, destination_gate: 0 });
        out
    }
}

/// The passenger ship ids as the command carries them: the first `passengers.len()` slots, the rest zero.
fn conduit_passengers(passengers: &[i64]) -> [i64; MAX_CONDUIT_PASSENGERS] {
    let mut slots = [0; MAX_CONDUIT_PASSENGERS];
    for (slot, ship) in slots.iter_mut().zip(passengers) {
        *slot = *ship;
    }
    slots
}
