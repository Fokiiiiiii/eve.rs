//! Turrets: how a shot is resolved (does it hit, how well, how much damage) and what the client is told of it.
//!
//! The rules are the reference's (`server/src/space/combat/laserTurrets.js`), which are TQ's turret mechanics: the chance to hit is
//! `0.5 ^ (tracking² + range²)` with `tracking = angular velocity × turret signature resolution / (tracking speed × target signature radius)` and
//! `range = max(0, surface distance − optimal) / falloff`; one roll decides everything: at or above the chance the shot misses, below 1 % it is a
//! wrecking shot (three times the damage), otherwise the damage is multiplied by `0.49 + roll`. The hit quality the combat log shows is one of the
//! six bands of EVE University's table (`hit_quality_tier`).

use eve_marshal::Value;

use crate::health::Damage;
use crate::modules::Effect;
use crate::npc::is_npc_session;
use crate::scene::{Out, Scene, SessionId};
use crate::wire::{dict, id, int, real, text, tuple};

/// The attributes the damage of a shot is told under in `OnDamageMessage`, in the order em, thermal, kinetic, explosive.
pub const DAMAGE_ATTRIBUTES: [i64; 4] = [114, 118, 117, 116];

/// The signature resolution a turret is taken to have when it says nothing (`optimalSigRadius`; a small turret's is 40, a medium's 125, a
/// large's 400).
pub const DEFAULT_SIGNATURE_RESOLUTION: f64 = 40_000.0;

/// What a turret does: the numbers dogma gives it, worked out by the host (or the NPC's type) and used by the scene.
#[derive(Debug, Clone, PartialEq)]
pub struct Turret {
    /// The type of the weapon (what `OnDamageMessage` calls `weapon` and `OnSpecialFX` the module type).
    pub weapon_type_id: i32,
    /// What the destiny stream calls the effect (`effects.Laser`, `effects.ProjectileFired`, `effects.HybridFired`).
    pub fx: String,
    /// The damage of a shot before the quality of the hit: em, thermal, kinetic, explosive (the damage multiplier is in it).
    pub raw_damage: Damage,
    pub optimal_range: f64,
    pub falloff: f64,
    /// Radians a second.
    pub tracking_speed: f64,
    pub signature_resolution: f64,
    /// The charge the turret fires when it takes one (the host works its damage into `raw_damage`): the scene keeps the rounds and spends them.
    pub charge: Option<Charge>,
}

/// The ammunition in a turret: the kind, the slot it sits in (the client knows a loaded charge by the ship, the slot and the kind), the rounds it holds and the
/// rounds a cycle spends.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Charge {
    pub type_id: i32,
    pub flag: i32,
    pub rounds: i64,
    pub per_cycle: i64,
}

/// Where and how a ship moves, for the sake of a shot.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Kinematics {
    pub position: [f64; 3],
    pub velocity: [f64; 3],
    pub radius: f64,
    /// How big the ship looks to a turret (`signatureRadius`); the radius when it has none.
    pub signature_radius: f64,
}

/// An offensive act: a weapon of `attacker` was fired at `target` (what the crimewatch tells a tree that waits for aggression).
#[derive(Debug, Clone, PartialEq)]
pub struct Aggression {
    pub attacker_ship: i64,
    pub target_ship: i64,
    pub module_type_id: i32,
    /// The character that flies the attacker, or its owner.
    pub source_owner_id: i64,
    pub target_type_id: i32,
}

/// What came of a shot.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Shot {
    pub hit: bool,
    pub wrecking: bool,
    /// The multiplier of the damage: 0 for a miss, 3 for a wrecking shot, else `0.49 + roll`.
    pub quality: f64,
    pub chance_to_hit: f64,
    pub roll: f64,
    pub damage: Damage,
}

fn round6(value: f64) -> f64 {
    (value * 1_000_000.0).round() / 1_000_000.0
}

fn sub(a: [f64; 3], b: [f64; 3]) -> [f64; 3] {
    [a[0] - b[0], a[1] - b[1], a[2] - b[2]]
}

fn dot(a: [f64; 3], b: [f64; 3]) -> f64 {
    a[0] * b[0] + a[1] * b[1] + a[2] * b[2]
}

fn length(a: [f64; 3]) -> f64 {
    dot(a, a).sqrt()
}

/// The distance between the surfaces of two ships.
pub fn surface_distance(attacker: &Kinematics, target: &Kinematics) -> f64 {
    (length(sub(target.position, attacker.position)) - attacker.radius.max(0.0) - target.radius.max(0.0))
        .max(0.0)
}

/// How fast the target crosses the attacker's line of sight, in radians a second.
pub fn angular_velocity(attacker: &Kinematics, target: &Kinematics) -> f64 {
    let relative = sub(target.position, attacker.position);
    let centre = length(relative).max(1.0);
    let unit = [relative[0] / centre, relative[1] / centre, relative[2] / centre];
    let velocity = sub(target.velocity, attacker.velocity);
    let radial = dot(velocity, unit);
    let tangential =
        [velocity[0] - unit[0] * radial, velocity[1] - unit[1] * radial, velocity[2] - unit[2] * radial];
    length(tangential) / centre
}

/// The chance that a shot hits (0 for a turret that cannot track, or that shoots past its optimal range with no falloff).
pub fn chance_to_hit(turret: &Turret, attacker: &Kinematics, target: &Kinematics) -> f64 {
    let distance = surface_distance(attacker, target);
    let angular = angular_velocity(attacker, target);
    let target_signature =
        if target.signature_radius > 0.0 { target.signature_radius } else { target.radius.max(1.0) };
    let resolution = turret.signature_resolution.max(1.0);
    let tracking_term = if turret.tracking_speed > 0.0 {
        angular * resolution / (turret.tracking_speed * target_signature.max(1.0))
    } else {
        f64::INFINITY
    };
    let optimal = turret.optimal_range.max(0.0);
    let range_term = if distance > optimal {
        if turret.falloff > 0.0 { (distance - optimal) / turret.falloff } else { f64::INFINITY }
    } else {
        0.0
    };
    if !tracking_term.is_finite() || !range_term.is_finite() {
        return 0.0;
    }
    0.5_f64.powf(tracking_term.powi(2) + range_term.powi(2)).clamp(0.0, 1.0)
}

/// One shot, decided by `roll` (a number of `[0, 1)`).
pub fn resolve_shot(turret: &Turret, attacker: &Kinematics, target: &Kinematics, roll: f64) -> Shot {
    let chance = chance_to_hit(turret, attacker, target);
    let roll = roll.clamp(0.0, 0.999_999_999);
    let total: f64 = turret.raw_damage.iter().sum();
    if total <= 0.0 || roll >= chance {
        return Shot {
            hit: false,
            wrecking: false,
            quality: 0.0,
            chance_to_hit: round6(chance),
            roll: round6(roll),
            damage: [0.0; 4],
        };
    }
    let wrecking = roll < 0.01;
    let quality = if wrecking { 3.0 } else { round6(0.49 + roll) };
    Shot {
        hit: true,
        wrecking,
        quality,
        chance_to_hit: round6(chance),
        roll: round6(roll),
        damage: turret.raw_damage.map(|d| round6(d * quality)),
    }
}

