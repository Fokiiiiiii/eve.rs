use super::*;

impl Scene {
    /// Applies a completed MJD translation along the heading at spool end; a space bump may change the result.
    pub(crate) fn perform_micro_jump(
        &mut self,
        session: SessionId,
        distance_meters: f64,
        now: i64,
    ) -> Vec<Out> {
        if !distance_meters.is_finite() || distance_meters <= 0.0 {
            return Vec::new();
        }
        self.settle_pilot_after(session, now, 0);
        {
            let Some(pilot) = self.pilots.get_mut(&session) else { return Vec::new() };
            let ship = &mut pilot.ship;
            let Some(direction) = motion::unit(ship.direction) else { return Vec::new() };
            ship.position = Vec3::new(
                ship.position.x + direction.x * distance_meters,
                ship.position.y + direction.y * distance_meters,
                ship.position.z + direction.z * distance_meters,
            );
        }
        self.resolve_collisions(now, Some(session), None);
        self.update_state(session, now)
    }

    /// `CmdStargateJump`: the ship stops where it is and the jump-out effect begins. The ship leaves for the other system a few seconds on
    /// (the later of four seconds from now and three from the effect's stamp, the reference's rule), which [`Scene::due_jumps`] finds.
    pub(super) fn jump(
        &mut self,
        session: SessionId,
        gate_id: i64,
        destination_gate: i64,
        now: i64,
    ) -> Vec<Out> {
        let code = Command::Jump { gate_id, destination_gate }.code();
        let refused = |reason: &'static str| vec![Out::Refused { session, command: code, reason }];
        let system = Arc::clone(&self.system);
        let Some(pilot) = self.pilots.get(&session) else { return refused("NO_SHIP_IN_SPACE") };
        if pilot.immobile_until > now {
            return refused("IMMOBILE");
        }
        if pilot.no_jump_until > now {
            return refused("NO_JUMP_OR_CLOAK");
        }
        if pilot.disrupted_until > now {
            return refused("JUMP_DISRUPTED");
        }
        if pilot.jump.is_some() {
            return refused("STARGATE_JUMP_IN_PROGRESS");
        }
        if pilot.docking.is_some() {
            return refused("DOCKING");
        }
        if pilot.flight.is_some() {
            return refused("IN_WARP");
        }
        let Some((gate, Detail::Stargate { destination_system, destination_gate: leads_to, .. })) =
            system.body(gate_id).filter(|b| b.kind == BodyKind::Stargate).map(|b| (b, &b.detail))
        else {
            return refused("STARGATE_NOT_FOUND");
        };
        if destination_gate != 0 && destination_gate != *leads_to {
            return refused("STARGATE_DESTINATION_MISMATCH");
        }
        if gate::surface_distance(gate, pilot.ship.position, pilot.ship.spec.radius) > gate::JUMP_RANGE {
            return refused("TOO_FAR_FROM_STARGATE");
        }

        let stamp = self.next_stamp(now);
        let boundary = if stamp > stamp_at(now) {
            (i64::from(stamp) + UNIX_EPOCH_SECONDS) * TICKS_PER_SECOND
        } else {
            now
        };
        let leaves_at = (now + JUMP_DELAY).max(boundary + JUMP_EFFECT_DELAY);
        let pilot = self.pilots.get_mut(&session).expect("checked above");
        let ship = &mut pilot.ship;
        if !matches!(ship.course, Course::Stop) || ship.speed_fraction > 0.0 || speed_of(ship.velocity) > 0.5
        {
            ship.course = Course::Stop;
            ship.speed_fraction = 0.0;
            ship.velocity = Vec3::ZERO;
        }
        pilot.jump = Some(PendingJump {
            gate_id,
            destination_system: *destination_system,
            destination_gate: *leads_to,
            field_id: None,
            leaves_at,
        });
        let ship_id = id(ship.spec.ship_id);
        let actions = [
            payload::action(
                stamp,
                "OnSpecialFX",
                [
                    ship_id.clone(),
                    Value::None,
                    Value::None,
                    id(gate_id),
                    Value::None,
                    text("effects.JumpOut"),
                    int(0),
                    int(1),
                    int(0),
                    int(-1),
                    Value::None,
                    Value::None,
                    int(0),
                    tuple([int(i64::from(*destination_system)), Value::None]),
                ],
            ),
            payload::action(stamp, "Stop", [ship_id]),
        ];
        let mut out = self.tell_all(session, actions.into());
        out.push(Out::Jumping {
            session,
            command: code,
            destination_system: *destination_system,
            destination_gate: *leads_to,
        });
        out
    }

