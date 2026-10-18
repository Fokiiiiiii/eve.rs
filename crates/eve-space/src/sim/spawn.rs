use super::*;

use crate::link::valid_abyssal_content_id;

impl Sim {
    pub(super) fn ensure_abyssal_environment_object(
        &mut self,
        request_id: u64,
        object: crate::link::AbyssalEnvironmentObject,
        now: i64,
    ) -> Vec<Message> {
        let failed = |reason: &str| {
            vec![Message::EnsureAbyssalEnvironmentObjectReply {
                request_id,
                outcome: Err(reason.to_string()),
            }]
        };
        if request_id == 0 || !crate::link::valid_abyssal_environment_object(&object) {
            return failed("BAD_ABYSSAL_ENVIRONMENT_OBJECT");
        }
        if self.scenes.iter().any(|(key, scene)| {
            *key != object.body.scene && scene.abyssal_environment_objects.contains_key(&object.body.item_id)
        }) {
            return failed("ABYSSAL_ENVIRONMENT_SCENE_MISMATCH");
        }
        let Some(scene) = self.scenes.get_mut(&object.body.scene) else {
            return failed("ABYSSAL_ENVIRONMENT_SCENE_MISSING");
        };
        match scene.ensure_abyssal_environment_object(object, now) {
            Ok(out) => {
                let mut messages = Self::messages(out);
                messages.push(Message::EnsureAbyssalEnvironmentObjectReply { request_id, outcome: Ok(()) });
                messages
            }
            Err(reason) => failed(&reason),
        }
    }

    pub(super) fn refresh_abyssal_npc(
        &mut self,
        request_id: u64,
        scene_key: SceneKey,
        session_id: SessionId,
        ship: crate::ship::ShipSpec,
        identity: crate::ship::NpcIdentity,
        now: i64,
    ) -> Vec<Message> {
        let failed = |reason: &str| {
            vec![Message::RefreshAbyssalNpcReply { request_id, outcome: Err(reason.to_string()) }]
        };
        if request_id == 0
            || scene_key.instance_id <= 0
            || !is_npc_session(session_id)
            || identity.wreck.is_some()
            || identity.host_wreck
        {
            return failed("BAD_ABYSSAL_NPC_REFRESH");
        }
        let Some(scene) = self.scenes.get_mut(&scene_key) else {
            return failed("ABYSSAL_NPC_SCENE_MISMATCH");
        };
        let Some(receipt) = scene.abyssal_npc_spawn_receipts.get(&ship.ship_id) else {
            return failed("ABYSSAL_NPC_IDENTITY_CONFLICT");
        };
        if receipt.destroyed {
            return failed("ABYSSAL_NPC_ALREADY_DESTROYED");
        }
        if receipt.owner_id != ship.owner_id
            || receipt.type_id != ship.type_id
            || scene.session_of(ship.ship_id) != Some(session_id)
        {
            return failed("ABYSSAL_NPC_IDENTITY_CONFLICT");
        }
        let mut out = scene.respec(session_id, ship, now);
        for weapon in &identity.combat.weapons {
            out.extend(scene.update_effect(
                session_id,
                weapon.module_id,
                crate::modules::PendingUpdate {
                    duration_ms: weapon.cycle_ms,
                    capacitor_need: weapon.capacitor_need,
                    turret: Some(weapon.turret.clone()),
                    changes: Vec::new(),
                },
            ));
        }
        if let Some(pilot) = scene.pilots.get_mut(&session_id) {
            pilot.ship.npc = Some(identity);
        }
        let mut messages = Self::messages(out);
        messages.push(Message::RefreshAbyssalNpcReply { request_id, outcome: Ok(()) });
        messages
    }

    pub(super) fn ensure_abyssal_reward_cache(
        &mut self,
        request_id: u64,
        cache: crate::link::AbyssalRewardCachePlacement,
        now: i64,
    ) -> Vec<Message> {
        let reply = |error: Option<String>| Message::EnsureAbyssalRewardCacheReply {
            request_id,
            outcome: error.map_or(Ok(()), Err),
        };
        if request_id == 0 || !crate::link::valid_abyssal_reward_cache(&cache) {
            return vec![reply(Some("BAD_ABYSSAL_REWARD_CACHE".to_string()))];
        }
        if self.scenes.iter().any(|(key, scene)| {
            *key != cache.scene
                && (scene.session_of(cache.ship.ship_id).is_some()
                    || scene.abyssal_reward_caches.contains_key(&cache.ship.ship_id))
        }) {
            return vec![reply(Some("ABYSSAL_CACHE_SCENE_MISMATCH".to_string()))];
        }
        let Some(scene) = self.scenes.get_mut(&cache.scene) else {
            return vec![reply(Some("ABYSSAL_CACHE_SCENE_MISSING".to_string()))];
        };
        let (out, error) = scene.ensure_abyssal_reward_cache(cache, now);
        let mut messages = Self::messages(out);
        messages.push(reply(error));
        messages
    }

