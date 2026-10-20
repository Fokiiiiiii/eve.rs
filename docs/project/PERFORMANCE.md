# Performance measurements

## PostgreSQL and Rust profile (2026-10-20)

- Scope: PostgreSQL 18 on `eve_p4` only. The workload comprised the existing live walk (60 passed, 8 skipped) and the serial Host test collection (296 assertions passed before the test host aborted after an expected handler exception). The Host database log covers fixture and service setup across the test collection; it is not a steady-state single-host profile.
- Query ranking by accumulated PostgreSQL execution duration during the Host test window: (1) numeric `types` field filters, 534,910 ms over 466 calls; (2) full `types` reads, 275,299 ms over 85; (3) projected `types` reads, 219,165 ms over 118; (4) full `typeDogma` reads, 216,838 ms over 200; (5) full `sde_mission.missions` reads, 91,846 ms over 83. The captured ranking is setup-heavy and repeated static-data loading dominates it.
- First query fix: `ReadByFieldAsync("types", "groupID", ...)` used a sequential scan. For groups 6–9, the baseline `EXPLAIN (ANALYZE, BUFFERS)` returned 59 rows in 1,385.984 ms with 15,220 blocks read. After `types_group_id_idx`, the same query used a bitmap index scan and returned 59 rows in 11.277 ms with 10 blocks read (123x faster in this pair of runs). Source tracing showed `TypesInGroupsAsync` gathers group IDs and sends one `ANY($1)` field query for the batch; that hot path is not a per-type N+1 loop.
- Second query fix: `ReadContainingAsync("typeDogma", ...)` used a sequential scan. For effect 101, the baseline returned 179 rows in 137.189 ms with 2,416 blocks read. After `type_dogma_doc_gin_idx` (`jsonb_path_ops`), it used a bitmap index scan and returned 179 rows in 1.498 ms with 0 blocks read (92x faster in this pair of runs).
- Third query fix: `ReadTypeSummariesAsync` projects the four catalog fields needed to choose among types; the Host log recorded 219,165 ms across 118 such calls. Its regression test checks the Ibis summary and group. A trial `types_summary_idx` plan used an ordered Index Scan and returned 52,591 rows in 1,910.411 ms with 17,891 blocks read; a forced sequential scan in a warm run took 1,337.311 ms with an external 3.5 MB sort. After dropping the trial index, the normal plan used Seq Scan plus sort and took 1,901.109 ms in a colder run. The index did not show a cold-read benefit, so it was removed from `BuildAsync`. Summary rows are now shared by data source, schema, and SDE build: one focused read of 52,591 rows took 1,510.018 ms; the next reader reused them in 1.215 ms.
- Full-table query fix: full reads of `types`, `typeDogma`, `missions`, and `mapSolarSystems` are shared by SDE readers backed by the same `NpgsqlDataSource`, schema, and build. The Host profile recorded 91,846 ms over 83 full `sde_mission.missions` reads and 85,299 ms over 474 full `mapSolarSystems` reads. In one focused run, PostgreSQL reads and shared-cache reads measured: 8,490 map-system rows 377.343 ms / 1.084 ms; 52,591 type rows 2,885.521 ms / 0.062 ms; 26,724 type-dogma rows 967.661 ms / 0.100 ms; 2,892 mission rows 908.775 ms / 0.075 ms. Build-number changes invalidate old snapshots, and a cancelled waiter leaves shared loads available to other callers.
- The Agent Research catalog path now reads projected type summaries before fetching its required dogma rows, avoiding a full JSON document read of the `types` table for that path. Before the change, full `types` reads totaled 275,299 ms across 85 calls in the Host test window. The focused Agent Research PostgreSQL tests passed 2/2.
- `eve-sde ensure` provisions both indexes after loading the SDE and when the requested build is already present. `SdeReader.BuildAsync` remains read-only and invalidates cached snapshots when the build changes. Provisioning is serialized per schema. PostgreSQL table and index storage grows; the indexes are a partial B-tree on numeric `types.groupID` and a GIN index over `typeDogma.doc`.
- The targeted PostgreSQL tests for both reader paths passed after the index changes. The broader Host test collection reported 296 passed, 0 failed, then aborted at the test-host process level; this is recorded separately from a completed test run.
- Rust benchmark profile: `CARGO_PROFILE_RELEASE_OPT_LEVEL=0`, 256 codegen units, LTO disabled, system allocator. Top functions: `marshal.encode.rowset-1000` 251 ops/s (32.8 MB/s, 5.24x JS); `destiny.ball.encode.500-ships` 789,841 ops/s (3.85x); `marshal.encode.small-call` 355,843 ops/s (2.60x). These are low-optimization baselines and do not describe the default release profile.
- PostgreSQL measurement used `ALTER DATABASE eve_p4 SET log_min_duration_statement=0` and `track_io_timing=on`; the settings were scoped to `eve_p4` and reset after collection. The final `SHOW` results were `-1` and `off`, and no `pg_stat_statements` extension remains; the running server lacks its required shared preload configuration.


