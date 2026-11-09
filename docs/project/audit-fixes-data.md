# Eve.Data audit findings and fixes

This record covers the confirmed findings in `/home/user/reports/90-audit-data-r15.md`.
Both findings were reproducible and fixed; no finding was dismissed as false.

## P2: quote schema identifiers in migrations

`Migrations.ApplyAsync` accepted lowercase PostgreSQL keywords such as `select`, then used the
schema name as an unquoted identifier for schema creation, migration records, and `search_path`.
The new PostgreSQL-backed regression test first failed with a syntax error near `select`.
`Migrations.ApplyAsync` now uses the shared `Sql.QuoteSchema` helper for every schema identifier
in those statements. It still validates the allowed schema syntax before opening a connection.
The test applies and reapplies a migration under the reserved schema `select`, reads its migration
record through `Sql.In`, and drops the schema in `finally`.

## P3: validate and quote `Sql.In` substitutions

`Sql.In` previously interpolated its schema argument with `string.Replace`, even though its public
contract promised validation. The new DB-backed regression test verifies an ordinary schema
substitution against a real migrations table and asserts that a malformed identifier is rejected
before SQL can be returned. Before the fix, the malformed-schema assertion failed because no
exception was thrown. `Sql.QuoteSchema` now calls `CheckSchema` and returns a quoted PostgreSQL
identifier; both `Sql.In` and migrations use this shared helper.

## Client and reference checks

The V24.01 client path was checked across the full `/home/user/eve-logic` source tree. For example,
`eve/client/script/ui/services/accountsvc.py:108-130` invokes `GetTransactions`, the personal and
corporate wallet panels call the account service, and
`eve/client/script/ui/shared/neocom/wallet/panels/transactionsPanel.py:126-145` consumes transaction
rows. EveJS v0.12.9 implements that RPC at
`/home/user/evejs-261028.1/server/src/services/account/accountService.js:882-912`.
Neither client-visible path exposes PostgreSQL schema names. EveJS's corresponding persistence is
SQLite (`server/src/gameStore/sqliteStore.js:56-61`), so its table-name validation is not the same
SQL API. The changes are internal PostgreSQL identifier handling and do not alter RPCs, wire rows,
gameplay state, or notifications. This is consistent with
`docs/design/adr/0001-external-compatibility-only.md:20-35`, which allows internal persistence to
be redesigned.

The report noted no current client-controlled route to an unchecked `Sql.In` argument. That remains
the case based on the call-site review; the P3 fix protects the shared public helper contract even
without evidence of a remote input path.

## Verification

The two new database tests were each run red before their corresponding fixes. After the fixes:

- Release builds of `dotnet/src/Eve.Data/Eve.Data.csproj` and
  `dotnet/tests/Eve.Identity.Tests/Eve.Identity.Tests.csproj` completed with 0 warnings and 0 errors.
- `Eve.Identity.Tests.MigrationsTests`: 9 passed, 0 failed, 0 skipped.
- `Eve.Characters.Tests.StoreTests`: 32 passed, 0 failed, 0 skipped.
- `Eve.Corporations.Tests.CorporationProtocolTests`: 55 passed, 0 failed, 0 skipped.
- The focused Host migration slices (`AccountWalletHostRpcTests`, `IndustryJobPersistenceTests`,
  `ProjectDiscoveryBonusPolicyTests`, `RaffleHostWireTests`, `RafflePersistenceTests`, and
  `TournamentArenaPersistenceTests`): 13 passed, 0 failed, 0 skipped.
- `Eve.Relations.Tests.StandingDirectionMigrationTests`: 1 passed, 0 failed, 0 skipped.
- `Eve.Structures.Tests.StructureStoreMigrationTests`: 1 passed, 0 failed, 0 skipped.
- The focused space-service slices (`UndockContrabandTests` and `PgEssStoreTests`): 18 passed,
  0 failed, 0 skipped.

These database tests used the job's `eve_p8` database. Before the final pre-push rerun, the matching
pinned build-3396210 SDE was loaded directly from the local cache into `eve_p8` (61 tables,
668,618 records). This was test-environment setup only; no SDE files or Rust source changes are part
of this patch.

## Not checked

- No graphical V24.01 client session or running EveJS end-to-end session was available for a live
  comparison; the client and EveJS checks above are source-level call-flow checks.
- No client-controlled path to an invalid schema was found; the P3 change is defense at the helper
  boundary, not a claim of a remotely reachable vulnerability.
- This record does not claim a server-restart persistence scenario or a new gameplay transition.

The repository MCP code-navigation service was unavailable in this environment. Required source
inspection therefore used `rg`, `sed`, and `nl` as a local fallback.
