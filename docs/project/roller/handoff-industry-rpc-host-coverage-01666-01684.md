# Industry RPC host coverage handoff (RPC-01666–RPC-01684)

## Status

The assigned service behavior is already implemented and Host-wired in the current source. This batch adds a real Host registry/dispatcher wire regression for all seven `industryManager` calls, and records that RPC-01680 is a client-side local dictionary lookup rather than a remote method. The two monitor methods are also already registered through the production inventory service list and covered by the existing Host-dispatch notification tests.

## Whole-client contract evidence

The full `/home/user/eve-logic` search found the `industryManager` remote calls only in `eve/client/script/industry/industrySvc.py`:

- RPC-01666/01667 `CancelJob`: the UI calls `IndustryService.CancelJob` from `submitButton.py:206`; it sends `(int(jobID), solarSystemID)` at `industrySvc.py:158-160` and ignores the reply.
- RPC-01668/01669 `CompleteJob`: callers are `submitButton.py:202` and `jobEntry.py:317`; the remote call sends `(int(jobID), solarSystemID)` at `industrySvc.py:149-151` and ignores the reply.
- RPC-01670/01671 `CompleteManyJobs`: `browserJobs.py:334` collects selected job/system pairs; `industrySvc.py:153-155` sends one list and ignores the reply.
- RPC-01672/01673 `GetJob`: `industrySvc.py:56-60` turns the returned KeyVal into a job; UI consumers include `industryWnd.py:147,223,451` and `nodegraph/client/events/industry.py:16`.
- RPC-01674/01675 `GetJobCounts`: `industrySvc.py:242-244` stores the returned dict and raises `OnIndustrySlotsUpdated`; `jobsStrip.py:92-119,176-191` and `industryTooltips.py:431,504` display its activity counts.
- RPC-01676/01677 `GetJobsByOwner`: `industrySvc.py:62-88` maps each returned KeyVal to a job; `browserJobs.py:204-206` displays personal/corporation job lists and `nodegraph/client/conditions/industry.py:35` checks character jobs.
- RPC-01678/01679 `InstallJob`: UI callers are `submitButton.py:220` and `blueprintEntry.py:306`; `industrySvc.py:139-146` sends `job.dump()` and returns the job ID. Both UI callers discard that result and follow the resulting job event/state.

RPC-01680 is not a remote call: `industrySvc.py:380-386` reads `self.slots.get(activityID, 0)` from the local dict returned by `GetJobCounts`. EveJS v0.12.9 has no `industryManager.get` method. No fake route was added.

For RPC-01681/01682 `ConnectJob`, `industrySvc.py:104-105,304-307` connects input-location changes and stores the returned `monitorID` and materials dict. RPC-01683/01684 `DisconnectJob` is called by `industrySvc.py:104,310-314` when the job closes; `industryWnd.py:107` also disconnects on window teardown. The server's `OnIndustryMaterials` notification is consumed at `industrySvc.py:323-328`, updates only the matching open job, then `industry/job.py:480` and `industry/material.py:58-102` use the available counts to show missing materials and validity; `materialGroupDashedCircle.py:77` displays available/required quantities.

Industry status notifications continue through `industrySvc.py:295-300`; `industryWnd.py:439`, `browserJobs.py:111` and `nodegraph/client/events/industry.py:11-16` consume the resulting client event.

## EveJS and Host comparison

EveJS v0.12.9 `server/src/services/industry/industryManagerService.js:60-149` implements `GetJob`, `GetJobsByOwner`, `GetJobCounts`, `InstallJob`, `CompleteJob`, `CompleteManyJobs` and `CancelJob`. Its `industryMonitorService.js:15-34` returns `(monitorID, availableMaterials)` and returns null for disconnect. eve.rs uses the persisted industry jobs provider through `IndustryRpc.cs:39-53`; `IndustryJobsService.cs:131-229` maps wire arguments, validates malformed input, applies owner visibility, reads persisted job state and crosses the typed mutation port. Host owns transactional inventory/wallet/job/outbox work in `PersistenceIndustryJobOperations`.

The monitor is included in `InventoryRpc.cs:42-46,72,79-80`: Host wraps the real inventory broker with `IndustryInventoryChangePublisher`, sends the inventory notification first, rereads available materials, and emits `OnIndustryMaterials` only after a changed quantity map. The service scopes watches to the session, persists monitor ID metadata, removes only the matching watch on disconnect, and clears live watches when a session closes.

The existing compatibility record documents the only client-irrelevant reply difference: EveJS returns a job payload from complete/cancel while eve.rs returns None; the client callers above discard those results (`docs/reference/compat/DEVIATIONS.md:4025-4032`).

## Checks

