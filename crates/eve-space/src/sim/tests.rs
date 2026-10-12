use super::*;
use crate::health::Condition;
use crate::scene::tests::{NOON, SESSION, STATION, spec, system};
use crate::world::SolarSystem;

const SYSTEM: i32 = 30_000_001;
const SECOND: i64 = 10_000_000;

/// A system whose station is big enough that a ship leaving it is at once within docking range, as in the reference.
fn big_station() -> SolarSystem {
    let mut big = (*system()).clone();
    big.bodies[0].radius = 70_000.0;
    big
}

fn sim() -> Sim {
    Sim::new(Arc::new(Universe::with_systems([big_station()])))
}

fn enter(session: SessionId, station_id: i64, system: i32) -> Message {
    Message::Enter(Box::new(Enter {
        session_id: session,
        solar_system_id: system,
        how: How::Undock,
        station_id,
        ship: spec(),
        position: None,
        direction: None,
    }))
}

fn undocked() -> Sim {
    let mut sim = sim();
    let reply = sim.handle(enter(SESSION, STATION, SYSTEM), NOON).unwrap();
    assert_eq!(reply, [Message::EnterReply { session_id: SESSION, outcome: Ok(()) }]);
    sim
}

fn afterburner(module: i64) -> crate::modules::Effect {
    crate::modules::Effect {
        rules: Default::default(),
        burns_fuel: false,
        module_id: module,
        module_type: 21857,
        effect_id: 6731,
        fx: "effects.Afterburner".to_string(),
        duration_ms: 10_000.0,
        capacitor_need: 5.0,
        repeat: 1000,
        target_id: 0,
        turret: None,
        action: None,
        on: None,
        off: None,
        on_changes: Vec::new(),
        off_changes: Vec::new(),
    }
}

/// A request for an NPC that leaves a wreck, `at` a place.
fn spawn(request_id: u64, system: i32, ship_id: i64, at: [f64; 3]) -> Message {
    let mut ship = spec();
    ship.ship_id = ship_id;
    ship.character_id = 0;
    ship.owner_id = 1_000_125;
    ship.category_id = 11;
    Message::Spawn(Box::new(crate::link::Spawn {
        request_id,
        solar_system_id: system,
        ship,
        anchor_ship_id: 0,
        position: at,
        velocity: [0.0; 3],
        direction: [1.0, 0.0, 0.0],
        behavior: String::new(),
        identity: crate::ship::NpcIdentity {
            wreck: Some(crate::wreck::WreckKind {
                type_id: 26_506,
                name: "Caldari Frigate Wreck".into(),
                graphic_id: Some(3117),
            }),
            ..crate::ship::NpcIdentity::default()
        },
    }))
}

/// Where the player's ship is, plus 500 m along x.
fn beside_the_player(sim: &mut Sim) -> [f64; 3] {
    let at = sim.scenes.get_mut(&SYSTEM).unwrap().ship(SESSION, NOON).unwrap().position;
    [at.x + 500.0, at.y, at.z]
}

fn place_player_outside_station(sim: &mut Sim) {
    let scene = sim.scenes.get_mut(&SYSTEM).unwrap();
    let station = scene.system().body(STATION).unwrap().clone();
    let pilot = scene.pilots.get_mut(&SESSION).unwrap();
    let direction = pilot.ship.direction;
    let distance = station.radius + pilot.ship.spec.radius + 1_000.0;
    pilot.ship.position = eve_destiny::Vec3::new(
        station.position.x + direction.x * distance,
        station.position.y + direction.y * distance,
        station.position.z + direction.z * distance,
    );
    pilot.ship.velocity = eve_destiny::Vec3::ZERO;
    pilot.ship.speed_fraction = 0.0;
    pilot.ship.course = crate::ship::Course::Stop;
}

#[test]
fn an_npc_the_host_asks_for_comes_into_the_scene_of_the_system_and_is_answered_with_its_session() {
    let mut sim = undocked();
    sim.handle(Message::Bind { session_id: SESSION }, NOON).unwrap();
    let at = beside_the_player(&mut sim);
    let reply = sim.handle(spawn(5, SYSTEM, 980_050_000_001, at), NOON + 60 * SECOND).unwrap();
    assert!(
        matches!(reply[0], Message::SpawnReply { request_id: 5, outcome: Ok(session) } if is_npc_session(session)),
        "{reply:?}"
    );
    // the player who sees it is told; nobody is told anything for the NPC
    assert!(reply[1..].iter().all(|m| matches!(m, Message::Notify { session_id: SESSION, .. })), "{reply:?}");
    assert_eq!(names(&reply[1..]), ["DoDestinyUpdate"]);
    assert_eq!(sim.ships(), 1, "an NPC is not a session of the host's");
}

#[test]
fn an_npc_the_host_places_from_a_ship_comes_that_far_from_where_the_ship_is_and_one_placed_from_a_ship_that_is_not_there_is_refused()
 {
    let mut sim = undocked();
    place_player_outside_station(&mut sim);
    let there = sim.scenes.get_mut(&SYSTEM).unwrap().ship(SESSION, NOON).unwrap().position;
    let Message::Spawn(mut request) = spawn(9, SYSTEM, 980_050_000_005, [0.0; 3]) else { unreachable!() };
    request.anchor_ship_id = spec().ship_id;
    request.position = [3_000.0, -200.0, 50.0];
    let reply = sim.handle(Message::Spawn(request.clone()), NOON).unwrap();
    assert!(
        matches!(reply[0], Message::SpawnReply { outcome: Ok(session), .. } if is_npc_session(session)),
        "{reply:?}"
    );
    let scene = sim.scenes.get_mut(&SYSTEM).unwrap();
    let npc = scene.session_of(980_050_000_005).unwrap();
    let at = scene.ship(npc, NOON).unwrap().position;
    assert_eq!((at.x - there.x, at.y - there.y, at.z - there.z), (3_000.0, -200.0, 50.0));
    request.anchor_ship_id = 12;
    request.ship.ship_id = 980_050_000_006;
    let refused = sim.handle(Message::Spawn(request), NOON).unwrap();
    assert_eq!(refused, [Message::SpawnReply { request_id: 9, outcome: Err("NO_SHIP 12".to_string()) }]);
}

#[test]
fn an_npc_is_refused_in_a_system_where_nobody_is_and_a_scripted_behavior_that_is_not_there_makes_none() {
    let mut sim = sim();
    let none = sim.handle(spawn(6, SYSTEM, 980_050_000_002, [0.0; 3]), NOON).unwrap();
    assert_eq!(none, [Message::SpawnReply { request_id: 6, outcome: Err(format!("NO_SCENE {SYSTEM}")) }]);
    let mut sim = undocked();
    let Message::Spawn(mut request) = spawn(7, SYSTEM, 980_050_000_003, [0.0; 3]) else { unreachable!() };
    request.behavior = "there is no such tree".to_string();
    let refused = sim.handle(Message::Spawn(request), NOON).unwrap();
    assert!(matches!(&refused[..], [Message::SpawnReply { request_id: 7, outcome: Err(_) }]), "{refused:?}");
}

#[test]
fn a_ship_the_host_asks_to_destroy_is_gone_and_seen_to_go_and_a_player_is_told_to_the_host() {
    let mut sim = undocked();
    place_player_outside_station(&mut sim);
    sim.handle(Message::Bind { session_id: SESSION }, NOON).unwrap();
    let at = beside_the_player(&mut sim);
    let reply = sim.handle(spawn(8, SYSTEM, 980_050_000_004, at), NOON + 60 * SECOND).unwrap();
    assert!(matches!(reply[0], Message::SpawnReply { outcome: Ok(_), .. }));
    let told = sim
        .handle(Message::Destroy { ship_id: 980_050_000_004, credit: Default::default() }, NOON + 61 * SECOND)
        .unwrap();
    // the explosion and wreck update the bound observer; the host receives the NPC's Destroyed record
    assert_eq!(&names(&told)[..2], ["DoDestinyUpdate", "DoDestinyUpdate"]);
    let gone: Vec<_> = told.iter().filter(|m| matches!(m, Message::Destroyed { .. })).collect();
    assert!(
        matches!(gone[..], [Message::Destroyed { ship_id: 980_050_000_004, wreck_id, killer_ship_id: 0, .. }] if *wreck_id != 0),
        "{told:?}"
    );
    assert_eq!(told.len(), 3);
    // a player's ship: the host is told with `Destroyed`, and the session is no longer in space
    let told = sim
        .handle(Message::Destroy { ship_id: spec().ship_id, credit: Default::default() }, NOON + 62 * SECOND)
        .unwrap();
    assert!(
        told.iter().any(|m| matches!(m, Message::Destroyed { session_id: SESSION, ship_id, wreck_id: 0, killer_ship_id: 0, .. } if *ship_id == spec().ship_id)),
        "{told:?}"
    );
    assert_eq!(sim.ships(), 0);
    let none =
        sim.handle(Message::Destroy { ship_id: 12, credit: Default::default() }, NOON + 63 * SECOND).unwrap();
    assert_eq!(none, []);
}

