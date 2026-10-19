//! The event messenger of the ballpark that behavior tasks subscribe to (`ballpark.eventMessenger`): messages about an item, told to whoever
//! asked to hear them. The server's own `ballpark` package is not part of the client; only what the behaviors use of it is here.

use std::collections::HashMap;

use crate::blackboards::Observer;

/// A kind of message (`ballpark.messenger.const`). The client's constants are numbers the world and the tasks share and its extract does not have
/// them; a named message is one the tasks here know by name (the world that sends it and the tasks that subscribe agree on it), and a numbered one is
/// what a task's authored attribute holds (`monitoredMessageId`).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, PartialOrd, Ord)]
pub enum MessageId {
    Named(&'static str),
    Number(i64),
}

/// The messages of `ballpark.messenger.const` (and `entitygroup`) the tasks use.
pub mod message {
    use super::MessageId;

    /// What the dogma engine tells of an effect that starts or stops (`RegisterEffectCallback(('OnEffect', task))`): a notify event, with the arguments
    /// `(effect id, start, active, ship id, other type id, duration, repeat, error)`.
    pub const EFFECT_CALLBACK: MessageId = MessageId::Named("OnEffect");

    /// What the ballpark tells of a ball entering or leaving a bubble that has events on (`DoBubbleProximity`): a notify event, with the arguments
    /// `(solar system id, bubble id, ball id, is entering)`.
    pub const DO_BUBBLE_PROXIMITY: MessageId = MessageId::Named("DoBubbleProximity");

    macro_rules! messages {
        ($($name:ident),* $(,)?) => {
            $(pub const $name: MessageId = MessageId::Named(concat!("MESSAGE_", stringify!($name)));)*
        };
    }

    messages!(
        INVULNERABILITY_CANCELED,
        ON_BUBBLE_PROXIMITY_ENTERED,
        ON_BALLPARK_ITEM_DESTROY,
        ON_ITEM_REMOVED_FROM_PARK,
        ON_PROXIMITY,
        ON_CLOSE_TO_ABYSSAL_DANGER_ZONE,
        TARGET_ADDED,
        TARGET_LOST,
        ON_DOGMA_ATTRIBUTE_VALUE_CHANGED,
        ON_DAMAGE_CHANGED,
        ON_ADDITIONAL_LOOT_REMOVED,
        ON_INVENTORY_CHANGE,
        ON_BALL_MODE_CHANGED,
        ON_UNSPAWNED_WITH_ADDITIONAL_LOOT,
        ON_DROPPED_ADDITIONAL_LOOT,
        CRIMEWATCH_EVENT_STARTED,
        INDIRECT_OFFENSE_EVENT,
        ON_CLOAK_BALL,
        ON_ENTITY_GROUP_MEMBERSHIP_CHANGED,
        MAX_LOCKED_TARGETS_CHANGED,
        SEC_STATUS_LOST_EVENT,
        ON_EFFECT_STARTED,
        ON_EFFECT_STOPPED,
        ON_DUNGEON_TIMER_EXPIRED,
        ON_DUNGEON_TRIGGER,
        ON_DUNGEON_TRIGGER_EVENT,
        TURBO_SHIELD_STATE_CHANGED,
        ENTITY_SUPER_WEAPON_STOPPED,
    );
}

/// What a message is about: an item, a bubble, an entity group, or the world at large (the client's service manager's notify events).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum Subject {
    Item(i64),
    Bubble(i64),
    Group(i64),
    /// `dungeon_spawn_key(dungeonSpawnId)`: what a dungeon's messages are about.
    DungeonSpawn(i64),
    Notify,
}

/// What a subscription is for: a kind of message, and, for the few kinds that come with one (an effect's id), which.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub struct EventKey {
    pub message: MessageId,
    pub param: Option<i64>,
}

impl EventKey {
    pub const fn of(message: MessageId) -> Self {
        Self { message, param: None }
    }

    pub const fn with(message: MessageId, param: i64) -> Self {
        Self { message, param: Some(param) }
    }
}

impl From<MessageId> for EventKey {
    fn from(message: MessageId) -> Self {
        EventKey::of(message)
    }
}

/// The subscriptions to messages about items.
#[derive(Debug, Default, Clone)]
pub struct EventMessenger {
    subscriptions: HashMap<(Subject, EventKey), Vec<Observer>>,
}

impl EventMessenger {
    pub fn new() -> Self {
        Self::default()
    }

    /// `SubscribeItem(itemID, messageId, handler)`.
    pub fn subscribe_item(&mut self, item_id: i64, key: impl Into<EventKey>, observer: Observer) {
        self.subscribe(Subject::Item(item_id), key, observer);
    }

    /// `UnsubscribeItem`: takes one subscription off; nothing happens when there is none.
    pub fn unsubscribe_item(&mut self, item_id: i64, key: impl Into<EventKey>, observer: &Observer) {
        self.unsubscribe(Subject::Item(item_id), key, observer);
    }

