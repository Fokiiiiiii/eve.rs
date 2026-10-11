use super::*;

impl Scene {
    /// A drone sent at a target: flies to it, and when it is in range fires its weapon each cycle; a target that is gone leaves it idle.
    pub(super) fn fight(&mut self, session: SessionId, target: i64, now: i64) -> Vec<Out> {
        let Some(target_session) = self.session_of(target) else {
            if let Some(ai) = self.drones.get_mut(&session) {
                ai.task = Task::Idle;
            }
            return self.go_home(session, IDLE, now);
        };
        let Some(apart) = self.apart(session, target_session) else { return Vec::new() };
        let Some((engagement, chase)) =
            self.drones.get(&session).map(|ai| (ai.engagement_range, ai.chase_range))
        else {
            return Vec::new();
        };
        let activity = self.activity_of(session);
        let in_range = apart <= engagement + 1.0;
        // what the drone is now: in range it fights (and fires at once), a target that runs out of reach is chased
        let wanted = if in_range {
            COMBAT
        } else if activity == COMBAT && apart > chase + 1.0 {
            PURSUIT
        } else if activity == PURSUIT && apart <= chase + 1.0 {
            APPROACHING
        } else {
            activity
        };
        let mut out = Vec::new();
        if wanted != activity {
            self.set_activity(session, wanted);
            if wanted == COMBAT
                && let Some(ai) = self.drones.get_mut(&session)
            {
                ai.next_shot = now;
                ai.next_remote = now;
            }
            out.extend(self.told_state(session));
        }
        if wanted != COMBAT {
            return out;
        }
        // the weapon and the remote effect each run on their own clocks
        let (next_shot, next_remote) =
            self.drones.get(&session).map_or((i64::MAX, i64::MAX), |ai| (ai.next_shot, ai.next_remote));
        if now >= next_shot
            && let Some(effect) = self.drone_effect(session, target)
        {
            if let Some(ai) = self.drones.get_mut(&session) {
                ai.next_shot =
                    now + (effect.duration_ms * crate::scene::TICKS_PER_SECOND as f64 / 1000.0) as i64;
            }
            if let Some(controller) = self.controller_of_drone(session).filter(|id| *id != 0) {
                out.extend(self.clear_tether_policy(controller, now));
            }
            out.extend(self.cycle_fx(session, &effect, now));
            out.extend(self.fire_turret(session, &effect, now));
        }
        if now >= next_remote
            && let Some((effect, lasting_ms)) = self.remote_effect(session, target)
        {
            if let Some(ai) = self.drones.get_mut(&session) {
                ai.next_remote =
                    now + (effect.duration_ms * crate::scene::TICKS_PER_SECOND as f64 / 1000.0) as i64;
            }
            out.extend(self.remote_cycle(session, &effect, lasting_ms, now));
        }
        // a target that went down with the shot leaves the drone nothing to do
        if !self.pilots.contains_key(&target_session) {
            if let Some(ai) = self.drones.get_mut(&session) {
                ai.task = Task::Idle;
            }
            out.extend(self.go_home(session, IDLE, now));
        }
        out
    }

    pub(crate) fn drone_is_attacking(&self, session: SessionId) -> bool {
        self.activity_of(session) == COMBAT && self.drones.get(&session).is_some_and(|ai| {
            ai.landing.as_ref().is_some_and(|effect| effect.offensive())
                || self.pilots.get(&session).and_then(|pilot| pilot.ship.npc.as_ref())
                    .is_some_and(|npc| !npc.combat.weapons.is_empty())
        })
    }

    /// The client is told that the drone's effect plays at its target (`OnSpecialFX`), to those who look on as to its pilot.
    pub(super) fn cycle_fx(&mut self, session: SessionId, effect: &Effect, now: i64) -> Vec<Out> {
        let drone_id = self.ship_id_of(session);
        let stamp = self.next_stamp(now);
        let fx = payload::action(
            stamp,
            "OnSpecialFX",
            [
                id(drone_id),
                id(drone_id),
                int(i64::from(effect.module_type)),
                id(effect.target_id),
                Value::None,
                text(effect.fx.clone()),
                int(i64::from(effect.offensive())),
                int(1),
                int(0),
                real(effect.duration_ms),
                int(1),
                Value::None,
                int(0),
                Value::None,
            ],
        );
        self.tell_all(session, vec![fx])
    }

    /// The remote effect a drone runs at a target each cycle, as an effect of the scene (its module is the drone), and how long what it lands lasts.
    pub(super) fn remote_effect(&self, session: SessionId, target: i64) -> Option<(Effect, f64)> {
        let remote = self.drones.get(&session)?.remote.as_ref()?;
        let spec = &self.pilots.get(&session)?.ship.spec;
        let effect = Effect {
            rules: Default::default(),
            burns_fuel: false,
            module_id: spec.ship_id,
            module_type: spec.type_id,
            effect_id: remote.effect_id,
            fx: remote.fx.clone(),
            duration_ms: remote.cycle_ms,
            capacitor_need: 0.0,
            repeat: i32::MAX,
            target_id: target,
            turret: None,
            action: Some(remote.action.clone()),
            on: None,
            off: None,
            on_changes: Vec::new(),
            off_changes: Vec::new(),
        };
        Some((effect, remote.lasting_ms))
    }

