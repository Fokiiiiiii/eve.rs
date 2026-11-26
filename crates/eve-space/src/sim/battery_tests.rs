use super::*;
use crate::link::BatteryPlacement;

const BATTERY: i64 = 1_050_000_001;
const TOWER: i64 = 1_060_000_001;

/// A battery placed on the ship, held by its pilot, is answered over the simulation: its lock is told to the holder only, and an
/// order for a battery that is not in the system is refused.
#[test]
fn battery_orders_are_answered_and_the_holder_alone_is_told_the_lock() {
    let mut sim = Sim::new(Arc::new(Universe::with_systems([(*system()).clone()])));
    sim.handle(enter(SESSION, STATION, SYSTEM), NOON).unwrap();
    let scene = SceneKey::solar_system(SYSTEM);
    let at = sim.scenes[&scene].pilots[&SESSION].ship.position;
    let placement = BatteryPlacement {
        battery_id: BATTERY,
        solar_system_id: SYSTEM,
        type_id: 16_631,
        group_id: 1_000,
        display_name: "Test battery".to_string(),
        holder_session: SESSION,
        position: [at.x, at.y, at.z],
        radius: 60.0,
        scan_resolution: 400.0,
        max_target_range: 50_000.0,
        max_locked_targets: 2,
        online: true,
    };
    let placed = sim.handle(Message::PlaceBattery(Box::new(placement)), NOON).unwrap();
    assert_eq!(placed, vec![Message::BatteryPlaced { battery_id: BATTERY, reason: String::new() }]);
    let lock = Message::BatteryTarget {
        request_id: 9,
        battery_id: BATTERY,
        solar_system_id: SYSTEM,
        order: 1,
        target: STATION,
    };
    let answered = sim.handle(lock, NOON).unwrap();
    assert!(
        matches!(&answered[0], Message::BatteryTargets { request_id: 9, reason, .. } if reason.is_empty()),
        "{answered:?}"
    );
    let later = sim.poll(NOON + 200 * SECOND);
    let told: Vec<&Message> = answered[1..]
        .iter()
        .chain(later.iter())
        .filter(|message| matches!(message, Message::Notify { name, .. } if name == "OnTargetOBO"))
        .collect();
    assert!(
        matches!(&told[..], [Message::Notify { session_id, .. }] if *session_id == SESSION),
        "the lock is told once, to the holder: {told:?}"
    );

    let missing = Message::BatteryTarget {
        request_id: 10,
        battery_id: BATTERY + 1,
        solar_system_id: SYSTEM,
        order: 1,
        target: STATION,
    };
    assert!(matches!(
        &sim.handle(missing, NOON).unwrap()[..],
        [Message::BatteryTargets { request_id: 10, reason, .. }] if reason == "BATTERY_NOT_IN_SPACE"
    ));
    let unknown = Message::PlaceBattery(Box::new(BatteryPlacement {
        battery_id: BATTERY + 2,
        solar_system_id: SYSTEM + 1,
        type_id: 16_631,
        group_id: 1_000,
        display_name: String::new(),
        holder_session: SESSION,
        position: [0.0, 0.0, 0.0],
        radius: 60.0,
        scan_resolution: 400.0,
        max_target_range: 50_000.0,
        max_locked_targets: 2,
        online: true,
    }));
    assert!(matches!(
        &sim.handle(unknown, NOON).unwrap()[..],
        [Message::BatteryPlaced { reason, .. }] if reason == "SYSTEM_UNKNOWN"
    ));
}

