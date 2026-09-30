//! A belt rat in a scene, beside a player: it sees the player come near, locks, shoots, and what the player is told is what the client is told when
//! an NPC attacks it on the reference's server (`oracle/fixtures/scenarios/health.json` has the shapes).

use std::sync::Arc;

use eve_destiny::Vec3;
use eve_marshal::Value;

use super::{BELT_RAT, RAT_TAG};
use crate::npc::NpcPlace;
use crate::scene::tests::{NOON, STATION, spec};
use crate::scene::{Out, Scene, SessionId, TICKS_PER_SECOND};
use crate::ship::{NpcCombat, NpcIdentity, NpcWeapon, ShipSpec};
use crate::weapons::Turret;
use crate::world::{Body, BodyKind, Detail, SolarSystem};

pub(super) const PLAYER: SessionId = 7;
pub(super) const SECOND: i64 = TICKS_PER_SECOND;
pub(super) const RAT_SHIP: i64 = 980_050_000_077;
/// The moment the rat comes: the player has left the station and is no longer under its undock protection (30 s).
pub(super) const LATER: i64 = NOON + 31 * SECOND;

/// A system whose station is in the middle of a box of the grid, so that the ships that fight beside it stay in each other's view (the boxes of the
/// grid are the bubbles, DEVIATIONS #44; the test system of the other modules has its station on the edge of eight of them).
pub(super) fn system() -> Arc<SolarSystem> {
    Arc::new(SolarSystem {
        id: 30_000_001,
        name: "Testing".to_string(),
        constellation_id: 20_000_001,
        region_id: 10_000_001,
        faction_id: None,
        security: 0.9,
        bodies: vec![Body {
            id: STATION,
            type_id: 1531,
            group_id: 15,
            category_id: 3,
            owner_id: 1_000_001,
            name: "Testing I - Trade Hub".to_string(),
            kind: BodyKind::Station,
            position: Vec3::new(3_900_000.0, 3_900_000.0, 3_900_000.0),
            radius: 1_000.0,
            graphic_id: None,
            detail: Detail::Station { corporation_id: 1_000_001 },
        }],
    })
}

pub(super) fn rat_spec() -> ShipSpec {
    let mut rat = spec();
    rat.ship_id = RAT_SHIP;
    rat.character_id = 0;
    rat.owner_id = 1_000_125;
    rat.corporation_id = 1_000_125;
    rat.category_id = 11;
    rat.name = String::new();
    rat.max_velocity = 350.0;
    rat.max_target_range = 60_000.0;
    rat.max_locked_targets = 3;
    rat.scan_resolution = 400.0;
    rat.signature_radius = 35.0;
    rat.capacitor_capacity = 500.0;
    rat
}

pub(super) fn rat_identity(aggression_range: f64) -> NpcIdentity {
    NpcIdentity {
        combat: NpcCombat {
            aggression_range,
            weapons: vec![NpcWeapon {
                module_id: RAT_SHIP,
                effect_id: 10,
                cycle_ms: 4_000.0,
                capacitor_need: 0.0,
                turret: Turret {
                    weapon_type_id: 5_678,
                    fx: "effects.Laser".to_string(),
                    raw_damage: [0.0, 0.0, 6.0, 4.0],
                    optimal_range: 20_000.0,
                    falloff: 10_000.0,
                    tracking_speed: 0.4,
                    signature_resolution: 40.0,
                },
            }],
        },
        ..NpcIdentity::default()
    }
}

pub(super) fn scene_with_a_player() -> Scene {
    let mut scene = Scene::new(system());
    scene.undock(PLAYER, spec(), STATION, NOON).unwrap();
    scene.bind(PLAYER, NOON);
    scene
}

pub(super) fn place_near(scene: &mut Scene, metres: f64) -> NpcPlace {
    let position = scene.ship(PLAYER, NOON).unwrap().position;
    NpcPlace::at_rest(Vec3::new(position.x + metres, position.y, position.z))
}

/// Polls every quarter of a second and gives what the player was told.
pub(super) fn run(scene: &mut Scene, from: i64, seconds: i64) -> Vec<Out> {
    let mut told = Vec::new();
    let mut now = from;
    while now <= from + seconds * SECOND {
        told.extend(scene.poll(now).into_iter().filter(|out| out.session() == PLAYER));
        now += SECOND / 4;
    }
    told
}

pub(super) fn notified<'a>(told: &'a [Out], name: &str) -> Vec<&'a Vec<Value>> {
    told.iter()
        .filter_map(|out| match out {
            Out::Notify { name: n, args, .. } if *n == name => Some(args),
            _ => None,
        })
        .collect()
}

/// The arguments of the destiny actions of that name that the player was told.
pub(super) fn actions_named(told: &[Out], wanted: &str) -> Vec<Vec<Value>> {
    let mut found = Vec::new();
    for args in notified(told, "DoDestinyUpdate") {
        let Some(Value::List(actions)) = args.first() else { continue };
        for action in actions {
            let Value::Tuple(pair) = action else { continue };
            let Value::Tuple(inner) = &pair[1] else { continue };
            let (Value::Str(name), Value::Tuple(arguments)) = (&inner[0], &inner[1]) else { continue };
            if name == wanted {
                found.push(arguments.clone());
            }
        }
    }
    found
}

