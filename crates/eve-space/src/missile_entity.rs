//! Missiles in flight: each one an entity of its own in the scene, a ball in the ballpark that is not a ship.
//!
//! A launcher does not hurt anything itself. Each cycle it makes a [`MissileEntity`], which keeps where it was launched from, where it is, how fast it flies, how long it has to
//! live, who fired it (the pilot, the ship, the launcher module) and what it flies at, and which the scene moves on at every poll: it homes on its target (a missile turns at once),
//! closes on it, and either reaches it (a swept test of the step against the target's radius, as the reference does), runs out of flight time (it stalls and falls away, or, when the
//! client's warhead is due by then, lands all the same), or loses its target (the target warped, was destroyed, left) and is gone. The damage is done when it lands, by the numbers of
//! the missile ([`crate::missiles::Missile::applied`]) against the signature and the speed the target has then.
//!
//! The rules and the numbers are the reference's (`runtime/missileEntity.js`, `destiny/simulation/movement.js`, `combat/missiles/missileSolver.js`). The client is told
//! as the reference tells it: `AddBalls2` of the missile with a `LaunchMissile` in one update when it is fired (the client flies it itself from the ball: a mode of its own,
//! `MISSILE`, with the target and the shooter in it), `RemoveBalls` when it is gone, with the explosion played first for one that lands and does not spread.
//!
//! A missile is kept apart from the ships: it has no place among the pilots, no slim item of a ship, no lock (nothing can target it), and no shield, armor or hull; the only way
//! to hurt it is the `hit_points` it carries, and the only things that do are the interceptors named by [`Interceptor`] (a smartbomb goes off among them; a defender missile
//! and a point defense battery are shot at them: `Scene::intercept_missile` and `Scene::damage_missile` are what they call).

use std::collections::BTreeSet;

use eve_destiny::ball::LegacyMode;
use eve_destiny::{BallEntity, IdRef, Kind, Vec3};
use eve_marshal::Value;

use crate::health::Damage;
use crate::missiles::Missile;
use crate::modules::Effect;
use crate::npc::is_npc_session;
use crate::payload;
use crate::scene::{Out, Scene, SessionId, TICKS_PER_SECOND};
use crate::visibility::{Presence, can_see};
use crate::weapons::{Aggression, Kinematics, MISSILE_RANGE_GRACE};
use crate::wire::{id, int, list, text, tuple, word};

/// The id of the first missile a scene makes (the reference's runtime ids; the first one recorded is `4200000000000000`).
pub const MISSILE_ID_BASE: i64 = 4_200_000_000_000_000;

/// The category of a missile's slim item: a charge.
pub const CHARGE_CATEGORY: i32 = 8;

/// A missile that takes this long (seconds) to reach its target does not fly out straight: the client spreads it (`doSpread`, the reference's
/// `MISSILE_CLIENT_NO_SPREAD_THRESHOLD_SECONDS`).
pub const NO_SPREAD_BELOW_SECONDS: f64 = 1.6;

/// How long the ball of a missile that spread stays after it has landed, for the client's warhead to finish (the reference's `MISSILE_CLIENT_RELEASE_GRACE_MS`).
pub const RELEASE_GRACE: i64 = 2 * TICKS_PER_SECOND;

/// Who fired a missile: the pilot, the ship it flies, the launcher and what the combat log calls the weapon.
#[derive(Debug, Clone, PartialEq)]
pub struct Shooter {
    pub session: SessionId,
    pub ship_id: i64,
    pub module_id: i64,
    /// The type of the launcher (what `OnDamageMessage` calls `weapon`).
    pub weapon_type_id: i32,
    /// The character that flies the ship, or its owner.
    pub owner_id: i64,
    pub corporation_id: i32,
    pub alliance_id: i32,
}

/// Where a missile is in its life.
#[derive(Debug, Clone, Copy, PartialEq)]
pub enum Phase {
    /// On its way: homing on the target and closing.
    Flying,
    /// It has landed and done its damage. A missile that spread keeps its ball up until `release_at` for the client's warhead to finish.
    Spent { release_at: i64 },
}

/// What brings a missile down before it gets there.
#[derive(Debug, Clone, Copy, PartialEq)]
pub enum Interceptor {
    /// A smartbomb that went off among the missiles: the ship of its pilot and the module.
    Smartbomb { ship_id: i64, module_id: i64 },
    /// A point defense battery shot at it.
    PointDefense { module_id: i64 },
    /// A defender missile met it: the defender's id.
    Defender { missile_id: i64 },
}