    pub(super) fn ensure_abyssal_loot_container(
        &mut self,
        request_id: u64,
        content_id: String,
        placement: crate::link::WreckPlacement,
        now: i64,
    ) -> Result<Vec<Message>, ProtocolError> {
        let reply = |error: Option<String>| Message::EnsureAbyssalLootContainerReply {
            request_id,
            outcome: error.map_or(Ok(()), Err),
        };
        if request_id == 0 || !crate::link::valid_abyssal_loot(&content_id, &placement) {
            return Ok(vec![reply(Some("BAD_ABYSSAL_LOOT_CONTAINER".to_string()))]);
        }
        if self.scenes.iter().any(|(key, scene)| {
            *key != placement.scene
                && (scene.wreck(placement.wreck_id).is_some()
                    || scene.abyssal_loot_receipts.contains_key(&placement.wreck_id))
        }) {
            return Ok(vec![reply(Some("ABYSSAL_LOOT_SCENE_MISMATCH".to_string()))]);
        }
        let Some(scene) = self.scenes.get(&placement.scene).filter(|scene| scene.abyssal.is_some()) else {
            return Ok(vec![reply(Some("ABYSSAL_LOOT_SCENE_MISSING".to_string()))]);
        };
        if let Some((stored_content, stored)) = scene.abyssal_loot_receipts.get(&placement.wreck_id) {
            let error = if stored_content != &content_id || stored != &placement {
                Some("ABYSSAL_LOOT_IDENTITY_CONFLICT".to_string())
            } else if scene.wreck(placement.wreck_id).is_none() {
                Some("ABYSSAL_LOOT_ALREADY_REMOVED".to_string())
            } else {
                None
            };
            return Ok(vec![reply(error)]);
        }
        if scene.wreck(placement.wreck_id).is_some() || scene.session_of(placement.wreck_id).is_some() {
            return Ok(vec![reply(Some("ABYSSAL_LOOT_IDENTITY_CONFLICT".to_string()))]);
        }
        let mut messages = self.handle_world(Message::PlaceWreck(Box::new(placement.clone())), now)?;
        if let Some(scene) = self.scenes.get_mut(&placement.scene) {
            scene.abyssal_loot_receipts.insert(placement.wreck_id, (content_id, placement));
        }
        messages.push(reply(None));
        Ok(messages)
    }

    pub(super) fn ensure_abyssal_scene(
        &mut self,
        request_id: u64,
        spec: AbyssalSceneSpec,
        now: i64,
    ) -> Vec<Message> {
        let reply = |outcome| Message::EnsureAbyssalSceneReply { request_id, outcome };
        if request_id == 0 || !spec.scene.is_valid() || spec.scene.instance_id <= 0 {
            return vec![reply(Err("BAD_ABYSSAL_SCENE_KEY".to_string()))];
        }
        let scene_key = spec.scene;
        if let Some(scene) = self.scenes.get_mut(&scene_key) {
            scene.set_abyssal_spec(spec, now);
            return vec![reply(Ok(()))];
        }
        let system = match self.universe.system(scene_key.location_id) {
            Ok(system) => system,
            Err(reason) => return vec![reply(Err(reason))],
        };
        let mut scene = self.universe.abyssal_scene_of(system);
        scene.set_abyssal_spec(spec, now);
        self.scenes.insert(scene_key, scene);
        vec![reply(Ok(()))]
    }

    pub(super) fn ensure_abyssal_scene_object(
        &mut self,
        request_id: u64,
        object: crate::link::AbyssalSceneObject,
        now: i64,
    ) -> Vec<Message> {
        let reply = |outcome| Message::EnsureAbyssalSceneObjectReply { request_id, outcome };
        if request_id == 0 || !crate::link::valid_abyssal_scene_object(&object) {
            return vec![reply(Err("BAD_ABYSSAL_SCENE_OBJECT".to_string()))];
        }
        let Some(scene) = self.scenes.get_mut(&object.scene) else {
            return vec![reply(Err("ABYSSAL_SCENE_NOT_FOUND".to_string()))];
        };
        match scene.ensure_abyssal_scene_object(object, now) {
            Ok(told) => {
                let mut messages = Self::messages(told);
                messages.push(reply(Ok(())));
                messages
            }
            Err(reason) => vec![reply(Err(reason))],
        }
    }

