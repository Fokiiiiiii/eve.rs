# SimClient outbound queue handoff (CX3)

## Status

The bounded queue implementation and post-merge local checks are complete. Queue change commit: `fd3524fc`; latest merge commit: `82e5db18` (includes `origin/main` `68e2db0c`). The queue change was pushed to `main` in commit `4fe2df93`. Main CI run 1518 was green before that push. Push-triggered CI run 1519 failed in `Eve.Host.Tests.RpcFuzzTests.Registered_rpcs_refuse_seeded_hostile_arguments_through_dispatcher` during scratch-schema cleanup; its PostgreSQL command timeout was raised from 120 to 300 seconds in CI fix commit `21780109`. Selected Agent test run 1329 and push-triggered CI run 1520 both passed.

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
- CI-fix Actions verification: selected Agent test run 1329 passed: https://github.com/Fokiiiiiii/eve.rs/actions/runs/41930004669. Push-triggered CI run 1520 passed: https://github.com/Fokiiiiiii/eve.rs/actions/runs/41929981873. `ci-status.py failures 21780109` reports no failed tests or jobs.
- CI-fix evidence: `ci-status.py failures 4fe2df93` reports an Npgsql stream timeout at `RpcFuzzTests.cs:195`, the `DROP SCHEMA ... CASCADE` command with a 120-second timeout. The failing test passed locally before the change (1 passed, 0 skipped) and the full `RpcFuzzTests` filter passed after the timeout increase (2 passed, 0 skipped) on `eve_p3` via `db-lock.sh`. `Eve.Host.Tests` builds with 0 warnings and errors.
- Post-merge recheck after fetching and merging `origin/main`: `RpcFuzzTests` filter passed on `eve_p3` via `db-lock.sh` (2 passed, 0 skipped).

## Main CI gate

The latest completed `CI` workflow on `main` is run 1520, successful on `2178010953f5`: https://github.com/Fokiiiiiii/eve.rs/actions/runs/41929981873. The focused Agent test 1329 also passed on `21780109`: https://github.com/Fokiiiiiii/eve.rs/actions/runs/41930004669. The prior run 1519 failure was addressed by the cleanup timeout adjustment in `21780109`.

Earlier, main CI run 1515 failed on `HostServerTests.Registered_lp_store_and_wallet_routes_return_the_client_consumed_shapes` (`LpStoreRpcHostWireTests.cs:154,165`). That failure was fixed on main at `96d6bb8f2b15`; CI run 1516 completed successfully: https://github.com/Fokiiiiiii/eve.rs/actions/runs/41898752417.