/// A missile in flight.
#[derive(Debug, Clone, PartialEq)]
pub struct MissileEntity {
    pub id: i64,
    pub shooter: Shooter,
    /// The ship it flies at.
    pub target: i64,
    /// What the missile is and does (the numbers of its charge).
    pub spec: Missile,
    /// The damage it carries before the explosion reaches the target (em, thermal, kinetic, explosive; the pilot's multiplier is in it).
    pub raw_damage: Damage,
    pub launch_position: Vec3,
    pub position: Vec3,
    pub velocity: Vec3,
    pub direction: Vec3,
    pub launched_at: i64,
    /// Where the poll last moved it to.
    pub stepped_at: i64,
    /// When its flight time is up (the reference adds the time it takes to clear the radius of the ship that fired it).
    pub expires_at: i64,
    /// When the client's warhead is due to land (the average of the surface and the centre time): a flight that ends after it still does its damage.
    pub impact_due: i64,
    /// Whether the client spreads the missile (it takes this long to get there that it does not fly out straight).
    pub spreads: bool,
    pub phase: Phase,
    /// What it has left of its own hit points (what an interceptor does to it; nothing a ship has is in it).
    pub hit_points: f64,
    /// Where the target was when the missile last looked: the start of the target's step in the swept test.
    pub target_seen: Vec3,
    /// Where the target was when it was fired (the ball's target point).
    pub target_at_launch: Vec3,
    /// The pilots that were told of it (they are told it is gone).
    pub watchers: BTreeSet<SessionId>,
}

fn sub(a: Vec3, b: Vec3) -> Vec3 {
    Vec3::new(a.x - b.x, a.y - b.y, a.z - b.z)
}

fn add(a: Vec3, b: Vec3) -> Vec3 {
    Vec3::new(a.x + b.x, a.y + b.y, a.z + b.z)
}

fn scale(a: Vec3, by: f64) -> Vec3 {
    Vec3::new(a.x * by, a.y * by, a.z * by)
}

fn dot(a: Vec3, b: Vec3) -> f64 {
    a.x * b.x + a.y * b.y + a.z * b.z
}

fn length(a: Vec3) -> f64 {
    dot(a, a).sqrt()
}

const REST: Vec3 = Vec3 { x: 0.0, y: 0.0, z: 0.0 };

fn unit(a: Vec3, fallback: Vec3) -> Vec3 {
    let magnitude = length(a);
    if magnitude.is_finite() && magnitude > 0.0 { scale(a, 1.0 / magnitude) } else { fallback }
}

/// A position or a velocity of a [`Kinematics`] as a vector.
fn vec(a: [f64; 3]) -> Vec3 {
    Vec3::new(a[0], a[1], a[2])
}

fn ticks(seconds: f64) -> i64 {
    (seconds * TICKS_PER_SECOND as f64).round() as i64
}

fn seconds(ticks: i64) -> f64 {
    ticks as f64 / TICKS_PER_SECOND as f64
}

/// Where a missile is put when it is fired: a muzzle in front of the ship, towards the target (the reference's launch position for a single launcher: a fraction of the
/// ship's radius, not less than 12 m and not more than 90).
pub fn muzzle(attacker: &Kinematics, toward: Vec3) -> Vec3 {
    let direction = unit(sub(toward, vec(attacker.position)), Vec3::new(1.0, 0.0, 0.0));
    add(vec(attacker.position), scale(direction, (attacker.radius.max(1.0) * 0.45).clamp(12.0, 90.0)))
}

/// How long a missile at `speed` takes to cover what is left of the way to a ship of this radius, in seconds: to its surface, or to its centre.
fn time_to_cover(from: Vec3, to: Vec3, target_radius: f64, speed: f64, to_centre: bool) -> f64 {
    if speed <= 0.000_001 {
        return 0.0;
    }
    let reach = if to_centre { 0.0 } else { target_radius.max(0.0) };
    (length(sub(to, from)) - reach).max(0.0) / speed
}

/// The step of a missile against the swept volume of its target: when, within a step of `step` seconds, the centre of the missile gets within `radius` of the
/// target's centre (the reference's `resolveMissileSweptImpact`). `None` when it does not.
fn swept_impact(
    missile_start: Vec3,
    missile_velocity: Vec3,
    target_start: Vec3,
    target_end: Vec3,
    radius: f64,
    step: f64,
) -> Option<f64> {
    let radius = radius.max(1.0);
    let target_velocity =
        if step > 0.000_001 { scale(sub(target_end, target_start), 1.0 / step) } else { REST };
    let start = sub(missile_start, target_start);
    let velocity = sub(missile_velocity, target_velocity);
    let speed_squared = dot(velocity, velocity);
    let c = dot(start, start) - radius * radius;
    if c <= 0.0 {
        return Some(0.0);
    }
    if speed_squared <= 0.000_001 {
        return None;
    }
    let b = 2.0 * dot(start, velocity);
    let discriminant = b * b - 4.0 * speed_squared * c;
    if discriminant < 0.0 {
        return None;
    }
    let root = discriminant.sqrt();
    [(-b - root) / (2.0 * speed_squared), (-b + root) / (2.0 * speed_squared)]
        .into_iter()
        .filter(|t| t.is_finite() && *t >= -0.000_001 && *t <= step + 0.000_001)
        .reduce(f64::min)
        .map(|t| t.clamp(0.0, step))
}

/// What the poll found of a missile when it moved it on.
#[derive(Debug, Clone, Copy, PartialEq)]
pub enum Motion {
    /// Still on its way.
    Flew,
    /// It reached the surface of the target.
    Reached,
    /// Its flight time is up before it reached the target: it stalls.
    RanOut,
}

