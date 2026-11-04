//! Blackboards: named message channels, grouped by scope. Ported from the client's `behaviors/blackboards/__init__.py`, `scopes.py` and
//! `utility/setposter.py`.
//!
//! A channel keeps the last messages sent to it (newest first, at most `max_length`) with the time each was sent, and tells its observers.
//! Python observers are bound methods; here an observer is the task it belongs to and which of its handlers to call (`Observer`), and sending a
//! message returns the observers to tell (the caller, `Cx`, tells them: the channel does not know the trees).

use std::collections::{HashMap, HashSet, VecDeque};

use crate::value::Value;

/// A tree in the system (the item id of the NPC it runs, normally).
pub type TreeId = i64;
/// A task of a tree.
pub type TaskId = usize;

/// Who to tell of a message: this handler of this task of this tree.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub struct Observer {
    pub tree: TreeId,
    pub task: TaskId,
    pub handler: u16,
}

/// What a channel keeps of a message.
#[derive(Debug, Clone, PartialEq)]
pub struct MessageData {
    pub time: i64,
    pub value: Value,
}

// ---- scopes ----

/// The kinds of blackboard.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, PartialOrd, Ord)]
pub enum ScopeType {
    Item,
    EntityGroup,
    Dungeon,
    Tale,
    SpawnPointPool,
    NodeGraph,
}

impl ScopeType {
    /// The name the client's data uses (`ScopeTypes`).
    pub fn name(self) -> &'static str {
        match self {
            ScopeType::Item => "item",
            ScopeType::EntityGroup => "entity_group",
            ScopeType::Dungeon => "dungeon",
            ScopeType::Tale => "tale",
            ScopeType::SpawnPointPool => "spawn_point_pool",
            ScopeType::NodeGraph => "node_graph",
        }
    }

    pub fn from_name(name: &str) -> Option<ScopeType> {
        Some(match name {
            "item" => ScopeType::Item,
            "entity_group" => ScopeType::EntityGroup,
            "dungeon" => ScopeType::Dungeon,
            "tale" => ScopeType::Tale,
            "spawn_point_pool" => ScopeType::SpawnPointPool,
            "node_graph" => ScopeType::NodeGraph,
            _ => return None,
        })
    }

    /// How the client's tools show the scope (`SCOPE_DISPLAY_INFO`).
    pub fn display(self, name: &str) -> String {
        let label = match self {
            ScopeType::Item => "Item Blackboard",
            ScopeType::EntityGroup => "Entity Group Blackboard",
            ScopeType::Dungeon => "Dungeon Blackboard (spawn)",
            ScopeType::Tale => "Tale Blackboard",
            ScopeType::SpawnPointPool => "Spawn Point Pool Blackboard",
            ScopeType::NodeGraph => "Node Graph Blackboard",
        };
        format!("{label}: {name}")
    }
}

/// A blackboard's identity: its kind and the id of what it belongs to.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub struct Scope {
    pub scope_type: ScopeType,
    pub id: i64,
}

pub fn item_scope(id: i64) -> Scope {
    Scope { scope_type: ScopeType::Item, id }
}

pub fn entity_group_scope(id: i64) -> Scope {
    Scope { scope_type: ScopeType::EntityGroup, id }
}

pub fn dungeon_scope(id: i64) -> Scope {
    Scope { scope_type: ScopeType::Dungeon, id }
}

pub fn tale_scope(id: i64) -> Scope {
    Scope { scope_type: ScopeType::Tale, id }
}

pub fn spawn_point_pool_scope(id: i64) -> Scope {
    Scope { scope_type: ScopeType::SpawnPointPool, id }
}

pub fn node_graph_scope(id: i64) -> Scope {
    Scope { scope_type: ScopeType::NodeGraph, id }
}