/// The hit quality the combat log shows, 0 to 6: a miss, grazes, glances off, hits, penetrates, smashes, wrecks (EVE University's bands of the
/// damage multiplier).
pub fn hit_quality_tier(shot: &Shot) -> u8 {
    if !shot.hit {
        return 0;
    }
    match shot.quality {
        q if q >= 3.0 => 6,
        q if q >= 1.25 => 5,
        q if q >= 1.0 => 4,
        q if q >= 0.75 => 3,
        q if q >= 0.625 => 2,
        _ => 1,
    }
}

/// The `OnDamageMessage` a shot makes: for the pilot of the ship that shot (`attackType` `me`) or for the pilot of the ship that was shot
/// (`otherPlayerWeapons`, with the attacker's id). `owner` is the character, or the corporation, that owns the attacking ship.
#[allow(clippy::too_many_arguments)]
pub fn damage_message(
    for_the_attacker: bool,
    attacker_id: i64,
    target_id: i64,
    weapon_type_id: i32,
    applied: f64,
    shot: &Shot,
    owner_id: i64,
) -> Value {
    let attributes = Value::Dict(
        DAMAGE_ATTRIBUTES
            .iter()
            .zip(shot.damage)
            .map(|(attribute, amount)| (int(*attribute), number6(amount)))
            .collect(),
    );
    let mut entries: Vec<(&str, Value)> = vec![
        ("attackType", text(if for_the_attacker { "me" } else { "otherPlayerWeapons" })),
        ("source", id(attacker_id)),
        ("target", id(target_id)),
        ("weapon", int(i64::from(weapon_type_id))),
        ("damage", number6(applied)),
        ("damageAttributes", attributes),
        ("splash", text("")),
        ("hitQuality", int(i64::from(hit_quality_tier(shot)))),
        ("isBanked", Value::Bool(false)),
    ];
    if owner_id > 0 {
        entries.push(("owner", tuple([int(2), id(owner_id)])));
    }
    if !for_the_attacker && attacker_id > 0 {
        entries.push(("attackerID", id(attacker_id)));
    }
    dict(entries)
}

/// A number as the reference marshals it: whole numbers are ints, the rest are rounded to six places.
fn number6(value: f64) -> Value {
    let rounded = round6(value);
    if rounded.fract() == 0.0 && rounded.abs() < 9.0e15 { int(rounded as i64) } else { real(rounded) }
}

impl Scene {
    /// Whether the weapon of a running effect still has what it shoots at: the target is in the scene and the ship has it locked.
    pub(crate) fn turret_target_ok(&self, session: SessionId, effect: &Effect) -> bool {
        self.session_of(effect.target_id).is_some() && self.locked_by(session).contains(&effect.target_id)
    }

    /// The kinematics of a ship of the scene, for a shot.
    fn kinematics_of(&self, session: SessionId) -> Option<Kinematics> {
        let ship = &self.pilots.get(&session)?.ship;
        let (p, v) = (ship.position, ship.velocity);
        Some(Kinematics {
            position: [p.x, p.y, p.z],
            velocity: [v.x, v.y, v.z],
            radius: ship.spec.radius,
            signature_radius: ship.spec.signature_radius,
        })
    }

    /// The character that owns a ship, or its owner when nobody flies it (what the combat log says the shot was fired by).
    pub(crate) fn combat_owner(&self, session: SessionId) -> i64 {
        self.pilots.get(&session).map_or(0, |pilot| {
            let spec = &pilot.ship.spec;
            if spec.character_id > 0 { spec.character_id } else { spec.owner_id }
        })
    }

    /// The ships of the scene within `range` of the surface of the ship of `session`, not its own and not in warp: `(session, ship id)`, by ship id.
    pub(crate) fn in_smartbomb_range(&self, session: SessionId, range: f64) -> Vec<(SessionId, i64)> {
        let mut inside: Vec<(SessionId, i64)> = self
            .pilots
            .iter()
            .filter(|(other, pilot)| {
                **other != session && !pilot.flight.as_ref().is_some_and(|flight| flight.is_warping())
            })
            .filter(|(other, _)| self.apart(session, **other).is_some_and(|apart| apart <= range + 1e-6))
            .map(|(other, pilot)| (*other, pilot.ship.spec.ship_id))
            .collect();
        inside.sort_by_key(|(_, ship_id)| *ship_id);
        inside
    }

    /// A smartbomb goes off as a cycle begins: every ship in its range takes the damage whole (a ship in its undock protection does not), and each is aggressed and
    /// told as a shot is. Where the safety forbids it (`hurts_pilots` false) and a pilot is in range, nothing is hurt and the effect stops at once.
    pub(crate) fn smartbomb(
        &mut self,
        session: SessionId,
        effect: &Effect,
        range: f64,
        damage: Damage,
        hurts_pilots: bool,
        at: i64,
    ) -> Vec<Out> {
        let Some(attacker_id) = self.pilots.get(&session).map(|pilot| pilot.ship.spec.ship_id) else {
            return Vec::new();
        };
        let in_range = self.in_smartbomb_range(session, range);
        if !hurts_pilots && in_range.iter().any(|(other, _)| !is_npc_session(*other)) {
            return self.stop_module(session, effect.module_id, at);
        }
        let owner = self.combat_owner(session);
        let mut out = Vec::new();
        for (target_session, target_id) in in_range {
            if self.pilots.get(&target_session).is_none_or(|pilot| pilot.protected_until > at) {
                continue;
            }
            let shot =
                Shot { hit: true, wrecking: false, quality: 1.0, chance_to_hit: 1.0, roll: 0.0, damage };
            let (told, hit) = self.damage(target_id, damage, at);
            out.extend(told);
            let applied = hit.map_or(0.0, |hit| hit.taken.shield + hit.taken.armor + hit.taken.structure);
            let destroyed = hit.is_some_and(|hit| hit.destroyed);
            let target_type_id = self.pilots.get(&target_session).map_or(0, |pilot| pilot.ship.spec.type_id);
            self.aggressions.push(Aggression {
                attacker_ship: attacker_id,
                target_ship: target_id,
                module_type_id: effect.module_type,
                source_owner_id: owner,
                target_type_id,
            });
            let message = |for_the_attacker| {
                damage_message(
                    for_the_attacker,
                    attacker_id,
                    target_id,
                    effect.module_type,
                    applied,
                    &shot,
                    owner,
                )
            };
            for (who, mine) in [(session, true), (target_session, false)] {
                if !is_npc_session(who) {
                    out.push(Out::Notify {
                        session: who,
                        name: "OnDamageMessage",
                        id_type: "clientID",
                        args: vec![message(mine)],
                    });
                }
            }
            if destroyed {
                out.extend(self.destroy(target_session, Some(session), at));
            }
        }
        out
    }

