using Eve.Data;
using Npgsql;

namespace Eve.GameStore.Tests;

public sealed class PgFactAttribute : FactAttribute
{
    public PgFactAttribute()
    {
        if (PgScope.Url is null)
        {
            Skip = "EVE_PG_URL is not set (see tools/dev/pg18.sh)";
        }
    }
}

/// <summary>A store in its own schema with a controllable clock; the schema is dropped on dispose.</summary>
public sealed class PgScope : IAsyncDisposable
{
    public static readonly string? Url = Environment.GetEnvironmentVariable("EVE_PG_URL");

    private long _now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private readonly NpgsqlDataSource _source;

    private PgScope(string connectionString, string schema)
    {
        ConnectionString = connectionString;
        Schema = schema;
        _source = NpgsqlDataSource.Create(Eve.Data.PgConnection.ForTest(connectionString));
        Store = new PersistenceStore(connectionString, schema, () => Interlocked.Read(ref _now));
    }

    public string ConnectionString { get; }

    public string Schema { get; }

    public PersistenceStore Store { get; }

    /// <summary>A second store on the same schema with the same clock (another process).</summary>
    public PersistenceStore SecondStore() => new(ConnectionString, Schema, () => Interlocked.Read(ref _now));

    public long Now => Interlocked.Read(ref _now);

    public void Advance(long ms) => Interlocked.Add(ref _now, ms);

    public static async Task<PgScope> CreateAsync()
    {
        var scope = new PgScope(PgConnection.ForTest(Url!), $"gs_test_{Guid.NewGuid():N}"[..24]);
        await scope.Store.EnsureSchemaAsync();
        return scope;
    }

    public async Task<T> ScalarAsync<T>(string sql)
    {
        await using var command = _source.CreateCommand(sql);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    public async Task ExecuteAsync(string sql)
    {
        await using var command = _source.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }

    public string Q(string table) => $"\"{Schema}\".\"{table}\"";

    public async ValueTask DisposeAsync()
    {
        await ExecuteAsync($"DROP SCHEMA IF EXISTS \"{Schema}\" CASCADE");
        await Store.DisposeAsync();
        await _source.DisposeAsync();
    }
}
