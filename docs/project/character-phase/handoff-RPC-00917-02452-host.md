# Host coverage handoff: RPC-00917–RPC-02452

## Current increment

- Added `ShipRpcBurstHostTests.Ship_drone_launch_accepts_the_normal_500_entry_wire_burst` in `dotnet/tests/Eve.Host.Tests/ShipRpcBurstHostTests.cs`.
- The test opens a TCP Host, registers the production `ShipService`, sends a bound `LaunchDrones` call with the transport `machoVersion` kwarg and 500 request tuples, and checks the 500-entry reply and provider input.
- Client evidence: `/home/user/eve-logic/eve/client/script/util/eveMisc.py:9-48` builds `oldItems`, calls `GetShipAccess().LaunchDrones` at line 28, consumes each returned mapping at lines 37-48, and scatters `OnItemLaunch` at line 48. The entire tree search found this direct `LaunchDrones` service call; the menu and drone UI paths funnel into it.
- EveJS comparison: `/home/user/evejs-v0.12.9/server/src/services/ship/shipService.js:1288-1303` passes the first argument list to `launchDronesForSession` and returns its dictionary response. eve.rs parses the first argument in `dotnet/src/Eve.Services.Space/ShipService.cs:99-112` and forwards the resulting requests to the existing `ISpaceDrones` owner.
- Local checks: `dotnet build dotnet/tests/Eve.Host.Tests/Eve.Host.Tests.csproj -p:UseSharedCompilation=false` passed with zero warnings/errors. The focused burst test passed (1 passed, 0 skipped). No PostgreSQL was used.

## Existing real-Host wire coverage observed

- `BeyonceRpcHostTests.cs` covers `AdminGetBubbleID`, `GetCynoJammerState`, and cached `GetFormations` through the TCP Host.
- `DogmaRpcHostTests.cs` covers the registered `CreateNewbieShip` call and its `None` reply.
- `ShipRpcHostTests.cs` covers fitting-info tuple shape and nested `GetDirtTimestamp`.
- `MoreShipRpcHostTests.cs` covers nested `Drop` and `LaunchDrones` argument/reply handling.
- Those tests are evidence for existing wire behavior, not for persistence or live graphical-client execution.

## Remaining work

The requested batch is still open. Missing service methods still requiring authoritative Host wiring and client/EveJS traces are `BookmarkScanResult`, `GetFleetLocationAndInfo`, `GetFleetState`, `GetRelativity`, `GetShipConfiguration`, `LaunchFromContainer`, and `ResetDirtTimestamp`. The remaining implemented rows need per-row caller/consumer evidence, behavior comparison, and focused regressions where current Host coverage does not exercise the client-visible path. No completion claim is made for the batch.
