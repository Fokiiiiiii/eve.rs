# Real graphical client diagnostics

The owner’s captures and process logs were read through `tools/mitm show` and `tools/mitm diff`. The extracts below omit local usernames and machine paths. The graphical run is evidence; focused headless and Host tests are recorded separately.

## (a) XmppChatMgr ServiceNotFound

- Symptom: `XmppChatMgr.Hostname()` received `ErrorResponse` (`ServiceNotFound`, call 63 in the graphical capture). EveJS answered `Hostname()` with `"localhost"`.
- Root cause: `HostComposition` registered the implemented RPC service only inside the optional XMPP listener block. With the listener disabled, MachoNet could not resolve the service.
- Fix: register `XmppChatMgr` independently from listener startup; the listener remains controlled by `EVE_XMPP_ENABLED`.
- Fix commit: `Register XmppChatMgr independently from listener`.
- Regression: `Xmpp_chat_manager_is_registered_when_the_listener_is_disabled` (Host composition, 1 passed).
- Client contract: V24.01 calls `XmppChatMgr.Hostname`; EveJS registers this service independently of its transport startup.

## (b) eventLog.LogClientStats service missing

- Symptom: `ServiceNotFound: eventLog`; the graphical capture calls `eventLog.LogClientStats` and receives an error. EveJS logs the event and returns `None`.
- Root cause: `eventLog` was not registered in the Host service registry, so MachoNet returned ServiceNotFound before the stats call could be recorded.
- Fix: add a client-facing `eventLog.LogClientStats` RPC that records a privacy-limited summary in the Host log and answers `None`, matching EveJS’s return shape.
- Fix commit: `Register eventLog stats RPC in Host`.
- Regression: `EventLogHostTests.The_host_registers_event_log_and_records_client_stats_before_returning_none` covers Host wiring, the four-argument call shape, the `None` response, and the recorded summary (1 passed).
- Sources: V24.01 `eve/client/script/sys/clientStatsSvc.py`; EveJS `server/src/services/logging/eventLogService.js`.

## (c) structureDirectory.GetStructureMapData errors

- Symptom: the capture calls `GetStructureMapData([30100032])` and `GetStructureMapData([30100041])`; each receives an unhandled `RuntimeError`. The Host log records PostgreSQL `42P01` for `sde.invTypes`.
- Root cause: the read model queried `sde.invTypes`, but the pinned SDE stores type rows in `sde.types`. The same invalid table name was used by the corporation structure listing.
- Fix: both reads now use the pinned `types` table and retain the SDE group ID in the map row.
- Fix commit: `Serve structure map data from the pinned SDE table`.
- Regression: `StructureMapDataTests.Structure_map_data_uses_the_pinned_type_table_and_returns_deployed_rows` passed on the merged tree (1/1); `StructureDirectoryServiceTests` passed (25/25); the Host directory persistence subset passed (4/4), all with zero skips.
- Reference: EveJS builds its map rows from the structures in the requested solar system; V24.01 calls this RPC from `structureDirectory.py:GetStructureMapData`.

## (d) UnavailableInventoryLocation in space

- Symptom: the client log says `UnavailableInventoryLocation: Failed to retrieve items; character is not located in a station` during `NeocomInventoryBadging.OnSessionChanged`. The request binds the prior station's hangar with `containerHangar=10004`. EveJS returns the bound inventory; eve.rs returned `SessionRpcBudgetExceeded` for the same request during the transition to space.
- Root cause: `PacketDispatcher` charged all expensive RPCs against one per-session bucket. Before the first failed hangar bind, the graphical capture had only four `invbroker` calls; unrelated setup RPCs consumed the shared bucket.
- Fix: expensive `invbroker` calls now use their own bounded per-session bucket. Inventory reads and writes still share that limit.
- Fix commit: `Give inventory RPCs a dedicated per-session budget`.
- Related station-hangar trace: `GetInventory(10004, None)` received the same shared-budget refusal in a capture written at 05:29 UTC, before commit `e1ed09a4` (06:35 UTC) separated the `invbroker` bucket. It does not establish a post-fix failure.
- Regression: `DispatchGoldenTests.Inventory_calls_keep_a_bounded_budget_separate_from_other_expensive_services` (local 1/1, 0 skipped) and the full `DispatchGoldenTests` class on the merged tree (157/157), zero skips.
- Reference: EveJS returns the requested bound substruct for the matching station-hangar call; V24.01 client callsite is `neocomInventoryBadging.py:GetItemsInCurrentStationShipHangar`.

## (e) goals/campaign GetUpdateRequest timeout