    pub(super) fn remove_abyssal_scene(
        &mut self,
        request_id: u64,
        scene_key: SceneKey,
        settled_npc_ids: Vec<i64>,
        settled_loot_ids: Vec<i64>,
        now: i64,
    ) -> Vec<Message> {
        let reply = |outcome| Message::RemoveAbyssalSceneReply { request_id, outcome };
        if request_id == 0 || !scene_key.is_valid() || scene_key.instance_id <= 0 {
            return vec![reply(Err("BAD_ABYSSAL_SCENE_KEY".to_string()))];
        }
        let valid_ids = |ids: &[i64]| {
            ids.len() <= 4096
                && ids.iter().all(|id| *id > 0)
                && ids.iter().collect::<std::collections::HashSet<_>>().len() == ids.len()
        };
        if !valid_ids(&settled_npc_ids)
            || !valid_ids(&settled_loot_ids)
            || settled_npc_ids.iter().any(|id| settled_loot_ids.contains(id))
        {
            return vec![reply(Err("BAD_ABYSSAL_SCENE_CLEANUP_IDS".to_string()))];
        }
        if self.scenes.iter().any(|(key, scene)| {
            *key != scene_key
                && settled_npc_ids.iter().chain(&settled_loot_ids).any(|id| {
                    scene.session_of(*id).is_some()
                        || scene.wreck(*id).is_some()
                        || scene.abyssal_npc_spawn_receipts.contains_key(id)
                        || scene.abyssal_participant_deaths.contains_key(id)
                        || scene.abyssal_loot_receipts.contains_key(id)
                        || scene.abyssal_reward_caches.contains_key(id)
                        || scene.abyssal_environment_objects.contains_key(id)
                })
        }) {
            return vec![reply(Err("ABYSSAL_CLEANUP_ENTITY_SCENE_MISMATCH".to_string()))];
        }
        let Some(mut scene) = self.scenes.remove(&scene_key) else { return vec![reply(Ok(()))] };
        // Check every participant/death receipt before removing even one supplied NPC or loot item.
        let participant_present = !scene.widowed.is_empty()
            || !scene.abyssal_participant_deaths.is_empty()
            || !scene.controlled_structures.is_empty()
            || scene.pilots.iter().any(|(session, pilot)| {
                !is_npc_session(*session)
                    && !scene.abyssal_reward_caches.contains_key(&pilot.ship.spec.ship_id)
            })
            || scene.unpiloted.keys().any(|ship| !scene.abyssal_reward_caches.contains_key(ship));
        let invalid_npc = settled_npc_ids.iter().any(|id| {
            (scene.session_of(*id).is_some() && !scene.abyssal_npc_spawn_receipts.contains_key(id))
                || scene.wreck(*id).is_some()
                || scene.abyssal_reward_caches.contains_key(id)
                || scene.abyssal_environment_objects.contains_key(id)
        });
        let invalid_loot = settled_loot_ids.iter().any(|id| {
            scene.session_of(*id).is_some()
                || scene.system().body(*id).is_some()
                || scene.abyssal_npc_spawn_receipts.contains_key(id)
                || scene.abyssal_reward_caches.contains_key(id)
                || scene.abyssal_participant_deaths.contains_key(id)
        });
        if participant_present || invalid_npc || invalid_loot {
            if participant_present {
                eprintln!(
                    "eve-sim: abyssal.remove_scene_not_empty branch=participant_guard scene={scene_key:?} {}",
                    scene.abyssal_cleanup_diagnostic(),
                );
            }
            self.scenes.insert(scene_key, scene);
            return vec![reply(Err(if participant_present {
                "ABYSSAL_SCENE_NOT_EMPTY"
            } else {
                "ABYSSAL_CLEANUP_ENTITY_IDENTITY_MISMATCH"
            }
            .to_string()))];
        }
        // A dead NPC's transient wreck has a different ID; only the NPC spawn receipt and the wreck's launcher link it to a settled hull.
        let settled_dead_npc_ids = settled_npc_ids
            .iter()
            .copied()
            .filter(|npc_id| {
                scene.session_of(*npc_id).is_none() && scene.abyssal_npc_spawn_receipts.contains_key(npc_id)
            })
            .collect::<std::collections::HashSet<_>>();
        let mut out = Vec::new();
        for ship_id in settled_npc_ids {
            if let Some(session) = scene.session_of(ship_id) {
                out.extend(scene.remove_npc(session, now).unwrap_or_default());
            }
        }
        for item_id in settled_loot_ids {
            if scene.wreck(item_id).is_some() {
                out.extend(scene.remove_wreck(item_id, 0, now));
            }
        }
        let settled_npc_wreck_ids = scene
            .wrecks
            .values()
            .filter(|wreck| settled_dead_npc_ids.contains(&wreck.launcher_id))
            .map(|wreck| wreck.id)
            .collect::<Vec<_>>();
        for wreck_id in settled_npc_wreck_ids {
            out.extend(scene.remove_wreck(wreck_id, 0, now));
        }
        if !scene.abyssal_scene_is_clear() && scene.abyssal_scene_is_clear_except_missiles() {
            out.extend(scene.retire_abyssal_projectiles(now));
        }
        if !scene.abyssal_scene_is_clear() {
            eprintln!(
                "eve-sim: abyssal.remove_scene_not_empty branch=actor_clear scene={scene_key:?} {}",
                scene.abyssal_cleanup_diagnostic(),
            );
            self.scenes.insert(scene_key, scene);
            let mut messages = Self::messages(out);
            messages.push(reply(Err("ABYSSAL_SCENE_NOT_EMPTY".to_string())));
            return messages;
        }
        out.extend(scene.take_sim_damageable_entity_removals());
        let mut messages = Self::messages(out);
        messages.push(reply(Ok(())));
        messages
    }

    pub(super) fn ensure_abyssal_trace(
        &mut self,
        request_id: u64,
        actor_session_id: Option<u64>,
        trace: crate::link::AbyssalTrace,
        now: i64,
    ) -> Vec<Message> {
        let failed = |reason: String| Message::EnsureAbyssalTracePublishedReply {
            request_id,
            status: crate::link::AbyssalTraceStatus::Failed,
            recipient_count: 0,
            actor_notified: false,
            error: Some(reason),
        };
        if request_id == 0
            || actor_session_id.is_some_and(|session| session == 0)
            || !crate::link::valid_abyssal_trace(&trace)
        {
            return vec![failed("BAD_ABYSSAL_TRACE".to_string())];
        }
        let scene_key = SceneKey::solar_system(trace.solar_system_id);
        if self
            .scenes
            .iter()
            .any(|(key, scene)| *key != scene_key && scene.abyssal_traces.contains_key(&trace.item_id))
        {
            return vec![failed("ABYSSAL_TRACE_LOCATION_MISMATCH".to_string())];
        }

        let (already_present, outs) = if let Some(scene) = self.scenes.get_mut(&scene_key) {
            match scene.ensure_abyssal_trace(trace.clone(), now) {
                Ok(result) => result,
                Err(reason) => return vec![failed(reason)],
            }
        } else {
            let system = match self.universe.system(trace.solar_system_id) {
                Ok(system) => system,
                Err(reason) => return vec![failed(reason)],
            };
            let mut scene = self.universe.scene_of(system);
            let result = match scene.ensure_abyssal_trace(trace, now) {
                Ok(result) => result,
                Err(reason) => return vec![failed(reason)],
            };
            self.scenes.insert(scene_key, scene);
            result
        };
        let recipient_count = i32::try_from(outs.len()).unwrap_or(i32::MAX);
        let actor_notified =
            actor_session_id.is_some_and(|actor| outs.iter().any(|out| out.session() == actor));
        let mut messages = Self::messages(outs);
        messages.push(Message::EnsureAbyssalTracePublishedReply {
            request_id,
            status: if already_present {
                crate::link::AbyssalTraceStatus::AlreadyPresent
            } else {
                crate::link::AbyssalTraceStatus::Created
            },
            recipient_count,
            actor_notified,
            error: None,
        });
        messages
    }

