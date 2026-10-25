# RPC-02875–RPC-02896 Host coverage handoff

Updated 2026-10-25.

## Client callers, consumed shapes, and EveJS comparison

- RPC-02875/02876: the only GetLinkedStructure caller is eve/client/script/ui/shared/info/infosvc.py:821-824. It treats a falsey result as no destination, otherwise unpacks (destinationStructureID, destinationSolarsystemID). EveJS v0.12.9 server/src/services/structure/structureJumpBridgeMgrService.js:1780-1790 returns that two-element pair for a reciprocal link and null when none exists.
- RPC-02877/02878: eve/client/script/ui/services/menuSvcExtras/marketMenu.py:17-20 calls CharacterCheckService(itemLocationID, SERVICE_MARKET) and opens the sell window after success; the value is ignored. EveJS structureSettingsService.js:70-80 returns true for access and throws the service access error otherwise.
- RPC-02879/02880: marketsvc.py:237-238 forwards CharacterGetService(structureID, SERVICE_MARKET) as market tax. structureServices.py:28-35,41-42,54-56 consumes CharacterGetServices as a service-ID map for the current structure or remote structure. Other direct consumers are infosvc.py:840-850 (iterates .items() for structure info) and parklife/autopilot.py:624-626 (membership check for jump bridges). EveJS structureSettingsService.js:51-68 returns null when the service is unavailable, true for services without an access setting, and otherwise the configured profile value; lines 32-42 return the service-ID keyed map.
- RPC-02881–02884 share those CharacterGetServices consumers. These are separate call sites/catalog rows, not distinct server methods.
- RPC-02885/02886: structureSettings.py:15-18 backs a two-second memoized CharacterHasService call. Callers found across eve-logic: eveCommands.py:2519-2522 (reprocessing), shared/neocom/contracts/contracts.py:899-902 (docking), shared/market/quote.py:294-295 (market), and structure/entries/structureEntryMyCorp.py:355-356 (moon mining). EveJS structureSettingsService.js:44-49 answers with character access to the service.
- RPC-02887–02890: structureSettings.py:19-22 backs memoized CharacterHasSetting. Callers found across eve-logic: moonmining/moonminingExtraHudBtns.py:77-80 and services/structure/structureControl.py:94-95 check control access; station/undockQuestions.py:54-56 checks housing dock access. EveJS structureSettingsService.js:82-87 returns the profile setting/access result.
- RPC-02891–02893 are not remote RPCs: structureSettings.py:23-26 clears the two local memoized Python methods when the session resets. marketsvc.py:138 clears a local memoized market read. EveJS has no corresponding structureSettings server handler for clear_memoized; registering one would not satisfy a client call.
- RPC-02894 is not a remote RPC either. structureServices.py:28-35 stores the returned CharacterGetServices dictionary as onlineServices, and line 45 calls onlineServices.keys() locally to find industry service availability. EveJS returns a normal dictionary at structureSettingsService.js:32-42; keys is a dictionary operation, not a service method.
- RPC-02895/02896: the caller structureBrowser/reinforcementTimeWnd.py:170-173 sends selected structure info plus weekday and hour; the successful return is ignored and the window closes. EveJS structureVulnerabilityService.js:183-261 validates all structures and management authority before mutation, persists pending reinforcement changes, schedules its notification, and returns null.

## eve.rs coverage and regression

The named runtime behavior already exists at this checkout: StructureRpc.cs:50-52 registers structureJumpBridgeMgr with the persisted directory callbacks; StructureTravelRpcService.cs:36-68 implements the pair-or-None reply. StructureRpc.cs:53 registers structureVulnerability, whose real callback is wired through StructureComposition.cs:480-481 to transactional StructureDeploymentAuthority.UpdateReinforceTimesAsync (StructureDeploymentAuthority.cs:30-65). StructureSettingsService is registered by StructureRpc.cs and its read model checks persisted deployed structure/profile/access-group state. Existing host read-model coverage is HostServerTests.Structure_settings_host_read_model_uses_persisted_owner_profile_and_access_group_rows.

Added StructureHostWireCoverageTests.Structure_settings_bridge_and_vulnerability_calls_keep_the_client_wire_contract. It routes the actual service classes through the Host registry and dispatcher; covers the linked pair and no-link shapes, all five settings reads, a 500-ID reinforcement burst with transport machoVersion, and rejection of 501 IDs before the backend callback. The reinforcement callback capture tests the wire adapter; the production composition and persistence callback remain the production state owner.

## Verification

- The local Eve.Host.Tests compile-check succeeded with 0 warnings and 0 errors.
- The focused Host wire test passed (1 passed, 0 skipped); no PostgreSQL database was used.
- GitHub Actions verification is pending the push.
