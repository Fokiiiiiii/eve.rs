use eve_destiny::Kind;

use super::*;
use crate::brains::rat_tests::{
    LATER, PLAYER, RAT_SHIP, SECOND, actions_named, notified, place_near, rat_identity, rat_spec, run,
    scene_with_a_player,
};
use crate::missiles::Blast;
use crate::modules::{Action, Effect};

const SQUADRON_SHIP: i64 = 9_988_400_004_100;

/// A light fighter of a squadron of six (Templar I's class and graphic), as a ship of its own.
fn squadron() -> ShipSpec {
    let mut squadron = crate::scene::tests::spec();
    squadron.ship_id = SQUADRON_SHIP;
    squadron.character_id = 0;
    squadron.type_id = 23_055;
    squadron.group_id = 1_652;
    squadron.category_id = 87;
    squadron.name = "Templar I".to_string();
    squadron.radius = 6.0;
    squadron.mass = 1_000.0;
    squadron.max_velocity = 800.0;
    squadron.agility = 100.0;
    squadron.modules = Vec::new();
    squadron
}

/// The pilot's squadron launched beside its ship at a cruise of half its top speed.
fn launched() -> (Scene, SessionId, Vec<Out>) {
    let mut scene = scene_with_a_player();
    let (squadron, told) = scene.launch_fighter(PLAYER, squadron(), 1_200, 0.5, 6, LATER).unwrap();
    (scene, squadron, told)
}

fn results_of(told: &[Out]) -> Vec<(i64, Option<&'static str>)> {
    told.iter()
        .find_map(|out| match out {
            Out::FighterResults { results, .. } => Some(results.clone()),
            _ => None,
        })
        .unwrap_or_default()
}

#[test]
fn a_squadron_launched_is_a_fighter_of_the_pilot_that_circles_its_ship_and_is_no_drone() {
    let (scene, squadron, told) = launched();
    let told: Vec<Out> = told.into_iter().filter(|out| out.session() == PLAYER).collect();
    assert!(scene.is_fighter(squadron));
    assert_eq!(scene.controller_of_drone(squadron), Some(PLAYER));
    assert_eq!(scene.pilots[&squadron].ship.ball().kind, Kind::Fighter);
    // the pilot hears of its squadron as it does of a ship that came into space, and it is circling its ship
    assert!(notified(&told, "OnDroneStateChange").is_empty());
    assert!(!actions_named(&told, "Orbit").is_empty());
}

#[test]
fn a_drone_order_is_refused_for_a_squadron_as_not_under_control() {
    let (mut scene, squadron, _) = launched();
    let told = scene.drone_command(
        PLAYER,
        &[SQUADRON_SHIP],
        crate::drones::DroneCommand::ReturnHome,
        LATER + SECOND,
    );
    let refused = told.iter().any(|out| {
        matches!(out, Out::DroneResults { results, .. } if results.as_slice() == [(SQUADRON_SHIP, Some(NOT_CONTROLLED))])
    });
    assert!(refused, "{told:?}");
    assert!(scene.is_fighter(squadron));
}

#[test]
fn an_orbit_order_circles_the_ship_it_names_and_a_follow_order_follows_it() {
    let (mut scene, squadron, _) = launched();
    let pilot_ship = scene.pilots[&PLAYER].ship.spec.ship_id;
    let told = scene.fighter_order(
        PLAYER,
        &[SQUADRON_SHIP],
        FighterOrder::Orbit { target: pilot_ship, distance: 800.0 },
        LATER + SECOND,
    );
    assert_eq!(results_of(&told), [(SQUADRON_SHIP, None)]);
    assert!(matches!(
        scene.pilots[&squadron].ship.course,
        Course::Orbit { target, distance } if target == pilot_ship && distance == 800.0
    ));
    let told = scene.fighter_order(
        PLAYER,
        &[SQUADRON_SHIP],
        FighterOrder::Follow { target: pilot_ship, range: 300.0 },
        LATER + 2 * SECOND,
    );
    assert_eq!(results_of(&told), [(SQUADRON_SHIP, None)]);
    assert!(matches!(
        scene.pilots[&squadron].ship.course,
        Course::Follow { target, range } if target == pilot_ship && range == 300.0
    ));
    // a target that is not in the scene is refused and the squadron keeps its course
    let told = scene.fighter_order(
        PLAYER,
        &[SQUADRON_SHIP],
        FighterOrder::Orbit { target: 42, distance: 800.0 },
        LATER + 3 * SECOND,
    );
    assert_eq!(results_of(&told), [(SQUADRON_SHIP, Some(NO_TARGET))]);
    assert!(matches!(scene.pilots[&squadron].ship.course, Course::Follow { .. }));
}