    pub(super) fn remove_abyssal_trace(
        &mut self,
        request_id: u64,
        solar_system_id: i32,
        item_id: i64,
        now: i64,
    ) -> Vec<Message> {
        let failed = |reason: String| Message::RemoveAbyssalTraceReply {
            request_id,
            status: crate::link::AbyssalTraceStatus::Failed,
            recipient_count: 0,
            error: Some(reason),
        };
        if request_id == 0 || solar_system_id <= 0 || item_id <= 0 {
            return vec![failed("BAD_ABYSSAL_TRACE_IDENTITY".to_string())];
        }
        let scene_key = SceneKey::solar_system(solar_system_id);
        if self
            .scenes
            .iter()
            .any(|(key, scene)| *key != scene_key && scene.abyssal_traces.contains_key(&item_id))
        {
            return vec![failed("ABYSSAL_TRACE_LOCATION_MISMATCH".to_string())];
        }

        let Some(scene) = self.scenes.get_mut(&scene_key) else {
            return vec![Message::RemoveAbyssalTraceReply {
                request_id,
                status: crate::link::AbyssalTraceStatus::AlreadyAbsent,
                recipient_count: 0,
                error: None,
            }];
        };
        let (already_absent, outs, should_remove_scene) = {
            let (already_absent, outs) = scene.remove_abyssal_trace(item_id, now);
            (already_absent, outs, scene.is_empty())
        };
        let recipient_count = i32::try_from(outs.len()).unwrap_or(i32::MAX);
        let mut messages = Self::messages(outs);
        if should_remove_scene && let Some(mut empty) = self.scenes.remove(&scene_key) {
            messages.extend(Self::messages(empty.take_sim_damageable_entity_removals()));
        }
        messages.push(Message::RemoveAbyssalTraceReply {
            request_id,
            status: if already_absent {
                crate::link::AbyssalTraceStatus::AlreadyAbsent
            } else {
                crate::link::AbyssalTraceStatus::Removed
            },
            recipient_count,
            error: None,
        });
        messages
    }

    pub(super) fn remove_abyssal_participant(
        &mut self,
        request_id: u64,
        scene_key: SceneKey,
        ship_id: i64,
        now: i64,
    ) -> Vec<Message> {
        let failed = |reason: String| Message::RemoveAbyssalParticipantReply {
            request_id,
            already_absent: false,
            error: Some(reason),
        };
        if request_id == 0 || !scene_key.is_valid() || scene_key.instance_id <= 0 || ship_id <= 0 {
            return vec![failed("BAD_ABYSSAL_PARTICIPANT_REMOVAL".to_string())];
        }
        let Some(session_id) = self.scenes.get(&scene_key).and_then(|scene| scene.session_of(ship_id)) else {
            if self.scenes.iter().any(|(key, scene)| {
                *key != scene_key
                    && (scene.session_of(ship_id).is_some()
                        || scene.abyssal_participant_deaths.contains_key(&ship_id))
            }) {
                return vec![failed("ABYSSAL_RESIDENT_LOCATION_MISMATCH".to_string())];
            }
            if let Some(session) = self
                .scenes
                .get_mut(&scene_key)
                .and_then(|scene| scene.abyssal_participant_deaths.remove(&ship_id))
                && self.widowed.get(&session) == Some(&scene_key)
            {
                self.widowed.remove(&session);
            }
            return vec![Message::RemoveAbyssalParticipantReply {
                request_id,
                already_absent: true,
                error: None,
            }];
        };
        if is_npc_session(session_id) || self.sessions.get(&session_id) != Some(&scene_key) {
            return vec![failed("ABYSSAL_RESIDENT_NOT_A_PARTICIPANT".to_string())];
        }
        let Some(mut outs) = self
            .scenes
            .get_mut(&scene_key)
            .and_then(|scene| scene.leave(session_id, crate::scene::Departure::LoggedOff, now))
        else {
            return vec![failed("ABYSSAL_RESIDENT_LEAVE_FAILED".to_string())];
        };
        outs.extend(self.forget(session_id));
        let mut messages = Self::messages(outs);
        messages.push(Message::RemoveAbyssalParticipantReply {
            request_id,
            already_absent: false,
            error: None,
        });
        messages
    }

