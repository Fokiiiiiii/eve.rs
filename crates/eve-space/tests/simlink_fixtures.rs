//! Replays oracle/fixtures/simlink/messages.json, the golden frames of docs/design/adr/0007 (the C# host replays the same
//! file): every valid frame encodes to its bytes and decodes back, every invalid one is refused for the stated reason.

use eve_space::death::Credit;
use eve_space::drones::DroneEffect;
use eve_space::health::{Condition, Layer, Resonances, Vitals};
use eve_space::link::{
    ChargeOp, ContainerPlacement, DecodeError, Enter, How, LaunchDrone, Message, Spawn, WreckPlacement,
};
use eve_space::missiles::{Blast, Missile};
use eve_space::modules::{Action, Applies, Change, Effect, MiningFamily, Numbers, Reach, Remote, Rules};
use eve_space::scene::{HeatModuleProfile, HeatProfile, HeatRack};
use eve_space::ship::{ModuleSpec, NpcCombat, NpcIdentity, NpcWeapon, ShipSpec};
use eve_space::weapons::{Aoe, Arc, Charge, Spool, Turret};
use eve_space::wreck::WreckKind;
use serde_json::Value;
use serde_json::json;

fn fixtures() -> Value {
    let path = concat!(env!("CARGO_MANIFEST_DIR"), "/../../oracle/fixtures/simlink/messages.json");
    serde_json::from_str(&std::fs::read_to_string(path).unwrap()).unwrap()
}

fn hex(text: &str) -> Vec<u8> {
    (0..text.len() / 2).map(|i| u8::from_str_radix(&text[2 * i..2 * i + 2], 16).unwrap()).collect()
}

fn text(case: &Value, field: &str) -> String {
    case[field].as_str().unwrap_or_else(|| panic!("{field} in {case}")).to_owned()
}

fn wide(case: &Value, field: &str) -> i64 {
    text(case, field).parse().unwrap()
}

fn condition_of(case: &Value) -> Condition {
    let number = |field: &str| case[field].as_f64().unwrap_or_else(|| panic!("{field} in {case}"));
    Condition {
        damage: number("damage"),
        armor_damage: number("armorDamage"),
        shield_charge: number("shieldCharge"),
        charge: number("charge"),
    }
}

fn vitals_of(case: &Value) -> Vitals {
    let number = |field: &str| case[field].as_f64().unwrap_or_else(|| panic!("{field} in {case}"));
    let four = |layer: &str| -> [f64; 4] {
        let list = case["resonances"][layer].as_array().unwrap_or_else(|| panic!("{layer} in {case}"));
        [
            list[0].as_f64().unwrap(),
            list[1].as_f64().unwrap(),
            list[2].as_f64().unwrap(),
            list[3].as_f64().unwrap(),
        ]
    };
    Vitals {
        shield_capacity: number("shieldCapacity"),
        armor_hp: number("armorHp"),
        structure_hp: number("structureHp"),
        resonances: Resonances { shield: four("shield"), armor: four("armor"), structure: four("structure") },
        armor_pinned: case["armorPinned"].as_bool().unwrap_or(false),
    }
}

fn ship_of(case: &Value) -> ShipSpec {
    let number = |field: &str| case[field].as_f64().unwrap_or_else(|| panic!("{field} in {case}"));
    let int = |field: &str| case[field].as_i64().unwrap_or_else(|| panic!("{field} in {case}")) as i32;
    ShipSpec {
        ship_id: wide(case, "shipId"),
        character_id: wide(case, "characterId"),
        type_id: int("typeId"),
        group_id: int("groupId"),
        category_id: int("categoryId"),
        name: text(case, "name"),
        owner_id: wide(case, "ownerId"),
        corporation_id: int("corporationId"),
        alliance_id: int("allianceId"),
        war_faction_id: int("warFactionId"),
        security_status: number("securityStatus"),
        radius: number("radius"),
        mass: number("mass"),
        max_velocity: number("maxVelocity"),
        agility: number("agility"),
        warp_speed_au: number("warpSpeedAu"),
        shield_recharge_ms: number("shieldRechargeMs"),
        max_target_range: number("maxTargetRange"),
        max_locked_targets: int("maxLockedTargets"),
        scan_resolution: number("scanResolution"),
        signature_radius: number("signatureRadius"),
        capacitor_capacity: number("capacitorCapacity"),
        capacitor_recharge_s: number("capacitorRechargeS"),
        sensor_strength: sensors_of(&case["sensorStrength"]),
        ecm_resistance: number("ecmResistance"),
        energy_warfare_resistance: number("energyWarfareResistance"),
        drone_damage_multiplier: case["droneDamageMultiplier"].as_f64().unwrap_or(1.0),
        vitals: vitals_of(&case["vitals"]),
        condition: condition_of(&case["condition"]),
        modules: case["modules"]
            .as_array()
            .unwrap()
            .iter()
            .map(|m| ModuleSpec {
                item_id: wide(m, "itemId"),
                type_id: m["typeId"].as_i64().unwrap() as i32,
                flag: m["flag"].as_i64().unwrap() as i32,
            })
            .collect(),
    }
}

fn sensors_of(list: &Value) -> [f64; 4] {
    let list = list.as_array().expect("four sensor strengths");
    [
        list[0].as_f64().unwrap(),
        list[1].as_f64().unwrap(),
        list[2].as_f64().unwrap(),
        list[3].as_f64().unwrap(),
    ]
}

