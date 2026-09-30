//! NPC ships in a scene: ships that nobody flies. The scene knows them as pilots under sessions of their own (numbered from [`NPC_SESSION_BASE`], far
//! above any the host gives a player), so a ship's motion, warp, locks, modules and health work the same for both and a player sees an NPC as it sees
//! any other ship. What differs is what the client is told about one: its ball has no session and is not interactive, its slim item has no name, and
//! nothing is ever sent to an NPC (`Out`s for an NPC's session are dropped where the scene meets the host, `Sim`).

use eve_destiny::Vec3;

use crate::modules::Modules;
use crate::scene::{Out, Pilot, Scene, SessionId};
use crate::ship::{Course, NpcIdentity, Ship, ShipSpec};

/// The session number the first NPC of a scene is known by.
pub const NPC_SESSION_BASE: SessionId = 1 << 62;

/// Whether a session is an NPC's.
pub fn is_npc_session(session: SessionId) -> bool {
    session >= NPC_SESSION_BASE
}

/// Where an NPC comes into a scene and how it is moving.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct NpcPlace {
    pub position: Vec3,
    pub velocity: Vec3,
    /// A unit vector.
    pub direction: Vec3,
    pub speed_fraction: f64,
    pub course: Course,
}

impl NpcPlace {
    /// At rest at a place.
    pub fn at_rest(position: Vec3) -> Self {
        Self {
            position,
            velocity: Vec3::ZERO,
            direction: Vec3::new(1.0, 0.0, 0.0),
            speed_fraction: 0.0,
            course: Course::Stop,
        }
    }
}

impl Scene {
    /// An NPC ship comes into the scene: the pilots that have bound the ballpark and can see it are sent it. The session it is known by, and what
    /// is told.
    pub fn spawn_npc(
        &mut self,
        spec: ShipSpec,
        identity: NpcIdentity,
        place: NpcPlace,
        now: i64,
    ) -> (SessionId, Vec<Out>) {
        self.npc_sequence += 1;
        let session = NPC_SESSION_BASE + self.npc_sequence;
        let modules = Modules::with_charge(
            spec.capacitor_capacity,
            spec.capacitor_recharge_s,
            spec.condition.charge,
            now,
        );
        let ship = Ship::new(
            spec,
            place.position,
            place.velocity,
            place.direction,
            place.speed_fraction,
            place.course,
            now,
        )
        .as_npc(identity);
        let entered = self.next_entry();
        self.pilots.insert(session, Pilot::new(ship, modules, entered, now, 0));
        (session, self.announce_arrival(session, now))
    }

    /// The sessions of the NPCs in the scene, in the order they came.
    pub fn npc_sessions(&self) -> Vec<SessionId> {
        self.in_entry_order().into_iter().filter(|session| is_npc_session(*session)).collect()
    }

    /// The sessions of the players in the scene, in the order they came.
    pub fn player_sessions(&self) -> Vec<SessionId> {
        self.in_entry_order().into_iter().filter(|session| !is_npc_session(*session)).collect()
    }

    /// An NPC ship leaves the scene (unspawned): those who had it in view are told. `None` when there is no such NPC.
    pub fn remove_npc(&mut self, session: SessionId, now: i64) -> Option<Vec<Out>> {
        if !is_npc_session(session) {
            return None;
        }
        self.leave(session, crate::scene::Departure::Docked, now)
    }
}

#[cfg(test)]
mod tests {
    use eve_destiny::encode_entity_ball;
    use eve_marshal::Value;

    use super::*;
    use crate::scene::tests::{NOON, STATION, spec, system};
    use crate::scene::{Command, Departure};
    use crate::sim::Sim;

    const PLAYER: SessionId = 7;
    const SECOND: i64 = 10_000_000;

    /// The spec of the Guristas frigate the reference recorded around the gates (`oracle/fixtures/scenarios/jump.json`, item 980050000055).
    fn frigate() -> ShipSpec {
        let mut npc = spec();
        npc.ship_id = 980_050_000_055;
        npc.character_id = 0;
        npc.owner_id = 1_000_125;
        npc.corporation_id = 1_000_125;
        npc.alliance_id = 0;
        npc.war_faction_id = 500_006;
        npc.type_id = 3863;
        npc.group_id = 301;
        npc.category_id = 11;
        npc.name = String::new();
        npc.security_status = 5.0;
        npc.radius = 38.0;
        npc.mass = 1_200_000.0;
        npc.max_velocity = 525.0;
        npc.agility = 3.1;
        npc.modules = Vec::new();
        npc
    }

    fn with_a_player() -> Scene {
        let mut scene = Scene::new(system());
        scene.undock(PLAYER, spec(), STATION, NOON).unwrap();
        scene.bind(PLAYER, NOON);
        scene
    }

    fn near_the_player(scene: &mut Scene) -> NpcPlace {
        let position = scene.ship(PLAYER, NOON).unwrap().position;
        NpcPlace::at_rest(Vec3::new(position.x + 5_000.0, position.y, position.z))
    }

