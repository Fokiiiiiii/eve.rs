using Eve.Data;
using Npgsql;

namespace Eve.Structures.Tests;

public sealed class StructureStoreMigrationTests
{
    [PgFact]
    public async Task Abandonment_migration_preserves_existing_low_power_deadlines()
    {
        const long lowPowerSince = 1_700_000_000_000;
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var schema = "str_mig_" + Guid.NewGuid().ToString("N")[..12];
        try
        {
            var previousMigrations = StructureStore.MigrationSet().Where(m => m.Name != "0009_abandon_at_ms");
            await Migrations.ApplyAsync(source, schema, previousMigrations);
            await using (var connection = await source.OpenConnectionAsync())
            {
                await using var command = new NpgsqlCommand($"""
                    INSERT INTO {schema}.structures (structure_id, type_id, solar_system_id, upkeep, low_power_since_ms, destroyed_ms)
                    VALUES (1, 35832, 30000001, 2, $1, 0), (2, 35832, 30000001, 2, $1, 1), (3, 35832, 30000001, 1, $1, 0)
                    """, connection);
                command.Parameters.AddWithValue(lowPowerSince);
                Assert.Equal(3, await command.ExecuteNonQueryAsync());
            }

            Assert.Contains("0009_abandon_at_ms", await StructureStore.MigrateAsync(source, schema));
            await using (var connection = await source.OpenConnectionAsync())
            await using (var command = new NpgsqlCommand($"SELECT structure_id, abandon_at_ms FROM {schema}.structures ORDER BY structure_id", connection))
            await using (var reader = await command.ExecuteReaderAsync())
            {
                Assert.True(await reader.ReadAsync());
                Assert.Equal(1, reader.GetInt64(0));
                Assert.Equal(lowPowerSince + StructureTimes.AbandonAfterMinMs, reader.GetInt64(1));
                Assert.True(await reader.ReadAsync());
                Assert.Equal(2, reader.GetInt64(0));
                Assert.Equal(0, reader.GetInt64(1));
                Assert.True(await reader.ReadAsync());
                Assert.Equal(3, reader.GetInt64(0));
                Assert.Equal(0, reader.GetInt64(1));
                Assert.False(await reader.ReadAsync());
            }

            Assert.Empty(await StructureStore.MigrateAsync(source, schema));
        }
        finally
        {
            await using var connection = await source.OpenConnectionAsync();
            await using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE", connection);
            await drop.ExecuteNonQueryAsync();
        }
    }
}
