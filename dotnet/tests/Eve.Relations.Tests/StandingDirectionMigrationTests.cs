using Eve.Data;
using Eve.Relations;
using Npgsql;

namespace Eve.Relations.Tests;

public sealed class StandingDirectionMigrationTests
{
    [PgFact]
    public async Task Legacy_player_standings_and_history_are_transposed_once()
    {
        var source = NpgsqlDataSource.Create(PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var schema = "standing_migration_" + Guid.NewGuid().ToString("N");
        try
        {
            var migrations = RelationStore.MigrationSet();
            await Migrations.ApplyAsync(source, schema, migrations.Where(m => m.Name == "0001_relations"));
            await using (var command = source.CreateCommand($"""
                INSERT INTO {schema}.relations (kind, subject, object, value, since_ms, updated_ms)
                VALUES ('standing', 140000001, 98000001, 4.5, 1, 2),
                       ('standing', 98000002, 140000002, 3.5, 1, 2),
                       ('standing', 140000003, 98000003, 4.5, 1, 2),
                       ('standing', 98000003, 140000003, 5.5, 1, 3);
                INSERT INTO {schema}.relation_history (kind, subject, object, old_value, new_value, at_ms)
                VALUES ('standing', 140000001, 98000001, 0, 4.5, 2);
                """))
            {
                await command.ExecuteNonQueryAsync();
            }

            Assert.Equal(["0002_standing_direction"], await Migrations.ApplyAsync(source, schema, migrations));
            Assert.Empty(await Migrations.ApplyAsync(source, schema, migrations));
            await using var check = source.CreateCommand($"""
                SELECT subject, object, value FROM {schema}.relations WHERE kind = 'standing' ORDER BY subject;
                """);
            await using var reader = await check.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal((98000001L, 140000001L), (reader.GetInt64(0), reader.GetInt64(1)));
            Assert.True(await reader.ReadAsync());
            Assert.Equal((98000002L, 140000002L), (reader.GetInt64(0), reader.GetInt64(1)));
            Assert.True(await reader.ReadAsync());
            Assert.Equal((98000003L, 140000003L, 5.5), (reader.GetInt64(0), reader.GetInt64(1), reader.GetDouble(2)));
            Assert.False(await reader.ReadAsync());
            await reader.CloseAsync();

            await using var history = source.CreateCommand($"SELECT subject, object FROM {schema}.relation_history WHERE kind = 'standing'");
            await using var historyReader = await history.ExecuteReaderAsync();
            Assert.True(await historyReader.ReadAsync());
            Assert.Equal((98000001L, 140000001L), (historyReader.GetInt64(0), historyReader.GetInt64(1)));
        }
        finally
        {
            await using var cleanup = source.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE");
            await cleanup.ExecuteNonQueryAsync();
            await source.DisposeAsync();
        }
    }
}
