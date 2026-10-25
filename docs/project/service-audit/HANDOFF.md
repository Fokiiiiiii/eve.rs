# Service audit handoff

## Current snapshot

- Client source: /home/user/eve-logic, commit 1dbb5711a072a146e1d228365668ac05655eefe1.
- Host source: this worktree after merging origin/main (6fbb4951615ae85fd9b8152700e650de98305590) and the P0 audit changes.
- Regenerate both CSVs and the P0/P1 work queue with:
  python3 tools/spec-extract/run_service_audit_current.py --queue-dir /tmp/service-audit-cx5-final
- Generated totals (2026-10-25): 4,173 client rows, 2,345 RPC rows, 1,395 active Host routes, and 62 queue files. Coverage: HOLLOW 10, IMPLEMENTED-TESTED 345, IMPLEMENTED-UNTESTED 1,565, MISSING 417, and REJECTS-VALID-INPUT 8.
- The Host route scanner resolves string constants in HasMethod comparisons, detects the factory-created InvBrokerService behind its Host wrapper, and prioritizes connected Host packet tests over generic handler tests. It distinguishes dict .get and local instance .clear from RPCs.
- CLIENT_CALL_MATRIX.csv records RPC calls, service binds, bound-object method dispatch, notification subscriptions/handlers, and session attributes. EVE_RS_COVERAGE.csv is the RPC-only join. Coverage statuses reflect current Host registrations and test sources; IMPLEMENTED-TESTED records a test source, not that the test ran in this audit.

## P0 service-audit review (2026-10-25)

### Verified from the whole V24.01 client and Host source

- Current EVE_RS_COVERAGE.csv rows for config.GetOwnersEx/GetPositionEx/GetPositionsEx, invbroker.GetItemDescriptor, and search.QuickQuery are IMPLEMENTED-TESTED by real Host test sources. The six extracted machoNet.get calls and station.clear row are absent after correcting the extraction.
- Config owners and positions are Recordsets at eve/common/script/sys/eveCfg.py:396-397 and :549-550. carbon/common/script/sys/cfg.py:461+ loads the tuple/header/data response; Recordset._Prime at :537-604 calls the batch fetcher and indexes returned rows by the configured key. CBM found 899 eveowners references across 292 files. ConfigRecordsetHostWireTests covers single and batch owners (501 IDs), position row headers and coordinates, and machoVersion=1 through Host packets. eve.rs uses ConfigService.Recordsets.cs:9-40 and existing authoritative owner, station and solar-system providers.
- invCache.GetItemHeader caches invbroker.GetItemDescriptor at eve/client/script/environment/invCache.py:427-431. Inventory CRowsets consume it at :1151 and :1167; godma.itemrd/sub-locations derive column and virtual-field metadata from it at eve/client/script/environment/godma.py:124-149. EveJS 261018.1 returns a packed descriptor from server/src/services/inventory/invBrokerService.js:14031-14035, with its builder at :10328-10333. eve.rs returns InventoryWire.DescriptorHeader at dotnet/src/Eve.Services.Inventory/InvBrokerService.cs:373-374; InventoryRpc wraps the factory-created broker at dotnet/src/Eve.Host/Rpc/InventoryRpc.cs:43-50. InvBrokerDescriptorHostTests sends a real Host packet and compares the decoded descriptor.
- QuickQuery's wire call is searchUtil.GetResultsList at eve/client/script/ui/util/searchUtil.py:296-300: two positional arguments plus hideNPC, onlyAltName, and exact. The whole-client inbound trace counted 30 callers across edit/search UI, bounty and kill rights, map, contracts, mail, owner search, petitions, shares, market, training, and organization selection. Callers consume returned IDs for lookup, selection, filtering, and owner-row priming (searchOld.py:37-44). searchOld enables onlyAltName for corporation/alliance searches at :31-44. SearchQuickQueryHostWireTests sends repeated group IDs, every client kwarg, and machoVersion=1 through a connected Host; the reply is a flat PyList of IDs.
- The apparent machoNet.get calls were Python dictionary lookup chained from GetGlobalConfig in characterCreationContainer.py:2684-2685, characterCreationContainer_new.py:1536-1537, charcreation/ccUtil.py:76,83, and charcreation_new/ccUtil.py:77,84. GetGlobalConfig returns an empty dictionary before configuration arrives at carbon/common/script/net/machoNet.py:1454-1467; EveJS 261018.1 also returns a dictionary at server/src/services/machoNet/machoNetService.js:340-343. The actual remote method GetGlobalConfig is separately represented in coverage.
- station.clear is local guest-cache state: StationSvc initializes self.guests at eve/client/script/ui/station/base.py:56, clears it at :101, calls station.GetGuests at :102, builds a charID-keyed map at :103-108, then reads it in IsGuest at :110-113. EveJS 261018.1 returns guest tuples and seeds its observer ledger at server/src/services/station/stationService.js:135-156. The actual station.GetGuests route is separate.

