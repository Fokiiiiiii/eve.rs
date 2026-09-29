using Eve.Data;
using Npgsql;

namespace Eve.Skills;

/// <summary>The queue, its running state and the history of skill training in PostgreSQL, in the skills module's schema.</summary>
public sealed class PgTrainingStore
{
    /// <summary>How many lines of history a character keeps.</summary>
    public const int HistoryLimit = 100;

    private readonly NpgsqlDataSource _source;
    private readonly string _schema;

    public PgTrainingStore(NpgsqlDataSource source, string schema = PgSkillStore.DefaultSchema)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(schema, "^[a-z_][a-z0-9_]{0,62}$"))
        {
            throw new ArgumentException($"not a usable schema name: {schema}", nameof(schema));
        }
        _source = source;
        _schema = schema;
    }

    /// <summary>The character's queue (empty when it has none).</summary>
    public async Task<QueueState> LoadAsync(long characterId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        return await LoadAsync(connection, null, characterId, cancellationToken);
    }

    public async Task<QueueState> LoadAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, long characterId, CancellationToken cancellationToken = default)
    {
        var entries = new List<QueueEntry>();
        await using (var command = new NpgsqlCommand($"SELECT type_id, to_level FROM {_schema}.skill_queue WHERE character_id = $1 ORDER BY position", connection, transaction)
        {
            Parameters = { new() { Value = characterId } },
        })
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                entries.Add(new QueueEntry(reader.GetInt32(0), reader.GetInt16(1)));
            }
        }
        await using var state = new NpgsqlCommand($"SELECT active, active_start FROM {_schema}.training_state WHERE character_id = $1", connection, transaction)
        {
            Parameters = { new() { Value = characterId } },
        };
        await using var row = await state.ExecuteReaderAsync(cancellationToken);
        if (!await row.ReadAsync(cancellationToken))
        {
            return new QueueState(entries, false, null);
        }
        return new QueueState(entries, row.GetBoolean(0), row.IsDBNull(1) ? null : row.GetInt64(1));
    }

    /// <summary>Keeps <paramref name="state"/> as the character's queue, inside the caller's transaction.</summary>
    public async Task SaveAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long characterId, QueueState state, CancellationToken cancellationToken = default)
    {
        await using (var clear = new NpgsqlCommand($"DELETE FROM {_schema}.skill_queue WHERE character_id = $1", connection, transaction) { Parameters = { new() { Value = characterId } } })
        {
            await clear.ExecuteNonQueryAsync(cancellationToken);
        }
        for (var position = 0; position < state.Entries.Count; position++)
        {
            await using var insert = new NpgsqlCommand($"INSERT INTO {_schema}.skill_queue (character_id, position, type_id, to_level) VALUES ($1, $2, $3, $4)", connection, transaction)
            {
                Parameters = { new() { Value = characterId }, new() { Value = position }, new() { Value = state.Entries[position].TypeId }, new() { Value = (short)state.Entries[position].ToLevel } },
            };
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        await using var upsert = new NpgsqlCommand(
            $"INSERT INTO {_schema}.training_state (character_id, active, active_start) VALUES ($1, $2, $3) " +
            "ON CONFLICT (character_id) DO UPDATE SET active = EXCLUDED.active, active_start = EXCLUDED.active_start", connection, transaction)
        {
            Parameters = { new() { Value = characterId }, new() { Value = state.Active }, new() { Value = state.ActiveStart is { } start ? start : DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Bigint } },
        };
        await upsert.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Adds lines to the character's history, dropping the oldest beyond <see cref="HistoryLimit"/>.</summary>
    public async Task AppendHistoryAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long characterId, IEnumerable<HistoryEntry> entries, CancellationToken cancellationToken = default)
    {
        var any = false;
        foreach (var entry in entries)
        {
            any = true;
            await using var insert = new NpgsqlCommand(
                $"INSERT INTO {_schema}.skill_history (character_id, log_date, event_type, type_id, absolute_points, level) VALUES ($1, $2, $3, $4, $5, $6)", connection, transaction)
            {
                Parameters =
                {
                    new() { Value = characterId }, new() { Value = entry.LogDate }, new() { Value = entry.EventType }, new() { Value = entry.TypeId },
                    new() { Value = Math.Max(0, entry.Points) }, new() { Value = (short)Math.Max(0, entry.Level) },
                },
            };
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        if (any)
        {
            await using var trim = new NpgsqlCommand(
                $"DELETE FROM {_schema}.skill_history WHERE character_id = $1 AND id NOT IN (SELECT id FROM {_schema}.skill_history WHERE character_id = $1 ORDER BY id DESC LIMIT {HistoryLimit})", connection, transaction)
            {
                Parameters = { new() { Value = characterId } },
            };
            await trim.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    /// <summary>The character's history, newest first.</summary>
    public async Task<IReadOnlyList<HistoryEntry>> HistoryAsync(long characterId, CancellationToken cancellationToken = default)
    {
        await using var command = _source.CreateCommand($"SELECT log_date, event_type, type_id, absolute_points, level FROM {_schema}.skill_history WHERE character_id = $1 ORDER BY id DESC LIMIT {HistoryLimit}");
        command.Parameters.Add(new NpgsqlParameter { Value = characterId });
        var list = new List<HistoryEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(new HistoryEntry(reader.GetInt64(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt64(3), reader.GetInt16(4)));
        }
        return list;
    }

    /// <summary>How many of these characters have a queue that runs.</summary>
    public async Task<int> RunningCountAsync(IEnumerable<long> characterIds, CancellationToken cancellationToken = default)
    {
        var ids = characterIds.ToArray();
        if (ids.Length == 0)
        {
            return 0;
        }
        await using var command = _source.CreateCommand(
            $"SELECT count(*) FROM {_schema}.training_state s WHERE s.character_id = ANY($1) AND s.active AND EXISTS (SELECT 1 FROM {_schema}.skill_queue q WHERE q.character_id = s.character_id)");
        command.Parameters.Add(new NpgsqlParameter { Value = ids });
        return (int)(long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }
}