#[test]
fn a_recalled_squadron_lands_when_it_is_within_reach_of_its_ship_and_the_host_is_told() {
    let (mut scene, _squadron, _) = launched();
    let order = scene.fighter_order(PLAYER, &[SQUADRON_SHIP], FighterOrder::Recall, LATER + SECOND);
    assert_eq!(results_of(&order), [(SQUADRON_SHIP, None)]);
    let told = run(&mut scene, LATER + 2 * SECOND, 20);
    assert!(
        told.iter().any(|out| matches!(out, Out::FighterLanded { session, fighter_id } if *session == PLAYER && *fighter_id == SQUADRON_SHIP)),
        "{told:?}"
    );
    assert!(scene.session_of(SQUADRON_SHIP).is_none(), "a squadron that landed is out of the scene");
    assert!(
        !told.iter().any(|out| matches!(out, Out::DroneScooped { .. })),
        "a squadron is not scooped as a drone"
    );
}

#[test]
fn a_squadron_sent_to_a_point_flies_there_and_is_told_the_point_as_the_reference_does() {
    let (mut scene, squadron, _) = launched();
    let start = scene.pilots[&squadron].ship.position;
    let point = [start.x + 2_000.0, start.y, start.z];
    let order =
        scene.fighter_order(PLAYER, &[SQUADRON_SHIP], FighterOrder::GotoPoint { point }, LATER + SECOND);
    assert_eq!(results_of(&order), [(SQUADRON_SHIP, None)]);
    assert!(matches!(scene.pilots[&squadron].ship.course, Course::GotoPoint { .. }));
    let told: Vec<Out> = order.into_iter().filter(|out| out.session() == PLAYER).collect();
    assert!(!actions_named(&told, "GotoPoint").is_empty(), "{told:?}");
    let told = run(&mut scene, LATER + 2 * SECOND, 200);
    let at = scene.pilots[&squadron].ship.position;
    let apart = ((at.x - point[0]).powi(2) + (at.y - point[1]).powi(2) + (at.z - point[2]).powi(2)).sqrt();
    assert!(apart < 5.0, "{apart} m from the point, {told:?}");
    // a squadron sent to a point is not recalled or launched by it
    assert!(scene.is_fighter(squadron));
}