fn field<'a>(message: &'a Value, key: &str) -> &'a Value {
    let Value::Dict(entries) = message else { panic!("not a dict: {message:?}") };
    entries
        .iter()
        .find(|(k, _)| matches!(k, Value::Str(name) if name == key))
        .map(|(_, v)| v)
        .unwrap_or_else(|| panic!("no {key}"))
}

#[test]
fn a_rat_that_was_given_its_tree_runs_it_without_raising_anything_and_has_a_sensor_of_the_range_it_attacks_at()
 {
    let mut scene = scene_with_a_player();
    let place = place_near(&mut scene, 60_000.0);
    let (rat, _) =
        scene.spawn_npc_with_behavior(rat_spec(), rat_identity(15_000.0), place, BELT_RAT, LATER).unwrap();
    run(&mut scene, LATER, 20);
    let brains = scene.brains().unwrap();
    assert_eq!(brains.faults(rat), []);
    let sensor = &brains.state.sensors[&RAT_SHIP];
    assert_eq!(
        (sensor.detection_range, sensor.tags.as_slice()),
        (15_000.0, [RAT_TAG.to_string()].as_slice())
    );
}

#[test]
fn a_rat_that_does_not_attack_what_comes_near_has_no_sensor() {
    let mut scene = scene_with_a_player();
    let place = place_near(&mut scene, 8_000.0);
    scene.spawn_npc_with_behavior(rat_spec(), rat_identity(0.0), place, BELT_RAT, LATER).unwrap();
    assert!(scene.brains().unwrap().state.sensors.is_empty());
    let told = run(&mut scene, LATER, 30);
    assert!(
        notified(&told, "OnTarget").is_empty() && notified(&told, "OnDamageMessage").is_empty(),
        "{told:?}"
    );
}

#[test]
fn a_rat_leaves_a_player_alone_who_is_beyond_its_range() {
    let mut scene = scene_with_a_player();
    let place = place_near(&mut scene, 40_000.0);
    scene.spawn_npc_with_behavior(rat_spec(), rat_identity(15_000.0), place, BELT_RAT, LATER).unwrap();
    let told = run(&mut scene, LATER, 30);
    assert!(
        notified(&told, "OnTarget").is_empty() && notified(&told, "OnDamageMessage").is_empty(),
        "{told:?}"
    );
}

#[test]
fn a_player_who_has_just_undocked_is_not_attacked_while_the_protection_lasts_and_is_when_it_is_over() {
    let mut scene = scene_with_a_player();
    let place = place_near(&mut scene, 8_000.0);
    let (rat, _) =
        scene.spawn_npc_with_behavior(rat_spec(), rat_identity(15_000.0), place, BELT_RAT, NOON).unwrap();
    let protected = run(&mut scene, NOON, 29);
    assert!(
        notified(&protected, "OnTarget").is_empty() && notified(&protected, "OnDamageMessage").is_empty(),
        "{protected:?}"
    );
    // the tree is told that the protection is over, starts afresh, and the rat goes for the player
    let after = run(&mut scene, NOON + 30 * SECOND, 40);
    assert_eq!(scene.brains().unwrap().faults(rat), []);
    assert!(!notified(&after, "OnTarget").is_empty(), "{after:?}");
    assert!(!notified(&after, "OnDamageMessage").is_empty(), "{after:?}");
}

#[test]
fn a_rat_locks_and_shoots_a_player_who_came_near_and_the_player_is_told_what_the_client_is_told_on_the_reference()
 {
    let mut scene = scene_with_a_player();
    let place = place_near(&mut scene, 8_000.0);
    let (rat, _) =
        scene.spawn_npc_with_behavior(rat_spec(), rat_identity(15_000.0), place, BELT_RAT, LATER).unwrap();
    let told = run(&mut scene, LATER, 40);
    assert_eq!(scene.brains().unwrap().faults(rat), []);

    // it locked the player: the player is told a ship has locked it
    let locks = notified(&told, "OnTarget");
    assert_eq!(locks, [&vec![Value::Str("otheradd".into()), Value::Long(RAT_SHIP)]]);

    // its weapon is seen running at the player: `OnSpecialFX(ship, module, weapon type, target, charge, fx, offensive, start, active, duration, repeat)`
    let fx = actions_named(&told, "OnSpecialFX");
    assert!(!fx.is_empty(), "{told:?}");
    let first = &fx[0];
    assert_eq!(
        first[..4],
        [Value::Long(RAT_SHIP), Value::Long(RAT_SHIP), Value::Int(5_678), Value::Long(spec().ship_id)]
    );
    assert_eq!(first[5], Value::Str("effects.Laser".into()));
    assert_eq!(first[9], Value::Int(4_000));

    // and it hit: one message of damage for a shot in each cycle of four seconds, told as an attack of another player's weapon
    let hits = notified(&told, "OnDamageMessage");
    assert!(hits.len() >= 5, "{} hits in 40 s", hits.len());
    let message = &hits[0][0];
    assert_eq!(field(message, "attackType"), &Value::Str("otherPlayerWeapons".into()));
    assert_eq!(field(message, "source"), &Value::Long(RAT_SHIP));
    assert_eq!(field(message, "target"), &Value::Long(spec().ship_id));
    assert_eq!(field(message, "weapon"), &Value::Int(5_678));

    // the player's ship is the worse for it
    let shield = scene.pilots[&PLAYER].ship.health.layers_at(LATER + 40 * SECOND).shield;
    assert!(shield < spec().vitals.shield_capacity, "shield {shield}");
}
