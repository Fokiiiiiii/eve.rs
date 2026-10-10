//! Throughput benchmarks in the same shape as `tools/bench/run.js`, so a Rust
//! result can be set beside the JS baseline recorded on the same machine.
//!
//!   cargo run --release -p eve-bench                     run and print
//!   cargo run --release -p eve-bench -- --compare        ... with ratios against oracle/bench/baseline.json
//!   cargo run --release -p eve-bench -- --filter decode  only benchmarks whose name contains "decode"
//!
//! Before timing a marshal workload the benchmark checks that the Rust encoder
//! produces the exact bytes (by FNV-1a hash) the JS benchmark recorded, so both
//! sides measure the same payload.

use std::hint::black_box;
use std::path::PathBuf;
use std::time::{Duration, Instant};

use eve_destiny::ball::{BallEntity, IdRef, Kind, LegacyMode, encode_entity_ball};
use eve_destiny::{Vec3, integrate_carbon_motion};
use eve_marshal::{Bytes, Descriptor, ObjectExKind, PackedRow, SubStream, Value, decode_bytes, encode};
use eve_oracle::marshal_adapter::plain_number;
use eve_space::scene::{Command, Scene};
use eve_space::ship::ShipSpec;
use eve_space::world::{Body, BodyKind, Detail, SolarSystem};
use serde_json::Value as Json;

#[cfg(feature = "mimalloc")]
#[global_allocator]
static ALLOCATOR: mimalloc::MiMalloc = mimalloc::MiMalloc;

const WARMUP: Duration = Duration::from_millis(300);
const BATCH: Duration = Duration::from_millis(250);
const BATCHES: usize = 9;

fn fnv1a64(bytes: &[u8]) -> String {
    let mut hash: u64 = 0xcbf2_9ce4_8422_2325;
    for &byte in bytes {
        hash ^= u64::from(byte);
        hash = hash.wrapping_mul(0x0000_0100_0000_01b3);
    }
    format!("{hash:016x}")
}

// ─── Workloads (kept identical to tools/bench/run.js) ───────────────────────

fn wstring(text: &'static str) -> Value {
    Value::WString(text.into())
}

fn small_call() -> Value {
    Value::Tuple(vec![
        Value::Int(0),
        Value::Tuple(vec![Value::Int(1), Value::Int(26000), Value::Long(1)]),
        Value::Tuple(vec![Value::Int(2), Value::Long(90_000_001), wstring("machoNet")]),
        Value::SubStream(SubStream::Decoded(Box::new(Value::Tuple(vec![
            Value::Tuple(vec![wstring("GetInitVals")]),
            Value::dict([("machoVersion", Value::Int(496)), ("ratio", Value::Real(0.25))]),
        ])))),
    ])
}

fn dict_200() -> Value {
    let entries = (0..200)
        .map(|index| {
            let value = match index % 3 {
                0 => Value::Int(index * 7),
                // JS numbers: an integer-valued product is encoded as an integer.
                1 => plain_number(index as f64 * 0.37).expect("finite"),
                _ => Value::string(format!("value-{index}")),
            };
            (Value::string(format!("attribute{index}")), value)
        })
        .collect();
    Value::Dict(entries)
}

