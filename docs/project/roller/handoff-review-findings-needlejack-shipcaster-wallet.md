# Needlejack, shipcaster, and corporate wallet review handoff

## Finding 3 — starter shipcaster eligibility

Verified: production registered `starterShipcasterTravelMgr` without an eligibility policy, while `ShipcasterRpcService.CallAsync` deliberately raises `MissingBackend` when the policy is absent. The V24.01 client returns this RPC result directly from `eve/client/script/parklife/shipcasterSvc.py:32-33`. The local launcher uses a separate local eligibility check at `eve/client/script/shipcaster/client/components/shipcasterLauncher.py:238-246`; a whole-client search found no caller of `CanJumpStarterShipcaster` in the snapshot. EveJS wires the same RPC to its runtime eligibility provider at `src/server/src/services/shipcaster/starterShipcasterTravelMgrService.js:23-29`.

Change: production now registers through `CharacterServicesComposition.CreateStarterShipcasterService`, which returns false and emits one warning on its first use until an eligibility provider is supplied. The service-level missing-policy exception remains a guard. The Host wire test uses this same production factory, checks repeated false results and one warning; `ShipcasterRpcTests` asserts true and false policy results.

Checks passed:
- `dotnet build dotnet/src/Eve.Services.Space -p:UseSharedCompilation=false`
- `dotnet build dotnet/src/Eve.Host -p:UseSharedCompilation=false`
- `MSBUILDDISABLENODEREUSE=1 dotnet test dotnet/tests/Eve.Services.Space.Tests --filter 'FullyQualifiedName~ShipcasterRpcTests' -p:UseSharedCompilation=false` (16 passed, 0 skipped)
- `MSBUILDDISABLENODEREUSE=1 dotnet test dotnet/tests/Eve.Host.Tests --filter 'FullyQualifiedName~Movement_rpcs_use_registered_host_routes_with_client_wire_shapes' -p:UseSharedCompilation=false` (1 passed, 0 skipped)

## Finding 3 — Actions confirmation

Commit `b71fcafcaacb` was pushed as `Fix starter shipcaster eligibility wiring`. Push-triggered CI run `42289361399` and selected agent-test run `42289403240` both completed successfully.

## Finding 1 — Needlejack cloak validation

Verified against the V24.01 client: `randomJump/client/clientRandomJumpValidator.py:99-102` treats either the ship ball's `isCloaked` state or an in-progress cloak as cloaked, and `:166-171` adds `SHIP_IS_CLOAKED` with the character ID. `randomJump/error.py:22-23` assigns code 15. `filamentSpoolup.py:25-36` calls the same validator for trace-gate activation; the random-jump service/controller are the only activation RPC path found in the client graph. The validator is local to each client, so checking each linked pilot server-side prevents moving a participating pilot whose own client would reject activation.

The Host can read cloak state from its authoritative pilot state: active cloak module effects are retained in `RunningModules`, and jump-cloak state is tracked separately. The Rust scene decloak state is not queried by this Host path. `SpaceSessions.IsCloaked` now checks these Host-owned states and the effect names handled by the shared cloaking effect. Needlejack validation returns code 15 with the affected character ID for a cloaked activator or in-range linked fleet pilot. The Host test activates a real cloaking module for both cases and checks the error IDs, unchanged filament, and absent activation row.

Local checks passed: Host and Space project builds; the focused Host regression and all `NeedlejackActivationHostTests` (9 passed, 0 skipped); four cloak-related Space tests (4 passed, 0 skipped). Changes are awaiting the required merge/check/push cycle.

## Remaining review findings

Findings 2 and 4 still need the committed-relocation recovery and corporate-wallet notification/session-role traces, tests, and independent pushes.
