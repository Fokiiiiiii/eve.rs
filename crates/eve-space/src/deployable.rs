//! Things a module puts in space for a while, kept apart from the ships like a missile (`missile_entity`): an interdiction probe, which is a warp-disruption source
//! for its lifetime, and a bomb, which is released at its ship and detonates where it is when its lifetime is up. The client is told each as a ball
//! (`AddBalls2` when it is released, `RemoveBalls` when it is gone, the bomb's explosion played first). Both lifetimes are the simulation's own clock (the SDE's
//! explosion delay): nothing is persisted, so a restart drops them (ADR 0012 addendum, a stated limitation).
//!
//! A bomb's blast is one `Area` (ADR 0011, its addendum of 2026-10-09): the centre is where the bomb is, the radius is the charge's blast radius, and
//! the recipients are the ships of the scene within that surface distance, but the ship that launched it (as a smartbomb spares its own).

use std::collections::BTreeSet;

use eve_destiny::ball::LegacyMode;
use eve_destiny::{BallEntity, IdRef, Kind, Vec3};
use eve_marshal::Value;

use crate::health::Damage;
use crate::missile_entity::CHARGE_CATEGORY;
use crate::modules::Effect;
use crate::payload;
use crate::scene::{Out, Scene, SessionId, TICKS_PER_SECOND};
use crate::wire::{id, int, list, text, tuple, word};

/// The id of the first deployable a scene makes (its own range, apart from the missiles and the wrecks).
pub const DEPLOYABLE_ID_BASE: i64 = 4_250_000_000_000_000;

/// The charge a deployable is: its type, its group, what the client calls it and the radius of its ball.
#[derive(Debug, Clone, PartialEq)]
pub struct Spec {
    pub charge_type_id: i32,
    pub group_id: i32,
    pub name: String,
    pub radius: f64,
}

/// What a bomb does where it goes off: the damage of a damage bomb, the ECM strength of a jamming bomb, or the capacitor it neutralises.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum BombKind {
    Damage,
    Ecm,
    Neutralize,
}

/// What a deployable does when its lifetime is up.
#[derive(Debug, Clone, PartialEq)]
pub enum Role {
    /// A warp-disruption source of `range` metres around it, for as long as it lives (an interdiction probe).
    WarpSource { range: f64 },
    /// A bomb: at its end the ships within `blast_radius` take its kind of effect (`ecm` for the jam, `neutralize` for the capacitor, `damage` for the hit).
    Bomb { kind: BombKind, blast_radius: f64, damage: Damage, ecm: [f64; 4], neutralize: f64 },
    /// A cynosural field (the field types of the generators, group 2020 or 897): it lives until its lifetime is up, and does nothing else. Lit where its ship is.
    CynoField,
    /// A scanner probe the host launched (protocol 45): a ball in space that does nothing; the host recalls it or its time is up.
    ScanProbe,
    /// A lance's beacon (EVE.js `spawnLanceBeacon`, protocol 39): a transient container the beam is drawn to. It does nothing but exist; it is taken out when
    /// its strike's effect ends (or stops), or when its lifetime is up.
    Beacon,
}

/// The beacon of a lance (EVE.js `MODULAR_EFFECT_BEACON_TYPE_ID` 41233, group 1704, radius 250): a transient "Modular Effect Beacon" container.
pub const LANCE_BEACON: (i32, i32) = (41_233, 1_704);
pub const LANCE_BEACON_RADIUS: f64 = 250.0;
/// The category of the beacon's container (EVE.js `MODULAR_EFFECT_BEACON_CATEGORY_ID`).
const BEACON_CATEGORY: i32 = 2;
/// The category of a cynosural field (EVE.js `cynosuralField.js`, the category it falls back to: 2).
const CYNO_FIELD_CATEGORY: i32 = 2;