    /// A turret shoots at the start of a cycle: one shot, rolled; a hit hurts the target (whoever sees it is told its damage state, its pilot the
    /// attributes that changed), and the combat log of each pilot is told the shot, hit or miss.
    pub(crate) fn fire_turret(&mut self, session: SessionId, effect: &Effect, at: i64) -> Vec<Out> {
        let Some(turret) = effect.turret.as_ref() else { return Vec::new() };
        let Some(target_session) = self.session_of(effect.target_id) else { return Vec::new() };
        let (Some(attacker), Some(target)) =
            (self.kinematics_of(session), self.kinematics_of(target_session))
        else {
            return Vec::new();
        };
        let attacker_id = self.pilots[&session].ship.spec.ship_id;
        // what disrupts the shooter's turrets works on the numbers the shot is rolled with
        let (range, falloff, tracking) = self.pilots[&session].incoming.weapon_factors();
        let disrupted;
        let turret = if (range, falloff, tracking) == (1.0, 1.0, 1.0) {
            turret
        } else {
            disrupted = Turret {
                optimal_range: turret.optimal_range * range,
                falloff: turret.falloff * falloff,
                tracking_speed: turret.tracking_speed * tracking,
                ..turret.clone()
            };
            &disrupted
        };
        let roll = self.rng.random();
        let shot = resolve_shot(turret, &attacker, &target, roll);
        let mut out = Vec::new();
        let mut applied = 0.0;
        let mut destroyed = false;
        if shot.hit {
            let (told, hit) = self.damage(effect.target_id, shot.damage, at);
            out.extend(told);
            applied = hit.map_or(0.0, |hit| hit.taken.shield + hit.taken.armor + hit.taken.structure);
            destroyed = hit.is_some_and(|hit| hit.destroyed);
        }
        let owner = self.combat_owner(session);
        let target_type_id = self.pilots.get(&target_session).map_or(0, |pilot| pilot.ship.spec.type_id);
        self.aggressions.push(Aggression {
            attacker_ship: attacker_id,
            target_ship: effect.target_id,
            module_type_id: turret.weapon_type_id,
            source_owner_id: owner,
            target_type_id,
        });
        let message = |for_the_attacker| {
            damage_message(
                for_the_attacker,
                attacker_id,
                effect.target_id,
                turret.weapon_type_id,
                applied,
                &shot,
                owner,
            )
        };
        if !is_npc_session(session) {
            out.push(Out::Notify {
                session,
                name: "OnDamageMessage",
                id_type: "clientID",
                args: vec![message(true)],
            });
        }
        if !is_npc_session(target_session) {
            out.push(Out::Notify {
                session: target_session,
                name: "OnDamageMessage",
                id_type: "clientID",
                args: vec![message(false)],
            });
        }
        // the shot that took the last of the hull is told first, then the ship is gone
        if destroyed {
            out.extend(self.destroy(target_session, Some(session), at));
        }
        out
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn ship(position: [f64; 3], velocity: [f64; 3], radius: f64, signature: f64) -> Kinematics {
        Kinematics { position, velocity, radius, signature_radius: signature }
    }

    fn turret(damage: Damage, tracking: f64, resolution: f64, optimal: f64, falloff: f64) -> Turret {
        Turret {
            weapon_type_id: 23_312,
            fx: "effects.Laser".to_string(),
            raw_damage: damage,
            optimal_range: optimal,
            falloff,
            tracking_speed: tracking,
            signature_resolution: resolution,
            charge: None,
        }
    }

    /// What the reference's `resolveTurretShot` answers for these (generated with `node`, `tools/oracle`-style, from `laserTurrets.js`).
    struct Case {
        target: Kinematics,
        turret: Turret,
        roll: f64,
        hit: bool,
        wrecking: bool,
        quality: f64,
        chance: f64,
        damage: Damage,
        tier: u8,
    }

    fn cases() -> Vec<Case> {
        let frigate = |t: Kinematics| t;
        let laser = turret([0.0, 14.087394, 14.087394, 0.0], 0.4, 40.0, 2_500.0, 5_000.0);
        let runner = ship([5_000.0, 0.0, 0.0], [0.0, 100.0, 0.0], 47.0, 28.0);
        let cruiser = ship([20_000.0, 3_000.0, 0.0], [50.0, 300.0, 0.0], 47.0, 120.0);
        let far_gun = turret([10.0, 0.0, 0.0, 20.0], 0.05, 125.0, 8_000.0, 6_000.0);
        let still = ship([3_000.0, 0.0, 0.0], [0.0; 3], 10.0, 50.0);
        let box_gun = |optimal, falloff, tracking| turret([5.0; 4], tracking, 40.0, optimal, falloff);
        vec![
            Case {
                target: frigate(runner),
                turret: laser.clone(),
                roll: 0.3,
                hit: true,
                wrecking: false,
                quality: 0.79,
                chance: 0.84769,
                damage: [0.0, 11.129041, 11.129041, 0.0],
                tier: 3,
            },
            Case {
                target: runner,
                turret: laser.clone(),
                roll: 0.005,
                hit: true,
                wrecking: true,
                quality: 3.0,
                chance: 0.84769,
                damage: [0.0, 42.262182, 42.262182, 0.0],
                tier: 6,
            },
            Case {
                target: runner,
                turret: laser,
                roll: 0.9,
                hit: false,
                wrecking: false,
                quality: 0.0,
                chance: 0.84769,
                damage: [0.0; 4],
                tier: 0,
            },
            Case {
                target: cruiser,
                turret: far_gun.clone(),
                roll: 0.05,
                hit: true,
                wrecking: false,
                quality: 0.54,
                chance: 0.055099,
                damage: [5.4, 0.0, 0.0, 10.8],
                tier: 1,
            },
            Case {
                target: cruiser,
                turret: far_gun,
                roll: 0.9,
                hit: false,
                wrecking: false,
                quality: 0.0,
                chance: 0.055099,
                damage: [0.0; 4],
                tier: 0,
            },
            Case {
                target: ship([1_000.0, 0.0, 0.0], [0.0; 3], 10.0, 0.0),
                turret: box_gun(2_000.0, 1_000.0, 0.0),
                roll: 0.0,
                hit: false,
                wrecking: false,
                quality: 0.0,
                chance: 0.0,
                damage: [0.0; 4],
                tier: 0,
            },
            Case {
                target: ship([9_000.0, 0.0, 0.0], [0.0; 3], 10.0, 50.0),
                turret: box_gun(2_000.0, 0.0, 1.0),
                roll: 0.2,
                hit: false,
                wrecking: false,
                quality: 0.0,
                chance: 0.0,
                damage: [0.0; 4],
                tier: 0,
            },
            Case {
                target: still,
                turret: box_gun(2_000.0, 0.0, 1.0),
                roll: 0.6,
                hit: false,
                wrecking: false,
                quality: 0.0,
                chance: 0.0,
                damage: [0.0; 4],
                tier: 0,
            },
            Case {
                target: still,
                turret: box_gun(5_000.0, 1_000.0, 1.0),
                roll: 0.6,
                hit: true,
                wrecking: false,
                quality: 1.09,
                chance: 1.0,
                damage: [5.45; 4],
                tier: 4,
            },
            Case {
                target: still,
                turret: box_gun(5_000.0, 1_000.0, 1.0),
                roll: 0.3,
                hit: true,
                wrecking: false,
                quality: 0.79,
                chance: 1.0,
                damage: [3.95; 4],
                tier: 3,
            },
            Case {
                target: still,
                turret: box_gun(5_000.0, 1_000.0, 1.0),
                roll: 0.99,
                hit: true,
                wrecking: false,
                quality: 1.48,
                chance: 1.0,
                damage: [7.4; 4],
                tier: 5,
            },
        ]
    }

    #[test]
    fn a_shot_is_resolved_as_the_reference_resolves_it() {
        let attacker = ship([0.0; 3], [0.0; 3], 38.0, 0.0);
        for (index, case) in cases().into_iter().enumerate() {
            let shot = resolve_shot(&case.turret, &attacker, &case.target, case.roll);
            assert_eq!(shot.hit, case.hit, "case {index}");
            assert_eq!(shot.wrecking, case.wrecking, "case {index}");
            assert!((shot.quality - case.quality).abs() < 1e-9, "case {index}: {}", shot.quality);
            assert!((shot.chance_to_hit - case.chance).abs() < 1e-6, "case {index}: {}", shot.chance_to_hit);
            for (got, want) in shot.damage.iter().zip(case.damage) {
                assert!((got - want).abs() < 1e-6, "case {index}: {:?} for {:?}", shot.damage, case.damage);
            }
            assert_eq!(hit_quality_tier(&shot), case.tier, "case {index}");
        }
    }

    #[test]
    fn a_roll_a_little_over_one_percent_is_not_a_wrecking_shot() {
        let attacker = ship([0.0; 3], [0.0; 3], 38.0, 0.0);
        let still = ship([3_000.0, 0.0, 0.0], [0.0; 3], 10.0, 50.0);
        let gun = turret([5.0; 4], 1.0, 40.0, 5_000.0, 1_000.0);
        let over = resolve_shot(&gun, &attacker, &still, 0.015);
        assert!(over.hit && !over.wrecking);
        assert!((over.quality - 0.505).abs() < 1e-9, "{}", over.quality);
        let at = resolve_shot(&gun, &attacker, &still, 0.01);
        assert!(!at.wrecking, "the roll has to be below one percent");
        let under = resolve_shot(&gun, &attacker, &still, 0.009);
        assert!(under.wrecking && under.quality == 3.0);
    }

    #[test]
    fn distances_and_angles_are_measured_between_surfaces_and_centres() {
        let attacker = ship([0.0; 3], [0.0; 3], 38.0, 0.0);
        let target = ship([5_000.0, 0.0, 0.0], [0.0, 100.0, 0.0], 47.0, 28.0);
        assert_eq!(surface_distance(&attacker, &target), 4_915.0);
        assert_eq!(angular_velocity(&attacker, &target), 0.02);
        // moving straight at or away from the attacker is no angular speed at all
        let coming = ship([5_000.0, 0.0, 0.0], [-300.0, 0.0, 0.0], 47.0, 28.0);
        assert!(angular_velocity(&attacker, &coming).abs() < 1e-12);
        // two ships that overlap are no distance apart
        assert_eq!(surface_distance(&attacker, &ship([10.0, 0.0, 0.0], [0.0; 3], 50.0, 1.0)), 0.0);
    }

    #[test]
    fn a_target_without_a_signature_is_as_big_as_its_radius() {
        let attacker = ship([0.0; 3], [0.0; 3], 10.0, 0.0);
        let gun = turret([1.0; 4], 0.4, 40.0, 5_000.0, 1_000.0);
        let with = ship([2_000.0, 0.0, 0.0], [0.0, 50.0, 0.0], 30.0, 30.0);
        let without = ship([2_000.0, 0.0, 0.0], [0.0, 50.0, 0.0], 30.0, 0.0);
        assert_eq!(chance_to_hit(&gun, &attacker, &with), chance_to_hit(&gun, &attacker, &without));
    }

    #[test]
    fn a_roll_of_the_chance_exactly_is_a_miss_and_nothing_beats_a_chance_of_nothing() {
        let attacker = ship([0.0; 3], [0.0; 3], 10.0, 0.0);
        let target = ship([3_000.0, 0.0, 0.0], [0.0; 3], 10.0, 50.0);
        let sure = turret([1.0; 4], 1.0, 40.0, 5_000.0, 1_000.0);
        assert!(resolve_shot(&sure, &attacker, &target, 0.999_999).hit);
        let uncertain = turret([1.0; 4], 1.0, 40.0, 1_000.0, 4_000.0);
        let chance = chance_to_hit(&uncertain, &attacker, &target);
        assert!(chance > 0.0 && chance < 1.0, "{chance}");
        assert!(resolve_shot(&uncertain, &attacker, &target, chance - 1e-9).hit);
        assert!(
            !resolve_shot(&uncertain, &attacker, &target, chance).hit,
            "a roll of the chance itself misses"
        );
        let blind = turret([1.0; 4], 0.0, 40.0, 5_000.0, 1_000.0);
        assert!(
            !resolve_shot(&blind, &attacker, &target, 0.0).hit,
            "not even a roll of nothing hits what cannot be tracked"
        );
        let harmless = turret([0.0; 4], 1.0, 40.0, 5_000.0, 1_000.0);
        assert!(!resolve_shot(&harmless, &attacker, &target, 0.5).hit);
    }

    #[test]
    fn the_quality_bands_break_at_the_eighths_of_the_range() {
        let shot = |quality| Shot {
            hit: true,
            wrecking: false,
            quality,
            chance_to_hit: 1.0,
            roll: 0.0,
            damage: [1.0; 4],
        };
        assert_eq!(hit_quality_tier(&shot(0.5)), 1);
        assert_eq!(hit_quality_tier(&shot(0.624_999)), 1);
        assert_eq!(hit_quality_tier(&shot(0.625)), 2);
        assert_eq!(hit_quality_tier(&shot(0.749_999)), 2);
        assert_eq!(hit_quality_tier(&shot(0.75)), 3);
        assert_eq!(hit_quality_tier(&shot(0.999_999)), 3);
        assert_eq!(hit_quality_tier(&shot(1.0)), 4);
        assert_eq!(hit_quality_tier(&shot(1.249_999)), 4);
        assert_eq!(hit_quality_tier(&shot(1.25)), 5);
        assert_eq!(hit_quality_tier(&shot(1.49)), 5);
        assert_eq!(hit_quality_tier(&shot(3.0)), 6);
        assert_eq!(hit_quality_tier(&Shot { hit: false, ..shot(1.0) }), 0);
    }

    #[test]
    fn the_message_to_the_one_who_was_shot_names_the_attacker_and_the_message_to_the_shooter_does_not() {
        let shot = Shot {
            hit: true,
            wrecking: false,
            quality: 1.09,
            chance_to_hit: 1.0,
            roll: 0.6,
            damage: [0.0, 14.087394, 14.087394, 0.0],
        };
        let to_the_target =
            damage_message(false, 980_050_000_109, 9_988_400_002_259, 23_312, 18.144563, &shot, 1_000_127);
        let Value::Dict(entries) = &to_the_target else { panic!("{to_the_target:?}") };
        let keys: Vec<&Value> = entries.iter().map(|(k, _)| k).collect();
        let names: Vec<String> = keys.iter().map(|k| format!("{k:?}")).collect();
        assert!(names.last().unwrap().contains("attackerID"), "{names:?}");
        let get = |name: &str| {
            entries.iter().find(|(k, _)| format!("{k:?}").contains(name)).map(|(_, v)| v.clone()).unwrap()
        };
        assert_eq!(get("hitQuality"), int(4));
        assert_eq!(get("weapon"), int(23_312));
        assert_eq!(get("owner"), tuple([int(2), id(1_000_127)]));
        assert_eq!(get("damage"), real(18.144563));
        let to_the_shooter =
            damage_message(true, 980_050_000_109, 9_988_400_002_259, 23_312, 18.144563, &shot, 1_000_127);
        let Value::Dict(mine) = &to_the_shooter else { panic!() };
        assert!(mine.iter().all(|(k, _)| !format!("{k:?}").contains("attackerID")));
        assert!(mine.iter().any(|(k, v)| format!("{k:?}").contains("attackType") && *v == text("me")));
        let anonymous = damage_message(true, 1, 2, 3, 0.0, &shot, 0);
        let Value::Dict(none) = &anonymous else { panic!() };
        assert!(none.iter().all(|(k, _)| !format!("{k:?}").contains("owner")), "no owner, no owner tuple");
    }

    #[test]
    fn damage_is_told_by_attribute_in_the_order_em_thermal_kinetic_explosive_with_whole_numbers_as_ints() {
        let shot = Shot {
            hit: true,
            wrecking: false,
            quality: 1.0,
            chance_to_hit: 1.0,
            roll: 0.0,
            damage: [0.0, 14.087394, 14.087394, 0.0],
        };
        let message = damage_message(false, 1, 2, 3, 28.174788, &shot, 5);
        let Value::Dict(entries) = message else { panic!() };
        let (_, attributes) =
            entries.iter().find(|(k, _)| format!("{k:?}").contains("damageAttributes")).unwrap();
        assert_eq!(
            *attributes,
            Value::Dict(vec![
                (int(114), int(0)),
                (int(118), real(14.087394)),
                (int(117), real(14.087394)),
                (int(116), int(0))
            ])
        );
    }

    // ---- turrets in a scene ----

    use crate::modules::Change;
    use crate::npc::{NpcPlace, is_npc_session};
    use crate::scene::tests::{NOON, STATION, spec, system};
    use crate::scene::{Command, Scene};
    use crate::ship::{NpcIdentity, ShipSpec};
    use crate::targeting::Order;

    const PLAYER: SessionId = 7;
    const SECOND: i64 = 10_000_000;
    const NPC_SHIP: i64 = 980_050_000_109;

    fn npc_spec() -> ShipSpec {
        let mut npc = spec();
        npc.ship_id = NPC_SHIP;
        npc.character_id = 0;
        npc.owner_id = 1_000_127;
        npc.corporation_id = 1_000_127;
        npc.category_id = 11;
        npc.name = String::new();
        npc
    }

    fn laser() -> Turret {
        turret([0.0, 14.0, 14.0, 0.0], 5.0, 40.0, 10_000.0, 5_000.0)
    }

    fn laser_effect(target_id: i64, turret: Turret) -> Effect {
        Effect {
            module_id: NPC_SHIP,
            module_type: 23_312,
            effect_id: 10,
            fx: "effects.Laser".to_string(),
            duration_ms: 2_500.0,
            capacitor_need: 0.0,
            repeat: 17_280,
            target_id,
            turret: Some(turret),
            action: None,
            on: None,
            off: None,
            on_changes: Vec::new(),
            off_changes: Vec::new(),
        }
    }

    /// A player who has bound the ballpark, and an NPC two kilometres off that has locked it (the moment is a minute after noon).
    fn duel() -> (Scene, SessionId, i64) {
        let mut scene = Scene::new(system());
        scene.undock(PLAYER, spec(), STATION, NOON).unwrap();
        scene.bind(PLAYER, NOON);
        scene.command(PLAYER, Command::Stop, NOON);
        let here = scene.ship(PLAYER, NOON).unwrap().position;
        let place = NpcPlace::at_rest(eve_destiny::Vec3::new(here.x + 2_000.0, here.y, here.z));
        let (npc, _) = scene.spawn_npc(npc_spec(), NpcIdentity::default(), place, NOON);
        let now = NOON + 60 * SECOND;
        scene.poll(now);
        scene.target_order(npc, Order::Lock(spec().ship_id), now);
        let mut now = now;
        for _ in 0..40 {
            now += SECOND / 4;
            scene.poll(now);
        }
        assert_eq!(scene.locked_by(npc), [spec().ship_id], "the lock is had");
        (scene, npc, now)
    }

    fn messages(out: &[Out], session: SessionId, name: &str) -> Vec<Value> {
        out.iter()
            .filter_map(|o| match o {
                Out::Notify { session: s, name: n, args, .. } if *s == session && *n == name => {
                    Some(args[0].clone())
                }
                _ => None,
            })
            .collect()
    }

    fn entry(message: &Value, key: &str) -> Value {
        let Value::Dict(entries) = message else { panic!("{message:?}") };
        entries
            .iter()
            .find(|(k, _)| format!("{k:?}").contains(key))
            .map(|(_, v)| v.clone())
            .unwrap_or(Value::None)
    }

    #[test]
    fn a_weapon_wants_a_target_and_a_lock_on_it() {
        let (mut scene, npc, now) = duel();
        let refused = |told: Vec<Out>| {
            told.iter().find_map(|o| match o {
                Out::Refused { reason, .. } => Some(*reason),
                _ => None,
            })
        };
        assert_eq!(refused(scene.activate(npc, laser_effect(0, laser()), now)), Some("NO_TARGET"));
        // what is not in the scene is not there; a ship that is, is not shot at before it is locked
        assert_eq!(refused(scene.activate(npc, laser_effect(1, laser()), now)), Some("TARGET_NOT_FOUND"));
        assert_eq!(
            refused(scene.activate(npc, laser_effect(npc_spec().ship_id, laser()), now)),
            Some("TARGET_NOT_LOCKED")
        );
        assert_eq!(refused(scene.activate(npc, laser_effect(spec().ship_id, laser()), now)), None);
    }

    /// The names of the notifications of these kinds that a ship is sent, in the order they come.
    fn order(out: &[Out], session: SessionId) -> Vec<&'static str> {
        out.iter()
            .filter_map(|o| match o {
                Out::Notify { session: s, name, .. }
                    if *s == session
                        && matches!(
                            *name,
                            "DoDestinyUpdate" | "OnModuleAttributeChanges" | "OnGodmaShipEffect" | "OnTarget"
                        ) =>
                {
                    Some(*name)
                }
                _ => None,
            })
            .collect()
    }