impl MissileEntity {
    /// A missile fired from `attacker` at `target` at `now`: it starts at the muzzle, aimed at the target, with the time it has to live and the time the client's warhead will take.
    #[allow(clippy::too_many_arguments)]
    pub fn launch(
        id: i64,
        shooter: Shooter,
        target_id: i64,
        spec: Missile,
        raw_damage: Damage,
        attacker: &Kinematics,
        target: &Kinematics,
        now: i64,
    ) -> Self {
        let target_position = vec(target.position);
        let launch_position = muzzle(attacker, target_position);
        let direction = unit(sub(target_position, launch_position), Vec3::new(1.0, 0.0, 0.0));
        let speed = spec.max_velocity.max(0.0);
        // the client spreads a missile that takes long enough to get there; its time of arrival is the average of the surface and the centre
        let surface = time_to_cover(launch_position, target_position, target.radius, speed, false);
        let centre = time_to_cover(launch_position, target_position, target.radius, speed, true).max(0.5);
        let visual = if surface > 0.0 { (surface + centre) * 0.5 } else { centre * 0.5 };
        // the flight is as long as the charge flies, and the time to clear the radius of the ship that fires it
        let budget = spec.flight_time_ms / 1000.0
            + if speed > 0.000_001 { attacker.radius.max(0.0) / speed } else { 0.0 };
        let hit_points = spec.hit_points;
        Self {
            id,
            shooter,
            target: target_id,
            spec,
            raw_damage,
            launch_position,
            position: launch_position,
            velocity: scale(direction, speed),
            direction,
            launched_at: now,
            stepped_at: now,
            expires_at: now + ticks(budget),
            impact_due: now + ticks(visual),
            spreads: surface >= NO_SPREAD_BELOW_SECONDS,
            phase: Phase::Flying,
            hit_points,
            target_seen: target_position,
            target_at_launch: target_position,
            watchers: BTreeSet::new(),
        }
    }

    /// The ball the client is given when the missile is fired. A missile that spreads starts at rest (the client lets it out slowly); one that does not starts at its top speed.
    pub fn ball(&self, target_radius: f64) -> BallEntity {
        let speed = self.spec.max_velocity.max(0.0);
        let at_rest = self.spreads || speed <= 0.0;
        BallEntity {
            item_id: IdRef::Valid(self.id),
            kind: Kind::Missile,
            legacy_mode: LegacyMode::Missile,
            is_massive: Some(true),
            radius: self.spec.radius,
            position: self.launch_position,
            velocity: if at_rest { REST } else { scale(self.direction, speed) },
            direction: self.direction,
            target_point: Some(self.target_at_launch),
            mass: Some(self.spec.mass),
            inertia: self.spec.agility,
            max_velocity: speed,
            speed_fraction: if at_rest { 0.0 } else { 1.0 },
            corporation_id: IdRef::Valid(i64::from(self.shooter.corporation_id)),
            alliance_id: IdRef::Valid(i64::from(self.shooter.alliance_id)),
            follow_target: IdRef::Valid(self.target),
            mode_owner: IdRef::Valid(self.shooter.ship_id),
            // the missile closes until its ball touches the target's: the range it follows at is minus the two radii
            follow_range: -(self.spec.radius.max(0.0) + target_radius.max(0.0)),
            ..BallEntity::default()
        }
    }

    /// The slim item of the missile: the charge it is, whose it is, the ship it was fired from and the launchers it came from.
    pub fn slim_entries(&self) -> Vec<(&'static str, Value)> {
        vec![
            ("itemID", id(self.id)),
            ("typeID", int(i64::from(self.spec.charge_type_id))),
            ("ownerID", id(self.shooter.owner_id)),
            ("name", text(self.spec.name.clone())),
            ("groupID", int(i64::from(self.spec.group_id))),
            ("categoryID", int(i64::from(CHARGE_CATEGORY))),
            ("sourceShipID", id(self.shooter.ship_id)),
            ("launchModules", list([id(self.shooter.module_id)])),
        ]
    }

    /// Whether it is still on its way (it has not landed, and nothing has brought it down).
    pub fn is_flying(&self) -> bool {
        self.phase == Phase::Flying
    }

    /// Moves the missile on to `now`. It homes on `target` (it turns at once and flies at its top speed) and reaches it when the step takes it within the target's radius; the
    /// step is not longer than the flight time it has left. A missile with nothing to home on does not move.
    pub fn advance(&mut self, target: Option<&Kinematics>, now: i64) -> Motion {
        let start = self.stepped_at.max(self.launched_at);
        let step = seconds((now - start).max(0)).min(seconds((self.expires_at - start).max(0)));
        self.stepped_at = now;
        let Some(target) = target else { return self.motion_at(now) };
        let target_position = vec(target.position);
        let previous = self.position;
        let speed = self.spec.max_velocity.max(0.0);
        self.direction = unit(sub(target_position, previous), self.direction);
        self.velocity = if speed > 0.0 { scale(self.direction, speed) } else { REST };
        let target_start = self.target_seen;
        self.position = add(previous, scale(self.velocity, step));
        self.target_seen = target_position;
        let radius = target.radius.max(1.0);
        if let Some(after) =
            swept_impact(previous, self.velocity, target_start, target_position, radius, step)
        {
            self.position = add(previous, scale(self.velocity, after));
            self.velocity = REST;
            return Motion::Reached;
        }
        if (length(sub(target_position, self.position)) - radius).max(0.0) <= 0.001 {
            self.velocity = REST;
            return Motion::Reached;
        }
        self.motion_at(now)
    }