- Symptom: client errors include `Failed to receive response from GetUpdateRequest`, `GetAllWithRewardsRequest returning with status code 408`, and `Campaigns: GetAllRequest: ERROR: TimeoutException`; the career-goal definitions request also times out. The first call is the map-tag flatbuffer repository's public-gateway request.
- Root cause: the graphical run's Host log contains only the MachoNet listener startup line and no public-gateway startup line. In this build `EVE_PUBLIC_GATEWAY` is unset by default, so `Program` does not start the local gateway. V24.01's `publicGateway.grpc.connectionConfig` uses `EVE_PUBLIC_GATEWAY_ADDRESS` when provided, otherwise it discovers CCP's `*-public-gateway.evetech.net` endpoint. EveJS has handlers for the map-tag, campaign, career-goal and daily-goal requests; the graphical run did not route those requests to an eve.rs listener. This is a run-configuration gap, not an unhandled MachoNet RPC. The local Host still lacks those gateway handlers; if the owner intends to route these APIs locally, their provider data and behavior must be implemented before changing the client endpoint.
- Owner action: launch Host with `EVE_PUBLIC_GATEWAY=<reachable-host>:<port>` and launch the client with `EVE_PUBLIC_GATEWAY_ADDRESS=<same-host>:<port>` for a local run, then capture the gateway requests. No server-side success payload is substituted for unavailable goal or campaign state.
- Fix commit: none; the evidence points to the external launch configuration, and implementing empty/stub goal or campaign responses would fabricate state.
- Regression test: none; no code fix was made. The current run has no gateway startup entry, and V24.01's public-gateway connection discovery contract was checked in the client's `publicGateway.grpc.connectionConfig` module.

## (f) TypeListNotFound / None errors

- Symptom: the client log reports `TypeListNotFoundException: 'NoneType' object is not subscriptable`; its stack resolves `GetTypeList(140)` while fitting the ship HUD charge icon.
- Root cause: the exception is raised in the client's `evetypes.GetTypeList`, which indexes the local `TypeListLoader.GetData()` loaded from `res:/staticdata/typeList.fsdbinary`. The receiver-side MachoNet capture has no type-list request or response, and eve.rs has no provider for this client-local static resource. The `NoneType` text indicates the local loader returned `None` before indexing list 140; it does not demonstrate a missing eve.rs RPC payload.
- Owner action: verify the installed V24.01 client's local `typeList.fsdbinary` resource loads. The server cannot repair a missing client-local static-data loader.
- Fix commit: none; no server defect is established by this evidence.
- Regression test: none; the failing operation is client-local and no server change was made.

## (g) chat objects are None after an XMPP failure

- Symptom: after the old build's `XmppChatMgr.Hostname()` `ServiceNotFound`, the client logs `AttributeError: 'NoneType' object has no attribute 'LeaveRoom'` in its XMPP channel cleanup. Its room name uses the configured domain (`conference.None`), so the failed manager lookup leaves no usable XMPP room/connection state.
- Root cause: the old build's Host service was absent (fixed by (a)). The current Host also starts its XMPP listener only when `EVE_XMPP_ENABLED` is true; the diagnostic Host log has no XMPP listener startup line. With the default disabled listener, the current service can return the configured domain and fallback host, but no XMPP transport accepts the client's connection. The server-side chat state is then unavailable because no authenticated XMPP connection was established.
- Owner action: for a graphical run that uses XMPP, enable `EVE_XMPP_ENABLED` and configure a client-reachable `EVE_XMPP_BIND_ADDRESS`, `EVE_XMPP_CONNECT_HOST`, `EVE_XMPP_DOMAIN`, and port. Capture again against the current Host to verify the client no longer derives `conference.None` and can authenticate its XMPP connection.
- Fix commit: the registration repair is `6f3d37df`; no additional code change is justified by the old-build capture.
- Regression: `Xmpp_chat_manager_is_registered_when_the_listener_is_disabled` proves the Host RPC remains available independently of transport startup (1 passed). `XmppHostConfigurationTests.XMPP_listener_and_client_endpoint_are_configured_from_environment` covers the listener and advertised endpoint settings. No current graphical-client retry has been run.

## (h) missing href and Japanese UnicodeEncodeError

