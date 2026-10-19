# Performance measurements

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

These are the same-run baseline numbers before codec changes. The shared machine makes latency noisy; allocation counts are the primary comparison.

## Simulation-to-Host frame batching baseline

Measured on 2026-10-19 in release mode with a deterministic synthetic batch of 64 `eve_space::link::Message::HelloAck` messages. The harness measures the former `Message::encode` batching path and the current `Message::encode_into` path in alternating sequence, five samples of 100,000 batches each. Each iteration consumes the output length. The workload isolates frame construction from socket scheduling and syscall noise.

Rerun with `CARGO_BUILD_JOBS=1 CARGO_PROFILE_BENCH_LTO=false CARGO_PROFILE_BENCH_CODEGEN_UNITS=16 CARGO_PROFILE_BENCH_OPT_LEVEL=1 cargo bench -p eve-sim --bench link_frames` (benchmark source: `crates/eve-sim/benches/link_frames.rs`). The build profile overrides keep the harness quick to compile in a shared checkout. The benchmark reports the median time for each path and checks that both produce the same frame bytes.

| Path | Iterations | Time per batch | Bytes per batch |
|---|---:|---:|---:|
| Before: individual `Message::encode` frames appended to a batch | 500,000 | 11.29 µs | 704 |
| After: frames appended with `Message::encode_into` | 500,000 | 4.45 µs | 704 |

The same 64 messages produced 704 bytes in both runs. The batch encoding path dropped from two per-message Vec allocations (payload and framed copy) to one (payload); the shared output Vec retains its normal amortized growth. The same-run median batch time decreased 60.6%. This is a serialization microbenchmark, not a full simulation tick or graphical-client measurement.
