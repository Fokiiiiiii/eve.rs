using Eve.Data;
using Eve.Host;
using Eve.Services.Space;
using Npgsql;

namespace Eve.Services.Space.Tests;

public sealed class MigrationSelectionTests
{
    [PgFact]
    public async Task Module_migrations_create_only_owned_tables_and_preserve_existing_tables()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(Rig.Url!));
        var siteSchema = $"migration_site_{Guid.NewGuid():N}";
        var configSchema = $"migration_config_{Guid.NewGuid():N}";

        Assert.Equal(
            new[] { "0001_site_containers", "0002_container_looted" },
            SiteContainerStore.MigrationSet().Select(migration => migration.Name).Order(StringComparer.Ordinal));
        Assert.Equal(
            new[] { "0001_global_config" },
            PgGlobalConfigPersistence.MigrationSet().Select(migration => migration.Name).Order(StringComparer.Ordinal));

        try
        {
            await ExecuteAsync(source,
                $"CREATE SCHEMA {siteSchema}; " +
                $"CREATE TABLE {siteSchema}.previously_deployed_table (marker integer NOT NULL); " +
                $"INSERT INTO {siteSchema}.previously_deployed_table VALUES (42)");

            Assert.Equal(
                new[] { "0001_site_containers", "0002_container_looted" },
                await SiteContainerStore.MigrateAsync(source, siteSchema));
            Assert.Equal(
                new[] { "0001_global_config" },
                await PgGlobalConfigPersistence.MigrateAsync(source, configSchema));

            Assert.Equal(
                new[] { "migrations", "previously_deployed_table", "site_containers" },
                await TablesInAsync(source, siteSchema));
            Assert.Equal(
                new[] { "global_config", "migrations" },
                await TablesInAsync(source, configSchema));
            Assert.Equal(42, await ScalarAsync<int>(
                source, $"SELECT marker FROM {siteSchema}.previously_deployed_table"));
        }
        finally
        {
            await ExecuteAsync(source,
                $"DROP SCHEMA IF EXISTS {siteSchema} CASCADE; DROP SCHEMA IF EXISTS {configSchema} CASCADE");
        }
    }

    private static async Task ExecuteAsync(NpgsqlDataSource source, string sql)
    {
        await using var command = source.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlDataSource source, string sql)
    {
        await using var command = source.CreateCommand(sql);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<string[]> TablesInAsync(NpgsqlDataSource source, string schema)
    {
        await using var command = source.CreateCommand(
            "SELECT tablename FROM pg_tables WHERE schemaname = $1 ORDER BY tablename");
        command.Parameters.Add(new NpgsqlParameter { Value = schema });
        await using var reader = await command.ExecuteReaderAsync();
        var tables = new List<string>();
        while (await reader.ReadAsync())
        {
            tables.Add(reader.GetString(0));
        }
        return [.. tables];
    }
}
