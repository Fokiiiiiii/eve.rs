using Eve.Characters;
using Eve.Data;
using Eve.Host.ProjectDiscovery;
using Npgsql;

namespace Eve.Host.Tests;

public sealed class ProjectDiscoveryBonusPolicyTests
{
    [Fact]
    public async Task DailyBudgetAndReceiptsAreAtomicAcrossConcurrentSubmissionsAndReset()
    {
        string schema = "pd_bonus_" + Guid.NewGuid().ToString("N")[..12];
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        try
        {
            await Migrations.ApplyAsync(source, schema, PgCharacterStore.MigrationSet().Where(m => m.Name != "0021_project_discovery_bonus_receipts"));
            await using (var existing = source.CreateCommand($"INSERT INTO {schema}.project_discovery_bonus_receipts(character_id,assignment,day,experience) VALUES(99,'legacy','2026-01-01',50)")) await existing.ExecuteNonQueryAsync();
            Assert.Equal(new[] { "0021_project_discovery_bonus_receipts" }, await PgCharacterStore.MigrateAsync(source, schema));
            await using (var existing = source.CreateCommand($"SELECT experience FROM {schema}.project_discovery_bonus_receipts WHERE character_id=99 AND assignment='legacy'")) Assert.Equal(50L, await existing.ExecuteScalarAsync());
            Assert.Empty(await PgCharacterStore.MigrateAsync(source, schema));
            var time = new DateTimeOffset(2026, 1, 2, 5, 59, 0, TimeSpan.Zero);
            var policy = new ProjectDiscoveryBonusPolicy(source, schema, 2, 75, 6, () => time);
            async Task Award(string assignment, bool commit = true)
            {
                await using var conn = await source.OpenConnectionAsync(); await using var tx = await conn.BeginTransactionAsync();
                await policy.AwardAsync(conn, tx, 42, assignment, default);
                if (commit) await tx.CommitAsync();
            }
            await Award("rolled-back", false);
            Assert.Equal(2, await policy.ReadAsync(42, "get_remaining_bonus_samples", default));
            await Task.WhenAll(Award("a"), Award("b"), Award("c"));
            Assert.Equal(0, await policy.ReadAsync(42, "get_remaining_bonus_samples", default));
            await Award("overflow");
            await using (var receipts = source.CreateCommand($"SELECT count(*) FROM {schema}.project_discovery_bonus_receipts WHERE character_id=42 AND experience=0")) Assert.Equal(2L, await receipts.ExecuteScalarAsync());
            await using (var xp = source.CreateCommand($"SELECT experience FROM {schema}.project_discovery_progress WHERE character_id=42")) Assert.Equal(150L, await xp.ExecuteScalarAsync());
            time = time.AddMinutes(1);
            Assert.Equal(2, await policy.ReadAsync(42, "get_remaining_bonus_samples", default));
            await Award("overflow"); // An assignment processed without a bonus cannot consume the next day's allocation.
            Assert.Equal(2, await policy.ReadAsync(42, "get_remaining_bonus_samples", default));
            await Task.WhenAll(Award("a"), Award("b"), Award("c")); // A prior assignment cannot consume tomorrow's allocation.
            Assert.Equal(2, await policy.ReadAsync(42, "get_remaining_bonus_samples", default));
            await Award("d");
            Assert.Equal(1, await policy.ReadAsync(42, "get_remaining_bonus_samples", default));
            await using (var xp = source.CreateCommand($"SELECT experience FROM {schema}.project_discovery_progress WHERE character_id=42")) Assert.Equal(225L, await xp.ExecuteScalarAsync());
            time = time.AddDays(1).AddMinutes(-1);
            await using var first = await source.OpenConnectionAsync();
            await using var firstTx = await first.BeginTransactionAsync();
            Assert.Equal(75L, await policy.AwardAsync(first, firstTx, 43, "boundary", default));
            time = time.AddMinutes(1);
            await using var second = await source.OpenConnectionAsync();
            await using var secondTx = await second.BeginTransactionAsync();
            var replay = policy.AwardAsync(second, secondTx, 43, "boundary", default);
            await firstTx.CommitAsync();
            Assert.Equal(0L, await replay);
            await secondTx.CommitAsync();
            Assert.Equal(2, await policy.ReadAsync(43, "get_remaining_bonus_samples", default));
        }
        finally { await using var cleanup = source.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE"); await cleanup.ExecuteNonQueryAsync(); }
    }
}