fn turret_of(w: &Value) -> Turret {
    let number = |field: &str| w[field].as_f64().unwrap_or_else(|| panic!("{field} in {w}"));
    let damage = w["rawDamage"].as_array().unwrap();
    Turret {
        weapon_type_id: w["weaponTypeId"].as_i64().unwrap() as i32,
        fx: text(w, "fx"),
        raw_damage: [
            damage[0].as_f64().unwrap(),
            damage[1].as_f64().unwrap(),
            damage[2].as_f64().unwrap(),
            damage[3].as_f64().unwrap(),
        ],
        optimal_range: number("optimalRange"),
        falloff: number("falloff"),
        tracking_speed: number("trackingSpeed"),
        signature_resolution: number("signatureResolution"),
        charge: w.get("charge").filter(|c| !c.is_null()).map(|c| Charge {
            type_id: c["typeId"].as_i64().unwrap() as i32,
            flag: c["flag"].as_i64().unwrap() as i32,
            rounds: c["rounds"].as_i64().unwrap(),
            per_cycle: c["perCycle"].as_i64().unwrap(),
            reloads: c.get("reloads").and_then(Value::as_bool).unwrap_or(false),
        }),
        missile: w.get("missile").filter(|m| !m.is_null()).map(|m| Missile {
            charge_type_id: m["chargeTypeId"].as_i64().unwrap() as i32,
            group_id: m["groupId"].as_i64().unwrap() as i32,
            name: m["name"].as_str().unwrap().to_string(),
            radius: m["radius"].as_f64().unwrap(),
            mass: m["mass"].as_f64().unwrap(),
            agility: m["agility"].as_f64().unwrap(),
            hit_points: m["hitPoints"].as_f64().unwrap(),
            max_velocity: m["maxVelocity"].as_f64().unwrap(),
            flight_time_ms: m["flightTimeMs"].as_f64().unwrap(),
            explosion_radius: m["explosionRadius"].as_f64().unwrap(),
            explosion_velocity: m["explosionVelocity"].as_f64().unwrap(),
            damage_reduction_factor: m["damageReductionFactor"].as_f64().unwrap(),
            damage_reduction_sensitivity: m["damageReductionSensitivity"].as_f64().unwrap(),
            dot: m.get("dot").filter(|d| !d.is_null()).map(|d| eve_space::missiles::Dot {
                duration_ms: d["durationMs"].as_f64().unwrap(),
                max_damage_per_tick: d["maxDamagePerTick"].as_f64().unwrap(),
                max_hp_percent_per_tick: d["maxHpPercentPerTick"].as_f64().unwrap(),
            }),
        }),
        spool: w
            .get("spool")
            .filter(|s| !s.is_null())
            .map(|s| Spool { per_cycle: s["perCycle"].as_f64().unwrap(), max: s["max"].as_f64().unwrap() }),
        aoe: w.get("aoe").filter(|a| !a.is_null()).map(|a| Aoe {
            explosion_radius: a["explosionRadius"].as_f64().unwrap(),
            explosion_velocity: a["explosionVelocity"].as_f64().unwrap(),
            damage_reduction_factor: a["damageReductionFactor"].as_f64().unwrap(),
            damage_reduction_sensitivity: a["damageReductionSensitivity"].as_f64().unwrap(),
        }),
        arc: w.get("arc").filter(|a| !a.is_null()).map(|a| Arc {
            range: a["range"].as_f64().unwrap(),
            targets: a["targets"].as_i64().unwrap() as i32,
        }),
        requires_gunnery: w["requiresGunnery"].as_bool().unwrap_or_else(|| panic!("requiresGunnery in {w}")),
    }
}

