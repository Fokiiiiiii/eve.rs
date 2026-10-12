//! Pochven: the NPCs of its four factions fight each other. The rule is `pochven.rs`, read by `SceneWorld::standings_from_npc_to_item`; the belt rat's
//! tree does the rest (it finds what is hostile to it in its sensor's range and fights it with its weapon).

use std::sync::Arc;

use eve_destiny::Vec3;

use super::POCHVEN_NPC;
use super::rat_tests::{rat_identity, rat_spec};
use crate::health::Vitals;
use crate::npc::NpcPlace;
use crate::scene::tests::{NOON, STATION};
use crate::scene::{Scene, SessionId, TICKS_PER_SECOND};
use crate::ship::{NpcIdentity, ShipSpec};
use crate::world::{Body, BodyKind, Detail, SolarSystem};

const TRIGLAVIAN_CORPORATION: i64 = 1_000_298;
const ROGUE_DRONE_CORPORATION: i64 = 1_000_287;
const POCHVEN_SYSTEM: i32 = 30_000_021;
const ATTACKER_SHIP: i64 = 980_060_000_001;
const TARGET_SHIP: i64 = 980_060_000_002;
const BOX_CENTRE: f64 = 3_932_160.0;

/// A system of the given region, with a station in it (the belt rat tests' shape).
fn system_in(region_id: i32) -> Arc<SolarSystem> {
    Arc::new(SolarSystem {
        // a system that is not a zone outside Pochven (the zones are data: `pochven_population`)
        id: if region_id == 10_000_070 { POCHVEN_SYSTEM } else { 30_000_001 },
        name: "Pochven test".to_string(),
        constellation_id: 20_000_787,
        region_id,
        faction_id: Some(500_026),
        security: -1.0,
        bodies: vec![Body {
            id: STATION,
            type_id: 1531,
            group_id: 15,
            category_id: 3,
            owner_id: 1_000_001,
            name: "Pochven test I".to_string(),
            kind: BodyKind::Station,
            position: Vec3::new(3_900_000.0, 3_900_000.0, 3_900_000.0),
            radius: 1_000.0,
            graphic_id: None,
            detail: Detail::Station { corporation_id: 1_000_001 },
        }],
    })
}

/// The attacker's identity: a rat's weapon, fitted to the attacker's own ship (the weapon's module is the ship, as on the belt rat).
fn attacker_identity() -> NpcIdentity {
    let mut identity = rat_identity(15_000.0);
    for weapon in &mut identity.combat.weapons {
        weapon.module_id = ATTACKER_SHIP;
    }
    identity
}

/// An NPC ship of a corporation, with hit points to lose.
fn npc(corporation: i64, ship_id: i64) -> ShipSpec {
    let mut ship = rat_spec();
    ship.ship_id = ship_id;
    ship.owner_id = corporation;
    ship.corporation_id = corporation as i32;
    ship.vitals =
        Vitals { shield_capacity: 5_000.0, armor_hp: 5_000.0, structure_hp: 5_000.0, ..Vitals::default() };
    ship
}

/// The points a ship has left, at `now`: the live damage state (`spec.vitals` is only the loadout).
fn points(scene: &Scene, session: SessionId, now: i64) -> f64 {
    let state = scene.pilots[&session].ship.health.state_at(now);
    state.shield + state.armor + state.structure
}

/// A Triglavian belt-rat-style attacker 8 km from a target of `target_corporation`, run for 60 s; gives the target's points before and after.
fn fight(region_id: i32, target_corporation: i64) -> (f64, f64) {
    let mut scene = Scene::new(system_in(region_id));
    // the middle of one visibility box (`GRID_BOX` is 7,864,320 m, and a box edge is the origin): a fight that stays in one bubble
    let attacker_place = NpcPlace::at_rest(Vec3::new(BOX_CENTRE, BOX_CENTRE, BOX_CENTRE));
    let target_place = NpcPlace::at_rest(Vec3::new(BOX_CENTRE + 8_000.0, BOX_CENTRE, BOX_CENTRE));
    let (attacker, _) = scene
        .spawn_npc_with_behavior(
            npc(TRIGLAVIAN_CORPORATION, ATTACKER_SHIP),
            attacker_identity(),
            attacker_place,
            POCHVEN_NPC,
            NOON,
        )
        .expect("the Pochven NPC tree is given");
    let (target, _) =
        scene.spawn_npc(npc(target_corporation, TARGET_SHIP), NpcIdentity::default(), target_place, NOON);
    let before = points(&scene, target, NOON);
    let mut now = NOON;
    while now <= NOON + 120 * TICKS_PER_SECOND {
        scene.poll(now);
        now += TICKS_PER_SECOND / 4;
    }
    assert_eq!(scene.brains().expect("brains").faults(attacker), []);
    (before, points(&scene, target, now))
}

#[test]
fn a_triglavian_npc_attacks_a_rogue_drone_npc_in_pochven() {
    let (before, after) = fight(10_000_070, ROGUE_DRONE_CORPORATION);
    assert!(after < before, "the rogue drone was not hit: {before} -> {after}");
}

#[test]
fn an_npc_does_not_attack_an_npc_of_its_own_faction_in_pochven() {
    let (before, after) = fight(10_000_070, TRIGLAVIAN_CORPORATION);
    assert_eq!(before, after);
}

#[test]
fn factions_do_not_attack_each_other_outside_pochven() {
    let (before, after) = fight(10_000_001, ROGUE_DRONE_CORPORATION);
    assert_eq!(before, after);
}
