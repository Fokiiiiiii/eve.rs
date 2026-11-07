# Characters migration regression test ordering

## Cause

Main CI run 1594 at `73b9b3b38727832eeb10131a659e164e9201aab6` failed
`Eve.Characters.Tests.StoreTests.Home_station_cooldown_migration_preserves_existing_rows` at the
second applied-migration assertion. Its expected array ended at
`0022_character_deletion_corporation`; PostgreSQL reported the actual applied list also contained
`0023_character_drone_settings`. This is a stale, hand-maintained test expectation, not a clock or
database race. The failure log shows the exact mismatch at `StoreTests.cs:196`.

The same assertion was previously updated in commit `c270580d` to add
`0021_project_discovery_bonus_receipts`, then again in `e7c133a0` to add
`0023_character_drone_settings`. That history is consistent with the migration list growing
without a matching test-array edit; it does not establish a timing-dependent failure. Run 1594's
failure is deterministic for source revision `73b9b3b3`: its expected array omitted a migration
that its `MigrationSet` applied.

The old test also used `MigrationSet().Take(3)` before the migration runner's ordinal sort. The
embedded migration reader (`Eve.Data.Migrations.Embedded`) returns manifest resources without an
ordering guarantee, while `ApplyAsync` sorts by migration name. Sorting the set before selecting
the legacy prefix makes the pre-migration fixture explicit and stable. The test now derives the
remaining expected migration names from that sorted set, so adding a migration cannot recreate the
same stale-array failure. It still asserts that the legacy prefix is exactly migrations 0001–0003,
that all subsequent migrations apply in order, that the legacy row retains its home station with a
zero cooldown, and that a second migration call applies nothing.

## Evidence

- `dotnet/src/Eve.Characters/Migrations/0004_remote_home_station_cooldown.sql:1-3` adds the
  cooldown with `NOT NULL DEFAULT 0`; it contains no current-time calculation.
- `dotnet/src/Eve.Data/Migrations.cs:29,74-80`: `Embedded` enumerates embedded resources;
  `ApplyAsync` sorts names with `StringComparer.Ordinal` and records applied migrations in the
  module schema.
- `dotnet/tests/Eve.Characters.Tests/Support/PgSupport.cs:27-39,64-72`: each test gets a
  GUID-suffixed Characters schema, so parallel test methods have isolated schemas.
- `dotnet/tests/Eve.Characters.Tests/StoreTests.cs:180-206` covers migration of an existing
  character and the returned migration sequence.
- Run 1594's .NET shard had 128 passed and 1 failed Characters tests, with 0 skipped. The failure
  was the expected/actual migration-name mismatch above.

This is a storage regression test; it changes no gameplay or client protocol behavior. No V24.01
client or EveJS gameplay comparison is applicable to the migration-list assertion.

## Verification

- Release build of `dotnet/tests/Eve.Characters.Tests/Eve.Characters.Tests.csproj`: 0 warnings,
  0 errors.
- Re-ran the focused test from historical snapshot `73b9b3b3` on `eve_p6`: reproduced the CI
  failure exactly (`0023_character_drone_settings` was actual but absent from the expected array).
- Focused migration test, 30 sequential runs on `eve_p6`: 30 passed, 0 failed, 0 skipped.
- Focused test concurrent with the other Characters tests on `eve_p6`: 1 passed; the other 128
  tests passed; 0 skipped.
- Full `Eve.Characters.Tests` on `eve_p6`: 129 passed, 0 failed, 0 skipped.
- These are headless PostgreSQL tests. The graphical EVE client was not launched; it is not needed
  to validate this migration test correction.
