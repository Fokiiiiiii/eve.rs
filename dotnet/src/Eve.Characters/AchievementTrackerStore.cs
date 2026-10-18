using Npgsql;

namespace Eve.Characters;

/// <summary>What a character's achievement tracker holds: the completed achievements (id and filetime) and the client event counts.</summary>
public sealed record AchievementTrackerRecord(IReadOnlyList<(int AchievementId, long CompletedFileTime)> Completed, IReadOnlyList<(string Name, long Count)> EventCounts);

/// <summary>
/// The achievement tracker of each character (tables <c>achievement_trackers</c>, <c>achievement_completions</c> and <c>achievement_event_counts</c>,
/// in the characters schema). The client sends both maps whole, so a write replaces them.
/// </summary>
public sealed class AchievementTrackerStore(NpgsqlDataSource source, string schema)
{
    /// <summary>The character's record, or null when it has none yet.</summary>
    public async Task<AchievementTrackerRecord?> ReadAsync(long characterId, CancellationToken cancellationToken = default)
    {
        await using var header = source.CreateCommand($"SELECT 1 FROM {schema}.achievement_trackers WHERE character_id = $1");
        header.Parameters.Add(new NpgsqlParameter { Value = characterId });
        if (await header.ExecuteScalarAsync(cancellationToken) is null)
            return null;

        var completed = new List<(int, long)>();
        await using (var command = source.CreateCommand($"SELECT achievement_id, completed_filetime FROM {schema}.achievement_completions WHERE character_id = $1 ORDER BY achievement_id"))
        {
            command.Parameters.Add(new NpgsqlParameter { Value = characterId });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                completed.Add((reader.GetInt32(0), reader.GetInt64(1)));
        }
        var events = new List<(string, long)>();
        await using (var command = source.CreateCommand($"SELECT event_name, event_count FROM {schema}.achievement_event_counts WHERE character_id = $1 ORDER BY event_name"))
        {
            command.Parameters.Add(new NpgsqlParameter { Value = characterId });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                events.Add((reader.GetString(0), reader.GetInt64(1)));
        }
        return new AchievementTrackerRecord(completed, events);
    }

    /// <summary>Replaces the character's record with what the client sent, in one transaction.</summary>
    public async Task WriteAsync(long characterId, IReadOnlyList<(int AchievementId, long CompletedFileTime)> completed,
        IReadOnlyList<(string Name, long Count)> eventCounts, long nowMs, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var upsert = new NpgsqlCommand($"""
            INSERT INTO {schema}.achievement_trackers (character_id, updated_ms) VALUES ($1, $2)
            ON CONFLICT (character_id) DO UPDATE SET updated_ms = EXCLUDED.updated_ms
            """, connection, transaction))
        {
            upsert.Parameters.Add(new NpgsqlParameter { Value = characterId });
            upsert.Parameters.Add(new NpgsqlParameter { Value = nowMs });
            await upsert.ExecuteNonQueryAsync(cancellationToken);
        }
        foreach (var table in new[] { "achievement_completions", "achievement_event_counts" })
        {
            await using var delete = new NpgsqlCommand($"DELETE FROM {schema}.{table} WHERE character_id = $1", connection, transaction);
            delete.Parameters.Add(new NpgsqlParameter { Value = characterId });
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var insert = new NpgsqlCommand($"INSERT INTO {schema}.achievement_completions (character_id, achievement_id, completed_filetime) VALUES ($1, $2, $3)", connection, transaction))
        {
            insert.Parameters.Add(new NpgsqlParameter { Value = characterId });
            insert.Parameters.Add(new NpgsqlParameter<int>());
            insert.Parameters.Add(new NpgsqlParameter<long>());
            foreach (var (achievementId, filetime) in completed)
            {
                insert.Parameters[1].Value = achievementId;
                insert.Parameters[2].Value = filetime;
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        await using (var insert = new NpgsqlCommand($"INSERT INTO {schema}.achievement_event_counts (character_id, event_name, event_count) VALUES ($1, $2, $3)", connection, transaction))
        {
            insert.Parameters.Add(new NpgsqlParameter { Value = characterId });
            insert.Parameters.Add(new NpgsqlParameter<string>());
            insert.Parameters.Add(new NpgsqlParameter<long>());
            foreach (var (name, count) in eventCounts)
            {
                insert.Parameters[1].Value = name;
                insert.Parameters[2].Value = count;
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        await transaction.CommitAsync(cancellationToken);
    }
}