fn effect_of(case: &Value) -> Effect {
    let number = |v: &Value, field: &str| v[field].as_f64().unwrap_or_else(|| panic!("{field} in {v}"));
    let numbers = |v: &Value| {
        (!v.is_null()).then(|| Numbers {
            mass: number(v, "mass"),
            max_velocity: number(v, "maxVelocity"),
            signature_radius: number(v, "signatureRadius"),
        })
    };
    let changes = |v: &Value| -> Vec<Change> {
        v.as_array()
            .unwrap()
            .iter()
            .map(|c| Change {
                item_id: wide(c, "itemId"),
                attribute_id: c["attributeId"].as_i64().unwrap() as i32,
                value: number(c, "value"),
            })
            .collect()
    };
    let rules = case.get("rules").map_or_else(Rules::default, |r| Rules {
        group_id: r["groupId"].as_i64().unwrap() as i32,
        max_group_active: r["maxGroupActive"].as_i64().unwrap() as i32,
        disallow_early_deactivation: r["disallowEarlyDeactivation"].as_bool().unwrap(),
        requires_industrial_core: r["requiresIndustrialCore"].as_bool().unwrap(),
        provides_industrial_core: r["providesIndustrialCore"].as_bool().unwrap(),
        reactivation_delay_ms: number(r, "reactivationDelayMs"),
        max_velocity_limit: r.get("maxVelocityLimit").and_then(Value::as_f64).unwrap_or(0.0),
        min_velocity_limit: r.get("minVelocityLimit").and_then(Value::as_f64).unwrap_or(0.0),
    });
    Effect {
        rules,
        burns_fuel: case.get("burnsFuel").and_then(|v| v.as_bool()).unwrap_or(false),
        module_id: wide(case, "moduleId"),
        module_type: case["moduleType"].as_i64().unwrap() as i32,
        effect_id: case["effectId"].as_i64().unwrap() as i32,
        fx: text(case, "fx"),
        duration_ms: number(case, "durationMs"),
        capacitor_need: number(case, "capacitorNeed"),
        repeat: case["repeat"].as_i64().unwrap() as i32,
        target_id: wide(case, "targetId"),
        turret: case.get("turret").map(turret_of),
        action: case.get("action").map(|a| {
            let layer = || match a["layer"].as_str().unwrap() {
                "shield" => Layer::Shield,
                "armor" => Layer::Armor,
                _ => Layer::Structure,
            };
            let when = if a["when"] == "end" { Applies::CycleEnd } else { Applies::CycleStart };
            let amount = a["amount"].as_f64().unwrap_or(0.0);
            if a["kind"] == "smartbomb" {
                let damage = a["damage"].as_array().unwrap();
                return Action::Smartbomb {
                    range: a["range"].as_f64().unwrap(),
                    damage: [
                        damage[0].as_f64().unwrap(),
                        damage[1].as_f64().unwrap(),
                        damage[2].as_f64().unwrap(),
                        damage[3].as_f64().unwrap(),
                    ],
                    hurts_pilots: a["hurtsPilots"].as_bool().unwrap(),
                };
            }
            if a["kind"] == "charged" {
                let repair = |value: &Value| Action::Repair {
                    layer: match value["layer"].as_str().unwrap() {
                        "shield" => Layer::Shield,
                        "armor" => Layer::Armor,
                        _ => Layer::Structure,
                    },
                    amount: value["amount"].as_f64().unwrap(),
                    when: if value["when"] == "end" { Applies::CycleEnd } else { Applies::CycleStart },
                };
                let fallback = (!a["fallback"].is_null()).then(|| Box::new(repair(&a["fallback"])));
                return Action::Charged {
                    action: Box::new(if a["action"]["kind"] == "commandBurst" {
                        Action::CommandBurst { range_meters: a["action"]["rangeMeters"].as_f64().unwrap() }
                    } else {
                        repair(&a["action"])
                    }),
                    fallback,
                    charge_type_id: a["chargeTypeId"].as_i64().unwrap() as i32,
                    charge_flag: a["chargeFlag"].as_i64().unwrap() as i32,
                    charge_per_cycle: a["chargePerCycle"].as_i64().unwrap(),
                    charges: a["charges"].as_i64().unwrap(),
                };
            }
            let deployable = |v: &Value| eve_space::deployable::Spec {
                charge_type_id: v["chargeTypeId"].as_i64().unwrap() as i32,
                group_id: v["groupId"].as_i64().unwrap() as i32,
                name: v["name"].as_str().unwrap().to_string(),
                radius: v["radius"].as_f64().unwrap(),
            };
            if a["kind"] == "cloak" {
                return Action::Cloak { warp_safe: a["warpSafe"].as_bool().unwrap_or(false) };
            }
            if a["kind"] == "warpBubble" {
                return Action::WarpBubble { range: a["range"].as_f64().unwrap() };
            }
            if a["kind"] == "warpImmunity" {
                return Action::WarpImmunity;
            }
            if a["kind"] == "scrambleStatus" {
                return Action::ScrambleStatus { delta: a["delta"].as_f64().unwrap() };
            }
            if a["kind"] == "defender" {
                let m = &a["missile"];
                let number = |key: &str| m[key].as_f64().unwrap();
                return Action::Defender {
                    missile: eve_space::missiles::Missile {
                        charge_type_id: m["chargeTypeId"].as_i64().unwrap() as i32,
                        group_id: m["groupId"].as_i64().unwrap() as i32,
                        name: m["name"].as_str().unwrap().to_string(),
                        radius: number("radius"),
                        mass: number("mass"),
                        agility: number("agility"),
                        hit_points: number("hitPoints"),
                        max_velocity: number("maxVelocity"),
                        flight_time_ms: number("flightTimeMs"),
                        explosion_radius: number("explosionRadius"),
                        explosion_velocity: number("explosionVelocity"),
                        damage_reduction_factor: number("damageReductionFactor"),
                        damage_reduction_sensitivity: number("damageReductionSensitivity"),
                        dot: None,
                    },
                    damage: sensors_of(&a["damage"]),
                };
            }
            if a["kind"] == "stasisBurst" {
                return Action::StasisBurst {
                    range: a["range"].as_f64().unwrap(),
                    duration_ms: a["durationMs"].as_f64().unwrap(),
                    speed_factor: a["speedFactor"].as_f64().unwrap(),
                };
            }
            if a["kind"] == "warpProbe" {
                return Action::WarpProbe {
                    ball: deployable(&a["ball"]),
                    range: a["range"].as_f64().unwrap(),
                    lifetime_ms: a["lifetimeMs"].as_f64().unwrap(),
                };
            }
            if a["kind"] == "commandBurst" {
                return Action::CommandBurst { range_meters: a["rangeMeters"].as_f64().unwrap() };
            }
            if a["kind"] == "panic" {
                return Action::Panic { range_meters: a["rangeMeters"].as_f64().unwrap() };
            }
            if a["kind"] == "cynoField" {
                return Action::CynoField {
                    ball: deployable(&a["ball"]),
                    lifetime_ms: a["lifetimeMs"].as_f64().unwrap(),
                };
            }
            if a["kind"] == "bomb" {
                let numbers = |key: &str| sensors_of(&a[key]);
                return Action::Bomb {
                    ball: deployable(&a["ball"]),
                    kind: match a["bombKind"].as_i64().unwrap() {
                        0 => eve_space::deployable::BombKind::Damage,
                        1 => eve_space::deployable::BombKind::Ecm,
                        _ => eve_space::deployable::BombKind::Neutralize,
                    },
                    lifetime_ms: a["lifetimeMs"].as_f64().unwrap(),
                    blast_radius: a["blastRadius"].as_f64().unwrap(),
                    damage: numbers("damage"),
                    ecm: numbers("ecm"),
                    neutralize: a["neutralize"].as_f64().unwrap(),
                };
            }
            if a["kind"] == "ecmBurst" {
                return Action::EcmBurst {
                    range: a["range"].as_f64().unwrap(),
                    strength: sensors_of(&a["strength"]),
                };
            }
            if a["kind"] == "mining" {
                return Action::Mining {
                    family: match a["family"].as_str().unwrap() {
                        "ore" => MiningFamily::Ore,
                        "ice" => MiningFamily::Ice,
                        _ => MiningFamily::Gas,
                    },
                    mining_amount_m3: a["miningAmountM3"].as_f64().unwrap(),
                    waste_multiplier: a["wasteMultiplier"].as_f64().unwrap(),
                    waste_probability: a["wasteProbability"].as_f64().unwrap(),
                    crit_probability: a["critProbability"].as_f64().unwrap(),
                    crit_multiplier: a["critMultiplier"].as_f64().unwrap(),
                };
            }
            if a["kind"] == "scan" {
                return Action::Scan {
                    cargo: a["cargo"].as_bool().unwrap(),
                    reach: Reach {
                        optimal: a["optimalMeters"].as_f64().unwrap(),
                        falloff: a["falloffMeters"].as_f64().unwrap(),
                    },
                };
            }
            if a["kind"] == "salvage" {
                return Action::Salvage { access_bonus: a["accessBonus"].as_f64().unwrap() };
            }
            if a["kind"] == "tractor" {
                return Action::Tractor {
                    velocity: a["velocity"].as_f64().unwrap(),
                    hold: a["hold"].as_f64().unwrap(),
                    range: a["range"].as_f64().unwrap(),
                };
            }
            if a["kind"] == "passiveLock" {
                return Action::PassiveLock;
            }
            if a["kind"] == "autoTarget" {
                return Action::AutoTarget { range: a["range"].as_f64().unwrap() };
            }
            if a["kind"] == "resistanceShift" {
                let four = |field: &str| -> [f64; 4] {
                    let list = a[field].as_array().unwrap_or_else(|| panic!("{field} in {a}"));
                    [
                        list[0].as_f64().unwrap(),
                        list[1].as_f64().unwrap(),
                        list[2].as_f64().unwrap(),
                        list[3].as_f64().unwrap(),
                    ]
                };
                let ids = a["attributeIds"].as_array().unwrap();
                return Action::ResistanceShift {
                    seed: four("seed"),
                    shift: a["shift"].as_f64().unwrap(),
                    attribute_ids: [
                        ids[0].as_i64().unwrap() as i32,
                        ids[1].as_i64().unwrap() as i32,
                        ids[2].as_i64().unwrap() as i32,
                        ids[3].as_i64().unwrap() as i32,
                    ],
                };
            }
            if a["kind"] == "doomsday" {
                let damage = a["damage"].as_array().unwrap();
                return Action::Doomsday {
                    delay_ms: a["delayMs"].as_f64().unwrap(),
                    damage: [
                        damage[0].as_f64().unwrap(),
                        damage[1].as_f64().unwrap(),
                        damage[2].as_f64().unwrap(),
                        damage[3].as_f64().unwrap(),
                    ],
                    immobile_ms: a["immobileMs"].as_f64().unwrap(),
                    no_jump_ms: a["noJumpMs"].as_f64().unwrap(),
                };
            }
            if a["kind"] == "field" {
                // the transport field: its sphere is a strike (the nested object), then the destination, the warp block and the two id lists
                let s = &a["strike"];
                let number = |v: &Value, key: &str| v[key].as_f64().unwrap();
                let vector =
                    |v: &Value| [v[0].as_f64().unwrap(), v[1].as_f64().unwrap(), v[2].as_f64().unwrap()];
                let ids = |key: &str| {
                    a[key].as_array().unwrap().iter().map(|id| id.as_i64().unwrap()).collect::<Vec<i64>>()
                };
                let damage = s["damage"].as_array().unwrap();
                return Action::Field {
                    strike: eve_space::strike::Strike {
                        shape: eve_space::strike::StrikeShape::from_wire(s["shape"].as_u64().unwrap() as u8)
                            .expect("a shape the simulation runs"),
                        direction: vector(&s["direction"]),
                        length: number(s, "length"),
                        radius: number(s, "radius"),
                        warning_ms: number(s, "warningMs"),
                        pulse_ms: number(s, "pulseMs"),
                        pulses: s["pulses"].as_i64().unwrap() as i32,
                        damage: [
                            damage[0].as_f64().unwrap(),
                            damage[1].as_f64().unwrap(),
                            damage[2].as_f64().unwrap(),
                            damage[3].as_f64().unwrap(),
                        ],
                        signature_radius: number(s, "signatureRadius"),
                        immobile_ms: number(s, "immobileMs"),
                        no_jump_ms: number(s, "noJumpMs"),
                        neut_amount: number(s, "neutAmount"),
                        neut_radius: number(s, "neutRadius"),
                        neut_signature_radius: number(s, "neutSignatureRadius"),
                        debuff_ms: s["debuffMs"].as_f64().unwrap_or(0.0),
                        sweep_end: None,
                        beacon: None,
                    },
                    destination: vector(&a["destination"]),
                    warp_block_ms: number(a, "warpBlockMs"),
                    moved: ids("moved"),
                    held: ids("held"),
                };
            }
            if a["kind"] == "strike" {
                let direction = a["direction"].as_array().unwrap();
                let damage = a["damage"].as_array().unwrap();
                let num = |key: &str| a[key].as_f64().unwrap();
                return Action::Strike {
                    strike: eve_space::strike::Strike {
                        shape: eve_space::strike::StrikeShape::from_wire(a["shape"].as_u64().unwrap() as u8)
                            .expect("a shape the simulation runs"),
                        direction: [
                            direction[0].as_f64().unwrap(),
                            direction[1].as_f64().unwrap(),
                            direction[2].as_f64().unwrap(),
                        ],
                        length: num("length"),
                        radius: num("radius"),
                        warning_ms: num("warningMs"),
                        pulse_ms: num("pulseMs"),
                        pulses: a["pulses"].as_i64().unwrap() as i32,
                        damage: [
                            damage[0].as_f64().unwrap(),
                            damage[1].as_f64().unwrap(),
                            damage[2].as_f64().unwrap(),
                            damage[3].as_f64().unwrap(),
                        ],
                        signature_radius: num("signatureRadius"),
                        immobile_ms: num("immobileMs"),
                        no_jump_ms: num("noJumpMs"),
                        neut_amount: num("neutAmount"),
                        neut_radius: num("neutRadius"),
                        neut_signature_radius: num("neutSignatureRadius"),
                        debuff_ms: a["debuffMs"].as_f64().unwrap_or(0.0),
                        sweep_end: a["sweepEnd"].as_array().map(|end| {
                            [end[0].as_f64().unwrap(), end[1].as_f64().unwrap(), end[2].as_f64().unwrap()]
                        }),
                        beacon: a["beacon"].as_array().map(|offset| {
                            [
                                offset[0].as_f64().unwrap(),
                                offset[1].as_f64().unwrap(),
                                offset[2].as_f64().unwrap(),
                            ]
                        }),
                    },
                };
            }
            if a["kind"] == "repair" {
                return Action::Repair { layer: layer(), amount, when };
            }
            let four = |key: &str| -> [f64; 4] {
                let v = a[key].as_array().unwrap();
                [
                    v[0].as_f64().unwrap(),
                    v[1].as_f64().unwrap(),
                    v[2].as_f64().unwrap(),
                    v[3].as_f64().unwrap(),
                ]
            };
            if a["kind"] == "kamikaze" {
                return Action::Kamikaze {
                    damage: four("damage"),
                    range: a["rangeMeters"].as_f64().unwrap(),
                };
            }
            if a["kind"] == "blast" {
                let c = &a["centre"];
                let centre = [c[0].as_f64().unwrap(), c[1].as_f64().unwrap(), c[2].as_f64().unwrap()];
                return Action::Blast {
                    centre,
                    radius: a["radiusMeters"].as_f64().unwrap(),
                    damage: four("damage"),
                };
            }
            if a["kind"] == "missile" {
                let num = |key: &str| a[key].as_f64().unwrap();
                let blast = Blast {
                    explosion_radius: num("explosionRadius"),
                    explosion_velocity: num("explosionVelocity"),
                    reduction_factor: num("reductionFactor"),
                    reduction_sensitivity: num("reductionSensitivity"),
                };
                return Action::Missile { damage: four("damage"), reach: num("reachMeters"), blast };
            }
            let resolution = || a["signatureResolution"].as_f64().unwrap();
            let percent = || a["percent"].as_f64().unwrap();
            let what = match a["what"].as_str().unwrap() {
                "repair" => Remote::Repair { layer: layer(), amount },
                "transfer" => Remote::Transfer { amount },
                "neutralize" => Remote::Neutralize { amount, signature_resolution: resolution() },
                "drain" => Remote::Drain { amount, signature_resolution: resolution() },
                "web" => Remote::Web { percent: percent() },
                "paint" => Remote::Paint { percent: percent() },
                "damp" => Remote::Damp {
                    range_percent: a["rangePercent"].as_f64().unwrap(),
                    resolution_percent: a["resolutionPercent"].as_f64().unwrap(),
                    gravimetric_percent: a["gravimetricStrengthPercent"].as_f64().unwrap_or(0.0),
                    ladar_percent: a["ladarStrengthPercent"].as_f64().unwrap_or(0.0),
                    magnetometric_percent: a["magnetometricStrengthPercent"].as_f64().unwrap_or(0.0),
                    radar_percent: a["radarStrengthPercent"].as_f64().unwrap_or(0.0),
                },
                "disrupt" => Remote::Disrupt {
                    range_percent: a["rangePercent"].as_f64().unwrap(),
                    falloff_percent: a["falloffPercent"].as_f64().unwrap(),
                    tracking_percent: a["trackingPercent"].as_f64().unwrap(),
                },
                "guidanceDisrupt" => Remote::GuidanceDisrupt {
                    velocity_percent: a["velocityPercent"].as_f64().unwrap(),
                    delay_percent: a["delayPercent"].as_f64().unwrap(),
                    aoe_velocity_percent: a["aoeVelocityPercent"].as_f64().unwrap(),
                    aoe_cloud_percent: a["aoeCloudPercent"].as_f64().unwrap(),
                },
                "jam" => Remote::Jam { strength: sensors_of(&a["strength"]) },
                _ => Remote::Scramble {
                    strength: a["strength"].as_f64().unwrap(),
                    blocks_mwd: a["blocksMwd"].as_bool().unwrap(),
                },
            };
            Action::Remote {
                what,
                reach: Reach {
                    optimal: a["optimal"].as_f64().unwrap(),
                    falloff: a["falloff"].as_f64().unwrap(),
                },
                when,
                jam: a["jam"].as_str().unwrap().to_string(),
            }
        }),
        on: numbers(&case["on"]),
        off: numbers(&case["off"]),
        on_changes: changes(&case["onChanges"]),
        off_changes: changes(&case["offChanges"]),
    }
}

