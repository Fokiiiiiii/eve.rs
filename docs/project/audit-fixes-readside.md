# ReadSide audit fixes (R14)

## P1 — CRowset wire representation (confirmed and fixed)

`RowSet.ToCRowset()` and the empty standings-composition response previously returned a regular `PyObject` with a `lines` dictionary entry. That is not the V24.01 by-value representation for `CRowset`.

Evidence from the V24.01 client:

- `carbon/common/script/sys/crowset.py:10-16` defines `CRowset` as a `list`, sets `__passbyvalue__ = 1`, and initializes the base list from `rows`.
- `eve/client/script/ui/services/standingsvc.py:282-283` calls `standingMgr.GetStandingCompositions`.
- `eve/client/script/ui/shared/neocom/charsheet/standingsPanel/standingsPanel.py:110-111` passes the returned value to the panel; `standingCompositionScroll.py:21-30` iterates it and reads `composition.ownerID`; `standingCompositionEntry.py:27-29` reads `ownerID` and `standing`.

EveJS v0.12.9 has the same client-visible contract: `server/src/services/character/standingMgrService.js:224-241` wraps the composition rows in a cached result, and `_shared/serviceHelpers.js:166-177` builds CRowset as `objectex2`, with the descriptor in the object header and packed rows in its list. `standingRuntime.js:487-519` supplies one `{ownerID, standing}` row per corporation member with that standing and sorts by standing descending, then owner ID.

The server now emits that Ex2 shape from `RowSet.ToCRowset()` and for the empty/unauthorized response. Packed rows from a single CRowset share one descriptor. The Host dispatch regression checks the empty response after marshal encode/decode; the database-backed corporation protocol regression checks two real composition rows, their DBTYPEs and values, and a marshal round trip.

Verified checks:

- Before the fix, both regressions failed at the wire type: actual `PyObject`, expected `PyObjectEx`.
- After the fix, `Standing_history_and_composition_keep_client_wire_shapes_through_host_dispatch` passed (Host socket dispatch, 1 passed, 0 skipped).
- After the fix, `Standing_manager_reads_character_rows_corporation_averages_history_and_npc_relations` passed against `eve_p3` (database-backed, 1 passed, 0 skipped).
- Executed EveJS `buildDbRowset` with one standings composition row; it produced `objectex2` containing one `packedrow` and the descriptor header field.

Not verified: interaction in the graphical V24.01 client or a full EveJS runtime RPC against persisted corporation and standing state. The isolated EveJS rowset builder was executed as noted above.

## P3 — SQL aliases that need quoting (confirmed and fixed)

`ColumnTypes.Word()` in `dotnet/src/Eve.ReadSide/RowsetQuery.cs:50-51` allowed digits in the first position and reserved words. Before the fix, `ReadSide.SqlOf()` emitted those names unquoted after `AS`, so values such as `123name` and `select` could be accepted at registration but produced invalid PostgreSQL SQL.

`SqlOf()` now double-quotes every declared output alias so digits and reserved words remain valid and retain their requested spelling. Because PostgreSQL resolves quoted mixed-case aliases case-sensitively, the existing `ORDER BY` clauses that refer to ReadSide output columns in `StandingMgrService.cs` and `StationService.cs` now quote those aliases too. Existing clauses that refer to underlying table columns remain unchanged.

The regression creates a PostgreSQL table on `eve_p3`, selects two columns aliased `123name` and `select`, orders by both aliases, and verifies the returned values. The test failed before the fix because the generated SQL contained `AS 123name` and `AS select`; after the fix the SQL executed and returned both values. A source search found no numeric aliases in current production registrations.

## P3 — missing `KeyColumn` validation (confirmed and fixed)

`RowsetQuery.KeyColumn` in `dotnet/src/Eve.ReadSide/RowsetQuery.cs:93-94` identifies the key used by indexed and key/value conversions. Before the fix, `ReadSide.Register()` did not ensure that key was among the query's columns. `RowSet.ToKeyValsByKey()` then indexed `Columns[-1]` and raised `IndexOutOfRangeException`; `ToIndexedRowset()` already raised a descriptive `InvalidOperationException`.

`ReadSide.Register()` now rejects a missing key with an `ArgumentException` naming the query and key. Both conversions also use one checked key lookup, so a `RowSet` built directly with the public `RowSet.FromRows()` factory reports the same descriptive `InvalidOperationException` instead of an indexing exception. A source search found no production query setting `KeyColumn`; this is still a confirmed ReadSide API defect and was not skipped.

