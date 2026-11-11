# Event bus transaction left open during Host shutdown

## Findings

Run 1701 (`75f852dc`, Host tests shard 6/6) and run 1704 (`cb69ebb6`, Host tests shard 1/6) failed `RpcFuzzTests.Registered_rpcs_refuse_seeded_hostile_arguments_through_dispatcher` during scratch-schema teardown. Both failures found a `RpcFuzz` PostgreSQL session idle in a transaction on the Events schema. Run 1704 reported the active statement as the subscriber cursor `UPDATE`. Run 1702 and run 1703 passed the fuzz shard, so the failure is intermittent. Run 1700 (`e4718ab1`), before the auction tax change, was green; that pass did not rule out a rare EventBus race. The auction buyout amount failure was separately fixed upstream in `b245d717` by correcting the fixture and wallet-journal assertions.

The fuzz test checks for active sessions after `HostRuntime.ShutdownAsync` and fails before dropping scratch schemas (`dotnet/tests/Eve.Host.Tests/RpcFuzzTests.cs:173-205`). Host shutdown retries pending bound-object releases with a ten-second timeout before disposing foundations (`dotnet/src/Eve.Host/Hosting/HostRuntime.cs:79-96`). Foundations then stop the EventBus (`dotnet/src/Eve.Host/Foundations.cs:70-75`), which cancels and awaits its pump (`dotnet/src/Eve.Events/EventBus.cs:141-165`).

When a subscriber has no matching event through the current outbox horizon, the pump advances its cursor in a transaction (`dotnet/src/Eve.Events/EventBus.cs:408-416`). The CI query and shutdown order are consistent with cancellation being observed after that `UPDATE` reached PostgreSQL but before its commit completed, leaving the connection in an open transaction. This is the diagnosed cause; the cancellation boundary was not instrumented directly in the CI run.

The cursor-advance update still observes the stop token. If cancellation interrupts that statement, the branch explicitly rolls the transaction back with a non-cancelable token before rethrowing. Once the update succeeds, its commit uses `CancellationToken.None`, closing the window where shutdown could cancel between the statement and `COMMIT`. The change is limited to this cursor-advance branch.

## Auction amount cross-check

A whole-checkout search of `/home/user/eve-logic` found the auction UI callsites in `eve/client/script/ui/shared/neocom/contracts/contractsDetailsWnd.py:588-612` and `contractentry.py:190-211`: placing a bid is a separate action from winner item-claim and issuer money-claim. `contracts.py:639-677` sends `PlaceBid` and `FinishAuction` as separate proxy methods. The creation preview uses Accounting-adjusted tax and a maximum from `CalcContractFees` (`createContractWnd.py:654-661`); the shared client calculation is 1%, reduced by 0.1 percentage points per Accounting level, with a 10,000 ISK minimum and an Accounting-adjusted cap (`eve/common/script/util/contractscommon.py:13-15,84-94`).

EveJS routes those separate RPCs at `server/src/services/_other/contractProxyService.js:886-907`. Its auction claim path calculates issuer sales tax from the actual winning bid and Accounting level, then subtracts it from proceeds (`server/src/services/contracts/contractAuctionClaims.js:63-110`, especially `:84-98`). Creation settlement records broker fee and deposit wallet legs (`server/src/services/contracts/contractRuntimeState.js:1353-1390,1969-1977`); sales tax is applied at the issuer's later claim. For the failing old fixture (50,000 ISK starting balance, 20,000 ISK winning bid, Accounting 0), the 10,000 ISK minimum tax leaves 10,000 ISK net proceeds, so the balance is 60,000 ISK. The old 70,000 ISK expectation was wrong. Main's `b245d717` fixture now checks a funded 20,000 ISK sale, a 10,000 ISK tax journal entry, and return of a 100,000 ISK deposit. EveJS behavior was traced from source; it was not launched for this check.

## Local verification

- Release builds for `Eve.Events`, `Eve.Events.Tests`, and `Eve.Host.Tests` completed with zero warnings and zero errors.
- The complete `Eve.Events.Tests` project passed on `eve_p3` against `616d3a9b7`: 30 passed, zero failed, zero skipped.
- The complete Host `RpcFuzzTests` class passed on `eve_p3` against `616d3a9b7`: 3 passed, zero failed, zero skipped.
- Main contains `616d3a9b7` (`Finish event cursor transactions during shutdown`). CI run 1705 completed green. Its Host shard 1/6 reported 213 passed and 3 skipped; shard 6/6 reported 212 passed and 3 skipped. The skipped cases in those shards are Tournament tests, not the RPC fuzz test.
- The documentation-only main commit `c375f086` did not trigger push CI because `docs/**` is ignored by `ci.yml`; full CI was therefore dispatched as run 1706 and completed green. Host shard 1/6, which includes the RPC fuzz test, reported 213 passed and 3 skipped. The newest two main CI runs are 1706 and 1705; both are green.

The local database test exercises Host shutdown and scratch-schema cleanup, but it did not deterministically pause between the cursor update and transaction commit. The observed regression has not been verified with the graphical EVE client; it is internal EventBus shutdown behavior and does not change the client wire protocol.
