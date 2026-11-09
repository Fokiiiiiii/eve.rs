# ReadSide audit R14 recheck on cx4

Reviewed the audit report against the current `cx4` checkout on 2026-11-09. All three findings described real defects in the earlier source, but their fixes are already ancestors of this checkout: `64788c649` fixes CRowset encoding and `e48e14e69` validates aliases and key columns. No additional production code change was needed for this recheck. The earlier implementation notes remain in `audit-fixes-readside.md`; this file records the current-source verification and tests run here.

## P1 — CRowset by-value wire shape: already fixed

`RowSet.ToCRowset()` now returns `PyObjectEx(ObjectExKind.Ex2)`, puts the CRowset class and descriptor in the object header, and puts packed rows in the Ex2 list (`dotnet/src/Eve.ReadSide/ReadSide.cs:52-73`). The empty standings response uses the same Ex2 contract (`dotnet/src/Eve.Services.Corporations/StandingMgrService.cs:231-243`). `GetStandingCompositions` reads rows for a valid corporation member and wraps the resulting CRowset in the existing five-minute cached method result (`StandingMgrService.cs:208-222`); `CorporationRpc.cs:77` registers the service in the Host.

The V24.01 client defines `CRowset` as a list with `__passbyvalue__ = 1` and initializes that list from its rows (`/home/user/eve-logic/carbon/common/script/sys/crowset.py:10-16`). The full-client search found the standings service wrapper (`eve/client/script/ui/services/standingsvc.py:282-283`), the panel call (`standingsPanel.py:110-111`), and the consumer loop (`standingCompositionScroll.py:21-30`), which iterates composition rows and reads `ownerID`. EveJS uses the same Ex2 header/list form in `_shared/serviceHelpers.js:166-177`; its `standingMgrService.js:224-241` returns that rowset in a cached result.

Existing regression coverage checks Host-dispatched empty composition wire shape and a database-backed successful composition response with two packed rows and a marshal round trip (`Eve.Host.Tests/StandingMgrHostWireTests.cs:12-42`, `Eve.Corporations.Tests/CorporationProtocolTests.cs:1615-1713`). The successful data-path test invokes the service directly; the separate Host test exercises socket dispatch. Production Host registration was verified in source. No graphical-client run was performed.

## P3 — numeric and reserved SQL aliases: already fixed

`ReadSide.SqlOf()` now double-quotes every declared output alias (`dotnet/src/Eve.ReadSide/ReadSide.cs:162-181`). Since accepted alias names contain only ASCII letters, digits, and underscores, they cannot inject a quote into the emitted identifier. The existing PostgreSQL regression creates aliases `123name` and `select`, orders by them, and reads the result (`dotnet/tests/Eve.ReadSide.Tests/ReadSideTests.cs:79-107`). This directly covers the audit's delayed SQL failure.

## P3 — missing `KeyColumn`: already fixed

`ReadSide.Register()` now rejects a configured key absent from `Columns` with an `ArgumentException` naming both the query and key (`dotnet/src/Eve.ReadSide/ReadSide.cs:136-155`). Both keyed conversions use `FindKeyColumn()`, which raises a descriptive `InvalidOperationException` instead of indexing at `-1` (`ReadSide.cs:75-105`). Tests cover registration rejection and consistent conversion errors (`ReadSideTests.cs:52-77`).

## Verification run for this recheck

- `Eve.ReadSide.Tests`: the quoted-alias PostgreSQL test and both key-column tests passed against `eve_p4` (3 passed, 0 skipped), then against `eve_p3` (3 passed, 0 skipped).
- `Eve.Corporations.Tests`: the successful standings-composition integration test passed against `eve_p4` (1 passed, 0 skipped), then against `eve_p3` (1 passed, 0 skipped).
- `Eve.Host.Tests`: the two selected standing wire-shape tests passed through Host socket dispatch (2 passed, 0 skipped).
- These Release test invocations built their projects and dependencies without reported warnings or errors. Both configured PostgreSQL databases were present and usable.

## Not checked

- I did not run the graphical V24.01 client or a full EveJS server session in this recheck. The client and EveJS source call paths were inspected; the previous project record documents an isolated EveJS builder execution.
- Codebase Memory and Serena MCP tools were unavailable, and `codebase-memory-mcp` was not installed. I used read-only `git show`/`git blame`, `rg`, and line-numbered source views as the fallback.
- No ReadSide production source was changed in this recheck. The worktree had pre-existing unrelated edits; they were left untouched.