    fn motion_at(&self, now: i64) -> Motion {
        if now >= self.expires_at { Motion::RanOut } else { Motion::Flew }
    }
}

/// What the scene needs to know of the target of a missile to move it on: where it is, and whether it is there to be hit (a ship in warp or under its undocking protection is not).
struct TargetState {
    kinematics: Kinematics,
    hittable: bool,
    session: SessionId,
}

impl Scene {
    fn missile_target(&self, ship_id: i64, now: i64) -> Option<TargetState> {
        let session = self.session_of(ship_id)?;
        let kinematics = self.kinematics_of(session)?;
        let pilot = self.pilots.get(&session)?;
        Some(TargetState {
            kinematics,
            hittable: pilot.protected_until <= now
                && !pilot.flight.as_ref().is_some_and(|flight| flight.is_warping()),
            session,
        })
    }

    /// The pilots that have bound the ballpark and can see this place (a missile is seen where it was fired from).
    fn missile_audience(&self, position: Vec3) -> Vec<SessionId> {
        self.in_entry_order()
            .into_iter()
            .filter(|s| !is_npc_session(*s) && self.pilots[s].bound)
            .filter(|s| {
                let pilot = &self.pilots[s];
                can_see(
                    Presence {
                        position: pilot.ship.position,
                        warping: pilot.flight.as_ref().is_some_and(|flight| flight.is_warping()),
                    },
                    Presence { position, warping: false },
                )
            })
            .collect()
    }

    /// A launcher fires: the missile is made, put in the scene and shown to those who can see it (`AddBalls2` and `LaunchMissile` in one update). The target is aggressed by the
    /// launch. A target the missile cannot get to before its time is up is still shot at (the pilot sees the missile stall and fall away).
    pub(crate) fn launch_missile(&mut self, session: SessionId, effect: &Effect, at: i64) -> Vec<Out> {
        let Some(turret) = effect.turret.as_ref() else { return Vec::new() };
        // a launcher that a guidance disruptor holds fires missiles that are slower, shorter-lived and wider-burst
        let Some(spec) =
            turret.missile.as_ref().map(|spec| spec.guided(self.pilots[&session].incoming.missile_factors()))
        else {
            return Vec::new();
        };
        let Some(target_session) = self.session_of(effect.target_id) else { return Vec::new() };
        let (Some(attacker), Some(target)) =
            (self.kinematics_of(session), self.kinematics_of(target_session))
        else {
            return Vec::new();
        };
        let owner = self.combat_owner(session);
        let (ship_id, corporation_id, alliance_id) = {
            let ship = &self.pilots[&session].ship.spec;
            (ship.ship_id, ship.corporation_id, ship.alliance_id)
        };
        let shooter = Shooter {
            session,
            ship_id,
            module_id: effect.module_id,
            weapon_type_id: turret.weapon_type_id,
            owner_id: owner,
            corporation_id,
            alliance_id,
        };
        let target_type_id = self.pilots.get(&target_session).map_or(0, |pilot| pilot.ship.spec.type_id);
        self.aggressions.push(Aggression {
            attacker_ship: ship_id,
            target_ship: effect.target_id,
            module_type_id: turret.weapon_type_id,
            source_owner_id: owner,
            target_type_id,
        });
        let missile_id = MISSILE_ID_BASE + self.missile_sequence as i64;
        self.missile_sequence += 1;
        let mut missile = MissileEntity::launch(
            missile_id,
            shooter,
            effect.target_id,
            spec,
            turret.raw_damage,
            &attacker,
            &target,
            at,
        );
        let watchers = self.missile_audience(missile.launch_position);
        missile.watchers = watchers.iter().copied().collect();
        let stamp = self.next_stamp(at);
        let mut out = Vec::new();
        if let Ok(actions) = launch_actions(stamp, &missile, target.radius) {
            out.extend(watchers.into_iter().map(|watcher| Self::update(watcher, actions.clone())));
        }
        self.missiles.insert(missile_id, missile);
        out
    }