fn capsule() -> Box<crate::ship::ShipSpec> {
    let mut capsule = spec();
    capsule.ship_id = 9_988_400_002_416;
    capsule.type_id = 670;
    capsule.group_id = 29;
    capsule.name = "Capsule".to_string();
    Box::new(capsule)
}

fn lose_the_ship(sim: &mut Sim, at: i64) -> Message {
    let told =
        sim.handle(Message::Destroy { ship_id: spec().ship_id, credit: Default::default() }, at).unwrap();
    told.into_iter().find(|m| matches!(m, Message::Destroyed { .. })).expect("the host is told")
}

#[test]
fn a_session_that_lost_its_ship_keeps_its_ballpark_and_is_given_a_capsule_by_the_host() {
    let mut sim = undocked();
    sim.handle(Message::Bind { session_id: SESSION }, NOON).unwrap();
    let Message::Destroyed { position, direction, .. } = lose_the_ship(&mut sim, NOON + 61 * SECOND) else {
        unreachable!()
    };
    assert_eq!(sim.ships(), 0, "no ship of it is in space");
    assert_eq!(sim.scenes.len(), 1, "the scene keeps its ballpark");

    let told = sim
        .handle(
            Message::Respawn { session_id: SESSION, ship: capsule(), position, direction },
            NOON + 62 * SECOND,
        )
        .unwrap();
    assert_eq!(told.last(), Some(&Message::EnterReply { session_id: SESSION, outcome: Ok(()) }));
    let notified = &told[..told.len() - 1];
    assert!(notified.iter().all(|m| matches!(m, Message::Notify { session_id: SESSION, .. })), "{told:?}");
    assert_eq!(names(notified), ["DoDestinyUpdate"], "the ball of the capsule, before the answer");
    assert_eq!(sim.ships(), 1);

    // and commands reach the new ship
    let stop = sim
        .handle(Message::Command { session_id: SESSION, command: 3, args: vec![] }, NOON + 63 * SECOND)
        .unwrap();
    assert!(!stop.iter().any(|m| matches!(m, Message::Refused { .. })), "{stop:?}");
}

#[test]
fn a_capsule_is_refused_to_a_session_that_did_not_lose_its_ship_and_one_that_left_loses_the_scene() {
    let mut sim = undocked();
    let refused = sim
        .handle(
            Message::Respawn {
                session_id: SESSION,
                ship: capsule(),
                position: [0.0; 3],
                direction: [1.0, 0.0, 0.0],
            },
            NOON,
        )
        .unwrap();
    assert!(
        matches!(&refused[..], [Message::EnterReply { outcome: Err(reason), .. }] if reason.starts_with("ALREADY_IN_SPACE")),
        "{refused:?}"
    );
    let none = sim
        .handle(
            Message::Respawn {
                session_id: 99,
                ship: capsule(),
                position: [0.0; 3],
                direction: [1.0, 0.0, 0.0],
            },
            NOON,
        )
        .unwrap();
    assert!(matches!(&none[..], [Message::EnterReply { session_id: 99, outcome: Err(_) }]), "{none:?}");

    lose_the_ship(&mut sim, NOON + 61 * SECOND);
    assert_eq!(sim.scenes.len(), 1);
    sim.handle(Message::Leave { session_id: SESSION, why: 1 }, NOON + 62 * SECOND).unwrap();
    assert_eq!(sim.scenes.len(), 0, "nobody is there: no scene");
    let late = sim
        .handle(
            Message::Respawn {
                session_id: SESSION,
                ship: capsule(),
                position: [0.0; 3],
                direction: [1.0, 0.0, 0.0],
            },
            NOON + 63 * SECOND,
        )
        .unwrap();
    assert!(matches!(&late[..], [Message::EnterReply { outcome: Err(_), .. }]), "{late:?}");
}

fn wreck_placement(system: i32, id: i64, at: [f64; 3]) -> Message {
    Message::PlaceWreck(Box::new(crate::link::WreckPlacement {
        solar_system_id: system,
        wreck_id: id,
        group_id: crate::wreck::WRECK_GROUP,
        kind: crate::wreck::WreckKind {
            type_id: 26_506,
            name: "Caldari Frigate Wreck".into(),
            graphic_id: Some(3117),
        },
        position: at,
        direction: [1.0, 0.0, 0.0],
        radius: 47.0,
        launcher_id: spec().ship_id,
        credit: crate::death::Credit {
            character_id: 140_000_342,
            corporation_id: 1_000_006,
            alliance_id: 0,
            war_faction_id: 500_001,
        },
        is_empty: false,
    }))
}

#[test]
fn a_wreck_the_host_made_is_put_in_space_with_its_id_for_whoever_can_see_it_and_kept_for_two_hours() {
    let mut sim = undocked();
    sim.handle(Message::Bind { session_id: SESSION }, NOON).unwrap();
    let at = beside_the_player(&mut sim);
    let told = sim.handle(wreck_placement(SYSTEM, 9_988_400_002_417, at), NOON + 5 * SECOND).unwrap();
    assert_eq!(names(&told), ["DoDestinyUpdate"], "{told:?}");
    assert_eq!(
        sim.scenes[&SYSTEM].wreck(9_988_400_002_417).map(|w| w.rights.character_id),
        Some(140_000_342)
    );
    assert_eq!(
        sim.scenes[&SYSTEM].wreck(9_988_400_002_417).map(|w| w.expires_at),
        Some(NOON + 5 * SECOND + crate::wreck::WRECK_LIFETIME)
    );
    // a system nobody is in has no scene to put a wreck in
    let none = sim.handle(wreck_placement(30_000_142, 9_988_400_002_418, at), NOON + 6 * SECOND).unwrap();
    assert_eq!(none, []);
}

fn can_placement(id: i64) -> Message {
    Message::PlaceContainer(Box::new(crate::link::ContainerPlacement {
        session_id: SESSION,
        container_id: id,
        kind: crate::wreck::WreckKind { type_id: 23, name: "Cargo Container".into(), graphic_id: Some(1176) },
        radius: 14.0,
        launcher_id: spec().ship_id,
        credit: crate::death::Credit {
            character_id: 140_000_342,
            corporation_id: 1_000_006,
            alliance_id: 0,
            war_faction_id: 0,
        },
        distance: 275.0,
    }))
}

/// The host's queries reach the scene (a position, a drop point): before, codes 21 to 23 were refused as unknown, so the host's position queries failed in the
/// simulation. A drop point is 2,500 m from the ship along its heading.
#[test]
fn the_host_queries_are_answered_and_a_drop_point_is_2500_metres_from_the_ship() {
    let mut sim = undocked();
    sim.handle(Message::Bind { session_id: SESSION }, NOON).unwrap();
    let ship = spec().ship_id as f64;
    let at = sim
        .handle(Message::Command { session_id: SESSION, command: 23, args: vec![ship] }, NOON + SECOND)
        .unwrap();
    let [Message::Position { command: 23, position: here, .. }] = &at[..] else { panic!("{at:?}") };
    let dropped = sim
        .handle(Message::Command { session_id: SESSION, command: 25, args: vec![2_500.0] }, NOON + SECOND)
        .unwrap();
    let [Message::Position { command: 25, position: there, .. }] = &dropped[..] else {
        panic!("{dropped:?}")
    };
    let length =
        ((there[0] - here[0]).powi(2) + (there[1] - here[1]).powi(2) + (there[2] - here[2]).powi(2)).sqrt();
    assert!((length - 2_500.0).abs() < 1e-6, "{dropped:?}");

    let refused = sim
        .handle(Message::Command { session_id: SESSION, command: 25, args: vec![-1.0] }, NOON + SECOND)
        .unwrap();
    assert!(
        matches!(&refused[..], [Message::Refused { session_id: SESSION, command: 25, .. }]),
        "{refused:?}"
    );
}

