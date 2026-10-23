use super::*;
use crate::link::AbyssalEnvironmentObject;
use crate::wire::{filetime, id, int, real, text, word};
use crate::world::Body;

#[derive(Debug)]
pub(crate) struct EnvironmentalRuntime {
    pub(crate) object: AbyssalEnvironmentObject,
    next_pulse_at: i64,
}

fn within(position: Vec3, center: [f64; 3], radius: f64) -> bool {
    let delta = [position.x - center[0], position.y - center[1], position.z - center[2]];
    delta.iter().map(|value| value * value).sum::<f64>() <= radius * radius
}

impl Scene {
    pub(crate) fn ensure_abyssal_environment_object(
        &mut self,
        object: AbyssalEnvironmentObject,
        now: i64,
    ) -> Result<Vec<Out>, String> {
        if !crate::link::valid_abyssal_environment_object(&object) {
            return Err("BAD_ABYSSAL_ENVIRONMENT_OBJECT".to_string());
        }
        if self.abyssal.as_ref().is_none_or(|spec| spec.scene != object.body.scene) {
            return Err("ABYSSAL_ENVIRONMENT_SCENE_MISMATCH".to_string());
        }
        let item_id = object.body.item_id;
        if let Some(existing) = self.abyssal_environment_objects.get(&item_id) {
            return if existing.object == object {
                Ok(Vec::new())
            } else {
                Err("ABYSSAL_ENVIRONMENT_IDENTITY_CONFLICT".to_string())
            };
        }
        if self.system.body(item_id).is_some()
            || self.session_of(item_id).is_some()
            || self.wrecks.contains_key(&item_id)
        {
            return Err("ABYSSAL_ENVIRONMENT_IDENTITY_CONFLICT".to_string());
        }
        let metadata = &object.body;
        let body = Body {
            id: item_id,
            type_id: metadata.type_id,
            group_id: metadata.group_id,
            category_id: metadata.category_id,
            owner_id: metadata.owner_id,
            name: metadata.name.clone(),
            kind: BodyKind::AbyssalEnvironment,
            position: Vec3::new(metadata.position[0], metadata.position[1], metadata.position[2]),
            radius: metadata.radius,
            graphic_id: Some(i64::from(metadata.graphic_id)),
            detail: Detail::AbyssalEnvironment {
                direction: Vec3::new(metadata.direction[0], metadata.direction[1], metadata.direction[2]),
            },
        };
        let stamp = self.next_stamp(now);
        let system = Arc::make_mut(&mut self.system);
        system.bodies.push(body);
        let added = match payload::add_abyssal_scene_object(stamp, system, system.bodies.len() - 1) {
            Ok(added) => added,
            Err(_) => {
                system.bodies.pop();
                return Err("ABYSSAL_ENVIRONMENT_SERIALIZATION_FAILED".to_string());
            }
        };
        let pulse_at = object.point_defense.as_ref().map_or(i64::MAX, |pulse| {
            now.saturating_add(pulse.tick_interval_ms.saturating_mul(TICKS_PER_SECOND / 1_000))
        });
        let viewers = self.view_sessions();
        let mut out: Vec<Out> =
            viewers.iter().map(|session| Self::update(*session, vec![added.clone()])).collect();
        self.abyssal_environment_objects
            .insert(item_id, EnvironmentalRuntime { object, next_pulse_at: pulse_at });
        for session in viewers {
            out.extend(self.abyssal_environment_fx(session, Some(item_id), now, true));
        }
        out.extend(self.advance_abyssal_environment(now));
        Ok(out)
    }

    /// Source-backed presentation from abyssalSuppressorPresentationProfile.js: source/self module, 2s, repeat 1000.
    pub(crate) fn abyssal_environment_fx(
        &mut self,
        session: SessionId,
        source: Option<i64>,
        now: i64,
        on: bool,
    ) -> Vec<Out> {
        let stamp = self.next_stamp(now);
        let actions = self
            .abyssal_environment_objects
            .iter()
            .filter(|(id, _)| source.is_none_or(|source| **id == source))
            .filter_map(|(_, runtime)| {
                let pulse = runtime.object.point_defense.as_ref()?;
                let body = &runtime.object.body;
                Some(payload::action(
                    stamp,
                    "OnSpecialFX",
                    [
                        id(body.item_id),
                        id(body.item_id),
                        int(i64::from(body.type_id)),
                        Value::None,
                        Value::None,
                        text(pulse.effect_name.clone()),
                        int(0),
                        int(i64::from(on)),
                        int(i64::from(on)),
                        int(pulse.tick_interval_ms),
                        if on { int(1_000) } else { Value::None },
                        if on { filetime(now) } else { Value::None },
                        int(0),
                        if on {
                            Value::Dict(vec![
                                (word("trackTargets"), Value::Bool(false)),
                                (word("hitRange"), real(runtime.object.effect_radius_meters)),
                                (word("candidateTypeListId"), int(i64::from(pulse.type_list_id))),
                            ])
                        } else {
                            Value::None
                        },
                    ],
                ))
            })
            .collect::<Vec<_>>();
        if actions.is_empty() { Vec::new() } else { vec![Self::update(session, actions)] }
    }

