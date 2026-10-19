# Performance

This document records repeatable measurements of client-facing hot paths and the changes made from them. Measurements use the dedicated `eve_p4` database lane and are compared within the same run where possible.

## dogmaIM.GetAllInfo (docked fitted rookie ship)

- Scenario: one Dogma service call and its deferred notifications per iteration against a newly created Caldari rookie character with its starter fit, using the Dogma test rig and loaded SDE. Ten warmup calls and 100 measured calls. Reports p50/p95 wall time and process-wide allocated bytes per call. In the same run, it measures one extra `BuildBrain` operation, which reconstructs the prior duplicate work.
- Rerun: `flock /tmp/eve-heavy-eve_p4.lock env EVE_PG_URL=postgres://postgres@127.0.0.1:5432/eve_p4 EVE_DOGMA_BENCH=1 DOTNET_CLI_USE_MSBUILD_SERVER=0 dotnet test dotnet/tests/Eve.Services.Dogma.Tests/Eve.Services.Dogma.Tests.csproj --no-restore --filter FullyQualifiedName~GetAllInfoBenchmarkTests -nodeReuse:false`.
- Before change (2026-10-19): 770,079 allocated bytes/call over 100 calls, with deferred notifications included in allocation measurement. Its wall-time timer stopped before those notifications, so that latency is excluded from comparison.
- After change (same fixture and 100 calls): p50 9.279 ms, p95 18.369 ms, 752,041 allocated bytes/call. The extra brain construction measured in that run took p50 0.111 ms, p95 0.146 ms, and allocated 17,136 bytes/call. Adding that operation back gives a same-run reference of 769,178 bytes/call; reusing the constructed brain saves 17,137 bytes/call (2.2%). The shared machine adds wall-time noise.
- No client wire values or notification ordering changed. The existing Dogma service tests cover the response and deferred brain notification flow.