The two regression tests were run before the fix: registration returned normally instead of rejecting the invalid query, and `ToKeyValsByKey()` raised `IndexOutOfRangeException`. After the fix, all 11 `ReadSideTests` passed, including both new key validation cases and the database-backed alias query.

## Verification after the fixes

- `Eve.Corporations.Tests.CorporationProtocolTests`: 55 passed, 0 skipped, against `eve_p3` after alias quoting and the standings `ORDER BY` update.
- `Eve.Services.Station.Tests.StationServiceTests`: 12 passed, 0 skipped, against `eve_p3`.
- `Eve.Host.Tests.HostServerTests`: 432 passed, 0 skipped, including socket-dispatched standings responses.
- The production `HostComposition` standings registration and alias test: 1 passed, 0 skipped, against `eve_p3`.
- Release builds for `Eve.ReadSide`, `Eve.Services.Corporations`, `Eve.Services.Station` and their affected test projects completed with 0 warnings and 0 errors.

Not verified: graphical-client interaction. Client code and EveJS sources were compared for CRowset; no separate EveJS stateful server session or graphical client run was performed.

## Recheck of `/home/user/reports/90-audit-eve-readside-r14.md` (2026-11-09)

The three findings in that report describe real defects in the earlier implementation, but all three are already fixed in the current `HEAD` (`6aed940a5`). They are not false positives; this recheck did not add duplicate source changes.

- **P1 CRowset wire shape:** `ReadSide.cs:53-60` now returns `PyObjectEx(Ex2)`, with the descriptor in the object header and packed rows in the object's list. `StandingMgrService.cs` uses the same Ex2 shape for the empty composition response. Host dispatch checks the empty response in `Eve.Host.Tests/StandingMgrHostWireTests.cs:12-36`; `CorporationProtocolTests.cs:1616-1712` checks two data rows, the shared row descriptor's DBTYPEs, values, and a marshal round trip. The implementation was fixed in `64788c649`.
- **Client and EveJS contract:** In V24.01, corporation selection reaches `standingsPanel.py:88-98,110-111`, then `standingsvc.py:282-283` calls `standingMgr.GetStandingCompositions`. `standingCompositionScroll.py:21-30` iterates the result, and `standingCompositionEntry.py:20-29` reads each row's `ownerID` and `standing`. `carbon/common/script/sys/crowset.py:10-16` defines CRowset as a pass-by-value list initialized from `rows`. EveJS `standingMgrService.js:224-240` wraps the result in the cache response; `standingRuntime.js:487-519` supplies and sorts the composition records; `_shared/serviceHelpers.js:157-178` emits the Ex2 header and packed rows. This confirms the current wire shape supports the client iteration and fields.
- **P3 SQL aliases:** `ReadSide.SqlOf()` now double-quotes output aliases; affected `ORDER BY` clauses were adjusted in the `e48e14e69` fix. `ReadSideTests.cs:80-111` executes both a numeric-leading alias and the reserved word `select` against PostgreSQL.
- **P3 KeyColumn:** `ReadSide.Register()` now rejects a configured key absent from `Columns`, and both keyed conversions share the checked lookup. `ReadSideTests.cs:53-77` covers registration rejection and consistent conversion errors. This was also fixed in `e48e14e69`.

Recheck verification on 2026-11-09:

- Release builds for `Eve.ReadSide`, `Eve.ReadSide.Tests`, `Eve.Corporations.Tests`, and `Eve.Host.Tests` succeeded with 0 warnings and 0 errors; the latter two builds also compiled `Eve.Services.Corporations` and `Eve.Host`.
- All 11 `Eve.ReadSide.Tests.ReadSideTests` passed against `eve_p4`, including the database-backed alias case (0 skipped).
- `HostServerTests.Standing_history_and_composition_keep_client_wire_shapes_through_host_dispatch` passed (1 test, 0 skipped).
- `CorporationProtocolTests.Standing_manager_reads_character_rows_corporation_averages_history_and_npc_relations` passed against `eve_p4` (1 test, 0 skipped).
- `StandingMgrServiceTests.Standing_history_is_scoped_to_the_active_character_and_corporation` passed against `eve_p3` (1 test, 0 skipped).

Not verified in this recheck: GUI-client interaction, a complete stateful EveJS server session, or the full CI workflow. The code and client protocol were checked from source, and the focused Host dispatch test exercised the registered RPC and encoded response.