#[test]
fn a_can_is_put_ahead_of_the_pilot_changed_when_something_is_in_it_measured_and_taken_out_of_space_with_each_answered_to_its_pilot()
 {
    let mut sim = undocked();
    sim.handle(Message::Bind { session_id: SESSION }, NOON).unwrap();
    let can = 9_988_400_002_948;

    let placed = sim.handle(can_placement(can), NOON + SECOND).unwrap();
    // the ball and the effect, then where it lies (after what the pilot is told)
    assert_eq!(names(&placed[..2]), ["DoDestinyUpdate", "DoDestinyUpdate"], "{placed:?}");
    assert!(
        matches!(placed[2], Message::ContainerPlaced { session_id: SESSION, container_id, .. } if container_id == can),
        "{placed:?}"
    );
    assert_eq!(placed.len(), 3);

    // a pilot that is not in space cannot jettison
    let none = sim
        .handle(
            Message::PlaceContainer(Box::new(match can_placement(can + 1) {
                Message::PlaceContainer(mut other) => {
                    other.session_id = 99;
                    *other
                }
                _ => unreachable!(),
            })),
            NOON + SECOND,
        )
        .unwrap();
    assert_eq!(
        none,
        [Message::Refused {
            session_id: 99,
            command: crate::link::kind::PLACE_CONTAINER,
            reason: "NO_SHIP_IN_SPACE".to_string()
        }]
    );

    let changed = sim
        .handle(
            Message::ChangeWreck { session_id: SESSION, wreck_id: can, is_empty: false },
            NOON + 2 * SECOND,
        )
        .unwrap();
    assert_eq!(names(&changed[..1]), ["DoDestinyUpdate"]);
    assert_eq!(
        changed.last(),
        Some(&Message::Accepted {
            session_id: SESSION,
            command: crate::link::kind::CHANGE_WRECK,
            at: NOON + 2 * SECOND
        }),
        "the answer comes after what the pilot is told"
    );
    let missing = sim
        .handle(
            Message::ChangeWreck { session_id: SESSION, wreck_id: can + 7, is_empty: true },
            NOON + 2 * SECOND,
        )
        .unwrap();
    assert_eq!(
        missing,
        [Message::Refused {
            session_id: SESSION,
            command: crate::link::kind::CHANGE_WRECK,
            reason: "NO_SUCH_WRECK".to_string()
        }]
    );

    let measured = sim
        .handle(
            Message::Command {
                session_id: SESSION,
                command: crate::link::COMMAND_MEASURE,
                args: vec![can as f64],
            },
            NOON + SECOND,
        )
        .unwrap();
    assert!(
        matches!(&measured[..], [Message::Measured { session_id: SESSION, command: 20, distance: Some(d) }] if (*d - 261.0).abs() < 1e-3),
        "{measured:?}"
    );
    let nothing = sim
        .handle(
            Message::Command { session_id: SESSION, command: crate::link::COMMAND_MEASURE, args: vec![12.0] },
            NOON + SECOND,
        )
        .unwrap();
    assert_eq!(nothing, [Message::Measured { session_id: SESSION, command: 20, distance: None }]);

    let gone = sim.handle(Message::RemoveWreck { wreck_id: can, effect: 3 }, NOON + 3 * SECOND).unwrap();
    assert_eq!(names(&gone), ["DoDestinyUpdate"]);
    assert!(
        sim.handle(Message::RemoveWreck { wreck_id: can, effect: 3 }, NOON + 4 * SECOND).unwrap().is_empty(),
        "nothing is told twice"
    );
}

#[test]
fn a_ship_is_healed_and_asked_what_it_has_taken_by_commands_18_and_19() {
    let mut sim = undocked();
    let asked =
        sim.handle(Message::Command { session_id: SESSION, command: 19, args: vec![] }, NOON).unwrap();
    assert_eq!(asked, [Message::Condition { session_id: SESSION, command: 19, condition: Condition::FULL }]);
    let healed =
        sim.handle(Message::Command { session_id: SESSION, command: 18, args: vec![] }, NOON).unwrap();
    assert_eq!(
        healed.last(),
        Some(&Message::Accepted { session_id: SESSION, command: 18, at: NOON }),
        "the answer comes after what the pilot is told"
    );
    for command in [18, 19] {
        let none = sim.handle(Message::Command { session_id: 99, command, args: vec![] }, NOON).unwrap();
        assert_eq!(
            none,
            [Message::Refused { session_id: 99, command, reason: "NO_SHIP_IN_SPACE".to_string() }]
        );
    }
}

#[test]
fn an_effect_is_started_by_a_message_and_stopped_by_the_command_and_each_is_answered_under_its_code() {
    let mut sim = undocked();
    let started = sim
        .handle(
            Message::Activate { session_id: SESSION, effect: Box::new(afterburner(77)) },
            NOON + 40 * SECOND,
        )
        .unwrap();
    assert!(
        started.contains(&Message::Accepted { session_id: SESSION, command: 17, at: NOON + 40 * SECOND }),
        "{started:?}"
    );
    let again = sim
        .handle(
            Message::Activate { session_id: SESSION, effect: Box::new(afterburner(77)) },
            NOON + 41 * SECOND,
        )
        .unwrap();
    assert_eq!(
        again,
        [Message::Refused { session_id: SESSION, command: 17, reason: "ALREADY_ACTIVE".to_string() }]
    );
    let stopped = sim
        .handle(Message::Command { session_id: SESSION, command: 16, args: vec![77.0] }, NOON + 42 * SECOND)
        .unwrap();
    assert_eq!(stopped, [Message::Accepted { session_id: SESSION, command: 16, at: NOON + 42 * SECOND }]);
    let unknown = sim
        .handle(Message::Command { session_id: SESSION, command: 16, args: vec![77.0] }, NOON + 43 * SECOND)
        .unwrap();
    assert_eq!(
        unknown,
        [Message::Accepted { session_id: SESSION, command: 16, at: NOON + 43 * SECOND }],
        "the effect is still running until its cycle ends"
    );
    let after = sim
        .handle(Message::Command { session_id: SESSION, command: 16, args: vec![99.0] }, NOON + 43 * SECOND)
        .unwrap();
    assert_eq!(
        after,
        [Message::Refused { session_id: SESSION, command: 16, reason: "NOT_ACTIVE".to_string() }]
    );
    // a command that is not one is refused, and so is one for a ship that is not there
    let bad = sim.handle(Message::Command { session_id: SESSION, command: 16, args: vec![] }, NOON).unwrap();
    assert_eq!(
        bad,
        [Message::Refused { session_id: SESSION, command: 16, reason: "UNKNOWN_COMMAND".to_string() }]
    );
    let gone = sim.handle(Message::Command { session_id: 555, command: 16, args: vec![77.0] }, NOON).unwrap();
    assert_eq!(
        gone,
        [Message::Refused { session_id: 555, command: 16, reason: "NO_SHIP_IN_SPACE".to_string() }]
    );
    let none =
        sim.handle(Message::Activate { session_id: 555, effect: Box::new(afterburner(77)) }, NOON).unwrap();
    assert_eq!(
        none,
        [Message::Refused { session_id: 555, command: 17, reason: "NO_SHIP_IN_SPACE".to_string() }]
    );
}

#[test]
fn an_offline_message_stops_a_running_module_immediately_and_idempotently() {
    let mut sim = undocked();
    sim.handle(
        Message::Activate { session_id: SESSION, effect: Box::new(afterburner(77)) },
        NOON + 40 * SECOND,
    )
    .unwrap();
    let stopped = sim
        .handle(Message::ModuleOffline { session_id: SESSION, module_id: 77 }, NOON + 41 * SECOND)
        .unwrap();
    assert!(!stopped.is_empty(), "the running effect is ended immediately");
    let repeated = sim
        .handle(Message::ModuleOffline { session_id: SESSION, module_id: 77 }, NOON + 42 * SECOND)
        .unwrap();
    assert!(repeated.is_empty(), "repeating offline after it stopped is idempotent");
}

fn names(messages: &[Message]) -> Vec<String> {
    messages
        .iter()
        .map(|m| match m {
            Message::Notify { name, .. } => name.clone(),
            other => format!("{other:?}"),
        })
        .collect()
}

#[test]
fn hello_is_answered_with_the_version_and_the_poll_rate() {
    let reply = sim().handle(Message::Hello { version: PROTOCOL_VERSION }, NOON).unwrap();
    assert_eq!(reply, [Message::HelloAck { version: PROTOCOL_VERSION, poll_hz: POLL_HZ }]);
}

#[test]
fn an_undock_puts_the_ship_in_space_and_binding_sends_it_the_scene_as_marshalled_notifications() {
    let mut sim = undocked();
    assert_eq!(sim.ships(), 1);
    let out = sim.handle(Message::Bind { session_id: SESSION }, NOON).unwrap();
    assert_eq!(
        names(&out),
        [
            "OnSetTimeDilation",
            "DoSimClockRebase",
            "DoDestinyUpdate",
            "DoDestinyUpdate",
            "DoDestinyUpdate",
            "DoDestinyUpdate"
        ]
    );
    let Message::Notify { id_type, args, session_id, .. } = &out[0] else { panic!() };
    assert_eq!((id_type.as_str(), *session_id), ("clientID", SESSION));
    // the arguments are a marshalled tuple
    let decoded = eve_marshal::decode_exact(args).unwrap();
    assert_eq!(decoded, Value::Tuple(vec![Value::Int(1), Value::Int(1), Value::Int(100_000_000)]));
}

