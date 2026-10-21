//! numbers of the sentries and the hulls (the SDE, build 3396210, the values below); it does not give where they stand, how often the factions fight or
//! how many of them there are. Those are the rules of this module, stated here and in DEVIATIONS:
//! * a sentry at every stargate of the system, 20 km from the gate towards the system's centre: an Entropic Disintegrator Werpost of the Triglavians,
//!   or an EDENCOM GunStar where EDENCOM holds the system (`data/pochven_states.json`); a sentry does not move;
//! * a camp of two Liminal Damavik at the first two gates of a system the Triglavians hold;
//! * a skirmish every [`SKIRMISH_INTERVAL_SECONDS`]: two groups of two hulls, of two of the four factions, 40 km apart, which attack each other;
//! * caps: at most [`MAX_POCHVEN_NPCS`] Pochven NPCs in a scene; a skirmish waits until it fits.
//!
//! The shot of a turret is the SDE's damage times the hull's damage multiplier, once every [`SHOT_CYCLE_MS`]: the SDE gives no cycle for these types, so the
//! cycle is the rule of this module.
use std::collections::HashMap;
use std::sync::OnceLock;

use eve_destiny::Vec3;

use crate::health::{Condition, Resonances, Vitals};
use crate::npc::NpcPlace;
use crate::pochven;
use crate::scene::{Out, Scene, TICKS_PER_SECOND};
use crate::ship::{NpcCombat, NpcIdentity, NpcWeapon, ShipSpec};
use crate::weapons::Turret;
use crate::world::BodyKind;

/// How often a skirmish starts in a scene, seconds (the rule of this module).
pub const SKIRMISH_INTERVAL_SECONDS: i64 = 900;
/// The most Pochven NPCs a scene holds (the cap of the server's load).
pub const MAX_POCHVEN_NPCS: usize = 40;
/// The NPCs of one skirmish: two groups of two.
const SKIRMISH_SIZE: usize = 4;
/// The distance from a gate at which its sentry stands, metres.
const SENTRY_OFFSET_METRES: f64 = 20_000.0;
/// The distance between the two groups of a skirmish, metres.
const SKIRMISH_GAP_METRES: f64 = 40_000.0;
/// The time a turret takes to fire once, ms (the rule of this module).
const SHOT_CYCLE_MS: f64 = 4_000.0;
/// The effect a turret's shot runs (`crate::brains`: the effect of the belt rat's weapon).
const TURRET_EFFECT: i32 = 10;
/// The fitting of the turret's effect, as the belt rat's: the name the client shows.
const TURRET_FX: &str = "effects.Laser";

/// What a Pochven NPC type is: the SDE's numbers for it (build 3396210, `typeDogma`, `types`, `groups`). Resonances are the SDE's; damage is the raw
/// turret damage with the hull's damage multiplier applied (em, thermal, kinetic, explosive).
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct NpcType {
    pub type_id: i32,
    pub group_id: i32,
    pub name: &'static str,
    pub corporation_id: i64,
    pub radius: f64,
    pub mass: f64,
    pub max_velocity: f64,
    pub agility: f64,
    pub max_target_range: f64,
    pub max_locked: i32,
    pub scan_resolution: f64,
    pub signature_radius: f64,
    pub capacitor_capacity: f64,
    pub capacitor_recharge_s: f64,
    pub shield_recharge_ms: f64,
    pub shield: f64,
    pub armor: f64,
    pub structure: f64,
    pub shield_resonance: [f64; 4],
    pub armor_resonance: [f64; 4],
    pub structure_resonance: [f64; 4],
    pub weapon_optimal: f64,
    pub weapon_falloff: f64,
    pub tracking: f64,
    pub shot: [f64; 4],
}

const TRIGLAVIAN: i64 = pochven::TRIGLAVIAN_CORPORATION_ID;
const EDENCOM: i64 = pochven::EDENCOM_CORPORATION_ID;
const ROGUE_DRONE: i64 = pochven::ROGUE_DRONE_CORPORATION_ID;
const DRIFTER: i64 = pochven::DRIFTER_CORPORATION_ID;

/// Liminal Damavik (52182), Triglavian Entities: the camp hull.
pub const DAMAVIK: NpcType = NpcType {
    type_id: 52182,
    group_id: 4028,
    name: "Liminal Damavik",
    corporation_id: TRIGLAVIAN,
    radius: 34.0,
    mass: 950_000.0,
    max_velocity: 3600.0,
    agility: 1.0,
    max_target_range: 75_000.0,
    max_locked: 5,
    scan_resolution: 800.0,
    signature_radius: 50.0,
    capacitor_capacity: 575.0,
    capacitor_recharge_s: 150.0,
    shield_recharge_ms: 400_000.0,
    shield: 650.0,
    armor: 2800.0,
    structure: 900.0,
    shield_resonance: [1.0, 0.8, 0.6, 0.5],
    armor_resonance: [0.49, 0.6, 0.51, 0.64],
    structure_resonance: [0.67; 4],
    weapon_optimal: 28_750.0,
    weapon_falloff: 0.0,
    tracking: 342.0,
    shot: [0.0, 54.9, 0.0, 29.7],
};

