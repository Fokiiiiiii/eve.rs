# P0 implemented-row client verification, batch 8

## Current state

Client-call and reply-consumer review is recorded in `CLIENT_VERIFIED.md` for all 24 requested rows. Existing real Host wire coverage confirms search.QuickQuery, station reads, stationSvc map/rowset endpoints, and subscriptionMgr replies. Added Host wire tests for search.Query and the populationCap load-slot mapping.

`populationCap.MoveCharacterToNewSystem` remains NO-TEST. Its handler validates the selected system/token and persists the move through `PgCharacterStore`; this database-free batch does not exercise the successful owned-character state transition. Close this row after a real Host test asserts the successful None reply and persisted system transition.

## Verification

- Host wire additions compiled; the focused search and populationCap load-slot tests passed locally (1/1 each).
- CharacterInfo test project compiled. Its PostgreSQL test was not run locally because this task forbids database use.
- Focused Agent Actions run `40263434792` passed the charMgr client-read wire contract, registry inventory, and private-info row shape checks.
- Push CI `40264738950` for `43c99ec67f74` has passed every job reported so far except `.NET (host tests 4/4)`, which is still running as of this note. All other host shards, all space shards, Rust, Python, build, Windows, and end-to-end jobs have passed. The first push exposed five unrelated tests in merged concurrent work; their targeted reruns passed in runs `40264236989`, `40264754322`, and `40265661762`.
- No graphical client session or database was used locally.
- Audit-current has no status-evidence update option; the audit CSV is unchanged.
- Batch 8 and subsequent corrections/merges are on main through `6f3b6291`. The client-consumer evidence and verdict table is in `CLIENT_VERIFIED.md`.

## Files

- `dotnet/tests/Eve.Host.Tests/SearchQueryHostWireTests.cs`
- `dotnet/tests/Eve.Host.Tests/PopulationCapHostWireTests.cs`
- `docs/project/service-audit/CLIENT_VERIFIED.md`
- `docs/project/service-audit/handoff-RPC-02258-02848-client.md`