/// A battery fires only at a target it holds. A start before the lock is had is refused; after it, a start is accepted and a stop ends it.
/// Removing the lock ends a fire at it. Without a holder, or offline, a battery does not fire.
#[test]
fn battery_fire_needs_a_held_target_and_ends_with_its_lock() {
    let mut sim = Sim::new(Arc::new(Universe::with_systems([(*system()).clone()])));
    sim.handle(enter(SESSION, STATION, SYSTEM), NOON).unwrap();
    let at = sim.scenes[&SceneKey::solar_system(SYSTEM)].pilots[&SESSION].ship.position;
    let place = |holder_session, online| BatteryPlacement {
        battery_id: BATTERY,
        solar_system_id: SYSTEM,
        type_id: 16_631,
        group_id: 1_000,
        display_name: "Test battery".to_string(),
        holder_session,
        position: [at.x, at.y, at.z],
        radius: 60.0,
        scan_resolution: 400.0,
        max_target_range: 50_000.0,
        max_locked_targets: 2,
        online,
    };
    let fire = |request_id: u64, on: bool| Message::BatteryFire {
        request_id,
        battery_id: BATTERY,
        solar_system_id: SYSTEM,
        on,
        target: STATION,
    };
    sim.handle(Message::PlaceBattery(Box::new(place(SESSION, true))), NOON).unwrap();

    let start_too_early = sim.handle(fire(30, true), NOON).unwrap();
    assert!(
        matches!(&start_too_early[..], [Message::BatteryFired { request_id: 30, reason, active: false, target: 0, .. }] if reason == "TARGET_NOT_LOCKED"),
        "{start_too_early:?}"
    );

    sim.handle(
        Message::BatteryTarget {
            request_id: 31,
            battery_id: BATTERY,
            solar_system_id: SYSTEM,
            order: 1,
            target: STATION,
        },
        NOON,
    )
    .unwrap();
    sim.poll(NOON + 200 * SECOND);
    let started = sim.handle(fire(32, true), NOON + 200 * SECOND).unwrap();
    assert!(
        matches!(&started[0], Message::BatteryFired { request_id: 32, reason, active: true, target, .. } if reason.is_empty() && *target == STATION),
        "{started:?}"
    );
    let stopped = sim.handle(fire(33, false), NOON + 200 * SECOND).unwrap();
    assert!(
        matches!(&stopped[..], [Message::BatteryFired { request_id: 33, reason, active: false, target: 0, .. }] if reason.is_empty()),
        "{stopped:?}"
    );

    // a fire that is on ends when its lock is removed: a new start then finds no lock
    sim.handle(fire(34, true), NOON + 200 * SECOND).unwrap();
    sim.handle(
        Message::BatteryTarget {
            request_id: 35,
            battery_id: BATTERY,
            solar_system_id: SYSTEM,
            order: 2,
            target: STATION,
        },
        NOON + 200 * SECOND,
    )
    .unwrap();
    let after_remove = sim.handle(fire(36, true), NOON + 200 * SECOND).unwrap();
    assert!(
        matches!(&after_remove[..], [Message::BatteryFired { request_id: 36, reason, active: false, .. }] if reason == "TARGET_NOT_LOCKED"),
        "{after_remove:?}"
    );

    // no holder: the battery does not fire, whatever it holds
    sim.handle(Message::PlaceBattery(Box::new(place(0, true))), NOON + 200 * SECOND).unwrap();
    let unheld = sim.handle(fire(37, true), NOON + 200 * SECOND).unwrap();
    assert!(
        matches!(&unheld[..], [Message::BatteryFired { request_id: 37, reason, active: false, .. }] if reason == "NO_CONTROL_GRANT"),
        "{unheld:?}"
    );

    // offline: refused with its own reason
    sim.handle(Message::PlaceBattery(Box::new(place(SESSION, false))), NOON + 200 * SECOND).unwrap();
    let offline = sim.handle(fire(38, true), NOON + 200 * SECOND).unwrap();
    assert!(
        matches!(&offline[..], [Message::BatteryFired { request_id: 38, reason, active: false, .. }] if reason == "BATTERY_OFFLINE"),
        "{offline:?}"
    );
}

