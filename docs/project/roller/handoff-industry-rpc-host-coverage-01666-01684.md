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

- PASS: `MSBUILDDISABLENODEREUSE=1 dotnet build dotnet/tests/Eve.Host.Tests/Eve.Host.Tests.csproj -p:UseSharedCompilation=false` (0 warnings, 0 errors).
- PASS: focused `IndustryJobsHostWireTests.Registered_industry_manager_routes_keep_client_shapes_and_accept_transport_kwargs` (1 passed, 0 failed, 0 skipped). It sends real Macho CallReq packets, includes transport `machoVersion`, dispatches through `ServiceRegistry` and `PacketDispatcher`, and checks replies plus subsequent state reads without PostgreSQL.
- PASS: Actions run `40179556882` on `c5387254` completed successfully with no failed tests or jobs.
- PASS: Actions run `40179555776` on `c5387254` completed successfully with no failed tests or jobs.
- Existing monitor regression `IndustryMonitorNoticeTests.Host_inventory_dispatch_publishes_changed_materials_once_after_item_change_and_isolates_sessions` was selected in its own Host test dispatch.
- At these successful run summaries, the status tool reported no failed tests but did not include skipped-test totals.
- PASS: push-triggered `ci.yml` run `40179543366` for `c5387254` completed successfully with no failed tests or jobs.
- PostgreSQL and graphical-client sessions were not used for this batch. The persistence implementation has separate PostgreSQL tests; the new wire regression checks the service and transport contract without that lane.