/// `IsPlayerItem` of the client's id checkers: `minPlayerItem <= itemID < minFakeItem`.
pub fn is_player_item(item_id: i64) -> bool {
    (100_000_000..9_000_000_000_000_000_000).contains(&item_id)
}

/// A blackboard of an item that can come back (a player's) may be made again after it was removed; one of anything else may not.
pub fn is_reusable_scope(scope: &Scope) -> bool {
    scope.scope_type == ScopeType::Item && is_player_item(scope.id)
}

/// A channel's address: the kind of blackboard and the channel's name (`(scopeType, messageName)`).
#[derive(Debug, Clone, PartialEq, Eq, Hash)]
pub struct Address {
    pub scope_type: ScopeType,
    pub name: String,
}

impl From<Address> for Value {
    fn from(address: Address) -> Value {
        address.to_value()
    }
}

impl From<&Address> for Value {
    fn from(address: &Address) -> Value {
        address.to_value()
    }
}

impl Address {
    pub fn new(scope_type: ScopeType, name: &str) -> Self {
        Self { scope_type, name: name.to_string() }
    }

    /// The address as a `Value` tuple `(scope type name, channel name)`, the form task attributes carry it in.
    pub fn to_value(&self) -> Value {
        Value::tuple([Value::Str(self.scope_type.name().to_string()), Value::Str(self.name.clone())])
    }

    /// An address from a `Value` tuple `(scope type name, channel name)`.
    pub fn from_value(value: &Value) -> Option<Address> {
        match value.items()? {
            [Value::Str(scope), Value::Str(name)] => {
                Some(Address { scope_type: ScopeType::from_name(scope)?, name: name.clone() })
            }
            _ => None,
        }
    }
}

// ---- channels and blackboards ----

/// A channel of messages.
#[derive(Debug, Clone)]
pub struct MessageChannel {
    pub name: String,
    messages: VecDeque<MessageData>,
    max_length: usize,
    observers: Vec<Observer>,
    reset_on_respawn: bool,
}

impl MessageChannel {
    pub fn new(name: &str, max_length: usize) -> Self {
        Self {
            name: name.to_string(),
            messages: VecDeque::new(),
            max_length,
            observers: Vec::new(),
            reset_on_respawn: true,
        }
    }

    pub fn history_length(&self) -> usize {
        self.max_length
    }

    /// Drops every message; the observers are told `None` (the caller tells them, see [`Blackboard::clear_channel`]).
    fn clear_all_messages(&mut self) {
        self.messages.clear();
    }

    /// Keeps a message (newest first).
    fn push(&mut self, time: i64, value: Value) -> MessageData {
        let message = MessageData { time, value };
        self.messages.push_front(message.clone());
        while self.messages.len() > self.max_length {
            self.messages.pop_back();
        }
        message
    }

    pub fn last_message(&self) -> Option<&MessageData> {
        self.messages.front()
    }

    pub fn last_message_with_max_age(&self, now: i64, max_age: i64) -> Option<&MessageData> {
        self.messages.front().filter(|m| m.time >= now - max_age)
    }

    /// The value of the last message, or `None`.
    pub fn last_message_value(&self) -> Value {
        self.last_message().map(|m| m.value.clone()).unwrap_or(Value::None)
    }

    pub fn last_message_value_with_max_age(&self, now: i64, max_age: i64) -> Value {
        self.last_message_with_max_age(now, max_age).map(|m| m.value.clone()).unwrap_or(Value::None)
    }

    /// The last message's value, to change in place (what a Python set posted to a channel is: one object every reader shares).
    pub fn last_value_mut(&mut self) -> Option<&mut Value> {
        self.messages.front_mut().map(|m| &mut m.value)
    }

    /// The messages, newest first.
    pub fn messages(&self) -> Vec<MessageData> {
        self.messages.iter().cloned().collect()
    }

    /// The messages sent no longer than `max_age` ago, newest first.
    pub fn messages_with_max_age(&self, now: i64, max_age: i64) -> Vec<MessageData> {
        let oldest = now - max_age;
        self.messages.iter().take_while(|m| m.time >= oldest).cloned().collect()
    }

