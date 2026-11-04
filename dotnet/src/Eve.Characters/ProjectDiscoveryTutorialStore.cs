using Npgsql;

namespace Eve.Characters;

/// <summary>The Project Discovery tutorial of a character: whether it is finished, and the level reached.</summary>
public sealed record ProjectDiscoveryTutorial(bool Finished, int Level);

/// <summary>
/// The Project Discovery tutorial of each character (table <c>project_discovery_tutorials</c>, in the characters schema). A character with no row
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

    public async Task<ProjectDiscoveryTutorial> MarkFinishedAsync(long characterId, long nowMs, CancellationToken ct = default)
    {
        await using var command = source.CreateCommand($"INSERT INTO {schema}.project_discovery_tutorials(character_id,finished,tutorial_level,updated_ms) VALUES($1,true,8,$2) ON CONFLICT(character_id) DO UPDATE SET finished=true,tutorial_level=8,updated_ms=$2 RETURNING finished,tutorial_level");
        command.Parameters.AddWithValue(characterId); command.Parameters.AddWithValue(nowMs);
        await using var reader = await command.ExecuteReaderAsync(ct); await reader.ReadAsync(ct); return new(reader.GetBoolean(0), reader.GetInt32(1));
    }
    public async Task<long> ReadExperienceAsync(long characterId, CancellationToken cancellationToken = default)
    {
        await using var command = source.CreateCommand($"SELECT experience FROM {schema}.project_discovery_progress WHERE character_id=$1");
        command.Parameters.AddWithValue(characterId);
        return await command.ExecuteScalarAsync(cancellationToken) is long value ? value : 0;
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

    /// <summary>Applies one tutorial action atomically and returns the state committed by that action.</summary>
    public async Task<ProjectDiscoveryTutorial> TransitionAsync(long characterId, string action, long nowMs, CancellationToken cancellationToken = default)
    {
        if (action is not ("reset" or "skip" or "increase"))
            throw new ArgumentOutOfRangeException(nameof(action));
        await using var command = source.CreateCommand(
            $"INSERT INTO {schema}.project_discovery_tutorials (character_id, finished, tutorial_level, updated_ms, participation_steps, participation_skipped) " +
            "VALUES ($1, $2 = 'skip', CASE $2 WHEN 'reset' THEN 0 WHEN 'skip' THEN 8 ELSE 1 END, $3, CASE WHEN $2 = 'increase' THEN 1 ELSE 0 END, $2 = 'skip') " +
            "ON CONFLICT (character_id) DO UPDATE SET " +
            "finished = CASE $2 WHEN 'reset' THEN false WHEN 'skip' THEN true ELSE project_discovery_tutorials.tutorial_level >= 7 END, " +
            "tutorial_level = CASE $2 WHEN 'reset' THEN 0 WHEN 'skip' THEN 8 ELSE LEAST(8, project_discovery_tutorials.tutorial_level + 1) END, " +
            "participation_steps = CASE $2 WHEN 'reset' THEN 0 WHEN 'skip' THEN 0 ELSE CASE WHEN project_discovery_tutorials.participation_skipped OR project_discovery_tutorials.finished THEN project_discovery_tutorials.participation_steps ELSE LEAST(8, project_discovery_tutorials.participation_steps + 1) END END, " +
            "participation_skipped = CASE $2 WHEN 'reset' THEN false WHEN 'skip' THEN true ELSE project_discovery_tutorials.participation_skipped END, " +
            "updated_ms = EXCLUDED.updated_ms " +
            "RETURNING finished, tutorial_level");
        command.Parameters.Add(new NpgsqlParameter { Value = characterId });
        command.Parameters.Add(new NpgsqlParameter { Value = action });
        command.Parameters.Add(new NpgsqlParameter { Value = nowMs });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException("Project Discovery tutorial transition returned no state.");
        return new ProjectDiscoveryTutorial(reader.GetBoolean(0), reader.GetInt32(1));
    }
}
