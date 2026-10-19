//! `behaviors/debugging/behaviortreemonitor.py`: what a developer's debugger is told of a behavior tree: the steps it took, the resets, the status
//! of every task at the end of a tick and the last message of every channel of the blackboards it uses, and a dump of the tree.
//!
//! The client's monitor subscribes to the tree's messenger and sends a notification (`OnBehaviorDebugUpdate`) to the client that is watching at
//! each tick's end. Here a tree records what it tells (`TreeCore::record_messages`, with the time each was sent at) and `process` is what those
//! subscriptions are: it is called after a tick. Sending, and resolving which node the client's session is on, are the server's (`BehaviorTreeMonitor`
//! keeps the ids it sends to). The client's `behaviors/debugging/tools.py` (`DepthFirstTaskIterator`) is not in the extract; the statuses are a dict,
//! so the order it walks the tree in does not show.

use crate::blackboards::{Scope, ScopeType, TaskId};
use crate::registry::{TaskKind, get_task_registry};
use crate::tree::{BehaviorContext, BehaviorTree, Shared, Status, TreeMessage};
use crate::value::Value;

/// The notification the client's debugger is sent at each tick's end.
pub const ON_BEHAVIOR_DEBUG_UPDATE: &str = "OnBehaviorDebugUpdate";

/// `scopes.ScopeTypes.values()`. The client's bundle is a dict and lists them in no order that matters; these are in the order it declares them.
const SCOPE_TYPES: [ScopeType; 6] = [
    ScopeType::Item,
    ScopeType::EntityGroup,
    ScopeType::Dungeon,
    ScopeType::Tale,
    ScopeType::SpawnPointPool,
    ScopeType::NodeGraph,
];

/// `task.status.__name__`: the name of the class the client keeps a status in.
pub fn status_class_name(status: Status) -> &'static str {
    match status {
        Status::Invalid => "TaskInvalidStatus",
        Status::Success => "TaskSuccessStatus",
        Status::Failure => "TaskFailureStatus",
        Status::Running => "TaskRunningStatus",
        Status::Suspended => "TaskSuspendedStatus",
        Status::Blocking => "TaskBlockingStatus",
    }
}

/// What the tree did, as the debugger is told: `(time, name, payload)`. A task is told by its id (`id(task)`: here its place in the tree's arena);
/// where the client's `id(None)` would be, there is nothing.
#[derive(Debug, Clone, PartialEq)]
pub enum MonitorEvent {
    /// `OnTreeReset`: `'Reset'`.
    Reset,
    /// `OnTreeResetRequest(requestedBy)`: `'RequestReset'`.
    RequestReset(Option<TaskId>),
    /// `OnTreeSetBlockReset(value, task)`: `'SetBlockReset'`.
    SetBlockReset {
        task: TaskId,
        blocked: bool,
    },

    Step(Option<TaskId>),
}

impl MonitorEvent {
    pub fn name(&self) -> &'static str {
        match self {
            MonitorEvent::Reset => "Reset",
            MonitorEvent::RequestReset(_) => "RequestReset",
            MonitorEvent::SetBlockReset { .. } => "SetBlockReset",
            MonitorEvent::Step(_) => "Step",
        }
    }

    pub fn payload(&self) -> Value {
        match self {
            MonitorEvent::Reset => Value::None,
            MonitorEvent::RequestReset(task) | MonitorEvent::Step(task) => task_id_value(*task),
            MonitorEvent::SetBlockReset { task, blocked } => {
                Value::tuple([task_id_value(Some(*task)), Value::Bool(*blocked)])
            }
        }
    }
}

fn task_id_value(task: Option<TaskId>) -> Value {
    task.map_or(Value::None, |task| Value::Int(task as i64))
}

/// An event with the time it happened at.
#[derive(Debug, Clone, PartialEq)]
pub struct TimedEvent {
    pub time: i64,
    pub event: MonitorEvent,
}

/// A channel of a blackboard and the last message on it: `(name, time, value)`, with `None` for both where there is no message.
#[derive(Debug, Clone, PartialEq)]
pub struct ChannelReport {
    pub name: String,
    pub last: Option<(i64, Value)>,
}

