//! The behavior tree runtime: tasks, their statuses, the tree that ticks them. Ported from the client's `behaviors/behaviortree.py`, `tasks.py` and
//! `status.py` (V24.01).
//!
//! The Python tasks point at each other (a task at its parent through an exit observer, at its tree and context through weak references) and
//! pass bound methods around as callbacks. Here the tasks of a tree live in one arena (`TreeCore`) and are named by `TaskId`; a callback is the
//! task it belongs to and the number of its handler (`Observer`); the tree, the behavior's context, the blackboards and the world are handed to a
//! task in a `TaskCtx` while it runs.
//!
//! A tree ticks like the original: every tick the tasks queued are stepped in order until the end marker (a task that runs is queued for the next
//! tick, one that starts is queued at the front), a tree that has asked to be reset is reset at the start of a tick unless a task blocks it.

use std::collections::{HashMap, VecDeque};

use crate::blackboards::{
    Address, BlackboardError, BlackboardManager, MessageData, Observer, Scope, ScopeType, TaskId, TreeId,
};
use crate::env::{SlimItem, World, WorldError, WorldResult, seconds_to_ticks};
use crate::events::{EventKey, EventMessenger, Subject};
use crate::timers::TimerManager;
use crate::value::Value;

/// What a task has come to.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum Status {
    Invalid,
    Success,
    Failure,
    Running,
    Suspended,
    Blocking,
}

/// What can go wrong in a tree: the client's exceptions. A tick that meets one stops where it is and returns it (the tasks and the end marker stay
/// as they were, as they do when the client's tick is unwound).
#[derive(Debug, Clone, PartialEq)]
pub enum BehaviorError {
    /// A composite or decorator with nothing to run (`NoSubTasksError`).
    NoSubTasks,
    /// A decorator with more than one task to decorate (`TooManySubTasksError`).
    TooManySubTasks,
    /// A task's status was invalid when it was updated (`InvalidStatusError`).
    InvalidStatus,
    /// A blackboard the behavior's context has no id for (`BlackboardContextIdNotInBehaviorContextError`).
    BlackboardContextIdNotInBehaviorContext(ScopeType),
    /// A blackboard that was removed and cannot come back (`BlackboardDeletedError`).
    BlackboardDeleted,
    /// A task attribute that is not there (`TaskAttributeMissingError`, or the `AttributeError` of the bundle).
    TaskAttributeMissing(String),
    /// A weighted selector with fewer weights than tasks (`WeightsMissingError`).
    WeightsMissing,
    /// A weighted selector with more weights than tasks (`ToManyWeightsError`).
    TooManyWeights,
    /// A tree or task that is built wrong (`BehaviorAuthoringException`).
    Authoring(String),
    /// A behavior that cannot go on (`UnrecoverableBehaviorError`): the system that owns the trees is to take it down.
    Unrecoverable(String),
    /// What a call to the server raised (`UserError`, `RuntimeError`, `ReferenceError`, `KeyError`) and no task caught.
    World(WorldError),
    /// Anything else a task raises.
    Task(String),
}

impl From<WorldError> for BehaviorError {
    fn from(error: WorldError) -> Self {
        BehaviorError::World(error)
    }
}

impl From<BlackboardError> for BehaviorError {
    fn from(_: BlackboardError) -> Self {
        BehaviorError::BlackboardDeleted
    }
}

/// The attributes of a task (the client's `Bundle`).
#[derive(Debug, Clone, Default, PartialEq)]
pub struct Attrs(HashMap<String, Value>);

impl Attrs {
    pub fn new() -> Self {
        Self::default()
    }

    /// `Bundle(name='...')` style: one attribute more.
    pub fn with(mut self, name: &str, value: impl Into<Value>) -> Self {
        self.0.insert(name.to_string(), value.into());
        self
    }

    pub fn set(&mut self, name: &str, value: impl Into<Value>) {
        self.0.insert(name.to_string(), value.into());
    }

    /// The attribute, or `None` when the task has none.
    pub fn value(&self, name: &str) -> Value {
        self.0.get(name).cloned().unwrap_or(Value::None)
    }

    pub fn get(&self, name: &str) -> Option<&Value> {
        self.0.get(name)
    }

    /// `Task.HasAttribute`: it is there and is not `None`.
    pub fn has(&self, name: &str) -> bool {
        self.0.get(name).is_some_and(|v| !v.is_none())
    }

    pub fn int(&self, name: &str) -> Option<i64> {
        self.0.get(name).and_then(Value::as_int)
    }

    pub fn float(&self, name: &str) -> Option<f64> {
        self.0.get(name).and_then(Value::as_float)
    }

    pub fn boolean(&self, name: &str) -> Option<bool> {
        self.0.get(name).and_then(|v| match v {
            Value::None => None,
            other => Some(other.truthy()),
        })
    }

    pub fn string(&self, name: &str) -> Option<String> {
        self.0.get(name).and_then(Value::as_str).map(str::to_string)
    }

    /// A blackboard address held by the attribute.
    pub fn address(&self, name: &str) -> Option<Address> {
        self.0.get(name).and_then(Address::from_value)
    }

    pub fn iter(&self) -> impl Iterator<Item = (&String, &Value)> {
        self.0.iter()
    }
}

/// What `BlackboardDeletedError` is for a task that catches it.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct BlackboardDeleted;

/// A channel of the blackboard of some scope: what `GetMessageChannelForItemId(itemId, name)` and `GetMessageChannelForGroupId(groupId, name)` answer.
#[derive(Debug, Clone, PartialEq)]
pub struct ChannelRef {
    pub scope: Scope,
    pub name: String,
}

/// A task: the behaviors a node type gives. The base behaviors (`Task`, `Composite`, `Decorator`, `BlockingTask`, `MonitorTask`) are the default
/// methods and the helpers of `composites`, `decorators` and `monitors`; a node that overrides one and calls `super()` in the client calls the
/// helper.
pub trait Task {
    /// The class name (`GetTaskName`).
    fn type_name(&self) -> &'static str;

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        t.set_status_to_running();
    }

    fn on_exit(&mut self, _t: &mut TaskCtx<'_, '_>) {}

    fn update(&mut self, _t: &mut TaskCtx<'_, '_>) {}

    fn clean_up(&mut self, t: &mut TaskCtx<'_, '_>) {
        t.set_status_to_invalid();
    }

    /// A composite's or decorator's `DoSubTaskCompleted`.
    fn do_sub_task_completed(&mut self, t: &mut TaskCtx<'_, '_>, _sub: TaskId) {
        t.raise(BehaviorError::Task(format!("{}.DoSubTaskCompleted is not implemented", self.type_name())));
    }

    /// A message about an item this task subscribed to (`SubscribeItem`): `handler` is the number the task gave when it subscribed.
    fn on_event(&mut self, _t: &mut TaskCtx<'_, '_>, _handler: u16, _args: &[Value]) {}

    /// A call this task asked for at a later time (`Shared::schedule_call`, the client's tasklet that sleeps and then does something for the
    /// task): `handler` is the number the task gave.
    fn on_call(&mut self, _t: &mut TaskCtx<'_, '_>, _handler: u16, _args: &[Value]) {}

    /// A message on a blackboard channel this task subscribed to (`SubscribeToBlackboard`, `channel.AddObserver`).
    fn on_blackboard_message(
        &mut self,
        _t: &mut TaskCtx<'_, '_>,
        _handler: u16,
        _channel: &str,
        _message: Option<&MessageData>,
    ) {
    }
}

/// The numbers of a tick, for whoever watches (`context.statistics`).
#[derive(Debug, Default, Clone, PartialEq)]
pub struct Statistics {
    pub ticks: u64,
    pub task_ticks: u64,
    pub reset_requests: u64,
    pub last_reset_requested_by: Option<String>,
}

/// The ids a behavior's blackboards are found by, and the numbers a behavior lives in: the client's behavior context (a dict with attributes).
#[derive(Debug, Default, Clone)]
pub struct BehaviorContext {
    pub my_item_id: Option<i64>,
    pub my_entity_group_id: Option<i64>,
    pub my_dungeon_spawn_id: Option<i64>,
    pub my_dungeon_spawn_scenario_id: Option<i64>,
    pub my_dungeon_id: Option<i64>,
    pub my_spawn_table_id: Option<i64>,
    pub my_tale_id: Option<i64>,
    pub my_spawn_point_pool_id: Option<i64>,
    pub my_node_graph_instance_id: Option<i64>,
    pub solar_system_id: Option<i64>,
    /// `context.mySlimItem`: the slim item of the entity the behavior is of, as it was when the behavior started.
    pub my_slim_item: Option<SlimItem>,
    /// Anything else the context carries.
    pub extra: HashMap<String, Value>,
    pub statistics: Statistics,
}