    pub(super) fn get_abyssal_participant_pose(
        &mut self,
        request_id: u64,
        scene_key: SceneKey,
        ship_id: i64,
        now: i64,
    ) -> Vec<Message> {
        let mut deployment = None;
        let mut outcome = Err("ABYSSAL_RESIDENT_SHIP_NOT_FOUND".to_string());
        if request_id == 0 || !scene_key.is_valid() || ship_id <= 0 {
            outcome = Err("BAD_RESIDENT_POSE_REQUEST".to_string());
        } else if let Some(scene) = self.scenes.get_mut(&scene_key) {
            if let Some(session_id) = scene.session_of(ship_id) {
                if is_npc_session(session_id) || self.sessions.get(&session_id) != Some(&scene_key) {
                    outcome = Err("ABYSSAL_RESIDENT_NOT_A_PARTICIPANT".to_string());
                } else {
                    if scene_key.instance_id == 0 {
                        deployment = scene.abyssal_deployment_snapshot(session_id, now);
                    } else {
                        scene.ship(session_id, now);
                    }
                    if let Some(ship) = scene.pilots.get(&session_id).map(|pilot| &pilot.ship) {
                        let position = [ship.position.x, ship.position.y, ship.position.z];
                        let direction = [ship.direction.x, ship.direction.y, ship.direction.z];
                        if position.iter().chain(&direction).all(|value| value.is_finite())
                            && direction.iter().any(|value| *value != 0.0)
                        {
                            outcome = Ok((position, direction));
                        } else {
                            outcome = Err("ABYSSAL_RESIDENT_POSE_INVALID".to_string());
                        }
                    } else {
                        outcome = Err("ABYSSAL_RESIDENT_POSE_NOT_AVAILABLE".to_string());
                    }
                }
            }
        }
        if outcome.is_err() {
            deployment = None;
        }
        vec![Message::GetAbyssalParticipantPoseReply {
            request_id,
            scene: scene_key,
            ship_id,
            outcome,
            deployment,
        }]
    }

    pub(super) fn move_abyssal_participant(
        &mut self,
        request_id: u64,
        ship_id: i64,
        source_key: SceneKey,
        destination_key: SceneKey,
        preserve_source_pose: bool,
        session_handoff: bool,
        position: [f64; 3],
        direction: [f64; 3],
        now: i64,
    ) -> Vec<Message> {
        let reply = |outcome| Message::MoveAbyssalParticipantReply { request_id, outcome };
        let room_transition = source_key.instance_id > 0
            && source_key.instance_id == destination_key.instance_id
            && source_key.location_id == destination_key.location_id
            && source_key.room_index != destination_key.room_index;
        let entry = source_key.instance_id == 0 && destination_key.instance_id > 0;
        let origin_return = source_key.instance_id > 0 && destination_key.instance_id == 0;
        if request_id == 0
            || ship_id <= 0
            || !source_key.is_valid()
            || !destination_key.is_valid()
            || !(room_transition || entry || origin_return)
            || (preserve_source_pose && !room_transition)
            || (session_handoff && !(entry || origin_return))
            || (!preserve_source_pose
                && (position.iter().chain(&direction).any(|value| !value.is_finite())
                    || direction.iter().all(|value| *value == 0.0)))
        {
            return vec![reply(Err("BAD_ABYSSAL_PARTICIPANT_MOVE".to_string()))];
        }
        let Some(session_id) = self.scenes.get(&source_key).and_then(|scene| scene.session_of(ship_id))
        else {
            return vec![reply(Err("ABYSSAL_RESIDENT_SHIP_NOT_FOUND".to_string()))];
        };
        if is_npc_session(session_id) || self.sessions.get(&session_id) != Some(&source_key) {
            return vec![reply(Err("ABYSSAL_RESIDENT_SHIP_NOT_A_PARTICIPANT".to_string()))];
        }
        let (position, direction) = if preserve_source_pose {
            let Some(ship) = self.scenes.get_mut(&source_key).and_then(|scene| scene.ship(session_id, now))
            else {
                return vec![reply(Err("ABYSSAL_RESIDENT_POSE_NOT_AVAILABLE".to_string()))];
            };
            let position = [ship.position.x, ship.position.y, ship.position.z];
            let direction = [ship.direction.x, ship.direction.y, ship.direction.z];
            if position.iter().chain(&direction).any(|value| !value.is_finite())
                || direction.iter().all(|value| *value == 0.0)
            {
                return vec![reply(Err("ABYSSAL_RESIDENT_POSE_INVALID".to_string()))];
            }
            (position, direction)
        } else {
            (position, direction)
        };

        let Some(mut source) = self.scenes.remove(&source_key) else {
            return vec![reply(Err("ABYSSAL_SOURCE_SCENE_NOT_FOUND".to_string()))];
        };
        let destination = if destination_key.instance_id == 0 {
            if let Some(destination) = self.scenes.remove(&destination_key) {
                Ok(destination)
            } else {
                self.universe.system(destination_key.location_id).map(|system| self.universe.scene_of(system))
            }
        } else {
            self.scenes
                .remove(&destination_key)
                .ok_or_else(|| "ABYSSAL_DESTINATION_SCENE_NOT_FOUND".to_string())
        };
        let Ok(mut destination) = destination else {
            self.scenes.insert(source_key, source);
            return vec![reply(Err(destination
                .err()
                .unwrap_or_else(|| "ABYSSAL_DESTINATION_SCENE_NOT_FOUND".to_string())))];
        };

        let moved = source.move_abyssal_participant_to(
            &mut destination,
            session_id,
            ship_id,
            position,
            direction,
            now,
        );
        self.scenes.insert(source_key, source);
        self.scenes.insert(destination_key, destination);
        match moved {
            Ok(told) => {
                self.sessions.insert(session_id, destination_key);
                let mut messages = Self::messages(told);
                if session_handoff {
                    let mut ordered = vec![reply(Ok(()))];
                    ordered.append(&mut messages);
                    ordered
                } else {
                    messages.push(reply(Ok(())));
                    messages
                }
            }
            Err(reason) => vec![reply(Err(reason))],
        }
    }

