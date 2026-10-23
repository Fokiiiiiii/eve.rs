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

- Symptom: the client log says `UnavailableInventoryLocation: Failed to retrieve items; character is not located in a station` during `NeocomInventoryBadging.OnSessionChanged`. The client callsite is `GetItemsInCurrentStationShipHangar`; the capture shows a station-bound `GetInventory(containerHangar=10004)` request receiving `SessionRpcBudgetExceeded` during the transition to space.
- Root cause: `PacketDispatcher` charges station inventory reads against the same per-session expensive-RPC budget as unrelated setup and market calls. After that shared bucket is consumed, the client's guarded station hangar read is refused and translated to `UnavailableInventoryLocation`.
- Fix commit: pending.
- Regression test: pending.

## (e) goals/campaign GetUpdateRequest timeout

- Symptom: the client log says `ServiceUnavailableError: ERROR: TimeoutException, Failed to receive response from GetUpdateRequest`; the call originates in the map-tag flatbuffer repository update task.
- Root cause: open; this capture identifies the map-tags public gateway request, separate from the goals/campaign requests reported by the owner.
- Fix commit: pending.
- Regression test: pending.

## (f) TypeListNotFound / None errors

- Symptom: the client log reports `TypeListNotFoundException: 'NoneType' object is not subscriptable`; its stack resolves `GetTypeList(140)` while fitting the ship HUD charge icon.
- Root cause: open; determine the client type-list source and the missing list’s actual contents.
- Fix commit: pending.
- Regression test: pending.

## (g) chat objects are None after an XMPP failure

- Symptom: owner reports chat objects become `None` after XMPP failure. The capture shows the preceding `XmppChatMgr.Hostname()` `ServiceNotFound` response.
- Root cause: open; trace the XMPP failure transition and the chat manager’s resulting state.
- Fix commit: pending.
- Regression test: pending.

## (h) missing href and Japanese UnicodeEncodeError

- Symptom: the client logs `KeyError: 'href'` in `aurumStoreView.SetupStoreData` while processing store offers. It separately logs a CP1252 `UnicodeEncodeError` while formatting a Japanese localized module name in `_DetectAndFixMissingHudModules`.
- Root cause: open; inspect the store response for the missing field. The Unicode exception is raised by client traceback encoding; identify whether a server payload contributes before deciding a server-side change.
- Fix commit: pending.
- Regression test: pending.

## (i) eve-net handshake timeout / eve-sim exits before hello

- Symptom: `eve-net: ... the handshake did not finish in 30s`; `eve-sim: the host connection ended: the host closed before it said hello`.
- Root cause: open; reproduce startup ordering and a delayed simulation handshake.
- Fix commit: pending.
- Regression test: pending; it must prove Host survival during a slow sim start.

## (j) EveJS session comparison

- Symptom: `tools/mitm diff` reports the first call-order divergence at event 4: EveJS calls `invbroker.GetItemDescriptor`, while eve.rs calls `machoNet.GetTime`.
- Root cause / decision: open; compare corresponding request and response flows before classifying this ordering difference.
- Fix commit: pending.
- Regression test: pending.