    fn gun_told_its_numbers() -> Effect {
        let mut effect = laser_effect(spec().ship_id, laser());
        let told = |value| Change { item_id: NPC_SHIP, attribute_id: 54, value };
        effect.on_changes = vec![told(10_000.0)];
        effect.off_changes = vec![told(10_000.0)];
        effect
    }

    #[test]
    fn a_turret_plays_its_effect_before_its_numbers_are_told_and_the_effect_is_on() {
        let (mut scene, npc, now) = duel();
        let started = order(&scene.activate(npc, gun_told_its_numbers(), now), npc);
        let at = started.iter().position(|n| *n == "DoDestinyUpdate").expect("the effect is played");
        assert_eq!(
            started[at..at + 3],
            ["DoDestinyUpdate", "OnModuleAttributeChanges", "OnGodmaShipEffect"],
            "{started:?}"
        );
    }

    #[test]
    fn a_weapon_stops_at_once_when_its_lock_is_given_up_and_the_ship_is_told_after_it_is_off() {
        let (mut scene, npc, now) = duel();
        scene.activate(npc, gun_told_its_numbers(), now);
        let stopped = scene.target_order(npc, Order::Remove(vec![spec().ship_id]), now + SECOND / 2);
        // the effect is taken off in the order the reference does it for a lock that goes: the update, the numbers, the effect, the lock lost
        assert_eq!(
            order(&stopped, npc),
            ["DoDestinyUpdate", "OnModuleAttributeChanges", "OnGodmaShipEffect", "OnTarget"],
            "{:?}",
            order(&stopped, npc)
        );
        // and it is not shot again: the cycle that was running is not finished
        let mut later = Vec::new();
        let mut at = now + SECOND / 2;
        for _ in 0..40 {
            at += SECOND / 4;
            later.extend(scene.poll(at));
        }
        assert!(!order(&later, npc).contains(&"OnGodmaShipEffect"), "{:?}", order(&later, npc));
        assert!(messages(&later, PLAYER, "OnDamageMessage").is_empty());
    }

