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
mod tests;