/// A blackboard and its channels.
#[derive(Debug, Clone, PartialEq)]
pub struct BlackboardReport {
    pub scope: Scope,
    pub channels: Vec<ChannelReport>,
}

/// What a tick's end sends: the events since the last one, the status of every task, the tasks that were stepped, and the blackboards.
#[derive(Debug, Clone, PartialEq)]
pub struct TickReport {
    pub events: Vec<TimedEvent>,
    pub task_statuses: Vec<(TaskId, &'static str)>,
    pub tasks_seen: Vec<TaskId>,
    pub blackboards: Vec<BlackboardReport>,
}

impl TickReport {
    /// The arguments the client is sent (`SendToClient(events, taskStatuses, tasksSeen, blackboards)`), after the item id.
    pub fn to_arguments(&self) -> Vec<Value> {
        let events = self
            .events
            .iter()
            .map(|e| Value::tuple([Value::Int(e.time), Value::from(e.event.name()), e.event.payload()]))
            .collect();
        let statuses = self
            .task_statuses
            .iter()
            .map(|(id, name)| (Value::Int(*id as i64), Value::from(*name)))
            .collect();
        let seen = self.tasks_seen.iter().map(|id| Value::Int(*id as i64)).collect();
        let blackboards = self
            .blackboards
            .iter()
            .map(|report| {
                let key =
                    Value::tuple([Value::from(report.scope.scope_type.name()), Value::Int(report.scope.id)]);
                let channels = report
                    .channels
                    .iter()
                    .map(|channel| match &channel.last {
                        None => Value::tuple([Value::from(channel.name.as_str()), Value::None, Value::None]),
                        Some((time, value)) => Value::tuple([
                            Value::from(channel.name.as_str()),
                            Value::Int(*time),
                            value.clone(),
                        ]),
                    })
                    .collect();
                (key, Value::List(channels))
            })
            .collect();
        vec![Value::List(events), Value::Dict(statuses), Value::List(seen), Value::Dict(blackboards)]
    }
}

/// `BehaviorTreeMonitor(serviceManager, clientID, itemID)`: the debugger of one client on the tree of one item.
#[derive(Debug, Clone)]
pub struct BehaviorTreeMonitor {
    pub client_id: i64,
    pub item_id: i64,
    events: Vec<TimedEvent>,
    tasks_seen: Vec<TaskId>,
    attached: bool,
}

impl BehaviorTreeMonitor {
    pub fn new(client_id: i64, item_id: i64) -> Self {
        Self { client_id, item_id, events: Vec::new(), tasks_seen: Vec::new(), attached: false }
    }

    pub fn is_attached(&self) -> bool {
        self.attached
    }

    /// `AttachToBehaviorTree(behaviorTree)`: the tree is to record what it tells.
    pub fn attach_to_behavior_tree(&mut self, tree: &mut BehaviorTree) {
        tree.core.record_messages = true;
        self.attached = true;
    }

    /// `DetachFromBehaviorTree()`: nothing happens for a monitor that is not attached.
    pub fn detach_from_behavior_tree(&mut self, tree: &mut BehaviorTree) {
        if self.attached {
            tree.core.record_messages = false;
            tree.take_messages();
            self.attached = false;
        }
    }

    /// `OnSessionDetach`: whoever is watching left. The tree lets go when `process` next sees it (or `detach_from_behavior_tree`).
    pub fn on_session_detach(&mut self, session_client_id: Option<i64>) {
        if session_client_id == Some(self.client_id) {
            self.attached = false;
        }
    }

    /// `OnSessionChanged(isRemote, sess, change)`: whoever is watching went to another solar system, where this tree is not.
    pub fn on_session_changed(&mut self, session_client_id: Option<i64>, changed: &[&str]) {
        if session_client_id == Some(self.client_id) && changed.contains(&"solarsystemid") {
            self.attached = false;
        }
    }

    /// `MachoResolve(session)`: the debugger's calls go to where the solar system the session is in runs (`GetNodeFromAddress(SERVICE_BEYONCE,
    /// solarsystemid2)`); nowhere for a session that is not in space.
    pub fn resolve_solar_system(solar_system_id2: Option<i64>) -> Option<i64> {
        solar_system_id2.filter(|id| *id != 0)
    }

