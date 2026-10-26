# P0 implemented-row client verification, batch 8

## Current state

Client-call and reply-consumer review is recorded in `CLIENT_VERIFIED.md` for all 24 requested rows. Existing real Host wire coverage confirms search.QuickQuery, station reads, stationSvc map/rowset endpoints, and subscriptionMgr replies. Added Host wire tests for search.Query and the populationCap load-slot mapping.

`populationCap.MoveCharacterToNewSystem` remains NO-TEST. Its handler validates the selected system/token and persists the move through `PgCharacterStore`; this database-free batch does not exercise the successful owned-character state transition. Close this row after a real Host test asserts the successful None reply and persisted system transition.

Push CI for `30f44c84` exposed three failures from the merged charMgr work: wire string comparison, an outdated registry count (1442 vs 1423), and a member editor test comparing `util.Row` to Rowset. The fixes are committed as `1376ec5b`; merge `8ec5a681` preserved the concurrent main updates, including its stronger full two-row and text-normalization wire assertions. The merged changes are ready to push; Actions verification is pending.

## Verification

- Before the merge update, the Host test project compiled and the focused `Char_mgr_client_read_shapes_match_the_registered_host_wire_contract` passed (1/1).
- CharacterInfo test project compiled. Its PostgreSQL test was not run locally because this task forbids database use.
- Earlier batch 8 Agent test run `40258937083` passed both new batch tests.
- No graphical client session or database was used.
- Audit-current has no status-evidence update option; the audit CSV is unchanged.
- Batch 8 was pushed as `30f44c84`; its push CI failed as recorded above.

## Files

- `dotnet/tests/Eve.Host.Tests/SearchQueryHostWireTests.cs`
- `dotnet/tests/Eve.Host.Tests/PopulationCapHostWireTests.cs`
- `docs/project/service-audit/CLIENT_VERIFIED.md`
- `docs/project/service-audit/handoff-RPC-02258-02848-client.md`
