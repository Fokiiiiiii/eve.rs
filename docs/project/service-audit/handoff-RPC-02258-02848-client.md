# P0 implemented-row client verification, batch 8

## Current state

Client-call and reply-consumer review is recorded in `CLIENT_VERIFIED.md` for all 24 requested rows. Existing real Host wire coverage confirms search.QuickQuery, station reads, stationSvc map/rowset endpoints, and subscriptionMgr replies. Added Host wire tests for search.Query and the populationCap load-slot mapping.

`populationCap.MoveCharacterToNewSystem` remains NO-TEST. The current handler validates the selected system/token and persists the move through `PgCharacterStore`; this batch is database-free, so the successful owned-character state transition has not been exercised. Do not close this row until a real Host test asserts the successful None reply and persisted system transition.

## Verification

- `dotnet build dotnet/tests/Eve.Host.Tests/Eve.Host.Tests.csproj --no-restore -m:1 -p:UseSharedCompilation=false` passed.
- Focused local test `Search_query_returns_group_keyed_result_lists_through_host_with_transport_kwargs` passed (1/1).
- Population load-slot wire test compiled but has not been run locally.
- No graphical client session or database was used.
- Audit-current does not expose a status-evidence option; the audit CSV is unchanged.

## Files

- `dotnet/tests/Eve.Host.Tests/SearchQueryHostWireTests.cs`
- `dotnet/tests/Eve.Host.Tests/PopulationCapHostWireTests.cs`
- `docs/project/service-audit/CLIENT_VERIFIED.md`
