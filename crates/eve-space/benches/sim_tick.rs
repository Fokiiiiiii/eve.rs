use std::hint::black_box;
use std::sync::Arc;
use std::time::{Duration, Instant};

use eve_space::health::{Condition, Resonances, Vitals};
use eve_space::link::{Enter, How, Message, SceneKey};
use eve_space::ship::{ModuleSpec, ShipSpec};
use eve_space::sim::{Sim, Universe};
use eve_space::world::SolarSystem;

const SYSTEM_ID: i32 = 30_000_001;
const NOON: i64 = 134_351_688_000_000_000;
const POLL_STEP: i64 = 2_500_000;
const TICKS_PER_SAMPLE: usize = 1_000;
const WARMUP_TICKS: usize = 1_000;
const SAMPLES: usize = 5;

fn simulation(pilots: usize) -> Sim {
    let system = SolarSystem {
        id: SYSTEM_ID,
        name: "Tick benchmark".to_string(),
        constellation_id: 20_000_001,
        region_id: 10_000_001,
        faction_id: None,
        security: 0.9,
        bodies: Vec::new(),
    };
    let universe = Arc::new(Universe::with_systems([system]));
    let mut sim = Sim::new(universe);
    let scene = SceneKey::solar_system(SYSTEM_ID);
    for ordinal in 0..pilots {
        let entity_id = 9_000_000_000i64 + ordinal as i64 * 10;
        let ship = ShipSpec {
            stance: None,
            ship_id: entity_id,
            character_id: 140_000_000 + ordinal as i64,
            type_id: 601,
            group_id: 237,
            category_id: 6,
            name: "Ibis".to_string(),
            owner_id: 140_000_000 + ordinal as i64,
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
            sensor_strength: [0.0; 4],
            ecm_resistance: 1.0,
            energy_warfare_resistance: 1.0,
            drone_damage_multiplier: 1.0,
            vitals: Vitals {
                shield_capacity: 220.0,
                armor_hp: 137.5,
                structure_hp: 201.25,
                resonances: Resonances {
                    shield: [0.0, 0.8, 0.6, 0.5],
                    armor: [0.5, 0.35, 0.25, 0.1],
                    structure: [0.33; 4],
                },
                armor_pinned: false,
            },
            condition: Condition::FULL,
            modules: vec![ModuleSpec { item_id: entity_id + 1, type_id: 21_857, flag: 19 }],
        };
        sim.handle(
            Message::Enter(Box::new(Enter {
                session_id: 1_000 + ordinal as u64,
                scene,
                how: How::InSpace,
                station_id: 0,
                ship,
                position: Some([ordinal as f64 * 5_000.0, 0.0, 0.0]),
                direction: Some([0.0, 0.0, 1.0]),
            })),
            NOON,
        )
        .expect("benchmark pilot enters the simulation");
    }
    sim
}

fn sample(sim: &mut Sim, tick: &mut usize) -> Duration {
    let started = Instant::now();
    for _ in 0..TICKS_PER_SAMPLE {
        *tick += 1;
        let messages = sim.poll(NOON + *tick as i64 * POLL_STEP);
        black_box(messages.len());
    }
    started.elapsed()
}

fn median_ns(samples: &mut [u128]) -> u128 {
    samples.sort_unstable();
    samples[samples.len() / 2]
}

fn main() {
    for pilots in [1, 16, 64] {
        let mut sim = simulation(pilots);
        let mut tick = 0;
        for _ in 0..WARMUP_TICKS {
            tick += 1;
            black_box(sim.poll(NOON + tick as i64 * POLL_STEP).len());
        }
        let mut samples = Vec::with_capacity(SAMPLES);
        for _ in 0..SAMPLES {
            samples.push(sample(&mut sim, &mut tick).as_nanos() / TICKS_PER_SAMPLE as u128);
        }
        println!(
            "path=Sim::poll pilots={pilots} ticks_per_sample={TICKS_PER_SAMPLE} samples={SAMPLES} median_ns_per_tick={}",
            median_ns(&mut samples),
        );
    }
}
