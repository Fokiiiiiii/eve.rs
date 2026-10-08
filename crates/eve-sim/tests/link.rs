//! The simulation process end to end over a stream: a host says hello, sends a ship into space, binds, docks it, and hears
//! that it has docked.

use std::sync::Arc;
use std::sync::atomic::{AtomicI64, Ordering};
use std::time::Duration;

use eve_destiny::Vec3;
use eve_sim::serve_connection;
use eve_space::link::{Enter, How, Message, PROTOCOL_VERSION};
use eve_space::ship::ShipSpec;
use eve_space::sim::{POLL_HZ, Universe};
use eve_space::world::{Body, BodyKind, Detail, SolarSystem};
use tokio::io::{AsyncReadExt, AsyncWriteExt, DuplexStream};

const SYSTEM: i32 = 30_000_001;
const STATION: i64 = 60_000_001;
const SESSION: u64 = 7;
const NOON: i64 = 134_351_688_000_000_000;
const SECOND: i64 = 10_000_000;

fn universe() -> Arc<Universe> {
    let station = Body {
        id: STATION,
        type_id: 1531,
        group_id: 15,
        category_id: 3,
        owner_id: 1_000_001,
        name: "Testing I - Trade Hub".to_string(),
        kind: BodyKind::Station,
        position: Vec3::new(1_000_000.0, 0.0, 0.0),
        // big, so a ship leaving it is within docking range at once (as in the reference)
        radius: 70_000.0,
        graphic_id: None,
        detail: Detail::Station { corporation_id: 1_000_001 },
    };
    let system = SolarSystem {
        id: SYSTEM,
        name: "Testing".to_string(),
        constellation_id: 20_000_001,
        region_id: 10_000_001,
        faction_id: None,
        security: 0.9,
        bodies: vec![station],
    };
    Arc::new(Universe::with_systems([system]))
}

fn ship() -> ShipSpec {
    ShipSpec {
        ship_id: 9_988_400_001_939,
        character_id: 140_000_342,
        type_id: 601,
        group_id: 237,
        category_id: 6,
        name: "Ibis".to_string(),
        owner_id: 140_000_342,
        corporation_id: 1_000_006,
        alliance_id: 0,
        war_faction_id: 0,
        security_status: 0.0,
        radius: 47.0,
        mass: 1_163_000.0,
        max_velocity: 339.25,
        agility: 4.0185,
        warp_speed_au: 3.0,
        shield_recharge_ms: 860_000.0,
        max_target_range: 29_700.0,
        max_locked_targets: 4,
        scan_resolution: 660.0,
        signature_radius: 28.0,
        capacitor_capacity: 149.5,
        capacitor_recharge_s: 55.25,
        sensor_strength: [0.0, 0.0, 0.0, 0.0],
        ecm_resistance: 1.0,
        energy_warfare_resistance: 1.0,
        vitals: eve_space::health::Vitals {
            shield_capacity: 220.0,
            armor_hp: 137.5,
            structure_hp: 201.25,
            resonances: eve_space::health::Resonances {
                shield: [0.0, 0.8, 0.6, 0.5],
                armor: [0.5, 0.35, 0.25, 0.1],
                structure: [0.33, 0.33, 0.33, 0.33],
            },
            armor_pinned: false,
        },
        condition: eve_space::health::Condition::FULL,
        modules: Vec::new(),
    }
}

/// The host's end of the link, and the simulation's clock.
struct Host {
    stream: DuplexStream,
    clock: Arc<AtomicI64>,
    served: tokio::task::JoinHandle<std::io::Result<()>>,
}

fn start() -> Host {
    let (near, far) = tokio::io::duplex(1 << 16);
    let clock = Arc::new(AtomicI64::new(NOON));
    let seen = Arc::clone(&clock);
    let served = tokio::spawn(serve_connection(far, universe(), move || seen.load(Ordering::SeqCst)));
    Host { stream: near, clock, served }
}

impl Host {
    async fn send(&mut self, message: Message) {
        self.stream.write_all(&message.encode()).await.unwrap();
    }

