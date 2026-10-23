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

No database was used locally. `Eve.Ships` built successfully with zero warnings and errors, and the focused no-database capacity predicate test passed.

Actions run 39962433299 passed the batch `MultiAdd` regression (1 passed, 0 failed, 0 skipped). Run 39960957457 compiled the solution but the ship offline regression failed: `SetOnlineAsync(..., online: false)` returned `ShipChanged` instead of the expected overflow refusal. The offline guard now checks the resulting capacity on every offline request, relying on the before/after capacity comparison to avoid refusing unchanged state; this change still needs an Actions rerun. The first headless run failed during setup because the module was not found in the hangar after using its multiword name; the walk now grants verified type ID 1319 and needs a fresh run. No graphical-client check was made.

Keep the worktree's pre-existing deletions under crates/eve-net, crates/eve-sde, crates/eve-sim, and crates/eve-space out of the commit.

Implementation pushes: 80d07d8c and 55f2dd87. Latest main: 731e8df2. Commit be2c316f contains the offline guard adjustment and the type-ID headless setup correction; rerun the ship test and live walk.