#[test]
fn an_undock_rejects_bad_locations_and_in_space_login_restores_the_saved_pose() {
    let mut sim = sim();
    let reason = |message: Vec<Message>| match &message[0] {
        Message::EnterReply { outcome: Err(reason), .. } => reason.clone(),
        other => panic!("{other:?}"),
    };
    assert!(reason(sim.handle(enter(SESSION, STATION, 42), NOON).unwrap()).starts_with("UNKNOWN_SYSTEM"));
    assert!(reason(sim.handle(enter(SESSION, 42, SYSTEM), NOON).unwrap()).starts_with("UNKNOWN_STATION"));
    assert_eq!((sim.ships(), sim.scenes.len()), (0, 0));
    sim.handle(enter(SESSION, STATION, SYSTEM), NOON).unwrap();
    assert!(
        reason(sim.handle(enter(SESSION, STATION, SYSTEM), NOON).unwrap()).starts_with("ALREADY_IN_SPACE")
    );

    let ship = spec();
    let ship_id = ship.ship_id;
    let position = [100.0, 200.0, 300.0];
    let messages = sim
        .handle(
            Message::Enter(Box::new(Enter {
                session_id: 9,
                solar_system_id: SYSTEM,
                how: How::InSpace,
                station_id: 0,
                ship,
                position: Some(position),
                direction: Some([0.0, 0.0, 1.0]),
            })),
            NOON,
        )
        .unwrap();
    assert!(matches!(messages.first(), Some(Message::EnterReply { session_id: 9, outcome: Ok(()) })));
    let restored = sim.scenes.get_mut(&SYSTEM).unwrap().ship(9, NOON).unwrap();
    assert_eq!(restored.spec.ship_id, ship_id);
    assert_eq!(restored.position, Vec3::new(position[0], position[1], position[2]));

    let primed = sim
        .handle(
            Message::Command {
                session_id: 9,
                command: crate::link::COMMAND_PRIME_LOGIN_BALLPARK_VISUALS,
                args: Vec::new(),
            },
            NOON,
        )
        .unwrap();
    assert_eq!(primed.len(), 4, "clock reset, visual AddBalls2, and command acknowledgement");
    assert!(matches!(
        primed.last(),
        Some(Message::Accepted {
            session_id: 9,
            command: crate::link::COMMAND_PRIME_LOGIN_BALLPARK_VISUALS,
            ..
        })
    ));

    let bound = sim.handle(Message::Bind { session_id: 9 }, NOON + 1).unwrap();
    assert_eq!(bound.len(), 2, "the full SetState and ship numbers follow the earlier visual prime");
}

#[test]
fn commands_are_read_from_their_codes_and_arguments() {
    assert_eq!(command_of(1, &[0.5]), Some(Command::SetSpeedFraction(0.5)));
    assert_eq!(command_of(2, &[1.0, 0.0, 0.0]), Some(Command::GotoDirection(Vec3::new(1.0, 0.0, 0.0))));
    assert_eq!(command_of(3, &[]), Some(Command::Stop));
    assert_eq!(command_of(4, &[60_015_249.0]), Some(Command::Dock { station_id: 60_015_249 }));
    assert_eq!(
        command_of(9, &[50_016_630.0, 50_016_839.0]),
        Some(Command::Jump { gate_id: 50_016_630, destination_gate: 50_016_839 })
    );
    assert_eq!(
        command_of(9, &[50_016_630.0]),
        Some(Command::Jump { gate_id: 50_016_630, destination_gate: 0 }),
        "the gate alone is enough"
    );
    for (code, args) in [
        (1, vec![]),
        (1, vec![1.0, 2.0]),
        (2, vec![1.0]),
        (3, vec![1.0]),
        (4, vec![1.5]),
        (4, vec![f64::NAN]),
        (4, vec![1e300]),
        (9, vec![]),
        (9, vec![1.5]),
        (9, vec![50_016_630.0, f64::NAN]),
        (9, vec![1.0, 2.0, 3.0]),
        (10, vec![]),
    ] {
        assert_eq!(command_of(code, &args), None, "{code} {args:?}");
    }
}

#[test]
fn a_command_the_scene_cannot_take_is_refused_with_the_reason() {
    let mut sim = undocked();
    let refused = |message: Message| match message {
        Message::Refused { command, reason, .. } => (command, reason),
        other => panic!("{other:?}"),
    };
    let out = sim.handle(Message::Command { session_id: SESSION, command: 10, args: vec![] }, NOON).unwrap();
    assert_eq!(refused(out[0].clone()), (10, "UNKNOWN_COMMAND".to_string()));
    let out = sim.handle(Message::Command { session_id: 99, command: 3, args: vec![] }, NOON).unwrap();
    assert_eq!(refused(out[0].clone()), (3, "NO_SHIP_IN_SPACE".to_string()));
    let out =
        sim.handle(Message::Command { session_id: SESSION, command: 4, args: vec![42.0] }, NOON).unwrap();
    assert_eq!(refused(out[0].clone()), (4, "STATION_NOT_FOUND".to_string()));
}

#[test]
fn a_command_is_answered_with_the_update_and_a_docking_is_finished_by_the_poll() {
    let mut sim = undocked();
    let out =
        sim.handle(Message::Command { session_id: SESSION, command: 1, args: vec![1.0] }, NOON).unwrap();
    assert_eq!(names(&out[..1]), ["DoDestinyUpdate"]);
    assert_eq!(out[1], Message::Accepted { session_id: SESSION, command: 1, at: NOON });

    // the ship has not gone anywhere yet, so it is still within range of the station
    let out = sim
        .handle(Message::Command { session_id: SESSION, command: 4, args: vec![STATION as f64] }, NOON)
        .unwrap();
    assert_eq!(names(&out[..2]), ["OnDockingAccepted", "DoDestinyUpdate"]);
    assert_eq!(out[2], Message::Accepted { session_id: SESSION, command: 4, at: NOON });
    assert!(sim.poll(NOON + 3 * SECOND).is_empty(), "three seconds are not enough");
    assert_eq!(
        sim.poll(NOON + 4 * SECOND),
        [Message::Docked { session_id: SESSION, station_id: STATION, condition: Condition::FULL }]
    );
    assert_eq!((sim.ships(), sim.scenes.len()), (0, 0), "an empty scene is dropped");
    assert!(sim.poll(NOON + 9 * SECOND).is_empty());
}

#[test]
fn a_players_warp_to_a_belt_is_told_to_the_host_as_an_arrival() {
    const BELT: i64 = 40_000_101;
    let mut world = big_station();
    world.bodies.push(crate::world::Body {
        id: BELT,
        type_id: 15,
        group_id: 9,
        category_id: 2,
        owner_id: 0,
        name: "Testing Belt".to_string(),
        kind: crate::world::BodyKind::Belt,
        position: eve_destiny::Vec3::new(20_000_000.0, 0.0, 0.0),
        radius: 1_000.0,
        graphic_id: None,
        detail: crate::world::Detail::None,
    });
    let mut sim = Sim::new(Arc::new(Universe::with_systems([world])));
    sim.handle(enter(SESSION, STATION, SYSTEM), NOON).unwrap();
    sim.handle(Message::Command { session_id: SESSION, command: 3, args: vec![0.0] }, NOON).unwrap();
    let warp = sim
        .handle(
            Message::Command { session_id: SESSION, command: 8, args: vec![BELT as f64, 0.0] },
            NOON + SECOND,
        )
        .unwrap();
    assert!(!warp.iter().any(|m| matches!(m, Message::Refused { .. })), "{warp:?}");
    let mut arrived = Vec::new();
    let mut t = NOON + SECOND;
    while t < NOON + 600 * SECOND {
        t += SECOND / 4;
        arrived.extend(sim.poll(t).into_iter().filter(|m| matches!(m, Message::Arrived { .. })));
    }
    let [Message::Arrived { session_id, belt_id, position }] = &arrived[..] else { panic!("{arrived:?}") };
    assert_eq!((*session_id, *belt_id), (SESSION, BELT));
    assert!((position[0] - 20_000_000.0).abs() < 4_000.0, "{position:?}: it stops 2.5 km short of the belt");
}

#[test]
fn leaving_takes_the_ship_out_and_the_scene_with_it() {
    let mut sim = undocked();
    assert!(sim.handle(Message::Leave { session_id: SESSION, why: 1 }, NOON).unwrap().is_empty());
    assert_eq!((sim.ships(), sim.scenes.len()), (0, 0));
    assert!(
        sim.handle(Message::Leave { session_id: SESSION, why: 1 }, NOON).unwrap().is_empty(),
        "leaving twice is harmless"
    );
    assert!(sim.handle(Message::Bind { session_id: SESSION }, NOON).unwrap().is_empty());
}