    /// `CmdJumpToFleetModuleBeacon` and `CmdJumpToFleetDeployableBeacon` (command 26): the ship's jump drive spools for `spool_ms`, and the ship then leaves
    /// for `destination_system` to appear at the cynosural field `field_id` lit there. The stop and the jump-out effect are the stargate jump's; the checks are
    /// its checks, without the gate. The host has checked the field, the fleet, the range, the fuel and the drive's cooldown. The activation takes
    /// `capacitor_fraction` of the full capacitor when the charge holds it (the wiki: 95 %, less the pilot's Jump Drive Operation).
    pub(super) fn cyno_jump(
        &mut self,
        session: SessionId,
        field_id: i64,
        destination_system: i32,
        spool_ms: f64,
        capacitor_fraction: f64,
        now: i64,
    ) -> Vec<Out> {
        let code =
            Command::CynoJump { field_id, destination_system, spool_ms, capacitor: capacitor_fraction }
                .code();
        let refused = |reason: &'static str| vec![Out::Refused { session, command: code, reason }];
        let Some(pilot) = self.pilots.get(&session) else { return refused("NO_SHIP_IN_SPACE") };
        if pilot.immobile_until > now {
            return refused("IMMOBILE");
        }
        if pilot.no_jump_until > now {
            return refused("NO_JUMP_OR_CLOAK");
        }
        if pilot.disrupted_until > now {
            return refused("JUMP_DISRUPTED");
        }
        if pilot.jump.is_some() {
            return refused("STARGATE_JUMP_IN_PROGRESS");
        }
        if pilot.docking.is_some() {
            return refused("DOCKING");
        }
        if pilot.flight.is_some() {
            return refused("IN_WARP");
        }
        if destination_system == self.system.id {
            return refused("WRONG_SOLAR_SYSTEM");
        }
        if self.warp_disrupted_at(pilot.ship.position, now) {
            return refused("WARP_DISRUPTED_BY_BUBBLE");
        }
        let Some(capacitor) = pilot.modules.capacitor.as_ref() else { return refused("NO_CAPACITOR") };
        let need = capacitor_fraction * capacitor.capacity();
        if capacitor.charge_at(now) < need {
            return refused("NOT_ENOUGH_CAPACITOR");
        }
        let stamp = self.next_stamp(now);
        let pilot = self.pilots.get_mut(&session).expect("checked above");
        let (before, after) = match pilot.modules.capacitor.as_mut() {
            Some(capacitor) => capacitor.draw(now, need),
            None => (0.0, 0.0),
        };
        let (owner, ship_no) = (pilot.ship.spec.character_id, pilot.ship.spec.ship_id);
        let ship = &mut pilot.ship;
        if !matches!(ship.course, Course::Stop) || ship.speed_fraction > 0.0 || speed_of(ship.velocity) > 0.5
        {
            ship.course = Course::Stop;
            ship.speed_fraction = 0.0;
            ship.velocity = Vec3::ZERO;
        }
        let leaves_at = now + (spool_ms * TICKS_PER_SECOND as f64 / 1000.0).round() as i64;
        pilot.jump = Some(PendingJump {
            gate_id: 0,
            destination_system,
            destination_gate: 0,
            field_id: Some(field_id),
            leaves_at,
        });
        let ship_id = id(ship.spec.ship_id);
        let actions = [
            payload::action(
                stamp,
                "OnSpecialFX",
                [
                    ship_id.clone(),
                    Value::None,
                    Value::None,
                    id(field_id),
                    Value::None,
                    text("effects.JumpDriveOut"),
                    int(0),
                    int(1),
                    int(0),
                    int(-1),
                    Value::None,
                    Value::None,
                    int(0),
                    tuple([int(i64::from(destination_system)), Value::None]),
                ],
            ),
            payload::action(stamp, "Stop", [ship_id]),
        ];
        let mut out = vec![Self::charge_told(session, owner, ship_no, now, after, before)];
        out.extend(self.tell_all(session, actions.into()));
        out.push(Out::Jumping { session, command: code, destination_system, destination_gate: 0 });
        out
    }

