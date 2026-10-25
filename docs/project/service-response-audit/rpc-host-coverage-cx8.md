# Host RPC coverage handoff: RPC-01800–02100 batch

## Scope and result

The listed rows resolve to three implemented contracts that lacked Host-wire regression coverage: lookup search, machoNet responses, and moon extraction mutations. No new runtime route was needed. In particular, the 15 `machoNet.get` rows are Python dictionary reads on the result of the existing `GetGlobalConfig` service call, not RPC methods named `get`.

Added `dotnet/tests/Eve.Host.Tests/Rpc01800To02100HostWireTests.cs`. Each test opens the real Host test TCP front, performs the session handshake, sends a marshalled RPC call with transport `machoVersion=1`, and checks the decoded reply. Lookup test registers `CoreServices.CreateLookupService`; MachoNet test uses the Host's standard service registrations; moon test registers `MoonExtractionsRpcService` and verifies fracture/cancel calls plus the following extraction read.

## Client contract evidence

- RPC-01800, RPC-01801 `lookupSvc.LookupOwners`: `eve/client/script/ui/util/searchOld.py:260-264` reads `ownerName`, `groupID`, `ownerID`, and `typeID`; `xmppchatclient/pcownerpickerdialog.py:60-61` treats `None`/empty results as no match. Whole-tree search found no other direct `LookupOwners` caller.
- RPC-01804, RPC-01805 `lookupSvc.LookupPCOwners`: `searchOld.py:258-264` uses the same owner keyvals and selects this method when hiding NPCs.
- RPC-01806 `LookupWarableCorporationsOrAlliances`: `eve/client/script/ui/services/wars/war_cso.py:336-346` consumes returned `ownerID` values to build a warable-owner map and filters ordinary corporations against it.
- RPC-01811 `GetClusterGameStatisticsForClient`: `eve/client/script/ui/shared/maps/mapSvc.py:428-441` unpacks solar-system counts, station counts, and divisor; missing station counts default to zero; it subtracts station counts and divides/rounds both values.
- RPC-01830, 01831, 01832, 01833, 01835 are `GetGlobalConfig().get` call sites in `eve/client/script/ui/eveCommands.py:3596-3622`: Academy landing URL, CSM candidate URL, CSM voting URL (two fallbacks), and Uprising patch notes URL.
- RPC-01836 uses `defaultPortraitSaveSize` at `eve/client/script/ui/services/evePhotosvc.py:430-432`; RPC-01852 reads the configured image-server key at `evePhotosvc.py:714-716`.
- RPC-01837 reads `disableAutopilotLogging` (default 0) at `eve/client/script/parklife/autopilot.py:74-75`.
- RPC-01838 reads `disableCorpRecruitmentLogging` (default 0) at `eve/client/script/ui/services/corporation/base_corporation.py:1048-1050`.
- RPC-01841 reads `disableLongCallWarning` at `carbon/client/script/remote/connectionService.py:296-298`.
- RPC-01846 reads `enableDustLink` at `eve/client/script/ui/shared/info/infosvc.py:2225-2227`.
- RPC-01847 reads `flightControlStatisticsInterval` (default 300) at `eve/client/script/ui/services/flightControls.py:376-377`; RPC-01848 reads `flightControlsThrottle` (default 400) at `flightControls.py:47-48`.
- RPC-01849 reads `hideReportBot` (default 0) at `eve/client/script/ui/shared/info/characterInfoWindow.py:205-206`.
- RPC-01855 reads the active order cap (default 100) at `eve/client/script/ui/shared/market/buySellMultiBase.py:251-253`.
- The full-tree search also found consumers at `serverInfo.py:108`, `carbon/common/lib/serverInfo.py:108`, `globalConfig/getFunctions.py:8-10`, `raffles/common/util.py:44`, and `monolithconfig/__init__.py:142`; plus client character creation/faction/school consumers at `eve/client/script/ui/login/characterCreationContainer.py:2684`, `characterCreationContainer_new.py:1536`, `charcreation/ccUtil.py:76,83`, and `charcreation_new/ccUtil.py:77,84`. These call the same mapping response; no new reply fields were introduced.
- RPC-02097, RPC-02098 `CancelExtraction`: `eve/client/script/ui/moonmining/schedulingSvc.py:70-74` asks for confirmation and ignores a success value; `schedulingSvc.py:111-113` clears the local fired timestamp when `OnMoonExtractionUpdate` arrives.
- RPC-02099, RPC-02100 `FractureChunkWithStructure`: `schedulingSvc.py:58-66` emits local fire feedback, invokes the RPC, and emits failure/reset behavior if the call raises. The return value is unused; the later notification clears the timestamp at lines 111-113.

## EveJS comparison and eve.rs wiring

- EveJS lookup handlers are in `server/src/services/corporation/lookupSvcService.js:500-531,578-626`; they return owner keyvals with the client-consumed field names and filter NPC/war eligibility.
- EveJS `GetGlobalConfig` returns its config map at `server/src/services/machoNet/machoNetService.js:340-343`; initialization values are assembled at lines 326-333. `GetClusterGameStatisticsForClient` returns the expected tuple-like `[{}, {}, 1]` for EVE and preserves the caller fallback for another cluster at lines 500-509.
- EveJS moon handlers are at `server/src/services/structure/moonExtractionsService.js:1247-1261`; shared mutation logic at lines 1057-1112 persists extraction state and sends the corresponding owner/update notifications. Client behavior above ignores mutation return values and reacts to RPC errors/updates.
- eve.rs already registers `GetGlobalConfig` and `GetClusterGameStatisticsForClient` in `dotnet/src/Eve.Host/MachoNetService.cs:84-85`. Moon RPC registration is in `dotnet/src/Eve.Host/Rpc/StructureRpc.cs`; production authority/store/scheduler and moon update delivery are composed in `dotnet/src/Eve.Host/Composition/StructureComposition.cs`. The wire test uses the real Host registry and real RPC service, but its mutation callback is an in-memory state provider; it does not exercise database durability or the production notification outbox. Existing structure persistence tests cover those production components separately.

## Verification and pushes

- Local Host test project build passed both before the initial push and after the wire-key assertion fix. Commands used `MSBUILDDISABLENODEREUSE=1`, `-p:UseSharedCompilation=false`, and `-p:BuildProjectReferences=false`.
- Local focused lookup wire test passed (1 passed, 0 skipped).
- First Actions run `40205736699` for `648e2c02`: 2 passed, 1 failed, 0 skipped. The MachoNet config assertion compared decoded `PyBytes` keys with `PyStr`; the assertion now converts wire keys with `JsSemantics.StringValue`. Fix commit `79e37068` was pushed after fetch/merge.
- Focused Actions rerun and push-triggered CI for the corrected commit are pending.