/// A stop is always accepted (protocol 104): a battery that is offline, has no holder or is not in space answers a stop with no reason, and a fire
/// that is on ends with its stop. A start on a battery that is offline is still refused.
#[test]
fn a_stop_is_always_accepted_for_an_offline_unheld_or_absent_battery() {
    let mut sim = Sim::new(Arc::new(Universe::with_systems([(*system()).clone()])));
    sim.handle(enter(SESSION, STATION, SYSTEM), NOON).unwrap();
    let at = sim.scenes[&SceneKey::solar_system(SYSTEM)].pilots[&SESSION].ship.position;
    let place = |holder_session, online| BatteryPlacement {
        battery_id: BATTERY,
        solar_system_id: SYSTEM,
        type_id: 16_631,
        group_id: 1_000,
        display_name: "Test battery".to_string(),
        holder_session,
        position: [at.x, at.y, at.z],
        radius: 60.0,
        scan_resolution: 400.0,
        max_target_range: 50_000.0,
        max_locked_targets: 2,
        online,
    };
    let fire = |request_id: u64, battery_id: i64, on: bool| Message::BatteryFire {
        request_id,
        battery_id,
        solar_system_id: SYSTEM,
        on,
        target: STATION,
    };
    sim.handle(Message::PlaceBattery(Box::new(place(SESSION, true))), NOON).unwrap();
    sim.handle(
        Message::BatteryTarget {
            request_id: 40,
            battery_id: BATTERY,
            solar_system_id: SYSTEM,
            order: 1,
            target: STATION,
        },
        NOON,
    )
    .unwrap();
    sim.poll(NOON + 200 * SECOND);
    let started = sim.handle(fire(41, BATTERY, true), NOON + 200 * SECOND).unwrap();
    assert!(
        matches!(&started[..], [Message::BatteryFired { request_id: 41, reason, active: true, .. }] if reason.is_empty()),
        "{started:?}"
    );

    // offline: going offline ends the fire, and the stop that follows is accepted
    sim.handle(Message::PlaceBattery(Box::new(place(SESSION, false))), NOON + 200 * SECOND).unwrap();
    let offline = sim.handle(fire(42, BATTERY, false), NOON + 200 * SECOND).unwrap();
    assert!(
        matches!(&offline[..], [Message::BatteryFired { request_id: 42, reason, active: false, target: 0, .. }] if reason.is_empty()),
        "{offline:?}"
    );

    // no holder, online: a stop is accepted as well
    sim.handle(Message::PlaceBattery(Box::new(place(0, true))), NOON + 200 * SECOND).unwrap();
    let unheld = sim.handle(fire(43, BATTERY, false), NOON + 200 * SECOND).unwrap();
    assert!(
        matches!(&unheld[..], [Message::BatteryFired { request_id: 43, reason, active: false, target: 0, .. }] if reason.is_empty()),
        "{unheld:?}"
    );

    // not in space: there is no fire to end, so the stop is accepted
    let absent = sim.handle(fire(44, BATTERY + 7, false), NOON + 200 * SECOND).unwrap();
    assert!(
        matches!(&absent[..], [Message::BatteryFired { request_id: 44, reason, active: false, target: 0, .. }] if reason.is_empty()),
        "{absent:?}"
    );

    // a start on the battery while it is offline is still refused
    sim.handle(Message::PlaceBattery(Box::new(place(SESSION, false))), NOON + 200 * SECOND).unwrap();
    let refused = sim.handle(fire(45, BATTERY, true), NOON + 200 * SECOND).unwrap();
    assert!(
        matches!(&refused[..], [Message::BatteryFired { request_id: 45, reason, active: false, .. }] if reason == "BATTERY_OFFLINE"),
        "{refused:?}"
    );
}

