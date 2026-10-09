use eve_destiny::encode_entity_ball;
use eve_marshal::Value;

use super::*;
use crate::scene::tests::{NOON, STATION, spec, system};
use crate::scene::{Command, Departure};
use crate::sim::Sim;

const PLAYER: SessionId = 7;
const SECOND: i64 = 10_000_000;

/// The spec of the Guristas frigate the reference recorded around the gates (`oracle/fixtures/scenarios/jump.json`, item 980050000055).
fn frigate() -> ShipSpec {
    let mut npc = spec();
    npc.ship_id = 980_050_000_055;
    npc.character_id = 0;
    npc.owner_id = 1_000_125;
    npc.corporation_id = 1_000_125;
    npc.alliance_id = 0;
    npc.war_faction_id = 500_006;
    npc.type_id = 3863;
    npc.group_id = 301;
    npc.category_id = 11;
    npc.name = String::new();
    npc.security_status = 5.0;
    npc.radius = 38.0;
    npc.mass = 1_200_000.0;
    npc.max_velocity = 525.0;
    npc.agility = 3.1;
    npc.modules = Vec::new();
    npc
}

fn with_a_player() -> Scene {
    let mut scene = Scene::new(system());
    scene.undock(PLAYER, spec(), STATION, NOON).unwrap();
    scene.bind(PLAYER, NOON);
    scene
}

fn near_the_player(scene: &mut Scene) -> NpcPlace {
    let position = scene.ship(PLAYER, NOON).unwrap().position;
    NpcPlace::at_rest(Vec3::new(position.x + 5_000.0, position.y, position.z))
}

fn action_names(out: &Out) -> Vec<String> {
    let Out::Notify { args, .. } = out else { return Vec::new() };
    let Some(Value::List(actions)) = args.first() else { return Vec::new() };
    actions
        .iter()
        .map(|action| {
            let Value::Tuple(pair) = action else { panic!("{action:?}") };
            let Value::Tuple(inner) = &pair[1] else { panic!("{:?}", pair[1]) };
            let Value::Str(name) = &inner[0] else { panic!("{:?}", inner[0]) };
            name.to_string()
        })
        .collect()
}

/// The ball the reference sent for item 980050000055 (an orbit around 50016630 at no distance, 525 m/s at full speed), byte for byte.
const RECORDED_BALL: &str = "b738882fe400000004000018426923f0c27d814a4212329eb98bbf01c2adea4ea429ad7b420100000000804f324100ffffffffffffffffbd420f000000000000400344d2e6301b6ddb76c0caebec76338876c05214e49ade133d40666646400000803fff7631fb020000000000000000";

fn hex(text: &str) -> Vec<u8> {
    (0..text.len()).step_by(2).map(|i| u8::from_str_radix(&text[i..i + 2], 16).unwrap()).collect()
}

#[test]
fn an_npc_ball_is_the_one_the_reference_sent_byte_for_byte() {
    let mut spec = frigate();
    spec.corporation_id = 1_000_125;
    let ship = Ship::new(
        spec,
        Vec3::new(227_683_304_928.276_64, -9_528_506_163.774_448, 1_901_908_870_382.667_2),
        Vec3::new(-365.714_137_259_504_35, -360.512_564_588_044_03, 29.077_615_433_391_04),
        Vec3::new(1.0, 0.0, 0.0),
        1.0,
        Course::Orbit { target: 50_016_630, distance: 0.0 },
        NOON,
    )
    .as_npc(NpcIdentity::default());
    assert_eq!(encode_entity_ball(&ship.ball()).unwrap(), hex(RECORDED_BALL));
}

#[test]
fn the_same_ship_flown_by_a_player_is_interactive_and_has_a_session() {
    let ship = Ship::new(spec(), Vec3::ZERO, Vec3::ZERO, Vec3::new(1.0, 0.0, 0.0), 0.0, Course::Stop, NOON);
    assert!(ship.ball().has_session);
    assert_eq!(ship.ball().free_ball_flags(), 0x9);
    let npc = ship.as_npc(NpcIdentity::default());
    assert!(!npc.ball().has_session);
    assert_eq!(npc.ball().free_ball_flags(), 0x1);
}

