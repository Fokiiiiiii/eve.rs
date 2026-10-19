use super::*;
use crate::missile_entity::AUTO_TARGET_ID;

impl Scene {
    /// Whether the weapon of a running effect still has what it shoots at: the target is in the scene and the ship has it locked.
    pub(crate) fn turret_target_ok(&self, session: SessionId, effect: &Effect) -> bool {
        // an F.O.F. missile locks nothing: its launcher picks the target when it fires (`missile_entity::AUTO_TARGET_ID`)
        if effect.target_id == AUTO_TARGET_ID {
            return effect.turret.as_ref().is_some_and(|turret| turret.missile.is_some());
        }
        self.target_in_scene(effect.target_id) && self.locked_by(session).contains(&effect.target_id)
    }

    /// Whether a target is in the scene a weapon can shoot at: a ship, or a structure the host put in space (its vitals are there).
    pub(crate) fn target_in_scene(&self, target: i64) -> bool {
        self.session_of(target).is_some() || self.struck_structure(target)
    }

    /// A structure of this system a weapon can hit: in space, with the vitals the host gave it.
    pub(crate) fn struck_structure(&self, target: i64) -> bool {
        self.structure_health.contains_key(&target) && self.structure_index(target).is_some()
    }

    /// The kinematics a shot is rolled against: a ship's, or a structure body's (it does not move, and its size is its signature).
    pub(crate) fn target_kinematics(&self, target: i64) -> Option<Kinematics> {
        if let Some(session) = self.session_of(target) {
            return self.kinematics_of(session);
        }
        let body = self.structure_body(target)?;
        let p = body.position;
        Some(Kinematics {
            position: [p.x, p.y, p.z],
            velocity: [0.0; 3],
            radius: body.radius,
            signature_radius: body.radius,
        })
    }

    /// Whether a precursor weapon that builds its damage up (a spool) has its target past its optimal range (with the shooter's range disruption
    pub(crate) fn spool_target_out_of_optimal(&self, session: SessionId, effect: &Effect) -> bool {
        let Some(turret) = effect.turret.as_ref() else { return false };
        if turret.spool.is_none() {
            return false;
        }
        let (Some(attacker), Some(target)) = (
            self.kinematics_of(session),
            self.session_of(effect.target_id).and_then(|target| self.kinematics_of(target)),
        ) else {
            return false;
        };
        let (range, _, _) = self.weapon_disruption(session, turret);
        surface_distance(&attacker, &target) > turret.optimal_range.max(0.0) * range + 1e-6
    }