    pub fn add_observer(&mut self, observer: Observer) {
        self.observers.push(observer);
    }

    /// `signal.disconnect`: takes one; nothing happens when it was not there.
    pub fn remove_observer(&mut self, observer: &Observer) {
        if let Some(at) = self.observers.iter().position(|o| o == observer) {
            self.observers.remove(at);
        }
    }

    pub fn remove_all_observers(&mut self) {
        self.observers.clear();
    }

    pub fn observers(&self) -> &[Observer] {
        &self.observers
    }

    pub fn set_reset_on_group_respawn(&mut self, reset: bool) {
        self.reset_on_respawn = reset;
    }

    pub fn should_reset_on_respawn(&self) -> bool {
        self.reset_on_respawn
    }
}

/// What was sent, and who is to be told: `(the message (None for a channel that was cleared), the observers of the channel and of its blackboard)`.
pub type Sent = (Option<MessageData>, Vec<Observer>);

/// A blackboard: the channels of one scope.
#[derive(Debug, Clone)]
pub struct Blackboard {
    pub identity: Scope,
    max_history_length: usize,
    channels: HashMap<String, MessageChannel>,
    /// The order the channels were made in (Python dicts keep it).
    order: Vec<String>,
    /// Told of every message of every channel.
    all_observers: Vec<Observer>,
}

impl Blackboard {
    pub fn new(identity: Scope, max_history_length: usize) -> Self {
        Self {
            identity,
            max_history_length,
            channels: HashMap::new(),
            order: Vec::new(),
            all_observers: Vec::new(),
        }
    }

    /// `CreateMessageChannel`: makes a channel; false (the client raises `MessageChannelAlreadyExistsError`) when there is one of the name.
    pub fn create_message_channel(&mut self, name: &str, max_history_length: usize) -> bool {
        if self.channels.contains_key(name) {
            return false;
        }
        self.channels.insert(name.to_string(), MessageChannel::new(name, max_history_length));
        self.order.push(name.to_string());
        true
    }

    /// The channel of this name, made when there is none.
    pub fn message_channel(&mut self, name: &str) -> &mut MessageChannel {
        if !self.channels.contains_key(name) {
            let length = self.max_history_length;
            self.create_message_channel(name, length);
        }
        self.channels.get_mut(name).expect("the channel was made")
    }

    pub fn has_channel(&self, name: &str) -> bool {
        self.channels.contains_key(name)
    }

    /// The channels in the order they were made.
    pub fn message_channel_list(&self) -> Vec<&MessageChannel> {
        self.order.iter().filter_map(|n| self.channels.get(n)).collect()
    }

    pub fn add_all_observer(&mut self, observer: Observer) {
        self.all_observers.push(observer);
    }

    /// `SendMessage`: keeps the message; who is to be told comes back.
    pub fn send_message(&mut self, name: &str, now: i64, value: Value) -> Sent {
        let channel = self.message_channel(name);
        let message = channel.push(now, value);
        let mut told = channel.observers.clone();
        told.extend(self.all_observers.iter().copied());
        (Some(message), told)
    }

    /// `ClearAllMessages` of a channel: its observers (and the blackboard's) are told `None`.
    pub fn clear_channel(&mut self, name: &str) -> Sent {
        let channel = self.message_channel(name);
        channel.clear_all_messages();
        let mut told = channel.observers.clone();
        told.extend(self.all_observers.iter().copied());
        (None, told)
    }

    /// `GetLastMessageValueForAll`.
    pub fn last_message_value_for_all(&self) -> Vec<(String, Value)> {
        self.order
            .iter()
            .filter_map(|n| self.channels.get(n).map(|c| (n.clone(), c.last_message_value())))
            .collect()
    }