    /// The missiles that are on their way are moved on to `now`, in the order they were fired: one that reached its target does its damage, one that ran out of flight time stalls and
    /// falls away (unless the client's warhead has landed by then, which does its damage after all, as in the reference), one whose target is gone is gone, and one that spread and has
    /// landed is let go when its time is up.
    pub(crate) fn advance_missiles(&mut self, now: i64) -> Vec<Out> {
        if self.missiles.is_empty() {
            return Vec::new();
        }
        let ids: Vec<i64> = self.missiles.keys().copied().collect();
        let mut out = Vec::new();
        for missile_id in ids {
            let Some(mut missile) = self.missiles.remove(&missile_id) else { continue };
            if self.step_missile(&mut missile, now, &mut out) {
                self.missiles.insert(missile_id, missile);
            }
        }
        out
    }

    /// One missile, moved on. `true` while it stays in the scene.
    fn step_missile(&mut self, missile: &mut MissileEntity, now: i64, out: &mut Vec<Out>) -> bool {
        if let Phase::Spent { release_at } = missile.phase {
            if now < release_at {
                return true;
            }
            out.extend(self.missile_gone(missile, false, now));
            return false;
        }
        // nothing to home on (the target left, warped, was destroyed, or is still under its undocking protection): the missile is gone
        let Some(target) = self.missile_target(missile.target, now).filter(|t| t.hittable) else {
            out.extend(self.missile_gone(missile, false, now));
            return false;
        };
        let landed = match missile.advance(Some(&target.kinematics), now) {
            Motion::Flew => return true,
            Motion::Reached => true,
            // the flight is over: the client's warhead lands at its time all the same, so a missile that is past it does its damage; before it, the missile is lost
            Motion::RanOut => now >= missile.impact_due,
        };
        if !landed {
            out.extend(self.missile_gone(missile, false, now));
            return false;
        }
        out.extend(self.missile_lands(missile, &target, now));
        if missile.spreads {
            missile.phase = Phase::Spent { release_at: missile.expires_at.max(now + RELEASE_GRACE) };
            return true;
        }
        out.extend(self.missile_gone(missile, true, now));
        false
    }

    /// A missile gets to its target: what it does is its damage times what the explosion reaches of a ship of that signature at that speed (`Missile::applied`), and both pilots
    /// are told as for a shot, with the hit quality of a missile (`3`).
    fn missile_lands(&mut self, missile: &MissileEntity, target: &TargetState, at: i64) -> Vec<Out> {
        let kinematics = &target.kinematics;
        let signature = if kinematics.signature_radius > 0.0 {
            kinematics.signature_radius
        } else {
            kinematics.radius.max(1.0)
        };
        let applied = missile.spec.applied(missile.raw_damage, signature, length(vec(kinematics.velocity)));
        let (mut out, hit) = self.damage(missile.target, applied, at);
        let taken = hit.map_or(0.0, |hit| hit.taken.shield + hit.taken.armor + hit.taken.structure);
        let destroyed = hit.is_some_and(|hit| hit.destroyed);
        let tier = if applied.iter().sum::<f64>() > 0.0 { 3 } else { 0 };
        // the pilot may have left, or flown another ship since: then only the target is told
        let attacker = self
            .pilots
            .get(&missile.shooter.session)
            .is_some_and(|pilot| pilot.ship.spec.ship_id == missile.shooter.ship_id)
            .then_some(missile.shooter.session);
        for (who, mine) in [(attacker, true), (Some(target.session), false)] {
            if let Some(who) = who.filter(|who| !is_npc_session(*who)) {
                out.push(Out::Notify {
                    session: who,
                    name: "OnDamageMessage",
                    id_type: "clientID",
                    args: vec![crate::weapons::quality_message(
                        mine,
                        missile.shooter.ship_id,
                        missile.target,
                        missile.shooter.weapon_type_id,
                        taken,
                        applied,
                        tier,
                        missile.shooter.owner_id,
                    )],
                });
            }
        }
        if destroyed {
            out.extend(self.destroy(target.session, attacker, at));
        }
        out
    }

    /// The missile is taken out of the ballpark of whoever was told of it: with the explosion played first when `explode`.
    fn missile_gone(&mut self, missile: &MissileEntity, explode: bool, now: i64) -> Vec<Out> {
        let stamp = self.next_stamp(now);
        let mut actions = Vec::new();
        if explode {
            actions.push(payload::action(
                stamp,
                "TerminalPlayDestructionEffect",
                [id(missile.id), int(crate::wreck::DESTRUCTION_EFFECT_EXPLOSION)],
            ));
        }
        actions.push(payload::remove_ships(stamp, [missile.id]));
        missile
            .watchers
            .iter()
            .filter(|watcher| self.pilots.get(watcher).is_some_and(|pilot| pilot.bound))
            .map(|watcher| Self::update(*watcher, actions.clone()))
            .collect()
    }

