use eve_marshal::Value;

use super::*;
use crate::brains::rat_tests::{
    LATER, PLAYER, RAT_SHIP, SECOND, actions_named, notified, place_near, rat_identity, rat_spec, run,
    scene_with_a_player,
};
use crate::health::{Condition, Resonances, Vitals};
use crate::scene::Departure;
use crate::weapons::Turret;

const DRONE_SHIP: i64 = 9_988_400_003_716;

/// A Hobgoblin I as the reference recorded it (`drone_flight.json`): 2,800 m/s, 15 m across, and a laser that does 32 thermal a shot every four seconds out to 2,100 m.
fn hobgoblin() -> ShipSpec {
    let mut drone = crate::scene::tests::spec();
    drone.ship_id = DRONE_SHIP;
    drone.character_id = 0;
    drone.type_id = 2454;
    drone.group_id = 100;
    drone.category_id = 18;
    drone.name = "Hobgoblin I".to_string();
    drone.radius = 15.0;
    drone.mass = 3_000.0;
    drone.max_velocity = 2_800.0;
    drone.agility = 100.0;
    drone.modules = Vec::new();
    drone.vitals = Vitals {
        shield_capacity: 50.0,
        armor_hp: 90.0,
        structure_hp: 200.0,
        resonances: Resonances { shield: [0.5; 4], armor: [0.5; 4], structure: [0.5; 4] },
        armor_pinned: false,
    };
    drone.condition = Condition::FULL;
    drone
}

fn laser() -> NpcWeapon {
    NpcWeapon {
        module_id: DRONE_SHIP,
        effect_id: 10,
        cycle_ms: 4_000.0,
        capacitor_need: 0.0,
        turret: Turret {
            weapon_type_id: 2454,
            fx: "effects.Laser".to_string(),
            raw_damage: [0.0, 32.0, 0.0, 0.0],
            optimal_range: 2_100.0,
            falloff: 2_000.0,
            tracking_speed: 1.815,
            signature_resolution: 25.0,
            charge: None,
            missile: None,
            spool: None,
            aoe: None,
            arc: None,
            requires_gunnery: true,
        },
    }
}

fn launched() -> (Scene, SessionId, Vec<Out>) {
    let mut scene = scene_with_a_player();
    let flight = DroneFlight {
        weapon: Some(laser()),
        graphic_id: 1_099,
        orbit_distance: 700.0,
        attack_range: 2_800.0,
        cruise_fraction: 660.0 / 2_800.0,
        work: DroneWorkProfile::default(),
        effect: None,
    };
    let (drone, told) = scene.launch_drone(PLAYER, hobgoblin(), flight, LATER).unwrap();
    (scene, drone, told)
}

#[test]
fn an_existing_drone_attack_task_cannot_hurt_a_tethered_ship() {
    let (mut scene, drone, _) = launched();
    let mut target = crate::scene::tests::spec();
    target.ship_id += 200;
    target.character_id += 200;
    target.max_velocity = 0.0;
    target.modules.clear();
    let target_id = target.ship_id;
    let target_session = PLAYER + 200;
    let p = scene.pilots[&drone].ship.position;
    scene.enter_in_space(target_session, target, [p.x + 100.0, p.y, p.z], [0.0, 0.0, 1.0], LATER).unwrap();
    let mut structure = crate::scene::tests::structure_placement(&scene, LATER);
    structure.position = [p.x + 100.0 + structure.radius + 47.0 + 500.0, p.y, p.z];
    let structure_id = structure.structure_id;
    scene.place_structure(structure, LATER);
    scene.set_tether_policy(
        target_session,
        0,
        60_000,
        vec![crate::link::TetherCandidate { structure_id, range: 1_000.0 }],
        LATER,
    );
    assert!(scene.is_tethered(target_id, LATER));
    scene.engage(drone, target_id, LATER);
    for second in 1..=5 {
        scene.poll(LATER + second * SECOND);
    }
    assert_eq!(
        Scene::condition_of(&scene.pilots[&target_session], LATER + 5 * SECOND),
        crate::health::Condition::FULL
    );
}

#[test]
fn a_drone_starting_an_offensive_effect_breaks_its_controllers_tether() {
    let mut scene = scene_with_a_player();
    scene.pilots.get_mut(&PLAYER).unwrap().ship.spec.corporation_id = 1_000_006;
    let drone = launched_with(&mut scene, webber());
    spawn_rat_at(&mut scene, RAT_SHIP, 2_000.0);
    let p = scene.pilots[&PLAYER].ship.position;
    let mut structure = crate::scene::tests::structure_placement(&scene, LATER);
    structure.position = [p.x - structure.radius - 47.0 - 500.0, p.y, p.z];
    let structure_id = structure.structure_id;
    scene.place_structure(structure, LATER);
    let candidates = vec![crate::link::TetherCandidate { structure_id, range: 10_000.0 }];
    scene.set_tether_policy(PLAYER, 0, 60_000, candidates.clone(), LATER);
    assert!(scene.is_tethered(scene.pilots[&PLAYER].ship.spec.ship_id, LATER));
    let sent = scene.drone_command(PLAYER, &[DRONE_SHIP], DroneCommand::Engage { target: RAT_SHIP }, LATER);
    assert_eq!(result_of(&sent), [(DRONE_SHIP, None)]);
    let immediate_changes = sent
        .iter()
        .filter(|out| {
            matches!(out,
        Out::TetherChanged { session: PLAYER, previous_structure_id, structure_id: 0, .. }
        if *previous_structure_id == structure_id)
        })
        .count();
    assert!(immediate_changes <= 1);
    let mut disengaged_at = (immediate_changes == 1).then_some(LATER);
    for tenth in 1..=100 {
        if disengaged_at.is_some() {
            break;
        }
        let at = LATER + tenth * SECOND / 10;
        let told = scene.poll(at);
        let changes = told
            .iter()
            .filter(|out| {
                matches!(out,
            Out::TetherChanged { session: PLAYER, previous_structure_id, structure_id: 0, .. }
            if *previous_structure_id == structure_id)
            })
            .count();
        if changes > 0 {
            assert_eq!(changes, 1);
            disengaged_at = Some(at);
            break;
        }
    }
    let at = disengaged_at.expect("the drone started its offensive cycle");
    assert!(scene.drone_is_attacking(drone));
    let stale_grant = scene.set_tether_policy(PLAYER, 0, 60_000, candidates.clone(), at);
    assert!(
        !stale_grant
            .iter()
            .any(|out| matches!(out, Out::TetherChanged { structure_id, .. } if *structure_id != 0))
    );
    assert!(!scene.is_tethered(scene.pilots[&PLAYER].ship.spec.ship_id, at));
    scene.drone_command(PLAYER, &[DRONE_SHIP], DroneCommand::ReturnHome, at);
    assert!(!scene.drone_is_attacking(drone));
    assert!(scene.tick_tethers(at).is_empty(), "the stale grant does not survive the attack");
    let refreshed = scene.set_tether_policy(PLAYER, 0, 60_000, candidates, at);
    assert!(refreshed.iter().any(|out| matches!(out, Out::TetherChanged { session: PLAYER, structure_id: id, .. } if *id == structure_id)));
}

fn work_drone() -> (Scene, SessionId, Vec<Out>) {
    let (mut scene, drone, outputs) = launched();
    scene.drones.get_mut(&drone).expect("launched drone").work = DroneWorkProfile {
        mining_amount_m3: 25.0,
        cycle_ms: 60_000.0,
        waste_multiplier: 0.0,
        waste_probability: 0.0,
        salvage_access_bonus: 3.0,
    };
    (scene, drone, outputs)
}

fn ore_asteroid(id: i64, position: Vec3, units_remaining: i64) -> crate::wreck::Asteroid {
    crate::wreck::Asteroid {
        id,
        type_id: 0,
        group_id: 0,
        category_id: 0,
        name: "Test ore".into(),
        graphic_id: None,
        position,
        direction: Vec3::new(1.0, 0.0, 0.0),
        radius: 10.0,
        units_remaining,
    }
}
fn for_the_player(told: &[Out]) -> Vec<Out> {
    told.iter().filter(|o| o.session() == PLAYER).cloned().collect()
}