impl BehaviorContext {
    /// `context.get(name)`, `getattr(context, name, None)` and `name in context` (a name the context has no value for is `None`): the context by the
    /// names the client gives its fields.
    pub fn get(&self, name: &str) -> Option<Value> {
        let id = |v: Option<i64>| v.map(Value::Int);
        match name {
            "myItemId" => id(self.my_item_id),
            "myEntityGroupId" => id(self.my_entity_group_id),
            "myDungeonSpawnId" => id(self.my_dungeon_spawn_id),
            "myDungeonSpawnScenarioId" => id(self.my_dungeon_spawn_scenario_id),
            "myDungeonId" => id(self.my_dungeon_id),
            "mySpawnTableId" => id(self.my_spawn_table_id),
            "myTaleId" => id(self.my_tale_id),
            "mySpawnPointPoolId" => id(self.my_spawn_point_pool_id),
            "myNodeGraphInstanceId" => id(self.my_node_graph_instance_id),
            "solarSystemId" => id(self.solar_system_id),
            other => self.extra.get(other).cloned(),
        }
    }

    /// The id the context has for a kind of blackboard (`CONTEXT_ID_BY_SCOPE`).
    pub fn id_for_scope(&self, scope_type: ScopeType) -> Option<i64> {
        match scope_type {
            ScopeType::Item => self.my_item_id,
            ScopeType::EntityGroup => self.my_entity_group_id,
            ScopeType::Dungeon => self.my_dungeon_spawn_id,
            ScopeType::Tale => self.my_tale_id,
            ScopeType::SpawnPointPool => self.my_spawn_point_pool_id,
            ScopeType::NodeGraph => self.my_node_graph_instance_id,
        }
    }
}

/// What every tree of a system shares: the clock, the blackboards, the messages about items, the timers, and what is waiting for a tree other than
/// the one that sent it.
#[derive(Debug)]
pub struct Shared {
    /// The simulation clock in the ticks of `gametime` (ten million a second).
    pub now: i64,
    pub blackboards: BlackboardManager,
    pub events: EventMessenger,
    pub timers: TimerManager,
    /// Blackboard messages and item messages for observers in another tree, to be delivered by the system that owns the trees.
    pub pending: Vec<Delivery>,
    /// Calls that tasks asked for at a later time.
    calls: Vec<ScheduledCall>,
    next_call: u64,
    next_tasklet: i64,
}

/// A call a task asked for at a later time.
#[derive(Debug, Clone, PartialEq)]
struct ScheduledCall {
    due: i64,
    seq: u64,
    observer: Observer,
    args: Vec<Value>,
    /// The tasklet the call is a part of, which can be killed.
    tasklet: Option<i64>,
}

impl Shared {
    pub fn new(max_history_length: usize) -> Self {
        Self {
            now: 0,
            blackboards: BlackboardManager::new(max_history_length),
            events: EventMessenger::new(),
            timers: TimerManager::new(),
            pending: Vec::new(),
            calls: Vec::new(),
            next_call: 0,
            next_tasklet: 1,
        }
    }
}

impl Shared {
    /// What a tasklet that sleeps `seconds` (`uthread2.SleepSim`) and then calls a task's method is here: the call is told to the task when the
    /// time comes (`fire_due_calls`, and then the system delivers it).
    pub fn schedule_call(&mut self, seconds: f64, observer: Observer, args: Vec<Value>) {
        self.push_call(None, seconds, observer, args);
    }

    fn push_call(&mut self, tasklet: Option<i64>, seconds: f64, observer: Observer, args: Vec<Value>) {
        let due = self.now + seconds_to_ticks(seconds);
        self.calls.push(ScheduledCall { due, seq: self.next_call, observer, args, tasklet });
        self.next_call += 1;
    }

    /// A tasklet that can be killed (`uthread2.StartTasklet`, whose result `.kill()` is called on): its number. The calls it makes are
    /// `schedule_tasklet_call`s.
    pub fn new_tasklet(&mut self) -> i64 {
        let id = self.next_tasklet;
        self.next_tasklet += 1;
        id
    }

    /// The next step of a tasklet: a call after `seconds`, that goes with the tasklet.
    pub fn schedule_tasklet_call(
        &mut self,
        tasklet: i64,
        seconds: f64,
        observer: Observer,
        args: Vec<Value>,
    ) {
        self.push_call(Some(tasklet), seconds, observer, args);
    }

    /// `tasklet.kill()`: what it had still to do is not done.
    pub fn kill_tasklet(&mut self, tasklet: i64) {
        self.calls.retain(|call| call.tasklet != Some(tasklet));
    }

    /// The calls that are due at `now`, in the order they were due (then the order they were asked for), are queued for delivery.
    pub fn fire_due_calls(&mut self) {
        let now = self.now;
        let mut due: Vec<ScheduledCall> = self.calls.iter().filter(|c| c.due <= now).cloned().collect();
        due.sort_by_key(|c| (c.due, c.seq));
        self.calls.retain(|c| c.due > now);
        for call in due {
            self.pending.push(Delivery::Call { observer: call.observer, args: call.args });
        }
    }

    pub fn scheduled_call_count(&self) -> usize {
        self.calls.len()
    }

    /// When the next scheduled call is due, in ticks of the clock.
    pub fn next_call_due(&self) -> Option<i64> {
        self.calls.iter().map(|c| c.due).min()
    }

    /// The timers that have run out: each tells its channel `None` (`Timer.ClearTimer`). The observers are told by the system that owns the trees
    /// (`pending`), whichever tree they are in.
    pub fn fire_due_timers(&mut self) {
        for timer in self.timers.take_due(self.now) {
            self.finish_timer(&timer);
        }
    }

    /// `TimerManager.ClearTimer(timerID)`: a timer that is stopped early tells its channel `None` as well.
    pub fn clear_timer(&mut self, id: i64) {
        if let Some(timer) = self.timers.clear_timer(id) {
            self.finish_timer(&timer);
        }
    }

    fn finish_timer(&mut self, timer: &crate::timers::Timer) {
        let scope = Scope { scope_type: timer.address.scope_type, id: timer.scope_id };
        let now = self.now;
        if let Ok(board) = self.blackboards.get_blackboard(scope) {
            let (message, observers) = board.send_message(&timer.address.name, now, Value::None);
            for observer in observers {
                self.pending.push(Delivery::Blackboard {
                    observer,
                    channel: timer.address.name.clone(),
                    message: message.clone(),
                });
            }
        }
    }
}

/// A message on its way to an observer in another tree.
#[derive(Debug, Clone, PartialEq)]
pub enum Delivery {
    Blackboard { observer: Observer, channel: String, message: Option<MessageData> },
    Event { observer: Observer, args: Vec<Value> },
    Call { observer: Observer, args: Vec<Value> },
}

/// What a tree tells whoever watches it (`MESSAGE_*`).
#[derive(Debug, Clone, PartialEq)]
pub enum TreeMessage {
    RequestReset(Option<TaskId>),
    Step(Option<TaskId>),
    Reset,
    TickDone,
    BlockReset(bool, TaskId),
}

/// Why a task's exit is told to another task.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum ExitObserver {
    None,
    Parent(TaskId),
    Root,
}

struct Slot {
    node: Option<Box<dyn Task>>,
    type_name: &'static str,
    status: Status,
    exit_observer: ExitObserver,
    sub_tasks: Vec<TaskId>,
    attributes: Attrs,
    /// `context.task_cache[task]`.
    cache: HashMap<String, Value>,
}

/// The arena of a tree's tasks, its queue and its flags.
pub struct TreeCore {
    pub id: TreeId,
    /// The id the behavior was loaded by: a number for an authored tree, a name for a scripted one.
    pub behavior_id: Option<Value>,
    pub scope_type: Option<ScopeType>,
    slots: Vec<Slot>,
    queue: VecDeque<Option<TaskId>>,
    root: Option<TaskId>,
    reset_requested: bool,
    reset_blocked: bool,
    fault: Option<BehaviorError>,
    /// Tasks that ended from one of their own handlers: their `OnExit` runs when the handler is done.
    deferred_exits: Vec<TaskId>,
    /// The messages the tree sent (kept when `record_messages` is set).
    pub messages: Vec<TreeMessage>,
    pub record_messages: bool,
}

/// A behavior tree.
pub struct BehaviorTree {
    pub core: TreeCore,
}

impl BehaviorTree {
    pub fn new(id: TreeId, behavior_id: Option<Value>, scope_type: Option<ScopeType>) -> Self {
        Self {
            core: TreeCore {
                id,
                behavior_id,
                scope_type,
                slots: Vec::new(),
                queue: VecDeque::new(),
                root: None,
                reset_requested: false,
                reset_blocked: false,
                fault: None,
                deferred_exits: Vec::new(),
                messages: Vec::new(),
                record_messages: false,
            },
        }
    }

