using Npgsql;

namespace Eve.Characters;

/// <summary>A character-scoped developer override for the next Project Discovery task.</summary>
public sealed class ProjectDiscoveryTaskPinStore(NpgsqlDataSource source, string schema)
{
    /// <summary>Returns the pinned task ID, or null when no override is set.</summary>
    public async Task<long?> ReadAsync(long characterId, CancellationToken cancellationToken = default)
    {
        await using var command = source.CreateCommand($"SELECT task_id FROM {schema}.project_discovery_task_pins WHERE character_id = $1");
        command.Parameters.Add(new NpgsqlParameter { Value = characterId });
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is long taskId ? taskId : null;
    }

    /// <summary>Sets or clears the character’s pinned task ID.</summary>
    public async Task SetAsync(long characterId, long? taskId, long nowMs, CancellationToken cancellationToken = default)
    {
        if (characterId <= 0)
            throw new ArgumentOutOfRangeException(nameof(characterId));
        if (taskId is <= 0)
            throw new ArgumentOutOfRangeException(nameof(taskId));

        if (taskId is null)
        {
            await using var delete = source.CreateCommand($"DELETE FROM {schema}.project_discovery_task_pins WHERE character_id = $1");
            delete.Parameters.Add(new NpgsqlParameter { Value = characterId });
            await delete.ExecuteNonQueryAsync(cancellationToken);
            return;
        }

        await using var command = source.CreateCommand(
            $"INSERT INTO {schema}.project_discovery_task_pins (character_id, task_id, updated_ms) VALUES ($1, $2, $3) " +
            "ON CONFLICT (character_id) DO UPDATE SET task_id = EXCLUDED.task_id, updated_ms = EXCLUDED.updated_ms");
        command.Parameters.Add(new NpgsqlParameter { Value = characterId });
        command.Parameters.Add(new NpgsqlParameter { Value = taskId.Value });
        command.Parameters.Add(new NpgsqlParameter { Value = nowMs });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
