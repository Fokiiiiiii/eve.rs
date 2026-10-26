# P0 implemented-row client verification, batch 8

## Current state

Client-call and reply-consumer review is recorded in `CLIENT_VERIFIED.md` for all 24 requested rows. Existing real Host wire coverage confirms search.QuickQuery, station reads, stationSvc map/rowset endpoints, and subscriptionMgr replies. Added Host wire tests for search.Query and the populationCap load-slot mapping.

`populationCap.MoveCharacterToNewSystem` remains NO-TEST. Its handler validates the selected system/token and persists the move through `PgCharacterStore`; this database-free batch does not exercise the successful owned-character state transition. Close this row after a real Host test asserts the successful None reply and persisted system transition.

The push-triggered CI run for `30f44c84` exposed three unrelated failures inherited from the merged charMgr work: wire string tags in `CharMgrClientReadHostWireTests`, a stale RPC inventory total, and a member-editor test expecting Rowset instead of the implemented `util.Row`. The fixes are staged for the next commit: preserve `util.Row` and check wire string text independent of bytes/unicode representation, sync the inventory total to 1423 registered rows, and assert the actual row object shape.

## Verification

- Host test project builds cleanly; focused local `Char_mgr_client_read_shapes_match_the_registered_host_wire_contract` passes (1/1).
- CharacterInfo test project builds cleanly. Its PostgreSQL test was not run locally because this task forbids database use.
- No graphical client session or database was used.
- Audit-current has no status-evidence update option; the audit CSV is unchanged.
- Commit `e54fb4e1` was merged with `origin/main` and pushed as `30f44c84`; push CI failed on the three issues above.
- Agent test run `40258937083` passed the two batch 8 focused tests.

## Files

- `dotnet/tests/Eve.Host.Tests/SearchQueryHostWireTests.cs`
- `dotnet/tests/Eve.Host.Tests/PopulationCapHostWireTests.cs`
- `docs/project/service-audit/CLIENT_VERIFIED.md`
- `docs/project/service-audit/handoff-RPC-02258-02848-client.md`