    /// Makes a task (not yet part of anything).
    pub fn add_task(&mut self, node: Box<dyn Task>, attributes: Attrs) -> TaskId {
        let type_name = node.type_name();
        self.core.slots.push(Slot {
            node: Some(node),
            type_name,
            status: Status::Invalid,
            exit_observer: ExitObserver::None,
            sub_tasks: Vec::new(),
            attributes,
            cache: HashMap::new(),
        });
        self.core.slots.len() - 1
    }

    /// `Composite.AddSubTask`: the sub task's exit is told to its parent.
    pub fn add_sub_task(&mut self, parent: TaskId, child: TaskId) {
        self.core.slots[child].exit_observer = ExitObserver::Parent(parent);
        self.core.slots[parent].sub_tasks.push(child);
    }

    /// `StartRootTask`: the root's exit asks for a reset, and the root is queued.
    pub fn start_root_task(&mut self, root: TaskId) {
        self.core.root = Some(root);
        self.core.slots[root].exit_observer = ExitObserver::Root;
        self.core.queue.push_front(Some(root));
    }

    pub fn root_task(&self) -> Option<TaskId> {
        self.core.root
    }

    pub fn status_of(&self, task: TaskId) -> Status {
        self.core.slots[task].status
    }

    pub fn sub_tasks_of(&self, task: TaskId) -> &[TaskId] {
        &self.core.slots[task].sub_tasks
    }

    pub fn attributes_of(&self, task: TaskId) -> &Attrs {
        &self.core.slots[task].attributes
    }

    pub fn type_name_of(&self, task: TaskId) -> &'static str {
        self.core.slots[task].type_name
    }

    pub fn task_count(&self) -> usize {
        self.core.slots.len()
    }

    /// `GetTaskList`: the queue.
    pub fn task_list(&self) -> Vec<Option<TaskId>> {
        self.core.queue.iter().copied().collect()
    }

    pub fn has_reset_request(&self) -> bool {
        self.core.reset_requested
    }

    pub fn is_reset_blocked(&self) -> bool {
        self.core.reset_blocked
    }

    /// `Tick`.
    pub fn tick(
        &mut self,
        ctx: &mut BehaviorContext,
        shared: &mut Shared,
        world: &mut dyn World,
    ) -> Result<(), BehaviorError> {
        let mut cx = Cx { core: &mut self.core, ctx, shared, world };
        cx.tick()
    }

    /// `RequestReset` from outside the tree.
    pub fn request_reset(&mut self, ctx: &mut BehaviorContext, shared: &mut Shared, world: &mut dyn World) {
        let mut cx = Cx { core: &mut self.core, ctx, shared, world };
        cx.request_reset(None);
    }

    /// `Release`: cleans the root up and lets go.
    pub fn release(&mut self, ctx: &mut BehaviorContext, shared: &mut Shared, world: &mut dyn World) {
        let mut cx = Cx { core: &mut self.core, ctx, shared, world };
        if let Some(root) = cx.core.root {
            cx.clean_up_task(root);
        }
        cx.core.root = None;
        shared.events.forget_tree(self.core.id);
    }

    /// Tells a task of this tree of a blackboard message or an event (what the system that owns the trees does with a `Delivery`).
    pub fn deliver(
        &mut self,
        ctx: &mut BehaviorContext,
        shared: &mut Shared,
        world: &mut dyn World,
        delivery: &Delivery,
    ) -> Result<(), BehaviorError> {
        let mut cx = Cx { core: &mut self.core, ctx, shared, world };
        match delivery {
            Delivery::Blackboard { observer, channel, message } => {
                cx.dispatch_blackboard(*observer, channel, message.as_ref())
            }
            Delivery::Event { observer, args } => cx.dispatch_event(*observer, args),
            Delivery::Call { observer, args } => cx.dispatch_call(*observer, args),
        }
        match cx.core.fault.take() {
            Some(error) => Err(error),
            None => Ok(()),
        }
    }

    /// The first error a task raised since the last tick, if any (a delivery can raise one outside a tick).
    pub fn take_fault(&mut self) -> Option<BehaviorError> {
        self.core.fault.take()
    }
}

/// Everything a task acts on: its tree, its behavior's context, what the trees share and the world.
pub struct Cx<'a> {
    pub core: &'a mut TreeCore,
    pub ctx: &'a mut BehaviorContext,
    pub shared: &'a mut Shared,
    pub world: &'a mut dyn World,
}

/// A task's view of everything: the task it is, and what it acts on.
pub struct TaskCtx<'a, 'b> {
    pub cx: &'a mut Cx<'b>,
    pub id: TaskId,
}