/// A deployable in space: where it is, when it ends, and who put it there (its launcher, for a bomb's blast and the aggression it makes).
#[derive(Debug, Clone, PartialEq)]
pub struct Deployable {
    pub id: i64,
    pub spec: Spec,
    pub role: Role,
    pub position: Vec3,
    /// When it ends, in the scene's ticks.
    pub ends_at: i64,
    pub launcher: SessionId,
    pub ship_id: i64,
    pub owner_id: i64,
    pub corporation_id: i32,
    pub alliance_id: i32,
    pub module_id: i64,
    pub module_type: i32,
    /// The pilots that were told of it (they are told it is gone).
    pub watchers: BTreeSet<SessionId>,
}

fn ticks(milliseconds: f64) -> i64 {
    (milliseconds * (TICKS_PER_SECOND as f64) / 1000.0).round() as i64
}

fn distance(a: Vec3, b: Vec3) -> f64 {
    ((a.x - b.x).powi(2) + (a.y - b.y).powi(2) + (a.z - b.z).powi(2)).sqrt()
}

/// What the client is told when a deployable is released: its ball (`AddBalls2` with its slim item) at one stamp.
fn release_actions(stamp: u32, deployable: &Deployable) -> Result<Vec<Value>, eve_destiny::BallError> {
    let (kind, mode) = match deployable.role {
        Role::WarpSource { .. } => (Kind::WarpDisruptionProbe, LegacyMode::Stop),
        Role::Bomb { .. } => (Kind::Missile, LegacyMode::Stop),
        Role::Beacon => (Kind::Container, LegacyMode::Stop),
        Role::CynoField => (Kind::Other, LegacyMode::Stop),
        Role::ScanProbe => (Kind::Other, LegacyMode::Stop),
    };
    let category = match deployable.role {
        Role::Beacon => BEACON_CATEGORY,
        Role::CynoField => CYNO_FIELD_CATEGORY,
        _ => CHARGE_CATEGORY,
    };
    let ball = BallEntity {
        item_id: IdRef::Valid(deployable.id),
        kind,
        legacy_mode: mode,
        is_massive: Some(true),
        radius: deployable.spec.radius,
        position: deployable.position,
        target_point: Some(deployable.position),
        mass: Some(1.0),
        corporation_id: IdRef::Valid(i64::from(deployable.corporation_id)),
        alliance_id: IdRef::Valid(i64::from(deployable.alliance_id)),
        mode_owner: IdRef::Valid(deployable.ship_id),
        ..BallEntity::default()
    };
    let buffer = payload::add_balls_buffer(stamp, &[ball])?;
    let slim = Value::Dict(
        [
            ("itemID", id(deployable.id)),
            ("typeID", int(i64::from(deployable.spec.charge_type_id))),
            ("ownerID", id(deployable.owner_id)),
            ("name", text(deployable.spec.name.clone())),
            ("groupID", int(i64::from(deployable.spec.group_id))),
            ("categoryID", int(i64::from(category))),
            ("sourceShipID", id(deployable.ship_id)),
            ("launchModules", list([id(deployable.module_id)])),
        ]
        .into_iter()
        .map(|(k, v)| (word(k), v))
        .collect(),
    );
    Ok(vec![payload::action(stamp, "AddBalls2", [tuple([buffer, list([slim])])])])
}

impl Scene {
    /// A launcher's cycle starts and releases a deployable at its ship: it is made, put in the scene and shown to those who can see the place. A probe starts
    /// disrupting warps where it is; a bomb is due to go off `lifetime_ms` later.
    pub(crate) fn release_deployable(
        &mut self,
        session: SessionId,
        effect: &Effect,
        spec: Spec,
        role: Role,
        lifetime_ms: f64,
        at: i64,
    ) -> Vec<Out> {
        let is_field = role == Role::CynoField;
        let (id, mut out) = self.place_deployable(session, effect, spec, role, None, lifetime_ms, at);
        if is_field {
            out.extend(self.field_lit(id, session, at));
        }
        out
    }

