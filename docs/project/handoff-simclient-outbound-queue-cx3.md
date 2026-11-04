# SimClient outbound queue handoff (CX3)

## Status

Implementation and local checks are complete after merging green main. Queue change commit: `fd3524fc`; merge commit: `a429d85a`. The change has not been pushed. Recheck the newest completed main CI run before pushing and record the commit-specific Actions results here afterward.

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
- Selected Host tests referencing `SimClient` (`AbyssalHostFlowTests` and `TournamentMatchPersistenceTests`) on `eve_p3`: 21 passed, 21 skipped. The skipped cases use `RealAbyssalSimPgFact` and need `EVE_ABYSSAL_SIM_BINARY` plus `EVE_ABYSSAL_SIM_SDE`; these skips are reported separately and do not count as passes.
- Change-specific Actions verification: pending push.

## Main CI gate at handoff

Main CI run 1515 was red on `HostServerTests.Registered_lp_store_and_wallet_routes_return_the_client_consumed_shapes` (`LpStoreRpcHostWireTests.cs:154,165`). That failure was fixed on main at `96d6bb8f2b15`; CI run 1516 completed successfully: https://github.com/Fokiiiiiii/eve.rs/actions/runs/41898752417. Green main was merged at `a429d85a`, then the focused, SimClient, and database-backed local checks listed above were rerun.