impl Cx<'_> {
    pub fn faulted(&self) -> bool {
        self.core.fault.is_some()
    }

    /// Raises an error: the first one is kept, and the tick stops.
    pub fn raise(&mut self, error: BehaviorError) {
        if self.core.fault.is_none() {
            self.core.fault = Some(error);
        }
    }

    fn send_tree_message(&mut self, message: TreeMessage) {
        if self.core.record_messages {
            self.core.messages.push(message);
        }
    }

    pub fn status(&self, id: TaskId) -> Status {
        self.core.slots[id].status
    }

    pub fn set_status(&mut self, id: TaskId, status: Status) {
        self.core.slots[id].status = status;
    }

    pub fn start_task(&mut self, id: TaskId) {
        self.core.queue.push_front(Some(id));
    }

    pub fn start_task_next_tick(&mut self, id: TaskId) {
        self.core.queue.push_back(Some(id));
    }

    /// Runs a node's method with the node taken out of its slot (a node may call for others, never for itself).
    fn with_node<R>(
        &mut self,
        id: TaskId,
        f: impl FnOnce(&mut dyn Task, &mut TaskCtx<'_, '_>) -> R,
    ) -> Option<R> {
        let mut node = self.core.slots[id].node.take()?;
        let result = {
            let mut t = TaskCtx { cx: self, id };
            f(node.as_mut(), &mut t)
        };
        self.core.slots[id].node = Some(node);
        // a task that ended from one of its own handlers has its `OnExit` now
        if let Some(at) = self.core.deferred_exits.iter().position(|d| *d == id) {
            self.core.deferred_exits.remove(at);
            self.run_on_exit(id);
        }
        Some(result)
    }

    /// `task.OnExit()`.
    fn run_on_exit(&mut self, id: TaskId) {
        let Some(mut node) = self.core.slots[id].node.take() else { return };
        {
            let mut t = TaskCtx { cx: self, id };
            node.on_exit(&mut t);
        }
        self.core.slots[id].node = Some(node);
    }

    /// `Task.CleanUp` of a task.
    pub fn clean_up_task(&mut self, id: TaskId) {
        self.with_node(id, |node, t| node.clean_up(t));
    }

    /// `Task.Tick`.
    fn tick_task(&mut self, id: TaskId) {
        if self.status(id) == Status::Invalid {
            self.with_node(id, |node, t| node.on_enter(t));
            if self.faulted() {
                return;
            }
        }
        self.with_node(id, |node, t| node.update(t));
        if self.faulted() {
            return;
        }
        self.on_status_update(id);
    }

    /// `status.OnUpdated(task)`.
    pub fn on_status_update(&mut self, id: TaskId) {
        match self.status(id) {
            Status::Invalid => self.raise(BehaviorError::InvalidStatus),
            Status::Success | Status::Failure => self.exit_task(id),
            Status::Running | Status::Blocking => self.start_task_next_tick(id),
            Status::Suspended => {}
        }
    }

    /// `Task.Exit`: whoever watches the exit is told, then the task's `OnExit` runs.
    pub fn exit_task(&mut self, id: TaskId) {
        match self.core.slots[id].exit_observer {
            ExitObserver::Parent(parent) => self.on_sub_task_completed(parent, id),
            ExitObserver::Root => self.request_reset(Some(id)),
            ExitObserver::None => {}
        }
        if self.faulted() {
            return;
        }
        if self.core.slots[id].node.is_none() {
            // the task is in one of its handlers: its `OnExit` follows the handler
            self.core.deferred_exits.push(id);
            return;
        }
        self.run_on_exit(id);
    }

    /// `Composite.OnSubTaskCompleted`.
    fn on_sub_task_completed(&mut self, parent: TaskId, sub: TaskId) {
        self.with_node(parent, |node, t| node.do_sub_task_completed(t, sub));
        if self.faulted() {
            return;
        }
        self.on_status_update(parent);
    }

    /// `RequestReset`.
    pub fn request_reset(&mut self, requested_by: Option<TaskId>) {
        self.core.reset_requested = true;
        self.send_tree_message(TreeMessage::RequestReset(requested_by));
        self.ctx.statistics.reset_requests += 1;
        self.ctx.statistics.last_reset_requested_by =
            requested_by.map(|t| self.core.slots[t].type_name.to_string());
    }

    pub fn block_reset(&mut self, task: TaskId) {
        self.send_tree_message(TreeMessage::BlockReset(true, task));
        self.core.reset_blocked = true;
    }

    pub fn unblock_reset(&mut self, task: TaskId) {
        self.send_tree_message(TreeMessage::BlockReset(false, task));
        self.core.reset_blocked = false;
    }

    /// `Reset`: forgets what is queued, cleans the root up and starts it again.
    fn reset(&mut self) {
        self.core.queue.clear();
        if let Some(root) = self.core.root {
            self.clean_up_task(root);
            self.start_task(root);
        }
        self.send_tree_message(TreeMessage::Reset);
    }

    fn process_reset_request(&mut self) {
        if self.core.reset_requested && !self.core.reset_blocked {
            self.core.reset_requested = false;
            self.reset();
        }
    }

    /// `Step`: one task of the queue. False at the end marker.
    fn step(&mut self) -> bool {
        let Some(entry) = self.core.queue.pop_front() else { return false };
        let more = match entry {
            None => false,
            Some(task) => {
                self.tick_task(task);
                self.ctx.statistics.task_ticks += 1;
                true
            }
        };
        self.send_tree_message(TreeMessage::Step(entry));
        more
    }

    /// `Tick`.
    fn tick(&mut self) -> Result<(), BehaviorError> {
        self.core.fault = None;
        self.process_reset_request();
        self.core.queue.push_back(None);
        while self.step() {
            if let Some(error) = self.core.fault.take() {
                return Err(error);
            }
        }
        if let Some(error) = self.core.fault.take() {
            return Err(error);
        }
        self.send_tree_message(TreeMessage::TickDone);
        self.ctx.statistics.ticks += 1;
        Ok(())
    }

    // ---- timers ----

    /// `timerManager.StartTimer(seconds, channel)`: the timer's id is sent to the channel now, `None` when it runs out (`Shared::fire_due_timers`).
    pub fn start_timer(&mut self, seconds: f64, address: &Address) {
        let Some(scope) = self.scope_of(address) else { return };
        let id = self.shared.timers.start_timer(
            self.shared.now,
            seconds_to_ticks(seconds),
            scope.id,
            address.clone(),
        );
        self.send_blackboard_value(address, Value::Int(id));
    }

    // ---- blackboards ----

    /// What an `ExceptionEater` does: what was raised while `f` ran is dropped (what was raised before it stays).
    pub fn eat_exceptions(&mut self, f: impl FnOnce(&mut Self)) {
        let had_fault = self.core.fault.is_some();
        f(self);
        if !had_fault {
            self.core.fault = None;
        }
    }

    /// The last value of a channel for a task that catches `BlackboardDeletedError`: `Err(BlackboardDeleted)` for a blackboard that is gone.
    pub fn last_blackboard_value_unless_deleted(
        &mut self,
        address: &Address,
    ) -> Result<Value, BlackboardDeleted> {
        let Some(scope) = self.scope_of(address) else { return Ok(Value::None) };
        match self.shared.blackboards.message_channel(scope, &address.name) {
            Ok(channel) => Ok(channel.last_message_value()),
            Err(BlackboardError::Deleted) => Err(BlackboardDeleted),
            Err(error) => {
                self.raise(error.into());
                Ok(Value::None)
            }
        }
    }

    /// `scopes.GetChannelFromAddress`: the scope of an address in this behavior's context.
    pub fn scope_of(&mut self, address: &Address) -> Option<Scope> {
        match self.ctx.id_for_scope(address.scope_type) {
            Some(id) => Some(Scope { scope_type: address.scope_type, id }),
            None => {
                self.raise(BehaviorError::BlackboardContextIdNotInBehaviorContext(address.scope_type));
                None
            }
        }
    }

    /// The last value of a channel (`None` when it has none).
    pub fn last_blackboard_value(&mut self, address: &Address) -> Value {
        let Some(scope) = self.scope_of(address) else { return Value::None };
        match self.shared.blackboards.message_channel(scope, &address.name) {
            Ok(channel) => channel.last_message_value(),
            Err(error) => {
                self.raise(error.into());
                Value::None
            }
        }
    }

    pub fn last_blackboard_value_with_max_age(&mut self, address: &Address, max_age: i64) -> Value {
        let now = self.shared.now;
        let Some(scope) = self.scope_of(address) else { return Value::None };
        match self.shared.blackboards.message_channel(scope, &address.name) {
            Ok(channel) => channel.last_message_value_with_max_age(now, max_age),
            Err(error) => {
                self.raise(error.into());
                Value::None
            }
        }
    }

    /// `SendBlackboardValue`: keeps the message and tells the observers.
    pub fn send_blackboard_value(&mut self, address: &Address, value: Value) {
        let Some(scope) = self.scope_of(address) else { return };
        self.send_to_channel(scope, &address.name, value);
    }

    /// `blackboardManager.GetBlackboard(scope).GetMessageChannel(name).SendMessage(value)`.
    pub fn send_to_channel(&mut self, scope: Scope, name: &str, value: Value) {
        let now = self.shared.now;
        let sent = match self.shared.blackboards.get_blackboard(scope) {
            Ok(board) => board.send_message(name, now, value),
            Err(error) => {
                self.raise(error.into());
                return;
            }
        };
        self.tell_blackboard_observers(name, sent.0, sent.1);
    }

    /// `blackboardManager.GetBlackboard(scope).GetMessageChannel(name).GetLastMessageValue()`.
    pub fn last_value_of_channel(&mut self, scope: Scope, name: &str) -> Value {
        match self.shared.blackboards.message_channel(scope, name) {
            Ok(channel) => channel.last_message_value(),
            Err(error) => {
                self.raise(error.into());
                Value::None
            }
        }
    }

    /// Tells the observers of a channel of a message: the tasks of this tree at once, the others through the system.
    pub fn tell_blackboard_observers(
        &mut self,
        channel: &str,
        message: Option<MessageData>,
        observers: Vec<Observer>,
    ) {
        for observer in observers {
            if observer.tree == self.core.id {
                self.dispatch_blackboard(observer, channel, message.as_ref());
            } else {
                self.shared.pending.push(Delivery::Blackboard {
                    observer,
                    channel: channel.to_string(),
                    message: message.clone(),
                });
            }
        }
    }

    pub fn dispatch_blackboard(&mut self, observer: Observer, channel: &str, message: Option<&MessageData>) {
        if observer.task >= self.core.slots.len() {
            return;
        }
        self.with_node(observer.task, |node, t| {
            node.on_blackboard_message(t, observer.handler, channel, message)
        });
    }

    pub fn dispatch_call(&mut self, observer: Observer, args: &[Value]) {
        if observer.task >= self.core.slots.len() {
            return;
        }
        self.with_node(observer.task, |node, t| node.on_call(t, observer.handler, args));
    }

    pub fn dispatch_event(&mut self, observer: Observer, args: &[Value]) {
        if observer.task >= self.core.slots.len() {
            return;
        }
        self.with_node(observer.task, |node, t| node.on_event(t, observer.handler, args));
    }

    /// A message about an item: its subscribers in this tree are told at once, those in others through the system.
    pub fn send_event(&mut self, item_id: i64, key: impl Into<EventKey>, args: &[Value]) {
        self.send_message_about(Subject::Item(item_id), key, args);
    }

    /// A message about a subject (an item, a bubble, a group, the world): its subscribers in this tree are told at once, those in others through the
    /// system.
    pub fn send_message_about(&mut self, subject: Subject, key: impl Into<EventKey>, args: &[Value]) {
        for observer in self.shared.events.subscribers_of(subject, key) {
            if observer.tree == self.core.id {
                self.dispatch_event(observer, args);
            } else {
                self.shared.pending.push(Delivery::Event { observer, args: args.to_vec() });
            }
        }
    }
}

impl TaskCtx<'_, '_> {
    pub fn raise(&mut self, error: BehaviorError) {
        self.cx.raise(error);
    }

    /// `try: ... except (SomeErrors)`: what `f` raised is given back (and is not raised) for the task to say which of it it catches; what it
    /// raised before stays.
    pub fn catch_exceptions(&mut self, f: impl FnOnce(&mut TaskCtx<'_, '_>)) -> Option<BehaviorError> {
        let earlier = self.cx.core.fault.take();
        f(self);
        let caught = self.cx.core.fault.take();
        self.cx.core.fault = earlier;
        caught
    }

    /// `with ExceptionEater(...)`: what is raised in `f` is dropped.
    pub fn eat_exceptions(&mut self, f: impl FnOnce(&mut TaskCtx<'_, '_>)) {
        let had_fault = self.cx.faulted();
        f(self);
        if !had_fault {
            self.cx.core.fault = None;
        }
    }

    /// `GetLastBlackboardValue` of a task that catches `BlackboardDeletedError`.
    pub fn get_last_blackboard_value_unless_deleted(
        &mut self,
        address: &Address,
    ) -> Result<Value, BlackboardDeleted> {
        self.cx.last_blackboard_value_unless_deleted(address)
    }

