//! The scan targets of a scene for the host's probe scans (command 27, protocol 52). The host decides what each one is worth; the scene says what is there.
//! A target is a ship (a player's or an NPC's), a drone or a fighter squadron (the companions of a ship), or a deployable a probe can scan: a cynosural field,
//! a Mobile Cynosural Inhibitor, a mobile warp disruptor (EVE.js `resolveEntityScanGroup`). A cloaked ship is not a target (the wiki: "cloaked ships cannot be
//! scanned with probes"). Wrecks, containers, asteroids, missiles, the scanner probes and the other deployables are not targets.

use crate::deployable::Role;
use crate::link::ScanTarget;
use crate::scene::{Command, Out, Scene, SessionId};
use crate::ship::CompanionKind;
use crate::visibility::CloakState;

/// The kinds of a scan target, as the wire numbers them.
pub const KIND_SHIP: u8 = 1;
pub const KIND_DRONE: u8 = 2;
pub const KIND_FIGHTER: u8 = 3;
pub const KIND_DEPLOYABLE: u8 = 4;

impl Scene {
    /// The scan targets the pilot of `session` can scan now: every other ship in the scene that is not cloaked, and the deployables that are still in their life.
    pub(super) fn scan_targets(&mut self, session: SessionId, now: i64) -> Vec<Out> {
        let command = Command::ScanTargets.code();
        let Some(own) = self.pilots.get(&session).map(|pilot| pilot.ship.spec.ship_id) else {
            return vec![Out::Refused { session, command, reason: "NO_SHIP_IN_SPACE" }];
        };
        let sessions: Vec<SessionId> = self.pilots.keys().copied().collect();
        for other in sessions {
            self.settle_pilot(other, now);
        }
        let mut targets = Vec::new();
        for pilot in self.pilots.values() {
            if pilot.ship.spec.ship_id == own || !matches!(pilot.cloak, CloakState::Uncloaked) {
                continue;
            }
            let kind = match pilot.ship.npc.as_ref().and_then(|npc| npc.drone.as_ref()).map(|link| link.kind)
            {
                Some(CompanionKind::Drone) => KIND_DRONE,
                Some(CompanionKind::Fighter) => KIND_FIGHTER,
                None => KIND_SHIP,
            };
            let p = pilot.ship.position;
            targets.push(ScanTarget {
                id: pilot.ship.spec.ship_id,
                kind,
                type_id: pilot.ship.spec.type_id,
                position: [p.x, p.y, p.z],
            });
        }
        for deployable in self.deployables.values() {
            let scannable = matches!(
                deployable.role,
                Role::CynoField
                    | Role::CynoInhibitor { .. }
                    | Role::MobileWarpSource { .. }
                    | Role::CynoBeacon { .. }
            );
            if scannable && deployable.ends_at > now {
                let p = deployable.position;
                targets.push(ScanTarget {
                    id: deployable.id,
                    kind: KIND_DEPLOYABLE,
                    type_id: deployable.spec.charge_type_id,
                    position: [p.x, p.y, p.z],
                });
            }
        }
        targets.sort_by_key(|target| target.id);
        vec![Out::ScanTargets { session, command, targets }]
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::link::{DisruptorPlacement, ProbePlacement};
    use crate::scene::tests::{NOON, STATION, spec, system};

    const A: SessionId = 7;
    const B: SessionId = 8;
    const SECOND: i64 = 10_000_000;
    const PROBE: i64 = 990_000_000_001;
    const DISRUPTOR: i64 = 990_000_000_002;

    /// Two ships in a system, both bound and settled: `A` (the scene's own) and `B` (another ship, another character).
    fn two_ships() -> (Scene, i64, i64) {
        let mut scene = Scene::new(system());
        let mut other = spec();
        other.ship_id += 4;
        other.character_id += 1;
        other.owner_id += 1;
        let (own_id, other_id) = (spec().ship_id, other.ship_id);
        scene.undock(A, spec(), STATION, NOON).unwrap();
        scene.bind(A, NOON);
        scene.undock(B, other, STATION, NOON).unwrap();
        scene.bind(B, NOON);
        scene.poll(NOON + 40 * SECOND);
        (scene, own_id, other_id)
    }

    fn targets_for(scene: &mut Scene, session: SessionId, at: i64) -> Vec<ScanTarget> {
        scene
            .command(session, Command::ScanTargets, at)
            .into_iter()
            .find_map(|out| match out {
                Out::ScanTargets { targets, .. } => Some(targets),
                _ => None,
            })
            .expect("a scan target answer")
    }

    #[test]
    fn a_pilot_is_offered_the_other_ships_and_the_scannable_deployables_and_not_its_own_ship_or_a_probe() {
        let (mut scene, own, other) = two_ships();
        scene.place_probe(
            ProbePlacement {
                probe_id: PROBE,
                solar_system_id: 30_000_142,
                name: "Core Scanner Probe I".to_string(),
                type_id: 30_013,
                group_id: 479,
                position: [1.0, 2.0, 3.0],
                radius: 40.0,
                owner_id: spec().owner_id,
                corporation_id: 0,
                alliance_id: 0,
                ship_id: own,
            },
            NOON,
        );
        scene.place_disruptor(
            DisruptorPlacement {
                deployable_id: DISRUPTOR,
                solar_system_id: 30_000_142,
                name: "Mobile Small Warp Disruptor I".to_string(),
                type_id: 12_198,
                group_id: 361,
                position: [5_000.0, 0.0, 0.0],
                radius: 70.0,
                range: 10_000.0,
                active: true,
                owner_id: spec().owner_id,
                corporation_id: 0,
                alliance_id: 0,
                ship_id: own,
            },
            NOON,
        );
        let targets = targets_for(&mut scene, A, NOON + 41 * SECOND);
        let ids: Vec<i64> = targets.iter().map(|t| t.id).collect();
        assert!(ids.contains(&other), "the other ship is a target: {targets:?}");
        assert!(ids.contains(&DISRUPTOR), "the mobile warp disruptor is a target: {targets:?}");
        assert!(!ids.contains(&own), "the pilot's own ship is not: {targets:?}");
        assert!(!ids.contains(&PROBE), "a scanner probe is not: {targets:?}");
        let ship = targets.iter().find(|t| t.id == other).unwrap();
        assert_eq!((ship.kind, ship.type_id), (KIND_SHIP, spec().type_id));
        let disruptor = targets.iter().find(|t| t.id == DISRUPTOR).unwrap();
        assert_eq!((disruptor.kind, disruptor.type_id), (KIND_DEPLOYABLE, 12_198));
    }

    #[test]
    fn a_cloaked_ship_is_not_offered_to_a_probe() {
        let (mut scene, _own, other) = two_ships();
        scene.pilots.get_mut(&B).unwrap().cloak = CloakState::Cloaked;
        let targets = targets_for(&mut scene, A, NOON + 41 * SECOND);
        assert!(targets.iter().all(|t| t.id != other), "a cloaked ship is not a target: {targets:?}");
    }
}