/// Imperial Navy Sentinel (52923), EDENCOM Entities: the EDENCOM hull of a skirmish.
pub const SENTINEL: NpcType = NpcType {
    type_id: 52923,
    group_id: 4034,
    name: "Imperial Navy Sentinel",
    corporation_id: EDENCOM,
    radius: 39.0,
    mass: 1_223_200.0,
    max_velocity: 2656.25,
    agility: 4.25,
    max_target_range: 35_000.0,
    max_locked: 7,
    scan_resolution: 575.0,
    signature_radius: 41.0,
    capacitor_capacity: 518.75,
    capacitor_recharge_s: 190.0,
    shield_recharge_ms: 625_000.0,
    shield: 300.0,
    armor: 1210.0,
    structure: 375.0,
    shield_resonance: [1.0, 0.8, 0.45, 0.25],
    armor_resonance: [0.5, 0.65, 0.56, 0.4],
    structure_resonance: [0.67; 4],
    weapon_optimal: 25_000.0,
    weapon_falloff: 6_250.0,
    tracking: 380.0,
    shot: [97.2, 69.435, 0.0, 0.0],
};

pub const LEGIONARY: NpcType = NpcType {
    type_id: 56493,
    group_id: 4037,
    name: "Interfering Phase-II Legionary",
    corporation_id: ROGUE_DRONE,
    radius: 20.0,
    mass: 6_666.67,
    max_velocity: 2000.0,
    agility: 2.0,
    max_target_range: 30_000.0,
    max_locked: 8,
    scan_resolution: 2000.0,
    signature_radius: 33.33,
    capacitor_capacity: 853.33,
    capacitor_recharge_s: 2500.0,
    shield_recharge_ms: 900_000_000_000.0,
    shield: 133.33,
    armor: 333.33,
    structure: 200.0,
    shield_resonance: [0.4, 0.3, 0.3, 0.4],
    armor_resonance: [0.4, 0.3, 0.3, 0.4],
    structure_resonance: [1.0; 4],
    weapon_optimal: 1_333.33,
    weapon_falloff: 18_000.0,
    tracking: 320.0,
    shot: [8.333, 8.333, 8.333, 8.333],
};

/// Drifter Assault Cruiser (56221), Drifter Entities: the Drifter hull of a skirmish. The SDE gives no agility or structure resonance for it: agility
/// 1 and resonance 1 (the rule of this module).
pub const ASSAULT_CRUISER: NpcType = NpcType {
    type_id: 56221,
    group_id: 4035,
    name: "Drifter Assault Cruiser",
    corporation_id: DRIFTER,
    radius: 150.0,
    mass: 1_000_000.0,
    max_velocity: 2430.0,
    agility: 1.0,
    max_target_range: 75_000.0,
    max_locked: 4,
    scan_resolution: 330.0,
    signature_radius: 150.0,
    capacitor_capacity: 4500.0,
    capacitor_recharge_s: 500.0,
    shield_recharge_ms: 250_000.0,
    shield: 3000.0,
    armor: 12_000.0,
    structure: 6000.0,
    shield_resonance: [0.51, 0.8, 0.51, 0.51],
    armor_resonance: [1.0, 0.5, 0.5, 0.5],
    structure_resonance: [1.0; 4],
    weapon_optimal: 75_000.0,
    weapon_falloff: 75_000.0,
    tracking: 0.06,
    shot: [604.5, 604.5, 604.5, 604.5],
};

/// Entropic Disintegrator Werpost (54577), Destructible Sentry Gun: the Triglavian gate sentry. Its shot is 250 each times 0.8.
pub const WERPOST: NpcType = NpcType {
    type_id: 54577,
    group_id: 383,
    name: "Entropic Disintegrator Werpost",
    corporation_id: TRIGLAVIAN,
    radius: 1359.0,
    mass: 1000.0,
    max_velocity: 0.0,
    agility: 0.0,
    max_target_range: 250_000.0,
    max_locked: 8,
    scan_resolution: 105.0,
    signature_radius: 400.0,
    capacitor_capacity: 1000.0,
    capacitor_recharge_s: 500.0,
    shield_recharge_ms: 500_000.0,
    shield: 18_000.0,
    armor: 37_500.0,
    structure: 15_000.0,
    shield_resonance: [1.0, 0.8, 0.6, 0.4],
    armor_resonance: [0.4, 0.8, 0.6, 0.9],
    structure_resonance: [1.0; 4],
    weapon_optimal: 250_000.0,
    weapon_falloff: 50_000.0,
    tracking: 35.0,
    shot: [200.0, 200.0, 200.0, 200.0],
};