    /// The answer of a call to the server: what it raised is raised here, and the tick stops.
    pub fn ok<T>(&mut self, result: WorldResult<T>) -> Option<T> {
        match result {
            Ok(value) => Some(value),
            Err(error) => {
                self.raise(error.into());
                None
            }
        }
    }

    /// An error was raised in this tick: what the client does after it does not run (its exception unwinds the tick).
    pub fn faulted(&self) -> bool {
        self.cx.faulted()
    }

    pub fn tree_id(&self) -> TreeId {
        self.cx.core.id
    }

    /// The observer for one of this task's handlers.
    pub fn observer(&self, handler: u16) -> Observer {
        Observer { tree: self.cx.core.id, task: self.id, handler }
    }

    pub fn ctx(&mut self) -> &mut BehaviorContext {
        self.cx.ctx
    }

    pub fn shared(&mut self) -> &mut Shared {
        self.cx.shared
    }

    pub fn world(&mut self) -> &mut dyn World {
        &mut *self.cx.world
    }

    /// The simulation clock.
    pub fn now(&self) -> i64 {
        self.cx.shared.now
    }

    pub fn attributes(&self) -> Attrs {
        self.cx.core.slots[self.id].attributes.clone()
    }

    pub fn attribute(&self, name: &str) -> Value {
        self.cx.core.slots[self.id].attributes.value(name)
    }

    pub fn has_attribute(&self, name: &str) -> bool {
        self.cx.core.slots[self.id].attributes.has(name)
    }

    pub fn set_attribute(&mut self, name: &str, value: impl Into<Value>) {
        self.cx.core.slots[self.id].attributes.set(name, value);
    }

    /// `self.attributes.name` of a task that needs it: an attribute that is not there raises, and the tick stops.
    pub fn required(&mut self, name: &str) -> Option<Value> {
        match self.cx.core.slots[self.id].attributes.get(name) {
            Some(value) => Some(value.clone()),
            None => {
                self.raise(BehaviorError::TaskAttributeMissing(name.to_string()));
                None
            }
        }
    }

    pub fn required_address(&mut self, name: &str) -> Option<Address> {
        let value = self.required(name)?;
        let address = Address::from_value(&value);
        if address.is_none() {
            self.raise(BehaviorError::Authoring(format!(
                "attribute {name} of {} is not a blackboard address",
                self.task_name()
            )));
        }
        address
    }

    pub fn required_float(&mut self, name: &str) -> Option<f64> {
        let value = self.required(name)?;
        let number = value.as_float();
        if number.is_none() {
            self.raise(BehaviorError::Authoring(format!(
                "attribute {name} of {} is not a number",
                self.task_name()
            )));
        }
        number
    }

    pub fn required_int(&mut self, name: &str) -> Option<i64> {
        let value = self.required(name)?;
        let number = value.as_int();
        if number.is_none() {
            self.raise(BehaviorError::Authoring(format!(
                "attribute {name} of {} is not a whole number",
                self.task_name()
            )));
        }
        number
    }

    pub fn required_string(&mut self, name: &str) -> Option<String> {
        let value = self.required(name)?;
        let text = value.as_str().map(str::to_string);
        if text.is_none() {
            self.raise(BehaviorError::Authoring(format!(
                "attribute {name} of {} is not text",
                self.task_name()
            )));
        }
        text
    }

    /// `self.GetLastBlackboardValue(self.attributes.<attribute>)`: the last value on the channel the attribute addresses (raises when the attribute is
    /// missing or is no address).
    pub fn value_at(&mut self, attribute: &str) -> Option<Value> {
        let address = self.required_address(attribute)?;
        let value = self.get_last_blackboard_value(&address);
        if self.faulted() { None } else { Some(value) }
    }

    /// A value the task uses as an id (an item, a ball, a group): what a blackboard holds where the client passes it on to the server. Anything that
    /// is not a whole number raises as the server's lookup would.
    pub fn expect_id(&mut self, value: &Value) -> Option<i64> {
        let id = value.as_int();
        if id.is_none() {
            self.raise(BehaviorError::World(WorldError::Key(value.to_string())));
        }
        id
    }

    /// `self.behaviorTree.GetBehaviorId()`.
    pub fn behavior_id(&self) -> Value {
        self.cx.core.behavior_id.clone().unwrap_or(Value::None)
    }

    /// `context.myItemId`.
    pub fn my_item_id(&mut self) -> Option<i64> {
        let id = self.cx.ctx.my_item_id;
        if id.is_none() {
            self.raise(BehaviorError::Task("the behavior context has no myItemId".to_string()));
        }
        id
    }

