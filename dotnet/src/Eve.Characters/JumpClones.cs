using Npgsql;

namespace Eve.Characters;

/// <summary>An installed jump clone: where it is, its name, and the implant types in it (slot order).</summary>
public sealed record InstalledJumpClone(long JumpCloneId, long LocationId, string LocationKind, string Name, IReadOnlyList<int> ImplantTypeIds);

/// <summary>What a character has installed, and the FILETIME of its last clone jump (0 when none).</summary>
public sealed record JumpCloneState(IReadOnlyList<InstalledJumpClone> Clones, long TimeLastJumpFileTime);

/// <summary>The installed jump clones (migration 0006 of this project), kept in the characters schema beside the character.</summary>
public sealed class PgJumpCloneStore
{
    private readonly NpgsqlDataSource _source;
    private readonly string _schema;

    public PgJumpCloneStore(NpgsqlDataSource source, string schema = PgCharacterStore.DefaultSchema)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(schema, "^[a-z_][a-z0-9_]{0,62}$"))
        {
            throw new ArgumentException($"not a usable schema name: {schema}", nameof(schema));
        }
        _source = source;
        _schema = schema;
    }

    public async Task<JumpCloneState> ReadAsync(long characterId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var lastJump = await ReadLastJumpAsync(connection, transaction, characterId, cancellationToken);
        var clones = await ReadClonesAsync(connection, transaction, characterId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new JumpCloneState(clones, lastJump);
    }

    /// <summary>Renames a clone the character owns. False when the character has no such clone.</summary>
    public async Task<bool> RenameAsync(long characterId, long jumpCloneId, string name, CancellationToken cancellationToken = default)
    {
        await using var command = _source.CreateCommand(
            $"UPDATE {_schema}.character_jump_clones SET clone_name = $3 WHERE jump_clone_id = $2 AND character_id = $1");
        command.Parameters.AddWithValue(characterId);
        command.Parameters.AddWithValue(jumpCloneId);
        command.Parameters.AddWithValue(name);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    /// <summary>Removes a clone the character owns and returns it. Null when the character has no such clone.</summary>
    public async Task<InstalledJumpClone?> DestroyAsync(long characterId, long jumpCloneId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var clone = (await ReadClonesAsync(connection, transaction, characterId, cancellationToken, jumpCloneId)).SingleOrDefault();
        if (clone is null)
        {
            return null;
        }
        await using var delete = new NpgsqlCommand(
            $"DELETE FROM {_schema}.character_jump_clones WHERE jump_clone_id = $1 AND character_id = $2", connection, transaction)
        {
            Parameters = { new() { Value = jumpCloneId }, new() { Value = characterId } },
        };
        await delete.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return clone;
    }

    /// <summary>Sets the last clone jump time back to zero, so the next jump has no cooldown.</summary>
    public async Task ResetLastJumpAsync(long characterId, CancellationToken cancellationToken = default)
    {
        await using var command = _source.CreateCommand(
            $"UPDATE {_schema}.characters SET time_last_clone_jump_filetime = 0 WHERE id = $1");
        command.Parameters.AddWithValue(characterId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<long> ReadLastJumpAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long characterId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"SELECT time_last_clone_jump_filetime FROM {_schema}.characters WHERE id = $1", connection, transaction)
        {
            Parameters = { new() { Value = characterId } },
        };
        return await command.ExecuteScalarAsync(cancellationToken) is long fileTime ? fileTime : 0L;
    }

    private async Task<IReadOnlyList<InstalledJumpClone>> ReadClonesAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long characterId, CancellationToken cancellationToken, long? onlyCloneId = null)
    {
        var clones = new List<(long Id, long LocationId, string Kind, string Name)>();
        await using (var command = new NpgsqlCommand(
            $"SELECT jump_clone_id, location_id, location_kind, clone_name FROM {_schema}.character_jump_clones " +
            "WHERE character_id = $1 AND ($2::bigint IS NULL OR jump_clone_id = $2) ORDER BY jump_clone_id",
            connection, transaction)
        {
            Parameters = { new() { Value = characterId }, new() { Value = (object?)onlyCloneId ?? DBNull.Value } },
        })
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                clones.Add((reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3)));
            }
        }
        var implants = new Dictionary<long, List<int>>();
        await using (var command = new NpgsqlCommand(
            $"SELECT i.jump_clone_id, i.type_id FROM {_schema}.character_jump_clone_implants i " +
            $"JOIN {_schema}.character_jump_clones c ON c.jump_clone_id = i.jump_clone_id " +
            "WHERE c.character_id = $1 AND ($2::bigint IS NULL OR c.jump_clone_id = $2) ORDER BY i.jump_clone_id, i.slot",
            connection, transaction)
        {
            Parameters = { new() { Value = characterId }, new() { Value = (object?)onlyCloneId ?? DBNull.Value } },
        })
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var cloneId = reader.GetInt64(0);
                if (!implants.TryGetValue(cloneId, out var list))
                {
                    implants[cloneId] = list = [];
                }
                list.Add(reader.GetInt32(1));
            }
        }
        return [.. clones.Select(c => new InstalledJumpClone(c.Id, c.LocationId, c.Kind, c.Name,
            implants.TryGetValue(c.Id, out var list) ? list : []))];
    }
}