- Symptom: the client logs `KeyError: 'href'` in `aurumStoreView.SetupStoreData` while processing store offers. It separately logs a CP1252 `UnicodeEncodeError` while formatting a Japanese localized module name in `_DetectAndFixMissingHudModules`.
- Root cause: the `href` exception is in the client `storeManager.get_offers(4)` path. The server's `NewEdenStoreCatalog` came from the approved EveJS seed, which has valid offer hrefs, but `gen-new-eden-store-catalog.py` explicitly removed each store 4 offer's href before generating the response. The separate Unicode exception is raised by the client's CP1252 traceback encoder while it logs Japanese localized names from the HUD repair diagnostic; it is not a server request failure and the names are valid localized client data.
- Fix: preserve the seed's store 4 offer hrefs in the generated catalog and test the Host service response for the Omega offer. The CP1252 exception is raised while formatting a client traceback, outside the RPC path; the Japanese names are not altered server-side.
- Fix commit: `Preserve New Eden Store offer links from the seed`.
- Regression test: `NewEdenStoreCatalogTests.The_store_4_offers_are_the_15_seeded_offers_with_real_type_ids_for_every_product` and `NewEdenStoreCatalogTests.The_store_lists_the_catalog_offers_and_answers_can_purchase_from_it` (6 focused tests passed); related catalog and store service tests passed 18/18; `NewEdenStoreHostTests` passed 7/7 through the `eve_p3` DB lock. Zero skips.

## (i) eve-net handshake timeout / eve-sim exits before hello

- Symptom: `eve-net: ... the handshake did not finish in 30s`; `eve-sim: the host connection ended: the host closed before it said hello`.
- Root cause: the diagnostic sim log shows an accepted connection ending before the first Hello, followed by another accepted connection. `eve-sim` logs a per-connection error and returns to its accept loop; this line does not mean the simulation process exited. Host composition starts `SimClient` in the background; it retries failed connects and does not await the simulator before serving the game. The Host log later records a connected simulation and a pilot entering space. The separate `eve-net` timeout means a client socket did not finish the Macho login handshake within its 30-second bound. The logs provide no shared session or connection identifier to establish whether that socket is the graphical pilot or an abandoned/retried connection.
- Decision: delayed simulation startup is already handled by the Host's retrying background link. A regression now proves that the Host simulation client reconnects when its listener starts late. The `eve-net` timeout remains owner-needed: correlate its connection with the graphical client's launch and retry timeline before changing the login handshake timeout or protocol.
- Fix commit: none; no protocol defect is established. Regression coverage is in `The_host_simulation_client_retries_until_a_slow_listener_starts`.
- Regression test: the delayed-listener case passed (1/1), and the full `SimClientTests` class passed (19/19), zero skips. This verifies the simulation client retry path; no current graphical-client retest was run.

## (j) EveJS session comparison

- Symptom: `tools/mitm diff --ignore-volatile` reports the first client-call-order divergence at event 4: EveJS calls `invbroker.GetItemDescriptor`, while eve.rs calls another `machoNet.GetTime`.
- Decision: both captures later contain `invbroker.GetItemDescriptor`, and both responses carry the same `blue.DBRowDescriptor` field layout. This first divergence is a difference in concurrent startup-call scheduling, not a missing service response. The captures then stop being the same scenario: EveJS creates a character before selecting it, while eve.rs selects an existing character. The EveJS login-campaign call also returns configured campaign data while eve.rs returns `None`; eve.rs only serves a campaign when `EVE_SEASONAL_LOGIN_CAMPAIGN` is configured. No current-build graphical capture with matched character state and campaign configuration is available, so later event-order or state differences are not classified as parity defects from these two sessions.
- Fix commit: none for the first divergence. The separately observed ServiceNotFound and inventory-budget failures are covered by (a) and (d).
- Regression test: none for concurrent call ordering. To close remaining parity questions, the owner needs a paired capture with the same starting character state and campaign configuration; the headless tests do not count as a graphical client comparison.

## (k) Industry job-count budget refusals

- Symptom: The 2026-10-23 client retry capture contains two `industryManager.GetJobCounts` responses with `SessionRpcBudgetExceeded`.
- Trace: V24.01 `industrySvc._UpdateSlots_thread` calls `GetJobCounts(session.charid)` to refresh industry slots. EveJS v0.12.9 checks that the requested installer is the session character, then returns counts from active jobs. eve.rs applies a 10-unit cost before dispatch and reads the authoritative job snapshot in `IndustryJobsService`; the error therefore means the dispatcher rejected the call before the Host service ran.
- Root cause and fix: industry calls shared the 160-unit general expensive-RPC bucket, so unrelated services could exhaust it. `industryManager` now has a separate per-session 160-unit rolling-minute bucket; the industry cap still returns `SessionRpcBudgetExceeded` before dispatch when depleted.
- Regression: `DispatchGoldenTests.Industry_jobs_use_a_separate_per_session_budget` verifies that unrelated expensive calls cannot block the industry handler and that the industry bucket still enforces its own limit.
- Check status: the focused `Eve.Services.Tests` Actions run `40010004814` completed successfully. No current graphical-client retest has been made.