    pub fn task_name(&self) -> &'static str {
        self.cx.core.slots[self.id].type_name
    }

    // ---- status ----

    pub fn status(&self) -> Status {
        self.cx.status(self.id)
    }

    pub fn set_status(&mut self, status: Status) {
        self.cx.set_status(self.id, status);
    }

    pub fn set_status_to_invalid(&mut self) {
        self.set_status(Status::Invalid);
    }

    pub fn set_status_to_success(&mut self) {
        self.set_status(Status::Success);
    }

    pub fn set_status_to_failed(&mut self) {
        self.set_status(Status::Failure);
    }

    pub fn set_status_to_suspended(&mut self) {
        self.set_status(Status::Suspended);
    }

    pub fn set_status_to_running(&mut self) {
        self.set_status(Status::Running);
    }

    pub fn set_status_to_blocked(&mut self) {
        self.set_status(Status::Blocking);
    }

    /// `self.status.OnUpdated(self)`: what the task's status asks of the tree (a finished task exits, a running one is queued for the next tick).
    pub fn on_status_updated(&mut self) {
        let me = self.id;
        self.cx.on_status_update(me);
    }

    pub fn is_invalid(&self) -> bool {
        self.status() == Status::Invalid
    }

    pub fn is_suspended(&self) -> bool {
        self.status() == Status::Suspended
    }

    pub fn is_running(&self) -> bool {
        self.status() == Status::Running
    }

    pub fn is_successful(&self) -> bool {
        self.status() == Status::Success
    }

    pub fn is_failed(&self) -> bool {
        self.status() == Status::Failure
    }

    pub fn is_blocked(&self) -> bool {
        self.status() == Status::Blocking
    }

    pub fn set_status_to_success_if_true_else_to_failed(&mut self, value: bool) {
        if value {
            self.set_status_to_success();
        } else {
            self.set_status_to_failed();
        }
    }

    // ---- the tree ----

    pub fn sub_tasks(&self) -> Vec<TaskId> {
        self.cx.core.slots[self.id].sub_tasks.clone()
    }

    pub fn sub_task_count(&self) -> usize {
        self.cx.core.slots[self.id].sub_tasks.len()
    }

    pub fn sub_task_status(&self, sub: TaskId) -> Status {
        self.cx.status(sub)
    }

    pub fn start_task(&mut self, task: TaskId) {
        self.cx.start_task(task);
    }

    pub fn start_task_next_tick(&mut self, task: TaskId) {
        self.cx.start_task_next_tick(task);
    }

    pub fn clean_up_task(&mut self, task: TaskId) {
        self.cx.clean_up_task(task);
    }

    /// `behaviorTree.RequestReset(requestedBy=self)`.
    pub fn request_reset(&mut self) {
        let me = self.id;
        self.cx.request_reset(Some(me));
    }

    pub fn block_reset(&mut self) {
        let me = self.id;
        self.cx.block_reset(me);
    }

    pub fn unblock_reset(&mut self) {
        let me = self.id;
        self.cx.unblock_reset(me);
    }

    // ---- what the task remembers (`context.task_cache[task]`) ----

    pub fn get_context_value(&self, name: &str) -> Value {
        self.cx.core.slots[self.id].cache.get(name).cloned().unwrap_or(Value::None)
    }

    pub fn set_context_value(&mut self, name: &str, value: Value) {
        self.cx.core.slots[self.id].cache.insert(name.to_string(), value);
    }

    pub fn has_context_value(&self, name: &str) -> bool {
        self.cx.core.slots[self.id].cache.contains_key(name)
    }

    // ---- blackboards ----

    pub fn get_last_blackboard_value(&mut self, address: &Address) -> Value {
        self.cx.last_blackboard_value(address)
    }

    pub fn get_last_blackboard_value_with_max_age(&mut self, address: &Address, max_age: i64) -> Value {
        self.cx.last_blackboard_value_with_max_age(address, max_age)
    }

    pub fn send_blackboard_value(&mut self, address: &Address, value: Value) {
        self.cx.send_blackboard_value(address, value);
    }

    /// `SubscribeToBlackboard(address, handler)`.
    pub fn start_timer(&mut self, seconds: f64, address: &Address) {
        self.cx.start_timer(seconds, address);
    }

    /// A tasklet that sleeps for `seconds` of simulation time and then calls `on_call(handler, args)` of this task.
    pub fn schedule_call(&mut self, seconds: f64, handler: u16, args: Vec<Value>) {
        let observer = self.observer(handler);
        self.cx.shared.schedule_call(seconds, observer, args);
    }

    /// `uthread2.StartTasklet(...)` of something that is to be killed: the number of a new tasklet, whose steps are `schedule_tasklet_call`s.
    pub fn new_tasklet(&mut self) -> i64 {
        self.cx.shared.new_tasklet()
    }

    /// The next step of a tasklet: `on_call(handler, args)` of this task after `seconds` of simulation time, unless the tasklet is killed.
    pub fn schedule_tasklet_call(&mut self, tasklet: i64, seconds: f64, handler: u16, args: Vec<Value>) {
        let observer = self.observer(handler);
        self.cx.shared.schedule_tasklet_call(tasklet, seconds, observer, args);
    }

    /// `tasklet.kill()`.
    pub fn kill_tasklet(&mut self, tasklet: i64) {
        self.cx.shared.kill_tasklet(tasklet);
    }

    /// This task's number in its tree, for the calls that name a task (`StartTaskNextTick(self)`).
    pub fn task_id(&self) -> TaskId {
        self.id
    }

    /// `self.behaviorTree.StartTaskNextTick(self)`.
    pub fn start_self_next_tick(&mut self) {
        let id = self.id;
        self.cx.start_task_next_tick(id);
    }

    /// `GetChannelFromAddress(address)`: the channel of the blackboard the address means for this task.
    pub fn channel_from_address(&mut self, address: &Address) -> Option<ChannelRef> {
        let scope = self.cx.scope_of(address)?;
        Some(ChannelRef { scope, name: address.name.clone() })
    }

    /// `blackboardManager.HasBlackboard(scope)`.
    pub fn has_blackboard(&self, scope: Scope) -> bool {
        self.cx.shared.blackboards.has_blackboard(&scope)
    }

    /// `blackboardManager.GetBlackboard(scope).SendMessage(channel, value)`.
    pub fn send_to_blackboard(&mut self, scope: Scope, channel: &str, value: Value) {
        self.cx.send_to_channel(scope, channel, value);
    }

    /// `timerManager.ClearTimer(timer_id)`.
    pub fn clear_timer(&mut self, timer_id: i64) {
        self.cx.shared.clear_timer(timer_id);
    }

    pub fn subscribe_to_blackboard(&mut self, address: &Address, handler: u16) {
        let observer = self.observer(handler);
        let Some(scope) = self.cx.scope_of(address) else { return };
        match self.cx.shared.blackboards.message_channel(scope, &address.name) {
            Ok(channel) => channel.add_observer(observer),
            Err(error) => self.cx.raise(error.into()),
        }
    }

    /// `SubscribeToBlackboard` of a task that catches `BlackboardDeletedError`.
    pub fn subscribe_to_blackboard_unless_deleted(
        &mut self,
        address: &Address,
        handler: u16,
    ) -> Result<(), BlackboardDeleted> {
        let observer = self.observer(handler);
        let Some(scope) = self.cx.scope_of(address) else { return Ok(()) };
        match self.cx.shared.blackboards.message_channel(scope, &address.name) {
            Ok(channel) => {
                channel.add_observer(observer);
                Ok(())
            }
            Err(BlackboardError::Deleted) => Err(BlackboardDeleted),
            Err(error) => {
                self.cx.raise(error.into());
                Ok(())
            }
        }
    }

    /// `UnsubscribeToBlackboard`: a blackboard that is gone is no error here.
    pub fn unsubscribe_to_blackboard(&mut self, address: &Address, handler: u16) {
        let observer = self.observer(handler);
        let Some(scope) = self.cx.scope_of(address) else { return };
        if let Ok(channel) = self.cx.shared.blackboards.message_channel(scope, &address.name) {
            channel.remove_observer(&observer);
        }
    }

    /// `GetMessageChannelForItemId(itemId, name)`.
    pub fn channel_for_item(&self, item_id: i64, name: &str) -> ChannelRef {
        ChannelRef { scope: crate::blackboards::item_scope(item_id), name: name.to_string() }
    }

    /// `GetMessageChannelForGroupId(groupId, name)`.
    pub fn channel_for_group(&self, group_id: i64, name: &str) -> ChannelRef {
        ChannelRef { scope: crate::blackboards::entity_group_scope(group_id), name: name.to_string() }
    }

    /// `channel.GetLastMessageValue()`.
    pub fn channel_last_value(&mut self, channel: &ChannelRef) -> Value {
        self.cx.last_value_of_channel(channel.scope, &channel.name)
    }

    /// `channel.GetLastMessage() is not None`: whether a message was ever sent on the channel (one whose value is `None` counts).
    pub fn channel_has_message(&mut self, channel: &ChannelRef) -> bool {
        match self.cx.shared.blackboards.message_channel(channel.scope, &channel.name) {
            Ok(c) => c.last_message().is_some(),
            Err(error) => {
                self.cx.raise(error.into());
                false
            }
        }
    }

    /// `channel.GetLastMessageValue()` of a task that catches `BlackboardDeletedError`.
    pub fn get_channel_last_value_unless_deleted(
        &mut self,
        channel: &ChannelRef,
    ) -> Result<Value, BlackboardDeleted> {
        match self.cx.shared.blackboards.message_channel(channel.scope, &channel.name) {
            Ok(c) => Ok(c.last_message_value()),
            Err(BlackboardError::Deleted) => Err(BlackboardDeleted),
            Err(error) => {
                self.cx.raise(error.into());
                Ok(Value::None)
            }
        }
    }

    /// `channel.SendMessage(value)`.
    pub fn channel_send(&mut self, channel: &ChannelRef, value: Value) {
        self.cx.send_to_channel(channel.scope, &channel.name, value);
    }

    /// `channel.AddObserver(handler)`.
    pub fn channel_add_observer(&mut self, channel: &ChannelRef, handler: u16) {
        let observer = self.observer(handler);
        match self.cx.shared.blackboards.message_channel(channel.scope, &channel.name) {
            Ok(c) => c.add_observer(observer),
            Err(error) => self.cx.raise(error.into()),
        }
    }

    /// The channel of an address, for the few tasks that work on it directly.
    pub fn with_channel<R>(
        &mut self,
        address: &Address,
        f: impl FnOnce(&mut crate::blackboards::MessageChannel) -> R,
    ) -> Option<R> {
        let scope = self.cx.scope_of(address)?;
        match self.cx.shared.blackboards.message_channel(scope, &address.name) {
            Ok(channel) => Some(f(channel)),
            Err(error) => {
                self.cx.raise(error.into());
                None
            }
        }
    }

    // ---- items ----

    /// `SubscribeItem(itemID, messageId, handler)`.
    pub fn subscribe_item(&mut self, item_id: i64, key: impl Into<EventKey>, handler: u16) {
        let observer = self.observer(handler);
        self.cx.shared.events.subscribe_item(item_id, key, observer);
    }

    pub fn unsubscribe_item(&mut self, item_id: i64, key: impl Into<EventKey>, handler: u16) {
        let observer = self.observer(handler);
        self.cx.shared.events.unsubscribe_item(item_id, key, &observer);
    }

    /// `messenger.SubscribeBubble(bubbleId, message, handler)`, `group.GetMessenger().SubscribeToMessage(message, handler)`,
    /// `sm.RegisterForNotifyEvent(self, name)`: a subscription to messages about anything.
    pub fn subscribe(&mut self, subject: Subject, key: impl Into<EventKey>, handler: u16) {
        let observer = self.observer(handler);
        self.cx.shared.events.subscribe(subject, key, observer);
    }

    /// The other way.
    pub fn unsubscribe(&mut self, subject: Subject, key: impl Into<EventKey>, handler: u16) {
        let observer = self.observer(handler);
        self.cx.shared.events.unsubscribe(subject, key, &observer);
    }

    /// `messenger.SendMessage(itemId, message, *args)`.
    pub fn send_event(&mut self, item_id: i64, key: impl Into<EventKey>, args: &[Value]) {
        self.cx.send_event(item_id, key, args);
    }
}

// ---- the tasks the base classes make ----

/// `BlockingTask`: while it runs no reset of the tree happens.
pub mod blocking {
    use super::TaskCtx;

    pub fn on_enter(t: &mut TaskCtx<'_, '_>) {
        t.set_status_to_blocked();
        t.block_reset();
    }

    pub fn on_exit(t: &mut TaskCtx<'_, '_>) {
        t.unblock_reset();
    }
}

#[cfg(test)]
pub(crate) mod tests {
    use std::cell::RefCell;
    use std::rc::Rc;

    use super::*;
    use crate::composites::{PrioritySelector, Sequence};
    use crate::env::TestWorld;

