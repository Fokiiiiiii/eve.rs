//! Upwell structures in the scene. A structure is a body of its system (`BodyKind::Structure`) that the host puts in space, takes out and gives
//! its slim state. The simulation shows what the host says and decides none of it (ADR 0012, rule 3).

use std::sync::Arc;

use eve_destiny::Vec3;

use super::{Out, Scene, SessionId};
use crate::health::{Damage, StructureHealth};
use crate::link::StructurePlacement;
use crate::payload;
use crate::world::{Body, BodyKind, Detail, STRUCTURE_CATEGORY, StructureStatus};

impl Scene {
    /// The index of a structure body of this system, if it is in space.
    pub(crate) fn structure_index(&self, structure_id: i64) -> Option<usize> {
        self.system.bodies.iter().position(|body| body.id == structure_id && body.kind == BodyKind::Structure)
    }

    /// The authoritative structure body shared by scene observers and host range queries.
    pub(crate) fn structure_body(&self, structure_id: i64) -> Option<&Body> {
        self.structure_index(structure_id).map(|index| &self.system.bodies[index])
    }

    /// Measures cargo-deposit range using this session's physical ship and the local structure body.
    pub(crate) fn measure_structure_deposit(
        &mut self,
        session: SessionId,
        structure_id: i64,
        ship_id: i64,
        now: i64,
    ) -> Vec<Out> {
        let refused = |reason| Out::Refused {
            session,
            command: crate::link::COMMAND_MEASURE,
            reason,
        };
        if session == 0 || structure_id <= 0 || ship_id <= 0 {
            return vec![refused("INVALID_STRUCTURE_DEPOSIT_IDS")];
        }
        if self.controlled_structures.contains_key(&session) {
            return vec![refused("NO_SHIP_IN_SPACE")];
        }
        let Some(pilot) = self.pilots.get(&session) else {
            return vec![refused("NO_SHIP_IN_SPACE")];
        };
        if pilot.ship.spec.ship_id != ship_id || self.unpiloted.contains_key(&ship_id) {
            return vec![refused("NOT_LOCAL_SHIP")];
        }
        self.settle_pilot(session, now);
        let distance = (|| {
            let pilot = self.pilots.get(&session).ok_or("NO_SHIP_IN_SPACE")?;
            if matches!(pilot.cloak, crate::visibility::CloakState::Cloaking | crate::visibility::CloakState::Cloaked) {
                return Err("CLOAKED");
            }
            if pilot.flight.is_some() {
                return Err("IN_WARP");
            }
            let body = self.structure_body(structure_id).ok_or("STRUCTURE_NOT_FOUND")?;
            let ship = &pilot.ship;
            if ![body.position.x, body.position.y, body.position.z,
                ship.position.x, ship.position.y, ship.position.z,
                body.radius, ship.spec.radius].iter().all(|value| value.is_finite())
                || body.radius <= 0.0 || ship.spec.radius <= 0.0
            {
                return Err("INVALID_STRUCTURE_DEPOSIT_STATE");
            }
            let centre = (ship.position.x - body.position.x)
                .hypot(ship.position.y - body.position.y)
                .hypot(ship.position.z - body.position.z);
            if !centre.is_finite() {
                return Err("INVALID_STRUCTURE_DEPOSIT_STATE");
            }
            Ok((centre - body.radius - ship.spec.radius).max(0.0))
        })();
        let out = match distance {
            Ok(distance) => Out::Measured {
                session,
                command: crate::link::COMMAND_MEASURE,
                distance: Some(distance),
            },
            Err(reason) => refused(reason),
        };
        self.with_backlog(vec![out])
    }

    /// Every bound view in this system: structures are visible from anywhere in their system.
    fn structure_audience(&self) -> Vec<SessionId> {
        self.view_sessions()
    }