    #[test]
    fn clearing_the_targets_stops_the_weapons_aimed_at_them_too() {
        let (mut scene, npc, now) = duel();
        scene.activate(npc, gun_told_its_numbers(), now);
        let stopped = order(&scene.target_order(npc, Order::Clear, now + SECOND / 2), npc);
        assert_eq!(
            stopped,
            ["DoDestinyUpdate", "OnModuleAttributeChanges", "OnGodmaShipEffect", "OnTarget"],
            "{stopped:?}"
        );
    }

    #[test]
    fn a_turret_that_is_disrupted_is_rolled_with_the_numbers_it_is_left_and_the_shot_misses_where_it_hit() {
        // undisrupted, the laser (10 km and 5 km of falloff) hits the player two kilometres off at the first cycle
        let (mut scene, npc, now) = duel();
        let free = scene.activate(npc, laser_effect(spec().ship_id, laser()), now);
        let free = messages(&free, PLAYER, "OnDamageMessage");
        assert_ne!(entry(&free[0], "damage"), number6(0.0), "{free:?}");
        // a tracking disruptor that takes the whole of its range and falloff leaves it nothing to reach with
        let (mut scene, npc, now) = duel();
        let until = now + 60 * SECOND;
        scene.pilots.get_mut(&npc).unwrap().incoming.upsert(
            777,
            888,
            crate::ewar::Lasting::Disrupt {
                range_percent: -100.0,
                falloff_percent: -100.0,
                tracking_percent: 0.0,
            },
            until,
        );
        let disrupted = scene.activate(npc, laser_effect(spec().ship_id, laser()), now);
        let disrupted = messages(&disrupted, PLAYER, "OnDamageMessage");
        assert_eq!(entry(&disrupted[0], "damage"), number6(0.0), "{disrupted:?}");
    }