#[test]
fn new_numbers_for_a_ship_are_sent_on() {
    let mut sim = undocked();
    let mut faster = spec();
    faster.max_velocity = 500.0;
    let out = sim.handle(Message::ShipSpec { session_id: SESSION, ship: Box::new(faster) }, NOON).unwrap();
    assert_eq!(names(&out), ["DoDestinyUpdate"]);
}

#[test]
fn a_heat_profile_link_message_installs_dogma_module_data_for_scene_overload() {
    let mut simulation = sim();
    let entering = enter(SESSION, STATION, SYSTEM);
    let fitted = match &entering {
        Message::Enter(entry) => entry.ship.modules.clone(),
        _ => unreachable!(),
    };
    simulation.handle(entering, NOON).unwrap();
    let profile = crate::scene::HeatProfile {
        generation_multiplier: 1.0,
        damage_multiplier: 0.95,
        thermodynamics_level: 1,
        capacity: [100.0; 3],
        initial_rack_heat: [0.0; 3],
        dissipation_rate: [0.01; 3],
        attenuation: [0.71; 3],
        modules: fitted
            .iter()
            .filter(|module| (11..=34).contains(&module.flag))
            .map(|module| crate::scene::HeatModuleProfile {
                module_id: module.item_id,
                flag: module.flag,
                structure_hp: 100.0,
                damage_hp: 0.0,
                heat_damage_hp: 6.0,
                heat_absorption_rate: 0.01,
                overloadable: true,
                heat_immune: false,
                online: true,
            })
            .collect(),
    };
    let module_id = profile.modules[0].module_id;
    simulation.handle(Message::HeatProfile { session_id: SESSION, profile }, NOON).unwrap();
    assert!(
        simulation
            .handle(Message::ModuleOverheat { session_id: SESSION, module_id, active: true }, NOON)
            .unwrap()
            .is_empty()
    );
}

#[test]
fn a_message_meant_for_the_host_is_a_protocol_error() {
    let error = sim()
        .handle(Message::Docked { session_id: 1, station_id: 2, condition: Condition::FULL }, NOON)
        .unwrap_err();
    assert!(error.0.contains("kind 10"), "{error}");
}

#[test]
fn two_simulations_given_the_same_messages_send_the_same_bytes() {
    let run = || {
        let mut sim = undocked();
        let mut out = sim.handle(Message::Bind { session_id: SESSION }, NOON).unwrap();
        out.extend(
            sim.handle(
                Message::Command { session_id: SESSION, command: 2, args: vec![0.0, 0.0, 1.0] },
                NOON + 3 * SECOND,
            )
            .unwrap(),
        );
        out.extend(sim.handle(Message::UpdateState { session_id: SESSION }, NOON + 6 * SECOND).unwrap());
        out.iter().flat_map(Message::encode).collect::<Vec<u8>>()
    };
    assert_eq!(run(), run());
}

// ── jumps ────────────────────────────────────────────────────────────────

fn gated_sim() -> Sim {
    let (here, there) = crate::scene::tests::gated_systems();
    Sim::new(Arc::new(Universe::with_systems([here, there])))
}

fn jump_command(gate: i64, leads_to: i64) -> Message {
    Message::Command { session_id: SESSION, command: 9, args: vec![gate as f64, leads_to as f64] }
}

#[test]
fn a_jump_takes_the_ship_to_the_scene_of_the_other_system_when_it_is_due() {
    use crate::scene::tests::{ARRIVAL_GATE, NEAR_GATE, OTHER_SYSTEM};
    let mut sim = gated_sim();
    sim.handle(enter(SESSION, STATION, SYSTEM), NOON).unwrap();
    let out = sim.handle(jump_command(NEAR_GATE, ARRIVAL_GATE), NOON).unwrap();
    assert_eq!(names(&out[..1]), ["DoDestinyUpdate"]);
    assert_eq!(
        out[1],
        Message::Jumping {
            session_id: SESSION,
            command: 9,
            destination_system: OTHER_SYSTEM,
            destination_gate: ARRIVAL_GATE
        }
    );
    assert!(sim.poll(NOON + 3 * SECOND).is_empty(), "the ship is still where it was");
    assert_eq!(sim.scenes.keys().copied().collect::<Vec<_>>(), [SYSTEM]);

    let gone = sim.poll(NOON + 4 * SECOND);
    assert_eq!(
        gone,
        [Message::Jumped {
            session_id: SESSION,
            from_system: SYSTEM,
            from_gate: NEAR_GATE,
            to_system: OTHER_SYSTEM,
            to_gate: ARRIVAL_GATE
        }]
    );
    assert_eq!(sim.ships(), 1);
    assert_eq!(
        sim.scenes.keys().copied().collect::<Vec<_>>(),
        [OTHER_SYSTEM],
        "the scene it left has no one in it and is dropped"
    );
    assert!(sim.poll(NOON + 9 * SECOND).is_empty(), "it goes once");

    // the client binds the ballpark of the new system and is sent it
    let bound = sim.handle(Message::Bind { session_id: SESSION }, NOON + 5 * SECOND).unwrap();
    assert_eq!(bound.len(), 5);
    // and orders go to the ship where it is
    let out = sim
        .handle(Message::Command { session_id: SESSION, command: 1, args: vec![1.0] }, NOON + 6 * SECOND)
        .unwrap();
    assert_eq!(names(&out[..1]), ["DoDestinyUpdate"]);
    // it is far from the gate it came out of, so it cannot go back at once
    let out = sim.handle(jump_command(ARRIVAL_GATE, NEAR_GATE), NOON + 6 * SECOND).unwrap();
    assert_eq!(
        out,
        [Message::Refused { session_id: SESSION, command: 9, reason: "TOO_FAR_FROM_STARGATE".to_string() }]
    );
}

#[test]
fn a_ship_that_is_asked_to_leave_while_it_jumps_leaves_from_the_scene_it_is_in() {
    use crate::scene::tests::{ARRIVAL_GATE, NEAR_GATE};
    let mut sim = gated_sim();
    sim.handle(enter(SESSION, STATION, SYSTEM), NOON).unwrap();
    sim.handle(jump_command(NEAR_GATE, ARRIVAL_GATE), NOON).unwrap();
    sim.poll(NOON + 4 * SECOND);
    assert!(
        sim.handle(Message::Leave { session_id: SESSION, why: 1 }, NOON + 5 * SECOND).unwrap().is_empty()
    );
    assert_eq!((sim.ships(), sim.scenes.len()), (0, 0));
}

#[test]
fn a_jump_to_a_system_that_cannot_be_built_leaves_the_ship_where_it_was_and_says_so() {
    use crate::scene::tests::{ARRIVAL_GATE, NEAR_GATE};
    let (here, _) = crate::scene::tests::gated_systems();
    let mut sim = Sim::new(Arc::new(Universe::with_systems([here])));
    sim.handle(enter(SESSION, STATION, SYSTEM), NOON).unwrap();
    sim.handle(jump_command(NEAR_GATE, ARRIVAL_GATE), NOON).unwrap();
    assert_eq!(
        sim.poll(NOON + 4 * SECOND),
        [Message::Refused { session_id: SESSION, command: 9, reason: "DESTINATION_UNAVAILABLE".to_string() }]
    );
    assert_eq!((sim.ships(), sim.scenes.keys().copied().collect::<Vec<_>>()), (1, vec![SYSTEM]));
    // the ship takes orders and may try again
    let out = sim.handle(jump_command(NEAR_GATE, ARRIVAL_GATE), NOON + 5 * SECOND).unwrap();
    assert!(matches!(out[1], Message::Jumping { .. }), "{out:?}");
}

#[test]
fn two_simulations_given_the_same_jump_send_the_same_bytes() {
    use crate::scene::tests::{ARRIVAL_GATE, NEAR_GATE};
    let run = || {
        let mut sim = gated_sim();
        sim.handle(enter(SESSION, STATION, SYSTEM), NOON).unwrap();
        let mut out = sim.handle(jump_command(NEAR_GATE, ARRIVAL_GATE), NOON).unwrap();
        out.extend(sim.poll(NOON + 4 * SECOND));
        out.extend(sim.handle(Message::Bind { session_id: SESSION }, NOON + 5 * SECOND).unwrap());
        out.iter().flat_map(Message::encode).collect::<Vec<u8>>()
    };
    assert_eq!(run(), run());
}

// ── other ships ──────────────────────────────────────────────────────────

const OTHER: SessionId = 9;

fn other_enter() -> Message {
    let mut ship = spec();
    ship.ship_id += 4;
    ship.character_id += 1;
    Message::Enter(Box::new(Enter {
        session_id: OTHER,
        solar_system_id: SYSTEM,
        how: How::Undock,
        station_id: STATION,
        ship,
        position: None,
        direction: None,
    }))
}

