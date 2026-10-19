using Npgsql;

namespace Eve.Agents;

public sealed record LocateRecord(long CharacterId, long AgentId, long? TargetCharacterId, DateTimeOffset? LocatedAt);

/// <summary>
/// Locator requests in PostgreSQL (migration 0004, the agents schema). The cooldown is read from <see cref="LocatedAt"/>; the
/// open request is cleared when the result is sent, in the same transaction that charges the fee.
/// </summary>
public sealed class LocateStore
{
    private readonly NpgsqlDataSource _source;
    private readonly string _schema;

    public LocateStore(NpgsqlDataSource source, string schema = ResearchProjectStore.DefaultSchema)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(schema, "^[a-z_][a-z0-9_]{0,62}$"))
        {
            throw new ArgumentException($"not a usable schema name: {schema}", nameof(schema));
        }
        _source = source;
        _schema = schema;
    }

    public async Task<LocateRecord?> ReadAsync(long characterId, long agentId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            $"SELECT character_id, agent_id, target_character_id, located_at_ms FROM {_schema}.locate_requests WHERE character_id = $1 AND agent_id = $2",
            connection)
        {
            Parameters = { new() { Value = characterId }, new() { Value = agentId } },
        };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    /// <summary>Opens a request for the character (<paramref name="targetCharacterId"/>), or clears the open one when it is null.</summary>
    public async Task SetTargetAsync(long characterId, long agentId, long? targetCharacterId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"""
            INSERT INTO {_schema}.locate_requests (character_id, agent_id, target_character_id) VALUES ($1, $2, $3)
            ON CONFLICT (character_id, agent_id) DO UPDATE SET target_character_id = EXCLUDED.target_character_id
            """, connection)
        {
            Parameters = { new() { Value = characterId }, new() { Value = agentId }, new() { Value = (object?)targetCharacterId ?? DBNull.Value } },
        };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Records a located result on the caller's transaction: the cooldown starts now and the open request is cleared.</summary>
    public async Task MarkLocatedAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long characterId, long agentId, DateTimeOffset at, CancellationToken cancellationToken = default)
    {
        await using var command = new NpgsqlCommand($"""
            INSERT INTO {_schema}.locate_requests (character_id, agent_id, target_character_id, located_at_ms) VALUES ($1, $2, NULL, $3)
            ON CONFLICT (character_id, agent_id) DO UPDATE SET target_character_id = NULL, located_at_ms = EXCLUDED.located_at_ms
            """, connection, transaction)
        {
            Parameters = { new() { Value = characterId }, new() { Value = agentId }, new() { Value = at.ToUnixTimeMilliseconds() } },
        };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static LocateRecord Read(NpgsqlDataReader reader) => new(
        reader.GetInt64(0), reader.GetInt64(1),
        reader.IsDBNull(2) ? null : reader.GetInt64(2),
        reader.IsDBNull(3) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3)));
}