    pub(crate) fn advance_abyssal_environment(&mut self, now: i64) -> Vec<Out> {
        let Some(spec) = self.abyssal.as_ref() else { return Vec::new() };
        let scene = spec.scene;
        let expired = now >= spec.expiry_filetime;
        let mut current = BTreeMap::new();
        if !expired {
            for (source, runtime) in &self.abyssal_environment_objects {
                if runtime.object.effect_radius_meters <= 0.0 {
                    continue;
                }
                for (session, pilot) in &self.pilots {
                    if !self.abyssal_reward_caches.contains_key(&pilot.ship.spec.ship_id)
                        && within(
                            pilot.ship.position,
                            runtime.object.body.position,
                            runtime.object.effect_radius_meters,
                        )
                    {
                        current.insert((*source, pilot.ship.spec.ship_id), *session);
                    }
                }
            }
        }
        let mut out = Vec::new();
        for ((source_item_id, ship_id), session_id) in &self.abyssal_field_memberships {
            if !current.contains_key(&(*source_item_id, *ship_id)) {
                out.push(Out::AbyssalFieldMembership {
                    scene,
                    source_item_id: *source_item_id,
                    ship_id: *ship_id,
                    session_id: *session_id,
                    inside: false,
                });
            }
        }
        for ((source_item_id, ship_id), session_id) in &current {
            if self.abyssal_field_memberships.get(&(*source_item_id, *ship_id)) != Some(session_id) {
                out.push(Out::AbyssalFieldMembership {
                    scene,
                    source_item_id: *source_item_id,
                    ship_id: *ship_id,
                    session_id: *session_id,
                    inside: true,
                });
            }
        }
        self.abyssal_field_memberships = current;
        if expired {
            return out;
        }
        let sources = self.abyssal_environment_objects.keys().copied().collect::<Vec<_>>();
        for source in sources {
            while let Some(runtime) = self.abyssal_environment_objects.get_mut(&source) {
                let Some(pulse) = runtime.object.point_defense.clone() else { break };
                let tick = runtime.next_pulse_at;
                if tick > now {
                    break;
                }
                let center = runtime.object.body.position;
                let radius = runtime.object.effect_radius_meters;
                let Some(next) =
                    tick.checked_add(pulse.tick_interval_ms.saturating_mul(TICKS_PER_SECOND / 1_000))
                else {
                    break;
                };
                runtime.next_pulse_at = next;
                let targets = self
                    .pilots
                    .iter()
                    .filter_map(|(session, pilot)| {
                        let spec = &pilot.ship.spec;
                        (pulse.names(spec.type_id, spec.group_id, spec.category_id)
                            && within(pilot.ship.position, center, radius))
                        .then_some((*session, spec.ship_id))
                    })
                    .collect::<Vec<_>>();
                for (session, target) in targets {
                    let (damaged, applied) = self.damage(target, pulse.damage, tick);
                    out.extend(damaged);
                    if applied.is_some_and(|hit| hit.destroyed) {
                        out.extend(self.destroy_credited(
                            session,
                            crate::death::Credit::default(),
                            Some(source),
                            tick,
                        ));
                    }
                }
                let missiles = self
                    .missiles
                    .iter()
                    .filter_map(|(id, missile)| {
                        (missile.is_flying()
                            && pulse.names(missile.spec.charge_type_id, missile.spec.group_id, 8)
                            && within(missile.position, center, radius))
                        .then_some(*id)
                    })
                    .collect::<Vec<_>>();
                for id in missiles {
                    out.extend(self.intercept_missile(
                        id,
                        crate::missile_entity::Interceptor::PointDefense { module_id: source },
                        tick,
                    ));
                }
            }
        }
        out
    }
}
