# Performance measurements

## Simulation-to-Host frame batching baseline

Measured on 2026-10-19 in release mode with a deterministic synthetic batch of 64 `eve_space::link::Message::HelloAck` messages. The harness measures the former `Message::encode` batching path and the current `Message::encode_into` path in alternating sequence, five samples of 100,000 batches each. Each iteration consumes the output length. The workload isolates frame construction from socket scheduling and syscall noise.

Rerun with `CARGO_BUILD_JOBS=1 CARGO_PROFILE_BENCH_LTO=false CARGO_PROFILE_BENCH_CODEGEN_UNITS=16 CARGO_PROFILE_BENCH_OPT_LEVEL=1 cargo bench -p eve-sim --bench link_frames` (benchmark source: `crates/eve-sim/benches/link_frames.rs`). The build profile overrides keep the harness quick to compile in a shared checkout. The benchmark reports the median time for each path and checks that both produce the same frame bytes.

| Path | Iterations | Time per batch | Bytes per batch |
|---|---:|---:|---:|
| Before: individual `Message::encode` frames appended to a batch | 500,000 | 11.29 µs | 704 |
| After: frames appended with `Message::encode_into` | 500,000 | 4.45 µs | 704 |

The same 64 messages produced 704 bytes in both runs. The batch encoding path dropped from two per-message Vec allocations (payload and framed copy) to one (payload); the shared output Vec retains its normal amortized growth. The same-run median batch time decreased 60.6%. This is a serialization microbenchmark, not a full simulation tick or graphical-client measurement.
