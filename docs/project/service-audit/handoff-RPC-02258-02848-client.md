# P0 implemented-row client verification, batch 8

## Current state

Client-call and reply-consumer review is recorded in `CLIENT_VERIFIED.md` for all 24 requested rows. Existing real Host wire coverage confirms search.QuickQuery, station reads, stationSvc map/rowset endpoints, and subscriptionMgr replies. Added Host wire tests for search.Query and the populationCap load-slot mapping.

`populationCap.MoveCharacterToNewSystem` remains NO-TEST. Its handler validates the selected system/token and persists the move through `PgCharacterStore`; this database-free batch does not exercise the successful owned-character state transition. Close this row after a real Host test asserts the successful None reply and persisted system transition.

## Verification

- Host wire additions compiled locally. The focused search and populationCap load-slot tests passed locally (1/1 each).
- CharacterInfo test project compiled. Its PostgreSQL test was not run locally because this task forbids database use.
- Full push CI `40264738950` for code SHA `43c99ec67f74` completed successfully; all jobs and the required gate passed.
- Focused Agent Actions `40268618319` on final source SHA `ba472355e9b3` passed the two Host wire tests. Earlier focused run `40267908952` also passed on `28fbd361`.
- Dispatch `40267317568` failed before executing tests because its filter matched zero tests; it is not counted as a pass.
- The first push exposed five tests in merged concurrent work; their targeted reruns passed in runs `40264236989`, `40264754322`, and `40265661762`.
- No graphical client session or database was used locally.
- Audit-current has no status-evidence update option; the audit CSV is unchanged.
- Client-consumer evidence and verdicts for all 24 rows are in `CLIENT_VERIFIED.md`; batch changes and this handoff are pushed to main.

## Files

- `dotnet/tests/Eve.Host.Tests/SearchQueryHostWireTests.cs`
- `dotnet/tests/Eve.Host.Tests/PopulationCapHostWireTests.cs`
- `docs/project/service-audit/CLIENT_VERIFIED.md`
- `docs/project/service-audit/handoff-RPC-02258-02848-client.md`
