# SimClient outbound queue handoff (CX3)

## Status

Implementation and local checks are complete. The change is in the cx3 worktree and has not been pushed. Recheck the main CI gate before pushing and record the commit-specific Actions results here afterward.

## Change

- `SimClient` now uses a single-reader bounded channel with a capacity of 64 queued frames. Each outbound payload is checked against `SimCodec.MaxFrame` (1 MiB), so queued buffers are finite; the writer may hold one additional frame while the stream write is in flight.
- Enqueue remains FIFO: producers register and enqueue under `_pendingGate`, and the existing single writer emits frames in channel order. Protocol version and frame encoding are unchanged.
- Saturation returns `SimUnavailableException("outbound queue is full ...")` to the overflowing sender, marks the link unavailable, faults already-pending response requests, cancels the active session, logs the overload, and lets the existing RunAsync loss path raise `Lost`. Subsequent senders also receive that failure until reconnect succeeds.
- All outbound paths, including `AdjustChargeAsync`, use the same bounded enqueue helper.

## Host and client flow checked

- This follows ADR 0007, Decision 1-3: one Host per simulation, with Host-owned client state and ordering. Saturation uses the existing Host loss transition rather than adding another state owner.
- `SpaceSessions` subscribes to `SimClient.Lost` in `dotnet/src/Eve.Services.Space/SpaceSessions.cs:203`. Its existing `OnLost` path at lines 1249-1322 reconciles simulation-owned state and brings pilots back to their previous station.
- `FinishDockingAsync` at `dotnet/src/Eve.Services.Space/SpaceSessions.Movement.cs:596-658` performs the existing ship move, sends `OnInvulnCancelled` and `OnItemsChanged`, updates session location attributes, then sends `OnDockingFinished`.
- Whole-tree searches in `/home/user/eve-logic` found no `SimHello`, `SimHelloAck`, `SimClient`, or `simlink` client symbols. No client RPC, reply, notification, or session field shape changed.
- Whole-tree searches in EveJS 261028.1 likewise found no simulation-process client or `SimHello` symbols; its `ClientSession._enqueueOutbound` promise chain (`src/server/src/network/clientSession.js:462-475`) is not the separate Host-to-simulation link this change handles.
- The existing dock transition's client consumers include `dockingHeroNotificationService.py:85,114` and `tacticalNavigationService.py:491` for `OnDockingFinished`; `invCache.py:218`, `shipSkinSequencingSvc.py:43,100`, and `missionObjectivesTracker.py:103` consume `OnItemsChanged`. Their observed inputs and order are unchanged.
- The unchanged `OnInvulnCancelled(shipID)` notification has client consumers at `void_space_activation_controller.py:189`, `pvpFilamentController.py:250`, `baseActivationController.py:136`, `crimewatchTimers.py:493`, and `randomJumpController.py:180`.
- Client session-change handling uses `session.stationid` when updating station inventory state (`eve/client/script/environment/invCache.py:142`); the existing `stationid`/`locationid` transition values are unchanged.
- The unchanged `OnInvulnCancelled(shipID)` notification has client consumers at `void_space_activation_controller.py:189`, `pvpFilamentController.py:250`, `baseActivationController.py:136`, `crimewatchTimers.py:493`, and `randomJumpController.py:180`.

## Local verification

- `dotnet build dotnet/src/Eve.Space/Eve.Space.csproj -p:UseSharedCompilation=false`: passed.
- `dotnet build dotnet/tests/Eve.Space.Tests/Eve.Space.Tests.csproj -p:UseSharedCompilation=false`: passed.
- Focused test `A_full_outbound_queue_fails_pending_requests_and_breaks_the_link`: 1 passed, 0 skipped.
- `SimClientTests`: 28 passed, 0 skipped.
- PostgreSQL-backed `SpaceServiceTests.When_the_simulation_is_lost_the_ship_docks_at_the_station_the_character_left` on `eve_p3`: 1 passed, 0 skipped.
- Change-specific Actions verification: pending push.

## Main CI gate at handoff

The newest completed main CI run observed was 1514 and green. Run 1515 was still in progress; its Host tests shard 3/6 had failed `HostServerTests.Registered_lp_store_and_wallet_routes_return_the_client_consumed_shapes` in `LpStoreRpcHostWireTests.cs:154,165`. Confirm its final status before pushing; do not push this unrelated change if that run completes red.