/// EDENCOM GunStar (54579), Destructible Sentry Gun: the EDENCOM gate sentry. Its shot is 25 each times 10.
pub const GUNSTAR: NpcType = NpcType {
    type_id: 54579,
    group_id: 383,
    name: "EDENCOM GunStar",
    corporation_id: EDENCOM,
    radius: 247.0,
    mass: 1000.0,
    max_velocity: 0.0,
    agility: 0.0,
    max_target_range: 250_000.0,
    max_locked: 8,
    scan_resolution: 105.0,
    signature_radius: 400.0,
    capacitor_capacity: 1000.0,
    capacitor_recharge_s: 500.0,
    shield_recharge_ms: 500_000.0,
    shield: 12_000.0,
    armor: 25_000.0,
    structure: 15_000.0,
    shield_resonance: [1.0, 0.8, 0.6, 0.4],
    armor_resonance: [0.4, 0.65, 0.75, 0.9],
    structure_resonance: [1.0; 4],
    weapon_optimal: 250_000.0,
    weapon_falloff: 50_000.0,
    tracking: 35.0,
    shot: [250.0, 250.0, 250.0, 250.0],
};

/// EDENCOM Heavy GunStar (54580), Destructible Sentry Gun: the sentry of a fortress. Its shot is 25 each times 20; it tracks slower.
pub const HEAVY_GUNSTAR: NpcType = NpcType {
    type_id: 54580,
    group_id: 383,
    name: "EDENCOM Heavy GunStar",
    corporation_id: EDENCOM,
    radius: 2222.0,
    mass: 1000.0,
    max_velocity: 0.0,
    agility: 0.0,
    max_target_range: 250_000.0,
    max_locked: 8,
    scan_resolution: 105.0,
    signature_radius: 400.0,
    capacitor_capacity: 1000.0,
    capacitor_recharge_s: 500.0,
    shield_recharge_ms: 500_000.0,
    shield: 18_000.0,
    armor: 37_500.0,
    structure: 15_000.0,
    shield_resonance: [1.0, 0.8, 0.6, 0.4],
    armor_resonance: [0.4, 0.65, 0.75, 0.9],
    structure_resonance: [1.0; 4],
    weapon_optimal: 250_000.0,
    weapon_falloff: 50_000.0,
    tracking: 15.0,
    shot: [500.0, 500.0, 500.0, 500.0],
};

/// The behavior names the Pochven NPCs are given (`Scene::spawn_npc_with_behavior`, the trees of `brains::rats`).
pub const CAMP: &str = "pochven camp";
pub const SENTRY_TRIGLAVIAN: &str = "pochven sentry triglavian";
pub const SENTRY_EDENCOM: &str = "pochven sentry edencom";
pub const CAMP_EDENCOM: &str = "pochven camp edencom";

#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct Owners {
    pub valid: Vec<i64>,
    pub invalid: Vec<i64>,
}

/// EDENCOM sentry attacks the other Pochven factions' NPCs and no pilot (EDENCOM rats are hostile below 0.00, and nothing here lowers a pilot's standing).
/// Outside Pochven nothing is attacked.
pub fn owners_for(behavior: &str, corporation_id: i64, in_zone: bool) -> Owners {
    match behavior {
        b if b == crate::brains::POCHVEN_NPC => {
            Owners { valid: pochven::hostile_corporations(corporation_id, in_zone), invalid: Vec::new() }
        }
        CAMP | SENTRY_TRIGLAVIAN if in_zone => Owners { valid: Vec::new(), invalid: vec![TRIGLAVIAN] },
        CAMP | SENTRY_TRIGLAVIAN => Owners { valid: vec![pochven::NO_OWNER], invalid: Vec::new() },
        SENTRY_EDENCOM | CAMP_EDENCOM => {
            Owners { valid: pochven::hostile_corporations(EDENCOM, in_zone), invalid: Vec::new() }
        }
        _ => Owners::default(),
    }
}

/// The zones (`data/pochven_states.json`): each system with a state ('triglavian' or 'edencom'). Pochven's 27 systems are among them.
fn states() -> &'static HashMap<i32, String> {
    static STATES: OnceLock<HashMap<i32, String>> = OnceLock::new();
    STATES.get_or_init(|| {
        let document: serde_json::Value = serde_json::from_str(include_str!("../data/pochven_states.json"))
            .expect("pochven_states.json is JSON");
        let systems = document["systems"].as_object().expect("pochven_states.json has systems");
        systems
            .iter()
            .map(|(id, state)| {
                (id.parse().expect("a system id"), state.as_str().unwrap_or("triglavian").to_string())
            })
            .collect()
    })
}

