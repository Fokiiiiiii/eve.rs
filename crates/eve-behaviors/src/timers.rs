//! Timers that send their id to a channel when they start and `None` when they run out (`behaviors/timers/__init__.py`). The client sleeps a tasklet
//! in simulation time; here the owner of the system asks which timers are due (`due`) and clears them.

use std::collections::BTreeMap;

use crate::blackboards::Address;

/// A timer: what channel it tells, and when it runs out.
#[derive(Debug, Clone, PartialEq)]
pub struct Timer {
    pub id: i64,
    pub due: i64,
    pub scope_id: i64,
    pub address: Address,
}

/// The timers that are running.
#[derive(Debug, Default, Clone)]
pub struct TimerManager {
    active: BTreeMap<i64, Timer>,
    next_id: i64,
}

impl TimerManager {
    pub fn new() -> Self {
        Self { active: BTreeMap::new(), next_id: 1 }
    }

    /// `StartTimer(time, channel)`: a timer that runs out `seconds` from `now` (in ticks of `unit` a second). Returns its id (which the caller
    /// sends to the channel, as the client's `Timer.__init__` does).
    pub fn start_timer(&mut self, now: i64, ticks: i64, scope_id: i64, address: Address) -> i64 {
        let id = self.next_id;
        self.next_id += 1;
        self.active.insert(id, Timer { id, due: now + ticks, scope_id, address });
        id
    }

    /// `RemoveTimer`.
    pub fn remove_timer(&mut self, id: i64) -> Option<Timer> {
        self.active.remove(&id)
    }

    /// `ClearTimer(timerID)` (stopping it, and telling the channel `None`, is for the caller: it gets the timer back).
    pub fn clear_timer(&mut self, id: i64) -> Option<Timer> {
        self.active.remove(&id)
    }

    /// The timers that have run out by `now`, earliest first; they are taken off (the caller tells their channels `None`).
    pub fn take_due(&mut self, now: i64) -> Vec<Timer> {
        let mut due: Vec<Timer> = self.active.values().filter(|t| t.due <= now).cloned().collect();
        due.sort_by_key(|t| (t.due, t.id));
        for timer in &due {
            self.active.remove(&timer.id);
        }
        due
    }

    pub fn active_count(&self) -> usize {
        self.active.len()
    }

    pub fn is_active(&self, id: i64) -> bool {
        self.active.contains_key(&id)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::blackboards::ScopeType;

    fn address() -> Address {
        Address::new(ScopeType::Item, "T")
    }

    #[test]
    fn timers_get_ids_from_one_and_run_out_in_the_order_they_are_due() {
        let mut timers = TimerManager::new();
        let a = timers.start_timer(0, 100, 5, address());
        let b = timers.start_timer(0, 50, 5, address());
        assert_eq!((a, b), (1, 2));
        assert!(timers.take_due(49).is_empty());
        assert_eq!(timers.take_due(100).iter().map(|t| t.id).collect::<Vec<_>>(), [2, 1]);
        assert_eq!(timers.active_count(), 0);
    }

    #[test]
    fn a_timer_that_is_cleared_does_not_run_out() {
        let mut timers = TimerManager::new();
        let a = timers.start_timer(0, 10, 5, address());
        assert!(timers.is_active(a));
        assert!(timers.clear_timer(a).is_some());
        assert!(timers.clear_timer(a).is_none(), "clearing a timer that is gone does nothing");
        assert!(timers.take_due(1_000).is_empty());
    }
}
