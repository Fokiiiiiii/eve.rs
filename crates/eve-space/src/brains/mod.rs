//! The NPCs' behavior trees, run in a scene. Each NPC ship has a brain: a tree of the client's `behaviors` package (`eve-behaviors`), the context
//! it runs in, and what it has raised. Every tree of a scene shares one set of blackboards, timers and messages (`Shared`), as the trees of a
//! solar system do on the reference's server; the scene is the world they act in (`SceneWorld`).
//!
//! The scene has no threads and no clock of its own, so the brains are run by `Scene::poll`: the calls and timers that are due, the ball callbacks
//! whose range has been reached, and the messages waiting for a tree are delivered every poll, and the trees are ticked once a second ([`TICK`]; the
//! client's package does not say how often, and the reference's own NPC loop is not a behavior tree).
mod ballpark;
mod dogma;
#[cfg(test)]
mod pochven_tests;
#[cfg(test)]
pub(crate) mod rat_tests;
mod rats;
mod world;
#[cfg(test)]
mod world_tests;

use std::collections::{BTreeMap, BTreeSet, HashMap};
use std::fmt;

use eve_behaviors::blackboards::{Observer, ScopeType, TreeId};
use eve_behaviors::env::SlimItem;
use eve_behaviors::loaders::LoaderError;
use eve_behaviors::loaders::scripted::ScriptedBehaviorLoader;
use eve_behaviors::tree::{BehaviorContext, BehaviorError, BehaviorTree, Delivery, Shared, Status};

use crate::scene::{Out, Scene, SessionId, TICKS_PER_SECOND};
use crate::ship::Ship;
use crate::warp::PythonRandom;

pub use rats::{BELT_RAT, POCHVEN_NPC, POCHVEN_NPC_TAG, RAT_TAG};
pub use world::SceneWorld;

/// How often the trees are ticked, in FILETIME ticks.
pub const TICK: i64 = TICKS_PER_SECOND;

/// How many messages a channel of a blackboard keeps (`maxHistoryLength`, which the server gives the blackboard manager).
const HISTORY: usize = 10;

/// How many rounds of messages between trees are delivered in one go: a message can make a task send another, and a cycle must not spin.
const DELIVERY_ROUNDS: usize = 16;

/// The type of the proximity sensor an NPC that attacks what comes near has (the item type of the component the reference's spawner registers; the trees
/// only pass it on).
const PROXIMITY_SENSOR_TYPE: i64 = 0;

/// How far from a rat the sensor that makes it notice a ship reaches at the least, whatever the range it attacks at (`NPC_MIN_ACQUISITION_RANGE_METERS` of the
/// reference's `npcBehaviorLoop.js`: "notice further than you shoot": a rat that came 30 km off a belt would otherwise never see the pilot at it). The lock still has to
/// pass the ship's own lock range, so the rat closes in to its weapons' range and does not snipe from this one.
pub const MIN_ACQUISITION_RANGE: f64 = 50_000.0;

/// How many faults a brain remembers.
const FAULTS_KEPT: usize = 8;

/// What a ball callback waits for: a ball to be within a range of a ball or a place (`GotoPoint`, `ApproachObject` with a notification range).
#[derive(Debug, Clone, Copy, PartialEq)]
pub(crate) enum NotifyTarget {
    Ball(i64),
    Place([f64; 3]),
}

/// A ball callback that has not been made yet: when `ball` is within `range` of the target, `observer` is told.
#[derive(Debug, Clone, Copy, PartialEq)]
pub(crate) struct RangeNotification {
    pub ball: i64,
    pub target: NotifyTarget,
    pub range: f64,
    pub observer: Observer,
}

impl WorldState {
    fn new() -> Self {
        Self {
            rng: PythonRandom::new(0x4e50_4353),
            notifications: Vec::new(),
            groups: BTreeMap::new(),
            sensors: BTreeMap::new(),
            locks_seen: BTreeMap::new(),
            protected: BTreeMap::new(),
        }
    }

    #[cfg(test)]
    pub(crate) fn for_test() -> Self {
        Self { rng: PythonRandom::new(1), ..Self::new() }
    }
}

/// One NPC's tree and what it runs in.
struct Brain {
    tree: BehaviorTree,
    ctx: BehaviorContext,
    /// The item id of the ship, and the entity group it belongs to.
    ship_id: i64,
    group_id: i64,
    /// What the tree raised (an exception in the client's tick), the latest last.
    faults: Vec<BehaviorError>,
}

/// What the world the trees act in keeps between polls (`SceneWorld` borrows it while the brains run).
pub(crate) struct WorldState {
    pub(crate) rng: PythonRandom,
    pub(crate) notifications: Vec<RangeNotification>,
    /// The entity groups: the ships that belong to each, in the order they joined (`groupManager`).
    pub(crate) groups: BTreeMap<i64, Vec<i64>>,
    /// The proximity sensors (`componentRegistry`): what sees which ships, by the item that has one.
    pub(crate) sensors: BTreeMap<i64, Sensor>,
    /// What each NPC had locked when the trees were last told (the locks that came or went since are told to them).
    pub(crate) locks_seen: BTreeMap<i64, Vec<i64>>,
    /// Which ships were under their undock protection when the trees were last told (the end of it is told to the bubble).
    pub(crate) protected: BTreeMap<i64, bool>,
}