    /// Who is to be told of a message about an item.
    pub fn subscribers(&self, item_id: i64, key: impl Into<EventKey>) -> Vec<Observer> {
        self.subscribers_of(Subject::Item(item_id), key)
    }

    /// `messenger.SubscribeBubble`, `group.GetMessenger().SubscribeToMessage`, `sm.RegisterForNotifyEvent`: a subscription to a message about anything.
    pub fn subscribe(&mut self, subject: Subject, key: impl Into<EventKey>, observer: Observer) {
        self.subscriptions.entry((subject, key.into())).or_default().push(observer);
    }

    /// The other way: takes one subscription off.
    pub fn unsubscribe(&mut self, subject: Subject, key: impl Into<EventKey>, observer: &Observer) {
        let key = (subject, key.into());
        if let Some(list) = self.subscriptions.get_mut(&key) {
            if let Some(at) = list.iter().position(|o| o == observer) {
                list.remove(at);
            }
            if list.is_empty() {
                self.subscriptions.remove(&key);
            }
        }
    }

    /// Who is to be told of a message about a subject.
    pub fn subscribers_of(&self, subject: Subject, key: impl Into<EventKey>) -> Vec<Observer> {
        self.subscriptions.get(&(subject, key.into())).cloned().unwrap_or_default()
    }

    pub fn forget_tree(&mut self, tree: i64) {
        for list in self.subscriptions.values_mut() {
            list.retain(|o| o.tree != tree);
        }
        self.subscriptions.retain(|_, list| !list.is_empty());
    }

    pub fn subscription_count(&self) -> usize {
        self.subscriptions.values().map(Vec::len).sum()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    const HIT: MessageId = MessageId::Named("Hit");
    const EFFECT: MessageId = MessageId::Named("EffectStarted");

    fn observer(task: usize) -> Observer {
        Observer { tree: 1, task, handler: 0 }
    }

    #[test]
    fn a_message_is_told_to_those_who_subscribed_to_it_for_that_item() {
        let mut messenger = EventMessenger::new();
        messenger.subscribe_item(7, HIT, observer(1));
        messenger.subscribe_item(7, HIT, observer(2));
        messenger.subscribe_item(8, HIT, observer(3));
        assert_eq!(messenger.subscribers(7, HIT), [observer(1), observer(2)]);
        assert_eq!(messenger.subscribers(8, HIT), [observer(3)]);
        assert!(messenger.subscribers(9, HIT).is_empty());
    }

    #[test]
    fn a_message_about_a_bubble_a_group_or_the_world_is_kept_apart_from_one_about_an_item() {
        let mut messenger = EventMessenger::new();
        messenger.subscribe(Subject::Bubble(7), HIT, observer(1));
        messenger.subscribe(Subject::Group(7), HIT, observer(2));
        messenger.subscribe(Subject::Notify, MessageId::Number(5), observer(3));
        assert!(messenger.subscribers(7, HIT).is_empty());
        assert_eq!(messenger.subscribers_of(Subject::Bubble(7), HIT), [observer(1)]);
        assert_eq!(messenger.subscribers_of(Subject::Group(7), HIT), [observer(2)]);
        assert_eq!(messenger.subscribers_of(Subject::Notify, MessageId::Number(5)), [observer(3)]);
        messenger.unsubscribe(Subject::Bubble(7), HIT, &observer(1));
        assert!(messenger.subscribers_of(Subject::Bubble(7), HIT).is_empty());
    }

    #[test]
    fn a_message_with_a_parameter_is_another_message() {
        let mut messenger = EventMessenger::new();
        messenger.subscribe_item(7, EventKey::with(EFFECT, 11), observer(1));
        assert!(messenger.subscribers(7, EFFECT).is_empty());
        assert_eq!(messenger.subscribers(7, EventKey::with(EFFECT, 11)), [observer(1)]);
        assert!(messenger.subscribers(7, EventKey::with(EFFECT, 12)).is_empty());
    }

    #[test]
    fn a_subscription_is_taken_off_once_and_one_that_is_not_there_is_ignored() {
        let mut messenger = EventMessenger::new();
        messenger.subscribe_item(7, HIT, observer(1));
        messenger.unsubscribe_item(7, HIT, &observer(2));
        assert_eq!(messenger.subscription_count(), 1);
        messenger.unsubscribe_item(7, HIT, &observer(1));
        assert_eq!(messenger.subscription_count(), 0);
        messenger.unsubscribe_item(7, HIT, &observer(1));
    }

    #[test]
    fn a_tree_that_goes_away_takes_its_subscriptions_with_it() {
        let mut messenger = EventMessenger::new();
        messenger.subscribe_item(7, HIT, Observer { tree: 1, task: 1, handler: 0 });
        messenger.subscribe_item(7, HIT, Observer { tree: 2, task: 1, handler: 0 });
        messenger.forget_tree(1);
        assert_eq!(messenger.subscribers(7, HIT), [Observer { tree: 2, task: 1, handler: 0 }]);
    }
}