fn rowset(rows: i64) -> Value {
    let columns: [(&str, i64); 11] = [
        ("itemID", 0x14),
        ("typeID", 0x03),
        ("ownerID", 0x03),
        ("locationID", 0x14),
        ("flagID", 0x02),
        ("quantity", 0x03),
        ("groupID", 0x03),
        ("categoryID", 0x03),
        ("customInfo", 0x81),
        ("singleton", 0x0b),
        ("stacksize", 0x03),
    ];
    let column_tuples: Vec<Value> =
        columns.iter().map(|(name, code)| Value::Tuple(vec![Value::str(name), Value::Int(*code)])).collect();
    let header = Value::ObjectEx {
        kind: ObjectExKind::Ex1,
        header: Box::new(Value::Tuple(vec![
            Value::Token("blue.DBRowDescriptor".into()),
            Value::Tuple(vec![Value::Tuple(column_tuples)]),
        ])),
        list: Vec::new(),
        dict: Vec::new(),
    };
    let descriptor = Descriptor::from_header(header).expect("valid descriptor");
    let items = (0..rows)
        .map(|index| {
            let values = vec![
                Value::Long(1_000_000_000 + index),
                Value::Int(587 + index % 50),
                Value::Int(90_000_001),
                Value::Long(60_003_760),
                Value::Int(4),
                Value::Int(1 + index % 7),
                Value::Int(25),
                Value::Int(6),
                Value::str(""),
                Value::Bool(index % 5 == 0),
                Value::Int(1 + index % 7),
            ];
            Value::PackedRow(Box::new(PackedRow::new(descriptor.clone(), values)))
        })
        .collect();
    Value::List(items)
}

fn buffers() -> Value {
    Value::Tuple((0..64).map(|index| Value::Buffer(vec![index as u8; 4096].into())).collect())
}

/// The 500-ship scene of `tools/bench/run.js`'s `makeShip`.
fn ships() -> Vec<BallEntity> {
    (0..500)
        .map(|index| BallEntity {
            item_id: IdRef::Valid(140_000_000 + index),
            kind: Kind::Ship,
            legacy_mode: LegacyMode::Goto,
            radius: 30.5,
            mass: Some(1_200_000.0),
            inertia: 3.2,
            max_velocity: 350.5,
            speed_fraction: 0.75,
            position: Vec3::new(index as f64 * 10.5, -2000.25, 3000.0),
            velocity: Vec3::new(10.0, 0.0, -5.0),
            direction: Vec3::new(1.0, 0.0, 0.0),
            target_point: Some(Vec3::new(5000.0, 0.0, 0.0)),
            corporation_id: IdRef::Valid(98_000_001),
            alliance_id: IdRef::Valid(0),
            has_session: true,
            pilot_positive: true,
            owner_positive: true,
            mode_owner: IdRef::Valid(90_000_001),
            ..BallEntity::default()
        })
        .collect()
}

fn check(name: &str, actual: &[u8], baseline: Option<&Json>) {
    match baseline.and_then(|doc| doc["results"][name]["encodedFnv1a64"].as_str()) {
        Some(expected) => {
            assert_eq!(fnv1a64(actual), expected, "{name}: Rust output differs from the JS benchmark's")
        }
        None => eprintln!("note: no recorded checksum for {name}; workload parity not verified"),
    }
}

// ─── The scene (no counterpart in the JS baseline: it needs the whole service graph and the generated tables) ───

const TICKS_PER_SECOND: i64 = 10_000_000;
/// 2026-09-29 12:00:00 UTC as a FILETIME.
const NOON: i64 = 134_351_688_000_000_000;

fn bench_system() -> std::sync::Arc<SolarSystem> {
    let station = Body {
        id: 60_000_001,
        type_id: 1531,
        group_id: 15,
        category_id: 3,
        owner_id: 1_000_001,
        name: "Bench I - Trade Hub".to_string(),
        kind: BodyKind::Station,
        position: Vec3::new(1_000_000.0, 0.0, 0.0),
        radius: 1_000.0,
        graphic_id: None,
        detail: Detail::Station { corporation_id: 1_000_001 },
    };
    std::sync::Arc::new(SolarSystem {
        id: 30_000_001,
        name: "Bench".to_string(),
        constellation_id: 20_000_001,
        region_id: 10_000_001,
        faction_id: None,
        security: 0.9,
        bodies: vec![station],
    })
}