- PASS: `MSBUILDDISABLENODEREUSE=1 dotnet build dotnet/tests/Eve.Host.Tests/Eve.Host.Tests.csproj --no-restore -p:UseSharedCompilation=false` (0 warnings, 0 errors).
- PASS: focused `IndustryHostRpcTests` (2 passed, 0 failed, 0 skipped). The real service implementations are dispatched as Macho CallReq through `ServiceRegistry` and `PacketDispatcher`; industry mutations use a recording operations-port fixture, and monitor materials use a provider fixture. No PostgreSQL was used.
- The regression checks all seven manager calls and both monitor calls, manager wire shapes and typed mutation inputs, material-change notification delivery, and disconnect cleanup.
- PASS: Actions run `40182750083` for `IndustryHostRpcTests` on `73d5442b` completed successfully with no failed tests or jobs; the status summary did not report skipped-test totals.
- `python3 tools/dev/ci-status.py failures 73d5442b` reported no push-triggered `ci.yml` run for that SHA.
- On merged SHA `2a28de00`, the selected Actions run exposed a compile error in the concurrently merged `StructureAssetSafetyRpcTests.cs`: it used `MachoMessage` without `using Eve.Macho;`. Added the missing namespace; the focused test `Structure_asset_safety_remote_calls_cross_the_registered_host_with_client_shapes` passed (1 passed, 0 failed, 0 skipped). A rerun on the fix commit is pending.
- A concurrent main merge then added the same import, producing a duplicate using on `854e82a5`; removed the duplicate. Release focused run of `IndustryHostRpcTests` and the structure wire test passed (3 passed, 0 failed, 0 skipped). Actions run `40186839806` on `6709a204` passed with no failed tests or jobs; the status summary did not report skipped totals.
- `python3 tools/dev/ci-status.py failures 6709a204` reported no push-triggered `ci.yml` run for that SHA.
- Final-tip Actions run `40187391504` for `IndustryHostRpcTests` on `b8efda91` passed with no failed tests or jobs; the status summary did not report skipped totals. `failures b8efda91` found no push-triggered `ci.yml` run.
- `python3 tools/dev/ci-status.py failures 2a28de00` reported no push-triggered `ci.yml` run for that SHA.
- PostgreSQL and graphical-client sessions were not used.

## Verification follow-up (2026-10-25)

On merged tip `a541187d`, the five assigned rows were rechecked. RPC-01680 remains a local lookup of the `GetJobCounts` result: `industrySvc.py:380-386` reads `self.slots`, fetched at `industrySvc.py:242`; slot UI consumers are `jobsStrip.py:119,191` and `industryTooltips.py:431,504`. It has no `industryManager.get` remote call in the client. Monitor request/reply consumers are `industrySvc.py:304-307`, notifications update only the matching monitor at `industrySvc.py:323-328`, and material counts feed missing-quantity display at `industry/material.py:58-102`. EveJS returns the `(monitorID, availableMaterials)` pair from `industryMonitorService.js:20-27` and null from disconnect at `29-33`.

Local verification on the worktree before the fast-forward to this tip: Host test project build succeeded (0 warnings, 0 errors); focused `IndustryHostRpcTests` passed 2/2, 0 skipped. No database was used. Both selected Actions run `40228403124` and push-triggered `ci.yml` run `40227646004` failed before tests at `actions/setup-dotnet`: the runner received HTTP 409 downloading .NET Runtime 10.0.14 and SDK 10.0.403. A retry (`40228620119`) repeated the same setup failure. The root `global.json` starts at SDK 10.0.401 but permits `latestFeature`, so passing it to setup-dotnet selected 10.0.403; setup-dotnet also attempted a separate latest-LTS runtime pre-pass (10.0.14). Both downloads returned HTTP 409. The workflows now request SDK 10.0.401. Selected run `40229267410` passed setup-dotnet but emitted an unsupported-input warning for `skip-lts-prepass` (v6.0.0), then the Release solution build failed on seven CS0103 references to `Py` in the concurrently merged `ShipRpcHostTests.cs`; `Py` is defined in `Eve.Services.Py`. Push-gate run `40229260672` also failed in the Windows .NET setup on the 10.0.14 runtime download. The missing `using Eve.Services;` is added. Upstream setup-dotnet commit `349aa191ae05aee6d53db4bcba37bd6980e812fe` adds the `skip-lts-prepass` input and its tests; the selected and push-gate workflows now pin that commit, request SDK 10.0.401, and skip the failing LTS pre-pass. Selected run `40230998319` succeeded with no failed tests or jobs; its summary did not report skipped-test totals. Push-gate run `40230993079` failed in Host shards 1 and 2 at `RpcSecurityInventoryTests.Real_host_registry_methods_are_enumerable`: expected method count 1414, actual 1420. The assertion is updated to 1420 from that production Host registry enumeration. A selected rerun for `Real_host_registry_methods_are_enumerable` is pending on the count fix commit. The focused industry test remains 2/2 passed locally with 0 skipped. After the latest merged main updates, the Host test project build also succeeded locally (0 warnings, 0 errors; no database). Fetched and merged origin/main at c0429d97; the pending count-fix commit remains in the integration history. Selected Actions run 40234793987 on 7f84ae3a passed Real_host_registry_methods_are_enumerable with no failed tests or jobs. `python3 tools/dev/ci-status.py failures 7f84ae3a` found no push-triggered ci.yml run.
