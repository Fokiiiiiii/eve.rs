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

No database was used locally. The focused no-database capacity predicate test passed. Ship and Inventory test projects both built successfully with zero warnings and errors.

The first Actions dispatch supplied an empty live-test input, but GitHub selected the workflow default fast set and started 12 live test files. That run failed during the live-test step; the returned log tail did not include the underlying case details. The new focused headless walk is added and should be dispatched by filename to avoid the default set. Database-backed ship and inventory cases still need Actions verification. No graphical-client check was made.

Keep the worktree's pre-existing deletions under crates/eve-net, crates/eve-sde, crates/eve-sim, and crates/eve-space out of the commit.

Implementation push: 80d07d8c. Latest Actions run: 39950881618 (default fast live set). Current headless regression and final CI run: pending.