## Proposed load scenarios

The candidate engineering SLOs and scenario list from the 2026-10-19 supplied research report are tracked in [TODO.md](TODO.md): handshake storm, 24-48 hour soak, movement-heavy load, dense grid, weapon activation burst, market hotspot, jump storm, mission wave burst, forced restart, cold SDE boot and malformed-protocol fuzz. These are proposals pending owner adoption, not measured targets.

## eve_p4 database and runtime profile (2026-10-20)

- Scope: the existing live walk ran against `eve_p4` (69 passed, 6 failed, 8 skipped); the failures were inventory access races and one structure availability case. The serial Host collection reported 457 passed and 2 skipped before its test-host process aborted, so it is not a completed suite. No graphical-client session was run.
- The captured Host-window query ranking remains the comparable baseline: (1) numeric `types` filters, 534,910 ms / 466 calls; (2) full `types` reads, 275,299 ms / 85; (3) projected `types` reads, 219,165 ms / 118; (4) full `typeDogma` reads, 216,838 ms / 200; (5) full mission-table reads, 91,846 ms / 83. This fixture-heavy window is dominated by repeated SDE loading. The live server log tail captured 8,223 statements over roughly three minutes and is not substituted for that full ranking.
- Existing measured fixes address the top five paths: the `types.groupID` B-tree reduced one 59-row query from 1,385.984 ms to 11.277 ms; the `typeDogma.doc` GIN index reduced one 179-row containment query from 137.189 ms to 1.498 ms; shared type summaries let a repeat 52,591-row read fall from 1,510.018 ms to 1.215 ms; shared snapshots cut a 26,724-row dogma reload from 967.661 ms to 0.100 ms; and shared mission snapshots cut a 2,892-row reload from 908.775 ms to 0.075 ms. These focused comparisons and scope are described above.
- `PlanetCatalogLoader.LoadAsync` now selects only `groupID`, `volume`, `basePrice`, and `capacity` from `sde.types` instead of fetching and parsing every matching type JSON document. Its focused SDE regression test checks these values against the source row and passed 1/1. A paired `EXPLAIN (ANALYZE, BUFFERS)` comparison over the same 174 matching rows measured the old full-document projection at 1,144.853 ms and 625.370 ms (median 885.112 ms), and the scalar projection at 702.141 ms and 846.684 ms (median 774.413 ms), a 12.5% lower median execution time in this pair. The runs alternated query shape; cache reads differed, and the two-sample result is a focused database comparison rather than a full loader benchmark. The prior whole-Host ranking remains a distinct workload.
- PostgreSQL `pg_stat_statements` could not provide counters because the running server does not preload it. Statement-duration logging and I/O timing were scoped to `eve_p4`; they are reset after the focused workload.
- Rust low-optimization benchmark sample (`OPT_LEVEL=0`, 256 codegen units, LTO disabled): `destiny.ball.encode.500-ships` 1,153,325 ops/s; `marshal.encode.small-call` 453,990 ops/s; `marshal.encode.rowset-1000` 428 ops/s (55.8 MB/s). This was a single sample on a shared host and is a baseline, not a before/after gain.

## Host startup and runtime settings (2026-10-20)

