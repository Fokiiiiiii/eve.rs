using Npgsql;

namespace Eve.AccessGroups.Tests;

public sealed class PgFactAttribute : FactAttribute
{
    public PgFactAttribute() { if (Rig.Url is null) Skip = "EVE_PG_URL is not set"; }
}

/// <summary>An access group store in its own schema, with a clock the test moves.</summary>
internal sealed class Rig : IAsyncDisposable
{
    internal static readonly string? Url = Environment.GetEnvironmentVariable("EVE_PG_URL");
    private readonly string _schema = "ag_" + Guid.NewGuid().ToString("N")[..12];

    private Rig(NpgsqlDataSource source)
    {
        Source = source;
        Store = new AccessGroupStore(source, _schema, () => Now);
    }

    public NpgsqlDataSource Source { get; }
    public AccessGroupStore Store { get; }
    public long Now { get; set; } = 1_800_000_000_000;
    public string Schema => _schema;

    public static async Task<Rig> CreateAsync()
    {
        var rig = new Rig(NpgsqlDataSource.Create(Eve.Data.PgConnection.ForTest(Url!)));
        await AccessGroupStore.MigrateAsync(rig.Source, rig._schema);
        return rig;
    }

    /// <summary>A second store over the same tables: what a restarted host reads.</summary>
    public AccessGroupStore Reopen() => new(Source, _schema, () => Now);

    public async ValueTask DisposeAsync()
    {
        await using (var command = Source.CreateCommand($"DROP SCHEMA IF EXISTS {_schema} CASCADE"))
            await command.ExecuteNonQueryAsync();
        await Source.DisposeAsync();
    }
}