#[test]
fn a_squadron_is_told_its_size_as_its_members_are_lost_and_is_destroyed_with_the_last() {
    let (mut scene, squadron, _) = launched();
    let (_, whole) = scene.pilots[&squadron].ship.health.points_left(LATER);
    let member = whole / 6.0;
    let mut last = 6;
    let mut sizes = Vec::new();
    let mut destroyed = None;
    for step in 1..=400 {
        let now = LATER + step * SECOND;
        let (told, hit) = scene.damage(SQUADRON_SHIP, [member * 0.5, 0.0, 0.0, 0.0], now);
        sizes.extend(told.iter().filter_map(|out| match out {
            Out::FighterSize { fighter_id, size, .. } if *fighter_id == SQUADRON_SHIP => Some(*size),
            _ => None,
        }));
        if hit.is_some_and(|hit| hit.destroyed) {
            destroyed = Some(now);
            break;
        }
        // the squadron has as many members as it has points for, and never more than it had (a shield recharges, a member does not return)
        let (left, _) = scene.pilots[&squadron].ship.health.points_left(now);
        last = last.min(((left / member) - 1e-6).ceil() as i32);
        assert_eq!(
            scene.pilots[&squadron]
                .ship
                .npc
                .as_ref()
                .and_then(|npc| npc.drone.as_ref())
                .and_then(|link| link.squadron)
                .map(|s| s.told_size),
            Some(last),
            "at step {step}"
        );
    }
    assert!(destroyed.is_some(), "the last member was not lost");
    assert!(!sizes.is_empty(), "no size was told");
    assert!(
        sizes.windows(2).all(|pair| pair[0] > pair[1]),
        "a size is told once each time it falls: {sizes:?}"
    );
    assert!(sizes.iter().all(|size| (1..=5).contains(size)), "{sizes:?}");
    // and the destruction is told to the host, with no drone notices to the pilot
    let told = scene.destroy(squadron, None, destroyed.unwrap_or(LATER) + SECOND);
    assert!(
        told.iter().any(|out| matches!(out, Out::Destroyed { ship_id, .. } if *ship_id == SQUADRON_SHIP)),
        "{told:?}"
    );
    assert!(notified(&told, "OnDroneStateChange").is_empty(), "a squadron's destruction is no drone's");
}

#[test]
fn a_squadron_runs_an_ability_through_the_simulation_as_a_pilot_does() {
    let (mut scene, squadron, _) = launched();
    // a self buff of the squadron's own type (no capacitor: a squadron has none), started as the host sends any module effect
    let effect = crate::modules::Effect {
        module_id: SQUADRON_SHIP * 8 + 1,
        module_type: 23_055,
        effect_id: 6_441,
        fx: "effects.MicroWarpDrive".to_string(),
        duration_ms: 20_000.0,
        capacitor_need: 0.0,
        repeat: 1,
        target_id: 0,
        turret: None,
        action: None,
        on: None,
        off: None,
        on_changes: Vec::new(),
        off_changes: Vec::new(),
        burns_fuel: false,
    };
    let told = scene.activate(squadron, effect.clone(), LATER + SECOND);
    assert!(told.iter().any(|out| matches!(out, Out::Accepted { command: 17, .. })), "{told:?}");
    // the same ability is not started again while it runs
    let again = scene.activate(squadron, effect, LATER + 2 * SECOND);
    assert!(
        again.iter().any(
            |out| matches!(out, Out::Refused { command: 17, reason, .. } if *reason == "ALREADY_ACTIVE")
        ),
        "{again:?}"
    );
}

#[test]
fn a_squadron_let_go_is_scooped_back_from_within_reach_and_only_from_its_own_ship() {
    let (mut scene, squadron, _) = launched();
    // one with a controller is not let go
    let refused = scene.fighter_order(PLAYER, &[SQUADRON_SHIP], FighterOrder::Scoop, LATER + SECOND);
    assert_eq!(results_of(&refused), [(SQUADRON_SHIP, Some(NOT_ABANDONED))]);
    assert!(scene.is_fighter(squadron));
    let _ = scene.fighter_order(PLAYER, &[SQUADRON_SHIP], FighterOrder::Abandon, LATER + 2 * SECOND);
    // a squadron launched from another ship is not this pilot's to take back
    let link =
        scene.pilots.get_mut(&squadron).and_then(|pilot| pilot.ship.npc.as_mut()?.drone.as_mut()).unwrap();
    link.launched_from_ship_id = 42;
    let other = scene.fighter_order(PLAYER, &[SQUADRON_SHIP], FighterOrder::Scoop, LATER + 3 * SECOND);
    assert_eq!(results_of(&other), [(SQUADRON_SHIP, Some(NOT_YOURS))]);
    let pilot_ship = scene.pilots[&PLAYER].ship.spec.ship_id;
    scene
        .pilots
        .get_mut(&squadron)
        .and_then(|pilot| pilot.ship.npc.as_mut()?.drone.as_mut())
        .unwrap()
        .launched_from_ship_id = pilot_ship;
    let taken = scene.fighter_order(PLAYER, &[SQUADRON_SHIP], FighterOrder::Scoop, LATER + 4 * SECOND);
    assert_eq!(results_of(&taken), [(SQUADRON_SHIP, None)]);
    assert!(scene.session_of(SQUADRON_SHIP).is_none(), "a squadron scooped is out of the scene");
    assert!(taken.iter().any(|out| matches!(out, Out::FighterLanded { session, .. } if *session == PLAYER)));
}

