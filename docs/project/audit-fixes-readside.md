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
