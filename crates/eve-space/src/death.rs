//! Ships that are destroyed. A ship whose hull is gone blows up: whoever has it in view is told to play the explosion where it was and to let go of
//! its ball (one stamp), it stops being a pilot of the scene (its locks are let go, its tree is forgotten), and it leaves a wreck (`crate::wreck`),
//! which those who can see the place are sent. What is done to the pilot of a player's ship (a capsule to fly, the ship's items, the wreck's
//! contents) is the host's: it is told with [`Out::Destroyed`].

use eve_destiny::Vec3;
use eve_marshal::Value;

use crate::modules::Modules;
use crate::npc::is_npc_session;
use crate::payload;
use crate::scene::{Out, Pilot, Scene, SceneError, SessionId, Widow};
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
        if !is_npc_session(session) && !unpiloted {
            // the client keeps its ballpark: what it is given next (a capsule) is added to it
            self.widowed.insert(
                session,
                Widow {
                    bound: pilot.bound,
                    sees: pilot.sees.clone(),
                    sees_wrecks: pilot.sees_wrecks.clone(),
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
        self.unpiloted.remove(&ship_id);
        let mut out = self.drop_targeting(ship_id, &pilot.targeting, now);
        self.destroyed.push(ship_id);
        let stamp = self.next_stamp(now);
        for watcher in told_of_it {
            out.push(Self::update(watcher, payload::destruction(stamp, ship_id)));
        }
        // a drone that is destroyed is gone: its controller is told what the reference tells (idle, nobody's) and nothing is left of it
        let drone = ship.npc.as_ref().and_then(|npc| npc.drone.as_ref());
        if let Some(link) = drone {
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
        let kind = if drone.is_some() {
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
                rights_known: false, pull: None, velocity: eve_destiny::Vec3::ZERO,
                expires_at: now + WRECK_LIFETIME,
            }
        });
        let wreck_id = wreck.as_ref().map(|wreck| wreck.id);
        if let Some(wreck) = wreck {
            out.extend(self.place_wreck(wreck, now));
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
        });
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
            // the new pilot's ship specification and ownership.
            pilot.ship.spec = spec;
            pilot.bound = widow.bound;
            pilot.entered = self.next_entry();
            pilot.sees = widow.sees;
            pilot.sees_wrecks = widow.sees_wrecks;
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
mod tests {
    use eve_marshal::Value;

    use super::*;
    use crate::brains::rat_tests::{
        LATER, PLAYER, RAT_SHIP, SECOND, actions_named, notified, place_near, rat_identity, rat_spec, run,
        scene_with_a_player,
    };
    use crate::npc::is_npc_session;
    use crate::scene::Command;
    use crate::scene::tests::{NOON, STATION, spec};
    use crate::ship::NpcIdentity;
    use crate::wreck::{WRECK_LIFETIME, WreckKind};

    const PLAYER_SHIP: i64 = 9_988_400_001_939;

    fn frigate_wreck() -> WreckKind {
        WreckKind { type_id: 26_506, name: "Caldari Frigate Wreck".to_string(), graphic_id: Some(3117) }
    }

    /// A player and an NPC (8 km off, at rest, leaving a wreck when it dies), the NPC's session.
    fn scene_with_a_mortal_rat() -> (Scene, SessionId) {
        let mut scene = scene_with_a_player();
        let place = place_near(&mut scene, 8_000.0);
        let identity = NpcIdentity { wreck: Some(frigate_wreck()), ..rat_identity(0.0) };
        let (rat, _) = scene.spawn_npc(rat_spec(), identity, place, LATER);
        (scene, rat)
    }

    /// The names of the destiny actions a player was told, with their stamps, in order.
    fn told_actions(told: &[Out]) -> Vec<(i64, String)> {
        let mut found = Vec::new();
        for args in notified(told, "DoDestinyUpdate") {
            let Some(Value::List(actions)) = args.first() else { continue };
            for action in actions {
                let Value::Tuple(pair) = action else { continue };
                let (Value::Int(stamp), Value::Tuple(inner)) = (&pair[0], &pair[1]) else { continue };
                let Value::Str(name) = &inner[0] else { continue };
                found.push((*stamp, name.to_string()));
            }
        }
        found
    }

    fn for_player(told: Vec<Out>) -> Vec<Out> {
        told.into_iter().filter(|out| out.session() == PLAYER).collect()
    }

    #[test]
    fn a_destroyed_ship_is_taken_from_the_scene_and_those_who_saw_it_are_told_to_play_the_explosion_and_let_go_of_the_ball_in_one_stamp()
     {
        let (mut scene, rat) = scene_with_a_mortal_rat();
        let told = for_player(scene.destroy(rat, None, LATER + SECOND));
        assert!(!scene.contains(rat));
        assert!(scene.pilots[&PLAYER].sees.is_empty(), "the pilot no longer has it in view");
        let actions = told_actions(&told);
        assert_eq!(actions[0].1, "TerminalPlayDestructionEffect");
        assert_eq!(actions[1].1, "RemoveBalls");
        assert_eq!(actions[0].0, actions[1].0, "one stamp");
        // the arguments are the reference's: the ball and the explosion (3), then the ball alone
        let Some(Out::Notify { args, .. }) = told.first() else { panic!("{told:?}") };
        let Value::List(list) = &args[0] else { panic!() };
        let arguments = |index: usize| {
            let Value::Tuple(pair) = &list[index] else { panic!() };
            let Value::Tuple(inner) = &pair[1] else { panic!() };
            inner[1].clone()
        };
        assert_eq!(arguments(0), Value::Tuple(vec![Value::Long(RAT_SHIP), Value::Int(3)]));
        assert_eq!(arguments(1), Value::Tuple(vec![Value::List(vec![Value::Long(RAT_SHIP)])]));
    }

    #[test]
    fn the_wreck_is_sent_after_the_explosion_with_the_slim_item_the_reference_gives_it_where_the_ship_was() {
        let (mut scene, rat) = scene_with_a_mortal_rat();
        let was_at = scene.ship(rat, LATER + SECOND).unwrap().position;
        let told = for_player(scene.destroy(rat, None, LATER + SECOND));
        let names: Vec<String> = told_actions(&told).into_iter().map(|(_, name)| name).collect();
        assert_eq!(names, ["TerminalPlayDestructionEffect", "RemoveBalls", "AddBalls2"]);
        let added = actions_named(&told, "AddBalls2");
        let Value::Tuple(payload) = &added[0][0] else { panic!("{added:?}") };
        let Value::List(extras) = &payload[1] else { panic!() };
        let Value::Tuple(extra) = &extras[0] else { panic!() };
        let Value::Dict(entries) = &extra[0] else { panic!("{extra:?}") };
        let entry = |key: &str| {
            entries.iter().find(|(k, _)| matches!(k, Value::Str(n) if n == key)).map(|(_, v)| v.clone())
        };
        assert_eq!(entry("typeID"), Some(Value::Int(26_506)));
        assert_eq!(entry("name"), Some(Value::Str("Caldari Frigate Wreck".into())));
        assert_eq!(entry("groupID"), Some(Value::Int(186)));
        assert_eq!(entry("launcherID"), Some(Value::Long(RAT_SHIP)));
        assert_eq!(scene.wrecks.len(), 1);
        let wreck = scene.wrecks.values().next().unwrap();
        assert_eq!((wreck.position, wreck.radius, wreck.id), (was_at, 47.0, TRANSIENT_ID_BASE + 1));
        assert_eq!(wreck.expires_at, LATER + SECOND + WRECK_LIFETIME);
    }

    #[test]
    fn an_npc_that_does_not_say_what_it_leaves_leaves_the_wreck_the_rules_give_its_type_and_one_that_does_leaves_that()
     {
        use crate::wreck_types::WreckCatalog;
        let catalog = std::sync::Arc::new(WreckCatalog::of([
            (601, "Ibis", "Frigate", 1, 312),
            (26_506, "Caldari Frigate Wreck", "Wreck", 0, 3117),
        ]));
        let mut scene = scene_with_a_player();
        scene.set_wreck_catalog(std::sync::Arc::clone(&catalog));
        let place = place_near(&mut scene, 8_000.0);
        let (rat, _) = scene.spawn_npc(rat_spec(), rat_identity(0.0), place, LATER);
        scene.destroy(rat, None, LATER);
        let wreck = scene.wrecks.values().next().unwrap();
        assert_eq!(
            (wreck.kind.type_id, wreck.kind.name.as_str(), wreck.kind.graphic_id),
            (26_506, "Caldari Frigate Wreck", Some(3117))
        );

        // a wreck an NPC names is the one it leaves; a player's ship leaves none of the scene's making (the host makes it)
        let mut scene = scene_with_a_player();
        scene.set_wreck_catalog(catalog);
        let place = place_near(&mut scene, 8_000.0);
        let named = WreckKind { type_id: 1, name: "Named".to_string(), graphic_id: None };
        let identity = NpcIdentity { wreck: Some(named.clone()), ..rat_identity(0.0) };
        let (rat, _) = scene.spawn_npc(rat_spec(), identity, place, LATER);
        scene.destroy(rat, None, LATER);
        assert_eq!(scene.wrecks.values().next().unwrap().kind, named);
        scene.destroy(PLAYER, None, LATER);
        assert_eq!(scene.wrecks.len(), 1);
    }

    fn capsule_spec() -> ShipSpec {
        ShipSpec {
            ship_id: 9_988_400_002_416,
            type_id: 670,
            group_id: 29,
            name: "Capsule".to_string(),
            ..spec()
        }
    }

    #[test]
    fn the_host_is_told_what_wreck_a_players_ship_leaves_and_the_scene_makes_none() {
        use crate::wreck_types::WreckCatalog;
        let mut scene = scene_with_a_player();
        scene.set_wreck_catalog(std::sync::Arc::new(WreckCatalog::of([
            (601, "Ibis", "Frigate", 1, 312),
            (26_506, "Caldari Frigate Wreck", "Wreck", 0, 3117),
        ])));
        let told = scene.destroy(PLAYER, None, LATER);
        assert!(scene.wrecks.is_empty());
        let Some(Out::Destroyed { wreck, wreck_kind, .. }) =
            told.iter().find(|out| matches!(out, Out::Destroyed { .. }))
        else {
            panic!("{told:?}")
        };
        assert_eq!((*wreck, wreck_kind.as_ref()), (None, Some(&frigate_wreck())));
    }

    /// The names of the destiny actions a session was told of, in order.
    fn action_names(told: &[Out], session: SessionId) -> Vec<String> {
        let told: Vec<Out> = told.iter().filter(|out| out.session() == session).cloned().collect();
        told_actions(&told).into_iter().map(|(_, name)| name).collect()
    }

    #[test]
    fn the_pilot_of_a_capsule_is_not_told_of_its_explosion_and_the_pilot_of_a_ship_is() {
        let mut scene = scene_with_a_player();
        let ship = scene.destroy(PLAYER, None, LATER);
        assert_eq!(told_actions(&for_player(ship)).len(), 2, "the explosion and the ball that goes");

        let mut scene = scene_with_a_player();
        let place = scene.ship(PLAYER, LATER).unwrap().position;
        scene.destroy(PLAYER, None, LATER);
        scene.respawn(PLAYER, capsule_spec(), place, Vec3::new(1.0, 0.0, 0.0), LATER + SECOND).unwrap();
        let capsule = scene.destroy(PLAYER, None, LATER + 2 * SECOND);
        assert_eq!(told_actions(&for_player(capsule.clone())), Vec::new());
        assert!(
            capsule.iter().any(|out| matches!(out, Out::Destroyed { ship_id: 9_988_400_002_416, .. })),
            "the host is told all the same"
        );
    }

    #[test]
    fn a_session_that_lost_its_ship_is_given_a_capsule_where_it_was_and_its_client_is_sent_the_ball() {
        let mut scene = scene_with_a_player();
        let place = scene.ship(PLAYER, LATER).unwrap().position;
        scene.destroy(PLAYER, None, LATER);
        assert!(!scene.contains(PLAYER));

        let told =
            scene.respawn(PLAYER, capsule_spec(), place, Vec3::new(1.0, 0.0, 0.0), LATER + SECOND).unwrap();
        assert!(scene.contains(PLAYER));
        assert_eq!(scene.ship(PLAYER, LATER + SECOND).unwrap().spec.ship_id, 9_988_400_002_416);
        assert_eq!(scene.ship(PLAYER, LATER + SECOND).unwrap().position, place);
        let names = action_names(&told, PLAYER);
        assert_eq!(names.first().map(String::as_str), Some("AddBalls2"), "{names:?}");
        assert!(scene.locked_by(PLAYER).is_empty());
        // it is the pilot's ship again: it answers to commands
        let answered = scene.command(PLAYER, Command::Stop, LATER + 2 * SECOND);
        assert!(
            !answered.is_empty() && !answered.iter().any(|out| matches!(out, Out::Refused { .. })),
            "{answered:?}"
        );
    }

    #[test]
    fn a_client_that_had_not_bound_the_ballpark_is_sent_nothing_of_the_capsule_and_is_given_it_when_it_binds()
    {
        let mut scene = Scene::new(crate::brains::rat_tests::system());
        scene.undock(PLAYER, spec(), STATION, NOON).unwrap();
        let place = scene.ship(PLAYER, NOON).unwrap().position;
        scene.destroy(PLAYER, None, LATER);
        let told =
            scene.respawn(PLAYER, capsule_spec(), place, Vec3::new(1.0, 0.0, 0.0), LATER + SECOND).unwrap();
        assert_eq!(told, Vec::new());
        let bound = scene.bind(PLAYER, LATER + 2 * SECOND);
        assert!(!bound.is_empty());
    }

    #[test]
    fn a_session_that_did_not_lose_its_ship_or_has_one_already_is_given_none() {
        let mut scene = scene_with_a_player();
        let place = scene.ship(PLAYER, LATER).unwrap().position;
        let direction = Vec3::new(1.0, 0.0, 0.0);
        assert_eq!(
            scene.respawn(PLAYER, capsule_spec(), place, direction, LATER),
            Err(SceneError::AlreadyInSpace(PLAYER))
        );
        scene.destroy(PLAYER, None, LATER);
        assert!(scene.respawn(PLAYER, capsule_spec(), place, direction, LATER).is_ok());
        assert_eq!(
            scene.respawn(PLAYER, capsule_spec(), place, direction, LATER),
            Err(SceneError::AlreadyInSpace(PLAYER))
        );
        assert_eq!(
            scene.respawn(PLAYER + 1, capsule_spec(), place, direction, LATER),
            Err(SceneError::NotDestroyed(PLAYER + 1))
        );
    }

    #[test]
    fn ejecting_and_boarding_keeps_both_ships_in_space() {
        let (mut scene, _) = scene_with_a_mortal_rat();
        let ship = scene.pilots[&PLAYER].ship.spec.clone();
        let capsule = capsule_spec();
        let place = Vec3::new(0.0, 0.0, 0.0);
        let direction = Vec3::new(1.0, 0.0, 0.0);

        scene.leave(PLAYER, crate::scene::Departure::Ejected, LATER).unwrap();
        scene.respawn(PLAYER, capsule.clone(), place, direction, LATER + SECOND).unwrap();
        assert_eq!(scene.pilots[&PLAYER].ship.spec.ship_id, capsule.ship_id);
        assert!(scene.pilots.values().any(|pilot| pilot.ship.spec.ship_id == ship.ship_id));

        scene.leave(PLAYER, crate::scene::Departure::Boarded, LATER + 2 * SECOND).unwrap();
        scene.respawn(PLAYER, ship.clone(), place, direction, LATER + 3 * SECOND).unwrap();
        assert_eq!(scene.pilots[&PLAYER].ship.spec.ship_id, ship.ship_id);
        assert!(scene.pilots.values().any(|pilot| pilot.ship.spec.ship_id == capsule.ship_id));
    }

    #[test]
    fn ejecting_and_boarding_keeps_hulls_in_space() {
        let (mut scene, _) = scene_with_a_mortal_rat();
        let ship = scene.pilots[&PLAYER].ship.spec.clone();
        let capsule = capsule_spec();
        let place = Vec3::new(0.0, 0.0, 0.0);
        let direction = Vec3::new(1.0, 0.0, 0.0);

        scene.leave(PLAYER, crate::scene::Departure::Ejected, LATER).unwrap();
        scene.respawn(PLAYER, capsule.clone(), place, direction, LATER + SECOND).unwrap();
        assert_eq!(scene.pilots[&PLAYER].ship.spec.ship_id, capsule.ship_id);
        assert!(scene.pilots.values().any(|pilot| pilot.ship.spec.ship_id == ship.ship_id));

        scene.leave(PLAYER, crate::scene::Departure::Boarded, LATER + 2 * SECOND).unwrap();
        scene.respawn(PLAYER, ship.clone(), place, direction, LATER + 3 * SECOND).unwrap();
        assert_eq!(scene.pilots[&PLAYER].ship.spec.ship_id, ship.ship_id);
        assert!(scene.pilots.values().any(|pilot| pilot.ship.spec.ship_id == capsule.ship_id));
    }

    #[test]
    fn a_capsule_that_comes_after_a_death_is_shown_to_those_who_watch_and_the_watchers_state_is_kept() {
        let (mut scene, rat) = scene_with_a_mortal_rat();
        scene.destroy(rat, None, LATER);
        let wreck = *scene.wrecks.keys().next().unwrap();
        scene.destroy(PLAYER, None, LATER);
        let place = Vec3::new(0.0, 0.0, 0.0);
        scene.respawn(PLAYER, capsule_spec(), place, Vec3::new(1.0, 0.0, 0.0), LATER + SECOND).unwrap();
        // it still knows the wreck it was sent: it is not sent it again
        assert!(scene.pilots[&PLAYER].sees_wrecks.contains(&wreck));
        assert!(scene.pilots[&PLAYER].bound);
    }

    #[test]
    fn a_ship_that_leaves_no_wreck_leaves_none() {
        let mut scene = scene_with_a_player();
        let place = place_near(&mut scene, 8_000.0);
        let (rat, _) = scene.spawn_npc(rat_spec(), rat_identity(0.0), place, LATER);
        let told = for_player(scene.destroy(rat, None, LATER));
        let names: Vec<String> = told_actions(&told).into_iter().map(|(_, name)| name).collect();
        assert_eq!(names, ["TerminalPlayDestructionEffect", "RemoveBalls"]);
        assert!(scene.wrecks.is_empty());
    }

    #[test]
    fn the_host_is_told_which_ship_took_the_last_of_an_npcs_hull_and_nothing_else_of_the_npc() {
        use crate::link::Message;
        let (mut scene, rat) = scene_with_a_mortal_rat();
        let told = crate::sim::Sim::messages(scene.destroy(rat, Some(PLAYER), LATER));
        let gone: Vec<(i64, i64, i64)> = told
            .iter()
            .filter_map(|m| match m {
                Message::Destroyed { ship_id, killer_ship_id, wreck_id, .. } => {
                    Some((*ship_id, *killer_ship_id, *wreck_id))
                }
                _ => None,
            })
            .collect();
        assert_eq!(gone, [(RAT_SHIP, PLAYER_SHIP, TRANSIENT_ID_BASE + 1)]);
        // what the scene says to the NPC itself (and to the player: the player's own client is told by the scene's other messages) is not the host's
        assert!(told.iter().all(|m| matches!(m, Message::Destroyed { .. })
            || !matches!(m, Message::Notify { session_id, .. } if crate::npc::is_npc_session(*session_id))));
    }

    #[test]
    fn the_host_is_told_of_a_destruction_with_the_place_and_what_the_scene_made_of_it() {
        let (mut scene, rat) = scene_with_a_mortal_rat();
        let was_at = scene.ship(rat, LATER).unwrap().position;
        let told = scene.destroy(rat, None, LATER);
        let destroyed: Vec<&Out> = told.iter().filter(|out| matches!(out, Out::Destroyed { .. })).collect();
        assert_eq!(destroyed.len(), 1);
        let Out::Destroyed { session, ship_id, wreck, position, radius, killer_ship, .. } = destroyed[0]
        else {
            unreachable!()
        };
        assert_eq!(
            (*session, *ship_id, *wreck, *position, *radius, *killer_ship),
            (rat, RAT_SHIP, Some(TRANSIENT_ID_BASE + 1), was_at, 47.0, None)
        );
        assert!(
            is_npc_session(*session),
            "the destruction is the NPC's, and the host is told of it (`Sim::messages`)"
        );
    }

    #[test]
    fn an_npc_whose_wreck_the_host_makes_leaves_none_in_the_scene_and_the_host_is_told_what_it_leaves() {
        let mut scene = scene_with_a_player();
        let place = place_near(&mut scene, 8_000.0);
        let identity = NpcIdentity { wreck: Some(frigate_wreck()), host_wreck: true, ..rat_identity(0.0) };
        let (rat, _) = scene.spawn_npc(rat_spec(), identity, place, LATER);
        let told = scene.destroy(rat, Some(PLAYER), LATER);
        // the explosion and the ship going, no wreck for those who watch: the host puts it in space (`PlaceWreck`) when it has made it
        let names: Vec<String> =
            told_actions(&for_player(told.clone())).into_iter().map(|(_, name)| name).collect();
        assert_eq!(names, ["TerminalPlayDestructionEffect", "RemoveBalls"]);
        assert!(scene.wrecks.is_empty());
        let Some(Out::Destroyed { wreck, wreck_kind, killer_ship, .. }) =
            told.iter().find(|out| matches!(out, Out::Destroyed { .. }))
        else {
            panic!("the host is told")
        };
        assert_eq!(
            (*wreck, wreck_kind.clone(), *killer_ship),
            (None, Some(frigate_wreck()), Some(PLAYER_SHIP))
        );
    }

    #[test]
    fn a_ship_may_follow_and_orbit_a_wreck_that_lies_in_the_scene_and_comes_to_it() {
        let (mut scene, rat) = scene_with_a_mortal_rat();
        scene.destroy(rat, None, LATER);
        let wreck = scene.wrecks.values().next().unwrap().clone();

        let out = scene.command(PLAYER, Command::FollowBall { target_id: wreck.id, range: 500.0 }, LATER);
        assert!(out.iter().any(|out| matches!(out, Out::Accepted { .. })), "{out:?}");
        let there = scene.ship(PLAYER, LATER + 300 * SECOND).unwrap().position;
        let away = ((there.x - wreck.position.x).powi(2)
            + (there.y - wreck.position.y).powi(2)
            + (there.z - wreck.position.z).powi(2))
        .sqrt();
        assert!(away < 1_000.0, "the pilot is {away} m from the wreck after five minutes");

        let out = scene.command(
            PLAYER,
            Command::Orbit { target_id: wreck.id, distance: 500.0 },
            LATER + 301 * SECOND,
        );
        assert!(out.iter().any(|out| matches!(out, Out::Accepted { .. })), "{out:?}");
        // what is not in the scene is still no target
        let out = scene.command(
            PLAYER,
            Command::Orbit { target_id: wreck.id + 1, distance: 500.0 },
            LATER + 302 * SECOND,
        );
        assert!(
            out.iter().any(|out| matches!(out, Out::Refused { reason: "UNKNOWN_TARGET", .. })),
            "{out:?}"
        );
    }

    #[test]
    fn a_pilot_who_had_the_ship_locked_lets_go_of_it_and_is_told() {
        let (mut scene, rat) = scene_with_a_mortal_rat();
        let mut now = LATER;
        scene.target_order(PLAYER, crate::targeting::Order::Lock(RAT_SHIP), now);
        for _ in 0..200 {
            now += SECOND / 4;
            scene.poll(now);
        }
        assert_eq!(scene.locked_by(PLAYER), [RAT_SHIP]);
        let told = for_player(scene.destroy(rat, None, now));
        assert_eq!(scene.locked_by(PLAYER), Vec::<i64>::new());
        let lost = notified(&told, "OnTarget");
        assert_eq!(lost.len(), 1, "{told:?}");
        assert_eq!(lost[0][0], Value::Str("lost".into()));
    }

    #[test]
    fn a_ship_that_is_not_there_is_not_destroyed_and_nobody_is_told() {
        let mut scene = scene_with_a_player();
        assert_eq!(scene.destroy(PLAYER + 1, None, NOON), Vec::new());
    }

    #[test]
    fn a_player_whose_ship_is_destroyed_is_told_of_it_as_the_others_are_and_the_host_is_told_who_did_it() {
        let (mut scene, rat) = scene_with_a_mortal_rat();
        // a shot from the NPC's ship is what destroys the player's
        let told = scene.destroy(PLAYER, Some(rat), LATER);
        assert!(!scene.contains(PLAYER));
        let to_the_pilot = for_player(told.clone());
        let names: Vec<String> = told_actions(&to_the_pilot).into_iter().map(|(_, name)| name).collect();
        assert_eq!(names, ["TerminalPlayDestructionEffect", "RemoveBalls"], "the pilot's own client too");
        let Some(Out::Destroyed { session, ship_id, wreck, killer_ship, .. }) =
            told.iter().find(|out| matches!(out, Out::Destroyed { .. }))
        else {
            panic!("{told:?}")
        };
        assert_eq!((*session, *ship_id, *wreck, *killer_ship), (PLAYER, PLAYER_SHIP, None, Some(RAT_SHIP)));
    }

    #[test]
    fn the_wreck_of_a_ship_a_player_destroyed_is_the_players_to_loot() {
        let (mut scene, rat) = scene_with_a_mortal_rat();
        scene.destroy(rat, Some(PLAYER), LATER);
        let wreck = scene.wrecks.values().next().unwrap();
        assert_eq!((wreck.owner_id, wreck.corporation_id, wreck.war_faction_id), (140_000_342, 1_000_006, 0));
        assert_eq!((wreck.rights.character_id, wreck.rights.corporation_id), (140_000_342, 1_000_006));
        assert_eq!(wreck.launcher_id, RAT_SHIP);
        assert_eq!(spec().character_id, 140_000_342);
    }

    #[test]
    fn a_wreck_is_the_loot_of_whoever_the_host_says_and_the_ship_that_did_it_is_none() {
        let (mut scene, rat) = scene_with_a_mortal_rat();
        let credit = Credit {
            character_id: 140_000_001,
            corporation_id: 1_000_006,
            alliance_id: 99_000_001,
            war_faction_id: 500_001,
        };
        scene.destroy_credited(rat, credit, None, LATER);
        let wreck = scene.wrecks.values().next().unwrap();
        assert_eq!(
            (wreck.owner_id, wreck.corporation_id, wreck.alliance_id, wreck.war_faction_id),
            (140_000_001, 1_000_006, 99_000_001, 500_001)
        );
        assert_eq!((wreck.rights.character_id, wreck.rights.corporation_id), (140_000_001, 1_000_006));
    }

    #[test]
    fn the_wreck_of_a_ship_an_npc_destroyed_is_its_owners_to_loot() {
        let (mut scene, rat) = scene_with_a_mortal_rat();
        let place = place_near(&mut scene, 9_000.0);
        let mut other = rat_spec();
        other.ship_id += 1;
        let (killer, _) = scene.spawn_npc(other, rat_identity(0.0), place, LATER);
        scene.destroy(rat, Some(killer), LATER);
        let wreck = scene.wrecks.values().next().unwrap();
        assert_eq!(
            (wreck.owner_id, wreck.rights.character_id, wreck.rights.corporation_id),
            (1_000_125, 1_000_125, 1_000_125)
        );
    }

    #[test]
    fn a_wreck_in_another_box_of_the_grid_is_not_sent_to_a_pilot_who_cannot_see_the_place() {
        let (mut scene, rat) = scene_with_a_mortal_rat();
        scene.pilots.get_mut(&rat).unwrap().ship.position.x += 8_000_000.0;
        let told = for_player(scene.destroy(rat, None, LATER));
        assert!(!told_actions(&told).iter().any(|(_, name)| name == "AddBalls2"), "{told:?}");
        assert_eq!(scene.wrecks.len(), 1);
        assert!(scene.wrecks_in_view(PLAYER).is_empty());
    }

    #[test]
    fn a_pilot_who_binds_later_is_given_the_wreck_with_the_rest_of_the_state() {
        let (mut scene, rat) = scene_with_a_mortal_rat();
        scene.destroy(rat, None, LATER);
        assert_eq!(scene.wrecks_in_view(PLAYER).len(), 1, "the pilot was told of it when it came");
        // a second pilot arriving after the death is given it when it binds
        let mut other = spec();
        other.ship_id += 1;
        other.character_id += 1;
        other.owner_id += 1;
        scene.undock(PLAYER + 1, other, crate::scene::tests::STATION, LATER).unwrap();
        assert!(scene.wrecks_in_view(PLAYER + 1).is_empty(), "not before it binds");
        let told = scene.bind(PLAYER + 1, LATER + SECOND);
        assert_eq!(scene.wrecks_in_view(PLAYER + 1).len(), 1);
        let has_wreck_ball = told.iter().any(|out| {
            matches!(out, Out::Notify { name: "DoDestinyUpdate", args, .. }
                if format!("{args:?}").contains("Caldari Frigate Wreck"))
        });
        assert!(has_wreck_ball, "{told:?}");
    }

    #[test]
    fn a_wreck_goes_when_its_time_is_up_and_those_who_saw_it_are_told_once() {
        let (mut scene, rat) = scene_with_a_mortal_rat();
        scene.destroy(rat, None, LATER);
        let before = for_player(scene.poll(LATER + WRECK_LIFETIME - SECOND));
        assert!(!told_actions(&before).iter().any(|(_, name)| name == "RemoveBalls"));
        assert_eq!(scene.wrecks.len(), 1);
        let told = for_player(scene.poll(LATER + WRECK_LIFETIME));
        let removals: Vec<_> =
            told_actions(&told).into_iter().filter(|(_, name)| name == "RemoveBalls").collect();
        assert_eq!(removals.len(), 1, "{told:?}");
        assert!(scene.wrecks.is_empty());
        assert!(scene.wrecks_in_view(PLAYER).is_empty());
        assert!(told_actions(&for_player(scene.poll(LATER + WRECK_LIFETIME + SECOND))).is_empty());
    }

    #[test]
    fn a_pilot_who_moves_into_the_box_of_a_wreck_is_sent_it_and_one_who_moves_out_is_told_it_is_gone() {
        let (mut scene, rat) = scene_with_a_mortal_rat();
        scene.destroy(rat, None, LATER);
        // the player is taken to a box of the grid the wreck is not in, and back
        let home = scene.pilots[&PLAYER].ship.position;
        scene.pilots.get_mut(&PLAYER).unwrap().ship.position.x += 8_000_000.0;
        let away = for_player(scene.poll(LATER + SECOND));
        assert!(told_actions(&away).iter().any(|(_, name)| name == "RemoveBalls"), "{away:?}");
        assert!(scene.wrecks_in_view(PLAYER).is_empty());
        scene.pilots.get_mut(&PLAYER).unwrap().ship.position = home;
        let back = for_player(scene.poll(LATER + 2 * SECOND));
        assert!(told_actions(&back).iter().any(|(_, name)| name == "AddBalls2"), "{back:?}");
        assert_eq!(scene.wrecks_in_view(PLAYER).len(), 1);
    }

    #[test]
    fn the_trees_that_watched_a_ship_are_told_when_it_is_destroyed_and_the_ship_lost_its_own() {
        let (mut scene, rat) = scene_with_a_mortal_rat();
        scene.give_behavior(rat, "sleep").unwrap();
        assert!(scene.brains().unwrap().has(rat));
        scene.destroy(rat, None, LATER);
        assert!(!scene.brains().unwrap().has(rat), "its tree is forgotten");
        assert_eq!(scene.destroyed, [RAT_SHIP]);
        scene.poll(LATER + SECOND);
        assert!(scene.destroyed.is_empty(), "told once");
        let _ = run(&mut scene, LATER + 2 * SECOND, 2);
    }
}
