using Eve.Data;
using Npgsql;

namespace Eve.Skills.Tests;

/// <summary>Skips a test, loudly, when there is no PostgreSQL to test against (see tools/dev/pg18.sh).</summary>
public sealed class PgFactAttribute : FactAttribute
{
    public PgFactAttribute()
    {
        if (PgSchema.Url is null)
        {
            Skip = "EVE_PG_URL is not set (see tools/dev/pg18.sh)";
        }
    }
}

/// <summary>A migrated skills schema, dropped on dispose.</summary>
public sealed class PgSchema : IAsyncDisposable
{
    public static readonly string? Url = Environment.GetEnvironmentVariable("EVE_PG_URL");

    private PgSchema(NpgsqlDataSource source, string name)
    {
        Source = source;
        Name = name;
        Store = new PgSkillStore(source, name);
    }

    public NpgsqlDataSource Source { get; }

    public string Name { get; }

    public PgSkillStore Store { get; }

    public static async Task<PgSchema> CreateAsync()
    {
        var source = NpgsqlDataSource.Create(PgConnection.ForTest(Url!));
        var schema = new PgSchema(source, $"sk_test_{Guid.NewGuid():N}"[..20]);
        await PgSkillStore.MigrateAsync(source, schema.Name);
        return schema;
    }

    public async Task GrantAsync(long characterId, IReadOnlyList<CharacterSkill> skills)
    {
        await using var connection = await Source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await Store.GrantAsync(connection, transaction, characterId, skills);
        await transaction.CommitAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await using var command = Source.CreateCommand($"DROP SCHEMA IF EXISTS {Name} CASCADE");
        await command.ExecuteNonQueryAsync();
        await Source.DisposeAsync();
    }
}