    /// `Reset`: forgets every channel and observer.
    pub fn reset(&mut self) {
        for channel in self.channels.values_mut() {
            channel.remove_all_observers();
        }
        self.all_observers.clear();
        self.channels.clear();
        self.order.clear();
    }

    /// `ResetOnGroupRespawn`: the channels that are to be reset lose their observers and their messages (their observers, being gone, are not told);
    /// the others keep both.
    pub fn reset_on_group_respawn(&mut self) {
        for name in self.order.clone() {
            if let Some(channel) = self.channels.get_mut(&name)
                && channel.should_reset_on_respawn()
            {
                channel.remove_all_observers();
                channel.clear_all_messages();
            }
        }
    }
}

/// What can go wrong with a blackboard (the client's exceptions).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum BlackboardError {
    AlreadyExists,
    Deleted,
}

/// Every blackboard of a system, by scope.
#[derive(Debug, Clone)]
pub struct BlackboardManager {
    blackboards: HashMap<Scope, Blackboard>,
    max_history_length: usize,
    removed: HashSet<Scope>,
}

impl BlackboardManager {
    pub fn new(max_history_length: usize) -> Self {
        Self { blackboards: HashMap::new(), max_history_length, removed: HashSet::new() }
    }

    pub fn create_blackboard(
        &mut self,
        scope: Scope,
        max_history_length: usize,
    ) -> Result<(), BlackboardError> {
        if self.blackboards.contains_key(&scope) {
            return Err(BlackboardError::AlreadyExists);
        }
        if !is_reusable_scope(&scope) && self.removed.contains(&scope) {
            return Err(BlackboardError::Deleted);
        }
        self.blackboards.insert(scope, Blackboard::new(scope, max_history_length));
        Ok(())
    }

    /// The blackboard of a scope, made when there is none; an error when it was removed and cannot come back.
    pub fn get_blackboard(&mut self, scope: Scope) -> Result<&mut Blackboard, BlackboardError> {
        if !self.blackboards.contains_key(&scope) {
            let length = self.max_history_length;
            self.create_blackboard(scope, length)?;
        }
        Ok(self.blackboards.get_mut(&scope).expect("the blackboard was made"))
    }

    pub fn remove_blackboard(&mut self, scope: Scope) {
        if let Some(mut blackboard) = self.blackboards.remove(&scope) {
            blackboard.reset();
            if !is_reusable_scope(&scope) {
                self.removed.insert(scope);
            }
        }
    }

    pub fn has_blackboard(&self, scope: &Scope) -> bool {
        self.blackboards.contains_key(scope)
    }

    pub fn blackboard(&self, scope: &Scope) -> Option<&Blackboard> {
        self.blackboards.get(scope)
    }

    pub fn message_channel(
        &mut self,
        scope: Scope,
        name: &str,
    ) -> Result<&mut MessageChannel, BlackboardError> {
        Ok(self.get_blackboard(scope)?.message_channel(name))
    }

    pub fn clean_up(&mut self) {
        for blackboard in self.blackboards.values_mut() {
            blackboard.reset();
        }
        self.blackboards.clear();
    }

    pub fn blackboard_count(&self) -> usize {
        self.blackboards.len()
    }
}

/// `SetPoster`: keeps a set in a channel. Adding an entry sends the set again (it is one object: earlier messages show it too); removing one changes
/// the set where it lies and sends nothing.
pub struct SetPoster;

impl SetPoster {
    /// Adds an entry to the set in the channel (making the set when there is none). Returns whether it was added and who is to be told.
    pub fn entry_added(
        blackboard: &mut Blackboard,
        channel: &str,
        now: i64,
        entry: Value,
    ) -> (bool, Vec<Observer>) {
        let last = blackboard.message_channel(channel).last_message_value();
        let value = match last {
            Value::None => Value::set([entry]),
            mut set => {
                if set.contains(&entry) {
                    return (false, Vec::new());
                }
                set.set_add(entry);
                set
            }
        };
        // the set is what the channel's last message holds: a later `entry_removed` changes it there
        let (_, told) = blackboard.send_message(channel, now, value);
        (true, told)
    }