    pub fn missiles_within(&self, centre: Vec3, range: f64) -> Vec<i64> {
        let candidates = self.missiles.values().filter(|missile| missile.is_flying()).map(|missile| {
            crate::geo::SpatialEntity {
                id: missile.id,
                kind: crate::geo::SpatialKind::Missile,
                character_id: None,
                owner_id: Some(missile.shooter.owner_id),
                corporation_id: Some(missile.shooter.corporation_id),
                alliance_id: Some(missile.shooter.alliance_id),
                type_id: None,
                group_id: None,
                category_id: None,
                position: [missile.position.x, missile.position.y, missile.position.z],
                radius: 0.0,
                warping: false,
                item: missile.id,
            }
        });
        let mut missiles: Vec<i64> = crate::geo::SpatialQuery::radius(
            [centre.x, centre.y, centre.z],
            range + MISSILE_RANGE_GRACE,
            crate::geo::SpatialFilter {
                kinds: Some(&[crate::geo::SpatialKind::Missile]),
                ..crate::geo::SpatialFilter::default()
            },
        )
        .execute(candidates)
        .into_iter()
        .map(|hit| hit.item)
        .collect();
        missiles.sort_unstable();
        missiles
    }

    /// A missile is shot down: it is gone with the explosion played, and does nothing to its target. What shot it down does not touch the ships: the missile is the only thing it hurt.
    pub fn intercept_missile(&mut self, missile_id: i64, _by: Interceptor, now: i64) -> Vec<Out> {
        let Some(missile) = self.missiles.remove(&missile_id) else { return Vec::new() };
        self.missile_gone(&missile, true, now)
    }

    /// What an interceptor does to a missile: `amount` points off its own hit points, and the missile is shot down when it has none left. A missile that has landed (it is only
    /// waiting to be let go) is not hurt.
    pub fn damage_missile(&mut self, missile_id: i64, amount: f64, by: Interceptor, now: i64) -> Vec<Out> {
        let Some(missile) = self.missiles.get_mut(&missile_id).filter(|missile| missile.is_flying()) else {
            return Vec::new();
        };
        missile.hit_points -= amount.max(0.0);
        if missile.hit_points > 0.0 {
            return Vec::new();
        }
        self.intercept_missile(missile_id, by, now)
    }

    /// The missiles in the scene, in the order they were fired.
    pub fn missiles(&self) -> impl Iterator<Item = &MissileEntity> {
        self.missiles.values()
    }

    /// The distance from the surface of a ship to the surface of a missile: what a point defense battery measures its range by.
    pub fn distance_to_missile(&self, ship_id: i64, missile_id: i64) -> Option<f64> {
        let missile = self.missiles.get(&missile_id)?;
        let session = self.session_of(ship_id)?;
        let ship = &self.pilots.get(&session)?.ship;
        let p = ship.position;
        let centre = length(sub(Vec3::new(p.x, p.y, p.z), missile.position));
        Some((centre - ship.spec.radius.max(0.0) - missile.spec.radius.max(0.0)).max(0.0))
    }
}

/// What the client is told when a missile is fired: its ball (`AddBalls2`, with the slim item as a plain dictionary and no damage state) and the launch, at one stamp.
fn launch_actions(
    stamp: u32,
    missile: &MissileEntity,
    target_radius: f64,
) -> Result<Vec<Value>, eve_destiny::BallError> {
    let buffer = payload::add_balls_buffer(stamp, &[missile.ball(target_radius)])?;
    let slim = Value::Dict(missile.slim_entries().into_iter().map(|(k, v)| (word(k), v)).collect());
    let added = payload::action(stamp, "AddBalls2", [tuple([buffer, list([slim])])]);
    // the last two are the massive state the client reads (a live missile is free and massive)
    let launched = payload::action(
        stamp,
        "LaunchMissile",
        [id(missile.id), id(missile.target), id(missile.shooter.ship_id), int(1), int(1)],
    );
    Ok(vec![added, launched])
}

#[cfg(test)]
mod tests {
    use super::*;

    const SECOND: i64 = TICKS_PER_SECOND;

    fn kin(position: [f64; 3], velocity: [f64; 3], radius: f64) -> Kinematics {
        Kinematics { position, velocity, radius, signature_radius: radius }
    }

    /// The scourge rocket of the reference's recording (`oracle/fixtures/scenarios/missile.json`): 2250 m/s, two seconds of flight, the ball of 300 m radius and 100 kg.
    fn scourge() -> Missile {
        Missile {
            charge_type_id: 266,
            group_id: 387,
            name: "Scourge Rocket".to_string(),
            radius: 300.0,
            mass: 100.0,
            agility: 5.220_125_693_558_398,
            hit_points: 90.0,
            max_velocity: 2_250.0,
            flight_time_ms: 2_000.0,
            explosion_radius: 20.0,
            explosion_velocity: 150.0,
            damage_reduction_factor: 0.644,
            damage_reduction_sensitivity: 5.5,
        }
    }

    fn shooter() -> Shooter {
        Shooter {
            session: 7,
            ship_id: 9_988_400_003_581,
            module_id: 9_988_400_003_586,
            weapon_type_id: 10_629,
            owner_id: 140_000_465,
            corporation_id: 1_000_006,
            alliance_id: 0,
        }
    }

    fn fired(attacker: &Kinematics, target: &Kinematics, spec: Missile) -> MissileEntity {
        MissileEntity::launch(
            MISSILE_ID_BASE,
            shooter(),
            980_050_000_111,
            spec,
            [0.0, 0.0, 38.115, 0.0],
            attacker,
            target,
            0,
        )
    }