fn names_of(told: &[Out]) -> Vec<String> {
    told.iter()
        .filter_map(|out| match out {
            Out::Notify { name: "DoDestinyUpdate", args, .. } => Some(args),
            _ => None,
        })
        .flat_map(|args| {
            let Some(Value::List(actions)) = args.first() else { return Vec::new() };
            actions
                .iter()
                .filter_map(|action| {
                    let Value::Tuple(pair) = action else { return None };
                    let Value::Tuple(inner) = &pair[1] else { return None };
                    let Value::Str(name) = &inner[0] else { return None };
                    Some(name.to_string())
                })
                .collect()
        })
        .collect()
}

fn states(told: &[Out]) -> Vec<i64> {
    notified(told, "OnDroneStateChange")
        .into_iter()
        .filter_map(|args| match args.get(3) {
            Some(Value::Int(state)) => Some(*state),
            Some(Value::Long(state)) => Some(*state),
            _ => None,
        })
        .collect()
}

#[test]
fn a_drone_is_told_to_its_controller_as_the_reference_tells_it_and_circles_the_ship_at_the_speed_of_its_cruise()
 {
    let (scene, drone, told) = launched();
    let told = for_the_player(&told);
    // one update adds the ball and says it is idle, the next sets it circling at a fraction of its speed (what the recorded launch is)
    let updates: Vec<Vec<String>> = told.iter().map(|out| names_of(std::slice::from_ref(out))).collect();
    assert_eq!(updates, [vec!["OnDroneStateChange", "AddBalls2"], vec!["Orbit", "SetSpeedFraction"]]);
    let orbit = &actions_named(&told, "Orbit")[0];
    assert_eq!(
        orbit[..],
        [Value::Long(DRONE_SHIP), Value::Long(crate::scene::tests::spec().ship_id), Value::Int(700)]
    );
    let fraction = &actions_named(&told, "SetSpeedFraction")[0];
    assert_eq!(fraction[1], Value::Real(660.0 / 2_800.0)); // the drone is of the controller, not a ship that could be locked as a pilot's
    assert_eq!(scene.drones_of(PLAYER), [DRONE_SHIP]);
    assert_eq!(scene.controller_of_drone(drone), Some(PLAYER));
    // its slim item is the drone's: the type's name and graphic and what it is doing
    let added = &actions_named(&told, "AddBalls2")[0];
    let Value::Tuple(parts) = &added[0] else { panic!("{added:?}") };
    let Value::List(slims) = &parts[1] else { panic!("{parts:?}") };
    let Value::Tuple(slim) = &slims[0] else { panic!("{slims:?}") };
    let Value::Dict(entries) = &slim[0] else { panic!("{slim:?}") };
    let entry = |name: &str| {
        entries.iter().find(|(k, _)| matches!(k, Value::Str(n) if n == name)).map(|(_, v)| v.clone())
    };
    assert_eq!(entry("name"), Some(Value::Str("Hobgoblin I".into())));
    assert_eq!(
        (entry("graphicID"), entry("activityState"), entry("categoryID")),
        (Some(Value::Int(1_099)), Some(Value::Int(0)), Some(Value::Int(18)))
    );
}

#[test]
fn a_drone_sent_at_a_rat_flies_at_it_fights_and_kills_it_and_the_pilots_log_tells_the_drones_shots_as_its_own()
 {
    let (mut scene, drone, _) = launched();
    let mut rat = rat_spec();
    // fifteen points of hull and nothing else: one hit of a drone kills it
    rat.vitals = Vitals {
        shield_capacity: 0.0,
        armor_hp: 0.0,
        structure_hp: 15.0,
        resonances: Resonances { shield: [1.0; 4], armor: [1.0; 4], structure: [1.0; 4] },
        armor_pinned: false,
    };
    let place = place_near(&mut scene, 1_500.0);
    let (rat_session, _) = scene.spawn_npc(rat, rat_identity(0.0), place, LATER);
    let sent = scene.drone_command(PLAYER, &[DRONE_SHIP], DroneCommand::Engage { target: RAT_SHIP }, LATER);
    let sent = for_the_player(&sent);
    // at 1,500 m the rat is within the laser's 2,100 m: the drone is told as fighting at once, circling the rat at full speed
    assert_eq!(states(&sent), [COMBAT as i64]);
    assert_eq!(
        actions_named(&sent, "Orbit")[0][..],
        [Value::Long(DRONE_SHIP), Value::Long(RAT_SHIP), Value::Int(700)]
    );
    assert_eq!(actions_named(&sent, "SetSpeedFraction")[0][1], Value::Real(1.0));
    let results = sent.iter().find_map(|out| match out {
        Out::DroneResults { results, command, .. } => Some((*command, results.clone())),
        _ => None,
    });
    assert_eq!(results, Some((1, vec![(DRONE_SHIP, None)])));

    let mut everything = Vec::new();
    let mut now = LATER + SECOND / 4;
    while now <= LATER + 40 * SECOND {
        everything.extend(scene.poll(now));
        now += SECOND / 4;
    }
    let told = for_the_player(&everything);
    // the pilot's combat log has the drone's shot as its own, and the rat is gone
    let hits = notified(&told, "OnDamageMessage");
    assert!(!hits.is_empty(), "the drone never hit");
    let Some(Value::Dict(first)) = hits[0].first() else { panic!("{hits:?}") };
    let field = |name: &str| {
        first.iter().find(|(k, _)| matches!(k, Value::Str(n) if n == name)).map(|(_, v)| v.clone())
    };
    assert_eq!(field("attackType"), Some(Value::Str("me".into())));
    assert_eq!(field("source"), Some(Value::Long(DRONE_SHIP)));
    assert_eq!(field("target"), Some(Value::Long(RAT_SHIP)));
    assert_eq!(field("weapon"), Some(Value::Int(2_454)));
    assert!(
        scene.session_of(RAT_SHIP).is_none() && !scene.pilots.contains_key(&rat_session),
        "the rat is destroyed"
    );
    // the kill is the pilot's: the host is told its ship did it, not the drone
    assert!(everything.iter().any(|o| matches!(o, Out::Destroyed { ship_id, killer_ship, .. } if *ship_id == RAT_SHIP && *killer_ship == Some(crate::scene::tests::spec().ship_id))));
    // then the drone is idle again and circles its controller
    assert_eq!(states(&told).last(), Some(&0));
    assert!(
        actions_named(&told, "Orbit")
            .iter()
            .any(|a| a[1] == Value::Long(crate::scene::tests::spec().ship_id))
    );
    assert_eq!(scene.activity_of(drone), IDLE);
}

#[test]
fn a_drone_whose_controller_deals_no_drone_damage_does_not_kill_the_rat_it_fights() {
    let (mut scene, _drone, _) = launched();
    scene.pilots.get_mut(&PLAYER).expect("the controller is in space").ship.spec.drone_damage_multiplier =
        0.0;
    let mut rat = rat_spec();
    rat.vitals = Vitals {
        shield_capacity: 0.0,
        armor_hp: 0.0,
        structure_hp: 15.0,
        resonances: Resonances { shield: [1.0; 4], armor: [1.0; 4], structure: [1.0; 4] },
        armor_pinned: false,
    };
    let place = place_near(&mut scene, 1_500.0);
    let (rat_session, _) = scene.spawn_npc(rat, rat_identity(0.0), place, LATER);
    let _ = scene.drone_command(PLAYER, &[DRONE_SHIP], DroneCommand::Engage { target: RAT_SHIP }, LATER);

    let mut everything = Vec::new();
    let mut now = LATER + SECOND / 4;
    while now <= LATER + 40 * SECOND {
        everything.extend(scene.poll(now));
        now += SECOND / 4;
    }
    assert!(
        !everything.iter().any(|o| matches!(o, Out::Destroyed { ship_id, .. } if *ship_id == RAT_SHIP)),
        "the rat survives: the drone deals no damage"
    );
    assert!(scene.session_of(RAT_SHIP).is_some() && scene.pilots.contains_key(&rat_session));
}

