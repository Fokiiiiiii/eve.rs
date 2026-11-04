# SimClient outbound queue handoff (CX3)

## Status

The bounded queue implementation and post-merge local checks are complete. Queue change commit: `fd3524fc`; latest merge commit: `82e5db18` (includes `origin/main` `68e2db0c`). The queue change was pushed to `main` in commit `4fe2df93`. Main CI run 1518 was green before that push. Push-triggered CI run 1519 failed in `Eve.Host.Tests.RpcFuzzTests.Registered_rpcs_refuse_seeded_hostile_arguments_through_dispatcher` during scratch-schema cleanup; its PostgreSQL command timeout is now raised from 120 to 300 seconds. Selected Agent test run 1328 passed. The CI fix must be pushed alone after the red-gate checks.

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
- `OnInvulnCancelled(shipID)` has five existing consumers that clear the current ship's invulnerability state or timer: `voidspace/client/void_space_activation_controller.py:189`, `pvpFilaments/client/activation_window/pvpFilamentController.py:250`, `eve/client/script/ui/shared/abyss/baseActivationController.py:136`, `eve/client/script/ui/crimewatch/crimewatchTimers.py:493`, and `randomJump/client/randomJumpController.py:180`. The notification shape is unchanged.
- Client-side station inventory reads `session.locationid` and selects the station manager from `session.stationid` (`eve/client/script/environment/invCache.py:198,207,211`); the existing `stationid`/`locationid` transition values are unchanged.

## Local verification

- `MSBUILDDISABLENODEREUSE=1 dotnet build dotnet/src/Eve.Space/Eve.Space.csproj -p:UseSharedCompilation=false`: passed.
- `MSBUILDDISABLENODEREUSE=1 dotnet build dotnet/tests/Eve.Space.Tests/Eve.Space.Tests.csproj -p:UseSharedCompilation=false`: passed.
- After merging `origin/main` `68e2db0c`, `SimClientTests`: 28 passed, 0 skipped (includes `A_full_outbound_queue_fails_pending_requests_and_breaks_the_link`).
- After the merge, `SpaceServiceTests.When_the_simulation_is_lost_the_ship_docks_at_the_station_the_character_left` on `eve_p3`: 1 passed, 0 skipped.
- After the merge, selected Host tests in `AbyssalHostFlowTests` and `TournamentMatchPersistenceTests` on `eve_p3`: 21 passed, 21 skipped, 0 failed. The skipped cases require `EVE_ABYSSAL_SIM_BINARY` and `EVE_ABYSSAL_SIM_SDE`; they are reported separately and are not counted as passes.
- Change-specific Actions verification: selected Agent test run 1328 passed: https://github.com/Fokiiiiiii/eve.rs/actions/runs/41923162574. Push-triggered CI run 1519 failed on scratch-schema teardown in the unrelated Host RPC fuzz test: https://github.com/Fokiiiiiii/eve.rs/actions/runs/41923134723.
- CI-fix evidence: `ci-status.py failures 4fe2df93` reports an Npgsql stream timeout at `RpcFuzzTests.cs:195`, the `DROP SCHEMA ... CASCADE` command with a 120-second timeout. The failing test passed locally before the change (1 passed, 0 skipped) and the full `RpcFuzzTests` filter passed after the timeout increase (2 passed, 0 skipped) on `eve_p3` via `db-lock.sh`. `Eve.Host.Tests` builds with 0 warnings and errors.

## Main CI gate

The RPC fuzz cleanup timeout fix reached `main` in `2178010953f5`. CI run 1520 completed successfully: https://github.com/Fokiiiiiii/eve.rs/actions/runs/41929981873. Selected Agent test run 1329 also passed: https://github.com/Fokiiiiiii/eve.rs/actions/runs/41930004669. Recheck the gate and fetch `origin/main` before the next push.

Earlier, main CI run 1515 failed on `HostServerTests.Registered_lp_store_and_wallet_routes_return_the_client_consumed_shapes` (`LpStoreRpcHostWireTests.cs:154,165`). That failure was fixed on main at `96d6bb8f2b15`; CI run 1516 completed successfully: https://github.com/Fokiiiiiii/eve.rs/actions/runs/41898752417.

## Cancellation and retry follow-up (CX10)

- Generic requests and charge adjustments register exact-entry cancellation cleanup and enqueue through the bounded outbound queue while holding `_pendingGate`. A pre-cancelled token removes its pending completion before enqueue; when enqueue wins the gate, a later cancellation affects the waiter while preserving the accepted frame. Late cleanup cannot remove a retry that reused the same key.
- Abyssal requests keep the cancelled-task return behavior before request validation, then check connection loss under `_pendingGate` and use the same generic enqueue path.
- The touched `SimClient` is the internal Host-to-simulation link (`dotnet/src/Eve.Space/SimClient.cs:5-8`). Whole-tree searches of `/home/user/eve-logic` found no `SimClient`, `SimAdjustCharge`, or `SimHello` symbols, so no EVE client RPC, reply, notification, or session-field shape changed.
- After merging `origin/main` at `2178010953f5`, `Eve.Space`, `Eve.Space.Tests`, `Eve.Host.Tests`, and `Eve.Services.Space.Tests` builds passed with 0 warnings and 0 errors. On `eve_p10`, `SimClientTests` passed 33/33; the 21 selected real-simulation Host DB tests passed 21/21; `When_the_simulation_is_lost_the_ship_docks_at_the_station_the_character_left` passed 1/1. All reported 0 skipped. The `eve-sim` Rust binary build also succeeded to run the real-simulation cases.
- The cancellation changes are in local commit `9bf04318`; the tested merge resolution is `be78f4aa`. This handoff was updated before pushing the combined history to `main`.
