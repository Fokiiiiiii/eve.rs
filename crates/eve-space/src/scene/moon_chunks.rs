//! Durable moon chunk balls and their Standup Moon Drill phase effects.
use std::sync::Arc;

use crate::link::MoonChunkPlacement;
use crate::payload;
use crate::wire::{id, int, real};
use crate::world::{Body, BodyKind, Detail};
use eve_destiny::Vec3;

use super::{Out, Scene};

impl Scene {
    /// Adds a chunk once, then updates its pose or `moon_mining` phase idempotently.
    pub(crate) fn upsert_moon_chunk(&mut self, placement: MoonChunkPlacement, now: i64) -> Vec<Out> {
        if placement.solar_system_id != self.solar_system_id()
            || placement.chunk_id <= 0
            || placement.source_structure_id <= 0
            || !((placement.module_id == 0 && placement.module_type_id == 0)
                || (placement.module_id > 0 && placement.module_type_id > 0))
            || placement.moon_id <= 0
            || !matches!(placement.phase.as_str(), "tractor_phase" | "stasis_phase" | "fracture_phase")
        {
            return Vec::new();
        }

        if let Some(index) = self
            .system
            .bodies
            .iter()
            .position(|body| body.id == placement.chunk_id && body.kind == BodyKind::MoonChunk)
        {
            let old = &self.system.bodies[index];
            let moved = distance(old.position, placement.position) >= 1.0;
            let phase_changed = !matches!(
                &old.detail,
                Detail::MoonChunk {
                    source_structure_id,
                    module_id,
                    module_type_id,
                    moon_id,
                    phase,
                    phase_started_filetime,
                } if *source_structure_id == placement.source_structure_id
                    && *module_id == placement.module_id
                    && *module_type_id == placement.module_type_id
                    && *moon_id == placement.moon_id
                    && *phase == placement.phase
                    && *phase_started_filetime == placement.phase_started_filetime
            );
            if !moved && !phase_changed {
                return Vec::new();
            }

            let stamp = self.next_stamp(now);
            let system = Arc::make_mut(&mut self.system);
            let body = &mut system.bodies[index];
            if moved {
                body.position = vector3(placement.position);
            }
            body.name.clone_from(&placement.name);
            body.type_id = placement.type_id;
            body.group_id = placement.group_id;
            body.category_id = placement.category_id;
            body.owner_id = placement.owner_id;
            body.radius = placement.radius;
            body.graphic_id = Some(i64::from(placement.graphic_id));
            body.detail = detail(&placement);

            let mut actions = Vec::new();
            if moved {
                actions.push(payload::action(
                    stamp,
                    "SetBallPosition",
                    [
                        id(placement.chunk_id),
                        real(placement.position[0]),
                        real(placement.position[1]),
                        real(placement.position[2]),
                    ],
                ));
            }
            if phase_changed && let Some(effect) = payload::moon_chunk_effect(stamp, body) {
                actions.push(effect);
            }
            if actions.is_empty() {
                return Vec::new();
            }
            return self
                .structure_audience()
                .into_iter()
                .map(|session| Self::update(session, actions.clone()))
                .collect();
        }

        let chunk_detail = detail(&placement);
        let body = Body {
            id: placement.chunk_id,
            type_id: placement.type_id,
            group_id: placement.group_id,
            category_id: placement.category_id,
            owner_id: placement.owner_id,
            name: placement.name,
            kind: BodyKind::MoonChunk,
            position: vector3(placement.position),
            radius: placement.radius,
            graphic_id: Some(i64::from(placement.graphic_id)),
            detail: chunk_detail,
        };
        let stamp = self.next_stamp(now);
        let system = Arc::make_mut(&mut self.system);
        system.bodies.push(body);
        let index = system.bodies.len() - 1;
        let Ok(actions) = payload::add_moon_chunk(stamp, system, index) else {
            system.bodies.pop();
            return Vec::new();
        };
        self.structure_audience().into_iter().map(|session| Self::update(session, actions.clone())).collect()
    }

    /// Removes a chunk after its persisted extraction ends; only a fracture plays effect 3.
    pub(crate) fn remove_moon_chunk(&mut self, chunk_id: i64, effect: i32, now: i64) -> Vec<Out> {
        let Some(index) = self
            .system
            .bodies
            .iter()
            .position(|body| body.id == chunk_id && body.kind == BodyKind::MoonChunk)
        else {
            return Vec::new();
        };
        if !matches!(effect, 0 | 3) {
            return Vec::new();
        }
        let audience = self.structure_audience();
        Arc::make_mut(&mut self.system).bodies.remove(index);
        let stamp = self.next_stamp(now);
        let mut actions = Vec::with_capacity(2);
        if effect != 0 {
            actions.push(payload::action(
                stamp,
                "TerminalPlayDestructionEffect",
                [id(chunk_id), int(i64::from(effect))],
            ));
        }
        actions.push(payload::remove_ships(stamp, [chunk_id]));
        audience.into_iter().map(|session| Self::update(session, actions.clone())).collect()
    }
}

fn detail(placement: &MoonChunkPlacement) -> Detail {
    Detail::MoonChunk {
        source_structure_id: placement.source_structure_id,
        module_id: placement.module_id,
        module_type_id: placement.module_type_id,
        moon_id: placement.moon_id,
        phase: placement.phase.clone(),
        phase_started_filetime: placement.phase_started_filetime,
    }
}

fn vector3(position: [f64; 3]) -> Vec3 {
    Vec3::new(position[0], position[1], position[2])
}

fn distance(position: Vec3, target: [f64; 3]) -> f64 {
    (position.x - target[0]).hypot(position.y - target[1]).hypot(position.z - target[2])
}
