//! `behaviors/conditions/randomized.py`.

use crate::tree::BehaviorError;

enter_task!(RandomChanceCondition, |t| {
    let Some(chance) = t.required_float("chance") else { return };
    let dice = t.world().random();
    t.set_status_to_success_if_true_else_to_failed(chance >= dice);
});

enter_task!(
    /// The client calls `GetLastBlackboardValue(address, maxAge=...)`, and its `GetLastBlackboardValue` takes no `maxAge`: the task raises a
    /// `TypeError` every time it is entered. It is ported as it is; a tree that uses it stops at it, as it does there.
    CachedRandomChanceCondition,
    |t| {
        t.raise(BehaviorError::Task("TypeError: GetLastBlackboardValue() got an unexpected keyword argument 'maxAge'".to_string()));
    }
);

#[cfg(test)]
mod tests {
    use super::*;
    use crate::env::TestWorld;
    use crate::tree::tests::Rig;
    use crate::tree::{Attrs, Status};

    #[test]
    fn a_random_chance_succeeds_when_the_chance_is_at_least_the_dice() {
        for (dice, wanted) in [(0.2, Status::Success), (0.3, Status::Success), (0.31, Status::Failure)] {
            let mut rig = Rig::new();
            rig.world = TestWorld::with_numbers(&[dice]);
            assert_eq!(rig.run(Box::new(RandomChanceCondition), Attrs::new().with("chance", 0.3)), wanted);
        }
    }

    #[test]
    fn the_cached_random_chance_raises_as_the_clients_does() {
        let mut rig = Rig::new();
        rig.run(Box::new(CachedRandomChanceCondition), Attrs::new());
        assert!(matches!(rig.last_error, Some(BehaviorError::Task(ref m)) if m.contains("maxAge")));
    }
}
