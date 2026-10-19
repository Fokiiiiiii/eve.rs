# Performance measurements

## Proposed load scenarios

The candidate engineering SLOs and scenario list from the 2026-10-19 supplied research report are tracked in [TODO.md](TODO.md): handshake storm, 24-48 hour soak, movement-heavy load, dense grid, weapon activation burst, market hotspot, jump storm, mission wave burst, forced restart, cold SDE boot and malformed-protocol fuzz. These are proposals pending owner adoption, not measured targets.

## dogmaIM.GetAllInfo (docked fitted rookie ship)

- Scenario: one Dogma service call and its deferred notifications per iteration against a newly created Caldari rookie character with its starter fit, using the Dogma test rig and loaded SDE. Ten warmup calls and 100 measured calls. Reports p50/p95 wall time and process-wide allocated bytes per call. In the same run, it measures one extra `BuildBrain` operation, which reconstructs the prior duplicate work.
- Rerun: `flock /tmp/eve-heavy-eve_p4.lock env EVE_PG_URL=postgres://postgres@127.0.0.1:5432/eve_p4 EVE_DOGMA_BENCH=1 DOTNET_CLI_USE_MSBUILD_SERVER=0 dotnet test dotnet/tests/Eve.Services.Dogma.Tests/Eve.Services.Dogma.Tests.csproj --no-restore --filter FullyQualifiedName~GetAllInfoBenchmarkTests -nodeReuse:false`.
- Before change (2026-10-19): 770,079 allocated bytes/call over 100 calls, with deferred notifications included in allocation measurement. Its wall-time timer stopped before those notifications, so that latency is excluded from comparison.
- After change (same fixture and 100 calls): p50 9.279 ms, p95 18.369 ms, 752,041 allocated bytes/call. The extra brain construction measured in that run took p50 0.111 ms, p95 0.146 ms, and allocated 17,136 bytes/call. Adding that operation back gives a same-run reference of 769,178 bytes/call; reusing the constructed brain saves 17,137 bytes/call (2.2%). The shared machine adds wall-time noise.
- No client wire values or notification ordering changed. The existing Dogma service tests cover the response and deferred brain notification flow.

## Marshal GetAllInfo reply baseline

Measured on 2026-10-19 using the docked fitted rookie-ship reply produced by `GetAllInfoBenchmarkTests`. The returned `PyValue` is encoded and decoded 1,000 times after 20 warmups. Time and per-thread allocations are measured around the codec calls only; the encoded payload is 18,828 bytes. This is a codec microbenchmark over a real service reply, not a graphical-client session.

Rerun with `flock /tmp/eve-heavy-eve_p4.lock env EVE_PG_URL=postgres://postgres@127.0.0.1:5432/eve_p4 EVE_DOGMA_BENCH=1 DOTNET_CLI_USE_MSBUILD_SERVER=0 dotnet test dotnet/tests/Eve.Services.Dogma.Tests/Eve.Services.Dogma.Tests.csproj --filter FullyQualifiedName~Measure_docked_GetAllInfo_with_a_fitted_rookie_ship -nodeReuse:false --logger 'console;verbosity=detailed'`.

| Path | Calls | Payload | Mean per call | Allocated per call |
|---|---:|---:|---:|---:|
| Marshal encode | 1,000 | 18,828 bytes | 691,982 ns | 139,456 bytes |
| Marshal decode | 1,000 | 18,828 bytes | 493,895 ns | 151,960 bytes |

These are the baseline numbers before the encoder change. After switching nested tuple, list, object-list and dictionary-pair traversal to indexed loops, the reply encoded deterministically on repeat, while the 1,107-case marshal golden suite continued to match the recorded wire bytes:

| Path | Calls | Payload | Mean per call | Allocated per call |
|---|---:|---:|---:|---:|
| Marshal encode after indexed traversal | 1,000 | 18,828 bytes | 1,025,006 ns | 122,688 bytes |
| Marshal decode (unchanged) | 1,000 | 18,828 bytes | 594,968 ns | 151,960 bytes |

Encoding saved 16,768 allocated bytes/call (12.0%). The two encode wall-time samples are noisy on the shared machine and do not show a latency improvement; no latency gain is claimed. The full `Eve.Marshal.Tests` suite passed (1,107 tests), and the benchmark asserts repeat encoding matches the measured bytes.

## Simulation-to-Host frame batching baseline

