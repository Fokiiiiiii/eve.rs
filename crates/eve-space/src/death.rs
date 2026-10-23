//! Ships that are destroyed. A ship whose hull is gone blows up: whoever has it in view is told to play the explosion where it was and to let go of
//! its ball (one stamp), it stops being a pilot of the scene (its locks are let go, its tree is forgotten), and it leaves a wreck (`crate::wreck`),
//! which those who can see the place are sent. What is done to the pilot of a player's ship (a capsule to fly, the ship's items, the wreck's
//! contents) is the host's: it is told with [`Out::Destroyed`].
use eve_destiny::Vec3;
use eve_marshal::Value;

use crate::modules::Modules;
use crate::npc::is_npc_session;
use crate::payload;
use crate::scene::{Out, Pilot, Scene, SceneError, SessionId, TICKS_PER_SECOND, Widow};
use crate::ship::{Course, Ship, ShipSpec};
use crate::wire::{id, int};
use crate::wreck::{LootRights, WRECK_LIFETIME, Wreck};

/// The first id the scene gives a wreck of its own making (the reference's transient items are numbered from about the same place; the ids of
/// the host's items are far below it).
pub const TRANSIENT_ID_BASE: i64 = 4_300_000_000_000_000;

/// The group of the capsules (`Capsule`): the ships a pilot is left in when its ship is destroyed.
pub const CAPSULE_GROUP: i32 = 29;

/// Who may take what a destroyed ship leaves: the pilot and the corporation, the alliance and the faction they are in.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct Credit {
    pub character_id: i64,
    pub corporation_id: i64,
    pub alliance_id: i64,
    pub war_faction_id: i64,
}

impl Scene {
    /// The ship of `session` is destroyed at `now` by the ship of `killer` (`None` when nobody did it): those who see it are told, it leaves the
    /// scene, and an NPC's wreck comes into it. The host is told of the destruction with [`Out::Destroyed`].
    pub fn destroy(&mut self, session: SessionId, killer: Option<SessionId>, now: i64) -> Vec<Out> {
        let credit = self.credit_of(killer);
        let killer_ship =
            killer.and_then(|session| self.pilots.get(&session)).map(|pilot| pilot.ship.spec.ship_id);
        self.destroy_credited(session, credit, killer_ship, now)
    }

    /// As [`Scene::destroy`], the wreck to be looted by `credit` (the host says who: the pilot who asked for the destruction).
    pub fn destroy_credited(
        &mut self,
        session: SessionId,
        credit: Credit,
        killer_ship: Option<i64>,
        now: i64,
    ) -> Vec<Out> {
        if !self.pilots.contains_key(&session) {
            return Vec::new();
        }
        self.settle_pilot(session, now);
        let mut told_of_it = self.observers_of(session);
        // a pilot is told of its own ship's explosion, but not of its capsule's: it is already on its way to the station of its clone (the reference's
        // `destroySessionCapsuleToHomeStation` takes the session out of the ballpark before the capsule goes)
        let pilot = &self.pilots[&session];
        if pilot.bound && !is_npc_session(session) && pilot.ship.spec.group_id != CAPSULE_GROUP {
            told_of_it.insert(0, session);
        }
        let Some(pilot) = self.pilots.remove(&session) else { return Vec::new() };
        let unpiloted = self.unpiloted.values().any(|candidate| *candidate == session);
        if self.abyssal.is_some() && !is_npc_session(session) && !unpiloted {
            self.abyssal_participant_deaths.insert(pilot.ship.spec.ship_id, session);
        }
        if self.abyssal.is_none() && !is_npc_session(session) && !unpiloted {
            // the client keeps its ballpark: what it is given next (a capsule) is added to it
            self.widowed.insert(
                session,
                Widow {
                    bound: pilot.bound,
                    sees: pilot.sees.clone(),
                    sees_wrecks: pilot.sees_wrecks.clone(),
                    sees_abyssal_traces: pilot.sees_abyssal_traces.clone(),
                    sees_asteroids: pilot.sees_asteroids.clone(),
                },
            );
        }
        if let Some(brains) = self.brains.as_mut() {
            brains.forget(session);
        }
        for other in self.pilots.values_mut() {
            other.sees.remove(&session);
        }
        let ship = &pilot.ship;
        let ship_id = ship.spec.ship_id;
        self.abyssal_boundary_exposure_ms.remove(&ship_id);
        if self.abyssal.is_some()
            && let Some(receipt) = self.abyssal_npc_spawn_receipts.get_mut(&ship_id)
        {
            receipt.destroyed = true;
        }
        self.sim_damageable_entities.remove(&ship_id);
        self.unpiloted.remove(&ship_id);
        let mut out = self.drop_targeting(ship_id, &pilot.targeting, now);
        self.destroyed.push(ship_id);
        let stamp = self.next_stamp(now);
        for watcher in told_of_it {
            out.push(Self::update(watcher, payload::destruction(stamp, ship_id)));
        }
        // a drone that is destroyed is gone: its controller is told what the reference tells (idle, nobody's) and nothing is left of it
        let drone = ship.npc.as_ref().and_then(|npc| npc.drone.as_ref());
        // a squadron's controller is told nothing of drones: its destruction is the host's to tell the pilot
        if let Some(link) = drone.filter(|link| link.kind == crate::ship::CompanionKind::Drone) {
            out.push(Out::Notify {
                session: link.controller,
                name: "OnDroneStateChange",
                id_type: "charid",
                args: vec![
                    id(ship_id),
                    int(0),
                    int(0),
                    int(0),
                    int(i64::from(ship.spec.type_id)),
                    int(0),
                    Value::None,
                ],
            });
            out.push(Out::Notify {
                session: link.controller,
                name: "OnDroneActivityChange",
                id_type: "charid",
                args: vec![id(ship_id), Value::None, Value::None],
            });
        }
        // an NPC that says what it leaves leaves that; else the wreck of the ship's type, as the reference's rules have it
        let kind = if drone.is_some() || self.abyssal_reward_caches.contains_key(&ship_id) {
            None
        } else {
            ship.npc.as_ref().and_then(|npc| npc.wreck.clone()).or_else(|| {
                let catalog = self.wreck_catalog.as_ref()?;
                catalog.resolve_ship_wreck_type(i64::from(ship.spec.type_id))
            })
        };
        let wreck_kind = kind.clone();
        // the scene makes the wreck of an NPC unless the host asks to make it (an item with what the NPC leaves in it); a player's is the host's to make, and it is told the kind
        let scene_makes_it = ship.npc.as_ref().is_some_and(|npc| !npc.host_wreck);
        let wreck = kind.clone().filter(|_| scene_makes_it).map(|kind| {
            self.wreck_sequence += 1;
            Wreck {
                id: TRANSIENT_ID_BASE + self.wreck_sequence as i64,
                group_id: crate::wreck::WRECK_GROUP,
                kind,
                position: ship.position,
                direction: ship.direction,
                radius: ship.spec.radius,
                owner_id: credit.character_id,
                corporation_id: credit.corporation_id,
                alliance_id: credit.alliance_id,
                war_faction_id: credit.war_faction_id,
                rights: LootRights {
                    character_id: credit.character_id,
                    corporation_id: credit.corporation_id,
                },
                launcher_id: ship_id,
                is_empty: true,
                rights_known: false,
                pull: None,
                velocity: eve_destiny::Vec3::ZERO,
                expires_at: now + WRECK_LIFETIME,
            }
        });
        let wreck_id = wreck.as_ref().map(|wreck| wreck.id);
        if let Some(wreck) = wreck {
            // EveJS sends a replacement wreck as a non-fresh follow-on, one Destiny stamp after RemoveBalls.
            // Advance only the presentation clock; the wreck's scene state still starts at now.
            out.extend(self.place_wreck_at(wreck, now, now.saturating_add(TICKS_PER_SECOND)));
        }
        out.push(Out::Destroyed {
            session,
            ship_id,
            wreck: wreck_id,
            wreck_kind,
            position: ship.position,
            direction: ship.direction,
            radius: ship.spec.radius,
            killer_ship,
            ship_type_id: ship.spec.type_id,
            corporation_id: ship.spec.corporation_id,
        });
        if let Some(receipt) = self.abyssal_reward_caches.get_mut(&ship_id) {
            receipt.destruction = out.last().cloned();
        }
        out
    }

