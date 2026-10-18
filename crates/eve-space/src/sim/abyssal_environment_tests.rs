use super::*;
use crate::link::{AbyssalEnvironmentObject, AbyssalPointDefense, AbyssalSceneObject};

fn cloud(scene: SceneKey, id: i64, effect_radius: f64) -> AbyssalEnvironmentObject {
    AbyssalEnvironmentObject {
        content_id: "12345678-1234-1234-1234-123456789abc".to_string(),
        body: AbyssalSceneObject {
            scene,
            item_id: id,
            type_id: 47_440,
            group_id: 1_971,
            category_id: 2,
            owner_id: 140_000_342,
            radius: 0.0,
            graphic_id: 22_080,
            name: "Medium Bioluminescence Cloud".to_string(),
            position: [0.0; 3],
            direction: [0.0, 0.0, 1.0],
            is_open: false,
        },
        effect_radius_meters: effect_radius,
        point_defense: None,
    }
}

#[test]
fn abyssal_environment_tracks_player_and_npc_entries_exits_and_presentation_only_radius() {
    let mut sim = sim();
    let scene = SceneKey { location_id: SYSTEM, instance_id: 882, room_index: 1 };
    ensure_abyssal(&mut sim, scene, NOON, 50_000.0, 0.0);
    let mut entry = abyssal_enter(SESSION, scene, 8_820_001, [0.0; 3]);
    let Message::Enter(player) = &mut entry else { unreachable!() };
    player.ship.mass = 1.0;
    player.ship.agility = 1.0;
    player.ship.max_velocity = 2_000.0;
    sim.handle(entry, NOON).unwrap();
    let mut request = abyssal_spawn(1, scene, 8_820_002, [2_000.0, 500.0, 0.0]);
    let Message::Spawn(spawn) = &mut request else { unreachable!() };
    spawn.ship.mass = 1.0;
    spawn.ship.agility = 1.0;
    spawn.ship.max_velocity = 1_500.0;
    let npc = sim.handle(request, NOON).unwrap();
    let npc_session = npc
        .iter()
        .find_map(|message| match message {
            Message::SpawnReply { outcome: Ok(session), .. } => Some(*session),
            _ => None,
        })
        .unwrap();
    let active = cloud(scene, 8_820_003, 1_000.0);
    let request = Message::EnsureAbyssalEnvironmentObject { request_id: 2, object: Box::new(active.clone()) };
    assert_eq!(Message::decode(&request.payload()).unwrap(), request);
    let entered = sim.handle(request.clone(), NOON).unwrap();
    assert!(entered.iter().any(|message| matches!(
        message,
        Message::AbyssalFieldMembership {
            source_item_id: 8_820_003,
            ship_id: 8_820_001,
            session_id: SESSION,
            inside: true,
            ..
        }
    )));
    assert_eq!(
        sim.handle(request, NOON).unwrap(),
        [Message::EnsureAbyssalEnvironmentObjectReply { request_id: 2, outcome: Ok(()) }]
    );
    let mut decor = cloud(scene, 8_820_004, 0.0);
    decor.body.radius = 75_000.0;
    let encoded = Message::EnsureAbyssalEnvironmentObject { request_id: 3, object: Box::new(decor.clone()) };
    assert_eq!(Message::decode(&encoded.payload()).unwrap(), encoded);
    sim.handle(encoded, NOON).unwrap();
    assert_eq!(sim.scenes[&scene].system().body(decor.body.item_id).unwrap().radius, 75_000.0);
    assert_eq!(sim.scenes[&scene].abyssal.as_ref().unwrap().boundary.radius_meters, 50_000.0);
    assert!(
        !sim.scenes[&scene].abyssal_field_memberships.keys().any(|(source, _)| *source == decor.body.item_id)
    );
    let late_bind = sim.handle(Message::Bind { session_id: SESSION }, NOON).unwrap();
    assert!(
        late_bind
            .iter()
            .any(|message| matches!(message, Message::Notify { name, .. } if name == "DoDestinyUpdate"))
    );
    let resident = sim.scenes.get_mut(&scene).unwrap();
    resident.command(SESSION, Command::GotoDirection(Vec3::new(1.0, 0.0, 0.0)), NOON);
    resident.command(SESSION, Command::SetSpeedFraction(1.0), NOON);
    resident.command(npc_session, Command::GotoDirection(Vec3::new(-1.0, 0.0, 0.0)), NOON);
    resident.command(npc_session, Command::SetSpeedFraction(1.0), NOON);
    let moved = sim.poll(NOON + SECOND);
    let player_after = &sim.scenes[&scene].pilots[&SESSION].ship;
    let npc_after = &sim.scenes[&scene].pilots[&npc_session].ship;
    assert!(
        player_after.position.x > 1_000.0,
        "position={:?} speed={} max={} course={:?}; npc={:?}",
        player_after.position,
        player_after.speed_fraction,
        player_after.spec.max_velocity,
        player_after.course,
        npc_after.position
    );
    assert!(
        moved.iter().any(|message| matches!(
            message,
            Message::AbyssalFieldMembership {
                source_item_id: 8_820_003,
                ship_id: 8_820_001,
                inside: false,
                ..
            }
        )),
        "memberships={:?}; events={:?}; player={:?}; npc={:?}",
        sim.scenes[&scene].abyssal_field_memberships,
        moved
            .iter()
            .filter(|message| matches!(message, Message::AbyssalFieldMembership { .. }))
            .collect::<Vec<_>>(),
        player_after.position,
        npc_after.position
    );
    assert!(moved.iter().any(|message| matches!(
        message,
        Message::AbyssalFieldMembership { source_item_id: 8_820_003, ship_id: 8_820_002, inside: true, .. }
    )));
    assert!(
        !moved.iter().any(|message| matches!(
            message,
            Message::AbyssalFieldMembership { source_item_id: 8_820_004, .. }
        ))
    );
}