    /// Takes an entry from the set in the channel; false when there was no set or the entry was not in it.
    pub fn entry_removed(blackboard: &mut Blackboard, channel: &str, entry: &Value) -> bool {
        match blackboard.message_channel(channel).last_value_mut() {
            Some(set) if set.truthy() => set.set_discard(entry),
            _ => false,
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn observer(handler: u16) -> Observer {
        Observer { tree: 1, task: 2, handler }
    }

    #[test]
    fn a_channel_keeps_the_newest_messages_first_and_no_more_than_its_length() {
        let mut board = Blackboard::new(item_scope(5), 3);
        for (t, v) in [(10, 1), (20, 2), (30, 3), (40, 4)] {
            board.send_message("A", t, Value::Int(v));
        }
        let channel = board.message_channel("A");
        assert_eq!(
            channel.messages().iter().map(|m| m.value.clone()).collect::<Vec<_>>(),
            [Value::Int(4), Value::Int(3), Value::Int(2)]
        );
        assert_eq!(channel.last_message_value(), Value::Int(4));
        assert_eq!(channel.history_length(), 3);
    }

    #[test]
    fn an_age_limit_keeps_the_messages_that_are_young_enough() {
        let mut board = Blackboard::new(item_scope(5), 10);
        board.send_message("A", 100, Value::Int(1));
        board.send_message("A", 200, Value::Int(2));
        let channel = board.message_channel("A");
        assert_eq!(channel.last_message_value_with_max_age(250, 100), Value::Int(2));
        assert_eq!(channel.last_message_value_with_max_age(350, 100), Value::None);
        assert_eq!(channel.messages_with_max_age(250, 150).len(), 2);
        assert_eq!(channel.messages_with_max_age(250, 60).len(), 1);
        // a message exactly as old as the limit is young enough
        assert_eq!(channel.messages_with_max_age(300, 100).len(), 1);
    }

    #[test]
    fn a_message_is_told_to_the_observers_of_its_channel_and_of_its_blackboard() {
        let mut board = Blackboard::new(item_scope(5), 10);
        board.message_channel("A").add_observer(observer(1));
        board.add_all_observer(observer(9));
        let (message, told) = board.send_message("A", 1, Value::Int(7));
        assert_eq!(message.unwrap().value, Value::Int(7));
        assert_eq!(told, [observer(1), observer(9)]);
        let (_, told) = board.send_message("B", 1, Value::Int(7));
        assert_eq!(told, [observer(9)], "another channel's observers are not told");
    }

    #[test]
    fn an_observer_is_taken_off_once_and_taking_off_one_that_is_not_there_does_nothing() {
        let mut channel = MessageChannel::new("A", 10);
        channel.add_observer(observer(1));
        channel.add_observer(observer(1));
        channel.remove_observer(&observer(1));
        assert_eq!(channel.observers().len(), 1);
        channel.remove_observer(&observer(2));
        assert_eq!(channel.observers().len(), 1);
    }

    #[test]
    fn a_cleared_channel_tells_its_observers_none() {
        let mut board = Blackboard::new(item_scope(5), 10);
        board.message_channel("A").add_observer(observer(1));
        board.send_message("A", 1, Value::Int(7));
        let (message, told) = board.clear_channel("A");
        assert_eq!((message, told), (None, vec![observer(1)]));
        assert_eq!(board.message_channel("A").last_message_value(), Value::None);
    }

    #[test]
    fn a_channel_made_twice_is_refused_and_one_asked_for_is_made() {
        let mut board = Blackboard::new(item_scope(5), 4);
        assert!(board.create_message_channel("A", 2));
        assert!(!board.create_message_channel("A", 2));
        assert_eq!(board.message_channel("A").history_length(), 2);
        assert_eq!(board.message_channel("B").history_length(), 4);
        assert_eq!(
            board.message_channel_list().iter().map(|c| c.name.as_str()).collect::<Vec<_>>(),
            ["A", "B"]
        );
    }

    #[test]
    fn a_group_respawn_clears_the_channels_that_are_to_be_and_keeps_the_others() {
        let mut board = Blackboard::new(entity_group_scope(1), 10);
        board.send_message("KEEP", 1, Value::Int(1));
        board.send_message("DROP", 1, Value::Int(2));
        board.message_channel("KEEP").set_reset_on_group_respawn(false);
        board.message_channel("KEEP").add_observer(observer(1));
        board.message_channel("DROP").add_observer(observer(2));
        board.reset_on_group_respawn();
        assert_eq!(board.message_channel("KEEP").last_message_value(), Value::Int(1));
        assert_eq!(board.message_channel("KEEP").observers().len(), 1);
        assert_eq!(board.message_channel("DROP").last_message_value(), Value::None);
        assert!(board.message_channel("DROP").observers().is_empty());
    }

    #[test]
    fn a_removed_blackboard_can_come_back_only_for_a_player_s_item() {
        let mut manager = BlackboardManager::new(10);
        let npc = item_scope(50);
        let player = item_scope(500_000_000);
        for scope in [npc, player, entity_group_scope(3)] {
            manager.get_blackboard(scope).unwrap();
            manager.remove_blackboard(scope);
        }
        assert_eq!(
            manager.get_blackboard(npc).err(),
            Some(BlackboardError::Deleted),
            "an NPC's blackboard is gone for good"
        );
        assert_eq!(manager.get_blackboard(entity_group_scope(3)).err(), Some(BlackboardError::Deleted));
        assert!(manager.get_blackboard(player).is_ok(), "a player's item may be made again");
    }

    #[test]
    fn player_items_are_the_client_s_range() {
        assert!(
            !is_player_item(99_999_999) && is_player_item(100_000_000) && is_player_item(980_050_000_108)
        );
        assert!(!is_player_item(9_000_000_000_000_000_000));
        assert!(
            is_reusable_scope(&item_scope(500_000_000))
                && !is_reusable_scope(&entity_group_scope(500_000_000))
        );
    }

    #[test]
    fn a_set_poster_sends_the_set_again_when_an_entry_is_added_and_changes_it_in_place_when_one_is_removed() {
        let mut board = Blackboard::new(entity_group_scope(1), 10);
        let (added, _) = SetPoster::entry_added(&mut board, "S", 1, Value::Int(1));
        assert!(added);
        assert!(SetPoster::entry_added(&mut board, "S", 2, Value::Int(2)).0);
        assert!(!SetPoster::entry_added(&mut board, "S", 3, Value::Int(2)).0, "already in the set");
        assert_eq!(
            board.message_channel("S").messages().len(),
            2,
            "sent when an entry was added, and not otherwise"
        );
        assert!(SetPoster::entry_removed(&mut board, "S", &Value::Int(1)));
        assert!(!SetPoster::entry_removed(&mut board, "S", &Value::Int(1)));
        assert_eq!(board.message_channel("S").last_message_value(), Value::Set(vec![Value::Int(2)]));
        assert_eq!(board.message_channel("S").messages().len(), 2, "nothing was sent for the removal");
        assert!(!SetPoster::entry_removed(&mut board, "NOTHING", &Value::Int(1)));
    }

    #[test]
    fn an_address_goes_to_a_value_and_back() {
        let a = Address::new(ScopeType::EntityGroup, "COMMANDER");
        assert_eq!(Address::from_value(&a.to_value()), Some(a));
        assert_eq!(Address::from_value(&Value::Int(3)), None);
        assert_eq!(ScopeType::Item.display("7"), "Item Blackboard: 7");
    }
}