    async fn recv(&mut self) -> Message {
        let mut length = [0u8; 4];
        tokio::time::timeout(Duration::from_secs(2), self.stream.read_exact(&mut length))
            .await
            .expect("a reply in time")
            .unwrap();
        let mut payload = vec![0u8; u32::from_le_bytes(length) as usize];
        self.stream.read_exact(&mut payload).await.unwrap();
        Message::decode(&payload).unwrap()
    }

    async fn hello(&mut self) {
        self.send(Message::Hello { version: PROTOCOL_VERSION }).await;
        assert_eq!(self.recv().await, Message::HelloAck { version: PROTOCOL_VERSION, poll_hz: POLL_HZ });
    }
}

fn enter() -> Message {
    Message::Enter(Box::new(Enter {
        session_id: SESSION,
        solar_system_id: SYSTEM,
        how: How::Undock,
        station_id: STATION,
        ship: ship(),
        // only an entry in space carries a pose (the codec writes none for an undock)
        position: None,
        direction: None,
    }))
}

#[tokio::test]
async fn a_ship_is_sent_out_bound_told_to_dock_and_reported_docked() {
    let mut host = start();
    host.hello().await;

    host.send(enter()).await;
    assert_eq!(host.recv().await, Message::EnterReply { session_id: SESSION, outcome: Ok(()) });

    host.send(Message::Bind { session_id: SESSION }).await;
    let mut names = Vec::new();
    for _ in 0..6 {
        let Message::Notify { name, id_type, session_id, .. } = host.recv().await else {
            panic!("a notification")
        };
        assert_eq!((id_type.as_str(), session_id), ("clientID", SESSION));
        names.push(name);
    }
    assert_eq!(names[..2], ["OnSetTimeDilation", "DoSimClockRebase"]);
    assert!(names[2..].iter().all(|n| n == "DoDestinyUpdate"));

    host.send(Message::Command { session_id: SESSION, command: 4, args: vec![STATION as f64] }).await;
    assert!(
        matches!(host.recv().await, Message::Notify { name, id_type, .. } if name == "OnDockingAccepted" && id_type == "charid")
    );
    assert!(matches!(host.recv().await, Message::Notify { name, .. } if name == "DoDestinyUpdate"));
    assert_eq!(host.recv().await, Message::Accepted { session_id: SESSION, command: 4, at: NOON });

    // the ship is docked once four seconds have gone by, which the simulation notices at its next poll
    host.clock.store(NOON + 5 * SECOND, Ordering::SeqCst);
    assert_eq!(
        host.recv().await,
        Message::Docked {
            session_id: SESSION,
            station_id: STATION,
            condition: eve_space::health::Condition::FULL
        }
    );

    drop(host.stream);
    assert!(host.served.await.unwrap().is_ok(), "a host that closes is a clean end");
}

#[tokio::test]
async fn a_refused_command_comes_back_as_a_refusal() {
    let mut host = start();
    host.hello().await;
    host.send(Message::Command { session_id: 99, command: 3, args: vec![] }).await;
    assert_eq!(
        host.recv().await,
        Message::Refused { session_id: 99, command: 3, reason: "NO_SHIP_IN_SPACE".to_string() }
    );
}

#[tokio::test]
async fn a_host_that_speaks_another_version_or_skips_hello_is_turned_away() {
    let mut host = start();
    host.send(Message::Hello { version: PROTOCOL_VERSION + 1 }).await;
    let error = host.served.await.unwrap().unwrap_err();
    assert!(error.to_string().contains("version"), "{error}");

    let mut host = start();
    host.send(Message::Bind { session_id: 1 }).await;
    assert!(host.served.await.unwrap().is_err());
}

#[tokio::test]
async fn a_frame_that_is_not_a_message_ends_the_connection() {
    let mut host = start();
    host.hello().await;
    host.stream.write_all(&[1, 0, 0, 0, 0xff]).await.unwrap();
    assert!(host.served.await.unwrap().is_err());
}

#[tokio::test]
async fn a_frame_beyond_the_limit_ends_the_connection() {
    let mut host = start();
    host.hello().await;
    host.stream.write_all(&(2u32 << 20).to_le_bytes()).await.unwrap();
    let error = host.served.await.unwrap().unwrap_err();
    assert!(error.to_string().contains("exceeds"), "{error}");
}