#[test]
fn a_drone_brought_to_the_bay_is_scooped_once_it_is_near_and_the_host_is_told() {
    let (mut scene, _, _) = launched();
    let sent = scene.drone_command(PLAYER, &[DRONE_SHIP], DroneCommand::ReturnBay, LATER);
    assert_eq!(states(&for_the_player(&sent)), [DEPARTING as i64]);
    let told = run(&mut scene, LATER + SECOND / 4, 3);
    assert!(
        told.iter().any(|o| matches!(o, Out::DroneScooped { drone_id, .. } if *drone_id == DRONE_SHIP)),
        "{told:?}"
    );
    // the ball is taken away with the drone's list entry
    assert!(names_of(&told).contains(&"RemoveBalls".to_string()));
    assert!(scene.drones_of(PLAYER).is_empty());
    assert!(scene.session_of(DRONE_SHIP).is_none());
}

#[test]
fn an_order_to_a_ship_that_is_not_a_drone_of_the_pilot_is_refused_for_that_ship_alone() {
    let (mut scene, _, _) = launched();
    let place = place_near(&mut scene, 1_500.0);
    scene.spawn_npc(rat_spec(), rat_identity(0.0), place, LATER);
    let told = scene.drone_command(PLAYER, &[DRONE_SHIP, RAT_SHIP, 12_345], DroneCommand::ReturnHome, LATER);
    let results = told.iter().find_map(|out| match out {
        Out::DroneResults { results, .. } => Some(results.clone()),
        _ => None,
    });
    assert_eq!(
        results,
        Some(vec![(DRONE_SHIP, None), (RAT_SHIP, Some("NOT_CONTROLLED")), (12_345, Some("NOT_CONTROLLED"))])
    );
    // and a pilot does not send its drones at itself
    let at_itself = scene.drone_command(
        PLAYER,
        &[DRONE_SHIP],
        DroneCommand::Engage { target: crate::scene::tests::spec().ship_id },
        LATER,
    );
    assert!(at_itself.iter().any(|o| matches!(o, Out::DroneResults { results, .. } if results == &vec![(DRONE_SHIP, Some("NO_TARGET"))])));
}

fn spawn_rat_at(scene: &mut Scene, ship_id: i64, distance: f64) -> SessionId {
    let mut rat = rat_spec();
    rat.ship_id = ship_id;
    // from where the pilot is when the order is given (the ship is flying: the place the other tests use is where it was at noon)
    let position = scene.ship(PLAYER, LATER).unwrap().position;
    let place = NpcPlace::at_rest(Vec3::new(position.x + distance, position.y, position.z));
    scene.spawn_npc(rat, rat_identity(0.0), place, LATER).0
}

fn result_of(told: &[Out]) -> Vec<(i64, Option<&'static str>)> {
    told.iter()
        .find_map(|out| match out {
            Out::DroneResults { results, .. } => Some(results.clone()),
            _ => None,
        })
        .unwrap_or_default()
}

fn target_of_the_state(told: &[Out]) -> Option<Value> {
    notified(told, "OnDroneStateChange").last().and_then(|args| args.get(6).cloned())
}

#[test]
fn an_order_to_engage_with_nothing_locked_sends_the_drone_at_the_nearest_npc_it_finds_by_itself() {
    let (mut scene, _, _) = launched();
    // the farther rat is placed first: it is the nearer that is found
    spawn_rat_at(&mut scene, RAT_SHIP + 1, 9_000.0);
    spawn_rat_at(&mut scene, RAT_SHIP, 3_000.0);
    let sent = scene.drone_command(PLAYER, &[DRONE_SHIP], DroneCommand::Engage { target: 0 }, LATER);
    assert_eq!(result_of(&sent), [(DRONE_SHIP, None)]);
    let told = for_the_player(&sent);
    assert_eq!(target_of_the_state(&told), Some(Value::Long(RAT_SHIP)), "{told:?}");
    // it flies at the rat it found and circles it, as when it is sent at it by name
    assert_eq!(actions_named(&told, "Orbit")[0][..2], [Value::Long(DRONE_SHIP), Value::Long(RAT_SHIP)]);
    assert_eq!(scene.drones[&scene.session_of(DRONE_SHIP).unwrap()].task, Task::Engage { target: RAT_SHIP });
}

#[test]
fn the_drones_look_for_those_that_attacked_the_pilot_before_the_nearest_and_never_beyond_their_range() {
    let (mut scene, _, _) = launched();
    spawn_rat_at(&mut scene, RAT_SHIP, 3_000.0);
    spawn_rat_at(&mut scene, RAT_SHIP + 1, 8_000.0);
    // a rat out of reach is nobody's to find, though it is the one that attacked
    spawn_rat_at(&mut scene, RAT_SHIP + 2, 45_000.0);
    let attacker = |ship: i64| crate::weapons::Aggression {
        attacker_ship: ship,
        target_ship: crate::scene::tests::spec().ship_id,
        module_type_id: 1,
        source_owner_id: 0,
        target_type_id: 0,
    };
    scene.drone_threats.insert(PLAYER, vec![(RAT_SHIP + 1, LATER), (RAT_SHIP + 2, LATER)]);
    assert_eq!(
        scene.opponents_of_with_range(PLAYER, LATER, CONTROL_RANGE),
        [RAT_SHIP + 1, RAT_SHIP],
        "the one that attacked, then the nearest; not the one out of range"
    );
    let _ = attacker;
    // what was long ago is forgotten
    assert_eq!(
        scene.opponents_of_with_range(PLAYER, LATER + 31 * SECOND, CONTROL_RANGE),
        [RAT_SHIP, RAT_SHIP + 1]
    );
}

#[test]
fn an_order_to_engage_with_nothing_locked_and_nobody_there_is_refused_for_the_drone() {
    let (mut scene, _, _) = launched();
    let sent = scene.drone_command(PLAYER, &[DRONE_SHIP], DroneCommand::Engage { target: 0 }, LATER);
    assert_eq!(result_of(&sent), [(DRONE_SHIP, Some("NO_OPPONENT"))]);
    assert!(states(&for_the_player(&sent)).is_empty(), "nothing is told of a drone that took no order");
    // a target that is named and not there is the other refusal
    let named = scene.drone_command(PLAYER, &[DRONE_SHIP], DroneCommand::Engage { target: 12_345 }, LATER);
    assert_eq!(result_of(&named), [(DRONE_SHIP, Some("NO_TARGET"))]);
}

#[test]
fn an_order_to_engage_a_target_beyond_the_control_range_is_refused_for_the_drone() {
    let (mut scene, _, _) = launched();
    spawn_rat_at(&mut scene, RAT_SHIP, 25_000.0);
    let sent = scene.drone_command(PLAYER, &[DRONE_SHIP], DroneCommand::Engage { target: RAT_SHIP }, LATER);
    assert_eq!(result_of(&sent), [(DRONE_SHIP, Some("OUT_OF_RANGE"))]);
    assert!(states(&for_the_player(&sent)).is_empty(), "nothing is told of a drone that took no order");
    // nearer than 20 km it is taken
    let (mut scene, _, _) = launched();
    spawn_rat_at(&mut scene, RAT_SHIP, 15_000.0);
    let sent = scene.drone_command(PLAYER, &[DRONE_SHIP], DroneCommand::Engage { target: RAT_SHIP }, LATER);
    assert_eq!(result_of(&sent), [(DRONE_SHIP, None)]);
}

