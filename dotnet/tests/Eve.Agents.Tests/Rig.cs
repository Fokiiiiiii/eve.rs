using Eve.Data;
using Npgsql;

namespace Eve.Agents.Tests;

public sealed class PgFactAttribute : FactAttribute
{
    public PgFactAttribute() { if (Environment.GetEnvironmentVariable("EVE_PG_URL") is null) Skip = "EVE_PG_URL is not set"; }
}

/// <summary>A store in a scratch schema, dropped at the end; the events it raised are kept in order.</summary>
internal sealed class Rig : IAsyncDisposable
{
    public static readonly DateTimeOffset Start = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

    private readonly string _schema;

    private Rig(NpgsqlDataSource source, string schema)
    {
        Source = source;
        _schema = schema;
        Store = new AgentMissionStore(source, schema);
        Store.Committed += Changes.Add;
    }

    public NpgsqlDataSource Source { get; }
    public AgentMissionStore Store { get; private set; }
    public List<AgentMissionChange> Changes { get; } = [];

    public static async Task<Rig> Create()
    {
        var url = Environment.GetEnvironmentVariable("EVE_PG_URL")!;
        var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(url));
        var schema = "agt_" + Guid.NewGuid().ToString("N")[..12];
        await AgentMissionStore.MigrateAsync(source, schema);
        return new Rig(source, schema);
    }

    /// <summary>A new store on the same schema, as after a restart.</summary>
    public AgentMissionStore Restart()
    {
        Store = new AgentMissionStore(Source, _schema);
        Store.Committed += Changes.Add;
        return Store;
    }

    public async Task<IReadOnlyList<string>> JournalAsync(long characterId, long agentId)
    {
        await using var connection = await Source.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            $"SELECT action || ':' || from_state || '>' || to_state FROM {_schema}.journal WHERE character_id = $1 AND agent_id = $2 ORDER BY id",
            connection)
        {
            Parameters = { new() { Value = characterId }, new() { Value = agentId } },
        };
        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) rows.Add(reader.GetString(0));
        return rows;
    }

    public async ValueTask DisposeAsync()
    {
        await using (var connection = await Source.OpenConnectionAsync())
        await using (var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {_schema} CASCADE", connection))
        {
            await drop.ExecuteNonQueryAsync();
        }
        await Source.DisposeAsync();
    }
}
