# Eve.Services.Core audit fixes

This note records the verified findings from `/home/user/reports/90-audit-eve-services-core-r14.md` and their Host-level coverage.
The codebase-memory-mcp and Serena tools were unavailable, so source tracing used targeted local reads and searches. The V24.01 client and
EveJS files named below were inspected directly.

## P1 — `ingamereport` acknowledged reports without storing them

**Verified behavior.** V24.01 `eve/client/script/ui/shared/zhReport/reportSvc.py:36-52` sends eight report types. The inappropriate-language
call includes target, comments, time, channel and chat content. The method returns its local success string after the RPC call. EveJS
`server/src/services/support/ingamereportService.js:61-120` records the report type, reporter character/account, target, comments and
language details in `auditReports` before returning `null`; that reference list is process-local. Before this fix, eve.rs accepted a nullable
callback in `dotnet/src/Eve.Services.Core/UnportedSupportRpcServices.cs` and returned `PyNone` when it was missing, while the production
call in `dotnet/src/Eve.Host/Hosting/HostComposition.cs` did not provide one.

**Fix.** The RPC validates the client argument count, target ID, comments and language details. It derives reporter IDs from the session and
requires a configured sink. Missing storage returns `INGAME_REPORT_STORAGE_UNAVAILABLE`; persistence errors propagate. Production Host wiring
now migrates and uses `support.ingame_reports` (overridable with `EVE_SUPPORT_SCHEMA`). Each accepted report is inserted with its type, method,
reporter account/character, target, comments, optional chat fields and database receipt time. The RPC returns `PyNone` only after the insert.
The database table is the operator-accessible inbox; no new client-facing support console was added.

**Regression evidence.** Before the fix, `In_game_report_without_a_sink_does_not_acknowledge_a_lost_report` failed because no exception was
thrown. The PostgreSQL Host test also observed no argument error for an invalid target. After the fix, `InGameReportPersistenceHostTests` sends all
eight client shapes, checks stored report details, rejects malformed input, runs four concurrent submissions, restarts the real Host and
checks the records remain. `Support_and_insurgency_rpcs_return_client_shapes_through_host_registration` exercises the wire reply and report
callback through `HostServerTests`.

## P2 — `communityTrackMgr` had no production source for instance attributes

**Verified behavior.** V24.01 `communitytracks/client/repository.py:82-93` calls `get_instance_attributes_public` and requires both returned
values. The Vanguard Nemesis page calls this path with instance ID `1337` and stops if the record is absent
(`seasons/client/vanguardnemesis/communityprogressbar.py:154-166`); the client constants declare definition ID `1`
(`communitytracks/common/const.py:7-8`). eve.rs held attributes in a service-local dictionary writable only through a test setter. EveJS
`server/src/services/activity/communityTrackMgrService.js:75-93,106-136` also had a test-only setter and no production producer.
Searching all Python sources in `/home/user/eve-logic` found client initialization, the public read, the Vanguard UI and completion notice,
and the QA mock (`communitytracks/client/mock/mock_gateway.py:23-27`, which returns a test progress of 12); it found no producer that writes
live instance progress. The bundled SDE directory has no community-track template data from which to derive a runtime instance or progress.

**Fix status: open; no code change.** A local prototype store and Host test briefly demonstrated that a separately seeded PostgreSQL row could
be read, but final tracing found no production caller that creates or advances such rows. Keeping that store would add an unreachable second
state owner and would leave the live RPC returning `(None, None)`. No authoritative runtime source exists in this repository, the V24 client,
EveJS, or the pinned SDE from which to create instance `1337` or derive its progress. The prototype and its test were removed; do not seed
definition `1` with a guessed progress value or copy the QA mock's value `12`.

**Regression evidence and limit.** Before the prototype, the Host test for an expected persisted instance failed because the registered route
returned `(None, None)`. No post-fix test is claimed for P2 because the prototype was removed. A production Vanguard Nemesis event producer
that creates or advances instance `1337` remains necessary. The client-facing event screen has not been exercised in the graphical client.

## Verification

The EveJS report handler was executed directly with reporter 42, account 77, target 9001 and the five-argument language report; its testing
view contained those IDs and all three chat fields. The EveJS logger's optional `picocolors` dependency was intercepted with an in-process
no-op logger for this check; the handler code ran unchanged.

Release builds completed with 0 warnings and 0 errors for `Eve.Services.Core`, `Eve.Host`, `Eve.Services.Core.Tests`, and `Eve.Host.Tests`
using `dotnet build <project> -c Release -warnaserror --no-restore`. Regression tests, all with 0 skipped, passed:

- `dotnet test dotnet/tests/Eve.Services.Core.Tests/Eve.Services.Core.Tests.csproj -c Release --no-build --filter 'FullyQualifiedName~UnportedSupportRpcTests|FullyQualifiedName~CompatibilityRpcServicesTests'` — 15 passed.
- `EVE_PG_URL=postgres://postgres@127.0.0.1:5432/eve_p3 dotnet test dotnet/tests/Eve.Host.Tests/Eve.Host.Tests.csproj -c Release --no-build --filter 'FullyQualifiedName~InGameReportPersistenceHostTests|FullyQualifiedName~Support_and_insurgency_rpcs_return_client_shapes_through_host_registration|FullyQualifiedName~Client_calls_for_chat_community_offers_employment_and_agency_cross_host_wire'` — 3 passed, 0 skipped; the PostgreSQL test covers all report shapes, concurrent submissions and persistence across restart.
- `EVE_PG_URL=postgres://postgres@127.0.0.1:5432/eve_p3 dotnet test dotnet/tests/Eve.Host.Tests/Eve.Host.Tests.csproj -c Release --no-build --filter 'FullyQualifiedName~HostServerTests'` — 412 passed.

No finding in the two-item audit was judged false. P1 is fixed. P2 remains open because its authoritative event producer is absent; no
graphical V24.01 client session was run.
