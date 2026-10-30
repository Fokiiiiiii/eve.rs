# Protocol audit handoff

## Changes

- `CorpRegistryService.GetMembersByIds` removes the 500-ID lookup ceiling. It queries persisted corporation members and characters in 500-ID batches, so each database query stays bounded and no per-ID reads are issued.
- `ReprocessingService.Reprocess` accepts corporation output only when the requested owner is the pilot's non-NPC corporation, an active office is present at the bound station, the requested output location is that office, the division flag is 115–121, and the pilot has the station-scoped Hangar Query role. Produced inventory rows and `OnItemChange` notifications use the corporation owner. `StationRpc` wires the corporation and office stores.
- `IndustryJobsService.WireField` reads nested payload fields from `PyDict`, `PyObject.Args` dictionaries, and `PyObjectEx.Dict`. Its registered Host wire test asserts `inputLocation` and `outputLocation` item and flag values for these forms.
- The client-side multi-source reprocessing unpack remains a client limitation. `itemReprocessor.py:18-46,69-70` aggregates per-source RPC results with `extend`; `controller.py:78-81` unpacks two values. Zero groups issue no RPC, and 2+ group results flatten past two values. The server cannot infer the number of future per-group calls. EveJS 261028.1 returns one pair per positive source location (`server/src/services/station/reprocessingService.js:673-764`), so no server-only wire change repairs that client code. See `docs/reference/compat/DEVIATIONS.md`.

## Client and EveJS evidence

- `eve/client/script/ui/services/corporation/bco_members.py:31-39` sends all uncached IDs in one `GetMembersByIds` call; `:73` also has the single-ID caller. `eve/client/script/ui/shared/neocom/corporation/corp_ui_member_view_role_mgt.py:138-179` passes the complete sorted member list. EveJS 261028.1 `server/src/services/corporation/corpRegistryRuntime.js:2784-2794` maps the requested IDs against corporation membership without a fixed 500-ID cap.
- `eve/client/script/ui/reprocessing/ui/reprocessingWnd.py:149-165,344-368` offers corporation divisions only when an office is present; `itemReprocessor.py:18-46` chooses `session.corpid` for division output and sends the office ID and flag. EveJS 261028.1 `server/src/services/station/reprocessingService.js:673-764` delegates corporate authorization and output ownership to its runtime.
- `eve/client/script/industry/industrySvc.py:139-146` sends `job.dump()` to `InstallJob`; `eve/client/script/industry/job.py:623-640,683-697` encodes nested `Location` instances. EveJS 261028.1 `server/src/services/industry/industryManagerService.js:91-95` calls `parseIndustryRequest`; `server/src/services/industry/industryPayloads.js:265-303` parses the nested location object forms.

## Verification

- `MSBUILDDISABLENODEREUSE=1 dotnet build dotnet/tests/Eve.Host.Tests/Eve.Host.Tests.csproj --no-restore -p:UseSharedCompilation=false` passed.
- `MSBUILDDISABLENODEREUSE=1 dotnet build dotnet/tests/Eve.Corporations.Tests/Eve.Corporations.Tests.csproj -p:UseSharedCompilation=false` passed.
- Focused registered Host test `Registered_industry_manager_routes_keep_client_shapes_and_accept_transport_kwargs`: passed, 1/1, 0 skipped.
- No PostgreSQL database was used. Corporation protocol and reprocessing persistence cases require the Actions database lane; the graphical V24.01 client was not run.

## Continuation

The local uncommitted `SkillHandlerRpcHostTests.cs` change was present before this task and must be kept out of this task's commit. Before pushing, inspect CI status; fetch and merge `origin/main`, rerun the focused test/builds, then push only with `git push origin HEAD:main`. After push, run the requested Agent Test shard and inspect both that run and the push-triggered CI run. Update this note after each push.