    /// A lance's beacon is put in space at `offset` from the ship that runs its strike (the strike's `beacon`), for the life of the effect (`lifetime_ms`).
    /// Returns the beacon's id, which the beam's effect is played on, and what the pilots are told.
    pub(crate) fn release_beacon(
        &mut self,
        session: SessionId,
        effect: &Effect,
        offset: [f64; 3],
        lifetime_ms: f64,
        at: i64,
    ) -> (i64, Vec<Out>) {
        let spec = Spec {
            charge_type_id: LANCE_BEACON.0,
            group_id: LANCE_BEACON.1,
            name: "Modular Effect Beacon".to_string(),
            radius: LANCE_BEACON_RADIUS,
        };
        let Some(pilot) = self.pilots.get(&session) else { return (0, Vec::new()) };
        let p = pilot.ship.position;
        let position = eve_destiny::Vec3::new(p.x + offset[0], p.y + offset[1], p.z + offset[2]);
        self.place_deployable(session, effect, spec, Role::Beacon, Some(position), lifetime_ms, at)
    }

    /// The beacon of a module's strike that is in space, or 0 (none, or already taken out).
    pub(crate) fn beacon_of(&self, module_id: i64) -> i64 {
        self.deployables
            .values()
            .find(|deployable| deployable.role == Role::Beacon && deployable.module_id == module_id)
            .map_or(0, |deployable| deployable.id)
    }

    /// A module's beacon is taken out now (its effect has ended): the pilots who were told of it are told it is gone.
    pub(crate) fn take_down_beacon(&mut self, module_id: i64, at: i64) -> Vec<Out> {
        let due: Vec<i64> = self
            .deployables
            .values()
            .filter(|deployable| deployable.role == Role::Beacon && deployable.module_id == module_id)
            .map(|deployable| deployable.id)
            .collect();
        let mut out = Vec::new();
        for deployable_id in due {
            let Some(deployable) = self.deployables.remove(&deployable_id) else { continue };
            let stamp = self.next_stamp(at);
            let actions = vec![payload::remove_ships(stamp, [deployable.id])];
            out.extend(
                deployable
                    .watchers
                    .iter()
                    .filter(|watcher| self.pilots.get(watcher).is_some_and(|pilot| pilot.bound))
                    .map(|watcher| Self::update(*watcher, actions.clone())),
            );
        }
        out
    }

    /// Puts a deployable in space at `position` (its launcher's ship when none is given), and tells those who can see the place of it.
    #[allow(clippy::too_many_arguments)]
    fn place_deployable(
        &mut self,
        session: SessionId,
        effect: &Effect,
        spec: Spec,
        role: Role,
        position: Option<eve_destiny::Vec3>,
        lifetime_ms: f64,
        at: i64,
    ) -> (i64, Vec<Out>) {
        let Some(pilot) = self.pilots.get(&session) else { return (0, Vec::new()) };
        let (position, ship_id) = (position.unwrap_or(pilot.ship.position), pilot.ship.spec.ship_id);
        let (corporation_id, alliance_id) = (pilot.ship.spec.corporation_id, pilot.ship.spec.alliance_id);
        let owner_id = self.combat_owner(session);
        let deployable_id = DEPLOYABLE_ID_BASE + self.deployable_sequence as i64;
        self.deployable_sequence += 1;
        let deployable = Deployable {
            id: deployable_id,
            spec,
            role,
            position,
            ends_at: at + ticks(lifetime_ms),
            launcher: session,
            ship_id,
            owner_id,
            corporation_id,
            alliance_id,
            module_id: effect.module_id,
            module_type: effect.module_type,
            watchers: BTreeSet::new(),
        };
        (deployable_id, self.insert_deployable(deployable, at))
    }

    /// Puts a made deployable into the scene: it is kept, and those who can see its place are told of it.
    fn insert_deployable(&mut self, mut deployable: Deployable, at: i64) -> Vec<Out> {
        let watchers = self.missile_audience(deployable.position);
        let stamp = self.next_stamp(at);
        let mut out = Vec::new();
        if let Ok(actions) = release_actions(stamp, &deployable) {
            out.extend(watchers.iter().map(|watcher| Self::update(*watcher, actions.clone())));
        }
        deployable.watchers = watchers.into_iter().collect();
        self.deployables.insert(deployable.id, deployable);
        out
    }

