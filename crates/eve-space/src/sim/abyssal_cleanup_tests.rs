use super::*;

fn loot(scene: SceneKey, id: i64) -> crate::link::WreckPlacement {
    crate::link::WreckPlacement {
        scene,
        wreck_id: id,
        group_id: 12,
        kind: crate::wreck::WreckKind { type_id: 23, name: "Cargo Container".to_string(), graphic_id: None },
        position: [100.0, 0.0, 0.0],
        direction: [0.0, 0.0, 1.0],
        radius: 14.0,
        launcher_id: 0,
        credit: crate::death::Credit { character_id: 140_000_342, ..Default::default() },
        is_empty: false,
    }
}

fn cleanup(scene: SceneKey, npcs: &[i64], items: &[i64]) -> Message {
    Message::RemoveAbyssalScene {
        request_id: 99,
        scene,
        settled_npc_ids: npcs.to_vec(),
        settled_loot_ids: items.to_vec(),
    }
}

#[test]
fn abyssal_confirmed_teardown_rejects_participants_and_cross_room_ids_then_removes_exact_owned_objects_without_death()
 {
    let mut sim = sim();
    let scene = SceneKey { location_id: SYSTEM, instance_id: 884, room_index: 1 };
    let other = SceneKey { room_index: 2, ..scene };
    ensure_abyssal(&mut sim, scene, NOON, 75_000.0, 0.0);
    ensure_abyssal(&mut sim, other, NOON, 75_000.0, 0.0);
    sim.handle(abyssal_spawn(1, scene, 8_840_001, [0.0; 3]), NOON).unwrap();
    sim.handle(abyssal_spawn(2, other, 8_840_002, [0.0; 3]), NOON).unwrap();
    sim.handle(
        Message::EnsureAbyssalLootContainer {
            request_id: 3,
            content_id: "12345678-1234-1234-1234-123456789abc".to_string(),
            placement: Box::new(loot(scene, 8_840_003)),
        },
        NOON,
    )
    .unwrap();
    sim.handle(abyssal_enter(SESSION, scene, 8_840_004, [10_000.0, 0.0, 0.0]), NOON).unwrap();
    let request = cleanup(scene, &[8_840_001], &[8_840_003]);
    assert_eq!(Message::decode(&request.payload()).unwrap(), request);
    assert!(
        matches!(sim.handle(request.clone(), NOON).unwrap().last(), Some(Message::RemoveAbyssalSceneReply { outcome: Err(reason), .. }) if reason == "ABYSSAL_SCENE_NOT_EMPTY")
    );
    assert!(sim.scenes[&scene].session_of(8_840_001).is_some());
    assert!(sim.scenes[&scene].wreck(8_840_003).is_some());
    sim.handle(
        Message::MoveAbyssalParticipant {
            request_id: 4,
            ship_id: 8_840_004,
            source: scene,
            destination: SceneKey::solar_system(SYSTEM),
            preserve_source_pose: false,
            session_handoff: true,
            position: [100_000.0, 0.0, 0.0],
            direction: [0.0, 0.0, 1.0],
        },
        NOON,
    )
    .unwrap();
    let wrong_room = sim.handle(cleanup(scene, &[8_840_002], &[8_840_003]), NOON).unwrap();
    assert!(
        matches!(wrong_room.last(), Some(Message::RemoveAbyssalSceneReply { outcome: Err(reason), .. }) if reason == "ABYSSAL_CLEANUP_ENTITY_SCENE_MISMATCH")
    );
    assert!(sim.scenes[&scene].session_of(8_840_001).is_some());
    let removed = sim.handle(request.clone(), NOON).unwrap();
    assert!(
        !removed.iter().any(|message| matches!(message, Message::Destroyed { .. })),
        "teardown is not a kill, and cannot award bounty or announce a room kill"
    );
    assert_eq!(removed.last(), Some(&Message::RemoveAbyssalSceneReply { request_id: 99, outcome: Ok(()) }));
    assert!(!sim.scenes.contains_key(&scene));
    assert!(sim.scenes[&other].session_of(8_840_002).is_some());
    assert!(sim.scenes[&SceneKey::solar_system(SYSTEM)].session_of(8_840_004).is_some());
    assert_eq!(
        sim.handle(request, NOON).unwrap(),
        [Message::RemoveAbyssalSceneReply { request_id: 99, outcome: Ok(()) }]
    );
}

