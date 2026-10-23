//! The walk through an acceleration gate of a dungeon room into another room of its site (command 34, protocol 75).
//!
//! The rules are EVE.js `activateAccelerationGateForSession`: the ship must be within the gate's reach (the gate's and the ship's radius plus
//! [`GATE_ACTIVATION_RANGE`], at least that range), and the transit is a warp to the other room's entry point, the client's own warp sequence. The
//! ship's modules stop and its locks are let go, as the warp begins (the reference runs the same stop for a gate transit).
use super::*;

use crate::deployable::Role;

/// How far a ship may be from an acceleration gate and still walk through it, metres (EVE.js
/// `DEFAULT_ACCELERATION_GATE_ACTIVATION_RANGE_METERS`).
pub(crate) const GATE_ACTIVATION_RANGE: f64 = 2_500.0;

impl Scene {
    /// Walks the ship through the acceleration gate `gate_id` to `destination`. The warp is begun first: it refuses a ship that cannot warp (docking, a
    /// jump, a warp in progress, immobile, disrupted), and then nothing has changed.
    pub(super) fn acceleration_gate_transit(
        &mut self,
        session: SessionId,
        gate_id: i64,
        destination: Vec3,
        now: i64,
    ) -> Vec<Out> {
        let code = Command::AccelerationGateTransit { gate_id, destination }.code();
        let refused = |reason: &'static str| vec![Out::Refused { session, command: code, reason }];
        let Some(gate) = self.deployables.get(&gate_id).filter(|gate| gate.role == Role::AccelerationGate)
        else {
            return refused("GATE_NOT_FOUND");
        };
        let (gate_position, gate_radius) = (gate.position, gate.spec.radius);
        let Some(pilot) = self.pilots.get(&session) else { return refused("NO_SHIP_IN_SPACE") };
        let reach = (gate_radius + pilot.ship.spec.radius + GATE_ACTIVATION_RANGE).max(GATE_ACTIVATION_RANGE);
        if distance(pilot.ship.position, gate_position) > reach {
            return refused("TOO_FAR_FROM_GATE");
        }
        // the warp answers under its own code (21): the host asked for this transit, so its answer is under 34
        // the room is the same site: the transit is not a player's warp, so no minimum distance applies to it
        let warp: Vec<Out> = self
            .begin_warp_min(
                session,
                Command::WarpToPoint { destination, range: 0.0 }.code(),
                None,
                destination,
                0.0,
                now,
                0.0,
            )
            .into_iter()
            .map(|out| match out {
                Out::Accepted { session, command: 21, at } => Out::Accepted { session, command: code, at },
                Out::Refused { session, command: 21, reason } => {
                    Out::Refused { session, command: code, reason }
                }
                other => other,
            })
            .collect();
        if warp.iter().any(|out| matches!(out, Out::Refused { .. })) {
            return warp;
        }
        let mut out = self.stop_all_modules(session, now);
        out.extend(self.clear_locks(session, true, now));
        out.extend(warp);
        out
    }
}