Measured on 2026-10-19 in release mode with a deterministic synthetic batch of 64 `eve_space::link::Message::HelloAck` messages. The harness measures the former `Message::encode` batching path and the current `Message::encode_into` path in alternating sequence, five samples of 100,000 batches each. Each iteration consumes the output length. The workload isolates frame construction from socket scheduling and syscall noise.

Rerun with `CARGO_BUILD_JOBS=1 CARGO_PROFILE_BENCH_LTO=false CARGO_PROFILE_BENCH_CODEGEN_UNITS=16 CARGO_PROFILE_BENCH_OPT_LEVEL=1 cargo bench -p eve-sim --bench link_frames` (benchmark source: `crates/eve-sim/benches/link_frames.rs`). The build profile overrides keep the harness quick to compile in a shared checkout. The benchmark reports the median time for each path and checks that both produce the same frame bytes.

| Path | Iterations | Time per batch | Bytes per batch |
|---|---:|---:|---:|
| Before: individual `Message::encode` frames appended to a batch | 500,000 | 11.29 µs | 704 |
| After: frames appended with `Message::encode_into` | 500,000 | 4.45 µs | 704 |

The same 64 messages produced 704 bytes in both runs. The batch encoding path dropped from two per-message Vec allocations (payload and framed copy) to one (payload); the shared output Vec retains its normal amortized growth. The same-run median batch time decreased 60.6%. This is a serialization microbenchmark, not a full simulation tick or graphical-client measurement.

## Simulation tick baseline

Measured `Sim::poll` with a hand-built universe and active in-space pilots: one system with no moving celestial objects, one fitted Ibis per pilot, spaced 5 km apart. Each case warms 1,000 ticks, then records five samples of 1,000 polls, advancing simulated time by 250 ms per poll and consuming each output message count. Pilot counts are 1, 16 and 64.

Rerun with `CARGO_BUILD_JOBS=1 CARGO_PROFILE_BENCH_LTO=false CARGO_PROFILE_BENCH_CODEGEN_UNITS=256 CARGO_PROFILE_BENCH_OPT_LEVEL=0 cargo bench -p eve-space --bench sim_tick` (source: `crates/eve-space/benches/sim_tick.rs`). This low-optimization profile avoided compiler memory pressure in the shared environment; its timings are a baseline for the exact harness and do not predict optimized server latency.

| Active pilots | Median time per `Sim::poll` |
|---:|---:|
| 1 | 48.01 µs |
| 16 | 404.52 µs |
| 64 | 3.57 ms |

This exercises the real simulation poll and scene tick path. The synthetic scene keeps pilots stationary and does not measure movement-heavy combat, real network writes, or graphical-client load. Attributing tick time to individual phases and measuring spatial queries, marshal, SDE loading and process memory remain open.

## Market order book read baseline

Measured on 2026-10-19 with the opt-in PostgreSQL market benchmark on `eve_p4`. It seeded 10,000 resting NPC asks for one type and region, placed 20 one-unit buys, then read the remaining book through `MarketStore.BookAsync`. The run measured p50 placement 12.6 ms, p95 18.5 ms, and maximum 621.6 ms; each placement locked exactly 50 candidate rows. The final book read returned 9,998 asks, no bids, in 67.8 ms. The maximum placement is an outlier on this shared test machine; the benchmark's deterministic row-lock count is the stable matching-work measure.

Rerun with `flock /tmp/eve-heavy-eve_p4.lock env EVE_PG_URL=postgres://postgres@127.0.0.1:5432/eve_p4 EVE_MARKET_BENCH=1 DOTNET_CLI_USE_MSBUILD_SERVER=0 dotnet test dotnet/tests/Eve.Market.Tests/Eve.Market.Tests.csproj --no-restore --filter FullyQualifiedName~Placement_on_a_10000_order_book_locks_a_bounded_number_of_rows -nodeReuse:false --logger 'console;verbosity=detailed'`. The benchmark source is `dotnet/tests/Eve.Market.Tests/OrderBookBenchmarkTests.cs`. It is a synthetic large-book database test, not a live client session. PostgreSQL statement-level totals were not available: `pg_stat_statements` is not installed in `eve_p4`, and `log_min_duration_statement` is `-1`; ranking the login/play query workload and capturing `EXPLAIN (ANALYZE, BUFFERS)` remain open.

A single-query variant was measured against this baseline, with asks and bids split while reading one ordered result. It preserved the market book regression test but changed the 10,000-order book read from 67.8 ms to 177.3 ms, with the same 9,998 asks; the shared host is noisy, but this result does not show a gain, so the variant was reverted. The two-query implementation remains in place.