    /// The record of a field that was lit by `session`'s ship at `at`, for the host (`DeployedLit`): its life is what is left of it at `at`, the whole of it.
    fn field_lit(&self, deployable_id: i64, session: SessionId, at: i64) -> Vec<Out> {
        let Some(d) = self.deployables.get(&deployable_id) else { return Vec::new() };
        let lifetime_ms = (d.ends_at - at) as f64 / (TICKS_PER_SECOND as f64 / 1000.0);
        let placement = crate::link::DeployedPlacement {
            solar_system_id: self.solar_system_id(),
            deployable_id: d.id,
            name: d.spec.name.clone(),
            type_id: d.spec.charge_type_id,
            group_id: d.spec.group_id,
            position: [d.position.x, d.position.y, d.position.z],
            radius: d.spec.radius,
            owner_id: d.owner_id,
            corporation_id: d.corporation_id,
            alliance_id: d.alliance_id,
            ship_id: d.ship_id,
            lifetime_ms,
        };
        vec![Out::DeployedLit { session, placement: Box::new(placement) }]
    }

    /// A cynosural field the host keeps a record of is put back in space after a restart: with the id it had, and what is left of its life. It is
    /// given once (a field already in the scene is left as it is) and one with no life left is not put back.
    /// A scanner probe the host launched is put in space where it is (protocol 45). It stays until the host takes it out: its end is the host's, not the
    /// simulation's, so its end time is never reached. Given once: a probe already in space is left as it is.
    pub(crate) fn place_probe(&mut self, placement: crate::link::ProbePlacement, now: i64) -> Vec<Out> {
        if self.deployables.contains_key(&placement.probe_id) {
            return Vec::new();
        }
        let [x, y, z] = placement.position;
        let deployable = Deployable {
            id: placement.probe_id,
            spec: Spec {
                charge_type_id: placement.type_id,
                group_id: placement.group_id,
                name: placement.name,
                radius: placement.radius,
            },
            role: Role::ScanProbe,
            position: Vec3::new(x, y, z),
            ends_at: i64::MAX,
            launcher: 0,
            ship_id: placement.ship_id,
            owner_id: placement.owner_id,
            corporation_id: placement.corporation_id,
            alliance_id: placement.alliance_id,
            module_id: 0,
            module_type: 0,
            watchers: BTreeSet::new(),
        };
        self.insert_deployable(deployable, now)
    }

    /// The host takes a scanner probe out of space: it is removed and those who see it are told it is gone. Only a probe is taken this way.
    pub(crate) fn remove_probe(&mut self, probe_id: i64, at: i64) -> Vec<Out> {
        if !matches!(self.deployables.get(&probe_id), Some(d) if d.role == Role::ScanProbe) {
            return Vec::new();
        }
        let Some(deployable) = self.deployables.remove(&probe_id) else { return Vec::new() };
        let stamp = self.next_stamp(at);
        let actions = vec![payload::remove_ships(stamp, [deployable.id])];
        deployable
            .watchers
            .iter()
            .filter(|watcher| self.pilots.get(watcher).is_some_and(|pilot| pilot.bound))
            .map(|watcher| Self::update(*watcher, actions.clone()))
            .collect()
    }

    pub(crate) fn restore_field(&mut self, placement: crate::link::DeployedPlacement, now: i64) -> Vec<Out> {
        let lifetime_ms = placement.lifetime_ms;
        if !lifetime_ms.is_finite()
            || lifetime_ms <= 0.0
            || self.deployables.contains_key(&placement.deployable_id)
        {
            return Vec::new();
        }
        if placement.deployable_id >= DEPLOYABLE_ID_BASE {
            let next = (placement.deployable_id - DEPLOYABLE_ID_BASE) as u64 + 1;
            self.deployable_sequence = self.deployable_sequence.max(next);
        }
        let [x, y, z] = placement.position;
        let deployable = Deployable {
            id: placement.deployable_id,
            spec: Spec {
                charge_type_id: placement.type_id,
                group_id: placement.group_id,
                name: placement.name,
                radius: placement.radius,
            },
            role: Role::CynoField,
            position: Vec3::new(x, y, z),
            ends_at: now + ticks(lifetime_ms),
            launcher: 0,
            ship_id: placement.ship_id,
            owner_id: placement.owner_id,
            corporation_id: placement.corporation_id,
            alliance_id: placement.alliance_id,
            module_id: 0,
            module_type: 0,
            watchers: BTreeSet::new(),
        };
        self.insert_deployable(deployable, now)
    }

