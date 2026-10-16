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

    /// <summary>Adds a clone at a location (inside the caller's transaction). Its implants are not copied yet (CH-15 gap).</summary>
    public async Task<long> InsertAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long characterId, long locationId, string locationKind, CancellationToken cancellationToken = default)
    {
        await using var command = new NpgsqlCommand(
            $"INSERT INTO {_schema}.character_jump_clones(character_id, location_id, location_kind) VALUES ($1, $2, $3) RETURNING jump_clone_id", connection, transaction)
        {
            Parameters = { new() { Value = characterId }, new() { Value = locationId }, new() { Value = locationKind } },
        };
        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    /// <summary>Sets the last clone jump time back to zero, so the next jump has no cooldown.</summary>
    public async Task ResetLastJumpAsync(long characterId, CancellationToken cancellationToken = default)
    {
        await using var command = _source.CreateCommand(
            $"UPDATE {_schema}.characters SET time_last_clone_jump_filetime = 0 WHERE id = $1");
        command.Parameters.AddWithValue(characterId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// The clone rows of a jump, inside the caller's transaction. The target clone goes (it becomes the body), and so does the clone at
    /// the origin, which is replaced by a clone of the body that leaves, with the implants that body wears (the wiki: they stay with the
    /// old body); a target at the origin keeps its id. The last jump time is set to <paramref name="nowFileTime"/>. Returns the target as
    /// it was, or null when the character has no such clone.
    /// </summary>
    public async Task<InstalledJumpClone?> JumpAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long characterId, long targetCloneId,
        long originLocationId, IReadOnlyList<(int Slot, int TypeId)> bodyImplants, long nowFileTime, CancellationToken cancellationToken = default)
    {
        var clones = await ReadClonesAsync(connection, transaction, characterId, cancellationToken);
        if (clones.FirstOrDefault(clone => clone.JumpCloneId == targetCloneId) is not { } target)
        {
            return null;
        }
        var origin = clones.FirstOrDefault(clone => clone.LocationId == originLocationId && clone.JumpCloneId != targetCloneId);
        foreach (var cloneId in origin is null ? new[] { targetCloneId } : new[] { origin.JumpCloneId, targetCloneId })
        {
            await using var delete = new NpgsqlCommand(
                $"DELETE FROM {_schema}.character_jump_clones WHERE jump_clone_id = $1 AND character_id = $2", connection, transaction)
            {
                Parameters = { new() { Value = cloneId }, new() { Value = characterId } },
            };
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        var keepId = target.LocationId == originLocationId;
        await using (var insert = new NpgsqlCommand(
            $"INSERT INTO {_schema}.character_jump_clones(jump_clone_id, character_id, location_id, location_kind, clone_name) " +
            "VALUES (COALESCE($1, nextval('" + _schema + ".jump_clone_ids')), $2, $3, $4, $5) RETURNING jump_clone_id", connection, transaction)
        {
            Parameters =
            {
                new() { Value = keepId ? (object)target.JumpCloneId : DBNull.Value },
                new() { Value = characterId },
                new() { Value = originLocationId },
                new() { Value = "station" },
                new() { Value = origin?.Name ?? "" },
            },
        })
        {
            var replacementId = (long)(await insert.ExecuteScalarAsync(cancellationToken))!;
            foreach (var (slot, typeId) in bodyImplants)
            {
                await using var implant = new NpgsqlCommand(
                    $"INSERT INTO {_schema}.character_jump_clone_implants(jump_clone_id, slot, type_id) VALUES ($1, $2, $3)", connection, transaction)
                {
                    Parameters = { new() { Value = replacementId }, new() { Value = (short)slot }, new() { Value = typeId } },
                };
                await implant.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await using (var time = new NpgsqlCommand(
            $"UPDATE {_schema}.characters SET time_last_clone_jump_filetime = $1 WHERE id = $2", connection, transaction)
        {
            Parameters = { new() { Value = nowFileTime }, new() { Value = characterId } },
        })
        {
            await time.ExecuteNonQueryAsync(cancellationToken);
        }
        return target;
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
