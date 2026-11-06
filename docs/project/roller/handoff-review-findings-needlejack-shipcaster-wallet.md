# Needlejack, shipcaster, and corporate wallet review handoff

## Finding 3 — starter shipcaster eligibility

Verified: production registered `starterShipcasterTravelMgr` without an eligibility policy, while `ShipcasterRpcService.CallAsync` deliberately raises `MissingBackend` when the policy is absent. The V24.01 client returns this RPC result directly from `eve/client/script/parklife/shipcasterSvc.py:32-33`. The local launcher uses a separate local eligibility check at `eve/client/script/shipcaster/client/components/shipcasterLauncher.py:238-246`; a whole-client search found no caller of `CanJumpStarterShipcaster` in the snapshot. EveJS wires the same RPC to its runtime eligibility provider at `src/server/src/services/shipcaster/starterShipcasterTravelMgrService.js:23-29`.

Change: production now registers through `CharacterServicesComposition.CreateStarterShipcasterService`, which returns false and emits one warning on its first use until an eligibility provider is supplied. The service-level missing-policy exception remains a guard. The Host wire test uses this same production factory, checks repeated false results and one warning; `ShipcasterRpcTests` asserts true and false policy results.

Checks passed:
- `dotnet build dotnet/src/Eve.Services.Space -p:UseSharedCompilation=false`
- `dotnet build dotnet/src/Eve.Host -p:UseSharedCompilation=false`
- `MSBUILDDISABLENODEREUSE=1 dotnet test dotnet/tests/Eve.Services.Space.Tests --filter 'FullyQualifiedName~ShipcasterRpcTests' -p:UseSharedCompilation=false` (16 passed, 0 skipped)
- `MSBUILDDISABLENODEREUSE=1 dotnet test dotnet/tests/Eve.Host.Tests --filter 'FullyQualifiedName~Movement_rpcs_use_registered_host_routes_with_client_wire_shapes' -p:UseSharedCompilation=false` (1 passed, 0 skipped)

## Remaining review findings

Findings 1, 2, and 4 are being traced against Host state, persistence, tests, and the full V24.01 client. Record their result, evidence, checks, commit, and Actions run here before handing off.
