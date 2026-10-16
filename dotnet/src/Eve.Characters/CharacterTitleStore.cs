using Npgsql;

namespace Eve.Characters;

/// <summary>
/// The titles of each character (tables <c>character_titles</c> and <c>character_equipped_titles</c>, migration 0013). Owned titles are
/// granted by a reward (EveJS <c>grantTitle</c>); equipping needs an owned title (EveJS <c>equipTitle</c>). The title's definition is the
/// SDE's; this store only knows the ids.
/// </summary>
public sealed class CharacterTitleStore(NpgsqlDataSource source, string schema)
{
    /// <summary>The owned titles of a character, in the order they were granted, from the offset (the page's start).</summary>
    public async Task<IReadOnlyList<Guid>> OwnedAsync(long characterId, int offset, int limit, CancellationToken cancellationToken = default)
    {
        await using var command = source.CreateCommand(
            $"SELECT title_id FROM {schema}.character_titles WHERE character_id = $1 ORDER BY granted_ms, title_id OFFSET $2 LIMIT $3");
        command.Parameters.Add(new NpgsqlParameter { Value = characterId });
        command.Parameters.Add(new NpgsqlParameter { Value = offset });
        command.Parameters.Add(new NpgsqlParameter { Value = limit });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var titles = new List<Guid>();
        while (await reader.ReadAsync(cancellationToken))
            titles.Add(reader.GetGuid(0));
        return titles;
    }

    /// <summary>The number of titles a character owns.</summary>
    public async Task<long> OwnedCountAsync(long characterId, CancellationToken cancellationToken = default)
    {
        await using var command = source.CreateCommand($"SELECT count(*) FROM {schema}.character_titles WHERE character_id = $1");
        command.Parameters.Add(new NpgsqlParameter { Value = characterId });
        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    /// <summary>The title a character wears, or null when it wears none.</summary>
    public async Task<Guid?> EquippedAsync(long characterId, CancellationToken cancellationToken = default)
    {
        await using var command = source.CreateCommand($"SELECT title_id FROM {schema}.character_equipped_titles WHERE character_id = $1");
        command.Parameters.Add(new NpgsqlParameter { Value = characterId });
        return await command.ExecuteScalarAsync(cancellationToken) is Guid title ? title : null;
    }

    /// <summary>Grants an owned title (a reward's effect). Granting a title the character owns changes nothing.</summary>
    public async Task GrantAsync(long characterId, Guid titleId, long nowMs, CancellationToken cancellationToken = default)
    {
        await using var command = source.CreateCommand(
            $"INSERT INTO {schema}.character_titles (character_id, title_id, granted_ms) VALUES ($1, $2, $3) ON CONFLICT (character_id, title_id) DO NOTHING");
        command.Parameters.Add(new NpgsqlParameter { Value = characterId });
        command.Parameters.Add(new NpgsqlParameter { Value = titleId });
        command.Parameters.Add(new NpgsqlParameter { Value = nowMs });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Wears an owned title. False when the character does not own it (nothing changes then).</summary>
    public async Task<bool> EquipAsync(long characterId, Guid titleId, CancellationToken cancellationToken = default)
    {
        await using var command = source.CreateCommand($"""
            INSERT INTO {schema}.character_equipped_titles (character_id, title_id)
            SELECT $1, $2 WHERE EXISTS (SELECT 1 FROM {schema}.character_titles WHERE character_id = $1 AND title_id = $2)
            ON CONFLICT (character_id) DO UPDATE SET title_id = EXCLUDED.title_id
            """);
        command.Parameters.Add(new NpgsqlParameter { Value = characterId });
        command.Parameters.Add(new NpgsqlParameter { Value = titleId });
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    /// <summary>Takes the worn title off (a character that wears none keeps none).</summary>
    public async Task UnequipAsync(long characterId, CancellationToken cancellationToken = default)
    {
        await using var command = source.CreateCommand($"DELETE FROM {schema}.character_equipped_titles WHERE character_id = $1");
        command.Parameters.Add(new NpgsqlParameter { Value = characterId });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