#[test]
fn a_drone_link_augmentor_range_allows_a_command_beyond_twenty_kilometers() {
    let (mut scene, _, _) = launched();
    spawn_rat_at(&mut scene, RAT_SHIP, 25_000.0);
    let sent = scene.drone_command_with_range(
        PLAYER,
        &[DRONE_SHIP],
        DroneCommand::Engage { target: RAT_SHIP },
        40_000.0,
        LATER,
    );
    assert_eq!(result_of(&sent), [(DRONE_SHIP, None)]);
}

#[test]
fn a_drone_link_augmentor_range_finds_unlocked_targets_beyond_twenty_kilometers() {
    let (mut scene, _, _) = launched();
    spawn_rat_at(&mut scene, RAT_SHIP, 25_000.0);
    let sent = scene.drone_command_with_range(
        PLAYER,
        &[DRONE_SHIP],
        DroneCommand::Engage { target: 0 },
        40_000.0,
        LATER,
    );
    assert_eq!(result_of(&sent), [(DRONE_SHIP, None)]);
    assert_eq!(target_of_the_state(&for_the_player(&sent)), Some(Value::Long(RAT_SHIP)));
}

#[test]
fn several_drones_sent_without_a_target_are_spread_over_those_found_and_the_last_takes_the_rest() {
    let mut scene = scene_with_a_player();
    let drones = [DRONE_SHIP, DRONE_SHIP + 1, DRONE_SHIP + 2];
    for ship in drones {
        let mut spec = hobgoblin();
        spec.ship_id = ship;
        let flight = DroneFlight {
            weapon: Some(laser()),
            graphic_id: 1_099,
            orbit_distance: 700.0,
            attack_range: 2_800.0,
            cruise_fraction: 0.2357,
            work: DroneWorkProfile::default(),
            effect: None,
        };
        scene.launch_drone(PLAYER, spec, flight, LATER).unwrap();
    }
    spawn_rat_at(&mut scene, RAT_SHIP, 2_000.0);
    spawn_rat_at(&mut scene, RAT_SHIP + 1, 4_000.0);
    let sent = scene.drone_command(PLAYER, &drones, DroneCommand::Engage { target: 0 }, LATER);
    assert_eq!(result_of(&sent).iter().map(|(_, reason)| *reason).collect::<Vec<_>>(), [None, None, None]);
    let tasks: Vec<Task> = drones.iter().map(|d| scene.drones[&scene.session_of(*d).unwrap()].task).collect();
    assert_eq!(
        tasks,
        [
            Task::Engage { target: RAT_SHIP },
            Task::Engage { target: RAT_SHIP + 1 },
            Task::Engage { target: RAT_SHIP + 1 }
        ]
    );
}

#[test]
fn an_idle_drone_takes_up_the_fight_when_the_pilot_is_attacked_and_one_that_was_sent_home_does_not() {
    let (mut scene, drone, _) = launched();
    let mut other = hobgoblin();
    other.ship_id = DRONE_SHIP + 1;
    let flight = DroneFlight {
        weapon: Some(laser()),
        graphic_id: 1_099,
        orbit_distance: 700.0,
        attack_range: 2_800.0,
        cruise_fraction: 0.2357,
        work: DroneWorkProfile::default(),
        effect: None,
    };
    let (second, _) = scene.launch_drone(PLAYER, other, flight, LATER).unwrap();
    scene.drone_command(PLAYER, &[DRONE_SHIP + 1], DroneCommand::ReturnHome, LATER);
    spawn_rat_at(&mut scene, RAT_SHIP, 3_000.0);
    scene.aggressions.push(crate::weapons::Aggression {
        attacker_ship: RAT_SHIP,
        target_ship: crate::scene::tests::spec().ship_id,
        module_type_id: 1,
        source_owner_id: 0,
        target_type_id: 0,
    });
    let told = scene.poll(LATER + SECOND);
    // the idle drone is told it goes at the rat (pursuit: the rat is further than the laser's reach and the drone's chase), and the pilot is not asked
    assert_eq!(target_of_the_state(&for_the_player(&told)), Some(Value::Long(RAT_SHIP)), "{told:?}");
    assert_eq!(scene.drones[&drone].task, Task::Engage { target: RAT_SHIP });
    assert_eq!(scene.drones[&second].task, Task::Home, "a drone that was sent home stays");
    // an attack on the pilot by its own drone's shot (told as the pilot's own) is not a threat
    assert_eq!(scene.threats_of(PLAYER, LATER + SECOND), [RAT_SHIP]);
}

#[test]
fn a_drone_sent_at_a_rat_far_off_flies_there_and_fights_it() {
    let (mut scene, drone, _) = launched();
    let mut rat = rat_spec();
    rat.vitals = Vitals {
        shield_capacity: 0.0,
        armor_hp: 0.0,
        structure_hp: 15.0,
        resonances: Resonances { shield: [1.0; 4], armor: [1.0; 4], structure: [1.0; 4] },
        armor_pinned: false,
    };
    let position = scene.ship(PLAYER, LATER).unwrap().position;
    let place = NpcPlace::at_rest(Vec3::new(position.x + 12_000.0, position.y, position.z));
    scene.spawn_npc(rat, rat_identity(0.0), place, LATER);
    let sent = scene.drone_command(PLAYER, &[DRONE_SHIP], DroneCommand::Engage { target: RAT_SHIP }, LATER);
    let mut told = for_the_player(&sent);
    let mut now = LATER + SECOND / 4;
    while now <= LATER + 30 * SECOND {
        told.extend(scene.poll(now).into_iter().filter(|o| o.session() == PLAYER));
        now += SECOND / 4;
    }
    assert_eq!(
        states(&told),
        [PURSUIT as i64, APPROACHING as i64, COMBAT as i64, IDLE as i64],
        "{:?}",
        states(&told)
    );
    let _ = drone;
}

const FRIEND_SHIP: i64 = 980_050_000_099;

/// A stasis webifier of a drone as the host gives it (`remoteWebifierEntity`, SDE: speedFactor -5, maxRange 5000, duration 5000): a web of 5 % that lands each cycle.
fn webber() -> DroneEffect {
    DroneEffect {
        effect_id: 6690,
        fx: "effects.ModifyTargetSpeed".to_string(),
        cycle_ms: 5_000.0,
        lasting_ms: 5_000.0,
        action: Action::Remote {
            what: Remote::Web { percent: -5.0 },
            reach: Reach { optimal: 5_000.0, falloff: 0.0 },
            when: Applies::CycleStart,
            jam: "webify".to_string(),
        },
    }
}

/// A light armor maintenance bot (`npcEntityRemoteArmorRepairer`, SDE: armorDamageAmount 12, maxRange 5000, duration 5000): it mends its target at the end of each cycle.
fn armor_bot() -> DroneEffect {
    DroneEffect {
        effect_id: 6687,
        fx: "effects.RemoteArmorRepairDrone".to_string(),
        cycle_ms: 5_000.0,
        lasting_ms: 5_000.0,
        action: Action::Remote {
            what: Remote::Repair { layer: crate::health::Layer::Armor, amount: 12.0 },
            reach: Reach { optimal: 5_000.0, falloff: 0.0 },
            when: Applies::CycleEnd,
            jam: "remoteArmorRepair".to_string(),
        },
    }
}

/// A drone of the Hobgoblin's ship that has only the given remote effect (no weapon), launched beside the player.
fn launched_with(scene: &mut Scene, effect: DroneEffect) -> SessionId {
    let flight = DroneFlight {
        weapon: None,
        graphic_id: 1_124,
        orbit_distance: 700.0,
        attack_range: 10_000.0,
        cruise_fraction: 0.25,
        work: DroneWorkProfile::default(),
        effect: Some(effect),
    };
    scene.launch_drone(PLAYER, hobgoblin(), flight, LATER).unwrap().0
}

