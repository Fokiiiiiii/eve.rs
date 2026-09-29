using Eve.Data;
using Eve.Inventory;
using Eve.Skills;
using Npgsql;

namespace Eve.Characters.Tests;

/// <summary>Skips a test, loudly, when there is no PostgreSQL to test against (see tools/dev/pg18.sh).</summary>
public sealed class PgFactAttribute : FactAttribute
{
    public PgFactAttribute()
    {
        if (PgSchemas.Url is null)
        {
            Skip = "EVE_PG_URL is not set (see tools/dev/pg18.sh)";
        }
    }
}

/// <summary>A schema each for the characters and the inventory modules, migrated, and dropped on dispose.</summary>
public sealed class PgSchemas : IAsyncDisposable
{
    public static readonly string? Url = Environment.GetEnvironmentVariable("EVE_PG_URL");

    private PgSchemas(NpgsqlDataSource source, string suffix)
    {
        Source = source;
        CharactersSchema = $"ch_test_{suffix}";
        InventorySchema = $"inv_test_{suffix}";
        SkillsSchema = $"sk_test_{suffix}";
        Characters = new PgCharacterStore(source, CharactersSchema);
        Items = new PgItemStore(source, InventorySchema);
        Skills = new PgSkillStore(source, SkillsSchema);
    }

    public NpgsqlDataSource Source { get; }

    public string CharactersSchema { get; }

    public string InventorySchema { get; }

    public string SkillsSchema { get; }

    public PgCharacterStore Characters { get; }

    public PgSkillStore Skills { get; }

    public PgItemStore Items { get; }

    public static async Task<PgSchemas> CreateAsync()
    {
        var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(Url!));
        var schemas = new PgSchemas(source, Guid.NewGuid().ToString("N")[..12]);
        await PgCharacterStore.MigrateAsync(source, schemas.CharactersSchema);
        await PgItemStore.MigrateAsync(source, schemas.InventorySchema);
        await PgSkillStore.MigrateAsync(source, schemas.SkillsSchema);
        return schemas;
    }

    public async Task<T> ScalarAsync<T>(string sql)
    {
        await using var command = Source.CreateCommand(sql);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var schema in new[] { CharactersSchema, InventorySchema, SkillsSchema })
        {
            await using var command = Source.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE");
            await command.ExecuteNonQueryAsync();
        }
        await Source.DisposeAsync();
    }
}
