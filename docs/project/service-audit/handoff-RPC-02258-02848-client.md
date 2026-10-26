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


## Batch 18 — implemented-row client verification (2026-10-26)

Appended all 30 requested rows RPC-02542 through RPC-02617 to `CLIENT_VERIFIED.md`. Confirmed the whole-tree client callsites/consumers, Host handlers and existing wire assertions, and EveJS 261018.1 handlers for the mapped rows.

Outstanding NO-TEST rows: RPC-02577–02582 (four StandingMgr methods) have no populated registered Host reply-shape test; RPC-02586–02587 (station.GetGuests) only have an empty-list Host assertion, not the four-integer tuple rows the client unpacks. The StandingMgr handlers depend on the PostgreSQL-backed Rowset Read Side; the station guest handler loads character state through PgCharacterStore. This task prohibits database use, so those success shapes were not exercised and no test was added. These rows remain open. The remaining rows match existing registered Host wire tests, including populated station map, station record/tuple, interference, slash dispatch, and asset-safety rows.

No code changed, no database or graphical client was used, and no tests were run in this docs-only update. Follow up by adding populated registered Host tests for the six rows above under an approved non-local database test lane.


### Batch 18 local verification update

After merging current origin/main, `dotnet test dotnet/tests/Eve.Host.Tests/Eve.Host.Tests.csproj -p:UseSharedCompilation=false` built the Host test project, then ended with 466 passed, 101 failed, and 227 skipped (794 total). Failures include tests that require `EVE_PG_URL`; no PostgreSQL database was used, as this task prohibits it. Skips are not counted as passing. The new upstream `StandingMgrHostWireTests.cs` covers an empty transaction list and cached empty CRowset shape, but does not assert populated row fields; the six NO-TEST rows recorded above remain open.


The focused non-database registered Host test `Standing_history_and_composition_keep_client_wire_shapes_through_host_dispatch` passed locally (1 passed, 0 failed, 0 skipped). It covers empty transaction/composition results only, not populated client rows.


### Post-merge rerun

After the next `origin/main` merge, the full Host test project built but finished with 468 passed, 101 failed, and 227 skipped (796 total); database/SDE environment tests still fail or skip without their assigned lane. No PostgreSQL database was used. The focused `Standing_history_and_composition_keep_client_wire_shapes_through_host_dispatch` test passed again (1 passed, 0 failed, 0 skipped).


### Pushed verification

Docs-only audit commit `ebe5c1be91b7` is on main. Agent Actions run `40334870781` succeeded for the focused Host test (1 passed, 0 failed, 0 skipped). `python3 tools/dev/ci-status.py failures ebe5c1be91b7b1b1aff71d9ce3e4e080f45b3118` reported no push-triggered `ci.yml` run for that SHA.


Final audit documentation SHA `da5ebf2e9326` also passed Agent Actions run `40335825708` for the focused Host test (1 passed, 0 failed, 0 skipped); no failed jobs/tests. `ci-status.py failures` found no `ci.yml` run for that SHA.


### Final merge verification

The post-merge source SHA `904ff3b09dab` passed Agent Actions run `40337503102` for the focused Host test (1 passed, 0 failed, 0 skipped); there were no failed jobs. `ci-status.py failures` found no `ci.yml` run for that SHA. Rust `cargo +1.94.1 fmt --all -- --check` and `CARGO_BUILD_JOBS=2 CARGO_TARGET_DIR=target-cx cargo +1.94.1 clippy --workspace --all-targets -- -D warnings` both passed.


### Latest main verification

After push/merge through `c1c71676`, the local focused Host test passed (1/1). Agent Actions run `40339412767` completed successfully with no failed jobs/tests; the wait script reported the requested ref `c1c71676` and run SHA `e75bbced6d1f`. `ci-status.py failures c1c71676` found no push-triggered `ci.yml` run.


### Batch 18 completion update

Added populated registered Host wire assertions for StandingMgr rowsets, standing transactions, and station guests. StandingMgr's read-side dependency now accepts the shared Rowset Read Side interface, with a validated RowSet factory for test/provider rows; station guests accept an injectable async source while preserving the existing PostgreSQL-backed path. No database was accessed.

Focused tests after the changes: 2 passed, 0 failed, 0 skipped (`Standing_rowsets_and_history_use_client_consumed_wire_shapes_through_host_dispatch` and `Station_guests_return_the_four_integer_client_row_through_host_dispatch`). Full Host project after changes: 470 passed, 102 failed, 227 skipped (799 total). The failures include existing database/SDE lane requirements; no PostgreSQL database was started or accessed. The six rows now have registered Host shape assertions. Rust was not changed.

### Batch 18 push verification

Commit `ec0be07abc90` was pushed to `main` after merging `origin/main`. Agent Actions run `40343246448` passed the two focused tests (no failed jobs/tests). The push-triggered `ci.yml` run `40343217296` was still in progress at handoff update; check it with `python3 tools/dev/ci-status.py failures ec0be07a` and address any failures at root cause.