### EveJS comparison and recorded deviations

- EveJS 261018.1 has GetMultiOwnersEx but no GetOwnersEx/GetPositionEx/GetPositionsEx in the inspected config service. eve.rs fills the client-declared recordsets with authoritative rowset builders.
- EveJS QuickQuery returns deduplicated flat numeric IDs and handles exact/hideNPC, but ignores onlyAltName at server/src/services/_other/searchService.js:479-500. eve.rs selects alternate/ticker names when the client requests them. Both differences are recorded in docs/reference/compat/DEVIATIONS.md:5833-5838.

### Local checks and remaining checks

- Passed: python3 -m unittest tools.spec-extract.test_service_audit_local_calls.
- Passed: MSBUILDDISABLENODEREUSE=1 dotnet build dotnet/tests/Eve.Host.Tests/Eve.Host.Tests.csproj -p:UseSharedCompilation=false, zero warnings and errors.
- No PostgreSQL database was used. The focused QuickQuery .NET test was compiled but not executed locally. Actions verification for this code and the graphical V24.01 client remain outstanding.

## Existing shipcaster and structure travel follow-up

Whole-client evidence and V24.01 consumers for the seven unique service methods (duplicate inventory IDs share these calls):

- shipCasterLandingPadMgr.GetFactionLandingPads(factionID) is called by eve/client/script/parklife/shipcasterSvc.py:22-23; GetAllLandingPads() at :25-26; shipCasterLauncherMgr.GetFactionsWithShipcaster() at :28-29. fwwarzone/client/dashboard/warzoneMapPanel.py:392-405 consumes factions and :420-433 consumes pad rows, reading faction, isLinked, and solarSystemID. shipCasterVisualization.py:57-98 and warzoneInfoSideCont.py:225-253 consume link/disruption fields. The row model is in shipcaster/landingPadData.py:10-17,45-58.
- starterShipcasterTravelMgr.CanCharacterJump() is called by shipcasterSvc.py:31-32. No other remote caller was found. The similarly named local component method in shipcaster/client/components/shipcasterLauncher.py:238-246 takes factionID; menucheckers/celestialCheckers.py:1481-1501,2135-2136 calls that local method.
- Shipcaster jumps start at shipcasterSvc.py:17-20,34-36 and run through SessionMgr.PerformSessionChange at carbon/common/script/sys/sessions.py:418-500; menusvc.py:3069-3075 and movementFunctions.py:601-616 call the wrapper. Target fields come from shipcaster/shipcasterUtil.py:38-53.
- structureCynoBeaconMgr.CmdBridgeToStructureBeacon(structureID, solarsystemID) is called at eve/client/script/parklife/cynoTravelSvc.py:74-82. CmdGroupJumpToStructureBeacon has the same arguments at :115-124 and returns a jump tuple consumed by the session transition; the separate pilot jump call is at :47-55.
- structureJumpBridgeMgr.CmdJumpThroughStructureStargate(structureID, solarsystemID) is called from autopilot.py:348-350 (false stops autopilot) and menusvc.py:2104-2108 (truthy removes the waypoint). Destination data is built at infosvc.py:821-828 and consumed through structureDirectory.py:175-176 and menuFunctions.py:360-364. Standard transports add machoVersion=1 at ServiceCallGPCS.py:221-225 and ObjectCallGPCS.py:403-407.
- EveJS 261018.1 bridge handling activates a standard Titan portal, notifies fleet members, and returns true (structureCynoBeaconMgrService.js:687-760). Group travel delegates to _jumpToBeacon (:680-685,547-674), carrying session, fuel, timer, and notification state. Ansiblex travel applies link, ACL, proximity, disruption, range/mass, fuel, fee, and wallet settlement checks (structureJumpBridgeMgrService.js:1724-1896). The standard Titan portal selection follows https://wiki.eveuniversity.org/Jump_drives.
- This worktree implements structure-beacon bridge and group-jump dispatch through registered Host RPC and existing Space session/bridge/conduit owners. Focused service tests and a Host socket wire test with machoVersion=1 cover the route and reply. Local verification for that earlier change: Space and Host test projects built; Structure_cyno_travel_uses_the_host_route_with_transport_macho_version passed 1/1, zero skipped. That test preceded the audit-tool change.
- Still open: shipcaster read providers and production eligibility lack a verified authoritative source in current eve.rs/EveJS runtime. Ansiblex CmdJumpThroughStructureStargate still needs full Host wiring, authoritative settlement/state transition, and the client-consumed result. Regenerate the CSV and update this handoff when those rows change.