    /// The ships whose jump is due at `now`, and what the ones who watched them go are told. Each ship has left this scene; the caller puts it in
    /// the scene of the system it is going to ([`Scene::arrive`]), or back ([`Scene::restore`]) when that cannot be done.
    pub fn due_jumps(&mut self, now: i64) -> (Vec<Traveller>, Vec<Out>) {
        let due: Vec<SessionId> = self
            .pilots
            .iter()
            .filter(|(_, pilot)| pilot.jump.as_ref().is_some_and(|jump| now >= jump.leaves_at))
            .map(|(session, _)| *session)
            .collect();
        let mut travellers = Vec::new();
        let mut told = Vec::new();
        for session in due {
            self.settle_pilot(session, now);
            // the jump stops what the ship runs before it leaves (EVE.js stops every active module at a stargate jump)
            told.extend(self.stop_all_modules(session, now));
            let Some(mut pilot) = self.pilots.remove(&session) else { continue };
            let Some(jump) = pilot.jump.take() else { continue };
            pilot.clear_overheat_for_jump();
            let held = std::mem::take(&mut pilot.targeting);
            told.extend(self.drop_targeting(pilot.ship.spec.ship_id, &held, now));
            told.extend(self.announce_departure(session, pilot.ship.spec.ship_id, now));
            travellers.push(Traveller {
                session,
                from_system: self.system.id,
                from_gate: jump.gate_id,
                to_system: jump.destination_system,
                to_gate: jump.destination_gate,
                to_field: jump.field_id,
                pilot,
            });
        }
        (travellers, told)
    }

    /// A ship that has jumped appears in front of the gate it came out of, at rest, and is bound like any ship that arrives (the client binds
    /// the ballpark of the new system when its session has changed). Gives the ship back when this system has no such gate.
    pub fn arrive(&mut self, traveller: Traveller, now: i64) -> Result<Vec<Out>, Box<Traveller>> {
        let system = Arc::clone(&self.system);
        // a ship that jumped to a field appears by the field while it is lit here; one that came through a gate, by that gate
        let (position, direction) = if let Some(field_id) = traveller.to_field {
            let Some(field) = self
                .deployables
                .get(&field_id)
                .filter(|field| field.role == crate::deployable::Role::CynoField && field.ends_at > now)
            else {
                return Err(Box::new(traveller));
            };
            let mut random = PythonRandom::new(seed_of(traveller.pilot.ship.spec.ship_id, field_id, now));
            cyno_arrival(field.position, &mut random)
        } else {
            let Some(gate) = system.body(traveller.to_gate).filter(|b| b.kind == BodyKind::Stargate) else {
                return Err(Box::new(traveller));
            };
            let mut random = PythonRandom::new(seed_of(traveller.pilot.ship.spec.ship_id, gate.id, now));
            gate::arrival(gate, traveller.pilot.ship.spec.radius, &mut random)
        };
        if self.pilots.contains_key(&traveller.session) {
            return Err(Box::new(traveller));
        }
        let Traveller { session, mut pilot, .. } = traveller;
        pilot.ship.position = position;
        pilot.ship.direction = direction;
        pilot.ship.velocity = Vec3::ZERO;
        pilot.ship.speed_fraction = 0.0;
        pilot.ship.course = Course::Stop;
        pilot.settled_at = now;
        pilot.docking = None;
        pilot.flight = None;
        pilot.jump = None;
        pilot.jumped_in = true;
        // the client binds the ballpark of this system afresh, and is then sent what is in view
        pilot.bound = false;
        pilot.sees.clear();
        pilot.entered = self.next_entry();
        self.pilots.insert(session, pilot);
        Ok(self.announce_arrival(session, now))
    }
}

/// How far from a cynosural field a ship that jumped to it appears (the static data's cynosural field spawn radius, attribute 2455 of the generator).
/// shortcut: one radius for every field type, the ring is at a random bearing and the ship faces the field; the reference's exact landing is not in the sources read.
pub(crate) const CYNO_ARRIVAL_RADIUS: f64 = 2_455.0;

/// Where a ship that jumped to a field appears and which way it faces: on the ring of [`CYNO_ARRIVAL_RADIUS`] around the field, at a random bearing,
/// facing the field.
fn cyno_arrival(field: Vec3, random: &mut PythonRandom) -> (Vec3, Vec3) {
    let bearing = random.random() * std::f64::consts::TAU;
    let position = Vec3::new(
        field.x + CYNO_ARRIVAL_RADIUS * bearing.cos(),
        field.y,
        field.z + CYNO_ARRIVAL_RADIUS * bearing.sin(),
    );
    let facing = motion::unit(Vec3::new(field.x - position.x, field.y - position.y, field.z - position.z))
        .unwrap_or(Vec3::new(1.0, 0.0, 0.0));
    (position, facing)
}
