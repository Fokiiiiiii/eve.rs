use super::*;

impl Scene {
    /// Send the visual half of a login-in-space bootstrap; the later ballpark bind sends the full SetState.
    pub fn prime_login_visuals(&mut self, session: SessionId, now: i64) -> Vec<Out> {
        if self.pilots.get(&session).is_none_or(|pilot| pilot.bound || pilot.login_visuals_sent) {
            return Vec::new();
        }
        self.settle_all(now);
        let stamp = self.next_stamp(now);
        let ships = self.ships_visible_to(session);
        let Ok(added) = payload::add_ships(stamp, &ships, now) else { return Vec::new() };
        let told = vec![
            Self::notify(session, "OnSetTimeDilation", vec![int(1), int(1), int(100_000_000)]),
            Self::notify(session, "DoSimClockRebase", vec![tuple([filetime(now), filetime(now)])]),
            Self::notify(session, "DoDestinyUpdate", payload::destiny_update([added], true)),
        ];
        if let Some(pilot) = self.pilots.get_mut(&session) {
            pilot.login_visuals_sent = true;
        }
        told
    }

    /// The client bound the system's ballpark: it is told the time, and given the ships in view (its own among them, in the order they came into
    /// the scene) and then everything around them.
    pub fn bind(&mut self, session: SessionId, now: i64) -> Vec<Out> {
        if !self.pilots.contains_key(&session) {
            return Vec::new();
        }
        let login_visuals_sent = self.pilots[&session].login_visuals_sent;
        self.settle_all(now);
        self.take_in_view(session);
        let first = self.next_stamp(now);
        let second = self.next_stamp(now);
        let system = Arc::clone(&self.system);
        let ships = self.ships_in_view(session);
        let Some(pilot) = self.pilots.get(&session) else { return Vec::new() };
        let ship = &pilot.ship;
        let ship_id = id(ship.spec.ship_id);
        let heading =
            |action_stamp: u32| payload::action(action_stamp, heading_name(ship), heading_args(ship));

        let added = if login_visuals_sent {
            None
        } else {
            let Ok(added) = payload::add_ships(first, &ships, now) else { return Vec::new() };
            Some(added)
        };
        let wrecks = self.wrecks_in_view(session);
        let Ok(state) = payload::set_state(second, &system, ship, &ships, &wrecks, now) else {
            return Vec::new();
        };
        let update = |actions: Vec<Value>| {
            Self::notify(session, "DoDestinyUpdate", payload::destiny_update(actions, false))
        };
        let mut told = Vec::new();
        if let Some(added) = added {
            told.extend([
                Self::notify(session, "OnSetTimeDilation", vec![int(1), int(1), int(100_000_000)]),
                Self::notify(session, "DoSimClockRebase", vec![tuple([filetime(now), filetime(now)])]),
                // An undocked ship waits for its ball; an in-space arrival does not.
                Self::notify(session, "DoDestinyUpdate", payload::destiny_update([added], !pilot.jumped_in)),
            ]);
        }
        if !pilot.jumped_in {
            // A ship that undocked moves as it is bound; an in-space arrival is already at rest.
            told.push(update(vec![
                payload::uncloak(first, ship.spec.ship_id),
                payload::action(
                    first,
                    "SetBallPosition",
                    with_id(&ship_id, vector(ship.position.x, ship.position.y, ship.position.z)),
                ),
                payload::action(first, "SetBallMassive", [ship_id.clone(), int(0)]),
                payload::action(first, "SetBallMass", [ship_id.clone(), real(ship.spec.mass)]),
                payload::action(
                    first,
                    "SetBallVelocity",
                    with_id(&ship_id, vector(ship.velocity.x, ship.velocity.y, ship.velocity.z)),
                ),
                heading(first),
            ]));
        }
        told.push(update(vec![state]));
        // The numbers of every ship in view; the pilot's own lack the mass when the bind has sent it already.
        let mut numbers = Vec::new();
        for other in &ships {
            if other.spec.ship_id != ship.spec.ship_id || pilot.jumped_in {
                numbers.extend(Self::numbers_of(other, second));
            } else {
                let own = id(ship.spec.ship_id);
                numbers.push(payload::action(
                    second,
                    "SetBallAgility",
                    [own.clone(), real(ship.spec.agility)],
                ));
                numbers.push(payload::action(second, "SetMaxSpeed", [own, real(ship.spec.max_velocity)]));
            }
        }
        told.push(update(numbers));
        if login_visuals_sent && let Some(pilot) = self.pilots.get_mut(&session) {
            pilot.login_visuals_sent = false;
        }
        self.with_backlog(told)
    }