    /// What the tree told since this was last called: each message is turned into an event, and each end of a tick into a report.
    pub fn process(
        &mut self,
        tree: &mut BehaviorTree,
        ctx: &BehaviorContext,
        shared: &mut Shared,
    ) -> Vec<TickReport> {
        let messages = tree.take_messages();
        if !self.attached {
            tree.core.record_messages = false;
            return Vec::new();
        }
        let mut reports = Vec::new();
        for (time, message) in messages {
            match message {
                TreeMessage::Reset => self.events.push(TimedEvent { time, event: MonitorEvent::Reset }),
                TreeMessage::RequestReset(requested_by) => {
                    self.events.push(TimedEvent { time, event: MonitorEvent::RequestReset(requested_by) });
                }
                TreeMessage::BlockReset(blocked, task) => {
                    self.events
                        .push(TimedEvent { time, event: MonitorEvent::SetBlockReset { task, blocked } });
                }
                TreeMessage::Step(task) => {
                    if let Some(task) = task {
                        self.tasks_seen.push(task);
                    }
                    self.events.push(TimedEvent { time, event: MonitorEvent::Step(task) });
                }
                TreeMessage::TickDone => reports.push(self.on_tick_done(tree, ctx, shared)),
            }
        }
        reports
    }

    /// `OnTickDone(context)`.
    fn on_tick_done(
        &mut self,
        tree: &BehaviorTree,
        ctx: &BehaviorContext,
        shared: &mut Shared,
    ) -> TickReport {
        let events = std::mem::take(&mut self.events);
        let task_statuses = depth_first_tasks(tree)
            .into_iter()
            .map(|task| (task, status_class_name(tree.status_of(task))))
            .collect();
        let tasks_seen = std::mem::take(&mut self.tasks_seen);
        TickReport { events, task_statuses, tasks_seen, blackboards: get_blackboards(ctx, shared) }
    }