    /// A session whose ship was destroyed is given a capsule; a transition may instead rebind a ship left in space.
    pub fn respawn(
        &mut self,
        session: SessionId,
        spec: ShipSpec,
        position: Vec3,
        direction: Vec3,
        now: i64,
    ) -> Result<Vec<Out>, SceneError> {
        if self.pilots.contains_key(&session) {
            return Err(SceneError::AlreadyInSpace(session));
        }
        let Some(widow) = self.widowed.remove(&session) else {
            return Err(SceneError::NotDestroyed(session));
        };

        if let Some(unpiloted_session) = self.unpiloted.remove(&spec.ship_id)
            && let Some(mut pilot) = self.pilots.remove(&unpiloted_session)
        {
            let ship_id = spec.ship_id;
            // Keep the hull's current position, velocity, and damage while applying

            pilot.ship.spec = spec;
            pilot.bound = widow.bound;
            pilot.entered = self.next_entry();
            pilot.sees = widow.sees;
            pilot.sees_wrecks = widow.sees_wrecks;
            pilot.sees_abyssal_traces = widow.sees_abyssal_traces;
            pilot.sees_asteroids = widow.sees_asteroids;
            self.pilots.insert(session, pilot);
            let mut out = self.announce_departure(unpiloted_session, ship_id, now);
            if widow.bound
                && let Some(actions) = self.introduction(session, now)
            {
                out.push(Self::update(session, actions));
            }
            out.extend(self.announce_arrival(session, now));
            return Ok(out);
        }

        let modules = Modules::with_charge(
            spec.capacitor_capacity,
            spec.capacitor_recharge_s,
            spec.condition.charge,
            now,
        );
        let ship = Ship::new(spec, position, Vec3::new(0.0, 0.0, 0.0), direction, 0.0, Course::Stop, now);
        let entered = self.next_entry();
        let mut pilot = Pilot::new(ship, modules, entered, now, 0);
        pilot.bound = widow.bound;
        pilot.sees = widow.sees;
        pilot.sees_wrecks = widow.sees_wrecks;
        pilot.sees_abyssal_traces = widow.sees_abyssal_traces;
        pilot.sees_asteroids = widow.sees_asteroids;
        self.pilots.insert(session, pilot);
        let mut out = Vec::new();
        if widow.bound
            && let Some(actions) = self.introduction(session, now)
        {
            out.push(Self::update(session, actions));
        }
        out.extend(self.announce_arrival(session, now));
        Ok(out)
    }

    /// Who a wreck made by `killer`'s ship is credited to: the killer's pilot and corporation (nobody when nobody did it).
    fn credit_of(&self, killer: Option<SessionId>) -> Credit {
        let Some(pilot) = killer.and_then(|session| self.pilots.get(&session)) else {
            return Credit::default();
        };
        let spec = &pilot.ship.spec;
        Credit {
            character_id: if spec.character_id > 0 { spec.character_id } else { spec.owner_id },
            corporation_id: i64::from(spec.corporation_id),
            alliance_id: i64::from(spec.alliance_id),
            war_faction_id: i64::from(spec.war_faction_id),
        }
    }
}

#[cfg(test)]
mod tests;
