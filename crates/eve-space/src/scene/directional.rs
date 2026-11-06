//! combat recon ships, the cloaked and the warping ships are not seen, nor the normal NPCs; the cosmic signatures and anomalies are not objects of the scene).
use crate::deployable::Role;
use crate::link::DirectionalTarget;
use crate::scene::{Command, Out, Scene, SessionId};
use crate::ship::CompanionKind;
use crate::visibility::CloakState;

/// The kinds of a directional target, as the wire numbers them.
pub const KIND_SHIP: u8 = 1;
pub const KIND_NPC: u8 = 2;
pub const KIND_DRONE: u8 = 3;
pub const KIND_FIGHTER: u8 = 4;
pub const KIND_PROBE: u8 = 5;
pub const KIND_WRECK: u8 = 6;
pub const KIND_CELESTIAL: u8 = 7;

impl Scene {
    /// Every object of the scene the pilot's directional scan can see at all (the host takes the cone out of them): the ships (a player's, an NPC's, the drones and
    /// fighter squadrons), the scanner probes, the wrecks, and the bodies of the system (planets, moons, belts, stations, stargates, the star). Not the pilot's own ship.
    pub(super) fn directional_targets(&mut self, session: SessionId, now: i64) -> Vec<Out> {
        let command = Command::DirectionalScan.code();
        let Some(own) = self.pilots.get(&session).map(|pilot| pilot.ship.spec.ship_id) else {
            return vec![Out::Refused { session, command, reason: "NO_SHIP_IN_SPACE" }];
        };
        let sessions: Vec<SessionId> = self.pilots.keys().copied().collect();
        for other in sessions {
            self.settle_pilot(other, now);
        }
        let mut targets = Vec::new();
        for pilot in self.pilots.values() {
            if pilot.ship.spec.ship_id == own {
                continue;
            }
            let kind = match pilot.ship.npc.as_ref() {
                None => KIND_SHIP,
                Some(npc) if npc.hull => KIND_SHIP,
                Some(npc) => match npc.drone.as_ref().map(|link| link.kind) {
                    Some(CompanionKind::Drone) => KIND_DRONE,
                    Some(CompanionKind::Fighter) => KIND_FIGHTER,
                    None => KIND_NPC,
                },
            };
            let p = pilot.ship.position;
            targets.push(DirectionalTarget {
                id: pilot.ship.spec.ship_id,
                kind,
                type_id: pilot.ship.spec.type_id,
                position: [p.x, p.y, p.z],
                radius: pilot.ship.spec.radius,
                cloaked: !matches!(pilot.cloak, CloakState::Uncloaked),
                warping: pilot.flight.is_some(),
            });
        }
        for deployable in self.deployables.values() {
            if deployable.role == Role::ScanProbe && deployable.ends_at > now {
                let p = deployable.position;
                targets.push(DirectionalTarget {
                    id: deployable.id,
                    kind: KIND_PROBE,
                    type_id: deployable.spec.charge_type_id,
                    position: [p.x, p.y, p.z],
                    radius: deployable.spec.radius,
                    cloaked: false,
                    warping: false,
                });
            }
        }
        for wreck in self.wrecks.values() {
            let p = wreck.position;
            targets.push(DirectionalTarget {
                id: wreck.id,
                kind: KIND_WRECK,
                type_id: wreck.kind.type_id,
                position: [p.x, p.y, p.z],
                radius: wreck.radius,
                cloaked: false,
                warping: false,
            });
        }
        for body in &self.system.bodies {
            let p = body.position;
            targets.push(DirectionalTarget {
                id: body.id,
                kind: KIND_CELESTIAL,
                type_id: body.type_id,
                position: [p.x, p.y, p.z],
                radius: body.radius,
                cloaked: false,
                warping: false,
            });
        }
        targets.sort_by_key(|target| target.id);
        vec![Out::DirectionalTargets { session, command, targets }]
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::link::{DirectionalTarget, ProbePlacement};
    use crate::scene::tests::{NOON, STATION, spec, system};

    const A: SessionId = 7;
    const B: SessionId = 8;
    const SECOND: i64 = 10_000_000;
    const PROBE: i64 = 990_000_000_001;

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

    fn targets_for(scene: &mut Scene, session: SessionId, at: i64) -> Vec<DirectionalTarget> {
        scene
            .command(session, Command::DirectionalScan, at)
            .into_iter()
            .find_map(|out| match out {
                Out::DirectionalTargets { targets, .. } => Some(targets),
                _ => None,
            })
            .expect("a directional answer")
    }

    #[test]
    fn the_scene_offers_the_ships_probes_wrecks_and_bodies_but_not_the_pilots_own_ship() {
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
        let targets = targets_for(&mut scene, A, NOON + 41 * SECOND);
        let ids: Vec<i64> = targets.iter().map(|t| t.id).collect();
        assert!(ids.contains(&other), "the other ship is offered: {targets:?}");
        assert!(!ids.contains(&own), "the pilot's own ship is not: {targets:?}");
        assert!(ids.contains(&PROBE), "a probe is offered: {targets:?}");
        let ship = targets.iter().find(|t| t.id == other).unwrap();
        assert_eq!((ship.kind, ship.type_id, ship.radius), (KIND_SHIP, spec().type_id, spec().radius));
        let probe = targets.iter().find(|t| t.id == PROBE).unwrap();
        assert_eq!(probe.kind, KIND_PROBE);
        // every body of the system is offered, as a celestial
        let bodies = scene_bodies_len(&scene);
        assert_eq!(targets.iter().filter(|t| t.kind == KIND_CELESTIAL).count(), bodies);
    }

    fn scene_bodies_len(scene: &Scene) -> usize {
        scene.system.bodies.len()
    }

    #[test]
    fn a_cloaked_ship_is_offered_with_its_cloak_so_the_host_leaves_it_out() {
        let (mut scene, _own, other) = two_ships();
        scene.pilots.get_mut(&B).unwrap().cloak = CloakState::Cloaked;
        let targets = targets_for(&mut scene, A, NOON + 41 * SECOND);
        let ship = targets.iter().find(|t| t.id == other).expect("the ship is in the answer");
        assert!(ship.cloaked);
    }

    #[test]
    fn a_pilot_with_no_ship_in_space_is_refused() {
        let mut scene = Scene::new(system());
        let out = scene.command(9, Command::DirectionalScan, NOON);
        assert!(matches!(out.last(), Some(Out::Refused { reason: "NO_SHIP_IN_SPACE", .. })), "{out:?}");
    }
}