## (l) 2026-10-23 in-space, undock and session notification audit

- Inventory and industry budget errors in the retry capture are already fixed on `origin/main` by `e1ed09a4` and `a8e6c9f5`; their existing evidence is recorded in (d) and (k). The capture's `GetJobCounts` and inventory refusals predate those fixes.
- The undock and session-change path is covered on `origin/main` by `7b011e25`, `e24b0e26` and `c068f252`. Current space tests assert the undock notification order (`OnItemsChanged`, disconnect notifications, `$sessionchange`, then `OnInvulunOnUndockingUpdated`), ship/session state updates, and clearing `stationid2`. In this retry capture, the pilot starts in space, so its initial session has no `stationid2`; the later session notifications do not independently exercise clearing that field.
- The capture has two failed `CmdStargateJump` attempts shortly after `CmdWarpToStuff`, followed by a successful retry and the normal arrival notifications/session change. EveJS rejects a stargate jump while warp landing is pending; the captured failures are consistent with that transition, but the server-side refusal reason is absent from this trace. eve.rs maps an unrecognized jump refusal to the same generic user error as EveJS. The existing jump tests cover refusal and successful state/notification order, so no server fix is supported by this evidence.
- Focused Actions verification on `origin/main` commit `6c822c03`, run `40048976357`, succeeded for the inventory budget, undock order/state, and jump refusal/success tests. The workflow summary did not include per-test skip counts. No current-build graphical-client retest was made; this audit found no open server-side defect in these categories.

## (m) Service reply inventory from graphical captures

- Scope: matched Macho CallReq and reply records in eve-rs-graphical.jsonl and eve-rs-10.0.402-client-retry.jsonl. The eve-rs-10.0.402-graphical.jsonl file has no service calls. The two captures contain 72 ErrorResponse replies and 123 successful empty replies.
- Empty results below are capture observations, not failures by themselves. None is common for void calls, and empty lists or dictionaries can mean there is no matching state. As noted in (j), the EveJS and eve.rs sessions start from different character states; no wrong-shaped response is established by comparing these unpaired sessions.

### Errors grouped by service

| Service | Captured error replies |
|---|---|
| bound object | CmdStargateJump: CustomInfo ×5; GetInfoWindowDataForChar: UnMachoDestination ×1; GetMyApplications: UnMachoDestination ×14 and SessionRpcBudgetExceeded ×1; GetSkillChangesForISIS: UnMachoDestination ×1; GetSkillQueueAndFreePoints: UnMachoDestination ×1 |
| corpStationMgr | MachoBindObject: SessionRpcBudgetExceeded ×1 |
| dogmaIM | MachoBindObject: SessionRpcBudgetExceeded ×1 |
| eventLog | LogClientStats: ServiceNotFound ×10 |
| fleetMgr | RegisterForDamageUpdates: FleetError ×7 |
| industryManager | GetJobCounts: SessionRpcBudgetExceeded ×2 |
| invbroker | MachoBindObject: SessionRpcBudgetExceeded ×7 |
| marketProxy | GetOrders: SessionRpcBudgetExceeded ×1; GetPlexHistory: SessionRpcBudgetExceeded ×2; GetRegionBest: SessionRpcBudgetExceeded ×3 |
| station | GetGuests: SessionRpcBudgetExceeded ×2 |
| structureDirectory | GetStructureMapData: RuntimeError ×11 |
| XmppChatMgr | Hostname: ServiceNotFound ×2 |

### Empty successful replies grouped by service