fn recipients(messages: &[Message]) -> Vec<(u64, String)> {
    messages
        .iter()
        .map(|m| match m {
            Message::Notify { session_id, name, .. } => (*session_id, name.clone()),
            Message::EnterReply { session_id, .. } => (*session_id, "EnterReply".to_string()),
            Message::Docked { session_id, .. } => (*session_id, "Docked".to_string()),
            other => panic!("{other:?}"),
        })
        .collect()
}

#[test]
fn a_second_ship_that_undocks_is_answered_and_shown_to_the_first_that_has_bound() {
    let mut sim = undocked();
    // the first has not bound: it is told nothing
    let reply = sim.handle(other_enter(), NOON).unwrap();
    assert_eq!(recipients(&reply), [(OTHER, "EnterReply".to_string())]);
    sim.handle(Message::Leave { session_id: OTHER, why: 1 }, NOON).unwrap();

    sim.handle(Message::Bind { session_id: SESSION }, NOON).unwrap();
    let reply = sim.handle(other_enter(), NOON + SECOND).unwrap();
    assert_eq!(
        recipients(&reply),
        [(OTHER, "EnterReply".to_string()), (SESSION, "DoDestinyUpdate".to_string())],
        "the answer first, then what the first ship is told"
    );
    assert_eq!(sim.ships(), 2);
}

#[test]
fn a_ship_that_leaves_is_taken_from_the_view_of_the_ones_that_saw_it() {
    let mut sim = undocked();
    sim.handle(Message::Bind { session_id: SESSION }, NOON).unwrap();
    sim.handle(other_enter(), NOON + SECOND).unwrap();
    let told = sim.handle(Message::Leave { session_id: OTHER, why: 1 }, NOON + 2 * SECOND).unwrap();
    assert_eq!(recipients(&told), [(SESSION, "DoDestinyUpdate".to_string())]);
    assert_eq!(sim.ships(), 1);
}

#[test]
fn a_ship_that_has_docked_is_taken_from_the_view_of_the_others_by_the_poll() {
    let mut sim = undocked();
    sim.handle(Message::Bind { session_id: SESSION }, NOON).unwrap();
    sim.handle(other_enter(), NOON + SECOND).unwrap();
    sim.handle(Message::Bind { session_id: OTHER }, NOON + SECOND).unwrap();
    sim.handle(
        Message::Command { session_id: OTHER, command: 4, args: vec![STATION as f64] },
        NOON + 2 * SECOND,
    )
    .unwrap();
    let gone = sim.poll(NOON + 6 * SECOND);
    assert_eq!(recipients(&gone), [(OTHER, "Docked".to_string()), (SESSION, "DoDestinyUpdate".to_string())]);
    assert_eq!(sim.ships(), 1, "the first ship is still there");
}

fn structure_observer_health_case(from_space: bool) {
    let mut simulation = if from_space { undocked() } else { sim() };
    let old_health = if from_space {
        let pilot = simulation.scenes.get_mut(&SYSTEM).unwrap().pilots.get_mut(&SESSION).unwrap();
        pilot.ship.health = crate::health::Health::new(
            pilot.ship.spec.vitals,
            pilot.ship.spec.shield_recharge_ms,
            Condition { damage: 0.3, armor_damage: 0.2, shield_charge: 0.8, charge: 1.0 },
            NOON,
        );
        Some(simulation.handle(Message::Command { session_id: SESSION, command: 19, args: vec![] }, NOON).unwrap())
    } else {
        None
    };
    let base = crate::scene::Scene::new(std::sync::Arc::new(big_station()));
    let placement = crate::scene::tests::structure_placement(&base, NOON);
    let structure_id = placement.structure_id;
    simulation.handle(Message::PlaceStructure(Box::new(placement)), NOON).unwrap();
    simulation.scenes.get_mut(&SYSTEM).unwrap().structure_health.get_mut(&structure_id).unwrap()
        .apply(NOON, [1_250.0, 0.0, 0.0, 0.0]).unwrap();
    simulation.handle(Message::ControlStructure {
        session_id: SESSION, solar_system_id: SYSTEM, structure_id,
        character_id: spec().character_id, active: true,
    }, NOON).unwrap();

    let expected = vec![Message::Condition {
        session_id: SESSION, command: 19,
        condition: Condition { damage: 0.0, armor_damage: 0.5, shield_charge: 0.0, charge: 1.0 },
    }];
    let observed = simulation.handle(Message::Command {
        session_id: SESSION, command: 19, args: vec![],
    }, NOON).unwrap();
    assert_eq!(observed, expected);
    if let Some(old_health) = &old_health {
        assert_ne!(&observed, old_health, "the structure HUD must not inherit the previous physical ship's health");
        assert!(simulation.scenes[&SYSTEM].unpiloted.contains_key(&spec().ship_id));
    }

    simulation.scenes.get_mut(&SYSTEM).unwrap().structure_health.get_mut(&structure_id).unwrap()
        .apply(NOON, [125.0, 0.0, 0.0, 0.0]).unwrap();
    assert_eq!(simulation.handle(Message::Command {
        session_id: SESSION, command: 19, args: vec![],
    }, NOON).unwrap(), [Message::Condition {
        session_id: SESSION, command: 19,
        condition: Condition { damage: 0.0, armor_damage: 0.75, shield_charge: 0.0, charge: 1.0 },
    }], "subsequent damage is read from the same structure health owner");

    simulation.handle(Message::ControlStructure {
        session_id: SESSION, solar_system_id: SYSTEM, structure_id: 0,
        character_id: 0, active: false,
    }, NOON).unwrap();
    let released = simulation.handle(Message::Command {
        session_id: SESSION, command: 19, args: vec![],
    }, NOON).unwrap();
    if let Some(old_health) = old_health {
        assert_eq!(released, old_health, "release resumes the unchanged physical ship's health");
    } else {
        assert_eq!(released, [Message::Refused {
            session_id: SESSION, command: 19, reason: "NO_SHIP_IN_SPACE".into(),
        }]);
    }
}

#[test]
fn command_19_reads_structure_health_for_a_docked_observer() {
    structure_observer_health_case(false);
}

#[test]
fn command_19_reads_structure_health_for_a_from_space_observer() {
    structure_observer_health_case(true);
}

fn structure_deposit_sim() -> (Sim, crate::link::StructurePlacement) {
    let mut simulation = undocked();
    let scene = &simulation.scenes[&SYSTEM];
    let ship = &scene.pilots[&SESSION].ship;
    let mut placement = crate::scene::tests::structure_placement(scene, NOON);
    placement.position = [
        ship.position.x + placement.radius + ship.spec.radius + 2_500.0,
        ship.position.y,
        ship.position.z,
    ];
    simulation.handle(Message::PlaceStructure(Box::new(placement.clone())), NOON).unwrap();
    (simulation, placement)
}

#[test]
fn a_destroyed_retained_hull_releases_into_a_real_capsule_respawn_context() {
    let (mut simulation, placement) = structure_deposit_sim();
    let old_ship_id = spec().ship_id;
    simulation.handle(Message::Bind { session_id: SESSION }, NOON).unwrap();
    simulation.handle(Message::ControlStructure {
        session_id: SESSION, solar_system_id: SYSTEM, structure_id: placement.structure_id,
        character_id: spec().character_id, active: true,
    }, NOON).unwrap();
    simulation.handle(Message::Destroy { ship_id: old_ship_id, credit: Default::default() }, NOON + SECOND).unwrap();
    assert_eq!(simulation.scenes[&SYSTEM].controlled_structure_id(SESSION), Some(placement.structure_id));
    let released = simulation.handle(Message::ControlStructure {
        session_id: SESSION, solar_system_id: SYSTEM, structure_id: 0, character_id: 0, active: false,
    }, NOON + 2 * SECOND).unwrap();
    assert!(released.iter().any(|message| matches!(message,
        Message::Accepted { session_id: SESSION, command: crate::link::COMMAND_STRUCTURE_CONTROL, .. })));
    // A refused/undelivered capsule spawn restores control before the client retries.
    simulation.handle(Message::ControlStructure {
        session_id: SESSION, solar_system_id: SYSTEM, structure_id: placement.structure_id,
        character_id: spec().character_id, active: true,
    }, NOON + 2 * SECOND).unwrap();
    simulation.handle(Message::ControlStructure {
        session_id: SESSION, solar_system_id: SYSTEM, structure_id: 0, character_id: 0, active: false,
    }, NOON + 2 * SECOND).unwrap();
    let mut capsule = spec();
    capsule.ship_id = old_ship_id + 1000;
    capsule.type_id = 670;
    capsule.group_id = 29;
    let capsule_id = capsule.ship_id;
    let respawned = simulation.handle(Message::Respawn {
        session_id: SESSION, ship: Box::new(capsule),
        position: [12300.0, 45600.0, 78900.0], direction: [0.0, 1.0, 0.0],
    }, NOON + 3 * SECOND).unwrap();
    assert!(respawned.iter().any(|message| matches!(message,
        Message::EnterReply { session_id: SESSION, outcome: Ok(..) })), "{respawned:?}");
    assert_eq!(simulation.scenes[&SYSTEM].pilots[&SESSION].ship.spec.ship_id, capsule_id);
    assert!(!simulation.scenes[&SYSTEM].controlled_structures.contains_key(&SESSION));
    assert_eq!(simulation.sessions.get(&SESSION), Some(&SYSTEM));
}