    /// What the shooter's disruption does to this weapon (range, falloff, tracking): a tracking disruptor or computer reaches only a weapon that
    /// requires Gunnery; any other weapon is shot with its own numbers.
    pub(crate) fn weapon_disruption(&self, session: SessionId, turret: &Turret) -> (f64, f64, f64) {
        if turret.requires_gunnery {
            self.pilots[&session].incoming.weapon_factors()
        } else {
            (1.0, 1.0, 1.0)
        }
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

    /// The structures of the system within a blast's range of a ship (surface to surface, as the ships are reached), in id order.
    pub(crate) fn structures_in_range(&self, session: SessionId, range: f64) -> Vec<i64> {
        let Some(pilot) = self.pilots.get(&session) else { return Vec::new() };
        let p = pilot.ship.position;
        self.structures_within([p.x, p.y, p.z], range + pilot.ship.spec.radius)
    }

    /// The structures of the system whose surface is within `reach` of a point, in id order (a bomb's blast reaches them from where it goes off).
    pub(crate) fn structures_within(&self, centre: [f64; 3], reach: f64) -> Vec<i64> {
        let mut ids: Vec<i64> = self
            .structure_health
            .keys()
            .copied()
            .filter(|id| {
                let Some(body) = self.structure_body(*id) else { return false };
                let (dx, dy, dz) =
                    (centre[0] - body.position.x, centre[1] - body.position.y, centre[2] - body.position.z);
                ((dx * dx + dy * dy + dz * dz).sqrt() - body.radius).max(0.0) <= reach
            })
            .collect();
        ids.sort_unstable();
        ids
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
        // the structures in its range take the blast in their layers (the host hears of an emptied layer); a structure is not a pilot, so no one is aggressed
        for structure in self.structures_in_range(session, range) {
            out.extend(self.damage_structure(structure, damage, at));
        }
        out
    }

    /// One ship hit by a controlled structure's bomb (ST-8a): the attacker is the structure, and its messages go to the controller that runs it (a structure
    /// has no pilot record of its own). The rest is as `damage_ship`.
    pub(crate) fn damage_ship_from_structure(
        &mut self,
        controller: SessionId,
        structure_id: i64,
        module_type: i32,
        target: (SessionId, i64),
        damage: Damage,
        at: i64,
    ) -> Vec<Out> {
        let (target_session, target_id) = target;
        if self.is_tethered_session(target_session, at) {
            return Vec::new();
        }
        let owner = self.combat_owner(controller);
        let mut out = Vec::new();
        let shot = Shot { hit: true, wrecking: false, quality: 1.0, chance_to_hit: 1.0, roll: 0.0, damage };
        let (told, hit) = self.damage(target_id, damage, at);
        out.extend(told);
        let applied = hit.map_or(0.0, |hit| hit.taken.shield + hit.taken.armor + hit.taken.structure);
        let destroyed = hit.is_some_and(|hit| hit.destroyed);
        let target_type_id = self.pilots.get(&target_session).map_or(0, |pilot| pilot.ship.spec.type_id);
        self.aggressions.push(Aggression {
            attacker_ship: structure_id,
            target_ship: target_id,
            module_type_id: module_type,
            source_owner_id: owner,
            target_type_id,
        });
        let message = |for_the_attacker| {
            damage_message(for_the_attacker, structure_id, target_id, module_type, applied, &shot, owner)
        };
        for (who, mine) in [(controller, true), (target_session, false)] {
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
            out.extend(self.destroy(target_session, Some(controller), at));
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
        if self.is_tethered_session(target_session, at) {
            return Vec::new();
        }
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
        let Some(target_session) = self.session_of(effect.target_id) else {
            return self.fire_at_structure(session, effect, at);
        };
        let (Some(attacker), Some(target)) =
            (self.kinematics_of(session), self.kinematics_of(target_session))
        else {
            return Vec::new();
        };
        let attacker_id = self.pilots[&session].ship.spec.ship_id;
        let reporter = self.controller_of_drone(session).unwrap_or(session);
        let aggressor_ship = if reporter == session { attacker_id } else { self.ship_id_of(reporter) };
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
        let (range, falloff, tracking) = self.weapon_disruption(session, turret);
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
        // A Vorton explosion does not roll (EVE.js resolveVortonShot); every other shot is rolled to hit.
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
        let structure_arc = self.controlled_structure_id(session).is_some()
            && self
                .controlled_structure_id(session)
                .is_some_and(|structure_id| self.control_lease_generation(session, structure_id).is_some())
            && turret.aoe.is_some()
            && turret.arc.is_some();

        // correlated Host authorization on each link before applying its secondary damage.
        let arc_victims = match (turret.aoe, turret.arc, structure_arc) {
            (Some(_), Some(arc), false) if shot.hit => {
                self.arc_victims(session, target_session, &target, arc)
            }
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
        if destroyed {
            out.extend(self.destroy(target_session, Some(reporter), at));
        }
        if structure_arc
            && shot.hit
            && let (Some(aoe), Some(arc)) = (turret.aoe, turret.arc)
        {
            let links_remaining = (arc.targets - 1).max(0) as u32;
            if links_remaining > 0 {
                let struck_ids = vec![attacker_id, effect.target_id];
                let candidate_ids = self.vorton_arc_candidate_ids(at, &target, arc.range, &struck_ids);
                out.extend(self.request_structure_damage_candidates(
                    session,
                    effect.module_id,
                    effect.effect_id,
                    0,
                    candidate_ids,
                    crate::modules::PendingStructureDamageOperation::VortonArcHop {
                        anchor: target,
                        arc,
                        aoe,
                        raw_damage: turret.raw_damage,
                        shooter,
                        links_remaining,
                        struck_ids,
                    },
                    at,
                ));
            }
        }
        // Each ship a normal pilot's arc reaches takes the same explosion as the primary target, without a roll.
        if !structure_arc && let Some(aoe) = turret.aoe {
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

    /// A turret's shot at a structure of the system (a FOB, an Upwell structure): rolled against the structure's body as against a ship, and a hit takes the
    /// structure's layers (`SimStructureDamaged` tells the host when a layer is emptied). shortcut: no spool, range disruption or area effect applies to a
    /// structure yet; the shot is the weapon's own roll.
    fn fire_at_structure(&mut self, session: SessionId, effect: &Effect, at: i64) -> Vec<Out> {
        let Some(turret) = effect.turret.as_ref() else { return Vec::new() };
        let (Some(attacker), Some(target)) =
            (self.kinematics_of(session), self.target_kinematics(effect.target_id))
        else {
            return Vec::new();
        };
        let roll = self.rng.random();
        let shot = resolve_shot(turret, &attacker, &target, roll);
        if !shot.hit {
            return Vec::new();
        }
        self.damage_structure(effect.target_id, shot.damage, at)
    }

    /// A shot lands on the ship of `target`: its damage is taken (whoever sees it is told its damage state), the shot is an offensive act (the crimewatch
    /// and the NPCs hear of it), and the pilots who see it are told the shot. Returns what was told, and whether the shot took the last of the hull: the
    /// caller destroys the ship once the shot is told.
    pub(crate) fn land_shot(
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

    /// Fresh Vorton arc candidates from the current scene, excluding ships already struck in this chain.
    pub(crate) fn vorton_arc_candidate_ids(
        &self,
        now: i64,
        anchor: &Kinematics,
        range: f64,
        struck_ids: &[i64],
    ) -> Vec<i64> {
        if !range.is_finite() || range <= 0.0 {
            return Vec::new();
        }
        self.damageable_entity_ids(now, |target_id, position, radius| {
            if struck_ids.contains(&target_id) || self.missiles.contains_key(&target_id) {
                return false;
            }
            let signature_radius = if let Some(session) = self.session_of(target_id) {
                self.pilots.get(&session).map_or(radius, |pilot| pilot.ship.spec.signature_radius)
            } else if self.structure_index(target_id).is_some() {
                radius
            } else {
                return false;
            };
            let candidate = Kinematics {
                position: [position.x, position.y, position.z],
                velocity: [0.0; 3],
                radius,
                signature_radius,
            };
            surface_distance(anchor, &candidate) <= range + 1e-6
        })
    }

    /// Orders the currently authorized Vorton targets by their surface distance to the arc anchor, then by ID.
    pub(crate) fn vorton_arc_target_order(&self, anchor: &Kinematics, target_ids: &[i64]) -> Vec<i64> {
        let mut ordered: Vec<(f64, i64)> = target_ids
            .iter()
            .copied()
            .filter_map(|target_id| {
                let target = if let Some(session) = self.session_of(target_id) {
                    self.kinematics_of(session)?
                } else if self.structure_index(target_id).is_some() {
                    let body = self.system().body(target_id)?;
                    Kinematics {
                        position: [body.position.x, body.position.y, body.position.z],
                        velocity: [0.0; 3],
                        radius: body.radius,
                        signature_radius: body.radius,
                    }
                } else {
                    return None;
                };
                Some((surface_distance(anchor, &target), target_id))
            })
            .filter(|(distance, _)| distance.is_finite())
            .collect();
        ordered.sort_by(|left, right| left.0.total_cmp(&right.0).then(left.1.cmp(&right.1)));
        ordered.into_iter().map(|(_, target_id)| target_id).collect()
    }

    /// Lands one authorized Vorton arc link through the ordinary per-type resistance path.
    pub(crate) fn damage_vorton_arc_target(
        &mut self,
        shooter: Shooter,
        aoe: Aoe,
        raw_damage: Damage,
        target_id: i64,
        now: i64,
    ) -> Vec<Out> {
        if let Some(target_session) = self.session_of(target_id) {
            if self.pilots.get(&target_session).is_some_and(|pilot| pilot.protected_until > now)
                || self.is_tethered_session(target_session, now)
            {
                return Vec::new();
            }
            let Some(target) = self.kinematics_of(target_session) else { return Vec::new() };
            let struck = explosion(&aoe, raw_damage, &target);
            let (mut out, destroyed) = self.land_shot(shooter, target_session, &struck, now);
            if destroyed {
                out.extend(self.destroy(target_session, Some(shooter.reporter), now));
            }
            return out;
        }
        if self.structure_index(target_id).is_some() {
            let Some(body) = self.system().body(target_id) else { return Vec::new() };
            let target = Kinematics {
                position: [body.position.x, body.position.y, body.position.z],
                velocity: [0.0; 3],
                radius: body.radius,
                signature_radius: body.radius,
            };
            let struck = explosion(&aoe, raw_damage, &target);
            return if struck.hit {
                self.damage_structure(target_id, struck.damage, now)
            } else {
                Vec::new()
            };
        }
        Vec::new()
    }
}