    /// A cycle of a remote effect: the cycle that has ended lands its end first; the target's HUD is told as the effect begins on it (refreshed while it goes on, and
    /// the previous target let go of when it changes); then the cycle lands now (a web, a jam) or at the end of the cycle (a repair).
    pub(super) fn remote_cycle(
        &mut self,
        session: SessionId,
        effect: &Effect,
        lasting_ms: f64,
        now: i64,
    ) -> Vec<Out> {
        let mut out = Vec::new();
        if let Some(ended) = self.drones.get_mut(&session).and_then(|ai| ai.ending.take()) {
            out.extend(self.land_remote(session, &ended, now));
        }
        if effect.offensive()
            && let Some(controller) = self.controller_of_drone(session).filter(|id| *id != 0) {
            out.extend(self.clear_tether_policy(controller, now));
        }
        let landing = Effect { duration_ms: lasting_ms, ..effect.clone() };
        let Some((_, _, when)) = landing.remote() else { return out };
        let same = self
            .drones
            .get(&session)
            .and_then(|ai| ai.landing.as_ref())
            .is_some_and(|current| current.target_id == landing.target_id);
        if !same {
            out.extend(self.stop_remote(session, now));
        }
        out.extend(self.cycle_fx(session, effect, now));
        out.extend(self.jam_told(session, &landing, now, if same { Jam::Refresh } else { Jam::Start }));
        match when {
            Applies::CycleStart => {
                out.extend(self.land_remote(session, &landing, now));
                if let Some(ai) = self.drones.get_mut(&session) {
                    ai.landing = Some(landing);
                }
            }
            Applies::CycleEnd => {
                if let Some(ai) = self.drones.get_mut(&session) {
                    ai.landing = Some(landing.clone());
                    ai.ending = Some(landing);
                }
            }
        }
        out
    }

    /// What a cycle lands on its target now: the effect projected at the ship it is aimed at, weakened by distance and by the target's resistance (the modules' `project`).
    pub(super) fn land_remote(&mut self, session: SessionId, landing: &Effect, now: i64) -> Vec<Out> {
        let Some((what, reach, _)) = landing.remote() else { return Vec::new() };
        self.project(session, landing, what, reach, now)
    }

    /// The drone stops its remote effect: a repair due at the end of its cycle lands first, then what it holds on its target is lifted and the target's HUD is told
    /// it ended.
    pub(crate) fn stop_remote(&mut self, session: SessionId, now: i64) -> Vec<Out> {
        let mut out = Vec::new();
        if let Some(ended) = self.drones.get_mut(&session).and_then(|ai| ai.ending.take()) {
            out.extend(self.land_remote(session, &ended, now));
        }
        let Some(landing) = self.drones.get_mut(&session).and_then(|ai| ai.landing.take()) else {
            return out;
        };
        let Some(source) = self.pilots.get(&session).map(|pilot| pilot.ship.spec.ship_id) else {
            return out;
        };
        out.extend(self.jam_told(session, &landing, now, Jam::End));
        out.extend(self.release(source, landing.module_id, landing.target_id, now));
        out
    }

    pub(super) fn activity_of(&self, session: SessionId) -> i32 {
        self.pilots
            .get(&session)
            .and_then(|pilot| pilot.ship.npc.as_ref())
            .and_then(|npc| npc.drone.as_ref())
            .map_or(IDLE, |link| link.activity)
    }

    /// The effect a drone's weapon runs at a target: the weapon the host gave it, each cycle one shot.
    pub(super) fn drone_effect(&self, session: SessionId, target: i64) -> Option<Effect> {
        let weapon = self.pilots.get(&session)?.ship.npc.as_ref()?.combat.weapons.first()?.clone();
        // a drone's shot is its controller's damage: the controller's drone damage multiplier (its ship's, 1 for none) scales it (protocol 47)
        let multiplier = self
            .controller_of_drone(session)
            .and_then(|controller| self.pilots.get(&controller))
            .map_or(1.0, |pilot| pilot.ship.spec.drone_damage_multiplier);
        let mut turret = weapon.turret;
        turret.raw_damage = turret.raw_damage.map(|damage| damage * multiplier);
        Some(Effect {
            rules: Default::default(),
            burns_fuel: false,
            module_id: weapon.module_id,
            module_type: turret.weapon_type_id,
            effect_id: weapon.effect_id,
            fx: turret.fx.clone(),
            duration_ms: weapon.cycle_ms,
            capacitor_need: 0.0,
            repeat: i32::MAX,
            target_id: target,
            turret: Some(turret),
            action: None,
            on: None,
            off: None,
            on_changes: Vec::new(),
            off_changes: Vec::new(),
        })
    }
}