    fn at_rest(x: f64) -> Kinematics {
        kin([x, 0.0, 0.0], [0.0; 3], 50.0)
    }

    /// The `AddBalls2` of the first missile the reference launched in the recording, byte for byte, the ball of the entity this builds.
    #[test]
    fn the_ball_of_a_missile_is_the_one_the_reference_launched() {
        let recorded = "012e8abf6a00800eb4e0eb0e000500009643e77da3cf02295242d4a8b21cdb5708c29bf160716c3c424205000000000000594000ffffffffffffffff46420f000000000000a00c45000000000000000000000000000000000000000000000000450ba74000000000ffef38882fe40000000000afc3fdb9089b150900000000000056b266d302295242204b5ad3da5708c2e4cde0716c3c4242";
        let launch = Vec3::new(311_989_845_645.967_2, -13_069_149_078.332_436, 156_646_302_401.887_54);
        let target = Vec3::new(311_989_849_498.786_5, -13_069_146_731.286_682, 156_646_302_657.608_52);
        let missile = MissileEntity {
            id: MISSILE_ID_BASE,
            shooter: shooter(),
            target: 980_050_000_111,
            spec: scourge(),
            raw_damage: [0.0, 0.0, 38.115, 0.0],
            launch_position: launch,
            position: launch,
            velocity: REST,
            direction: unit(sub(target, launch), Vec3::new(1.0, 0.0, 0.0)),
            launched_at: 0,
            stepped_at: 0,
            expires_at: 0,
            impact_due: 0,
            spreads: true,
            phase: Phase::Flying,
            hit_points: 90.0,
            target_seen: target,
            target_at_launch: target,
            watchers: BTreeSet::new(),
        };
        // the target is a rat of 50 m: the missile follows it at minus the two radii, 300 + 50
        let buffer = payload::add_balls_buffer(1_790_937_646, &[missile.ball(50.0)]).unwrap();
        let Value::Buffer(bytes) = buffer else { panic!("{buffer:?}") };
        let hex: String = bytes.iter().map(|byte| format!("{byte:02x}")).collect();
        assert_eq!(hex, recorded);
    }

    #[test]
    fn the_slim_item_and_the_launch_are_what_the_client_was_sent() {
        let (attacker, target) = (at_rest(0.0), at_rest(4_500.0));
        let missile = fired(&attacker, &target, scourge());
        let keys: Vec<&str> = missile.slim_entries().iter().map(|(k, _)| *k).collect();
        assert_eq!(
            keys,
            ["itemID", "typeID", "ownerID", "name", "groupID", "categoryID", "sourceShipID", "launchModules"]
        );
        let entries = missile.slim_entries();
        assert_eq!(entries[1].1, int(266));
        assert_eq!(entries[3].1, text("Scourge Rocket"));
        assert_eq!((entries[4].1.clone(), entries[5].1.clone()), (int(387), int(8)));
        assert_eq!(entries[7].1, list([id(9_988_400_003_586)]));
        let actions = launch_actions(1_790_937_646, &missile, 50.0).unwrap();
        assert_eq!(actions.len(), 2, "the ball and the launch are told together");
        let launched = payload::action(
            1_790_937_646,
            "LaunchMissile",
            [id(MISSILE_ID_BASE), id(980_050_000_111), id(9_988_400_003_581), int(1), int(1)],
        );
        assert_eq!(actions[1], launched);
    }

    #[test]
    fn a_missile_is_put_at_a_muzzle_in_front_of_the_ship_towards_the_target() {
        // a ship of 47 m is given a muzzle of 21 m (its radius times 0.45, between 12 and 90)
        let near = muzzle(&kin([0.0; 3], [0.0; 3], 47.0), Vec3::new(1_000.0, 0.0, 0.0));
        assert!((near.x - 21.15).abs() < 1e-9 && near.y == 0.0);
        assert_eq!(muzzle(&kin([0.0; 3], [0.0; 3], 5.0), Vec3::new(0.0, 1_000.0, 0.0)).y, 12.0);
        assert_eq!(muzzle(&kin([0.0; 3], [0.0; 3], 500.0), Vec3::new(0.0, 0.0, 1_000.0)).z, 90.0);
    }

    #[test]
    fn a_missile_that_takes_1_6_seconds_or_more_to_get_there_spreads_and_a_nearer_target_does_not() {
        // 2250 m/s: 3.6 km of surface is 1.6 s
        assert!(fired(&at_rest(0.0), &at_rest(3_700.0 + 50.0 + 21.0), scourge()).spreads);
        assert!(!fired(&at_rest(0.0), &at_rest(3_000.0), scourge()).spreads);
        // what spreads starts at rest and what does not starts at its top speed
        let (far, near) = (
            fired(&at_rest(0.0), &at_rest(4_500.0), scourge()),
            fired(&at_rest(0.0), &at_rest(1_000.0), scourge()),
        );
        assert_eq!((far.ball(50.0).speed_fraction, far.ball(50.0).velocity), (0.0, REST));
        assert_eq!(near.ball(50.0).speed_fraction, 1.0);
        assert!((length(near.ball(50.0).velocity) - 2_250.0).abs() < 1e-6);
    }

