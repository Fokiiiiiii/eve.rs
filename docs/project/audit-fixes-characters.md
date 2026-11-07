# Character audit fixes (2026-11-07)

This records the verified findings from `90-audit-eve-characters-r14.md` and their disposition. The audit's paths refer to the `cx6` worktree; source claims were checked against this worktree, the complete V24.01 client source in `/home/user/eve-logic`, and EveJS in `/home/user/evejs-261028.1` before changes.

All three findings were confirmed. No audit finding was skipped as false.

## P1 — Activate a clone installed in a ship

**Confirmed and fixed.** The client recognizes `clone.in_ship` in `jumpClonesPanel.py:624-637`, calls `CloneJump` in `:607`, and handles `JumpCheckIntoShip` by prompting and retrying with `confirmed=True` in `clonejumpsvc.py:213-230`. The ship disembark flow is `clonejumpsvc.py:234-261`. EveJS returns the same confirmation (`jumpCloneRuntime.js:1871-1875`) and transitions through `jumpSessionToShipCloneBay` (`:2210-2224`, `transitions.js:4919+`). Its arrival pose is the target's position minus the normalized facing vector times `max(2500, radius + 1500)` (`transitions.js:1493-1522`).

eve.rs now resolves the ship's authoritative space pose, validates the ship and its current system, asks for client confirmation before settlement, moves the capsule to the matching arrival pose, atomically moves implants and the clone, charges the fee and starts cooldown, then publishes the item/session and clone-cache notifications in order. The Host/PostgreSQL regression covers confirmation, settlement, remote-system arrival, persistence and notification order with the real Rust simulation. A separate regression checks that a ship not currently in space is refused without state, wallet or notification changes. The original confirmation test was run before the fix and failed with `CustomNotify` instead of `JumpCheckIntoShip`.

**Not adopted from the suggested fix:** checking for a fitted Clone Vat Bay again at activation is unsupported by observed behavior. The client checks for a Clone Vat Bay in the ship-install path (`clonejumpsvc.py:286-293`); its activation RPC path does not make that check. EveJS validates the target as a ship with an active space state, but does not recheck a fitted bay in `jumpSessionToShipCloneBay` (`transitions.js:4919+`). The bay is therefore an installation requirement, not an activation gate in these references.

## P2 — Serialize clone installs and bay capacity

**Confirmed and fixed.** Before the fix, the store inserted after snapshot checks without locking or rechecking the character's limit, location uniqueness, or shared ship capacity. The new Host/PostgreSQL logic takes a character row lock, rereads the skill-based clone limit and that character's station/structure occupancy inside the install transaction, and takes a transaction-scoped advisory lock by physical ship ID before checking the shared ship-bay capacity. A refusal happens before insertion and fee settlement. This follows EveJS `validateInstallJumpClone` (`jumpCloneRuntime.js:1522-1552`), which checks the requesting character's own `state.clones`.

The station-limit regression was run before the fix and failed: all concurrent installs succeeded where only one could fit. A second fail-first Host regression exposed that a global station count also refused a different character's later install; it failed before the character-scoped count was added. Passing tests assert one committed install and one fee under concurrent same-character station requests, two characters each installing at the same station, and one remaining slot under concurrent installs by different characters into the same ship. The shared-bay limit comes from pinned SDE type 28352's `maxJumpClones` attribute 979.

## P3 — Stale clone-destination documentation

**Confirmed and corrected.** The CH-16 record previously described only station destinations and called structure and ship destinations unsupported. `DEVIATIONS.md` now records station, structure and ship destinations, their different evidence levels, the ship confirmation and arrival behavior, and the absence of a verified activation-time Clone Vat Bay gate. `TODO.md` now separates the Host/headless coverage from the remaining graphical-client check and live pod-death check.

## Verification boundary

Headless Host/space simulation tests are not graphical V24.01 client verification. The ship-jump scenario runs the Host and Rust simulation and checks wire/session state, persistence, wallet settlement and notifications. Pod-death respawn was not exercised with a live kill in this task.