pub fn is_zone(system_id: i32) -> bool {
    states().contains_key(&system_id)
}

/// Whether the EDENCOM hold a zone (its state in the data file).
pub fn held_by_edencom(system_id: i32) -> bool {
    states().get(&system_id).is_some_and(|state| state == "edencom")
}

/// The Pochven population state of a scene.
#[derive(Debug, Default)]
pub(crate) struct Population {
    populated: bool,
    next_skirmish_at: i64,
    skirmishes: u64,
}

/// The NPC type of a Pochven hull by its corporation, for a skirmish.
fn hull_of(corporation_id: i64) -> NpcType {
    match corporation_id {
        TRIGLAVIAN => DAMAVIK,
        EDENCOM => SENTINEL,
        ROGUE_DRONE => LEGIONARY,
        _ => ASSAULT_CRUISER,
    }
}

/// The ship of a Pochven NPC type, as the scene's physics and the host's slim item need it (the fitting is the type's own: no modules).
fn npc_spec(ship_id: i64, npc_type: &NpcType) -> ShipSpec {
    ShipSpec {
        stance: None,
        ship_id,
        character_id: 0,
        type_id: npc_type.type_id,
        group_id: npc_type.group_id,
        category_id: 11,
        name: String::new(),
        owner_id: npc_type.corporation_id,
        corporation_id: npc_type.corporation_id as i32,
        alliance_id: 0,
        war_faction_id: 0,
        security_status: 0.0,
        radius: npc_type.radius,
        mass: npc_type.mass,
        max_velocity: npc_type.max_velocity,
        agility: npc_type.agility,
        warp_speed_au: 3.0,
        shield_recharge_ms: npc_type.shield_recharge_ms,
        max_target_range: npc_type.max_target_range,
        max_locked_targets: npc_type.max_locked,
        scan_resolution: npc_type.scan_resolution,
        signature_radius: npc_type.signature_radius,
        capacitor_capacity: npc_type.capacitor_capacity,
        capacitor_recharge_s: npc_type.capacitor_recharge_s,
        sensor_strength: [0.0; 4],
        ecm_resistance: 1.0,
        energy_warfare_resistance: 1.0,
        drone_damage_multiplier: 1.0,
        vitals: Vitals {
            shield_capacity: npc_type.shield,
            armor_hp: npc_type.armor,
            structure_hp: npc_type.structure,
            resonances: Resonances {
                shield: npc_type.shield_resonance,
                armor: npc_type.armor_resonance,
                structure: npc_type.structure_resonance,
            },
            armor_pinned: false,
        },
        condition: Condition::FULL,
        modules: Vec::new(),
    }
}

/// The identity of a Pochven NPC: its turret, fitted to its own ship (as the belt rat's), with the aggression range given.
fn npc_identity(ship_id: i64, npc_type: &NpcType, aggression_range: f64) -> NpcIdentity {
    NpcIdentity {
        combat: NpcCombat {
            weapons: vec![NpcWeapon {
                module_id: ship_id,
                effect_id: TURRET_EFFECT,
                cycle_ms: SHOT_CYCLE_MS,
                capacitor_need: 0.0,
                turret: Turret {
                    weapon_type_id: npc_type.type_id,
                    fx: TURRET_FX.to_string(),
                    raw_damage: npc_type.shot,
                    optimal_range: npc_type.weapon_optimal,
                    falloff: npc_type.weapon_falloff,
                    tracking_speed: npc_type.tracking,
                    signature_resolution: 40.0,
                    charge: None,
                    missile: None,
                    spool: None,
                    aoe: None,
                    arc: None,
                    requires_gunnery: true,
                },
            }],
            aggression_range,
        },
        ..NpcIdentity::default()
    }
}

/// The point `distance` metres from `from` towards the origin (the system's middle), or straight up when `from` is at the origin.
fn towards_centre(from: Vec3, distance: f64) -> Vec3 {
    let length = (from.x * from.x + from.y * from.y + from.z * from.z).sqrt();
    if length <= f64::EPSILON {
        return Vec3::new(from.x, from.y, from.z + distance);
    }
    let k = distance / length;
    Vec3::new(from.x - from.x * k, from.y - from.y * k, from.z - from.z * k)
}

