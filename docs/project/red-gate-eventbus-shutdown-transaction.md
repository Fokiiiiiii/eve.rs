# Event bus transaction left open during Host shutdown

## Findings

Run 1701 (`75f852dc`, Host tests shard 6/6) and run 1704 (`cb69ebb6`, Host tests shard 1/6) failed `RpcFuzzTests.Registered_rpcs_refuse_seeded_hostile_arguments_through_dispatcher` during scratch-schema teardown. Both failures found a `RpcFuzz` PostgreSQL session idle in a transaction on the Events schema. Run 1704 reported the active statement as the subscriber cursor `UPDATE`. Run 1702 and run 1703 passed the fuzz shard, so the failure is intermittent. The auction buyout amount failure was separately fixed upstream in `b245d717` by correcting the fixture and wallet-journal assertions.

The fuzz test checks for active sessions after `HostRuntime.ShutdownAsync` and fails before dropping scratch schemas (`dotnet/tests/Eve.Host.Tests/RpcFuzzTests.cs:173-205`). Host shutdown retries pending bound-object releases with a ten-second timeout before disposing foundations (`dotnet/src/Eve.Host/Hosting/HostRuntime.cs:79-96`). Foundations then stop the EventBus (`dotnet/src/Eve.Host/Foundations.cs:70-75`), which cancels and awaits its pump (`dotnet/src/Eve.Events/EventBus.cs:141-165`).

When a subscriber has no matching event through the current outbox horizon, the pump advances its cursor in a transaction (`dotnet/src/Eve.Events/EventBus.cs:408-416`). The CI query and shutdown order are consistent with cancellation being observed after that `UPDATE` reached PostgreSQL but before its commit completed, leaving the connection in an open transaction. This is the diagnosed cause; the cancellation boundary was not instrumented directly in the CI run.

The cursor-advance update still observes the stop token. If cancellation interrupts that statement, the branch explicitly rolls the transaction back with a non-cancelable token before rethrowing. Once the update succeeds, its commit uses `CancellationToken.None`, closing the window where shutdown could cancel between the statement and `COMMIT`. The change is limited to this cursor-advance branch.

## Local verification

- Release builds for `Eve.Events`, `Eve.Events.Tests`, and `Eve.Host.Tests` completed with zero warnings and zero errors.
- The complete `Eve.Events.Tests` project passed on `eve_p3` against `616d3a9b7`: 30 passed, zero failed, zero skipped.
- The complete Host `RpcFuzzTests` class passed on `eve_p3` against `616d3a9b7`: 3 passed, zero failed, zero skipped.
- Main contains `616d3a9b7` (`Finish event cursor transactions during shutdown`). CI run 1705 completed green. Its Host shard 1/6 reported 213 passed and 3 skipped; shard 6/6 reported 212 passed and 3 skipped. The skipped cases in those shards are Tournament tests, not the RPC fuzz test. At this point run 1704 was still the preceding red run, so another green main run was needed to make the newest two runs green.

The local database test exercises Host shutdown and scratch-schema cleanup, but it did not deterministically pause between the cursor update and transaction commit. The observed regression has not been verified with the graphical EVE client; it is internal EventBus shutdown behavior and does not change the client wire protocol.
