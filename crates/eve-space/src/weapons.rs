//! Turrets: how a shot is resolved (does it hit, how well, how much damage) and what the client is told of it.
//!
//! The rules are the reference's (`server/src/space/combat/laserTurrets.js`), which are TQ's turret mechanics: the chance to hit is
//! `0.5 ^ (tracking² + range²)` with `tracking = angular velocity × turret signature resolution / (tracking speed × target signature radius)` and
//! `range = max(0, surface distance − optimal) / falloff`; one roll decides everything: at or above the chance the shot misses, below 1 % it is a
//! wrecking shot (three times the damage), otherwise the damage is multiplied by `0.49 + roll`. The hit quality the combat log shows is one of the
//! six bands of EVE University's table (`hit_quality_tier`).

use eve_marshal::Value;

use crate::health::Damage;
use crate::missiles::{application_factor_of, Missile};
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
    /// What a launcher flies (a launcher is a weapon that fires a missile in the place of a shot: its `raw_damage` is the missile's, its `optimal_range` how far the missile goes).
    pub missile: Option<Missile>,
    /// The damage ramp of a weapon that builds up its damage on the target it keeps shooting at (a precursor weapon); none for the others.
    pub spool: Option<Spool>,
    /// How a shot that explodes on its target does its damage (a vorton projector: no roll; the explosion's cloud and velocity decide how much of the
    /// damage reaches the target, as a missile's do); none for a weapon that rolls to hit.
    pub aoe: Option<Aoe>,
    /// The chain a shot links from its target to other ships (a vorton projector's arc); none for the others.
    pub arc: Option<Arc>,
}

/// The explosion of a shot that does not roll to hit (a vorton projector): the cloud's radius and velocity, and the damage reduction factor and its
/// sensitivity, as a missile's explosion has them (`missiles::application_factor_of`).
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Aoe {
    pub explosion_radius: f64,
    pub explosion_velocity: f64,
    pub damage_reduction_factor: f64,
    pub damage_reduction_sensitivity: f64,
}

/// The chain a shot links from its target (EVE.js `vortonArcChain`): other ships within `range` of the target, up to `targets` ships in all (the target
/// counts, so `targets - 1` links are made), each struck with the explosion of the shot.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Arc {
    pub range: f64,
    pub targets: i32,
}

/// The damage ramp of a weapon that builds up its damage on the target it keeps shooting at (a precursor weapon, EVE.js `precursorTurrets.js`): each
/// cycle the bonus grows by `per_cycle` up to `max`, and a shot is multiplied by `1 + bonus`, the bonus built up before that shot (the first has none).
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Spool {
    pub per_cycle: f64,
    pub max: f64,
}

/// A shot that does not roll (a vorton projector's primary, EVE.js `resolveVortonShot`): it hits its target whole when the target is within the weapon's
/// optimal range (nothing beyond it, no falloff, no tracking), and what the explosion does to the target is `explosion`.
pub(crate) fn explosion_shot(
    aoe: &Aoe,
    raw: Damage,
    optimal: f64,
    attacker: &Kinematics,
    target: &Kinematics,
) -> Shot {
    let out_of_range = surface_distance(attacker, target) > optimal.max(0.0) + 1e-6;
    if out_of_range || raw.iter().sum::<f64>() <= 0.0 {
        let chance_to_hit = if out_of_range { 0.0 } else { 1.0 };
        return Shot {
            hit: false,
            wrecking: false,
            quality: 0.0,
            chance_to_hit,
            roll: 0.0,
            damage: [0.0; 4],
        };
    }
    explosion(aoe, raw, target)
}

/// What an explosion does to one ship, with no range check (an arc's victim, EVE.js `resolveVortonArcShot`): the raw damage times the application factor
/// of the ship's signature and speed, the missile's curve. A hit whenever there is damage to do.
pub(crate) fn explosion(aoe: &Aoe, raw: Damage, target: &Kinematics) -> Shot {
    if raw.iter().sum::<f64>() <= 0.0 {
        return Shot {
            hit: false,
            wrecking: false,
            quality: 0.0,
            chance_to_hit: 0.0,
            roll: 0.0,
            damage: [0.0; 4],
        };
    }
    let signature =
        if target.signature_radius > 0.0 { target.signature_radius } else { target.radius.max(1.0) }.max(1.0);
    let speed = length(target.velocity);
    let factor = application_factor_of(
        aoe.explosion_radius,
        aoe.explosion_velocity,
        aoe.damage_reduction_factor,
        aoe.damage_reduction_sensitivity,
        signature,
        speed,
    )
    .clamp(0.0, 1.0);
    Shot {
        hit: true,
        wrecking: false,
        quality: 1.0,
        chance_to_hit: 1.0,
        roll: 0.0,
        damage: raw.map(|amount| round6(amount.max(0.0) * factor)),
    }
}

/// What a running weapon has built up: the bonus its next shot takes, and the target it was built on.
#[derive(Debug, Clone, Copy, Default, PartialEq)]
pub(crate) struct SpoolState {
    pub(crate) bonus: f64,
    pub(crate) target_id: i64,
}

impl Spool {
    /// The bonus the next shot at `target_id` takes: a target other than the one it was built on starts it again from none.
    pub(crate) fn bonus_for(self, state: &mut SpoolState, target_id: i64) -> f64 {
        if state.target_id != target_id {
            *state = SpoolState { bonus: 0.0, target_id };
        }
        state.bonus = state.bonus.clamp(0.0, self.max.max(0.0));
        state.bonus
    }

    /// A cycle has been shot: the bonus grows by a cycle's worth, up to the maximum.
    pub(crate) fn advance(self, state: &mut SpoolState) {
        state.bonus = (state.bonus + self.per_cycle.max(0.0)).min(self.max.max(0.0));
    }
}

/// The raw damage of a shot with the bonus a spool has built up: each em, thermal, kinetic and explosive part times `1 + bonus`, rounded as the reference
/// rounds it. No bonus leaves the damage as it is.
pub(crate) fn spooled_damage(raw: Damage, bonus: f64) -> Damage {
    if bonus <= 0.0 {
        return raw;
    }
    let multiplier = round6(1.0 + bonus);
    raw.map(|damage| round6(damage * multiplier))
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
    quality_message(
        for_the_attacker,
        attacker_id,
        target_id,
        weapon_type_id,
        applied,
        shot.damage,
        hit_quality_tier(shot),
        owner_id,
    )
}