/// A proximity sensor component: an item that sees the ships within a range.
#[derive(Debug, Clone, PartialEq)]
pub(crate) struct Sensor {
    pub type_id: i64,
    pub detection_range: f64,
    pub tags: Vec<String>,
    /// The ships it saw when the trees were last told.
    pub in_range: BTreeSet<i64>,
}

/// The brains of a scene and what they share.
pub struct Brains {
    shared: Shared,
    brains: BTreeMap<SessionId, Brain>,
    trees: HashMap<TreeId, SessionId>,
    next_tree: TreeId,
    next_tick: i64,
    state: WorldState,
    loader: ScriptedBehaviorLoader,
}

impl Default for Brains {
    fn default() -> Self {
        Self {
            shared: Shared::new(HISTORY),
            brains: BTreeMap::new(),
            trees: HashMap::new(),
            next_tree: 1,
            next_tick: 0,
            state: WorldState::new(),
            loader: ScriptedBehaviorLoader::new(),
        }
    }
}

impl fmt::Debug for Brains {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.debug_struct("Brains")
            .field("npcs", &self.brains.len())
            .field("next_tick", &self.next_tick)
            .finish()
    }
}

/// Why an NPC could not be given a behavior.
#[derive(Debug, Clone, PartialEq)]
pub enum GiveError {
    /// The scripted behavior of that name is not there for an item.
    Unknown(String),
    Loader(LoaderError),
}

impl Brains {
    pub fn is_empty(&self) -> bool {
        self.brains.is_empty()
    }

    /// The number of NPCs that have a brain.
    pub fn len(&self) -> usize {
        self.brains.len()
    }

    /// Whether an NPC has a brain.
    pub fn has(&self, session: SessionId) -> bool {
        self.brains.contains_key(&session)
    }

    /// How many ball callbacks wait for a ship to come within range.
    pub fn waiting_notifications(&self) -> usize {
        self.state.notifications.len()
    }

    /// What an NPC's tree raised, oldest first.
    pub fn faults(&self, session: SessionId) -> &[BehaviorError] {
        self.brains.get(&session).map_or(&[], |brain| brain.faults.as_slice())
    }

    /// Whether the NPC's assigned tree delegates self-repair activation to the damage-gated ActiveTank task.
    pub(crate) fn has_active_tank(&self, session: SessionId) -> bool {
        self.brains.get(&session).is_some_and(|brain| {
            (0..brain.tree.task_count()).any(|task| brain.tree.type_name_of(task) == "ActiveTank")
        })
    }

    /// Tell an item's existing behavior subscribers that its damage state changed.
    pub(crate) fn damage_changed(&mut self, item_id: i64) {
        self.shared.tell_subscribers(
            eve_behaviors::events::Subject::Item(item_id),
            eve_behaviors::events::message::ON_DAMAGE_CHANGED,
            &[],
        );
    }

    /// Deliver the recurring NPC module-cycle start to behaviors that subscribed to this effect.
    pub(crate) fn effect_started(&mut self, item_id: i64, effect_id: i64) {
        self.shared.tell_subscribers(
            eve_behaviors::events::Subject::Item(item_id),
            eve_behaviors::events::EventKey::with(
                eve_behaviors::events::message::ON_EFFECT_STARTED,
                effect_id,
            ),
            &[],
        );
    }

    /// The status of the root of an NPC's tree, if it has one.
    pub fn root_status(&self, session: SessionId) -> Option<Status> {
        let brain = self.brains.get(&session)?;
        Some(brain.tree.status_of(brain.tree.root_task()?))
    }

    /// Gives an NPC the scripted behavior of that name (`ScriptedBehaviorLoader.load(behaviorId, ScopeTypes.Item)`).
    pub(crate) fn give(
        &mut self,
        session: SessionId,
        ship: &Ship,
        solar_system_id: i64,
        behavior: &str,
        group_id: i64,
        owners: crate::pochven_population::Owners,
    ) -> Result<(), GiveError> {
        let tree = if behavior == BELT_RAT {
            rats::create_belt_rat_behavior_tree()
        } else if let Some(stationary) = rats::pochven_tree_kind(behavior) {
            rats::create_pochven_behavior_tree(stationary, &owners)
        } else {
            self.loader.load(behavior, Some(ScopeType::Item)).map_err(|error| match error {
                LoaderError::BehaviorDoesNotExist => GiveError::Unknown(behavior.to_string()),
                other => GiveError::Loader(other),
            })?
        };
        let ctx = BehaviorContext {
            my_item_id: Some(ship.spec.ship_id),
            my_entity_group_id: Some(group_id),
            solar_system_id: Some(solar_system_id),
            my_slim_item: Some(SlimItem {
                item_id: ship.spec.ship_id,
                type_id: i64::from(ship.spec.type_id),
                group_id: i64::from(ship.spec.group_id),
                category_id: i64::from(ship.spec.category_id),
                owner_id: Some(ship.spec.owner_id),
                char_id: None,
            }),
            ..BehaviorContext::default()
        };
        self.insert(session, tree, ctx);
        if let Some(npc) = ship.npc.as_ref().filter(|npc| npc.combat.aggression_range > 0.0) {
            self.state.sensors.insert(
                ship.spec.ship_id,
                Sensor {
                    type_id: PROXIMITY_SENSOR_TYPE,
                    detection_range: npc.combat.aggression_range.max(MIN_ACQUISITION_RANGE),
                    // the tag the behavior's tree listens for (`rats.rs`): a Pochven NPC's tree listens for its own
                    tags: vec![
                        if rats::pochven_tree_kind(behavior).is_some() { POCHVEN_NPC_TAG } else { RAT_TAG }
                            .to_string(),
                    ],
                    in_range: BTreeSet::new(),
                },
            );
        }
        Ok(())
    }