- The Release Host was started with no database URL and an isolated Unix socket. Startup time is measured from process launch to the first successful socket connection. This isolates runtime startup and listener readiness; it does not include PostgreSQL migrations, SDE loading, or client login.
- Three samples per setting: tiered compilation enabled (default) took 960.2, 540.4, and 416.0 ms (median 540.4 ms); tiered compilation disabled took 807.8, 725.0, and 567.7 ms (median 725.0 ms); server GC enabled took 819.3, 427.8, and 376.0 ms (median 427.8 ms). The samples were run as separate sequential batches on a shared host, so their spread and order make the medians directional only. No tiered compilation or GC setting was changed.
- A second three-sample comparison measured the ordinary IL Release output at 360.0, 470.6, and 246.1 ms (median 360.0 ms), and a `PublishReadyToRun=true` output at 150.7, 348.5, and 279.6 ms (median 279.6 ms). Both used the same host-only readiness check. This small sample does not cover database-backed startup or steady-state behavior; ReadyToRun is not enabled in the project.
- `HostComposition.BuildAsync` creates `NpgsqlDataSource` from the supplied connection string without setting pool limits. The Npgsql default `Maximum Pool Size` is 100 ([connection string parameters](https://www.npgsql.org/doc/connection-string-parameters)). The captured Host collection and live walk were serial and did not collect concurrent pool occupancy or wait-time counters, so they provide no evidence that the default pool is exhausted or should be resized. No pool setting was changed.
- Release Host build succeeded with zero warnings and errors. The startup harness used the built executable; these comparisons do not assert gameplay behavior or graphical-client compatibility.

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


## Protocol parser limits and benchmark (2026-10-20)

The inbound path is length prefix → payload → marshal decode → Macho packet validation → session-state check → service/method authorization → typed argument validation → dispatch. TCP frame payloads must be 1 byte through 16 MiB. Marshal decoding accepts payloads through 16 MiB, nesting through 256 values, at most 4,194,304 decoded values, declared string/byte lengths through 16 MiB, and an estimated 256 MiB of decoder-owned allocation. Nested substreams share the element and allocation budgets. `PyString`, `PyBuffer`, and `PyCPickled` values continue to reference slices of the input `Bytes`; UTF-16 conversion no longer builds a second temporary code-unit vector. A zero-length frame remains a valid transport frame and is delivered to marshal decoding, where it fails as a malformed payload.

The V24-oriented Host regression `HostServerTests.An_undecodable_packet_is_dropped_and_the_session_carries_on` verifies that a malformed marshal packet is discarded while a later valid ping from the same session receives a response; it passed on `eve_p3` (1/1). Rust parser property checks are `eve-marshal/tests/robustness.rs` (generated codec values, arbitrary malformed bytes, boundary limits) and `eve-net/tests/frame_properties.rs` (generated coalesced frames, empty/oversized declarations, and truncated frames). The full focused `cargo test -p eve-marshal` and `cargo test -p eve-net` commands passed.

`cargo run --release -p eve-bench -- --filter marshal.decode` uses the system allocator, warms each workload for 300 ms, measures nine 250 ms batches, and reports their median throughput. Results:

| Workload | Decode throughput | Input throughput |
| --- | ---: | ---: |
| `small-call` | 452,512 ops/s | 41.2 MB/s |
| `dict-200` | 25,105 ops/s | 104.8 MB/s |
| `rowset-1000` | 1,125 ops/s | 146.6 MB/s |
| `buffers-64x4k` | 106,430 ops/s | 27,941.7 MB/s |

The `--latency --filter marshal.decode` mode warms each workload for 300 ms, records 20,000 individual decode durations with `Instant`, and reports nearest-rank p50/p95/p99. Results (system allocator):

| Workload | p50 | p95 | p99 |
| --- | ---: | ---: | ---: |
| `small-call` | 0.824 µs | 1.165 µs | 1.444 µs |
| `dict-200` | 19.272 µs | 26.021 µs | 55.050 µs |
| `rowset-1000` | 404.124 µs | 937.535 µs | 4,515.394 µs |
| `buffers-64x4k` | 3.609 µs | 4.057 µs | 8.104 µs |

Each codec-only workload's measured p95 is below the proposed cheap-RPC compute budget of 20 ms. These samples time marshal decode alone: they exclude TCP, session validation, authorization, dispatch, database work, and concurrent sessions. They do not establish the end-to-end local RPC p95 target of 100 ms. Multi-session load, long soak, malformed traffic under concurrency, and full request-to-response percentile measurements remain open.