fn structure_deposit_query(simulation: &mut Sim, structure_id: i64, ship_id: i64) -> Vec<Message> {
    simulation.handle(Message::Command {
        session_id: SESSION, command: crate::link::COMMAND_MEASURE,
        args: vec![structure_id as f64, ship_id as f64],
    }, NOON).unwrap()
}

#[test]
fn structure_deposit_measure_uses_actual_surfaces_and_preserves_the_one_argument_path() {
    let (mut simulation, mut placement) = structure_deposit_sim();
    let structure_id = placement.structure_id;
    let ship_id = spec().ship_id;
    assert_eq!(structure_deposit_query(&mut simulation, structure_id, ship_id),
        [Message::Measured { session_id: SESSION, command: 20, distance: Some(2_500.0) }]);

    simulation.scenes.get_mut(&SYSTEM).unwrap().pilots.get_mut(&SESSION).unwrap().ship.spec.radius += 100.0;
    assert_eq!(structure_deposit_query(&mut simulation, structure_id, ship_id),
        [Message::Measured { session_id: SESSION, command: 20, distance: Some(2_400.0) }]);

    simulation.handle(Message::RemoveStructure { structure_id }, NOON).unwrap();
    placement.position[0] += 1_000.0;
    placement.radius += 500.0;
    simulation.handle(Message::PlaceStructure(Box::new(placement)), NOON).unwrap();
    assert_eq!(structure_deposit_query(&mut simulation, structure_id, ship_id),
        [Message::Measured { session_id: SESSION, command: 20, distance: Some(2_900.0) }]);

    let can = 1_050_000_000_002;
    simulation.handle(can_placement(can), NOON).unwrap();
    let old_measure = simulation.handle(Message::Command {
        session_id: SESSION, command: 20, args: vec![can as f64],
    }, NOON).unwrap();
    assert!(matches!(old_measure.as_slice(),
        [Message::Measured { session_id: SESSION, command: 20, distance: Some(distance) }]
            if (*distance - 261.0).abs() < 1e-3), "{old_measure:?}");
    assert_eq!(simulation.handle(Message::Command {
        session_id: SESSION, command: 20, args: vec![structure_id as f64],
    }, NOON).unwrap(), [Message::Measured { session_id: SESSION, command: 20, distance: None }]);
}

#[test]
fn structure_deposit_measure_refuses_missing_nonlocal_cloaked_warping_and_observer_ships() {
    let (mut simulation, placement) = structure_deposit_sim();
    let structure_id = placement.structure_id;
    let ship_id = spec().ship_id;
    let refusal = |reason: &str| vec![Message::Refused {
        session_id: SESSION, command: 20, reason: reason.into(),
    }];
    assert_eq!(structure_deposit_query(&mut simulation, structure_id + 1, ship_id), refusal("STRUCTURE_NOT_FOUND"));
    assert_eq!(structure_deposit_query(&mut simulation, STATION, ship_id), refusal("STRUCTURE_NOT_FOUND"));
    assert_eq!(structure_deposit_query(&mut simulation, structure_id, ship_id + 1), refusal("NOT_LOCAL_SHIP"));

    for cloak in [crate::visibility::CloakState::Cloaking, crate::visibility::CloakState::Cloaked] {
        simulation.scenes.get_mut(&SYSTEM).unwrap().pilots.get_mut(&SESSION).unwrap().cloak = cloak;
        assert_eq!(structure_deposit_query(&mut simulation, structure_id, ship_id), refusal("CLOAKED"));
    }
    let pilot = simulation.scenes.get_mut(&SYSTEM).unwrap().pilots.get_mut(&SESSION).unwrap();
    pilot.cloak = crate::visibility::CloakState::Uncloaked;
    let destination = Vec3::new(pilot.ship.position.x + 1.0e12, pilot.ship.position.y, pilot.ship.position.z);
    pilot.flight = Some(crate::flight::Flight::begin(NOON, destination, 0.0));
    assert_eq!(structure_deposit_query(&mut simulation, structure_id, ship_id), refusal("IN_WARP"));

    let pilot = simulation.scenes.get_mut(&SYSTEM).unwrap().pilots.get_mut(&SESSION).unwrap();
    pilot.ship.direction = Vec3::new(1.0, 0.0, 0.0);
    pilot.ship.velocity = Vec3::new(pilot.ship.spec.max_velocity, 0.0, 0.0);
    pilot.ship.speed_fraction = 1.0;
    let mut flight = crate::flight::Flight::begin(NOON - SECOND, destination, 0.0);
    flight.advance(&mut pilot.ship, NOON - SECOND, NOON);
    assert!(flight.is_warping(), "the regression reaches actual warp");
    pilot.flight = Some(flight);
    assert_eq!(structure_deposit_query(&mut simulation, structure_id, ship_id), refusal("IN_WARP"));
    simulation.scenes.get_mut(&SYSTEM).unwrap().pilots.get_mut(&SESSION).unwrap().flight = None;

    simulation.handle(Message::ControlStructure {
        session_id: SESSION, solar_system_id: SYSTEM, structure_id,
        character_id: spec().character_id, active: true,
    }, NOON).unwrap();
    assert_eq!(structure_deposit_query(&mut simulation, structure_id, ship_id), refusal("NO_SHIP_IN_SPACE"));

    for args in [
        vec![0.0, ship_id as f64], vec![-1.0, ship_id as f64],
        vec![structure_id as f64, 0.0], vec![structure_id as f64, -1.0],
        vec![1.5, ship_id as f64], vec![f64::NAN, ship_id as f64],
        vec![structure_id as f64, f64::INFINITY], vec![structure_id as f64, 9_007_199_254_740_992.0],
        vec![structure_id as f64, ship_id as f64, 1.0],
    ] {
        assert_eq!(simulation.handle(Message::Command { session_id: SESSION, command: 20, args }, NOON).unwrap(),
            refusal("UNKNOWN_COMMAND"));
    }
}