    /// The host takes a cynosural field out of space (its record's time is up): it is removed and those who see it are told it is gone. Only a field
    /// is taken this way; anything else, or a field not in space, is left alone.
    pub(crate) fn remove_field(&mut self, deployable_id: i64, at: i64) -> Vec<Out> {
        if !matches!(self.deployables.get(&deployable_id), Some(d) if d.role == Role::CynoField) {
            return Vec::new();
        }
        let Some(deployable) = self.deployables.remove(&deployable_id) else { return Vec::new() };
        let stamp = self.next_stamp(at);
        let actions = vec![payload::remove_ships(stamp, [deployable.id])];
        deployable
            .watchers
            .iter()
            .filter(|watcher| self.pilots.get(watcher).is_some_and(|pilot| pilot.bound))
            .map(|watcher| Self::update(*watcher, actions.clone()))
            .collect()
    }

    /// The deployables whose lifetime is up at `now` end, in the order they were released: a probe is taken out of the scene, a bomb goes off where it is
    /// first and is taken out with the explosion played.
    pub(crate) fn advance_deployables(&mut self, now: i64) -> Vec<Out> {
        let due: Vec<i64> = self
            .deployables
            .iter()
            .filter(|(_, deployable)| deployable.ends_at <= now)
            .map(|(id, _)| *id)
            .collect();
        let mut out = Vec::new();
        for deployable_id in due {
            let Some(deployable) = self.deployables.remove(&deployable_id) else { continue };
            let explode = matches!(deployable.role, Role::Bomb { .. });
            if explode {
                out.extend(self.detonate(&deployable, now));
            }
            let stamp = self.next_stamp(now);
            let mut actions = Vec::new();
            if explode {
                actions.push(payload::action(
                    stamp,
                    "TerminalPlayDestructionEffect",
                    [id(deployable.id), int(crate::wreck::DESTRUCTION_EFFECT_EXPLOSION)],
                ));
            }
            actions.push(payload::remove_ships(stamp, [deployable.id]));
            out.extend(
                deployable
                    .watchers
                    .iter()
                    .filter(|watcher| self.pilots.get(watcher).is_some_and(|pilot| pilot.bound))
                    .map(|watcher| Self::update(*watcher, actions.clone())),
            );
        }
        out
    }