/// The `OnDamageMessage` of whatever damage reached a ship, with the hit quality the combat log is to show (a missile that lands is always `3`).
#[allow(clippy::too_many_arguments)]
pub(crate) fn quality_message(
    for_the_attacker: bool,
    attacker_id: i64,
    target_id: i64,
    weapon_type_id: i32,
    applied: f64,
    damage: Damage,
    tier: u8,
    owner_id: i64,
) -> Value {
    let attributes = Value::Dict(
        DAMAGE_ATTRIBUTES
            .iter()
            .zip(damage)
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
        ("hitQuality", int(i64::from(tier))),
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
    pub(crate) fn kinematics_of(&self, session: SessionId) -> Option<Kinematics> {
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

    pub(crate) fn in_smartbomb_range(&self, session: SessionId, range: f64) -> Vec<(SessionId, i64)> {
        let Some(source) = self.pilots.get(&session) else { return Vec::new() };
        let source_ship_id = source.ship.spec.ship_id;
        let position = [source.ship.position.x, source.ship.position.y, source.ship.position.z];
        let candidates = self.pilots.iter().map(|(other, pilot)| self.spatial_ship(*other, pilot));
        let mut inside: Vec<_> = crate::geo::SpatialQuery::surface_radius(
            position,
            source.ship.spec.radius,
            range,
            1e-6,
            crate::geo::SpatialFilter {
                exclude_id: Some(source_ship_id),
                exclude_warping: true,
                ..crate::geo::SpatialFilter::default()
            },
        )
        .execute(candidates)
        .into_iter()
        .filter_map(|hit| self.pilots.get(&hit.item).map(|pilot| (hit.item, pilot.ship.spec.ship_id)))
        .collect();
        inside.sort_by_key(|(_, ship_id)| *ship_id);
        inside
    }

    /// A smartbomb goes off as a cycle begins: every ship in its range takes the damage whole (a ship in its undock protection does not), and each is aggressed and
    /// told as a shot is. Where the safety forbids it (`hurts_pilots` false) and a pilot is in range, nothing is hurt and the effect stops at once. The missiles of others on
    /// their way within its reach are hurt too, in their own hit points (`Scene::damage_missile`): a bomb of enough damage shoots them down.
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
        // EVE.js server/src/space/modules/smartbombRuntime.js (828823a75f211fdecb945b087a26c225f111491f)
        // runs empWave per cycle; eve-logic EMPWave.py (1dbb5711a072a146e1d228365668ac05655eefe1)
        // renders the field and impacts from the source ship and module type.
        // Advance past the previous one-shot's expiry before reusing this module ID.
        let _ = self.next_stamp(at);
        let wave_stamp = self.next_stamp(at);
        let wave = crate::payload::action(
            wave_stamp,
            "OnSpecialFX",
            [
                crate::wire::id(attacker_id),
                crate::wire::id(effect.module_id),
                crate::wire::int(i64::from(effect.module_type)),
                Value::None,
                Value::None,
                crate::wire::text("effects.EMPWave"),
                crate::wire::int(1),
                crate::wire::int(1),
                crate::wire::int(1),
                crate::wire::real(effect.duration_ms),
                Value::None,
            ],
        );
        let mut out = self.tell_all(session, vec![wave]);
        // the missiles on their way within its reach are hurt as well, by the damage the bomb does (their own hit points, not a ship's): the pilot's own are spared
        let (centre, reach) = {
            let ship = &self.pilots[&session].ship;
            (
                eve_destiny::Vec3::new(ship.position.x, ship.position.y, ship.position.z),
                range + ship.spec.radius.max(0.0),
            )
        };
        let blast: f64 = damage.iter().sum();
        for missile_id in self.missiles_within(centre, reach) {
            if self.missiles.get(&missile_id).is_some_and(|missile| missile.shooter.owner_id != owner) {
                let by = crate::missile_entity::Interceptor::Smartbomb {
                    ship_id: attacker_id,
                    module_id: effect.module_id,
                };
                out.extend(self.damage_missile(missile_id, blast, by, at));
            }
        }
        for target in in_range {
            if self.pilots.get(&target.0).is_none_or(|pilot| pilot.protected_until > at) {
                continue;
            }
            out.extend(self.damage_ship(session, effect.module_type, target, damage, at));
        }
        out
    }

    /// One ship hit by a module's area effect (a smartbomb's blast, a doomsday's pulse): the damage it takes, the aggression it is recorded as, the messages
    /// its pilot and the attacker's are told, and its destruction. `target` is the pilot's session and its ship's id.
    pub(crate) fn damage_ship(&mut self, session: SessionId, module_type: i32, target: (SessionId, i64), damage: Damage, at: i64) -> Vec<Out> {
        let (target_session, target_id) = target;
        let Some(attacker_id) = self.pilots.get(&session).map(|pilot| pilot.ship.spec.ship_id) else {
            return Vec::new();
        };
        let owner = self.combat_owner(session);
        let mut out = Vec::new();
        let shot = Shot { hit: true, wrecking: false, quality: 1.0, chance_to_hit: 1.0, roll: 0.0, damage };
        let (told, hit) = self.damage(target_id, damage, at);
        out.extend(told);
        let applied = hit.map_or(0.0, |hit| hit.taken.shield + hit.taken.armor + hit.taken.structure);
        let destroyed = hit.is_some_and(|hit| hit.destroyed);
        let target_type_id = self.pilots.get(&target_session).map_or(0, |pilot| pilot.ship.spec.type_id);
        self.aggressions.push(Aggression {
            attacker_ship: attacker_id,
            target_ship: target_id,
            module_type_id: module_type,
            source_owner_id: owner,
            target_type_id,
        });
        let message = |for_the_attacker| {
            damage_message(for_the_attacker, attacker_id, target_id, module_type, applied, &shot, owner)
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
        out
    }

    /// A turret shoots at the start of a cycle: one shot, rolled; a hit hurts the target (whoever sees it is told its damage state, its pilot the
    /// attributes that changed), and the combat log of each pilot is told the shot, hit or miss.
    pub(crate) fn fire_turret(&mut self, session: SessionId, effect: &Effect, at: i64) -> Vec<Out> {
        let Some(turret) = effect.turret.as_ref() else { return Vec::new() };
        if turret.missile.is_some() {
            return self.launch_missile(session, effect, at);
        }
        let Some(target_session) = self.session_of(effect.target_id) else { return Vec::new() };
        let (Some(attacker), Some(target)) =
            (self.kinematics_of(session), self.kinematics_of(target_session))
        else {
            return Vec::new();
        };
        let attacker_id = self.pilots[&session].ship.spec.ship_id;
        // the combat log, the aggression and the kill of a drone's shot are its controller's
        let reporter = self.controller_of_drone(session).unwrap_or(session);
        let aggressor_ship = if reporter == session { attacker_id } else { self.ship_id_of(reporter) };
        // a weapon that builds up its damage shoots with the bonus it has built on this target (the ramp of its running effect); the bonus grows after the shot
        let spool = turret.spool;
        let bonus =
            spool.map_or(0.0, |spool| self.spool_bonus(session, effect.module_id, effect.target_id, spool));
        let spooled;
        let turret = if bonus > 0.0 {
            spooled = Turret { raw_damage: spooled_damage(turret.raw_damage, bonus), ..turret.clone() };
            &spooled
        } else {
            turret
        };
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
        // a vorton's explosion does not roll (EVE.js resolveVortonShot); every other shot is rolled to hit
        let shot = match turret.aoe {
            Some(aoe) => explosion_shot(&aoe, turret.raw_damage, turret.optimal_range, &attacker, &target),
            None => {
                let roll = self.rng.random();
                resolve_shot(turret, &attacker, &target, roll)
            }
        };
        if let Some(spool) = spool {
            self.spool_advance(session, effect.module_id, spool);
        }
        // the ships an arc reaches are chosen from the scene as the shot lands, before the target can leave it
        let arc_victims = match (turret.aoe, turret.arc) {
            (Some(_), Some(arc)) if shot.hit => self.arc_victims(session, target_session, &target, arc),
            _ => Vec::new(),
        };
        let shooter = Shooter {
            reporter,
            attacker_ship: attacker_id,
            aggressor_ship,
            owner: self.combat_owner(session),
            weapon: turret.weapon_type_id,
        };
        let (mut out, destroyed) = self.land_shot(shooter, target_session, &shot, at);
        // the shot that took the last of the hull is told first, then the ship is gone
        if destroyed {
            out.extend(self.destroy(target_session, Some(reporter), at));
        }
        // each ship the arc reaches takes the explosion as the target did, without a roll
        if let Some(aoe) = turret.aoe {
            for victim in arc_victims {
                let Some(victim_kinematics) = self.kinematics_of(victim) else { continue };
                let struck = explosion(&aoe, turret.raw_damage, &victim_kinematics);
                let (told, destroyed) = self.land_shot(shooter, victim, &struck, at);
                out.extend(told);
                if destroyed {
                    out.extend(self.destroy(victim, Some(reporter), at));
                }
            }
        }
        out
    }

    /// A shot lands on the ship of `target`: its damage is taken (whoever sees it is told its damage state), the shot is an offensive act (the crimewatch
    /// and the NPCs hear of it), and the pilots who see it are told the shot. Returns what was told, and whether the shot took the last of the hull: the
    /// caller destroys the ship once the shot is told.
    fn land_shot(&mut self, shooter: Shooter, target: SessionId, shot: &Shot, at: i64) -> (Vec<Out>, bool) {
        let mut out = Vec::new();
        let target_id = self.pilots.get(&target).map_or(0, |pilot| pilot.ship.spec.ship_id);
        let mut applied = 0.0;
        let mut destroyed = false;
        if shot.hit {
            let (told, hit) = self.damage(target_id, shot.damage, at);
            out.extend(told);
            applied = hit.map_or(0.0, |hit| hit.taken.shield + hit.taken.armor + hit.taken.structure);
            destroyed = hit.is_some_and(|hit| hit.destroyed);
        }
        let target_type_id = self.pilots.get(&target).map_or(0, |pilot| pilot.ship.spec.type_id);
        self.aggressions.push(Aggression {
            attacker_ship: shooter.aggressor_ship,
            target_ship: target_id,
            module_type_id: shooter.weapon,
            source_owner_id: shooter.owner,
            target_type_id,
        });
        let message = |for_the_attacker| {
            damage_message(
                for_the_attacker,
                shooter.attacker_ship,
                target_id,
                shooter.weapon,
                applied,
                shot,
                shooter.owner,
            )
        };
        if !is_npc_session(shooter.reporter) {
            out.push(Out::Notify {
                session: shooter.reporter,
                name: "OnDamageMessage",
                id_type: "clientID",
                args: vec![message(true)],
            });
        }
        if !is_npc_session(target) {
            out.push(Out::Notify {
                session: target,
                name: "OnDamageMessage",
                id_type: "clientID",
                args: vec![message(false)],
            });
        }
        (out, destroyed)
    }

    /// The ships a vorton's arc reaches from its target (EVE.js `resolveVortonArcChain` and `selectVortonArcTargets`): the ships of the scene other than the
    /// shooter and the target whose surface is within the arc's range of the target's. When there are more of them than the arc has links (its ships less
    /// the target), that many are drawn at random (EVE.js's partial Fisher-Yates, one draw each); they come back in order of their distance from the
    /// target, the ship id deciding a tie.
    fn arc_victims(
        &mut self,
        shooter: SessionId,
        target: SessionId,
        target_kinematics: &Kinematics,
        arc: Arc,
    ) -> Vec<SessionId> {
        let links = (arc.targets - 1).max(0) as usize;
        if arc.range <= 0.0 || links == 0 {
            return Vec::new();
        }
        let mut sessions: Vec<SessionId> = self.pilots.keys().copied().collect();
        sessions.sort_unstable();
        let mut eligible: Vec<(f64, i64, SessionId)> = Vec::new();
        for session in sessions {
            if session == shooter || session == target {
                continue;
            }
            let Some(kinematics) = self.kinematics_of(session) else { continue };
            let ship_id = self.pilots.get(&session).map_or(0, |pilot| pilot.ship.spec.ship_id);
            let distance = round6(surface_distance(target_kinematics, &kinematics));
            if distance <= arc.range + 1e-6 {
                eligible.push((distance, ship_id, session));
            }
        }
        let mut chosen = if eligible.len() <= links {
            eligible
        } else {
            let mut pool = eligible;
            let mut drawn = Vec::with_capacity(links);
            for _ in 0..links {
                let rolled = self.rng.random().clamp(0.0, 0.999_999_999);
                let index = ((rolled * pool.len() as f64) as usize).min(pool.len() - 1);
                drawn.push(pool.remove(index));
            }
            drawn
        };
        chosen.sort_by(|a, b| a.0.total_cmp(&b.0).then(a.1.cmp(&b.1)));
        chosen.into_iter().map(|(_, _, session)| session).collect()
    }
}

/// Who a shot is from: the pilot that is told of it (a drone's controller), the ship it counts against and the ship that is aggressed by it, the owner
/// the combat log names, and the weapon's type.
#[derive(Debug, Clone, Copy)]
struct Shooter {
    reporter: SessionId,
    attacker_ship: i64,
    aggressor_ship: i64,
    owner: i64,
    weapon: i32,
}

/// A missile that is this much further from its target than it can fly (metres) still gets there (the reference's `graceMeters`).
pub(crate) const MISSILE_RANGE_GRACE: f64 = 1.0;

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
            missile: None,
            spool: None,
            aoe: None,
            arc: None,
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
            burns_fuel: false,
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

    /// The explosion of the Small Vorton Projector I of SDE build 3396210 (aoeCloudSize 43, aoeVelocity 190, aoeDamageReductionFactor 0.4; the
    /// sensitivity is the reference's default, the attribute being absent).
    fn vorton_aoe() -> Aoe {
        Aoe { explosion_radius: 43.0, explosion_velocity: 190.0, damage_reduction_factor: 0.4, damage_reduction_sensitivity: 5.5 }
    }

    /// A vorton projector's turret: no tracking speed, its explosion, its optimal range (maxRange 22464) and the em and kinetic damage of its charge.
    fn vorton(arc: Option<Arc>) -> Turret {
        let mut weapon = laser();
        weapon.raw_damage = [116.0, 0.0, 17.0, 0.0];
        weapon.optimal_range = 22_464.0;
        weapon.tracking_speed = 0.0;
        weapon.aoe = Some(vorton_aoe());
        weapon.arc = arc;
        weapon
    }

    #[test]
    fn a_vorton_explosion_draws_no_roll_and_hits_whole_within_its_optimal_range_with_the_missile_curve() {
        let (mut scene, npc, now) = duel();
        let weapon = vorton(None);
        let attacker = scene.kinematics_of(npc).unwrap();
        let target = scene.kinematics_of(PLAYER).unwrap();
        let expected = explosion_shot(&vorton_aoe(), weapon.raw_damage, weapon.optimal_range, &attacker, &target);
        assert!(expected.hit, "{expected:?}");
        // the next roll of the scene is the one a rolled shot would take: the explosion must leave it for what comes after
        let next = scene.rng.clone().random();
        let started = scene.activate(npc, laser_effect(spec().ship_id, weapon), now);
        let told = messages(&started, PLAYER, "OnDamageMessage");
        assert_eq!(told.len(), 1, "{started:?}");
        assert_eq!(entry(&told[0], "damageAttributes"), damage_told(&expected));
        assert_eq!(scene.rng.clone().random(), next, "a vorton's explosion draws no roll");
    }

    #[test]
    fn a_vorton_explosion_does_nothing_beyond_its_optimal_range() {
        let (scene, npc, _now) = duel();
        let attacker = scene.kinematics_of(npc).unwrap();
        let target = scene.kinematics_of(PLAYER).unwrap();
        // no optimal range at all: the target, which is off the attacker's surface, is beyond it
        let shot = explosion_shot(&vorton_aoe(), [116.0, 0.0, 17.0, 0.0], 0.0, &attacker, &target);
        let apart = surface_distance(&attacker, &target);
        assert!(apart > 1e-6, "the target is off the attacker's surface: {apart}");
        assert_eq!((shot.hit, shot.chance_to_hit, shot.damage), (false, 0.0, [0.0; 4]), "{apart} m apart");
    }

    #[test]
    fn an_explosion_takes_each_ship_by_its_own_signature_and_speed() {
        let raw = [116.0, 0.0, 17.0, 0.0];
        let small = ship([0.0; 3], [0.0; 3], 20.0, 28.0);
        let big = ship([0.0; 3], [0.0; 3], 60.0, 150.0);
        let fast_small = ship([0.0; 3], [3_000.0, 0.0, 0.0], 20.0, 28.0);
        let on_small = explosion(&vorton_aoe(), raw, &small).damage;
        let on_big = explosion(&vorton_aoe(), raw, &big).damage;
        let on_fast = explosion(&vorton_aoe(), raw, &fast_small).damage;
        // a ship bigger than the cloud takes it whole; a small one takes its signature's share, and a fast one less of that
        assert_eq!(on_big, raw);
        assert!(on_small[0] < raw[0] && on_small[0] > 0.0, "{on_small:?}");
        assert!(on_fast[0] < on_small[0], "{on_fast:?} against {on_small:?}");
    }

    /// Spawns an NPC ship of its own id `metres` off the player along z (the player is where it was at the start of the duel).
    fn ship_off_the_player(scene: &mut Scene, ship_id: i64, metres: f64) -> SessionId {
        let here = scene.ship(PLAYER, NOON).unwrap().position;
        let mut spec = npc_spec();
        spec.ship_id = ship_id;
        let place = NpcPlace::at_rest(eve_destiny::Vec3::new(here.x, here.y, here.z + metres));
        scene.spawn_npc(spec, NpcIdentity::default(), place, NOON).0
    }

    #[test]
    fn a_vorton_arc_strikes_the_ships_near_its_target_nearest_first_and_not_the_shooter_or_the_far_ones() {
        let (mut scene, npc, now) = duel();
        let near = ship_off_the_player(&mut scene, 9_000_000_001, 3_000.0);
        let far = ship_off_the_player(&mut scene, 9_000_000_002, 8_000.0);
        let beyond = ship_off_the_player(&mut scene, 9_000_000_003, 15_000.0);
        scene.aggressions.clear();
        let weapon = vorton(Some(Arc { range: 10_000.0, targets: 3 }));
        scene.activate(npc, laser_effect(spec().ship_id, weapon), now);
        // the target, then the two ships within 10 km of it, the nearer first; the shooter and the ship beyond the arc's reach are not struck
        let struck: Vec<i64> = scene.aggressions.iter().map(|a| a.target_ship).collect();
        assert_eq!(struck, [spec().ship_id, scene.ship_id_of(near), scene.ship_id_of(far)]);
        assert!(!struck.contains(&scene.ship_id_of(beyond)));
        assert!(!struck.contains(&NPC_SHIP));
    }

    #[test]
    fn a_vorton_arc_with_more_ships_than_it_has_links_strikes_as_many_as_it_has_drawing_one_roll_each() {
        let (mut scene, npc, now) = duel();
        let near = ship_off_the_player(&mut scene, 9_000_000_001, 3_000.0);
        let far = ship_off_the_player(&mut scene, 9_000_000_002, 8_000.0);
        scene.aggressions.clear();
        // two targets is the target and one link: of the two ships in reach, one is drawn
        let weapon = vorton(Some(Arc { range: 10_000.0, targets: 2 }));
        let mut expected_rolls = scene.rng.clone();
        expected_rolls.random();
        scene.activate(npc, laser_effect(spec().ship_id, weapon), now);
        let struck: Vec<i64> = scene.aggressions.iter().map(|a| a.target_ship).collect();
        assert_eq!(struck.len(), 2, "{struck:?}");
        assert_eq!(struck[0], spec().ship_id);
        assert!(struck[1] == scene.ship_id_of(near) || struck[1] == scene.ship_id_of(far), "{struck:?}");
        assert_eq!(scene.rng.clone().random(), expected_rolls.random(), "the draw takes one roll for the one link");
    }

    #[test]
    fn a_vorton_with_one_target_arcs_to_nobody() {
        let (mut scene, npc, now) = duel();
        let _near = ship_off_the_player(&mut scene, 9_000_000_001, 3_000.0);
        scene.aggressions.clear();
        let weapon = vorton(Some(Arc { range: 10_000.0, targets: 1 }));
        scene.activate(npc, laser_effect(spec().ship_id, weapon), now);
        let struck: Vec<i64> = scene.aggressions.iter().map(|a| a.target_ship).collect();
        assert_eq!(struck, [spec().ship_id]);
    }

    /// A bonus is the same to within rounding (the sums of a ramp are not exact in binary).
    fn assert_bonus_close(actual: f64, expected: f64) {
        assert!((actual - expected).abs() < 1e-9, "{actual} is not {expected}");
    }

    #[test]
    fn a_spool_has_no_bonus_for_its_first_cycle_then_a_cycle_s_worth_more_after_each_shot_up_to_the_most() {
        let spool = Spool { per_cycle: 0.07, max: 0.2 };
        let mut state = SpoolState::default();
        assert_eq!(spool.bonus_for(&mut state, 7), 0.0, "the first cycle shoots with none");
        spool.advance(&mut state);
        assert_bonus_close(spool.bonus_for(&mut state, 7), 0.07);
        spool.advance(&mut state);
        assert_bonus_close(spool.bonus_for(&mut state, 7), 0.14);
        spool.advance(&mut state);
        assert_bonus_close(spool.bonus_for(&mut state, 7), 0.2);
        spool.advance(&mut state);
        assert_eq!(spool.bonus_for(&mut state, 7), 0.2, "the most is the most");
    }

    #[test]
    fn the_nth_cycle_shoots_with_n_minus_one_cycles_of_bonus_and_the_ramp_stops_at_its_maximum() {
        let spool = Spool { per_cycle: 0.07, max: 2.125 };
        let mut state = SpoolState::default();
        for cycle in 0..=40 {
            assert_bonus_close(spool.bonus_for(&mut state, 7), (cycle as f64 * 0.07).min(2.125));
            spool.advance(&mut state);
        }
        // 31 cycles' worth passes 2.125, so from the 32nd cycle on the weapon shoots at the most
        assert_eq!(spool.bonus_for(&mut state, 7), 2.125);
    }

    #[test]
    fn a_shot_at_another_target_starts_the_ramp_again_from_none() {
        let spool = Spool { per_cycle: 0.07, max: 2.125 };
        let mut state = SpoolState::default();
        for _ in 0..3 {
            spool.bonus_for(&mut state, 7);
            spool.advance(&mut state);
        }
        assert_bonus_close(spool.bonus_for(&mut state, 7), 0.21);
        assert_eq!(spool.bonus_for(&mut state, 8), 0.0, "a new target has none built");
        spool.advance(&mut state);
        assert_bonus_close(spool.bonus_for(&mut state, 8), 0.07);
    }

    #[test]
    fn a_spool_multiplies_each_part_of_a_shot_by_one_plus_its_bonus_and_no_bonus_leaves_the_shot_alone() {
        let raw = [0.0, 10.0, 20.0, 0.0];
        assert_eq!(spooled_damage(raw, 0.0), raw);
        assert_eq!(spooled_damage(raw, 0.5), [0.0, 15.0, 30.0, 0.0]);
        assert_eq!(spooled_damage(raw, 0.07), [0.0, 10.7, 21.4, 0.0]);
    }

    /// The shot the scene is about to make with `weapon` from the NPC at the player, rolled with the number the scene draws next (read from a copy of its generator).
    fn next_shot(scene: &Scene, npc: SessionId, weapon: &Turret) -> Shot {
        let mut next_roll = scene.rng.clone();
        let roll = next_roll.random();
        let attacker = scene.kinematics_of(npc).expect("the NPC is in space");
        let target = scene.kinematics_of(PLAYER).expect("the player is in space");
        resolve_shot(weapon, &attacker, &target, roll)
    }

    /// The `damageAttributes` a shot is told as, the way `quality_message` writes them.
    fn damage_told(shot: &Shot) -> Value {
        Value::Dict(
            DAMAGE_ATTRIBUTES
                .iter()
                .zip(shot.damage)
                .map(|(attribute, amount)| (int(*attribute), number6(amount)))
                .collect(),
        )
    }

    #[test]
    fn a_precursor_shot_takes_the_bonus_built_before_it_and_the_ramp_grows_after_each_shot() {
        let (mut scene, npc, now) = duel();
        let cycle = 2_500 * SECOND / 1_000;
        let raw = laser().raw_damage;
        let mut weapon = laser();
        weapon.spool = Some(Spool { per_cycle: 0.07, max: 0.2 });
        // the first shot has no bonus: its damage is the weapon's own
        let first = next_shot(&scene, npc, &laser());
        let started = scene.activate(npc, laser_effect(spec().ship_id, weapon), now);
        let told = messages(&started, PLAYER, "OnDamageMessage");
        assert_eq!(told.len(), 1, "{started:?}");
        assert_eq!(entry(&told[0], "damageAttributes"), damage_told(&first));
        assert_bonus_close(scene.spool_of(npc, NPC_SHIP).unwrap().bonus, 0.07);
        // the next shots take 0.07, then 0.14, then the most (0.2), and the ramp does not go past it
        for (cycle_number, bonus_before, bonus_after) in [(1, 0.07, 0.14), (2, 0.14, 0.2), (3, 0.2, 0.2)] {
            let mut shot_weapon = laser();
            shot_weapon.raw_damage = spooled_damage(raw, bonus_before);
            let expected = next_shot(&scene, npc, &shot_weapon);
            let cycle_told = scene.poll(now + cycle_number * cycle + 1);
            let told = messages(&cycle_told, PLAYER, "OnDamageMessage");
            assert_eq!(told.len(), 1, "{cycle_told:?}");
            assert_eq!(
                entry(&told[0], "damageAttributes"),
                damage_told(&expected),
                "the shot after {bonus_before} of bonus"
            );
            assert_bonus_close(scene.spool_of(npc, NPC_SHIP).unwrap().bonus, bonus_after);
        }
    }

    #[test]
    fn a_precursor_weapon_that_stops_starts_its_ramp_again_from_none_when_it_runs_again() {
        let (mut scene, npc, now) = duel();
        let cycle = 2_500 * SECOND / 1_000;
        let mut weapon = laser();
        weapon.spool = Some(Spool { per_cycle: 0.07, max: 2.125 });
        scene.activate(npc, laser_effect(spec().ship_id, weapon.clone()), now);
        scene.poll(now + cycle + 1);
        scene.poll(now + 2 * cycle + 2);
        assert_bonus_close(scene.spool_of(npc, NPC_SHIP).unwrap().bonus, 0.21);
        // the weapon is asked to stop: it stops at the end of the cycle it is in, and what it has built goes with it
        scene.deactivate(npc, NPC_SHIP, now + 2 * cycle + 3);
        scene.poll(now + 3 * cycle + 4);
        assert_eq!(scene.spool_of(npc, NPC_SHIP), None, "the effect is over");
        // it runs again from none: the first shot has no bonus, and one cycle's worth is built after it
        scene.activate(npc, laser_effect(spec().ship_id, weapon), now + 4 * cycle);
        assert_bonus_close(scene.spool_of(npc, NPC_SHIP).unwrap().bonus, 0.07);
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

    // ---- launchers in a scene ----

    /// A launcher of rockets: what a pilot fits to fire scourge rockets, with the speed, the flight time and the explosion radius of the missile that suit the test.
    fn launcher(max_velocity: f64, flight_time_ms: f64, explosion_radius: f64) -> Turret {
        Turret {
            weapon_type_id: 10_629,
            fx: "effects.MissileDeployment".to_string(),
            raw_damage: [0.0, 0.0, 38.115, 0.0],
            optimal_range: max_velocity * flight_time_ms / 1000.0,
            falloff: 0.0,
            tracking_speed: 0.0,
            signature_resolution: 40_000.0,
            charge: None,
            missile: Some(Missile {
                charge_type_id: 266,
                group_id: 387,
                name: "Scourge Rocket".to_string(),
                radius: 300.0,
                mass: 100.0,
                agility: 5.22,
                hit_points: 90.0,
                max_velocity,
                flight_time_ms,
                explosion_radius,
                explosion_velocity: 1.0e9,
                damage_reduction_factor: 0.5,
                damage_reduction_sensitivity: crate::missiles::DEFAULT_REDUCTION_SENSITIVITY,
            }),
            spool: None,
            aoe: None,
            arc: None,
        }
    }

    /// One cycle of a launcher at a target (what the tests look at is the one missile it fires).
    fn missile_effect(target_id: i64, turret: Turret) -> Effect {
        Effect {
            burns_fuel: false,
            effect_id: 101,
            fx: "effects.MissileDeployment".to_string(),
            repeat: 1,
            ..laser_effect(target_id, turret)
        }
    }

    /// The names of the actions in the destiny updates a ship was sent, in order.
    fn action_names(out: &[Out], session: SessionId) -> Vec<String> {
        out.iter()
            .filter_map(|o| match o {
                Out::Notify { session: s, name: "DoDestinyUpdate", args, .. } if *s == session => {
                    Some(args[0].clone())
                }
                _ => None,
            })
            .flat_map(|actions| {
                let Value::List(actions) = actions else { return Vec::new() };
                actions
                    .into_iter()
                    .filter_map(|action| {
                        let Value::Tuple(parts) = action else { return None };
                        let Some(Value::Tuple(body)) = parts.get(1) else { return None };
                        Some(format!("{:?}", body[0]))
                    })
                    .collect()
            })
            .collect()
    }

    #[test]
    fn a_launcher_makes_a_missile_of_its_own_that_the_client_is_given_as_a_ball_and_that_is_no_ship() {
        let (mut scene, npc, now) = duel();
        let started =
            scene.activate(npc, missile_effect(spec().ship_id, launcher(2_250.0, 2_000.0, 56.0)), now);
        assert_eq!(scene.missiles.len(), 1);
        let missile = scene.missiles().next().unwrap().clone();
        assert_eq!(missile.id, crate::missile_entity::MISSILE_ID_BASE);
        assert_eq!((missile.shooter.ship_id, missile.target), (NPC_SHIP, spec().ship_id));
        // the player is shown the ball and the launch in one update
        let names = action_names(&started, PLAYER);
        assert!(
            names.iter().any(|n| n.contains("AddBalls2"))
                && names.iter().any(|n| n.contains("LaunchMissile")),
            "{names:?}"
        );
        // it is not a pilot: it has no place among the ships, and nothing can lock it
        assert!(scene.session_of(missile.id).is_none());
        assert!(scene.pilots.values().all(|pilot| pilot.ship.spec.ship_id != missile.id));
        let refused = scene.target_order(PLAYER, Order::Lock(missile.id), now);
        assert!(messages(&refused, PLAYER, "OnTarget").is_empty(), "a missile cannot be locked");
        assert!(scene.locked_by(PLAYER).is_empty());
    }

    #[test]
    fn a_launcher_that_a_guidance_disruptor_holds_fires_slower_shorter_and_wider_missiles() {
        // undisrupted, the rocket flies at 2250 m/s for 2 s and bursts 56 m wide
        let (mut scene, npc, now) = duel();
        scene.activate(npc, missile_effect(spec().ship_id, launcher(2_250.0, 2_000.0, 56.0)), now);
        let free = scene.missiles().next().unwrap().spec.clone();
        assert_eq!((free.max_velocity, free.flight_time_ms, free.explosion_radius), (2_250.0, 2_000.0, 56.0));
        // a guidance disruptor on the launcher's ship takes a tenth of the speed and the flight time and a fifth of the explosion velocity, and widens the burst a fifth
        let (mut scene, npc, now) = duel();
        scene.pilots.get_mut(&npc).unwrap().incoming.upsert(
            777,
            888,
            crate::ewar::Lasting::GuidanceDisrupt {
                velocity_percent: -10.0,
                delay_percent: -10.0,
                aoe_velocity_percent: -20.0,
                aoe_cloud_percent: 20.0,
            },
            now + 60 * SECOND,
        );
        scene.activate(npc, missile_effect(spec().ship_id, launcher(2_250.0, 2_000.0, 56.0)), now);
        let disrupted = scene.missiles().next().unwrap().spec.clone();
        assert_eq!(disrupted.max_velocity, 2_025.0);
        assert_eq!(disrupted.flight_time_ms, 1_800.0);
        assert_eq!(disrupted.explosion_radius, 67.2);
        assert_eq!(disrupted.explosion_velocity, 8.0e8);
        // what the missile is stays what it is
        assert_eq!((disrupted.charge_type_id, disrupted.hit_points), (free.charge_type_id, free.hit_points));
    }

    #[test]
    fn nothing_is_told_of_the_damage_while_the_missile_flies_and_a_hit_of_quality_three_when_it_lands() {
        let (mut scene, npc, now) = duel();
        // the player's ship is 28 m across to a missile that explodes at 56: the rocket does half what it carries
        let started =
            scene.activate(npc, missile_effect(spec().ship_id, launcher(2_250.0, 2_000.0, 56.0)), now);
        assert!(messages(&started, PLAYER, "OnDamageMessage").is_empty(), "the missile has only just left");
        let mut told = Vec::new();
        let mut landed_at = None;
        for step in 1..=12 {
            let at = now + step * SECOND / 10;
            let out = scene.poll(at);
            if landed_at.is_none() && !messages(&out, PLAYER, "OnDamageMessage").is_empty() {
                landed_at = Some(at);
            }
            told.extend(out);
        }
        let landed = landed_at.expect("the missile gets there");
        // 600 m of surface at 2250 m/s: a quarter of a second or so
        assert!(
            landed - now > SECOND / 10 && landed - now <= SECOND / 2,
            "{}",
            (landed - now) as f64 / SECOND as f64
        );
        let hits = messages(&told, PLAYER, "OnDamageMessage");
        assert_eq!(hits.len(), 1, "the one missile hits once: {hits:?}");
        assert_eq!(entry(&hits[0], "attackType"), text("otherPlayerWeapons"));
        assert_eq!(entry(&hits[0], "hitQuality"), int(3));
        assert_eq!(entry(&hits[0], "weapon"), int(10_629));
        assert_eq!(
            entry(&hits[0], "damageAttributes"),
            Value::Dict(vec![
                (int(114), int(0)),
                (int(118), int(0)),
                (int(117), real(19.0575)),
                (int(116), int(0))
            ])
        );
        // it explodes where it lands, and the ball goes
        let names = action_names(&told, PLAYER);
        let explosion = names
            .iter()
            .position(|n| n.contains("TerminalPlayDestructionEffect"))
            .expect("the explosion is played");
        assert!(names[explosion + 1].contains("RemoveBalls"), "{names:?}");
        assert!(scene.missiles.is_empty(), "it is spent");
    }

    #[test]
    fn a_missile_that_takes_long_to_get_there_spreads_and_its_ball_stays_for_the_warhead_a_while_after_it_lands()
     {
        let (mut scene, npc, now) = duel();
        // 600 m at 300 m/s is two seconds: more than the client flies out straight
        scene.activate(npc, missile_effect(spec().ship_id, launcher(300.0, 5_000.0, 56.0)), now);
        assert!(scene.missiles().next().unwrap().spreads);
        let mut told = Vec::new();
        let mut hit_at = None;
        for step in 1..=60 {
            let at = now + step * SECOND / 10;
            let out = scene.poll(at);
            if hit_at.is_none() && !messages(&out, PLAYER, "OnDamageMessage").is_empty() {
                hit_at = Some(at);
            }
            told.extend(out);
            if scene.missiles.is_empty() {
                break;
            }
        }
        let hit_at = hit_at.expect("it lands");
        let names = action_names(&told, PLAYER);
        assert!(
            !names.iter().any(|n| n.contains("TerminalPlayDestructionEffect")),
            "a spread missile plays no explosion: {names:?}"
        );
        assert!(
            names.iter().any(|n| n.contains("RemoveBalls")),
            "and the ball is let go in the end: {names:?}"
        );
        assert!(scene.missiles.is_empty());
        // it stayed at least the grace after it landed (and the rest of its flight)
        assert!(now + 5 * SECOND >= hit_at);
    }

    #[test]
    fn a_target_further_off_than_the_missile_flies_refuses_the_launcher_with_how_far_it_goes() {
        let (mut scene, npc, now) = duel();
        // 300 m of flight against the distance between the two ships
        let apart = scene.apart(npc, PLAYER).unwrap();
        assert!(apart > 301.0);
        let refused =
            scene.activate(npc, missile_effect(spec().ship_id, launcher(1_500.0, 200.0, 56.0)), now);
        let Some(Out::TargetRefused { reason, desired_range, actual_distance, target_type, .. }) =
            refused.first()
        else {
            panic!("{refused:?}")
        };
        assert_eq!(*reason, "TARGET_OUT_OF_RANGE");
        assert_eq!(*desired_range, 300.0);
        assert_eq!(*actual_distance, apart);
        assert_eq!(*target_type, spec().type_id);
        assert_eq!(scene.pilots[&npc].modules.running_count(), 0, "it never started");
        assert!(scene.missiles.is_empty());
    }

    #[test]
    fn a_missile_whose_target_has_left_is_gone_with_it_and_does_nothing() {
        let (mut scene, npc, now) = duel();
        scene.activate(npc, missile_effect(spec().ship_id, launcher(300.0, 5_000.0, 56.0)), now);
        scene.leave(PLAYER, crate::scene::Departure::LoggedOff, now + SECOND / 10);
        let later = scene.poll(now + SECOND / 5);
        assert!(messages(&later, PLAYER, "OnDamageMessage").is_empty());
        assert!(scene.missiles.is_empty(), "it is gone with its target");
    }

    #[test]
    fn a_launcher_aggresses_its_target_when_it_fires_not_when_the_missile_lands() {
        let (mut scene, npc, now) = duel();
        scene.activate(npc, missile_effect(spec().ship_id, launcher(2_250.0, 2_000.0, 56.0)), now);
        assert_eq!(scene.aggressions.len(), 1);
        assert_eq!(scene.aggressions[0].target_ship, spec().ship_id);
        assert_eq!(scene.aggressions[0].module_type_id, 10_629);
    }

    #[test]
    fn what_shoots_a_missile_down_hurts_the_missile_not_the_ships_and_the_missile_does_nothing_when_it_is_down()
     {
        use crate::missile_entity::Interceptor;
        let (mut scene, npc, now) = duel();
        scene.activate(npc, missile_effect(spec().ship_id, launcher(300.0, 5_000.0, 56.0)), now);
        let id = scene.missiles().next().unwrap().id;
        let player_at = scene.ship(PLAYER, now).unwrap().position;
        assert_eq!(scene.missiles_within(player_at, 1_000_000.0), [id], "it is in range of anything near it");
        let by = Interceptor::Smartbomb { ship_id: spec().ship_id, module_id: 1 };
        // a hit that is not enough leaves it flying, with what it has left
        assert!(scene.damage_missile(id, 50.0, by, now).is_empty());
        assert_eq!(scene.missiles().next().unwrap().hit_points, 40.0);
        // the one that is enough brings it down: the explosion is played and the ball goes, to whoever was told of it
        let shot_down = scene.damage_missile(id, 40.0, by, now + SECOND / 10);
        let names = action_names(&shot_down, PLAYER);
        assert!(
            names.iter().any(|n| n.contains("TerminalPlayDestructionEffect"))
                && names.iter().any(|n| n.contains("RemoveBalls")),
            "{names:?}"
        );
        assert!(scene.missiles.is_empty());
        // nothing lands afterwards, and no ship was hurt by what shot it
        let mut told = Vec::new();
        for step in 1..=60 {
            told.extend(scene.poll(now + SECOND / 10 + step * SECOND / 10));
        }
        assert!(messages(&told, PLAYER, "OnDamageMessage").is_empty());
        assert!(scene.missiles_within(player_at, 1_000_000.0).is_empty());
    }

    /// A doomsday's strike in the shape of the Bosonic Field Generator (40633), with `pulses` pulses of one unit of em damage, one second apart, after two.
    fn strike_of(pulses: i32) -> crate::strike::Strike {
        crate::strike::Strike {
            shape: crate::strike::StrikeShape::Cone,
            direction: [1.0, 0.0, 0.0],
            length: 30_000.0,
            radius: 0.0,
            warning_ms: 2_000.0,
            pulse_ms: 1_000.0,
            pulses,
            damage: [1.0, 0.0, 0.0, 0.0],
            signature_radius: 1.0,
            immobile_ms: 30_000.0,
            no_jump_ms: 300_000.0,
            neut_amount: 0.0,
            neut_radius: 0.0,
            neut_signature_radius: 1.0,
            debuff_ms: 0.0,
            sweep_end: None,
        }
    }

    fn strike_effect(module_id: i64, strike: crate::strike::Strike) -> Effect {
        Effect {
            burns_fuel: false,
            module_id,
            module_type: 40_633,
            effect_id: 6_473,
            fx: String::new(),
            duration_ms: 300_000.0,
            capacitor_need: 0.0,
            repeat: 1,
            target_id: 0,
            turret: None,
            action: Some(crate::modules::Action::Strike { strike }),
            on: None,
            off: None,
            on_changes: Vec::new(),
            off_changes: Vec::new(),
        }
    }

    /// Runs `body` on its own thread and fails the test when it has not returned within `seconds`. A simulation whose moment never clears (a
    /// module moment that is due again at once) spins in the caller's loop forever; here it is a failure in `seconds`, and a panic in `body` is
    /// re-raised with its message.
    fn finishes_within(seconds: u64, body: impl FnOnce() + Send + 'static) {
        use std::sync::mpsc::RecvTimeoutError;
        let (done, finished) = std::sync::mpsc::channel::<()>();
        let worker = std::thread::spawn(move || {
            body();
            let _ = done.send(());
        });
        match finished.recv_timeout(std::time::Duration::from_secs(seconds)) {
            Ok(()) | Err(RecvTimeoutError::Disconnected) => {
                if let Err(panic) = worker.join() {
                    std::panic::resume_unwind(panic);
                }
            }
            Err(RecvTimeoutError::Timeout) => {
                panic!("did not finish within {seconds} s: a simulation moment does not clear")
            }
        }
    }

    /// The damage messages the pilot of `session` is told about its own hits.
    fn hits_told(out: &[Out], session: SessionId) -> usize {
        messages(out, session, "OnDamageMessage")
            .iter()
            .filter(|args| matches!(args, Value::Dict(entries) if entries.iter().any(|(k, v)| matches!(k, Value::Str(s) if s.as_ref() == "attackType") && *v == Value::Str("me".into()))))
            .count()
    }

    /// The armour a remote repair of 1000 gives a damaged ship in the duel, with the ship disrupted or not (protocol 28: half while disrupted).
    fn armor_gained_from_remote_repair(disrupted: bool) -> f64 {
        let (mut scene, npc, _) = duel();
        let ship = scene.pilots[&npc].ship.spec.ship_id;
        let now = NOON + 120 * SECOND;
        scene.damage(ship, [60_000.0, 60_000.0, 60_000.0, 60_000.0], now);
        if disrupted {
            scene.pilots.get_mut(&npc).unwrap().disrupted_until = now + 60 * SECOND;
        }
        let before = scene.pilots[&npc].ship.health.layers_at(now).armor;
        scene.mend_remote(ship, crate::health::Layer::Armor, 50.0, now);
        scene.pilots[&npc].ship.health.layers_at(now).armor - before
    }

    #[test]
    fn a_disrupted_ship_takes_half_the_remote_repair_from_another_ship() {
        let full = armor_gained_from_remote_repair(false);
        let halved = armor_gained_from_remote_repair(true);
        assert!(full > 0.0, "the control repair lands: {full}");
        assert!((halved - full * crate::strike::DISRUPTED_REMOTE_REPAIR).abs() < 1e-6, "half of {full} is {halved}");
    }

    #[test]
    fn a_sweeping_strike_runs_its_axis_from_its_start_point_to_its_end_point() {
        let (mut scene, _npc, _) = duel();
        let now = NOON + 120 * SECOND;
        // the axis starts on the npc (2 km down x, two kilometres long) and turns to the y axis by the last pulse, where the npc is no longer
        let strike = crate::strike::Strike {
            shape: crate::strike::StrikeShape::Cylinder,
            radius: 500.0,
            length: 2_000.0,
            warning_ms: 2_000.0,
            sweep_end: Some([0.0, 2_000.0, 0.0]),
            ..strike_of(2)
        };
        let out = scene.activate(PLAYER, strike_effect(spec().ship_id + 1, strike), now);
        assert!(!out.iter().any(|o| matches!(o, Out::Refused { .. })), "{out:?}");
        assert_eq!(hits_told(&scene.poll(now + 2 * SECOND), PLAYER), 1, "the first pulse, on the start point");
        assert_eq!(hits_told(&scene.poll(now + 3 * SECOND), PLAYER), 0, "the last pulse is on the end point, off the npc");
    }

    #[test]
    fn a_disruptive_pulse_disrupts_each_ship_it_covers_and_leaves_the_others_alone() {
        let (mut scene, npc, _) = duel();
        let now = NOON + 120 * SECOND;
        let strike = crate::strike::Strike { debuff_ms: 60_000.0, ..strike_of(1) };
        scene.activate(PLAYER, strike_effect(spec().ship_id + 1, strike), now);
        assert_eq!(scene.pilots[&npc].disrupted_until, 0, "nothing is disrupted before the pulse");
        scene.poll(now + 2 * SECOND);
        assert_eq!(scene.pilots[&npc].disrupted_until, now + 62 * SECOND, "covered: disrupted for the debuff after the pulse");
        // the same pulse aimed away from the npc leaves it undisrupted
        let (mut scene, npc, _) = duel();
        let away = crate::strike::Strike { debuff_ms: 60_000.0, direction: [-1.0, 0.0, 0.0], ..strike_of(1) };
        scene.activate(PLAYER, strike_effect(spec().ship_id + 1, away), now);
        scene.poll(now + 2 * SECOND);
        assert_eq!(scene.pilots[&npc].disrupted_until, 0, "not covered: not disrupted");
    }

    #[test]
    fn a_strike_hits_the_ships_in_its_area_pulse_by_pulse_after_its_warning() {
        let (mut scene, _npc, _) = duel();
        let now = NOON + 120 * SECOND;
        // the pilot aims down the x axis, where the npc is 2 km away: the first pulse is due at the warning, then every second
        let out = scene.activate(PLAYER, strike_effect(spec().ship_id + 1, strike_of(3)), now);
        assert!(!out.iter().any(|o| matches!(o, Out::Refused { .. })), "{out:?}");
        assert_eq!(hits_told(&out, PLAYER), 0, "nothing hits before the warning");
        assert_eq!(hits_told(&scene.poll(now + SECOND), PLAYER), 0, "one second in: still warning");
        assert_eq!(hits_told(&scene.poll(now + 2 * SECOND), PLAYER), 1, "the first pulse at the warning");
        assert_eq!(hits_told(&scene.poll(now + 3 * SECOND), PLAYER), 1, "the second pulse");
        assert_eq!(hits_told(&scene.poll(now + 4 * SECOND), PLAYER), 1, "the third pulse");
        assert_eq!(hits_told(&scene.poll(now + 9 * SECOND), PLAYER), 0, "the pulses are spent: no more hits");
    }

    #[test]
    fn a_strike_does_not_hit_what_is_behind_or_off_its_axis() {
        let (mut scene, npc, _) = duel();
        let now = NOON + 120 * SECOND;
        // aimed the other way, along -x: the npc is behind the pilot's aim
        let mut strike = strike_of(2);
        strike.direction = [-1.0, 0.0, 0.0];
        scene.activate(PLAYER, strike_effect(spec().ship_id + 1, strike), now);
        assert_eq!(hits_told(&scene.poll(now + 5 * SECOND), PLAYER), 0, "the npc is not in the cone");
        let _ = npc;
    }

    #[test]
    fn a_strike_holds_the_ship_for_its_immobility_window_and_then_lets_it_go() {
        let (mut scene, _npc, _) = duel();
        let now = NOON + 120 * SECOND;
        scene.activate(PLAYER, strike_effect(spec().ship_id + 1, strike_of(1)), now);
        let refused = scene.command(PLAYER, Command::Stop, now + SECOND);
        assert!(
            refused.iter().any(|o| matches!(o, Out::Refused { reason: "IMMOBILE", .. })),
            "a held ship takes no orders: {refused:?}"
        );
        let released = scene.command(PLAYER, Command::Stop, now + 31 * SECOND);
        assert!(!released.iter().any(|o| matches!(o, Out::Refused { .. })), "after 30 s: {released:?}");
    }

    #[test]
    fn a_strike_stops_the_jump_and_the_cloak_for_its_no_jump_window() {
        let (mut scene, _npc, _) = duel();
        let now = NOON + 120 * SECOND;
        scene.activate(PLAYER, strike_effect(spec().ship_id + 1, strike_of(1)), now);
        let cloak = Effect {
            effect_id: 0,
            module_id: spec().ship_id + 2,
            fx: "effects.Cloaking".to_string(),
            action: Some(crate::modules::Action::Cloak),
            duration_ms: 1_000.0,
            ..strike_effect(spec().ship_id + 2, strike_of(1))
        };
        let out = scene.activate(PLAYER, cloak, now + 10 * SECOND);
        assert!(
            out.iter().any(|o| matches!(o, Out::Refused { reason: "NO_JUMP_OR_CLOAK", .. })),
            "a ship in its no-jump window does not cloak: {out:?}"
        );
    }

    #[test]
    fn a_strike_drains_the_capacitor_of_the_ships_within_its_neutralising_radius() {
        let (mut scene, npc, _) = duel();
        let now = NOON + 120 * SECOND;
        let before = scene.pilots[&PLAYER].modules.capacitor.as_ref().expect("the pilot has a capacitor").charge_at(now);
        // the npc fires a strike whose neutralising wave reaches the pilot 2 km away
        let mut strike = strike_of(1);
        strike.neut_amount = 50.0;
        strike.neut_radius = 10_000.0;
        scene.activate(npc, strike_effect(NPC_SHIP + 1, strike), now);
        let after = scene.pilots[&PLAYER].modules.capacitor.as_ref().expect("the pilot has a capacitor").charge_at(now);
        assert!(before - after > 49.0 && before - after <= 50.0 + 1e-6, "drained by the neutralising amount: {before} -> {after}");
    }

    #[test]
    fn a_strike_neutralisation_is_weakened_by_the_targets_energy_warfare_resistance() {
        let (mut scene, npc, _) = duel();
        let now = NOON + 120 * SECOND;
        scene.pilots.get_mut(&PLAYER).unwrap().ship.spec.energy_warfare_resistance = 0.5;
        let before = scene.pilots[&PLAYER]
            .modules
            .capacitor
            .as_ref()
            .expect("the pilot has a capacitor")
            .charge_at(now);
        let mut strike = strike_of(1);
        strike.neut_amount = 50.0;
        strike.neut_radius = 10_000.0;
        scene.activate(npc, strike_effect(NPC_SHIP + 1, strike), now);
        let after = scene.pilots[&PLAYER]
            .modules
            .capacitor
            .as_ref()
            .expect("the pilot has a capacitor")
            .charge_at(now);
        assert!(
            (before - after - 25.0).abs() < 1e-3,
            "half of 50 against a resistance of 0.5: {before} -> {after}"
        );
    }

    /// A titan doomsday's effect: one hit on `target_id`, the module of a Judgment (24550, effect 4489), one cycle.
    fn doomsday_effect(module_id: i64, target_id: i64, action: crate::modules::Action) -> Effect {
        Effect {
            burns_fuel: false,
            module_id,
            module_type: 24_550,
            effect_id: 4_489,
            fx: String::new(),
            duration_ms: 300_000.0,
            capacitor_need: 0.0,
            repeat: 1,
            target_id,
            turret: None,
            action: Some(action),
            on: None,
            off: None,
            on_changes: Vec::new(),
            off_changes: Vec::new(),
        }
    }

    /// The pilot locks `target` and the lock is had: returns the moment it is had.
    fn lock_target(scene: &mut Scene, session: SessionId, target: i64, now: i64) -> i64 {
        scene.target_order(session, Order::Lock(target), now);
        let mut now = now;
        for _ in 0..40 {
            now += SECOND / 4;
            scene.poll(now);
        }
        assert!(scene.locked_by(session).contains(&target), "the lock on {target} is had");
        now
    }

    #[test]
    fn a_titan_doomsday_hits_its_locked_target_once_after_its_delay_and_holds_the_pilot() {
        // bounded: a hit that is not recorded makes the moment due again at once, and the scheduler would never leave it
        finishes_within(10, || {
            let (mut scene, _npc, now) = duel();
            let now = lock_target(&mut scene, PLAYER, NPC_SHIP, now);
            let hit = crate::modules::Action::Doomsday {
                delay_ms: 9_000.0,
                damage: [2_400_000.0, 0.0, 0.0, 0.0],
                immobile_ms: 30_000.0,
                no_jump_ms: 300_000.0,
            };
            let out = scene.activate(PLAYER, doomsday_effect(spec().ship_id + 1, NPC_SHIP, hit), now);
            assert!(!out.iter().any(|o| matches!(o, Out::Refused { .. })), "{out:?}");
            assert_eq!(hits_told(&out, PLAYER), 0, "nothing hits before its delay");
            assert_eq!(hits_told(&scene.poll(now + 8 * SECOND), PLAYER), 0, "8 s in: still charging");
            assert_eq!(hits_told(&scene.poll(now + 10 * SECOND), PLAYER), 1, "the hit, 9 s after the start");
            assert_eq!(hits_told(&scene.poll(now + 20 * SECOND), PLAYER), 0, "once: no second hit");
            let held = scene.command(PLAYER, Command::Stop, now + 11 * SECOND);
            assert!(
                held.iter().any(|o| matches!(o, Out::Refused { reason: "IMMOBILE", .. })),
                "held by its window: {held:?}"
            );
        });
    }

    #[test]
    fn a_titan_doomsday_is_refused_without_a_locked_target_and_hits_nothing_it_is_not_locked_on() {
        let (mut scene, _npc, now) = duel();
        let hit = crate::modules::Action::Doomsday { delay_ms: 9_000.0, damage: [1.0, 0.0, 0.0, 0.0], immobile_ms: 0.0, no_jump_ms: 0.0 };
        let none = scene.activate(PLAYER, doomsday_effect(spec().ship_id + 1, 0, hit.clone()), now);
        assert!(none.iter().any(|o| matches!(o, Out::Refused { reason: "TARGET_REQUIRED", .. })), "{none:?}");
        let unlocked = scene.activate(PLAYER, doomsday_effect(spec().ship_id + 1, NPC_SHIP, hit.clone()), now);
        assert!(unlocked.iter().any(|o| matches!(o, Out::Refused { reason: "TARGET_NOT_LOCKED", .. })), "{unlocked:?}");
        let gone = scene.activate(PLAYER, doomsday_effect(spec().ship_id + 1, 999_999, hit), now);
        assert!(gone.iter().any(|o| matches!(o, Out::Refused { reason: "TARGET_NOT_FOUND", .. })), "{gone:?}");
    }

    fn smartbomb_effect(module_id: i64, damage: Damage) -> Effect {
        Effect {
            burns_fuel: false,
            module_id,
            module_type: 3_977,
            effect_id: 1,
            fx: String::new(),
            duration_ms: 3_000.0,
            capacitor_need: 0.0,
            repeat: 1,
            target_id: 0,
            turret: None,
            action: Some(crate::modules::Action::Smartbomb { range: 5_000.0, damage, hurts_pilots: true }),
            on: None,
            off: None,
            on_changes: Vec::new(),
            off_changes: Vec::new(),
        }
    }

    #[test]
    fn a_smartbomb_shoots_down_the_missiles_of_others_in_its_reach_and_spares_its_own_pilot_s() {
        let (mut scene, npc, now) = duel();
        scene.activate(npc, missile_effect(spec().ship_id, launcher(300.0, 5_000.0, 56.0)), now);
        assert_eq!(scene.missiles.len(), 1);
        // the pilot who fired the missile sets off a bomb of its own: its missile is not hurt
        scene.activate(npc, smartbomb_effect(NPC_SHIP + 1, [200.0, 0.0, 0.0, 0.0]), now + SECOND / 20);
        assert_eq!(scene.missiles.len(), 1, "its own missile is spared");
        assert_eq!(scene.missiles().next().unwrap().hit_points, 90.0);
        // the target's bomb is enough to bring it down (it has 90 hit points), with the explosion played to whoever saw it launched
        let out = scene.activate(
            PLAYER,
            smartbomb_effect(spec().ship_id + 1, [100.0, 0.0, 0.0, 0.0]),
            now + SECOND / 10,
        );
        assert!(scene.missiles.is_empty(), "shot down");
        let names = action_names(&out, PLAYER);
        assert!(
            names.iter().any(|n| n.contains("TerminalPlayDestructionEffect"))
                && names.iter().any(|n| n.contains("RemoveBalls")),
            "{names:?}"
        );
    }

    #[test]
    fn a_smartbomb_broadcasts_its_emp_wave_on_every_cycle() {
        let (mut scene, npc, now) = duel();
        let module_id = NPC_SHIP + 1;
        let mut effect = smartbomb_effect(module_id, [40.0, 0.0, 0.0, 0.0]);
        effect.repeat = 2;

        let wave_fx = |out: &[Out]| {
            let player_out =
                out.iter().filter(|message| message.session() == PLAYER).cloned().collect::<Vec<_>>();
            special_fx(&player_out)
                .into_iter()
                .filter(|args| args.get(5) == Some(&Value::Str("effects.EMPWave".into())))
                .collect::<Vec<_>>()
        };

        let first = scene.activate(npc, effect, now);
        let waves = wave_fx(&first);
        assert_eq!(waves.len(), 1, "{waves:?}");
        assert_eq!(waves[0][0], id(NPC_SHIP));
        assert_eq!(waves[0][1], id(module_id));
        assert_eq!(waves[0][2], int(3_977));
        assert_eq!(waves[0][9], crate::wire::real(3_000.0));

        let second = scene.poll(now + 3 * SECOND);
        let waves = wave_fx(&second);
        assert_eq!(waves.len(), 1, "{waves:?}");
        assert_eq!(waves[0][0], id(NPC_SHIP));
        assert_eq!(waves[0][1], id(module_id));
        assert_eq!(waves[0][5], Value::Str("effects.EMPWave".into()));
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