#[test]
fn docked_structure_control_dispatches_bind_updates_detach_and_leave() {
    let mut simulation = sim();
    let base = crate::scene::Scene::new(std::sync::Arc::new(big_station()));
    let placement = crate::scene::tests::structure_placement(&base, NOON);
    let structure_id = placement.structure_id;
    let mut changed_status = placement.status.clone();
    changed_status.state += 1;
    simulation.handle(Message::PlaceStructure(Box::new(placement)), NOON).unwrap();
    assert!(simulation.scenes.contains_key(&SYSTEM), "a structures-only scene is retained");

    let observer = 71;
    let missing = simulation.handle(Message::ControlStructure {
        session_id: observer, solar_system_id: SYSTEM, structure_id: structure_id + 1,
        character_id: spec().character_id, active: true,
    }, NOON).unwrap();
    assert!(missing.iter().any(|message| matches!(message,
        Message::Refused { session_id, command, reason }
            if *session_id == observer && *command == crate::link::COMMAND_STRUCTURE_CONTROL && reason.as_str() == "STRUCTURE_NOT_FOUND")));

    simulation.handle(enter(SESSION, STATION, SYSTEM), NOON + SECOND).unwrap();
    simulation.handle(Message::Bind { session_id: SESSION }, NOON + SECOND).unwrap();
    let owner_attached = simulation.handle(Message::ControlStructure {
        session_id: SESSION, solar_system_id: SYSTEM, structure_id,
        character_id: spec().character_id, active: true,
    }, NOON + 2 * SECOND).unwrap();
    assert!(owner_attached.iter().any(|message| matches!(message,
        Message::Accepted { session_id, command, .. }
            if *session_id == SESSION && *command == crate::link::COMMAND_STRUCTURE_CONTROL)));
    assert!(!simulation.scenes[&SYSTEM].pilots.contains_key(&SESSION));
    assert!(simulation.scenes[&SYSTEM].unpiloted.contains_key(&spec().ship_id));

    let attached = simulation.handle(Message::ControlStructure {
        session_id: observer, solar_system_id: SYSTEM, structure_id,
        character_id: 9002, active: true,
    }, NOON + 3 * SECOND).unwrap();
    assert!(attached.iter().any(|message| matches!(message,
        Message::Accepted { session_id, command, .. }
            if *session_id == observer && *command == crate::link::COMMAND_STRUCTURE_CONTROL)));
    assert_eq!(simulation.sessions.get(&observer), Some(&SYSTEM));
    assert_eq!(simulation.sessions.get(&SESSION), Some(&SYSTEM), "transferring control restores the previous pilot mapping");
    assert!(simulation.scenes[&SYSTEM].pilots.contains_key(&SESSION));
    assert!(!simulation.scenes[&SYSTEM].pilots.contains_key(&observer));

    let bound = simulation.handle(Message::Bind { session_id: observer }, NOON + 3 * SECOND).unwrap();
    assert!(names(&bound).iter().any(|name| name == "DoDestinyUpdate"));
    let snapshot = simulation.handle(Message::UpdateState { session_id: observer }, NOON + 3 * SECOND).unwrap();
    assert!(names(&snapshot).iter().any(|name| name == "DoDestinyUpdate"));
    let updated = simulation.handle(Message::SetStructureState {
        solar_system_id: SYSTEM, structure_id, status: changed_status,
    }, NOON + 3 * SECOND).unwrap();
    assert!(names(&updated).iter().any(|name| name == "DoDestinyUpdate"));

    let detached = simulation.handle(Message::ControlStructure {
        session_id: observer, solar_system_id: SYSTEM, structure_id: 0,
        character_id: 0, active: false,
    }, NOON + 4 * SECOND).unwrap();
    assert!(detached.iter().any(|message| matches!(message,
        Message::Accepted { session_id, command, .. }
            if *session_id == observer && *command == crate::link::COMMAND_STRUCTURE_CONTROL)));
    assert!(simulation.scenes[&SYSTEM].controlled_structure_id(observer).is_none());
    assert!(!simulation.sessions.contains_key(&observer));
    let repeated_detach = simulation.handle(Message::ControlStructure {
        session_id: observer, solar_system_id: SYSTEM, structure_id: 0,
        character_id: 0, active: false,
    }, NOON + 4 * SECOND).unwrap();
    assert!(repeated_detach.iter().any(|message| matches!(message,
        Message::Refused { session_id, command, reason }
            if *session_id == observer && *command == crate::link::COMMAND_STRUCTURE_CONTROL && reason.as_str() == "CONTROL_STRUCTURE_NOT_ACTIVE")));

    simulation.handle(Message::ControlStructure {
        session_id: observer, solar_system_id: SYSTEM, structure_id,
        character_id: spec().character_id, active: true,
    }, NOON + 5 * SECOND).unwrap();
    simulation.handle(Message::Bind { session_id: observer }, NOON + 5 * SECOND).unwrap();
    simulation.handle(Message::Leave { session_id: observer, why: 0 }, NOON + 6 * SECOND).unwrap();
    assert!(simulation.scenes[&SYSTEM].controlled_structure_id(observer).is_none());
    assert!(!simulation.sessions.contains_key(&observer));
    assert!(simulation.scenes.contains_key(&SYSTEM), "leaving the observer does not discard the structure");

    simulation.handle(Message::ControlStructure {
        session_id: observer, solar_system_id: SYSTEM, structure_id,
        character_id: spec().character_id, active: true,
    }, NOON + 7 * SECOND).unwrap();
    simulation.handle(Message::Bind { session_id: observer }, NOON + 7 * SECOND).unwrap();
    let removed = simulation.handle(Message::RemoveStructure { structure_id }, NOON + 8 * SECOND).unwrap();
    assert!(names(&removed).iter().any(|name| name == "DoDestinyUpdate"));
    assert!(simulation.scenes[&SYSTEM].structure_index(structure_id).is_none());
    assert!(simulation.scenes[&SYSTEM].controlled_structure_id(observer).is_none());
    assert!(!simulation.sessions.contains_key(&observer));
}

#[test]
fn tether_protocol62_frames_round_trip_in_little_endian_with_standard_bool() {
    let policy = Message::TetherPolicy {
        session_id: 7, blocked_for_ms: 1_500, lease_ms: 5_000,
        candidates: vec![
            crate::link::TetherCandidate { structure_id: 1_040_000_000_001, range: 10_000.0 },
            crate::link::TetherCandidate { structure_id: 1_040_000_000_002, range: 20_000.0 },
        ],
    };
    let frame = policy.encode();
    assert_eq!(u32::from_le_bytes(frame[..4].try_into().unwrap()), 61);
    assert_eq!(frame[4], 80);
    assert_eq!(&frame[5..13], &7_u64.to_le_bytes());
    assert_eq!(&frame[13..21], &1_500_u64.to_le_bytes());
    assert_eq!(&frame[21..29], &5_000_u64.to_le_bytes());
    assert_eq!(&frame[29..33], &2_u32.to_le_bytes());
    assert_eq!(&frame[33..41], &1_040_000_000_001_u64.to_le_bytes());
    assert_eq!(&frame[41..49], &10_000_f64.to_le_bytes());
    assert_eq!(Message::decode(&frame[4..]).unwrap(), policy);
    let changed = Message::TetherChanged {
        session_id: 7, previous_structure_id: 0, structure_id: 1_040_000_000_001, repairing: true,
    };
    let frame = changed.encode();
    assert_eq!(u32::from_le_bytes(frame[..4].try_into().unwrap()), 26);
    assert_eq!(frame[4], 81);
    assert_eq!(frame[29], 1);
    assert_eq!(Message::decode(&frame[4..]).unwrap(), changed);
    let mut invalid = frame[4..].to_vec();
    *invalid.last_mut().unwrap() = 2;
    assert!(matches!(Message::decode(&invalid), Err(crate::link::DecodeError::BadValue("tether repairing"))));
}

#[test]
fn tether_codec_rejects_nonpositive_nonfinite_ranges_truncation_and_trailing_bytes() {
    for range in [0.0, -1.0, f64::NAN, f64::INFINITY, f64::NEG_INFINITY] {
        let frame = Message::TetherPolicy {
            session_id: SESSION, blocked_for_ms: 0, lease_ms: 10,
            candidates: vec![crate::link::TetherCandidate { structure_id: 1, range }],
        }.encode();
        assert!(matches!(Message::decode(&frame[4..]), Err(crate::link::DecodeError::BadValue("tether range"))));
    }
    let policy = Message::TetherPolicy { session_id: SESSION, blocked_for_ms: 0, lease_ms: 10, candidates: Vec::new() };
    let frame = policy.encode();
    let mut malformed = frame[4..].to_vec();
    malformed[25..29].copy_from_slice(&u32::MAX.to_le_bytes());
    assert_eq!(Message::decode(&malformed), Err(crate::link::DecodeError::Truncated));
    let mut trailing = frame[4..].to_vec();
    trailing.push(0);
    assert_eq!(Message::decode(&trailing), Err(crate::link::DecodeError::Trailing));
}

#[test]
fn tether_host_policy_dispatches_to_the_real_scene_and_transitions_return_to_the_host() {
    let mut simulation = undocked();
    let placement = crate::scene::tests::structure_placement(&simulation.scenes[&SYSTEM], NOON);
    let structure_id = placement.structure_id;
    let position = [placement.position[0] + placement.radius + spec().radius + 500.0, 0.0, 0.0];
    simulation.handle(Message::PlaceStructure(Box::new(placement)), NOON).unwrap();
    let pilot = simulation.scenes.get_mut(&SYSTEM).unwrap().pilots.get_mut(&SESSION).unwrap();
    pilot.ship.position = eve_destiny::Vec3::new(position[0], position[1], position[2]);
    pilot.ship.velocity = eve_destiny::Vec3::ZERO;
    pilot.ship.speed_fraction = 0.0;
    pilot.ship.course = crate::ship::Course::Stop;
    pilot.protected_until = NOON;
    let policy = Message::TetherPolicy {
        session_id: SESSION, blocked_for_ms: 0, lease_ms: 1_000,
        candidates: vec![crate::link::TetherCandidate { structure_id, range: 1_000.0 }],
    };
    assert_eq!(simulation.handle(policy.clone(), NOON).unwrap(), [
        Message::TetherChanged { session_id: SESSION, previous_structure_id: 0, structure_id, repairing: false },
    ]);
    assert!(simulation.handle(policy, NOON).unwrap().is_empty());
    let expired = simulation.poll(NOON + SECOND);
    assert!(expired.iter().any(|message| matches!(message,
        Message::TetherChanged { session_id: SESSION, previous_structure_id, structure_id: 0, repairing: false }
        if *previous_structure_id == structure_id)));
    let wrong_direction = simulation.handle(
        Message::TetherChanged { session_id: SESSION, previous_structure_id: 0, structure_id, repairing: false }, NOON,
    ).unwrap_err();
    assert!(wrong_direction.0.contains("kind 81"));
    assert_eq!(crate::link::PROTOCOL_VERSION, 64);
}
