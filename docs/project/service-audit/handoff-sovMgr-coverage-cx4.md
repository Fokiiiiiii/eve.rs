# sovMgr coverage handoff — cx4

## Completed

- RPC-02548–RPC-02560 (12 coverage rows, 9 methods) are marked `IMPLEMENTED-TESTED` in `EVE_RS_COVERAGE.csv`.
- Host composition already registers `SovFwService.Create` through `ItemRpc` (`dotnet/src/Eve.Host/Rpc/ItemRpc.cs:64-78`); `CharacterServiceList` includes `ItemRpc` (`dotnet/src/Eve.Host/Rpc/CharacterServiceList.cs:54`). The work added a PostgreSQL-backed wire regression test, not another service or state store.
- `SovMgrPostgresHostWireTests.Sov_mgr_reads_and_skyhook_and_fuel_access_writes_round_trip_through_host_and_postgres` sends real Macho RPCs through Host, reads the persisted sovereignty/access-group state, changes/clears/restores the fuel ACL, destroys a Skyhook, acquires another, checks the persisted acquisition settlement and wallet debit, and retries acquisition without another debit. It also checks missing-state `None` and empty-list replies.
- EveJS 261028.1 `server/src/services/map/sovMgrService.js:78-136` uses the same request arguments and response intent. The eve.rs writer uses the existing GameStore and wallet transaction paths; no new timer, event list, state store, or notification was added.
- The existing 24-hour Skyhook management window is consistent with the [EVE University v23.01 patch notes](https://wiki.eveuniversity.org/UniWiki:Patch_Notes/Patch_Notes_-_Version_23.01); no mechanics or SDE-derived values changed in this task.
- Client-wide V24.01 review:
  - `eve/client/script/ui/services/sovSvc.py:71-119` caches sovereignty info and structures, selects the local-system RPC, injects the requested system/constellation IDs, and consumes `allianceID` from `SovClaimInfo` in `GetSovInfoForSolarsystem` (`:300-308`). `sovereignty/data_types.py:41-66` defines the `SovClaimInfo` properties.
  - Hub consumers read `claimTime` and owner state in `sovHub/hubController.py:247-249`, `sovSvc.py:403-405`, and `skyhooksCleanup/cleanupWndController.py:118-119`; anchoring preflight handles a missing hub at `structure/structureDeployment.py:111-112`, and celestial menu checks run at `menucheckers/celestialCheckers.py:1065,1079`.
  - Structure-row consumers include `infoPanelLocationInfo.py:347-348`, `shared/skyhook/uiController.py:124-133`, `shared/info/panels/panelSov.py:27-43`, `shared/info/panels/PanelSovConstellation.py:40-51`, and `shared/neocom/Alliances/sovHubs/sovHubEntryController.py:44,111` for campaign/vulnerability state.
  - Fuel ACL is loaded by `sovHub/hubController.py:44`; after `SetSovHubFuelAccessGroup`, the controller updates local ACL state at `:382-386`. `openFunctions.py:173` consumes `IsOnLocalSovHubFuelAccessGroup`.
  - `cleanupWndController.py:124-145` calls acquire/destroy in sequence and uses the returned processed-ID lists to mark rows; the caller does not wait for a sovereignty notification. No notification is required by these client callsites.
- Wire shape follows the client and EveJS: sovereignty/hub info are typed Ex1 objects; structures are iterable KeyVal lists; absent claim/hub/fuel-group is `None`; an absent system's structures are an empty list; skyhook methods return processed IDs; successful fuel ACL writes return `None`.

## Checks

- Built `dotnet/tests/Eve.Host.Tests` with `MSBUILDDISABLENODEREUSE=1` and `-p:UseSharedCompilation=false`: 0 warnings, 0 errors.
- On `eve_p4` through `tools/dev/db-lock.sh`, focused Host checks passed: 4 passed, 0 failed, 0 skipped. This included the new PostgreSQL Host wire test, the existing registered-route wire test, the persistence writer test, and the sovereignty read-model test.
- Pushed commit `ba381dd1f49caaa74fabdd06b3b6cf4e44e90133` to `main`. Push CI run 1362 and selected Agent test run 1209 both succeeded with no failed or skipped tests.
- No graphical V24.01 client session was used.

## Open rows

- None among the P1 `sovMgr` rows in `EVE_RS_COVERAGE.csv`.