fn vector_of(case: &Value, field: &str) -> [f64; 3] {
    let list = case[field].as_array().unwrap_or_else(|| panic!("{field} in {case}"));
    [list[0].as_f64().unwrap(), list[1].as_f64().unwrap(), list[2].as_f64().unwrap()]
}

fn identity_of(case: &Value) -> NpcIdentity {
    let number = |v: &Value, field: &str| v[field].as_f64().unwrap_or_else(|| panic!("{field} in {v}"));
    let weapons = case["weapons"]
        .as_array()
        .unwrap()
        .iter()
        .map(|w| NpcWeapon {
            module_id: wide(w, "moduleId"),
            effect_id: w["effectId"].as_i64().unwrap() as i32,
            cycle_ms: number(w, "cycleMs"),
            capacitor_need: number(w, "capacitorNeed"),
            turret: turret_of(w),
        })
        .collect();
    let wreck = wreck_kind_of(case.get("wreck"));
    NpcIdentity {
        hostile_response_threshold: case["hostileResponseThreshold"].as_i64().unwrap(),
        friendly_response_threshold: case["friendlyResponseThreshold"].as_i64().unwrap(),
        combat: NpcCombat { weapons, aggression_range: number(case, "aggressionRange") },
        wreck,
        hull: case.get("hull").and_then(Value::as_bool).unwrap_or(false),
        host_wreck: case.get("hostWreck").and_then(Value::as_bool).unwrap_or(false),
        drone: None,
    }
}

