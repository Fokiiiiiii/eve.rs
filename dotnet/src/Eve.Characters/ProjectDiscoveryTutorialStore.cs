using Npgsql;

namespace Eve.Characters;

/// <summary>The Project Discovery tutorial of a character: whether it is finished, and the level reached.</summary>
public sealed record ProjectDiscoveryTutorial(bool Finished, int Level);

/// <summary>
/// The Project Discovery tutorial of each character (table <c>project_discovery_tutorials</c>, in the characters schema). A character with no row
/// has not started: not finished, at level 0 (the EveJS default state).
/// </summary>
public sealed class ProjectDiscoveryTutorialStore(NpgsqlDataSource source, string schema)
{
    /// <summary>The stored tutorial, or null when the character has none yet.</summary>
    public async Task<ProjectDiscoveryTutorial?> ReadAsync(long characterId, CancellationToken cancellationToken = default)
    {
        await using var command = source.CreateCommand($"SELECT finished, tutorial_level FROM {schema}.project_discovery_tutorials WHERE character_id = $1");
        command.Parameters.Add(new NpgsqlParameter { Value = characterId });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;
        return new ProjectDiscoveryTutorial(reader.GetBoolean(0), reader.GetInt32(1));
    }

    /// <summary>Stores the character's tutorial, replacing any earlier one.</summary>
    public async Task WriteAsync(long characterId, ProjectDiscoveryTutorial tutorial, long nowMs, CancellationToken cancellationToken = default)
    {
        await using var command = source.CreateCommand(
            $"INSERT INTO {schema}.project_discovery_tutorials (character_id, finished, tutorial_level, updated_ms) VALUES ($1, $2, $3, $4) " +
            "ON CONFLICT (character_id) DO UPDATE SET finished = EXCLUDED.finished, tutorial_level = EXCLUDED.tutorial_level, updated_ms = EXCLUDED.updated_ms");
        command.Parameters.Add(new NpgsqlParameter { Value = characterId });
        command.Parameters.Add(new NpgsqlParameter { Value = tutorial.Finished });
        command.Parameters.Add(new NpgsqlParameter { Value = tutorial.Level });
        command.Parameters.Add(new NpgsqlParameter { Value = nowMs });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