#[test]
fn a_squadron_let_go_too_far_from_its_ship_is_not_scooped() {
    let (mut scene, squadron, _) = launched();
    let start = scene.pilots[&squadron].ship.position;
    let far = [start.x + 20_000.0, start.y, start.z];
    let _ =
        scene.fighter_order(PLAYER, &[SQUADRON_SHIP], FighterOrder::GotoPoint { point: far }, LATER + SECOND);
    let _ = run(&mut scene, LATER + 2 * SECOND, 400);
    let _ = scene.fighter_order(PLAYER, &[SQUADRON_SHIP], FighterOrder::Abandon, LATER + 500 * SECOND);
    let refused = scene.fighter_order(PLAYER, &[SQUADRON_SHIP], FighterOrder::Scoop, LATER + 501 * SECOND);
    assert_eq!(
        results_of(&refused),
        [(SQUADRON_SHIP, Some(TOO_FAR))],
        "{refused:?} at {:?}",
        scene.pilots[&squadron].ship.position
    );
    assert!(scene.is_fighter(squadron), "it stays in space");
}

#[test]
fn a_squadron_within_reach_of_a_pilot_who_logs_off_lands_and_one_beyond_it_is_let_go() {
    let (mut scene, _squadron, _) = launched();
    let told = scene.leave(PLAYER, crate::scene::Departure::LoggedOff, LATER + SECOND).unwrap();
    assert!(
        told.iter()
            .any(|out| matches!(out, Out::FighterLanded { fighter_id, .. } if *fighter_id == SQUADRON_SHIP)),
        "{told:?}"
    );
    assert!(scene.session_of(SQUADRON_SHIP).is_none(), "a squadron that landed is out of the scene");

    // beyond the recovery distance it is let go instead, and stays in space
    let (mut scene, squadron, _) = launched();
    let start = scene.pilots[&squadron].ship.position;
    let far = [start.x + 20_000.0, start.y, start.z];
    let _ =
        scene.fighter_order(PLAYER, &[SQUADRON_SHIP], FighterOrder::GotoPoint { point: far }, LATER + SECOND);
    let _ = run(&mut scene, LATER + 2 * SECOND, 400);
    let told = scene.leave(PLAYER, crate::scene::Departure::LoggedOff, LATER + 500 * SECOND).unwrap();
    assert!(
        told.iter().any(
            |out| matches!(out, Out::FighterReleased { fighter_id, .. } if *fighter_id == SQUADRON_SHIP)
        ),
        "{told:?}"
    );
    assert!(scene.session_of(SQUADRON_SHIP).is_some(), "it stays in space");
}

#[test]
fn an_abandoned_squadron_stays_in_space_under_nobody_and_the_host_is_told_where_it_is() {
    let (mut scene, squadron, _) = launched();
    let told = scene.fighter_order(PLAYER, &[SQUADRON_SHIP], FighterOrder::Abandon, LATER + SECOND);
    assert!(
        told.iter().any(|out| matches!(out, Out::FighterReleased { session, fighter_id, .. } if *session == PLAYER && *fighter_id == SQUADRON_SHIP)),
        "{told:?}"
    );
    assert_ne!(scene.controller_of_drone(squadron), Some(PLAYER));
    assert!(scene.session_of(SQUADRON_SHIP).is_some(), "it is still in the scene");
    let refused = scene.fighter_order(PLAYER, &[SQUADRON_SHIP], FighterOrder::Stop, LATER + 2 * SECOND);
    assert_eq!(results_of(&refused), [(SQUADRON_SHIP, Some(NOT_CONTROLLED))]);
}

