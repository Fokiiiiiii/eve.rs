# Host coverage handoff: RPC-00917–RPC-02452

## Current increment

- Added `ShipRpcBurstHostTests.Ship_drone_launch_accepts_the_normal_500_entry_wire_burst` in `dotnet/tests/Eve.Host.Tests/ShipRpcBurstHostTests.cs`.
- The test opens a TCP Host, registers the production `ShipService`, sends a bound `LaunchDrones` call with transport `machoVersion` and 500 request tuples, then checks the 500-entry reply and provider input.
- Client evidence: `/home/user/eve-logic/eve/client/script/util/eveMisc.py:9-48` builds `oldItems`, calls `GetShipAccess().LaunchDrones` at line 28, consumes each returned mapping at lines 37-48, and scatters `OnItemLaunch` at line 48. A whole-tree search found this service call; UI and menu entry points funnel through it.
- EveJS comparison: `/home/user/evejs-v0.12.9/server/src/services/ship/shipService.js:1288-1303` passes the first argument list to `launchDronesForSession` and returns its dictionary response. eve.rs parses the first argument at `dotnet/src/Eve.Services.Space/ShipService.cs:99-112` and forwards requests to `ISpaceDrones`.
- Local checks: `dotnet build dotnet/tests/Eve.Host.Tests/Eve.Host.Tests.csproj -p:UseSharedCompilation=false` passed with zero warnings/errors. The focused burst test passed (1 passed, 0 skipped). No PostgreSQL was used.
- GitHub Actions focused run `40237033323` passed for commit `34e9d74ca09703e0684e06bc1bf757ae09ac6499` (1 passed, 0 skipped). Push-triggered `ci.yml` run `40237025398` remains in progress at this handoff update.

## Existing real-Host wire coverage observed

- `BeyonceRpcHostTests.cs` covers `AdminGetBubbleID`, `GetCynoJammerState`, and cached `GetFormations` through the TCP Host.
- `DogmaRpcHostTests.cs` covers registered `CreateNewbieShip` and its `None` reply.
- `ShipRpcHostTests.cs` covers the fitting-info tuple and nested `GetDirtTimestamp`.
- `MoreShipRpcHostTests.cs` covers nested `Drop` and `LaunchDrones` arguments and replies.
- These tests verify wire behavior, not persistence or the graphical client.

## Remaining work

The requested batch is still open. Missing service methods still requiring authoritative Host wiring and client/EveJS traces are `BookmarkScanResult`, `GetFleetLocationAndInfo`, `GetFleetState`, `GetRelativity`, `GetShipConfiguration`, `LaunchFromContainer`, and `ResetDirtTimestamp`. The other implemented rows still need per-row caller/consumer evidence, behavior comparison, and focused regressions wherever existing Host tests do not exercise the client-visible path. No batch completion claim is made.
