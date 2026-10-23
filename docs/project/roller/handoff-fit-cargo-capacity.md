# Cargo capacity loss while unfit or offline

## Goal and source decisions

The active ship must not be left with cargo volume above the hold capacity after a fitted capacity modifier is removed or taken offline. The check allows cargo exactly at the resulting capacity (using the existing hold tolerance). It uses Dogma's calculated capacity and item volume from the pinned SDE.

EveJS 261018.1 checks this before changing state and returns a CustomNotify carrying measured cargo volume and resulting capacity. V24.01's inventory and Dogma callers surface that notification. The matching EVE University rules say unfitted modules occupy cargo and offline modules give no benefit. EveJS v0.12.9 lacks this capacity-loss guard.

## Implementation

- CharacterShips.MoveAsync checks fitting-slot departures and moves to cargo before commit.
- CharacterShips.SetOnlineAsync checks calculated capacity before persisting the offline state.
- InvBrokerService.MultiAdd preflights a batch unfit so an initially overflowing batch does not partially move modules.
- The refusal carries measured volumes and maps to the reference notification text.
- Ship tests cover below/equal/above resulting limits, refusal state preservation, freeing cargo and retrying, online/offline, unfit, batch preflight, notification order, and a fresh ship-state reload.
- Inventory service tests dispatch client-shaped batch MultiAdd, assert the notification payload, and check all source items and cargo stay put.
- The headless client scenario follows V24.01 inventory Add and Dogma online/offline calls, fills cargo until the server refuses another addition, checks an offline refusal, frees cargo and retries, then unfits the module.

## Verification and handoff

No database was used locally. `Eve.Ships` and `Eve.Ships.Tests` built successfully with zero warnings and errors, and the focused no-database capacity predicate test passed. `Eve.Host` built successfully with zero warnings and errors using `BuildProjectReferences=false` and analyzers disabled after its initial full build was killed while compiling Host under local memory pressure. `bash -n tools/dev/run-eve-live.sh` passes after the SSO port check fix.

Actions run 39971814620 passed selected capacity predicate, offline refusal, and batch inventory regressions (1 passed, 0 failed, 0 skipped). Run 39989364663 passed the selected capacity tests (Inventory 4 passed, Ships 11 passed, 0 skipped); its live job initially exposed a missing Host wrapper unwrap, now handled by SingleRegisteredService<T>. The next live run, 39990657912, passed the same .NET tests but Host startup then failed reading `sde.skinrComponentPointValues` because the relation was absent. The SDE lock lists this table and the workflow runs `eve-sde ensure`; the import/startup mismatch needs to be diagnosed before the capacity walk can proceed. No graphical-client check was made.

Keep the worktree's pre-existing deletions under crates/eve-net, crates/eve-sde, crates/eve-sim, and crates/eve-space out of the commit.

Implementation pushes: 80d07d8c and 55f2dd87. Cargo implementation commit: 64bdd41c (merged to main); headless setup/error reporting changes are in 30f338d7 and 5398d1ca. The Host wrapper correction and live-script syntax correction are on main.