    /// Puts a structure in space where the host placed it (protocol 59): a body of the system, told to those who see it. Given once; one already
    /// in space is left as it is.
    pub(crate) fn place_structure(&mut self, placement: StructurePlacement, now: i64) -> Vec<Out> {
        if self.structure_index(placement.structure_id).is_some() {
            return Vec::new();
        }
        let [x, y, z] = placement.position;
        let body = Body {
            id: placement.structure_id,
            type_id: placement.type_id,
            group_id: placement.group_id,
            category_id: STRUCTURE_CATEGORY,
            owner_id: placement.owner_id,
            name: placement.name,
            kind: BodyKind::Structure,
            position: Vec3::new(x, y, z),
            radius: placement.radius,
            graphic_id: None,
            detail: Detail::Structure {
                corporation_id: i64::from(placement.corporation_id),
                alliance_id: i64::from(placement.alliance_id),
                status: placement.status,
            },
        };
        let stamp = self.next_stamp(now);
        let system = Arc::make_mut(&mut self.system);
        system.bodies.push(body);
        let index = system.bodies.len() - 1;
        let added = match payload::add_structure(stamp, system, index, now) {
            Ok(added) => added,
            Err(_) => {
                system.bodies.pop();
                return Vec::new();
            }
        };
        let health = StructureHealth::new(placement.vitals, placement.shield_recharge_ms, placement.limits, now);
        self.structure_health.insert(placement.structure_id, health);
        self.structure_audience().into_iter().map(|session| Self::update(session, vec![added.clone()])).collect()
    }

    /// The host takes a structure out of space: its body goes and views that had it are told.
    pub(crate) fn remove_structure(&mut self, structure_id: i64, at: i64) -> Vec<Out> {
        let Some(index) = self.structure_index(structure_id) else { return Vec::new() };
        let audience = self.structure_audience();
        let mut out = self.break_structure_tethers(structure_id, at);
        if let Some(controller) = self.controller_of_structure(structure_id)
            && let Some(released) = self.release_controlled_structure(controller, structure_id, at)
        {
            out.extend(released);
        }
        Arc::make_mut(&mut self.system).bodies.remove(index);
        self.structure_health.remove(&structure_id);
        let stamp = self.next_stamp(at);
        let removed = payload::remove_ships(stamp, [structure_id]);
        out.extend(audience.into_iter().map(|session| Self::update(session, vec![removed.clone()])));
        out
    }

    /// The host sets a structure's slim state: those who have it in view are told its slim item again (`OnSlimItemChange`). A state already
    /// set is not told again.
    pub(crate) fn set_structure_state(&mut self, structure_id: i64, status: StructureStatus, at: i64) -> Vec<Out> {
        let Some(index) = self.structure_index(structure_id) else { return Vec::new() };
        if matches!(&self.system.bodies[index].detail, Detail::Structure { status: current, .. } if *current == status) {
            return Vec::new();
        }
        let stamp = self.next_stamp(at);
        let system = Arc::make_mut(&mut self.system);
        if let Detail::Structure { status: current, .. } = &mut system.bodies[index].detail {
            *current = status;
        }
        let change = payload::structure_slim_change(stamp, system, index);
        self.structure_audience().into_iter().map(|session| Self::update(session, vec![change.clone()])).collect()
    }

    /// A hit on a structure of this system: its layers take what the hit does, cut to the per-second limit of the layer it lands in. Told to the host
    /// when a layer is emptied or a layer's repair is paused in this second (`StructureDamaged`). A structure not in space is left alone.
    // shortcut: no weapon path hits a structure body yet (ST-8a, and the host's damage command in ST-1 d); the tests call it
    #[cfg_attr(not(test), allow(dead_code))]
    pub(crate) fn damage_structure(&mut self, structure_id: i64, raw: Damage, now: i64) -> Vec<Out> {
        if self.structure_index(structure_id).is_none() {
            return Vec::new();
        }
        let Some(health) = self.structure_health.get_mut(&structure_id) else { return Vec::new() };
        let Some(hit) = health.apply(now, raw) else { return Vec::new() };
        if hit.depleted.is_none() && !hit.over_threshold {
            return Vec::new();
        }
        vec![Out::StructureDamaged { structure_id, depleted: hit.depleted, over_threshold: hit.over_threshold }]
    }
}