- achievementTrackerMgr: UpdateClientAchievmentsAndCounters → None ×8.
- agentMgr: GetMyEpicArcStatus → {} ×2.
- bound object: CheckAndSendNotifications → [] ×2; CmdDock → None ×2; CmdFollowBall → None ×1; CmdWarpToStuff → None ×4; GetBoosters → {} ×2; GetImplants → {} ×2; GetMyApplications → {} ×1; GetSkillHistory → [] ×1; GetTargeters → [] ×5; GetTargets → [] ×5; GetWars → {} ×2; ListIncludingContainers → [] ×1; SetModuleOnline → None ×1.
- bountyProxy: GetMyKillRights → [] ×2.
- charFittingMgr: GetFittings → {} ×2.
- charUnboundMgr: GetCharacterLockType → None ×2; SelectCharacterID → None ×2.
- config: GetAverageMarketPrices → {} ×2; GetBlackListedPlanets → [] ×2; GetOldStationData → [] ×2.
- dungeonInstanceCacheMgr: GetFactionWarfareInstances, GetHomefrontSiteInstances, GetIceBeltInstances, GetOreAnomalyInstances, and GetPirateInsurgencyInstances → {} ×3 each.
- essMgr: GetDataForClientSolarSystem → None ×4.
- expertSystemMgr: GetMyExpertSystems → {} ×1.
- facWarMgr: GetMyCharacterRankInfo → None ×1.
- keeper: GetCurrentDungeonForCharacter → None ×2.
- loginCampaignManager: get_client_campaign_state → None ×2.
- marketProxy: GetRegionBest → {} ×7.
- milestoneMgr: ProcessCharacterLogon → None ×2.
- notificationMgr: GetByGroupID → [] ×2.
- officeManager: GetMyCorporationsOffices → [] ×2.
- operationsManager: get_active_category_id → None ×2.
- scanMgr: SignalTrackerRegister → None ×13.
- seasonManager: get_season_data_for_character → None ×2.
- shipCosmeticsMgr: GetEnabledCosmetics → {} ×5.
- stationSvc: GetAllianceSystems → [] ×1.
- structureAssetSafety: GetItemsInSafetyForCharacter → [] ×4.
- structureDirectory: GetMyDockableStructures → [] ×4.
- userSvc: GetMultiCharactersTrainingSlots → {} ×1.

### Remaining repeated budget failures

- Existing fixes already cover eventLog, structureDirectory, inventory, industry, and market errors in (b), (c), (d), and (k). fleetMgr.RegisterForDamageUpdates returned FleetError because the character was not in a fleet, matching EveJS membership rejection. CmdStargateJump returned action-dependent CustomInfo. The old GetMyApplications UnMachoDestination replies precede the current route; the retry capture returned an empty dictionary.
- The remaining repeated failures are station.GetGuests (2), dogmaIM.MachoBindObject (1), and corpStationMgr.MachoBindObject (1). PacketDispatcher charged these calls to the same general per-session budget as unrelated expensive calls and rejected them before the registered service handler ran. The station alias resolves to stationSvc; GetGuests costs 10 units. The dogma bind call is charged for its nested GetAllInfo method. corpStationMgr.MachoBindObject itself costs 10 units; the captured nested call was DoStandingCheckForStationService.
- Fix: each of stationSvc, dogmaIM, and corpStationMgr now has its own per-session rolling-minute bucket, each capped by MaxExpensiveRpcUnitsPerMinute (160 by default). The existing Host services still produce the response after the dispatcher allows the call, and each isolated bucket continues to reject calls after its cap is reached.
- Regression: DispatchGoldenTests.Station_and_bound_service_rpcs_have_independent_bounded_budgets sends real encoded Macho CallReq packets through PacketDispatcher. It checks that exhausting the general bucket does not block each service, and that each new bucket still returns SessionRpcBudgetExceeded at its own cap.
- Local checks: Eve.Services and Eve.Services.Tests builds succeeded with zero warnings or errors; the focused dispatcher wire test passed (1/1, zero skips). GitHub Actions run 40051187174 on 3361cdda855c passed (1/1, zero skips). A post-fix graphical-client retest is still pending.

## (n) Traceback sweep of the 2026-10-23 captures

- Re-read the client exception payloads embedded in `alert.SendClientStackTraceAlert` across the supplied captures and checked the current `origin/main` history for each server-side reply issue. All identified missing or malformed server replies already have fixes recorded in (a)–(m); this sweep found no additional open server-side reply defect to implement.
- The remaining repeated budget failures in the captures were fixed by c964917d. The focused packet-dispatch wire regression passed on GitHub Actions run 40051187174: 1 test passed, zero skipped. This verifies the isolated stationSvc, dogmaIM, and corpStationMgr budget behavior and its rejection limits.
- Other visible exceptions do not identify a local MachoNet reply defect: the `TypeListNotFoundException` is raised while loading the client-local type-list resource; the `UnicodeEncodeError` is in client logging; the UI attribute errors have no preceding failed server call in these captures; and the public-gateway calls were not routed to the local Host because gateway discovery was not configured. The `fleetMgr.RegisterForDamageUpdates` FleetError matches the no-membership rejection in EveJS; current character selection clears stale fleet session fields.
- A post-fix graphical-client capture is not available, so this documents the packet-level regression and trace review, not graphical-client verification.