#[test]
fn an_npcs_slim_item_has_no_name_and_ends_with_the_response_thresholds() {
    let ship =
        Ship::new(frigate(), Vec3::ZERO, Vec3::ZERO, Vec3::new(1.0, 0.0, 0.0), 0.0, Course::Stop, NOON)
            .as_npc(NpcIdentity::default());
    let entries = crate::slim::ship_entries(&ship);
    let keys: Vec<&str> = entries.iter().map(|(key, _)| *key).collect();
    assert_eq!(
        keys,
        [
            "itemID",
            "typeID",
            "ownerID",
            "groupID",
            "categoryID",
            "corpID",
            "allianceID",
            "warFactionID",
            "charID",
            "kills",
            "skinMaterialSetID",
            "modules",
            "securityStatus",
            "bounty",
            "hostile_response_threshold",
            "friendly_response_threshold",
        ]
    );
    let value = |name: &str| entries.iter().find(|(key, _)| *key == name).unwrap().1.clone();
    assert_eq!(value("securityStatus"), Value::Int(5));
    assert_eq!(value("hostile_response_threshold"), Value::Int(-11));
    assert_eq!(value("friendly_response_threshold"), Value::Int(11));
    assert_eq!(value("charID"), Value::Int(0));
    let player = crate::slim::ship_entries(&Ship::new(
        spec(),
        Vec3::ZERO,
        Vec3::ZERO,
        Vec3::new(1.0, 0.0, 0.0),
        0.0,
        Course::Stop,
        NOON,
    ));
    assert!(player.iter().any(|(key, _)| *key == "name"));
    assert!(player.iter().all(|(key, _)| !key.ends_with("_response_threshold")));
}

#[test]
fn a_player_who_has_bound_the_ballpark_is_sent_an_npc_that_comes_into_view() {
    let mut scene = with_a_player();
    let place = near_the_player(&mut scene);
    let (session, told) = scene.spawn_npc(frigate(), NpcIdentity::default(), place, NOON + SECOND);
    assert!(is_npc_session(session));
    assert_eq!(told.len(), 1);
    assert_eq!(told[0].session(), PLAYER);
    assert_eq!(action_names(&told[0]), ["AddBalls2", "SetBallAgility", "SetBallMass", "SetMaxSpeed"]);
    assert!(scene.contains(session));
    assert_eq!(scene.npc_sessions(), [session]);
    assert_eq!(scene.player_sessions(), [PLAYER]);
}

#[test]
fn an_npc_is_in_the_state_a_player_binds_later() {
    let mut scene = Scene::new(system());
    scene.undock(PLAYER, spec(), STATION, NOON).unwrap();
    let place = near_the_player(&mut scene);
    let (_, told) = scene.spawn_npc(frigate(), NpcIdentity::default(), place, NOON);
    assert!(told.is_empty(), "a player that has not bound is told nothing");
    let bound = scene.bind(PLAYER, NOON);
    let first = &bound[2];
    assert!(action_names(first).contains(&"AddBalls2".to_string()));
    let ships = scene.ships_in_view(PLAYER);
    assert_eq!(ships.len(), 2);
    assert_eq!(ships[1].spec.ship_id, 980_050_000_055);
}

#[test]
fn an_npc_out_of_view_is_not_told_to_a_player() {
    let mut scene = with_a_player();
    let far = NpcPlace::at_rest(Vec3::new(1.0e12, 0.0, 0.0));
    let (session, told) = scene.spawn_npc(frigate(), NpcIdentity::default(), far, NOON);
    assert!(told.is_empty());
    assert!(scene.contains(session));
}