    /// The client asked for the state again (`UpdateStateRequest`): every ball as it is now.
    pub fn update_state(&mut self, session: SessionId, now: i64) -> Vec<Out> {
        if !self.pilots.contains_key(&session) {
            return Vec::new();
        }
        self.settle_all(now);
        let stamp = self.next_stamp(now);
        let system = Arc::clone(&self.system);
        let ships = self.ships_in_view(session);
        let Some(pilot) = self.pilots.get(&session) else { return Vec::new() };
        let wrecks = self.wrecks_in_view(session);
        let told = match payload::set_state(stamp, &system, &pilot.ship, &ships, &wrecks, now) {
            Ok(state) => {
                vec![Self::notify(session, "DoDestinyUpdate", payload::destiny_update([state], false))]
            }
            Err(_) => Vec::new(),
        };
        self.with_backlog(told)
    }

    /// A command of the player's. Movement is applied from `now`; what the client is told is one update at the next stamp (none when the
    /// ship already does what it is asked), and the host is told the command was accepted, after the update.
    pub fn command(&mut self, session: SessionId, command: Command, now: i64) -> Vec<Out> {
        let code = command.code();
        if !self.pilots.contains_key(&session) {
            return vec![Out::Refused { session, command: code, reason: "NO_SHIP_IN_SPACE" }];
        }
        if self.link_holds(session, &command, now) {
            return vec![Out::Refused { session, command: code, reason: "LINK_HOLD" }];
        }
        self.settle_pilot(session, now);
        let told = match command {
            Command::Dock { station_id } => self.dock(session, station_id, now),
            Command::WarpTo { target_id, range } => self.warp_to(session, target_id, range, now),
            Command::WarpToPoint { destination, range } => {
                self.warp_to_point(session, destination, range, now)
            }
            Command::ResolveWarpTarget { target_id, range } => {
                self.resolve_warp_target(session, target_id, range, now)
            }
            Command::PositionOf { target_id } => self.position_of(session, target_id),
            Command::DropPoint { distance } => self.drop_point(session, distance),
            Command::ScanTargets => self.scan_targets(session, now),
            Command::DirectionalScan => self.directional_targets(session, now),
            Command::Jump { gate_id, destination_gate } => self.jump(session, gate_id, destination_gate, now),
            Command::CynoJump { field_id, destination_system, spool_ms, capacitor } => {
                self.cyno_jump(session, field_id, destination_system, spool_ms, capacitor, now)
            }
            Command::OpenBridge {
                field_id,
                destination_system,
                lifetime_ms,
                immobile_ms,
                range_meters,
                capacitor,
            } => self.open_bridge(
                session,
                field_id,
                destination_system,
                (lifetime_ms, immobile_ms, range_meters),
                capacitor,
                now,
            ),
            Command::ConduitJump { field_id, destination_system, passenger_count, passengers, .. } => self
                .conduit_jump(
                    session,
                    field_id,
                    destination_system,
                    &passengers[..passenger_count as usize],
                    now,
                ),
            Command::BridgeJump { bridge_ship_id, field_id, destination_system } => {
                self.bridge_jump(session, bridge_ship_id, field_id, destination_system, now)
            }
            other => self.steer_pilot(session, other, now),
        };
        // the ship was flown: the undocking protection is over (a docking does not end it)
        if told.iter().any(|o| matches!(o, Out::Accepted { command, .. } if *command != 4))
            && let Some(pilot) = self.pilots.get_mut(&session)
        {
            pilot.protected_until = 0;
        }
        self.with_backlog(told)
    }

    /// Tells what happened while ships were moved on, then `told`.
    pub(crate) fn with_backlog(&mut self, told: Vec<Out>) -> Vec<Out> {
        let mut out = std::mem::take(&mut self.backlog);
        out.extend(told);
        out
    }