    /// The ships of the scene within `radius` of `centre` (their surface), but `exclude_ship`, which do not warp: each with its pilot's session, by ship id.
    pub(crate) fn ships_in_area(
        &self,
        centre: [f64; 3],
        radius: f64,
        exclude_ship: Option<i64>,
    ) -> Vec<(SessionId, i64)> {
        let candidates = self.pilots.iter().map(|(other, pilot)| self.spatial_ship(*other, pilot));
        let mut inside: Vec<(SessionId, i64)> = crate::geo::SpatialQuery::surface_radius(
            centre,
            0.0,
            radius,
            1e-6,
            crate::geo::SpatialFilter {
                exclude_id: exclude_ship,
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

    /// A bomb goes off: its blast reaches the ships of its area (a ship in its undock protection is spared the damage, as a smartbomb spares it), and what it
    /// does is what its kind says. A launcher that has left the scene makes no aggression: its bomb still goes off where it is.
    fn detonate(&mut self, deployable: &Deployable, at: i64) -> Vec<Out> {
        let Role::Bomb { kind, blast_radius, damage, ecm, neutralize } = &deployable.role else {
            return Vec::new();
        };
        let centre = [deployable.position.x, deployable.position.y, deployable.position.z];
        match kind {
            BombKind::Damage => {
                let mut out = Vec::new();
                for target in self.ships_in_area(centre, *blast_radius, Some(deployable.ship_id)) {
                    if self.pilots.get(&target.0).is_none_or(|pilot| pilot.protected_until > at) {
                        continue;
                    }
                    out.extend(self.damage_ship(
                        deployable.launcher,
                        deployable.module_type,
                        target,
                        *damage,
                        at,
                    ));
                }
                out
            }
            BombKind::Ecm => self.jam_at(centre, 0.0, Some(deployable.ship_id), ecm, *blast_radius, at),
            BombKind::Neutralize => {
                let mut out = Vec::new();
                for (target, _) in self.ships_in_area(centre, *blast_radius, Some(deployable.ship_id)) {
                    let amount = neutralize * self.capacitor_warfare_multiplier(target);
                    out.extend(self.take_capacitor(target, amount, at).0);
                }
                out
            }
        }
    }

    /// A stasis burst goes off as a cycle starts: every ship within `range` of the ship that runs it (its own ship spared) is webbed by `speed_factor` for
    /// `duration_ms`, through the web of the module (`Scene::hold_for`), and aggressed as a web is. Nothing checks who the ships belong to: the reference's burst
    /// has no such check that is written down (DEVIATIONS).
    pub(crate) fn stasis_burst(
        &mut self,
        session: SessionId,
        effect: &Effect,
        range: f64,
        duration_ms: f64,
        speed_factor: f64,
        at: i64,
    ) -> Vec<Out> {
        let Some(pilot) = self.pilots.get(&session) else { return Vec::new() };
        let (own, owner, p) = (pilot.ship.spec.ship_id, self.combat_owner(session), pilot.ship.position);
        let mut out = Vec::new();
        for target in self.ships_in_area([p.x, p.y, p.z], range, Some(own)) {
            let target_type_id = self.pilots.get(&target.0).map_or(0, |pilot| pilot.ship.spec.type_id);
            self.aggressions.push(crate::weapons::Aggression {
                attacker_ship: own,
                target_ship: target.1,
                module_type_id: effect.module_type,
                source_owner_id: owner,
                target_type_id,
            });
            out.extend(self.hold_for(
                session,
                target.0,
                effect.module_id,
                crate::ewar::Lasting::Web { percent: speed_factor },
                duration_ms,
                at,
            ));
        }
        out
    }

    /// A fighter's LaunchBomb blast goes off at `centre` when its window ends (ADR 0011's area, decided for the fighter by EVE.js `executeFighterBombCycle`):
    /// every ship within `radius` of it (its surface) takes `damage` from `launcher`. No ship is spared, not even the launcher's own, and the undock
    /// protection is not read, as the reference does not read it (DEVIATIONS, Fighter LaunchBomb).
    pub(crate) fn blast_at(
        &mut self,
        launcher: SessionId,
        module_type: i32,
        centre: [f64; 3],
        radius: f64,
        damage: crate::health::Damage,
        at: i64,
    ) -> Vec<Out> {
        let mut out = Vec::new();
        for target in self.ships_in_area(centre, radius, None) {
            out.extend(self.damage_ship(launcher, module_type, target, damage, at));
        }
        out
    }

    /// Whether a warp-disruption source covers `position` at `now`: a probe still in space within its range, or a bubble (a ship's running field generator, see
    /// `Action::WarpBubble`) within its range of the ship's position. No owner is spared (the reference's check has none).
    pub(crate) fn warp_disrupted_at(&self, position: Vec3, now: i64) -> bool {
        let bubble = self.pilots.values().any(|pilot| {
            let reach = distance(position, pilot.ship.position);
            pilot.modules.running.iter().filter_map(|run| run.effect.action.as_ref()).any(
                |action| matches!(action, crate::modules::Action::WarpBubble { range } if reach <= *range),
            )
        });
        bubble
            || self.deployables.values().any(|deployable| match deployable.role {
                Role::WarpSource { range } => {
                    deployable.ends_at > now && distance(position, deployable.position) <= range
                }
                Role::Bomb { .. } | Role::Beacon | Role::CynoField | Role::ScanProbe => false,
            })
    }
}

#[cfg(test)]
mod tests;
