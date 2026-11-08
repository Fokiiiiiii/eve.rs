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