    #[test]
    fn a_turret_shoots_at_the_start_of_each_cycle_and_the_one_shot_at_is_told() {
        let (mut scene, npc, now) = duel();
        let started = scene.activate(npc, laser_effect(spec().ship_id, laser()), now);
        // a hit at the first cycle: damage message to the player, the shield's charge, the damage state and the effect
        let to_the_player = messages(&started, PLAYER, "OnDamageMessage");
        assert_eq!(to_the_player.len(), 1, "{started:?}");
        assert_eq!(entry(&to_the_player[0], "attackType"), text("otherPlayerWeapons"));
        assert_eq!(entry(&to_the_player[0], "attackerID"), id(NPC_SHIP));
        assert_eq!(entry(&to_the_player[0], "target"), id(spec().ship_id));
        assert_eq!(entry(&to_the_player[0], "weapon"), int(23_312));
        assert_eq!(entry(&to_the_player[0], "owner"), tuple([int(2), id(1_000_127)]));
        assert!(messages(&started, npc, "OnDamageMessage").is_empty(), "nothing is sent to an NPC");
        assert!(
            started.iter().any(|o| o.session() == PLAYER
                && matches!(o, Out::Notify { name: "OnModuleAttributeChanges", .. }))
        );
        // the shot is an offensive act, which the crimewatch tells those who wait for one
        assert_eq!(
            scene.aggressions,
            [Aggression {
                attacker_ship: NPC_SHIP,
                target_ship: spec().ship_id,
                module_type_id: 23_312,
                source_owner_id: 1_000_127,
                target_type_id: spec().type_id,
            }]
        );
        // one more cycle, one more shot
        let cycle = scene.poll(now + 2_500 * SECOND / 1_000 + 1);
        assert_eq!(messages(&cycle, PLAYER, "OnDamageMessage").len(), 1);
        assert_eq!(scene.aggressions.len(), 1, "the act of this poll waits for the next one to be told");
        scene.poll(now + 2_500 * SECOND / 1_000 + 2);
        assert!(scene.aggressions.is_empty(), "no tree hears of the acts, so a poll does not keep them");
        // the shield has taken what the shots did
        let condition = scene.ship(PLAYER, now + 3 * SECOND).unwrap().health.condition_at(now + 3 * SECOND);
        assert!(condition.shield_charge < 1.0, "{condition:?}");
    }

