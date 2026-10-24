# Config recordset RPC batch 01179-01194

## Implementation

The real `config` Host service now registers single-item owner, corporation ticker, alliance short-name, location, and position lookups; `GetLocationsLocal` is also routable. Single calls delegate to the existing bulk lookups, preserving the recordset header/data tuple shape. Position rows use SDE-backed solar-system/station xyz and `None` for yaw/pitch/roll, which these catalogs do not store. Existing unknown IDs remain omitted. These are read-only lookups: no mutation, persistence, or notification is expected.

`RequestedIds` now accepts up to 10,000 IDs (was 500), still validates list/tuple shape and invalid identifiers. The real Host wire regression sends transport-added `machoVersion=1`, includes a 501-owner burst, and verifies rowset columns/data and static location/position/station replies.

## Client trace

- RPC-01179/01183: `eve/common/script/sys/eveCfg.py:378` initializes `allianceshortnames` with single and multi methods. Its cache supplies alliance short names, e.g. `eve/client/script/ui/shared/killRights.py:154`, `bountyWindow.py:773,817,856,894`, and `kill_mail.py:138`.
- RPC-01180/01184: `eve/common/script/sys/eveCfg.py:479` initializes `corptickernames`. Consumers include `eve/client/script/ui/shared/uilog.py:340`, `userentry.py:139`, `neocom/corporation/corp_ui_wars.py:192,238`, `ui/util/searchUtil.py:331`, and `ui/inflight/overview/overviewUtil.py:34,38`.
- RPC-01181/01182/01185: `eve/common/script/sys/eveCfg.py:665` initializes `evelocations` with single, multi, and client-local fetchers. `eveCfg.py:864-880` shows `GetLocationsLocal(keys)` is client-only; `carbon/common/script/sys/cfg.py:580-585` calls the local fetcher before remote multi-prime, while `:454-478` consumes header/data rows. Other local-location callers include `eve/client/script/ui/shared/info/infosvc.py:1299`, `ui/shared/maps/mapSvc.py:135`, `ui/shared/agencyNew/contentPieces/asteroidBeltSystemContentPiece.py:36`, `stargate/client/localGates.py:13`, and `nodegraph/client/getters/location.py:94`.
- RPC-01186/01187: `eve/common/script/sys/eveCfg.py:549` initializes `eveowners`. The cache is broadly consumed (401 client files); examples include `eve/client/script/ui/shared/info/infosvc.py:1175+` for owner/CEO display and `eve/client/script/ui/shared/mapView/mapViewColorHandler.py:202-203` for corp coloring. `carbon/common/script/sys/cfg.py:600-602,643-645` raises RecordNotFound when the requested row is absent.
- RPC-01188/01189: `eve/common/script/sys/eveCfg.py:396` initializes `positions`; whole-tree search found no later `cfg.positions` consumer in V24.01.
- RPC-01190/01191/01192: `eve/client/script/ui/shared/info/infosvc.py:1163` calls `GetStationSolarSystemsByOwner`; `:1278-1318` consumes `solarSystemID` to build route and station info. The duplicate map rows resolve to the same caller: `ui/shared/mapView/mapViewColorHandler.py:199-203` reads `solarSystemID` for coloring.
- RPC-01193/01194: the duplicate settled-corp rows resolve to `eve/client/script/ui/shared/mapView/filters/mapFilterSettledByCorp.py:16-17`, which keys by each row's `solarSystemID`.

The client transport injects `machoVersion` in `carbon/common/script/net/ServiceCallGPCS.py:221-225` and `ObjectCallGPCS.py:403-407`; the Host test exercises the service call with this kwarg.

## EveJS comparison

EveJS v0.12.9 implements bulk owners at `server/src/services/config/configService.js:378-478`, bulk locations at `:486-761`, and station systems by owner at `:843-855`. No matching single-item handlers or positions handlers were found. eve.rs reuses its existing bulk store-backed implementations for the new singles. EveJS may synthesize rows for unknown owner IDs; eve.rs omits them, matching the client recordset behavior where a missing key raises RecordNotFound. Locations and station output use the corresponding client-consumed row/key shapes.

## Verification

Implementation commit and origin/main merge were pushed to `main` as `1e3675a8`; the handoff update is at `6a3288c6`. The dispatched `agent-test.yml` run `40097845169` for `6a3288c6` succeeded (440 checks, no failed tests or jobs). `ci-status.py failures 6a3288c6` found no push-triggered `ci.yml` run.

- `dotnet build dotnet/src/Eve.Services.CharacterInfo/Eve.Services.CharacterInfo.csproj -p:UseSharedCompilation=false`: passed.
- `dotnet build dotnet/tests/Eve.Host.Tests/Eve.Host.Tests.csproj -p:UseSharedCompilation=false --no-restore`: passed, 0 warnings/errors.
- `dotnet build dotnet/tests/Eve.Services.CharacterInfo.Tests/Eve.Services.CharacterInfo.Tests.csproj -p:UseSharedCompilation=false`: passed, 0 warnings/errors.
- Focused real-Host wire test `Config_recordset_routes_return_client_rows_through_host_with_transport_kwargs`: passed (1 passed, 0 failed, 0 skipped).
- No PostgreSQL database was used. No graphical-client run was performed.
