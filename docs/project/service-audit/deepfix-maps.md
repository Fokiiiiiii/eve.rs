# Deep correctness fix: map history ship type filtering

## Verified defect and correction

`MapTelemetryState.History` accepted a killmail whenever its victim ship type was positive and its category was either absent or category 6. The missing category case meant an unknown typeID in persisted killmail data was counted as a ship kill. EveJS requires `isKnownShipOrCapsuleType` before counting: `/home/user/evejs-261028.1/src/server/src/services/map/mapService.js:287-300`; faction warfare history applies the same check at `:333-345`.

The host now requires an SDE type category entry whose group category is 6 before including a killmail (`dotnet/src/Eve.Host/Composition/MapTelemetry.cs:128-150`). Host composition builds that lookup from pinned SDE `types` and `groups` (`dotnet/src/Eve.Host/Composition/MapTelemetryComposition.cs:65-88`; `types.groupID` -> `groups.categoryID`). This matches EveJS known ship/capsule filtering and category 6. It changes no stored records, output columns, cache interval, error mapping, or notifications.

The Host wire regression adds an otherwise valid recent faction kill with a typeID absent from the SDE category fixture (`dotnet/tests/Eve.Host.Tests/MapTelemetryHostWireTests.cs:52-81`). Before the fix, the registered map RPC returned 3 ship kills where the assertion expected 2. After the fix it returns the expected row `(solarSystemID, value1=2, value2=1, value3=1)` for regular kills and the corresponding faction warfare row. The route is registered through `DungeonRpc` and `MapAccessGuardService` (`dotnet/src/Eve.Host/Rpc/DungeonRpc.cs:40-56`).

## Whole-client trace and behavior

The existing whole-tree client audit `DEEPCHECK_map.md` traced every `RemoteSvc('map').GetHistory` caller. The pathfinder reads `value3` as pod kills (`/home/user/eve-logic/eve/client/script/util/clientPathfinderService.py:241`); map UI callers include `starModeHandler.py:479,509,538,541,570`, `mapViewColorHandler.py:612,624,638,641,654`, and filters `mapFilterJumps.py:21`, `mapFilterKills.py:25`, `mapFilterPodKills.py:20`, and `mapFilterFactionKills.py:23`. Those consumers use `solarSystemID` and `value1/value2/value3` to color or filter systems. The reply remains the same cached rowset with those columns and a one-minute version check (`dotnet/src/Eve.Services.Station/StationService.cs:379-384`; EveJS `mapService.js:590-603`). This is a read-only RPC with no state transition, notification, or player-facing error path. The existing Abyssal map guard remains in place.

Other client behavior checked for this area is recorded in `DEEPCHECK_map.md`; this fix only covers the kill-history classification defect above.

## Checks

- `dotnet build dotnet/tests/Eve.Host.Tests/Eve.Host.Tests.csproj -p:UseSharedCompilation=false --no-restore` passed, 0 warnings and 0 errors.
- On the old predicate, `Map_telemetry_and_beacons_keep_client_wire_shapes_through_registered_route` failed: expected 2 kills, got 3.
- On PostgreSQL lane `eve_p5`, via `tools/dev/db-lock.sh`, the fixed `Map_telemetry_and_beacons_keep_client_wire_shapes_through_registered_route` and `Map_inside_abyssal_space_returns_the_reference_user_error_over_host_wire` passed: 2 passed, 0 failed, 0 skipped.
- That focused Host fixture deliberately uses a closed PostgreSQL port for its injected read source; the database lane/lock was used, but the focused regression itself does not query PostgreSQL. The change is limited to pure aggregation logic, not persistence or database query behavior.
- No graphical V24.01 client session was run.