    /// Gives an NPC a tree that is already built (a server-authored one, or a test's).
    pub(crate) fn insert(&mut self, session: SessionId, mut tree: BehaviorTree, ctx: BehaviorContext) {
        tree.core.id = self.next_tree;
        self.trees.insert(self.next_tree, session);
        self.next_tree += 1;
        let ship_id = ctx.my_item_id.unwrap_or_default();
        let group_id = ctx.my_entity_group_id.unwrap_or_default();
        if ctx.my_entity_group_id.is_some() {
            self.state.groups.entry(group_id).or_default().push(ship_id);
        }
        self.brains.insert(session, Brain { tree, ctx, ship_id, group_id, faults: Vec::new() });
    }

    /// An NPC is gone: its tree lets go (its subscriptions and ball callbacks with it).
    pub(crate) fn forget(&mut self, session: SessionId) {
        if let Some(brain) = self.brains.remove(&session) {
            let tree = brain.tree.core.id;
            self.trees.remove(&tree);
            self.shared.events.forget_tree(tree);
            self.state.notifications.retain(|n| n.observer.tree != tree);
            self.state.sensors.remove(&brain.ship_id);
            self.state.locks_seen.remove(&brain.ship_id);
            if let Some(members) = self.state.groups.get_mut(&brain.group_id) {
                members.retain(|member| *member != brain.ship_id);
                if members.is_empty() {
                    self.state.groups.remove(&brain.group_id);
                }
            }
        }
    }

    /// What was made while the brains were being run (an NPC that another spawned) is taken in.
    pub(crate) fn absorb(&mut self, other: Brains) {
        for (session, mut brain) in other.brains {
            let tree = self.next_tree;
            self.next_tree += 1;
            brain.tree.core.id = tree;
            self.trees.insert(tree, session);
            self.state.groups.entry(brain.group_id).or_default().push(brain.ship_id);
            self.brains.insert(session, brain);
        }
    }

    /// One poll: calls, timers and ball callbacks that are due, the messages that wait, and, when it is time, a tick of every tree. What the scene
    /// has to tell the host is given back.
    pub(crate) fn run(&mut self, scene: &mut Scene, now: i64) -> Vec<Out> {
        let mut out = Vec::new();
        let Brains { shared, brains, trees, next_tick, state, .. } = self;
        shared.now = now;
        let mut world = SceneWorld::new(scene, now, &mut out, state);
        shared.fire_due_calls();
        shared.fire_due_timers();
        for notification in world.take_due_notifications() {
            shared.pending.push(Delivery::Call { observer: notification.observer, args: Vec::new() });
        }
        world.tell_what_happened(shared);
        deliver_pending(shared, brains, trees, &mut world);
        if now >= *next_tick {
            *next_tick = now + TICK;
            for brain in brains.values_mut() {
                if let Err(error) = brain.tree.tick(&mut brain.ctx, shared, &mut world) {
                    fault(brain, shared, &mut world, error);
                }
            }
            deliver_pending(shared, brains, trees, &mut world);
        }
        out
    }
}

/// A tree raised: it is remembered, and the tree is reset (a tick that meets an exception is unwound and leaves the tree's queue as it was, so
/// nothing would run it again; the client's package does not say what the system that owns the trees does, and an NPC that stops for good is worse
/// than one that starts over).
fn fault(brain: &mut Brain, shared: &mut Shared, world: &mut SceneWorld<'_>, error: BehaviorError) {
    if brain.faults.len() == FAULTS_KEPT {
        brain.faults.remove(0);
    }
    brain.faults.push(error);
    brain.tree.request_reset(&mut brain.ctx, shared, world);
}