    /// A gun that fires antimatter, with this many rounds in it and a cycle that spends one.
    fn charged_gun(rounds: i64) -> Effect {
        let mut gun = laser_effect(spec().ship_id, laser());
        gun.turret.as_mut().unwrap().charge = Some(Charge { type_id: 222, flag: 28, rounds, per_cycle: 1 });
        gun
    }

    /// The rounds the host is told are left, in the order it is told them.
    fn rounds_left(out: &[Out]) -> Vec<i64> {
        out.iter()
            .filter_map(|o| match o {
                Out::ChargeSpent { rounds_left, .. } => Some(*rounds_left),
                _ => None,
            })
            .collect()
    }

    #[test]
    fn a_turret_spends_a_round_for_each_shot_and_stops_with_no_charges_when_they_are_gone() {
        let (mut scene, npc, now) = duel();
        let mut out = scene.activate(npc, charged_gun(3), now);
        let mut at = now;
        for _ in 0..80 {
            at += SECOND / 4;
            out.extend(scene.poll(at));
        }
        // three rounds, three shots, the host is told each one, and the gun is off with the error the client shows
        assert_eq!(rounds_left(&out), [2, 1, 0], "{out:?}");
        let shots = out.iter().filter(|o| matches!(o, Out::Notify { name: "OnDamageMessage", .. })).count();
        assert_eq!(shots, 3, "{out:?}");
        assert!(format!("{out:?}").contains("NoCharges"), "{out:?}");
        // nothing more is fired or spent once it has stopped
        let mut later = Vec::new();
        for _ in 0..40 {
            at += SECOND / 4;
            later.extend(scene.poll(at));
        }
        assert!(
            rounds_left(&later).is_empty() && !order(&later, npc).contains(&"OnGodmaShipEffect"),
            "{later:?}"
        );
    }

    /// What the host is answered when it changes the rounds of a gun: whether it runs, the rounds it had and the rounds it has.
    fn adjusted(out: &[Out]) -> (bool, i64, i64) {
        match out {
            [Out::ChargeAdjusted { running, before, after, .. }] => (*running, *before, *after),
            other => panic!("one answer was expected: {other:?}"),
        }
    }

    #[test]
    fn rounds_the_host_adds_to_a_gun_that_runs_are_spent_by_the_shots_that_follow() {
        use crate::link::ChargeOp;
        let (mut scene, npc, now) = duel();
        let started = scene.activate(npc, charged_gun(3), now);
        assert_eq!(rounds_left(&started), [2]);
        // a gun that does not run is not here (the host has its rounds), and neither is a session that is not in space
        assert_eq!(
            adjusted(&scene.adjust_charge(
                1,
                PLAYER,
                999,
                222,
                ChargeOp::Add { capacity: 8, available: 100 }
            )),
            (false, 0, 0)
        );
        // up to the capacity and no more than the hold has: 2 rounds in it, room for 6, and the hold has 4
        let topped = scene.adjust_charge(2, npc, NPC_SHIP, 222, ChargeOp::Add { capacity: 8, available: 4 });
        assert_eq!(adjusted(&topped), (true, 2, 6));
        // a kind that is not the one it fires is left as it is (it has room: 6 of 8)
        let other = scene.adjust_charge(4, npc, NPC_SHIP, 223, ChargeOp::Add { capacity: 8, available: 100 });
        assert_eq!(adjusted(&other), (true, 6, 6));
        // the hold has more than the gun takes
        let more = scene.adjust_charge(3, npc, NPC_SHIP, 222, ChargeOp::Add { capacity: 8, available: 100 });
        assert_eq!(adjusted(&more), (true, 6, 8));
        // the next shot spends from what it comes to now
        let next = scene.poll(now + 2_500 * SECOND / 1_000 + 1);
        assert_eq!(rounds_left(&next), [7], "{next:?}");
    }

    #[test]
    fn rounds_the_host_takes_out_of_a_gun_that_runs_leave_it_with_what_is_left_and_stop_it_when_none_is() {
        use crate::link::ChargeOp;
        let (mut scene, npc, now) = duel();
        scene.activate(npc, charged_gun(8), now);
        let part = scene.adjust_charge(1, npc, NPC_SHIP, 0, ChargeOp::Remove { quantity: 3 });
        assert_eq!(adjusted(&part), (true, 7, 4));
        let all = scene.adjust_charge(2, npc, NPC_SHIP, 0, ChargeOp::Remove { quantity: 0 });
        assert_eq!(adjusted(&all), (true, 4, 0));
        // the cycle that was on ends, and the gun does not shoot again: it stops with the error the client shows
        let ended = scene.poll(now + 2_500 * SECOND / 1_000 + 1);
        assert!(rounds_left(&ended).is_empty(), "no round is spent that was not there: {ended:?}");
        assert!(
            !ended.iter().any(|o| matches!(o, Out::Notify { name: "OnDamageMessage", .. })),
            "no shot: {ended:?}"
        );
        assert!(format!("{ended:?}").contains("NoCharges"), "{ended:?}");
    }

    #[test]
    fn a_turret_with_fewer_rounds_than_a_cycle_spends_is_refused_and_one_without_a_charge_is_not() {
        let (mut scene, npc, now) = duel();
        let mut empty = charged_gun(0);
        assert!(matches!(
            scene.activate(npc, empty.clone(), now).as_slice(),
            [Out::Refused { reason: "NO_AMMO", .. }]
        ));
        empty.turret.as_mut().unwrap().charge =
            Some(Charge { type_id: 222, flag: 28, rounds: 5, per_cycle: 8 });
        assert!(matches!(
            scene.activate(npc, empty, now).as_slice(),
            [Out::Refused { reason: "NO_AMMO", .. }]
        ));
        // a gun that takes no charge is never short of rounds
        let started = scene.activate(npc, laser_effect(spec().ship_id, laser()), now);
        assert!(!started.iter().any(|o| matches!(o, Out::Refused { .. })), "{started:?}");
        assert!(rounds_left(&started).is_empty());
    }

    /// The arguments of the `OnSpecialFX` actions in the updates.
    fn special_fx(out: &[Out]) -> Vec<Vec<Value>> {
        let mut found = Vec::new();
        for o in out {
            let Out::Notify { name: "DoDestinyUpdate", args, .. } = o else { continue };
            let Some(Value::List(actions)) = args.first() else { continue };
            for action in actions {
                let Value::Tuple(pair) = action else { continue };
                let Value::Tuple(inner) = &pair[1] else { continue };
                if inner[0] == Value::Str("OnSpecialFX".into()) {
                    let Value::Tuple(arguments) = &inner[1] else { continue };
                    found.push(arguments.to_vec());
                }
            }
        }
        found
    }

