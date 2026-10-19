# Performance

This document records repeatable measurements of client-facing hot paths and the changes made from them. Measurements use the dedicated `eve_p4` database lane and are compared within the same run where possible.

## dogmaIM.GetAllInfo (docked fitted rookie ship)

- Scenario: one real Host RPC per iteration against a newly created Caldari rookie character with its starter fit, using the Dogma test rig and the loaded SDE. Ten warmup calls followed by 100 measured calls. Reports p50/p95 wall time and process-wide allocated bytes per measured call.
- Rerun: `flock /tmp/eve-heavy-eve_p4.lock env EVE_PG_URL=postgres://postgres@127.0.0.1:5432/eve_p4 EVE_DOGMA_BENCH=1 dotnet test dotnet/tests/Eve.Services.Dogma.Tests/Eve.Services.Dogma.Tests.csproj --no-restore --filter FullyQualifiedName~GetAllInfoBenchmarkTests -nodeReuse:false` (with `DOTNET_CLI_USE_MSBUILD_SERVER=0`).
- Baseline (one run, 2026-10-19): p50 6.979 ms, p95 94.138 ms, 452,636 allocated bytes/call across 100 calls. The wide tail reflects shared-machine and DB timing noise; allocation is the more stable signal.
