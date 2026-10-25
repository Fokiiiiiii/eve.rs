# RPC Host coverage handoff: cx5

## Current state

This handoff tracks the requested batch of 24 RPC coverage rows. Method names and client callsites match this checkout, but some supplied RPC numbers differ from `docs/project/service-audit/EVE_RS_COVERAGE.csv`: `CreateNewbieShip` is 01355/01356 here, and the listed ship methods are 02388–02406. Keep the current catalogue IDs.

Added real socket Host regressions:
- `ShipRpcHostTests.cs`: `ship.GetShipFittingInfo` and bound `ship.GetDirtTimestamp`, both with transport `machoVersion`.
- `BeyonceRpcHostTests.cs`: `AdminGetBubbleID`, `GetCynoJammerState`, and `GetFormations`, with the client-consumed shapes and transport `machoVersion`.
- `DogmaRpcHostTests.cs`: `CreateNewbieShip` uses the registered DogmaIM service and callback, with transport `machoVersion`.

These use the actual Host dispatcher, socket, registry, packet codec, and production services. The fitting and newbie-ship providers are test callbacks. Dispatch `40227652132` and push gate `40227646004` failed before build/test execution: `actions/setup-dotnet` attempted to download .NET 10.0.14 and SDK 10.0.403 from `dotnetorigin.blob.core.windows.net`, both returning HTTP 409. The official .NET download page lists runtime 10.0.13 and SDK 10.0.402 as the October 13, 2026 release.

## Client flow evidence

- `eve/client/script/ui/crate/controller.py:78,258-259` destructures `GetShipFittingInfo` into name, icon, hull type, and insurance.
- `eve/client/script/ui/services/menusvc.py:866` and `eve/client/script/ui/view/hangarBehaviours/baseHangarBehaviours.py:88-89` consume `GetDirtTimestamp` for hangar dirt state.
- `eve/client/script/ui/services/shipConfigSvc.py:50-56` reads `GetShipConfiguration` into cached config; `SetShipConfig` then calls `ConfigureShip`.
- `eve/client/script/ui/services/menuSvcExtras/invItemFunctions.py:36-38` calls `LaunchFromContainer(locationID, itemID)` after the client state check.
- `eve/client/script/ui/shared/fitting/cleanShipButton.py:28-35` consumes `ResetDirtTimestamp` and refreshes the hangar view.
- `eve/client/script/ui/shared/bookmarks/bookmarkSvc.py:622-633` unpacks eight values from `BookmarkScanResult` and inserts the returned bookmark into the client cache.
- `eve/client/script/parklife/fleetSvc.py:883-891` consumes fleet member positions from `GetFleetLocationAndInfo` and derives nearest-ball IDs.
- `eve/client/script/remote/michelle.py:286-295` caches `GetFleetState` only when a ballpark exists and fleet ID is set; :323-325 loads `GetFormations`; :535-548 unpacks seven values from `GetRelativity`; :762-764 returns `AdminGetBubbleID` to its caller.
- `sovDashboard/cynoJammerIcon.py:45-46` passes `GetCynoJammerState` to its icon state updater.
- `eve/client/script/ui/station/base.py:596-614` calls `CreateNewbieShip(shipID, locationID)`; the result is unused there.
- `eve/client/script/ui/services/menuSvcExtras/invItemFunctions.py:163-200` calls `AssembleShip(itemIDs, name, subSystems)` and uses the returned item ID.
- `eve/client/script/util/eveMisc.py:28-31` invokes `LaunchDrones` and `Drop`; `eve/client/script/ui/services/structure/structureDocking.py:91-94` consumes the capsule ID from `LeaveShip`.

## EveJS comparison

- Ship handlers: `shipService.js:1161-1171` (`GetDirtTimestamp`), :1193-1213 (`ResetDirtTimestamp`), :1248-1274 (`GetShipFittingInfo`), :1288-1303 (`LaunchDrones`), :1458-1476 (`LaunchFromContainer`), :1820-1836 (`LeaveShip`), :2062-2086 (`GetShipConfiguration`).
- `beyonceService.js:4262-4286` resolves the scan result to a bookmark target, adds it to the requested folder, and returns the created bookmark tuple.
- Indexed v0.12.9 search did not locate `GetFleetLocationAndInfo`, `GetFleetState`, or `GetRelativity`; the bound park implementation and client wire contract remain to be examined.

## Remaining work

The other requested rows still need their Host providers/state transitions, real Host wire regressions, current catalogue updates, full client caller/consumer evidence, focused verification, and small green commits pushed to main. Do not mark a row complete until its test passes and required persistence/notification behavior is covered.
