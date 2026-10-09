use super::*;
use crate::missile_entity::AUTO_TARGET_ID;

impl Scene {
    /// Whether the weapon of a running effect still has what it shoots at: the target is in the scene and the ship has it locked.
    pub(crate) fn turret_target_ok(&self, session: SessionId, effect: &Effect) -> bool {
        // an F.O.F. missile locks nothing: its launcher picks the target when it fires (`missile_entity::AUTO_TARGET_ID`)
        if effect.target_id == AUTO_TARGET_ID {
            return effect.turret.as_ref().is_some_and(|turret| turret.missile.is_some());
        }
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
    pub(crate) fn damage_ship(
        &mut self,
        session: SessionId,
        module_type: i32,
        target: (SessionId, i64),
        damage: Damage,
        at: i64,
    ) -> Vec<Out> {
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
    pub(super) fn land_shot(
        &mut self,
        shooter: Shooter,
        target: SessionId,
        shot: &Shot,
        at: i64,
    ) -> (Vec<Out>, bool) {
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
    pub(super) fn arc_victims(
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