#[test]
fn a_cloaked_ship_launches_no_drone_and_orders_none_to_engage() {
    let mut scene = scene_with_a_player();
    launched_with(&mut scene, armor_bot());
    let drone = scene.drones_of(PLAYER)[0];
    scene.pilots.get_mut(&PLAYER).unwrap().cloak = crate::visibility::CloakState::Cloaked;
    let flight = DroneFlight {
        weapon: None,
        graphic_id: 1_124,
        orbit_distance: 700.0,
        attack_range: 10_000.0,
        cruise_fraction: 0.25,
        work: DroneWorkProfile::default(),
        effect: Some(armor_bot()),
    };
    assert_eq!(scene.launch_drone(PLAYER, hobgoblin(), flight, LATER).err(), Some("CLOAKED"));
    let out = scene.drone_command(PLAYER, &[drone], DroneCommand::Engage { target: 1 }, LATER);
    assert!(
        out.iter().any(
            |o| matches!(o, Out::DroneResults { results, .. } if results.contains(&(drone, Some("CLOAKED"))))
        ),
        "{out:?}"
    );
}

#[test]
fn a_web_drone_slows_the_rat_it_is_sent_at_while_its_cycle_lands_and_lets_go_when_called_home() {
    let mut scene = scene_with_a_player();
    scene.pilots.get_mut(&PLAYER).unwrap().ship.spec.corporation_id = 1_000_006;
    launched_with(&mut scene, webber());
    let rat = spawn_rat_at(&mut scene, RAT_SHIP, 2_000.0);
    scene.drone_command(PLAYER, &[DRONE_SHIP], DroneCommand::Engage { target: RAT_SHIP }, LATER);
    let mut now = LATER + SECOND / 4;
    while now <= LATER + 10 * SECOND {
        scene.poll(now);
        now += SECOND / 4;
    }
    // the rat's top speed is what it is less the web: 5 % off, as one web stacks
    let pilot = &scene.pilots[&rat];
    let base = pilot.incoming.base.expect("the web landed on the rat");
    let worked = pilot.incoming.worked(&base);
    assert!(
        (worked.max_velocity - base.max_velocity * 0.95).abs() < 1e-6,
        "{} of {}",
        worked.max_velocity,
        base.max_velocity
    );

    // called home, the drone lets go of it: the web is lifted at once
    scene.drone_command(PLAYER, &[DRONE_SHIP], DroneCommand::ReturnHome, now);
    assert!(scene.pilots[&rat].incoming.is_empty());
}

#[test]
fn an_armor_bot_mends_a_friendly_ship_only_when_its_cycle_ends() {
    let mut scene = scene_with_a_player();
    scene.pilots.get_mut(&PLAYER).unwrap().ship.spec.corporation_id = 1_000_006;
    let friend = spawn_rat_at(&mut scene, FRIEND_SHIP, 2_000.0);
    scene.pilots.get_mut(&friend).unwrap().ship.spec.corporation_id = 1_000_006;
    // strips the friend's shield and some of its armor
    let (_, applied) = scene.damage(FRIEND_SHIP, [0.0, 600.0, 0.0, 0.0], LATER);
    assert!(applied.is_some());
    let before = scene.pilots[&friend].ship.health.layers_at(LATER).armor;
    launched_with(&mut scene, armor_bot());
    let sent =
        scene.drone_command(PLAYER, &[DRONE_SHIP], DroneCommand::Engage { target: FRIEND_SHIP }, LATER);
    assert_eq!(result_of(&sent), [(DRONE_SHIP, None)]);

    // the cycle is five seconds: nothing is mended while it runs, the points are given as it ends
    let mut now = LATER + SECOND / 4;
    while now < LATER + 4 * SECOND {
        scene.poll(now);
        now += SECOND / 4;
    }
    assert_eq!(scene.pilots[&friend].ship.health.layers_at(now).armor, before);
    while now <= LATER + 7 * SECOND {
        scene.poll(now);
        now += SECOND / 4;
    }
    let after = scene.pilots[&friend].ship.health.layers_at(now).armor;

    assert!((after - before - 12.0).abs() < 1e-3, "{before} -> {after}");
}
#[test]
fn a_drone_that_only_mends_is_refused_a_hostile_target_and_finds_nothing_to_mend_by_itself() {
    let mut scene = scene_with_a_player();
    scene.pilots.get_mut(&PLAYER).unwrap().ship.spec.corporation_id = 1_000_006;
    launched_with(&mut scene, armor_bot());
    // a rat is not on the pilot's side
    spawn_rat_at(&mut scene, RAT_SHIP, 2_000.0);
    let sent = scene.drone_command(PLAYER, &[DRONE_SHIP], DroneCommand::Engage { target: RAT_SHIP }, LATER);
    assert_eq!(result_of(&sent), [(DRONE_SHIP, Some("NO_TARGET"))]);
    let searched = scene.drone_command(PLAYER, &[DRONE_SHIP], DroneCommand::Engage { target: 0 }, LATER);
    assert_eq!(result_of(&searched), [(DRONE_SHIP, Some("NO_OPPONENT"))]);
}

#[test]
fn a_drone_that_is_destroyed_leaves_no_wreck_and_its_controller_is_told_it_is_gone() {
    use crate::wreck_types::WreckCatalog;
    let (mut scene, drone, _) = launched();
    // even a catalog that names a wreck for the drone's type leaves none: a drone is not a ship
    scene.set_wreck_catalog(std::sync::Arc::new(WreckCatalog::of([
        (2_454, "Hobgoblin I", "Frigate", 1, 1_099),
        (26_506, "Caldari Frigate Wreck", "Wreck", 0, 3_117),
    ])));
    let told = scene.destroy(drone, None, LATER + SECOND);
    assert!(scene.wrecks.is_empty(), "no wreck is made");
    let destroyed = told.iter().find_map(|o| match o {
        Out::Destroyed { ship_id, wreck, wreck_kind, .. } if *ship_id == DRONE_SHIP => {
            Some((*wreck, wreck_kind.clone()))
        }
        _ => None,
    });
    assert_eq!(destroyed, Some((None, None)), "{told:?}");

    let player = for_the_player(&told);
    let state = notified(&player, "OnDroneStateChange");
    assert_eq!(state.len(), 1);
    assert_eq!(
        state[0],
        &vec![
            Value::Long(DRONE_SHIP),
            Value::Int(0),
            Value::Int(0),
            Value::Int(0),
            Value::Int(2_454),
            Value::Int(0),
            Value::None,
        ]
    );
    let activity = notified(&player, "OnDroneActivityChange");
    assert_eq!(activity.len(), 1);
    assert_eq!(activity[0], &vec![Value::Long(DRONE_SHIP), Value::None, Value::None]);
    assert!(scene.drones_of(PLAYER).is_empty());
}

#[test]
fn the_drones_of_a_pilot_that_leaves_are_let_go_and_the_host_is_told_of_each() {
    let (mut scene, _, _) = launched();
    let told = scene.leave(PLAYER, Departure::Docked, LATER + SECOND).unwrap();
    assert!(told.iter().any(|o| matches!(o, Out::DroneReleased { drone_id, session, .. } if *drone_id == DRONE_SHIP && *session == PLAYER)));
    let drone = scene.session_of(DRONE_SHIP).expect("the abandoned drone remains in the scene");
    assert_eq!(scene.controller_of_drone(drone), Some(0));
    assert!(scene.drones_of(PLAYER).is_empty());
    assert_eq!(scene.drones.len(), 1);
}

#[test]
fn a_drone_is_released_when_its_controller_ship_is_destroyed() {
    let (mut scene, _, _) = launched();
    let destroyed = scene.destroy(PLAYER, None, LATER + SECOND);
    assert!(destroyed.iter().any(|out| matches!(out, Out::Destroyed { session, .. } if *session == PLAYER)));
    let released = scene.advance_drones(LATER + 2 * SECOND);
    assert!(released.iter().any(|out| matches!(out, Out::DroneReleased { session, drone_id, .. } if *session == PLAYER && *drone_id == DRONE_SHIP)), "{released:?}");
    let drone = scene.session_of(DRONE_SHIP).expect("the released drone remains in the scene");
    assert_eq!(scene.controller_of_drone(drone), Some(0));
    assert!(scene.drones_of(PLAYER).is_empty());
    assert!(scene.advance_drones(LATER + 3 * SECOND).is_empty(), "the release event is emitted once");
}