EveJS was source-traced but not launched: the reference checkout has no clone-jump test suite or server test script to run as an isolated functional flow. Its activation and arrival paths are cited above; the same successful and refused scenarios were executed against eve.rs Host plus the real Rust simulation.

## Pre-push verification record

- Release builds with warnings treated as errors: all five touched source projects (`Eve.Characters`, `Eve.Services.Core`, `Eve.Space`, `Eve.Services.Space`, `Eve.Host`) and six related test projects (`Eve.Characters.Tests`, `Eve.Services.Core.Tests`, `Eve.Services.Space.Tests`, `Eve.Host.Tests`, `Eve.Space.Tests`, `Eve.Services.Dogma.Tests`) succeeded with 0 warnings and 0 errors.
- With `EVE_PG_URL=postgres://postgres@127.0.0.1:5432/eve_p5`: `CharacterHostTests`, `JumpCloneReadHostTests`, `JumpCloneSettlementHostWireTests` and `ServiceAuditGtoMHostWireTests` reported 53 passed, 0 failed, 1 skipped. The skip was the real-simulation ship-jump test because that invocation omitted its simulation/SDE variables; that skip was not counted as a pass. Re-running `Confirmed_ship_clone_jump_moves_the_capsule_settles_state_and_notifies_after_space_entry` with `EVE_ABYSSAL_SIM_BINARY` pointing to the built `eve-sim` and `EVE_ABYSSAL_SIM_SDE` pointing to pinned build 3396210 SDE passed: 1 passed, 0 failed, 0 skipped. `JumpCloneStoreTests`: 8 passed, 0 failed, 0 skipped.
- `JumpCloneServiceTests`, `JumpCloneShipInstallTests`, `CoreCatalogTests` and `GameRpcServicesTests`: 16 passed, 0 failed, 0 skipped.
- `StructureAnchorTests` and `StructureAnchorDelayTests`: 13 passed, 0 failed, 0 skipped. `TeleportCommandTests`: 26 passed, 0 failed, 0 skipped. `SimClientTests` and `SimCodecTests`: 518 passed, 0 failed, 0 skipped.
- `PlayerDeathTests`, `RelocationTests` and `SpaceServiceTests` in one combined run: 190 passed, 1 failed, 0 skipped. The failure was `Autopilot_warp_uses_the_reference_arrival_range_and_keeps_the_warp_refusal_contract`, rejected by the dock movement policy. That test passed on focused retry, and the complete `SpaceServiceTests` class passed separately: 168 passed, 0 failed, 0 skipped. The injected session-change delay completes immediately, while `LocalTimers.Set` dispatches its completion action with `Task.Run`; this is a plausible source of the one-off policy race, but that cause was not directly captured. No assertion or expected result was changed.
- The existing `ModuleEffectsTests`, `ModuleRuleTests`, `PlayerDeathTests` and `SuperWeaponAttributeIdsTests` slice also passed: 54 passed, 0 failed, 0 skipped.
- Rust: `cargo fmt --all`, `cargo fmt --all -- --check`, `cargo check -p eve-space`, `cargo clippy -p eve-space --all-targets -- -D warnings`, `cargo build -p eve-sim`, and the three focused `eve-space` tests passed (one test per focused invocation).
- Main CI run 1611 on `47ec83bf6ad4` first failed in Host shard 1/6 during RPC-fuzz teardown, which found an idle-in-transaction EventBus subscriber cursor update. The same failed shard was rerun and passed; two isolated Actions runs of the reported RPC-fuzz test also passed. The failure did not reproduce, so its underlying timing cause remains unconfirmed. The overall run then completed successfully.

No test was skipped in the dedicated P1 simulation run. The initial aggregate Host skip and the initial combined Space test failure are recorded above rather than counted as passes.
- The new confirmation regression and concurrent station-install regression both failed before their respective fixes. The Host suite above passed with both fixes present.
