# fwWarzoneSolarsystem implementation handoff — CX5

## Coverage

Closed RPC-01550–01561 (all P1 rows; twelve inventory rows for five distinct methods) in `EVE_RS_COVERAGE.csv`. The methods were already present in the active Host composition; this batch added the missing database-backed socket regression and brought the coverage ledger up to date.

- Production registration: `dotnet/src/Eve.Host/Rpc/ItemRpc.cs:64-78` composes `SovFwService` and registers `FwWarzoneSolarsystemHostService`.
- Production route: `dotnet/src/Eve.Host/Rpc/FwWarzoneSolarsystemHostService.cs:13-78`; occupation reads flow through `FactionWarfareSnapshotAsync`, which reads persisted `FwStore.OccupiedSystemsAsync` state (`dotnet/src/Eve.Host/Composition/FactionWarfareComposition.cs:24-35`). HQ systems use pinned SDE `chrFactions.solarSystemID` and adjacency uses pinned SDE `mapStargates` (`dotnet/src/Eve.Host/Rpc/FwWarzoneAdjacencyComposition.cs:10-70`).
- Exact replies: `GetAllWarzones` is int-keyed dict of faction-ID lists; both occupation methods are nested int-keyed dicts of `util.KeyVal` records with `occupierID`, `solarSystemID`, `warzoneID`, and `adjacencyState`; HQ is faction-to-system dict; local state is a two-item tuple of system ID and KeyVal or None.
- EveJS 261028.1 implements the same methods and shapes in `server/src/services/map/fwWarzoneSolarsystemService.js:49-88`. eve.rs derives adjacency from the pinned SDE; the existing compatibility decision is recorded in `docs/reference/compat/DEVIATIONS.md` (Faction-war adjacency entry).

## Whole-client trace (V24.01 eve-logic)

- Calls: `eve/client/script/ui/services/fwWarzoneSvc.py:51-54,72,75,208,212`. All six call sites send no arguments; the duplicate inventory rows are alternate RPC IDs for these same sites.
- Warzone map: `fwWarzoneSvc.py:186-192` searches faction membership in the returned dict; callers are `eve/client/script/ui/shared/theatersOfWar/landingPage/landingPagePanel.py:135-136` and `eve/client/script/ui/shared/factionalWarfare/enlistmentFlow/warzoneGaugeCont.py:49-52`.
- HQ: `fwWarzoneSvc.py:194-197` consumes faction-to-system mapping; `eve/client/script/ui/shared/agencyNew/ui/contentPagesUI/factionWarfare/panels/warZones.py:55-56` uses the system ID.
- All occupation map consumers found: `eve/client/script/ui/shared/maps/starModeHandler.py:246-249`, `starMapSvc.py:1611-1614`, `mapViewColorHandler.py:429-432`, `mapViewData.py:103-106`, `mapView/filters/mapFilterMilitia.py:64-67`, and `eve/client/script/ui/services/facWarSvc.py:106-120`. They iterate the nested dict and read each record's `occupierID`.
- Local and per-system consumers found: `infoPanelFactionalWarfare.py:66,94-96,204-209,258-264`, `infoPanelLocationInfo.py:340-346`, `eve/client/script/ui/station/undockQuestions.py:63-68,165-168`, `eve/client/script/ui/shared/info/panels/panelFactionalWarfare.py:65-68,92-94`, and the Agency FW provider/piece at `contentProviderFactionalWarfareSystems.py:24-68` and `factionalWarfareSystemContentPiece.py:107-110`. These handle None for non-warzone systems and use occupier/adjacency values for panels, restrictions, and filters.
- Notification consumer: `fwWarzoneSvc.py:77-91` accepts `OnWarzoneOccupationStateUpdated`, updates the current-system cache and scatters `OnWarzoneOccupationStateUpdated_Local`; UI listeners are `infoPanelFactionalWarfare.py:41,272-275` and `infoPanelLocationInfo.py:492-494`. The five RPCs here are read-only and do not emit notifications.

## Tests and remaining work

- `dotnet build dotnet/tests/Eve.Host.Tests -p:UseSharedCompilation=false`: passed, 0 warnings/errors.
- With database `eve_p5`, `db-lock.sh dotnet test dotnet/tests/Eve.Host.Tests --no-build --filter 'FullyQualifiedName~Faction_warfare_warzone_calls_use_real_host_wire_shapes_and_sde_adjacency_values|FullyQualifiedName~Registered_host_warzone_read_returns_occupation_persisted_in_postgres'`: passed, 2/2, no skips. The second test calls the registered socket route and reads seeded occupation from PostgreSQL.
- With `eve_p5`, focused `Eve.FactionWarfare.Tests` capture and startup-seeding tests passed, 2/2, no skips.
- The Host wire check covers all five RPCs, exact container/object shapes, FL/SL/BL adjacency, the session-derived local tuple, and the non-warzone None case. The DB socket test verifies that persisted FW occupancy reaches the reply after a fresh store-backed read.
- Separate follow-up: a whole-tree .NET search found no producer of `OnWarzoneOccupationStateUpdated`; `FwService.CaptureAsync` documents that its hub-destruction trigger is not connected (`dotnet/src/Eve.FactionWarfare/FwService.cs:167-169`). This transition/notification path is not one of the read-only RPC rows and remains open for the faction-warfare gameplay integration.
- No graphical-client session was run. Before this documentation/test-only push, the newest completed CI-on-main run was green (run 41295493287, SHA e18791c7).