fn bench_ship(index: u64) -> ShipSpec {
    ShipSpec { stance: None,
        ship_id: 9_988_400_000_000 + index as i64,
        character_id: 140_000_000 + index as i64,
        type_id: 601,
        group_id: 237,
        category_id: 6,
        name: "Ibis".to_string(),
        owner_id: 140_000_000 + index as i64,
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
        drone_damage_multiplier: 1.0,
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

/// A scene with `ships` ships that came out of the station, have bound the ballpark and see one another.
fn crowded(ships: u64) -> Scene {
    let mut scene = Scene::new(bench_system());
    for index in 1..=ships {
        let at = NOON + index as i64 * TICKS_PER_SECOND / 10;
        scene.undock(index, bench_ship(index), 60_000_001, at).expect("the bench station is there");
        scene.bind(index, at);
    }
    scene
}

fn scene_benches(benches: &mut Vec<Bench>) {
    for ships in [1u64, 10, 100, 500] {
        // one poll of the scene a quarter of a second after the last: every ship's motion brought on, and what each sees checked
        let mut scene: Option<Scene> = None;
        let mut now = NOON + 3_600 * TICKS_PER_SECOND;
        benches.push(Bench {
            name: format!("scene.poll.{ships}-ships"),
            bytes_per_op: None,
            ops_per_run: 1,
            run: Box::new(move || {
                let scene = scene.get_or_insert_with(|| crowded(ships));
                now += TICKS_PER_SECOND / 4;
                black_box(scene.poll(black_box(now)));
            }),
        });
        // one order given to a ship that every other ship has in view: the update is told to all of them
        let mut scene: Option<Scene> = None;
        let mut now = NOON + 3_600 * TICKS_PER_SECOND;
        let mut fraction = 0.5;
        benches.push(Bench {
            name: format!("scene.command.told-to-{ships}"),
            bytes_per_op: None,
            ops_per_run: 1,
            run: Box::new(move || {
                let scene = scene.get_or_insert_with(|| crowded(ships));
                now += TICKS_PER_SECOND / 4;
                fraction = if fraction > 0.75 { 0.5 } else { 1.0 };
                black_box(scene.command(1, Command::SetSpeedFraction(black_box(fraction)), now));
            }),
        });
    }
}

struct Bench {
    name: String,
    bytes_per_op: Option<usize>,
    /// How many operations one call of `run` performs (default 1).
    ops_per_run: usize,
    run: Box<dyn FnMut()>,
}

fn build(baseline: Option<&Json>) -> Vec<Bench> {
    let payloads: [(&str, Value); 4] = [
        ("small-call", small_call()),
        ("dict-200", dict_200()),
        ("rowset-1000", rowset(1000)),
        ("buffers-64x4k", buffers()),
    ];

    let mut benches = Vec::new();
    for (name, value) in payloads {
        let encoded = encode(&value).expect("payload encodes");

        let recorded = baseline
            .and_then(|doc| doc["results"][format!("marshal.encode.{name}")]["encodedFnv1a64"].as_str());
        match recorded {
            Some(expected) => assert_eq!(
                fnv1a64(&encoded),
                expected,
                "payload {name}: the Rust encoder does not reproduce the bytes the JS benchmark recorded"
            ),
            None => eprintln!("note: no recorded checksum for {name}; workload parity not verified"),
        }

        let for_encode = value.clone();
        benches.push(Bench {
            name: format!("marshal.encode.{name}"),
            bytes_per_op: Some(encoded.len()),
            ops_per_run: 1,
            run: Box::new(move || {
                black_box(encode(black_box(&for_encode)).unwrap());
            }),
        });
        let length = encoded.len();
        // The input as a socket read would hand it over: already a `Bytes`.
        let input = Bytes::from(encoded);
        benches.push(Bench {
            name: format!("marshal.decode.{name}"),
            bytes_per_op: Some(length),
            ops_per_run: 1,
            run: Box::new(move || {
                black_box(decode_bytes(black_box(&input)).unwrap());
            }),
        });
    }

    // Destiny kernels.
    let (position, velocity, acceleration) =
        (Vec3::new(1234.5, -987.25, 42.0), Vec3::new(120.5, 3.25, -60.0), Vec3::new(10.0, 0.0, -2.0));
    let step = integrate_carbon_motion(position, velocity, acceleration, 1_200_000.0, 1_000_000.0, 0.1);
    let bits: Vec<u8> = [step.position, step.velocity]
        .iter()
        .flat_map(|vector| [vector.x, vector.y, vector.z])
        .flat_map(f64::to_le_bytes)
        .collect();
    check("destiny.carbon.integrate", &bits, baseline);
    benches.push(Bench {
        name: "destiny.carbon.integrate".into(),
        bytes_per_op: None,
        ops_per_run: 1,
        run: Box::new(move || {
            black_box(integrate_carbon_motion(
                black_box(position),
                black_box(velocity),
                black_box(acceleration),
                1_200_000.0,
                1_000_000.0,
                0.1,
            ));
        }),
    });

    let scene = ships();
    let encoded: Vec<u8> =
        scene.iter().flat_map(|ship| encode_entity_ball(ship).expect("ship encodes")).collect();
    check("destiny.ball.encode.500-ships", &encoded, baseline);
    benches.push(Bench {
        name: "destiny.ball.encode.500-ships".into(),
        bytes_per_op: None,
        ops_per_run: scene.len(),
        run: Box::new(move || {
            for ship in &scene {
                black_box(encode_entity_ball(black_box(ship)).unwrap());
            }
        }),
    });
    scene_benches(&mut benches);
    benches
}

fn median(values: &mut [f64]) -> f64 {
    values.sort_by(|a, b| a.partial_cmp(b).expect("no NaN"));
    values[values.len() / 2]
}

fn measure(bench: &mut Bench) -> f64 {
    let warmup_end = Instant::now() + WARMUP;
    while Instant::now() < warmup_end {
        (bench.run)();
    }
    let mut rates = Vec::with_capacity(BATCHES);
    for _ in 0..BATCHES {
        let start = Instant::now();
        let mut runs = 0u64;
        let mut now = start;
        while now.duration_since(start) < BATCH {
            (bench.run)();
            runs += 1;
            now = Instant::now();
        }
        rates.push((runs * bench.ops_per_run as u64) as f64 / now.duration_since(start).as_secs_f64());
    }
    median(&mut rates)
}

fn thousands(value: f64) -> String {
    let digits = format!("{}", value.round() as u64);
    let mut out = String::new();
    for (index, digit) in digits.chars().enumerate() {
        if index > 0 && (digits.len() - index) % 3 == 0 {
            out.push(',');
        }
        out.push(digit);
    }
    out
}

fn main() {
    let args: Vec<String> = std::env::args().skip(1).collect();
    let filter = args
        .iter()
        .position(|arg| arg == "--filter")
        .and_then(|at| args.get(at + 1))
        .cloned()
        .unwrap_or_default();
    let baseline_path = PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../../oracle/bench/baseline.json");
    let baseline: Option<Json> =
        std::fs::read_to_string(&baseline_path).ok().and_then(|text| serde_json::from_str(&text).ok());
    let compare = args.iter().any(|arg| arg == "--compare");
    if compare && baseline.is_none() {
        eprintln!("--compare needs {}", baseline_path.display());
        std::process::exit(2);
    }

    println!(
        "(rust, release profile, {} allocator; ratios are Rust / JS baseline)",
        if cfg!(feature = "mimalloc") { "mimalloc" } else { "system" }
    );
    for mut bench in build(baseline.as_ref()) {
        if !bench.name.contains(&filter) {
            continue;
        }
        let rate = measure(&mut bench);
        let throughput = bench
            .bytes_per_op
            .map(|bytes| format!(" ({:.1} MB/s)", rate * bytes as f64 / 1e6))
            .unwrap_or_default();
        let mut line = format!("{:<40} {} ops/s{throughput}", bench.name, thousands(rate));
        if let Some(before) =
            baseline.as_ref().and_then(|doc| doc["results"][&bench.name]["opsPerSec"].as_f64())
        {
            line.push_str(&format!("   x{:.2} vs JS", rate / before));
        }
        println!("{line}");
    }
}