    #[test]
    fn a_missile_homes_on_its_target_and_reaches_it_when_it_gets_within_its_radius() {
        let mut missile = fired(&at_rest(0.0), &at_rest(2_250.0 + 21.15), scourge());
        let target = at_rest(2_250.0 + 21.15);
        // 2250 m of the target's centre less its radius of 50 is 2200 m from the muzzle of 21: a second less a hair
        let mut now = 0;
        let mut reached = None;
        while now < 2 * SECOND && reached.is_none() {
            now += SECOND / 10;
            if missile.advance(Some(&target), now) == Motion::Reached {
                reached = Some(now);
            }
        }
        let reached = reached.expect("it gets there");
        assert_eq!(
            reached, SECOND,
            "after the tenth step, which is the one that crosses the radius of the target"
        );
        // it stops where it touches the target's radius, not at its centre
        let distance = length(sub(missile.position, vec(target.position)));
        assert!((distance - 50.0).abs() < 1.0, "{distance}");
    }

    #[test]
    fn a_missile_does_not_fly_through_a_target_in_one_long_step() {
        let mut missile = fired(&at_rest(0.0), &at_rest(1_000.0), scourge());
        // a step of ten seconds takes it twenty kilometres further than the target is: the swept test still finds it
        assert_eq!(missile.advance(Some(&at_rest(1_000.0)), 10 * SECOND), Motion::Reached);
        assert!(missile.position.x < 1_000.0, "it stopped at the surface: {:?}", missile.position);
    }

    #[test]
    fn a_missile_turns_at_once_to_follow_a_target_that_moves() {
        let mut missile = fired(&at_rest(0.0), &at_rest(3_000.0), scourge());
        // the target runs off at a right angle: the missile's heading follows it, and the closing speed is the missile's own
        let mut now = 0;
        let mut y = 0.0;
        for _ in 0..3 {
            now += SECOND / 10;
            y += 100.0;
            let target = kin([3_000.0, y, 0.0], [0.0, 1_000.0, 0.0], 50.0);
            missile.advance(Some(&target), now);
            let toward = unit(sub(vec(target.position), missile.position), REST);
            assert!(dot(missile.direction, toward) > 0.999, "it is aimed at where the target is");
        }
        assert!(missile.position.y > 0.0, "it went after the target");
        assert!((length(missile.velocity) - 2_250.0).abs() < 1e-6);
    }

    #[test]
    fn a_missile_that_cannot_catch_its_target_stalls_when_its_flight_time_is_up_short_of_it() {
        // the target is out of reach, and runs: 2 s of 2250 m/s is 4.5 km, the target is 20 km off
        let mut missile = fired(&at_rest(0.0), &at_rest(20_000.0), scourge());
        let target = at_rest(20_000.0);
        let mut now = 0;
        let outcome = loop {
            now += SECOND / 10;
            match missile.advance(Some(&target), now) {
                Motion::Flew => continue,
                other => break other,
            }
        };
        assert_eq!(outcome, Motion::RanOut);
        // the flight is two seconds and the time to clear the radius of the ship that fired it (a ship of 50 m: 50 / 2250 s)
        assert!(
            now >= missile.expires_at && now - missile.expires_at <= SECOND / 10,
            "{now} {}",
            missile.expires_at
        );
        assert!(
            missile.position.x < 4_800.0 && missile.position.x > 4_000.0,
            "it did not go beyond what it flies: {:?}",
            missile.position
        );
    }

    #[test]
    fn a_step_is_never_longer_than_the_flight_time_the_missile_has_left() {
        let mut missile = fired(&at_rest(0.0), &at_rest(20_000.0), scourge());
        let target = at_rest(20_000.0);
        // one poll a long time after it was fired still only flies what it had: 2 s and a hair
        missile.advance(Some(&target), 60 * SECOND);
        assert!(missile.position.x < 4_700.0, "{:?}", missile.position);
    }

    #[test]
    fn the_client_s_warhead_is_due_at_the_average_of_the_surface_and_the_centre_time() {
        let missile = fired(&at_rest(0.0), &at_rest(4_500.0), scourge());
        let muzzle = 22.5; // a ship of 50 m: 0.45 of its radius
        let (surface, centre) = ((4_500.0 - muzzle - 50.0) / 2_250.0, (4_500.0 - muzzle) / 2_250.0);
        let due = (surface + centre) / 2.0;
        assert!((seconds(missile.impact_due) - due).abs() < 1e-6, "{} {due}", seconds(missile.impact_due));
    }

    #[test]
    fn a_missile_has_the_hit_points_of_its_charge_apart_from_any_ship() {
        let missile = fired(&at_rest(0.0), &at_rest(1_000.0), scourge());
        assert_eq!(missile.hit_points, 90.0);
        assert!(missile.is_flying());
    }
}