    fn action_names(out: &Out) -> Vec<String> {
        let Out::Notify { args, .. } = out else { return Vec::new() };
        let Some(Value::List(actions)) = args.first() else { return Vec::new() };
        actions
            .iter()
            .map(|action| {
                let Value::Tuple(pair) = action else { panic!("{action:?}") };
                let Value::Tuple(inner) = &pair[1] else { panic!("{:?}", pair[1]) };
                let Value::Str(name) = &inner[0] else { panic!("{:?}", inner[0]) };
                name.to_string()
            })
            .collect()
    }

    /// The ball the reference sent for item 980050000055 (an orbit around 50016630 at no distance, 525 m/s at full speed), byte for byte.
    const RECORDED_BALL: &str = "b738882fe400000004000018426923f0c27d814a4212329eb98bbf01c2adea4ea429ad7b420100000000804f324100ffffffffffffffffbd420f000000000000400344d2e6301b6ddb76c0caebec76338876c05214e49ade133d40666646400000803fff7631fb020000000000000000";

    fn hex(text: &str) -> Vec<u8> {
        (0..text.len()).step_by(2).map(|i| u8::from_str_radix(&text[i..i + 2], 16).unwrap()).collect()
    }

    #[test]
    fn an_npc_ball_is_the_one_the_reference_sent_byte_for_byte() {
        let mut spec = frigate();
        spec.corporation_id = 1_000_125;
        let ship = Ship::new(
            spec,
            Vec3::new(227_683_304_928.276_64, -9_528_506_163.774_448, 1_901_908_870_382.667_2),
            Vec3::new(-365.714_137_259_504_35, -360.512_564_588_044_03, 29.077_615_433_391_04),
            Vec3::new(1.0, 0.0, 0.0),
            1.0,
            Course::Orbit { target: 50_016_630, distance: 0.0 },
            NOON,
        )
        .as_npc(NpcIdentity::default());
        assert_eq!(encode_entity_ball(&ship.ball()).unwrap(), hex(RECORDED_BALL));
    }

    #[test]
    fn the_same_ship_flown_by_a_player_is_interactive_and_has_a_session() {
        let ship =
            Ship::new(spec(), Vec3::ZERO, Vec3::ZERO, Vec3::new(1.0, 0.0, 0.0), 0.0, Course::Stop, NOON);
        assert!(ship.ball().has_session);
        assert_eq!(ship.ball().free_ball_flags(), 0x9);
        let npc = ship.as_npc(NpcIdentity::default());
        assert!(!npc.ball().has_session);
        assert_eq!(npc.ball().free_ball_flags(), 0x1);
    }

    #[test]
    fn an_npcs_slim_item_has_no_name_and_ends_with_the_response_thresholds() {
        let ship =
            Ship::new(frigate(), Vec3::ZERO, Vec3::ZERO, Vec3::new(1.0, 0.0, 0.0), 0.0, Course::Stop, NOON)
                .as_npc(NpcIdentity::default());
        let entries = crate::slim::ship_entries(&ship);
        let keys: Vec<&str> = entries.iter().map(|(key, _)| *key).collect();
        assert_eq!(
            keys,
            [
                "itemID",
                "typeID",
                "ownerID",
                "groupID",
                "categoryID",
                "corpID",
                "allianceID",
                "warFactionID",
                "charID",
                "kills",
                "skinMaterialSetID",
                "modules",
                "securityStatus",
                "bounty",
                "hostile_response_threshold",
                "friendly_response_threshold",
            ]
        );
        let value = |name: &str| entries.iter().find(|(key, _)| *key == name).unwrap().1.clone();
        assert_eq!(value("securityStatus"), Value::Int(5));
        assert_eq!(value("hostile_response_threshold"), Value::Int(-11));
        assert_eq!(value("friendly_response_threshold"), Value::Int(11));
        assert_eq!(value("charID"), Value::Int(0));
        let player = crate::slim::ship_entries(&Ship::new(
            spec(),
            Vec3::ZERO,
            Vec3::ZERO,
            Vec3::new(1.0, 0.0, 0.0),
            0.0,
            Course::Stop,
            NOON,
        ));
        assert!(player.iter().any(|(key, _)| *key == "name"));
        assert!(player.iter().all(|(key, _)| !key.ends_with("_response_threshold")));
    }

    #[test]
    fn a_player_who_has_bound_the_ballpark_is_sent_an_npc_that_comes_into_view() {
        let mut scene = with_a_player();
        let place = near_the_player(&mut scene);
        let (session, told) = scene.spawn_npc(frigate(), NpcIdentity::default(), place, NOON + SECOND);
        assert!(is_npc_session(session));
        assert_eq!(told.len(), 1);
        assert_eq!(told[0].session(), PLAYER);
        assert_eq!(action_names(&told[0]), ["AddBalls2", "SetBallAgility", "SetBallMass", "SetMaxSpeed"]);
        assert!(scene.contains(session));
        assert_eq!(scene.npc_sessions(), [session]);
        assert_eq!(scene.player_sessions(), [PLAYER]);
    }

