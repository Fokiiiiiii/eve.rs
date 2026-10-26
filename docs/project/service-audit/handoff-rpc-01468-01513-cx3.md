# RPC-01468–01513 Host coverage handoff

## Verified against the V24.01 client

- `dynamicResourceSvc.py:69-77, 135-152` calls the ESS read/mutation routes. The existing `Ess_epic_arc_and_wreck_rpcs_are_dispatched_with_client_shapes` test uses the real Host registry, dispatcher and loopback socket (`dotnet/tests/Eve.Host.Tests/EssRpcHostWireTests.cs:10-110`) and covers their reply shapes and operation dispatch.
- `dynamicResourceSvc.py:166-170, 179, 189, 195` calls client-side cache helpers. `prime_cache_result` and `clear_cache` are wrapper methods in `brennivin/threadutils.py:312-334`, not remote methods. EveJS ESS behavior is in `server/src/services/dynamic/essMgrService.js:204-344`.
- `facWarSvc.py:142-145` forwards `GetFactionMilitiaCorporation` and `infrastructureHub.py:119-120, 219-220` uses the returned corporation ID. A focused assertion exercises this method through the real Host socket route; Caldari faction 500001 maps to militia corporation 1000180 per the pinned SDE faction data (`FwFactions.cs`).
- The full-tree client mapping for the other requested FW methods and consumers is in `docs/project/service-audit/handoff-RPC-01483-01519.md`, section “V24.01 caller and consumer evidence”. In particular, `facWarSvc.py:90-101, 272-273, 351-379, 446-470` forwards faction IDs, corporation status, statistics, system LP and donation calls to `facWarMgr`. The EveJS v0.12.9 comparison is in that handoff's “EveJS v0.12.9 comparison” section.

## Checks and remaining work

- Local focused check: `Faction_warfare_rank_calls_round_trip_through_host_registration` passed (1 passed, 0 failed, 0 skipped), including the added militia-corporation response. `dotnet build` of `Eve.Host.Tests` passed with 0 warnings and 0 errors. No database was used.
- `RPC-01502`, `01503`, `01505`–`01507`, and `01511`–`01513` still need success-path Host-wire regression coverage. Their `FwService` provider reads/writes PostgreSQL state. This pass did not exercise those success paths because the task forbade database use.
- Commit `3c42c6b9` was pushed to `main`. Targeted Actions and the push-triggered CI gate remain to be checked.