/// Every answer of the simulation from `from` for `seconds`, as the poll gives them (the squadron's own too).
fn poll_all(scene: &mut Scene, from: i64, seconds: i64) -> Vec<Out> {
    let mut told = Vec::new();
    let mut now = from;
    while now <= from + seconds * SECOND {
        told.extend(scene.poll(now));
        now += SECOND / 4;
    }
    told
}

/// A rat `metres` off the pilot's ship (the target the squadron is sent at).
fn rat_at(scene: &mut Scene, metres: f64) {
    let mut rat = rat_spec();
    rat.ship_id = RAT_SHIP;
    let place = place_near(scene, metres);
    scene.spawn_npc(rat, rat_identity(0.0), place, LATER);
}

/// A squadron's kamikaze ability, locked on `target` (the effect the host builds from the type's dogma).
fn kamikaze(target: i64, range: f64, damage: f64) -> Effect {
    Effect {
        burns_fuel: false,
        module_id: SQUADRON_SHIP * 8 + 2,
        module_type: 23_055,
        effect_id: 6_554,
        fx: String::new(),
        duration_ms: 60_000.0,
        capacitor_need: 0.0,
        repeat: 1,
        target_id: target,
        turret: None,
        action: Some(Action::Kamikaze { damage: [damage, 0.0, 0.0, 0.0], range }),
        on: None,
        off: None,
        on_changes: Vec::new(),
        off_changes: Vec::new(),
    }
}

/// A squadron's missiles, locked on `target`, reaching `reach` metres, with the explosion `blast` (the squadron's own numbers).
fn missiles(target: i64, reach: f64, blast: Blast, damage: f64) -> Effect {
    Effect {
        module_id: SQUADRON_SHIP * 8 + 3,
        effect_id: 6_441,
        duration_ms: 6_000.0,
        action: Some(Action::Missile { damage: [damage, 0.0, 0.0, 0.0], reach, blast }),
        ..kamikaze(target, 0.0, 0.0)
    }
}

/// The points a ship has left, from the pilot of its session.
fn points_of(scene: &Scene, ship: i64, now: i64) -> f64 {
    let session = scene.session_of(ship).expect("the ship is in the scene");
    scene.pilots[&session].ship.health.points_left(now).0
}

#[test]
fn a_kamikaze_squadron_closes_in_on_its_target_detonates_and_is_destroyed() {
    let (mut scene, squadron, _) = launched();
    rat_at(&mut scene, 3_000.0);
    let before = points_of(&scene, RAT_SHIP, LATER);
    let told = scene.activate(squadron, kamikaze(RAT_SHIP, 500.0, 5_000.0), LATER + SECOND);
    assert!(told.iter().any(|out| matches!(out, Out::Accepted { command: 17, .. })), "{told:?}");
    // the squadron flies to within its range of the rat first (the reference's approach), and has not yet detonated
    assert!(matches!(
        scene.pilots[&squadron].ship.course,
        Course::Follow { target, range } if target == RAT_SHIP && range == 500.0
    ));
    let told = poll_all(&mut scene, LATER + SECOND, 60);
    assert!(
        told.iter().any(|out| matches!(out, Out::Destroyed { ship_id, .. } if *ship_id == SQUADRON_SHIP)),
        "the squadron detonates and is destroyed: {told:?}"
    );
    assert!(scene.session_of(SQUADRON_SHIP).is_none(), "a detonated squadron is out of the scene");
    let hurt =
        scene.session_of(RAT_SHIP).is_none_or(|_| points_of(&scene, RAT_SHIP, LATER + 61 * SECOND) < before);
    assert!(hurt, "the rat took the detonation");
}

