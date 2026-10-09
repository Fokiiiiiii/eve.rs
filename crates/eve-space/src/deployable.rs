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
}

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
            ("categoryID", int(i64::from(CHARGE_CATEGORY))),
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
        let Some(pilot) = self.pilots.get(&session) else { return Vec::new() };
        let (position, ship_id) = (pilot.ship.position, pilot.ship.spec.ship_id);
        let (corporation_id, alliance_id) = (pilot.ship.spec.corporation_id, pilot.ship.spec.alliance_id);
        let owner_id = self.combat_owner(session);
        let deployable_id = DEPLOYABLE_ID_BASE + self.deployable_sequence as i64;
        self.deployable_sequence += 1;
        let mut deployable = Deployable {
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
        let watchers = self.missile_audience(position);
        let stamp = self.next_stamp(at);
        let mut out = Vec::new();
        if let Ok(actions) = release_actions(stamp, &deployable) {
            out.extend(watchers.iter().map(|watcher| Self::update(*watcher, actions.clone())));
        }
        deployable.watchers = watchers.into_iter().collect();
        self.deployables.insert(deployable_id, deployable);
        out
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
        exclude_ship: i64,
    ) -> Vec<(SessionId, i64)> {
        let candidates = self.pilots.iter().map(|(other, pilot)| self.spatial_ship(*other, pilot));
        let mut inside: Vec<(SessionId, i64)> = crate::geo::SpatialQuery::surface_radius(
            centre,
            0.0,
            radius,
            1e-6,
            crate::geo::SpatialFilter {
                exclude_id: Some(exclude_ship),
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
                for target in self.ships_in_area(centre, *blast_radius, deployable.ship_id) {
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
                for (target, _) in self.ships_in_area(centre, *blast_radius, deployable.ship_id) {
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
        for target in self.ships_in_area([p.x, p.y, p.z], range, own) {
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
                Role::Bomb { .. } => false,
            })
    }
}

#[cfg(test)]
mod tests;
