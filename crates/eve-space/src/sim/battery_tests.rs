use super::*;
use crate::link::BatteryPlacement;

const BATTERY: i64 = 1_050_000_001;

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
        name: "Test battery".to_string(),
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
        name: String::new(),
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