impl Scene {
    /// The Pochven population: the first look at a Pochven system places its sentries and camps, and every look runs the skirmish schedule within the
    /// caps. A system outside Pochven is left alone.
    pub(crate) fn populate_pochven(&mut self, now: i64) -> Vec<Out> {
        if !is_zone(self.system().id) {
            return Vec::new();
        }
        let mut out = Vec::new();
        if !self.pochven.populated {
            self.pochven.populated = true;
            self.pochven.next_skirmish_at = now + SKIRMISH_INTERVAL_SECONDS * TICKS_PER_SECOND;
            out.extend(self.place_gate_defences(now));
        }
        let pochven_system = self.system().region_id == pochven::POCHVEN_REGION_ID;
        if pochven_system
            && now >= self.pochven.next_skirmish_at
            && self.pochven_npc_count() + SKIRMISH_SIZE <= MAX_POCHVEN_NPCS
        {
            self.pochven.next_skirmish_at = now + SKIRMISH_INTERVAL_SECONDS * TICKS_PER_SECOND;
            out.extend(self.spawn_skirmish(now));
        }
        out
    }

    /// How many NPCs of the Pochven factions the scene holds.
    fn pochven_npc_count(&self) -> usize {
        self.pilots
            .values()
            .filter(|pilot| {
                pilot.ship.npc.is_some()
                    && pochven::faction_of_corporation(pilot.ship.spec.owner_id).is_some()
            })
            .count()
    }

    /// A sentry at each stargate, and the camp of the Triglavian systems at their first two gates.
    fn place_gate_defences(&mut self, now: i64) -> Vec<Out> {
        let system_id = self.system().id;
        let edencom = held_by_edencom(system_id);
        let gates: Vec<Vec3> = self
            .system()
            .bodies
            .iter()
            .filter(|body| body.kind == BodyKind::Stargate)
            .map(|body| body.position)
            .collect();
        let mut out = Vec::new();
        for (index, gate) in gates.iter().enumerate() {
            let (sentry, behavior) =
                if edencom { (GUNSTAR, SENTRY_EDENCOM) } else { (WERPOST, SENTRY_TRIGLAVIAN) };
            let at = towards_centre(*gate, SENTRY_OFFSET_METRES);
            out.extend(self.spawn_pochven(&sentry, behavior, at, sentry.max_target_range, now));
            if index < 2 {
                let (camp, camp_behavior) = if edencom { (SENTINEL, CAMP_EDENCOM) } else { (DAMAVIK, CAMP) };
                let at = towards_centre(*gate, SENTRY_OFFSET_METRES / 4.0);
                out.extend(self.spawn_pochven(&camp, camp_behavior, at, camp.max_target_range, now));
            }
        }
        out
    }

    /// One skirmish: two groups of two hulls, of two of the four factions, that attack each other. The pair follows the skirmish's number.
    fn spawn_skirmish(&mut self, now: i64) -> Vec<Out> {
        const PAIRS: [(i64, i64); 4] =
            [(TRIGLAVIAN, EDENCOM), (ROGUE_DRONE, DRIFTER), (TRIGLAVIAN, ROGUE_DRONE), (EDENCOM, DRIFTER)];
        let number = self.pochven.skirmishes;
        self.pochven.skirmishes += 1;
        let (first, second) = PAIRS[(number % 4) as usize];
        let anchor = self
            .system()
            .bodies
            .iter()
            .find(|body| body.kind == BodyKind::Stargate)
            .map_or(Vec3::new(0.0, 0.0, 0.0), |body| body.position);
        let mut out = Vec::new();
        for (side, corporation) in [(first, -1.0), (second, 1.0)] {
            let hull = hull_of(side);
            for index in 0..2 {
                let along = f64::from(index) * 2_000.0;
                let at =
                    Vec3::new(anchor.x + corporation * SKIRMISH_GAP_METRES / 2.0, anchor.y + along, anchor.z);
                out.extend(self.spawn_pochven(
                    &hull,
                    crate::brains::POCHVEN_NPC,
                    [at.x, at.y, at.z],
                    hull.max_target_range,
                    now,
                ));
            }
        }
        out
    }

    fn spawn_pochven(
        &mut self,
        npc_type: &NpcType,
        behavior: &str,
        at: impl Into<PlaceAt>,
        aggression: f64,
        now: i64,
    ) -> Vec<Out> {
        let at = at.into();
        let ship_id = 980_100_000_000 + self.npc_sequence as i64;
        let place = NpcPlace::at_rest(Vec3::new(at.0, at.1, at.2));
        let mut identity = npc_identity(ship_id, npc_type, aggression);
        if matches!(behavior, SENTRY_TRIGLAVIAN | CAMP) {
            // A neutral pilot has 0.00 standing to the Triglavians and is hostile at that inclusive boundary.
            // The shared behavior task uses a strict comparison, so move its integer threshold just above zero.
            identity.hostile_response_threshold = 1;
        }
        match self.spawn_npc_with_behavior(npc_spec(ship_id, npc_type), identity, place, behavior, now) {
            Ok((_, mut outs)) => {
                outs.insert(
                    0,
                    self.publish_sim_damageable_entity(
                        ship_id,
                        npc_type.type_id,
                        npc_type.corporation_id,
                        npc_type.corporation_id,
                        0,
                        0,
                    ),
                );
                outs
            }
            Err(_) => Vec::new(),
        }
    }
}