#[test]
fn a_pilot_cannot_reconnect_a_drone_from_a_different_ship() {
    let (mut scene, _, _) = launched();
    scene.drone_command(PLAYER, &[DRONE_SHIP], DroneCommand::Abandon, LATER);
    let drone_session = scene.session_of(DRONE_SHIP).unwrap();
    let launched_from = scene.pilots.get(&PLAYER).unwrap().ship.spec.ship_id;
    scene.pilots.get_mut(&PLAYER).unwrap().ship.spec.ship_id = launched_from + 1;

    let result = scene.drone_command(PLAYER, &[DRONE_SHIP], DroneCommand::Reconnect, LATER + SECOND);
    assert!(
        result.iter().any(|out| matches!(
            out,
            Out::DroneResults { session, command: 9, results }
                if *session == PLAYER && results == &[(DRONE_SHIP, Some("NOT_CONTROLLED"))]
        )),
        "{result:?}"
    );
    assert_eq!(
        scene
            .pilots
            .get(&drone_session)
            .unwrap()
            .ship
            .npc
            .as_ref()
            .unwrap()
            .drone
            .as_ref()
            .unwrap()
            .controller,
        0
    );
}

#[test]
fn a_pilot_can_reconnect_to_a_drone_they_abandoned_while_it_remains_in_the_scene() {
    let (mut scene, _, _) = launched();
    let released = scene.drone_command(PLAYER, &[DRONE_SHIP], DroneCommand::Abandon, LATER);
    assert!(released.iter().any(|out| matches!(out, Out::DroneReleased { session, drone_id, .. } if *session == PLAYER && *drone_id == DRONE_SHIP)));
    assert!(scene.session_of(DRONE_SHIP).is_some());

    let reconnected = scene.drone_command(PLAYER, &[DRONE_SHIP], DroneCommand::Reconnect, LATER + SECOND);
    assert!(reconnected.iter().any(|out| matches!(out, Out::DroneResults { session, command: 9, results } if *session == PLAYER && results == &[(DRONE_SHIP, None)])), "{reconnected:?}");
    let drone_session = scene.session_of(DRONE_SHIP).unwrap();
    assert_eq!(scene.controller_of_drone(drone_session), Some(PLAYER));
    assert_eq!(scene.drones_of(PLAYER), [DRONE_SHIP]);
}

#[test]
fn another_pilot_can_scoop_only_a_nearby_abandoned_drone_and_gets_destiny_before_the_result() {
    let (mut scene, _, _) = launched();
    let scooper_session = PLAYER + 1;
    let mut scooper = crate::scene::tests::spec();
    scooper.ship_id = DRONE_SHIP + 1;
    scooper.character_id = 22;
    scene.undock(scooper_session, scooper, crate::scene::tests::STATION, LATER).unwrap();
    scene.bind(scooper_session, LATER);

    let controlled = scene.drone_command(scooper_session, &[DRONE_SHIP], DroneCommand::Scoop, LATER);
    assert_eq!(result_of(&controlled), [(DRONE_SHIP, Some("NOT_CONTROLLED"))]);

    let released = scene.drone_command(PLAYER, &[DRONE_SHIP], DroneCommand::Abandon, LATER);
    assert!(released.iter().any(|out| matches!(out, Out::DroneReleased { session, drone_id, .. } if *session == PLAYER && *drone_id == DRONE_SHIP)));

    let owner_position = scene.pilots[&PLAYER].ship.position;
    scene.pilots.get_mut(&scooper_session).unwrap().ship.position.x = owner_position.x + 5_000.0;
    let too_far = scene.drone_command(scooper_session, &[DRONE_SHIP], DroneCommand::Scoop, LATER + SECOND);
    assert_eq!(result_of(&too_far), [(DRONE_SHIP, Some("OUT_OF_RANGE"))]);
    assert!(scene.session_of(DRONE_SHIP).is_some(), "a refusal leaves the abandoned ball in space");

    scene.pilots.get_mut(&scooper_session).unwrap().ship.position = owner_position;
    let scooped =
        scene.drone_command(scooper_session, &[DRONE_SHIP], DroneCommand::Scoop, LATER + 2 * SECOND);
    assert_eq!(result_of(&scooped), [(DRONE_SHIP, None)]);
    assert!(scooped.iter().any(|out| matches!(out, Out::DroneScooped { session, drone_id } if *session == scooper_session && *drone_id == DRONE_SHIP)));
    assert_eq!(
        actions_named(
            &scooped.iter().filter(|out| out.session() == scooper_session).cloned().collect::<Vec<_>>(),
            "OnDroneStateChange"
        )
        .len(),
        1
    );
    let destiny = scooped.iter().position(|out| matches!(out, Out::Notify { session, name: "DoDestinyUpdate", .. } if *session == scooper_session)).unwrap();
    let result = scooped.iter().position(|out| matches!(out, Out::DroneResults { session, command: 10, results } if *session == scooper_session && results == &[(DRONE_SHIP, None)])).unwrap();
    assert!(destiny < result, "the scene removal is sent before the command reply");
    assert!(scene.session_of(DRONE_SHIP).is_none());

    let retry = scene.drone_command(scooper_session, &[DRONE_SHIP], DroneCommand::Scoop, LATER + 3 * SECOND);
    assert_eq!(result_of(&retry), [(DRONE_SHIP, Some("NOT_CONTROLLED"))]);
}

#[test]
fn a_drone_is_released_when_its_controller_session_returns_in_a_different_ship() {
    let (mut scene, _, _) = launched();
    let old_ship_id = scene.pilots[&PLAYER].ship.spec.ship_id;
    scene.pilots.get_mut(&PLAYER).unwrap().ship.spec.ship_id = old_ship_id + 1;
    let released = scene.advance_drones(LATER + SECOND);
    assert!(released.iter().any(|out| matches!(out, Out::DroneReleased { session, drone_id, .. } if *session == PLAYER && *drone_id == DRONE_SHIP)), "{released:?}");
    let drone = scene.session_of(DRONE_SHIP).expect("the released drone remains in the scene");
    assert_eq!(scene.controller_of_drone(drone), Some(0));
    assert!(scene.drones_of(PLAYER).is_empty());
}

#[test]
fn assist_and_guard_follow_the_designated_pilots_combat_target() {
    let (mut scene, drone, _) = launched();
    let character_id = scene.pilots[&PLAYER].ship.spec.character_id;
    let pilot_ship = scene.pilots[&PLAYER].ship.spec.ship_id;
    spawn_rat_at(&mut scene, RAT_SHIP, 1_500.0);

    let assist = scene.drone_command(PLAYER, &[DRONE_SHIP], DroneCommand::Assist { character_id }, LATER);
    assert_eq!(
        assist.iter().find_map(|out| match out {
            Out::DroneResults { command, results, .. } => Some((*command, results.clone())),
            _ => None,
        }),
        Some((5, vec![(DRONE_SHIP, None)]))
    );
    assert_eq!(target_of_the_state(&assist), Some(Value::Long(pilot_ship)));
    assert_eq!(
        scene.drones[&drone].follow,
        Some(DroneFollow::Assist { character_id, control_range: CONTROL_RANGE })
    );

    scene.aggressions.push(crate::weapons::Aggression {
        attacker_ship: pilot_ship,
        target_ship: RAT_SHIP,
        module_type_id: 0,
        source_owner_id: character_id,
        target_type_id: 0,
    });
    let assisted = for_the_player(&scene.note_drone_threats(LATER + SECOND));
    assert!(states(&assisted).contains(&(COMBAT as i64)));
    assert_eq!(scene.drones[&drone].task, Task::Engage { target: RAT_SHIP });

    let guard =
        scene.drone_command(PLAYER, &[DRONE_SHIP], DroneCommand::Guard { character_id }, LATER + SECOND);
    assert_eq!(
        guard.iter().find_map(|out| match out {
            Out::DroneResults { command, results, .. } => Some((*command, results.clone())),
            _ => None,
        }),
        Some((6, vec![(DRONE_SHIP, None)]))
    );
    assert_eq!(target_of_the_state(&guard), Some(Value::Long(pilot_ship)));
    assert_eq!(
        scene.drones[&drone].follow,
        Some(DroneFollow::Guard { character_id, control_range: CONTROL_RANGE })
    );

    scene.aggressions.push(crate::weapons::Aggression {
        attacker_ship: RAT_SHIP,
        target_ship: pilot_ship,
        module_type_id: 0,
        source_owner_id: 0,
        target_type_id: 0,
    });
    let guarded = for_the_player(&scene.note_drone_threats(LATER + 2 * SECOND));
    assert!(states(&guarded).contains(&(COMBAT as i64)));
    assert_eq!(scene.drones[&drone].task, Task::Engage { target: RAT_SHIP });
}