    pub(super) fn rebind_abyssal_session(
        &mut self,
        request_id: u64,
        new_session: SessionId,
        ship_id: i64,
        scene_key: SceneKey,
        now: i64,
    ) -> Vec<Message> {
        let reply = |outcome| Message::RebindAbyssalSessionReply { request_id, outcome };
        if request_id == 0 || new_session == 0 || ship_id <= 0 || !scene_key.is_valid() {
            return vec![reply(Err("BAD_ABYSSAL_SESSION_REBIND".to_string()))];
        }
        if self.sessions.contains_key(&new_session) {
            return vec![reply(Err("ABYSSAL_SESSION_ALREADY_IN_USE".to_string()))];
        }
        let Some(old_session) = self.scenes.get(&scene_key).and_then(|scene| scene.session_of(ship_id))
        else {
            return vec![reply(Err("ABYSSAL_RESIDENT_SHIP_NOT_FOUND".to_string()))];
        };
        if is_npc_session(old_session) || self.sessions.get(&old_session) != Some(&scene_key) {
            return vec![reply(Err("ABYSSAL_RESIDENT_SESSION_NOT_FOUND".to_string()))];
        }
        if old_session == new_session {
            return vec![reply(Ok(()))];
        }
        let Some(scene) = self.scenes.get_mut(&scene_key) else {
            return vec![reply(Err("ABYSSAL_SCENE_NOT_FOUND".to_string()))];
        };
        if let Err(reason) = scene.rebind_abyssal_session(old_session, new_session, ship_id, now) {
            return vec![reply(Err(reason))];
        }
        self.sessions.remove(&old_session);
        self.widowed.remove(&old_session);
        self.sessions.insert(new_session, scene_key);
        vec![reply(Ok(()))]
    }

    pub(super) fn enter(&mut self, enter: Enter, now: i64) -> Vec<Message> {
        let Enter { session_id, scene: scene_key, how, station_id, ship, position, direction } = enter;
        let reply = |outcome| vec![Message::EnterReply { session_id, outcome }];
        if self.sessions.contains_key(&session_id) {
            return reply(Err(format!("ALREADY_IN_SPACE {session_id}")));
        }
        if !scene_key.is_valid() {
            return reply(Err("BAD_SCENE_KEY".to_string()));
        }
        let scene = if scene_key.instance_id == 0 {
            let system = match self.universe.system(scene_key.location_id) {
                Ok(system) => system,
                Err(reason) => return reply(Err(reason)),
            };
            self.scenes.entry(scene_key).or_insert_with(|| self.universe.scene_of(system))
        } else if how == How::InSpace {
            let Some(scene) = self.scenes.get_mut(&scene_key) else {
                return reply(Err(format!("NO_SCENE {}:{}", scene_key.instance_id, scene_key.room_index)));
            };
            scene
        } else {
            return reply(Err("ABYSSAL_REQUIRES_IN_SPACE".to_string()));
        };
        let entered = match how {
            How::Undock => scene.undock(session_id, ship, station_id, now),
            How::Anchor => scene.enter_at_anchor(session_id, ship, now),
            How::InSpace => {
                let (Some(position), Some(direction)) = (position, direction) else {
                    return reply(Err("SPACE_STATE_REQUIRED".to_string()));
                };
                if position.iter().chain(direction.iter()).any(|value| !value.is_finite())
                    || direction.iter().all(|value| *value == 0.0)
                {
                    return reply(Err("BAD_SPACE_STATE".to_string()));
                }
                scene.enter_in_space(session_id, ship, position, direction, now)
            }
        };
        match entered {
            Ok(told) => {
                self.sessions.insert(session_id, scene_key);
                let mut messages = reply(Ok(()));
                messages.extend(Self::messages(told));
                messages
            }
            Err(SceneError::UnknownStation(station)) => {
                let mut messages = reply(Err(format!("UNKNOWN_STATION {station}")));
                if scene_key.instance_id == 0
                    && scene.is_empty()
                    && let Some(mut scene) = self.scenes.remove(&scene_key)
                {
                    messages.extend(Self::messages(scene.take_sim_damageable_entity_removals()));
                }
                messages
            }
            Err(SceneError::AlreadyInSpace(_)) => reply(Err(format!("ALREADY_IN_SPACE {session_id}"))),
            Err(SceneError::NotDestroyed(_)) => reply(Err(format!("NOT_DESTROYED {session_id}"))),
        }
    }

    pub(super) fn forget(&mut self, session: SessionId) -> Vec<Out> {
        let system = self.sessions.remove(&session).or_else(|| {
            let system = self.widowed.remove(&session)?;
            if let Some(scene) = self.scenes.get_mut(&system) {
                scene.forget_widow(session);
            }
            Some(system)
        });
        if let Some(scene_key) = system
            && scene_key.instance_id == 0
            && self.scenes.get(&scene_key).is_some_and(Scene::is_empty)
            && let Some(mut scene) = self.scenes.remove(&scene_key)
        {
            return scene.take_sim_damageable_entity_removals();
        }
        Vec::new()
    }