/// A drone's remote effect of the fixture: its id, name and cycle numbers, and the action a cycle does (read as an activation's, which holds the same action).
fn drone_effect_of(case: &Value) -> DroneEffect {
    let activation = json!({
        "moduleId": "0", "moduleType": 0, "effectId": case["effectId"], "fx": case["fx"],
        "durationMs": case["cycleMs"], "capacitorNeed": 0.0, "repeat": 1, "targetId": "0",
        "onChanges": [], "offChanges": [], "action": case["action"],
    });
    DroneEffect {
        effect_id: case["effectId"].as_i64().unwrap() as i32,
        fx: text(case, "fx"),
        cycle_ms: case["cycleMs"].as_f64().unwrap(),
        lasting_ms: case["lastingMs"].as_f64().unwrap(),
        action: effect_of(&activation).action.expect("the drone effect's action"),
    }
}

fn weapon_of(w: &Value) -> NpcWeapon {
    let number = |field: &str| w[field].as_f64().unwrap_or_else(|| panic!("{field} in {w}"));
    NpcWeapon {
        module_id: wide(w, "moduleId"),
        effect_id: w["effectId"].as_i64().unwrap() as i32,
        cycle_ms: number("cycleMs"),
        capacitor_need: number("capacitorNeed"),
        turret: turret_of(w),
    }
}

fn wreck_kind_of(wreck: Option<&Value>) -> Option<WreckKind> {
    wreck.filter(|w| !w.is_null()).map(|w| WreckKind {
        type_id: w["typeId"].as_i64().unwrap() as i32,
        name: text(w, "name"),
        graphic_id: Some(w["graphicId"].as_i64().unwrap() as i32).filter(|g| *g != 0),
    })
}

/// The attributes a change tells the client (its item, its attribute and its value).
fn changes_of(list: &Value) -> Vec<Change> {
    list.as_array()
        .unwrap()
        .iter()
        .map(|c| Change {
            item_id: wide(c, "itemId"),
            attribute_id: c["attributeId"].as_i64().unwrap() as i32,
            value: c["value"].as_f64().unwrap(),
        })
        .collect()
}

