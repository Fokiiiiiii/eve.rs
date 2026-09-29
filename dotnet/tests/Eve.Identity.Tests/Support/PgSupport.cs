using Eve.Data;
using Npgsql;

namespace Eve.Identity.Tests;

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

/// <summary>A module schema of its own, migrated, and dropped on dispose.</summary>
public sealed class PgSchema : IAsyncDisposable
{
    public static readonly string? Url = Environment.GetEnvironmentVariable("EVE_PG_URL");

    private PgSchema(NpgsqlDataSource source, string name)
    {
        Source = source;
        Name = name;
    }

    public NpgsqlDataSource Source { get; }

    public string Name { get; }

    public static async Task<PgSchema> CreateAsync(bool migrate = true)
    {
        var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(Url!));
        var scope = new PgSchema(source, $"id_test_{Guid.NewGuid():N}"[..22]);
        if (migrate)
        {
            await PgAccountDirectory.MigrateAsync(source, scope.Name);
        }
        return scope;
    }

    public async Task<T> ScalarAsync<T>(string sql, params object[] parameters)
    {
        await using var command = Source.CreateCommand(sql);
        foreach (var parameter in parameters)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = parameter });
        }
        return (T)(await command.ExecuteScalarAsync())!;
    }

    public async Task ExecuteAsync(string sql, params object[] parameters)
    {
        await using var command = Source.CreateCommand(sql);
        foreach (var parameter in parameters)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = parameter });
        }
        await command.ExecuteNonQueryAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await ExecuteAsync($"DROP SCHEMA IF EXISTS {Name} CASCADE");
        await Source.DisposeAsync();
    }
}