    /// `GetTreeDump()`.
    pub fn get_tree_dump(&self, tree: &BehaviorTree) -> Vec<TaskDump> {
        dump_tree(tree)
    }
}

/// `DepthFirstTaskIterator(rootTask)`: the root and everything under it.
pub fn depth_first_tasks(tree: &BehaviorTree) -> Vec<TaskId> {
    fn walk(tree: &BehaviorTree, task: TaskId, tasks: &mut Vec<TaskId>) {
        tasks.push(task);
        for sub in tree.sub_tasks_of(task) {
            walk(tree, *sub, tasks);
        }
    }
    let mut tasks = Vec::new();
    if let Some(root) = tree.root_task() {
        walk(tree, root, &mut tasks);
    }
    tasks
}

/// A task of the dump: `dict(type=, id=, attributes=, status=, depth=)` and, for a composite, `subtasks`.
#[derive(Debug, Clone, PartialEq)]
pub struct TaskDump {
    pub type_name: &'static str,
    pub id: TaskId,
    /// `get_attribute_dict(task)`, in the order of the names (the client's dict has no order that shows).
    pub attributes: Vec<(String, Value)>,
    pub status: &'static str,
    pub depth: usize,
    pub subtasks: Option<Vec<TaskId>>,
}

/// `DumpTree(behaviorTree)`: every task, each before its sub tasks. Nothing for a tree that has no root.
pub fn dump_tree(tree: &BehaviorTree) -> Vec<TaskDump> {
    let mut task_list = Vec::new();
    if let Some(root) = tree.root_task() {
        recursive_dump_task(tree, &mut task_list, root, 0);
    }
    task_list
}

/// `issubclass(task.__class__, Composite)`: a composite, or a decorator (which is one). A class the registry does not know is one when it has sub
/// tasks.
fn is_composite(tree: &BehaviorTree, task: TaskId) -> bool {
    match get_task_registry().get(tree.type_name_of(task)) {
        Ok(entry) => matches!(entry.kind, TaskKind::Composite | TaskKind::Decorator),
        Err(_) => !tree.sub_tasks_of(task).is_empty(),
    }
}

fn recursive_dump_task(tree: &BehaviorTree, task_list: &mut Vec<TaskDump>, task: TaskId, depth: usize) {
    let index = task_list.len();
    task_list.push(dump_task(tree, task, depth));
    if is_composite(tree, task) {
        let mut sub_tasks = Vec::new();
        for sub_task in tree.sub_tasks_of(task) {
            sub_tasks.push(*sub_task);
            recursive_dump_task(tree, task_list, *sub_task, depth + 1);
        }
        task_list[index].subtasks = Some(sub_tasks);
    }
}

/// `DumpTask(task, depth)`.
fn dump_task(tree: &BehaviorTree, task: TaskId, depth: usize) -> TaskDump {
    let mut attributes: Vec<(String, Value)> =
        tree.attributes_of(task).iter().map(|(name, value)| (name.clone(), value.clone())).collect();
    attributes.sort_by(|a, b| a.0.cmp(&b.0));
    TaskDump {
        type_name: tree.type_name_of(task),
        id: task,
        attributes,
        status: status_class_name(tree.status_of(task)),
        depth,
        subtasks: None,
    }
}

/// `GetBlackboards(context)`: for each kind of blackboard the context has an id for, the last message of each of its channels. A blackboard that was
/// deleted is left out.
pub fn get_blackboards(ctx: &BehaviorContext, shared: &mut Shared) -> Vec<BlackboardReport> {
    let mut blackboards = Vec::new();
    for scope_type in SCOPE_TYPES {
        let Some(scope_id) = ctx.id_for_scope(scope_type) else { continue };
        let scope = Scope { scope_type, id: scope_id };
        let Ok(blackboard) = shared.blackboards.get_blackboard(scope) else { continue };
        let channels = blackboard
            .message_channel_list()
            .into_iter()
            .map(|channel| ChannelReport {
                name: channel.name.clone(),
                last: channel.last_message().map(|message| (message.time, message.value.clone())),
            })
            .collect();
        blackboards.push(BlackboardReport { scope, channels });
    }
    blackboards
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::blackboards::{Address, item_scope};
    use crate::tree::Attrs;
    use crate::tree::tests::Rig;

    fn id(value: TaskId) -> Value {
        Value::Int(value as i64)
    }

    #[test]
    fn statuses_are_named_by_the_classes_that_hold_them() {
        assert_eq!(status_class_name(Status::Invalid), "TaskInvalidStatus");
        assert_eq!(status_class_name(Status::Success), "TaskSuccessStatus");
        assert_eq!(status_class_name(Status::Failure), "TaskFailureStatus");
        assert_eq!(status_class_name(Status::Running), "TaskRunningStatus");
        assert_eq!(status_class_name(Status::Suspended), "TaskSuspendedStatus");
        assert_eq!(status_class_name(Status::Blocking), "TaskBlockingStatus");
    }

    #[test]
    fn a_tick_is_reported_with_its_steps_statuses_and_blackboards() {
        let mut rig = Rig::new();
        let a = rig.leaf("a", Status::Success, 0);
        let b = rig.leaf("b", Status::Failure, 1);
        let root = rig.sequence(&[a, b]);
        rig.tree.start_root_task(root);
        let mut monitor = BehaviorTreeMonitor::new(5, 1);
        monitor.attach_to_behavior_tree(&mut rig.tree);
        rig.post(ScopeType::Item, "greeting", Value::from("hello"));

        rig.tick().unwrap();
        let reports = monitor.process(&mut rig.tree, &rig.ctx, &mut rig.shared);
        assert_eq!(reports.len(), 1);
        let report = &reports[0];
        let steps: Vec<_> = report.events.iter().map(|e| e.event.clone()).collect();
        assert_eq!(
            steps,
            [
                MonitorEvent::Step(Some(root)),
                MonitorEvent::Step(Some(a)),
                MonitorEvent::Step(Some(b)),
                MonitorEvent::Step(None),
            ]
        );
        assert!(report.events.iter().all(|e| e.time == 1_000_000), "stamped with the time it was sent at");
        assert_eq!(report.tasks_seen, [root, a, b]);
        let mut statuses = report.task_statuses.clone();
        statuses.sort();
        assert_eq!(statuses.len(), 3);
        assert!(statuses.contains(&(a, "TaskSuccessStatus")));
        assert!(statuses.contains(&(b, "TaskRunningStatus")));
        assert!(statuses.contains(&(root, "TaskSuspendedStatus")), "a composite waits for its sub task");
        assert_eq!(report.blackboards.len(), 2);
        assert_eq!(report.blackboards[0].scope, item_scope(1));
        assert_eq!(
            report.blackboards[0].channels,
            [ChannelReport { name: "greeting".into(), last: Some((0, Value::from("hello"))) }]
        );
        assert_eq!(report.blackboards[1].scope.scope_type, ScopeType::EntityGroup);

        // The next tick starts from nothing.
        rig.tick().unwrap();
        let reports = monitor.process(&mut rig.tree, &rig.ctx, &mut rig.shared);
        assert_eq!(reports.len(), 1);
        assert_eq!(reports[0].tasks_seen, [b]);
        assert!(rig.tree.core.messages.is_empty(), "what was taken is not kept");
    }

    #[test]
    fn resets_are_told_in_the_order_they_happened() {
        let mut rig = Rig::new();
        let a = rig.leaf("a", Status::Success, 0);
        rig.tree.start_root_task(a);
        let mut monitor = BehaviorTreeMonitor::new(5, 1);
        monitor.attach_to_behavior_tree(&mut rig.tree);
        rig.tick().unwrap();
        monitor.process(&mut rig.tree, &rig.ctx, &mut rig.shared);
        // The root ended, so a reset was asked for and is done at the start of the next tick.
        rig.tick().unwrap();
        let report = monitor.process(&mut rig.tree, &rig.ctx, &mut rig.shared).pop().unwrap();
        let events: Vec<_> = report.events.iter().map(|e| e.event.clone()).collect();
        assert_eq!(
            events,
            [
                MonitorEvent::Reset,
                MonitorEvent::RequestReset(Some(a)),
                MonitorEvent::Step(Some(a)),
                MonitorEvent::Step(None)
            ]
        );
    }

    #[test]
    fn events_are_named_and_carry_what_the_client_sends() {
        assert_eq!(MonitorEvent::Reset.name(), "Reset");
        assert_eq!(MonitorEvent::Reset.payload(), Value::None);
        assert_eq!(MonitorEvent::RequestReset(Some(3)).name(), "RequestReset");
        assert_eq!(MonitorEvent::RequestReset(Some(3)).payload(), id(3));
        assert_eq!(MonitorEvent::RequestReset(None).payload(), Value::None);
        assert_eq!(MonitorEvent::SetBlockReset { task: 4, blocked: true }.name(), "SetBlockReset");
        assert_eq!(
            MonitorEvent::SetBlockReset { task: 4, blocked: false }.payload(),
            Value::tuple([id(4), Value::Bool(false)])
        );
        assert_eq!(MonitorEvent::Step(Some(2)).name(), "Step");
        assert_eq!(MonitorEvent::Step(None).payload(), Value::None);
    }

    #[test]
    fn what_is_sent_to_the_client_is_four_values() {
        let report = TickReport {
            events: vec![TimedEvent { time: 7, event: MonitorEvent::Step(Some(2)) }],
            task_statuses: vec![(2, "TaskRunningStatus")],
            tasks_seen: vec![2],
            blackboards: vec![BlackboardReport {
                scope: item_scope(9),
                channels: vec![
                    ChannelReport { name: "a".into(), last: None },
                    ChannelReport { name: "b".into(), last: Some((3, Value::Int(1))) },
                ],
            }],
        };
        assert_eq!(
            report.to_arguments(),
            [
                Value::List(vec![Value::tuple([Value::Int(7), Value::from("Step"), id(2)])]),
                Value::Dict(vec![(id(2), Value::from("TaskRunningStatus"))]),
                Value::List(vec![id(2)]),
                Value::Dict(vec![(
                    Value::tuple([Value::from("item"), Value::Int(9)]),
                    Value::List(vec![
                        Value::tuple([Value::from("a"), Value::None, Value::None]),
                        Value::tuple([Value::from("b"), Value::Int(3), Value::Int(1)]),
                    ])
                )]),
            ]
        );
        assert_eq!(ON_BEHAVIOR_DEBUG_UPDATE, "OnBehaviorDebugUpdate");
    }

    #[test]
    fn a_monitor_that_is_not_attached_hears_nothing_and_the_tree_stops_recording() {
        let mut rig = Rig::new();
        let a = rig.leaf("a", Status::Success, 0);
        rig.tree.start_root_task(a);
        let mut monitor = BehaviorTreeMonitor::new(5, 1);
        assert!(!monitor.is_attached());
        rig.tick().unwrap();
        assert!(monitor.process(&mut rig.tree, &rig.ctx, &mut rig.shared).is_empty());
        assert!(!rig.tree.core.record_messages);
        assert!(rig.tree.core.messages.is_empty());

        monitor.attach_to_behavior_tree(&mut rig.tree);
        assert!(monitor.is_attached());
        assert!(rig.tree.core.record_messages);
        monitor.detach_from_behavior_tree(&mut rig.tree);
        assert!(!monitor.is_attached());
        assert!(!rig.tree.core.record_messages);
        // Detaching again is nothing: what somebody else set on the tree stays.
        rig.tree.core.record_messages = true;
        monitor.detach_from_behavior_tree(&mut rig.tree);
        assert!(!monitor.is_attached());
        assert!(rig.tree.core.record_messages);
    }

    #[test]
    fn the_watcher_leaving_or_changing_system_detaches_the_monitor() {
        let mut rig = Rig::new();
        let attach =
            |monitor: &mut BehaviorTreeMonitor, rig: &mut Rig| monitor.attach_to_behavior_tree(&mut rig.tree);
        let mut monitor = BehaviorTreeMonitor::new(5, 1);

        attach(&mut monitor, &mut rig);
        monitor.on_session_detach(Some(6));
        monitor.on_session_detach(None);
        assert!(monitor.is_attached(), "somebody else's session");
        monitor.on_session_detach(Some(5));
        assert!(!monitor.is_attached());

        attach(&mut monitor, &mut rig);
        monitor.on_session_changed(Some(5), &["stationid", "shipid"]);
        assert!(monitor.is_attached(), "a change that is not of the system");
        monitor.on_session_changed(Some(6), &["solarsystemid"]);
        assert!(monitor.is_attached(), "somebody else's session");
        monitor.on_session_changed(Some(5), &["shipid", "solarsystemid"]);
        assert!(!monitor.is_attached());
    }

    #[test]
    fn the_debugger_is_resolved_to_the_system_the_session_is_in() {
        assert_eq!(BehaviorTreeMonitor::resolve_solar_system(Some(30_000_142)), Some(30_000_142));
        assert_eq!(BehaviorTreeMonitor::resolve_solar_system(Some(0)), None);
        assert_eq!(BehaviorTreeMonitor::resolve_solar_system(None), None);
    }

    #[test]
    fn the_dump_lists_each_task_before_its_sub_tasks_with_composites_naming_theirs() {
        let mut rig = Rig::new();
        let a = rig.leaf("a", Status::Success, 0);
        let b = rig.leaf("b", Status::Success, 0);
        let inner = rig.selector(&[b]);
        let root = rig.sequence(&[a, inner]);
        rig.tree.start_root_task(root);
        let dump = dump_tree(&rig.tree);
        assert_eq!(dump.iter().map(|d| d.id).collect::<Vec<_>>(), [root, a, inner, b]);
        assert_eq!(dump.iter().map(|d| d.depth).collect::<Vec<_>>(), [0, 1, 1, 2]);
        assert_eq!(
            dump.iter().map(|d| d.type_name).collect::<Vec<_>>(),
            ["Sequence", "Leaf", "PrioritySelector", "Leaf"]
        );
        assert_eq!(dump[0].subtasks, Some(vec![a, inner]));
        assert_eq!(dump[1].subtasks, None, "a leaf has no such key");
        assert_eq!(dump[2].subtasks, Some(vec![b]));
        assert_eq!(dump[1].attributes, [("name".to_string(), Value::from("a"))]);
        assert!(dump[0].attributes.is_empty());
        assert!(dump.iter().all(|d| d.status == "TaskInvalidStatus"));
        let monitor = BehaviorTreeMonitor::new(5, 1);
        assert_eq!(monitor.get_tree_dump(&rig.tree), dump);
    }

    #[test]
    fn a_decorator_is_a_composite_and_names_the_task_it_decorates() {
        let mut rig = Rig::new();
        let a = rig.leaf("a", Status::Success, 0);
        let root = rig.tree.add_task(Box::new(crate::decorators::Not), Attrs::new());
        rig.tree.add_sub_task(root, a);
        rig.tree.start_root_task(root);
        let dump = dump_tree(&rig.tree);
        assert_eq!(dump.iter().map(|d| d.type_name).collect::<Vec<_>>(), ["Not", "Leaf"]);
        assert_eq!(dump[0].subtasks, Some(vec![a]));
        assert_eq!(dump[1].depth, 1);
    }

    #[test]
    fn a_tree_without_a_root_has_no_dump_and_no_statuses() {
        let rig = Rig::new();
        assert!(dump_tree(&rig.tree).is_empty());
        assert!(depth_first_tasks(&rig.tree).is_empty());
    }

    #[test]
    fn a_composite_with_nothing_in_it_still_has_the_key() {
        let mut rig = Rig::new();
        let root = rig.sequence(&[]);
        rig.tree.start_root_task(root);
        assert_eq!(dump_tree(&rig.tree)[0].subtasks, Some(Vec::new()));
    }

    #[test]
    fn the_attributes_are_the_tasks_own_and_sorted_by_name() {
        let names = ["hotel", "alpha", "golf", "bravo", "foxtrot", "charlie", "echo", "delta"];
        // A map lists its entries in an order of its own that differs from one map to the next: many are tried.
        for _ in 0..64 {
            let mut rig = Rig::new();
            let mut attributes = Attrs::new();
            for (index, name) in names.iter().enumerate() {
                attributes.set(name, index as i64);
            }
            let task = rig.tree.add_task(Box::new(crate::composites::Sequence), attributes);
            rig.tree.start_root_task(task);
            let dump = dump_tree(&rig.tree);
            let dumped: Vec<&str> = dump[0].attributes.iter().map(|(name, _)| name.as_str()).collect();
            assert_eq!(dumped, ["alpha", "bravo", "charlie", "delta", "echo", "foxtrot", "golf", "hotel"]);
            assert_eq!(dump[0].attributes[0].1, Value::Int(1));
            assert_eq!(dump[0].attributes[7].1, Value::Int(0));
        }
    }

    #[test]
    fn only_the_blackboards_the_context_has_ids_for_are_reported() {
        let mut rig = Rig::new();
        rig.ctx.my_tale_id = Some(44);
        rig.ctx.my_item_id = None;
        rig.post(ScopeType::EntityGroup, "loot", Value::Int(3));
        let boards = get_blackboards(&rig.ctx, &mut rig.shared);
        assert_eq!(
            boards.iter().map(|b| (b.scope.scope_type, b.scope.id)).collect::<Vec<_>>(),
            [(ScopeType::EntityGroup, 10), (ScopeType::Tale, 44)]
        );
        assert_eq!(boards[0].channels.len(), 1);
        assert_eq!(boards[0].channels[0].last, Some((0, Value::Int(3))));
        assert!(boards[1].channels.is_empty());
        let _ = Address::new(ScopeType::Item, "unused");
    }

    #[test]
    fn a_blackboard_that_was_deleted_is_left_out() {
        let mut rig = Rig::new();
        rig.post(ScopeType::Item, "a", Value::Int(1));
        assert_eq!(get_blackboards(&rig.ctx, &mut rig.shared).len(), 2);
        rig.shared.blackboards.remove_blackboard(item_scope(1));
        let boards = get_blackboards(&rig.ctx, &mut rig.shared);
        assert_eq!(boards.iter().map(|b| b.scope.scope_type).collect::<Vec<_>>(), [ScopeType::EntityGroup]);
    }
}