#[test]
fn mining_and_salvage_orders_are_visible_on_the_drone_state() {
    let (mut scene, drone, _) = work_drone();
    let mine_target = 90_001;
    let pilot_position = scene.ship(PLAYER, LATER).unwrap().position;
    scene.place_asteroid(ore_asteroid(mine_target, pilot_position, 1_000), LATER);
    let mined = scene.drone_command(
        PLAYER,
        &[DRONE_SHIP],
        DroneCommand::MineRepeatedly { target: mine_target },
        LATER,
    );
    assert_eq!(scene.drones[&drone].task, Task::MineRepeatedly { target: mine_target });
    assert_eq!(target_of_the_state(&mined), Some(Value::Long(mine_target)));
    assert!(mined.iter().any(
        |out| matches!(out, Out::DroneResults { command: 7, results, .. } if results == &[(DRONE_SHIP, None)])
    ));
    let character_id = scene.pilots[&PLAYER].ship.spec.character_id;
    let corporation_id = i64::from(scene.pilots[&PLAYER].ship.spec.corporation_id);
    let wreck_id = 90_002;
    scene.place_wreck(
        crate::wreck::Wreck {
            id: wreck_id,
            group_id: crate::wreck::WRECK_GROUP,
            kind: crate::wreck::WreckKind { type_id: 1, name: "Wreck".into(), graphic_id: None },
            position: pilot_position,
            direction: Vec3::new(1.0, 0.0, 0.0),
            radius: 20.0,
            owner_id: character_id,
            corporation_id,
            alliance_id: 0,
            war_faction_id: 0,
            rights: crate::wreck::LootRights { character_id, corporation_id },
            launcher_id: 0,
            is_empty: false,
            rights_known: true,
            pull: None,
            velocity: eve_destiny::Vec3::ZERO,
            expires_at: LATER + 60 * SECOND,
        },
        LATER,
    );
    let salvaged =
        scene.drone_command(PLAYER, &[DRONE_SHIP], DroneCommand::Salvage { target: None }, LATER + SECOND);
    assert_eq!(scene.drones[&drone].task, Task::Salvage { target: Some(wreck_id) });
    assert_eq!(target_of_the_state(&salvaged), Some(Value::Long(wreck_id)));
    assert!(salvaged.iter().any(
        |out| matches!(out, Out::DroneResults { command: 8, results, .. } if results == &[(DRONE_SHIP, None)])
    ));
}

#[test]
fn a_mining_drone_emits_one_host_cycle_after_its_timed_pass_and_return() {
    let (mut scene, drone, _) = work_drone();
    scene.drones.get_mut(&drone).expect("launched drone").work.cycle_ms = 1.0;
    let target = 90_003;
    let drone_position = scene.ship(drone, LATER).unwrap().position;
    scene.place_asteroid(ore_asteroid(target, drone_position, 100), LATER);
    let order = scene.drone_command(PLAYER, &[DRONE_SHIP], DroneCommand::MineRepeatedly { target }, LATER);
    assert!(order.iter().any(
        |out| matches!(out, Out::DroneResults { command: 7, results, .. } if results == &[(DRONE_SHIP, None)])
    ));

    let _ = scene.advance_drones(LATER);
    assert_eq!(scene.drones[&drone].work_phase, DroneWorkPhase::Cycling);
    let ready_at = scene.drones[&drone].work_ready_at;
    let _ = scene.advance_drones(ready_at);
    assert_eq!(scene.drones[&drone].work_phase, DroneWorkPhase::Returning);
    let completed = scene.advance_drones(ready_at + SECOND);
    let cycles: Vec<_> = completed
        .iter()
        .filter_map(|out| match out {
            Out::DroneWorkCycle { command, target_id, cycle_id, .. } => {
                Some((*command, *target_id, *cycle_id))
            }
            _ => None,
        })
        .collect();
    // the id is drawn from the process-wide counter (modules.rs `next_work_cycle_id`), so only its command and target are fixed
    assert_eq!(
        cycles.iter().map(|(command, target, _)| (*command, *target)).collect::<Vec<_>>(),
        [(7, target)]
    );
    assert_ne!(cycles[0].2, 0);
    assert!(scene.drones[&drone].awaiting_work);
    assert!(
        scene
            .advance_drones(ready_at + 2 * SECOND)
            .iter()
            .all(|out| !matches!(out, Out::DroneWorkCycle { .. }))
    );
}

#[test]
fn mining_results_mirror_stock_monotonically_then_remove_depleted_rocks() {
    let (mut scene, drone, _) = work_drone();
    let target = 90_004;
    let pilot_position = scene.ship(PLAYER, LATER).unwrap().position;
    scene.place_asteroid(ore_asteroid(target, pilot_position, 100), LATER);
    let order = scene.drone_command(PLAYER, &[DRONE_SHIP], DroneCommand::MineRepeatedly { target }, LATER);
    assert!(
        order
            .iter()
            .any(|out| matches!(out, Out::DroneResults { results, .. } if results == &[(DRONE_SHIP, None)]))
    );
    {
        let ai = scene.drones.get_mut(&drone).expect("launched drone");
        ai.awaiting_work = true;
        ai.work_cycle_id = 1;
    }

    assert!(
        scene
            .drone_work_result(
                u64::MAX,
                DRONE_SHIP,
                DroneWorkAnswer {
                    target_id: target,
                    cycle_id: 1,
                    accepted: true,
                    units_remaining: 40,
                    complete: false
                },
                LATER + SECOND
            )
            .is_empty()
    );
    assert!(
        scene
            .drone_work_result(
                PLAYER,
                DRONE_SHIP,
                DroneWorkAnswer {
                    target_id: target + 1,
                    cycle_id: 1,
                    accepted: true,
                    units_remaining: 40,
                    complete: false
                },
                LATER + SECOND
            )
            .is_empty()
    );
    assert_eq!(scene.asteroids[&target].units_remaining, 100);
    assert!(scene.drones[&drone].awaiting_work);

    let _ = scene.drone_work_result(
        PLAYER,
        DRONE_SHIP,
        DroneWorkAnswer {
            target_id: target,
            cycle_id: 1,
            accepted: true,
            units_remaining: 40,
            complete: false,
        },
        LATER + SECOND,
    );
    assert_eq!(scene.asteroids[&target].units_remaining, 40);
    assert_eq!(scene.drones[&drone].task, Task::MineRepeatedly { target });
    assert!(
        scene
            .drone_work_result(
                PLAYER,
                DRONE_SHIP,
                DroneWorkAnswer {
                    target_id: target,
                    cycle_id: 1,
                    accepted: true,
                    units_remaining: 20,
                    complete: false
                },
                LATER + SECOND
            )
            .is_empty()
    );
    assert_eq!(scene.asteroids[&target].units_remaining, 40);

    {
        let ai = scene.drones.get_mut(&drone).expect("launched drone");
        ai.awaiting_work = true;
        ai.work_cycle_id = 2;
    }
    let _ = scene.drone_work_result(
        PLAYER,
        DRONE_SHIP,
        DroneWorkAnswer {
            target_id: target,
            cycle_id: 2,
            accepted: true,
            units_remaining: 60,
            complete: false,
        },
        LATER + 2 * SECOND,
    );
    assert_eq!(scene.asteroids[&target].units_remaining, 40);

    {
        let ai = scene.drones.get_mut(&drone).expect("launched drone");
        ai.awaiting_work = true;
        ai.work_cycle_id = 3;
    }
    let _ = scene.drone_work_result(
        PLAYER,
        DRONE_SHIP,
        DroneWorkAnswer {
            target_id: target,
            cycle_id: 3,
            accepted: true,
            units_remaining: 0,
            complete: true,
        },
        LATER + 3 * SECOND,
    );
    assert!(!scene.asteroids.contains_key(&target));
    assert_eq!(scene.drones[&drone].task, Task::Idle);
}