#[test]
fn abyssal_dead_participant_is_not_hidden_by_teardown_until_its_confirmed_settlement_detach() {
    let mut sim = sim();
    let scene = SceneKey { location_id: SYSTEM, instance_id: 885, room_index: 1 };
    ensure_abyssal(&mut sim, scene, NOON, 75_000.0, 0.0);
    let ship_id = 8_850_001;
    sim.handle(abyssal_enter(SESSION, scene, ship_id, [0.0; 3]), NOON).unwrap();
    let death = sim.scenes.get_mut(&scene).unwrap().destroy(SESSION, None, NOON);
    sim.forget_the_destroyed(&death);
    assert!(sim.scenes[&scene].abyssal_participant_deaths.contains_key(&ship_id));
    assert!(
        sim.scenes[&scene].widowed.is_empty(),
        "Abyssal death must not prepare a normal capsule respawn inside the run"
    );
    assert!(
        matches!(sim.handle(cleanup(scene, &[], &[]), NOON).unwrap().last(), Some(Message::RemoveAbyssalSceneReply { outcome: Err(reason), .. }) if reason == "ABYSSAL_SCENE_NOT_EMPTY")
    );
    let detach = Message::RemoveAbyssalParticipant { request_id: 5, scene, ship_id };
    assert_eq!(
        sim.handle(detach.clone(), NOON).unwrap(),
        [Message::RemoveAbyssalParticipantReply { request_id: 5, already_absent: true, error: None }]
    );
    assert!(!sim.scenes[&scene].abyssal_participant_deaths.contains_key(&ship_id));
    assert!(!sim.widowed.contains_key(&SESSION));
    assert_eq!(
        sim.handle(detach, NOON).unwrap(),
        [Message::RemoveAbyssalParticipantReply { request_id: 5, already_absent: true, error: None }]
    );
    assert_eq!(
        sim.handle(cleanup(scene, &[], &[]), NOON).unwrap(),
        [Message::RemoveAbyssalSceneReply { request_id: 99, outcome: Ok(()) }]
    );
}

#[test]
fn abyssal_teardown_does_not_drop_unlisted_cargo_or_other_unsettled_room_objects() {
    let mut sim = sim();
    let scene = SceneKey { location_id: SYSTEM, instance_id: 886, room_index: 1 };
    ensure_abyssal(&mut sim, scene, NOON, 75_000.0, 0.0);
    sim.handle(abyssal_spawn(1, scene, 8_860_001, [0.0; 3]), NOON).unwrap();
    sim.handle(Message::PlaceWreck(Box::new(loot(scene, 8_860_002))), NOON).unwrap();
    let reply = sim.handle(cleanup(scene, &[8_860_001], &[]), NOON).unwrap();
    assert!(
        matches!(reply.last(), Some(Message::RemoveAbyssalSceneReply { outcome: Err(reason), .. }) if reason == "ABYSSAL_SCENE_NOT_EMPTY")
    );
    assert!(!reply.iter().any(|message| matches!(message, Message::Destroyed { .. })));
    assert!(sim.scenes[&scene].wreck(8_860_002).is_some());
    assert_eq!(
        sim.handle(cleanup(scene, &[8_860_001], &[8_860_002]), NOON).unwrap().last(),
        Some(&Message::RemoveAbyssalSceneReply { request_id: 99, outcome: Ok(()) })
    );
}

