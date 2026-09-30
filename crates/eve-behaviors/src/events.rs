//! The event messenger of the ballpark that behavior tasks subscribe to (`ballpark.eventMessenger`): messages about an item, told to whoever
//! asked to hear them. The server's own `ballpark` package is not part of the client; only what the behaviors use of it is here.

use std::collections::HashMap;

use crate::blackboards::Observer;

/// The name of a kind of message (`ballpark.messenger.const`). The client's constants are numbers the world and the tasks share; here a name is
/// enough, and the world that sends them and the tasks that subscribe agree on it.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, PartialOrd, Ord)]
pub struct MessageId(pub &'static str);

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
    subscriptions: HashMap<(i64, EventKey), Vec<Observer>>,
}

impl EventMessenger {
    pub fn new() -> Self {
        Self::default()
    }

    /// `SubscribeItem(itemID, messageId, handler)`.
    pub fn subscribe_item(&mut self, item_id: i64, key: impl Into<EventKey>, observer: Observer) {
        self.subscriptions.entry((item_id, key.into())).or_default().push(observer);
    }

    /// `UnsubscribeItem`: takes one subscription off; nothing happens when there is none.
    pub fn unsubscribe_item(&mut self, item_id: i64, key: impl Into<EventKey>, observer: &Observer) {
        let key = (item_id, key.into());
        if let Some(list) = self.subscriptions.get_mut(&key) {
            if let Some(at) = list.iter().position(|o| o == observer) {
                list.remove(at);
            }
            if list.is_empty() {
                self.subscriptions.remove(&key);
            }
        }
    }

    /// Who is to be told of a message about an item.
    pub fn subscribers(&self, item_id: i64, key: impl Into<EventKey>) -> Vec<Observer> {
        self.subscriptions.get(&(item_id, key.into())).cloned().unwrap_or_default()
    }

    /// Drops everything a tree subscribed to (its owner is gone).
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

    const HIT: MessageId = MessageId("Hit");
    const EFFECT: MessageId = MessageId("EffectStarted");

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