/// A place given as its three coordinates, from a `Vec3` or an array.
pub(crate) struct PlaceAt(f64, f64, f64);

impl From<Vec3> for PlaceAt {
    fn from(v: Vec3) -> Self {
        PlaceAt(v.x, v.y, v.z)
    }
}

impl From<[f64; 3]> for PlaceAt {
    fn from(v: [f64; 3]) -> Self {
        PlaceAt(v[0], v[1], v[2])
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_data_file_names_the_27_pochven_systems_as_triglavian_and_arshat_as_edencom() {
        let mut ids: Vec<i32> = states().keys().copied().collect();
        ids.sort_unstable();
        assert_eq!(ids.len(), 28);
        assert!(ids.contains(&30_000_021) && ids.contains(&30_045_329) && ids.contains(&ARSHAT));
        assert_eq!(states().values().filter(|state| *state == "triglavian").count(), 27);
        assert!(!held_by_edencom(30_000_021));
        assert!(held_by_edencom(ARSHAT) && is_zone(ARSHAT));
        assert!(!is_zone(30_000_062), "Iosantin is a 2020 target, not a zone");
    }

    #[test]
    fn the_owners_of_each_behavior_follow_the_rules() {
        assert_eq!(
            owners_for(CAMP, TRIGLAVIAN, true),
            Owners { valid: Vec::new(), invalid: vec![TRIGLAVIAN] }
        );
        assert_eq!(owners_for(SENTRY_TRIGLAVIAN, TRIGLAVIAN, false).valid, vec![pochven::NO_OWNER]);
        assert_eq!(owners_for(SENTRY_EDENCOM, EDENCOM, true).valid, vec![DRIFTER, ROGUE_DRONE, TRIGLAVIAN]);
        assert!(owners_for(SENTRY_EDENCOM, EDENCOM, true).invalid.is_empty());
        assert_eq!(
            owners_for(crate::brains::POCHVEN_NPC, ROGUE_DRONE, true).valid,
            vec![DRIFTER, TRIGLAVIAN, EDENCOM]
        );
    }

    #[test]
    fn a_sentry_stands_towards_the_centre_at_the_offset_from_its_gate() {
        let gate = Vec3::new(3_000_000.0, 4_000_000.0, 0.0);
        let at = towards_centre(gate, 20_000.0);
        let distance = ((at.x - gate.x).powi(2) + (at.y - gate.y).powi(2) + (at.z - gate.z).powi(2)).sqrt();
        assert!((distance - 20_000.0).abs() < 1e-6);
        assert!(at.x.hypot(at.y) < gate.x.hypot(gate.y));
    }

    #[test]
    fn the_npc_types_carry_the_sde_numbers_they_are_written_with() {
        assert_eq!(WERPOST.shield, 18_000.0);
        assert_eq!(GUNSTAR.shot, [250.0, 250.0, 250.0, 250.0]);
        assert_eq!(HEAVY_GUNSTAR.shot, [500.0; 4]);
        assert_eq!(DAMAVIK.max_target_range, 75_000.0);
        assert_eq!(DAMAVIK.structure, 900.0);
    }

    use std::sync::Arc;

    use crate::npc::NpcPlace as _NpcPlace;
    use crate::world::{Body, Detail, SolarSystem};

    const POCHVEN_SYSTEM: i32 = 30_000_021;
    const ARSHAT: i32 = 30_002_253;
    const ARSHAT_REGION: i32 = 10_000_043;

    /// A Pochven system with `gates` stargates in a row (each leads out of the region) and a station.
    fn pochven_scene(gates: usize) -> Scene {
        zone_scene(POCHVEN_SYSTEM, pochven::POCHVEN_REGION_ID, gates)
    }

    /// A zone of the data file, in `region`, with `gates` stargates in a row.
    fn zone_scene(system_id: i32, region: i32, gates: usize) -> Scene {
        let mut bodies = Vec::new();
        for index in 0..gates {
            bodies.push(Body {
                id: 50_000_000 + index as i64,
                type_id: 56317,
                group_id: 10,
                category_id: 2,
                owner_id: 0,
                name: format!("Pochven gate {index}"),
                kind: BodyKind::Stargate,
                position: Vec3::new(3_932_160.0 + 1_000_000.0 * index as f64, 3_932_160.0, 3_932_160.0),
                radius: 15_000.0,
                graphic_id: None,
                detail: Detail::Stargate {
                    destination_system: 30_000_022,
                    destination_gate: 50_100_000 + index as i64,
                    destination_name: "outside".to_string(),
                    destination_security: 0.5,
                    destination_faction: None,
                    hazardous: false,
                    facing: None,
                    heading: None,
                },
            });
        }
        let system = SolarSystem {
            id: system_id,
            name: "Zone".to_string(),
            constellation_id: 20_000_788,
            region_id: region,
            faction_id: Some(500_026),
            security: -1.0,
            bodies,
        };
        Scene::new(Arc::new(system))
    }

    /// Polls the scene every quarter second from `from` for `seconds`.
    fn run(scene: &mut Scene, from: i64, seconds: i64) {
        let mut now = from;
        while now <= from + seconds * TICKS_PER_SECOND {
            scene.poll(now);
            now += TICKS_PER_SECOND / 4;
        }
    }

    /// The NPCs of the scene by their hull's type id.
    fn count_of(scene: &Scene, type_id: i32) -> usize {
        scene
            .pilots
            .values()
            .filter(|pilot| pilot.ship.npc.is_some() && pilot.ship.spec.type_id == type_id)
            .count()
    }

    const T0: i64 = 134_351_688_000_000_000;

    #[test]
    fn a_held_pochven_system_gets_a_sentry_at_each_gate_and_a_camp_at_its_first_two() {
        let mut scene = pochven_scene(3);
        run(&mut scene, T0, 1);
        assert_eq!(count_of(&scene, WERPOST.type_id), 3, "a werpost at each of the three gates");
        assert_eq!(count_of(&scene, DAMAVIK.type_id), 2, "a camp of one hull at each of the first two gates");
        assert_eq!(count_of(&scene, GUNSTAR.type_id), 0);
    }

    #[test]
    fn an_edencom_zone_gets_gunstars_and_sentinel_camps_and_no_skirmish() {
        let mut scene = zone_scene(ARSHAT, ARSHAT_REGION, 2);
        run(&mut scene, T0, SKIRMISH_INTERVAL_SECONDS + 5);
        assert_eq!(count_of(&scene, GUNSTAR.type_id), 2, "a GunStar at each gate");
        assert_eq!(count_of(&scene, SENTINEL.type_id), 2, "an EDENCOM camp at each of the two gates");
        assert_eq!(count_of(&scene, WERPOST.type_id), 0);
        assert_eq!(count_of(&scene, DAMAVIK.type_id), 0);
        assert_eq!(scene.pochven_npc_count(), 4, "no skirmish outside Pochven: only the sentries and camps");
    }

    #[test]
    fn a_system_that_is_not_a_zone_gets_nothing() {
        let mut scene = zone_scene(30_000_062, 10_000_001, 2);
        run(&mut scene, T0, 1);
        assert!(scene.pilots.values().all(|p| p.ship.npc.is_none()));
    }

    #[test]
    fn a_sentry_stands_20_km_from_its_gate_and_does_not_move() {
        let mut scene = pochven_scene(1);
        run(&mut scene, T0, 1);
        let sentry = scene
            .pilots
            .values()
            .find(|pilot| pilot.ship.spec.type_id == WERPOST.type_id)
            .expect("a werpost");
        let gate = Vec3::new(3_932_160.0, 3_932_160.0, 3_932_160.0);
        let p = sentry.ship.position;
        let distance = ((p.x - gate.x).powi(2) + (p.y - gate.y).powi(2) + (p.z - gate.z).powi(2)).sqrt();
        assert!((distance - SENTRY_OFFSET_METRES).abs() < 1.0, "{distance}");
        assert_eq!(sentry.ship.spec.max_velocity, 0.0);
    }

    #[test]
    fn the_skirmishes_start_after_the_interval_and_never_exceed_the_cap() {
        let mut scene = pochven_scene(3);
        run(&mut scene, T0, 1);
        let before = scene.pilots.values().filter(|p| p.ship.npc.is_some()).count();
        run(&mut scene, T0 + TICKS_PER_SECOND, SKIRMISH_INTERVAL_SECONDS + 5);
        let during = scene.pochven_npc_count();
        assert!(during > 5, "the first skirmish is in: {during} NPCs against {before} before");
        run(
            &mut scene,
            T0 + (SKIRMISH_INTERVAL_SECONDS + 10) * TICKS_PER_SECOND,
            5 * SKIRMISH_INTERVAL_SECONDS,
        );
        assert!(
            scene.pochven_npc_count() <= MAX_POCHVEN_NPCS,
            "the cap holds: {}",
            scene.pochven_npc_count()
        );
    }

    #[test]
    fn the_same_scene_run_twice_has_the_same_population() {
        let population = |scene: &Scene| {
            let mut ships: Vec<(i64, i32, i64, i64)> = scene
                .pilots
                .values()
                .map(|p| {
                    (
                        p.ship.spec.ship_id,
                        p.ship.spec.type_id,
                        p.ship.position.x as i64,
                        p.ship.position.z as i64,
                    )
                })
                .collect();
            ships.sort_unstable();
            ships
        };
        let mut first = pochven_scene(2);
        let mut second = pochven_scene(2);
        run(&mut first, T0, SKIRMISH_INTERVAL_SECONDS + 5);
        run(&mut second, T0, SKIRMISH_INTERVAL_SECONDS + 5);
        assert_eq!(population(&first), population(&second));
    }

    #[test]
    fn a_werpost_locks_a_rogue_drone_in_its_reach_and_never_a_fellow_triglavian() {
        let mut scene = pochven_scene(1);
        run(&mut scene, T0, 1);
        let sentry_session =
            *scene.pilots.iter().find(|(_, p)| p.ship.spec.type_id == WERPOST.type_id).expect("a werpost").0;
        let sentry_at = scene.pilots[&sentry_session].ship.position;
        let rogue_ship = 980_200_000_001;
        let fellow_ship = 980_200_000_002;
        let rogue_at = _NpcPlace::at_rest(Vec3::new(sentry_at.x + 40_000.0, sentry_at.y, sentry_at.z));
        let fellow_at = _NpcPlace::at_rest(Vec3::new(sentry_at.x - 40_000.0, sentry_at.y, sentry_at.z));
        scene
            .spawn_npc_with_behavior(
                npc_spec(rogue_ship, &LEGIONARY),
                npc_identity(rogue_ship, &LEGIONARY, 0.0),
                rogue_at,
                crate::brains::POCHVEN_NPC,
                T0,
            )
            .expect("a rogue drone is given its tree");
        scene
            .spawn_npc_with_behavior(
                npc_spec(fellow_ship, &DAMAVIK),
                npc_identity(fellow_ship, &DAMAVIK, 0.0),
                fellow_at,
                crate::brains::POCHVEN_NPC,
                T0,
            )
            .expect("a triglavian is given its tree");
        let mut locked_rogue = false;
        let mut locked_fellow = false;
        let mut now = T0;
        while now <= T0 + 30 * TICKS_PER_SECOND {
            scene.poll(now);
            let locks = scene.locked_by(sentry_session);
            locked_rogue |= locks.contains(&rogue_ship);
            locked_fellow |= locks.contains(&fellow_ship);
            now += TICKS_PER_SECOND / 4;
        }
        assert!(locked_rogue, "the sentry did not lock the rogue drone");
        assert!(!locked_fellow, "the sentry locked a fellow triglavian");
    }

    #[test]
    fn sim_owned_pochven_damageable_profiles_follow_insert_remove_and_teardown() {
        let mut scene = pochven_scene(3);
        let spawned = scene.poll(T0);
        let registrations: Vec<(i64, i32, i64, i64, i64, i64)> = spawned
            .iter()
            .filter_map(|out| match out {
                Out::SimDamageableEntityUpserted {
                    solar_system_id,
                    entity_id,
                    type_id,
                    owner_id,
                    corporation_id,
                    alliance_id,
                    war_faction_id,
                } => {
                    assert_eq!(*solar_system_id, scene.solar_system_id());
                    Some((*entity_id, *type_id, *owner_id, *corporation_id, *alliance_id, *war_faction_id))
                }
                _ => None,
            })
            .collect();
        assert_eq!(registrations.len(), 5, "three gate sentries and two camp NPCs are registered");
        assert!(registrations.iter().all(
            |(_, type_id, owner_id, corporation_id, alliance_id, war_faction_id)| {
                [WERPOST.type_id, DAMAVIK.type_id].contains(type_id)
                    && owner_id == corporation_id
                    && *alliance_id == 0
                    && *war_faction_id == 0
            }
        ));

        let removed_id = registrations[0].0;
        let npc_session = scene.session_of(removed_id).expect("the registered entity is in the scene");
        let removed = scene.remove_npc(npc_session, T0 + 1).expect("the NPC can leave without dying");
        assert!(removed.iter().any(|out| matches!(
            out,
            Out::SimDamageableEntityRemoved { solar_system_id, entity_id }
                if *solar_system_id == scene.solar_system_id() && *entity_id == removed_id
        )));
        assert!(
            !removed
                .iter()
                .any(|out| matches!(out, Out::Destroyed { ship_id, .. } if *ship_id == removed_id))
        );

        let teardown = scene.take_sim_damageable_entity_removals();
        assert_eq!(teardown.len(), 4, "scene teardown unregisters all remaining Sim-owned identities");
        assert!(!teardown.iter().any(|out| matches!(
            out,
            Out::SimDamageableEntityRemoved { entity_id, .. } if *entity_id == removed_id
        )));
        assert!(scene.take_sim_damageable_entity_removals().is_empty(), "teardown is idempotent");
    }
}
