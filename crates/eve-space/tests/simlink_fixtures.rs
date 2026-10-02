//! Replays oracle/fixtures/simlink/messages.json, the golden frames of docs/adr/0007 (the C# host replays the same
//! file): every valid frame encodes to its bytes and decodes back, every invalid one is refused for the stated reason.

use eve_space::death::Credit;
use eve_space::health::{Condition, Layer, Resonances, Vitals};
use eve_space::link::{
    ChargeOp, ContainerPlacement, DecodeError, Enter, How, Message, Spawn, WreckPlacement,
};
use eve_space::missiles::Missile;
use eve_space::modules::{Action, Applies, Change, Effect, Numbers, Reach, Remote};
use eve_space::ship::{ModuleSpec, NpcCombat, NpcIdentity, NpcWeapon, ShipSpec};
use eve_space::weapons::{Charge, Turret};
use eve_space::wreck::WreckKind;
use serde_json::Value;

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
        }),
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
    Effect {
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
            if a["kind"] == "repair" {
                return Action::Repair { layer: layer(), amount, when };
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
    }
}

fn wreck_kind_of(wreck: Option<&Value>) -> Option<WreckKind> {
    wreck.filter(|w| !w.is_null()).map(|w| WreckKind {
        type_id: w["typeId"].as_i64().unwrap() as i32,
        name: text(w, "name"),
        graphic_id: Some(w["graphicId"].as_i64().unwrap() as i32).filter(|g| *g != 0),
    })
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
fn every_invalid_frame_is_refused_for_the_stated_reason() {
    let fixtures = fixtures();
    for case in fixtures["invalid"].as_array().unwrap() {
        let name = text(case, "name");
        let frame = hex(case["hex"].as_str().unwrap());
        let error = Message::decode(&frame[4..]).unwrap_err();
        assert_eq!(reason_of(error), text(case, "reason"), "{name}");
    }
}