#[test]
fn an_npc_that_is_ordered_about_moves_and_the_player_who_sees_it_is_told() {
    let mut scene = with_a_player();
    let place = near_the_player(&mut scene);
    let (npc, _) = scene.spawn_npc(frigate(), NpcIdentity::default(), place, NOON);
    let told = scene.command(npc, Command::SetSpeedFraction(1.0), NOON + SECOND);
    let to_the_player: Vec<&Out> = told.iter().filter(|out| out.session() == PLAYER).collect();
    assert!(!to_the_player.is_empty(), "the watcher hears of it: {told:?}");
    assert!(told.iter().any(|out| out.session() == npc), "the scene addresses the NPC as it does any ship");
    let ship = scene.ship(npc, NOON + 3 * SECOND).unwrap();
    assert!(ship.velocity.x > 0.0 || ship.velocity.y != 0.0 || ship.velocity.z != 0.0);
}

#[test]
fn a_player_can_lock_an_npc_at_once_as_an_npc_has_no_undocking_protection() {
    let mut scene = with_a_player();
    let place = near_the_player(&mut scene);
    scene.spawn_npc(frigate(), NpcIdentity::default(), place, NOON);
    let told = scene.target_order(PLAYER, crate::targeting::Order::Lock(980_050_000_055), NOON + SECOND);
    assert!(
        told.iter().any(|out| matches!(out, Out::Targets { pending: true, .. })),
        "the lock is going: {told:?}"
    );
}

#[test]
fn a_ship_can_approach_orbit_and_align_to_another_ship_and_not_to_itself_or_to_nothing() {
    let mut scene = with_a_player();
    scene.command(PLAYER, Command::Stop, NOON);
    let place = near_the_player(&mut scene);
    let (npc, _) = scene.spawn_npc(frigate(), NpcIdentity::default(), place, NOON);
    let refusal = |told: Vec<Out>| {
        told.into_iter().find_map(|out| match out {
            Out::Refused { reason, .. } => Some(reason),
            _ => None,
        })
    };
    let follow = |target_id| Command::FollowBall { target_id, range: 500.0 };
    assert_eq!(refusal(scene.command(npc, follow(1), NOON + SECOND)), Some("UNKNOWN_TARGET"));
    assert_eq!(
        refusal(scene.command(npc, follow(980_050_000_055), NOON + SECOND)),
        Some("UNKNOWN_TARGET"),
        "itself"
    );
    assert_eq!(
        refusal(scene.command(npc, Command::Orbit { target_id: 1, distance: 100.0 }, NOON + SECOND)),
        Some("UNKNOWN_TARGET")
    );
    assert_eq!(
        refusal(scene.command(npc, Command::AlignTo { target_id: 1 }, NOON + SECOND)),
        Some("UNKNOWN_TARGET")
    );
    let player_ship = spec().ship_id;
    assert_eq!(refusal(scene.command(npc, follow(player_ship), NOON + SECOND)), None);
    assert!(
        matches!(scene.ship(npc, NOON + SECOND).unwrap().course, Course::Follow { target, .. } if target == player_ship)
    );
    assert_eq!(
        refusal(scene.command(npc, Command::AlignTo { target_id: player_ship }, NOON + 2 * SECOND)),
        None
    );
    assert_eq!(
        refusal(scene.command(
            npc,
            Command::Orbit { target_id: player_ship, distance: 2_000.0 },
            NOON + 3 * SECOND
        )),
        None
    );
}

#[test]
fn a_ship_that_follows_another_ship_comes_to_rest_at_the_range_it_was_given() {
    let mut scene = with_a_player();
    scene.command(PLAYER, Command::Stop, NOON);
    let mut place = near_the_player(&mut scene);
    place.position.x += 20_000.0;
    let (npc, _) = scene.spawn_npc(frigate(), NpcIdentity::default(), place, NOON);
    scene.command(npc, Command::FollowBall { target_id: spec().ship_id, range: 1_000.0 }, NOON);
    let end = NOON + 120 * SECOND;
    let mut now = NOON;
    while now < end {
        now += SECOND / 4;
        scene.poll(now);
    }
    let there = scene.ship(npc, end).unwrap().clone();
    let player = scene.ship(PLAYER, end).unwrap().clone();
    let gap = ((there.position.x - player.position.x).powi(2)
        + (there.position.y - player.position.y).powi(2)
        + (there.position.z - player.position.z).powi(2))
    .sqrt();
    let surface = gap - there.spec.radius - player.spec.radius;
    assert!((surface - 1_000.0).abs() < 50.0, "the surfaces are a kilometre apart: {surface}");
}

