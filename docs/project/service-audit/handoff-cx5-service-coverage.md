# CX5 service coverage handoff

## Completed increment

The `repairSvc` NPC station flow has a registered Host wire regression:
`dotnet/tests/Eve.Host.Tests/RepairSvcHostWireTests.cs:Repair_service_binds_quotes_and_commits_station_repairs_over_host_wire`.
It sends `MachoBindObject`, bound `GetRepairQuotes`, and `RepairItemsInStation` through the Host socket. It asserts quote rows, `OnItemChange` notification before the `None` reply, repaired ship state, wallet balance and repair journal persistence. The matching CSV rows RPC-02214 and RPC-02215 cite this test. The merged `RepairStructureHostWireTests.Registered_repair_service_quotes_charges_tax_and_persists_or_refuses_atomically` also covers RPC-02216 with structure tax, notification, persistence and insufficient-funds refusal.

## Client and reference evidence

- V24.01 `eve/client/script/ui/station/repairshop/base_repairshop.py:51-70` binds the docked station repair moniker and calls the quote, station-repair, or structure-repair RPC. `base_repairshop.py:238-243` consumes quotes keyed by item ID.
- Repair notification consumers include `eve/client/script/ui/services/inventorysvc.py:56-57` and `eve/client/script/environment/invCache.py:222-232`; these update the client inventory cache from `OnItemChange`.
- EveJS 261028.1 `server/src/services/station/repairService.js:181-205` returns null after syncing repair changes. Its `repairRuntime.js:1436-1468` computes default full payment and enforces the station context.
- Production Host composition registers this service in `dotnet/src/Eve.Host/Rpc/StationRpc.cs:45`.

## Checks

- `MSBUILDDISABLENODEREUSE=1 dotnet build dotnet/tests/Eve.Host.Tests --no-restore -p:UseSharedCompilation=false -m:2`: passed, 0 warnings/errors.
- Post-merge `MSBUILDDISABLENODEREUSE=1 dotnet build dotnet/tests/Eve.Host.Tests --no-restore -p:UseSharedCompilation=false -m:2`: passed, 0 warnings/errors.
- Post-merge eve_p5 Host tests (`Repair_service_binds_quotes_and_commits_station_repairs_over_host_wire` and `Registered_repair_service_quotes_charges_tax_and_persists_or_refuses_atomically`): 2 passed, 0 failed, 0 skipped.
- Post-merge eve_p5 `Eve.Services.Station.Tests`, filter `RepairServiceTests`: 8 passed, 0 failed, 0 skipped.
- No graphical-client session was run.

## Remaining requested service groups

`voteManager` is now covered on main: `dotnet/tests/Eve.Host.Tests/VoteManagerHostTests.cs:Vote_manager_rpc_flow_uses_the_host_wire_and_persists_cases_and_ballots`.

`repairSvc` has all three requested rows covered, including the merged structure-repair test described above.

Continue with `authentication`, `inventoryAssetHoldingMgr`, `cache`, `machoNet`, `seasonManager`, `notificationMgr`, `operationsManager`, and `tourneyMgr`.

Current machoNet investigation: the client uses `GetConnectionProperties` in `eve/client/script/util/profiling.py:141,152`, `eve/devtools/script/networkdatamonitor.py:70,84-88`, `carbon/client/script/util/monitor.py:401,695-699`, and `carbon/common/script/net/http.py:46`; `GetConnectedClusterName` is used by `monolithconfig/__init__.py:396`. The Host `MachoNetService` currently exposes neither these three methods nor an implementation of `GetConnectionProperties`; its class body is `dotnet/src/Eve.Host/MachoNetService.cs:62-170`. Do not mark these rows tested until Host behavior exists and client-consumed values are sourced from authoritative runtime state.

The existing `handoff-cx5-tourneyMgr.md` records that only a narrow subset of reads/actions exists and that match lifecycle methods lack an authoritative runtime. Resolve implementation before adding tests for such methods.

## Worktree recovery

This increment did not include the earlier POS control-tower changes in `SpaceComposition.cs`, `HostComposition.cs`, `SpaceSessions.cs`, `PosMgrControlTowerHostWireTests.cs`, or the pre-existing shipcaster/tournament handoffs.