#[test]
fn a_kamikaze_out_of_its_window_before_it_reaches_its_target_does_not_detonate() {
    let (mut scene, squadron, _) = launched();
    rat_at(&mut scene, 40_000.0);
    let mut effect = kamikaze(RAT_SHIP, 500.0, 5_000.0);
    effect.duration_ms = 2_000.0;
    scene.activate(squadron, effect, LATER + SECOND);
    let told = poll_all(&mut scene, LATER + SECOND, 10);
    assert!(
        !told.iter().any(|out| matches!(out, Out::Destroyed { ship_id, .. } if *ship_id == SQUADRON_SHIP)),
        "a squadron that never reached its target is not destroyed: {told:?}"
    );
    assert!(scene.session_of(SQUADRON_SHIP).is_some());
}

#[test]
fn a_missile_within_reach_hits_its_target_scaled_by_the_application_factor() {
    // a target the explosion covers entirely: the full damage lands
    let (mut scene, squadron, _) = launched();
    rat_at(&mut scene, 3_000.0);
    let before = points_of(&scene, RAT_SHIP, LATER);
    let full = Blast {
        explosion_radius: 1.0,
        explosion_velocity: 1.0,
        reduction_factor: 1.0,
        reduction_sensitivity: 5.5,
    };
    scene.activate(squadron, missiles(RAT_SHIP, 5_000.0, full, 500.0), LATER + SECOND);
    poll_all(&mut scene, LATER + SECOND, 8);
    let full_loss = before - points_of(&scene, RAT_SHIP, LATER + 9 * SECOND);
    assert!(full_loss > 0.0, "the missile hits a target in reach");

    // the same missile on a target its explosion barely reaches: almost nothing lands
    let (mut scene, squadron, _) = launched();
    rat_at(&mut scene, 3_000.0);
    let before = points_of(&scene, RAT_SHIP, LATER);
    let wide = Blast { explosion_radius: 1.0e9, ..full };
    scene.activate(squadron, missiles(RAT_SHIP, 5_000.0, wide, 500.0), LATER + SECOND);
    poll_all(&mut scene, LATER + SECOND, 8);
    let thin_loss = before - points_of(&scene, RAT_SHIP, LATER + 9 * SECOND);
    assert!(
        thin_loss < full_loss / 100.0,
        "the application factor scales the damage: {thin_loss} against {full_loss}"
    );
}

#[test]
fn a_missile_is_not_fired_beyond_its_reach_and_the_squadron_closes_in_first() {
    let (mut scene, squadron, _) = launched();
    rat_at(&mut scene, 30_000.0);
    let before = points_of(&scene, RAT_SHIP, LATER);
    let full = Blast {
        explosion_radius: 1.0,
        explosion_velocity: 1.0,
        reduction_factor: 1.0,
        reduction_sensitivity: 5.5,
    };
    let told = scene.activate(squadron, missiles(RAT_SHIP, 5_000.0, full, 500.0), LATER + SECOND);
    assert!(told.iter().any(|out| matches!(out, Out::Accepted { command: 17, .. })), "{told:?}");
    assert!(matches!(
        scene.pilots[&squadron].ship.course,
        Course::Follow { target, range } if target == RAT_SHIP && range == 5_000.0
    ));
    // the cycle ends before the squadron gets there: nothing hits the rat
    poll_all(&mut scene, LATER + SECOND, 8);
    assert_eq!(points_of(&scene, RAT_SHIP, LATER + 9 * SECOND), before, "no hit beyond reach");
}

#[test]
fn a_missile_blast_scales_the_damage_by_the_signature_against_the_explosion_radius() {
    let blast = Blast {
        explosion_radius: 80.0,
        explosion_velocity: 1.0,
        reduction_factor: 1.0,
        reduction_sensitivity: 5.5,
    };
    assert_eq!(blast.applied([100.0, 0.0, 0.0, 0.0], 40.0, 0.0), [50.0, 0.0, 0.0, 0.0]);
    assert_eq!(blast.applied([100.0, 0.0, 0.0, 0.0], 160.0, 0.0), [100.0, 0.0, 0.0, 0.0]);
}