fn message_of(case: &Value) -> Message {
    let number = |field: &str| case[field].as_u64().unwrap_or_else(|| panic!("{field} in {case}"));
    let session_id = || wide(case, "sessionId") as u64;
    match case["kind"].as_str().unwrap() {
        "Hello" => Message::Hello { version: number("version") as u16 },
        "HelloAck" => {
            Message::HelloAck { version: number("version") as u16, poll_hz: number("pollHz") as u32 }
        }
        "Enter" => Message::Enter(Box::new(Enter {
            session_id: session_id(),
            solar_system_id: number("solarSystemId") as i32,
            how: if number("how") == 0 { How::Undock } else { How::InSpace },
            station_id: wide(case, "stationId"),
            ship: ship_of(&case["ship"]),
            position: case.get("position").map(|vector| {
                [vector[0].as_f64().unwrap(), vector[1].as_f64().unwrap(), vector[2].as_f64().unwrap()]
            }),
            direction: case.get("direction").map(|vector| {
                [vector[0].as_f64().unwrap(), vector[1].as_f64().unwrap(), vector[2].as_f64().unwrap()]
            }),
        })),
        "EnterReply" => Message::EnterReply {
            session_id: session_id(),
            outcome: if case["ok"].as_bool().unwrap() { Ok(()) } else { Err(text(case, "reason")) },
        },
        "Leave" => Message::Leave { session_id: session_id(), why: number("why") as u8 },
        "Bind" => Message::Bind { session_id: session_id() },
        "UpdateState" => Message::UpdateState { session_id: session_id() },
        "Command" => Message::Command {
            session_id: session_id(),
            command: number("command") as u8,
            args: case["args"].as_array().unwrap().iter().map(|a| a.as_f64().unwrap()).collect(),
        },
        "ShipSpec" => Message::ShipSpec { session_id: session_id(), ship: Box::new(ship_of(&case["ship"])) },
        "Notify" => Message::Notify {
            session_id: session_id(),
            name: text(case, "notification"),
            id_type: text(case, "idType"),
            args: hex(case["args"].as_str().unwrap()),
        },
        "Docked" => Message::Docked {
            session_id: session_id(),
            station_id: wide(case, "stationId"),
            condition: condition_of(&case["condition"]),
        },
        "Condition" => Message::Condition {
            session_id: session_id(),
            command: number("command") as u8,
            condition: condition_of(&case["condition"]),
        },
        "Refused" => Message::Refused {
            session_id: session_id(),
            command: number("command") as u8,
            reason: text(case, "reason"),
        },
        "Accepted" => Message::Accepted {
            session_id: session_id(),
            command: number("command") as u8,
            at: wide(case, "at"),
        },
        "Approaching" => Message::Approaching {
            session_id: session_id(),
            command: number("command") as u8,
            distance: case["distance"].as_f64().unwrap(),
        },
        "Jumping" => Message::Jumping {
            session_id: session_id(),
            command: number("command") as u8,
            destination_system: number("destinationSystem") as i32,
            destination_gate: wide(case, "destinationGate"),
        },
        "Jumped" => Message::Jumped {
            session_id: session_id(),
            from_system: number("fromSystem") as i32,
            from_gate: wide(case, "fromGate"),
            to_system: number("toSystem") as i32,
            to_gate: wide(case, "toGate"),
        },
        "Activate" => {
            Message::Activate { session_id: session_id(), effect: Box::new(effect_of(&case["effect"])) }
        }
        "ModuleWorkCycle" => Message::ModuleWorkCycle {
            session_id: session_id(),
            character_id: wide(case, "characterId"),
            ship_id: wide(case, "shipId"),
            solar_system_id: case["solarSystemId"].as_i64().unwrap() as i32,
            module_id: wide(case, "moduleId"),
            target_id: wide(case, "targetId"),
            cycle_id: wide(case, "cycleId") as u64,
            command: number("command") as u8,
            mining_amount_m3: case["miningAmountM3"].as_f64().unwrap(),
            waste_multiplier: case["wasteMultiplier"].as_f64().unwrap(),
            waste_probability: case["wasteProbability"].as_f64().unwrap(),
            crit_probability: case["critProbability"].as_f64().unwrap(),
            crit_multiplier: case["critMultiplier"].as_f64().unwrap(),
            access_difficulty_bonus: case["accessDifficultyBonus"].as_f64().unwrap(),
            waste_roll: case["wasteRoll"].as_f64().unwrap(),
            rounding_roll: case["roundingRoll"].as_f64().unwrap(),
            crit_roll: case["critRoll"].as_f64().unwrap(),
            recipients: case.get("recipients").and_then(|list| list.as_array()).map_or_else(
                Vec::new,
                |list| {
                    list.iter()
                        .map(|ship| {
                            ship.as_str().map_or_else(|| ship.as_i64().unwrap(), |text| text.parse().unwrap())
                        })
                        .collect()
                },
            ),
        },
        "ModuleWorkResult" => Message::ModuleWorkResult {
            session_id: session_id(),
            module_id: wide(case, "moduleId"),
            target_id: wide(case, "targetId"),
            cycle_id: wide(case, "cycleId") as u64,
            accepted: case["accepted"].as_bool().unwrap(),
            reason: text(case, "reason"),
            units_remaining: wide(case, "unitsRemaining"),
            delivered_units: wide(case, "deliveredUnits"),
            complete: case["complete"].as_bool().unwrap(),
        },
        "ModuleEnded" => Message::ModuleEnded {
            session_id: session_id(),
            module_id: wide(case, "moduleId"),
            reason: number("reason") as u8,
        },
        "Targets" => Message::Targets {
            session_id: session_id(),
            command: number("command") as u8,
            pending: case["pending"].as_bool().unwrap(),
            targets: case["targets"]
                .as_array()
                .unwrap()
                .iter()
                .map(|t| t.as_str().unwrap().parse().unwrap())
                .collect(),
        },
        "TargetRefused" => Message::TargetRefused {
            session_id: session_id(),
            command: number("command") as u8,
            reason: text(case, "reason"),
            target_type: case["targetType"].as_i64().unwrap() as i32,
            desired_range: case["desiredRange"].as_f64().unwrap(),
            actual_distance: case["actualDistance"].as_f64().unwrap(),
        },
        "ChargeSpent" => Message::ChargeSpent {
            session_id: session_id(),
            module_id: wide(case, "moduleId"),
            rounds_left: wide(case, "roundsLeft"),
        },
        "BuffState" => Message::BuffState {
            session_id: session_id(),
            ship_id: wide(case, "shipId"),
            entries: case["entries"]
                .as_array()
                .unwrap()
                .iter()
                .map(|entry| eve_space::link::BuffEntry {
                    collection_id: entry["collectionId"].as_i64().unwrap() as i32,
                    value: entry["value"].as_f64().unwrap(),
                    remaining_ms: entry["remainingMs"].as_f64().unwrap(),
                })
                .collect(),
        },
        "UpdateEffect" => Message::UpdateEffect {
            session_id: session_id(),
            module_id: wide(case, "moduleId"),
            duration_ms: case["durationMs"].as_f64().unwrap(),
            capacitor_need: case["capacitorNeed"].as_f64().unwrap(),
            turret: case.get("turret").filter(|t| !t.is_null()).map(|t| Box::new(turret_of(t))),
            changes: changes_of(&case["changes"]),
        },
        "AdjustCharge" => Message::AdjustCharge {
            request_id: wide(case, "requestId") as u64,
            session_id: session_id(),
            module_id: wide(case, "moduleId"),
            charge_type: number("chargeType") as i32,
            op: match case["op"].as_str().unwrap() {
                "add" => {
                    ChargeOp::Add { capacity: wide(case, "capacity"), available: wide(case, "available") }
                }
                "remove" => ChargeOp::Remove { quantity: wide(case, "quantity") },
                other => panic!("unknown charge op {other}"),
            },
        },
        "ChargeAdjusted" => Message::ChargeAdjusted {
            request_id: wide(case, "requestId") as u64,
            running: case["running"].as_bool().unwrap(),
            before: wide(case, "before"),
            after: wide(case, "after"),
        },
        "Destroyed" => Message::Destroyed {
            session_id: session_id(),
            ship_id: wide(case, "shipId"),
            wreck_id: wide(case, "wreckId"),
            position: vector_of(case, "position"),
            direction: vector_of(case, "direction"),
            radius: case["radius"].as_f64().unwrap(),
            killer_ship_id: wide(case, "killerShipId"),
            wreck_kind: wreck_kind_of(case.get("wreckKind")),
        },
        "Respawn" => Message::Respawn {
            session_id: session_id(),
            ship: Box::new(ship_of(&case["ship"])),
            position: vector_of(case, "position"),
            direction: vector_of(case, "direction"),
        },
        "PlaceWreck" => Message::PlaceWreck(Box::new(WreckPlacement {
            solar_system_id: number("solarSystemId") as i32,
            wreck_id: wide(case, "wreckId"),
            group_id: number("groupId") as i32,
            kind: wreck_kind_of(case.get("wreck")).unwrap(),
            position: vector_of(case, "position"),
            direction: vector_of(case, "direction"),
            radius: case["radius"].as_f64().unwrap(),
            launcher_id: wide(case, "launcherId"),
            credit: Credit {
                character_id: wide(case, "characterId"),
                corporation_id: wide(case, "corporationId"),
                alliance_id: wide(case, "allianceId"),
                war_faction_id: wide(case, "warFactionId"),
            },
            is_empty: case["isEmpty"].as_bool().unwrap(),
        })),
        "PlaceContainer" => Message::PlaceContainer(Box::new(ContainerPlacement {
            session_id: session_id(),
            container_id: wide(case, "containerId"),
            kind: wreck_kind_of(case.get("container")).unwrap(),
            radius: case["radius"].as_f64().unwrap(),
            launcher_id: wide(case, "launcherId"),
            credit: Credit {
                character_id: wide(case, "characterId"),
                corporation_id: wide(case, "corporationId"),
                alliance_id: wide(case, "allianceId"),
                war_faction_id: wide(case, "warFactionId"),
            },
            distance: case["distance"].as_f64().unwrap(),
        })),
        "ContainerPlaced" => Message::ContainerPlaced {
            session_id: session_id(),
            container_id: wide(case, "containerId"),
            position: vector_of(case, "position"),
            direction: vector_of(case, "direction"),
        },
        "Arrived" => Message::Arrived {
            session_id: session_id(),
            belt_id: wide(case, "beltId"),
            position: vector_of(case, "position"),
        },
        "ChangeWreck" => Message::ChangeWreck {
            session_id: session_id(),
            wreck_id: wide(case, "wreckId"),
            is_empty: case["isEmpty"].as_bool().unwrap(),
        },
        "RemoveWreck" => {
            Message::RemoveWreck { wreck_id: wide(case, "wreckId"), effect: number("effect") as i32 }
        }
        "Deployed" => Message::Deployed(Box::new(eve_space::link::DeployedPlacement {
            solar_system_id: number("solarSystemId") as i32,
            deployable_id: wide(case, "deployableId"),
            name: text(case, "typeName"),
            type_id: number("typeId") as i32,
            group_id: number("groupId") as i32,
            position: vector_of(case, "position"),
            radius: case["radius"].as_f64().unwrap(),
            owner_id: wide(case, "ownerId"),
            corporation_id: number("corporationId") as i32,
            alliance_id: number("allianceId") as i32,
            ship_id: wide(case, "shipId"),
            lifetime_ms: case["lifetimeMs"].as_f64().unwrap(),
        })),
        "RemoveDeployed" => Message::RemoveDeployed { deployable_id: wide(case, "deployableId") },
        "PlaceProbe" => Message::PlaceProbe(Box::new(eve_space::link::ProbePlacement {
            probe_id: wide(case, "probeId"),
            solar_system_id: number("solarSystemId") as i32,
            name: text(case, "typeName"),
            type_id: number("typeId") as i32,
            group_id: number("groupId") as i32,
            position: vector_of(case, "position"),
            radius: case["radius"].as_f64().unwrap(),
            owner_id: wide(case, "ownerId"),
            corporation_id: number("corporationId") as i32,
            alliance_id: number("allianceId") as i32,
            ship_id: wide(case, "shipId"),
        })),
        "RemoveProbe" => Message::RemoveProbe { probe_id: wide(case, "probeId") },
        "PlaceDisruptor" => Message::PlaceDisruptor(Box::new(eve_space::link::DisruptorPlacement {
            deployable_id: wide(case, "deployableId"),
            solar_system_id: number("solarSystemId") as i32,
            name: text(case, "typeName"),
            type_id: number("typeId") as i32,
            group_id: number("groupId") as i32,
            position: vector_of(case, "position"),
            radius: case["radius"].as_f64().unwrap(),
            range: case["range"].as_f64().unwrap(),
            active: case["active"].as_bool().unwrap(),
            owner_id: wide(case, "ownerId"),
            corporation_id: number("corporationId") as i32,
            alliance_id: number("allianceId") as i32,
            ship_id: wide(case, "shipId"),
        })),
        "SetDisruptorActive" => Message::SetDisruptorActive {
            deployable_id: wide(case, "deployableId"),
            active: case["active"].as_bool().unwrap(),
        },
        "Measured" => Message::Measured {
            session_id: session_id(),
            command: number("command") as u8,
            distance: case["distance"].as_f64(),
        },
        "Spawn" => Message::Spawn(Box::new(Spawn {
            request_id: wide(case, "requestId") as u64,
            solar_system_id: number("solarSystemId") as i32,
            ship: ship_of(&case["ship"]),
            anchor_ship_id: wide(case, "anchorShipId"),
            position: vector_of(case, "position"),
            velocity: vector_of(case, "velocity"),
            direction: vector_of(case, "direction"),
            behavior: text(case, "behavior"),
            identity: identity_of(&case["identity"]),
        })),
        "SpawnReply" => Message::SpawnReply {
            request_id: wide(case, "requestId") as u64,
            outcome: if case["ok"].as_bool().unwrap() {
                Ok(wide(case, "session") as u64)
            } else {
                Err(text(case, "reason"))
            },
        },
        "LaunchDrone" => Message::LaunchDrone(Box::new(LaunchDrone {
            request_id: wide(case, "requestId") as u64,
            session_id: session_id(),
            ship: ship_of(&case["ship"]),
            weapon: case.get("weapon").filter(|weapon| !weapon.is_null()).map(weapon_of),
            graphic_id: number("graphicId") as i32,
            orbit_distance: case["orbitDistance"].as_f64().unwrap(),
            attack_range: case["attackRange"].as_f64().unwrap(),
            cruise_fraction: case["cruiseFraction"].as_f64().unwrap(),
            mining_amount_m3: case.get("miningAmountM3").and_then(Value::as_f64).unwrap_or(0.0),
            work_cycle_ms: case.get("workCycleMs").and_then(Value::as_f64).unwrap_or(0.0),
            mining_waste_multiplier: case.get("miningWasteMultiplier").and_then(Value::as_f64).unwrap_or(0.0),
            mining_waste_probability: case
                .get("miningWasteProbability")
                .and_then(Value::as_f64)
                .unwrap_or(0.0),
            salvage_access_bonus: case.get("salvageAccessBonus").and_then(Value::as_f64).unwrap_or(0.0),
            effect: case.get("effect").filter(|effect| !effect.is_null()).map(drone_effect_of),
        })),
        "DroneCommand" => Message::DroneCommand {
            session_id: session_id(),
            command: number("command") as u8,
            target_id: wide(case, "targetId"),
            drone_ids: case["droneIds"]
                .as_array()
                .unwrap()
                .iter()
                .map(|d| d.as_str().unwrap().parse().unwrap())
                .collect(),
            control_range: case["controlRange"].as_f64().unwrap(),
        },
        "DroneResults" => Message::DroneResults {
            session_id: session_id(),
            command: number("command") as u8,
            results: case["results"]
                .as_array()
                .unwrap()
                .iter()
                .map(|r| (wide(r, "droneId"), text(r, "reason")))
                .collect(),
        },
        "DroneScooped" => Message::DroneScooped { session_id: session_id(), drone_id: wide(case, "droneId") },
        "DroneReleased" => Message::DroneReleased {
            session_id: session_id(),
            drone_id: wide(case, "droneId"),
            position: vector_of(case, "position"),
        },
        "LaunchFighter" => Message::LaunchFighter {
            request_id: wide(case, "requestId") as u64,
            session_id: session_id(),
            ship: Box::new(ship_of(&case["ship"])),
            graphic_id: number("graphicId") as i32,
            cruise_fraction: case["cruiseFraction"].as_f64().unwrap(),
            squadron_size: number("squadronSize") as i32,
        },
        "FighterOrder" => Message::FighterOrder {
            session_id: session_id(),
            command: number("command") as u8,
            target_id: wide(case, "targetId"),
            fighter_ids: case["fighterIds"]
                .as_array()
                .unwrap()
                .iter()
                .map(|f| f.as_str().unwrap().parse().unwrap())
                .collect(),
            range: case["range"].as_f64().unwrap(),
        },
        "FighterResults" => Message::FighterResults {
            session_id: session_id(),
            command: number("command") as u8,
            results: case["results"]
                .as_array()
                .unwrap()
                .iter()
                .map(|r| (wide(r, "fighterId"), text(r, "reason")))
                .collect(),
        },
        "FighterSize" => Message::FighterSize {
            session_id: session_id(),
            fighter_id: wide(case, "fighterId"),
            size: number("size") as i32,
        },
        "FighterGotoPoint" => Message::FighterGotoPoint {
            session_id: session_id(),
            fighter_ids: case["fighterIds"]
                .as_array()
                .unwrap()
                .iter()
                .map(|f| f.as_str().unwrap().parse().unwrap())
                .collect(),
            point: vector_of(case, "point"),
        },
        "FighterLanded" => {
            Message::FighterLanded { session_id: session_id(), fighter_id: wide(case, "fighterId") }
        }
        "FighterReleased" => Message::FighterReleased {
            session_id: session_id(),
            fighter_id: wide(case, "fighterId"),
            position: vector_of(case, "position"),
        },
        "EcmBurst" => Message::EcmBurst {
            session_id: session_id(),
            ship_id: wide(case, "shipId"),
            reached: case["reached"]
                .as_array()
                .unwrap()
                .iter()
                .map(|r| (wide(r, "shipId"), r["jammed"].as_bool().unwrap()))
                .collect(),
        },
        "Destroy" => Message::Destroy {
            ship_id: wide(case, "shipId"),
            credit: Credit {
                character_id: wide(case, "characterId"),
                corporation_id: wide(case, "corporationId"),
                alliance_id: wide(case, "allianceId"),
                war_faction_id: wide(case, "warFactionId"),
            },
        },
        other => panic!("unknown kind {other}"),
    }
}