/// Delivers what waits for the trees (blackboard messages, messages about items, calls), until nothing waits or the rounds are used up.
fn deliver_pending(
    shared: &mut Shared,
    brains: &mut BTreeMap<SessionId, Brain>,
    trees: &HashMap<TreeId, SessionId>,
    world: &mut SceneWorld<'_>,
) {
    for _ in 0..DELIVERY_ROUNDS {
        if shared.pending.is_empty() {
            return;
        }
        let batch: Vec<Delivery> = std::mem::take(&mut shared.pending);
        for delivery in batch {
            let tree = match &delivery {
                Delivery::Blackboard { observer, .. }
                | Delivery::Event { observer, .. }
                | Delivery::Call { observer, .. } => observer.tree,
            };
            let Some(session) = trees.get(&tree) else { continue };
            let Some(brain) = brains.get_mut(session) else { continue };
            if let Err(error) = brain.tree.deliver(&mut brain.ctx, shared, world, &delivery) {
                fault(brain, shared, world, error);
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use std::sync::Arc;
    use std::sync::atomic::{AtomicU32, Ordering};

    use eve_behaviors::env::{Ballpark, Random};
    use eve_behaviors::tree::{Attrs, Status, Task, TaskCtx};
    use eve_behaviors::value::Value;
    use eve_destiny::Vec3;

    use super::*;
    use crate::npc::{NpcPlace, is_npc_session};
    use crate::scene::Command;
    use crate::scene::tests::{NOON, STATION, spec, system};
    use crate::ship::{Course, NpcIdentity, ShipSpec};

    const PLAYER: SessionId = 7;
    const SECOND: i64 = TICKS_PER_SECOND;
    const NPC_SHIP: i64 = 980_050_000_055;

    fn npc_spec() -> ShipSpec {
        let mut npc = spec();
        npc.ship_id = NPC_SHIP;
        npc.character_id = 0;
        npc.owner_id = 1_000_125;
        npc.corporation_id = 1_000_125;
        npc.category_id = 11;
        npc.name = String::new();
        npc.max_velocity = 500.0;
        npc
    }

    /// A scene with a player that has bound the ballpark, and the place `metres` from the player along x.
    fn scene_with_a_player() -> Scene {
        let mut scene = Scene::new(system());
        scene.undock(PLAYER, spec(), STATION, NOON).unwrap();
        scene.bind(PLAYER, NOON);
        scene
    }

    fn place_near(scene: &mut Scene, metres: f64) -> NpcPlace {
        let position = scene.ship(PLAYER, NOON).unwrap().position;
        NpcPlace::at_rest(Vec3::new(position.x + metres, position.y, position.z))
    }

    /// A tree of one task.
    fn tree_of(task: impl Task + 'static) -> BehaviorTree {
        let mut tree = BehaviorTree::new(0, None, None);
        let root = tree.add_task(Box::new(task), Attrs::new());
        tree.start_root_task(root);
        tree
    }

    fn context_of(ship_id: i64) -> BehaviorContext {
        BehaviorContext { my_item_id: Some(ship_id), ..BehaviorContext::default() }
    }

    fn npc_with_tree(scene: &mut Scene, place: NpcPlace, tree: BehaviorTree) -> SessionId {
        let (session, _) = scene.spawn_npc(npc_spec(), NpcIdentity::default(), place, NOON);
        scene.brains.get_or_insert_with(Brains::default).insert(session, tree, context_of(NPC_SHIP));
        session
    }

    /// Counts how often it is updated.
    struct Counter(Arc<AtomicU32>);

    impl Task for Counter {
        fn type_name(&self) -> &'static str {
            "Counter"
        }

        fn update(&mut self, t: &mut TaskCtx<'_, '_>) {
            self.0.fetch_add(1, Ordering::SeqCst);
            t.set_status_to_running();
        }
    }

    /// Approaches the player's ship and is done.
    struct Approach;

    impl Task for Approach {
        fn type_name(&self) -> &'static str {
            "Approach"
        }

        fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
            let me = t.my_item_id().unwrap();
            t.world().approach_object(me, spec().ship_id, 500.0, false);
            t.set_status_to_success();
        }
    }

    /// Orbits the player's ship and waits until it is within `range` (a ball callback).
    struct OrbitUntil(f64);

    impl Task for OrbitUntil {
        fn type_name(&self) -> &'static str {
            "OrbitUntil"
        }

        fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
            let me = t.my_item_id().unwrap();
            let observer = t.observer(0);
            let result = t.world().orbit_object(me, spec().ship_id, 5_000.0, Some((self.0, observer)));
            assert!(result.is_ok());
            t.set_status_to_suspended();
        }

        fn on_call(&mut self, t: &mut TaskCtx<'_, '_>, _handler: u16, _args: &[Value]) {
            t.set_status_to_success();
        }
    }

    /// Raises an error every time it is updated.
    struct Faulty;

    impl Task for Faulty {
        fn type_name(&self) -> &'static str {
            "Faulty"
        }

        fn update(&mut self, t: &mut TaskCtx<'_, '_>) {
            t.raise(BehaviorError::Task("no good".to_string()));
        }
    }

    /// Goes to a place and waits until it is within `range` of it (a ball callback), and then to a second place, at once.
    struct GotoUntil {
        place: [f64; 3],
        range: f64,
    }

    impl Task for GotoUntil {
        fn type_name(&self) -> &'static str {
            "GotoUntil"
        }

        fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
            let me = t.my_item_id().unwrap();
            let observer = t.observer(0);
            t.world().goto_point_notify(me, self.place, self.range, observer);
            t.set_status_to_suspended();
        }

        fn on_call(&mut self, t: &mut TaskCtx<'_, '_>, _handler: u16, _args: &[Value]) {
            t.set_status_to_success();
        }
    }

    /// Sleeps `seconds` (a call the scene makes of it when the time is up) and is done.
    struct SleepThenDone(f64, Arc<AtomicU32>);

    impl Task for SleepThenDone {
        fn type_name(&self) -> &'static str {
            "SleepThenDone"
        }

        fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
            t.schedule_call(self.0, 0, vec![Value::Int(7)]);
            t.set_status_to_suspended();
        }

        fn on_call(&mut self, t: &mut TaskCtx<'_, '_>, _handler: u16, args: &[Value]) {
            assert_eq!(args, [Value::Int(7)]);
            self.1.fetch_add(1, Ordering::SeqCst);
            t.set_status_to_success();
        }
    }

    fn polls(scene: &mut Scene, from: i64, seconds: i64, step: i64) -> Vec<Out> {
        let mut out = Vec::new();
        let mut now = from;
        while now <= from + seconds * SECOND {
            out.extend(scene.poll(now));
            now += step;
        }
        out
    }

    #[test]
    fn a_tree_is_ticked_once_a_second_however_often_the_scene_is_polled() {
        let mut scene = scene_with_a_player();
        let place = place_near(&mut scene, 5_000.0);
        let count = Arc::new(AtomicU32::new(0));
        npc_with_tree(&mut scene, place, tree_of(Counter(Arc::clone(&count))));
        scene.poll(NOON);
        assert_eq!(count.load(Ordering::SeqCst), 1, "the first poll ticks");
        scene.poll(NOON + SECOND / 4);
        scene.poll(NOON + SECOND / 2);
        scene.poll(NOON + 3 * SECOND / 4);
        assert_eq!(count.load(Ordering::SeqCst), 1, "the poll rate is four a second, the tick one");
        scene.poll(NOON + SECOND);
        assert_eq!(count.load(Ordering::SeqCst), 2);
        polls(&mut scene, NOON + SECOND + 1, 10, SECOND / 4);
        assert_eq!(count.load(Ordering::SeqCst), 12);
    }

    #[test]
    fn a_scene_without_brains_polls_as_before() {
        let mut scene = scene_with_a_player();
        assert!(scene.brains().is_none());
        assert!(scene.poll(NOON + SECOND).is_empty() || scene.brains().is_none());
        let place = place_near(&mut scene, 5_000.0);
        scene.spawn_npc(npc_spec(), NpcIdentity::default(), place, NOON);
        scene.poll(NOON + 2 * SECOND);
        assert!(scene.brains().is_none(), "an NPC with no behavior makes no brain");
    }

    #[test]
    fn a_task_that_orders_a_ship_moves_it_and_the_player_who_sees_it_is_told() {
        let mut scene = scene_with_a_player();
        let place = place_near(&mut scene, 5_000.0);
        let npc = npc_with_tree(&mut scene, place, tree_of(Approach));
        let out = scene.poll(NOON + SECOND);
        assert!(
            matches!(scene.ship(npc, NOON + SECOND).unwrap().course, Course::Follow { target, range } if target == spec().ship_id && range == 500.0),
            "{:?}",
            scene.ship(npc, NOON + SECOND).unwrap().course
        );
        assert!(out.iter().any(|o| o.session() == PLAYER), "the watcher is told: {out:?}");
        assert!(out.iter().all(|o| !is_npc_session(o.session())), "and nothing is addressed to the NPC");
    }

    #[test]
    fn a_ball_callback_is_made_when_the_ship_is_in_range_and_not_before() {
        let mut scene = scene_with_a_player();
        // the player stops; 20 km off, the NPC has to fly to within six kilometres of it
        scene.command(PLAYER, Command::Stop, NOON);
        let place = place_near(&mut scene, 20_000.0);
        let npc = npc_with_tree(&mut scene, place, tree_of(OrbitUntil(6_000.0)));
        scene.poll(NOON);
        assert_eq!(scene.brains().unwrap().root_status(npc), Some(Status::Suspended));
        let mut done_at = None;
        for step in 1..120 {
            scene.poll(NOON + step * SECOND);
            if scene.brains().unwrap().root_status(npc) != Some(Status::Suspended) {
                done_at = Some(step);
                break;
            }
        }
        let done_at = done_at.expect("the NPC got there");
        assert!(
            (20..100).contains(&done_at),
            "at half a kilometre a second fourteen kilometres take half a minute: {done_at}"
        );
        let there = scene.ship(npc, NOON + done_at * SECOND).unwrap().position;
        let player = scene.ship(PLAYER, NOON + done_at * SECOND).unwrap().position;
        let gap =
            ((there.x - player.x).powi(2) + (there.y - player.y).powi(2) + (there.z - player.z).powi(2))
                .sqrt();
        assert!(gap < 6_500.0, "within reach: {gap}");
        assert_eq!(scene.brains().unwrap().faults(npc), []);
    }

    #[test]
    fn a_ball_callback_for_a_ball_already_in_range_is_made_at_the_next_poll() {
        let mut scene = scene_with_a_player();
        let place = place_near(&mut scene, 300.0);
        let npc = npc_with_tree(&mut scene, place, tree_of(OrbitUntil(5_000.0)));
        scene.poll(NOON);
        scene.poll(NOON + SECOND / 4);
        assert_eq!(scene.brains().unwrap().root_status(npc), Some(Status::Success));
    }

    #[test]
    fn a_tree_that_raises_is_remembered_and_the_other_trees_go_on() {
        let mut scene = scene_with_a_player();
        let place = place_near(&mut scene, 5_000.0);
        let count = Arc::new(AtomicU32::new(0));
        let bad = npc_with_tree(&mut scene, place, tree_of(Faulty));
        let (good, _) = scene.spawn_npc(
            {
                let mut s = npc_spec();
                s.ship_id += 1;
                s
            },
            NpcIdentity::default(),
            place,
            NOON,
        );
        scene.brains.get_or_insert_with(Brains::default).insert(
            good,
            tree_of(Counter(Arc::clone(&count))),
            context_of(NPC_SHIP + 1),
        );
        polls(&mut scene, NOON, 3, SECOND);
        assert_eq!(count.load(Ordering::SeqCst), 4);
        let faults = scene.brains().unwrap().faults(bad);
        assert!(!faults.is_empty());
        assert!(faults.iter().all(|f| *f == BehaviorError::Task("no good".to_string())));
        assert!(scene.brains().unwrap().faults(good).is_empty());
    }

    #[test]
    fn a_call_a_task_asked_for_at_a_later_time_is_made_when_the_time_is_up() {
        let mut scene = scene_with_a_player();
        let place = place_near(&mut scene, 5_000.0);
        let woken = Arc::new(AtomicU32::new(0));
        let npc = npc_with_tree(&mut scene, place, tree_of(SleepThenDone(5.0, Arc::clone(&woken))));
        scene.poll(NOON);
        assert_eq!(scene.brains().unwrap().root_status(npc), Some(Status::Suspended));
        polls(&mut scene, NOON + SECOND / 4, 4, SECOND / 4);
        assert_eq!(woken.load(Ordering::SeqCst), 0, "not yet");
        polls(&mut scene, NOON + 5 * SECOND, 1, SECOND / 4);
        assert_eq!(woken.load(Ordering::SeqCst), 1, "once");
        assert_eq!(scene.brains().unwrap().root_status(npc), Some(Status::Success));
    }

    #[test]
    fn a_ship_that_goes_to_a_place_calls_back_when_it_is_within_range_of_it_and_not_before() {
        let mut scene = scene_with_a_player();
        let here = scene.ship(PLAYER, NOON).unwrap().position;
        let mut place = place_near(&mut scene, 30_000.0);
        place.position.y = here.y;
        let goal = [here.x + 5_000.0, here.y, here.z];
        let npc = npc_with_tree(&mut scene, place, tree_of(GotoUntil { place: goal, range: 2_000.0 }));
        scene.poll(NOON);
        assert_eq!(scene.brains().unwrap().waiting_notifications(), 1);
        assert_eq!(scene.brains().unwrap().root_status(npc), Some(Status::Suspended));
        let mut now = NOON;
        let mut done_after = None;
        while now < NOON + 200 * SECOND {
            now += SECOND / 4;
            scene.poll(now);
            if scene.brains().unwrap().root_status(npc) == Some(Status::Success) {
                done_after = Some((now - NOON) / SECOND);
                break;
            }
        }
        let done_after = done_after.expect("it got there");
        assert!(done_after >= 20, "twenty-eight kilometres at half a kilometre a second: {done_after}");
        assert_eq!(scene.brains().unwrap().waiting_notifications(), 0, "the callback is made once");
        let at = scene.ship(npc, now).unwrap().position;
        let gap = ((at.x - goal[0]).powi(2) + (at.y - goal[1]).powi(2) + (at.z - goal[2]).powi(2)).sqrt();
        assert!(gap <= 2_600.0, "within range of it: {gap}");
    }

    #[test]
    fn an_npc_that_leaves_takes_its_waiting_callbacks_with_it() {
        let mut scene = scene_with_a_player();
        let place = place_near(&mut scene, 40_000.0);
        let npc = npc_with_tree(&mut scene, place, tree_of(OrbitUntil(100.0)));
        scene.poll(NOON);
        assert_eq!(scene.brains().unwrap().waiting_notifications(), 1);
        scene.remove_npc(npc, NOON + SECOND).unwrap();
        assert_eq!(scene.brains().unwrap().waiting_notifications(), 0);
    }

    #[test]
    fn a_brain_keeps_the_latest_faults_and_no_more() {
        let mut scene = scene_with_a_player();
        let place = place_near(&mut scene, 5_000.0);
        let npc = npc_with_tree(&mut scene, place, tree_of(Faulty));
        polls(&mut scene, NOON, 20, SECOND);
        assert_eq!(scene.brains().unwrap().faults(npc).len(), FAULTS_KEPT);
    }

    #[test]
    fn an_npc_that_leaves_loses_its_brain() {
        let mut scene = scene_with_a_player();
        let place = place_near(&mut scene, 5_000.0);
        let count = Arc::new(AtomicU32::new(0));
        let npc = npc_with_tree(&mut scene, place, tree_of(Counter(Arc::clone(&count))));
        assert!(scene.brains().unwrap().has(npc));
        scene.poll(NOON);
        assert_eq!(count.load(Ordering::SeqCst), 1);
        scene.remove_npc(npc, NOON + SECOND).unwrap();
        assert!(!scene.brains().unwrap().has(npc));
        assert_eq!(scene.brains().unwrap().len(), 0);
        scene.poll(NOON + 2 * SECOND);
        assert_eq!(count.load(Ordering::SeqCst), 1, "nobody ticks a tree that is gone");
    }

    #[test]
    fn the_scripted_behaviors_run_in_the_scene_without_raising_anything() {
        for behavior in ["sleep", "dps"] {
            let mut scene = scene_with_a_player();
            let place = place_near(&mut scene, 5_000.0);
            let (npc, _) = scene
                .spawn_npc_with_behavior(npc_spec(), NpcIdentity::default(), place, behavior, NOON)
                .unwrap();
            assert!(scene.brains().unwrap().has(npc));
            polls(&mut scene, NOON, 30, SECOND / 4);
            assert_eq!(scene.brains().unwrap().faults(npc), [], "behavior {behavior}");
        }
    }

    #[test]
    fn a_behavior_that_is_not_there_makes_no_npc() {
        let mut scene = scene_with_a_player();
        let place = place_near(&mut scene, 5_000.0);
        let result =
            scene.spawn_npc_with_behavior(npc_spec(), NpcIdentity::default(), place, "no such tree", NOON);
        assert_eq!(result.unwrap_err(), GiveError::Unknown("no such tree".to_string()));
        assert!(scene.npc_sessions().is_empty());
    }

    /// Runs `body` against the scene as the world an NPC's tree acts in (with what the scene's brains keep between polls).
    fn with_world<R>(scene: &mut Scene, now: i64, body: impl FnOnce(&mut SceneWorld<'_>) -> R) -> R {
        let mut out = Vec::new();
        let mut brains = scene.brains.take().unwrap_or_default();
        let result = {
            let mut world = SceneWorld::new(scene, now, &mut out, &mut brains.state);
            body(&mut world)
        };
        scene.brains = Some(brains);
        result
    }

    #[test]
    fn the_world_knows_the_balls_of_the_scene() {
        let mut scene = scene_with_a_player();
        let place = place_near(&mut scene, 5_000.0);
        scene.spawn_npc(npc_spec(), NpcIdentity::default(), place, NOON);
        with_world(&mut scene, NOON, |world| {
            assert!(world.has_ball(NPC_SHIP));
            assert!(world.has_ball(spec().ship_id));
            assert!(world.has_ball(STATION), "the bodies are balls too");
            assert!(!world.has_ball(1));
            let ball = world.ball(NPC_SHIP).unwrap();
            assert_eq!(ball.id, NPC_SHIP);
            assert!(ball.is_free);
            assert_eq!(ball.mode, eve_behaviors::consts::DESTINY_MODE_STOP);
            let station = world.ball(STATION).unwrap();
            assert!(!station.is_free);
            assert_eq!(world.slim_item(NPC_SHIP).unwrap().owner_id, Some(1_000_125));
            assert_eq!(world.slim_item(NPC_SHIP).unwrap().char_id, None);
            assert_eq!(world.slim_item(spec().ship_id).unwrap().char_id, Some(140_000_342));
            assert_eq!(world.solar_system_id(), Some(30_000_001));
            assert!(world.check_ball(NPC_SHIP).is_ok());
            assert!(world.check_ball(2).is_err());
        });
    }

    #[test]
    fn the_world_measures_between_balls_and_lists_what_is_in_range() {
        let mut scene = scene_with_a_player();
        let body_id = scene.system().bodies.first().expect("test system has a body").id;
        let place = place_near(&mut scene, 5_000.0);
        scene.spawn_npc(npc_spec(), NpcIdentity::default(), place, NOON);
        with_world(&mut scene, NOON, |world| {
            let centre = world.distance_between(NPC_SHIP, spec().ship_id).unwrap();
            assert!((centre - 5_000.0).abs() < 1e-6, "{centre}");
            let surface = world.surface_dist(NPC_SHIP, spec().ship_id).unwrap();
            assert!((surface - (5_000.0 - 47.0 - 47.0)).abs() < 1e-6, "{surface}");
            assert!(world.distance_between(NPC_SHIP, 1).is_err());
            let near = world.ball_ids_and_dist_in_range(NPC_SHIP, 6_000.0);
            assert!(
                near.iter()
                    .any(|(squared, id)| *id == spec().ship_id && (squared - 25_000_000.0).abs() < 1.0),
                "{near:?}"
            );
            assert!(near.iter().all(|(_, id)| *id != NPC_SHIP), "not itself");
            let npc_near_player = world.ball_ids_and_dist_in_range(spec().ship_id, 6_000.0);
            assert!(
                npc_near_player
                    .iter()
                    .any(|(squared, id)| *id == NPC_SHIP && (squared - 25_000_000.0).abs() < 1.0),
                "{npc_near_player:?}"
            );
            assert!(
                world
                    .ball_ids_and_dist_in_range(NPC_SHIP, 1_000.0)
                    .iter()
                    .all(|(_, id)| *id != spec().ship_id)
            );
            let exactly = world.ball_ids_and_dist_in_range(NPC_SHIP, 5_000.0);
            assert!(exactly.iter().any(|(_, id)| *id == spec().ship_id), "a ball at the range is in it");
            let short = world.ball_ids_and_dist_in_range(NPC_SHIP, 4_999.0);
            assert!(short.iter().all(|(_, id)| *id != spec().ship_id), "and one a little beyond is not");
            let all_balls = world.ball_ids_and_dist_in_range(NPC_SHIP, f64::MAX);
            assert!(all_balls.iter().any(|(_, id)| *id == body_id), "{all_balls:?}");
        });
    }

    #[test]
    fn a_ship_that_warps_is_in_warp_mode_and_one_that_does_not_is_not() {
        use crate::world::{Body, BodyKind, Detail};
        use eve_behaviors::consts::{DESTINY_MODE_STOP, DESTINY_MODE_WARP, SHIP_NOT_WARPING};
        let mut world_data = (*system()).clone();
        world_data.bodies.push(Body {
            id: 40_000_001,
            type_id: 2015,
            group_id: 7,
            category_id: 2,
            owner_id: 0,
            name: "Testing III".to_string(),
            kind: BodyKind::Planet,
            position: Vec3::new(45_000_000_000.0, 0.0, 20_000_000_000.0),
            radius: 6_000_000.0,
            graphic_id: None,
            detail: Detail::None,
        });
        let mut scene = Scene::new(std::sync::Arc::new(world_data));
        scene.undock(PLAYER, spec(), STATION, NOON).unwrap();
        scene.bind(PLAYER, NOON);
        let place = place_near(&mut scene, 5_000.0);
        let (npc, _) = scene.spawn_npc(npc_spec(), NpcIdentity::default(), place, NOON);
        with_world(&mut scene, NOON, |world| {
            assert_eq!(world.is_warping(NPC_SHIP), Ok(SHIP_NOT_WARPING));
            assert_eq!(world.ball(NPC_SHIP).unwrap().mode, DESTINY_MODE_STOP);
        });
        scene.command(npc, Command::WarpTo { target_id: 40_000_001, range: 1_000_000.0 }, NOON);
        with_world(&mut scene, NOON, |world| {
            assert_eq!(world.is_warping(NPC_SHIP), Ok(1));
            assert_eq!(world.ball(NPC_SHIP).unwrap().mode, DESTINY_MODE_WARP);
            assert_eq!(world.is_warping(spec().ship_id), Ok(SHIP_NOT_WARPING));
            assert_eq!(world.is_warping(1), Ok(SHIP_NOT_WARPING));
        });
    }

    #[test]
    fn balls_in_one_box_of_the_grid_share_a_bubble() {
        let mut scene = scene_with_a_player();
        let place = place_near(&mut scene, 5_000.0);
        scene.spawn_npc(npc_spec(), NpcIdentity::default(), place, NOON);
        with_world(&mut scene, NOON, |world| {
            let mine = world.ball(NPC_SHIP).unwrap().new_bubble_id;
            assert!(mine > 0);
            assert_eq!(world.ball(spec().ship_id).unwrap().new_bubble_id, mine);
            assert!(world.in_same_bubble(NPC_SHIP, spec().ship_id));
            assert_eq!(world.bubble_at_coordinates(world.ball(NPC_SHIP).unwrap().position), mine);
            let ships = world.ships_in_same_bubble(NPC_SHIP);
            assert!(ships.contains(&NPC_SHIP) && ships.contains(&spec().ship_id));
            assert!(world.items_in_bubble_of(NPC_SHIP).contains(&NPC_SHIP));
            assert!(world.ball_ids_in_bubble(mine).unwrap().contains(&spec().ship_id));
            assert!(world.ball_ids_in_bubble(mine + 1).is_none());
        });
    }

    #[test]
    fn npcs_of_one_group_are_its_members_and_a_group_made_for_an_npc_has_it_alone() {
        use eve_behaviors::env::Entities;
        let mut scene = scene_with_a_player();
        let place = place_near(&mut scene, 5_000.0);
        let group = scene.new_entity_group();
        let mut other = npc_spec();
        other.ship_id += 1;
        let (a, _) = scene
            .spawn_npc_in_group(npc_spec(), NpcIdentity::default(), place, "sleep", Some(group), NOON)
            .unwrap();
        scene.spawn_npc_in_group(other, NpcIdentity::default(), place, "sleep", Some(group), NOON).unwrap();
        let mut third = npc_spec();
        third.ship_id += 2;
        let (c, _) =
            scene.spawn_npc_with_behavior(third, NpcIdentity::default(), place, "sleep", NOON).unwrap();
        with_world(&mut scene, NOON, |world| {
            assert!(world.group_exists(group));
            assert!(!world.group_exists(group + 100));
            assert_eq!(world.group_members(group), Some(vec![NPC_SHIP, NPC_SHIP + 1]));
            assert_eq!(world.group_count(group), Some(2));
            assert_eq!(world.group_has_entity(group, NPC_SHIP + 1), Some(true));
            assert_eq!(world.group_has_entity(group, NPC_SHIP + 2), Some(false));
            assert_eq!(world.group_has_entity(group + 100, NPC_SHIP), None);
            assert_eq!(world.group_initial_spawning_complete(group), Some(true));
            assert_eq!(world.group_initial_spawning_complete(group + 100), None);
            assert_eq!(world.find_group_id_for_item(NPC_SHIP), group);
            assert_eq!(world.find_group_id_for_item(1), crate::npc::NO_GROUP);
            let alone = world.find_group_id_for_item(NPC_SHIP + 2);
            assert_ne!(alone, group);
            assert_eq!(world.group_members(alone), Some(vec![NPC_SHIP + 2]));
        });
        // a member that leaves leaves the group, and a group without members is gone
        scene.remove_npc(a, NOON + SECOND).unwrap();
        scene.remove_npc(c, NOON + SECOND).unwrap();
        with_world(&mut scene, NOON, |world| {
            assert_eq!(world.group_members(group), Some(vec![NPC_SHIP + 1]));
            assert_eq!(world.find_group_id_for_item(NPC_SHIP + 2), crate::npc::NO_GROUP);
        });
    }

    #[test]
    fn the_dice_of_the_world_are_the_scenes_own_and_repeat() {
        let mut scene = scene_with_a_player();
        let draws = |scene: &mut Scene| -> Vec<f64> {
            let mut out = Vec::new();
            let mut state = WorldState::for_test();
            let mut world = SceneWorld::new(scene, NOON, &mut out, &mut state);
            (0..3).map(|_| world.random()).collect()
        };
        let (first, second) = (draws(&mut scene), draws(&mut scene));
        assert_eq!(first, second, "the same seed gives the same draws");
        assert!(first.iter().all(|x| (0.0..1.0).contains(x)));
        let next: Vec<f64> = with_world(&mut scene, NOON, |world| (0..3).map(|_| world.random()).collect());
        let after: Vec<f64> = with_world(&mut scene, NOON, |world| (0..3).map(|_| world.random()).collect());
        assert_ne!(next, after, "and the scene's own dice go on from where they were");
    }
}