    /// A player whose ship was destroyed is not in space any more; its scene keeps its ballpark until the host gives it a capsule (`Respawn`) or it leaves.
    pub(super) fn forget_the_destroyed(&mut self, outs: &[Out]) {
        for out in outs {
            if let Out::Destroyed { session, .. } = out
                && !is_npc_session(*session)
                && let Some(system) = self.sessions.remove(session)
            {
                self.widowed.insert(*session, system);
            }
        }
    }

    /// The host asks for an NPC: it comes into the requested scene if the scene has been created.
    pub(super) fn spawn(&mut self, spawn: Spawn, now: i64) -> Vec<Message> {
        let Spawn {
            request_id,
            abyssal_content_id,
            scene: scene_key,
            ship,
            anchor_ship_id,
            position,
            velocity,
            direction,
            behavior,
            identity,
        } = spawn;
        let reply = |outcome| Message::SpawnReply { request_id, outcome };
        let Some(target_scene) = self.scenes.get(&scene_key) else {
            return vec![reply(Err(format!(
                "NO_SCENE {}:{}:{}",
                scene_key.location_id, scene_key.instance_id, scene_key.room_index
            )))];
        };
        let abyssal_scene = target_scene.abyssal.is_some();
        let ship_id = ship.ship_id;
        let type_id = ship.type_id;
        let owner_id = ship.owner_id;
        let content_id = match (abyssal_scene, abyssal_content_id) {
            (true, Some(content_id)) if valid_abyssal_content_id(&content_id) => Some(content_id),
            (true, Some(_)) => return vec![reply(Err("BAD_ABYSSAL_CONTENT_ID".to_string()))],
            (true, None) => return vec![reply(Err("ABYSSAL_CONTENT_ID_REQUIRED".to_string()))],
            (false, Some(_)) => {
                return vec![reply(Err("ABYSSAL_CONTENT_ID_WITHOUT_PRIVATE_SCENE".to_string()))];
            }
            (false, None) => None,
        };

        if let Some(content_id) = content_id.as_deref() {
            if scene_key.instance_id <= 0 || anchor_ship_id != 0 || ship_id <= 0 || type_id <= 0 {
                return vec![reply(Err("BAD_ABYSSAL_NPC_SPAWN".to_string()))];
            }
            if self.scenes.iter().any(|(other_key, scene)| {
                *other_key != scene_key
                    && (scene.session_of(ship_id).is_some()
                        || scene.abyssal_npc_spawn_receipts.contains_key(&ship_id))
            }) {
                return vec![reply(Err("ABYSSAL_NPC_SCENE_MISMATCH".to_string()))];
            }
            if let Some(receipt) =
                self.scenes.get(&scene_key).and_then(|scene| scene.abyssal_npc_spawn_receipts.get(&ship_id))
            {
                if receipt.content_id != content_id
                    || receipt.type_id != type_id
                    || receipt.owner_id != owner_id
                {
                    return vec![reply(Err("ABYSSAL_NPC_IDENTITY_CONFLICT".to_string()))];
                }
                if receipt.destroyed {
                    return vec![reply(Err("ABYSSAL_NPC_ALREADY_DESTROYED".to_string()))];
                }
                let Some(existing_session) =
                    self.scenes.get(&scene_key).and_then(|scene| scene.session_of(ship_id))
                else {
                    return vec![reply(Err("ABYSSAL_NPC_RESIDENT_MISSING".to_string()))];
                };
                if !is_npc_session(existing_session) {
                    return vec![reply(Err("ABYSSAL_NPC_IDENTITY_CONFLICT".to_string()))];
                }
                let existing_matches = self
                    .scenes
                    .get_mut(&scene_key)
                    .and_then(|scene| scene.ship(existing_session, now))
                    .is_some_and(|existing| {
                        existing.spec.type_id == type_id && existing.spec.owner_id == owner_id
                    });
                return if existing_matches {
                    vec![reply(Ok(existing_session))]
                } else {
                    vec![reply(Err("ABYSSAL_NPC_IDENTITY_CONFLICT".to_string()))]
                };
            }
            if self.scenes.get(&scene_key).and_then(|scene| scene.session_of(ship_id)).is_some() {
                return vec![reply(Err("ABYSSAL_NPC_IDENTITY_CONFLICT".to_string()))];
            }
        }

        let Some(scene) = self.scenes.get_mut(&scene_key) else {
            return vec![reply(Err(format!(
                "NO_SCENE {}:{}:{}",
                scene_key.location_id, scene_key.instance_id, scene_key.room_index
            )))];
        };
        let mut position = Vec3::new(position[0], position[1], position[2]);
        if anchor_ship_id != 0 {
            let Some(anchor) = scene
                .session_of(anchor_ship_id)
                .and_then(|session| scene.ship(session, now))
                .map(|anchor| anchor.position)
            else {
                return vec![reply(Err(format!("NO_SHIP {anchor_ship_id}")))];
            };
            position = Vec3::new(anchor.x + position.x, anchor.y + position.y, anchor.z + position.z);
        }
        let place = NpcPlace {
            position,
            velocity: Vec3::new(velocity[0], velocity[1], velocity[2]),
            direction: Vec3::new(direction[0], direction[1], direction[2]),
            speed_fraction: 0.0,
            course: Course::Stop,
        };
        let spawned = if behavior.is_empty() {
            Ok(scene.spawn_npc(ship, identity, place, now))
        } else {
            scene.spawn_npc_with_behavior(ship, identity, place, &behavior, now)
        };
        match spawned {
            Ok((session, told)) => {
                if let Some(content_id) = content_id {
                    scene.abyssal_npc_spawn_receipts.insert(
                        ship_id,
                        crate::scene::AbyssalNpcSpawnReceipt {
                            content_id,
                            type_id,
                            owner_id,
                            destroyed: false,
                        },
                    );
                }
                let mut messages = vec![reply(Ok(session))];
                messages.extend(Self::messages(told));
                messages
            }
            Err(error) => vec![reply(Err(format!("{error:?}")))],
        }
    }

