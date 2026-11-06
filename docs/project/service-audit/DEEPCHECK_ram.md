# Deep check: `ram`

Checked 2026-10-27 against the whole V24.01 client tree at `/home/user/eve-logic` (commit `1dbb5711a072a146e1d228365668ac05655eefe1`), eve.rs Host sources, and EveJS 261018.1 under `/home/user/evejs-261018.1/src`.

## Client inventory and verdict

The client has **zero RPC calls to a service named `ram`**, including direct remote calls and bound/moniker forms. Whole-tree searches for service accessors and moniker/bind calls found no `ram` target. Exact `'ram'` string hits are unrelated: audio MIME types (`scriber/const.py:79`, `bugreporter/util.py:358`), CherryPy memory-session storage (`cherrypy/test/test_session.py:26`, `cherrypy/lib/sessions.py:513`, and their `carbon/common/lib/cherrypy` copies at the same lines), and the alert metric label (`carbon/common/script/sys/alert.py:212`).

| Required check | Result for `ram` |
|---|---|
| Client methods and callsites | None |
| Host registration and route | No `ram`-named service or alias in `dotnet/src` |
| Arguments, including kwargs / `machoVersion`, integer widths, and `None` | No client method signature exists |
| Reply types and expected errors | No client reply/error contract exists |
| Persisted state and subsequent calls | No client transition depends on a `ram` call |
| Follow-up notifications | None expected after a `ram` call |

EveJS 261018.1 also has no `ram` service class or `ram` service registration in `server/src/services`. No Host handler or wire test was added: that would create an uncalled route without a V24.01 contract.

## Closest V24.01 service path

The closest feature is manufacturing through `industryManager`, with `industryMonitor` for live material counts. These are distinct client service names, not aliases for `ram`.

| Client RPC | Client call shape and immediate consumption |
|---|---|
| `industryManager.GetJob` | One `jobID`; the returned record feeds `_JobInstance` (`eve/client/script/industry/industrySvc.py:57-60`). |
| `industryManager.GetJobsByOwner` | `ownerID, includeCompleted`; rows become job objects and populate the owner cache (`industrySvc.py:62-83`). |
| `industryManager.InstallJob` | One `job.dump()`; the wrapper returns the installed job ID (`industrySvc.py:138-146`). |
| `industryManager.CompleteJob` | `jobID, solarSystemID`; reply is ignored (`industrySvc.py:148-151`). |
| `industryManager.CompleteManyJobs` | One list of `(jobID, solarSystemID)` pairs; reply is ignored (`industrySvc.py:152-156`). |
| `industryManager.CancelJob` | `jobID, solarSystemID`; reply is ignored (`industrySvc.py:157-161`). |
| `industryManager.GetJobCounts` | One character ID; the returned mapping is cached as slots and read by activity ID (`industrySvc.py:241-246,381-386`). |
| `industryMonitor.ConnectJob` | One `job.dump()`; the returned `(monitorID, availableMaterials)` pair updates the job (`industrySvc.py:304-308`). |
| `industryMonitor.DisconnectJob` | One monitor ID or `None`; the result is ignored (`industrySvc.py:310-314`). The later `OnIndustryMaterials(jobID, materials)` callback updates the active job (`industrySvc.py:323-327`). |

EveJS confirms these separate names and handlers: `IndustryManagerService` registers `industryManager` and handles the seven methods above (`server/src/services/industry/industryManagerService.js:60-149`); `IndustryMonitorService` registers `industryMonitor` and handles connect/disconnect (`industryMonitorService.js:15-34`).

Host source likewise registers `IndustryJobsService` as `industryManager` (`dotnet/src/Eve.Services.Core/IndustryJobsService.cs:136-138`, `dotnet/src/Eve.Host/Rpc/IndustryRpc.cs:42-58`) and `IndustryMonitorRpcService` as `industryMonitor` (`dotnet/src/Eve.Host/IndustryMonitorRpcService.cs:39-43`, `dotnet/src/Eve.Host/Rpc/InventoryRpc.cs:42-80`). Existing packet-dispatch coverage for these nearest services is in `dotnet/tests/Eve.Host.Tests/IndustryHostRpcTests.cs:13,78`; it is not a test of a `ram` contract.

## Verification and remaining checks

- Passed on the audited Host sources: `dotnet test dotnet/tests/Eve.Host.Tests --no-restore -p:UseSharedCompilation=false --filter FullyQualifiedName~IndustryHostRpcTests` (2 passed, 0 failed, 0 skipped). This verifies the existing closest-service packet tests only.
- Full Host project shard on the inherited `cx3-fitting` worktree: 518 passed, 100 failed, 227 skipped, with `EVE_PG_URL` unset. Some failures explicitly require that setting; the remaining failures are outside this ram audit. This is not a passing full-shard result; those inherited code changes are excluded from the ram-only push.
- No PostgreSQL database was used. Production database persistence/restart recovery and the graphical client were not exercised.
