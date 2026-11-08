using Npgsql;
using NpgsqlTypes;

namespace Eve.Characters;

/// <summary>The character's saved dogma settings for aggressive and focused-fire drone behavior.</summary>
public sealed partial class PgCharacterStore
{
    public async Task<IReadOnlyDictionary<int, bool>> DroneSettingsAsync(long characterId, CancellationToken cancellationToken = default)
    {
        await using var command = _source.CreateCommand(
            $"SELECT attribute_id, value FROM {_schema}.character_drone_settings WHERE character_id = $1 ORDER BY attribute_id");
        command.Parameters.Add(new NpgsqlParameter { Value = characterId });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var settings = new Dictionary<int, bool>();
        while (await reader.ReadAsync(cancellationToken))
            settings.Add(reader.GetInt32(0), reader.GetBoolean(1));
        return settings;
    }

    /// <summary>Writes changed attributes together; independent keys merge without replacing other saved settings.</summary>
    public async Task UpdateDroneSettingsAsync(long characterId, IReadOnlyDictionary<int, bool> changes, CancellationToken cancellationToken = default)
    {
        if (changes.Count == 0)
            return;

        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var (attributeId, value) in changes.OrderBy(change => change.Key))
        {
            await using var command = new NpgsqlCommand($"""
                INSERT INTO {_schema}.character_drone_settings (character_id, attribute_id, value)
                VALUES ($1, $2, $3)
                ON CONFLICT (character_id, attribute_id) DO UPDATE SET value = EXCLUDED.value
                """, connection, transaction);
            command.Parameters.Add(new NpgsqlParameter { Value = characterId });
            command.Parameters.Add(new NpgsqlParameter { Value = attributeId });
            command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Boolean, Value = value });
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }
}
