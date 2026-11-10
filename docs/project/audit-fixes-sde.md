# SDE audit recheck (R15)

## Recheck of `/home/user/reports/90-audit-sde-r15.md`

The report records zero P1–P3 findings and identifies no reproducible defect in `Eve.Sde`. I rechecked its implementation claims before changing anything. No source bug was confirmed, so there is no code fix or new regression test to add; no reported finding was skipped as a false positive because the report contains no findings.

## Verified from the implementation

- `dotnet/src/Eve.Sde/SdeReader.cs:22-26,136-145,176-180,230-235,260-265` uses full-string schema, table, and field-name validation before interpolating identifiers. `ReadCoreAsync` binds key values at `:205-213`; `ReadByFieldAsync` binds field values at `:238-243`; `ReadContainingAsync` binds its JSON value at `:266-271`.
- All three row readers order by `ord` (`SdeReader.cs:205-209,238-240,268-270`), parse numeric keys, and clone each JSON document before disposing it (`:214-223,244-253,272-281`). The importer defines the matching `(ord, key, doc jsonb)` table shape and records `_build` (`crates/eve-sde/src/load.rs:3-6,159-185`).
- Type summaries read `groupID`, English `name`, and `published` in file order (`SdeReader.cs:284-300`). Missing or null `published` maps to `true`, as the method comment specifies. The cache is scoped to each data source and keyed by schema/build/table (and schema/build for summaries); `BuildAsync` invalidates stale builds (`:31-39,120-133,147-169`). Shared loads use a non-caller cancellation token and `WaitAsync`, so cancelling a waiter does not cancel the shared load (`:41-79,81-118,183-195,303-315`).
- Existing database tests pin V24.01 build 3396210 and assert the type-summary count, digest, ordering, fields, and Ibis record (`dotnet/tests/Eve.Characters.Tests/CatalogTests.cs:246-278`). Other tests exercise filtered reads, JSON containment, cache sharing/cancellation, and name validation (`:183-244,280-387`). Those additional tests were inspected but not run in this recheck.

## Client and EveJS comparison

- In the V24.01 client, `eve/client/script/dogma/clientDogmaStaticSvc.py:18-43` obtains type attributes through `dogma_data.get_type_attributes` and falls back to `evetypes.GetMass`, `GetCapacity`, and `GetVolume`. A repository-wide search for these calls found the static client consumers; it did not reveal an RPC contract that exposes `SdeReader` rows directly. The server's named SDE/module-table query and client-rowset boundary is `Eve.ReadSide` (`docs/design/adr/0010-shared-foundations.md:20-24`, `dotnet/src/Eve.ReadSide/ReadSide.cs:114-123`).
- The audit report cites EveJS snapshot `261028.1`; this recheck used the job-specified `/home/user/evejs-261108.1`. `server/src/gameStore/tableOwnership.js:24-28` classifies non-runtime tables as static reference data. `server/src/services/_shared/referenceData.js:336-392` reads the loaded static game store or JSON table and caches the normalized result in process; `server/src/services/_shared/staticJsonTableReader.js:8-40` validates table names and reads the JSON file. This confirms the report's static-data boundary, while not treating EveJS's in-memory layout as an eve.rs architecture requirement.
- The comparison found no client-visible behavior divergence in the audited SDE reader contract. This recheck introduces no intentional EveJS compatibility difference.

## Verification run

- `target/release/eve-sde ensure` loaded pinned build 3396210 into `sde` on both local PostgreSQL databases, `eve_p6` and `eve_p12` (61 tables, 668,618 records each). The archive was downloaded from CCP's official URL recorded for build 3396210 in `sde/lock.json`; the extracted SDE remains ignored under `.cache/sde`.
- `/root/.dotnet/dotnet build dotnet/tests/Eve.Characters.Tests/Eve.Characters.Tests.csproj --configuration Release -m:1` succeeded with 0 warnings and 0 errors.
- `CatalogTests.ReadTypeSummariesAsync_returns_ordered_catalog_fields` passed against `eve_p6` (1 passed, 0 failed, 0 skipped) and `eve_p12` (1 passed, 0 failed, 0 skipped).

## Not checked

- The full `CatalogTests` class and its cache, filtering, and containment cases were not run; only the focused type-summary test ran.
- EveJS source was compared, but an EveJS runtime scenario was not executed. The graphical V24.01 client was not launched.
- No gameplay RPC or persisted player-state flow is owned by `Eve.Sde`; this recheck did not claim end-to-end graphical-client acceptance.

## Reverification (2026-11-10)

- Rechecked the SDE reader and its callers against the current source tree, the V24.01 client, and the job-specified EveJS checkout `/home/user/evejs-261108.1`. The audit report still contains no P1–P3 findings; no source defect was confirmed and no code change or regression test was warranted.
- `eve_p6` contained SDE build 3396210. `eve_p12` was absent and was created as a PostgreSQL template copy of `eve_p6`, so the two databases had the same pinned SDE for this verification.
- `/root/.dotnet/dotnet build dotnet/tests/Eve.Characters.Tests/Eve.Characters.Tests.csproj --configuration Release -m:1` succeeded with 0 warnings and 0 errors.
- `CatalogTests.ReadTypeSummariesAsync_returns_ordered_catalog_fields` passed on `eve_p6` and `eve_p12` separately (1 passed each, 0 failed, 0 skipped).
- Codebase-memory MCP and Serena were unavailable and `codebase-memory-mcp` was not installed; repository search and line-numbered source inspection used `rg`, `nl`, and `sed` as the fallback.
