# Service audit handoff

## Current snapshot

- Client source: /home/user/eve-logic, commit 1dbb5711a072a146e1d228365668ac05655eefe1.
- Host source: this worktree after merging origin/main at fb5aa2b9ebeebdda8b26ff48113c37bae13fc665 and the P0 audit changes.
- P0 implementation/test push: 21f75f92. Latest main synchronization push: 22c62010.
- Focused Actions run 40219719160 passed for Search_quick_query_returns_client_id_list_through_host_with_transport_kwargs; no failed tests or jobs. Push CI run 40219660982 passed; no failed tests or jobs. No separate ci.yml run was created for the docs-only synchronization at 22c62010.
- Regenerate both CSVs and the P0/P1 work queue with:
  python3 tools/spec-extract/run_service_audit_current.py --queue-dir /tmp/service-audit-cx5-final
- Latest generated totals (2026-10-25): 4,173 client rows, 2,345 RPC rows, 1,408 active Host routes, and 61 queue files. Coverage: CLIENT-LOCAL 1, HOLLOW 10, IMPLEMENTED-TESTED 371, IMPLEMENTED-UNTESTED 1,552, MISSING 403, REJECTS-VALID-INPUT 8.
- The Host route scanner resolves HasMethod string constants, inspects partials under Host Composition, recognizes factory-created InvBrokerService behind its Host wrapper, and prioritizes connected Host packet tests over generic handler tests. It distinguishes dictionary .get and local instance .clear from RPCs.
- CLIENT_CALL_MATRIX.csv records RPC calls, service binds, bound-object dispatch, notification subscriptions/handlers, and session attributes. EVE_RS_COVERAGE.csv is the RPC-only join. IMPLEMENTED-TESTED marks a matching test source, not a test run.

## P0 service-audit review (2026-10-25)

### Whole-client evidence and Host behavior

- Current coverage rows for config.GetOwnersEx/GetPositionEx/GetPositionsEx, invbroker.GetItemDescriptor, and search.QuickQuery are IMPLEMENTED-TESTED by real Host test sources. Six extracted machoNet.get calls and station.clear were removed as local operations.
- Config owners and positions are Recordsets at eve/common/script/sys/eveCfg.py:396-397 and :549-550. carbon/common/script/sys/cfg.py:461+ loads tuple/header/rows; Recordset._Prime at :537-604 invokes batch fetch and indexes by the configured key. CBM found 899 eveowners references across 292 files. ConfigRecordsetHostWireTests covers single and batch owners (501 IDs), positions headers/coordinates, and machoVersion=1 through Host packets. eve.rs uses ConfigService.Recordsets.cs:9-40 and authoritative owner, station, and solar-system providers.
- invCache.GetItemHeader caches invbroker.GetItemDescriptor at eve/client/script/environment/invCache.py:427-431. Inventory CRowsets consume it at :1151 and :1167; godma.itemrd/sub-locations derive metadata at eve/client/script/environment/godma.py:124-149. EveJS 261018.1 returns a packed descriptor from server/src/services/inventory/invBrokerService.js:14031-14035, builder :10328-10333. eve.rs returns InventoryWire.DescriptorHeader at dotnet/src/Eve.Services.Inventory/InvBrokerService.cs:373-374; InventoryRpc wraps its factory-created broker at dotnet/src/Eve.Host/Rpc/InventoryRpc.cs:43-50. InvBrokerDescriptorHostTests sends a real Host packet and compares the decoded descriptor.
- QuickQuery is called by searchUtil.GetResultsList at eve/client/script/ui/util/searchUtil.py:296-300 with two positional arguments and hideNPC, onlyAltName, exact kwargs. Whole-client CBM traced 30 callers across edit/search, bounty/kill-rights, map, contracts, mail, owner search, petitions, shares, market, training, and organization selection. They use returned IDs for lookup, selection, filtering, and owner-row priming (searchOld.py:37-44); searchOld enables onlyAltName for corporation/alliance searches at :31-44. SearchQuickQueryHostWireTests sends repeated group IDs, all client kwargs, and machoVersion=1 through a connected Host; the reply is a flat PyList of IDs.
- The apparent machoNet.get calls were dictionary lookups after GetGlobalConfig in characterCreationContainer.py:2684-2685, characterCreationContainer_new.py:1536-1537, charcreation/ccUtil.py:76,83, and charcreation_new/ccUtil.py:77,84. GetGlobalConfig returns an empty dictionary before configuration arrives at carbon/common/script/net/machoNet.py:1454-1467; EveJS 261018.1 returns a dictionary at server/src/services/machoNet/machoNetService.js:340-343. The actual remote GetGlobalConfig is separate in coverage.
- station.clear is local guest-cache state: StationSvc initializes self.guests at eve/client/script/ui/station/base.py:56, clears it at :101, calls station.GetGuests at :102, builds a charID map at :103-108, then reads it in IsGuest at :110-113. EveJS 261018.1 returns guest tuples and seeds its observer ledger at server/src/services/station/stationService.js:135-156.

### EveJS comparison and intentional differences

- EveJS 261018.1 has GetMultiOwnersEx but no GetOwnersEx/GetPositionEx/GetPositionsEx in the inspected config service. eve.rs supplies client-declared recordsets from authoritative rowset builders.
- EveJS QuickQuery returns deduplicated flat IDs and handles exact/hideNPC, but ignores onlyAltName at server/src/services/_other/searchService.js:479-500. eve.rs honors alternate/ticker names requested by the client. Both differences are recorded in docs/reference/compat/DEVIATIONS.md:5833-5838.