fn reason_of(error: DecodeError) -> &'static str {
    match error {
        DecodeError::Empty => "empty",
        DecodeError::UnknownKind(_) => "unknown-kind",
        DecodeError::Truncated => "truncated",
        DecodeError::Trailing => "trailing",
        DecodeError::Utf8 => "utf8",
        DecodeError::BadValue(_) => "bad-value",
    }
}

#[test]
fn every_valid_frame_encodes_to_its_bytes_and_decodes_back() {
    let fixtures = fixtures();
    let cases = fixtures["valid"].as_array().unwrap();
    assert!(cases.len() >= 20);
    for case in cases {
        let name = text(case, "name");
        let expected = hex(case["hex"].as_str().unwrap());
        let message = message_of(case);
        assert_eq!(message.encode(), expected, "encoding of {name}");
        // the frame is its length, then the payload
        let payload = &expected[4..];
        assert_eq!(
            u32::from_le_bytes(expected[..4].try_into().unwrap()) as usize,
            payload.len(),
            "length of {name}"
        );
        assert_eq!(Message::decode(payload).unwrap(), message, "decoding of {name}");
    }
}

#[test]
fn a_launch_drone_without_a_weapon_roundtrips_with_the_empty_weapon_record() {
    let mut case = fixtures()["valid"]
        .as_array()
        .unwrap()
        .iter()
        .find(|case| text(case, "name") == "launch of a drone")
        .unwrap()
        .clone();
    case["weapon"] = Value::Null;
    let message = message_of(&case);
    let encoded = message.encode();
    assert_eq!(Message::decode(&encoded[4..]).unwrap(), message);
}