#[test]
fn abyssal_suppressor_pulses_at_two_seconds_filters_type_list_and_intercepts_real_missiles() {
    let mut sim = sim();
    let scene = SceneKey { location_id: SYSTEM, instance_id: 883, room_index: 1 };
    ensure_abyssal(&mut sim, scene, NOON, 75_000.0, 0.0);
    sim.handle(abyssal_enter(SESSION, scene, 8_830_001, [0.0; 3]), NOON).unwrap();
    let mut sessions = Vec::new();
    for (id, category, type_id) in [(8_830_002, 18, 2_454), (8_830_003, 87, 48_265), (8_830_004, 87, 40_000)]
    {
        let mut request = abyssal_spawn(id as u64, scene, id, [100.0, 0.0, 0.0]);
        let Message::Spawn(spawn) = &mut request else { unreachable!() };
        spawn.ship.category_id = category;
        spawn.ship.type_id = type_id;
        spawn.ship.vitals = crate::health::Vitals {
            shield_capacity: 100.0,
            armor_hp: 100.0,
            structure_hp: 100.0,
            resonances: Default::default(),
            armor_pinned: false,
        };
        spawn.ship.shield_recharge_ms = 0.0;
        let reply = sim.handle(request, NOON).unwrap();
        sessions.push(
            reply
                .iter()
                .find_map(|message| match message {
                    Message::SpawnReply { outcome: Ok(session), .. } => Some(*session),
                    _ => None,
                })
                .unwrap(),
        );
    }
    let mut source = cloud(scene, 8_830_005, 15_000.0);
    source.body.type_id = 47_437;
    source.body.group_id = 1_981;
    source.body.radius = 7_500.0;
    source.body.graphic_id = 22_035;
    source.body.name = "Short-Range Deviant Automata Suppressor".to_string();
    source.point_defense = Some(AbyssalPointDefense {
        tick_interval_ms: 2_000,
        damage: [5.0, 0.0, 0.0, 0.0],
        type_list_id: 46,
        targets: [vec![87, 18, 8], vec![1_997], vec![], vec![], vec![], vec![48_265, 48_264, 48_263, 48_262]],
        effect_name: "effects.TriGlavianBS01a".to_string(),
    });
    let request = Message::EnsureAbyssalEnvironmentObject { request_id: 4, object: Box::new(source) };
    assert_eq!(Message::decode(&request.payload()).unwrap(), request);
    sim.handle(request, NOON).unwrap();
    let resident = sim.scenes.get_mut(&scene).unwrap();
    let normal_before = resident.pilots[&SESSION].ship.health.condition_at(NOON);
    let rocket = crate::missiles::Missile {
        charge_type_id: 266,
        group_id: 387,
        name: "Scourge Rocket".to_string(),
        radius: 300.0,
        mass: 100.0,
        agility: 5.22,
        hit_points: 90.0,
        max_velocity: 2_250.0,
        flight_time_ms: 60_000.0,
        explosion_radius: 20.0,
        explosion_velocity: 150.0,
        damage_reduction_factor: 0.644,
        damage_reduction_sensitivity: 5.5,
        dot: None,
    };
    let missile_id = 8_830_006;
    let shooter = crate::missile_entity::Shooter {
        session: SESSION,
        ship_id: 8_830_001,
        module_id: 8_830_007,
        weapon_type_id: 10_629,
        owner_id: 140_000_342,
        corporation_id: 0,
        alliance_id: 0,
    };
    let attacker = crate::weapons::Kinematics {
        position: [0.0; 3],
        velocity: [0.0; 3],
        radius: 1.0,
        signature_radius: 1.0,
    };
    let target = crate::weapons::Kinematics { position: [100_000.0, 0.0, 0.0], ..attacker };
    let missile = crate::missile_entity::MissileEntity::launch(
        missile_id, shooter, 8_830_001, rocket, [0.0; 4], &attacker, &target, NOON,
    );
    resident.missiles.insert(missile_id, missile);
    resident.advance_abyssal_environment(NOON + 2 * SECOND - 1);
    assert!(resident.missiles.contains_key(&missile_id));
    assert_eq!(resident.pilots[&sessions[0]].ship.health.condition_at(NOON).shield_charge, 1.0);
    resident.advance_abyssal_environment(NOON + 2 * SECOND);
    assert!(!resident.missiles.contains_key(&missile_id));
    assert!(resident.pilots[&sessions[0]].ship.health.condition_at(NOON + 2 * SECOND).shield_charge < 1.0);
    assert_eq!(resident.pilots[&sessions[1]].ship.health.condition_at(NOON + 2 * SECOND).shield_charge, 1.0);
    assert!(resident.pilots[&sessions[2]].ship.health.condition_at(NOON + 2 * SECOND).shield_charge < 1.0);
    assert_eq!(resident.pilots[&SESSION].ship.health.condition_at(NOON + 2 * SECOND), normal_before);
    let fx = resident.abyssal_environment_fx(SESSION, None, NOON + 2 * SECOND, true);
    let rendered = format!("{fx:?}");
    assert!(
        rendered.contains("OnSpecialFX")
            && rendered.contains("effects.TriGlavianBS01a")
            && rendered.contains("candidateTypeListId")
    );
}
