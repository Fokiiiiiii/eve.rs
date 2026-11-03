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
            await using (var setup = source.CreateCommand($"CREATE SCHEMA {schema}; CREATE TABLE {schema}.project_discovery_bonus_days(character_id bigint, day date, used integer, PRIMARY KEY(character_id,day)); CREATE TABLE {schema}.project_discovery_bonus_receipts(character_id bigint, assignment text, day date, experience bigint, PRIMARY KEY(character_id,assignment)); CREATE TABLE {schema}.project_discovery_progress(character_id bigint PRIMARY KEY,experience bigint NOT NULL)")) await setup.ExecuteNonQueryAsync();
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
            await using (var xp = source.CreateCommand($"SELECT experience FROM {schema}.project_discovery_progress WHERE character_id=42")) Assert.Equal(150L, await xp.ExecuteScalarAsync());
            time = time.AddMinutes(1);
            Assert.Equal(2, await policy.ReadAsync(42, "get_remaining_bonus_samples", default));
            await Award("a"); // A prior assignment cannot consume tomorrow's allocation.
            Assert.Equal(2, await policy.ReadAsync(42, "get_remaining_bonus_samples", default));
            await Award("d");
            Assert.Equal(1, await policy.ReadAsync(42, "get_remaining_bonus_samples", default));
            await using (var xp = source.CreateCommand($"SELECT experience FROM {schema}.project_discovery_progress WHERE character_id=42")) Assert.Equal(225L, await xp.ExecuteScalarAsync());
        }
        finally { await using var cleanup = source.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE"); await cleanup.ExecuteNonQueryAsync(); }
    }
}
