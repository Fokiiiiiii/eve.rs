//! The scene as the world of the NPCs' trees: what it tells the trees that ask to hear (sensors, locks, aggression, the end of an undock protection),
//! and what it answers as a dogma engine for the weapons of an NPC.

use eve_behaviors::blackboards::Observer;
use eve_behaviors::env::{Ballpark, Dogma, WorldError};
use eve_behaviors::events::{Subject, message};
use eve_behaviors::tree::Delivery;
use eve_behaviors::value::Value;

use super::rat_tests::{
    LATER, PLAYER, RAT_SHIP, SECOND, place_near, rat_identity, rat_spec, scene_with_a_player,
};
use super::world::distance;
use super::{Brains, SceneWorld};
use crate::npc::is_npc_session;
use crate::scene::tests::NOON;
use crate::scene::{Scene, SessionId};
use crate::targeting::Order;
use crate::weapons::Aggression;

const PLAYER_SHIP: i64 = 9_988_400_001_939;
const WATCHER: Observer = Observer { tree: 1, task: 2, handler: 3 };

/// A scene with a player and an NPC without a brain, 8 km from it, and the NPC's session.
fn scene_with_a_rat() -> (Scene, SessionId) {
    let mut scene = scene_with_a_player();
    let place = place_near(&mut scene, 8_000.0);
    let (rat, _) = scene.spawn_npc(rat_spec(), rat_identity(0.0), place, LATER);
    (scene, rat)
}