fn distance(a: Vec3, b: Vec3) -> f64 {
    ((a.x - b.x).powi(2) + (a.y - b.y).powi(2) + (a.z - b.z).powi(2)).sqrt()
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::link::DisruptorPlacement;
    use crate::scene::tests::{NOON, SESSION, STATION, spec, system};

    const GATE: i64 = 990_000_000_600;

    /// A pilot undocked in the test system, and an acceleration gate of a room placed `offset` metres from its ship.
    fn scene_with_gate(offset: f64) -> (Scene, Vec3) {
        let mut scene = Scene::new(system());
        scene.undock(SESSION, spec(), STATION, NOON).unwrap();
        scene.bind(SESSION, NOON);
        let ship = scene.pilots[&SESSION].ship.position;
        let system_id = scene.solar_system_id();
        let placed = scene.place_acceleration_gate(
            DisruptorPlacement {
                deployable_id: GATE,
                solar_system_id: system_id,
                name: "Acceleration Gate".to_string(),
                type_id: 17_831,
                group_id: 366,
                position: [ship.x + offset, ship.y, ship.z],
                radius: 0.0,
                range: 0.0,
                active: true,
                owner_id: 0,
                corporation_id: 0,
                alliance_id: 0,
                ship_id: 0,
            },
            NOON,
        );
        assert_eq!(scene.deployables[&GATE].role, crate::deployable::Role::AccelerationGate);
        assert!(
            placed.iter().any(|out| format!("{out:?}").contains("AddBalls2")),
            "the gate's ball is added to the pilots' scene"
        );
        (scene, ship)
    }

    fn refusal(out: &[Out]) -> &'static str {
        match out {
            [Out::Refused { command: 34, reason, .. }] => reason,
            other => panic!("{other:?}"),
        }
    }

    #[test]
    fn a_warp_to_a_placed_gate_starts_and_is_not_refused_as_a_missing_target() {
        // the pilot warps to its room's gate (CmdWarpToStuff): the gate is a deployable ball, so the warp finds it and starts
        // 200 km: a warp that ends nearer than the 150 km minimum is refused as too close (warp.rs MINIMUM_WARP_DISTANCE)
        let (mut scene, _) = scene_with_gate(200_000.0);
        let out = scene.command(SESSION, Command::WarpTo { target_id: GATE, range: 0.0 }, NOON);
        assert!(
            !out.iter().any(|o| matches!(o, Out::Refused { .. })),
            "a warp to a placed gate is refused: {out:?}"
        );
        assert!(scene.pilots[&SESSION].flight.is_some(), "the warp to the gate starts");
    }

    #[test]
    fn a_warp_to_a_placed_gate_ends_next_to_it() {
        // the pilot flies a while, then warps to the gate 200 km away: the warp ends at the gate's centre (the range is 0), not a gate's radius short of it
        let (mut scene, ship0) = scene_with_gate(200_000.0);
        let gate = scene.deployables[&GATE].position;
        scene.command(SESSION, Command::SetSpeedFraction(1.0), NOON);
        scene.command(SESSION, Command::GotoDirection(Vec3::new(ship0.x + 1.0, ship0.y, ship0.z)), NOON);
        let mut now = NOON;
        for _ in 0..5 {
            now += crate::scene::TICKS_PER_SECOND;
            scene.update_state(SESSION, now);
        }
        let out = scene.command(SESSION, Command::WarpTo { target_id: GATE, range: 0.0 }, now);
        assert!(
            !out.iter().any(|o| matches!(o, Out::Refused { .. })),
            "the warp to the gate is refused: {out:?}"
        );
        for _ in 0..60 {
            now += crate::scene::TICKS_PER_SECOND;
            scene.update_state(SESSION, now);
        }
        let at = scene.pilots[&SESSION].ship.position;
        let distance = ((at.x - gate.x).powi(2) + (at.y - gate.y).powi(2) + (at.z - gate.z).powi(2)).sqrt();
        assert!(scene.pilots[&SESSION].flight.is_none(), "the warp is over");
        assert!(distance < 1_000.0, "the pilot is {distance:.0} m from the gate after the warp");
    }

    #[test]
    fn a_transit_into_a_room_nearer_than_the_minimum_warp_distance_is_still_walked() {
        // the room's entry is a few km away (the site is one point): the transit is not a player's warp, so the 150 km minimum does not refuse it
        let (mut scene, ship) = scene_with_gate(2_000.0);
        let near = Vec3::new(ship.x + 7_000.0, ship.y, ship.z);
        let out = scene.command(
            SESSION,
            Command::AccelerationGateTransit { gate_id: GATE, destination: near },
            NOON,
        );
        assert!(!out.iter().any(|o| matches!(o, Out::Refused { .. })), "the transit is refused: {out:?}");
        assert!(scene.pilots[&SESSION].flight.is_some(), "the pilot is warping to the room");
    }

    #[test]
    fn a_transit_through_a_gate_that_is_not_there_is_refused() {
        let (mut scene, ship) = scene_with_gate(1_000.0);
        let far = Vec3::new(ship.x + 1_000_000.0, ship.y, ship.z);
        let out = scene.command(
            SESSION,
            Command::AccelerationGateTransit { gate_id: GATE + 1, destination: far },
            NOON,
        );
        assert_eq!(refusal(&out), "GATE_NOT_FOUND");
    }

    #[test]
    fn a_transit_from_beyond_the_gate_reach_is_refused_and_one_within_it_warps_the_pilot_to_the_other_room() {
        let (mut scene, ship) = scene_with_gate(10_000.0);
        let far = Vec3::new(ship.x + 1_000_000.0, ship.y, ship.z);
        let out = scene.command(
            SESSION,
            Command::AccelerationGateTransit { gate_id: GATE, destination: far },
            NOON,
        );
        assert_eq!(refusal(&out), "TOO_FAR_FROM_GATE");
        assert!(scene.pilots[&SESSION].flight.is_none(), "a refused transit starts no warp");

        let (mut scene, ship) = scene_with_gate(2_000.0);
        let far = Vec3::new(ship.x + 1_000_000.0, ship.y, ship.z);
        let out = scene.command(
            SESSION,
            Command::AccelerationGateTransit { gate_id: GATE, destination: far },
            NOON,
        );
        assert!(
            out.iter().any(|o| matches!(o, Out::Accepted { command: 34, .. })),
            "the transit is accepted: {out:?}"
        );
        assert!(scene.pilots[&SESSION].flight.is_some(), "the pilot is set to warp to the other room");
    }
}