    #[test]
    fn the_effect_the_client_plays_is_offensive_names_the_target_and_carries_the_repeat() {
        let (mut scene, npc, now) = duel();
        let started = scene.activate(npc, laser_effect(spec().ship_id, laser()), now);
        let seen_by_the_player: Vec<Out> =
            started.iter().filter(|o| o.session() == PLAYER).cloned().collect();
        let fx = special_fx(&seen_by_the_player);
        assert_eq!(fx.len(), 1, "{started:?}");
        let arguments = &fx[0];
        assert_eq!(arguments[0], id(NPC_SHIP));
        assert_eq!(arguments[1], id(NPC_SHIP), "the module is the ship for an NPC");
        assert_eq!(arguments[2], int(23_312));
        assert_eq!(arguments[3], id(spec().ship_id));
        assert_eq!(arguments[5], text("effects.Laser"));
        assert_eq!(arguments[6], int(1), "offensive");
        assert_eq!(arguments[7], int(1), "start");
        assert_eq!(arguments[9], int(2_500));
        assert_eq!(arguments[10], int(17_280), "repeat");
    }

    #[test]
    fn a_shot_that_misses_is_told_with_no_damage_and_no_quality_and_hurts_nothing() {
        let (mut scene, npc, now) = duel();
        let blind = turret([0.0, 14.0, 14.0, 0.0], 0.0, 40.0, 10_000.0, 5_000.0);
        let started = scene.activate(npc, laser_effect(spec().ship_id, blind), now);
        let told = messages(&started, PLAYER, "OnDamageMessage");
        assert_eq!(told.len(), 1);
        assert_eq!(entry(&told[0], "hitQuality"), int(0));
        assert_eq!(entry(&told[0], "damage"), int(0));
        assert!(
            messages(&started, PLAYER, "OnModuleAttributeChanges").is_empty(),
            "nothing changed on the ship that was shot at"
        );
        let state = scene.ship(PLAYER, now).unwrap().health.condition_at(now);
        assert_eq!(state.shield_charge, 1.0, "nothing was hurt");
    }

    #[test]
    fn what_a_hit_does_is_what_the_health_model_does_with_it() {
        let (mut scene, npc, now) = duel();
        // a weapon of one very heavy shot: a guaranteed hit at the top of the quality band
        let heavy = turret([0.0, 1_000.0, 0.0, 0.0], 5.0, 40.0, 10_000.0, 5_000.0);
        let before = scene.ship(PLAYER, now).unwrap().health.layers_at(now);
        scene.activate(npc, laser_effect(spec().ship_id, heavy), now);
        let after = scene.ship(PLAYER, now).unwrap().health.layers_at(now);
        assert!(after.shield < before.shield || after.armor < before.armor, "{before:?} {after:?}");
    }

    #[test]
    fn what_the_message_says_was_done_is_what_all_three_layers_took() {
        let (mut scene, npc, now) = duel();
        // a hit that goes through the shield and into the armor
        let heavy = turret([0.0, 1_000.0, 0.0, 0.0], 5.0, 40.0, 10_000.0, 5_000.0);
        let before = scene.ship(PLAYER, now).unwrap().health.layers_at(now);
        let started = scene.activate(npc, laser_effect(spec().ship_id, heavy), now);
        let after = scene.ship(PLAYER, now).unwrap().health.layers_at(now);
        let took = (before.shield - after.shield)
            + (before.armor - after.armor)
            + (before.structure - after.structure);
        assert!(before.armor - after.armor > 0.0, "the armor was hit: {before:?} {after:?}");
        let told = messages(&started, PLAYER, "OnDamageMessage");
        let Value::Real(damage) = entry(&told[0], "damage") else {
            panic!("{:?}", entry(&told[0], "damage"))
        };
        assert!((damage - took).abs() < 1e-3, "{damage} for {took}");
    }

    #[test]
    fn the_combat_log_names_the_character_who_flies_a_ship_and_the_owner_of_one_nobody_flies() {
        let mut scene = Scene::new(system());
        let mut pilot = spec();
        pilot.owner_id = 98_000_001;
        scene.undock(PLAYER, pilot, STATION, NOON).unwrap();
        let (npc, _) = scene.spawn_npc(
            npc_spec(),
            NpcIdentity::default(),
            NpcPlace::at_rest(eve_destiny::Vec3::ZERO),
            NOON,
        );
        assert_eq!(scene.combat_owner(PLAYER), 140_000_342);
        assert_eq!(scene.combat_owner(npc), 1_000_127);
        assert_eq!(scene.combat_owner(999), 0);
    }

    #[test]
    fn a_player_who_shoots_is_told_the_shot_as_me_and_the_ship_that_is_shot_at_as_the_target() {
        let (mut scene, _npc, now) = duel();
        let mut effect = laser_effect(NPC_SHIP, laser());
        effect.module_id = spec().ship_id + 1;
        // the player locks the NPC first
        scene.target_order(PLAYER, Order::Lock(NPC_SHIP), now);
        let mut t = now;
        for _ in 0..40 {
            t += SECOND / 4;
            scene.poll(t);
        }
        let started = scene.activate(PLAYER, effect, t);
        let mine = messages(&started, PLAYER, "OnDamageMessage");
        assert_eq!(mine.len(), 1);
        assert_eq!(entry(&mine[0], "attackType"), text("me"));
        assert_eq!(entry(&mine[0], "attackerID"), Value::None);
        assert_eq!(entry(&mine[0], "owner"), tuple([int(2), id(140_000_342)]));
    }

    #[test]
    fn a_weapon_stops_at_the_end_of_the_cycle_when_its_target_is_gone() {
        let (mut scene, npc, now) = duel();
        scene.activate(npc, laser_effect(spec().ship_id, laser()), now);
        assert_eq!(scene.pilots[&npc].modules.running_count(), 1);
        // the player docks away
        scene.leave(PLAYER, crate::scene::Departure::LoggedOff, now + SECOND / 10);
        let later = scene.poll(now + 3 * SECOND);
        assert!(messages(&later, PLAYER, "OnDamageMessage").is_empty());
        assert_eq!(scene.pilots[&npc].modules.running_count(), 0, "the effect is over");
        assert!(scene.locked_by(npc).is_empty());
    }

    #[test]
    fn the_same_calls_at_the_same_times_shoot_the_same_shots() {
        let run = || {
            let (mut scene, npc, now) = duel();
            let mut out = scene.activate(npc, laser_effect(spec().ship_id, laser()), now);
            for step in 1..=20 {
                out.extend(scene.poll(now + step * SECOND / 2));
            }
            out
        };
        assert_eq!(run(), run());
        assert!(run().iter().any(|o| o.session() == PLAYER && !is_npc_session(o.session())));
    }
}
