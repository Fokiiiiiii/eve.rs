use super::docking::{is_dock_body, surface_distance};
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

    pub(crate) fn controlled_structure_id(&self, session: SessionId) -> Option<i64> {
        self.controlled_structures.get(&session).map(|observer| observer.structure_id)
    }

    /// The Host's current structure-pilot record version for this controller lease.

    pub(crate) fn burst_source_id(&self, session: SessionId) -> Option<i64> {
        self.controlled_structure_id(session)
            .or_else(|| self.pilots.get(&session).map(|pilot| pilot.ship.spec.ship_id))
    }

    /// The control lease of a burst source: a structure's lease, or 0 for a ship (it has no lease; the Host authorizes its pulses by the pilot).
    pub(crate) fn burst_source_lease(&self, session: SessionId, source_id: i64) -> Option<i64> {
        match self.controlled_structure_id(session) {
            Some(structure_id) => self.control_lease_generation(session, structure_id),
            None => self.pilots.get(&session).filter(|pilot| pilot.ship.spec.ship_id == source_id).map(|_| 0),
        }
    }

    pub(crate) fn control_lease_generation(&self, session: SessionId, structure_id: i64) -> Option<i64> {
        (self.controlled_structure_id(session) == Some(structure_id))
            .then(|| self.control_lease_generations.get(&structure_id).copied())
            .flatten()
    }

    pub(crate) fn with_controlled_structure_modules<R>(
        &mut self,
        now: i64,
        run: impl FnOnce(&mut Self) -> R,
    ) -> R {
        let sessions: Vec<_> = self.controlled_structures.keys().copied().collect();
        let mut inserted = Vec::new();
        for session in sessions {
            if self.pilots.contains_key(&session) {
                continue;
            }
            let Some(structure_id) = self.controlled_structure_id(session) else { continue };
            let Some(state) = self.structure_modules.get(&structure_id).cloned() else { continue };
            let Some((position, radius)) =
                self.structure_body(structure_id).map(|body| (body.position, body.radius))
            else {
                continue;
            };
            let Some(health) =
                self.structure_health.get(&structure_id).map(|health| health.health_snapshot())
            else {
                continue;
            };
            let Some(observer) = self.controlled_structures.get(&session) else { continue };
            let mut spec = state.spec;
            spec.radius = radius;
            let ship = crate::ship::Ship {
                spec,
                npc: None,
                position,
                velocity: eve_destiny::Vec3::new(0.0, 0.0, 0.0),
                direction: eve_destiny::Vec3::new(1.0, 0.0, 0.0),
                speed_fraction: 0.0,
                course: crate::ship::Course::Stop,
                health,
            };
            let mut pilot = Pilot::new(ship, state.modules, structure_id as u64, now, 0);
            pilot.targeting = state.targeting;
            pilot.bound = observer.bound;
            pilot.sees.clone_from(&observer.sees);
            pilot.sees_wrecks.clone_from(&observer.sees_wrecks);
            pilot.sees_abyssal_traces.clone_from(&observer.sees_abyssal_traces);
            pilot.sees_asteroids.clone_from(&observer.sees_asteroids);
            self.pilots.insert(session, pilot);
            inserted.push((session, structure_id));
        }

        let result = run(self);
        for (session, structure_id) in inserted {
            let Some(pilot) = self.pilots.remove(&session) else { continue };
            if let Some(state) = self.structure_modules.get_mut(&structure_id) {
                state.spec = pilot.ship.spec.clone();
                state.modules = pilot.modules.clone();
                state.targeting = pilot.targeting;
            }
            if let Some(health) = self.structure_health.get_mut(&structure_id) {
                health.replace_health_from_controlled_modules(pilot.ship.health);
            }
        }
        result
    }

    pub(crate) fn controller_of_structure(&self, structure_id: i64) -> Option<SessionId> {
        self.controlled_structures
            .iter()
            .find_map(|(session, observer)| (observer.structure_id == structure_id).then_some(*session))
    }

    /// Attaches a view to an existing structure body, or restores it to the session's prior ship.
    #[allow(clippy::too_many_arguments)]
    pub(crate) fn control_structure(
        &mut self,
        session: SessionId,
        solar_system_id: i32,
        structure_id: i64,
        character_id: i64,
        active: bool,
        control_lease_generation: i64,
        now: i64,
    ) -> Vec<Out> {
        let refused =
            |reason| vec![Out::Refused { session, command: crate::link::COMMAND_STRUCTURE_CONTROL, reason }];
        let accepted = || Out::Accepted { session, command: crate::link::COMMAND_STRUCTURE_CONTROL, at: now };
        if session == 0
            || solar_system_id <= 0
            || structure_id < 0
            || character_id < 0
            || (active && (structure_id == 0 || character_id == 0))
        {
            return refused("INVALID_CONTROL_STRUCTURE_IDS");
        }
        if solar_system_id != self.system.id {
            return refused("STRUCTURE_WRONG_SYSTEM");
        }
        if !active {
            let Some(current) = self.controlled_structure_id(session) else {
                return refused("CONTROL_STRUCTURE_NOT_ACTIVE");
            };
            if structure_id != 0 && structure_id != current {
                return refused("CONTROL_STRUCTURE_MISMATCH");
            }
            if self
                .control_lease_generations
                .get(&current)
                .is_some_and(|latest| control_lease_generation < *latest)
            {
                return refused("CONTROL_STRUCTURE_GENERATION_STALE");
            }
            self.control_lease_generations.insert(current, control_lease_generation);
            let mut out = self.release_controlled_structure(session, current, now).unwrap_or_default();
            out.push(accepted());
            return out;
        }
        if self.structure_index(structure_id).is_none() || !self.structure_health.contains_key(&structure_id)
        {
            return refused("STRUCTURE_NOT_FOUND");
        }
        if self.pilots.values().any(|pilot| pilot.ship.spec.ship_id == structure_id) {
            return refused("STRUCTURE_ALREADY_ACTIVE_AS_SHIP");
        }
        if self.controlled_structure_id(session) == Some(structure_id) {
            if self
                .control_lease_generations
                .get(&structure_id)
                .is_some_and(|latest| control_lease_generation < *latest)
            {
                return refused("CONTROL_STRUCTURE_GENERATION_STALE");
            }
            self.control_lease_generations.insert(structure_id, control_lease_generation);
            return vec![accepted()];
        }
        if self
            .control_lease_generations
            .get(&structure_id)
            .is_some_and(|latest| control_lease_generation <= *latest)
        {
            return refused("CONTROL_STRUCTURE_GENERATION_STALE");
        }

        let mut out = Vec::new();
        if let Some(current) = self.controlled_structure_id(session)
            && let Some(released) = self.release_controlled_structure(session, current, now)
        {
            out.extend(released);
        }
        if let Some(previous) = self.controller_of_structure(structure_id)
            && previous != session
            && let Some(released) = self.release_controlled_structure(previous, structure_id, now)
        {
            out.extend(released);
        }

        if self.pilots.contains_key(&session) {
            self.settle_pilot(session, now);
        }
        let (previous_ship_id, bound) = self
            .pilots
            .get(&session)
            .map(|pilot| (Some(pilot.ship.spec.ship_id), pilot.bound))
            .unwrap_or((None, false));
        if previous_ship_id.is_some() {
            out.extend(self.leave(session, Departure::Boarded, now).unwrap_or_default());
        }
        self.control_lease_generations.insert(structure_id, control_lease_generation);
        self.controlled_structures.insert(
            session,
            ControlledStructureObserver {
                structure_id,
                previous_ship_id,
                needs_initial_ballpark: !bound,
                bound: false,
                sees: BTreeSet::new(),
                sees_wrecks: BTreeSet::new(),
                sees_abyssal_traces: BTreeSet::new(),
                sees_asteroids: BTreeSet::new(),
            },
        );
        if bound {
            self.take_in_view(session);
        }
        out.push(accepted());
        out
    }

    /// Restores the same unpiloted Pilot when its ship is still in this scene.
    pub(crate) fn release_controlled_structure(
        &mut self,
        session: SessionId,
        structure_id: i64,
        now: i64,
    ) -> Option<Vec<Out>> {
        let observer = self.controlled_structures.get(&session)?;
        if observer.structure_id != structure_id {
            return None;
        }
        let previous_ship_id = observer.previous_ship_id;
        let mut out = self.teardown_controlled_structure(session, structure_id, now);
        self.controlled_structures.remove(&session);
        if let Some(ship_id) = previous_ship_id
            && let Some(fake_session) = self.unpiloted.get(&ship_id).copied()
        {
            if self.pilots.contains_key(&fake_session) {
                self.settle_pilot(fake_session, now);
                out.extend(self.announce_departure(fake_session, ship_id, now));
                self.unpiloted.remove(&ship_id);
                let mut pilot = self.pilots.remove(&fake_session)?;
                if let Some(widow) = self.widowed.remove(&session) {
                    pilot.bound = widow.bound;
                    pilot.sees = widow.sees;
                    pilot.sees_wrecks = widow.sees_wrecks;
                    pilot.sees_abyssal_traces = widow.sees_abyssal_traces;
                    pilot.sees_asteroids = widow.sees_asteroids;
                }
                let bound = pilot.bound;
                self.pilots.insert(session, pilot);
                if bound {
                    self.take_in_view(session);
                }
                out.extend(self.announce_arrival(session, now));
                return Some(out);
            }
            self.unpiloted.remove(&ship_id);
        }
        // A lost retained hull keeps its respawn context across control restoration and retry.
        Some(out)
    }

    pub(crate) fn teardown_controlled_structure(
        &mut self,
        session: SessionId,
        structure_id: i64,
        now: i64,
    ) -> Vec<Out> {
        if self.controlled_structure_id(session) != Some(structure_id) {
            return Vec::new();
        }
        self.settle_modules(now);
        self.with_controlled_structure_modules(now, |scene| {
            let held = scene.pilots.get(&session).map(|pilot| pilot.targeting.clone()).unwrap_or_default();
            let mut out = scene.stop_controlled_structure_modules(session, now);
            out.extend(scene.clear_locks(session, true, now));
            out.extend(scene.drop_targeting(structure_id, &held, now));
            out
        })
    }

    /// The client bound the system's ballpark: it is told the time, and given the ships in view (its own among them, in the order they came into
    /// the scene) and then everything around them.
    pub fn bind(&mut self, session: SessionId, now: i64) -> Vec<Out> {
        if !self.pilots.contains_key(&session) {
            return self.bind_controlled_structure(session, now);
        }
        let login_visuals_sent = self.pilots[&session].login_visuals_sent;
        self.settle_all(now);
        self.take_in_view(session);
        let first = self.next_stamp(now);
        let second = self.next_stamp(now);
        let system = Arc::clone(&self.system);
        let ships = self.ships_in_view(session);
        let traces = self.abyssal_traces_in_view(session);
        let trace_adds: Vec<_> =
            match traces.iter().map(|trace| payload::add_abyssal_trace(first, trace)).collect() {
                Ok(adds) => adds,
                Err(_) => return Vec::new(),
            };
        let Some(pilot) = self.pilots.get(&session) else { return Vec::new() };
        let ship = &pilot.ship;
        let ship_id = id(ship.spec.ship_id);
        let heading =
            |action_stamp: u32| payload::action(action_stamp, heading_name(ship), heading_args(ship));

        let mut added_actions = Vec::new();
        if !login_visuals_sent {
            let Ok(ship_added) = payload::add_ships(first, &ships, now) else { return Vec::new() };
            added_actions.push(ship_added);
        }
        let Ok(abyssal_scene_objects) = payload::add_abyssal_scene_objects(first, &system) else {
            return Vec::new();
        };
        // login visual priming adds ships only; structures and private scene objects still need their balls.
        added_actions.extend(payload::add_structures(first, &system, now));
        added_actions.extend(abyssal_scene_objects);
        added_actions.extend(trace_adds);
        let added = (!added_actions.is_empty()).then_some(added_actions);
        let wrecks = self.wrecks_in_view(session);
        let Ok(state) = payload::set_state_for_ego(
            second,
            &system,
            ship.spec.ship_id,
            &ships,
            &wrecks,
            &self.structure_health,
            now,
        ) else {
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
                Self::notify(session, "DoDestinyUpdate", payload::destiny_update(added, !pilot.jumped_in)),
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

    fn bind_controlled_structure(&mut self, session: SessionId, now: i64) -> Vec<Out> {
        let Some(observer) = self.controlled_structures.get(&session) else { return Vec::new() };
        let (structure_id, needs_initial_ballpark) = (observer.structure_id, observer.needs_initial_ballpark);
        self.settle_all(now);
        self.take_in_view(session);
        let first = self.next_stamp(now);
        let second = self.next_stamp(now);
        let system = Arc::clone(&self.system);
        let ships = self.ships_in_view(session);
        let wrecks = self.wrecks_in_view(session);
        let traces = self.abyssal_traces_in_view(session);
        let trace_adds: Vec<_> =
            match traces.iter().map(|trace| payload::add_abyssal_trace(first, trace)).collect() {
                Ok(adds) => adds,
                Err(_) => return Vec::new(),
            };
        let added = if needs_initial_ballpark {
            let Ok(ship_added) = payload::add_ships(first, &ships, now) else { return Vec::new() };
            let mut actions = vec![ship_added];
            actions.extend(trace_adds);
            Some(actions)
        } else if !trace_adds.is_empty() {
            Some(trace_adds)
        } else {
            None
        };
        let Ok(state) = payload::set_state_for_ego(
            second,
            &system,
            structure_id,
            &ships,
            &wrecks,
            &self.structure_health,
            now,
        ) else {
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
                Self::notify(
                    session,
                    "DoDestinyUpdate",
                    payload::destiny_update(added, needs_initial_ballpark),
                ),
            ]);
        }
        told.push(update(vec![state]));
        let numbers = ships.iter().flat_map(|ship| Self::numbers_of(ship, second)).collect();
        told.push(update(numbers));
        if let Some(observer) = self.controlled_structures.get_mut(&session) {
            observer.needs_initial_ballpark = false;
        }
        self.with_backlog(told)
    }

    fn update_controlled_structure_state(&mut self, session: SessionId, now: i64) -> Vec<Out> {
        let Some(observer) = self.controlled_structures.get(&session) else { return Vec::new() };
        let structure_id = observer.structure_id;
        self.settle_all(now);
        let stamp = self.next_stamp(now);
        let system = Arc::clone(&self.system);
        let ships = self.ships_in_view(session);
        let wrecks = self.wrecks_in_view(session);
        let told = match payload::set_state_for_ego(
            stamp,
            &system,
            structure_id,
            &ships,
            &wrecks,
            &self.structure_health,
            now,
        ) {
            Ok(state) => {
                vec![Self::notify(session, "DoDestinyUpdate", payload::destiny_update([state], false))]
            }
            Err(_) => Vec::new(),
        };
        self.with_backlog(told)
    }

    /// The client asked for the state again (`UpdateStateRequest`): every ball as it is now.
    pub fn update_state(&mut self, session: SessionId, now: i64) -> Vec<Out> {
        if !self.pilots.contains_key(&session) {
            return self.update_controlled_structure_state(session, now);
        }
        self.settle_all(now);
        let stamp = self.next_stamp(now);
        let system = Arc::clone(&self.system);
        let ships = self.ships_in_view(session);
        let Some(pilot) = self.pilots.get(&session) else { return Vec::new() };
        let wrecks = self.wrecks_in_view(session);
        let told = match payload::set_state_for_ego(
            stamp,
            &system,
            pilot.ship.spec.ship_id,
            &ships,
            &wrecks,
            &self.structure_health,
            now,
        ) {
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
        if self.abyssal.is_some()
            && matches!(
                command,
                Command::WarpTo { .. }
                    | Command::WarpToPoint { .. }
                    | Command::Jump { .. }
                    | Command::CynoJump { .. }
                    | Command::OpenBridge { .. }
                    | Command::BridgeJump { .. }
                    | Command::ConduitJump { .. }
            )
        {
            return vec![Out::Refused { session, command: code, reason: "ABYSSAL_WARP_RESTRICTED" }];
        }
        if self.link_holds(session, &command, now) {
            return vec![Out::Refused { session, command: code, reason: "LINK_HOLD" }];
        }
        if !matches!(command, Command::Dock { .. }) {
            self.pilots.get_mut(&session).expect("the caller checked").docking_approach = None;
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
            Command::AccelerationGateTransit { gate_id, destination } => {
                self.acceleration_gate_transit(session, gate_id, destination, now)
            }
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
            let Some(structure_id) = self.controlled_structure_id(session).filter(|id| *id == spec.ship_id)
            else {
                return Vec::new();
            };
            if self.structure_body(structure_id).is_none()
                || !self.structure_health.contains_key(&structure_id)
            {
                return Vec::new();
            }
            if let Some(state) = self.structure_modules.get_mut(&structure_id) {
                state.modules.respec_capacitor(spec.capacitor_capacity, spec.capacitor_recharge_s, now);
            } else {
                self.structure_modules.insert(
                    structure_id,
                    ControlledStructureModules {
                        spec: spec.clone(),
                        modules: crate::modules::Modules::with_charge(
                            spec.capacitor_capacity,
                            spec.capacitor_recharge_s,
                            1.0,
                            now,
                        ),
                        targeting: crate::targeting::Targeting::default(),
                    },
                );
            }
            return self.with_controlled_structure_modules(now, |scene| {
                let mut out = scene.respec(session, spec, now);
                out.extend(scene.stop_unfitted_structure_modules(session, now));
                out
            });
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
        let sim_profile_update = self.sim_damageable_entities.contains(&old.ship_id).then_some((
            old.ship_id,
            new.ship_id,
            new.type_id,
            new.owner_id,
            i64::from(new.corporation_id),
            i64::from(new.alliance_id),
            i64::from(new.war_faction_id),
        ));
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
        let mut out = self.with_backlog(told);
        if let Some((
            previous_id,
            entity_id,
            type_id,
            owner_id,
            corporation_id,
            alliance_id,
            war_faction_id,
        )) = sim_profile_update
        {
            if previous_id != entity_id
                && let Some(removed) = self.remove_sim_damageable_entity(previous_id)
            {
                out.push(removed);
            }
            out.push(self.publish_sim_damageable_entity(
                entity_id,
                type_id,
                owner_id,
                corporation_id,
                alliance_id,
                war_faction_id,
            ));
        }
        out
    }

    pub fn poll(&mut self, now: i64) -> Vec<Out> {
        self.settle_all(now);
        let mut told = self.populate_pochven(now);
        let arrived: Vec<(SessionId, i64)> = self
            .pilots
            .iter()
            .filter_map(|(session, pilot)| {
                let station_id = pilot.docking_approach?;
                let station = self.system.body(station_id).filter(|body| is_dock_body(body))?;
                (surface_distance(&pilot.ship, station.position, station.radius) <= DOCKING_RANGE)
                    .then_some((*session, station_id))
            })
            .collect();
        for (session, station_id) in arrived {
            if let Some(pilot) = self.pilots.get_mut(&session) {
                pilot.docking_approach = None;
            }
            told.extend(self.dock(session, station_id, now));
        }
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
        told.extend(self.reconcile_abyssal_traces(now));
        told.extend(self.reconcile_asteroids(now));
        told.extend(self.reconcile_deployables(now));
        told.extend(self.expire_wrecks(now));
        told.extend(self.tick_tethers(now));
        self.with_backlog(told)
    }
}