    #[test]
    fn an_npc_is_in_the_state_a_player_binds_later() {
        let mut scene = Scene::new(system());
        scene.undock(PLAYER, spec(), STATION, NOON).unwrap();
        let place = near_the_player(&mut scene);
        let (_, told) = scene.spawn_npc(frigate(), NpcIdentity::default(), place, NOON);
        assert!(told.is_empty(), "a player that has not bound is told nothing");
        let bound = scene.bind(PLAYER, NOON);
        let first = &bound[2];
        assert!(action_names(first).contains(&"AddBalls2".to_string()));
        let ships = scene.ships_in_view(PLAYER);
        assert_eq!(ships.len(), 2);
        assert_eq!(ships[1].spec.ship_id, 980_050_000_055);
    }

    #[test]
    fn an_npc_out_of_view_is_not_told_to_a_player() {
        let mut scene = with_a_player();
        let far = NpcPlace::at_rest(Vec3::new(1.0e12, 0.0, 0.0));
        let (session, told) = scene.spawn_npc(frigate(), NpcIdentity::default(), far, NOON);
        assert!(told.is_empty());
        assert!(scene.contains(session));
    }

    #[test]
    fn an_npc_that_is_ordered_about_moves_and_the_player_who_sees_it_is_told() {
        let mut scene = with_a_player();
        let place = near_the_player(&mut scene);
        let (npc, _) = scene.spawn_npc(frigate(), NpcIdentity::default(), place, NOON);
        let told = scene.command(npc, Command::SetSpeedFraction(1.0), NOON + SECOND);
        let to_the_player: Vec<&Out> = told.iter().filter(|out| out.session() == PLAYER).collect();
        assert!(!to_the_player.is_empty(), "the watcher hears of it: {told:?}");
        assert!(
            told.iter().any(|out| out.session() == npc),
            "the scene addresses the NPC as it does any ship"
        );
        let ship = scene.ship(npc, NOON + 3 * SECOND).unwrap();
        assert!(ship.velocity.x > 0.0 || ship.velocity.y != 0.0 || ship.velocity.z != 0.0);
    }

    #[test]
    fn a_player_can_lock_an_npc_at_once_as_an_npc_has_no_undocking_protection() {
        let mut scene = with_a_player();
        let place = near_the_player(&mut scene);
        scene.spawn_npc(frigate(), NpcIdentity::default(), place, NOON);
        let told = scene.target_order(PLAYER, crate::targeting::Order::Lock(980_050_000_055), NOON + SECOND);
        assert!(
            told.iter().any(|out| matches!(out, Out::Targets { pending: true, .. })),
            "the lock is going: {told:?}"
        );
    }

    #[test]
    fn an_npc_that_leaves_is_taken_out_of_the_view_of_the_player() {
        let mut scene = with_a_player();
        let place = near_the_player(&mut scene);
        let (npc, _) = scene.spawn_npc(frigate(), NpcIdentity::default(), place, NOON);
        let told = scene.remove_npc(npc, NOON + SECOND).unwrap();
        assert_eq!(told.len(), 1);
        assert_eq!(told[0].session(), PLAYER);
        assert_eq!(action_names(&told[0]), ["RemoveBalls"]);
        assert!(!scene.contains(npc));
        assert!(scene.remove_npc(npc, NOON + SECOND).is_none());
        assert!(scene.remove_npc(PLAYER, NOON + SECOND).is_none(), "a player is not an NPC");
        assert!(scene.contains(PLAYER));
    }

    #[test]
    fn a_scene_with_only_npcs_in_it_is_empty() {
        let mut scene = Scene::new(system());
        assert!(scene.is_empty());
        let (npc, _) =
            scene.spawn_npc(frigate(), NpcIdentity::default(), NpcPlace::at_rest(Vec3::ZERO), NOON);
        assert!(scene.is_empty(), "nobody to see them");
        assert!(scene.contains(npc));
        scene.undock(PLAYER, spec(), STATION, NOON).unwrap();
        assert!(!scene.is_empty());
        scene.leave(PLAYER, Departure::LoggedOff, NOON).unwrap();
        assert!(scene.is_empty());
    }

    #[test]
    fn npcs_are_numbered_from_the_base_in_the_order_they_are_made() {
        let mut scene = Scene::new(system());
        let (a, _) = scene.spawn_npc(frigate(), NpcIdentity::default(), NpcPlace::at_rest(Vec3::ZERO), NOON);
        let (b, _) = scene.spawn_npc(frigate(), NpcIdentity::default(), NpcPlace::at_rest(Vec3::ZERO), NOON);
        assert_eq!((a, b), (NPC_SESSION_BASE + 1, NPC_SESSION_BASE + 2));
        assert_eq!(scene.npc_sessions(), [a, b]);
        assert!(!is_npc_session(NPC_SESSION_BASE - 1));
        assert!(is_npc_session(NPC_SESSION_BASE));
    }

    #[test]
    fn the_host_is_told_nothing_that_is_for_an_npc() {
        let for_the_player = Out::Refused { session: PLAYER, command: 1, reason: "X" };
        let for_an_npc = Out::Refused { session: NPC_SESSION_BASE + 1, command: 1, reason: "X" };
        let messages = Sim::messages([for_an_npc.clone(), for_the_player.clone(), for_an_npc]);
        assert_eq!(messages.len(), 1);
    }
}