#[test]
fn a_ship_that_follows_a_ship_that_moves_keeps_up_with_it() {
    let mut scene = with_a_player();
    let place = near_the_player(&mut scene);
    let mut fast = frigate();
    fast.max_velocity = 800.0;
    let (npc, _) = scene.spawn_npc(fast, NpcIdentity::default(), place, NOON);
    scene.command(npc, Command::FollowBall { target_id: spec().ship_id, range: 500.0 }, NOON);
    let end = NOON + 90 * SECOND;
    let mut now = NOON;
    while now < end {
        now += SECOND / 4;
        scene.poll(now);
    }
    let there = scene.ship(npc, end).unwrap().clone();
    let player = scene.ship(PLAYER, end).unwrap().clone();
    let gap = ((there.position.x - player.position.x).powi(2)
        + (there.position.y - player.position.y).powi(2)
        + (there.position.z - player.position.z).powi(2))
    .sqrt();
    assert!(gap < 1_500.0, "the player flies away at 339 m/s and the follower keeps up: {gap}");
    assert!(player.velocity.x.abs() + player.velocity.y.abs() + player.velocity.z.abs() > 100.0);
}

#[test]
fn an_npc_that_leaves_is_taken_out_of_the_view_of_the_player() {
    let mut scene = with_a_player();
    let place = near_the_player(&mut scene);
    let (npc, _) = scene.spawn_npc(frigate(), NpcIdentity::default(), place, NOON);
    let told = scene.remove_npc(npc, NOON + SECOND).unwrap();
    assert_eq!(told.len(), 1);
    assert_eq!(told[0].session(), PLAYER);
    assert_eq!(action_names(&told[0]), ["RemoveBalls"]);
    assert!(!scene.contains(npc));
    assert!(scene.remove_npc(npc, NOON + SECOND).is_none());
    assert!(scene.remove_npc(PLAYER, NOON + SECOND).is_none(), "a player is not an NPC");
    assert!(scene.contains(PLAYER));
}

#[test]
fn a_scene_with_only_npcs_in_it_is_empty() {
    let mut scene = Scene::new(system());
    assert!(scene.is_empty());
    let (npc, _) = scene.spawn_npc(frigate(), NpcIdentity::default(), NpcPlace::at_rest(Vec3::ZERO), NOON);
    assert!(scene.is_empty(), "nobody to see them");
    assert!(scene.contains(npc));
    scene.undock(PLAYER, spec(), STATION, NOON).unwrap();
    assert!(!scene.is_empty());
    scene.leave(PLAYER, Departure::LoggedOff, NOON).unwrap();
    assert!(scene.is_empty());
}

#[test]
fn npcs_are_numbered_from_the_base_in_the_order_they_are_made() {
    let mut scene = Scene::new(system());
    let (a, _) = scene.spawn_npc(frigate(), NpcIdentity::default(), NpcPlace::at_rest(Vec3::ZERO), NOON);
    let (b, _) = scene.spawn_npc(frigate(), NpcIdentity::default(), NpcPlace::at_rest(Vec3::ZERO), NOON);
    assert_eq!((a, b), (NPC_SESSION_BASE + 1, NPC_SESSION_BASE + 2));
    assert_eq!(scene.npc_sessions(), [a, b]);
    assert!(!is_npc_session(NPC_SESSION_BASE - 1));
    assert!(is_npc_session(NPC_SESSION_BASE));
}

#[test]
fn the_host_is_told_nothing_that_is_for_an_npc() {
    let for_the_player = Out::Refused { session: PLAYER, command: 1, reason: "X" };
    let for_an_npc = Out::Refused { session: NPC_SESSION_BASE + 1, command: 1, reason: "X" };
    let messages = Sim::messages([for_an_npc.clone(), for_the_player.clone(), for_an_npc]);
    assert_eq!(messages.len(), 1);
}