#[test]
fn abyssal_jettison_fences_db_scope_against_a_group_gate_move_and_ship_swap() {
    let mut sim = sim();
    let first = SceneKey { location_id: SYSTEM, instance_id: 887, room_index: 1 };
    let next = SceneKey { room_index: 2, ..first };
    let other_run = SceneKey { instance_id: 888, ..next };
    for scene in [first, next, other_run] {
        ensure_abyssal(&mut sim, scene, NOON, 75_000.0, 0.0);
    }
    let ship_id = 8_870_001;
    sim.handle(abyssal_enter(SESSION, first, ship_id, [0.0; 3]), NOON).unwrap();
    let make_can = |scene, launcher_id| {
        Message::PlaceContainer(Box::new(crate::link::ContainerPlacement {
            session_id: SESSION,
            container_id: 8_870_002,
            kind: crate::wreck::WreckKind {
                type_id: 23,
                name: "Cargo Container".into(),
                graphic_id: Some(1176),
            },
            radius: 14.0,
            launcher_id,
            credit: crate::death::Credit::default(),
            distance: 275.0,
            expected_scene: Some(scene),
        }))
    };
    let request = make_can(first, ship_id);
    assert_eq!(Message::decode(&request.payload()).unwrap(), request);
    sim.handle(
        Message::MoveAbyssalParticipant {
            request_id: 5,
            ship_id,
            source: first,
            destination: next,
            preserve_source_pose: false,
            session_handoff: false,
            position: [0.0; 3],
            direction: [0.0, 0.0, 1.0],
        },
        NOON,
    )
    .unwrap();
    assert_eq!(sim.sessions.get(&SESSION), Some(&next));
    for expected in [first, other_run, SceneKey::solar_system(SYSTEM)] {
        let reply = sim.handle(make_can(expected, ship_id), NOON).unwrap();
        assert!(
            matches!(reply.last(), Some(Message::Refused { reason, .. }) if reason == "SCENE_CHANGED"),
            "{reply:?}"
        );
        assert!(sim.scenes.values().all(|scene| scene.wreck(8_870_002).is_none()));
    }
    let reply = sim.handle(make_can(next, ship_id + 1), NOON).unwrap();
    assert!(matches!(reply.last(), Some(Message::Refused { reason, .. }) if reason == "SHIP_CHANGED"));
    assert!(sim.scenes.values().all(|scene| scene.wreck(8_870_002).is_none()));
    let reply = sim.handle(make_can(next, ship_id), NOON).unwrap();
    assert!(matches!(reply.last(), Some(Message::ContainerPlaced { container_id: 8_870_002, .. })));
    assert!(sim.scenes[&next].wreck(8_870_002).is_some());
}

#[test]
fn abyssal_gameplay_warp_and_cloak_guards_do_not_block_manager_room_transitions() {
    let mut sim = sim();
    let first = SceneKey { location_id: SYSTEM, instance_id: 889, room_index: 1 };
    let second = SceneKey { room_index: 2, ..first };
    for scene in [first, second] {
        ensure_abyssal(&mut sim, scene, NOON, 75_000.0, 0.0);
    }
    sim.handle(abyssal_enter(SESSION, first, 8_890_001, [0.0; 3]), NOON).unwrap();
    for (command, args) in [(8, vec![400_000_001.0, 0.0]), (21, vec![1_000_000.0, 0.0, 0.0, 0.0])] {
        let reply = sim.handle(Message::Command { session_id: SESSION, command, args }, NOON).unwrap();
        assert!(reply.iter().any(|message| matches!(message, Message::Refused { reason, .. } if reason == "ABYSSAL_WARP_RESTRICTED")), "{reply:?}");
    }
    let mut effect = afterburner(8_890_002);
    effect.action = Some(crate::modules::Action::Cloak { warp_safe: true, targeting_delay_ms: 0.0 });
    let reply =
        sim.handle(Message::Activate { session_id: SESSION, effect: Box::new(effect) }, NOON).unwrap();
    assert!(reply.iter().any(|message| matches!(message, Message::Refused { reason, .. } if reason == "ABYSSAL_CLOAK_RESTRICTED")), "{reply:?}");
    let reply = sim
        .handle(
            Message::MoveAbyssalParticipant {
                request_id: 9,
                ship_id: 8_890_001,
                source: first,
                destination: second,
                preserve_source_pose: true,
                session_handoff: false,
                position: [0.0; 3],
                direction: [0.0, 0.0, 1.0],
            },
            NOON,
        )
        .unwrap();
    assert!(matches!(reply.last(), Some(Message::MoveAbyssalParticipantReply { outcome: Ok(()), .. })));
    assert_eq!(sim.sessions.get(&SESSION), Some(&second));
}