    /// A drone comes into the scene of the pilot's ship: the answer carries the session the drone is known by.
    pub(super) fn launch_drone(&mut self, launch: LaunchDrone, now: i64) -> Vec<Message> {
        let LaunchDrone {
            request_id,
            session_id,
            ship,
            weapon,
            graphic_id,
            orbit_distance,
            attack_range,
            cruise_fraction,
            mining_amount_m3,
            work_cycle_ms,
            mining_waste_multiplier,
            mining_waste_probability,
            salvage_access_bonus,
            effect,
        } = launch;
        let reply = |outcome| Message::SpawnReply { request_id, outcome };
        let Some(scene) = self.scene_of(session_id) else {
            return vec![reply(Err(format!("NO_SHIP {session_id}")))];
        };
        match scene.launch_drone(
            session_id,
            ship,
            DroneFlight {
                weapon,
                graphic_id,
                orbit_distance,
                attack_range,
                cruise_fraction,
                work: crate::drones::DroneWorkProfile {
                    mining_amount_m3,
                    cycle_ms: work_cycle_ms,
                    waste_multiplier: mining_waste_multiplier,
                    waste_probability: mining_waste_probability,
                    salvage_access_bonus,
                },
                effect,
            },
            now,
        ) {
            Ok((session, told)) => {
                let mut messages = Self::messages(told);
                messages.push(reply(Ok(session)));
                messages
            }
            Err(reason) => vec![reply(Err(reason.to_string()))],
        }
    }

    pub fn poll(&mut self, now: i64) -> Vec<Message> {
        let mut outs = Vec::new();
        for scene in self.scenes.values_mut() {
            outs.extend(scene.poll(now));
            outs.extend(scene.advance_abyssal_environment(now));
            outs.extend(scene.advance_abyssal_boundary(now));
        }
        let mut teardown = Vec::new();
        for out in &outs {
            if let Out::Docked { session, .. } = out {
                teardown.extend(self.forget(*session));
            }
        }
        self.forget_the_destroyed(&outs);
        outs.extend(teardown);
        let mut messages: Vec<Message> = Self::messages(outs);
        let mut travellers: Vec<Traveller> = Vec::new();
        for scene in self.scenes.values_mut() {
            let (gone, told) = scene.due_jumps(now);
            travellers.extend(gone);
            messages.extend(Self::messages(told));
        }
        for traveller in travellers {
            messages.extend(self.arrive(traveller, now));
        }
        let empty_systems: Vec<SceneKey> = self
            .scenes
            .iter()
            .filter_map(|(scene_key, scene)| {
                (scene_key.instance_id == 0 && scene.is_empty()).then_some(*scene_key)
            })
            .collect();
        for scene_key in empty_systems {
            if let Some(mut scene) = self.scenes.remove(&scene_key) {
                messages.extend(Self::messages(scene.take_sim_damageable_entity_removals()));
            }
        }
        messages
    }

    pub(super) fn arrive(&mut self, traveller: Traveller, now: i64) -> Vec<Message> {
        let (session, from_system, from_gate, to_system, to_gate) = (
            traveller.session,
            traveller.from_system,
            traveller.from_gate,
            traveller.to_system,
            traveller.to_gate,
        );
        let failed = |sim: &mut Self, traveller: Traveller| {
            let mut messages = vec![Message::Refused {
                session_id: session,
                command: 9,
                reason: "DESTINATION_UNAVAILABLE".to_string(),
            }];
            if let Some(scene) = sim.scenes.get_mut(&SceneKey::solar_system(traveller.from_system)) {
                messages.extend(Self::messages(scene.restore(traveller, now)));
            }
            messages
        };
        let Ok(system) = self.universe.system(to_system) else { return failed(self, traveller) };
        let scene_key = SceneKey::solar_system(to_system);
        let scene = self.scenes.entry(scene_key).or_insert_with(|| self.universe.scene_of(system));
        match scene.arrive(traveller, now) {
            Ok(told) => {
                self.sessions.insert(session, scene_key);
                let mut messages =
                    vec![Message::Jumped { session_id: session, from_system, from_gate, to_system, to_gate }];
                messages.extend(Self::messages(told));
                messages
            }
            Err(traveller) => {
                let should_remove = scene.is_empty();
                let mut removals = if should_remove {
                    self.scenes
                        .remove(&scene_key)
                        .map(|mut scene| Self::messages(scene.take_sim_damageable_entity_removals()))
                        .unwrap_or_default()
                } else {
                    Vec::new()
                };
                let mut messages = failed(self, *traveller);
                messages.append(&mut removals);
                messages
            }
        }
    }
}
