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
- Regression: `DispatchGoldenTests.Inventory_calls_keep_a_bounded_budget_separate_from_other_expensive_services` (1/1) and the full `DispatchGoldenTests` class on the merged tree (157/157), zero skips.
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