#[test]
fn abyssal_deployment_snapshot_uses_exact_scene_deduped_centers_and_live_ship_state() {
    let mut world = big_station();
    let base = world.bodies[0].clone();
    world.bodies.clear();
    for (id, x) in [(8_910_002, 1_000_000.0), (8_910_003, 1_000_000.01), (8_910_004, 2_000_000.0)] {
        let mut body = base.clone();
        body.type_id = 23;
        body.id = id;
        body.position = eve_destiny::Vec3::new(x, 0.0, 0.0);
        body.radius = 100_000.0;
        world.bodies.push(body);
    }
    let mut sim = Sim::new(Arc::new(Universe::with_systems([world])));
    let source = SceneKey::solar_system(SYSTEM);
    let private = SceneKey { location_id: SYSTEM, instance_id: 891, room_index: 1 };
    ensure_abyssal(&mut sim, private, NOON, 75_000.0, 0.0);
    sim.handle(abyssal_enter(SESSION, source, 8_910_001, [0.0; 3]), NOON).unwrap();
    sim.handle(abyssal_enter(SESSION + 1, source, 8_910_004, [5_000.0, 0.0, 0.0]), NOON).unwrap();
    sim.handle(abyssal_enter(SESSION + 2, private, 8_910_005, [0.0; 3]), NOON).unwrap();
    let request = Message::GetAbyssalParticipantPose { request_id: 1, scene: source, ship_id: 8_910_001 };
    let reply = sim.handle(request.clone(), NOON).unwrap();
    let Message::GetAbyssalParticipantPoseReply { deployment: Some(snapshot), .. } = &reply[0] else {
        panic!("{reply:?}")
    };
    assert_eq!(
        snapshot.nearby_objects.iter().map(|object| object.item_id).collect::<Vec<_>>(),
        [8_910_002, 8_910_004],
        "reply={reply:?} bodies={:?}",
        sim.scenes[&source].system().bodies
    );
    assert_eq!(snapshot.nearby_objects[0].center_distance_meters, 1_000_000.0);
    assert_eq!(snapshot.nearby_objects[1].center_distance_meters, 5_000.0);
    assert!(!snapshot.warping && !snapshot.cloaked && !snapshot.cyno_active && !snapshot.warp_scrambled);
    assert_eq!(Message::decode(&reply[0].payload()).unwrap(), reply[0]);
    let pilot = sim.scenes.get_mut(&source).unwrap().pilots.get_mut(&SESSION).unwrap();
    pilot.cloak = crate::visibility::CloakState::Cloaked;
    pilot.protected_until = NOON + SECOND;
    pilot.incoming.upsert(
        99,
        98,
        crate::ewar::Lasting::Scramble { strength: 2.0, blocks_mwd: true },
        NOON + SECOND,
    );
    let reply = sim.handle(request, NOON).unwrap();
    let Message::GetAbyssalParticipantPoseReply { deployment: Some(snapshot), .. } = &reply[0] else {
        panic!("{reply:?}")
    };
    assert!(snapshot.cloaked && snapshot.undock_invulnerable && snapshot.warp_scrambled);
    assert_eq!(Message::decode(&reply[0].payload()).unwrap(), reply[0]);
}