/// Runs `body` against the scene as the world of the trees, with the brains the scene keeps.
fn with_world<R>(scene: &mut Scene, now: i64, body: impl FnOnce(&mut SceneWorld<'_>, &mut Brains) -> R) -> R {
    let mut out = Vec::new();
    let mut brains = scene.brains.take().unwrap_or_default();
    let mut state = std::mem::replace(&mut brains.state, super::WorldState::for_test());
    let result = {
        let mut world = SceneWorld::new(scene, now, &mut out, &mut state);
        body(&mut world, &mut brains)
    };
    brains.state = state;
    scene.brains = Some(brains);
    result
}

/// What was told to `WATCHER`, taken off the trees' queue.
fn heard(brains: &mut Brains) -> Vec<Vec<Value>> {
    std::mem::take(&mut brains.shared.pending)
        .into_iter()
        .filter_map(|delivery| match delivery {
            Delivery::Event { observer, args } if observer == WATCHER => Some(args),
            _ => None,
        })
        .collect()
}

#[test]
fn a_sensor_sees_the_ships_within_its_range_measured_between_centres_and_never_its_own() {
    let (mut scene, _) = scene_with_a_rat();
    with_world(&mut scene, LATER, |world, _| {
        assert!(world.proximity_sensor(RAT_SHIP).is_none(), "an item without a sensor has none");
        let apart =
            distance(world.ball_position(RAT_SHIP).unwrap(), world.ball_position(PLAYER_SHIP).unwrap());
        let sensor = world.add_proximity_sensor(RAT_SHIP, 5, apart).expect("the ship has a ball");
        assert_eq!(
            (sensor.item_id, sensor.ball_ids_in_range.clone()),
            (RAT_SHIP, vec![PLAYER_SHIP]),
            "range {apart}"
        );
        world.state.sensors.get_mut(&RAT_SHIP).unwrap().detection_range = apart - 0.001;
        assert_eq!(world.proximity_sensor(RAT_SHIP).unwrap().ball_ids_in_range, Vec::<i64>::new());
        assert!(world.ships_in_sensor_range(PLAYER_SHIP).is_empty(), "the player has no sensor");
    });
}

#[test]
fn sensors_are_found_by_the_tags_they_all_have_and_their_bubble_is_that_of_their_ship() {
    let (mut scene, _) = scene_with_a_rat();
    with_world(&mut scene, LATER, |world, _| {
        world.add_proximity_sensor(RAT_SHIP, 5, 10_000.0);
        world.add_proximity_sensor_tag(RAT_SHIP, "a");
        world.add_proximity_sensor_tag(RAT_SHIP, "a");
        world.add_proximity_sensor_tag(RAT_SHIP, "b");
        assert_eq!(world.state.sensors[&RAT_SHIP].tags, ["a", "b"], "a tag is put once");
        let by = |tags: &[&str]| {
            let tags: Vec<String> = tags.iter().map(ToString::to_string).collect();
            world.proximity_sensors(&tags).len()
        };
        assert_eq!((by(&["a"]), by(&["a", "b"]), by(&["a", "c"]), by(&["c"]), by(&[])), (1, 1, 0, 0, 1));
        let bubble = world.proximity_sensor(RAT_SHIP).unwrap().bubble_id;
        assert_eq!(Some(bubble), world.ball(RAT_SHIP).map(|ball| ball.new_bubble_id));
        assert_eq!(world.register_proximity_sensor(RAT_SHIP), Some(RAT_SHIP));
        assert_eq!(world.register_proximity_sensor(PLAYER_SHIP), None);
    });
}

#[test]
fn a_sensor_tells_the_trees_of_the_ships_that_come_into_its_range_and_leave_it_once_each() {
    let (mut scene, _) = scene_with_a_rat();
    with_world(&mut scene, LATER, |world, brains| {
        world.add_proximity_sensor(RAT_SHIP, 5, 10_000.0);
        brains.shared.events.subscribe_item(RAT_SHIP, message::ON_PROXIMITY, WATCHER);
        world.tell_what_happened(&mut brains.shared);
        assert_eq!(heard(brains), [vec![Value::Int(PLAYER_SHIP), Value::Bool(true)]]);
        world.tell_what_happened(&mut brains.shared);
        assert!(heard(brains).is_empty(), "what was told is not told again");
        world.state.sensors.get_mut(&RAT_SHIP).unwrap().detection_range = 100.0;
        world.tell_what_happened(&mut brains.shared);
        assert_eq!(heard(brains), [vec![Value::Int(PLAYER_SHIP), Value::Bool(false)]]);
        // a sensor that is replaced sees afresh: what it saw is seen again
        world.state.sensors.get_mut(&RAT_SHIP).unwrap().detection_range = 10_000.0;
        world.tell_what_happened(&mut brains.shared);
        heard(brains);
        world.replace_proximity_sensor(RAT_SHIP);
        assert!(world.state.sensors[&RAT_SHIP].in_range.is_empty());
        world.tell_what_happened(&mut brains.shared);
        assert_eq!(heard(brains), [vec![Value::Int(PLAYER_SHIP), Value::Bool(true)]]);
    });
}

#[test]
fn the_trees_are_told_of_the_locks_an_npc_gains_and_loses() {
    let (mut scene, rat) = scene_with_a_rat();
    let mut now = LATER;
    scene.target_order(rat, Order::Lock(PLAYER_SHIP), now);
    for _ in 0..80 {
        now += SECOND / 4;
        scene.poll(now);
    }
    assert_eq!(scene.locked_by(rat), [PLAYER_SHIP]);
    with_world(&mut scene, now, |world, brains| {
        brains.shared.events.subscribe_item(RAT_SHIP, message::TARGET_ADDED, WATCHER);
        brains.shared.events.subscribe_item(RAT_SHIP, message::TARGET_LOST, WATCHER);
        world.tell_what_happened(&mut brains.shared);
        assert_eq!(heard(brains), [vec![Value::Int(PLAYER_SHIP)]]);
        world.tell_what_happened(&mut brains.shared);
        assert!(heard(brains).is_empty());
    });
    scene.target_order(rat, Order::Remove(vec![PLAYER_SHIP]), now);
    with_world(&mut scene, now, |world, brains| {
        world.tell_what_happened(&mut brains.shared);
        assert_eq!(heard(brains), [vec![Value::Int(PLAYER_SHIP), Value::from("lost")]]);
    });
}

#[test]
fn an_offensive_act_is_told_to_those_who_wait_for_one_on_the_ship_it_was_done_to() {
    let (mut scene, _) = scene_with_a_rat();
    scene.aggressions.push(Aggression {
        attacker_ship: PLAYER_SHIP,
        target_ship: RAT_SHIP,
        module_type_id: 3_074,
        source_owner_id: 140_000_342,
        target_type_id: 601,
    });
    with_world(&mut scene, LATER, |world, brains| {
        brains.shared.events.subscribe_item(RAT_SHIP, message::CRIMEWATCH_EVENT_STARTED, WATCHER);
        brains.shared.events.subscribe_item(
            PLAYER_SHIP,
            message::CRIMEWATCH_EVENT_STARTED,
            Observer { tree: 9, ..WATCHER },
        );
        world.tell_what_happened(&mut brains.shared);
        let told = heard(brains);
        assert_eq!(told.len(), 1, "only the ship that was shot at is told");
        assert_eq!(told[0][0], Value::record(&[("shipID", Value::Int(PLAYER_SHIP))]));
        assert_eq!(
            told[0][1],
            Value::record(&[
                ("isAssistance", Value::Bool(false)),
                ("isOffensive", Value::Bool(true)),
                ("moduleTypeID", Value::Int(3_074)),
                ("sourceOwnerID", Value::Int(140_000_342)),
                ("targetTypeID", Value::Int(601)),
            ])
        );
        assert!(world.scene.aggressions.is_empty(), "an act is told once");
    });
}

#[test]
fn the_bubble_is_told_when_the_protection_of_a_ship_ends_by_time_or_by_the_pilot() {
    let (mut scene, _) = scene_with_a_rat();
    let bubble_of_player =
        with_world(&mut scene, NOON, |world, _| world.ball(PLAYER_SHIP).unwrap().new_bubble_id);
    let told_at = |scene: &mut Scene, now: i64| {
        with_world(scene, now, |world, brains| {
            let subject = Subject::Bubble(bubble_of_player);
            if brains.shared.events.subscribers_of(subject, message::INVULNERABILITY_CANCELED).is_empty() {
                brains.shared.events.subscribe(subject, message::INVULNERABILITY_CANCELED, WATCHER);
            }
            world.tell_what_happened(&mut brains.shared);
            heard(brains)
        })
    };
    assert!(told_at(&mut scene, NOON + SECOND).is_empty(), "the ship is protected: nothing is told");
    assert_eq!(
        told_at(&mut scene, NOON + 30 * SECOND),
        [vec![Value::Int(PLAYER_SHIP), Value::Bool(true), Value::None]]
    );
    assert!(told_at(&mut scene, NOON + 32 * SECOND).is_empty(), "it is told once");

    // a pilot who flies the ship gives the protection up
    let (mut scene, _) = scene_with_a_rat();
    assert!(told_at(&mut scene, NOON + SECOND).is_empty());
    scene.command(PLAYER, crate::scene::Command::Stop, NOON + 2 * SECOND);
    assert_eq!(
        told_at(&mut scene, NOON + 3 * SECOND),
        [vec![Value::Int(PLAYER_SHIP), Value::Bool(false), Value::None]]
    );
}

#[test]
fn an_npcs_weapon_is_a_module_that_is_its_ship_and_the_ships_type_has_the_effect() {
    let (mut scene, _) = scene_with_a_rat();
    with_world(&mut scene, LATER, |world, _| {
        let type_id = i64::from(rat_spec().type_id);
        assert!(world.type_has_effect(type_id, 10));
        assert!(!world.type_has_effect(type_id, 11));
        assert!(!world.type_has_effect(type_id + 1, 10));
        assert_eq!(world.default_effect_id(type_id), Some(10));
        assert_eq!(world.default_effect_id(type_id + 1), None);
        // the numbers the trees read of the weapon are the weapon's
        assert_eq!(world.attribute_value(RAT_SHIP, 54).unwrap(), 20_000.0, "optimal");
        assert_eq!(world.attribute_value(RAT_SHIP, 158).unwrap(), 10_000.0, "falloff");
        assert_eq!(world.attribute_value(RAT_SHIP, 51).unwrap(), 4_000.0, "cycle");
        assert_eq!(world.attribute_value(RAT_SHIP, 117).unwrap(), 6.0, "kinetic");
        assert_eq!(world.attribute_value(RAT_SHIP, 116).unwrap(), 4.0, "explosive");
        assert_eq!(world.attribute_value(RAT_SHIP, 114).unwrap(), 0.0, "em");
        assert_eq!(world.attribute_value(RAT_SHIP, 118).unwrap(), 0.0, "thermal");
        assert_eq!(world.attribute_value(RAT_SHIP, 37).unwrap(), 350.0, "the ship's speed");
        assert!(
            matches!(world.attribute_value(1, 54), Err(WorldError::Key(_))),
            "an item that is not in the scene"
        );
        assert_eq!(world.effect_range_and_falloff_for_item(RAT_SHIP, 10), (Some(20_000.0), Some(10_000.0)));
        assert_eq!(world.effect_range_and_falloff_for_item(RAT_SHIP, 11), (None, None));
        assert_eq!(world.effect_max_effective_range_with_falloff(10, RAT_SHIP), 30_000.0);
        assert_eq!(world.effect_range_with_falloff_multiplier(10, RAT_SHIP, 2.0), 40_000.0);
        assert_eq!(world.effect_range_with_falloff_multiplier(11, RAT_SHIP, 2.0), 0.0);
        assert_eq!(world.range_falloff_modifier_at_distance(10, RAT_SHIP, 20_000.0), 1.0);
        assert_eq!(world.range_falloff_modifier_at_distance(10, RAT_SHIP, 30_000.0), 0.5);
    });
}

#[test]
fn an_npc_that_is_told_to_fire_at_a_target_it_has_not_locked_is_refused_and_one_that_has_fires() {
    let (mut scene, rat) = scene_with_a_rat();
    let refused = with_world(&mut scene, LATER, |world, _| {
        world.activate_with_context(0, RAT_SHIP, Some(10), Some(PLAYER_SHIP), 1, &Value::None)
    });
    assert!(
        matches!(&refused, Err(WorldError::User(reason)) if reason == "TARGET_NOT_LOCKED"),
        "{refused:?}"
    );
    let not_a_module = with_world(&mut scene, LATER, |world, _| {
        world.activate_with_context(0, PLAYER_SHIP, Some(10), Some(PLAYER_SHIP), 1, &Value::None)
    });
    assert!(
        matches!(&not_a_module, Err(WorldError::User(reason)) if reason == "NotAModule"),
        "{not_a_module:?}"
    );
    let wrong_effect = with_world(&mut scene, LATER, |world, _| {
        world.activate_with_context(0, RAT_SHIP, Some(11), Some(PLAYER_SHIP), 1, &Value::None)
    });
    assert!(
        matches!(&wrong_effect, Err(WorldError::User(reason)) if reason == "NoSuchEffect"),
        "{wrong_effect:?}"
    );

    let mut now = LATER;
    scene.target_order(rat, Order::Lock(PLAYER_SHIP), now);
    for _ in 0..80 {
        now += SECOND / 4;
        scene.poll(now);
    }
    with_world(&mut scene, now, |world, _| {
        assert!(
            world.activate_with_context(0, RAT_SHIP, Some(10), Some(PLAYER_SHIP), 1, &Value::None).is_ok()
        );
        assert!(world.is_effect_active(RAT_SHIP, 10));
        assert_eq!(world.active_effect_target_id(RAT_SHIP, 10), Some(Some(PLAYER_SHIP)));
        assert_eq!(world.active_effect_data(RAT_SHIP, 10).map(|c| c.duration_ms), Some(4_000.0));
        assert!(world.targets(RAT_SHIP).contains(&PLAYER_SHIP));
        assert!(world.is_targeting(RAT_SHIP, Some(PLAYER_SHIP)));
        // what it did the player is told, and the host is told of nothing for the NPC
        assert!(world.out.iter().any(|out| out.session() == PLAYER));
        assert!(world.out.iter().all(|out| !is_npc_session(out.session())));
        world.stop_effect(10, RAT_SHIP, true).unwrap();
    });
    assert!(scene.pilots[&rat].modules.running_effects().any(|(effect, _)| effect.module_id == RAT_SHIP));
}

#[test]
fn an_npc_locks_and_lets_go_of_a_target_through_the_world_and_is_refused_what_the_scene_refuses() {
    let (mut scene, rat) = scene_with_a_rat();
    // the player is still under the protection of its undocking
    let refused =
        with_world(&mut scene, NOON + SECOND, |world, _| world.add_target_ex(RAT_SHIP, PLAYER_SHIP));
    assert!(
        matches!(&refused, Err(WorldError::User(reason)) if reason == "TARGET_INVULNERABLE"),
        "{refused:?}"
    );
    assert!(matches!(
        with_world(&mut scene, LATER, |world, _| world.add_target_ex(1, PLAYER_SHIP)),
        Err(WorldError::Key(_))
    ));

    with_world(&mut scene, LATER, |world, _| {
        assert!(!world.has_pending_target(RAT_SHIP, None));
        assert!(world.add_target_ex(RAT_SHIP, PLAYER_SHIP).is_ok());
        assert!(world.has_pending_target(RAT_SHIP, Some(PLAYER_SHIP)));
        assert!(!world.has_pending_target(RAT_SHIP, Some(RAT_SHIP)));
        assert_eq!(world.targets_ex(RAT_SHIP), [PLAYER_SHIP]);
        assert!(!world.is_targeting(RAT_SHIP, Some(PLAYER_SHIP)), "the lock is not had yet");
    });
    let mut now = LATER;
    for _ in 0..80 {
        now += SECOND / 4;
        scene.poll(now);
    }
    assert_eq!(scene.locked_by(rat), [PLAYER_SHIP]);
    with_world(&mut scene, now, |world, _| {
        assert!(world.is_targeting(RAT_SHIP, Some(PLAYER_SHIP)) && world.is_targeting(RAT_SHIP, None));
        assert_eq!(world.locked_targets(RAT_SHIP), [PLAYER_SHIP]);
        world.remove_target(RAT_SHIP, PLAYER_SHIP);
        assert!(!world.is_targeting(RAT_SHIP, None));
    });
    assert_eq!(scene.locked_by(rat), Vec::<i64>::new());
}

#[test]
fn offensive_acts_that_no_tree_is_there_to_hear_are_not_kept() {
    let (mut scene, _) = scene_with_a_rat();
    scene.brains.get_or_insert_with(Brains::default);
    scene.aggressions.push(Aggression {
        attacker_ship: PLAYER_SHIP,
        target_ship: RAT_SHIP,
        module_type_id: 3_074,
        source_owner_id: 140_000_342,
        target_type_id: 601,
    });
    scene.poll(LATER + SECOND);
    assert!(scene.aggressions.is_empty());
}

#[test]
fn the_trees_that_watch_a_ship_are_told_when_it_is_destroyed_once() {
    let (mut scene, rat) = scene_with_a_rat();
    scene.destroy(rat, None, LATER);
    with_world(&mut scene, LATER, |world, brains| {
        brains.shared.events.subscribe_item(RAT_SHIP, message::ON_BALLPARK_ITEM_DESTROY, WATCHER);
        brains.shared.events.subscribe_item(
            PLAYER_SHIP,
            message::ON_BALLPARK_ITEM_DESTROY,
            Observer { tree: 9, ..WATCHER },
        );
        world.tell_what_happened(&mut brains.shared);
        assert_eq!(heard(brains), [vec![Value::Int(RAT_SHIP)]]);
        world.tell_what_happened(&mut brains.shared);
        assert!(heard(brains).is_empty());
    });
}
