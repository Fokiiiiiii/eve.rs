//! `behaviors/actions/*`: tasks that do something to the world or to the blackboards.

pub mod awareness;
pub mod battlefield;
pub mod blackboards;
pub mod combat;
pub mod combatmodules;
pub mod combatnavigation;
pub mod combattargets;
pub mod damage;
pub mod drones;
pub mod dungeon;
pub mod effects;
pub mod entities;
pub mod entityspawning;
pub mod eventlogger;
pub mod fighterabilities;
pub mod fleet;
pub mod group;
pub mod guard;
pub mod hauling;
pub mod map;
pub mod mathtasks;
pub mod objectspawning;
pub mod operations;
pub mod randomized;
pub mod reference_subtree;
pub mod reinforcements;
pub mod resourcewars;
pub mod rewards;
pub mod structures;
pub mod tale;
pub mod tanking;
pub mod timer;

use crate::tree::Status;

enter_task!(
    /// `ToggleAction`: succeeds or fails as `trueOrFalse` says.
    ToggleAction,
    |t| {
        let Some(value) = t.required("trueOrFalse") else { return };
        t.set_status_to_success_if_true_else_to_failed(value.truthy());
    }
);

enter_task!(
    /// `WaitAction`: waits (suspended) for something else to end it.
    WaitAction,
    |t| {
        t.set_status(Status::Suspended);
    }
);

enter_task!(
    /// `AutoFailAction`: fails.
    AutoFailAction,
    |t| {
        t.set_status(Status::Failure);
    }
);

#[cfg(test)]
mod tests {
    use super::*;
    use crate::tree::Attrs;
    use crate::tree::tests::Rig;

    #[test]
    fn toggle_wait_and_auto_fail() {
        assert_eq!(
            Rig::new().run(Box::new(ToggleAction), Attrs::new().with("trueOrFalse", true)),
            Status::Success
        );
        assert_eq!(
            Rig::new().run(Box::new(ToggleAction), Attrs::new().with("trueOrFalse", false)),
            Status::Failure
        );
        assert_eq!(Rig::new().run(Box::new(WaitAction), Attrs::new()), Status::Suspended);
        assert_eq!(Rig::new().run(Box::new(AutoFailAction), Attrs::new()), Status::Failure);
    }
}