    /// A leaf that ends in the status it is given, after `ticks` ticks of running, and writes what it did in a log.
    pub struct Leaf {
        pub name: &'static str,
        pub result: Status,
        pub ticks: u32,
        pub log: Rc<RefCell<Vec<String>>>,
        seen: u32,
    }

    impl Leaf {
        pub fn new(
            name: &'static str,
            result: Status,
            ticks: u32,
            log: &Rc<RefCell<Vec<String>>>,
        ) -> Box<Self> {
            Box::new(Self { name, result, ticks, log: Rc::clone(log), seen: 0 })
        }
    }

    impl Task for Leaf {
        fn type_name(&self) -> &'static str {
            "Leaf"
        }

        fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
            self.log.borrow_mut().push(format!("{}:enter", self.name));
            self.seen = 0;
            t.set_status_to_running();
        }

        fn update(&mut self, t: &mut TaskCtx<'_, '_>) {
            self.log.borrow_mut().push(format!("{}:update", self.name));
            self.seen += 1;
            if self.seen > self.ticks {
                t.set_status(self.result);
            }
        }

        fn on_exit(&mut self, _t: &mut TaskCtx<'_, '_>) {
            self.log.borrow_mut().push(format!("{}:exit", self.name));
        }

        fn clean_up(&mut self, t: &mut TaskCtx<'_, '_>) {
            self.log.borrow_mut().push(format!("{}:cleanup", self.name));
            t.set_status_to_invalid();
        }
    }

    pub struct Rig {
        pub tree: BehaviorTree,
        pub ctx: BehaviorContext,
        pub shared: Shared,
        pub world: TestWorld,
        pub log: Rc<RefCell<Vec<String>>>,
        /// What the last tick raised, for `with_task`.
        pub last_error: Option<BehaviorError>,
    }

    impl Rig {
        pub fn new() -> Self {
            let ctx =
                BehaviorContext { my_item_id: Some(1), my_entity_group_id: Some(10), ..Default::default() };
            let mut tree = BehaviorTree::new(1, None, None);
            tree.core.record_messages = true;
            Self {
                tree,
                ctx,
                shared: Shared::new(10),
                world: TestWorld::default(),
                log: Rc::default(),
                last_error: None,
            }
        }

        pub fn tick(&mut self) -> Result<(), BehaviorError> {
            self.shared.now += 1_000_000;
            self.tree.tick(&mut self.ctx, &mut self.shared, &mut self.world)
        }

        /// Lets the time pass without a tick.
        pub fn advance_seconds(&mut self, seconds: f64) {
            self.shared.now += seconds_to_ticks(seconds);
        }

        /// The calls that are due are delivered to their tasks (what they raise is in `last_error`).
        pub fn deliver_scheduled(&mut self) {
            self.shared.fire_due_calls();
            for delivery in std::mem::take(&mut self.shared.pending) {
                if let Err(error) =
                    self.tree.deliver(&mut self.ctx, &mut self.shared, &mut self.world, &delivery)
                {
                    self.last_error.get_or_insert(error);
                }
            }
        }

        /// A message about an item is sent (`SendMessage` of the ballpark's messenger).
        pub fn send_event(&mut self, item_id: i64, key: impl Into<EventKey>, args: &[Value]) {
            let mut cx = Cx {
                core: &mut self.tree.core,
                ctx: &mut self.ctx,
                shared: &mut self.shared,
                world: &mut self.world,
            };
            cx.send_event(item_id, key, args);
        }

        /// The values of the messages a channel of the blackboard of my item holds, oldest first.
        pub fn history(&mut self, channel: &str) -> Vec<Value> {
            match self.shared.blackboards.message_channel(crate::blackboards::item_scope(1), channel) {
                Ok(channel) => channel.messages().into_iter().map(|m| m.value).collect(),
                Err(_) => Vec::new(),
            }
        }

        /// How many messages a channel of the blackboard of my item holds.
        pub fn messages_on(&mut self, channel: &str) -> usize {
            match self.shared.blackboards.message_channel(crate::blackboards::item_scope(1), channel) {
                Ok(channel) => channel.messages().len(),
                Err(_) => 0,
            }
        }

        pub fn take_log(&self) -> Vec<String> {
            std::mem::take(&mut *self.log.borrow_mut())
        }

        pub fn leaf(&mut self, name: &'static str, result: Status, ticks: u32) -> TaskId {
            let node = Leaf::new(name, result, ticks, &self.log);
            self.tree.add_task(node, Attrs::new().with("name", name))
        }

        /// Puts a value on a channel of the rig's blackboards (`address` names the scope; the ids are the rig's context's).
        pub fn post(&mut self, scope_type: ScopeType, channel: &str, value: Value) {
            let mut cx = Cx {
                core: &mut self.tree.core,
                ctx: &mut self.ctx,
                shared: &mut self.shared,
                world: &mut self.world,
            };
            cx.send_blackboard_value(&Address::new(scope_type, channel), value);
        }

        /// The last value on a channel.
        pub fn last(&mut self, scope_type: ScopeType, channel: &str) -> Value {
            let mut cx = Cx {
                core: &mut self.tree.core,
                ctx: &mut self.ctx,
                shared: &mut self.shared,
                world: &mut self.world,
            };
            cx.last_blackboard_value(&Address::new(scope_type, channel))
        }

        /// The last value of a channel of any blackboard.
        pub fn last_in(&mut self, scope: crate::blackboards::Scope, channel: &str) -> Value {
            let mut cx = Cx {
                core: &mut self.tree.core,
                ctx: &mut self.ctx,
                shared: &mut self.shared,
                world: &mut self.world,
            };
            cx.last_value_of_channel(scope, channel)
        }

        /// Makes a task the root, ticks once, and gives back the status it ended in (what the tick raised is in `last_error`).
        pub fn run(&mut self, node: Box<dyn Task>, attributes: Attrs) -> Status {
            let task = self.tree.add_task(node, attributes);
            self.tree.start_root_task(task);
            self.last_error = self.tick().err();
            self.tree.status_of(task)
        }

        pub fn sequence(&mut self, children: &[TaskId]) -> TaskId {
            let id = self.tree.add_task(Box::new(Sequence), Attrs::new());
            for c in children {
                self.tree.add_sub_task(id, *c);
            }
            id
        }

        pub fn selector(&mut self, children: &[TaskId]) -> TaskId {
            let id = self.tree.add_task(Box::new(PrioritySelector), Attrs::new());
            for c in children {
                self.tree.add_sub_task(id, *c);
            }
            id
        }
    }

    /// Runs `body` as the first update of a task in a rig set up by `setup`, and gives back the rig (with what the tick raised in `last_error`) and
    /// what `body` answered.
    pub fn with_task<R: 'static>(
        setup: impl FnOnce(&mut Rig),
        body: impl FnOnce(&mut TaskCtx<'_, '_>) -> R + 'static,
    ) -> (Rig, Option<R>) {
        type Body<R> = Box<dyn FnOnce(&mut TaskCtx<'_, '_>) -> R>;
        struct Probe<R> {
            body: Option<Body<R>>,
            out: Rc<RefCell<Option<R>>>,
        }
        impl<R> Task for Probe<R> {
            fn type_name(&self) -> &'static str {
                "Probe"
            }
            fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
                if let Some(body) = self.body.take() {
                    *self.out.borrow_mut() = Some(body(t));
                }
                t.set_status(Status::Success);
            }
        }
        let mut rig = Rig::new();
        setup(&mut rig);
        let out = Rc::new(RefCell::new(None));
        let probe = rig
            .tree
            .add_task(Box::new(Probe { body: Some(Box::new(body)), out: Rc::clone(&out) }), Attrs::new());
        rig.tree.start_root_task(probe);
        rig.last_error = rig.tick().err();
        let answer = out.borrow_mut().take();
        (rig, answer)
    }

    #[test]
    fn a_leaf_that_succeeds_at_once_enters_updates_and_exits_in_one_tick_and_the_tree_asks_to_be_reset() {
        let mut rig = Rig::new();
        let leaf = rig.leaf("a", Status::Success, 0);
        rig.tree.start_root_task(leaf);
        rig.tick().unwrap();
        assert_eq!(rig.take_log(), ["a:enter", "a:update", "a:exit"]);
        assert!(rig.tree.has_reset_request(), "the root's exit asks for a reset");
        // the next tick resets: the root is cleaned up and starts over
        rig.tick().unwrap();
        assert_eq!(rig.take_log(), ["a:cleanup", "a:enter", "a:update", "a:exit"]);
    }

    #[test]
    fn a_leaf_that_is_running_is_ticked_once_a_tick_and_a_tick_ends_at_its_marker() {
        let mut rig = Rig::new();
        let leaf = rig.leaf("a", Status::Success, 2);
        rig.tree.start_root_task(leaf);
        rig.tick().unwrap();
        assert_eq!(rig.take_log(), ["a:enter", "a:update"]);
        assert_eq!(rig.tree.status_of(leaf), Status::Running);
        assert_eq!(rig.tree.task_list(), [Some(leaf)], "queued for the next tick: the marker was taken");
        rig.tick().unwrap();
        assert_eq!(rig.take_log(), ["a:update"]);
        rig.tick().unwrap();
        assert_eq!(rig.take_log(), ["a:update", "a:exit"]);
    }

    #[test]
    fn a_sequence_runs_its_tasks_one_after_the_other_in_the_same_tick_while_they_end_at_once() {
        let mut rig = Rig::new();
        let (a, b) = (rig.leaf("a", Status::Success, 0), rig.leaf("b", Status::Success, 0));
        let root = rig.sequence(&[a, b]);
        rig.tree.start_root_task(root);
        rig.tick().unwrap();
        assert_eq!(rig.take_log(), ["a:enter", "a:update", "a:exit", "b:enter", "b:update", "b:exit"]);
        assert_eq!(rig.tree.status_of(root), Status::Success);
    }

    #[test]
    fn a_sequence_fails_at_the_first_task_that_fails() {
        let mut rig = Rig::new();
        let (a, b, c) = (
            rig.leaf("a", Status::Success, 0),
            rig.leaf("b", Status::Failure, 0),
            rig.leaf("c", Status::Success, 0),
        );
        let root = rig.sequence(&[a, b, c]);
        rig.tree.start_root_task(root);
        rig.tick().unwrap();
        assert_eq!(rig.take_log(), ["a:enter", "a:update", "a:exit", "b:enter", "b:update", "b:exit"]);
        assert_eq!(rig.tree.status_of(root), Status::Failure);
        assert_eq!(rig.tree.status_of(c), Status::Invalid, "the last was never started");
    }

    #[test]
    fn a_priority_selector_goes_on_to_the_next_task_after_a_failure_and_stops_at_a_success() {
        let mut rig = Rig::new();
        let (a, b, c) = (
            rig.leaf("a", Status::Failure, 0),
            rig.leaf("b", Status::Success, 0),
            rig.leaf("c", Status::Success, 0),
        );
        let root = rig.selector(&[a, b, c]);
        rig.tree.start_root_task(root);
        rig.tick().unwrap();
        assert_eq!(rig.take_log(), ["a:enter", "a:update", "a:exit", "b:enter", "b:update", "b:exit"]);
        assert_eq!(rig.tree.status_of(root), Status::Success);
    }

    #[test]
    fn a_priority_selector_whose_tasks_all_fail_fails() {
        let mut rig = Rig::new();
        let (a, b) = (rig.leaf("a", Status::Failure, 0), rig.leaf("b", Status::Failure, 0));
        let root = rig.selector(&[a, b]);
        rig.tree.start_root_task(root);
        rig.tick().unwrap();
        assert_eq!(rig.tree.status_of(root), Status::Failure);
    }

    #[test]
    fn a_composite_with_nothing_to_run_is_an_error_and_the_tick_stops() {
        let mut rig = Rig::new();
        let root = rig.sequence(&[]);
        rig.tree.start_root_task(root);
        assert_eq!(rig.tick(), Err(BehaviorError::NoSubTasks));
    }

    #[test]
    fn a_reset_cleans_up_what_was_started_and_a_blocking_task_holds_it_off() {
        struct Blocker;
        impl Task for Blocker {
            fn type_name(&self) -> &'static str {
                "Blocker"
            }
            fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
                blocking::on_enter(t);
            }
            fn on_exit(&mut self, t: &mut TaskCtx<'_, '_>) {
                blocking::on_exit(t);
            }
            fn update(&mut self, t: &mut TaskCtx<'_, '_>) {
                if t.get_context_value("done").truthy() {
                    t.set_status_to_success();
                }
            }
        }
        let mut rig = Rig::new();
        let a = rig.leaf("a", Status::Success, 5);
        let blocker = rig.tree.add_task(Box::new(Blocker), Attrs::new());
        let root = rig.sequence(&[a, blocker]);
        rig.tree.start_root_task(root);
        rig.tick().unwrap();
        // the tree is asked to reset from outside while `a` runs: it is, at the next tick
        rig.tree.request_reset(&mut rig.ctx, &mut rig.shared, &mut rig.world);
        rig.take_log();
        rig.tick().unwrap();
        assert_eq!(rig.take_log(), ["a:cleanup", "a:enter", "a:update"]);
        // a blocking task holds a reset off
        let mut rig = Rig::new();
        let blocker = rig.tree.add_task(Box::new(Blocker), Attrs::new());
        rig.tree.start_root_task(blocker);
        rig.tick().unwrap();
        assert!(rig.tree.is_reset_blocked());
        assert_eq!(rig.tree.status_of(blocker), Status::Blocking);
        rig.tree.request_reset(&mut rig.ctx, &mut rig.shared, &mut rig.world);
        rig.tick().unwrap();
        assert!(rig.tree.has_reset_request(), "the request waits");
        assert_eq!(rig.tree.status_of(blocker), Status::Blocking, "and nothing was cleaned up");
    }

    #[test]
    fn the_tree_tells_what_it_does_to_whoever_watches() {
        let mut rig = Rig::new();
        let leaf = rig.leaf("a", Status::Success, 0);
        rig.tree.start_root_task(leaf);
        rig.tick().unwrap();
        assert_eq!(
            rig.tree.core.messages,
            [
                TreeMessage::RequestReset(Some(leaf)),
                TreeMessage::Step(Some(leaf)),
                TreeMessage::Step(None),
                TreeMessage::TickDone
            ]
        );
        assert_eq!(rig.ctx.statistics.ticks, 1);
        assert_eq!(rig.ctx.statistics.task_ticks, 1);
        assert_eq!(rig.ctx.statistics.reset_requests, 1);
        assert_eq!(rig.ctx.statistics.last_reset_requested_by.as_deref(), Some("Leaf"));
    }

    #[test]
    fn a_task_that_asks_for_a_blackboard_the_context_has_no_id_for_raises() {
        struct Asker;
        impl Task for Asker {
            fn type_name(&self) -> &'static str {
                "Asker"
            }
            fn update(&mut self, t: &mut TaskCtx<'_, '_>) {
                t.get_last_blackboard_value(&Address::new(ScopeType::Dungeon, "X"));
            }
        }
        let mut rig = Rig::new();
        let asker = rig.tree.add_task(Box::new(Asker), Attrs::new());
        rig.tree.start_root_task(asker);
        assert_eq!(
            rig.tick(),
            Err(BehaviorError::BlackboardContextIdNotInBehaviorContext(ScopeType::Dungeon))
        );
    }

    #[test]
    fn a_task_hears_of_a_message_on_a_channel_it_subscribed_to_and_a_task_of_another_tree_is_told_through_the_system()
     {
        struct Listener {
            heard: Rc<RefCell<Vec<Value>>>,
        }
        impl Task for Listener {
            fn type_name(&self) -> &'static str {
                "Listener"
            }
            fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
                t.subscribe_to_blackboard(&Address::new(ScopeType::EntityGroup, "CH"), 3);
                t.set_status_to_suspended();
            }
            fn on_blackboard_message(
                &mut self,
                _t: &mut TaskCtx<'_, '_>,
                handler: u16,
                channel: &str,
                message: Option<&MessageData>,
            ) {
                assert_eq!((handler, channel), (3, "CH"));
                self.heard.borrow_mut().push(message.map(|m| m.value.clone()).unwrap_or(Value::None));
            }
        }
        let heard = Rc::new(RefCell::new(Vec::new()));
        let mut rig = Rig::new();
        let listener = rig.tree.add_task(Box::new(Listener { heard: Rc::clone(&heard) }), Attrs::new());
        rig.tree.start_root_task(listener);
        rig.tick().unwrap();
        // the same tree posts to it
        let mut cx = Cx {
            core: &mut rig.tree.core,
            ctx: &mut rig.ctx,
            shared: &mut rig.shared,
            world: &mut rig.world,
        };
        cx.send_blackboard_value(&Address::new(ScopeType::EntityGroup, "CH"), Value::Int(5));
        assert_eq!(*heard.borrow(), [Value::Int(5)]);
        // a listener of another tree: the message waits in the system
        cx.shared
            .blackboards
            .message_channel(Scope { scope_type: ScopeType::EntityGroup, id: 10 }, "CH")
            .unwrap()
            .add_observer(Observer { tree: 99, task: 0, handler: 1 });
        cx.send_blackboard_value(&Address::new(ScopeType::EntityGroup, "CH"), Value::Int(6));
        assert_eq!(rig.shared.pending.len(), 1);
        assert!(
            matches!(&rig.shared.pending[0], Delivery::Blackboard { observer, .. } if observer.tree == 99)
        );
    }
}
