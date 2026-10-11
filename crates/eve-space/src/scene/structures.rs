//! Upwell structures in the scene. A structure is a body of its system (`BodyKind::Structure`) that the host puts in space, takes out and gives
//! its slim state. The simulation shows what the host says and decides none of it (ADR 0012, rule 3).

use std::sync::Arc;

use eve_destiny::Vec3;

use super::{Out, Scene, SessionId};
use crate::link::StructurePlacement;
use crate::payload;
use crate::world::{Body, BodyKind, Detail, STRUCTURE_CATEGORY, StructureStatus};

impl Scene {
    /// The index of a structure body of this system, if it is in space.
    pub(crate) fn structure_index(&self, structure_id: i64) -> Option<usize> {
        self.system.bodies.iter().position(|body| body.id == structure_id && body.kind == BodyKind::Structure)
    }

    /// Every bound pilot of the scene: a structure is in view from anywhere in its system.
    fn structure_audience(&self) -> Vec<SessionId> {
        self.pilots.iter().filter(|(_, pilot)| pilot.bound).map(|(session, _)| *session).collect()
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
        self.structure_audience().into_iter().map(|session| Self::update(session, vec![added.clone()])).collect()
    }

    /// The host takes a structure out of space: its body goes and those who had it in view are told (`RemoveBalls`). Only a structure is taken so.
    pub(crate) fn remove_structure(&mut self, structure_id: i64, at: i64) -> Vec<Out> {
        let Some(index) = self.structure_index(structure_id) else { return Vec::new() };
        Arc::make_mut(&mut self.system).bodies.remove(index);
        let stamp = self.next_stamp(at);
        let removed = payload::remove_ships(stamp, [structure_id]);
        self.structure_audience().into_iter().map(|session| Self::update(session, vec![removed.clone()])).collect()
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
}