    /// The numbers of a ship in space changed (a skill finished, a module went on): the client is told the ones that differ.
    pub fn respec(&mut self, session: SessionId, spec: ShipSpec, now: i64) -> Vec<Out> {
        if !self.pilots.contains_key(&session) {
            return Vec::new();
        }
        self.settle_pilot(session, now);
        let stamp = self.next_stamp(now);
        let shift_attributes = self.shift_attributes(session);
        let Some(pilot) = self.pilots.get_mut(&session) else { return Vec::new() };
        let armor_before = pilot.ship.health.armor_resonance();
        let old = std::mem::replace(&mut pilot.ship.spec, spec);
        // a switch is from the stance the ship had: the host sends the new one, the simulation knows the old one
        if pilot.ship.spec.stance != old.stance
            && let (Some(now), Some(before)) = (pilot.ship.spec.stance.as_mut(), old.stance)
        {
            now.old_stance = before.stance;
        }
        let new = &pilot.ship.spec;
        pilot.ship.health.respec(new.vitals, new.shield_recharge_ms, now);
        // a reactive armor hardener that runs is shown the armor's resonances as they now are (a pin, or a new base, moves them)
        let armor_changes = shift_attributes
            .map(|ids| {
                crate::modules::armor_changes(&ids, &armor_before, &pilot.ship.health.armor_resonance())
            })
            .unwrap_or_default();
        let security_changed = new.security_status != old.security_status;
        // a stance that changed is in the slim item too (`shipStance`, the reference's slim update after a switch)
        let stance_changed = new.stance != old.stance;
        let ship_id = id(new.ship_id);
        let (owner, ship) = (new.character_id, new.ship_id);
        let mut actions = Vec::new();
        if new.mass != old.mass {
            actions.push(payload::action(stamp, "SetBallMass", [ship_id.clone(), real(new.mass)]));
        }
        if new.agility != old.agility {
            actions.push(payload::action(stamp, "SetBallAgility", [ship_id.clone(), real(new.agility)]));
        }
        if new.max_velocity != old.max_velocity {
            actions.push(payload::action(stamp, "SetMaxSpeed", [ship_id, real(new.max_velocity)]));
        }
        let mut told: Vec<Out> =
            Self::health_told(session, owner, ship, now, &armor_changes).into_iter().collect();
        if !actions.is_empty() {
            told.extend(self.tell_all(session, actions));
        }
        // a security status that changed is in the slim item: the pilot and whoever sees the ship are shown it again (the reference does, when a crime costs a pilot some)
        if security_changed || stance_changed {
            let ship = &self.pilots[&session].ship;
            let change = payload::ship_slim_item_change(stamp, ship);
            told.extend(self.tell_all(session, vec![change]));
        }
        self.with_backlog(told)
    }

    pub fn poll(&mut self, now: i64) -> Vec<Out> {
        self.settle_all(now);
        let mut told = self.populate_pochven(now);
        let due: Vec<(SessionId, i64)> = self
            .pilots
            .iter()
            .filter_map(|(session, pilot)| {
                pilot
                    .docking
                    .as_ref()
                    .filter(|d| now - d.accepted_at >= DOCKING_DELAY)
                    .map(|d| (*session, d.station_id))
            })
            .collect();
        for (session, station_id) in due {
            told.extend(self.clear_tether_policy(session, now));
            if let Some(pilot) = self.pilots.remove(&session) {
                told.push(Out::Docked { session, station_id, condition: Self::condition_of(&pilot, now) });
                told.extend(self.drop_targeting(pilot.ship.spec.ship_id, &pilot.targeting, now));
                told.extend(self.announce_departure(session, pilot.ship.spec.ship_id, now));
                told.extend(self.release_drones(session, None, now));
            }
        }
        told.extend(self.note_drone_threats(now));
        told.extend(self.tick_brains(now));
        told.extend(self.settle_targets(now));
        while let Some(at) = self.next_heat_cycle_boundary(now) {
            let heat = self.prepare_overheating(at);
            told.extend(self.settle_modules(at));
            told.extend(self.apply_overheating(heat, at));
        }
        let heat = self.prepare_overheating(now);
        told.extend(self.settle_modules(now));
        told.extend(self.apply_overheating(heat, now));
        told.extend(self.decloak_by_proximity(now));
        told.extend(self.advance_missiles(now));
        told.extend(self.advance_dots(now));
        told.extend(self.advance_deployables(now));
        told.extend(self.advance_drones(now));
        told.extend(self.reconcile(now));
        told.extend(self.reconcile_wrecks(now));
        told.extend(self.reconcile_asteroids(now));
        told.extend(self.expire_wrecks(now));
        told.extend(self.tick_tethers(now));
        self.with_backlog(told)
    }
}
