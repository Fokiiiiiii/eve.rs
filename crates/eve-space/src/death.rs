//! Ships that are destroyed. A ship whose hull is gone blows up: whoever has it in view is told to play the explosion where it was and to let go of
//! its ball (one stamp), it stops being a pilot of the scene (its locks are let go, its tree is forgotten), and it leaves a wreck (`crate::wreck`),
//! which those who can see the place are sent. What is done to the pilot of a player's ship (a capsule to fly, the ship's items, the wreck's
//! contents) is the host's: it is told with [`Out::Destroyed`].

use crate::npc::is_npc_session;
use crate::payload;
use crate::scene::{Out, Scene, SessionId};
use crate::wreck::{LootRights, WRECK_LIFETIME, Wreck};

/// The first id the scene gives a wreck of its own making (the reference's transient items are numbered from about the same place; the ids of
/// the host's items are far below it).
pub const TRANSIENT_ID_BASE: i64 = 4_300_000_000_000_000;

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
        if self.pilots[&session].bound && !is_npc_session(session) {
            told_of_it.insert(0, session);
        }
        let Some(pilot) = self.pilots.remove(&session) else { return Vec::new() };
        if let Some(brains) = self.brains.as_mut() {
            brains.forget(session);
        }
        for other in self.pilots.values_mut() {
            other.sees.remove(&session);
        }
        let ship = &pilot.ship;
        let ship_id = ship.spec.ship_id;
        let mut out = self.drop_targeting(ship_id, &pilot.targeting);
        self.destroyed.push(ship_id);
        let stamp = self.next_stamp(now);
        for watcher in told_of_it {
            out.push(Self::update(watcher, payload::destruction(stamp, ship_id)));
        }
        // an NPC that says what it leaves leaves that; else the wreck of the ship's type, as the reference's rules have it
        let kind = ship.npc.as_ref().and_then(|npc| npc.wreck.clone()).or_else(|| {
            let catalog = self.wreck_catalog.as_ref()?;
            ship.npc.as_ref().and_then(|_| catalog.resolve_ship_wreck_type(i64::from(ship.spec.type_id)))
        });
        let wreck = kind.map(|kind| {
            self.wreck_sequence += 1;
            Wreck {
                id: TRANSIENT_ID_BASE + self.wreck_sequence as i64,
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
            position: ship.position,
            direction: ship.direction,
            radius: ship.spec.radius,
            killer_ship,
        });
        out
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
    use crate::scene::tests::{NOON, spec};
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
            "an NPC's destruction is for the scene alone: the host is not told of it"
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