/// The orders 4 (cancel) and 3 (clear) are answered through the simulation: a cancel gives up a lock that is going and leaves a held one, and a clear
/// lets go of every lock and tells the holder.
#[test]
fn the_cancel_and_clear_orders_are_answered_through_the_simulation() {
    let mut sim = Sim::new(Arc::new(Universe::with_systems([(*system()).clone()])));
    sim.handle(enter(SESSION, STATION, SYSTEM), NOON).unwrap();
    let at = sim.scenes[&SceneKey::solar_system(SYSTEM)].pilots[&SESSION].ship.position;
    let placement = BatteryPlacement {
        battery_id: BATTERY,
        solar_system_id: SYSTEM,
        type_id: 16_631,
        group_id: 1_000,
        display_name: "Test battery".to_string(),
        holder_session: SESSION,
        position: [at.x, at.y, at.z],
        radius: 60.0,
        scan_resolution: 400.0,
        max_target_range: 50_000.0,
        max_locked_targets: 2,
        online: true,
    };
    sim.handle(Message::PlaceBattery(Box::new(placement)), NOON).unwrap();
    let order = |request_id: u64, order: u8| Message::BatteryTarget {
        request_id,
        battery_id: BATTERY,
        solar_system_id: SYSTEM,
        order,
        target: STATION,
    };
    let answer_of = |messages: &[Message], id: u64| {
        messages
            .iter()
            .find_map(|message| match message {
                Message::BatteryTargets { request_id, reason, pending, targets, .. } if *request_id == id => {
                    Some((reason.clone(), *pending, targets.clone()))
                }
                _ => None,
            })
            .unwrap_or_else(|| panic!("no answer to request {id}: {messages:?}"))
    };

    // a lock that is going, then cancelled: nothing is held
    let locked = sim.handle(order(50, 1), NOON).unwrap();
    assert_eq!(answer_of(&locked, 50), (String::new(), true, Vec::new()), "{locked:?}");
    let cancelled = sim.handle(order(51, 4), NOON).unwrap();
    assert_eq!(answer_of(&cancelled, 51), (String::new(), false, Vec::new()), "{cancelled:?}");

    // a lock that is had, then cancelled: a cancel gives up only a lock that is going, so the held one stays
    sim.handle(order(52, 1), NOON).unwrap();
    sim.poll(NOON + 200 * SECOND);
    let held = sim.handle(order(53, 4), NOON + 200 * SECOND).unwrap();
    assert_eq!(answer_of(&held, 53), (String::new(), false, vec![STATION]), "{held:?}");

    // a clear lets go of every lock and tells the holder
    let cleared = sim.handle(order(54, 3), NOON + 200 * SECOND).unwrap();
    assert_eq!(answer_of(&cleared, 54), (String::new(), false, Vec::new()), "{cleared:?}");
    assert!(
        cleared
            .iter()
            .any(|message| matches!(message, Message::Notify { name, .. } if name == "OnTargetOBO")),
        "the holder was not told the clear: {cleared:?}"
    );
}

/// A sentry binding sent for a battery the scene does not hold creates no battery, and the scene's own battery is left as it is.
#[test]
fn a_sentry_binding_for_a_battery_not_in_the_scene_creates_none() {
    let mut sim = Sim::new(Arc::new(Universe::with_systems([(*system()).clone()])));
    sim.handle(enter(SESSION, STATION, SYSTEM), NOON).unwrap();
    let at = sim.scenes[&SceneKey::solar_system(SYSTEM)].pilots[&SESSION].ship.position;
    let placement = BatteryPlacement {
        battery_id: BATTERY,
        solar_system_id: SYSTEM,
        type_id: 16_631,
        group_id: 1_000,
        display_name: "Test battery".to_string(),
        holder_session: 0,
        position: [at.x, at.y, at.z],
        radius: 60.0,
        scan_resolution: 400.0,
        max_target_range: 50_000.0,
        max_locked_targets: 2,
        online: true,
    };
    sim.handle(Message::PlaceBattery(Box::new(placement)), NOON).unwrap();
    let told = sim
        .handle(
            Message::SentryBattery { battery_id: BATTERY + 1, solar_system_id: SYSTEM, tower_id: TOWER },
            NOON,
        )
        .unwrap();
    assert!(told.is_empty(), "{told:?}");
    let scene = &sim.scenes[&SceneKey::solar_system(SYSTEM)];
    assert!(!scene.batteries.contains_key(&(BATTERY + 1)), "a sentry binding created a battery");
    assert!(scene.batteries.contains_key(&BATTERY), "the battery in the scene was lost");
}