### Local checks and limits

- Passed: python3 -m unittest tools.spec-extract.test_service_audit_local_calls.
- Passed after latest merge: target-only dotnet builds for Eve.Services.Space.Tests and Eve.Host.Tests, both zero warnings/errors. Their dependencies passed earlier full builds. The Host inventory test source was restored from the complete main-checkout version after an upstream update truncated it; the corrected source compiles.
- No PostgreSQL database was used. The focused QuickQuery .NET test passed on Actions, not locally. The graphical V24.01 client has not been exercised.

## Existing audit follow-up

- A (MISSING): latest generator reports 403 RPC rows without active Host routes.
- B (REJECTS-VALID-INPUT): 8 rows remain for Ping, AIR NPE, Config owner IDs, clone-grade reads, and no-fleet updates. EveJS confirms RegisterForDamageUpdates requires fleet membership. Owner logs report normal-burst SessionRpcBudgetExceeded while binding services; the method-level coverage table does not replay binding bursts.
- C (HOLLOW): 10 rows remain: eight Shipcaster inventory rows and two Ansiblex travel rows. Three structure cyno rows are IMPLEMENTED-TESTED in the generated CSV.

## Shipcaster and structure travel evidence

- shipCasterLandingPadMgr.GetFactionLandingPads(factionID) is called by eve/client/script/parklife/shipcasterSvc.py:22-23; GetAllLandingPads() at :25-26; shipCasterLauncherMgr.GetFactionsWithShipcaster() at :28-29. fwwarzone/client/dashboard/warzoneMapPanel.py:392-405 consumes faction rows and :420-433 consumes pad rows including faction, isLinked, and solarSystemID. shipCasterVisualization.py:57-98 and warzoneInfoSideCont.py:225-253 consume link/disruption data; row fields are at shipcaster/landingPadData.py:10-17,45-58.
- starterShipcasterTravelMgr.CanCharacterJump() is called by shipcasterSvc.py:31-32. The similarly named local component at shipcaster/client/components/shipcasterLauncher.py:238-246 takes factionID; menucheckers/celestialCheckers.py:1481-1501,2135-2136 calls that local method.
- Shipcaster jumps start at shipcasterSvc.py:17-20,34-36, through SessionMgr.PerformSessionChange at carbon/common/script/sys/sessions.py:418-500; menusvc.py:3069-3075 and movementFunctions.py:601-616 call the wrapper. Target fields come from shipcaster/shipcasterUtil.py:38-53.
- structureCynoBeaconMgr.CmdBridgeToStructureBeacon(structureID, solarsystemID) is called at eve/client/script/parklife/cynoTravelSvc.py:74-82. CmdGroupJumpToStructureBeacon has the same arguments at :115-124 and returns a jump tuple consumed by session transition; pilot jump is at :47-55.
- structureJumpBridgeMgr.CmdJumpThroughStructureStargate(structureID, solarsystemID) is called from autopilot.py:348-350 (false stops autopilot) and menusvc.py:2104-2108 (truthy removes waypoint). Destination data comes from infosvc.py:821-828 and is consumed through structureDirectory.py:175-176 and menuFunctions.py:360-364. Service/object transports add machoVersion=1 at ServiceCallGPCS.py:221-225 and ObjectCallGPCS.py:403-407.
- EveJS 261018.1 bridge handling activates a Titan portal, notifies fleet members, and returns true (structureCynoBeaconMgrService.js:687-760). Group travel delegates to _jumpToBeacon (:680-685,547-674), including session, fuel, timer, and notifications. Ansiblex checks link, ACL, proximity, disruption, range/mass, fuel, fee, and wallet settlement (structureJumpBridgeMgrService.js:1724-1896). Titan portal selection follows https://wiki.eveuniversity.org/Jump_drives.
- eve.rs implements structure-beacon bridge/group-jump through registered Host RPC and existing Space owners. Local checks for that earlier change passed the Space and Host test builds and Structure_cyno_travel_uses_the_host_route_with_transport_macho_version (1 passed, 0 skipped). Actions run 40211733150 passed at f73ea669; push CI run 40211717451 had no failed jobs or tests.
- Still open: Shipcaster production read providers and eligibility lack authoritative state sources. Ansiblex CmdJumpThroughStructureStargate lacks full Host travel wiring and settlement/state transition. AgentRpc.cs:110 registers Shipcaster services without state/travel/eligibility providers; ShipcasterRpcService.cs:22-44,57-90 supports callbacks but production leaves them absent. ShipcasterHostWireTests.cs:17-20 uses fixture pad state only. StructureRpc.cs:49-52 registers destination/link lookups without travel; PersistenceStructureDirectoryReadModel.cs:61-90 exposes persisted pair data, while crates/eve-space/src/scene/jump.rs:28-63 accepts only Stargate/Wormhole bodies for CmdStargateJump. A Shipcaster callback/test WIP remains in a Git stash and does not establish production state wiring.
- Earlier follow-up checks on 2026-10-25: Space, Space.Tests, and Host.Tests builds had zero warnings/errors; Shipcaster_landing_pad_rpc_uses_the_host_route_with_transport_macho_version passed 1/1, zero skipped. That wire test used a fixture callback and does not prove production state wiring.