#[test]
fn a_heat_profile_roundtrips_through_the_link() {
    let profile = HeatProfile {
        generation_multiplier: 0.75,
        damage_multiplier: 0.85,
        thermodynamics_level: 3,
        capacity: [100.0; 3],
        initial_rack_heat: [0.0, 1.0, 2.0],
        dissipation_rate: [0.01; 3],
        attenuation: [0.71; 3],
        modules: vec![HeatModuleProfile {
            module_id: 123,
            flag: 27,
            structure_hp: 40.0,
            damage_hp: 2.0,
            heat_damage_hp: 6.6,
            heat_absorption_rate: 0.01,
            overloadable: true,
            heat_immune: false,
            online: true,
        }],
    };
    let message = Message::HeatProfile { session_id: 42, profile: profile.clone() };
    let frame = message.encode();
    assert_eq!(Message::decode(&frame[4..]).unwrap(), message);
}

#[test]
fn charged_actions_roundtrip_cycle_charge_and_fallback_in_the_shared_link() {
    let fixtures = fixtures();
    let case = fixtures["valid"]
        .as_array()
        .unwrap()
        .iter()
        .find(|case| case["name"] == "activate an ECM burst")
        .unwrap();
    let Message::Activate { session_id, effect } = message_of(case) else {
        panic!("fixture must activate an effect")
    };
    let mut effect = *effect;
    effect.action = Some(Action::Charged {
        action: Box::new(Action::Remote {
            what: Remote::Transfer { amount: 60.0 },
            reach: Reach { optimal: 0.0, falloff: 0.0 },
            when: Applies::CycleStart,
            jam: "energyTransfer".to_string(),
        }),
        fallback: Some(Box::new(Action::Repair {
            layer: Layer::Armor,
            amount: 24.0,
            when: Applies::CycleEnd,
        })),
        charge_type_id: 16272,
        charge_flag: 27,
        charge_per_cycle: 3,
        charges: 5,
    });
    let message = Message::Activate { session_id, effect: Box::new(effect) };
    let frame = message.encode();
    assert_eq!(Message::decode(&frame[4..]).unwrap(), message);
}

#[test]
fn module_offline_and_rack_overheat_orders_roundtrip_through_the_link() {
    for message in [
        Message::ModuleOffline { session_id: 42, module_id: 123 },
        Message::RackOverheat { session_id: 42, rack: HeatRack::Medium, active: true },
    ] {
        let frame = message.encode();
        assert_eq!(Message::decode(&frame[4..]).unwrap(), message);
    }
}

#[test]
fn mutadaptive_remote_repair_action_and_cycle_messages_roundtrip_through_the_link() {
    let effect = eve_space::modules::Effect {
        rules: Default::default(),
        burns_fuel: false,
        module_id: 700,
        module_type: 35298,
        effect_id: 8,
        fx: "effects.RemoteArmorRepair".to_string(),
        duration_ms: 6_000.0,
        capacitor_need: 513.0,
        repeat: i32::MAX,
        target_id: 701,
        turret: None,
        action: Some(eve_space::modules::Action::Remote {
            what: eve_space::modules::Remote::MutadaptiveRepair {
                layer: eve_space::health::Layer::Armor,
                amount: 256.0,
                bonus_per_cycle: 0.1,
                bonus_max: 1.5,
            },
            reach: eve_space::modules::Reach { optimal: 12_000.0, falloff: 0.0 },
            when: eve_space::modules::Applies::CycleEnd,
            jam: "remoteArmorRepair".to_string(),
        }),
        on: None,
        off: None,
        on_changes: Vec::new(),
        off_changes: Vec::new(),
    };
    let activation = Message::Activate { session_id: 42, effect: Box::new(effect) };
    let frame = activation.encode();
    assert_eq!(Message::decode(&frame[4..]).unwrap(), activation);

    let messages = [
        Message::RemoteRepairCycle {
            session_id: 42,
            module_id: 700,
            target_id: 701,
            cycle_id: 9,
            cycle_number: 15,
            cycle_at_ms: 123_456,
            cycle_duration_ms: 6_000,
            ends_effect_after_cycle: true,
            base_amount: 256.0,
            bonus_per_cycle: 0.1,
            bonus_max: 1.5,
        },
        Message::RemoteRepairCycleResult {
            session_id: 42,
            module_id: 700,
            target_id: 701,
            cycle_id: 9,
            accepted: true,
            reason: String::new(),
            amount: 640.0,
        },
    ];
    for message in messages {
        let frame = message.encode();
        assert_eq!(Message::decode(&frame[4..]).unwrap(), message);
    }
}

#[test]
fn every_invalid_frame_is_refused_for_the_stated_reason() {
    let fixtures = fixtures();
    for case in fixtures["invalid"].as_array().unwrap() {
        let name = text(case, "name");
        let frame = hex(case["hex"].as_str().unwrap());
        let error = Message::decode(&frame[4..]).unwrap_err();
        assert_eq!(reason_of(error), text(case, "reason"), "{name}");
    }
}