#[test]
fn auto_salvage_rejects_foreign_wrecks_but_manual_salvage_repeats_until_complete_even_if_empty() {
    let (mut scene, drone, _) = work_drone();
    let character_id = scene.pilots[&PLAYER].ship.spec.character_id;
    let corporation_id = i64::from(scene.pilots[&PLAYER].ship.spec.corporation_id);
    let target = 90_005;
    let pilot_position = scene.ship(PLAYER, LATER).unwrap().position;
    scene.place_wreck(
        crate::wreck::Wreck {
            id: target,
            group_id: crate::wreck::WRECK_GROUP,
            kind: crate::wreck::WreckKind { type_id: 1, name: "Foreign wreck".into(), graphic_id: None },
            position: pilot_position,
            direction: Vec3::new(1.0, 0.0, 0.0),
            radius: 20.0,
            owner_id: character_id + 1,
            corporation_id: corporation_id + 1,
            alliance_id: 0,
            war_faction_id: 0,
            rights: crate::wreck::LootRights {
                character_id: character_id + 1,
                corporation_id: corporation_id + 1,
            },
            launcher_id: 0,
            is_empty: true,
            rights_known: true,
            pull: None,
            velocity: eve_destiny::Vec3::ZERO,
            expires_at: LATER + 60 * SECOND,
        },
        LATER,
    );

    let automatic = scene.drone_command(PLAYER, &[DRONE_SHIP], DroneCommand::Salvage { target: None }, LATER);
    assert!(automatic.iter().any(|out| matches!(out, Out::DroneResults { command: 8, results, .. } if results == &[(DRONE_SHIP, Some("NO_WRECK"))])));
    assert_eq!(scene.drones[&drone].task, Task::Idle);

    let manual = scene.drone_command(
        PLAYER,
        &[DRONE_SHIP],
        DroneCommand::Salvage { target: Some(target) },
        LATER + SECOND,
    );
    assert!(manual.iter().any(
        |out| matches!(out, Out::DroneResults { command: 8, results, .. } if results == &[(DRONE_SHIP, None)])
    ));
    assert_eq!(scene.drones[&drone].task, Task::Salvage { target: Some(target) });
    {
        let ai = scene.drones.get_mut(&drone).expect("launched drone");
        ai.awaiting_work = true;
        ai.work_cycle_id = 1;
    }

    let _ = scene.drone_work_result(
        PLAYER,
        DRONE_SHIP,
        DroneWorkAnswer {
            target_id: target,
            cycle_id: 1,
            accepted: true,
            units_remaining: 1,
            complete: false,
        },
        LATER + 2 * SECOND,
    );
    assert!(scene.wreck(target).is_some_and(|wreck| wreck.is_empty));
    assert_eq!(scene.drones[&drone].task, Task::Salvage { target: Some(target) });

    {
        let ai = scene.drones.get_mut(&drone).expect("launched drone");
        ai.awaiting_work = true;
        ai.work_cycle_id = 2;
    }
    let _ = scene.drone_work_result(
        PLAYER,
        DRONE_SHIP,
        DroneWorkAnswer {
            target_id: target,
            cycle_id: 2,
            accepted: true,
            units_remaining: 0,
            complete: true,
        },
        LATER + 3 * SECOND,
    );
    assert!(scene.wreck(target).is_none());
    assert_eq!(scene.drones[&drone].task, Task::Idle);
}

#[test]
fn mine_repeatedly_rejects_depleted_rocks_and_cancels_when_control_range_is_lost() {
    let (mut scene, drone, _) = work_drone();
    let pilot_position = scene.ship(PLAYER, LATER).unwrap().position;
    let depleted = 90_006;
    scene.place_asteroid(ore_asteroid(depleted, pilot_position, 0), LATER);
    let refused =
        scene.drone_command(PLAYER, &[DRONE_SHIP], DroneCommand::MineRepeatedly { target: depleted }, LATER);
    assert!(refused.iter().any(|out| matches!(out, Out::DroneResults { command: 7, results, .. } if results == &[(DRONE_SHIP, Some("NO_TARGET"))])));
    assert_eq!(scene.drones[&drone].task, Task::Idle);

    let target = 90_007;
    let drone_position = scene.ship(drone, LATER + SECOND).unwrap().position;
    scene.place_asteroid(ore_asteroid(target, drone_position, 100), LATER + SECOND);
    let accepted =
        scene.drone_command(PLAYER, &[DRONE_SHIP], DroneCommand::MineRepeatedly { target }, LATER + SECOND);
    assert!(accepted.iter().any(
        |out| matches!(out, Out::DroneResults { command: 7, results, .. } if results == &[(DRONE_SHIP, None)])
    ));
    let controller_distance = scene.distance_to_asteroid(PLAYER, target, LATER + SECOND).unwrap();
    assert!(controller_distance > 0.0);
    {
        let ai = scene.drones.get_mut(&drone).expect("launched drone");
        ai.work_phase = DroneWorkPhase::Cycling;
        ai.work_ready_at = LATER + 2 * SECOND;
        ai.work_control_range = controller_distance / 2.0;
    }

    let cancelled = scene.advance_drone_work(drone, target, 7, LATER + 2 * SECOND);
    assert!(cancelled.iter().all(|out| !matches!(out, Out::DroneWorkCycle { .. })));
    assert_eq!(scene.drones[&drone].task, Task::Idle);
}

#[test]
fn a_drone_sent_at_a_structure_in_its_reach_fires_at_its_layers_and_the_host_is_told_when_the_shield_is_gone()
{
    const STRUCTURE_ID: i64 = 98_100_020;
    let (mut scene, drone, _) = launched();
    let here = scene.pilots[&drone].ship.position;
    let mut placement = crate::scene::tests::structure_placement(&scene, LATER);
    placement.structure_id = STRUCTURE_ID;
    placement.position = [here.x + 60_000.0 + 800.0, here.y, here.z];
    placement.vitals = crate::health::Vitals {
        shield_capacity: 1.0,
        armor_hp: 500.0,
        structure_hp: 300.0,
        ..Default::default()
    };
    placement.limits = crate::health::StructureLimits { per_second: [1e9; 3], repair_pause: [0.0; 3] };
    scene.place_structure(placement, LATER);

    let mut told =
        scene.drone_command(PLAYER, &[DRONE_SHIP], DroneCommand::Engage { target: STRUCTURE_ID }, LATER);
    let results = told.iter().find_map(|out| match out {
        Out::DroneResults { results, .. } => Some(results.clone()),
        _ => None,
    });
    assert_eq!(results, Some(vec![(DRONE_SHIP, None)]), "the drone takes the structure as a target");
    let mut now = LATER;
    for _ in 0..80 {
        now += SECOND / 4;
        told.extend(scene.poll(now));
        if told.iter().any(|o| matches!(o, Out::StructureDamaged { .. })) {
            break;
        }
    }
    assert!(
        told.iter().any(|o| matches!(
            o,
            Out::StructureDamaged { structure_id, depleted: Some(crate::health::Layer::Shield), .. } if *structure_id == STRUCTURE_ID
        )),
        "{told:?}"
    );
}
